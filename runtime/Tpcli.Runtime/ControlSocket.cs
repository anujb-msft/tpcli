using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;
using Tpcli.Core;

namespace Tpcli.Runtime;

internal static class ControlSocket
{
    public static async Task HandleAsync(string sessionId, HttpContext context, CallRuntime runtime, EventJournal journal)
    {
        if (!context.WebSockets.IsWebSocketRequest) throw new ControlException("WEBSOCKET_REQUIRED", 400);
        var caller = context.Caller();
        var connectionId = Safe.Id("owner");
        var session = await runtime.AttachAsync(caller, sessionId, connectionId, context.RequestAborted);
        using var subscription = journal.Subscribe(sessionId);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, subscription.Disconnected);
        if (long.TryParse(context.User.FindFirstValue("exp"), out var expiry))
        {
            var remaining = DateTimeOffset.FromUnixTimeSeconds(expiry) - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) stopped.Cancel();
            else stopped.CancelAfter(remaining);
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await SendAsync(socket, SessionEvent(session, "session.ready"), stopped.Token);
            var receiver = ReceiveAsync(socket, caller, sessionId, connectionId, runtime, journal, stopped.Token);
            var sender = SendEventsAsync(socket, caller, sessionId, runtime, journal, subscription, stopped.Token);
            await Task.WhenAny(receiver, sender);
            stopped.Cancel();
            using (var revokeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                await runtime.RevokeAsync(caller, sessionId, connectionId, revokeTimeout.Token);
            var policyViolation = subscription.SlowConsumer;
            var reason = subscription.SlowConsumer ? "SLOW_CONSUMER" : "SESSION_CLOSED";
            try { await Task.WhenAll(receiver, sender); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ControlException) { policyViolation = true; reason = "INVALID_CONTROL_MESSAGE"; }
            catch (JsonException) { policyViolation = true; reason = "INVALID_CONTROL_MESSAGE"; }
            catch (TimeoutException) { policyViolation = true; reason = "SLOW_CONSUMER"; }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await socket.CloseOutputAsync(policyViolation ? WebSocketCloseStatus.PolicyViolation
                        : WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }
        }
        finally
        {
            stopped.Cancel();
            journal.Unsubscribe(subscription);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await runtime.RevokeAsync(caller, sessionId, connectionId, timeout.Token); }
            catch (OperationCanceledException) { }
            catch (ControlException) { }
        }
    }

    private static EventEnvelope SessionEvent(SessionInfo session, string type) =>
        new(Protocol.Version, Safe.Id("evt"), session.SessionId, null, 0, DateTimeOffset.UtcNow, type, null,
            new JsonObject { ["session"] = Safe.Json(session), ["heartbeat_seconds"] = 5, ["lease_seconds"] = 15 });

    private static async Task SendAsync(WebSocket socket, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = Safe.Json(envelope);
        json["call_id"] = envelope.CallId;
        json["command_id"] = envelope.CommandId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json, Protocol.Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private static async Task SendEventsAsync(WebSocket socket, Caller caller, string sessionId,
        CallRuntime runtime, EventJournal journal, EventSubscription subscription, CancellationToken cancellationToken)
    {
        var cursors = new Dictionary<string, long>();
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var state in await runtime.SessionCallsAsync(caller, sessionId, cancellationToken))
            {
                var cursor = cursors.GetValueOrDefault(state.CallId);
                while (true)
                {
                    var batch = await journal.ReadAsync(caller, state.CallId, cursor, cancellationToken);
                    foreach (var envelope in batch.Events) await SendAsync(socket, envelope, cancellationToken);
                    cursor = batch.NextCursor;
                    cursors[state.CallId] = cursor;
                    if (!batch.HasMore) break;
                }
            }
            var session = await runtime.SessionAsync(caller, sessionId, cancellationToken);
            if (session.Status == "revoked")
            {
                await SendAsync(socket, SessionEvent(session, "session.revoked"), cancellationToken);
                return;
            }
            while (subscription.Changes.TryRead(out _)) { }
            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, Caller caller, string sessionId, string connectionId,
        CallRuntime runtime, EventJournal journal, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) return;
                if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > buffer.Length)
                    throw new ControlException("INVALID_CONTROL_MESSAGE", 400);
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            var payload = JsonNode.Parse(message.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 4 }) as JsonObject
                ?? throw new ControlException("INVALID_CONTROL_MESSAGE", 400);
            switch (Safe.Text(payload, "type"))
            {
                case "heartbeat" when payload.Count == 1:
                    await runtime.HeartbeatAsync(caller, sessionId, connectionId, cancellationToken);
                    break;
                case "ack" when payload.Count == 3 && Safe.Text(payload, "call_id") is { Length: <= 128 } callId
                    && payload["sequence"] is JsonValue sequenceNode && sequenceNode.TryGetValue<long>(out var sequence):
                    await journal.AckAsync(caller, sessionId, callId, sequence, cancellationToken);
                    break;
                default: throw new ControlException("INVALID_CONTROL_MESSAGE", 400);
            }
        }
    }
}
