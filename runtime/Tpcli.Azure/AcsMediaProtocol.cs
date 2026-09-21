using System.Net.WebSockets;
using System.Text;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed class AcsMediaProtocol(string recipient, TimeProvider? clock = null)
{
    private readonly PhoneNumberIdentifier _recipient = new(recipient);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset? _lastAudioTimestamp;
    internal bool MetadataReceived { get; private set; }

    internal ReadOnlyMemory<byte>? Read(string message)
    {
        StreamingData data;
        try { data = StreamingData.Parse(message); }
        catch (Exception) { throw new ProviderException("MEDIA_PROTOCOL_INVALID"); }
        switch (data)
        {
            case AudioMetadata metadata:
                if (MetadataReceived || metadata.Encoding != "PCM" ||
                    metadata.SampleRate != 24_000 || metadata.Channels != AudioChannel.Mono)
                    throw new ProviderException("MEDIA_FORMAT_UNSUPPORTED");
                MetadataReceived = true;
                return null;
            case AudioData audio:
                if (!MetadataReceived)
                    throw new ProviderException("MEDIA_METADATA_REQUIRED");
                if (audio.Data.Length == 0 || audio.Data.Length % 2 != 0 ||
                    audio.Data.Length > AzureValidation.MaximumAudioBytes)
                    throw new ProviderException("MEDIA_FORMAT_INVALID");
                // Unmixed streams identify their origin. A mixed/anonymous frame is never trusted.
                if (audio.Participant is null)
                    throw new ProviderException("MEDIA_PARTICIPANT_MISSING");
                if (!_recipient.Equals(audio.Participant)) return null;
                if (audio.Timestamp < _clock.GetUtcNow().AddSeconds(-2) ||
                    audio.Timestamp > _clock.GetUtcNow().AddSeconds(30))
                    throw new ProviderException("MEDIA_TIMESTAMP_STALE");
                if (_lastAudioTimestamp is { } last && audio.Timestamp <= last)
                    throw new ProviderException("MEDIA_TIMESTAMP_REPLAY");
                _lastAudioTimestamp = audio.Timestamp;
                return audio.Data;
            case DtmfData:
                return null;
            default:
                throw new ProviderException("MEDIA_PROTOCOL_UNSUPPORTED");
        }
    }

    internal static byte[] Outgoing(byte[] pcm) =>
        Encoding.UTF8.GetBytes(OutStreamingData.GetAudioDataForOutbound(pcm));

    internal static byte[] StopAudio() =>
        Encoding.UTF8.GetBytes(OutStreamingData.GetStopAudioForOutbound());

    internal static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 256 * 1024)
                throw new ProviderException("MEDIA_PROTOCOL_INVALID");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
}
