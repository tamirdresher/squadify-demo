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
    /// Builds and runs the squadified MAF workflow.
    /// </summary>
    /// <param name="pooledSquadAgent">
    /// An optional pre-warmed <see cref="SquadAgent"/> rented from the <c>SquadAgentPool</c>.
    /// When supplied, the Squad node reuses it (no per-run host build, no dispose) — the
    /// cold-start optimization. When null, the Squad node builds and disposes its own agent
    /// (the original cold path, used as a fallback).
    /// </param>
    public static async Task<string> RunWorkflowAsync(
        string alertInput, string squadFolder, ILogger logger,
        IChatClient? chatClient = null, SquadAgent? pooledSquadAgent = null)
    {
        logger.LogInformation("Building squadified MAF workflow (WorkflowBuilder DAG)...");
        logger.LogInformation("  Graph: [validator] → [squad-analysis] → [enricher] → fan-out{{slack,pagerduty,statuspage}} → barrier → [aggregator]");

        // ── Build the executor nodes as FACTORY-BOUND bindings ──
        // MAF's Concurrent execution environment (InProcessExecution.Concurrent) refuses to run
        // a graph whose executors are shared, pre-instantiated instances — it demands each
        // executor be either "cross-run share-capable" or "factory-created" so every concurrent
        // run gets its own fresh, isolated executor. We register each node via a factory delegate
        // (Func<id, sessionId, ValueTask<TExecutor>>) using ExecutorBinding's BindExecutor helper.
        // The per-run inputs (chatClient, squadFolder, pooledSquadAgent, logger) are captured in
        // the closures; because RunWorkflowAsync builds a fresh workflow per run, each run's
        // factories close over that run's own rented pooled agent.
        ExecutorBinding validator =
            new Func<string, string, ValueTask<ValidatorExecutor>>(
                (id, _) => new ValueTask<ValidatorExecutor>(
                    new ValidatorExecutor(id, BuildValidatorAgent(chatClient, logger), chatClient, logger)))
            .BindExecutor("validator");

        ExecutorBinding squad =
            new Func<string, string, ValueTask<SquadExecutor>>(
                (id, _) => new ValueTask<SquadExecutor>(
                    new SquadExecutor(id, squadFolder, logger, pooledSquadAgent)))
            .BindExecutor("squad-analysis");

        ExecutorBinding enricher =
            new Func<string, string, ValueTask<EnricherExecutor>>(
                (id, _) => new ValueTask<EnricherExecutor>(new EnricherExecutor(id, logger)))
            .BindExecutor("enricher");

        ExecutorBinding slack =
            new Func<string, string, ValueTask<NotifierExecutor>>(
                (id, _) => new ValueTask<NotifierExecutor>(
                    new NotifierExecutor(id, "Slack #incidents", logger)))
            .BindExecutor("notify-slack");

        ExecutorBinding pagerDuty =
            new Func<string, string, ValueTask<NotifierExecutor>>(
                (id, _) => new ValueTask<NotifierExecutor>(
                    new NotifierExecutor(id, "PagerDuty", logger)))
            .BindExecutor("notify-pagerduty");

        ExecutorBinding statusPage =
            new Func<string, string, ValueTask<NotifierExecutor>>(
                (id, _) => new ValueTask<NotifierExecutor>(
                    new NotifierExecutor(id, "StatusPage", logger)))
            .BindExecutor("notify-statuspage");

        ExecutorBinding aggregator =
            new Func<string, string, ValueTask<NotificationAggregator>>(
                (id, _) => new ValueTask<NotificationAggregator>(
                    new NotificationAggregator(id, expected: 3)))
            .BindExecutor("aggregator");

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

        // Use the purpose-built Concurrent execution environment so multiple workflow
        // runs can execute in parallel. Each run builds a FRESH workflow instance above,
        // and the Concurrent environment (enableConcurrentRuns: true) does not take an
        // exclusive ownership lock on the workflow — so concurrent runs don't serialize.
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, alertInput);
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
        private readonly SquadAgent? _pooledAgent;

        public SquadExecutor(string id, string squadFolder, ILogger logger, SquadAgent? pooledAgent = null) : base(id)
        {
            _squadFolder = squadFolder;
            _logger = logger;
            _pooledAgent = pooledAgent;
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

            // WARM PATH: reuse a pre-built agent rented from the pool. No host build, no dispose —
            // the pool owns the host lifetime. This is the cold-start optimization.
            if (_pooledAgent is not null)
            {
                _logger.LogInformation("[squad-analysis] ♻️  Reusing warm pooled Squad agent: {Name}", _pooledAgent.Name);
                return await RunWithAgentAsync(_pooledAgent, validatedAlert);
            }

            // COLD PATH (fallback): build a dedicated host + agent for this run and dispose it after.
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
            });

            using var host = builder.Build();
            var squad = host.Services.GetRequiredService<SquadAgent>();
            _logger.LogInformation("[squad-analysis] Squad agent ready: {Name}", squad.Name);

            try
            {
                return await RunWithAgentAsync(squad, validatedAlert);
            }
            finally
            {
                if (squad is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
            }
        }

        /// <summary>
        /// Shared run logic: open a fresh session on the given Squad agent, stream the analysis,
        /// and return the accumulated text. Used by both the warm (pooled) and cold (build-own) paths.
        /// </summary>
        private async Task<string> RunWithAgentAsync(SquadAgent squad, string validatedAlert)
        {
            try
            {
                var session = await squad.CreateSessionAsync();
                var responseText = new System.Text.StringBuilder();
                await foreach (var update in squad.RunStreamingAsync(validatedAlert, session))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        responseText.Append(update.Text);
                }

                var analysisResult = responseText.ToString();
                if (string.IsNullOrWhiteSpace(analysisResult))
                    analysisResult = "(Squad agent returned empty response)";

                _logger.LogInformation("[squad-analysis] → Analysis complete ({Length} chars)", analysisResult.Length);
                return analysisResult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[squad-analysis] Squad agent error");
                return $"Analysis unavailable — Squad agent error: {ex.Message}";
            }
        }
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
