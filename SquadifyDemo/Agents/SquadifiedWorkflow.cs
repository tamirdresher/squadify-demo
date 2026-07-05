using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squad.Agents.AI;

namespace SquadifyDemo.Agents;

/// <summary>
/// The "Squadified" workflow — a REAL Microsoft Agent Framework workflow graph built with
/// <see cref="WorkflowBuilder"/> from <c>Microsoft.Agents.AI.Workflows</c>.
///
/// It composes an explicit DAG that mixes three kinds of nodes:
///   1. A validator node that hosts a real MAF <see cref="ChatClientAgent"/> (Azure OpenAI).
///   2. The Squad agent node — Squad.Agents.AI running the full team (Copilot CLI + sub-agents),
///      the star of the graph, whose sub-agent spans surface in the Aspire trace view.
///   3. Deterministic executor nodes (context enrichment + notification channels).
///
/// Graph shape (edges + fan-out + fan-in barrier):
///
///   [validator] ──▶ [squad-analysis] ──▶ [enricher] ─┬─▶ [notify-slack] ──────┐
///                                                     ├─▶ [notify-pagerduty] ──┤ (fan-in barrier)
///                                                     └─▶ [notify-statuspage] ─┴─▶ [aggregator] ──▶ output
///
/// The whole graph is instrumented with <c>.WithOpenTelemetry(...)</c> so every executor
/// invocation becomes a span, alongside the Squad sub-agent spans.
/// </summary>
public static class SquadifiedWorkflow
{
    /// <summary>
    /// ActivitySource for the workflow. MUST match the source registered in the web host's
    /// TracerProvider (<c>"SquadifyDemo.Workflow"</c>) so workflow spans reach the Aspire dashboard.
    /// </summary>
    private static readonly ActivitySource s_workflowSource = new("SquadifyDemo.Workflow");

    /// <summary>
    /// Builds and runs the squadified MAF workflow. Signature is preserved for the web host call site.
    /// </summary>
    public static async Task<string> RunWorkflowAsync(
        string alertInput, string squadFolder, ILogger logger, IChatClient? chatClient = null)
    {
        logger.LogInformation("Building squadified MAF workflow (WorkflowBuilder DAG)...");
        logger.LogInformation("  Graph: [validator] → [squad-analysis] → [enricher] → fan-out{{slack,pagerduty,statuspage}} → barrier → [aggregator]");

        // ── Build the executor nodes ──
        var validator = new ValidatorExecutor("validator", BuildValidatorAgent(chatClient, logger), chatClient, logger);
        var squad = new SquadExecutor("squad-analysis", squadFolder, logger);
        var enricher = new EnricherExecutor("enricher", logger);
        var slack = new NotifierExecutor("notify-slack", "Slack #incidents", logger);
        var pagerDuty = new NotifierExecutor("notify-pagerduty", "PagerDuty", logger);
        var statusPage = new NotifierExecutor("notify-statuspage", "StatusPage", logger);
        var aggregator = new NotificationAggregator("aggregator", expected: 3);

        // ── Wire the DAG with real WorkflowBuilder edges ──
        var workflow = new WorkflowBuilder(validator)
            .AddEdge(validator, squad)
            .AddEdge(squad, enricher)
            .AddFanOutEdge(enricher, new ExecutorBinding[] { slack, pagerDuty, statusPage })
            .AddFanInBarrierEdge(new ExecutorBinding[] { slack, pagerDuty, statusPage }, aggregator)
            .WithIntermediateOutputFrom(new ExecutorBinding[] { enricher })
            .WithOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true, activitySource: s_workflowSource)
            .WithOutputFrom(aggregator)
            .Build();

        logger.LogInformation("[Workflow] ▶️  Streaming run started...");

        // ── Run and consume the event stream ──
        string enriched = "";
        string notifications = "";

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, alertInput);
        await foreach (var evt in run.WatchStreamAsync())
        {
            switch (evt)
            {
                case ExecutorInvokedEvent e:
                    logger.LogInformation("[Workflow] ▶️  {Id}", e.ExecutorId);
                    break;
                case ExecutorCompletedEvent e:
                    logger.LogInformation("[Workflow] ✅ {Id}", e.ExecutorId);
                    break;
                case WorkflowErrorEvent e:
                    logger.LogWarning(e.Exception, "[Workflow] ⚠️  workflow error");
                    break;
                case WorkflowOutputEvent e:
                    var text = e.As<string>() ?? "";
                    if (e.IsIntermediate())
                        enriched = text;   // enriched Squad analysis (intermediate)
                    else
                        notifications = text; // aggregated notifications (final)
                    break;
            }
        }

        logger.LogInformation("✅ Workflow completed.");

        // Combine the enriched analysis with the notification report for the UI.
        if (string.IsNullOrWhiteSpace(enriched))
            return string.IsNullOrWhiteSpace(notifications) ? "Workflow produced no output." : notifications;

        return string.IsNullOrWhiteSpace(notifications)
            ? enriched
            : enriched + "\n\n" + notifications;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agent construction — real MAF ChatClientAgent + AIAgentBuilder
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the AlertValidator as a real MAF <see cref="ChatClientAgent"/> wrapped with an
    /// <see cref="AIAgentBuilder"/> middleware pipeline (OpenTelemetry). Returns null when no
    /// IChatClient is configured (the executor then uses a deterministic fallback).
    /// </summary>
    private static AIAgent? BuildValidatorAgent(IChatClient? chatClient, ILogger logger)
    {
        if (chatClient == null) return null;

        var innerAgent = new ChatClientAgent(
            chatClient,
            instructions: """
                You are an alert triage agent. Analyze production alerts and determine:
                1. Is it actionable? (yes/no)
                2. Severity classification (P1-critical, P2-high, P3-medium, P4-low)
                3. Brief reason (1 sentence)

                Respond in this exact format:
                ACTIONABLE: yes/no
                SEVERITY: P1/P2/P3/P4
                REASON: <brief explanation>
                """,
            name: "AlertValidator",
            description: "Classifies alert severity and determines if actionable");

        return new AIAgentBuilder(innerAgent)
            .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Executor nodes
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Start node. Hosts a real MAF <see cref="ChatClientAgent"/> to classify the alert,
    /// then forwards the validated alert downstream. Deterministic fallback when no LLM.
    /// </summary>
    private sealed class ValidatorExecutor : Executor<string, string>
    {
        private readonly AIAgent? _agent;
        private readonly IChatClient? _chatClient;
        private readonly ILogger _logger;

        public ValidatorExecutor(string id, AIAgent? agent, IChatClient? chatClient, ILogger logger)
            : base(id)
        {
            _agent = agent;
            _chatClient = chatClient;
            _logger = logger;
        }

        public override async ValueTask<string> HandleAsync(
            string rawAlert, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[validator] 🤖 Running MAF ChatClientAgent...");

            if (_agent == null || _chatClient == null)
            {
                _logger.LogWarning("[validator] No IChatClient — using deterministic fallback");
                var isActionable = rawAlert.Contains("error", StringComparison.OrdinalIgnoreCase)
                                || rawAlert.Contains("sev", StringComparison.OrdinalIgnoreCase);
                return isActionable
                    ? $"✅ Alert validated as actionable (deterministic fallback).\n\n{rawAlert}"
                    : $"⚠️ Alert not classified as actionable (deterministic fallback), continuing for demo.\n\n{rawAlert}";
            }

            var response = await _agent.RunAsync($"Alert: {rawAlert}");
            var result = response.Text ?? "";
            _logger.LogInformation("[validator] → {Result}", result.Split('\n').FirstOrDefault()?.Trim() ?? "classified");

            return $"✅ Alert validated by MAF ChatClientAgent:\n{result}\n\nOriginal alert: {rawAlert}";
        }
    }

    /// <summary>
    /// The star of the graph: the Squad agent node. Runs the full Squad team (Copilot CLI +
    /// sub-agent dispatch) using the proven session API. Sub-agent activities surface as OTel
    /// spans under "Microsoft.Agents.AI.Squad" in the Aspire trace view.
    /// </summary>
    private sealed class SquadExecutor : Executor<string, string>
    {
        private readonly string _squadFolder;
        private readonly ILogger _logger;

        public SquadExecutor(string id, string squadFolder, ILogger logger) : base(id)
        {
            _squadFolder = squadFolder;
            _logger = logger;
        }

        public override async ValueTask<string> HandleAsync(
            string validatedAlert, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[squad-analysis] 🏗️ Squad Agent (MAF AIAgent) — delegating to AI team...");
            _logger.LogInformation("[squad-analysis] Team root: {TeamRoot}", _squadFolder);

            // Explicit parent span so the built-in Squad subagent spans ("Microsoft.Agents.AI.Squad")
            // nest under this executor in the Aspire trace view. MAF's workflow OTel does not make the
            // executor-invocation span the ambient Activity.Current during HandleAsync, so we start our own.
            using var squadActivity = s_workflowSource.StartActivity("squad-analysis");

            // Option B — live child spans, one per sub-agent tool call. The Squad SDK raises
            // ToolStart/ToolComplete trace events (out-of-process, on the CLI subprocess). We turn
            // each into an OTel span parented to squadActivity so the Aspire trace view shows what
            // the team is *doing* while it runs — not just an opaque "squad-analysis" span.
            // Keyed by ToolCallId so ToolStart and ToolComplete correlate.
            var toolSpans = new ConcurrentDictionary<string, Activity>();

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddSquadAgent(o =>
            {
                o.SquadFolderPath = _squadFolder;
                o.AgentName = "IncidentSquad";
                o.Instructions = "You are an incident response team coordinator. " +
                    "You MUST use the task tool to spawn specialist sub-agents for this work. " +
                    "Dispatch at least 2 agents in parallel: one for root cause analysis and one for remediation planning. " +
                    "Use agent_type: 'general-purpose' and mode: 'background' for each. " +
                    "Analyze this production alert, identify root cause, assess blast radius, and provide actionable remediation steps.";
                o.EmitSubagentActivities = true;
                // Option C — fine-grained trace events (tool calls + assistant messages), not just
                // sub-agent lifecycle. Required for ToolStart/ToolComplete/AssistantMessage to fire.
                o.TraceEvents = true;
                o.OnSubagentTrace = evt =>
                {
                    switch (evt.Kind)
                    {
                        case SquadAgentTraceEventKind.SubagentSelected:
                            _logger.LogInformation("[Squad] 🎯 Selected: {Name} — tools: {Tools}",
                                evt.DispatchedPersonaName ?? evt.SubagentName ?? "(coordinator)",
                                evt.RequestedToolNames is { Count: > 0 }
                                    ? string.Join(", ", evt.RequestedToolNames)
                                    : "(none)");
                            break;
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
                        case SquadAgentTraceEventKind.AssistantMessage:
                            if (!string.IsNullOrWhiteSpace(evt.Content))
                                _logger.LogInformation("[Squad] 💬 {Name}: {Message}",
                                    evt.SubagentName ?? "coordinator", Trim(evt.Content));
                            break;
                        case SquadAgentTraceEventKind.ToolStart:
                        {
                            var toolName = ResolveToolName(evt);
                            _logger.LogInformation("[Squad] 🔧 Tool start: {Tool} (subagent: {Sub})",
                                toolName, evt.SubagentName ?? "coordinator");

                            // Parent explicitly — the callback may run off the ambient Activity.Current.
                            var toolActivity = s_workflowSource.StartActivity(
                                $"tool:{toolName}", ActivityKind.Internal, squadActivity?.Context ?? default);
                            if (toolActivity is not null)
                            {
                                toolActivity.SetTag("squad.subagent", evt.SubagentName);
                                toolActivity.SetTag("squad.subagent.display", evt.SubagentDisplayName);
                                toolActivity.SetTag("squad.tool", toolName);
                                toolActivity.SetTag("squad.tool_call_id", evt.ToolCallId);
                                if (!string.IsNullOrWhiteSpace(evt.Content))
                                    toolActivity.SetTag("squad.tool.args", Trim(evt.Content, 400));

                                if (!string.IsNullOrEmpty(evt.ToolCallId))
                                    toolSpans[evt.ToolCallId] = toolActivity;
                                else
                                    toolActivity.Dispose(); // no correlation id — close immediately
                            }
                            break;
                        }
                        case SquadAgentTraceEventKind.ToolComplete:
                        {
                            var toolName = ResolveToolName(evt);
                            _logger.LogInformation("[Squad] ✔️  Tool done: {Tool} (success: {Success})",
                                toolName, evt.Success?.ToString() ?? "n/a");

                            if (!string.IsNullOrEmpty(evt.ToolCallId)
                                && toolSpans.TryRemove(evt.ToolCallId, out var toolActivity))
                            {
                                toolActivity.SetStatus(
                                    evt.Success == false ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
                                toolActivity.Dispose();
                            }
                            break;
                        }
                    }
                };
            });

            using var host = builder.Build();
            var squad = host.Services.GetRequiredService<SquadAgent>();
            _logger.LogInformation("[squad-analysis] Squad agent ready: {Name}", squad.Name);

            string analysisResult;
            try
            {
                var session = await squad.CreateSessionAsync();
                var responseText = new System.Text.StringBuilder();
                await foreach (var update in squad.RunStreamingAsync(validatedAlert, session))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        responseText.Append(update.Text);
                }

                analysisResult = responseText.ToString();
                if (string.IsNullOrWhiteSpace(analysisResult))
                    analysisResult = "(Squad agent returned empty response)";

                _logger.LogInformation("[squad-analysis] → Analysis complete ({Length} chars)", analysisResult.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[squad-analysis] Squad agent error");
                analysisResult = $"Analysis unavailable — Squad agent error: {ex.Message}";
            }
            finally
            {
                // Dispose any tool spans that never received a ToolComplete (e.g. cancellation).
                foreach (var leftover in toolSpans.Values)
                    leftover.Dispose();
                toolSpans.Clear();

                if (squad is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
            }

            return analysisResult;
        }

        /// <summary>Picks a human-readable tool name from whichever fields the SDK populated.</summary>
        private static string ResolveToolName(SquadAgentTraceEvent evt)
        {
            if (evt.RequestedToolNames is { Count: > 0 })
                return string.Join("+", evt.RequestedToolNames);
            if (!string.IsNullOrWhiteSpace(evt.SubagentName))
                return evt.SubagentName!;
            return "call";
        }

        /// <summary>Truncates long trace content so spans/logs stay readable.</summary>
        private static string Trim(string s, int max = 160)
            => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
    }

    /// <summary>
    /// Deterministic node: enriches the Squad analysis with operational context (no LLM).
    /// Its output is registered as an intermediate workflow output so the UI can show the
    /// enriched analysis distinctly from the final notification report.
    /// </summary>
    private sealed class EnricherExecutor : Executor<string, string>
    {
        private readonly ILogger _logger;

        public EnricherExecutor(string id, ILogger logger) : base(id) => _logger = logger;

        public override ValueTask<string> HandleAsync(
            string analysisResult, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[enricher] 📋 Enriching with operational data...");

            var enriched = analysisResult +
                "\n\n📊 Operational Context (auto-enriched):\n" +
                "   • SLA: 99.95% — current: 99.2% (BREACHED)\n" +
                "   • On-call: @sarah-ops (primary), @mike-sre (secondary)\n" +
                "   • Service tier: P1 (revenue-critical)\n" +
                "   • Last deploy: 45 min ago (commit abc123f)\n" +
                "   • Related incidents: INC-2847 (similar pattern, 2 weeks ago)\n";

            _logger.LogInformation("[enricher] → SLA breached, P1 tier, on-call info added");
            return new ValueTask<string>(enriched);
        }
    }

    /// <summary>
    /// Deterministic notification node — one instance per channel (fan-out target).
    /// Formats a channel-specific message from the enriched incident context.
    /// </summary>
    private sealed class NotifierExecutor : Executor<string, string>
    {
        private readonly string _channel;
        private readonly ILogger _logger;

        public NotifierExecutor(string id, string channel, ILogger logger) : base(id)
        {
            _channel = channel;
            _logger = logger;
        }

        public override ValueTask<string> HandleAsync(
            string enriched, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[{Id}] 📨 Formatting notification for {Channel}...", Id, _channel);

            var firstLine = enriched.Split('\n').FirstOrDefault()?.Trim() ?? "Incident";
            var message = _channel switch
            {
                "Slack #incidents" =>
                    $"• {_channel}: 🔴 P1 incident — SLA breached (99.2%). On-call @sarah-ops paged. {firstLine}",
                "PagerDuty" =>
                    $"• {_channel}: 🚨 P1 alert acknowledged — urgency HIGH, escalation policy engaged.",
                "StatusPage" =>
                    $"• {_channel}: ⚠️ Degraded performance — we're investigating and will post updates.",
                _ =>
                    $"• {_channel}: notification dispatched.",
            };

            return new ValueTask<string>(message);
        }
    }

    /// <summary>
    /// Fan-in barrier target. Accumulates the notification messages from all channels and
    /// yields a single combined report as the workflow's final output once all have arrived.
    /// </summary>
    [YieldsOutput(typeof(string))]
    private sealed class NotificationAggregator : Executor<string>
    {
        private readonly int _expected;
        private readonly List<string> _parts = new();

        public NotificationAggregator(string id, int expected) : base(id) => _expected = expected;

        public override async ValueTask HandleAsync(
            string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _parts.Add(message);
            if (_parts.Count >= _expected)
            {
                var report = "📨 Notifications dispatched to all channels:\n\n" + string.Join("\n", _parts);
                await context.YieldOutputAsync(report, cancellationToken);
            }
        }
    }
}
