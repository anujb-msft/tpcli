using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed class FakeCallTerminator(ControlStore store) : ICallTerminator
{
    public Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken) =>
        store.TransactionAsync(async tx =>
        {
            if (!connectionId.StartsWith("fake:", StringComparison.Ordinal)) return TerminationEvidence.Unknown;
            var simulation = await tx.FakeCallAsync(connectionId);
            if (simulation is null) return TerminationEvidence.Unknown;
            var evidence = simulation.Status == "terminated" || !simulation.HangupUnknown
                ? TerminationEvidence.Confirmed : TerminationEvidence.Unknown;
            await tx.SaveFakeCallAsync(simulation with
            {
                HangupCount = simulation.HangupCount + 1,
                Status = evidence == TerminationEvidence.Confirmed ? "terminated" : simulation.Status,
                TerminatedAt = evidence == TerminationEvidence.Confirmed ? tx.Now : simulation.TerminatedAt
            });
            return evidence;
        }, cancellationToken);
}

public sealed class FakeCallProvider(
    RuntimeSettings settings, ControlStore store, IProviderEventSink sink, ICallTerminator terminator) : ICallProviderFactory
{
    public string Mode => "local-fake";
    public Task<ProviderCapabilities> CheckReadinessAsync(bool online, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCapabilities(Mode, true, settings.Fake.TeamsEnabled, true,
            "local-fake", "simulation",
            [new("local-fake", "simulation", "NO_LIVE_PROVIDER", "Local simulation only; no telephone or Azure connection.")]));

    public async Task<ICallConnection> PrepareAsync(CallContext context, CancellationToken cancellationToken)
    {
        if (settings.Fake.PrepareDelayMilliseconds > 0)
            await Task.Delay(settings.Fake.PrepareDelayMilliseconds, cancellationToken);
        if (settings.Fake.FailPreflight) throw new ProviderException("FAKE_PREFLIGHT_FAILED");
        var call = await store.TransactionAsync(async tx =>
            await tx.CallAsync(context.CallId) ?? throw new ControlException("NOT_FOUND", 404), cancellationToken);
        return new Connection(context, call.WorkerId, call.Fence, settings.Fake, store, sink, terminator);
    }

    private sealed class Connection(
        CallContext context, string workerId, long fence, FakeSettings settings,
        ControlStore store, IProviderEventSink sink, ICallTerminator terminator) : ICallConnection
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly string connectionId = "fake:" + context.CallId;

        public async Task<ProviderHandle> DialAsync(CancellationToken cancellationToken)
        {
            if (settings.FailDialBeforeDispatch) throw new ProviderException("FAKE_DIAL_FAILED");
            await store.TransactionAsync(async tx =>
            {
                var call = await tx.CallAsync(context.CallId) ?? throw new ControlException("NOT_FOUND", 404);
                await tx.GuardActionAsync(call, workerId, fence);
                if (await tx.FakeCallAsync(connectionId) is not null)
                    throw new ProviderException("CREATE_AMBIGUOUS", true);
                await tx.SaveFakeCallAsync(new FakeCallRecord(context.CallId, connectionId,
                    "dialing", 1, 0, settings.HangupUnknown, tx.Now, null));
                return true;
            }, cancellationToken);
            if (settings.AutoConnect) _ = ConnectAsync();
            if (settings.CreateAmbiguous) throw new ProviderException("CREATE_AMBIGUOUS", true);
            return new ProviderHandle(connectionId);
        }

        private async Task ConnectAsync()
        {
            try
            {
                await Task.Delay(settings.ConnectDelayMilliseconds, lifetime.Token);
                var live = await store.TransactionAsync(async tx =>
                {
                    var simulation = await tx.FakeCallAsync(connectionId);
                    if (simulation is null || simulation.Status == "terminated") return false;
                    await tx.SaveFakeCallAsync(simulation with { Status = "connected" });
                    return true;
                }, lifetime.Token);
                if (!live) return;
                await sink.PublishAsync(context.CallId, new ProviderSignal("connected",
                    new JsonObject { ["connection_id"] = connectionId }, "fake-connected:" + context.CallId), lifetime.Token);
                if (settings.EmitTranscript)
                {
                    await sink.PublishAsync(context.CallId, new ProviderSignal("transcript.final", new JsonObject
                    {
                        ["segment_id"] = "fake-greeting",
                        ["speaker"] = "assistant",
                        ["text"] = "This is a local simulation. No telephone call has been placed.",
                        ["revision"] = 1,
                        ["final"] = true,
                        ["interrupted"] = false,
                        ["delivery"] = "generated"
                    }, "fake-transcript:" + context.CallId), lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                try { await sink.PublishAsync(context.CallId, new ProviderSignal("failure", new JsonObject { ["code"] = "PROVIDER_FAILURE" })); }
                catch { /* Supervision and the independent watchdog remain authoritative. */ }
            }
        }

        public Task InstructAsync(string text, CancellationToken cancellationToken) => ApplyAsync("instruct", cancellationToken);
        public Task SendDtmfAsync(string digits, CancellationToken cancellationToken) => ApplyAsync("dtmf", cancellationToken);
        public Task CompleteToolAsync(string toolCallId, JsonObject result, CancellationToken cancellationToken) =>
            ApplyAsync("tool", cancellationToken);
        private async Task ApplyAsync(string operation, CancellationToken cancellationToken)
        {
            await store.TransactionAsync(async tx =>
            {
                var simulation = await tx.FakeCallAsync(connectionId);
                if (simulation?.Status != "connected") throw new ProviderException("PROVIDER_DISCONNECTED");
                var call = await tx.CallAsync(context.CallId) ?? throw new ControlException("NOT_FOUND", 404);
                await tx.GuardActionAsync(call, workerId, fence, connected: true);
                await tx.RecordFakeOperationAsync(context.CallId, operation);
                return true;
            }, cancellationToken);
        }
        public Task<TerminationEvidence> HangupAsync(CancellationToken cancellationToken) =>
            terminator.TerminateAsync(connectionId, cancellationToken);
        public ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
