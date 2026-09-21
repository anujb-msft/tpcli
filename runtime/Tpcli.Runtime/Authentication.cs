using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Tpcli.Core;

namespace Tpcli.Runtime;

internal sealed class FakeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, RuntimeSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || authorization.Length > 4103)
            return Task.FromResult(AuthenticateResult.Fail("AUTHENTICATION_REQUIRED"));
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.FakeToken!));
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
            return Task.FromResult(AuthenticateResult.Fail("AUTHENTICATION_REQUIRED"));
        var identity = new ClaimsIdentity(
            [new Claim("tid", "local-fake-tenant"), new Claim("oid", "local-fake-principal")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal static class RuntimeAuthentication
{
    public static void AddRuntimeAuthentication(this IServiceCollection services, RuntimeSettings settings)
    {
        if (settings.Mode == "local-fake")
            services.AddAuthentication("LocalFake").AddScheme<AuthenticationSchemeOptions, FakeAuthenticationHandler>("LocalFake", _ => { });
        else
        {
            var tenant = Guid.Parse(settings.TenantId!).ToString("D");
            var issuer = $"https://login.microsoftonline.com/{tenant}/v2.0";
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.Authority = issuer;
                options.Audience = settings.Audience;
                options.RequireHttpsMetadata = true;
                options.IncludeErrorDetails = false;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "oid"
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var claims = context.Principal!;
                        if (!string.Equals(claims.FindFirstValue("tid"), tenant, StringComparison.OrdinalIgnoreCase)
                            || !Guid.TryParse(claims.FindFirstValue("oid"), out _)
                            || !(claims.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .Contains(settings.Scope, StringComparer.Ordinal))
                            context.Fail("INVALID_AUTHORIZATION");
                        return Task.CompletedTask;
                    }
                };
            });
        }
        services.AddAuthorization();
    }

    public static Caller Caller(this HttpContext context)
    {
        var tenant = context.User.FindFirstValue("tid") ?? throw new ControlException("AUTHENTICATION_REQUIRED", 401);
        var principal = context.User.FindFirstValue("oid") ?? throw new ControlException("AUTHENTICATION_REQUIRED", 401);
        return new Caller(Guid.TryParse(tenant, out var tenantId) ? tenantId.ToString("D") : tenant,
            Guid.TryParse(principal, out var principalId) ? principalId.ToString("D") : principal);
    }

    public static bool LoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}
