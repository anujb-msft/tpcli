using Azure;
using Azure.AI.VoiceLive;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Core;
using Tpcli.Contracts;

namespace Tpcli.Azure.Tests;

public sealed class SdkTransportTests
{
    [Fact]
    public async Task PinnedVoiceSdkRequestsAzureAiScopeBeforeAnyNetworkConnection()
    {
        var credential = new NoNetworkCredential();
        var options = new VoiceLiveClientOptions(VoiceLiveClientOptions.ServiceVersion.V2026_07_15);
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        var sdk = new VoiceLiveClient(new Uri("https://example.services.ai.azure.com/"), credential, options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.StartSessionAsync("gpt-realtime"));
        Assert.Equal(new[] { "https://ai.azure.com/.default" }, credential.Scopes);
    }

    [Fact]
    public async Task RealSdkHangupOverloadAlwaysUsesForEveryoneAndDoesNotClaimConfirmation()
    {
        var sdk = new StubSdk();
        var transport = Transport(sdk);
        Assert.Equal(TerminationEvidence.Pending, await transport.TerminateAsync(Fixture.ConnectionId, CancellationToken.None));
        Assert.True(sdk.Connection.ForEveryone);
        Assert.Equal(Fixture.ConnectionId, sdk.RequestedId);
        sdk.Connection.Failure = new RequestFailedException(404, "synthetic provider detail must not escape");
        Assert.Equal(TerminationEvidence.Unknown, await transport.TerminateAsync(Fixture.ConnectionId, CancellationToken.None));
    }

    [Fact]
    public async Task AmbiguousSdkCreateHasOneAttemptAndOnlyASafeCode()
    {
        var sdk = new StubSdk();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Transport(sdk).DialAsync(Fixture.Context(), Fixture.TransportGrant(), CancellationToken.None));
        Assert.Equal(1, sdk.Creates);
        Assert.True(error.MayHaveDispatched);
        Assert.Equal("PROVIDER_DISPATCH_UNKNOWN", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.NotNull(sdk.CreateOptions!.TeamsAppSource);
        Assert.Null(sdk.CreateOptions.CallInvite.SourceCallerIdNumber);
    }

    [Fact]
    public void PinnedSdkStartStreamingCannotOverrideTheCreateCallTransportUri()
    {
        Assert.Equal(["OperationCallbackUri", "OperationContext"],
            typeof(StartMediaStreamingOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Null(typeof(StartMediaStreamingOptions).GetProperty("TransportUri"));
        Assert.NotNull(typeof(CallMedia).GetMethod(nameof(CallMedia.StartMediaStreamingAsync),
            [typeof(StartMediaStreamingOptions), typeof(CancellationToken)]));
        Assert.Equal(typeof(Uri), typeof(MediaStreamingOptions).GetProperty(nameof(MediaStreamingOptions.TransportUri))!.PropertyType);
    }

    [Fact]
    public async Task AnExpiredGrantIsRejectedBeforeAnySdkCreateAttempt()
    {
        var sdk = new StubSdk();
        var grant = Fixture.TransportGrant(new ManualClock(DateTimeOffset.UtcNow.AddMinutes(-2)));
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            Transport(sdk).DialAsync(Fixture.Context(), grant, CancellationToken.None));
        Assert.Equal("MEDIA_GRANT_EXPIRED", error.Code);
        Assert.False(error.MayHaveDispatched);
        Assert.Equal(0, sdk.Creates);
    }

    [Fact]
    public async Task ActualSdkDtmfUsesOnlyTheSelectedPstnRecipient()
    {
        var sdk = new StubSdk();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Transport(sdk).SendDtmfAsync(
            Fixture.ConnectionId, Fixture.Recipient, Fixture.CallId, "12*#", CancellationToken.None));
        Assert.Equal("DTMF_DELIVERY_UNKNOWN", error.Code);
        Assert.Equal(Fixture.ConnectionId, sdk.RequestedId);
        Assert.Equal(Fixture.Recipient, Assert.IsType<PhoneNumberIdentifier>(sdk.Connection.Media.Options!.TargetParticipant).PhoneNumber);
        Assert.Equal(new[] { DtmfTone.One, DtmfTone.Two, DtmfTone.Asterisk, DtmfTone.Pound }, sdk.Connection.Media.Options.Tones);
    }

    private static CallAutomationTransport Transport(StubSdk sdk)
    {
        var options = Fixture.Options();
        return new CallAutomationTransport(options, new AzureCredentials(options), TimeProvider.System, sdk);
    }

    private sealed class StubSdk : CallAutomationClient
    {
        internal readonly StubConnection Connection = new();
        internal string? RequestedId;
        internal int Creates;
        internal CreateCallOptions? CreateOptions;
        public override CallConnection GetCallConnection(string callConnectionId)
        {
            RequestedId = callConnectionId;
            return Connection;
        }

        public override Task<Response<CreateCallResult>> CreateCallAsync(CreateCallOptions options, CancellationToken cancellationToken = default)
        {
            Creates++;
            CreateOptions = options;
            throw new RequestFailedException(503, "synthetic provider detail must not escape");
        }
    }

    private sealed class StubConnection : CallConnection
    {
        internal readonly StubMedia Media = new();
        internal bool ForEveryone;
        internal Exception? Failure;
        public override CallMedia GetCallMedia() => Media;
        public override Task<Response> HangUpAsync(bool forEveryone, CancellationToken cancellationToken = default)
        {
            ForEveryone = forEveryone;
            if (Failure is not null) throw Failure;
            return Task.FromResult<Response>(new StubResponse());
        }
    }

    private sealed class StubMedia : CallMedia
    {
        internal SendDtmfTonesOptions? Options;
        public override Task<Response<SendDtmfTonesResult>> SendDtmfTonesAsync(SendDtmfTonesOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            throw new RequestFailedException(503, "synthetic provider detail must not escape");
        }
    }

    private sealed class StubResponse : Response
    {
        public override int Status => 204;
        public override string ReasonPhrase => "No Content";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        internal string[]? Scopes;
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Offline test stops before WebSocket connection.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            throw new InvalidOperationException("Offline test stops before WebSocket connection.");
        }
    }
}
