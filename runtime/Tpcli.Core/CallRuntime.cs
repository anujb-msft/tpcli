using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed class CallRuntime(
    ControlStore store, ICallProviderFactory provider, TerminationEngine termination,
    EventJournal journal, RuntimeSettings settings, TimeProvider clock) : IProviderEventSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CallActor> actors = new();
    private readonly SemaphoreSlim submissions = new(1);
    private readonly SemaphoreSlim initialization = new(1);
    private bool initialized;
    private int disposed;
    public string WorkerId { get; } = Safe.Id("worker");
    public ProviderCapabilities Capabilities { get; private set; } = null!;
    internal ControlStore Store => store;
    internal ICallProviderFactory Provider => provider;
    internal TerminationEngine Termination => termination;
    internal EventJournal Journal => journal;
    internal RuntimeSettings Settings => settings;
    internal TimeProvider Clock => clock;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            await store.InitializeAsync(cancellationToken);
            if (provider.Mode != settings.Mode) throw new InvalidOperationException("PROVIDER_MODE_MISMATCH");
            Capabilities = await provider.CheckReadinessAsync(false, cancellationToken);
            if (Capabilities.Mode != settings.Mode) throw new InvalidOperationException("PROVIDER_MODE_MISMATCH");
            await store.TransactionAsync(tx => tx.HeartbeatWorkerAsync(WorkerId), cancellationToken);
            termination.Terminating += CancelActor;
            initialized = true;
        }
        finally { initialization.Release(); }
    }

    public Task<ProviderCapabilities> CheckReadinessAsync(bool online, CancellationToken cancellationToken) =>
        provider.CheckReadinessAsync(online, cancellationToken);

    public async Task<SessionInfo> CreateSessionAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        return await store.TransactionAsync(async tx =>
        {
            var session = new SessionRecord(Safe.Id("sess"), caller.Tenant, caller.Principal,
                settings.Profile, "active", tx.Now + RuntimeSettings.OwnerLease, null);
            await tx.SaveSessionAsync(session);
            return session.View(settings.Mode);
        }, cancellationToken);
    }

    public async Task<SessionInfo> AttachAsync(Caller caller, string sessionId, string connectionId, CancellationToken cancellationToken)
    {
        return await store.TransactionAsync(async tx =>
        {
            var session = await OwnedSessionAsync(tx, caller, sessionId);
            if (session.Status != "active" || session.LeaseExpiresAt <= tx.Now)
                throw new ControlException("SESSION_REVOKED");
            if (session.ConnectionId is not null) throw new ControlException("SESSION_ALREADY_OWNED");
            session = session with { ConnectionId = connectionId, LeaseExpiresAt = tx.Now + RuntimeSettings.OwnerLease };
            await tx.SaveSessionAsync(session);
            return session.View(settings.Mode);
        }, cancellationToken);
    }

    public Task<SessionInfo> SessionAsync(Caller caller, string sessionId, CancellationToken cancellationToken = default) =>
        store.TransactionAsync(async tx => (await OwnedSessionAsync(tx, caller, sessionId)).View(settings.Mode), cancellationToken);

    public async Task HeartbeatAsync(Caller caller, string sessionId, string connectionId, CancellationToken cancellationToken)
    {
        await store.TransactionAsync(async tx =>
        {
            var session = await OwnedSessionAsync(tx, caller, sessionId);
            if (!session.Live(tx.Now) || session.ConnectionId != connectionId)
                throw new ControlException("SESSION_REVOKED");
            await tx.SaveSessionAsync(session with { LeaseExpiresAt = tx.Now + RuntimeSettings.OwnerLease });
            return true;
        }, cancellationToken);
    }

    public async Task<SessionInfo> RevokeAsync(Caller caller, string sessionId,
        string? connectionId = null, CancellationToken cancellationToken = default)
    {
        var (session, calls) = await store.TransactionAsync(async tx =>
        {
            var session = await OwnedSessionAsync(tx, caller, sessionId);
            if (connectionId is not null && session.ConnectionId is not null && session.ConnectionId != connectionId)
                throw new ControlException("SESSION_ALREADY_OWNED");
            session = session with { Status = "revoked", ConnectionId = null, LeaseExpiresAt = tx.Now };
            await tx.SaveSessionAsync(session);
            return (session, await tx.SessionCallsAsync(sessionId));
        }, cancellationToken);
        foreach (var call in calls.Where(call => call.Active))
            await StopAsync(call.State.CallId, "owner_disconnected");
        journal.Notify(sessionId);
        return session.View(settings.Mode);
    }

    private static async Task<SessionRecord> OwnedSessionAsync(ControlTransaction tx, Caller caller, string id)
    {
        var session = await tx.SessionAsync(id);
        if (session is null || !session.OwnedBy(caller)) throw new ControlException("NOT_FOUND", 404);
        return session;
    }

    public async Task<CommandReceipt> SubmitAsync(Caller caller, CommandRequest input, CancellationToken cancellationToken = default)
    {
        var validated = CommandValidation.Validate(input);
        var request = validated.Request;
        await submissions.WaitAsync(cancellationToken);
        string? registered = null;
        try
        {
            var accepted = await store.TransactionAsync(async tx =>
            {
                var session = await OwnedSessionAsync(tx, caller, request.SessionId);
                var previous = await tx.CommandByKeyAsync(caller, Safe.Hash(request.IdempotencyKey));
                if (previous is not null)
                {
                    if (previous.PayloadHash != validated.PayloadHash) throw new ControlException("IDEMPOTENCY_CONFLICT");
                    return (Receipt: previous.Receipt, Call: (CallRecord?)null, New: false);
                }
                if (request.Operation != "calls.stop" && !session.Live(tx.Now))
                    throw new ControlException("SESSION_REQUIRED");
                CallRecord call;
                if (request.Operation == "calls.start")
                {
                    if (!await tx.WorkerAliveAsync(WorkerId)) throw new ControlException("WORKER_UNAVAILABLE", 503, true);
                    var target = Safe.Text(request.Payload, "target")!;
                    if (target.StartsWith("teams:", StringComparison.Ordinal) && !Capabilities.Teams)
                        throw new ControlException("TEAMS_ROUTE_UNSUPPORTED");
                    if (target.StartsWith("pstn:", StringComparison.Ordinal) && !Capabilities.Pstn)
                        throw new ControlException("PSTN_ROUTE_UNSUPPORTED");
                    if (await tx.HasActiveCallAsync(caller, session.Profile)) throw new ControlException("ACTIVE_CALL_EXISTS");
                    if (actors.Count >= settings.MaxResidentCalls) throw new ControlException("RUNTIME_CAPACITY", 429, true);
                    var id = Safe.Id("call");
                    journal.Register(id);
                    registered = id;
                    var duration = settings.Mode == "local-fake" && settings.Fake.TestDeadlineMilliseconds is { } milliseconds
                        ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.FromSeconds(validated.DurationSeconds);
                    call = new CallRecord
                    {
                        Tenant = caller.Tenant,
                        Principal = caller.Principal,
                        Profile = session.Profile,
                        WorkerId = WorkerId,
                        State = new CallState(id, session.Id, target, Capabilities.Source, Capabilities.Route,
                            settings.Mode, tx.Now, tx.Now, tx.Now + duration, "accepted", "not_started",
                            null, "not_applicable", "unknown", "unavailable", 0)
                    };
                    await tx.SaveCallAsync(call);
                }
                else
                {
                    call = await tx.OwnedCallAsync(caller, request.CallId!, session.Id);
                    if (request.Operation != "calls.stop")
                    {
                        await tx.GuardActionAsync(call, WorkerId, call.Fence, connected: true);
                        if (!actors.ContainsKey(call.State.CallId)) throw new ControlException("WORKER_UNAVAILABLE");
                    }
                    if (request.Operation == "approvals.resolve")
                        await ValidateApprovalAsync(tx, call, request.Payload);
                }
                var receipt = new CommandReceipt(Protocol.Version, Safe.Id("cmd"), session.Id,
                    call.State.CallId, request.Operation, "accepted", tx.Now, tx.Now,
                    Result: new JsonObject { ["provider_mode"] = settings.Mode, ["route"] = call.State.Route });
                await tx.SaveCommandAsync(new StoredCommand(caller.Tenant, caller.Principal,
                    Safe.Hash(request.IdempotencyKey), validated.PayloadHash, receipt));
                await tx.EmitReceiptAsync(call, receipt);
                if (request.Operation == "calls.start") await tx.EmitStateAsync(call);
                return (Receipt: receipt, Call: (CallRecord?)call, New: true);
            }, cancellationToken);
            if (!accepted.New) return accepted.Receipt;
            journal.Notify(request.SessionId);
            var call = accepted.Call!;
            if (request.Operation == "calls.start")
            {
                var context = new CallContext(call.State.CallId, request.SessionId,
                    call.State.Target, Safe.Text(request.Payload, "task")!, call.State.Deadline,
                    request.Payload["allow_voicemail"]!.GetValue<bool>());
                var actor = new CallActor(this, call, context);
                if (!actors.TryAdd(call.State.CallId, actor)) throw new InvalidOperationException("ACTOR_ALREADY_EXISTS");
                actor.Start();
            }
            else if (request.Operation == "calls.stop")
            {
                await StopAsync(call.State.CallId, "user_cancelled");
                if (Safe.Terminal(call.State))
                    await CompleteCommandAsync(call.State.CallId, accepted.Receipt.CommandId, null);
            }
            else if (!actors[call.State.CallId].PostCommand(accepted.Receipt, request, caller))
                await CompleteCommandAsync(call.State.CallId, accepted.Receipt.CommandId, "ACTOR_QUEUE_FULL");
            return accepted.Receipt;
        }
        catch
        {
            // If commit succeeded but enqueue failed, recovery must terminate rather than redial.
            if (registered is not null && !actors.ContainsKey(registered))
            {
                try { await termination.InitiateAsync(registered, "worker_lost", WorkerId); }
                catch { }
            }
            throw;
        }
        finally { submissions.Release(); }
    }

    internal static async Task<StoredApproval> ValidateApprovalAsync(ControlTransaction tx, CallRecord call, JsonObject payload)
    {
        var id = Safe.Text(payload, "approval_id");
        var approval = (await tx.ApprovalsAsync(call.State.CallId)).SingleOrDefault(a => a.View.ApprovalId == id);
        if (approval is null) throw new ControlException("APPROVAL_NOT_FOUND", 404);
        if (approval.View.Status != "pending" || approval.View.ExpiresAt <= tx.Now
            || approval.View.ConversationVersion != call.ConversationVersion
            || approval.View.ActionHash != Safe.Text(payload, "action_hash"))
            throw new ControlException("STALE_APPROVAL");
        return approval;
    }

    public async Task<CommandReceipt> CommandAsync(Caller caller, string commandId, CancellationToken cancellationToken = default) =>
        await store.TransactionAsync(async tx =>
        {
            var command = await tx.CommandAsync(commandId);
            if (command is null || command.Tenant != caller.Tenant || command.Principal != caller.Principal)
                throw new ControlException("NOT_FOUND", 404);
            await OwnedSessionAsync(tx, caller, command.Receipt.SessionId);
            if (command.Receipt.CallId is not null)
                await tx.OwnedCallAsync(caller, command.Receipt.CallId, command.Receipt.SessionId);
            return command.Receipt;
        }, cancellationToken);

    public async Task<CallState> StateAsync(Caller caller, string callId, CancellationToken cancellationToken = default)
    {
        var call = await store.TransactionAsync(tx => tx.OwnedCallAsync(caller, callId), cancellationToken);
        return await journal.ExposedStateAsync(call);
    }
    public async Task<IReadOnlyList<CallState>> SessionCallsAsync(Caller caller, string sessionId, CancellationToken cancellationToken)
    {
        return await store.TransactionAsync(async tx =>
        {
            await OwnedSessionAsync(tx, caller, sessionId);
            return (IReadOnlyList<CallState>)(await tx.SessionCallsAsync(sessionId)).Select(c => c.State).ToArray();
        }, cancellationToken);
    }

    public async Task<EventBatch> EventsAsync(Caller caller, string callId, long after = 0,
        int waitSeconds = 0, CancellationToken cancellationToken = default)
    {
        if (after < 0 || waitSeconds is < 0 or > 30) throw new ControlException("INVALID_CURSOR", 400);
        var started = clock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(waitSeconds);
        while (true)
        {
            var batch = await journal.ReadAsync(caller, callId, after, cancellationToken);
            var remaining = budget - clock.GetElapsedTime(started);
            if (batch.Events.Count > 0 || remaining <= TimeSpan.Zero) return batch;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ApprovalView>> ApprovalsAsync(Caller caller, string callId, CancellationToken cancellationToken = default)
    {
        var rows = await store.TransactionAsync(async tx =>
        {
            var call = await tx.OwnedCallAsync(caller, callId);
            return await tx.ApprovalsAsync(call.State.CallId);
        }, cancellationToken);
        return await Task.WhenAll(rows.Select(row => journal.WithContentAsync(row.View)));
    }

    public async Task InjectAsync(Caller caller, string callId, ProviderSignal signal, CancellationToken cancellationToken)
    {
        if (settings.Mode != "local-fake") throw new ControlException("NOT_FOUND", 404);
        await store.TransactionAsync(tx => tx.OwnedCallAsync(caller, callId), cancellationToken);
        await PublishAsync(callId, signal, cancellationToken);
    }

    public async Task PublishAsync(string callId, ProviderSignal signal, CancellationToken cancellationToken = default)
    {
        if (signal.Payload is null || signal.Type is not ("connected" or "disconnected" or "transcript.partial"
            or "transcript.final" or "transcript.interrupted" or "transcript.gap" or "tool.requested" or "warning" or "failure")
            || signal.ProviderEventId?.Length > 4096
            || JsonSerializer.SerializeToUtf8Bytes(signal, Protocol.Json).Length > RuntimeSettings.MaxRequestBytes)
            throw new ControlException("INVALID_PROVIDER_SIGNAL", 400);
        if (actors.TryGetValue(callId, out var actor))
        {
            if (!actor.PostSignal(signal))
                await StopAsync(callId, "media_error");
            return;
        }
        // A new process must not reconstruct a conversational actor from incomplete content.
        var call = await store.TransactionAsync(async tx =>
        {
            var call = await tx.CallAsync(callId);
            if (call is null) throw new ControlException("NOT_FOUND", 404);
            if (!await tx.RememberProviderEventAsync(callId, signal.ProviderEventId)) return null;
            if (signal.Type == "connected") await RetainHandleAsync(tx, call, signal.Payload);
            if (signal.Type == "disconnected") await DisconnectedAsync(tx, call);
            return call;
        });
        if (call is null) return;
        journal.Notify(call.State.SessionId);
        if (signal.Type != "disconnected")
            await termination.InitiateAsync(callId, "worker_lost", WorkerId,
                containLateConnection: signal.Type == "connected");
    }

    internal static async Task RetainHandleAsync(ControlTransaction tx, CallRecord call, JsonObject payload)
    {
        var connection = Safe.Text(payload, "connection_id");
        if (connection is null) return;
        if (connection.Length is 0 or > 4096) throw new ControlException("INVALID_PROVIDER_HANDLE");
        var server = Safe.Text(payload, "server_call_id");
        if (server?.Length > 4096) throw new ControlException("INVALID_PROVIDER_HANDLE");
        if (call.Handle is not null && call.Handle.ConnectionId != connection)
            throw new ControlException("PROVIDER_HANDLE_CONFLICT");
        call.Handle = new ProviderHandle(connection, server ?? call.Handle?.ServerCallId);
        if (call.DispatchStatus == "none") call.DispatchStatus = "returned";
        await tx.SaveCallAsync(call);
    }

    internal async Task CompleteCommandAsync(string callId, string commandId, string? code)
    {
        var session = await store.TransactionAsync(async tx =>
        {
            var call = await tx.CallAsync(callId);
            var command = await tx.CommandAsync(commandId);
            if (call is null || command is null || command.Receipt.Status != "accepted") return null;
            var receipt = command.Receipt with
            {
                Status = code is null ? "succeeded" : "failed",
                UpdatedAt = tx.Now,
                Error = code is null ? null : new WireError(code, code)
            };
            await tx.SaveCommandAsync(command with { Receipt = receipt });
            await tx.EmitReceiptAsync(call, receipt);
            return call.State.SessionId;
        });
        if (session is not null) journal.Notify(session);
    }

    internal static async Task DisconnectedAsync(ControlTransaction tx, CallRecord call)
    {
        if (call.State.ProviderMode == "local-fake"
            && await tx.FakeCallAsync("fake:" + call.State.CallId) is { } simulation)
            await tx.SaveFakeCallAsync(simulation with { Status = "terminated", TerminatedAt = tx.Now });
        if (Safe.Terminal(call.State)) return;
        call.State = call.State with
        {
            Lifecycle = "ended",
            HangupStatus = "confirmed",
            TerminationReason = call.State.TerminationReason ?? "recipient_hangup"
        };
        call.Fence++;
        call.CompletedAt = tx.Now;
        TerminationEngine.FinalizeOutcome(call);
        await tx.InvalidateApprovalsAsync(call);
        await tx.EmitStateAsync(call, "call.termination_confirmed");
        await TerminationEngine.FinishPendingCommandsAsync(tx, call);
        await tx.EmitStateAsync(call, "call.result");
    }

    internal async Task StopAsync(string callId, string reason, CancellationToken cancellationToken = default,
        bool containLateConnection = false)
    {
        actors.TryGetValue(callId, out var actor);
        actor?.CancelActions();
        await termination.InitiateAsync(callId, reason, WorkerId, actor is null ? null : actor.HangupAsync,
            containLateConnection, cancellationToken);
    }

    private void CancelActor(string callId)
    {
        if (actors.TryGetValue(callId, out var actor)) actor.CancelActions();
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var expiredSessions = await store.TransactionAsync(async tx =>
        {
            await tx.HeartbeatWorkerAsync(WorkerId);
            var expired = new List<string>();
            foreach (var session in await tx.SessionsAsync())
            {
                if (session.Status == "active" && session.LeaseExpiresAt <= tx.Now)
                {
                    await tx.SaveSessionAsync(session with { Status = "revoked", ConnectionId = null });
                    expired.Add(session.Id);
                }
            }
            return expired;
        }, cancellationToken);
        foreach (var session in expiredSessions) journal.Notify(session);
        await termination.SweepAsync(WorkerId, cancellationToken);
        foreach (var actor in actors.Values) actor.PostTick();
        journal.Prune();
    }

    internal async Task ActorFinishedAsync(string callId)
    {
        if (actors.TryRemove(callId, out var actor)) await actor.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        termination.Terminating -= CancelActor;
        try { await store.TransactionAsync(tx => tx.ExpireWorkerAsync(WorkerId)); }
        catch { }
        foreach (var actor in actors.Values)
        {
            try { await StopAsync(actor.CallId, "worker_lost"); }
            catch { }
        }
        foreach (var actor in actors.Values) await actor.DisposeAsync();
        actors.Clear();
    }
}
