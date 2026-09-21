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
        await authentication.AuthenticateAsync(authorization, cancellationToken).ConfigureAwait(false);
        using var document = ParseDocument(body);
        CloudEvent[] events;
        try { events = CloudEvent.ParseMany(new BinaryData(body)); }
        catch (Exception) { throw new ProviderException("CALLBACK_INVALID"); }
        if (events.Length is < 1 or > 16)
            throw new ProviderException("CALLBACK_INVALID");
        var eventBodies = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        if (eventBodies.Length != events.Length)
            throw new ProviderException("CALLBACK_INVALID");
        for (var index = 0; index < events.Length; index++)
        {
            var cloudEvent = events[index];
            if (string.IsNullOrEmpty(cloudEvent.Id) || cloudEvent.Id.Length > 1024 ||
                cloudEvent.Time is not { } timestamp || timestamp > clock.GetUtcNow().AddSeconds(30) ||
                !cloudEvent.Type.StartsWith("Microsoft.Communication.", StringComparison.Ordinal))
                throw new ProviderException("CALLBACK_INVALID");
            var fingerprint = CanonicalEventBody(eventBodies[index]);
            CallAutomationEventBase parsed;
            try { parsed = CallAutomationEventParser.Parse(cloudEvent); }
            catch (Exception) { throw new ProviderException("CALLBACK_INVALID"); }
            if (parsed is null || string.IsNullOrEmpty(parsed.CallConnectionId) || parsed.CallConnectionId.Length > 8192 ||
                (parsed.OperationContext is { Length: > 0 } context && context != callId))
                throw new ProviderException("CALLBACK_CORRELATION_REJECTED");
            if (!await correlate(callId, parsed.CallConnectionId, parsed.ServerCallId, cancellationToken).ConfigureAwait(false))
                throw new ProviderException("CALLBACK_CORRELATION_REJECTED");
            var eventKey = callId + ":" + cloudEvent.Id;
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

    private static JsonDocument ParseDocument(byte[] body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new ProviderException("CALLBACK_INVALID"); }
    }

    private static byte[] CanonicalEventBody(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, value);
        return buffer.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                string? previousName = null;
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (property.Name == previousName)
                        throw new ProviderException("CALLBACK_INVALID");
                    previousName = property.Name;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static JsonObject HandlePayload(ProviderHandle handle)
    {
        var value = new JsonObject { ["connection_id"] = handle.ConnectionId };
        if (!string.IsNullOrEmpty(handle.ServerCallId)) value["server_call_id"] = handle.ServerCallId;
        return value;
    }
}
