using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.VoiceLive;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal interface IVoiceTransport : IAsyncDisposable
{
    Task SendAsync(JsonObject message, CancellationToken cancellationToken);
    IAsyncEnumerable<JsonObject> ReadAsync(CancellationToken cancellationToken);
}

internal interface IVoiceTransportFactory
{
    Task<IVoiceTransport> ConnectAsync(CancellationToken cancellationToken);
}

internal sealed class VoiceLiveTransportFactory(AzureOptions options, AzureCredentials credentials) : IVoiceTransportFactory
{
    public async Task<IVoiceTransport> ConnectAsync(CancellationToken cancellationToken)
    {
        var endpoint = AzureValidation.Endpoint(options.VoiceLive.Endpoint, "services.ai.azure.com", "cognitiveservices.azure.com");
        if (options.VoiceLive.ApiVersion != AzureValidation.VoiceApiVersion)
            throw new ProviderException("VOICE_API_VERSION_UNSUPPORTED");
        var clientOptions = new VoiceLiveClientOptions(VoiceLiveClientOptions.ServiceVersion.V2026_07_15);
        clientOptions.Diagnostics.IsLoggingEnabled = false;
        clientOptions.Diagnostics.IsLoggingContentEnabled = false;
        clientOptions.Diagnostics.IsDistributedTracingEnabled = false;
        clientOptions.Retry.MaxRetries = 0;
        var client = new VoiceLiveClient(endpoint, credentials.Get(), clientOptions);
        try
        {
            return new VoiceLiveTransport(await client.StartSessionAsync(options.VoiceLive.Model, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw new ProviderException("VOICE_CONNECTION_FAILED");
        }
    }
}

internal sealed class VoiceLiveTransport(VoiceLiveSession session) : IVoiceTransport
{
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.SendCommandAsync(BinaryData.FromString(message.ToJsonString()), cancellationToken).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }

    public async IAsyncEnumerable<JsonObject> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Use the SDK's raw protocol surface, not its opt-in GenAI content telemetry.
        await foreach (var update in session.ReceiveUpdatesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (update.ToMemory().Length > 512 * 1024)
                throw new ProviderException("VOICE_MESSAGE_TOO_LARGE");
            JsonObject message;
            try { message = JsonNode.Parse(update.ToMemory().Span) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new ProviderException("VOICE_PROTOCOL_INVALID"); }
            yield return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        // The SDK's asynchronous graceful close has no cancellation token. Abort
        // disposal cannot wait indefinitely for a failed voice peer to acknowledge.
        session.Dispose();
        return ValueTask.CompletedTask;
    }
}
