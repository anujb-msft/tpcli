using Tpcli.Azure;
using Tpcli.Core;

namespace Tpcli.Runtime;

internal sealed class AzureDurableCorrelation(DurableCallCorrelation correlation) : IAzureCallCorrelation
{
    public Task<bool> TryBindAsync(string callId, string connectionId, string? serverCallId, CancellationToken cancellationToken) =>
        correlation.TryBindAsync(callId, connectionId, serverCallId, cancellationToken);
}
