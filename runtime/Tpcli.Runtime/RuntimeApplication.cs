using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Tpcli.Azure;
using Tpcli.Contracts;
using Tpcli.Core;

namespace Tpcli.Runtime;

public static class RuntimeApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var settings = RuntimeSettings.Load(builder.Configuration);
        settings.Validate(httpServer: true);
        ConfigureTransport(builder, settings);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
        builder.Services.AddSingleton<IPostConfigureOptions<LoggerFilterOptions>, TransportPrivacyLogFilters>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = Protocol.Json.PropertyNamingPolicy;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.AddTpcliControlStore(settings).AddTpcliCallRuntime();
        if (settings.Mode == "azure")
        {
            builder.Services.AddSingleton<IAzureCallCorrelation, AzureDurableCorrelation>();
            builder.Services.AddSingleton<IAzureMediaGrantStore, AzureDurableMediaGrants>();
            builder.Services.AddTpcliAzure(builder.Configuration);
        }
        builder.Services.AddRuntimeAuthentication(settings);
        var app = builder.Build();
        if (settings.Mode == "azure" && settings.TrustedProxies.Length > 0) app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            try
            {
                if (settings.Mode == "local-fake")
                {
                    if (context.Request.IsHttps || !RuntimeAuthentication.LoopbackHost(context.Request.Host.Host)
                        || context.Connection.RemoteIpAddress is { } peer && !IPAddress.IsLoopback(peer))
                        throw new ControlException("LOCAL_FAKE_LOOPBACK_HTTP_ONLY", 403);
                }
                else if (!context.Request.IsHttps) throw new ControlException("HTTPS_REQUIRED", 426);
                if (context.Request.ContentLength > RuntimeSettings.MaxRequestBytes)
                    throw new ControlException("REQUEST_TOO_LARGE", 413);
                context.Response.Headers.CacheControl = "no-store";
                await next(context);
            }
            catch (ControlException error) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error.HttpStatus;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(error.Error), Protocol.Json, context.RequestAborted);
            }
            catch (JsonException) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(new WireError("INVALID_JSON", "INVALID_JSON")),
                    Protocol.Json, context.RequestAborted);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error.StatusCode == 413 ? 413 : 400;
                var code = error.StatusCode == 413 ? "REQUEST_TOO_LARGE" : "INVALID_REQUEST";
                await context.Response.WriteAsJsonAsync(new ErrorResponse(new WireError(code, code)),
                    Protocol.Json, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(new WireError("RUNTIME_UNAVAILABLE", "RUNTIME_UNAVAILABLE", true)),
                    Protocol.Json, context.RequestAborted);
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
        var api = app.MapGroup("/v1").RequireAuthorization();
        api.MapGet("/capabilities", async (HttpContext context, CallRuntime runtime) =>
        {
            var online = false;
            if (context.Request.Query.TryGetValue("online", out var text) && !bool.TryParse(text, out online))
                throw new ControlException("INVALID_ONLINE_FLAG", 400);
            return Results.Json(await runtime.CheckReadinessAsync(online, context.RequestAborted), Protocol.Json);
        });
        api.MapPost("/sessions", async (HttpContext context, CallRuntime runtime) =>
        {
            await ReadLimitedAsync(context.Request, context.RequestAborted);
            var session = await runtime.CreateSessionAsync(context.Caller(), context.RequestAborted);
            context.Response.Headers.Location = "/v1/sessions/" + session.SessionId;
            return Results.Json(session, Protocol.Json, statusCode: 201);
        });
        api.MapDelete("/sessions/{sessionId}", async (string sessionId, HttpContext context, CallRuntime runtime) =>
            Results.Json(await runtime.RevokeAsync(context.Caller(), sessionId, cancellationToken: context.RequestAborted), Protocol.Json));
        api.MapGet("/sessions/{sessionId}/control", ControlSocket.HandleAsync);
        api.MapPost("/commands", async (HttpContext context, CallRuntime runtime) =>
        {
            var request = await ReadJsonAsync<CommandRequest>(context.Request, context.RequestAborted);
            var receipt = await runtime.SubmitAsync(context.Caller(), request, context.RequestAborted);
            context.Response.Headers.Location = "/v1/commands/" + receipt.CommandId;
            return Results.Json(receipt, Protocol.Json, statusCode: 202);
        });
        api.MapGet("/commands/{commandId}", async (string commandId, HttpContext context, CallRuntime runtime) =>
            Results.Json(await runtime.CommandAsync(context.Caller(), commandId, context.RequestAborted), Protocol.Json));
        api.MapGet("/calls/{callId}", async (string callId, HttpContext context, CallRuntime runtime) =>
            Results.Json(await runtime.StateAsync(context.Caller(), callId, context.RequestAborted), Protocol.Json));
        api.MapGet("/calls/{callId}/events", async (string callId, HttpContext context, CallRuntime runtime) =>
        {
            var after = 0L;
            var wait = 0;
            if (context.Request.Query.TryGetValue("after", out var cursor) && !long.TryParse(cursor, out after)
                || context.Request.Query.TryGetValue("wait_seconds", out var seconds) && !int.TryParse(seconds, out wait))
                throw new ControlException("INVALID_CURSOR", 400);
            return Results.Json(await runtime.EventsAsync(context.Caller(), callId, after, wait, context.RequestAborted), Protocol.Json);
        });
        api.MapGet("/calls/{callId}/approvals", async (string callId, HttpContext context, CallRuntime runtime) =>
            Results.Json(await runtime.ApprovalsAsync(context.Caller(), callId, context.RequestAborted), Protocol.Json));
        if (settings.Mode == "local-fake")
        {
            app.MapPost("/test/calls/{callId}/signals", async (string callId, HttpContext context, CallRuntime runtime) =>
            {
                var signal = await ReadJsonAsync<ProviderSignal>(context.Request, context.RequestAborted);
                await runtime.InjectAsync(context.Caller(), callId, signal, context.RequestAborted);
                return Results.Json(new { accepted = true, provider_mode = "local-fake", route = "simulation" }, Protocol.Json, statusCode: 202);
            }).RequireAuthorization();
        }
        else app.MapTpcliAzureEndpoints();
        app.MapGet("/healthz", () => Results.Json(new { runtime = "alive", provider_mode = settings.Mode }, Protocol.Json));
        return app;
    }

    private static void ConfigureTransport(WebApplicationBuilder builder, RuntimeSettings settings)
    {
        var urls = builder.Configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls))
        {
            if (settings.Mode != "local-fake") throw new InvalidOperationException("EXPLICIT_LISTEN_URL_REQUIRED");
            urls = "http://127.0.0.1:5080";
        }
        var configured = urls.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Concat(builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
                .Select(endpoint => endpoint["Url"] ?? "")).ToArray();
        foreach (var url in configured)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || settings.Mode == "local-fake" && (uri.Scheme != "http" || !RuntimeAuthentication.LoopbackHost(uri.Host))
                || settings.Mode == "azure" && uri.Scheme != "https" && settings.TrustedProxies.Length == 0)
                throw new InvalidOperationException("UNSAFE_LISTEN_URL");
        }
        builder.WebHost.UseUrls(urls);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = RuntimeSettings.MaxRequestBytes;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        });
        var proxies = settings.TrustedProxies.Select(value =>
            IPAddress.TryParse(value, out var address) ? address : throw new InvalidOperationException("INVALID_TRUSTED_PROXY")).ToArray();
        builder.Services.PostConfigure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = settings.Mode == "azure" && proxies.Length > 0
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
        });
    }

    private static readonly JsonSerializerOptions InputJson = new(Protocol.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private static async Task<T> ReadJsonAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        var bytes = await ReadLimitedAsync(request, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, InputJson) ?? throw new ControlException("INVALID_JSON", 400);
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (memory.Length + count > RuntimeSettings.MaxRequestBytes) throw new ControlException("REQUEST_TOO_LARGE", 413);
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
}
