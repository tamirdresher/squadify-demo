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

**(D) Full end-to-end chat in the "AI details" view.** The `squad-analysis` span is decorated with
the OpenTelemetry **GenAI semantic-convention** attributes, so the Aspire dashboard renders it in the
same **"AI details" chat visualizer** it uses for an in-process `ChatClientAgent` — except here the
conversation is the Squad's *entire* internal exchange, end to end:

- `gen_ai.operation.name = "chat"` — the flag that tells the dashboard to show the AI-details panel.
- `gen_ai.provider.name = "squad"`, `gen_ai.request.model` — provider/model labels.
- `gen_ai.system_instructions` — the coordinator's system prompt.
- `gen_ai.input.messages` — what we sent the coordinator (system instructions + the validated alert).
- `gen_ai.output.messages` — the **live transcript**: every coordinator turn, each sub-agent
  dispatch/tool call, each tool response, and every assistant message, appended in arrival order as
  the run progresses (guarded by a lock because callbacks fire from parallel sub-agent threads).

The message JSON matches `Microsoft.Extensions.AI`'s `OpenTelemetryChatClient` shape (snake_case
`parts`: `text`, `tool_call`, `tool_call_response`), which is what the dashboard's parser expects. The
result: opening the `squad-analysis` span in the Aspire GenAI view shows the whole
coordinator → sub-agent → coordinator conversation as a chat, not just the final report. This closes
the gap noted above — the Copilot CLI's reasoning is out-of-process, but the SDK's trace events give us
enough to reconstruct and surface the conversation as a first-class GenAI chat.

> **`SquadAgentTraceEventKind` values** (SDK 0.5.5): `AssistantMessage`, `Other`, `SessionIdle`,
> `SubagentCompleted`, `SubagentDispatched`, `SubagentFailed`, `SubagentSelected`, `SubagentStarted`,
> `ToolComplete`, `ToolStart`.

## Two independent "trace" views — don't confuse them

1. **The demo UI's in-memory trace** at `/trace` — a simple `WorkflowTraceStore` the demo web app
   maintains for its own UI. **Not** OpenTelemetry.
2. **The real Aspire dashboard traces** at `.../traces/detail/<id>` — fed by the OpenTelemetry
   `ActivitySource` spans described above. This is the one you inspect for distributed tracing.
