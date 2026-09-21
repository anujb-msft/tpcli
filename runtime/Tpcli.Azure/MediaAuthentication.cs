using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Tpcli.Contracts;

namespace Tpcli.Azure;

public sealed record AzureMediaGrantScope(string CallId, string SessionId, string TenantId, string PrincipalId,
    string WorkerId, long WorkerFence, long OwnerGeneration);

/// <summary>
/// Durable, atomic grant storage. Capture the local worker's existing authority;
/// issue/consume must compare every captured scope field to current durable state,
/// require live ownership/worker leases and a non-ending call, and enforce deadline.
/// Persist only the digest and minimal scope metadata. Permit one grant per call.
/// </summary>
public interface IAzureMediaGrantStore
{
    Task<AzureMediaGrantScope?> CaptureScopeAsync(string callId, string sessionId, CancellationToken cancellationToken);
    Task<bool> TryIssueAsync(AzureMediaGrantScope scope, string digest, string origin, string path,
        DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<bool> TryConsumeAsync(AzureMediaGrantScope scope, string digest, string origin, string path,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableAzureMediaGrantStore : IAzureMediaGrantStore
{
    public Task<AzureMediaGrantScope?> CaptureScopeAsync(string callId, string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<AzureMediaGrantScope?>(null);
    public Task<bool> TryIssueAsync(AzureMediaGrantScope scope, string digest, string origin, string path,
        DateTimeOffset expiresAt, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> TryConsumeAsync(AzureMediaGrantScope scope, string digest, string origin, string path,
        CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed record AuthenticatedMedia(string CallId, string ConnectionId, string CorrelationId);
internal sealed record MediaRequestCredential(string? Digest);

internal sealed class MediaTransportGrant
{
    private readonly Uri _origin;
    private readonly string _path;
    private readonly string _token;
    internal DateTimeOffset ExpiresAt { get; }

    internal MediaTransportGrant(Uri origin, string path, string token, DateTimeOffset expiresAt)
    {
        _origin = origin;
        _path = path;
        _token = token;
        ExpiresAt = expiresAt;
    }

    internal Uri ForSdk() => new UriBuilder(_origin)
    {
        Scheme = "wss",
        Port = -1,
        Path = _path,
        Query = MediaGrantAuthentication.QueryName + "=" + _token
    }.Uri;

    public override string ToString() => "MediaTransportGrant [redacted]";
}

internal sealed class MediaGrantAuthentication(AzureOptions options, IAzureMediaGrantStore store, TimeProvider clock)
{
    internal const string QueryName = "media_grant";
    internal bool IsSupported => store is not UnavailableAzureMediaGrantStore;

    internal async Task<AzureMediaGrantScope> CaptureScopeAsync(CallContext context, CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new ProviderException("MEDIA_GRANT_STORE_UNCONFIGURED");
        var scope = await StoreAsync(() => store.CaptureScopeAsync(context.CallId, context.SessionId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (scope is null || scope.CallId != context.CallId || scope.SessionId != context.SessionId ||
            string.IsNullOrEmpty(scope.TenantId) || string.IsNullOrEmpty(scope.PrincipalId) ||
            string.IsNullOrEmpty(scope.WorkerId) || scope.WorkerFence < 0 || scope.OwnerGeneration < 0)
            throw new ProviderException("MEDIA_GRANT_SCOPE_REJECTED");
        return scope;
    }

    internal async Task<MediaTransportGrant> IssueAsync(CallContext context, AzureMediaGrantScope scope,
        CancellationToken cancellationToken)
    {
        EnsureLoggingVerified();
        AzureValidation.Deadline(context, clock);
        if (options.Media.SetupGrantTtlSeconds is < 5 or > 120)
            throw new ProviderException("MEDIA_GRANT_TTL_INVALID");
        if (scope.CallId != context.CallId || scope.SessionId != context.SessionId)
            throw new ProviderException("MEDIA_GRANT_SCOPE_REJECTED");
        var origin = AzureValidation.Endpoint(options.CallAutomation.PublicBaseUrl);
        var path = "/azure/media/" + context.CallId;
        var expiresAt = clock.GetUtcNow().AddSeconds(options.Media.SetupGrantTtlSeconds);
        if (expiresAt > context.Deadline) expiresAt = context.Deadline;
        var random = RandomNumberGenerator.GetBytes(32);
        string token;
        string digest;
        try
        {
            token = WebEncoders.Base64UrlEncode(random);
            digest = Convert.ToHexString(SHA256.HashData(random));
        }
        finally { CryptographicOperations.ZeroMemory(random); }
        if (!await StoreAsync(() => store.TryIssueAsync(scope, digest, origin.GetLeftPart(UriPartial.Authority),
            path, expiresAt, cancellationToken), cancellationToken).ConfigureAwait(false))
            throw new ProviderException("MEDIA_GRANT_ISSUE_REJECTED");
        AzureValidation.Deadline(context, clock);
        if (clock.GetUtcNow() >= expiresAt)
            throw new ProviderException("MEDIA_GRANT_EXPIRED");
        return new MediaTransportGrant(origin, path, token, expiresAt);
    }

    internal async Task RevalidateScopeAsync(CallContext context, AzureMediaGrantScope scope, CancellationToken cancellationToken)
    {
        var current = await CaptureScopeAsync(context, cancellationToken).ConfigureAwait(false);
        if (current != scope) throw new ProviderException("MEDIA_GRANT_SCOPE_REJECTED");
    }

    internal async Task ConsumeAsync(HttpContext http, CallContext context, AzureMediaGrantScope scope,
        CancellationToken cancellationToken)
    {
        EnsureLoggingVerified();
        AzureValidation.Deadline(context, clock);
        var credential = http.Features.Get<MediaRequestCredential>();
        http.Features.Set<MediaRequestCredential>(null);
        if (credential?.Digest is not { } digest || scope.CallId != context.CallId || scope.SessionId != context.SessionId)
            throw new ProviderException("MEDIA_GRANT_REJECTED");
        var expected = AzureValidation.Endpoint(options.CallAutomation.PublicBaseUrl).GetLeftPart(UriPartial.Authority);
        string origin;
        try { origin = AzureValidation.Endpoint(http.Request.Scheme + "://" + http.Request.Host.ToUriComponent()).GetLeftPart(UriPartial.Authority); }
        catch (ProviderException) { throw new ProviderException("MEDIA_GRANT_REJECTED"); }
        var path = http.Request.PathBase.Add(http.Request.Path).Value ?? "";
        if (origin != expected || path != "/azure/media/" + context.CallId ||
            (http.Request.Headers.Origin.Count != 0 && http.Request.Headers.Origin.ToString() != expected))
            throw new ProviderException("MEDIA_GRANT_REJECTED");
        if (!await StoreAsync(() => store.TryConsumeAsync(scope, digest, origin, path, cancellationToken),
            cancellationToken).ConfigureAwait(false))
            throw new ProviderException("MEDIA_GRANT_REJECTED");
    }

    internal void EnsureLoggingVerified()
    {
        if (!options.Media.IsLoggingEvidenceCurrent(clock.GetUtcNow()))
            throw new ProviderException("MEDIA_URL_LOGGING_UNVERIFIED");
    }

    internal static MediaRequestCredential ParseCredential(string query)
    {
        var prefix = "?" + QueryName + "=";
        if (!query.StartsWith(prefix, StringComparison.Ordinal) || query.Length != prefix.Length + 43)
            return new(null);
        var token = query[prefix.Length..];
        if (token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return new(null);
        byte[]? bytes = null;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(token);
            if (bytes.Length != 32 || WebEncoders.Base64UrlEncode(bytes) != token) return new(null);
            return new(Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (FormatException) { return new(null); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<T> StoreAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ProviderException("MEDIA_GRANT_STORE_UNAVAILABLE"); }
    }
}
