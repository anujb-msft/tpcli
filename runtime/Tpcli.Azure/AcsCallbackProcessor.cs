using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed class AcsCallbackProcessor(
    CallbackAuthentication authentication,
    CallbackReplayGuard replay,
    CorrelateCall correlate,
    AzureCallRegistry registry,
    IProviderEventSink sink,
    TimeProvider clock)
{
    internal async Task ProcessAsync(string callId, string authorization, byte[] body, CancellationToken cancellationToken)
    {
        if (!AzureValidation.IsCallId(callId) || body.Length is 0 or > 262_144)
            throw new ProviderException("CALLBACK_INVALID");
        var token = await authentication.AuthenticateAsync(authorization, cancellationToken).ConfigureAwait(false);
        CloudEvent[] events;
        try { events = CloudEvent.ParseMany(new BinaryData(body)); }
        catch (Exception) { throw new ProviderException("CALLBACK_INVALID"); }
        if (events.Length is < 1 or > 16)
            throw new ProviderException("CALLBACK_INVALID");
        replay.BindToken(token, body);
        foreach (var cloudEvent in events)
        {
            if (string.IsNullOrEmpty(cloudEvent.Id) || cloudEvent.Id.Length > 1024 ||
                cloudEvent.Time is not { } timestamp || timestamp > clock.GetUtcNow().AddSeconds(30) ||
                !cloudEvent.Type.StartsWith("Microsoft.Communication.", StringComparison.Ordinal))
                throw new ProviderException("CALLBACK_INVALID");
            CallAutomationEventBase parsed;
            try { parsed = CallAutomationEventParser.Parse(cloudEvent); }
            catch (Exception) { throw new ProviderException("CALLBACK_INVALID"); }
            if (parsed is null || string.IsNullOrEmpty(parsed.CallConnectionId) || parsed.CallConnectionId.Length > 8192 ||
                (parsed.OperationContext is { Length: > 0 } context && context != callId))
                throw new ProviderException("CALLBACK_CORRELATION_REJECTED");
            if (!await correlate(callId, parsed.CallConnectionId, parsed.ServerCallId, cancellationToken).ConfigureAwait(false))
                throw new ProviderException("CALLBACK_CORRELATION_REJECTED");
            var eventKey = callId + ":" + cloudEvent.Id;
            var fingerprint = Encoding.UTF8.GetBytes(cloudEvent.Type + "\n" + cloudEvent.Data);
            if (replay.BeginEvent(eventKey, fingerprint) == ReplayDisposition.Duplicate)
                continue;
            try
            {
                var handle = new ProviderHandle(parsed.CallConnectionId, parsed.ServerCallId);
                ProviderSignal? signal = parsed switch
                {
                    CallConnected => new("connected", HandlePayload(handle), cloudEvent.Id),
                    CallDisconnected => new("disconnected", HandlePayload(handle), cloudEvent.Id),
                    CreateCallFailed => new("failure", new JsonObject { ["code"] = "TPE_CREATE_FAILED" }, cloudEvent.Id),
                    SendDtmfTonesFailed => new("warning", new JsonObject { ["code"] = "DTMF_DELIVERY_FAILED" }, cloudEvent.Id),
                    MediaStreamingFailed => new("failure", new JsonObject { ["code"] = "MEDIA_STREAMING_FAILED" }, cloudEvent.Id),
                    _ => null
                };
                if (parsed is CallConnected) registry.Find(callId)?.Connected(handle, parsed.CorrelationId);
                if (parsed is CallDisconnected) registry.Find(callId)?.Disconnected(handle);
                // No live worker is required. Durable runtime correlation handles late/revoked calls.
                if (signal is not null)
                    await sink.PublishAsync(callId, signal, cancellationToken).ConfigureAwait(false);
                replay.Complete(eventKey);
            }
            catch
            {
                replay.Retry(eventKey);
                throw;
            }
        }
    }

    private static JsonObject HandlePayload(ProviderHandle handle)
    {
        var value = new JsonObject { ["connection_id"] = handle.ConnectionId };
        if (!string.IsNullOrEmpty(handle.ServerCallId)) value["server_call_id"] = handle.ServerCallId;
        return value;
    }
}
