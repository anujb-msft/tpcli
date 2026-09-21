using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed class AzureCallRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AzureCallConnection> _connections = new(StringComparer.Ordinal);

    internal void Add(string id, AzureCallConnection connection)
    {
        lock (_gate)
        {
            if (_connections.Count >= 64) throw new ProviderException("PROVIDER_CAPACITY_EXCEEDED");
            if (!_connections.TryAdd(id, connection)) throw new ProviderException("CALL_ALREADY_PREPARED");
        }
    }

    internal AzureCallConnection? Find(string id)
    {
        lock (_gate) return _connections.GetValueOrDefault(id);
    }

    internal void Remove(string id, AzureCallConnection connection)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(id, out var existing) && ReferenceEquals(existing, connection))
                _connections.Remove(id);
        }
    }
}

internal delegate Task<bool> CorrelateCall(string callId, string connectionId, string? serverCallId, CancellationToken cancellationToken);

internal sealed class AzureCallConnection : ICallConnection
{
    private readonly object _gate = new();
    private readonly CallContext _context;
    private readonly AzureOptions _options;
    private readonly ICallAutomationTransport _telephony;
    private readonly IProviderEventSink _sink;
    private readonly CorrelateCall _correlate;
    private readonly AzureCallRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly AudioBuffer _input;
    private readonly AudioBuffer _output;
    private readonly SemaphoreSlim _mediaSend = new(1, 1);
    private readonly Channel<ProviderSignal> _signals = Channel.CreateBounded<ProviderSignal>(
        new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mediaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IVoiceTransportFactory _voiceFactory;
    private readonly MediaGrantAuthentication _mediaAuthentication;
    private readonly AzureMediaGrantScope _mediaScope;
    private VoiceConversation? _voice;
    private readonly ITimer _deadlineTimer;
    private ITimer? _mediaSetupTimer;
    private Task? _signalPump;
    private Task? _inputPump;
    private WebSocket? _socket;
    private ProviderHandle? _handle;
    private string? _correlationId;
    private bool _disconnected;
    private int _dialStarted;
    private int _mediaAccepted;
    private int _mediaAuthorized;
    private int _voiceStarted;
    private int _voicePrepared;
    private int _mediaPrepared;
    private int _failed;
    private int _disposed;

    internal AzureCallConnection(CallContext context, AzureOptions options, ICallAutomationTransport telephony,
        IVoiceTransportFactory voice, IProviderEventSink sink, CorrelateCall correlate, AzureCallRegistry registry, TimeProvider clock,
        MediaGrantAuthentication mediaAuthentication, AzureMediaGrantScope mediaScope)
    {
        _context = context;
        _options = options;
        _telephony = telephony;
        _sink = sink;
        _correlate = correlate;
        _registry = registry;
        _clock = clock;
        _input = new AudioBuffer(clock);
        _output = new AudioBuffer(clock);
        _voiceFactory = voice;
        _mediaAuthentication = mediaAuthentication;
        _mediaScope = mediaScope;
        var remaining = context.Deadline - clock.GetUtcNow();
        _deadlineTimer = clock.CreateTimer(_ => _ = FailAsync("DEADLINE_EXCEEDED"), null,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        if (Interlocked.Exchange(ref _voiceStarted, 1) != 0)
            throw new ProviderException("VOICE_ALREADY_PREPARED");
        _signalPump = SignalPumpAsync();
        try
        {
            _mediaAuthentication.EnsureLoggingVerified();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _mediaAuthentication.RevalidateScopeAsync(_context, _mediaScope, timeout.Token).ConfigureAwait(false);
            CheckSideEffect(timeout.Token);
            var transport = await _voiceFactory.ConnectAsync(timeout.Token).ConfigureAwait(false);
            _voice = new VoiceConversation(transport, _context, _options.VoiceLive, _clock, _output, Publish,
                StopOutputAsync, FailAsync, _stop.Token);
            await _voice.InitializeAsync(timeout.Token).ConfigureAwait(false);
            await _mediaAuthentication.RevalidateScopeAsync(_context, _mediaScope, timeout.Token).ConfigureAwait(false);
            _mediaAuthentication.EnsureLoggingVerified();
            CheckSideEffect(timeout.Token);
            Volatile.Write(ref _voicePrepared, 1);
        }
        catch
        {
            await FailAsync("VOICE_INITIALIZATION_FAILED").ConfigureAwait(false);
            throw new ProviderException("VOICE_INITIALIZATION_FAILED");
        }
    }

    public async Task<ProviderHandle> DialAsync(CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        if (Volatile.Read(ref _voicePrepared) != 1) throw new ProviderException("VOICE_NOT_READY");
        if (!_options.Evidence.IsCurrent(_clock.GetUtcNow()))
            throw new ProviderException("TPE_READINESS_UNVERIFIED");
        if (Interlocked.Exchange(ref _dialStarted, 1) != 0)
            throw new ProviderException("DIAL_ALREADY_DISPATCHED", true);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        ProviderHandle? dispatched = null;
        try
        {
            var grant = await _mediaAuthentication.IssueAsync(_context, _mediaScope, linked.Token).ConfigureAwait(false);
            CheckSideEffect(linked.Token);
            var remaining = grant.ExpiresAt - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new ProviderException("MEDIA_GRANT_EXPIRED");
            _mediaSetupTimer = _clock.CreateTimer(_ => _ = FailAsync("MEDIA_GRANT_EXPIRED"), null,
                remaining, Timeout.InfiniteTimeSpan);
            var handle = await _telephony.DialAsync(_context, grant, linked.Token).ConfigureAwait(false);
            dispatched = handle;
            BindHandle(handle);
            if (!await _correlate(_context.CallId, handle.ConnectionId, handle.ServerCallId, cancellationToken).ConfigureAwait(false))
                throw new ProviderException("PROVIDER_CORRELATION_REJECTED", true);
            if (_stop.IsCancellationRequested || _clock.GetUtcNow() >= _context.Deadline)
            {
                await TerminateBoundedAsync(handle.ConnectionId).ConfigureAwait(false);
                throw new ProviderException("DEADLINE_EXCEEDED", true);
            }
            return handle;
        }
        catch (Exception ex)
        {
            if (dispatched is not null)
                await TerminateBoundedAsync(dispatched.ConnectionId).ConfigureAwait(false);
            if (_handle is { } known && known.ConnectionId != dispatched?.ConnectionId)
                await TerminateBoundedAsync(known.ConnectionId).ConfigureAwait(false);
            if (ex is ProviderException) throw;
            throw new ProviderException("PROVIDER_DISPATCH_UNKNOWN", true);
        }
    }

    private void BindHandle(ProviderHandle handle)
    {
        lock (_gate)
        {
            if (_handle is { } previous && previous.ConnectionId != handle.ConnectionId)
                throw new ProviderException("PROVIDER_CORRELATION_REJECTED", true);
            _handle = handle;
        }
    }

    internal void Connected(ProviderHandle handle, string? correlationId = null)
    {
        BindHandle(handle);
        lock (_gate)
        {
            if (_correlationId is not null && _correlationId != correlationId)
                throw new ProviderException("PROVIDER_CORRELATION_REJECTED");
            _correlationId = correlationId;
        }
        if (_stop.IsCancellationRequested || _clock.GetUtcNow() >= _context.Deadline)
        {
            if (Volatile.Read(ref _failed) != 0)
                _ = TerminateBoundedAsync(handle.ConnectionId);
            else
                _ = FailAsync("LATE_CONNECTION");
            return;
        }
        if (_connected.TrySetResult()) _ = WatchMediaStartupAsync();
    }

    private async Task WatchMediaStartupAsync()
    {
        try { await _mediaReady.Task.WaitAsync(TimeSpan.FromSeconds(30), _clock, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (TimeoutException) { await FailAsync("MEDIA_START_TIMEOUT").ConfigureAwait(false); }
    }

    internal async Task<AuthenticatedMedia> AuthenticateMediaAsync(HttpContext http, CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _mediaAuthentication.ConsumeAsync(http, _context, _mediaScope, linked.Token).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _mediaAuthorized, 1) != 0)
            throw new ProviderException("MEDIA_REPLAY_REJECTED");
        _mediaSetupTimer?.Dispose();
        try
        {
            await _connected.Task.WaitAsync(TimeSpan.FromSeconds(10), _clock, linked.Token).ConfigureAwait(false);
            CheckSideEffect(linked.Token);
            var handle = _handle;
            if (handle is null || string.IsNullOrEmpty(_correlationId) ||
                http.Request.Headers["x-ms-call-connection-id"].ToString() != handle.ConnectionId ||
                http.Request.Headers["x-ms-call-correlation-id"].ToString() != _correlationId)
                throw new ProviderException("MEDIA_CORRELATION_REJECTED");
            return new AuthenticatedMedia(_context.CallId, handle.ConnectionId, _correlationId);
        }
        catch
        {
            await FailAsync("MEDIA_AUTHENTICATED_SETUP_FAILED").ConfigureAwait(false);
            throw new ProviderException("MEDIA_AUTHENTICATED_SETUP_FAILED");
        }
    }

    internal async Task PrepareMediaAsync(AuthenticatedMedia identity, CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        if (Volatile.Read(ref _mediaAuthorized) != 1 || identity.CallId != _context.CallId ||
            identity.ConnectionId != _handle?.ConnectionId || identity.CorrelationId != _correlationId)
            throw new ProviderException("MEDIA_GRANT_REJECTED");
        if (_voice is null || Volatile.Read(ref _voicePrepared) != 1)
            throw new ProviderException("VOICE_NOT_READY");
        if (Interlocked.Exchange(ref _mediaPrepared, 1) != 0)
            throw new ProviderException("MEDIA_REPLAY_REJECTED");
        try
        {
            _mediaAuthentication.EnsureLoggingVerified();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _mediaAuthentication.RevalidateScopeAsync(_context, _mediaScope, timeout.Token).ConfigureAwait(false);
            CheckSideEffect(timeout.Token);
            await _voice.ActivateAsync(timeout.Token).ConfigureAwait(false);
            await _mediaAuthentication.RevalidateScopeAsync(_context, _mediaScope, timeout.Token).ConfigureAwait(false);
            _mediaAuthentication.EnsureLoggingVerified();
            CheckSideEffect(timeout.Token);
            _inputPump = InputPumpAsync();
        }
        catch
        {
            await FailAsync("VOICE_INITIALIZATION_FAILED").ConfigureAwait(false);
            throw new ProviderException("VOICE_INITIALIZATION_FAILED");
        }
    }

    internal Task MediaSetupFailedAsync() => FailAsync("MEDIA_TRANSPORT_FAILED");

    internal void Disconnected(ProviderHandle handle)
    {
        BindHandle(handle);
        lock (_gate) _disconnected = true;
        _stop.Cancel();
        _input.Clear();
        _output.Clear();
        _socket?.Abort();
    }

    private void CheckSideEffect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AzureValidation.Deadline(_context, _clock);
        if (_stop.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            throw new ProviderException("CALL_ENDING");
    }

    public async Task InstructAsync(string text, CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        if (!_mediaReady.Task.IsCompletedSuccessfully) throw new ProviderException("VOICE_NOT_READY");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var voice = _voice ?? throw new ProviderException("VOICE_NOT_READY");
        await ControlAsync(() => voice.InstructAsync(text, linked.Token)).ConfigureAwait(false);
    }

    public async Task SendDtmfAsync(string digits, CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        var handle = _handle ?? throw new ProviderException("CALL_NOT_CONNECTED");
        if (!_connected.Task.IsCompletedSuccessfully)
            throw new ProviderException("CALL_NOT_CONNECTED");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        CheckSideEffect(linked.Token);
        await _telephony.SendDtmfAsync(handle.ConnectionId, AzureValidation.PstnTarget(_context.Target),
            _context.CallId, digits, linked.Token).ConfigureAwait(false);
    }

    public async Task CompleteToolAsync(string toolCallId, JsonObject result, CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        if (!_mediaReady.Task.IsCompletedSuccessfully) throw new ProviderException("VOICE_NOT_READY");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var voice = _voice ?? throw new ProviderException("VOICE_NOT_READY");
        await ControlAsync(() => voice.CompleteToolAsync(toolCallId, result, linked.Token)).ConfigureAwait(false);
    }

    private async Task ControlAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (ProviderException) { throw; }
        catch (Exception)
        {
            await FailAsync("VOICE_CONTROL_FAILED").ConfigureAwait(false);
            throw new ProviderException("VOICE_CONTROL_FAILED");
        }
    }

    internal async Task RunMediaAsync(WebSocket socket, AuthenticatedMedia identity, CancellationToken cancellationToken)
    {
        CheckSideEffect(cancellationToken);
        if (Volatile.Read(ref _mediaAuthorized) != 1)
            throw new ProviderException("MEDIA_GRANT_REJECTED");
        if (_voice is null || Volatile.Read(ref _mediaPrepared) != 1) throw new ProviderException("VOICE_NOT_READY");
        if (identity.CallId != _context.CallId || identity.ConnectionId != _handle?.ConnectionId ||
            string.IsNullOrEmpty(identity.CorrelationId))
            throw new ProviderException("MEDIA_CORRELATION_REJECTED");
        if (Interlocked.Exchange(ref _mediaAccepted, 1) != 0)
            throw new ProviderException("MEDIA_REPLAY_REJECTED");
        _socket = socket;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Task? sender = null;
        try
        {
            var protocol = new AcsMediaProtocol(AzureValidation.PstnTarget(_context.Target), _clock);
            using (var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                metadataTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var metadata = await AcsMediaProtocol.ReceiveAsync(socket, metadataTimeout.Token).ConfigureAwait(false);
                if (metadata is null) throw new ProviderException("MEDIA_DISCONNECTED");
                protocol.Read(metadata);
                if (!protocol.MetadataReceived) throw new ProviderException("MEDIA_METADATA_REQUIRED");
            }
            await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30), _clock, linked.Token).ConfigureAwait(false);
            CheckSideEffect(linked.Token);
            if (string.IsNullOrEmpty(_correlationId) || identity.CorrelationId != _correlationId)
                throw new ProviderException("MEDIA_CORRELATION_REJECTED");
            _mediaReady.TrySetResult();
            sender = OutputPumpAsync(socket, linked.Token);
            await _voice.DiscloseAsync(linked.Token).ConfigureAwait(false);
            while (!linked.IsCancellationRequested)
            {
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                idleTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var message = await AcsMediaProtocol.ReceiveAsync(socket, idleTimeout.Token).ConfigureAwait(false);
                if (message is null) throw new ProviderException("MEDIA_DISCONNECTED");
                CheckSideEffect(linked.Token);
                if (protocol.Read(message) is { } audio)
                    _input.Enqueue(audio.Span);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await FailAsync(ex is ProviderException provider ? provider.Code : "MEDIA_DISCONNECTED").ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            socket.Abort();
            if (sender is not null) await sender.ConfigureAwait(false);
        }
    }

    private async Task InputPumpAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await _input.TakeAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                    CheckSideEffect(timeout.Token);
                    await _voice!.AppendAudioAsync(frame.Bytes, timeout.Token).ConfigureAwait(false);
                }
                finally { _input.Release(frame); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { await FailAsync(ex is ProviderException provider ? provider.Code : "MEDIA_INPUT_FAILED").ConfigureAwait(false); }
    }

    private async Task OutputPumpAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _output.TakeAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                    await _mediaSend.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        CheckSideEffect(timeout.Token);
                        if (_voice!.CanSend(frame))
                        {
                            await socket.SendAsync(AcsMediaProtocol.Outgoing(frame.Bytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                            _voice.AudioSent(frame);
                        }
                    }
                    finally { _mediaSend.Release(); }
                    // Bound ACS's playback backlog too; never blast accumulated PCM into the carrier.
                    await Task.Delay(TimeSpan.FromSeconds(frame.Bytes.Length / (double)AzureValidation.BytesPerSecond), _clock, cancellationToken).ConfigureAwait(false);
                }
                finally { _output.Release(frame); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { await FailAsync(ex is ProviderException provider ? provider.Code : "MEDIA_OUTPUT_FAILED").ConfigureAwait(false); }
    }

    private async Task StopOutputAsync(CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(150));
        await _mediaSend.WaitAsync(timeout.Token).ConfigureAwait(false);
        try { await socket.SendAsync(AcsMediaProtocol.StopAudio(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); }
        finally { _mediaSend.Release(); }
    }

    private void Publish(ProviderSignal signal)
    {
        if (!_signals.Writer.TryWrite(signal))
            throw new ProviderException("PROVIDER_EVENT_BACKLOG_EXCEEDED");
    }

    private async Task SignalPumpAsync()
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await _sink.PublishAsync(_context.CallId, signal, _stop.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2), _clock, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { await FailAsync("PROVIDER_EVENT_DELIVERY_FAILED").ConfigureAwait(false); }
    }

    private async Task<TerminationEvidence> TerminateBoundedAsync(string connectionId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await _telephony.TerminateAsync(connectionId, timeout.Token).ConfigureAwait(false); }
        catch (Exception) { return TerminationEvidence.Unknown; }
    }

    private async Task FailAsync(string code)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        _stop.Cancel();
        _input.Clear();
        _output.Clear();
        _socket?.Abort();
        // Termination has priority over potentially blocked event consumers.
        if (_handle is { } handle) await TerminateBoundedAsync(handle.ConnectionId).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _sink.PublishAsync(_context.CallId,
                new ProviderSignal("failure", new JsonObject { ["code"] = code }), timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    public async Task<TerminationEvidence> HangupAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        _input.Clear();
        _output.Clear();
        _socket?.Abort();
        if (_disconnected) return TerminationEvidence.Confirmed;
        if (_handle is not { } handle) return TerminationEvidence.Unknown;
        return await _telephony.TerminateAsync(handle.ConnectionId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registry.Remove(_context.CallId, this);
        _deadlineTimer.Dispose();
        _mediaSetupTimer?.Dispose();
        if (!_disconnected && _handle is { } handle)
            await TerminateBoundedAsync(handle.ConnectionId).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        _socket?.Abort();
        _signals.Writer.TryComplete();
        if (_voice is not null) await _voice.DisposeAsync().ConfigureAwait(false);
        if (_inputPump is not null) await _inputPump.ConfigureAwait(false);
        if (_signalPump is not null) await _signalPump.ConfigureAwait(false);
        _input.Dispose();
        _output.Dispose();
        _stop.Dispose();
    }
}
