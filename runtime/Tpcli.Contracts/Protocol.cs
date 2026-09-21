using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tpcli.Contracts;

public static class Protocol
{
    public const string Version = "1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record WireError(string Code, string Message, bool Retryable = false, long? NextCursor = null);
public sealed record ErrorResponse(WireError Error);
public sealed record CommandRequest(
    string SchemaVersion,
    string SessionId,
    string Operation,
    string IdempotencyKey,
    JsonObject Payload,
    string? CallId = null);

public sealed record CommandReceipt(
    string SchemaVersion,
    string CommandId,
    string SessionId,
    string? CallId,
    string Operation,
    string Status,
    DateTimeOffset AcceptedAt,
    DateTimeOffset UpdatedAt,
    WireError? Error = null,
    JsonObject? Result = null);

public sealed record SessionInfo(
    string SchemaVersion,
    string SessionId,
    string Status,
    DateTimeOffset LeaseExpiresAt,
    string ProviderMode);

public sealed record CallState(
    string CallId,
    string SessionId,
    string Target,
    string Source,
    string Route,
    string ProviderMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset Deadline,
    string Lifecycle,
    string TaskOutcome,
    string? TerminationReason,
    string HangupStatus,
    string TranscriptStatus,
    string SummaryStatus,
    long LastSequence);

public sealed record EventEnvelope(
    string SchemaVersion,
    string EventId,
    string SessionId,
    string? CallId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Type,
    string? CommandId,
    JsonObject Payload);

public sealed record EventBatch(
    IReadOnlyList<EventEnvelope> Events,
    long NextCursor,
    bool HasMore,
    CallState? CallState);

public sealed record TaskSummary(
    string Summary,
    string TaskOutcome,
    string TranscriptStatus,
    string ProviderMode,
    string Status,
    IReadOnlyList<string>? Facts = null,
    IReadOnlyList<string>? Commitments = null,
    IReadOnlyList<string>? OutstandingItems = null,
    IReadOnlyList<string>? SourceReferences = null);

public sealed record ApprovalView(
    string ApprovalId,
    string CallId,
    string ActionHash,
    DateTimeOffset ExpiresAt,
    string Status,
    long ConversationVersion,
    string? Description = null,
    JsonObject? MaterialTerms = null,
    string? Actor = null);

public sealed record ReadinessCheck(string Name, string Status, string Code, string Message);
public sealed record ProviderCapabilities(
    string Mode,
    bool Pstn,
    bool Teams,
    bool BidirectionalAudio,
    string Source,
    string Route,
    IReadOnlyList<ReadinessCheck> Checks);
