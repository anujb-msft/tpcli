using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class StoreAndReplayTests
{
    [Fact]
    public async Task AnExpiredWorkerCannotRenewItsFenceOrResumeActions()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        await h.Runtime.TickAsync();
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.Equal("worker_lost", state.TerminationReason);
        Assert.False(await h.Store.TransactionAsync(tx => tx.WorkerAliveAsync(h.Runtime.WorkerId)));
        Assert.Equal("WORKER_UNAVAILABLE", (await Assert.ThrowsAsync<ControlException>(() => h.StartAsync())).Code);
    }

    [Fact]
    public async Task FiniteIdleLongPollPreservesCursorAndConcurrentCommandsWakeIt()
    {
        await using var h = new Harness(s => s.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var cursor = (await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).NextCursor;
        var idle = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, cursor, waitSeconds: 1);
        Assert.Empty(idle.Events);
        Assert.Equal(cursor, idle.NextCursor);
        var poll = h.Runtime.EventsAsync(h.Owner, receipt.CallId!, cursor, waitSeconds: 3);
        await h.CommandAsync("calls.dtmf", receipt.CallId!, new JsonObject { ["digits"] = "12#" });
        var awakened = await poll.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(awakened.Events);
        Assert.True(awakened.NextCursor > cursor);
        Assert.Equal("connected", awakened.CallState!.Lifecycle);
    }

    [Fact]
    public async Task WatchdogUsesIndependentStoreAndNoMediaWorkerState()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        using var independent = new ControlStore(h.Settings, h.Clock);
        var journal = new EventJournal(independent, h.Settings, h.Clock);
        var watchdog = new TerminationEngine(independent, new FakeCallTerminator(independent), h.Settings, journal);
        await watchdog.SweepAsync("independent-watchdog");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await watchdog.DrainAsync(timeout.Token);
        var record = await independent.TransactionAsync(tx => tx.CallAsync(receipt.CallId!));
        Assert.Equal("ended", record!.State.Lifecycle);
        Assert.Equal("worker_lost", record.State.TerminationReason);
        Assert.Equal("confirmed", record.State.HangupStatus);
        var attempt = Assert.Single(await independent.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)));
        Assert.Equal("independent-watchdog", attempt.Executor);
        Assert.Equal("local-fake", attempt.ProviderMode);
        Assert.Equal("confirmed", attempt.Status);
        Assert.Equal(h.Clock.GetUtcNow(), attempt.StartedAt);
        Assert.Equal(h.Clock.GetUtcNow(), attempt.CompletedAt);
        Assert.True(record.Fence > 1);
        var action = await Assert.ThrowsAsync<ControlException>(() => independent.TransactionAsync(async tx =>
        {
            var call = (await tx.CallAsync(receipt.CallId!))!;
            await tx.GuardActionAsync(call, h.Runtime.WorkerId, 1, connected: true);
            return true;
        }));
        Assert.Equal("WORKER_FENCED", action.Code);
    }

    [Fact]
    public async Task SilentOwnerExpiresAtFifteenSecondsAndUnknownTerminationStaysVisible()
    {
        await using var h = new Harness(s => s.Fake.HangupUnknown = true);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        for (var second = 0; second < 14; second++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            await h.Runtime.TickAsync();
        }
        Assert.Equal("connected", (await h.Runtime.StateAsync(h.Owner, receipt.CallId!)).Lifecycle);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Runtime.TickAsync();
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "termination_unknown");
        Assert.Equal("owner_disconnected", state.TerminationReason);
        Assert.Equal("unknown", state.HangupStatus);
        Assert.Equal("SESSION_REVOKED", (await Assert.ThrowsAsync<ControlException>(() =>
            h.Runtime.HeartbeatAsync(h.Owner, h.Session.SessionId, h.ConnectionId, CancellationToken.None))).Code);
        Assert.Contains(await h.Store.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)), a => a.Status == "unknown");
        Assert.Single(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }

    [Fact]
    public async Task FakeTerminatorCannotClaimUnknownOrAzureHandlesWereTerminated()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var terminator = new FakeCallTerminator(h.Store);
        Assert.Equal(TerminationEvidence.Unknown, await terminator.TerminateAsync("fake:missing", CancellationToken.None));
        Assert.Equal(TerminationEvidence.Unknown, await terminator.TerminateAsync("azure-opaque-id", CancellationToken.None));
        Assert.Empty(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }

    [Fact]
    public async Task ReplayIsBoundedAndVolatileLossIsExplicitAfterAckEvictionAndRestart()
    {
        await using var h = new Harness(s => s.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var text = new string('x', 48 * 1024);
        for (var index = 0; index < 140; index++)
        {
            var segment = index;
            await h.Journal.AppendAsync(receipt.CallId!, (tx, call) => Task.FromResult<(string, JsonObject)?>(
                ("transcript.final", new JsonObject
                {
                    ["segment_id"] = "segment-" + segment,
                    ["text"] = text,
                    ["speaker"] = "recipient",
                    ["revision"] = 1,
                    ["final"] = true,
                    ["interrupted"] = false,
                    ["delivery"] = "received"
                })));
        }
        var first = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!);
        Assert.Equal(128, first.Events.Count);
        Assert.True(first.HasMore);
        Assert.Contains(first.Events, e => e.Type == "transcript.gap");
        var second = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, first.NextCursor);
        Assert.False(second.HasMore);
        Assert.True(second.Events[0].Sequence > first.NextCursor);
        Assert.True(first.Events.Concat(second.Events).Sum(e => JsonSerializer.SerializeToUtf8Bytes(e, Protocol.Json).Length)
            < RuntimeSettings.ReplayBytesPerCall + 100_000);
        await h.Journal.AckAsync(h.Owner, h.Session.SessionId, receipt.CallId!, second.NextCursor, CancellationToken.None);
        var acknowledged = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, first.NextCursor);
        Assert.All(acknowledged.Events.Where(e => e.Sequence > first.NextCursor), e => Assert.Equal("transcript.gap", e.Type));
        var freshJournal = new EventJournal(h.Store, h.Settings, h.Clock);
        var restarted = await freshJournal.ReadAsync(h.Owner, receipt.CallId!, 0);
        Assert.Contains(restarted.Events, e => e.Type == "command.accepted");
        Assert.DoesNotContain(restarted.Events, e => e.Type == "transcript.final");
        Assert.Contains(restarted.Events, e => e.Type == "transcript.gap");
        var empty = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, second.NextCursor);
        Assert.Empty(empty.Events);
        Assert.Equal(second.NextCursor, empty.NextCursor);
    }

    [Fact]
    public async Task SlowSubscribersCannotBlockProducerOrRevokeAnotherOwner()
    {
        await using var h = new Harness(s => s.SubscriberQueueCapacity = 4);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        using var subscription = h.Journal.Subscribe(h.Session.SessionId);
        for (var i = 0; i < 100; i++) h.Journal.Notify(h.Session.SessionId);
        Assert.True(subscription.SlowConsumer);
        Assert.True(subscription.Disconnected.IsCancellationRequested);
        Assert.Equal("connected", (await h.Runtime.StateAsync(h.Owner, receipt.CallId!)).Lifecycle);
        h.Journal.Unsubscribe(subscription);
    }

    [Fact]
    public async Task ReplayLiveHandoffAlwaysContainsEveryAllocatedSequence()
    {
        await using var h = new Harness(s => s.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var producer = Task.Run(async () =>
        {
            for (var index = 0; index < 60; index++)
                await h.Journal.AppendAsync(receipt.CallId!, (tx, call) =>
                    Task.FromResult<(string, JsonObject)?>(("transcript.partial", new JsonObject { ["revision"] = index, ["text"] = "content" })));
        });
        var seen = new List<EventEnvelope>();
        var cursor = 0L;
        do
        {
            var batch = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, cursor);
            seen.AddRange(batch.Events);
            cursor = batch.NextCursor;
            await Task.Delay(1);
        } while (!producer.IsCompleted);
        await producer;
        var last = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, cursor);
        seen.AddRange(last.Events);
        Assert.Equal(last.CallState!.LastSequence, seen.Count);
        Assert.Equal(Enumerable.Range(1, seen.Count).Select(i => (long)i), seen.Select(e => e.Sequence));
        Assert.DoesNotContain(seen, e => e.Type == "transcript.gap");
    }

    [Fact]
    public async Task StoreNeverPersistsTaskTranscriptInstructionApprovalTermsOrSummary()
    {
        await using var h = new Harness(s => s.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        const string privateTask = "PRIVATE_TASK_aaed0ed34638";
        const string privateInstruction = "PRIVATE_OPERATOR_2ef260587d8c";
        const string privateDescription = "PRIVATE_APPROVAL_a6a2800af49d";
        const string privateTranscript = "PRIVATE_TRANSCRIPT_477939ad9816";
        const string privateSummary = "PRIVATE_SUMMARY_1d8b05b2cbbb";
        var receipt = await h.StartAsync(task: privateTask);
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var instruction = await h.CommandAsync("calls.instruct", receipt.CallId!, new JsonObject { ["text"] = privateInstruction });
        await Harness.EventuallyAsync(async () => (await h.Runtime.CommandAsync(h.Owner, instruction.CommandId)).Status == "succeeded");
        await h.ApprovalAsync(receipt.CallId!, privateDescription);
        await h.SignalAsync(receipt.CallId!, "transcript.final", new JsonObject
        {
            ["segment_id"] = "private",
            ["speaker"] = "recipient",
            ["text"] = privateTranscript,
            ["revision"] = 1,
            ["final"] = true,
            ["interrupted"] = false,
            ["delivery"] = "received"
        });
        await h.ToolAsync(receipt.CallId!, "report_result", new JsonObject { ["outcome"] = "partial", ["summary"] = privateSummary });
        await Harness.EventuallyAsync(async () => (await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events.Any(e => e.Type == "summary.ready"));
        await h.CommandAsync("calls.stop", receipt.CallId!);
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        foreach (var path in Directory.GetFiles(h.DirectoryPath))
        {
            var content = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            foreach (var secret in new[] { privateTask, privateInstruction, privateDescription, privateTranscript, privateSummary })
                Assert.DoesNotContain(secret, content);
        }
        using var fresh = new ControlStore(h.Settings, h.Clock);
        var approvals = await fresh.TransactionAsync(tx => tx.ApprovalsAsync(receipt.CallId!));
        Assert.All(approvals, approval =>
        {
            Assert.Null(approval.View.Description);
            Assert.Null(approval.View.MaterialTerms);
        });
    }

    [Fact]
    public async Task CompletedContentExpiresAfterFiveMinutesWithoutDeletingControlHistory()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        await Harness.EventuallyAsync(async () => (await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events.Any(e => e.Type == "transcript.final"));
        await h.CommandAsync("calls.stop", receipt.CallId!);
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var events = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!);
        Assert.DoesNotContain(events.Events, e => e.Type == "transcript.final");
        Assert.Contains(events.Events, e => e.Type == "transcript.gap");
        Assert.Contains(events.Events, e => e.Type == "call.result");
    }

    [Theory]
    [InlineData("command.json")]
    [InlineData("event.json")]
    public async Task SharedFixturesDeserializeAndReserializeWithTheAuthoritativeContracts(string filename)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", filename));
        if (filename == "command.json")
        {
            var command = JsonSerializer.Deserialize<CommandRequest>(text, Protocol.Json)!;
            var roundtrip = JsonSerializer.Deserialize<CommandRequest>(JsonSerializer.Serialize(command, Protocol.Json), Protocol.Json)!;
            Assert.Equal(command.Operation, roundtrip.Operation);
            Assert.Equal(command.SessionId, roundtrip.SessionId);
            Assert.Equal(Safe.Canonical(command.Payload), Safe.Canonical(roundtrip.Payload));
            Assert.Equal(Protocol.Version, command.SchemaVersion);
            CommandValidation.Validate(roundtrip);
        }
        else
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(text, Protocol.Json)!;
            var roundtrip = JsonSerializer.Deserialize<EventEnvelope>(JsonSerializer.Serialize(envelope, Protocol.Json), Protocol.Json)!;
            Assert.Equal(envelope.Sequence, roundtrip.Sequence);
            Assert.Equal(envelope.EventId, roundtrip.EventId);
            Assert.Equal(envelope.CallId, roundtrip.CallId);
            Assert.Equal(Safe.Canonical(envelope.Payload), Safe.Canonical(roundtrip.Payload));
            Assert.Equal(Protocol.Version, envelope.SchemaVersion);
        }
    }
}
