using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

internal sealed class PausingProvider(ControlStore store, IProviderEventSink sink, ICallTerminator terminator) : ICallProviderFactory
{
    public bool BlockDial { get; set; }
    public TaskCompletionSource DialEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DialContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ToolEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ToolContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DialCount;
    public int HangupCount;
    public string Mode => "local-fake";
    public Task<ProviderCapabilities> CheckReadinessAsync(bool online, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCapabilities("local-fake", true, false, true, "local-fake", "simulation", []));
    public Task<ICallConnection> PrepareAsync(CallContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ICallConnection>(new Connection(this, context.CallId, store, sink, terminator));

    private sealed class Connection(PausingProvider owner, string callId, ControlStore store,
        IProviderEventSink sink, ICallTerminator terminator) : ICallConnection
    {
        public async Task<ProviderHandle> DialAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.DialCount);
            await store.TransactionAsync(async tx =>
            {
                await tx.SaveFakeCallAsync(new FakeCallRecord(callId, "fake:" + callId, "connected", 1, 0, false, tx.Now, null));
                return true;
            });
            owner.DialEntered.TrySetResult();
            if (owner.BlockDial) await owner.DialContinue.Task;
            await sink.PublishAsync(callId, new ProviderSignal("connected", new JsonObject { ["connection_id"] = "fake:" + callId }));
            return new ProviderHandle("fake:" + callId);
        }
        public Task InstructAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDtmfAsync(string digits, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task CompleteToolAsync(string toolCallId, JsonObject result, CancellationToken cancellationToken)
        {
            owner.ToolEntered.TrySetResult();
            await owner.ToolContinue.Task;
        }
        public Task<TerminationEvidence> HangupAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.HangupCount);
            return terminator.TerminateAsync("fake:" + callId, cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class PreemptionTests
{
    private static void ReplaceProvider(IServiceCollection services)
    {
        services.RemoveAll<ICallProviderFactory>();
        services.AddSingleton<PausingProvider>();
        services.AddSingleton<ICallProviderFactory>(provider => provider.GetRequiredService<PausingProvider>());
    }

    [Fact]
    public async Task AnApprovalThatExpiresInsideAStalledContinuationFailsClosed()
    {
        await using var h = new Harness(s => s.Fake.ApprovalTimeoutMilliseconds = 150);
        await h.InitializeAsync(ReplaceProvider);
        var provider = h.Services.GetRequiredService<PausingProvider>();
        try
        {
            var receipt = await h.StartAsync();
            await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
            var approval = await h.ApprovalAsync(receipt.CallId!);
            var resolution = await h.CommandAsync("approvals.resolve", receipt.CallId!, h.Decision(approval));
            await provider.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
            var result = await h.Runtime.CommandAsync(h.Owner, resolution.CommandId);
            Assert.Equal("failed", result.Status);
            Assert.Equal("STALE_APPROVAL", result.Error!.Code);
            Assert.False(provider.ToolContinue.Task.IsCompleted);
        }
        finally { provider.ToolContinue.TrySetResult(); }
    }

    [Fact]
    public async Task StopPreemptsAnUncooperativeApprovalContinuation()
    {
        await using var h = new Harness();
        await h.InitializeAsync(ReplaceProvider);
        var provider = h.Services.GetRequiredService<PausingProvider>();
        try
        {
            var receipt = await h.StartAsync();
            await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
            var approval = await h.ApprovalAsync(receipt.CallId!);
            await h.CommandAsync("approvals.resolve", receipt.CallId!, h.Decision(approval));
            await provider.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopwatch = Stopwatch.StartNew();
            var stop = await h.CommandAsync("calls.stop", receipt.CallId!);
            Assert.Equal("accepted", stop.Status);
            await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.True(provider.HangupCount > 0);
            Assert.False(provider.ToolContinue.Task.IsCompleted);
        }
        finally { provider.ToolContinue.TrySetResult(); }
    }

    [Fact]
    public async Task LateUncancelableDialReturnIsRetainedAndHungUpWithoutRedial()
    {
        await using var h = new Harness();
        await h.InitializeAsync(ReplaceProvider);
        var provider = h.Services.GetRequiredService<PausingProvider>();
        provider.BlockDial = true;
        try
        {
            var receipt = await h.StartAsync();
            await provider.DialEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await h.Runtime.RevokeAsync(h.Owner, h.Session.SessionId);
            await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
            provider.DialContinue.TrySetResult();
            await Harness.EventuallyAsync(async () =>
                (await h.Store.TransactionAsync(tx => tx.CallAsync(receipt.CallId!)))?.DispatchStatus == "returned");
            var record = await h.Store.TransactionAsync(tx => tx.CallAsync(receipt.CallId!));
            Assert.Equal("fake:" + receipt.CallId, record!.Handle!.ConnectionId);
            Assert.Equal("ended", record.State.Lifecycle);
            Assert.Equal(1, provider.DialCount);
            Assert.True(provider.HangupCount >= 1);
        }
        finally { provider.DialContinue.TrySetResult(); }
    }

    [Fact]
    public async Task ANewRuntimeTerminatesInsteadOfResumingExistingConversation()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        await h.Store.TransactionAsync(tx => tx.ExpireWorkerAsync(h.Runtime.WorkerId));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(h.Clock);
        services.AddTpcliControlStore(h.Settings).AddTpcliCallRuntime();
        await using var restartedServices = services.BuildServiceProvider();
        var restarted = restartedServices.GetRequiredService<CallRuntime>();
        await restarted.InitializeAsync();
        await restarted.TickAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await restartedServices.GetRequiredService<TerminationEngine>().DrainAsync(timeout.Token);
        var state = await restarted.StateAsync(h.Owner, receipt.CallId!);
        Assert.Equal("ended", state.Lifecycle);
        Assert.Equal("worker_lost", state.TerminationReason);
        Assert.Equal(1, (await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId)))!.DialCount);
        Assert.DoesNotContain((await restarted.EventsAsync(h.Owner, receipt.CallId!)).Events, e => e.Type == "transcript.final");
    }

    [Fact]
    public async Task SeparatelyExecutedWatchdogTerminatesUsingOnlyTheSharedDatabase()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        await h.Store.TransactionAsync(async tx =>
        {
            var call = (await tx.CallAsync(receipt.CallId!))!;
            call.State = call.State with { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) };
            await tx.SaveCallAsync(call);
            var session = (await tx.SessionAsync(h.Session.SessionId))!;
            await tx.SaveSessionAsync(session with { LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });
            await tx.ExpireWorkerAsync(h.Runtime.WorkerId);
            return true;
        });
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var watchdog = Path.Combine(runtimeRoot, "Tpcli.Watchdog", "bin", configuration, "net10.0", "Tpcli.Watchdog.dll");
        Assert.True(File.Exists(watchdog), "The referenced independent watchdog executable must be built.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = h.DirectoryPath
        };
        start.ArgumentList.Add(watchdog);
        start.ArgumentList.Add("--once");
        start.Environment["Tpcli__Mode"] = "local-fake";
        start.Environment["Tpcli__Store__Provider"] = "sqlite";
        start.Environment["Tpcli__Store__ConnectionString"] = h.Settings.Store.ConnectionString;
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.ExitCode == 0, "Independent watchdog failed: " + await errors + await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        var state = await h.Runtime.StateAsync(h.Owner, receipt.CallId!);
        Assert.Equal("ended", state.Lifecycle);
        Assert.Equal("worker_lost", state.TerminationReason);
        var attempt = Assert.Single(await h.Store.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)));
        Assert.StartsWith("watchdog_", attempt.Executor, StringComparison.Ordinal);
        Assert.Equal("confirmed", attempt.Status);
        Assert.Equal("local-fake", attempt.ProviderMode);
    }
}
