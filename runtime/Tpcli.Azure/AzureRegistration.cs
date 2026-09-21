using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tpcli.Contracts;

namespace Tpcli.Azure;

/// <summary>
/// Implement with the runtime's durable control store. Bind once only for a known
/// Azure call with dispatch intent; retain stopped-call bindings for late callbacks.
/// </summary>
public interface IAzureCallCorrelation
{
    Task<bool> TryBindAsync(string callId, string connectionId, string? serverCallId, CancellationToken cancellationToken);
}

internal sealed class UnavailableAzureCallCorrelation : IAzureCallCorrelation
{
    public Task<bool> TryBindAsync(string callId, string connectionId, string? serverCallId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

public static class AzureRegistration
{
    public static IServiceCollection AddTpcliAzure(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new AzureOptions();
        configuration.GetSection("Azure").Bind(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AzureCredentials>();
        services.AddSingleton<ICallAutomationTransport, CallAutomationTransport>();
        services.AddSingleton<IVoiceTransportFactory, VoiceLiveTransportFactory>();
        services.AddSingleton<IMediaAuthentication, UnverifiedMediaAuthentication>();
        services.AddSingleton(sp => new CallbackAuthentication(sp.GetRequiredService<AzureOptions>()));
        services.AddSingleton<CallbackReplayGuard>();
        services.AddSingleton<AzureCallRegistry>();
        services.TryAddSingleton<IAzureCallCorrelation, UnavailableAzureCallCorrelation>();
        services.AddSingleton<CorrelateCall>(sp => sp.GetRequiredService<IAzureCallCorrelation>().TryBindAsync);
        services.AddSingleton<AcsCallbackProcessor>();
        services.AddSingleton<ICallProviderFactory, AzureCallProviderFactory>();
        services.AddSingleton<ICallTerminator, AzureCallTerminator>();
        services.AddHttpLoggingInterceptor<AzurePrivacyLoggingInterceptor>();
        return services;
    }

    public static WebApplication MapTpcliAzureEndpoints(this WebApplication app)
    {
        app.UseWebSockets();
        // These routes perform their own ACS authentication, not the CLI's Entra policy.
        app.MapPost("/azure/callbacks/{callId}", CallbackAsync).AllowAnonymous();
        app.MapGet("/azure/media/{callId}", MediaAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> CallbackAsync(HttpContext http, string callId, AcsCallbackProcessor processor)
    {
        if (!http.Request.IsHttps || http.Request.QueryString.HasValue)
            return Error("CALLBACK_TRANSPORT_INVALID", StatusCodes.Status400BadRequest);
        try
        {
            if (http.Request.ContentLength > 262_144)
                return Error("CALLBACK_TOO_LARGE", StatusCodes.Status413PayloadTooLarge);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted).ConfigureAwait(false)) != 0)
            {
                if (body.Length + read > 262_144)
                    return Error("CALLBACK_TOO_LARGE", StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, read);
            }
            await processor.ProcessAsync(callId, http.Request.Headers.Authorization.ToString(), body.ToArray(), http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (ProviderException ex)
        {
            return Error(ex.Code, ex.Code switch
            {
                "CALLBACK_UNAUTHORIZED" => StatusCodes.Status401Unauthorized,
                "CALLBACK_CORRELATION_REJECTED" or "CALLBACK_REPLAY_REJECTED" => StatusCodes.Status403Forbidden,
                "CALLBACK_RETRY_REQUIRED" or "CALLBACK_CAPACITY_EXCEEDED" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest
            });
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception)
        {
            return Error("CALLBACK_RETRY_REQUIRED", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task MediaAsync(HttpContext http, string callId, IMediaAuthentication authentication,
        AzureCallRegistry registry)
    {
        try
        {
            if (!http.Request.IsHttps || http.Request.QueryString.HasValue || !http.WebSockets.IsWebSocketRequest)
                throw new ProviderException("MEDIA_TRANSPORT_INVALID");
            // No key in a URL, callback JWT assumption, IP-only rule, or anonymous fallback.
            var identity = await authentication.AuthenticateAsync(http, callId, http.RequestAborted).ConfigureAwait(false);
            if (identity.CallId != callId ||
                http.Request.Headers["x-ms-call-connection-id"].ToString() != identity.ConnectionId ||
                http.Request.Headers["x-ms-call-correlation-id"].ToString() != identity.CorrelationId)
                throw new ProviderException("MEDIA_CORRELATION_REJECTED");
            var connection = registry.Find(callId) ?? throw new ProviderException("MEDIA_CALL_UNAVAILABLE");
            using var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await connection.RunMediaAsync(socket, identity, http.RequestAborted).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            if (!http.Response.HasStarted)
                await Error(ex.Code, ex.Code == "MEDIA_AUTH_UNVERIFIED"
                    ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden).ExecuteAsync(http).ConfigureAwait(false);
            else http.Abort();
        }
        catch (Exception)
        {
            if (!http.Response.HasStarted)
                await Error("MEDIA_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable).ExecuteAsync(http).ConfigureAwait(false);
            else http.Abort();
        }
    }

    private static IResult Error(string code, int status) =>
        Results.Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code } }, statusCode: status);
}

internal sealed class AzurePrivacyLoggingInterceptor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
    {
        if (logContext.HttpContext.Request.Path.StartsWithSegments("/azure"))
            logContext.LoggingFields = HttpLoggingFields.None;
        return ValueTask.CompletedTask;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext) => OnRequestAsync(logContext);
}
