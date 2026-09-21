using Azure;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal interface ICallAutomationTransport
{
    Task<ProviderHandle> DialAsync(CallContext context, CancellationToken cancellationToken);
    Task SendDtmfAsync(string connectionId, string recipient, string callId, string digits, CancellationToken cancellationToken);
    Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken);
}

internal sealed class CallAutomationTransport(
    AzureOptions options, AzureCredentials credentials, TimeProvider clock, CallAutomationClient? sdkClient = null) : ICallAutomationTransport
{
    private CallAutomationClient? _client = sdkClient;

    private CallAutomationClient Client
    {
        get
        {
            var endpoint = AzureValidation.Endpoint(options.CallAutomation.Endpoint, "communication.azure.com");
            var clientOptions = new CallAutomationClientOptions(CallAutomationClientOptions.ServiceVersion.V2026_03_12);
            clientOptions.Retry.MaxRetries = 0; // An ambiguous create must never automatically redial.
            clientOptions.Diagnostics.IsLoggingEnabled = false;
            clientOptions.Diagnostics.IsLoggingContentEnabled = false;
            clientOptions.Diagnostics.IsDistributedTracingEnabled = false;
            return _client ??= new CallAutomationClient(endpoint, credentials.Get(), clientOptions);
        }
    }

    internal static CreateCallOptions BuildCreateOptions(CallContext context, CallAutomationOptions options)
    {
        var recipient = AzureValidation.PstnTarget(context.Target);
        if (!Guid.TryParse(options.ResourceAccountObjectId, out _) || !AzureValidation.IsPhone(options.TeamsServiceNumber))
            throw new ProviderException("TPE_SOURCE_NOT_CONFIGURED");
        if (!AzureValidation.IsCallId(context.CallId))
            throw new ProviderException("INVALID_CALL_ID");
        var baseUri = AzureValidation.Endpoint(options.PublicBaseUrl);
        var mediaUri = new UriBuilder(new Uri(baseUri, $"azure/media/{context.CallId}")) { Scheme = "wss", Port = -1 };
        return new CreateCallOptions(
            new CallInvite(new PhoneNumberIdentifier(recipient), null),
            new Uri(baseUri, $"azure/callbacks/{context.CallId}"))
        {
            TeamsAppSource = new MicrosoftTeamsAppIdentifier(options.ResourceAccountObjectId),
            OperationContext = context.CallId,
            MediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Unmixed)
            {
                TransportUri = mediaUri.Uri,
                AudioFormat = AudioFormat.Pcm24KMono,
                EnableBidirectional = true,
                EnableDtmfTones = false,
                StartMediaStreaming = true
            }
        };
    }

    public async Task<ProviderHandle> DialAsync(CallContext context, CancellationToken cancellationToken)
    {
        var request = BuildCreateOptions(context, options.CallAutomation);
        var client = Client;
        AzureValidation.Deadline(context, clock);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await client.CreateCallAsync(request, cancellationToken).ConfigureAwait(false);
            var properties = result.Value.CallConnectionProperties;
            if (string.IsNullOrEmpty(properties.CallConnectionId))
                throw new ProviderException("PROVIDER_DISPATCH_UNKNOWN", true);
            if (properties.SourceCallerIdNumber is { } caller &&
                caller.PhoneNumber != options.CallAutomation.TeamsServiceNumber)
            {
                using var terminationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await TerminateAsync(properties.CallConnectionId, terminationTimeout.Token).ConfigureAwait(false);
                throw new ProviderException("TPE_SOURCE_MISMATCH", true);
            }
            return new ProviderHandle(properties.CallConnectionId, properties.ServerCallId);
        }
        catch (ProviderException) { throw; }
        catch (Exception)
        {
            throw new ProviderException("PROVIDER_DISPATCH_UNKNOWN", true);
        }
    }

    internal static IReadOnlyList<DtmfTone> ParseTones(string digits)
    {
        if (digits.Length is < 1 or > 32)
            throw new ProviderException("INVALID_DTMF");
        return digits.Select(c => c switch
        {
            '0' => DtmfTone.Zero,
            '1' => DtmfTone.One,
            '2' => DtmfTone.Two,
            '3' => DtmfTone.Three,
            '4' => DtmfTone.Four,
            '5' => DtmfTone.Five,
            '6' => DtmfTone.Six,
            '7' => DtmfTone.Seven,
            '8' => DtmfTone.Eight,
            '9' => DtmfTone.Nine,
            '*' => DtmfTone.Asterisk,
            '#' => DtmfTone.Pound,
            _ => throw new ProviderException("INVALID_DTMF")
        }).ToArray();
    }

    public async Task SendDtmfAsync(string connectionId, string recipient, string callId, string digits, CancellationToken cancellationToken)
    {
        var request = new SendDtmfTonesOptions(ParseTones(digits), new PhoneNumberIdentifier(recipient))
        {
            OperationContext = callId
        };
        try
        {
            await Client.GetCallConnection(connectionId).GetCallMedia()
                .SendDtmfTonesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException) { throw; }
        catch (Exception)
        {
            throw new ProviderException("DTMF_DELIVERY_UNKNOWN", true);
        }
    }

    public async Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 8192)
            throw new ProviderException("INVALID_PROVIDER_HANDLE");
        try
        {
            await Client.GetCallConnection(connectionId).HangUpAsync(forEveryone: true, cancellationToken).ConfigureAwait(false);
            return TerminationEvidence.Pending;
        }
        catch (Exception)
        {
            // Not even a 404 establishes whether a carrier leg has ended.
            return TerminationEvidence.Unknown;
        }
    }
}

internal sealed class AzureCallTerminator(ICallAutomationTransport transport) : ICallTerminator
{
    public Task<TerminationEvidence> TerminateAsync(string connectionId, CancellationToken cancellationToken) =>
        transport.TerminateAsync(connectionId, cancellationToken);
}
