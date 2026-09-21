using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tpcli.Contracts;

namespace Tpcli.Azure.Tests;

public sealed class MediaPrivacyTests
{
    [Fact]
    public void PrivacyFilterPrecedesAlreadyRegisteredHostStartupFilters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStartupFilter, ExistingHostFilter>();
        services.AddTpcliAzure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureMediaPrivacyFilter>(provider.GetServices<IStartupFilter>().First());
    }

    [Fact]
    public void InfrastructureFiltersOverrideVerboseProviderSpecificRules()
    {
        var capture = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddLogging(log =>
        {
            log.SetMinimumLevel(LogLevel.Trace);
            log.AddProvider(capture);
            log.AddFilter<CapturedLogs>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Trace);
            log.AddFilter<CapturedLogs>("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.Trace);
            log.AddFilter<CapturedLogs>("Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware", LogLevel.Trace);
            log.AddFilter<CapturedLogs>("Azure.Core", LogLevel.Trace);
            log.AddFilter("System.Net.Http", LogLevel.None);
            log.AddFilter<CapturedLogs>("System.Net.Http.HttpClient.media.LogicalHandler", LogLevel.Trace);
            log.AddFilter<CapturedLogs>("System.Net.Http.HttpClient.media.ClientHandler", LogLevel.Trace);
        });
        services.AddTpcliAzure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();
        var url = Fixture.TransportGrant().ForSdk().AbsoluteUri;
        foreach (var category in new[] { "Microsoft.AspNetCore.Hosting.Diagnostics",
            "Microsoft.AspNetCore.Server.Kestrel.BadRequests", "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware", "Azure.Core",
            "System.Net.Http.HttpClient.media.LogicalHandler", "System.Net.Http.HttpClient.media.ClientHandler" })
            factory.CreateLogger(category).LogError(new InvalidOperationException(url), "Request starting {Url}", url);
        factory.CreateLogger("Tpcli.Azure.Tests").LogInformation("safe application marker");
        Assert.Contains(capture.Messages, message => message.Contains("safe application marker", StringComparison.Ordinal));
        Assert.True(capture.Messages.All(message => !message.Contains(url, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryRawTargetAndActivityAreScrubbedEvenForRejectedCapabilities(bool malformed)
    {
        var uri = Fixture.TransportGrant().ForSdk();
        var raw = uri.Query + (malformed ? "&extra=value" : "");
        var token = uri.Query[(MediaGrantAuthentication.QueryName.Length + 2)..];
        var http = Fixture.MediaRequest(null);
        http.Request.QueryString = new QueryString(raw);
        http.Features.Get<IHttpRequestFeature>()!.RawTarget = uri.AbsolutePath + raw;
        using var activity = new Activity("local-request-fixture").Start();
        activity.SetTag("url.full", uri.AbsoluteUri);
        activity.SetTag("url.query", raw);
        activity.SetTag("http.target", uri.PathAndQuery);
        activity.SetTag("http.request.header.referer", uri.AbsoluteUri);
        Assert.True(AzureMediaPrivacyFilter.Sanitize(http));
        Assert.False(http.Request.QueryString.HasValue);
        Assert.Empty(http.Request.Query);
        Assert.True(!http.Features.Get<IHttpRequestFeature>()!.RawTarget.Contains(token, StringComparison.Ordinal));
        Assert.True(activity.TagObjects.All(tag => !Convert.ToString(tag.Value)!.Contains(token, StringComparison.Ordinal)));
        Assert.False(activity.IsAllDataRequested);
        Assert.Equal(malformed, http.Features.Get<MediaRequestCredential>()!.Digest is null);
        Assert.Equal("no-referrer", http.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public void AnEncodedCapabilityKeyOnAnUnknownRouteIsStillRedactedButNotAccepted()
    {
        var uri = Fixture.TransportGrant().ForSdk();
        var http = Fixture.MediaRequest(null);
        http.Request.Path = "/unknown-route";
        http.Request.QueryString = new QueryString(uri.Query.Replace("media_grant", "%6dedia_grant", StringComparison.Ordinal));
        Assert.True(AzureMediaPrivacyFilter.Sanitize(http));
        Assert.False(http.Request.QueryString.HasValue);
        Assert.Null(http.Features.Get<MediaRequestCredential>()!.Digest);
    }

    [Fact]
    public async Task RealKestrelRequestStartAndInvalidReplayUpgradeLogsNeverContainCapability()
    {
        var registry = new AzureCallRegistry();
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        Uri? issued = null;
        telephony.GrantObserver = grant => issued = grant.ForSdk();
        await using var connection = Fixture.Connection(voice, telephony, new RecordingSink(), registry: registry);
        registry.Add(Fixture.CallId, connection);
        await connection.InitializeAsync(CancellationToken.None);
        connection.Connected(await connection.DialAsync(CancellationToken.None), "correlation-test");
        await connection.AuthenticateMediaAsync(Fixture.MediaRequest(telephony.Credential), CancellationToken.None);

        var capture = new CapturedLogs();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture);
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IProviderEventSink, RecordingSink>();
        builder.Services.AddHttpLogging(options => options.LoggingFields = HttpLoggingFields.All);
        builder.Services.AddTpcliAzure(new ConfigurationBuilder().Build());
        builder.Services.RemoveAll<AzureCallRegistry>();
        builder.Services.AddSingleton(registry);
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            // Local-only test proxy: no external endpoint or TLS credential is used.
            http.Request.Scheme = "https";
            app.Logger.LogInformation("sanitized application request {Path}{Query}", http.Request.Path, http.Request.QueryString);
            await next(http);
        });
        app.UseHttpLogging();
        app.MapTpcliAzureEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
            var unknown = Fixture.TransportGrant().ForSdk();
            var paths = new[] { issued!.PathAndQuery, unknown.PathAndQuery, issued.PathAndQuery + "&extra=value",
                "/azure/media/unknown-call" + issued.Query };
            foreach (var path in paths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address + path);
                request.Headers.Host = "runtime.example.invalid";
                request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
                request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
                request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
                request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
                request.Headers.TryAddWithoutValidation("x-ms-call-connection-id", Fixture.ConnectionId);
                request.Headers.TryAddWithoutValidation("x-ms-call-correlation-id", "correlation-test");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            using (var callback = new HttpRequestMessage(HttpMethod.Post, address + "/azure/callbacks/" + Fixture.CallId + issued.Query))
            {
                callback.Headers.Host = "runtime.example.invalid";
                callback.Content = new StringContent("[]");
                using var response = await client.SendAsync(callback);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains("CALLBACK_TRANSPORT_INVALID", await response.Content.ReadAsStringAsync());
            }
            Assert.Contains(capture.Messages, message => message.Contains("sanitized application request", StringComparison.Ordinal));
            foreach (var uri in new[] { issued, unknown })
            {
                var token = uri.Query[(MediaGrantAuthentication.QueryName.Length + 2)..];
                Assert.True(capture.Messages.All(message => !message.Contains(token, StringComparison.Ordinal)));
            }
            Assert.Equal(1, voice.ConnectCount);
            Fixture.AssertEmptyPrewarm(voice);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ExistingHostFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => next;
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal readonly ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }

        private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
}
