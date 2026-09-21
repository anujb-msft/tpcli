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
        var request = CallAutomationTransport.BuildCreateOptions(Fixture.Context(), options);
        Assert.IsType<MicrosoftTeamsAppIdentifier>(request.TeamsAppSource);
        Assert.Equal(Fixture.ResourceAccount, request.TeamsAppSource.AppId);
        Assert.Null(request.CallInvite.SourceCallerIdNumber);
        Assert.Equal(Fixture.CallId, request.OperationContext);
        Assert.Equal(Fixture.Recipient, Assert.IsType<PhoneNumberIdentifier>(request.CallInvite.Target).PhoneNumber);
        Assert.Equal(AudioFormat.Pcm24KMono, request.MediaStreamingOptions.AudioFormat);
        Assert.Equal(MediaStreamingAudioChannel.Unmixed, request.MediaStreamingOptions.MediaStreamingAudioChannel);
        Assert.True(request.MediaStreamingOptions.EnableBidirectional);
        Assert.True(request.MediaStreamingOptions.StartMediaStreaming);
        Assert.Equal("", request.MediaStreamingOptions.TransportUri.Query);
        Assert.Equal("wss", request.MediaStreamingOptions.TransportUri.Scheme);
    }

    [Fact]
    public void MissingTeamsNumberHasNoFallbackAndDirectTeamsIsUnsupported()
    {
        var options = Fixture.Options().CallAutomation;
        options.TeamsServiceNumber = "";
        Assert.Equal("TPE_SOURCE_NOT_CONFIGURED", Assert.Throws<ProviderException>(() =>
            CallAutomationTransport.BuildCreateOptions(Fixture.Context(), options)).Code);
        Assert.Equal("CAPABILITY_UNSUPPORTED", Assert.Throws<ProviderException>(() =>
            CallAutomationTransport.BuildCreateOptions(Fixture.Context() with { Target = "teams:" + Fixture.ResourceAccount }, options)).Code);
    }

    [Fact]
    public async Task ProductionGateRejectsBeforeVoiceOrDialAndOfflineDoctorIsOffline()
    {
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        using var jwt = new JwtFixture();
        var options = Fixture.Options();
        var factory = new AzureCallProviderFactory(options, new AzureCredentials(options), telephony, voice,
            new UnverifiedMediaAuthentication(), jwt.Authentication, new RecordingSink(), new TestCorrelation(),
            new AzureCallRegistry(), TimeProvider.System);
        var readiness = await factory.CheckReadinessAsync(false, CancellationToken.None);
        Assert.False(readiness.Pstn);
        Assert.False(readiness.Teams);
        Assert.Contains(readiness.Checks, x => x.Code == "MEDIA_AUTH_UNVERIFIED" && x.Status == "blocked");
        Assert.Contains(readiness.Checks, x => x.Code == "OFFLINE_ONLY");
        Assert.Equal("MEDIA_AUTH_UNVERIFIED", (await Assert.ThrowsAsync<ProviderException>(() =>
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
    public async Task VoiceIsConfiguredBeforeDialAndDialCannotBeRepeated()
    {
        var voice = new RecordingVoice();
        var telephony = new FakeTelephony();
        var context = Fixture.Context();
        var options = Fixture.Options();
        using var jwt = new JwtFixture();
        var factory = new AzureCallProviderFactory(options, new AzureCredentials(options), telephony, voice,
            new TestMediaAuthentication(), jwt.Authentication, new RecordingSink(), new TestCorrelation(),
            new AzureCallRegistry(), TimeProvider.System);
        await using var connection = await factory.PrepareAsync(context, CancellationToken.None);
        Assert.Equal(0, telephony.DialCount);
        Assert.Equal("session.update", voice.Sent.First()["type"]!.GetValue<string>());
        Assert.Equal("conversation.item.create", voice.Sent.Last()["type"]!.GetValue<string>());
        Assert.DoesNotContain(voice.Sent, x => x["type"]!.GetValue<string>() == "response.create");
        await connection.DialAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ProviderException>(() => connection.DialAsync(CancellationToken.None));
        Assert.Equal(1, telephony.DialCount);
    }

    [Fact]
    public async Task DeadlineBlocksAllNonTerminationSideEffects()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var telephony = new FakeTelephony();
        var registry = new AzureCallRegistry();
        var correlation = new TestCorrelation();
        await using var connection = new AzureCallConnection(Fixture.Context(clock), Fixture.Options(clock), telephony,
            new RecordingVoice(), new RecordingSink(), correlation.TryBindAsync, registry, clock);
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
        await using var connection = new AzureCallConnection(Fixture.Context(), Fixture.Options(), telephony,
            voice, sink, correlation.TryBindAsync, new AzureCallRegistry(), TimeProvider.System);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "correlation-test");
        using var socket = new MemoryWebSocket();
        socket.Feed(AudioTests.Metadata);
        var media = connection.RunMediaAsync(socket,
            new AuthenticatedMedia(Fixture.CallId, Fixture.ConnectionId, "correlation-test"), CancellationToken.None);
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
        await using var connection = new AzureCallConnection(Fixture.Context(), Fixture.Options(), telephony,
            voice, sink, correlation.TryBindAsync, new AzureCallRegistry(), TimeProvider.System);
        await connection.InitializeAsync(CancellationToken.None);
        var handle = await connection.DialAsync(CancellationToken.None);
        connection.Connected(handle, "expected-correlation");
        using var socket = new MemoryWebSocket();
        socket.Feed(AudioTests.Metadata);
        await connection.RunMediaAsync(socket,
            new AuthenticatedMedia(Fixture.CallId, Fixture.ConnectionId, "wrong-correlation"), CancellationToken.None);
        Assert.DoesNotContain(voice.Sent, x => x["type"]!.GetValue<string>() == "response.create");
        Assert.Contains(sink.Events, x => x.Signal.Payload["code"]?.GetValue<string>() == "MEDIA_CORRELATION_REJECTED");
        Assert.True(telephony.HangupCount > 0);
    }

    [Fact]
    public async Task AConnectionArrivingAfterAnEarlierFailureStillGetsTerminated()
    {
        var telephony = new FakeTelephony();
        var voice = new RecordingVoice();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        await using var connection = new AzureCallConnection(Fixture.Context(), Fixture.Options(), telephony,
            voice, sink, correlation.TryBindAsync, new AzureCallRegistry(), TimeProvider.System);
        await connection.InitializeAsync(CancellationToken.None);
        voice.Receive(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["code"] = "synthetic-failure" } });
        await Fixture.WaitUntilAsync(() => sink.Events.Any(x => x.Signal.Type == "failure"));
        Assert.Equal(0, telephony.HangupCount);
        connection.Connected(new ProviderHandle(Fixture.ConnectionId), "correlation-test");
        await Fixture.WaitUntilAsync(() => telephony.HangupCount != 0);
    }

    [Fact]
    public async Task AudioOverflowIsFatalAndNeverBecomesUnboundedBuffering()
    {
        var telephony = new FakeTelephony();
        var voice = new RecordingVoice();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        await using var connection = new AzureCallConnection(Fixture.Context(), Fixture.Options(), telephony,
            voice, sink, correlation.TryBindAsync, new AzureCallRegistry(), TimeProvider.System);
        await connection.InitializeAsync(CancellationToken.None);
        await connection.DialAsync(CancellationToken.None);
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
        await using var first = new AzureCallConnection(Fixture.Context(), Fixture.Options(), new FakeTelephony(),
            new RecordingVoice(), new RecordingSink(), correlation.TryBindAsync, registry, TimeProvider.System);
        registry.Add(Fixture.CallId, first);
        var second = new AzureCallConnection(Fixture.Context(), Fixture.Options(), new FakeTelephony(),
            new RecordingVoice(), new RecordingSink(), correlation.TryBindAsync, registry, TimeProvider.System);
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
