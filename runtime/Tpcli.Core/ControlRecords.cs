using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed record SessionRecord(
    string Id, string Tenant, string Principal, string Profile, string Status,
    DateTimeOffset LeaseExpiresAt, string? ConnectionId)
{
    public bool OwnedBy(Caller caller) => Tenant == caller.Tenant && Principal == caller.Principal;
    public bool Live(DateTimeOffset now) => Status == "active" && ConnectionId is not null && LeaseExpiresAt > now;
    public SessionInfo View(string mode) => new(Protocol.Version, Id, Status, LeaseExpiresAt, mode);
}

public sealed class CallRecord
{
    public required CallState State { get; set; }
    public required string Tenant { get; init; }
    public required string Principal { get; init; }
    public required string Profile { get; init; }
    public required string WorkerId { get; init; }
    public long Fence { get; set; } = 1;
    public string DispatchStatus { get; set; } = "none";
    public ProviderHandle? Handle { get; set; }
    public long ConversationVersion { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastTerminationAt { get; set; }
    public bool Active => !Safe.Terminal(State);
    public bool OwnedBy(Caller caller) => Tenant == caller.Tenant && Principal == caller.Principal;
}

public sealed record StoredCommand(
    string Tenant, string Principal, string KeyHash, string PayloadHash, CommandReceipt Receipt);
public sealed record StoredApproval(ApprovalView View, string ToolHash);
public sealed record StoredEvent(
    string EventId, string SessionId, string CallId, long Sequence,
    DateTimeOffset Timestamp, string Type, string? CommandId, JsonObject? Payload)
{
    public EventEnvelope Envelope(JsonObject payload, string? type = null) =>
        new(Protocol.Version, EventId, SessionId, CallId, Sequence, Timestamp, type ?? Type, CommandId, payload);
}
public sealed record TerminationAttempt(
    string Id, string CallId, string ProviderMode, string Reason, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, string Status, string Executor, string? ConnectionId);
public sealed record FakeCallRecord(
    string CallId, string ConnectionId, string Status, int DialCount, int HangupCount,
    bool HangupUnknown, DateTimeOffset CreatedAt, DateTimeOffset? TerminatedAt);
