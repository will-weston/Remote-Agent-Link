using System.Buffers.Binary;
using System.Diagnostics;
using AgentLink.Core;

namespace AgentLink.Broker;

// Enforces a small pre-admission allocation bound before Wire.ReadAsync sees a
// header. Normal wire limits apply only after TLS and immutable endpoint hello.
internal sealed class ChannelFrameStream(Stream inner, Func<bool> ready) : Stream
{
    internal const int MaximumPendingFrameBytes = 4096;
    private byte[] _prefix = [];
    private int _prefixOffset;
    private int _remaining;
    private long _window = Stopwatch.GetTimestamp();
    private int _frames;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;
        if (_prefixOffset == _prefix.Length && _remaining == 0)
        {
            var header = new byte[5];
            if (await inner.ReadAsync(header.AsMemory(0, 1), cancellationToken) == 0)
                return 0;
            await inner.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var maximum = header[4] == 2 ? Wire.MaximumMessageBytes : Wire.MaximumRequestBytes;
            var pending = !ready();
            if (header[4] is < 1 or > 3 || length <= 0 || length > (pending ? MaximumPendingFrameBytes : maximum))
                throw new AgentLinkException("invalid_frame", "The channel frame exceeds its admission limits.");
            _prefix = header;
            _prefixOffset = 0;
            _remaining = length;
            if (pending)
            {
                if (Stopwatch.GetElapsedTime(_window) >= TimeSpan.FromSeconds(5))
                {
                    _window = Stopwatch.GetTimestamp();
                    _frames = 0;
                }
                if (++_frames > 32)
                    throw new AgentLinkException("metadata_rate_limit", "Too many pending channel messages.");
                _prefix = new byte[5 + length];
                header.CopyTo(_prefix, 0);
                await inner.ReadExactlyAsync(_prefix.AsMemory(5), cancellationToken);
                using var frame = new MemoryStream(_prefix, false);
                ChannelProtocol.ValidatePendingFrame((await Wire.ReadAsync(frame, cancellationToken))!);
                _remaining = 0;
            }
        }
        if (_prefixOffset < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
            _prefix.AsMemory(_prefixOffset, count).CopyTo(buffer);
            _prefixOffset += count;
            return count;
        }
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], cancellationToken);
        if (read == 0)
            throw new EndOfStreamException("The channel frame was truncated.");
        _remaining -= read;
        return read;
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
