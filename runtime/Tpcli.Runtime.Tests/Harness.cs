using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

internal sealed class TestClock : TimeProvider
{
    private long milliseconds = new DateTimeOffset(2026, 9, 21, 17, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));
    public void Advance(TimeSpan duration) => Interlocked.Add(ref milliseconds, (long)duration.TotalMilliseconds);
}

internal sealed class Harness : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    public RuntimeSettings Settings { get; }
    public TestClock Clock { get; } = new();
    public Caller Owner { get; } = new("test-tenant", "test-principal");
    public ServiceProvider Services { get; private set; } = null!;
    public CallRuntime Runtime => Services.GetRequiredService<CallRuntime>();
    public ControlStore Store => Services.GetRequiredService<ControlStore>();
    public EventJournal Journal => Services.GetRequiredService<EventJournal>();
    public SessionInfo Session { get; private set; } = null!;
    public string ConnectionId { get; } = "test-owner";

    public Harness(Action<RuntimeSettings>? configure = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        Settings = new RuntimeSettings
        {
            Mode = "local-fake",
            Store = new StoreSettings { Provider = "sqlite", ConnectionString = "Data Source=" + Path.Combine(DirectoryPath, "control.db") },
            Fake = new FakeSettings { ConnectDelayMilliseconds = 5 },
            FakeToken = new string('t', 40)
        };
        configure?.Invoke(Settings);
    }
    public async Task InitializeAsync(Action<IServiceCollection>? customize = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddTpcliControlStore(Settings).AddTpcliCallRuntime();
        customize?.Invoke(services);
        Services = services.BuildServiceProvider();
        await Runtime.InitializeAsync();
        Session = await Runtime.CreateSessionAsync(Owner);
        await Runtime.AttachAsync(Owner, Session.SessionId, ConnectionId, CancellationToken.None);
    }

    public CommandRequest StartRequest(string? key = null, string task = "Ask for public opening hours.") =>
        new(Protocol.Version, Session.SessionId, "calls.start", key ?? Guid.NewGuid().ToString("N"),
            new JsonObject { ["target"] = "pstn:+12025550123", ["task"] = task, ["max_duration_seconds"] = 30 });
    public Task<CommandReceipt> StartAsync(string? key = null, string task = "Ask for public opening hours.") =>
        Runtime.SubmitAsync(Owner, StartRequest(key, task));
    public CommandRequest Request(string operation, string callId, JsonObject? payload = null, string? key = null) =>
        new(Protocol.Version, Session.SessionId, operation, key ?? Guid.NewGuid().ToString("N"), payload ?? [], callId);
    public Task<CommandReceipt> CommandAsync(string operation, string callId, JsonObject? payload = null) =>
        Runtime.SubmitAsync(Owner, Request(operation, callId, payload));
    public Task SignalAsync(string callId, string type, JsonObject? payload = null, string? id = null) =>
        Runtime.PublishAsync(callId, new ProviderSignal(type, payload ?? [], id));
    public Task ToolAsync(string callId, string name, JsonObject arguments, string? toolId = null, string? providerEventId = null) =>
        SignalAsync(callId, "tool.requested", new JsonObject
        {
            ["tool_call_id"] = toolId ?? Guid.NewGuid().ToString("N"),
            ["name"] = name,
            ["arguments"] = arguments
        }, providerEventId);
    public async Task<CallState> WaitStateAsync(string callId, Func<CallState, bool> predicate)
    {
        CallState? latest = null;
        await EventuallyAsync(async () =>
        {
            latest = await Runtime.StateAsync(Owner, callId);
            return predicate(latest);
        });
        return latest!;
    }
    public async Task<ApprovalView> ApprovalAsync(string callId, string description = "Confirm a test appointment.", string toolId = "test-approval")
    {
        await ToolAsync(callId, "request_approval", new JsonObject
        {
            ["action"] = "confirm_appointment",
            ["description"] = description,
            ["material_terms"] = new JsonObject { ["time"] = "10:00", ["charge"] = 0 }
        }, toolId);
        ApprovalView? result = null;
        await EventuallyAsync(async () =>
        {
            result = (await Runtime.ApprovalsAsync(Owner, callId)).LastOrDefault(a => a.Status == "pending");
            return result is not null;
        });
        return result!;
    }
    public JsonObject Decision(ApprovalView approval, string decision = "approve") => new()
    {
        ["approval_id"] = approval.ApprovalId,
        ["action_hash"] = approval.ActionHash,
        ["decision"] = decision
    };
    public static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutMilliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Condition did not become true within the bounded test timeout.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Services is not null)
        {
            await Runtime.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await Services.GetRequiredService<TerminationEngine>().DrainAsync(timeout.Token);
            await Services.DisposeAsync();
        }
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
