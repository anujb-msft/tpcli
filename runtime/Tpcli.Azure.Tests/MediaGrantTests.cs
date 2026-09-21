using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Tpcli.Contracts;

namespace Tpcli.Azure.Tests;

public sealed class MediaGrantTests
{
    [Fact]
    public async Task GrantHas256RandomBitsDigestOnlyStorageAndRedactedStringRepresentation()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryGrantStore(clock);
        var auth = new MediaGrantAuthentication(Fixture.Options(clock), store, clock);
        var context = Fixture.Context(clock);
        var scope = await auth.CaptureScopeAsync(context, CancellationToken.None);
        var grant = await auth.IssueAsync(context, scope, CancellationToken.None);
        var uri = grant.ForSdk();
        var token = uri.Query[(MediaGrantAuthentication.QueryName.Length + 2)..];
        var bytes = WebEncoders.Base64UrlDecode(token);
        Assert.Equal(32, bytes.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), store.Entry!.Digest);
        Assert.Equal(clock.GetUtcNow().AddSeconds(90), grant.ExpiresAt);
        Assert.Equal("https://runtime.example.invalid", store.Entry.Origin);
        Assert.Equal("/azure/media/" + context.CallId, store.Entry.Path);
        Assert.Equal("wss", uri.Scheme);
        Assert.True(!grant.ToString().Contains(token, StringComparison.Ordinal));
        Assert.True(!store.Entry.ToString().Contains(token, StringComparison.Ordinal));
        CryptographicOperations.ZeroMemory(bytes);
    }

    [Fact]
    public async Task GrantExpiryIsCappedByCallDeadlineAndCannotBeRenewed()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryGrantStore(clock);
        var auth = new MediaGrantAuthentication(Fixture.Options(clock), store, clock);
        var context = Fixture.Context(clock) with { Deadline = clock.GetUtcNow().AddSeconds(12) };
        var grant = await auth.IssueAsync(context, Fixture.GrantScope, CancellationToken.None);
        Assert.Equal(context.Deadline, grant.ExpiresAt);
        await auth.ConsumeAsync(Request(grant), context, Fixture.GrantScope, CancellationToken.None);
        Assert.Equal("MEDIA_GRANT_ISSUE_REJECTED", (await Assert.ThrowsAsync<ProviderException>(() =>
            auth.IssueAsync(context, Fixture.GrantScope, CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("reused")]
    [InlineData("call")]
    [InlineData("session")]
    [InlineData("tenant")]
    [InlineData("principal")]
    [InlineData("worker")]
    [InlineData("fence")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("worker-expired")]
    [InlineData("ending")]
    [InlineData("deadline")]
    [InlineData("origin")]
    [InlineData("path")]
    [InlineData("browser-origin")]
    [InlineData("duplicate-query")]
    [InlineData("extra-query")]
    public async Task InvalidOrStaleGrantNeverAuthorizes(string change)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryGrantStore(clock);
        var auth = new MediaGrantAuthentication(Fixture.Options(clock), store, clock);
        var context = Fixture.Context(clock);
        var scope = await auth.CaptureScopeAsync(context, CancellationToken.None);
        var grant = await auth.IssueAsync(context, scope, CancellationToken.None);
        var request = Request(grant);
        switch (change)
        {
            case "unknown": request = Request(Fixture.TransportGrant(clock)); break;
            case "tampered":
                var query = grant.ForSdk().Query;
                var offset = MediaGrantAuthentication.QueryName.Length + 2;
                query = query[..offset] + (query[offset] == 'A' ? 'B' : 'A') + query[(offset + 1)..];
                request.Features.Set(MediaGrantAuthentication.ParseCredential(query));
                break;
            case "missing": request.Features.Set<MediaRequestCredential>(null); break;
            case "expired": clock.Advance(TimeSpan.FromSeconds(91)); break;
            case "reused": await auth.ConsumeAsync(Request(grant), context, scope, CancellationToken.None); break;
            case "call": context = context with { CallId = "another-call" }; break;
            case "session": context = context with { SessionId = "another-session" }; break;
            case "tenant": store.CurrentScope = scope with { TenantId = "another-tenant" }; break;
            case "principal": store.CurrentScope = scope with { PrincipalId = "another-principal" }; break;
            case "worker": store.CurrentScope = scope with { WorkerId = "another-worker" }; break;
            case "fence": store.CurrentScope = scope with { WorkerFence = scope.WorkerFence + 1 }; break;
            case "generation": store.CurrentScope = scope with { OwnerGeneration = scope.OwnerGeneration + 1 }; break;
            case "owner": store.OwnerValid = false; break;
            case "worker-expired": store.WorkerValid = false; break;
            case "ending": store.Active = false; break;
            case "deadline": clock.Advance(TimeSpan.FromMinutes(11)); break;
            case "origin": request.Request.Host = new HostString("other.example.invalid"); break;
            case "path": request.Request.Path = "/azure/media/another-call"; break;
            case "browser-origin": request.Request.Headers.Origin = "https://other.example.invalid"; break;
            case "duplicate-query":
                request.Features.Set(MediaGrantAuthentication.ParseCredential(grant.ForSdk().Query + "&" + grant.ForSdk().Query[1..]));
                break;
            case "extra-query":
                request.Features.Set(MediaGrantAuthentication.ParseCredential(grant.ForSdk().Query + "&extra=value"));
                break;
        }
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            auth.ConsumeAsync(request, context, scope, CancellationToken.None));
        Assert.Contains(error.Code, new[] { "MEDIA_GRANT_REJECTED", "DEADLINE_EXCEEDED" });
        Assert.Equal(error.Code, error.Message);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("worker")]
    [InlineData("fence")]
    [InlineData("generation")]
    [InlineData("ending")]
    public async Task IssuanceRejectsCapturedScopeAfterAuthorityChanges(string change)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryGrantStore(clock);
        var auth = new MediaGrantAuthentication(Fixture.Options(clock), store, clock);
        var scope = await auth.CaptureScopeAsync(Fixture.Context(clock), CancellationToken.None);
        switch (change)
        {
            case "owner": store.OwnerValid = false; break;
            case "worker": store.WorkerValid = false; break;
            case "fence": store.CurrentScope = scope with { WorkerFence = 2 }; break;
            case "generation": store.CurrentScope = scope with { OwnerGeneration = 2 }; break;
            case "ending": store.Active = false; break;
        }
        Assert.Equal("MEDIA_GRANT_ISSUE_REJECTED", (await Assert.ThrowsAsync<ProviderException>(() =>
            auth.IssueAsync(Fixture.Context(clock), scope, CancellationToken.None))).Code);
        Assert.Null(store.Entry);
    }

    [Fact]
    public async Task ConcurrentConsumersAcrossAdapterInstancesHaveExactlyOneWinner()
    {
        var clock = TimeProvider.System;
        var store = new MemoryGrantStore(clock);
        var first = new MediaGrantAuthentication(Fixture.Options(), store, clock);
        var second = new MediaGrantAuthentication(Fixture.Options(), store, clock);
        var context = Fixture.Context();
        var grant = await first.IssueAsync(context, Fixture.GrantScope, CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 32).Select(async i =>
        {
            await start.Task;
            try
            {
                await (i % 2 == 0 ? first : second).ConsumeAsync(Request(grant), context, Fixture.GrantScope, CancellationToken.None);
                return true;
            }
            catch (ProviderException ex)
            {
                Assert.Equal("MEDIA_GRANT_REJECTED", ex.Code);
                return false;
            }
        }).ToArray();
        start.SetResult();
        Assert.Equal(1, (await Task.WhenAll(attempts)).Count(won => won));
        Assert.True(store.Entry!.Consumed);
    }

    [Fact]
    public async Task UrlLoggingAttestationIsASeparateGateNotAnAuthenticationBypass()
    {
        var options = Fixture.Options();
        var store = new MemoryGrantStore(TimeProvider.System);
        var auth = new MediaGrantAuthentication(options, store, TimeProvider.System);
        options.Media.UrlLoggingVerified = false;
        Assert.Equal("MEDIA_URL_LOGGING_UNVERIFIED", (await Assert.ThrowsAsync<ProviderException>(() =>
            auth.IssueAsync(Fixture.Context(), Fixture.GrantScope, CancellationToken.None))).Code);
        options.Media.UrlLoggingVerified = true;
        var grant = await auth.IssueAsync(Fixture.Context(), Fixture.GrantScope, CancellationToken.None);
        await Assert.ThrowsAsync<ProviderException>(() =>
            auth.ConsumeAsync(Request(Fixture.TransportGrant()), Fixture.Context(), Fixture.GrantScope, CancellationToken.None));
        options.Media.UrlLoggingValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal("MEDIA_URL_LOGGING_UNVERIFIED", (await Assert.ThrowsAsync<ProviderException>(() =>
            auth.ConsumeAsync(Request(grant), Fixture.Context(), Fixture.GrantScope, CancellationToken.None))).Code);
        Assert.False(store.Entry!.Consumed);
    }

    [Fact]
    public async Task EmptyPrewarmDoesNotAuthorizeWebSocketOrConversationWithoutConsumedGrant()
    {
        var registry = new AzureCallRegistry();
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        Uri? url = null;
        telephony.GrantObserver = grant => url = grant.ForSdk();
        await using var connection = Fixture.Connection(voice, telephony, new RecordingSink(), registry: registry);
        registry.Add(Fixture.CallId, connection);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "correlation-test");
        Assert.Equal(1, voice.ConnectCount);
        Fixture.AssertEmptyPrewarm(voice);

        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var rejected = Request(Fixture.TransportGrant());
        rejected.RequestServices = services;
        rejected.Response.Body = new MemoryStream();
        var rejectedSocket = new SocketFeature();
        rejected.Features.Set<IHttpWebSocketFeature>(rejectedSocket);
        await AzureRegistration.MediaAsync(rejected, Fixture.CallId, registry);
        Assert.Equal(1, voice.ConnectCount);
        Fixture.AssertEmptyPrewarm(voice);
        Assert.Equal(0, rejectedSocket.AcceptCount);

        var accepted = Request(url!);
        accepted.RequestServices = services;
        using var socket = new MemoryWebSocket();
        socket.Feed(AudioTests.Metadata);
        socket.RemoteClose();
        var acceptedSocket = new SocketFeature(socket, () => Assert.Equal(1, voice.ConnectCount));
        accepted.Features.Set<IHttpWebSocketFeature>(acceptedSocket);
        await AzureRegistration.MediaAsync(accepted, Fixture.CallId, registry);
        Assert.Equal(1, voice.ConnectCount);
        Assert.Equal(1, acceptedSocket.AcceptCount);
        Assert.True(telephony.HangupCount > 0);
    }

    [Fact]
    public async Task ForgedInternalMediaIdentityCannotSkipGrantConsumption()
    {
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        await using var connection = Fixture.Connection(voice, telephony, new RecordingSink());
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "correlation-test");
        var identity = new AuthenticatedMedia(Fixture.CallId, Fixture.ConnectionId, "correlation-test");
        await Assert.ThrowsAsync<ProviderException>(() => connection.PrepareMediaAsync(identity, CancellationToken.None));
        using var socket = new MemoryWebSocket();
        await Assert.ThrowsAsync<ProviderException>(() => connection.RunMediaAsync(socket, identity, CancellationToken.None));
        Assert.Equal(1, voice.ConnectCount);
        Fixture.AssertEmptyPrewarm(voice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorityAndLoggingEvidenceAreRecheckedAroundEmptyPrewarm(bool duringColdStart, bool loggingExpires)
    {
        var options = Fixture.Options();
        var store = new MemoryGrantStore(TimeProvider.System);
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        var sink = new RecordingSink();
        await using var connection = new AzureCallConnection(Fixture.Context(), options, telephony, voice, sink,
            new TestCorrelation().TryBindAsync, new AzureCallRegistry(), TimeProvider.System,
            new MediaGrantAuthentication(options, store, TimeProvider.System), Fixture.GrantScope);
        void Revoke()
        {
            if (loggingExpires) options.Media.UrlLoggingValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            else store.OwnerValid = false;
        }
        if (duringColdStart) voice.OnConnect = Revoke;
        else Revoke();
        var error = await Assert.ThrowsAsync<ProviderException>(() => connection.InitializeAsync(CancellationToken.None));
        Assert.Equal("VOICE_INITIALIZATION_FAILED", error.Code);
        Assert.Equal(duringColdStart ? 1 : 0, voice.ConnectCount);
        Assert.Equal(0, telephony.DialCount);
        Assert.Equal(0, telephony.HangupCount);
        Assert.Null(store.Entry);
        Assert.DoesNotContain(voice.Sent, x => x["type"]!.GetValue<string>() != "session.update");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationAfterGrantConsumptionPreventsConversationActivation(bool loggingExpires)
    {
        var options = Fixture.Options();
        var store = new MemoryGrantStore(TimeProvider.System);
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        await using var connection = new AzureCallConnection(Fixture.Context(), options, telephony, voice, new RecordingSink(),
            new TestCorrelation().TryBindAsync, new AzureCallRegistry(), TimeProvider.System,
            new MediaGrantAuthentication(options, store, TimeProvider.System), Fixture.GrantScope);
        await connection.InitializeAsync(CancellationToken.None);
        connection.Connected(await connection.DialAsync(CancellationToken.None), "correlation-test");
        var identity = await connection.AuthenticateMediaAsync(Fixture.MediaRequest(telephony.Credential), CancellationToken.None);
        if (loggingExpires) options.Media.UrlLoggingValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        else store.OwnerValid = false;
        await Assert.ThrowsAsync<ProviderException>(() => connection.PrepareMediaAsync(identity, CancellationToken.None));
        Fixture.AssertEmptyPrewarm(voice);
        Assert.True(store.Entry!.Consumed);
        Assert.True(telephony.HangupCount > 0);
    }

    [Fact]
    public async Task VoiceConfigurationFailurePreventsDialAndGrantIssuance()
    {
        var voice = new RecordingVoice { AutoConfigure = false };
        var telephony = new FakeTelephony();
        await using var connection = Fixture.Connection(voice, telephony, new RecordingSink());
        var preparing = connection.InitializeAsync(CancellationToken.None);
        await Fixture.WaitUntilAsync(() => !voice.Sent.IsEmpty);
        voice.Receive(new JsonObject
        {
            ["type"] = "session.updated",
            ["session"] = new JsonObject { ["model"] = "wrong-model" }
        });
        Assert.Equal("VOICE_INITIALIZATION_FAILED", (await Assert.ThrowsAsync<ProviderException>(() => preparing)).Code);
        await Assert.ThrowsAsync<ProviderException>(() => connection.DialAsync(CancellationToken.None));
        Assert.Null(telephony.Credential);
        Assert.Equal(0, telephony.DialCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(121)]
    public void SetupTtlCannotBeExtendedOutsideItsHardBound(int seconds)
    {
        var options = Fixture.Options();
        options.Media.SetupGrantTtlSeconds = seconds;
        Assert.Equal("MEDIA_GRANT_TTL_INVALID", Assert.Throws<ProviderException>(() => AzureValidation.Configuration(options)).Code);
    }

    [Fact]
    public async Task ExpiredPredialSetupGrantTerminatesEvenWhenTheCallNeverConnects()
    {
        var options = Fixture.Options();
        options.Media.SetupGrantTtlSeconds = 5;
        var store = new MemoryGrantStore(TimeProvider.System);
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        var sink = new RecordingSink();
        await using var connection = new AzureCallConnection(Fixture.Context(), options, telephony, voice, sink,
            new TestCorrelation().TryBindAsync, new AzureCallRegistry(), TimeProvider.System,
            new MediaGrantAuthentication(options, store, TimeProvider.System), Fixture.GrantScope);
        await connection.InitializeAsync(CancellationToken.None);
        await connection.DialAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Contains(sink.Events, e => e.Signal.Payload["code"]?.GetValue<string>() == "MEDIA_GRANT_EXPIRED");
        Assert.True(telephony.HangupCount > 0);
        Assert.Equal(1, voice.ConnectCount);
        Fixture.AssertEmptyPrewarm(voice);
        await Assert.ThrowsAsync<ProviderException>(() =>
            connection.AuthenticateMediaAsync(Fixture.MediaRequest(telephony.Credential), CancellationToken.None));
    }

    private static DefaultHttpContext Request(MediaTransportGrant grant) => Request(grant.ForSdk());
    internal static DefaultHttpContext Request(Uri uri)
    {
        var http = Fixture.MediaRequest(null);
        http.Request.QueryString = new QueryString(uri.Query);
        http.Features.Get<IHttpRequestFeature>()!.RawTarget = uri.PathAndQuery;
        AzureMediaPrivacyFilter.Sanitize(http);
        return http;
    }

    private sealed class SocketFeature(WebSocket? socket = null, Action? accepting = null) : IHttpWebSocketFeature
    {
        internal int AcceptCount;
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            AcceptCount++;
            accepting?.Invoke();
            return Task.FromResult(socket ?? throw new InvalidOperationException("Unexpected WebSocket acceptance"));
        }
    }
}
