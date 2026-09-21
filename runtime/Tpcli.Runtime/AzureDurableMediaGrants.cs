using Tpcli.Azure;
using Tpcli.Core;

namespace Tpcli.Runtime;

internal sealed class AzureDurableMediaGrants(DurableMediaGrants grants) : IAzureMediaGrantStore
{
    public async Task<AzureMediaGrantScope?> CaptureScopeAsync(string callId, string sessionId, CancellationToken cancellationToken)
    {
        var scope = await grants.CaptureScopeAsync(callId, sessionId, cancellationToken);
        return scope is null ? null : new(scope.CallId, scope.SessionId, scope.TenantId, scope.PrincipalId,
            scope.WorkerId, scope.WorkerFence, scope.OwnerGeneration);
    }

    public Task<bool> TryIssueAsync(AzureMediaGrantScope scope, string digest, string origin, string path,
        DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        grants.TryIssueAsync(Convert(scope), digest, origin, path, expiresAt, cancellationToken);

    public Task<bool> TryConsumeAsync(AzureMediaGrantScope scope, string digest, string origin, string path,
        CancellationToken cancellationToken) =>
        grants.TryConsumeAsync(Convert(scope), digest, origin, path, cancellationToken);

    private static MediaGrantScope Convert(AzureMediaGrantScope scope) =>
        new(scope.CallId, scope.SessionId, scope.TenantId, scope.PrincipalId,
            scope.WorkerId, scope.WorkerFence, scope.OwnerGeneration);
}
