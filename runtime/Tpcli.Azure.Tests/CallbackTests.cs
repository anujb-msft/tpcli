using System.Text;
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
        var token = await trusted.Authentication.AuthenticateAsync(trusted.Token(), CancellationToken.None);
        Assert.Equal(64, token.TokenHash.Length);
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
    public async Task RetriesAreIdempotentAndBearerReplayWithChangedBodyFails()
    {
        using var jwt = new JwtFixture();
        var sink = new RecordingSink();
        var processor = Processor(jwt, sink, new TestCorrelation());
        var token = jwt.Token();
        var body = Body();
        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        await processor.ProcessAsync(Fixture.CallId, token, body, CancellationToken.None);
        await processor.ProcessAsync(Fixture.CallId, jwt.Token(), body, CancellationToken.None);
        Assert.Single(sink.Events);
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            processor.ProcessAsync(Fixture.CallId, token, Body(eventId: "changed"), CancellationToken.None));
        Assert.Equal("CALLBACK_REPLAY_REJECTED", error.Code);
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

    private static AcsCallbackProcessor Processor(JwtFixture jwt, RecordingSink sink, TestCorrelation correlation) =>
        new(jwt.Authentication, new CallbackReplayGuard(TimeProvider.System), correlation.TryBindAsync,
            new AzureCallRegistry(), sink, TimeProvider.System);

    private static byte[] Body(string eventId = "event-1", DateTimeOffset? time = null, string operationContext = Fixture.CallId) =>
        Encoding.UTF8.GetBytes(new JsonArray(new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = eventId,
            ["source"] = "calling/callConnections/" + Fixture.ConnectionId,
            ["type"] = "Microsoft.Communication.CallConnected",
            ["time"] = (time ?? DateTimeOffset.UtcNow).ToString("O"),
            ["data"] = new JsonObject
            {
                ["callConnectionId"] = Fixture.ConnectionId,
                ["serverCallId"] = "opaque-server",
                ["correlationId"] = "correlation-test",
                ["operationContext"] = operationContext
            }
        }).ToJsonString());
}
