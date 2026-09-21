global using Xunit;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Tpcli.Contracts;
using CallContext = Tpcli.Contracts.CallContext;

namespace Tpcli.Azure.Tests;

internal static class Fixture
{
    internal const string Audience = "11111111-1111-4111-8111-111111111111";
    internal const string ResourceAccount = "22222222-2222-4222-8222-222222222222";
    internal const string Recipient = "+12025550123";
    internal const string CallId = "call-test";
    internal const string ConnectionId = "opaque/connection:+id?not-a-guid";

    internal static CallContext Context(TimeProvider? clock = null) =>
        new(CallId, "session-test", "pstn:" + Recipient, "Ask about opening hours.", (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(10));

    internal static AzureOptions Options(TimeProvider? clock = null) => new()
    {
        CallAutomation = new()
        {
            Endpoint = "https://example.communication.azure.com/",
            ResourceId = Audience,
            ResourceAccountObjectId = ResourceAccount,
            TeamsServiceNumber = "+12025550124",
            PublicBaseUrl = "https://runtime.example.invalid/"
        },
        VoiceLive = new()
        {
            Endpoint = "https://example.services.ai.azure.com/",
            ApiVersion = "2026-07-15",
            Model = "gpt-realtime",
            Voice = "en-US-AvaNeural",
            Locale = "en-US",
            TranscriptionModel = "gpt-4o-transcribe"
        },
        Evidence = new()
        {
            ResourceAccountBound = true,
            ServiceNumberAssigned = true,
            ResourceAccountLicensed = true,
            ServerCallingAuthorized = true,
            OutboundPstnFunded = true,
            ValidUntilUtc = (clock ?? TimeProvider.System).GetUtcNow().AddHours(1)
        }
    };

    internal static JsonObject Event(string type, params (string Name, JsonNode? Value)[] values)
    {
        var message = new JsonObject { ["type"] = type };
        foreach (var (name, value) in values) message[name] = value;
        return message;
    }

    internal static JsonObject Audio(byte[] bytes, string response = "response-1", string item = "item-1") =>
        Event("response.audio.delta", ("response_id", JsonValue.Create(response)), ("item_id", JsonValue.Create(item)),
            ("content_index", JsonValue.Create(0)), ("delta", JsonValue.Create(Convert.ToBase64String(bytes))));

    internal static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class RecordingSink : IProviderEventSink
{
    internal readonly ConcurrentQueue<(string CallId, ProviderSignal Signal)> Events = new();
    public Task PublishAsync(string callId, ProviderSignal signal, CancellationToken cancellationToken = default)
    {
        Events.Enqueue((callId, signal));
        return Task.CompletedTask;
    }
}

internal sealed class TestCorrelation : IAzureCallCorrelation
{
    private readonly object _gate = new();
    internal string? BoundConnection;
    internal bool HasDispatch = true;
    public Task<bool> TryBindAsync(string callId, string connectionId, string? serverCallId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!HasDispatch || callId != Fixture.CallId || (BoundConnection is { } expected && expected != connectionId))
                return Task.FromResult(false);
            BoundConnection = connectionId;
            return Task.FromResult(true);
        }
    }
}

internal sealed class FakeTelephony : ICallAutomationTransport
{
    internal int DialCount;
    internal int HangupCount;
    internal readonly ConcurrentQueue<string> Dtmf = new();
    public Task<ProviderHandle> DialAsync(CallContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref DialCount);
        return Task.FromResult(new ProviderHandle(Fixture.ConnectionId, "opaque-server"));
    }

    public Task SendDtmfAsync(string connectionId, string recipient, string callId, string digits, CancellationToken cancellationToken)
    {
        Dtmf.Enqueue(digits);
        return Task.CompletedTask;
    }

    public Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref HangupCount);
        return Task.FromResult(TerminationEvidence.Pending);
    }
}

internal sealed class RecordingVoice : IVoiceTransport, IVoiceTransportFactory
{
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    internal readonly ConcurrentQueue<JsonObject> Sent = new();
    internal bool AutoConfigure = true;
    internal int ConnectCount;
    public Task<IVoiceTransport> ConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ConnectCount);
        return Task.FromResult<IVoiceTransport>(this);
    }

    internal void Receive(JsonObject message) => _incoming.Writer.TryWrite(message);

    public Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Enqueue((JsonObject)message.DeepClone());
        if (AutoConfigure && message["type"]?.GetValue<string>() == "session.update" &&
            message["session"]?["input_audio_format"] is not null)
            Receive(new JsonObject { ["type"] = "session.updated", ["session"] = message["session"]!.DeepClone() });
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonObject> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken)) yield return message;
    }

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestMediaAuthentication : IMediaAuthentication
{
    public bool IsSupported => true;
    public Task<AuthenticatedMedia> AuthenticateAsync(HttpContext context, string callId, CancellationToken cancellationToken) =>
        Task.FromResult(new AuthenticatedMedia(callId, Fixture.ConnectionId, "correlation-test"));
}

internal sealed class JwtFixture : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    internal CallbackAuthentication Authentication { get; }

    internal JwtFixture()
    {
        var metadata = new OpenIdConnectConfiguration { Issuer = CallbackAuthentication.Issuer };
        metadata.SigningKeys.Add(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "local-fixture" });
        Authentication = new CallbackAuthentication(Fixture.Audience, new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
    }

    internal string Token(string? issuer = null, string? audience = null, bool expired = false)
    {
        var now = DateTime.UtcNow;
        return "Bearer " + new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? CallbackAuthentication.Issuer,
            Audience = audience ?? Fixture.Audience,
            IssuedAt = now.AddMinutes(expired ? -10 : -1),
            NotBefore = now.AddMinutes(expired ? -10 : -1),
            Expires = now.AddMinutes(expired ? -5 : 5),
            Claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "local-fixture" }, SecurityAlgorithms.RsaSha256)
        });
    }

    public void Dispose() => _rsa.Dispose();
}

internal sealed class MemoryWebSocket : WebSocket
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private WebSocketState _state = WebSocketState.Open;
    private byte[]? _current;
    private int _offset;
    internal readonly ConcurrentQueue<JsonObject> Sent = new();
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;
    internal void Feed(string json) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
    internal void RemoteClose() => _incoming.Writer.TryComplete();
    public override void Abort() { _state = WebSocketState.Aborted; _incoming.Writer.TryComplete(); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);
    public override void Dispose() => Abort();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            _current = await _incoming.Reader.ReadAsync(cancellationToken);
            _offset = 0;
        }
        var size = Math.Min(buffer.Count, _current.Length - _offset);
        _current.AsSpan(_offset, size).CopyTo(buffer.AsSpan());
        _offset += size;
        var end = _offset == _current.Length;
        if (end) _current = null;
        return new WebSocketReceiveResult(size, WebSocketMessageType.Text, end);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Enqueue(JsonNode.Parse(buffer.AsSpan())!.AsObject());
        return Task.CompletedTask;
    }
}
