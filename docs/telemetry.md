# Telemetry & Observability

This demo produces **distributed traces, structured logs, and a GenAI view** in the Aspire
dashboard. This doc explains the telemetry chain, why the Squad span looks different from the
`validator` span, and the sub-agent observability enhancements on the
`feature/subagent-observability` branch.

## The trace tree

A single "Trigger Incident" run produces a trace shaped like this:

```
POST /incidents/simulate                         (ASP.NET Core span)
└─ workflow run                                   (SquadifyDemo.Workflow)
   ├─ validator                                   (executor span)
   │  └─ chat  gpt-... / phi-4                     (GenAI span — Experimental.Microsoft.Extensions.AI)
   ├─ squad-analysis                              (our explicit parent span)
   │  ├─ subagent: analyzer                        (Microsoft.Agents.AI.Squad)
   │  └─ subagent: remediator                      (Microsoft.Agents.AI.Squad)
   ├─ enricher                                    (executor span)
   ├─ notify-slack / notify-pagerduty / notify-statuspage
   └─ aggregator
```

## Trace sources

Every span belongs to a named `ActivitySource`. They are all registered in
[`SquadifyWebProgram.cs`](../SquadifyDemo/SquadifyWebProgram.cs):

| Source | Emits |
|--------|-------|
| `SquadifyDemo` | App-level custom spans. |
| `SquadifyDemo.Workflow` | The workflow run + our explicit `squad-analysis` parent span. |
| `Microsoft.Agents.AI.Squad` | Built-in Squad **sub-agent** spans (`EmitSubagentActivities = true`). |
| `Experimental.Microsoft.Extensions.AI` | `IChatClient` model-request spans (the GenAI view). |
| `Experimental.Microsoft.Agents.AI` | MAF agent-level spans. |

## The telemetry chain

```
SquadAgent : DelegatingAIAgent        ← MAF AIAgent, lives in your process
   └─ GitHubCopilotAgent : AIAgent    ← wraps the Copilot SDK
        └─ Copilot SDK
             └─ Copilot CLI (subprocess)   ← model reasoning happens HERE, out-of-process
                  └─ specialist sub-agents
```

### Why the Squad span looks different from the `validator` span

The `validator` node is a plain MAF `ChatClientAgent` calling an `IChatClient` **in-process**. The
`.UseOpenTelemetry()` middleware wraps that call and emits a rich **GenAI span** with the model name,
token usage, prompt/completion (when `EnableSensitiveData = true`).

The Squad node is different. `SquadAgent` delegates to the **Copilot CLI running as a separate
process**. The actual LLM calls happen inside that subprocess, over which the in-process OpenTelemetry
middleware has no visibility. So:

- ✅ You **do** get Squad sub-agent spans (dispatch/start/complete) via `EmitSubagentActivities`.
- ❌ You **don't** automatically get per-model-call GenAI spans (tokens, prompts) for the sub-agents,
  because that reasoning is out-of-process.

This is **not** a bug in the `SquadAgent` implementation — it's an inherent consequence of the CLI
being a subprocess. Wrapping the Copilot agent in a MAF `AIAgent` gives you agent-level and
sub-agent-level telemetry, but not the in-process model spans you'd get from a direct `IChatClient`.

## Live sub-agent callbacks

On `main`, the `SquadExecutor` subscribes to `OnSubagentTrace` to log sub-agent lifecycle events as
they happen:

```csharp
o.OnSubagentTrace = evt =>
{
    switch (evt.Kind)
    {
        case SquadAgentTraceEventKind.SubagentDispatched:
            _logger.LogInformation("[Squad] 📤 Dispatching: {Name} ({Type})",
                evt.DispatchedPersonaName, evt.DispatchedAgentType);
            break;
        case SquadAgentTraceEventKind.SubagentStarted:
            _logger.LogInformation("[Squad] ▶️  Started: {Name}", evt.SubagentName);
            break;
        case SquadAgentTraceEventKind.SubagentCompleted:
            _logger.LogInformation("[Squad] ✅ Completed: {Name}", evt.SubagentName);
            break;
        case SquadAgentTraceEventKind.SubagentFailed:
            _logger.LogWarning("[Squad] ❌ Failed: {Name}", evt.SubagentName);
            break;
    }
};
```

These become **structured logs** in the Aspire dashboard, correlated with the trace, so during a run
you can see the team dispatching agents instead of a silent "thinking…" gap.

## The `feature/subagent-observability` branch

The default branch shows sub-agent **dispatch** spans, but the individual **tool calls** each
sub-agent makes (and their progress) aren't surfaced — a long run can still look "stuck." The
`feature/subagent-observability` branch adds two enhancements:

**(B) Child span per tool call.** With `o.TraceEvents = true`, the executor listens for `ToolStart` /
`ToolComplete` events and starts a child `Activity` per tool invocation, parented to the
`squad-analysis` span and keyed by `ToolCallId`. You get a span per tool call — visible in the trace
tree, with duration and success/failure status.

**(C) Correlated live logs.** The `OnSubagentTrace` switch is expanded to log `ToolStart`,
`ToolComplete`, `AssistantMessage`, and `SubagentSelected` events, so the dashboard's log view shows a
running commentary of what each sub-agent is doing while it works.

Together, B + C turn the opaque "Squad is thinking" period into a visible, inspectable timeline of
tool calls and messages.

> **`SquadAgentTraceEventKind` values** (SDK 0.5.5): `AssistantMessage`, `Other`, `SessionIdle`,
> `SubagentCompleted`, `SubagentDispatched`, `SubagentFailed`, `SubagentSelected`, `SubagentStarted`,
> `ToolComplete`, `ToolStart`.

## Two independent "trace" views — don't confuse them

1. **The demo UI's in-memory trace** at `/trace` — a simple `WorkflowTraceStore` the demo web app
   maintains for its own UI. **Not** OpenTelemetry.
2. **The real Aspire dashboard traces** at `.../traces/detail/<id>` — fed by the OpenTelemetry
   `ActivitySource` spans described above. This is the one you inspect for distributed tracing.

## Concurrent runs, per-run traces, and warm-up

The `feature/concurrent-workflows` branch adds three improvements so multiple incidents can be
triggered at once, each with its own clean trace, without the long cold-start penalty on every run.

### 1. One trace per run (no more merged traces)

Each triggered incident starts its own root `Activity`, so the Aspire dashboard shows a **separate
trace per run** instead of collapsing every workflow into one giant trace. The demo UI keeps a
per-run entry too — `/trace` returns an array of runs and `/trace/{runId}` returns a single run.
Every run carries a distinct `runId`, and its executor spans (`validator` → `squad-analysis` →
`enricher` → `notify-*` → `aggregator`) hang off that run's root.

### 2. Warm agent pool — cold-start reuse

Building a `SquadAgent` (which boots the Copilot CLI subprocess and loads the team) is expensive.
Previously that cost was paid on **every** run. Now a `SquadAgentPool` pre-builds a bounded set of
warm agents at startup via a `BackgroundService` (`SquadAgentPoolWarmer`), and each Demo-3 run
**rents** a ready agent instead of constructing one:

- Pool size is configurable with `SQUAD_POOL_SIZE` (default **3**).
- `RentAsync` hands out a warm agent; `Return` puts it back for the next run.
- The pool doubles as a concurrency cap — no more agents run at once than the pool holds.

**Measured impact:** first (cold) run ≈ **280,724 ms**; a warmed run ≈ **141,928 ms** — about a
**49% reduction** in end-to-end time once the pool is primed.

### 3. True concurrent execution

The web app no longer serializes runs behind a single gate. `WorkflowRunnerState` is a
`ConcurrentDictionary<string, RunInfo>`, each incident executes on its own `Task.Run`, and both
trigger endpoints return **202 Accepted** immediately with the new `runId`.

To make the MAF workflow itself safe to run concurrently, the workflow uses
`InProcessExecution.Concurrent.RunStreamingAsync`. That path rejects pre-instantiated shared
executors at DAG-build time:

> *Workflow must only consist of cross-run share-capable or factory-created executors…*

The fix is to register every executor through a **factory** so each run gets a fresh instance.
`BindExecutor` takes a `Func<string, string, ValueTask<TExecutor>>` (params are `(id, sessionId)`),
invoked per run:

```csharp
ExecutorBinding validator =
    new Func<string, string, ValueTask<ValidatorExecutor>>(
        (id, _) => new ValueTask<ValidatorExecutor>(
            new ValidatorExecutor(id, BuildValidatorAgent(chatClient, logger), chatClient, logger)))
    .BindExecutor("validator");
```

Because `RunWorkflowAsync` builds a fresh workflow per run, each run's factory closures capture that
run's own rented pooled agent — so concurrent runs never share executor state.

**Verified live:** three Demo-3 runs fired within ~27 s of each other all ran simultaneously
(`activeCount: 3`) and completed with overlapping windows — e.g. runs starting at 20:29:40, 20:30:06,
and 20:30:07 were all in-flight together for ~3 minutes, then finished at 20:33:07, 20:33:16, and
20:35:23. Serialized execution could not produce those overlapping timestamps. Each run produced its
own distinct 6-step trace.
