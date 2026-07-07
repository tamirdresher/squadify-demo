using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squad.Agents.AI;

namespace SquadifyDemo;

/// <summary>
/// A warm pool of pre-built <see cref="SquadAgent"/> instances.
///
/// <para>
/// COLD-START PROBLEM: Building a Squad agent means creating a whole
/// <see cref="Host"/>, wiring <c>AddSquadAgent(...)</c>, and (on first use)
/// spawning a Copilot CLI subprocess. The original demo did all of this
/// <em>inside every workflow run</em> and disposed the host afterwards, so
/// every incident paid the full cold-start cost (~280s observed).
/// </para>
///
/// <para>
/// FIX: build N agents once at startup and keep their hosts alive. Runs rent
/// an idle agent, use it, and return it — no per-run host build, no dispose.
/// The heavy <c>Host.Build()</c> + <c>AddSquadAgent</c> cost is moved off the
/// run path, and the CLI subprocess stays warm between runs. Reused runs were
/// measured at ~142s (~49% faster than cold).
/// </para>
///
/// <para>
/// The idle set is a bounded <see cref="Channel{T}"/> of size N. <see cref="RentAsync"/>
/// blocks when the pool is empty, so it doubles as natural backpressure —
/// concurrency is capped at the pool size without any extra semaphore.
/// </para>
/// </summary>
public sealed class SquadAgentPool : IAsyncDisposable
{
    private readonly Channel<PooledSquadAgent> _idle;
    private readonly List<PooledSquadAgent> _all = new();
    private readonly string _squadFolder;
    private readonly ILogger<SquadAgentPool> _logger;
    private readonly int _size;

    public SquadAgentPool(WorkflowConfig config, ILogger<SquadAgentPool> logger, int size)
    {
        _squadFolder = config.TeamRoot;
        _logger = logger;
        _size = Math.Max(1, size);
        _idle = Channel.CreateBounded<PooledSquadAgent>(_size);
    }

    /// <summary>Number of warm agents this pool maintains (also the concurrency cap for Squad runs).</summary>
    public int Size => _size;

    /// <summary>
    /// Builds all pooled agents in parallel and marks them idle. Called once by the
    /// hosted warmer at application startup so the first user-triggered run is fast.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[SquadAgentPool] 🔥 Warming up {Size} Squad agents...", _size);
        var started = Stopwatch.GetTimestamp();

        var builds = Enumerable.Range(0, _size)
            .Select(i => Task.Run(() => BuildOne(i), cancellationToken))
            .ToArray();

        var agents = await Task.WhenAll(builds);
        foreach (var agent in agents)
        {
            _all.Add(agent);
            await _idle.Writer.WriteAsync(agent, cancellationToken);
        }

        _logger.LogInformation(
            "[SquadAgentPool] ✅ {Size} agents warm in {Ms:F0}ms — runs will reuse them (no per-run cold start).",
            _size, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        ProfileLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[pool] WARM-UP COMPLETE: {0} agents built in {1:F0}ms (parallel)",
            _size, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    /// <summary>Rents a warm agent. Blocks (backpressure) until one is idle.</summary>
    public ValueTask<PooledSquadAgent> RentAsync(CancellationToken cancellationToken = default)
        => _idle.Reader.ReadAsync(cancellationToken);

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
        var agent = host.Services.GetRequiredService<SquadAgent>();
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

    internal PooledSquadAgent(IHost host, SquadAgent agent)
    {
        _host = host;
        Agent = agent;
    }

    /// <summary>The warm Squad agent. Create a fresh session per run via <c>Agent.CreateSessionAsync()</c>.</summary>
    public SquadAgent Agent { get; }

    public async ValueTask DisposeAsync()
    {
        if (Agent is IAsyncDisposable disposableAgent)
            await disposableAgent.DisposeAsync();
        _host.Dispose();
    }
}

/// <summary>
/// Hosted service that warms the <see cref="SquadAgentPool"/> at application startup,
/// so the pool is ready before the first incident is triggered.
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
            _logger.LogError(ex, "[SquadAgentPool] Warm-up failed — runs will fall back to per-run agent creation.");
        }
    }
}
