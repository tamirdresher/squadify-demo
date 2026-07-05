# Architecture

This doc explains how **Squad**, **Microsoft Agent Framework (MAF)**, and **.NET Aspire** compose
in this demo, and the design decisions behind each layer.

## The three layers

### 1. Aspire AppHost — hosting & orchestration

[`SquadifyDemo.AppHost/Program.cs`](../SquadifyDemo.AppHost/Program.cs) is the composition root.
It declares the distributed app model:

```csharp
// Custom Aspire resource — surfaces the Squad team in the dashboard.
var squad = builder.AddSquad("incident-response-squad",
    teamRoot: Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..")));

// The workflow web app, referencing the squad and the model provider.
var workflow = builder.AddProject<Projects.SquadifyDemo>("squadify-workflow")
    .WithReference(squad)
    .WithEnvironment("USE_AZURE_OPENAI", useAzure ? "true" : "false")
    .WithHttpCommand("/incidents/simulate", "Trigger Incident", ...);
```

- **`AddSquad(...)`** is a custom Aspire hosting resource (vendored in
  [`src/CommunityToolkit.Aspire.Hosting.Squad/`](../src/CommunityToolkit.Aspire.Hosting.Squad/)).
  It registers the Squad team (`team.md` under `teamRoot/.squad/`) as a resource so it appears in the
  dashboard with its own state and dashboard properties.
- **Model selection** is conditional: if `AZURE_OPENAI_ENDPOINT` is configured, the workflow uses
  Azure OpenAI; otherwise the AppHost starts **Foundry Local** with **Phi-4** and references it.
- **`WithHttpCommand`** adds dashboard buttons that POST to app endpoints — the demo's "Trigger
  Incident" button lives here.

### 2. MAF workflow — the DAG

[`SquadifyDemo/Agents/SquadifiedWorkflow.cs`](../SquadifyDemo/Agents/SquadifiedWorkflow.cs) builds a
**real** MAF workflow with `WorkflowBuilder` — not a hand-rolled orchestration loop:

```csharp
var workflow = new WorkflowBuilder(validator)
    .AddEdge(validator, squad)
    .AddEdge(squad, enricher)
    .AddFanOutEdge(enricher, new ExecutorBinding[] { slack, pagerDuty, statusPage })
    .AddFanInBarrierEdge(new ExecutorBinding[] { slack, pagerDuty, statusPage }, aggregator)
    .WithIntermediateOutputFrom(new ExecutorBinding[] { enricher })
    .WithOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true, activitySource: s_workflowSource)
    .WithOutputFrom(aggregator)
    .Build();
```

Key MAF primitives on display:

| Primitive | Used for |
|-----------|----------|
| `Executor<TIn, TOut>` | Typed nodes. Each `HandleAsync` receives the previous node's output. |
| `AddEdge` | Sequential edges (`validator → squad → enricher`). |
| `AddFanOutEdge` | Parallel dispatch to the three notification channels. |
| `AddFanInBarrierEdge` | Barrier that waits for all channels before the aggregator runs. |
| `WithIntermediateOutputFrom` | Exposes the enricher's output as an intermediate result for the UI. |
| `WithOpenTelemetry` | Instruments every executor invocation as a span. |
| `[YieldsOutput(typeof(string))]` + `YieldOutputAsync` | The aggregator's final output. |
| `InProcessExecution.RunStreamingAsync` + `WatchStreamAsync` | Streaming run — the app consumes `ExecutorInvokedEvent`, `ExecutorCompletedEvent`, `WorkflowOutputEvent`, etc. |

Two of the nodes are AI agents:

- **`validator`** hosts a MAF `ChatClientAgent` wrapped in an `AIAgentBuilder().UseOpenTelemetry()`
  pipeline. This is the "textbook" in-process agent — its model call shows up in the Aspire GenAI view.
- **`squad-analysis`** hosts the **Squad team** (next section).

### 3. Squad — the team node

The `SquadExecutor` node runs an entire AI team inside one executor:

```csharp
builder.Services.AddSquadAgent(o =>
{
    o.SquadFolderPath = _squadFolder;
    o.AgentName = "IncidentSquad";
    o.Instructions = "You are an incident response team coordinator. " +
        "You MUST use the task tool to spawn specialist sub-agents ...";
    o.EmitSubagentActivities = true;              // built-in OTel spans for sub-agents
    o.OnSubagentTrace = evt => { /* live callbacks */ };
});

var squad = host.Services.GetRequiredService<SquadAgent>();
var session = await squad.CreateSessionAsync();
await foreach (var update in squad.RunStreamingAsync(validatedAlert, session)) { ... }
```

`SquadAgent` is a MAF `AIAgent`, so it drops straight into the workflow like any other agent. Under
the hood it drives the **Copilot CLI**, which spawns specialist sub-agents (defined by the charters
in `.squad/agents/`). `EmitSubagentActivities = true` makes those sub-agents emit OpenTelemetry spans
under the `Microsoft.Agents.AI.Squad` source.

#### Why an explicit parent span?

```csharp
using var squadActivity = s_workflowSource.StartActivity("squad-analysis");
```

MAF's workflow OpenTelemetry does **not** make the executor-invocation span the ambient
`Activity.Current` during `HandleAsync`. Without an explicit parent, the built-in Squad sub-agent
spans would attach to the wrong parent (or none) and appear detached in the trace tree. Starting our
own activity gives the sub-agent spans a correct parent to nest under.

## OpenTelemetry wiring

[`SquadifyDemo/SquadifyWebProgram.cs`](../SquadifyDemo/SquadifyWebProgram.cs) registers all the trace
sources with the `TracerProvider` and exports to the Aspire OTLP endpoint:

```csharp
.WithTracing(tracing => tracing
    .AddSource(SquadifyTelemetry.ActivitySourceName)                 // "SquadifyDemo"
    .AddSource("SquadifyDemo.Workflow")                              // workflow + our parent span
    .AddSource(Squad.Agents.AI.SquadAgentDiagnostics.ActivitySourceName) // "Microsoft.Agents.AI.Squad"
    .AddSource("Experimental.Microsoft.Extensions.AI")               // IChatClient / model spans
    .AddSource("Experimental.Microsoft.Agents.AI")                   // agent-level spans
    .AddOtlpExporter(ConfigureOtlp));
```

Aspire injects the OTLP endpoint + headers via environment variables (`OTEL_EXPORTER_OTLP_*`), which
`ConfigureOtlp` reads — so no hard-coded dashboard URL.

## Why vendor the Squad Aspire resource?

The `CommunityToolkit.Aspire.Hosting.Squad` extension is not (yet) on NuGet, and referencing it from
an external checkout would break clone-and-run. We **vendored** a self-contained copy into
[`src/CommunityToolkit.Aspire.Hosting.Squad/`](../src/CommunityToolkit.Aspire.Hosting.Squad/). Its
only external dependency is `Aspire.Hosting`. In production, prefer the package reference once it
ships.
