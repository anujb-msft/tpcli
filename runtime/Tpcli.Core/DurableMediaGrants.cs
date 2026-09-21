namespace Tpcli.Core;

public sealed record MediaGrantScope(string CallId, string SessionId, string TenantId, string PrincipalId,
    string WorkerId, long WorkerFence, long OwnerGeneration);

public sealed class DurableMediaGrants(ControlStore store, Func<string> localWorker)
{
    public Task<MediaGrantScope?> CaptureScopeAsync(string callId, string sessionId, CancellationToken cancellationToken = default) =>
        store.TransactionAsync(tx => tx.MediaScopeAsync(callId, sessionId, localWorker()), cancellationToken);

    public Task<bool> TryIssueAsync(MediaGrantScope scope, string digest, string origin, string path,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        store.TransactionAsync(tx => tx.IssueMediaGrantAsync(scope, localWorker(), digest, origin, path, expiresAt),
            cancellationToken);

    public Task<bool> TryConsumeAsync(MediaGrantScope scope, string digest, string origin, string path,
        CancellationToken cancellationToken = default) =>
        store.TransactionAsync(tx => tx.ConsumeMediaGrantAsync(scope, localWorker(), digest, origin, path),
            cancellationToken);
}

public sealed partial class ControlTransaction
{
    private static bool MediaCallActive(CallRecord call) =>
        call.State.ProviderMode == "azure"
        && call.State.Lifecycle is "accepted" or "preflight" or "dialing" or "connected";

    private Task<int> SyncOwnerGenerationAsync(SessionRecord session) => ExecuteAsync("""
        INSERT INTO owner_generations(session_id,generation,connection_id) VALUES(@p0,1,@p1)
        ON CONFLICT(session_id) DO UPDATE SET
          generation=CASE WHEN owner_generations.connection_id=excluded.connection_id
            OR (owner_generations.connection_id IS NULL AND excluded.connection_id IS NULL)
            THEN owner_generations.generation ELSE owner_generations.generation+1 END,
          connection_id=excluded.connection_id
        """, session.Id, session.ConnectionId);

    private Task<int> RevokeMediaGrantAsync(string callId) => ExecuteAsync("""
        UPDATE media_grants SET revoked_ms=@p1
        WHERE call_id=@p0 AND consumed_ms IS NULL AND revoked_ms IS NULL
        """, callId, Now.ToUnixTimeMilliseconds());

    internal Task<int> RevokeUnavailableMediaGrantsAsync() => ExecuteAsync("""
        UPDATE media_grants SET revoked_ms=@p0
        WHERE consumed_ms IS NULL AND revoked_ms IS NULL AND
          (expires_ms<=@p0 OR NOT EXISTS (
            SELECT 1 FROM calls c
            JOIN sessions s ON s.id=c.session_id
            JOIN workers w ON w.id=c.worker_id
            JOIN owner_generations g ON g.session_id=s.id
            WHERE c.id=media_grants.call_id AND c.session_id=media_grants.session_id
              AND c.tenant=media_grants.tenant AND s.tenant=media_grants.tenant
              AND c.principal=media_grants.principal AND s.principal=media_grants.principal
              AND c.profile=s.profile AND c.worker_id=media_grants.worker_id
              AND c.fence=media_grants.worker_fence AND g.generation=media_grants.owner_generation
              AND g.connection_id=s.connection_id AND s.status='active'
              AND s.connection_id IS NOT NULL AND s.lease_expires_ms>@p0
              AND w.lease_expires_ms>@p0 AND c.active=1 AND c.deadline_ms>@p0))
        """, Now.ToUnixTimeMilliseconds());

    internal async Task<MediaGrantScope?> MediaScopeAsync(string callId, string sessionId, string localWorker)
    {
        var call = await CallAsync(callId);
        if (call is null || call.State.SessionId != sessionId || call.WorkerId != localWorker
            || !MediaCallActive(call) || call.State.Deadline <= Now || !await WorkerAliveAsync(localWorker))
            return null;
        var owner = await SessionAsync(sessionId);
        if (owner is null || !owner.Live(Now) || string.IsNullOrEmpty(owner.ConnectionId)
            || owner.Tenant != call.Tenant || owner.Principal != call.Principal || owner.Profile != call.Profile)
            return null;
        await SyncOwnerGenerationAsync(owner);
        var generation = (await QueryAsync(
            "SELECT generation FROM owner_generations WHERE session_id=@p0",
            row => row.GetInt64(0), sessionId)).Single();
        return new(callId, sessionId, call.Tenant, call.Principal, localWorker, call.Fence, generation);
    }

    private static bool ValidMediaBinding(MediaGrantScope scope, string digest, string origin, string path) =>
        digest.Length == 64 && digest.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F')
        && path == "/azure/media/" + scope.CallId
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && origin == uri.GetLeftPart(UriPartial.Authority);

    internal async Task<bool> IssueMediaGrantAsync(MediaGrantScope scope, string localWorker, string digest,
        string origin, string path, DateTimeOffset expiresAt)
    {
        if (!ValidMediaBinding(scope, digest, origin, path) || expiresAt <= Now
            || expiresAt > Now.AddSeconds(120)
            || await MediaScopeAsync(scope.CallId, scope.SessionId, localWorker) != scope)
            return false;
        var call = (await CallAsync(scope.CallId))!;
        if (expiresAt > call.State.Deadline) return false;
        return await ExecuteAsync("""
            INSERT INTO media_grants(call_id,session_id,tenant,principal,worker_id,worker_fence,owner_generation,
              digest,origin,path,issued_ms,expires_ms,consumed_ms,revoked_ms)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,NULL,NULL)
            ON CONFLICT DO NOTHING
            """, scope.CallId, scope.SessionId, scope.TenantId, scope.PrincipalId, scope.WorkerId, scope.WorkerFence,
            scope.OwnerGeneration, digest, origin, path, Now.ToUnixTimeMilliseconds(), expiresAt.ToUnixTimeMilliseconds()) == 1;
    }

    internal async Task<bool> ConsumeMediaGrantAsync(MediaGrantScope scope, string localWorker, string digest,
        string origin, string path)
    {
        if (!ValidMediaBinding(scope, digest, origin, path)
            || await MediaScopeAsync(scope.CallId, scope.SessionId, localWorker) != scope)
            return false;
        var call = (await CallAsync(scope.CallId))!;
        if (call.DispatchStatus is not ("intent" or "returned" or "ambiguous")) return false;
        return await ExecuteAsync("""
            UPDATE media_grants SET consumed_ms=@p10
            WHERE call_id=@p0 AND session_id=@p1 AND tenant=@p2 AND principal=@p3
              AND worker_id=@p4 AND worker_fence=@p5 AND owner_generation=@p6
              AND digest=@p7 AND origin=@p8 AND path=@p9
              AND consumed_ms IS NULL AND revoked_ms IS NULL AND expires_ms>@p10
            """, scope.CallId, scope.SessionId, scope.TenantId, scope.PrincipalId, scope.WorkerId, scope.WorkerFence,
            scope.OwnerGeneration, digest, origin, path, Now.ToUnixTimeMilliseconds()) == 1;
    }
}
