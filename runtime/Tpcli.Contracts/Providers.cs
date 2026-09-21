using System.Text.Json.Nodes;

namespace Tpcli.Contracts;

public sealed record CallContext(
    string CallId,
    string SessionId,
    string Target,
    string Task,
    DateTimeOffset Deadline,
    bool AllowVoicemail = false);

public sealed record ProviderHandle(string ConnectionId, string? ServerCallId = null);

// Signals are in-memory only. The runtime persists an allowlist of control metadata.
public sealed record ProviderSignal(string Type, JsonObject Payload, string? ProviderEventId = null);

public interface IProviderEventSink
{
    Task PublishAsync(string callId, ProviderSignal signal, CancellationToken cancellationToken = default);
}

public interface ICallProviderFactory
{
    string Mode { get; }
    Task<ProviderCapabilities> CheckReadinessAsync(bool online, CancellationToken cancellationToken);
    Task<ICallConnection> PrepareAsync(CallContext context, CancellationToken cancellationToken);
}

public interface ICallConnection : IAsyncDisposable
{
    Task<ProviderHandle> DialAsync(CancellationToken cancellationToken);
    Task InstructAsync(string text, CancellationToken cancellationToken);
    Task SendDtmfAsync(string digits, CancellationToken cancellationToken);
    Task CompleteToolAsync(string toolCallId, JsonObject result, CancellationToken cancellationToken);
    Task<TerminationEvidence> HangupAsync(CancellationToken cancellationToken);
}

public enum TerminationEvidence { Pending, Confirmed, Unknown }

// Must be usable by a separately deployed process without any media-worker state.
public interface ICallTerminator
{
    Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken);
}

public sealed class ProviderException(string code, bool mayHaveDispatched = false)
    : Exception(code)
{
    public string Code { get; } = code;
    public bool MayHaveDispatched { get; } = mayHaveDispatched;
}
