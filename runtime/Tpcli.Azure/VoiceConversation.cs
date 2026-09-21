using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed class VoiceConversation : IAsyncDisposable
{
    private sealed class ResponseState(string id)
    {
        internal readonly string Id = id;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Obsolete;
        internal bool CancelRequested;
        internal string? CancelEventId;
        internal readonly Dictionary<string, ItemState> Items = new(StringComparer.Ordinal);
    }

    private sealed class ItemState(string id, int contentIndex)
    {
        internal readonly string Id = id;
        internal readonly int ContentIndex = contentIndex;
        internal string SegmentId => $"assistant:{Id}:{ContentIndex}";
        internal long Generated;
        internal long Sent;
        internal bool AudioDone;
        internal DateTimeOffset? FirstSentAt;
    }

    private sealed record ToolState(string Name, string Arguments, bool Completed = false);
    private readonly object _gate = new();
    private readonly IVoiceTransport _transport;
    private readonly CallContext _context;
    private readonly VoiceLiveOptions _options;
    private readonly TimeProvider _clock;
    private readonly Action<ProviderSignal> _publish;
    private readonly Func<CancellationToken, Task> _stopAudio;
    private readonly Func<string, Task> _fail;
    private readonly AudioBuffer _output;
    private readonly TranscriptTracker _transcripts;
    private readonly Dictionary<string, ResponseState> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolState> _tools = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop;
    private Task? _reader;
    private ResponseState? _current;
    private bool _disclosureRequested;
    private bool _interruptInProgress;

    internal VoiceConversation(IVoiceTransport transport, CallContext context, VoiceLiveOptions options,
        TimeProvider clock, AudioBuffer output, Action<ProviderSignal> publish,
        Func<CancellationToken, Task> stopAudio, Func<string, Task> fail, CancellationToken cancellationToken)
    {
        _transport = transport;
        _context = context;
        _options = options;
        _clock = clock;
        _output = output;
        _publish = publish;
        _stopAudio = stopAudio;
        _fail = fail;
        _transcripts = new TranscriptTracker(publish);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        CheckDeadline(cancellationToken);
        _reader = ReadLoopAsync();
        await _transport.SendAsync(VoiceProtocol.Configure(_options), cancellationToken).ConfigureAwait(false);
        await _configured.Task.WaitAsync(TimeSpan.FromSeconds(15), _clock, cancellationToken).ConfigureAwait(false);
        CheckDeadline(cancellationToken);
        await _transport.SendAsync(VoiceProtocol.OperatorContext(_context.Task, _context.AllowVoicemail, "initial_task"), cancellationToken).ConfigureAwait(false);
    }

    private void CheckDeadline(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        AzureValidation.Deadline(_context, _clock);
    }

    internal async Task DiscloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disclosureRequested) return;
            _disclosureRequested = true;
        }
        CheckDeadline(cancellationToken);
        await _transport.SendAsync(VoiceProtocol.Response(disclosure: true), cancellationToken).ConfigureAwait(false);
    }

    internal async Task AppendAudioAsync(byte[] audio, CancellationToken cancellationToken)
    {
        CheckDeadline(cancellationToken);
        await _transport.SendAsync(new JsonObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(audio)
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task InstructAsync(string text, CancellationToken cancellationToken)
    {
        AzureValidation.Text(text);
        CheckDeadline(cancellationToken);
        await InterruptAsync(cancellationToken).ConfigureAwait(false);
        await WaitForResponseAsync(cancellationToken).ConfigureAwait(false);
        CheckDeadline(cancellationToken);
        await _transport.SendAsync(VoiceProtocol.OperatorContext(text, _context.AllowVoicemail, "mid_call_instruction"), cancellationToken).ConfigureAwait(false);
        CheckDeadline(cancellationToken);
        await _transport.SendAsync(VoiceProtocol.Response(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task CompleteToolAsync(string toolCallId, JsonObject result, CancellationToken cancellationToken)
    {
        CheckDeadline(cancellationToken);
        if (result.ToJsonString().Length > 65_536)
            throw new ProviderException("VOICE_TOOL_RESULT_TOO_LARGE");
        ToolState tool;
        lock (_gate)
        {
            if (!_tools.TryGetValue(toolCallId, out tool!))
                throw new ProviderException("VOICE_TOOL_UNKNOWN");
            if (tool.Completed)
                throw new ProviderException("VOICE_TOOL_ALREADY_COMPLETED");
            // Consume once before wire dispatch; an ambiguous continuation is never automatically replayed.
            _tools[toolCallId] = tool with { Completed = true };
        }
        await _transport.SendAsync(VoiceProtocol.FunctionOutput(toolCallId, result), cancellationToken).ConfigureAwait(false);
        if (tool.Name == "request_approval")
            await SetAutomaticResponseAsync(true, cancellationToken).ConfigureAwait(false);
        await WaitForResponseAsync(cancellationToken).ConfigureAwait(false);
        CheckDeadline(cancellationToken);
        await _transport.SendAsync(VoiceProtocol.Response(), cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForResponseAsync(CancellationToken cancellationToken)
    {
        Task? done;
        lock (_gate) done = _current?.Done.Task;
        if (done is not null)
            await done.WaitAsync(TimeSpan.FromSeconds(2), _clock, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var message in _transport.ReadAsync(_stop.Token).ConfigureAwait(false))
                await HandleAsync(message, _stop.Token).ConfigureAwait(false);
            if (!_stop.IsCancellationRequested)
                throw new ProviderException("VOICE_DISCONNECTED");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var code = ex is ProviderException provider ? provider.Code : "VOICE_PROTOCOL_FAILED";
            _configured.TrySetException(new ProviderException(code));
            await _fail(code).ConfigureAwait(false);
        }
    }

    internal async Task HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var type = VoiceProtocol.RequiredString(message, "type");
        switch (type)
        {
            case "session.updated":
                if (!_configured.Task.IsCompleted)
                {
                    if (message["session"] is not JsonObject session ||
                        session["input_audio_format"]?.GetValue<string>() != "pcm16" ||
                        session["output_audio_format"]?.GetValue<string>() != "pcm16" ||
                        session["input_audio_sampling_rate"]?.GetValue<int>() != 24_000 ||
                        session["voice"]?["name"]?.GetValue<string>() != _options.Voice ||
                        session["voice"]?["type"]?.GetValue<string>() != "azure-standard" ||
                        session["model"]?.GetValue<string>() != _options.Model)
                        throw new ProviderException("VOICE_SESSION_FORMAT_UNSUPPORTED");
                    _configured.TrySetResult();
                }
                break;
            case "response.created":
                var id = VoiceProtocol.RequiredString(message["response"] as JsonObject ?? throw new JsonException(), "id");
                lock (_gate)
                {
                    if (_responses.ContainsKey(id)) break;
                    if (_interruptInProgress)
                        throw new ProviderException("VOICE_RESPONSE_DURING_INTERRUPTION");
                    if (_responses.Count >= 2048)
                        throw new ProviderException("VOICE_RESPONSE_CAPACITY_EXCEEDED");
                    if (_current is { Obsolete: false } previous && !previous.Done.Task.IsCompleted)
                        throw new ProviderException("VOICE_PARALLEL_RESPONSE_UNSUPPORTED");
                    _current = new ResponseState(id);
                    _responses.Add(id, _current);
                }
                break;
            case "response.audio.delta":
                CheckDeadline(cancellationToken);
                var responseId = VoiceProtocol.RequiredString(message, "response_id");
                var itemId = VoiceProtocol.RequiredString(message, "item_id");
                var contentIndex = message["content_index"]?.GetValue<int>() ?? 0;
                byte[] audio;
                var encoded = message["delta"]?.GetValue<string>();
                if (string.IsNullOrEmpty(encoded) || encoded.Length > AzureValidation.MaximumAudioBytes * 4 / 3)
                    throw new ProviderException("VOICE_AUDIO_INVALID");
                try { audio = Convert.FromBase64String(encoded); }
                catch (FormatException) { throw new ProviderException("VOICE_AUDIO_INVALID"); }
                lock (_gate)
                {
                    var response = GetResponse(responseId);
                    if (response.Obsolete) break;
                    var item = GetItem(response, itemId, contentIndex);
                    _output.Enqueue(audio, responseId, itemId, contentIndex);
                    item.Generated += audio.Length;
                }
                break;
            case "response.audio.done":
                lock (_gate)
                {
                    var response = GetResponse(VoiceProtocol.RequiredString(message, "response_id"));
                    var item = GetItem(response, VoiceProtocol.RequiredString(message, "item_id"), message["content_index"]?.GetValue<int>() ?? 0);
                    item.AudioDone = true;
                    MarkSent(response, item);
                }
                break;
            case "response.audio_transcript.delta":
            case "response.audio_transcript.done":
                lock (_gate)
                {
                    var response = GetResponse(VoiceProtocol.RequiredString(message, "response_id"));
                    var item = GetItem(response, VoiceProtocol.RequiredString(message, "item_id"), message["content_index"]?.GetValue<int>() ?? 0);
                    var final = type.EndsWith(".done", StringComparison.Ordinal);
                    _transcripts.Text(item.SegmentId, "assistant", message[final ? "transcript" : "delta"]?.GetValue<string>() ?? "", final);
                    if (response.Obsolete) _transcripts.Interrupt(item.SegmentId);
                    MarkSent(response, item);
                }
                break;
            case "conversation.item.input_audio_transcription.delta":
            case "conversation.item.input_audio_transcription.completed":
                var inputFinal = type.EndsWith(".completed", StringComparison.Ordinal);
                _transcripts.Text("recipient:" + VoiceProtocol.RequiredString(message, "item_id"), "recipient",
                    message[inputFinal ? "transcript" : "delta"]?.GetValue<string>() ?? "", inputFinal);
                break;
            case "conversation.item.input_audio_transcription.failed":
                _publish(new ProviderSignal("transcript.gap", new JsonObject { ["code"] = "RECIPIENT_TRANSCRIPT_UNAVAILABLE" }));
                break;
            case "input_audio_buffer.speech_started":
                await InterruptAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "response.function_call_arguments.done":
                var name = VoiceProtocol.RequiredString(message, "name");
                var arguments = VoiceProtocol.RequiredString(message, "arguments");
                var callId = VoiceProtocol.RequiredString(message, "call_id");
                var parsed = VoiceProtocol.ParseTool(name, arguments);
                lock (_gate)
                {
                    if (_tools.TryGetValue(callId, out var existing))
                    {
                        if (existing.Name != name || existing.Arguments != arguments)
                            throw new ProviderException("VOICE_TOOL_ID_REUSED");
                        break;
                    }
                    if (_tools.Count >= 256)
                        throw new ProviderException("VOICE_TOOL_CAPACITY_EXCEEDED");
                    _tools.Add(callId, new ToolState(name, arguments));
                }
                if (name == "request_approval")
                    await SetAutomaticResponseAsync(false, cancellationToken).ConfigureAwait(false);
                _publish(new ProviderSignal("tool.requested", new JsonObject
                {
                    ["tool_call_id"] = callId,
                    ["name"] = name,
                    ["arguments"] = parsed
                }));
                break;
            case "response.done":
                if (message["response"] is not JsonObject completed)
                    throw new ProviderException("VOICE_PROTOCOL_INVALID");
                lock (_gate)
                {
                    var response = GetResponse(VoiceProtocol.RequiredString(completed, "id"));
                    response.Done.TrySetResult();
                    if (completed["status"]?.GetValue<string>() == "failed")
                        throw new ProviderException("VOICE_RESPONSE_FAILED");
                    foreach (var item in response.Items.Values)
                    {
                        if (item.Generated > 0 && !_transcripts.HasText(item.SegmentId))
                            _publish(new ProviderSignal("transcript.gap", new JsonObject { ["code"] = "ASSISTANT_TRANSCRIPT_UNAVAILABLE" }));
                    }
                }
                break;
            case "error":
                if (message["error"]?["code"]?.GetValue<string>() == "response_cancel_not_active")
                {
                    lock (_gate)
                    {
                        var eventId = message["error"]?["event_id"]?.GetValue<string>();
                        var cancelling = _responses.Values.Where(x => x.CancelRequested && !x.Done.Task.IsCompleted &&
                            eventId is not null && x.CancelEventId == eventId).ToArray();
                        if (cancelling.Length == 1)
                        {
                            cancelling[0].Done.TrySetResult();
                            break;
                        }
                    }
                }
                throw new ProviderException("VOICE_PROVIDER_ERROR");
        }
    }

    private Task SetAutomaticResponseAsync(bool enabled, CancellationToken cancellationToken)
    {
        CheckDeadline(cancellationToken);
        return _transport.SendAsync(new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["turn_detection"] = new JsonObject
                {
                    ["type"] = "azure_semantic_vad",
                    ["create_response"] = enabled,
                    ["interrupt_response"] = false,
                    ["auto_truncate"] = false,
                    ["silence_duration_ms"] = 500
                }
            }
        }, cancellationToken);
    }

    internal bool CanSend(AudioFrame frame)
    {
        lock (_gate)
            return frame.ResponseId is { } id && _responses.TryGetValue(id, out var response) && !response.Obsolete;
    }

    internal void AudioSent(AudioFrame frame)
    {
        lock (_gate)
        {
            if (frame.ResponseId is null || frame.ItemId is null) return;
            var response = GetResponse(frame.ResponseId);
            var item = GetItem(response, frame.ItemId, frame.ContentIndex);
            item.FirstSentAt ??= _clock.GetUtcNow();
            item.Sent += frame.Bytes.Length;
            MarkSent(response, item);
        }
    }

    private void MarkSent(ResponseState response, ItemState item)
    {
        if (!response.Obsolete && item.AudioDone && item.Generated > 0 && item.Sent >= item.Generated)
            _transcripts.Sent(item.SegmentId);
    }

    internal async Task InterruptAsync(CancellationToken cancellationToken)
    {
        ResponseState? response;
        List<(string ItemId, int ContentIndex, int Milliseconds)> truncations = [];
        var sendCancel = false;
        lock (_gate)
        {
            if (_interruptInProgress)
                throw new ProviderException("VOICE_INTERRUPTION_IN_PROGRESS");
            _interruptInProgress = true;
            _output.Clear();
            response = _current;
            if (response is not null && !response.Obsolete)
            {
                response.Obsolete = true;
                sendCancel = !response.Done.Task.IsCompleted;
                response.CancelRequested = sendCancel;
                if (sendCancel) response.CancelEventId = "cancel_" + Guid.NewGuid().ToString("N");
                foreach (var item in response.Items.Values)
                {
                    var elapsed = item.FirstSentAt is { } sent ? (_clock.GetUtcNow() - sent).TotalMilliseconds : 0;
                    var estimated = (int)Math.Max(0, Math.Min(elapsed, item.Sent * 1000d / AzureValidation.BytesPerSecond));
                    truncations.Add((item.Id, item.ContentIndex, estimated));
                    _transcripts.Interrupt(item.SegmentId);
                }
            }
        }
        try
        {
            // Stop the provider playback queue first, independently of model cancellation acknowledgement.
            await _stopAudio(cancellationToken).ConfigureAwait(false);
            if (sendCancel && response is not null)
            {
                await _transport.SendAsync(VoiceProtocol.Cancel(response.Id, response.CancelEventId!), cancellationToken).ConfigureAwait(false);
                _ = WatchCancellationAsync(response);
            }
            foreach (var (itemId, contentIndex, milliseconds) in truncations)
                await _transport.SendAsync(VoiceProtocol.Truncate(itemId, contentIndex, milliseconds), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _interruptInProgress = false;
        }
    }

    private async Task WatchCancellationAsync(ResponseState response)
    {
        try { await response.Done.Task.WaitAsync(TimeSpan.FromSeconds(2), _clock, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (TimeoutException) { await _fail("VOICE_CANCEL_UNCONFIRMED").ConfigureAwait(false); }
    }

    private ResponseState GetResponse(string id) =>
        _responses.TryGetValue(id, out var response) ? response : throw new ProviderException("VOICE_RESPONSE_UNKNOWN");

    private static ItemState GetItem(ResponseState response, string itemId, int contentIndex)
    {
        if (contentIndex is < 0 or > 128 || itemId.Length > 512)
            throw new ProviderException("VOICE_PROTOCOL_INVALID");
        var key = $"{itemId}:{contentIndex}";
        if (response.Items.TryGetValue(key, out var item)) return item;
        if (response.Items.Count >= 128) throw new ProviderException("VOICE_RESPONSE_CAPACITY_EXCEEDED");
        item = new ItemState(itemId, contentIndex);
        response.Items.Add(key, item);
        return item;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null)
            await _reader.ConfigureAwait(false);
        _stop.Dispose();
    }
}
