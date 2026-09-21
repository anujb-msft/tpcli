using Microsoft.AspNetCore.Http;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed record AuthenticatedMedia(string CallId, string ConnectionId, string CorrelationId);

internal interface IMediaAuthentication
{
    bool IsSupported { get; }
    Task<AuthenticatedMedia> AuthenticateAsync(HttpContext context, string callId, CancellationToken cancellationToken);
}

internal sealed class UnverifiedMediaAuthentication : IMediaAuthentication
{
    public bool IsSupported => false;

    public Task<AuthenticatedMedia> AuthenticateAsync(HttpContext context, string callId, CancellationToken cancellationToken) =>
        throw new ProviderException("MEDIA_AUTH_UNVERIFIED");
}
