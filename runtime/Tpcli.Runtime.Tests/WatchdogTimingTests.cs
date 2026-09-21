using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class WatchdogTimingTests
{
    private static TerminationEngine IndependentWatchdog(Harness harness, ControlStore store,
        ICallTerminator? terminator = null) =>
        new(store, terminator ?? new FakeCallTerminator(store), harness.Settings,
            new EventJournal(store, harness.Settings, harness.Clock), NullLogger<TerminationEngine>.Instance);

    private static async Task DrainAsync(TerminationEngine watchdog)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await watchdog.DrainAsync(timeout.Token);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(249, 0)]
    [InlineData(0, 750)]
    [InlineData(1, 750)]
    [InlineData(249, 750)]
    [InlineData(249, 751)]
    public async Task A5DispatchAndSimulationEffectStayWithin16000MillisecondsOfLastAcceptedHeartbeat(
        int pollPhaseMilliseconds, int dispatchJitterMilliseconds)
    {
        await using var h = new Harness(settings => settings.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        h.Clock.Advance(TimeSpan.FromMilliseconds(2300));
        await h.Runtime.HeartbeatAsync(h.Owner, h.Session.SessionId, h.ConnectionId, CancellationToken.None);
        var session = (await h.Store.TransactionAsync(tx => tx.SessionAsync(h.Session.SessionId)))!;
        var heartbeat = session.LeaseExpiresAt - RuntimeSettings.OwnerLease;
        Assert.Equal(h.Clock.GetUtcNow(), heartbeat);
        Assert.True(heartbeat > (await h.Runtime.StateAsync(h.Owner, receipt.CallId!)).CreatedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(250), RuntimeSettings.WatchdogCadence);

        using var independent = new ControlStore(h.Settings, h.Clock);
        var probe = new ProbeTerminator(new FakeCallTerminator(independent), h.Clock);
        var watchdog = IndependentWatchdog(h, independent, probe);
        watchdog.Terminating += _ => h.Clock.Advance(TimeSpan.FromMilliseconds(dispatchJitterMilliseconds));
        TerminationAttempt? attempt = null;
        for (var elapsed = pollPhaseMilliseconds; elapsed <= 16000; elapsed += (int)RuntimeSettings.WatchdogCadence.TotalMilliseconds)
        {
            h.Clock.Advance(heartbeat.AddMilliseconds(elapsed) - h.Clock.GetUtcNow());
            await h.Store.TransactionAsync(tx => tx.HeartbeatWorkerAsync(h.Runtime.WorkerId));
            await watchdog.SweepAsync("a5-independent");
            await DrainAsync(watchdog);
            var attempts = await independent.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!));
            if (attempts.Count == 0) continue;
            attempt = Assert.Single(attempts);
            break;
        }
        Assert.NotNull(attempt);
        Assert.Equal("owner_disconnected", attempt.Reason);
        Assert.Equal("a5-independent", attempt.Executor);
        Assert.Equal("local-fake", attempt.ProviderMode);
        Assert.Equal("confirmed", attempt.Status);
        Assert.NotNull(attempt.StartedAt);
        Assert.Equal(probe.InvokedAt, attempt.StartedAt);
        Assert.InRange((attempt.StartedAt.Value - heartbeat).TotalMilliseconds, 15000, 16000);
        Assert.Equal(dispatchJitterMilliseconds, (attempt.StartedAt.Value - attempt.RequestedAt).TotalMilliseconds);
        var simulation = (await independent.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId)))!;
        Assert.Equal("terminated", simulation.Status);
        Assert.NotNull(simulation.TerminatedAt);
        Assert.InRange((simulation.TerminatedAt.Value - heartbeat).TotalMilliseconds, 15000, 16000);
    }

    [Fact]
    public async Task QueuedIntentHasNoDispatchTimeAndDispatchDelayCannotBeHidden()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        using var independent = new ControlStore(h.Settings, h.Clock);
        var probe = new ProbeTerminator(new FakeCallTerminator(independent), h.Clock);
        var watchdog = IndependentWatchdog(h, independent, probe);
        var requested = h.Clock.GetUtcNow();
        watchdog.Terminating += _ =>
        {
            using var connection = new SqliteConnection(h.Settings.Store.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT requested_ms,started_ms,status FROM termination_attempts WHERE call_id=$id";
            command.Parameters.AddWithValue("$id", receipt.CallId);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(requested.ToUnixTimeMilliseconds(), row.GetInt64(0));
            Assert.True(row.IsDBNull(1));
            Assert.Equal("queued", row.GetString(2));
            Assert.Null(probe.InvokedAt);
            h.Clock.Advance(TimeSpan.FromMilliseconds(1250));
        };
        await watchdog.InitiateAsync(receipt.CallId!, "user_cancelled", "queued-proof");
        await DrainAsync(watchdog);
        var attempt = Assert.Single(await independent.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)));
        Assert.Equal(requested, attempt.RequestedAt);
        Assert.Equal(requested.AddMilliseconds(1250), attempt.StartedAt);
        Assert.Equal(probe.InvokedAt, attempt.StartedAt);
    }

    [Fact]
    public async Task DispatchEvidenceIsDurableWhileTheProviderOperationIsStillPending()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        using var independent = new ControlStore(h.Settings, h.Clock);
        var gate = new PendingTerminator();
        var watchdog = IndependentWatchdog(h, independent, gate);
        try
        {
            await watchdog.InitiateAsync(receipt.CallId!, "user_cancelled", "pending-proof");
            await Harness.EventuallyAsync(async () =>
                (await independent.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)))
                    .SingleOrDefault()?.StartedAt is not null);
            var attempt = Assert.Single(await independent.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)));
            Assert.True(gate.Invoked);
            Assert.Equal("pending", attempt.Status);
            Assert.NotNull(attempt.StartedAt);
            Assert.Null(attempt.CompletedAt);
            Assert.Equal("ending", (await h.Runtime.StateAsync(h.Owner, receipt.CallId!)).Lifecycle);
        }
        finally
        {
            gate.Completion.TrySetResult(TerminationEvidence.Unknown);
            await DrainAsync(watchdog);
        }
    }

    [Theory]
    [InlineData("none", "not_applicable")]
    [InlineData("ambiguous", "unknown")]
    public async Task UnissuedTerminationNeverClaimsADispatchTimestamp(string dispatch, string expected)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var id = await h.Store.TransactionAsync(async tx =>
        {
            var id = Safe.Id("call");
            await tx.SaveCallAsync(new CallRecord
            {
                Tenant = h.Owner.Tenant,
                Principal = h.Owner.Principal,
                Profile = h.Settings.Profile,
                WorkerId = h.Runtime.WorkerId,
                DispatchStatus = dispatch,
                State = new CallState(id, h.Session.SessionId, "pstn:+12025550123", "local-fake", "simulation",
                    "local-fake", tx.Now, tx.Now, tx.Now.AddSeconds(30), "dialing", "not_started",
                    null, "unknown", "unknown", "unavailable", 0)
            });
            return id;
        });
        using var independent = new ControlStore(h.Settings, h.Clock);
        var probe = new ProbeTerminator(new FakeCallTerminator(independent), h.Clock);
        var watchdog = IndependentWatchdog(h, independent, probe);
        await watchdog.InitiateAsync(id, "user_cancelled", "unissued-proof");
        await DrainAsync(watchdog);
        var attempt = Assert.Single(await independent.TransactionAsync(tx => tx.AttemptsAsync(id)));
        Assert.Equal(expected, attempt.Status);
        Assert.Null(attempt.StartedAt);
        Assert.Null(probe.InvokedAt);
        Assert.NotNull(attempt.CompletedAt);
    }

    [Fact]
    public async Task FasterPollingDoesNotIncreaseUnknownHangupRetryFrequency()
    {
        await using var h = new Harness(settings => settings.Fake.HangupUnknown = true);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        using var independent = new ControlStore(h.Settings, h.Clock);
        var watchdog = IndependentWatchdog(h, independent);
        await watchdog.InitiateAsync(receipt.CallId!, "user_cancelled", "retry-proof");
        await DrainAsync(watchdog);
        for (var poll = 1; poll <= 4; poll++)
        {
            h.Clock.Advance(RuntimeSettings.WatchdogCadence);
            await watchdog.SweepAsync("retry-proof");
            await DrainAsync(watchdog);
            Assert.Equal(poll == 4 ? 2 : 1,
                (await independent.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!))).Count);
        }
    }

    [Fact]
    public async Task IdempotentSimulationHangupPreservesTheFirstActualEffectTimestamp()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        var terminator = new FakeCallTerminator(h.Store);
        var handle = "fake:" + receipt.CallId;
        var firstEffect = h.Clock.GetUtcNow();
        Assert.Equal(TerminationEvidence.Confirmed, await terminator.TerminateAsync(handle, CancellationToken.None));
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TerminationEvidence.Confirmed, await terminator.TerminateAsync(handle, CancellationToken.None));
        var simulation = (await h.Store.TransactionAsync(tx => tx.FakeCallAsync(handle)))!;
        Assert.Equal(2, simulation.HangupCount);
        Assert.Equal(firstEffect, simulation.TerminatedAt);
    }

    [Fact]
    public async Task LegacyQueuedTimestampsMigrateWithoutInventingHistoricalDispatchEvidence()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        await using (var connection = new SqliteConnection(h.Settings.Store.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE termination_attempts DROP COLUMN started_ms;
                ALTER TABLE termination_attempts RENAME COLUMN requested_ms TO started_ms;
                INSERT INTO termination_attempts(id,call_id,provider_mode,reason,started_ms,completed_ms,status,executor,connection_id)
                VALUES('legacy',$id,'local-fake','user_cancelled',$time,$time,'confirmed','legacy-watchdog',$handle);
                """;
            command.Parameters.AddWithValue("$id", receipt.CallId);
            command.Parameters.AddWithValue("$time", h.Clock.GetUtcNow().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$handle", "fake:" + receipt.CallId);
            await command.ExecuteNonQueryAsync();
        }
        using var first = new ControlStore(h.Settings, h.Clock);
        using var second = new ControlStore(h.Settings, h.Clock);
        await Task.WhenAll(Task.Run(() => first.InitializeAsync()), Task.Run(() => second.InitializeAsync()));
        var legacy = Assert.Single(await first.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!)));
        Assert.Equal("legacy", legacy.Id);
        Assert.Equal(h.Clock.GetUtcNow(), legacy.RequestedAt);
        Assert.Null(legacy.StartedAt);
        Assert.Equal("confirmed", legacy.Status);
    }

    private sealed class ProbeTerminator(ICallTerminator inner, TestClock clock) : ICallTerminator
    {
        internal DateTimeOffset? InvokedAt { get; private set; }
        public Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken)
        {
            InvokedAt = clock.GetUtcNow();
            return inner.TerminateAsync(connectionId, cancellationToken);
        }
    }

    private sealed class PendingTerminator : ICallTerminator
    {
        internal readonly TaskCompletionSource<TerminationEvidence> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Invoked { get; private set; }
        public Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken)
        {
            Invoked = true;
            return Completion.Task;
        }
    }
}
