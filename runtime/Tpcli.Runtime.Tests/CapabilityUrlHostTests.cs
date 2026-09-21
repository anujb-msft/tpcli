using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tpcli.Azure;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class CapabilityUrlHostTests
{
    private static readonly string[] TransportLogCategories =
    [
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.Server.Kestrel.BadRequests",
        "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware",
        "Azure.Core",
        "Azure.Communication.CallAutomation",
        "System.Net.Http.HttpClient.CallAutomation.LogicalHandler",
        "System.Net.WebSockets.Client",
        "Microsoft.Extensions.Http.DefaultHttpClientFactory",
        "Microsoft.ApplicationInsights.AspNetCore",
        "OpenTelemetry.Instrumentation.AspNetCore"
    ];

    [Theory]
    [InlineData("/azure/media/missing?media_grant={0}", HttpStatusCode.Forbidden)]
    [InlineData("/azure/media/missing?media_grant={0}&extra=value", HttpStatusCode.Forbidden)]
    [InlineData("/azure/media/missing?media_grant={0}&media_grant={0}", HttpStatusCode.Forbidden)]
    [InlineData("/azure/media/missing?media_grant={0}%2f", HttpStatusCode.Forbidden)]
    [InlineData("/azure/media/missing?unknown={0}", HttpStatusCode.Forbidden)]
    [InlineData("/missing?media_grant={0}", HttpStatusCode.NotFound)]
    [InlineData("/missing?%6dedia_grant={0}", HttpStatusCode.NotFound)]
    [InlineData("/missing?MEDIA_GRANT={0}", HttpStatusCode.NotFound)]
    [InlineData("/healthz?media_grant={0}", HttpStatusCode.OK)]
    [InlineData("/v1/sessions/missing/control?media_grant={0}", HttpStatusCode.Unauthorized)]
    public async Task RejectedAndMisdirectedCapabilitiesAreScrubbedBeforeRuntimeMiddleware(
        string pathTemplate, HttpStatusCode expected)
    {
        await using var host = await PrivacyHost.StartAsync();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        using var response = await host.Client.GetAsync(string.Format(pathTemplate, token));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(body.Contains(token, StringComparison.Ordinal), "Capability appeared in a response body.");
        var request = Assert.Single(host.Requests);
        Assert.Equal("", request.Query);
        Assert.Equal(0, request.QueryCount);
        Assert.False(request.RawTarget.Contains('?'), "RawTarget retained a query.");
        Assert.False(request.RawTarget.Contains(token, StringComparison.Ordinal), "RawTarget retained a capability.");
        var activity = Assert.Single(host.Activities);
        Assert.False(activity.AllDataRequested);
        Assert.DoesNotContain(activity.Tags, pair =>
            pair.Key is "url.full" or "http.target" or "http.query");
        Assert.Contains(activity.Tags, pair => pair.Key == "server.address" && pair.Value?.ToString() == "localhost");
        Assert.Contains(host.Logs.Entries, entry => entry.Contains("SANITIZED_REQUEST", StringComparison.Ordinal));
        Assert.False(host.Logs.Entries.Any(entry => entry.Contains(token, StringComparison.Ordinal)),
            "Capability appeared in captured log messages, structured values, exceptions, or scopes.");
    }

    [Fact]
    public async Task ProviderSpecificVerboseRulesCannotReenablePreMiddlewareTransportLogs()
    {
        await using var host = await PrivacyHost.StartAsync();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var unsafeTarget = "/azure/media/missing?media_grant=" + token + "&invalid=true";
        foreach (var category in TransportLogCategories)
            host.LoggerFactory.CreateLogger(category).LogWarning(new InvalidOperationException(unsafeTarget),
                "Rejected transport target {Target}", unsafeTarget);
        host.LoggerFactory.CreateLogger("Tpcli.PrivacyFixture").LogWarning("SAFE_DIAGNOSTIC");
        Assert.Contains(host.Logs.Entries, entry => entry.Contains("SAFE_DIAGNOSTIC", StringComparison.Ordinal));
        Assert.False(host.Logs.Entries.Any(entry => entry.Contains(token, StringComparison.Ordinal)),
            "A transport logger exposed a capability despite host privacy filtering.");
    }

    private sealed record RequestObservation(string Query, int QueryCount, string RawTarget);
    private sealed record ActivityObservation(bool AllDataRequested, KeyValuePair<string, object?>[] Tags);

    private sealed class PrivacyHost : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        private WebApplication application = null!;
        internal readonly ConcurrentQueue<RequestObservation> Requests = new();
        internal readonly ConcurrentQueue<ActivityObservation> Activities = new();
        internal readonly CaptureLogs Logs = new();
        internal HttpClient Client { get; private set; } = null!;
        internal ILoggerFactory LoggerFactory => application.Services.GetRequiredService<ILoggerFactory>();

        internal static async Task<PrivacyHost> StartAsync()
        {
            var host = new PrivacyHost();
            Directory.CreateDirectory(host.directory);
            try
            {
                host.application = RuntimeApplication.Build([], builder =>
                {
                    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Tpcli:Mode"] = "local-fake",
                        ["Tpcli:FakeToken"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                        ["Tpcli:Store:Provider"] = "sqlite",
                        ["Tpcli:Store:ConnectionString"] = "Data Source=" + Path.Combine(host.directory, "privacy.db"),
                        ["urls"] = "http://127.0.0.1:5080",
                        ["Logging:LogLevel:Default"] = "Trace"
                    });
                    builder.WebHost.UseTestServer();
                    builder.Services.AddSingleton<IStartupFilter>(new ActivitySeed(host.Activities));
                    // Install the production Azure privacy filters, then let the real host
                    // select its fake provider. No Azure credential or SDK transport is used.
                    builder.Services.AddTpcliAzure(builder.Configuration);
                    builder.Services.AddSingleton<IStartupFilter>(new RequestProbe(host.Requests));
                    builder.Services.Configure<LoggerFilterOptions>(options =>
                    {
                        foreach (var category in TransportLogCategories)
                            options.Rules.Add(new LoggerFilterRule(typeof(CaptureLogs).FullName, category, LogLevel.Trace, null));
                    });
                });
                host.LoggerFactory.AddProvider(host.Logs);
                host.application.MapTpcliAzureEndpoints();
                await host.application.StartAsync();
                host.Client = host.application.GetTestClient();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (application is not null)
            {
                await application.StopAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await application.Services.GetRequiredService<TerminationEngine>().DrainAsync(timeout.Token);
                await application.DisposeAsync();
            }
            Client?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ActivitySeed(ConcurrentQueue<ActivityObservation> observations) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, downstream) =>
            {
                using var activity = new Activity("capability-fixture").Start();
                activity.SetTag("url.full", "http://localhost" + http.Request.Path + http.Request.QueryString);
                activity.SetTag("http.target", http.Request.Path + http.Request.QueryString);
                activity.SetTag("http.query", http.Request.QueryString.Value);
                activity.SetTag("server.address", "localhost");
                await downstream(http);
                observations.Enqueue(new ActivityObservation(activity.IsAllDataRequested, activity.TagObjects.ToArray()));
            });
            next(app);
        };
    }

    private sealed class RequestProbe(ConcurrentQueue<RequestObservation> observations) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, downstream) =>
            {
                var query = http.Request.QueryString.Value ?? "";
                var rawTarget = http.Features.Get<IHttpRequestFeature>()!.RawTarget;
                observations.Enqueue(new RequestObservation(query, http.Request.Query.Count, rawTarget));
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tpcli.PrivacyFixture")
                    .LogWarning("SANITIZED_REQUEST {Query} {RawTarget}", query, rawTarget);
                await downstream(http);
            });
            next(app);
        };
    }

    private sealed class CaptureLogs : ILoggerProvider, ISupportExternalScope
    {
        internal readonly ConcurrentQueue<string> Entries = new();
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public void Dispose() { }

        private static void AppendState(object? state, StringBuilder text)
        {
            text.Append(state);
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                foreach (var field in fields) text.Append(field.Key).Append('=').Append(field.Value);
        }

        private sealed class CaptureLogger(CaptureLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
                Func<TState, Exception?, string> formatter)
            {
                var text = new StringBuilder(category).Append(formatter(state, error)).Append(error);
                AppendState(state, text);
                owner.scopes.ForEachScope(AppendState, text);
                owner.Entries.Enqueue(text.ToString());
            }
        }
    }
}
