using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SquadifyDemo.Agents;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace SquadifyDemo;

/// <summary>
/// Web-hosted workflow runner matching the .NET Aspire community toolkit pattern.
/// Exposes HTTP endpoints for triggering demos and reports status via /health and /status.
/// </summary>
public static class SquadifyWebProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ── OpenTelemetry (lights up in Aspire dashboard) ─────────────────
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("squadify-workflow"));
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.AddOtlpExporter(ConfigureOtlp);
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("squadify-workflow"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(SquadifyTelemetry.MeterName)
                .AddOtlpExporter(ConfigureOtlp))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(SquadifyTelemetry.ActivitySourceName)
                .AddSource("SquadifyDemo.Workflow")
                .AddSource(Squad.Agents.AI.SquadAgentDiagnostics.ActivitySourceName) // "Microsoft.Agents.AI.Squad" — built-in subagent spans
                // ── GenAI telemetry (standard MAF sources) — renders agent chat + model requests in the Aspire GenAI view ──
                .AddSource("Experimental.Microsoft.Extensions.AI")  // IChatClient / model-request spans
                .AddSource("Experimental.Microsoft.Agents.AI")      // agent-level spans
                .AddOtlpExporter(ConfigureOtlp));

        // ── Services ──────────────────────────────────────────────────────
        var teamRoot = ReadOption(args, "--team-root")
            ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, ".."));

        builder.Services.AddSingleton(new WorkflowConfig(teamRoot));
        builder.Services.AddSingleton<SquadifyTelemetry>();
        builder.Services.AddSingleton<WorkflowRunnerState>();
        builder.Services.AddSingleton<WorkflowTraceStore>();
        builder.Services.AddSingleton<WorkflowTrigger>();
        var useAzure = Environment.GetEnvironmentVariable("USE_AZURE_OPENAI") == "true";

        if (useAzure)
        {
            builder.Services.AddSingleton<IChatClient>(sp =>
            {
                var endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
                    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
                var key = builder.Configuration["AZURE_OPENAI_KEY"]
                    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") ?? "";

                return new AzureOpenAIClient(
                        new Uri(endpoint),
                        new System.ClientModel.ApiKeyCredential(key))
                    .GetChatClient("gpt-4o-mini")
                    .AsIChatClient()
                    .AsBuilder()
                    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)
                    .Build();
            });
        }
        else
        {
            // Foundry Local — uses the "chat" connection string from Aspire
            builder.Services.AddSingleton<IChatClient>(sp =>
            {
                var connStr = builder.Configuration.GetConnectionString("chat")
                    ?? Environment.GetEnvironmentVariable("ConnectionStrings__chat");

                if (!string.IsNullOrEmpty(connStr))
                {
                    // Foundry Local exposes an OpenAI-compatible endpoint
                    return new OpenAI.OpenAIClient(
                            new System.ClientModel.ApiKeyCredential("unused-key"),
                            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(connStr) })
                        .GetChatClient("phi-4")
                        .AsIChatClient()
                        .AsBuilder()
                        .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)
                        .Build();
                }

                return new MockChatClient()
                    .AsBuilder()
                    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)
                    .Build();
            });
        }
        builder.Services.AddHostedService<WorkflowTriggeredRunner>();

        // ── Build app ─────────────────────────────────────────────────────
        var app = builder.Build();

        // ── Static files (serves wwwroot/index.html as the demo UI) ──────
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // ── Endpoints (matching community toolkit pattern) ────────────────
        app.MapGet("/health", () => Results.Ok("healthy"));

        app.MapGet("/status", (WorkflowRunnerState state) => Results.Json(state.GetSnapshot()));

        app.MapGet("/trace", (WorkflowTraceStore traceStore) => Results.Json(traceStore.GetAllRuns()));

        app.MapGet("/trace/{runId}", (string runId, WorkflowTraceStore traceStore) =>
        {
            var run = traceStore.GetRun(runId);
            return run is not null ? Results.Json(run) : Results.NotFound();
        });

        app.MapPost("/incidents/simulate", (HttpRequest request, WorkflowTrigger trigger) =>
        {
            var title = request.Query["title"].FirstOrDefault()
                ?? "Checkout failures spiking on PaymentService. Error rate > 50%";
            var severity = request.Query["severity"].FirstOrDefault() ?? "Sev2";
            var demo = request.Query["demo"].FirstOrDefault() ?? "3";

            var incident = new SimulatedIncident(
                Guid.NewGuid().ToString("n"), title, severity, int.Parse(demo), DateTimeOffset.UtcNow);

            return trigger.TryTrigger(incident)
                ? Results.Accepted("/status", trigger.State.GetSnapshot())
                : Results.Conflict(trigger.State.GetSnapshot());
        });

        app.MapPost("/demo/{id:int}", (int id, WorkflowTrigger trigger) =>
        {
            if (id < 1 || id > 3) return Results.BadRequest("Demo must be 1, 2, or 3");

            var incident = new SimulatedIncident(
                Guid.NewGuid().ToString("n"),
                id switch
                {
                    1 => "Investigate checkout failures on PaymentService",
                    2 => "Checkout is failing. PaymentService error rate > 50%. Investigate and fix.",
                    _ => "ALERT: Checkout failures spiking on PaymentService. Error rate > 50%."
                },
                "Sev2", id, DateTimeOffset.UtcNow);

            return trigger.TryTrigger(incident)
                ? Results.Accepted("/status", trigger.State.GetSnapshot())
                : Results.Conflict(trigger.State.GetSnapshot());
        });

        await app.RunAsync();
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void ConfigureOtlp(OtlpExporterOptions options)
    {
        options.Protocol = OtlpExportProtocol.Grpc;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  Supporting types (matching community toolkit structure)
// ═══════════════════════════════════════════════════════════════════════════

public sealed record WorkflowConfig(string TeamRoot);

public sealed record SimulatedIncident(
    string Id, string Title, string Severity, int DemoNumber, DateTimeOffset TriggeredAt);

public sealed class SquadifyTelemetry
{
    public const string MeterName = "SquadifyDemo";
    public const string ActivitySourceName = "SquadifyDemo";

    private readonly Meter _meter = new(MeterName);
    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly Counter<long> _incidentsTriggered;
    private readonly Counter<long> _workflowsCompleted;

    public SquadifyTelemetry()
    {
        _incidentsTriggered = _meter.CreateCounter<long>("squadify.incidents.triggered");
        _workflowsCompleted = _meter.CreateCounter<long>("squadify.workflows.completed");
    }

    public void RecordIncidentTriggered(SimulatedIncident incident)
    {
        _incidentsTriggered.Add(1, new KeyValuePair<string, object?>("severity", incident.Severity));
    }

    public Activity? StartWorkflowActivity(SimulatedIncident incident)
    {
        var activity = _activitySource.StartActivity("SquadifyWorkflow", ActivityKind.Internal);
        activity?.SetTag("incident.id", incident.Id);
        activity?.SetTag("incident.severity", incident.Severity);
        activity?.SetTag("demo.number", incident.DemoNumber);
        return activity;
    }

    public void RecordWorkflowCompleted(SimulatedIncident incident, TimeSpan duration)
    {
        _workflowsCompleted.Add(1,
            new KeyValuePair<string, object?>("severity", incident.Severity),
            new KeyValuePair<string, object?>("demo", incident.DemoNumber));
    }
}

public sealed class WorkflowRunnerState
{
    private readonly object _gate = new();

    public WorkflowRunnerState()
    {
        Status = "WaitingForIncident";
        LastMessage = "POST /incidents/simulate to trigger the workflow. Or POST /demo/1, /demo/2, /demo/3.";
    }

    public string Status { get; private set; }
    public int? ExitCode { get; private set; }
    public string LastMessage { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public SimulatedIncident? CurrentIncident { get; private set; }
    public string? LastRunResult { get; private set; }

    public bool TryMarkQueued(SimulatedIncident incident)
    {
        lock (_gate)
        {
            if (Status is "Queued" or "Running") return false;
            Status = "Queued";
            StartedAt = null;
            CompletedAt = null;
            ExitCode = null;
            CurrentIncident = incident;
            LastMessage = $"Incident {incident.Id} queued — Demo {incident.DemoNumber} will start shortly.";
            return true;
        }
    }

    public void MarkRunning(SimulatedIncident incident)
    {
        lock (_gate)
        {
            Status = "Running";
            StartedAt = DateTimeOffset.UtcNow;
            LastMessage = $"Running Demo {incident.DemoNumber}: {incident.Title}";
        }
    }

    public void MarkCompleted(string? result = null)
    {
        lock (_gate)
        {
            Status = "Completed";
            ExitCode = 0;
            CompletedAt = DateTimeOffset.UtcNow;
            LastRunResult = result;
            LastMessage = "Workflow completed. POST /incidents/simulate to trigger again. GET /trace for results.";
        }
    }

    public void MarkFailed(Exception ex)
    {
        lock (_gate)
        {
            Status = "Failed";
            ExitCode = 2;
            CompletedAt = DateTimeOffset.UtcNow;
            LastMessage = ex.Message;
        }
    }

    public object GetSnapshot()
    {
        lock (_gate)
        {
            return new
            {
                service = "squadify-workflow",
                status = Status,
                exitCode = ExitCode,
                startedAt = StartedAt,
                completedAt = CompletedAt,
                message = LastMessage,
                incident = CurrentIncident,
                lastRunResult = LastRunResult,
                triggerEndpoint = "POST /incidents/simulate?severity=Sev2&title=Database%20latency&demo=3",
                traceEndpoint = "GET /trace"
            };
        }
    }
}

public sealed class WorkflowTrigger
{
    private readonly Channel<SimulatedIncident> _channel = Channel.CreateUnbounded<SimulatedIncident>();
    private readonly ILogger<WorkflowTrigger> _logger;
    private readonly SquadifyTelemetry _telemetry;

    public WorkflowTrigger(
        WorkflowRunnerState state,
        SquadifyTelemetry telemetry,
        ILogger<WorkflowTrigger> logger)
    {
        State = state;
        _telemetry = telemetry;
        _logger = logger;
    }

    public WorkflowRunnerState State { get; }
    public ChannelReader<SimulatedIncident> Incidents => _channel.Reader;

    public bool TryTrigger(SimulatedIncident incident)
    {
        if (!State.TryMarkQueued(incident)) return false;

        if (_channel.Writer.TryWrite(incident))
        {
            _telemetry.RecordIncidentTriggered(incident);
            _logger.LogInformation(
                "Queued incident {IncidentId} for Demo {Demo} — {Title}",
                incident.Id, incident.DemoNumber, incident.Title);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Stores workflow run results and step-by-step traces so they can be
/// retrieved later via GET /trace and GET /trace/{runId}.
/// </summary>
public sealed class WorkflowTraceStore
{
    private readonly object _gate = new();
    private readonly List<WorkflowRunTrace> _runs = new();

    public WorkflowRunTrace StartRun(SimulatedIncident incident)
    {
        var run = new WorkflowRunTrace(incident.Id, incident.DemoNumber, incident.Title, incident.Severity);
        lock (_gate) { _runs.Add(run); }
        return run;
    }

    public IReadOnlyList<object> GetAllRuns()
    {
        lock (_gate) { return _runs.Select(r => r.ToSnapshot()).ToList(); }
    }

    public object? GetRun(string runId)
    {
        lock (_gate) { return _runs.FirstOrDefault(r => r.RunId == runId)?.ToSnapshot(); }
    }

    public object? GetLatestRun()
    {
        lock (_gate) { return _runs.LastOrDefault()?.ToSnapshot(); }
    }
}

public sealed class WorkflowRunTrace
{
    private readonly object _gate = new();
    private readonly List<WorkflowStepResult> _steps = new();

    public WorkflowRunTrace(string runId, int demoNumber, string title, string severity)
    {
        RunId = runId;
        DemoNumber = demoNumber;
        Title = title;
        Severity = severity;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string RunId { get; }
    public int DemoNumber { get; }
    public string Title { get; }
    public string Severity { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FinalResult { get; private set; }
    public string Status { get; private set; } = "Running";

    public void AddStep(string stepName, string stepType, string output)
    {
        lock (_gate)
        {
            _steps.Add(new WorkflowStepResult(stepName, stepType, output, DateTimeOffset.UtcNow));
        }
    }

    public void Complete(string? finalResult = null)
    {
        lock (_gate)
        {
            CompletedAt = DateTimeOffset.UtcNow;
            FinalResult = finalResult;
            Status = "Completed";
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            CompletedAt = DateTimeOffset.UtcNow;
            FinalResult = error;
            Status = "Failed";
        }
    }

    public object ToSnapshot()
    {
        lock (_gate)
        {
            return new
            {
                runId = RunId,
                demo = DemoNumber,
                title = Title,
                severity = Severity,
                status = Status,
                startedAt = StartedAt,
                completedAt = CompletedAt,
                durationMs = CompletedAt.HasValue ? (CompletedAt.Value - StartedAt).TotalMilliseconds : null as double?,
                finalResult = FinalResult,
                steps = _steps.Select(s => new
                {
                    step = s.StepName,
                    type = s.StepType,
                    output = s.Output,
                    timestamp = s.Timestamp
                }).ToList()
            };
        }
    }
}

public sealed record WorkflowStepResult(string StepName, string StepType, string Output, DateTimeOffset Timestamp);

/// <summary>
/// Background service that processes workflow triggers — matches the
/// RealSquadTriggeredRunner pattern from the community toolkit.
/// </summary>
public sealed class WorkflowTriggeredRunner : BackgroundService
{
    private readonly WorkflowRunnerState _state;
    private readonly WorkflowTrigger _trigger;
    private readonly SquadifyTelemetry _telemetry;
    private readonly WorkflowTraceStore _traceStore;
    private readonly WorkflowConfig _config;
    private readonly IChatClient _chatClient;
    private readonly ILogger<WorkflowTriggeredRunner> _logger;

    public WorkflowTriggeredRunner(
        WorkflowRunnerState state,
        WorkflowTrigger trigger,
        SquadifyTelemetry telemetry,
        WorkflowTraceStore traceStore,
        WorkflowConfig config,
        IChatClient chatClient,
        ILogger<WorkflowTriggeredRunner> logger)
    {
        _state = state;
        _trigger = trigger;
        _telemetry = telemetry;
        _traceStore = traceStore;
        _config = config;
        _chatClient = chatClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorkflowTriggeredRunner started. Team root: {TeamRoot}", _config.TeamRoot);

        await foreach (var incident in _trigger.Incidents.ReadAllAsync(stoppingToken))
        {
            _state.MarkRunning(incident);
            var runTrace = _traceStore.StartRun(incident);
            using var activity = _telemetry.StartWorkflowActivity(incident);
            var started = Stopwatch.GetTimestamp();

            _logger.LogInformation(
                "Starting Demo {Demo} for incident {Id}: {Title}",
                incident.DemoNumber, incident.Id, incident.Title);

            try
            {
                await RunDemoAsync(incident, runTrace, stoppingToken);
                runTrace.Complete(runTrace.FinalResult);
                var duration = Stopwatch.GetElapsedTime(started);
                _state.MarkCompleted(runTrace.FinalResult);
                _telemetry.RecordWorkflowCompleted(incident, duration);
                activity?.SetStatus(ActivityStatusCode.Ok);
                _logger.LogInformation(
                    "Demo {Demo} completed in {Duration:F0}ms",
                    incident.DemoNumber, duration.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                runTrace.Complete("Cancelled");
                _state.MarkCompleted();
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                runTrace.Fail(ex.Message);
                _state.MarkFailed(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Demo {Demo} failed for incident {Id}", incident.DemoNumber, incident.Id);
            }
        }
    }

    private async Task RunDemoAsync(SimulatedIncident incident, WorkflowRunTrace trace, CancellationToken ct)
    {
        switch (incident.DemoNumber)
        {
            case 1:
                await RunDemo1Async(incident, trace);
                break;
            case 2:
                await RunDemo2Async(incident, trace);
                break;
            case 3:
            default:
                await RunDemo3Async(incident, trace);
                break;
        }
    }

    /// <summary>Demo 1: Single Agent with Tools</summary>
    private async Task RunDemo1Async(SimulatedIncident incident, WorkflowRunTrace trace)
    {
        _logger.LogInformation("━━━ Demo 1: Single Agent with Tools ━━━");

        var analyzer = IncidentWorkflow.CreateLogAnalyzer(_chatClient);
        var sb = new System.Text.StringBuilder();

        await foreach (var update in analyzer.RunStreamingAsync(incident.Title))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
                Console.Out.Write(update.Text);
            }
        }

        Console.Out.WriteLine();
        var result = sb.ToString();
        trace.AddStep("LogAnalyzer", "AIAgent", result);
        trace.Complete(result);
    }

    /// <summary>Demo 2: Sequential Workflow (Analyze → Remediate)</summary>
    private async Task RunDemo2Async(SimulatedIncident incident, WorkflowRunTrace trace)
    {
        _logger.LogInformation("━━━ Demo 2: Sequential Workflow (Analyze → Fix) ━━━");

        var pipeline = IncidentWorkflow.BuildSequentialPipeline(_chatClient);
        var sb = new System.Text.StringBuilder();

        await foreach (var update in pipeline.RunStreamingAsync(incident.Title))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
                Console.Out.Write(update.Text);
            }
        }

        Console.Out.WriteLine();
        var result = sb.ToString();
        trace.AddStep("SequentialPipeline", "Workflow", result);
        trace.Complete(result);
    }

    /// <summary>
    /// Demo 3: Squadified Pipeline — REAL MAF Workflow with Squad Agent + code steps
    /// connected via WorkflowBuilder edges. This is the key demo showing Squad
    /// integrated as an executor node alongside deterministic code executors.
    /// </summary>
    private async Task RunDemo3Async(SimulatedIncident incident, WorkflowRunTrace trace)
    {
        _logger.LogInformation("━━━ Demo 3: Squadified MAF Workflow (WorkflowBuilder) ━━━");
        _logger.LogInformation("  Building DAG: [Validator] → [SquadAnalysis] → [Enricher] → fan-out{Slack,PagerDuty,StatusPage} → barrier → [Aggregator]");

        trace.AddStep("WorkflowBuilder", "Infrastructure", "Building MAF DAG: 7 executors, edges + fan-out + fan-in barrier, WithOpenTelemetry");

        var alertInput = $"ALERT [{incident.Severity}]: {incident.Title} (id={incident.Id})";
        var result = await SquadifiedWorkflow.RunWorkflowAsync(alertInput, _config.TeamRoot, _logger, _chatClient);

        trace.AddStep("AlertValidator", "MAF ChatClientAgent (Azure OpenAI)", "LLM classified alert severity");
        trace.AddStep("SquadAnalysis", "Squad Agent node (Copilot CLI)", "Squad team analyzed via sub-agents");
        trace.AddStep("ContextEnricher", "Code executor (deterministic)", "Enriched with SLA/on-call data — intermediate output");
        trace.AddStep("Notify (fan-out)", "3 code executors (deterministic)", "Slack + PagerDuty + StatusPage formatted in parallel");
        trace.AddStep("Aggregator (fan-in barrier)", "Code executor", "Combined all channels → final workflow output");

        _logger.LogInformation("✅ Squadified MAF Workflow complete — all executors ran via edges.");
        trace.Complete(result);
    }
}
