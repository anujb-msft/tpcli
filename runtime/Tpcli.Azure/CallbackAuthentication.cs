using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed record AuthenticatedCallback(string TokenHash, DateTimeOffset ExpiresAt);

internal sealed class CallbackAuthentication
{
    internal const string Issuer = "https://acscallautomation.communication.azure.com";
    internal const string MetadataAddress = Issuer + "/calling/.well-known/acsopenidconfiguration";
    private readonly string _audience;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuration;

    internal CallbackAuthentication(AzureOptions options)
        : this(options.CallAutomation.ResourceId,
            new ConfigurationManager<OpenIdConnectConfiguration>(MetadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                {
                    Timeout = TimeSpan.FromSeconds(10)
                })
                { RequireHttps = true }))
    {
    }

    internal CallbackAuthentication(string audience, IConfigurationManager<OpenIdConnectConfiguration> configuration)
    {
        _audience = audience;
        _configuration = configuration;
    }

    internal async Task CheckDiscoveryAsync(CancellationToken cancellationToken)
    {
        var metadata = await _configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (metadata.Issuer != Issuer || metadata.SigningKeys.Count == 0)
            throw new ProviderException("CALLBACK_AUTH_METADATA_INVALID");
    }

    internal async Task<AuthenticatedCallback> AuthenticateAsync(string authorization, CancellationToken cancellationToken)
    {
        if (authorization.Length > 16_384 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(_audience, out _))
            throw new ProviderException("CALLBACK_UNAUTHORIZED");
        var token = authorization[7..];
        if (token.Length == 0 || token.Any(char.IsWhiteSpace))
            throw new ProviderException("CALLBACK_UNAUTHORIZED");
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var metadata = await _configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                if (metadata.Issuer != Issuer)
                    break;
                var result = await new JsonWebTokenHandler { MaximumTokenSizeInBytes = 16_384 }
                    .ValidateTokenAsync(token, new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = _audience,
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        RequireSignedTokens = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKeys = metadata.SigningKeys,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        ClockSkew = TimeSpan.FromSeconds(30),
                        IncludeTokenOnFailedValidation = false
                    }).ConfigureAwait(false);
                if (result.IsValid && result.SecurityToken is JsonWebToken jwt)
                {
                    return new AuthenticatedCallback(
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
                        new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
                }
                if (attempt == 0 && result.Exception is SecurityTokenSignatureKeyNotFoundException)
                    _configuration.RequestRefresh();
                else
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
        throw new ProviderException("CALLBACK_UNAUTHORIZED");
    }
}

internal enum ReplayDisposition { New, Duplicate }

internal sealed class CallbackReplayGuard(TimeProvider clock, int capacity = 8192)
{
    private sealed record Entry(string Hash, DateTimeOffset ExpiresAt, bool Completed = false);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _events = new(StringComparer.Ordinal);

    internal void BindToken(AuthenticatedCallback token, ReadOnlySpan<byte> body)
    {
        var hash = Convert.ToHexString(SHA256.HashData(body));
        lock (_gate)
        {
            Prune();
            if (_tokens.TryGetValue(token.TokenHash, out var entry))
            {
                if (entry.Hash != hash)
                    throw new ProviderException("CALLBACK_REPLAY_REJECTED");
                return;
            }
            if (_tokens.Count >= capacity)
                throw new ProviderException("CALLBACK_CAPACITY_EXCEEDED");
            _tokens.Add(token.TokenHash, new Entry(hash, token.ExpiresAt.AddSeconds(30)));
        }
    }

    internal ReplayDisposition BeginEvent(string eventKey, ReadOnlySpan<byte> body)
    {
        var hash = Convert.ToHexString(SHA256.HashData(body));
        lock (_gate)
        {
            Prune();
            if (_events.TryGetValue(eventKey, out var entry))
            {
                if (entry.Hash != hash)
                    throw new ProviderException("CALLBACK_REPLAY_REJECTED");
                if (!entry.Completed)
                    throw new ProviderException("CALLBACK_RETRY_REQUIRED");
                return ReplayDisposition.Duplicate;
            }
            if (_events.Count >= capacity)
                throw new ProviderException("CALLBACK_CAPACITY_EXCEEDED");
            _events.Add(eventKey, new Entry(hash, clock.GetUtcNow().AddMinutes(10)));
            return ReplayDisposition.New;
        }
    }

    internal void Complete(string eventKey)
    {
        lock (_gate)
        {
            if (_events.TryGetValue(eventKey, out var entry))
                _events[eventKey] = entry with { Completed = true };
        }
    }

    internal void Retry(string eventKey)
    {
        lock (_gate)
        {
            if (_events.TryGetValue(eventKey, out var entry) && !entry.Completed)
                _events.Remove(eventKey);
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _tokens.Where(p => p.Value.ExpiresAt < now).Select(p => p.Key).ToArray())
            _tokens.Remove(key);
        foreach (var key in _events.Where(p => p.Value.ExpiresAt < now && p.Value.Completed).Select(p => p.Key).ToArray())
            _events.Remove(key);
    }
}
