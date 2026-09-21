using System.Text.Json.Nodes;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task InjectedConnectionEnablesRealSimulatedControlAndDeduplicatesEffects()
    {
        await using var h = new Harness(s => s.Fake.AutoConnect = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await Harness.EventuallyAsync(async () =>
            await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId)) is not null);
        await h.Runtime.InjectAsync(h.Owner, receipt.CallId!, new ProviderSignal("connected",
            new JsonObject { ["connection_id"] = "fake:" + receipt.CallId }, "injected-connection"), CancellationToken.None);
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var command = h.Request("calls.dtmf", receipt.CallId!, new JsonObject { ["digits"] = "12#" }, "one-dtmf");
        var first = await h.Runtime.SubmitAsync(h.Owner, command);
        var duplicate = await h.Runtime.SubmitAsync(h.Owner, command);
        Assert.Equal(first.CommandId, duplicate.CommandId);
        await Harness.EventuallyAsync(async () =>
            (await h.Runtime.CommandAsync(h.Owner, first.CommandId)).Status == "succeeded");
        Assert.Equal(1, await h.Store.TransactionAsync(tx => tx.FakeOperationCountAsync(receipt.CallId!, "dtmf")));
        await h.ToolAsync(receipt.CallId!, "send_dtmf", new JsonObject { ["digits"] = "3#" }, "same-tool", "event-1");
        await h.ToolAsync(receipt.CallId!, "send_dtmf", new JsonObject { ["digits"] = "3#" }, "same-tool", "event-2");
        await Harness.EventuallyAsync(async () =>
            await h.Store.TransactionAsync(tx => tx.FakeOperationCountAsync(receipt.CallId!, "tool")) == 1);
        Assert.Equal(2, await h.Store.TransactionAsync(tx => tx.FakeOperationCountAsync(receipt.CallId!, "dtmf")));
        await h.SignalAsync(receipt.CallId!, "disconnected", id: "injected-disconnection");
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.Equal("terminated", (await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId)))!.Status);
    }

    [Fact]
    public async Task ConcurrentIdenticalSubmissionsAllocateOnlyOneCallAndOneCreate()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var request = h.StartRequest("concurrent-idempotency");
        var receipts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => h.Runtime.SubmitAsync(h.Owner, request)));
        Assert.Single(receipts.Select(r => r.CommandId).Distinct());
        Assert.Single(receipts.Select(r => r.CallId).Distinct());
        var callId = receipts[0].CallId!;
        await h.WaitStateAsync(callId, s => s.Lifecycle == "connected");
        Assert.Equal(1, (await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + callId)))!.DialCount);
        Assert.Single(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }

    [Fact]
    public async Task SessionAloneDoesNotDialAndUnattachedSessionCannotStart()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        Assert.Empty(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
        var session = await h.Runtime.CreateSessionAsync(h.Owner);
        var error = await Assert.ThrowsAsync<ControlException>(() =>
            h.Runtime.SubmitAsync(h.Owner, h.StartRequest() with { SessionId = session.SessionId }));
        Assert.Equal("SESSION_REQUIRED", error.Code);
        Assert.Empty(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }

    [Fact]
    public async Task StartIsDurableAcceptanceNotConnectionAndCanonicalRetryDoesNotRedial()
    {
        await using var h = new Harness(s => s.Fake.AutoConnect = false);
        await h.InitializeAsync();
        var request = h.StartRequest("same-key");
        var receipt = await h.Runtime.SubmitAsync(h.Owner, request);
        Assert.Equal("accepted", receipt.Status);
        Assert.Equal("local-fake", Safe.Text(receipt.Result!, "provider_mode"));
        var reordered = new JsonObject
        {
            ["allow_voicemail"] = false,
            ["max_duration_seconds"] = 30,
            ["task"] = "Ask for public opening hours.",
            ["target"] = "pstn:+12025550123"
        };
        var duplicate = await h.Runtime.SubmitAsync(h.Owner, request with { Payload = reordered });
        Assert.Equal(receipt.CommandId, duplicate.CommandId);
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "dialing");
        await Harness.EventuallyAsync(async () => await h.Store.TransactionAsync(tx =>
            tx.FakeCallAsync("fake:" + receipt.CallId)) is not null);
        using var reopened = new ControlStore(h.Settings, h.Clock);
        var command = await reopened.TransactionAsync(tx => tx.CommandAsync(receipt.CommandId));
        Assert.Equal(receipt.CommandId, command!.Receipt.CommandId);
        var simulation = await reopened.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId));
        Assert.Equal(1, simulation!.DialCount);
        var conflict = await Assert.ThrowsAsync<ControlException>(() =>
            h.Runtime.SubmitAsync(h.Owner, request with { Payload = new JsonObject { ["target"] = "pstn:+12025550123", ["task"] = "Different" } }));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
    }

    [Fact]
    public async Task DefaultFakeConnectsTranscribesAndWaitsForExplicitStop()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        Assert.Equal("simulation", state.Route);
        Assert.Equal("in_progress", state.TaskOutcome);
        Assert.NotEqual("complete", state.SummaryStatus);
        await Harness.EventuallyAsync(async () =>
            (await h.Runtime.EventsAsync(h.Owner, state.CallId)).Events.Any(e => e.Type == "transcript.final"));
        Assert.Equal("succeeded", (await h.Runtime.CommandAsync(h.Owner, receipt.CommandId)).Status);
        await h.CommandAsync("calls.stop", state.CallId);
        state = await h.WaitStateAsync(state.CallId, s => s.Lifecycle == "ended");
        Assert.Equal("confirmed", state.HangupStatus);
        Assert.Equal("not_completed", state.TaskOutcome);
        Assert.Equal("user_cancelled", state.TerminationReason);
    }

    [Fact]
    public async Task AmbiguousCreateIsNeverRetriedAndUnknownHangupStillBlocksASecondCall()
    {
        await using var h = new Harness(s =>
        {
            s.Fake.CreateAmbiguous = true;
            s.Fake.HangupUnknown = true;
            s.Fake.AutoConnect = false;
        });
        await h.InitializeAsync();
        var request = h.StartRequest("ambiguous");
        var receipt = await h.Runtime.SubmitAsync(h.Owner, request);
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "termination_unknown");
        Assert.Equal("unknown", state.HangupStatus);
        var retry = await h.Runtime.SubmitAsync(h.Owner, request);
        Assert.Equal(receipt.CommandId, retry.CommandId);
        var conflict = await Assert.ThrowsAsync<ControlException>(() => h.StartAsync());
        Assert.Equal("ACTIVE_CALL_EXISTS", conflict.Code);
        var simulation = await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + state.CallId));
        Assert.Equal(1, simulation!.DialCount);
        await h.SignalAsync(state.CallId, "disconnected", id: "definitively-disconnected");
        await h.WaitStateAsync(state.CallId, s => s.Lifecycle == "ended" && s.HangupStatus == "confirmed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreDispatchFailuresAreNotAmbiguous(bool prepare)
    {
        await using var h = new Harness(s =>
        {
            s.Fake.FailPreflight = prepare;
            s.Fake.FailDialBeforeDispatch = !prepare;
        });
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "failed_before_connect");
        Assert.Equal("not_applicable", state.HangupStatus);
        Assert.Equal("failed", (await h.Runtime.CommandAsync(h.Owner, receipt.CommandId)).Status);
        Assert.Null(await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + state.CallId)));
    }

    [Fact]
    public async Task OwnerLossWhileDialingAndLateCallbacksNeverReviveTheCall()
    {
        await using var h = new Harness(s => s.Fake.AutoConnect = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await Harness.EventuallyAsync(async () =>
            await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId)) is not null);
        await h.Runtime.RevokeAsync(h.Owner, h.Session.SessionId);
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.Equal("owner_disconnected", state.TerminationReason);
        await h.SignalAsync(state.CallId, "connected", new JsonObject { ["connection_id"] = "fake:" + state.CallId }, "late-connected");
        await Harness.EventuallyAsync(async () =>
            (await h.Store.TransactionAsync(tx => tx.FakeCallAsync("fake:" + state.CallId)))!.HangupCount >= 2);
        state = await h.Runtime.StateAsync(h.Owner, state.CallId);
        Assert.Equal("ended", state.Lifecycle);
        var error = await Assert.ThrowsAsync<ControlException>(() =>
            h.Runtime.AttachAsync(h.Owner, h.Session.SessionId, "second-owner", CancellationToken.None));
        Assert.Equal("SESSION_REVOKED", error.Code);
        Assert.Equal("SESSION_REQUIRED", (await Assert.ThrowsAsync<ControlException>(() => h.StartAsync())).Code);
    }

    [Fact]
    public async Task DuplicateAndReorderedSignalsCannotRegressOrDuplicateTranscript()
    {
        await using var h = new Harness(s => s.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var payload = new JsonObject
        {
            ["segment_id"] = "one",
            ["speaker"] = "recipient",
            ["text"] = "Hello",
            ["revision"] = 2,
            ["final"] = true,
            ["interrupted"] = false,
            ["delivery"] = "received"
        };
        await h.SignalAsync(receipt.CallId!, "transcript.final", payload, "provider-final");
        await h.SignalAsync(receipt.CallId!, "transcript.final", payload.DeepClone().AsObject(), "provider-final");
        await h.SignalAsync(receipt.CallId!, "transcript.final", payload.DeepClone().AsObject(), "different-provider-id");
        var partial = payload.DeepClone().AsObject();
        partial["revision"] = 1;
        partial["final"] = false;
        await h.SignalAsync(receipt.CallId!, "transcript.partial", partial, "old-partial");
        await Harness.EventuallyAsync(async () =>
            (await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events.Any(e => e.Type == "transcript.final"));
        await h.SignalAsync(receipt.CallId!, "disconnected", id: "disconnected");
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        await h.SignalAsync(receipt.CallId!, "connected", new JsonObject { ["connection_id"] = "fake:" + receipt.CallId }, "out-of-order-connected");
        var batch = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!);
        Assert.Single(batch.Events, e => e.Type == "transcript.final");
        Assert.DoesNotContain(batch.Events, e => e.Type == "transcript.partial");
        Assert.Equal("ended", batch.CallState!.Lifecycle);
        Assert.Equal(batch.Events.Count, batch.Events.Select(e => e.Sequence).Distinct().Count());
        Assert.True(batch.Events.Zip(batch.Events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public async Task WholeCallDeadlineUsesTestTtlWithoutRelaxingPublicDurationValidation()
    {
        await using var h = new Harness(s => s.Fake.TestDeadlineMilliseconds = 1000);
        await h.InitializeAsync();
        var invalid = h.StartRequest();
        invalid.Payload["max_duration_seconds"] = 1;
        Assert.Equal("INVALID_DURATION", (await Assert.ThrowsAsync<ControlException>(() =>
            h.Runtime.SubmitAsync(h.Owner, invalid))).Code);
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Runtime.TickAsync();
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.Equal("deadline", state.TerminationReason);
        Assert.Equal("confirmed", state.HangupStatus);
    }

    [Fact]
    public async Task EveryReadAndControlIsBoundToTenantPrincipalAndSession()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        foreach (var other in new[] { new Caller("other-tenant", h.Owner.Principal), new Caller(h.Owner.Tenant, "other-principal") })
        {
            Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.StateAsync(other, receipt.CallId!))).HttpStatus);
            Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.CommandAsync(other, receipt.CommandId))).HttpStatus);
            Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.EventsAsync(other, receipt.CallId!))).HttpStatus);
            Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.ApprovalsAsync(other, receipt.CallId!))).HttpStatus);
            Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.InjectAsync(other, receipt.CallId!,
                new ProviderSignal("disconnected", []), CancellationToken.None))).HttpStatus);
            Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.RevokeAsync(other, h.Session.SessionId))).HttpStatus);
        }
        var otherSession = await h.Runtime.CreateSessionAsync(h.Owner);
        await h.Runtime.AttachAsync(h.Owner, otherSession.SessionId, "other", CancellationToken.None);
        var wrongSession = h.Request("calls.stop", receipt.CallId!) with { SessionId = otherSession.SessionId };
        Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Runtime.SubmitAsync(h.Owner, wrongSession))).HttpStatus);
        Assert.Equal(404, (await Assert.ThrowsAsync<ControlException>(() => h.Journal.AckAsync(h.Owner, otherSession.SessionId,
            receipt.CallId!, 1, CancellationToken.None))).HttpStatus);
    }

    [Theory]
    [InlineData("+12025550123")]
    [InlineData("teams:someone@example.invalid")]
    [InlineData("pstn:911")]
    [InlineData("teams:00000000-0000-0000-0000-000000000001")]
    public async Task InvalidOrUnsupportedTargetsNeverCreate(string target)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var request = h.StartRequest();
        request.Payload["target"] = target;
        await Assert.ThrowsAsync<ControlException>(() => h.Runtime.SubmitAsync(h.Owner, request));
        Assert.Empty(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }
}
