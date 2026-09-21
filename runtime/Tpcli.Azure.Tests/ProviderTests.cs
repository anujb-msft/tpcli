using System.Text.Json.Nodes;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tpcli.Contracts;

namespace Tpcli.Azure.Tests;

public sealed class ProviderTests
{
    [Fact]
    public void TpeCreateOptionsUseTeamsAppIdentifierAndNeverAnAcsCallerNumber()
    {
        var options = Fixture.Options().CallAutomation;
        var request = CallAutomationTransport.BuildCreateOptions(Fixture.Context(), options, Fixture.TransportGrant());
        Assert.IsType<MicrosoftTeamsAppIdentifier>(request.TeamsAppSource);
        Assert.Equal(Fixture.ResourceAccount, request.TeamsAppSource.AppId);
        Assert.Null(request.CallInvite.SourceCallerIdNumber);
        Assert.Equal(Fixture.CallId, request.OperationContext);
        Assert.Equal(Fixture.Recipient, Assert.IsType<PhoneNumberIdentifier>(request.CallInvite.Target).PhoneNumber);
        Assert.Equal(AudioFormat.Pcm24KMono, request.MediaStreamingOptions.AudioFormat);
        Assert.Equal(MediaStreamingAudioChannel.Unmixed, request.MediaStreamingOptions.MediaStreamingAudioChannel);
        Assert.True(request.MediaStreamingOptions.EnableBidirectional);
        Assert.True(request.MediaStreamingOptions.StartMediaStreaming);
        Assert.True(MediaGrantAuthentication.ParseCredential(request.MediaStreamingOptions.TransportUri.Query).Digest is not null);
        Assert.Equal("wss", request.MediaStreamingOptions.TransportUri.Scheme);
    }

    [Fact]
    public void MissingTeamsNumberHasNoFallbackAndDirectTeamsIsUnsupported()
    {
        var options = Fixture.Options().CallAutomation;
        options.TeamsServiceNumber = "";
        Assert.Equal("TPE_SOURCE_NOT_CONFIGURED", Assert.Throws<ProviderException>(() =>
            CallAutomationTransport.BuildCreateOptions(Fixture.Context(), options, Fixture.TransportGrant())).Code);
        Assert.Equal("CAPABILITY_UNSUPPORTED", Assert.Throws<ProviderException>(() =>
            CallAutomationTransport.BuildCreateOptions(Fixture.Context() with { Target = "teams:" + Fixture.ResourceAccount }, options, Fixture.TransportGrant())).Code);
    }

    [Fact]
    public async Task ProductionGateRejectsBeforeVoiceOrDialAndOfflineDoctorIsOffline()
    {
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        using var jwt = new JwtFixture();
        var options = Fixture.Options();
        var factory = new AzureCallProviderFactory(options, new AzureCredentials(options), telephony, voice,
            new MediaGrantAuthentication(options, new UnavailableAzureMediaGrantStore(), TimeProvider.System),
            jwt.Authentication, new RecordingSink(), new TestCorrelation(),
            new AzureCallRegistry(), TimeProvider.System);
        var readiness = await factory.CheckReadinessAsync(false, CancellationToken.None);
        Assert.False(readiness.Pstn);
        Assert.False(readiness.Teams);
        Assert.Contains(readiness.Checks, x => x.Code == "MEDIA_GRANT_STORE_UNCONFIGURED" && x.Status == "blocked");
        Assert.Contains(readiness.Checks, x => x.Code == "OFFLINE_ONLY");
        Assert.Equal("MEDIA_GRANT_STORE_UNCONFIGURED", (await Assert.ThrowsAsync<ProviderException>(() =>
            factory.PrepareAsync(Fixture.Context(), CancellationToken.None))).Code);
        Assert.Equal("CAPABILITY_UNSUPPORTED", (await Assert.ThrowsAsync<ProviderException>(() =>
            factory.PrepareAsync(Fixture.Context() with { Target = "teams:" + Fixture.ResourceAccount }, CancellationToken.None))).Code);
        Assert.Equal(0, voice.ConnectCount);
        Assert.Equal(0, telephony.DialCount);
    }

    [Fact]
    public async Task OfficialRegistrationResolvesBothContractsWithoutAcquiringCredentials()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProviderEventSink, RecordingSink>();
        services.AddTpcliAzure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICallProviderFactory>();
        Assert.Equal("azure", factory.Mode);
        Assert.NotNull(provider.GetRequiredService<ICallTerminator>());
        var readiness = await factory.CheckReadinessAsync(false, CancellationToken.None);
        Assert.Contains(readiness.Checks, x => x.Code == "CALLBACK_CORRELATION_UNCONFIGURED");
    }

    [Fact]
    public async Task VoicePrewarmsEmptyBeforeDialButConversationRequiresConsumedMediaGrant()
    {
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        var context = Fixture.Context();
        var observedGrants = 0;
        telephony.GrantObserver = grant =>
        {
            observedGrants++;
            Assert.Equal(0, telephony.DialCount);
            Assert.Equal(1, voice.ConnectCount);
            Fixture.AssertEmptyPrewarm(voice);
            Assert.True(grant.ExpiresAt <= context.Deadline);
            Assert.True(grant.ExpiresAt > DateTimeOffset.UtcNow);
        };
        var options = Fixture.Options();
        using var jwt = new JwtFixture();
        var factory = new AzureCallProviderFactory(options, new AzureCredentials(options), telephony, voice,
            new MediaGrantAuthentication(options, new MemoryGrantStore(TimeProvider.System), TimeProvider.System),
            jwt.Authentication, new RecordingSink(), new TestCorrelation(),
            new AzureCallRegistry(), TimeProvider.System);
        await using var connection = await factory.PrepareAsync(context, CancellationToken.None);
        Assert.Equal(0, telephony.DialCount);
        Assert.Equal(0, observedGrants);
        Assert.Equal(1, voice.ConnectCount);
        Fixture.AssertEmptyPrewarm(voice);
        Assert.Equal("VOICE_NOT_READY", (await Assert.ThrowsAsync<ProviderException>(() =>
            connection.InstructAsync("Authorized update", CancellationToken.None))).Code);
        Assert.Equal("VOICE_NOT_READY", (await Assert.ThrowsAsync<ProviderException>(() =>
            connection.CompleteToolAsync("unexpected-tool", new JsonObject(), CancellationToken.None))).Code);
        var handle = await connection.DialAsync(CancellationToken.None);
        Assert.Equal(1, observedGrants);
        Fixture.AssertEmptyPrewarm(voice);
        var azure = Assert.IsType<AzureCallConnection>(connection);
        azure.Connected(handle, "correlation-test");
        await Fixture.AuthorizeMediaAsync(azure, telephony);
        Assert.Equal(1, voice.ConnectCount);
        Assert.Equal("session.update", voice.Sent.First()["type"]!.GetValue<string>());
        Assert.Single(voice.Sent, x => x["type"]!.GetValue<string>() == "conversation.item.create");
        Assert.True(voice.Sent.Last()["session"]!["turn_detection"]!["create_response"]!.GetValue<bool>());
        Assert.DoesNotContain(voice.Sent, x => x["type"]!.GetValue<string>() == "response.create");
        await Assert.ThrowsAsync<ProviderException>(() => connection.DialAsync(CancellationToken.None));
        Assert.Equal(1, telephony.DialCount);
        Assert.Equal(1, observedGrants);
    }

    [Fact]
    public async Task EarlyMediaUpgradeConsumesGrantButWaitsForTrustedCallbackWithoutBindingHeaders()
    {
        var options = Fixture.Options();
        var store = new MemoryGrantStore(TimeProvider.System);
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        var correlation = new TestCorrelation();
        await using var connection = new AzureCallConnection(Fixture.Context(), options, telephony, voice,
            new RecordingSink(), correlation.TryBindAsync, new AzureCallRegistry(), TimeProvider.System,
            new MediaGrantAuthentication(options, store, TimeProvider.System), Fixture.GrantScope);
        Task<AuthenticatedMedia>? authenticating = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        telephony.GrantObserver = _ =>
        {
            authenticating = connection.AuthenticateMediaAsync(Fixture.MediaRequest(telephony.Credential), timeout.Token);
            Assert.True(store.Entry!.Consumed);
            Assert.False(authenticating.IsCompleted);
            Assert.Null(correlation.BoundConnection);
            Fixture.AssertEmptyPrewarm(voice);
        };

        await connection.InitializeAsync(timeout.Token);
        var handle = await connection.DialAsync(timeout.Token);
        Assert.NotNull(authenticating);
        Assert.False(authenticating.IsCompleted);
        connection.Connected(handle, "correlation-test");
        var identity = await authenticating.WaitAsync(timeout.Token);
        Assert.Equal(handle.ConnectionId, identity.ConnectionId);
        Assert.Equal("correlation-test", identity.CorrelationId);
        Fixture.AssertEmptyPrewarm(voice);
    }

    [Fact]
    public async Task DeadlineBlocksAllNonTerminationSideEffects()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var telephony = new FakeTelephony();
        var registry = new AzureCallRegistry();
        var correlation = new TestCorrelation();
        await using var connection = Fixture.Connection(new RecordingVoice(), telephony, new RecordingSink(), correlation, registry, clock);
        clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<ProviderException>(() => connection.DialAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ProviderException>(() => connection.InstructAsync("More detail", CancellationToken.None));
        await Assert.ThrowsAsync<ProviderException>(() => connection.SendDtmfAsync("1", CancellationToken.None));
        await Assert.ThrowsAsync<ProviderException>(() => connection.CompleteToolAsync("tool", new JsonObject(), CancellationToken.None));
        Assert.Equal(0, telephony.DialCount);
    }

    [Fact]
    public async Task IndependentTerminatorRequiresOnlyOpaqueHandle()
    {
        var telephony = new FakeTelephony();
        ICallTerminator terminator = new AzureCallTerminator(telephony);
        Assert.Equal(TerminationEvidence.Pending, await terminator.TerminateAsync(Fixture.ConnectionId, CancellationToken.None));
        Assert.Equal(1, telephony.HangupCount);
    }

    [Fact]
    public void IndependentTerminatorRegistrationDoesNotRequireAWorkerOrEventSink()
    {
        var services = new ServiceCollection();
        services.AddTpcliAzure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ICallTerminator>());
    }

    [Fact]
    public async Task MediaDisconnectIsFatalAndInitiatesWholeCallTermination()
    {
        var telephony = new FakeTelephony();
        var voice = new RecordingVoice();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        await using var connection = Fixture.Connection(voice, telephony, sink, correlation);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "correlation-test");
        var identity = await Fixture.AuthorizeMediaAsync(connection, telephony);
        using var socket = new MemoryWebSocket();
        socket.Feed(AudioTests.Metadata);
        var media = connection.RunMediaAsync(socket,
            identity, CancellationToken.None);
        await Fixture.WaitUntilAsync(() => voice.Sent.Any(x => x["type"]!.GetValue<string>() == "response.create"));
        socket.RemoteClose();
        await media.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(telephony.HangupCount > 0);
        Assert.Contains(sink.Events, x => x.Signal.Type == "failure" && x.Signal.Payload["code"]!.GetValue<string>() == "MEDIA_DISCONNECTED");
    }

    [Fact]
    public async Task MediaCorrelationMustMatchTheAuthenticatedConnectedCallback()
    {
        var telephony = new FakeTelephony();
        var voice = new RecordingVoice();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        await using var connection = Fixture.Connection(voice, telephony, sink, correlation);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "expected-correlation");
        await Assert.ThrowsAsync<ProviderException>(() =>
            connection.AuthenticateMediaAsync(Fixture.MediaRequest(telephony.Credential, "wrong-correlation"), CancellationToken.None));
        Assert.Equal(1, voice.ConnectCount);
        Fixture.AssertEmptyPrewarm(voice);
        Assert.Contains(sink.Events, x => x.Signal.Payload["code"]?.GetValue<string>() == "MEDIA_AUTHENTICATED_SETUP_FAILED");
        Assert.True(telephony.HangupCount > 0);
    }

    [Fact]
    public async Task AConnectionArrivingAfterAnEarlierFailureStillGetsTerminated()
    {
        var telephony = new FakeTelephony();
        var voice = new RecordingVoice();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        await using var connection = Fixture.Connection(voice, telephony, sink, correlation);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "correlation-test");
        await Fixture.AuthorizeMediaAsync(connection, telephony);
        voice.Receive(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["code"] = "synthetic-failure" } });
        await Fixture.WaitUntilAsync(() => sink.Events.Any(x => x.Signal.Type == "failure"));
        var previousHangups = telephony.HangupCount;
        connection.Connected(new ProviderHandle(Fixture.ConnectionId), "correlation-test");
        await Fixture.WaitUntilAsync(() => telephony.HangupCount > previousHangups);
    }

    [Fact]
    public async Task AudioOverflowIsFatalAndNeverBecomesUnboundedBuffering()
    {
        var telephony = new FakeTelephony();
        var voice = new RecordingVoice();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        await using var connection = Fixture.Connection(voice, telephony, sink, correlation);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "correlation-test");
        await Fixture.AuthorizeMediaAsync(connection, telephony);
        voice.Receive(Fixture.Event("response.created", ("response", new JsonObject { ["id"] = "response-1" })));
        voice.Receive(Fixture.Audio(new byte[96_000]));
        voice.Receive(Fixture.Audio(new byte[960]));
        await Fixture.WaitUntilAsync(() => sink.Events.Any(x =>
            x.Signal.Type == "failure" && x.Signal.Payload["code"]!.GetValue<string>() == "MEDIA_BACKLOG_EXCEEDED"));
        Assert.True(telephony.HangupCount > 0);
    }

    [Fact]
    public async Task RejectedDuplicatePrepareCannotRemoveTheOriginalWorkerBinding()
    {
        var registry = new AzureCallRegistry();
        var correlation = new TestCorrelation();
        await using var first = Fixture.Connection(new RecordingVoice(), new FakeTelephony(), new RecordingSink(), correlation, registry);
        registry.Add(Fixture.CallId, first);
        var second = Fixture.Connection(new RecordingVoice(), new FakeTelephony(), new RecordingSink(), correlation, registry);
        Assert.Throws<ProviderException>(() => registry.Add(Fixture.CallId, second));
        await second.DisposeAsync();
        Assert.Same(first, registry.Find(Fixture.CallId));
    }

    [Theory]
    [InlineData("https://example.openai.azure.com/")]
    [InlineData("wss://example.services.ai.azure.com/")]
    [InlineData("https://example.services.ai.azure.com/?api-key=not-a-secret")]
    public void VoiceEndpointCannotSubstituteOpenAiOrPutCredentialsInQuery(string endpoint)
    {
        var options = Fixture.Options();
        options.VoiceLive.Endpoint = endpoint;
        Assert.Equal("AZURE_ENDPOINT_INVALID", Assert.Throws<ProviderException>(() => AzureValidation.Configuration(options)).Code);
    }
}
