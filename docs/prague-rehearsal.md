# Prague 2026: engineering-team rehearsal

The 50-minute talk plus 10-minute Q&A is about a human-led engineering team in Git.
This application supplies a real reliability bug and deterministic acceptance tests.
The incident-response `.squad` and `.github/agents/squad.agent.md` are preserved.
They are **not** an architect/developer/tester/reviewer engineering team.

**Ready:** local synthetic regression path. **Not rehearsed:** engineering-team casting,
issue triage, parallel agent branches, independent blocking review, PR checks, or an
operator-approved merge. No issue or PR was created during preparation. Do not present
this branch or application as production-ready.

## Copyable issue brief

**Title:** Squad failure can be reported as a successful incident workflow

**Observed original code:** `SquadifiedWorkflow.SquadExecutor.RunWithAgentAsync` catches
exceptions and returns `"Analysis unavailable ..."` as ordinary analysis. Downstream
formatters/aggregator still produce output, so the runner marks the incident Completed
and its activity OK. Workflow error events were only logged and missing output could
also become a success string.

**Impact:** the presenter/operator cannot distinguish failed agent execution from completed
analysis. A related pool-initialization failure logs a nonexistent fallback while Demo 3
waits forever for a slot. Shutdown cancellation was also reported as Completed.

**Acceptance spec:**

- An exception or empty response from the Squad node fails the DAG, preserving the cause.
- A `WorkflowErrorEvent` or missing final output fails, never a success-shaped fallback.
- Pool initialization failure releases existing and future renters with an error.
- Initialization waits, occupied-slot waits, validator and Squad calls support cancellation.
- `/status`, `/trace` and UI distinguish Failed, Cancelled and Completed; cancelled activities
  are not OK. `/health` is liveness; `/ready` is initialized-slot readiness only.
- Malformed/out-of-range `demo` query values return HTTP 400 without enqueuing a run.
- A synthetic successful DAG still reaches all three notification formatters.
- Regressions make no model/service calls and do not start Copilot or Foundry.

**Expected failing regression:** `SquadFailureOrEmptyResponseFailsTheRealDag` injects a
synthetic `ChatClientAgent` into the actual MAF DAG. A throwing or empty client must cause
the workflow task to throw before the test's 10-second safety timeout. Returning text from
the catch block instead yields `Assert.ThrowsAny() Failure: No exception was thrown`.

## No-cloud preflight and evidence

Run from the repository root in PowerShell:

```powershell
dotnet --info
dotnet restore .\SquadifyDemo.slnx --configfile .\NuGet.Config
dotnet build .\SquadifyDemo.slnx --no-restore
dotnet test .\SquadifyDemo.Tests\SquadifyDemo.Tests.csproj --no-restore
node --test .\SquadifyDemo.Tests\ui-regressions.cjs
dotnet test .\SquadifyDemo.Tests\SquadifyDemo.Tests.csproj --no-restore --filter 'FullyQualifiedName~SquadFailureOrEmptyResponseFailsTheRealDag'
dotnet test .\SquadifyDemo.Tests\SquadifyDemo.Tests.csproj --no-restore --filter 'FullyQualifiedName~Pool|FullyQualifiedName~Cancellation|FullyQualifiedName~Liveness'
dotnet list .\SquadifyDemo.slnx package --vulnerable --include-transitive --no-restore
```

NuGet and the SDK's build-time CLI download use Microsoft CFS. Tests use synthetic chat
clients, an in-process HTTP server, and a real in-process MAF graph; they do not prove
Copilot authentication, live sub-agent dispatch, model quality, or GitHub delivery.
The UI tests require Node.js 22+ and its built-in runner, with no npm dependencies.
They execute the actual inline JavaScript and inspect its class routing and CSS rules:
Completed details are green, Failed red, Cancelled amber, and nonterminal details neutral.
This is not a browser screenshot or computed-layout test.

**Red/green witness, 2026-11-01:** temporarily restoring the original catch-and-return
behavior in the fixed tree made both synthetic Squad failure cases fail with
`No exception was thrown` (2 failed, 0 passed). Restoring `throw;` makes them pass.
This is a controlled regression replay, not a claim that the new harness existed on
the original `9b82f12` baseline.

After restoring the fix, the full solution built with zero errors and **29/29 tests
passed** on 2026-11-01. Existing package-advisory warnings remain (see below).

For a reproducible rehearsal, use a throwaway worktree based on this feature branch:

```powershell
# Choose a new, absent path. Do not use the original checkout or an app-managed worktree.
$rehearsalRoot = Join-Path $HOME 'source\rehearsals'
New-Item -ItemType Directory -Path $rehearsalRoot -Force | Out-Null
$rehearsal = Join-Path $rehearsalRoot 'squadify-prague-rehearsal'
git worktree add -b rehearsal/prague-reliability $rehearsal tamirdresher-microsoft-update-prague-2026
Set-Location $rehearsal
```

First run the green regression. In this disposable branch only, change the general
`catch (Exception ex)` in `SquadExecutor.RunWithAgentAsync` from `throw;` to:

```csharp
return $"Analysis unavailable - Squad agent error: {ex.Message}";
```

Run the filtered regression again: expect two failures, not a hang or model call.
Label this as a **seeded replay of the original defect**. Commit the seed only to the
rehearsal branch if the presenter wants the engineering team to repair it from an issue.
Never push the seed to the prepared feature branch. Restoring `throw;` is the green fix.
Pool/cancellation/HTTP behavior have separate tests and can be additional issue scope,
not unrelated live feature work.

## Separate engineering-team setup: prerequisite, not ready-made capability

Verified locally: .NET SDK 10.0.403; Aspire CLI 13.4.6; Copilot CLI 1.0.106-2;
Squad CLI 1.1.0. The NuGet SDK also bundles Copilot CLI 1.0.57 at build time.
Do not assume the app's bundled CLI equals the interactive engineering CLI.

`squad init --help`, `squad --help`, `squad roles --search architect`,
`squad roles --search developer`, `squad roles --search tester`, and
`squad roles --search reviewer` confirmed available lead, developer, tester, and reviewer
role definitions. **`--roles` does not create the members:** a deterministic scratch probe
produced empty Members and routing tables. `--preset default` failed locally because no
presets directory was installed; do not use it as a shortcut.

Recommended additive setup, **only after reviewing its side effects**, from the disposable
rehearsal worktree:

```powershell
New-Item -ItemType Directory .\engineering-demo
Set-Location .\engineering-demo
squad init --roles --no-workflows --no-vscode-default
```

This exact initializer was exercised in session scratch storage, not this repository.
It generates `.squad` memory/casting/templates/identity, empty team/routing, built-in
scribe/Ralph/RAI/fact-checker charters, `.github/skills`, `.github/agents/squad.agent.md`,
`.mcp.json`, local `.copilot/mcp-config.json`, `.gitattributes` and `.gitignore`.
It is a substantial installation, intentionally **not committed** just for a demo.
Inspect generated files before trusting them or authorizing MCP startup.

Before any model-backed work, give the generated engineering coordinator a unique file
name and frontmatter `name` (for example `engineering-squad`); leave the repository-root
incident coordinator unchanged. Copilot help verifies `--agent`, `-C`, `--add-dir` and
`--additional-mcp-config` syntax. Proposed launch from `engineering-demo`:

```powershell
copilot --agent engineering-squad --add-dir .. --additional-mcp-config '@.mcp.json'
```

Custom-agent discovery, the loaded engineering `TEAM_ROOT`, parent source access,
authentication and MCP availability must be confirmed in a separately authorized rehearsal.
Do not grant all permissions to make a demonstration work. Ask the coordinator to propose
lead/architect, developer, tester and independent reviewer members, then inspect/approve
their actual charters, routing, decision inbox and governance. Verify the reviewer cannot
self-approve and a rejected author does not silently bypass the gate. Generated prompt rules
are not evidence that these gates executed. Do not start a watch loop or model call as part
of the deterministic regression path.

## Presenter beats and isolation

1. **Prepared issue (4 minutes):** inspect the copyable issue/spec and seeded red test.
   The lead divides work: a developer repairs failure propagation, while a tester works
   from the acceptance spec in a different branch. Merge neither while work is in flight.
   Use separate `git worktree add -b rehearsal/prague-dev <new-path> <seed-commit>` and
   `git worktree add -b rehearsal/prague-tests <new-path> <seed-commit>` worktrees; choose
   actual seed commit and fresh paths during preparation. Worktrees share Git history,
   not mutable agent workspaces. Pass the approved engineering team root explicitly.
2. **Review (3 minutes):** inspect an independently produced blocking review, the correction,
   targeted green tests and actual PR diff/checks if separately prepared. A different fixer
   handles rejection if required by the approved governance. Show concrete review evidence,
   not a fabricated finding or a scripted claim of approval.
3. **Merge is human-controlled:** creating an issue/PR, pushing branches, invoking models
   and merging require separate authorization. Merge only with explicit presenter approval
   and repository checks. There is no PR URL or completed issue-to-merge rehearsal yet.

If live team setup is unready, show the issue brief, original catch behavior and current
synthetic red/green evidence, explicitly labeled prepared evidence. The MAF/Aspire app is
context, not a substitute for the accepted engineering-team story.

## Readiness caveats and existing advisories

The application has simulated data/tools/notifications, an unbounded trigger queue and
in-memory state. Foundry Local only replaces the in-process model path; the Squad node
still requires Copilot and its service. Startup prepares agent objects, not a preauthenticated
CLI. Sensitive prompt/response tracing is enabled: never use customer incidents, credentials
or private operational data. No live model or AppHost rehearsal was performed for this change.

NuGet reported these **existing** advisories on 2026-11-01; versions were not upgraded
or warnings suppressed. This is a package inventory, not an exploitability assessment:

| Project / dependency | Relationship | Version | Report |
|---|---|---|---|
| Web app: `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Direct runtime dependency | 1.12.0 | Moderate: GHSA-4625-4j76-fww9 |
| Vendored hosting: `MessagePack` | Transitive via Aspire hosting | 2.5.192 | High: GHSA-hv8m-jj95-wg3x, GHSA-vh6j-jc39-fggf; ten moderate advisories |
| AppHost: `MessagePack` | Transitive hosting dependency | 2.5.302 | Moderate: GHSA-qhrr-8q5h-9q3h |

The test project inherits the web app's OTLP warning. Use the inventory command above for
full advisory URLs and current reports. Resolving these needs a separately reviewed dependency
update, not a hidden upgrade during conference preparation.
