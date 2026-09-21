using System.Security.Cryptography;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed record AudioFrame(byte[] Bytes, DateTimeOffset QueuedAt, string? ResponseId = null, string? ItemId = null, int ContentIndex = 0);

internal sealed class AudioBuffer(TimeProvider clock) : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<AudioFrame> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private int _reservedBytes;
    private bool _disposed;

    internal int ReservedBytes { get { lock (_gate) return _reservedBytes; } }

    internal void Enqueue(ReadOnlySpan<byte> bytes, string? responseId = null, string? itemId = null, int contentIndex = 0)
    {
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
            throw new ProviderException("MEDIA_FORMAT_INVALID");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reservedBytes + bytes.Length > AzureValidation.MaximumAudioBytes)
                throw new ProviderException("MEDIA_BACKLOG_EXCEEDED");
            var now = clock.GetUtcNow();
            // Twenty milliseconds per packet; the reservation includes the frame being sent.
            for (var offset = 0; offset < bytes.Length; offset += 960)
            {
                var frame = bytes.Slice(offset, Math.Min(960, bytes.Length - offset)).ToArray();
                _queue.Enqueue(new AudioFrame(frame, now, responseId, itemId, contentIndex));
                _reservedBytes += frame.Length;
                _available.Release();
            }
        }
    }

    internal async Task<AudioFrame> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_queue.TryDequeue(out var frame))
                    continue;
                if (clock.GetUtcNow() - frame.QueuedAt > TimeSpan.FromSeconds(2))
                {
                    _reservedBytes -= frame.Bytes.Length;
                    CryptographicOperations.ZeroMemory(frame.Bytes);
                    throw new ProviderException("MEDIA_BACKLOG_STALE");
                }
                return frame;
            }
        }
    }

    internal void Release(AudioFrame frame)
    {
        lock (_gate)
        {
            _reservedBytes -= frame.Bytes.Length;
            CryptographicOperations.ZeroMemory(frame.Bytes);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var frame))
            {
                _reservedBytes -= frame.Bytes.Length;
                CryptographicOperations.ZeroMemory(frame.Bytes);
            }
            while (_available.Wait(0)) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Clear();
        }
    }
}
