using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed class EventSubscription : IDisposable
{
    internal EventSubscription(string sessionId, int capacity)
    {
        SessionId = sessionId;
        Queue = Channel.CreateBounded<bool>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }
    public string SessionId { get; }
    internal Channel<bool> Queue { get; }
    public ChannelReader<bool> Changes => Queue.Reader;
    public CancellationToken Disconnected => stopped.Token;
    private readonly CancellationTokenSource stopped = new();
    public bool SlowConsumer { get; private set; }
    internal void Notify()
    {
        if (!Queue.Writer.TryWrite(true))
        {
            SlowConsumer = true;
            stopped.Cancel();
            Queue.Writer.TryComplete();
        }
    }
    public void Dispose()
    {
        stopped.Cancel();
        Queue.Writer.TryComplete();
    }
}

public sealed class EventJournal(ControlStore store, RuntimeSettings settings, TimeProvider clock)
{
    private sealed class Buffer
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly SortedDictionary<long, (EventEnvelope Event, int Bytes)> Events = [];
        public int Bytes;
        public bool Dropped;
        public DateTimeOffset? CompletedAt;
    }
    private readonly ConcurrentDictionary<string, Buffer> buffers = new();
    private readonly ConcurrentDictionary<EventSubscription, byte> subscribers = new();

    public EventSubscription Subscribe(string sessionId)
    {
        var subscription = new EventSubscription(sessionId, settings.SubscriberQueueCapacity);
        subscribers[subscription] = 0;
        return subscription;
    }
    public void Unsubscribe(EventSubscription subscription)
    {
        subscribers.TryRemove(subscription, out _);
        subscription.Dispose();
    }
    public void Notify(string sessionId)
    {
        foreach (var subscriber in subscribers.Keys)
        {
            if (subscriber.Disconnected.IsCancellationRequested)
                subscribers.TryRemove(subscriber, out _);
            else if (subscriber.SessionId == sessionId) subscriber.Notify();
        }
    }

    public void Register(string callId)
    {
        if (buffers.ContainsKey(callId)) return;
        if (buffers.Count >= settings.MaxResidentCalls) Prune();
        if (buffers.Count >= settings.MaxResidentCalls) throw new ControlException("RUNTIME_CAPACITY", 429, true);
        buffers.TryAdd(callId, new Buffer());
    }

    public async Task<EventEnvelope?> AppendAsync(string callId,
        Func<ControlTransaction, CallRecord, Task<(string Type, JsonObject Payload)?>> prepare,
        CancellationToken cancellationToken = default)
    {
        if (!buffers.TryGetValue(callId, out var buffer)) return null;
        await buffer.Gate.WaitAsync(cancellationToken);
        EventEnvelope? envelope;
        try
        {
            envelope = await store.TransactionAsync(async tx =>
            {
                var call = await tx.CallAsync(callId);
                if (call is null) return null;
                var payload = await prepare(tx, call);
                return payload is null ? null : await tx.EmitVolatileAsync(call, payload.Value.Type, payload.Value.Payload);
            }, cancellationToken);
            if (envelope is null) return null;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Protocol.Json).Length;
            if (bytes <= RuntimeSettings.ReplayBytesPerCall)
            {
                buffer.Events.Add(envelope.Sequence, (envelope, bytes));
                buffer.Bytes += bytes;
            }
            else buffer.Dropped = true;
            while (buffer.Bytes > RuntimeSettings.ReplayBytesPerCall)
            {
                var first = buffer.Events.First();
                buffer.Events.Remove(first.Key);
                buffer.Bytes -= first.Value.Bytes;
                buffer.Dropped = true;
            }
        }
        finally { buffer.Gate.Release(); }
        Notify(envelope.SessionId);
        return envelope;
    }

    public async Task<EventBatch> ReadAsync(Caller caller, string callId, long after, CancellationToken cancellationToken = default)
    {
        buffers.TryGetValue(callId, out var buffer);
        if (buffer is not null) await buffer.Gate.WaitAsync(cancellationToken);
        try
        {
            var (call, rows) = await store.TransactionAsync(async tx =>
            {
                var call = await tx.OwnedCallAsync(caller, callId);
                return (call, await tx.EventsAsync(callId, after));
            }, cancellationToken);
            if (buffer is not null)
            {
                buffer.CompletedAt ??= call.CompletedAt;
                Expire(buffer);
            }
            var events = rows.Select(row =>
            {
                if (row.Payload is not null) return row.Envelope(row.Payload);
                if (buffer is not null && buffer.Events.TryGetValue(row.Sequence, out var item)) return item.Event;
                return row.Envelope(new JsonObject
                {
                    ["from_sequence"] = row.Sequence,
                    ["to_sequence"] = row.Sequence,
                    ["reason"] = "volatile_content_unavailable"
                }, "transcript.gap");
            }).ToArray();
            var next = events.Length == 0 ? after : events[^1].Sequence;
            return new EventBatch(events, next, call.State.LastSequence > next, ExposedState(call.State, buffer));
        }
        finally { buffer?.Gate.Release(); }
    }

    public async Task AckAsync(Caller caller, string sessionId, string callId, long sequence, CancellationToken cancellationToken)
    {
        var state = await store.TransactionAsync(async tx =>
            (await tx.OwnedCallAsync(caller, callId, sessionId)).State, cancellationToken);
        if (sequence < 0 || sequence > state.LastSequence) throw new ControlException("INVALID_ACK", 400);
        if (!buffers.TryGetValue(callId, out var buffer)) return;
        await buffer.Gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var key in buffer.Events.Keys.TakeWhile(key => key <= sequence).ToArray())
            {
                buffer.Bytes -= buffer.Events[key].Bytes;
                buffer.Events.Remove(key);
            }
        }
        finally { buffer.Gate.Release(); }
    }

    public async Task<ApprovalView> WithContentAsync(ApprovalView approval)
    {
        if (!buffers.TryGetValue(approval.CallId, out var buffer)) return approval;
        await buffer.Gate.WaitAsync();
        try
        {
            Expire(buffer);
            var content = buffer.Events.Values.Select(value => value.Event)
                .FirstOrDefault(value => value.Type == "approval.requested"
                    && Safe.Text(value.Payload, "approval_id") == approval.ApprovalId);
            return content is null ? approval : approval with
            {
                Description = Safe.Text(content.Payload, "description"),
                MaterialTerms = content.Payload["material_terms"]?.DeepClone() as JsonObject
            };
        }
        finally { buffer.Gate.Release(); }
    }

    public async Task<CallState> ExposedStateAsync(CallRecord call)
    {
        if (!buffers.TryGetValue(call.State.CallId, out var buffer)) return ExposedState(call.State, null);
        await buffer.Gate.WaitAsync();
        try
        {
            buffer.CompletedAt ??= call.CompletedAt;
            Expire(buffer);
            return ExposedState(call.State, buffer);
        }
        finally { buffer.Gate.Release(); }
    }
    private static CallState ExposedState(CallState state, Buffer? buffer)
    {
        var transcriptMissing = buffer is null || buffer.Dropped;
        var summaryMissing = buffer is null || !buffer.Events.Values.Any(v => v.Event.Type == "summary.ready");
        return state with
        {
            TranscriptStatus = transcriptMissing && state.TranscriptStatus == "complete" ? "partial" : state.TranscriptStatus,
            SummaryStatus = summaryMissing && state.SummaryStatus == "complete" ? "unavailable" : state.SummaryStatus
        };
    }
    public async Task MarkCompletedAsync(string callId, DateTimeOffset completedAt)
    {
        if (!buffers.TryGetValue(callId, out var buffer)) return;
        await buffer.Gate.WaitAsync();
        try { buffer.CompletedAt ??= completedAt; }
        finally { buffer.Gate.Release(); }
    }
    private void Expire(Buffer buffer)
    {
        if (buffer.CompletedAt is { } completed && clock.GetUtcNow() - completed >= RuntimeSettings.ReplayRetention)
        {
            buffer.Events.Clear();
            buffer.Bytes = 0;
            buffer.Dropped = true;
        }
    }
    public void Prune()
    {
        foreach (var pair in buffers)
        {
            if (!pair.Value.Gate.Wait(0)) continue;
            try
            {
                if (pair.Value.CompletedAt is { } at && clock.GetUtcNow() - at >= RuntimeSettings.ReplayRetention)
                    buffers.TryRemove(pair.Key, out _);
            }
            finally { pair.Value.Gate.Release(); }
        }
    }
}
