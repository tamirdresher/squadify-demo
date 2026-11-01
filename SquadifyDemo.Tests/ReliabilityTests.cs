using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using SquadifyDemo.Agents;
using Xunit;

namespace SquadifyDemo.Tests;

public sealed class ReliabilityTests
{
    private static readonly WorkflowConfig Config = new(".");

    [Fact]
    public async Task PoolInitializationFailureReleasesWaitingRenters()
    {
        await using var pool = NewPool();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rent = pool.RentAsync(timeout.Token).AsTask();
        Assert.False(pool.IsReady);
        Assert.Equal("Initializing", pool.InitializationStatus);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WarmUpAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rent);
        Assert.Equal("Synthetic initialization failure", error.InnerException?.Message);
        Assert.False(pool.IsReady);
        Assert.Equal("Failed", pool.InitializationStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync(timeout.Token).AsTask());
    }

    [Fact]
    public async Task RentCanBeCancelledBeforeInitialization()
    {
        await using var pool = NewPool();
        using var cancellation = new CancellationTokenSource();
        var rent = pool.RentAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rent);
    }

    [Fact]
    public async Task CancelledInitializationDoesNotLeaveRentersWaiting()
    {
        await using var pool = NewPool();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.WarmUpAsync(cancellation.Token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync(timeout.Token).AsTask());
        Assert.False(timeout.IsCancellationRequested);
        Assert.False(pool.IsReady);
    }

    [Fact]
    public async Task ReadyPoolReusesSlotsAndWaitingRentCanBeCancelled()
    {
        await using var pool = new SquadAgentPool(Config, NullLogger<SquadAgentPool>.Instance, 1,
            _ => new PooledSquadAgent(new HostBuilder().Build(), new ChatClientAgent(new MockChatClient())));
        await pool.WarmUpAsync();
        Assert.True(pool.IsReady);
        Assert.Equal("Ready", pool.InitializationStatus);
        var first = await pool.RentAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = pool.RentAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        pool.Return(first);
        Assert.Same(first, await pool.RentAsync());
    }

    [Fact]
    public async Task WorkflowErrorEventCannotBecomeSuccessfulOutput()
    {
        var cause = new InvalidOperationException("Synthetic executor failure");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SquadifiedWorkflow.ReadOutputAsync(Events(new WorkflowErrorEvent(cause)), NullLogger.Instance));
        Assert.Same(cause, error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingFinalOutputFails(string? output)
    {
        var events = output is null ? Events() : Events(new WorkflowOutputEvent(output, "aggregator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SquadifiedWorkflow.ReadOutputAsync(events, NullLogger.Instance));
    }

    [Fact]
    public async Task FinalOutputIsPreserved()
    {
        var result = await SquadifiedWorkflow.ReadOutputAsync(
            Events(new WorkflowOutputEvent("Synthetic notifications", "aggregator")), NullLogger.Instance);
        Assert.Equal("Synthetic notifications", result);
    }

    [Fact]
    public async Task SuccessfulSyntheticSquadRunsThroughTheRealDag()
    {
        using var client = new StubChatClient(_ => Task.FromResult("Synthetic analysis"));
        var agent = new ChatClientAgent(client, name: "SyntheticSquad");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await SquadifiedWorkflow.RunWorkflowAsync("Synthetic error alert", ".", NullLogger.Instance,
            new MockChatClient(), agent, timeout.Token);
        Assert.Contains("Synthetic analysis", result);
        Assert.Contains("Slack", result);
        Assert.Contains("PagerDuty", result);
        Assert.Contains("StatusPage", result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SquadFailureOrEmptyResponseFailsTheRealDag(bool empty)
    {
        using var client = new StubChatClient(_ => empty
            ? Task.FromResult("")
            : Task.FromException<string>(new InvalidOperationException("Synthetic Squad failure")));
        var agent = new ChatClientAgent(client, name: "SyntheticSquad");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => SquadifiedWorkflow.RunWorkflowAsync(
            "Synthetic error alert", ".", NullLogger.Instance,
            new MockChatClient(), agent, timeout.Token));
        Assert.False(timeout.IsCancellationRequested, "Failure must propagate rather than wait for a timeout.");
        Assert.Contains(empty ? "no analysis" : "Synthetic Squad failure", error.ToString());
    }

    [Fact]
    public async Task DagCancellationReachesSquadStreaming()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new StubChatClient(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "";
        });
        var agent = new ChatClientAgent(client, name: "SyntheticSquad");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = SquadifiedWorkflow.RunWorkflowAsync("Synthetic error alert", ".", NullLogger.Instance,
            new MockChatClient(), agent, cancellation.Token);
        await entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData("exception", "Failed")]
    [InlineData("empty", "Failed")]
    [InlineData("success", "Completed")]
    public async Task RunnerReportsActualTerminalOutcome(string mode, string expected)
    {
        using var client = new StubChatClient(_ => mode switch
        {
            "exception" => Task.FromException<string>(new InvalidOperationException("Synthetic agent failure")),
            "empty" => Task.FromResult(""),
            _ => Task.FromResult("Synthetic result")
        });
        await using var pool = NewPool();
        var (runner, state, traces, incident) = NewRunner(client, pool);
        await runner.RunOneAsync(incident, CancellationToken.None);
        AssertTerminalState(state, traces, incident.Id, expected);
    }

    [Fact]
    public async Task RunnerCancellationIsNotCompletedOrSuccessfulTelemetry()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SquadifyTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new StubChatClient(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "";
        });
        await using var pool = NewPool();
        var (runner, state, traces, incident) = NewRunner(client, pool);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = runner.RunOneAsync(incident, cancellation.Token);
        await entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await run;
        AssertTerminalState(state, traces, incident.Id, "Cancelled");
        var activity = Assert.Single(activities, a => a.GetTagItem("incident.id")?.ToString() == incident.Id);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task RunnerFailsInsteadOfHangingWhenPoolInitializationFailed()
    {
        await using var pool = NewPool();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WarmUpAsync());
        var (runner, state, traces, incident) = NewRunner(new MockChatClient(), pool, demo: 3);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await runner.RunOneAsync(incident, timeout.Token);
        Assert.False(timeout.IsCancellationRequested);
        AssertTerminalState(state, traces, incident.Id, "Failed");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("9999999999999999999999999")]
    public async Task InvalidDemoQueryReturns400WithoutQueuing(string demo)
    {
        await using var app = await NewEndpointApp();
        var response = await app.GetTestClient().PostAsync(
            "/incidents/simulate?demo=" + Uri.EscapeDataString(demo), null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, Snapshot(app.Services.GetRequiredService<WorkflowRunnerState>().GetSnapshot())
            .GetProperty("activeCount").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?demo=1")]
    [InlineData("?demo=2")]
    [InlineData("?demo=3")]
    public async Task ValidDemoQueryStillQueues(string query)
    {
        await using var app = await NewEndpointApp();
        var response = await app.GetTestClient().PostAsync("/incidents/simulate" + query, null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task LivenessDoesNotClaimPoolReadiness()
    {
        await using var app = await NewEndpointApp();
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync("/health")).StatusCode);
        var ready = await app.GetTestClient().GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var pool = app.Services.GetRequiredService<SquadAgentPool>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WarmUpAsync());
        ready = await app.GetTestClient().GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("Failed", await ready.Content.ReadAsStringAsync());
    }

    private static SquadAgentPool NewPool() =>
        new(Config, NullLogger<SquadAgentPool>.Instance, 1,
            _ => throw new InvalidOperationException("Synthetic initialization failure"));

    private static (WorkflowTriggeredRunner, WorkflowRunnerState, WorkflowTraceStore, SimulatedIncident)
        NewRunner(IChatClient client, SquadAgentPool pool, int demo = 1)
    {
        var state = new WorkflowRunnerState();
        var traces = new WorkflowTraceStore();
        var telemetry = new SquadifyTelemetry();
        var trigger = new WorkflowTrigger(state, telemetry, NullLogger<WorkflowTrigger>.Instance);
        var incident = new SimulatedIncident(Guid.NewGuid().ToString("n"), "Synthetic incident", "Sev2", demo, DateTimeOffset.UtcNow);
        Assert.True(trigger.TryTrigger(incident));
        return (new WorkflowTriggeredRunner(state, trigger, telemetry, traces, Config, client, pool,
            NullLogger<WorkflowTriggeredRunner>.Instance), state, traces, incident);
    }

    private static void AssertTerminalState(WorkflowRunnerState state, WorkflowTraceStore traces, string id, string expected)
    {
        var status = Snapshot(state.GetSnapshot());
        Assert.Equal(0, status.GetProperty("activeCount").GetInt32());
        Assert.Equal(expected, status.GetProperty("recentRuns")[0].GetProperty("status").GetString());
        Assert.Equal(expected, Snapshot(traces.GetRun(id)!).GetProperty("status").GetString());
    }

    private static JsonElement Snapshot(object value) => JsonSerializer.SerializeToElement(value);

    private static async Task<WebApplication> NewEndpointApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<WorkflowRunnerState>();
        builder.Services.AddSingleton<WorkflowTraceStore>();
        builder.Services.AddSingleton<SquadifyTelemetry>();
        builder.Services.AddSingleton<WorkflowTrigger>();
        builder.Services.AddSingleton(_ => NewPool());
        var app = builder.Build();
        SquadifyWebProgram.MapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static async IAsyncEnumerable<WorkflowEvent> Events(params WorkflowEvent[] events)
    {
        foreach (var item in events) yield return item;
        await Task.CompletedTask;
    }

    private sealed class StubChatClient(Func<CancellationToken, Task<string>> respond) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => new(new ChatMessage(ChatRole.Assistant, await respond(cancellationToken)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await respond(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("Synthetic") : null;

        public void Dispose() { }
    }
}
