using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squad.Agents.AI;

namespace SquadifyDemo;

/// <summary>
/// A bounded pool of reusable <see cref="SquadAgent"/> instances.
///
/// <para>
/// Startup builds the agent hosts, not the Copilot runtime. The CLI starts lazily
/// on first session creation; successful slot initialization does not verify authentication
/// or model access. Later runs reuse the same agent with a fresh session.
/// </para>
///
/// <para>
/// Renting awaits initialization and an idle slot with cancellation. Initialization failure
/// releases waiters with an explicit error. This bounds Demo-3 agent use, not the HTTP backlog.
/// </para>
/// </summary>
public sealed class SquadAgentPool : IAsyncDisposable
{
    private readonly Channel<PooledSquadAgent> _idle;
    private readonly List<PooledSquadAgent> _all = new();
    private readonly string _squadFolder;
    private readonly ILogger<SquadAgentPool> _logger;
    private readonly int _size;
    private readonly Func<int, PooledSquadAgent> _buildAgent;
    private readonly TaskCompletionSource<Exception?> _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _initializationStarted;

    public SquadAgentPool(WorkflowConfig config, ILogger<SquadAgentPool> logger, int size)
    {
        _squadFolder = config.TeamRoot;
        _logger = logger;
        _size = Math.Max(1, size);
        _idle = Channel.CreateBounded<PooledSquadAgent>(_size);
        _buildAgent = BuildOne;
    }

    internal SquadAgentPool(WorkflowConfig config, ILogger<SquadAgentPool> logger, int size,
        Func<int, PooledSquadAgent> buildAgent) : this(config, logger, size)
        => _buildAgent = buildAgent;

    /// <summary>Number of agent slots this pool maintains (also the concurrency cap for Squad runs).</summary>
    public int Size => _size;

    public bool IsReady => _initialization.Task.IsCompletedSuccessfully && _initialization.Task.Result is null;
    public string InitializationStatus => !_initialization.Task.IsCompleted ? "Initializing" : IsReady ? "Ready" : "Failed";

    /// <summary>
    /// Builds all pooled agents in parallel and marks them idle. Called once by the
    /// hosted initializer at application startup. The first run still starts the CLI lazily.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initializationStarted, 1) != 0)
            throw new InvalidOperationException("Squad agent pool initialization has already started.");

        _logger.LogInformation("[SquadAgentPool] Preparing {Size} Squad agent slots (CLI starts lazily)...", _size);
        var started = Stopwatch.GetTimestamp();

        var builds = Enumerable.Range(0, _size)
            .Select(i => Task.Run(() => _buildAgent(i), cancellationToken))
            .ToArray();

        try
        {
            var agents = await Task.WhenAll(builds);
            _all.AddRange(agents);
            foreach (var agent in agents)
                await _idle.Writer.WriteAsync(agent, cancellationToken);

            _initialization.SetResult(null);
        }
        catch (Exception ex)
        {
            _initialization.TrySetResult(ex);
            _idle.Writer.TryComplete(ex);
            // Task.WhenAll has settled every build; release successful slots after partial failure.
            foreach (var build in builds.Where(t => t.IsCompletedSuccessfully))
                await build.Result.DisposeAsync();
            _all.Clear();
            throw;
        }

        _logger.LogInformation(
            "[SquadAgentPool] {Size} agent slots prepared in {Ms:F0}ms. Copilot authentication/runtime are not verified until first use.",
            _size, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        ProfileLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[pool] SLOT INITIALIZATION COMPLETE: {0} agents built in {1:F0}ms (parallel), runtime not verified",
            _size, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    /// <summary>Rents a warm agent. Blocks (backpressure) until one is idle.</summary>
    public async ValueTask<PooledSquadAgent> RentAsync(CancellationToken cancellationToken = default)
    {
        var error = await _initialization.Task.WaitAsync(cancellationToken);
        if (error is not null)
            throw new InvalidOperationException("Squad agent pool initialization failed.", error);
        return await _idle.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>Returns an agent to the idle set for reuse by the next run.</summary>
    public void Return(PooledSquadAgent agent) => _idle.Writer.TryWrite(agent);

    private PooledSquadAgent BuildOne(int index)
    {
        // ── TIMING INSTRUMENTATION ──
        // Splits BuildOne into its phases so we can see (in the logs) exactly where
        // the "warm-up" cost lands. Decompiling Squad.Agents.AI 0.5.5 showed that
        // AddSquadAgent → SquadAgent construction only does `new CopilotClient(options)`
        // and `client.AsAIAgent(sessionConfig)` — it does NOT call StartAsync. The CLI
        // runtime subprocess spawns LAZILY on the first CreateSessionAsync (see the
        // session-vs-stream timing in SquadifiedWorkflow.RunWithAgentAsync). So we expect
        // these phases to be cheap; the real cold-start lands on the first run.
        var swTotal = Stopwatch.StartNew();

        var swBuilder = Stopwatch.StartNew();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        swBuilder.Stop();

        var swAdd = Stopwatch.StartNew();
        builder.Services.AddSquadAgent(o =>
        {
            o.SquadFolderPath = _squadFolder;
            o.AgentName = $"IncidentSquad-{index}";
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
        swAdd.Stop();

        var swBuild = Stopwatch.StartNew();
        var host = builder.Build();
        swBuild.Stop();

        var swResolve = Stopwatch.StartNew();
        SquadAgent agent;
        try
        {
            agent = host.Services.GetRequiredService<SquadAgent>();
        }
        catch
        {
            host.Dispose();
            throw;
        }
        swResolve.Stop();

        swTotal.Stop();
        _logger.LogInformation(
        "[SquadAgentPool] Built agent slot {Index}: {Name} — total {Total:F0}ms " +
        "(builder {Builder:F0}ms, AddSquadAgent {Add:F0}ms, host.Build {Build:F0}ms, resolve {Resolve:F0}ms). " +
        "NOTE: runtime NOT started yet (lazy on first CreateSessionAsync).",
        index, agent.Name, swTotal.Elapsed.TotalMilliseconds,
        swBuilder.Elapsed.TotalMilliseconds, swAdd.Elapsed.TotalMilliseconds,
        swBuild.Elapsed.TotalMilliseconds, swResolve.Elapsed.TotalMilliseconds);
        ProfileLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[pool] slot {0} '{1}' build total={2:F0}ms (builder={3:F0} add={4:F0} host.Build={5:F0} resolve={6:F0}) [runtime NOT started yet]",
            index, agent.Name, swTotal.Elapsed.TotalMilliseconds,
            swBuilder.Elapsed.TotalMilliseconds, swAdd.Elapsed.TotalMilliseconds,
            swBuild.Elapsed.TotalMilliseconds, swResolve.Elapsed.TotalMilliseconds));
        return new PooledSquadAgent(host, agent);
    }

    public async ValueTask DisposeAsync()
    {
        _initialization.TrySetResult(new ObjectDisposedException(nameof(SquadAgentPool)));
        _idle.Writer.TryComplete();
        foreach (var agent in _all)
            await agent.DisposeAsync();
    }
}

/// <summary>
/// One warm slot in the <see cref="SquadAgentPool"/>: a kept-alive <see cref="IHost"/>
/// plus the <see cref="SquadAgent"/> resolved from it. The host is only disposed when
/// the pool itself is disposed at application shutdown — never per run.
/// </summary>
public sealed class PooledSquadAgent : IAsyncDisposable
{
    private readonly IHost _host;

    internal PooledSquadAgent(IHost host, AIAgent agent)
    {
        _host = host;
        Agent = agent;
    }

    /// <summary>The warm Squad agent. Create a fresh session per run via <c>Agent.CreateSessionAsync()</c>.</summary>
    public AIAgent Agent { get; }

    public async ValueTask DisposeAsync()
    {
        if (Agent is IAsyncDisposable disposableAgent)
            await disposableAgent.DisposeAsync();
        _host.Dispose();
    }
}

/// <summary>
/// Initializes reusable slots at startup without starting Copilot sessions or model calls.
/// </summary>
public sealed class SquadAgentPoolWarmer : BackgroundService
{
    private readonly SquadAgentPool _pool;
    private readonly ILogger<SquadAgentPoolWarmer> _logger;

    public SquadAgentPoolWarmer(SquadAgentPool pool, ILogger<SquadAgentPoolWarmer> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _pool.WarmUpAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down during warm-up — nothing to do
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SquadAgentPool] Initialization failed. Readiness is unavailable and waiting runs will fail.");
        }
    }
}
