# Squadify Your App — Squad + Microsoft Agent Framework + .NET Aspire

A runnable C#/.NET demo that shows how to **"squadify"** an application: drop an autonomous
[**Squad**](https://github.com/bradygaster/squad) AI team into a real
[**Microsoft Agent Framework (MAF)**](https://learn.microsoft.com/agent-framework/) workflow,
host the whole thing with [**.NET Aspire**](https://learn.microsoft.com/dotnet/aspire/), and watch
sub-agent activity light up as OpenTelemetry spans in the Aspire dashboard.

It is the companion demo for the **"Squadify Your App"** talk.

> **TL;DR** — One MAF `WorkflowBuilder` DAG. One of its nodes is a whole Squad AI team
> (Copilot CLI + specialist sub-agents). Aspire hosts it, wires OpenTelemetry, and gives you
> distributed traces, logs, and a GenAI view for free.

---

## What this demonstrates

1. **A real MAF workflow** — an explicit DAG built with `WorkflowBuilder` from
   `Microsoft.Agents.AI.Workflows` (edges, fan-out, fan-in barrier — not a hand-rolled loop).
2. **Squad as a first-class workflow node** — the `SquadAgent` (from `Squad.Agents.AI`) runs an
   entire incident-response team inside a single executor. It dispatches specialist sub-agents
   (root-cause analysis, remediation planning) in parallel via the Copilot CLI.
3. **Aspire hosting + observability** — a custom `AddSquad(...)` Aspire resource surfaces the team
   in the dashboard, and OpenTelemetry wiring means every executor invocation, agent call, and
   sub-agent dispatch becomes a span you can inspect.
4. **Model flexibility** — runs against **Azure OpenAI** when configured, or falls back to
   **Foundry Local** (Phi-4, on-device, no cloud required).

---

## The workflow graph

The "squadified" workflow is a MAF DAG that mixes three kinds of nodes: a MAF `ChatClientAgent`,
the Squad team, and deterministic executors.

```
[validator] ──▶ [squad-analysis] ──▶ [enricher] ─┬─▶ [notify-slack] ──────┐
   (MAF            (Squad team:          (context   ├─▶ [notify-pagerduty] ──┤ fan-in
    agent)          Copilot CLI +         enrich)   └─▶ [notify-statuspage] ─┘ barrier
                    sub-agents)                                    │
                                                                   ▼
                                                              [aggregator] ──▶ output
```

| Node | Type | What it does |
|------|------|--------------|
| `validator` | MAF `ChatClientAgent` | Classifies the alert (actionable? severity P1–P4). |
| `squad-analysis` | **Squad team** | Delegates to the Copilot CLI, spawns specialist sub-agents for root-cause + remediation. **The star of the graph.** |
| `enricher` | Deterministic executor | Adds operational context (SLA, on-call, service tier). Emitted as an *intermediate* output. |
| `notify-slack` / `notify-pagerduty` / `notify-statuspage` | Deterministic (fan-out) | Format channel-specific notifications in parallel. |
| `aggregator` | Fan-in barrier | Waits for all three channels, yields the final report. |

See [`SquadifyDemo/Agents/SquadifiedWorkflow.cs`](SquadifyDemo/Agents/SquadifiedWorkflow.cs) for the
fully-commented implementation.

---

## Architecture

```
┌─────────────────────────── .NET Aspire AppHost ───────────────────────────┐
│                                                                            │
│   AddSquad("incident-response-squad")      AddProject<SquadifyDemo>        │
│        │  (custom Aspire resource)              │  (the workflow web app)  │
│        │  surfaces the team + team.md           │                          │
│        └───────────────  WithReference  ────────┘                          │
│                                                                            │
│   AddFoundry("foundry").RunAsFoundryLocal()  ← only when no Azure OpenAI   │
│                                                                            │
│   OTLP endpoint ◀── traces / logs / metrics ── from the workflow project   │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
              ┌─────────────── SquadifyDemo (web) ───────────────┐
              │  MAF WorkflowBuilder DAG (.WithOpenTelemetry)     │
              │      └─ SquadExecutor                             │
              │           └─ SquadAgent (Squad.Agents.AI)         │
              │                └─ Copilot CLI subprocess          │
              │                     └─ specialist sub-agents      │
              └───────────────────────────────────────────────────┘
```

**Telemetry chain:** `SquadAgent : DelegatingAIAgent` → `GitHubCopilotAgent : AIAgent` → Copilot SDK
→ Copilot CLI subprocess. Model reasoning happens **out-of-process** in the CLI, which is why the
Squad span looks different from an in-process `ChatClientAgent` span — see
[`docs/telemetry.md`](docs/telemetry.md) for the full explanation.

---

## Prerequisites

- **.NET 10 SDK** (the projects target `net10.0`).
- **[GitHub Copilot CLI](https://github.com/github/copilot-cli)** installed and authenticated
  (`copilot` on your PATH). The Squad team runs on top of it.
- **A model provider — pick one:**
  - **Azure OpenAI** — set `AZURE_OPENAI_ENDPOINT` (+ `AZURE_OPENAI_KEY`), see below. Fast, recommended for the demo.
  - **Foundry Local** — nothing to configure; the AppHost starts Foundry Local with **Phi-4** automatically. On-device, no cloud, but slower.
- **Docker / a container runtime** is **not** required for the core demo.

---

## Getting started

```bash
git clone https://github.com/tamirdresher/squadify-demo.git
cd squadify-demo

# (optional) point at Azure OpenAI for faster, higher-quality runs:
dotnet user-secrets --project SquadifyDemo.AppHost set AZURE_OPENAI_ENDPOINT "https://<your>.openai.azure.com/"
dotnet user-secrets --project SquadifyDemo.AppHost set AZURE_OPENAI_KEY "<your-key>"

# run the Aspire AppHost (starts the dashboard, the workflow app, and — if no Azure — Foundry Local)
$env:ASPIRE_ALLOW_UNSECURED_TRANSPORT="true"     # PowerShell; use export on bash/zsh
dotnet run --project SquadifyDemo.AppHost/SquadifyDemo.AppHost.csproj
```

If `AZURE_OPENAI_ENDPOINT` is not set, the AppHost falls back to Foundry Local + Phi-4 — the first
run will download the model.

---

## Running the demo

1. Open the **Aspire dashboard** (the URL is printed on startup, typically `https://localhost:17xxx`).
2. On the `squadify-workflow` resource, click the **⚡ Trigger Incident** command
   (it POSTs to `/incidents/simulate`).
3. Watch the workflow run. Then open **Traces** and select the newest trace.

### What to look for in the trace

- A top-level workflow span, with a child span **per executor** (`validator`, `squad-analysis`,
  `enricher`, the three notifiers, `aggregator`).
- Under `squad-analysis`, the built-in **Squad sub-agent spans** (source
  `Microsoft.Agents.AI.Squad`) showing the team dispatching specialist agents.
- The **GenAI view** for the `validator` node's model call (source
  `Experimental.Microsoft.Extensions.AI`).
- Correlated **structured logs** — the workflow and Squad both log progress (`[Workflow] ▶️ …`,
  `[Squad] 📤 Dispatching: …`).

> **Heads-up:** the Squad team runs the Copilot CLI as a subprocess, so its model reasoning is
> out-of-process and won't appear as an in-process GenAI span the way `validator` does. The
> `feature/subagent-observability` branch adds child spans + correlated live logs so you can see
> exactly what each sub-agent is doing while it runs. See [`docs/telemetry.md`](docs/telemetry.md).

---

## Concurrent runs, warm-up & per-run traces

You can fire several incidents at once and watch them run **truly in parallel**, each with its own
clean trace — without paying the Squad cold-start cost on every run.

- **True concurrent execution.** Trigger the incident command (or `POST /incidents/simulate`) multiple
  times back-to-back. Each run executes on its own `Task.Run`, the endpoint returns **202 Accepted**
  immediately with a fresh `runId`, and `/status` reports the live `activeCount`. Under the hood the
  workflow uses MAF's `InProcessExecution.Concurrent.RunStreamingAsync`.
- **A warm agent pool kills the per-run cold start.** Building a `SquadAgent` (which boots the Copilot
  CLI subprocess and loads the team) is expensive. A `SquadAgentPool` pre-builds a bounded set of warm
  agents at startup via a `BackgroundService`, and each run **rents** a ready agent instead of building
  one. Pool size is configurable with `SQUAD_POOL_SIZE` (default **3**), and the pool doubles as the
  concurrency cap. Measured impact: first cold run ≈ 280 s vs a warmed run ≈ 142 s (~49% faster).
- **One trace per run.** Each run starts its own root `Activity`, so the Aspire dashboard shows a
  **separate trace per incident** instead of one merged giant trace. `/trace` returns the array of
  runs; `/trace/{runId}` returns a single run.
- **Factory-bound executors.** MAF's concurrent runtime rejects shared, pre-instantiated executors, so
  every node is registered through a factory (a small `Bind<T>` helper) — each concurrent run gets a
  fresh, isolated executor closing over its own rented pooled agent.

See [`docs/telemetry.md`](docs/telemetry.md) for the trace shapes, the `Bind<T>` pattern, and the
live concurrency verification.

---

## Project layout

| Path | What it is |
|------|-----------|
| `SquadifyDemo.AppHost/` | The Aspire AppHost. Wires the Squad resource, the workflow project, and the model provider. |
| `SquadifyDemo/` | The workflow web app. Hosts the MAF DAG and the demo UI. |
| `SquadifyDemo/Agents/SquadifiedWorkflow.cs` | **The MAF `WorkflowBuilder` DAG** — the heart of the demo. |
| `SquadifyDemo/SquadifyWebProgram.cs` | Web host + OpenTelemetry wiring (trace sources, OTLP export). |
| `SquadifyDemo/Tools/` | Deterministic tools used by the incident scenario. |
| `src/CommunityToolkit.Aspire.Hosting.Squad/` | **Vendored** copy of the Squad Aspire hosting resource (`AddSquad`). Self-contained so the repo clones-and-runs. |
| `.squad/` | The Squad team definition — agent charters (`analyzer`, `coordinator`, `remediator`), `team.md`, `decisions.md`. |
| `docs/` | Architecture & telemetry deep-dives. |

---

## Key packages

| Package | Version | Role |
|---------|---------|------|
| `Microsoft.Agents.AI` | 1.13.0 | MAF agents (`ChatClientAgent`, `AIAgent`). |
| `Microsoft.Agents.AI.Workflows` | 1.13.0 | `WorkflowBuilder` DAG API. |
| `Squad.Agents.AI` | 0.5.5 | `SquadAgent` — runs the Squad team as a MAF `AIAgent`. |
| `Microsoft.Extensions.AI` (+ `.OpenAI`) | 10.7.0 | `IChatClient` abstraction + OpenTelemetry middleware. |
| `Aspire.Hosting` | 13.2.0 | Aspire app model (used by the vendored Squad resource). |
| `Aspire.Hosting.Foundry` | 13.4.6-preview | Foundry Local integration. |
| `OpenTelemetry.*` | 1.12.0 | Traces, logs, OTLP export. |

---

## Learn more

- [`docs/architecture.md`](docs/architecture.md) — how the pieces fit together and why.
- [`docs/telemetry.md`](docs/telemetry.md) — the telemetry chain, why the Squad span looks the way
  it does, and the sub-agent observability enhancements.
- **Squad** — https://github.com/bradygaster/squad
- **Microsoft Agent Framework** — https://learn.microsoft.com/agent-framework/
- **.NET Aspire** — https://learn.microsoft.com/dotnet/aspire/
- **Aspire Community Toolkit** — https://github.com/CommunityToolkit/Aspire

---

## Notes

- The vendored `src/CommunityToolkit.Aspire.Hosting.Squad/` exists so this repo is **self-contained**.
  In your own app, once the Squad hosting extension ships on NuGet, prefer the package reference.
- Squad runtime state (`session-state/`, `session-store.db`) is regenerated at runtime and is
  intentionally git-ignored.

## License

Provided as-is for learning and demonstration purposes.
