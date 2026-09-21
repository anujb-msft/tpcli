using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Azure.Tests;

public sealed class CallbackTests
{
    [Theory]
    [InlineData("https://attacker.example.invalid", null, false)]
    [InlineData(null, "33333333-3333-4333-8333-333333333333", false)]
    [InlineData(null, null, true)]
    public async Task LocallySignedTokensRequireIssuerAudienceAndLifetime(string? issuer, string? audience, bool expired)
    {
        using var jwt = new JwtFixture();
        var error = await Assert.ThrowsAsync<ProviderException>(() => jwt.Authentication.AuthenticateAsync(jwt.Token(issuer, audience, expired), CancellationToken.None));
        Assert.Equal("CALLBACK_UNAUTHORIZED", error.Code);
        Assert.Equal(error.Code, error.Message);
    }

    [Fact]
    public async Task AValidLocalTokenPassesButAnotherKeyAndNoTokenFail()
    {
        using var trusted = new JwtFixture();
        using var other = new JwtFixture();
        await trusted.Authentication.AuthenticateAsync(trusted.Token(), CancellationToken.None);
        await Assert.ThrowsAsync<ProviderException>(() => trusted.Authentication.AuthenticateAsync(other.Token(), CancellationToken.None));
        await Assert.ThrowsAsync<ProviderException>(() => trusted.Authentication.AuthenticateAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticatedLateCallbackReachesSinkWithoutAnyWorker()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        var processor = Processor(jwt, sink, correlation);
        await processor.ProcessAsync(Fixture.CallId, jwt.Token(), Body(time: DateTimeOffset.UtcNow.AddMinutes(-30)), CancellationToken.None);
        var recorded = Assert.Single(sink.Events);
        Assert.Equal(Fixture.CallId, recorded.CallId);
        Assert.Equal("connected", recorded.Signal.Type);
        Assert.Equal(Fixture.ConnectionId, recorded.Signal.Payload["connection_id"]!.GetValue<string>());
        Assert.Equal(Fixture.ConnectionId, correlation.BoundConnection);
    }

    [Fact]
    public async Task CallbackCannotReplaceBoundConnectionOrInventCallOrDispatch()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation { BoundConnection = "existing-opaque-id" };
        var processor = Processor(jwt, sink, correlation);
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, jwt.Token(), Body(), CancellationToken.None));
        Assert.Equal("CALLBACK_CORRELATION_REJECTED", error.Code);
        correlation.BoundConnection = null;
        correlation.HasDispatch = false;
        await Assert.ThrowsAsync<ProviderException>(() => processor.ProcessAsync(Fixture.CallId, jwt.Token(), Body(), CancellationToken.None));
        correlation.HasDispatch = true;
        await Assert.ThrowsAsync<ProviderException>(() => processor.ProcessAsync("call-other", jwt.Token(), Body(), CancellationToken.None));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task RetriesAreIdempotentAcrossBearerReuseReplacementAndCanonicalJson()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        var token = jwt.Token();
        var body = Body();
        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        await processor.ProcessAsync(Fixture.CallId, jwt.Token(), body, CancellationToken.None);
        var reordered = ReverseObjectProperties(JsonNode.Parse(body));
        var formatted = Encoding.UTF8.GetBytes(reordered!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotEqual(Encoding.UTF8.GetString(body), Encoding.UTF8.GetString(formatted));
        await processor.ProcessAsync(Fixture.CallId, token, formatted, CancellationToken.None);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task OneServiceBearerAcceptsDistinctConnectedAndLateDisconnectedEventsAndRegroupedRetries()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        var token = jwt.Token();
        var connected = Body(eventId: "connected-event", time: DateTimeOffset.UtcNow.AddMinutes(-31));
        var disconnected = Body(eventId: "disconnected-event", time: DateTimeOffset.UtcNow.AddMinutes(-30),
            eventType: "CallDisconnected");

        await processor.ProcessAsync(Fixture.CallId, token, connected, CancellationToken.None);
        await processor.ProcessAsync(Fixture.CallId, token, disconnected, CancellationToken.None);
        var batch = new JsonArray(JsonNode.Parse(disconnected)![0]!.DeepClone(), JsonNode.Parse(connected)![0]!.DeepClone());
        await processor.ProcessAsync(Fixture.CallId, token, Encoding.UTF8.GetBytes(batch.ToJsonString()), CancellationToken.None);

        Assert.Equal(["connected", "disconnected"], sink.Events.Select(e => e.Signal.Type));
        Assert.Equal(["connected-event", "disconnected-event"], sink.Events.Select(e => e.Signal.ProviderEventId));
        Assert.All(sink.Events, e => Assert.Equal(Fixture.ConnectionId, e.Signal.Payload["connection_id"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("type", false)]
    [InlineData("type", true)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("time", false)]
    [InlineData("time", true)]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("extension", false)]
    [InlineData("extension", true)]
    public async Task ReusingEventIdWithAlteredCanonicalContentFailsEvenWithAnotherBearer(string field, bool freshBearer)
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        var token = jwt.Token();
        var body = Body();
        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        var changed = JsonNode.Parse(body)!;
        var cloudEvent = changed[0]!;
        switch (field)
        {
            case "type": cloudEvent["type"] = "Microsoft.Communication.CallDisconnected"; break;
            case "source": cloudEvent["source"] = "calling/another-source"; break;
            case "time": cloudEvent["time"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"); break;
            case "data": cloudEvent["data"]!["serverCallId"] = "altered-server"; break;
            case "extension": cloudEvent["extension"] = "altered-metadata"; break;
        }
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, freshBearer ? jwt.Token() : token,
                Encoding.UTF8.GetBytes(changed.ToJsonString()), CancellationToken.None));
        Assert.Equal("CALLBACK_REPLAY_REJECTED", error.Code);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task ChangingEventIdCannotBypassExpectedConnectionCorrelation()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        var token = jwt.Token();
        await processor.ProcessAsync(Fixture.CallId, token, Body(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, token,
                Body(eventId: "another-event", eventType: "CallDisconnected", connectionId: "another-connection"),
                CancellationToken.None));
        Assert.Equal("CALLBACK_CORRELATION_REJECTED", error.Code);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task FailedSinkDeliveryRetainsContentBindingAndAllowsOnlyIdenticalRetry()
    {
        using var jwt = new JwtFixture();
        var sink = new RetrySink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        var token = jwt.Token();
        var body = Body();
        await Assert.ThrowsAsync<IOException>(() =>
            processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None));

        var changed = JsonNode.Parse(body)!;
        changed[0]!["data"]!["serverCallId"] = "altered-server";
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, jwt.Token(), Encoding.UTF8.GetBytes(changed.ToJsonString()), CancellationToken.None));
        Assert.Equal("CALLBACK_REPLAY_REJECTED", error.Code);

        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        Assert.Equal(2, sink.Attempts);
        Assert.Single(sink.Delivered.Events);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("callConnectionId")]
    public async Task AmbiguousDuplicateJsonKeysFailBeforeDurableBinding(string property)
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var correlation = new TestCorrelation();
        var processor = Processor(jwt, sink, correlation);
        var body = Encoding.UTF8.GetString(Body())
            .Replace($"\"{property}\":", $"\"{property}\":\"ambiguous\",\"{property}\":", StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, jwt.Token(), Encoding.UTF8.GetBytes(body), CancellationToken.None));
        Assert.Equal("CALLBACK_INVALID", error.Code);
        Assert.Null(correlation.BoundConnection);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ConcurrentDeliveryRequiresRetryWithoutLosingContentBinding()
    {
        var replay = new CallbackReplayGuard(TimeProvider.System);
        Assert.Equal(ReplayDisposition.New, replay.BeginEvent("call:event", [1]));
        Assert.Equal("CALLBACK_RETRY_REQUIRED",
            Assert.Throws<ProviderException>(() => replay.BeginEvent("call:event", [1])).Code);
        Assert.Equal("CALLBACK_REPLAY_REJECTED",
            Assert.Throws<ProviderException>(() => replay.BeginEvent("call:event", [2])).Code);
        replay.Retry("call:event");
        Assert.Equal("CALLBACK_REPLAY_REJECTED",
            Assert.Throws<ProviderException>(() => replay.BeginEvent("call:event", [2])).Code);
        Assert.Equal(ReplayDisposition.New, replay.BeginEvent("call:event", [1]));
        replay.Complete("call:event");
        Assert.Equal(ReplayDisposition.Duplicate, replay.BeginEvent("call:event", [1]));
    }

    [Fact]
    public void ReplayCacheIsBoundedAndExpiresIdleBindingsButNotActiveDeliveries()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var replay = new CallbackReplayGuard(clock, capacity: 1);
        Assert.Equal(ReplayDisposition.New, replay.BeginEvent("call:first", [1]));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("CALLBACK_CAPACITY_EXCEEDED",
            Assert.Throws<ProviderException>(() => replay.BeginEvent("call:second", [2])).Code);
        replay.Retry("call:first");
        Assert.Equal(ReplayDisposition.New, replay.BeginEvent("call:second", [2]));
        replay.Complete("call:second");
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(ReplayDisposition.New, replay.BeginEvent("call:third", [3]));
    }

    [Fact]
    public async Task FutureEventAndWrongOperationContextFailBeforePublishing()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, jwt.Token(), Body(time: DateTimeOffset.UtcNow.AddMinutes(10)), CancellationToken.None));
        await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, jwt.Token(), Body(operationContext: "call-other"), CancellationToken.None));
        Assert.Empty(sink.Events);
    }

    private static AcsCallbackProcessor Processor(JwtFixture jwt, IProviderEventSink sink, TestCorrelation correlation) =>
        new(jwt.Authentication, new CallbackReplayGuard(TimeProvider.System), correlation.TryBindAsync,
            new AzureCallRegistry(), sink, TimeProvider.System);

    private static JsonNode? ReverseObjectProperties(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, ReverseObjectProperties(p.Value)))),
        JsonArray array => new JsonArray(array.Select(ReverseObjectProperties).ToArray()),
        _ => value?.DeepClone()
    };

    private sealed class RetrySink : IProviderEventSink
    {
        internal int Attempts;
        internal readonly RecordingSink Delivered = new();
        public Task PublishAsync(string callId, ProviderSignal signal, CancellationToken cancellationToken = default) =>
            ++Attempts == 1 ? Task.FromException(new IOException("Test delivery failure"))
                : Delivered.PublishAsync(callId, signal, cancellationToken);
    }

    private static byte[] Body(string eventId = "event-1", DateTimeOffset? time = null, string operationContext = Fixture.CallId,
        string eventType = "CallConnected", string connectionId = Fixture.ConnectionId) =>
        Encoding.UTF8.GetBytes(new JsonArray(new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = eventId,
            ["source"] = "calling/callConnections/" + connectionId,
            ["type"] = "Microsoft.Communication." + eventType,
            ["time"] = (time ?? DateTimeOffset.UtcNow).ToString("O"),
            ["data"] = new JsonObject
            {
                ["callConnectionId"] = connectionId,
                ["serverCallId"] = "opaque-server",
                ["correlationId"] = "correlation-test",
                ["operationContext"] = operationContext
            }
        }).ToJsonString());
}
