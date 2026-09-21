using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed class DurableCallCorrelation(ControlStore store)
{
    public Task<bool> TryBindAsync(string callId, string connectionId, string? serverCallId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 128
            || string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 4096
            || serverCallId is not null && (string.IsNullOrWhiteSpace(serverCallId) || serverCallId.Length > 4096))
            return Task.FromResult(false);
        return store.TransactionAsync(async tx =>
        {
            var call = await tx.CallAsync(callId);
            if (call is null || call.State.ProviderMode != "azure"
                || call.DispatchStatus is not ("intent" or "returned" or "ambiguous"))
                return false;
            if (call.Handle is { } handle && (handle.ConnectionId != connectionId
                || handle.ServerCallId is not null && serverCallId is not null && handle.ServerCallId != serverCallId))
                return false;
            if (await tx.HandleBoundElsewhereAsync(callId, connectionId, serverCallId))
                return false;
            // Binding authenticates correlation, not ownership or permission to resume a call.
            // Expired/revoked/fenced calls must remain bindable so late callbacks can be terminated.
            call.Handle = new ProviderHandle(connectionId, serverCallId ?? call.Handle?.ServerCallId);
            await tx.SaveCallAsync(call);
            return true;
        }, cancellationToken);
    }
}
