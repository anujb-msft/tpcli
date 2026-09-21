using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Npgsql;
using Tpcli.Contracts;

namespace Tpcli.Core;

// Transactions contain control metadata only. Content-bearing event rows have a NULL payload.
public sealed class ControlStore(RuntimeSettings settings, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private bool initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            await using var connection = await OpenAsync(cancellationToken);
            if (connection is SqliteConnection)
            {
                await using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync(cancellationToken);
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task<T> TransactionAsync<T>(
        Func<ControlTransaction, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection is SqliteConnection sqlite
                ? sqlite.BeginTransaction(deferred: false)
                : await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            if (connection is NpgsqlConnection)
            {
                // One short metadata transaction at a time across runtime replicas and watchdogs.
                // This is deliberately conservative; no provider/network work occurs under the lock.
                await using var fence = connection.CreateCommand();
                fence.Transaction = transaction;
                fence.CommandText = "SELECT pg_advisory_xact_lock(78042160117);";
                await fence.ExecuteNonQueryAsync(cancellationToken);
                fence.CommandText = "SELECT floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint;";
                now = DateTimeOffset.FromUnixTimeMilliseconds((long)(await fence.ExecuteScalarAsync(cancellationToken))!);
            }
            var unit = new ControlTransaction(connection, transaction, now, cancellationToken);
            var result = await operation(unit);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection result;
        if (settings.Store.Provider == "postgres")
        {
            var builder = new NpgsqlConnectionStringBuilder(settings.Store.ConnectionString)
            {
                IncludeErrorDetail = false,
                LogParameters = false,
                Timeout = 5,
                CommandTimeout = 10
            };
            if (settings.Mode == "azure") builder.SslMode = SslMode.VerifyFull;
            result = new NpgsqlConnection(builder.ConnectionString);
        }
        else
        {
            var builder = new SqliteConnectionStringBuilder(settings.Store.ConnectionString)
            {
                DefaultTimeout = 5,
                Pooling = false,
                ForeignKeys = true
            };
            if (builder.DataSource is "" or ":memory:")
                throw new InvalidOperationException("DURABLE_SQLITE_FILE_REQUIRED");
            result = new SqliteConnection(builder.ConnectionString);
        }
        try
        {
            await result.OpenAsync(cancellationToken);
            if (result is SqliteConnection)
            {
                await using var command = result.CreateCommand();
                command.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public void Dispose() => gate.Dispose();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY, tenant TEXT NOT NULL, principal TEXT NOT NULL, profile TEXT NOT NULL,
            status TEXT NOT NULL, lease_expires_ms BIGINT NOT NULL, connection_id TEXT NULL);
        CREATE TABLE IF NOT EXISTS workers (id TEXT PRIMARY KEY, lease_expires_ms BIGINT NOT NULL);
        CREATE TABLE IF NOT EXISTS calls (
            id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id), tenant TEXT NOT NULL,
            principal TEXT NOT NULL, profile TEXT NOT NULL, worker_id TEXT NOT NULL, fence BIGINT NOT NULL,
            dispatch_status TEXT NOT NULL, connection_id TEXT NULL, server_call_id TEXT NULL,
            conversation_version BIGINT NOT NULL, completed_ms BIGINT NULL, last_termination_ms BIGINT NULL,
            state_json TEXT NOT NULL, active INTEGER NOT NULL, deadline_ms BIGINT NOT NULL,
            last_sequence BIGINT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_call ON calls(tenant, principal, profile) WHERE active=1;
        CREATE INDEX IF NOT EXISTS calls_session ON calls(session_id);
        CREATE TABLE IF NOT EXISTS commands (
            id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id),
            call_id TEXT NULL REFERENCES calls(id) ON DELETE CASCADE, tenant TEXT NOT NULL,
            principal TEXT NOT NULL, key_hash TEXT NOT NULL, payload_hash TEXT NOT NULL,
            status TEXT NOT NULL, receipt_json TEXT NOT NULL,
            UNIQUE(tenant, principal, key_hash));
        CREATE TABLE IF NOT EXISTS events (
            call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE, sequence BIGINT NOT NULL,
            event_id TEXT NOT NULL, session_id TEXT NOT NULL, timestamp_ms BIGINT NOT NULL,
            type TEXT NOT NULL, command_id TEXT NULL, payload_json TEXT NULL,
            PRIMARY KEY(call_id, sequence));
        CREATE TABLE IF NOT EXISTS approvals (
            id TEXT PRIMARY KEY, call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE,
            action_hash TEXT NOT NULL, expires_ms BIGINT NOT NULL, status TEXT NOT NULL,
            version BIGINT NOT NULL, actor TEXT NULL, tool_hash TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS approvals_call ON approvals(call_id);
        CREATE TABLE IF NOT EXISTS provider_events (
            call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE, event_hash TEXT NOT NULL,
            PRIMARY KEY(call_id, event_hash));
        CREATE TABLE IF NOT EXISTS tool_requests (
            call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE, tool_hash TEXT NOT NULL,
            args_hash TEXT NOT NULL, status TEXT NOT NULL, PRIMARY KEY(call_id, tool_hash));
        CREATE TABLE IF NOT EXISTS transcript_segments (
            call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE, segment_hash TEXT NOT NULL,
            revision BIGINT NOT NULL, is_final INTEGER NOT NULL, interrupted INTEGER NOT NULL,
            PRIMARY KEY(call_id, segment_hash));
        CREATE TABLE IF NOT EXISTS termination_attempts (
            id TEXT PRIMARY KEY, call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE,
            provider_mode TEXT NOT NULL, reason TEXT NOT NULL, started_ms BIGINT NOT NULL,
            completed_ms BIGINT NULL, status TEXT NOT NULL, executor TEXT NOT NULL,
            connection_id TEXT NULL);
        CREATE INDEX IF NOT EXISTS attempts_call ON termination_attempts(call_id);
        CREATE TABLE IF NOT EXISTS fake_calls (
            call_id TEXT PRIMARY KEY REFERENCES calls(id) ON DELETE CASCADE,
            connection_id TEXT UNIQUE NOT NULL, status TEXT NOT NULL, dial_count INTEGER NOT NULL,
            hangup_count INTEGER NOT NULL, hangup_unknown INTEGER NOT NULL,
            created_ms BIGINT NOT NULL, terminated_ms BIGINT NULL);
        CREATE TABLE IF NOT EXISTS fake_operations (
            call_id TEXT NOT NULL REFERENCES calls(id) ON DELETE CASCADE, operation TEXT NOT NULL,
            operation_count BIGINT NOT NULL, PRIMARY KEY(call_id, operation));
        """;
}

public sealed class ControlTransaction
{
    private readonly DbConnection connection;
    private readonly DbTransaction transaction;
    private readonly CancellationToken cancellationToken;
    public DateTimeOffset Now { get; }

    internal ControlTransaction(DbConnection connection, DbTransaction transaction,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.cancellationToken = cancellationToken;
        Now = now;
    }

    private DbCommand Command(string sql, params object?[] arguments)
    {
        var result = connection.CreateCommand();
        result.Transaction = transaction;
        result.CommandText = sql;
        for (var index = 0; index < arguments.Length; index++)
        {
            var parameter = result.CreateParameter();
            parameter.ParameterName = "p" + index;
            parameter.Value = arguments[index] ?? DBNull.Value;
            result.Parameters.Add(parameter);
        }
        return result;
    }

    private async Task<int> ExecuteAsync(string sql, params object?[] arguments)
    {
        await using var command = Command(sql, arguments);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> read, params object?[] arguments)
    {
        await using var command = Command(sql, arguments);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<T>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(read(reader));
        return result;
    }

    private static string? Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset Time(DbDataReader reader, int index) => DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));
    private static DateTimeOffset? OptionalTime(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : Time(reader, index);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Protocol.Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Protocol.Json)!;

    public async Task<SessionRecord?> SessionAsync(string id) =>
        (await QueryAsync("SELECT id,tenant,principal,profile,status,lease_expires_ms,connection_id FROM sessions WHERE id=@p0",
            ReadSession, id)).SingleOrDefault();
    public Task<List<SessionRecord>> SessionsAsync() =>
        QueryAsync("SELECT id,tenant,principal,profile,status,lease_expires_ms,connection_id FROM sessions", ReadSession);
    private static SessionRecord ReadSession(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), Time(reader, 5), Text(reader, 6));
    public Task<int> SaveSessionAsync(SessionRecord session) => ExecuteAsync("""
        INSERT INTO sessions(id,tenant,principal,profile,status,lease_expires_ms,connection_id)
        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)
        ON CONFLICT(id) DO UPDATE SET status=excluded.status,lease_expires_ms=excluded.lease_expires_ms,connection_id=excluded.connection_id
        """, session.Id, session.Tenant, session.Principal, session.Profile, session.Status,
        session.LeaseExpiresAt.ToUnixTimeMilliseconds(), session.ConnectionId);
    public Task<int> HeartbeatWorkerAsync(string workerId) => ExecuteAsync("""
        INSERT INTO workers(id,lease_expires_ms) VALUES(@p0,@p1)
        ON CONFLICT(id) DO UPDATE SET lease_expires_ms=excluded.lease_expires_ms
        WHERE workers.lease_expires_ms>@p2
        """, workerId, (Now + RuntimeSettings.WorkerLease).ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds());
    public async Task<bool> WorkerAliveAsync(string workerId) =>
        (await QueryAsync("SELECT lease_expires_ms FROM workers WHERE id=@p0", r => Time(r, 0), workerId))
            .Any(expiry => expiry > Now);
    public Task<int> ExpireWorkerAsync(string workerId) =>
        ExecuteAsync("UPDATE workers SET lease_expires_ms=0 WHERE id=@p0", workerId);

    private const string CallSelect = """
        SELECT state_json,tenant,principal,profile,worker_id,fence,dispatch_status,connection_id,
        server_call_id,conversation_version,completed_ms,last_termination_ms FROM calls
        """;
    private static CallRecord ReadCall(DbDataReader reader) => new()
    {
        State = Deserialize<CallState>(reader.GetString(0)),
        Tenant = reader.GetString(1),
        Principal = reader.GetString(2),
        Profile = reader.GetString(3),
        WorkerId = reader.GetString(4),
        Fence = reader.GetInt64(5),
        DispatchStatus = reader.GetString(6),
        Handle = reader.IsDBNull(7) ? null : new ProviderHandle(reader.GetString(7), Text(reader, 8)),
        ConversationVersion = reader.GetInt64(9),
        CompletedAt = OptionalTime(reader, 10),
        LastTerminationAt = OptionalTime(reader, 11)
    };
    public async Task<CallRecord?> CallAsync(string callId) =>
        (await QueryAsync(CallSelect + " WHERE id=@p0", ReadCall, callId)).SingleOrDefault();
    public Task<List<CallRecord>> ActiveCallsAsync() => QueryAsync(CallSelect + " WHERE active=1", ReadCall);
    public Task<List<CallRecord>> SessionCallsAsync(string sessionId) =>
        QueryAsync(CallSelect + " WHERE session_id=@p0 ORDER BY id", ReadCall, sessionId);
    public async Task<bool> HasActiveCallAsync(Caller caller, string profile) =>
        (await QueryAsync("SELECT id FROM calls WHERE tenant=@p0 AND principal=@p1 AND profile=@p2 AND active=1",
            r => r.GetString(0), caller.Tenant, caller.Principal, profile)).Count != 0;
    public Task<int> SaveCallAsync(CallRecord call) => ExecuteAsync("""
        INSERT INTO calls(id,session_id,tenant,principal,profile,worker_id,fence,dispatch_status,connection_id,
        server_call_id,conversation_version,completed_ms,last_termination_ms,state_json,active,deadline_ms,last_sequence)
        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16)
        ON CONFLICT(id) DO UPDATE SET fence=excluded.fence,dispatch_status=excluded.dispatch_status,
        connection_id=excluded.connection_id,server_call_id=excluded.server_call_id,
        conversation_version=excluded.conversation_version,completed_ms=excluded.completed_ms,
        last_termination_ms=excluded.last_termination_ms,state_json=excluded.state_json,active=excluded.active,
        last_sequence=excluded.last_sequence
        """, call.State.CallId, call.State.SessionId, call.Tenant, call.Principal, call.Profile,
        call.WorkerId, call.Fence, call.DispatchStatus, call.Handle?.ConnectionId, call.Handle?.ServerCallId,
        call.ConversationVersion, call.CompletedAt?.ToUnixTimeMilliseconds(),
        call.LastTerminationAt?.ToUnixTimeMilliseconds(), Serialize(call.State), call.Active ? 1 : 0,
        call.State.Deadline.ToUnixTimeMilliseconds(), call.State.LastSequence);

    public async Task<CallRecord> OwnedCallAsync(Caller caller, string id, string? sessionId = null)
    {
        var call = await CallAsync(id);
        if (call is null || !call.OwnedBy(caller) || (sessionId is not null && call.State.SessionId != sessionId))
            throw new ControlException("NOT_FOUND", 404);
        var session = await SessionAsync(call.State.SessionId);
        if (session is null || !session.OwnedBy(caller))
            throw new ControlException("NOT_FOUND", 404);
        return call;
    }

    public async Task GuardActionAsync(CallRecord call, string workerId, long fence, bool connected = false)
    {
        if (call.WorkerId != workerId || call.Fence != fence || !await WorkerAliveAsync(workerId))
            throw new ControlException("WORKER_FENCED");
        if (call.State.Deadline <= Now) throw new ControlException("DEADLINE_EXPIRED");
        var session = await SessionAsync(call.State.SessionId);
        if (session is null || !session.Live(Now)) throw new ControlException("SESSION_REVOKED");
        if (call.State.Lifecycle is "ending" or "termination_unknown" || Safe.Terminal(call.State))
            throw new ControlException("CALL_ENDED");
        if (connected && call.State.Lifecycle != "connected") throw new ControlException("CALL_NOT_CONNECTED");
    }

    private const string CommandSelect = "SELECT tenant,principal,key_hash,payload_hash,receipt_json FROM commands";
    private static StoredCommand ReadCommand(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Deserialize<CommandReceipt>(reader.GetString(4)));
    public async Task<StoredCommand?> CommandAsync(string commandId) =>
        (await QueryAsync(CommandSelect + " WHERE id=@p0", ReadCommand, commandId)).SingleOrDefault();
    public async Task<StoredCommand?> CommandByKeyAsync(Caller caller, string keyHash) =>
        (await QueryAsync(CommandSelect + " WHERE tenant=@p0 AND principal=@p1 AND key_hash=@p2",
            ReadCommand, caller.Tenant, caller.Principal, keyHash)).SingleOrDefault();
    public Task<List<StoredCommand>> PendingCommandsAsync(string callId) =>
        QueryAsync(CommandSelect + " WHERE call_id=@p0 AND status='accepted'", ReadCommand, callId);
    public Task<int> SaveCommandAsync(StoredCommand command) => ExecuteAsync("""
        INSERT INTO commands(id,session_id,call_id,tenant,principal,key_hash,payload_hash,status,receipt_json)
        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)
        ON CONFLICT(id) DO UPDATE SET status=excluded.status,receipt_json=excluded.receipt_json
        """, command.Receipt.CommandId, command.Receipt.SessionId, command.Receipt.CallId,
        command.Tenant, command.Principal, command.KeyHash, command.PayloadHash,
        command.Receipt.Status, Serialize(command.Receipt));

    public async Task<EventEnvelope> EmitStateAsync(CallRecord call, string type = "call.state_changed", string? commandId = null)
    {
        if (type is not ("call.state_changed" or "call.result" or "call.failed"
            or "call.termination_requested" or "call.termination_confirmed"))
            throw new InvalidOperationException("UNSAFE_DURABLE_EVENT");
        Advance(call);
        return await InsertEventAsync(call, type, new JsonObject { ["state"] = Safe.Json(call.State) }, commandId, true);
    }
    public async Task<EventEnvelope> EmitReceiptAsync(CallRecord call, CommandReceipt receipt)
    {
        Advance(call);
        return await InsertEventAsync(call, "command." + receipt.Status,
            new JsonObject { ["receipt"] = Safe.Json(receipt) }, receipt.CommandId, true);
    }
    public async Task<EventEnvelope> EmitApprovalAsync(CallRecord call, ApprovalView approval)
    {
        Advance(call);
        // Strip content even if a caller passes an in-memory view.
        var metadata = approval with { Description = null, MaterialTerms = null };
        return await InsertEventAsync(call, approval.Status == "expired" ? "approval.expired" : "approval.resolved",
            Safe.Json(metadata), null, true);
    }
    public async Task<EventEnvelope> EmitWarningAsync(CallRecord call, string code)
    {
        Advance(call);
        return await InsertEventAsync(call, "call.warning", new JsonObject { ["code"] = Safe.ProviderCode(code) }, null, true);
    }
    public async Task<EventEnvelope> EmitVolatileAsync(CallRecord call, string type, JsonObject payload)
    {
        if (type is not ("transcript.partial" or "transcript.final" or "transcript.interrupted"
            or "transcript.gap" or "approval.requested" or "summary.ready"))
            throw new InvalidOperationException("INVALID_VOLATILE_EVENT");
        Advance(call);
        return await InsertEventAsync(call, type, payload, null, false);
    }
    private void Advance(CallRecord call) =>
        call.State = call.State with { LastSequence = call.State.LastSequence + 1, UpdatedAt = Now };
    private async Task<EventEnvelope> InsertEventAsync(CallRecord call, string type, JsonObject payload,
        string? commandId, bool durable)
    {
        var envelope = new EventEnvelope(Protocol.Version, Safe.Id("evt"), call.State.SessionId,
            call.State.CallId, call.State.LastSequence, Now, type, commandId, payload);
        await ExecuteAsync("""
            INSERT INTO events(call_id,sequence,event_id,session_id,timestamp_ms,type,command_id,payload_json)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)
            """, call.State.CallId, envelope.Sequence, envelope.EventId, envelope.SessionId,
            Now.ToUnixTimeMilliseconds(), type, commandId, durable ? Serialize(payload) : null);
        await SaveCallAsync(call);
        return envelope;
    }
    public Task<List<StoredEvent>> EventsAsync(string callId, long after, int limit = 128) =>
        QueryAsync("""
            SELECT event_id,session_id,call_id,sequence,timestamp_ms,type,command_id,payload_json
            FROM events WHERE call_id=@p0 AND sequence>@p1 ORDER BY sequence LIMIT @p2
            """, r => new StoredEvent(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                Time(r, 4), r.GetString(5), Text(r, 6),
                r.IsDBNull(7) ? null : JsonNode.Parse(r.GetString(7))!.AsObject()), callId, after, limit);

    public Task<List<StoredApproval>> ApprovalsAsync(string callId) =>
        QueryAsync("""
            SELECT id,call_id,action_hash,expires_ms,status,version,actor,tool_hash FROM approvals
            WHERE call_id=@p0 ORDER BY expires_ms,id
            """, r => new StoredApproval(new ApprovalView(r.GetString(0), r.GetString(1), r.GetString(2),
                Time(r, 3), r.GetString(4), r.GetInt64(5), Actor: Text(r, 6)), r.GetString(7)), callId);
    public Task<int> SaveApprovalAsync(StoredApproval approval) => ExecuteAsync("""
        INSERT INTO approvals(id,call_id,action_hash,expires_ms,status,version,actor,tool_hash)
        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)
        ON CONFLICT(id) DO UPDATE SET status=excluded.status,actor=excluded.actor
        """, approval.View.ApprovalId, approval.View.CallId, approval.View.ActionHash,
        approval.View.ExpiresAt.ToUnixTimeMilliseconds(), approval.View.Status,
        approval.View.ConversationVersion, approval.View.Actor, approval.ToolHash);
    public async Task<List<StoredApproval>> InvalidateApprovalsAsync(CallRecord call, string status = "invalidated")
    {
        var changed = new List<StoredApproval>();
        foreach (var approval in (await ApprovalsAsync(call.State.CallId)).Where(a => a.View.Status == "pending"))
        {
            var updated = approval with { View = approval.View with { Status = status } };
            await SaveApprovalAsync(updated);
            await EmitApprovalAsync(call, updated.View);
            changed.Add(updated);
        }
        return changed;
    }

    public async Task<bool> RememberProviderEventAsync(string callId, string? eventId)
    {
        if (eventId is null) return true;
        return await ExecuteAsync("""
            INSERT INTO provider_events(call_id,event_hash) VALUES(@p0,@p1)
            ON CONFLICT(call_id,event_hash) DO NOTHING
            """, callId, Safe.Hash(eventId)) == 1;
    }
    public async Task<bool> RememberToolAsync(string callId, string toolId, JsonObject arguments)
    {
        var hash = Safe.CanonicalHash(arguments);
        var old = await QueryAsync("SELECT args_hash FROM tool_requests WHERE call_id=@p0 AND tool_hash=@p1",
            r => r.GetString(0), callId, Safe.Hash(toolId));
        if (old.Count > 0)
        {
            if (old[0] != hash) throw new ControlException("TOOL_ID_CONFLICT");
            return false;
        }
        await ExecuteAsync("INSERT INTO tool_requests(call_id,tool_hash,args_hash,status) VALUES(@p0,@p1,@p2,'claimed')",
            callId, Safe.Hash(toolId), hash);
        return true;
    }
    public async Task<bool> RememberSegmentAsync(string callId, string segmentId, long revision, bool final, bool interrupted)
    {
        var old = (await QueryAsync("""
            SELECT revision,is_final,interrupted FROM transcript_segments WHERE call_id=@p0 AND segment_hash=@p1
            """, r => (Revision: r.GetInt64(0), Final: r.GetInt32(1) != 0, Interrupted: r.GetInt32(2) != 0),
            callId, Safe.Hash(segmentId))).ToArray();
        if (old.Length > 0 && (old[0].Final && !interrupted
            || revision < old[0].Revision || (revision == old[0].Revision
                && (!final || old[0].Final) && (!interrupted || old[0].Interrupted))))
            return false;
        await ExecuteAsync("""
            INSERT INTO transcript_segments(call_id,segment_hash,revision,is_final,interrupted) VALUES(@p0,@p1,@p2,@p3,@p4)
            ON CONFLICT(call_id,segment_hash) DO UPDATE SET revision=excluded.revision,
            is_final=excluded.is_final,interrupted=excluded.interrupted
            """, callId, Safe.Hash(segmentId), revision,
            final || (old.Length > 0 && old[0].Final) ? 1 : 0,
            interrupted || (old.Length > 0 && old[0].Interrupted) ? 1 : 0);
        return true;
    }

    public Task<int> SaveAttemptAsync(TerminationAttempt attempt) => ExecuteAsync("""
        INSERT INTO termination_attempts(id,call_id,provider_mode,reason,started_ms,completed_ms,status,executor,connection_id)
        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)
        ON CONFLICT(id) DO UPDATE SET completed_ms=excluded.completed_ms,status=excluded.status
        """, attempt.Id, attempt.CallId, attempt.ProviderMode, attempt.Reason,
        attempt.StartedAt.ToUnixTimeMilliseconds(), attempt.CompletedAt?.ToUnixTimeMilliseconds(),
        attempt.Status, attempt.Executor, attempt.ConnectionId);
    public Task<List<TerminationAttempt>> AttemptsAsync(string callId) => QueryAsync("""
        SELECT id,call_id,provider_mode,reason,started_ms,completed_ms,status,executor,connection_id
        FROM termination_attempts WHERE call_id=@p0 ORDER BY started_ms,id
        """, r => new TerminationAttempt(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            Time(r, 4), OptionalTime(r, 5), r.GetString(6), r.GetString(7), Text(r, 8)), callId);
    public async Task<FakeCallRecord?> FakeCallAsync(string connectionId) => (await QueryAsync("""
        SELECT call_id,connection_id,status,dial_count,hangup_count,hangup_unknown,created_ms,terminated_ms
        FROM fake_calls WHERE connection_id=@p0
        """, r => new FakeCallRecord(r.GetString(0), r.GetString(1), r.GetString(2),
            r.GetInt32(3), r.GetInt32(4), r.GetInt32(5) != 0, Time(r, 6), OptionalTime(r, 7)), connectionId)).SingleOrDefault();
    public Task<int> SaveFakeCallAsync(FakeCallRecord call) => ExecuteAsync("""
        INSERT INTO fake_calls(call_id,connection_id,status,dial_count,hangup_count,hangup_unknown,created_ms,terminated_ms)
        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)
        ON CONFLICT(call_id) DO UPDATE SET status=excluded.status,dial_count=excluded.dial_count,
        hangup_count=excluded.hangup_count,terminated_ms=excluded.terminated_ms
        """, call.CallId, call.ConnectionId, call.Status, call.DialCount, call.HangupCount,
        call.HangupUnknown ? 1 : 0, call.CreatedAt.ToUnixTimeMilliseconds(), call.TerminatedAt?.ToUnixTimeMilliseconds());
    public Task<int> RecordFakeOperationAsync(string callId, string operation)
    {
        if (operation is not ("instruct" or "dtmf" or "tool")) throw new InvalidOperationException("INVALID_SIMULATION_OPERATION");
        return ExecuteAsync("""
            INSERT INTO fake_operations(call_id,operation,operation_count) VALUES(@p0,@p1,1)
            ON CONFLICT(call_id,operation) DO UPDATE SET operation_count=fake_operations.operation_count+1
            """, callId, operation);
    }
    public async Task<long> FakeOperationCountAsync(string callId, string operation) =>
        (await QueryAsync("SELECT operation_count FROM fake_operations WHERE call_id=@p0 AND operation=@p1",
            r => r.GetInt64(0), callId, operation)).SingleOrDefault();

    public async Task<int> PruneAsync()
    {
        var cutoff = Now.AddHours(-72).ToUnixTimeMilliseconds();
        var count = await ExecuteAsync("DELETE FROM calls WHERE active=0 AND completed_ms<@p0", cutoff);
        await ExecuteAsync("""
            DELETE FROM sessions WHERE status='revoked' AND lease_expires_ms<@p0
            AND NOT EXISTS(SELECT 1 FROM calls WHERE calls.session_id=sessions.id)
            AND NOT EXISTS(SELECT 1 FROM commands WHERE commands.session_id=sessions.id)
            """, cutoff);
        await ExecuteAsync("DELETE FROM workers WHERE lease_expires_ms<@p0", cutoff);
        return count;
    }
}
