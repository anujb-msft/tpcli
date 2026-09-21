using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed class TerminationEngine(
    ControlStore store, ICallTerminator terminator, RuntimeSettings settings, EventJournal journal,
    ILogger<TerminationEngine> logger)
{
    private readonly ConcurrentDictionary<string, byte> inFlight = new();
    public event Action<string>? Terminating;

    public async Task SweepAsync(string executor, CancellationToken cancellationToken = default)
    {
        var candidates = await store.TransactionAsync(async tx =>
        {
            var result = new List<(string Id, string Reason)>();
            foreach (var call in await tx.ActiveCallsAsync())
            {
                var session = await tx.SessionAsync(call.State.SessionId);
                var reason = call.State.Deadline <= tx.Now ? "deadline"
                    : session is null || !session.Live(tx.Now) ? "owner_disconnected"
                    : !await tx.WorkerAliveAsync(call.WorkerId) ? "worker_lost"
                    : call.State.Lifecycle is "ending" or "termination_unknown" ? call.State.TerminationReason ?? "provider_error"
                    : null;
                if (reason is not null && (call.LastTerminationAt is null
                    || tx.Now - call.LastTerminationAt >= RuntimeSettings.TerminationRetryCadence))
                    result.Add((call.State.CallId, reason));
            }
            return result;
        }, cancellationToken);
        foreach (var (id, reason) in candidates)
            await InitiateAsync(id, reason, executor, cancellationToken: cancellationToken);
    }

    public async Task InitiateAsync(string callId, string reason, string executor,
        Func<CancellationToken, Task<TerminationEvidence>>? localHangup = null,
        bool containLateConnection = false, CancellationToken cancellationToken = default)
    {
        if (!inFlight.TryAdd(callId, 0)) return;
        try
        {
            var claim = await store.TransactionAsync<(TerminationAttempt Attempt, CallRecord Call)?>(async tx =>
            {
                var call = await tx.CallAsync(callId);
                if (call is null || (Safe.Terminal(call.State) && !containLateConnection)) return null;
                var wasTerminal = Safe.Terminal(call.State);
                if (!wasTerminal)
                {
                    call.Fence++;
                    call.State = call.State with
                    {
                        Lifecycle = "ending",
                        HangupStatus = call.DispatchStatus == "none" ? "not_applicable" : "pending",
                        TerminationReason = call.State.TerminationReason ?? reason,
                        SummaryStatus = call.State.SummaryStatus == "pending" ? "unavailable" : call.State.SummaryStatus,
                        TranscriptStatus = reason == "worker_lost" && call.State.TranscriptStatus != "unknown"
                            ? "partial" : call.State.TranscriptStatus
                    };
                    await tx.InvalidateApprovalsAsync(call);
                    await tx.EmitStateAsync(call, "call.termination_requested");
                    await FinishPendingCommandsAsync(tx, call);
                }
                call.LastTerminationAt = tx.Now;
                var handle = call.Handle?.ConnectionId;
                if (handle is null && call.State.ProviderMode == "local-fake"
                    && await tx.FakeCallAsync("fake:" + callId) is { } simulation)
                {
                    handle = simulation.ConnectionId;
                    call.Handle = new ProviderHandle(handle);
                }
                var status = call.DispatchStatus == "none" && handle is null ? "not_applicable"
                    : handle is null || call.State.ProviderMode != settings.Mode ? "unknown" : "queued";
                var attempt = new TerminationAttempt(Safe.Id("term"), callId, call.State.ProviderMode,
                    call.State.TerminationReason ?? reason, tx.Now, null, status == "queued" ? null : tx.Now,
                    status, executor, handle);
                await tx.SaveAttemptAsync(attempt);
                if (!wasTerminal && status != "queued")
                {
                    call.State = call.State with
                    {
                        Lifecycle = status == "not_applicable" ? "ended" : "termination_unknown",
                        HangupStatus = status == "not_applicable" ? "not_applicable" : "unknown"
                    };
                    FinalizeOutcome(call);
                    if (Safe.Terminal(call.State)) call.CompletedAt = tx.Now;
                    await tx.EmitStateAsync(call, Safe.Terminal(call.State) ? "call.result" : "call.state_changed");
                }
                await tx.SaveCallAsync(call);
                return (Attempt: attempt, Call: call);
            }, cancellationToken);
            if (claim is null) { inFlight.TryRemove(callId, out _); return; }
            Terminating?.Invoke(callId);
            journal.Notify(claim.Value.Call.State.SessionId);
            if (claim.Value.Call.CompletedAt is { } complete) await journal.MarkCompletedAsync(callId, complete);
            if (claim.Value.Attempt.Status != "queued")
            {
                inFlight.TryRemove(callId, out _);
                return;
            }
            // Provider I/O is deliberately detached from the actor, API, and watchdog polling loop.
            _ = ExecuteAsync(claim.Value.Attempt, localHangup);
        }
        catch
        {
            inFlight.TryRemove(callId, out _);
            throw;
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (!inFlight.IsEmpty) await Task.Delay(25, cancellationToken);
    }

    private async Task ExecuteAsync(TerminationAttempt attempt,
        Func<CancellationToken, Task<TerminationEvidence>>? localHangup)
    {
        try
        {
            TerminationEvidence evidence;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                Task<TerminationEvidence> operation;
                try
                {
                    operation = localHangup is null
                        ? terminator.TerminateAsync(attempt.ConnectionId!, timeout.Token)
                        : localHangup(timeout.Token);
                }
                catch { operation = Task.FromResult(TerminationEvidence.Unknown); }
                // Observe dispatch on the database clock only after invoking the provider.
                // This conservatively includes audit-write delay, never queued intent.
                try
                {
                    attempt = await store.TransactionAsync(async tx =>
                    {
                        var started = attempt with { StartedAt = tx.Now, Status = "pending" };
                        await tx.SaveAttemptAsync(started);
                        return started;
                    });
                }
                catch { logger.LogWarning("TERMINATION_DISPATCH_AUDIT_UNAVAILABLE"); }
                try
                {
                    evidence = await operation.WaitAsync(timeout.Token);
                }
                catch { evidence = TerminationEvidence.Unknown; }
                if (evidence == TerminationEvidence.Unknown && localHangup is not null && !timeout.IsCancellationRequested)
                {
                    try
                    {
                        evidence = await terminator.TerminateAsync(attempt.ConnectionId!, timeout.Token).WaitAsync(timeout.Token);
                    }
                    catch { evidence = TerminationEvidence.Unknown; }
                }
            }
            var call = await store.TransactionAsync(async tx =>
            {
                var call = await tx.CallAsync(attempt.CallId);
                await tx.SaveAttemptAsync(attempt with
                {
                    CompletedAt = tx.Now,
                    Status = evidence.ToString().ToLowerInvariant()
                });
                if (call is null || Safe.Terminal(call.State)) return call;
                call.State = call.State with
                {
                    Lifecycle = evidence == TerminationEvidence.Confirmed ? "ended"
                        : evidence == TerminationEvidence.Pending ? "ending" : "termination_unknown",
                    HangupStatus = evidence.ToString().ToLowerInvariant()
                };
                FinalizeOutcome(call);
                if (evidence == TerminationEvidence.Confirmed)
                {
                    call.CompletedAt = tx.Now;
                    await tx.EmitStateAsync(call, "call.termination_confirmed");
                    await tx.EmitStateAsync(call, "call.result");
                }
                else await tx.EmitStateAsync(call);
                return call;
            });
            if (call is not null)
            {
                journal.Notify(call.State.SessionId);
                if (call.CompletedAt is { } complete) await journal.MarkCompletedAsync(call.State.CallId, complete);
            }
        }
        catch
        {
            logger.LogWarning("TERMINATION_AUDIT_UNAVAILABLE");
            // The durable queued/pending attempt and fenced call remain retryable.
        }
        finally { inFlight.TryRemove(attempt.CallId, out _); }
    }

    internal static void FinalizeOutcome(CallRecord call)
    {
        call.State = call.State with
        {
            TaskOutcome = call.State.TaskOutcome is "not_started" or "in_progress" ? "not_completed" : call.State.TaskOutcome,
            SummaryStatus = call.State.SummaryStatus == "pending" ? "unavailable" : call.State.SummaryStatus
        };
    }

    internal static async Task FinishPendingCommandsAsync(ControlTransaction tx, CallRecord call)
    {
        foreach (var command in await tx.PendingCommandsAsync(call.State.CallId))
        {
            var isStop = command.Receipt.Operation == "calls.stop";
            var receipt = command.Receipt with
            {
                Status = isStop ? "succeeded" : "failed",
                UpdatedAt = tx.Now,
                Error = isStop ? null : new WireError("CALL_TERMINATED", "CALL_TERMINATED"),
                Result = new JsonObject { ["provider_mode"] = call.State.ProviderMode, ["route"] = call.State.Route }
            };
            await tx.SaveCommandAsync(command with { Receipt = receipt });
            await tx.EmitReceiptAsync(call, receipt);
        }
    }
}
