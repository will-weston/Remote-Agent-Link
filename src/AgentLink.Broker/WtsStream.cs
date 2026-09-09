using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentLink.Broker;

internal sealed class WtsStream : Stream
{
    private readonly WtsHandle _handle;
    private readonly SemaphoreSlim _read = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _closed = new();

    public WtsStream()
    {
        var handle = Native.RAL_Open("RALink.v1", out var error);
        if (handle == IntPtr.Zero)
            throw new Win32Exception((int)error, "Could not open the remote RDP dynamic channel.");
        _handle = new WtsHandle(handle);
    }

    public override bool CanRead => !_closed.IsCancellationRequested;
    public override bool CanWrite => !_closed.IsCancellationRequested;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        await _read.WaitAsync(linked.Token);
        try
        {
            var temporary = new byte[Math.Min(buffer.Length, 65536)];
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var read = await Task.Run(() =>
                {
                    var error = Native.RAL_Read(_handle, temporary, (uint)temporary.Length, 100, out var count);
                    if (error != 0)
                        throw new IOException("RDP channel read failed.", new Win32Exception((int)error));
                    if (count > temporary.Length)
                        throw new IOException("Native transport returned an invalid byte count.");
                    return (int)count;
                }, linked.Token);
                if (read != 0)
                {
                    temporary.AsMemory(0, read).CopyTo(buffer);
                    return read;
                }
            }
        }
        finally { _read.Release(); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        await _write.WaitAsync(linked.Token);
        try
        {
            while (!buffer.IsEmpty)
            {
                linked.Token.ThrowIfCancellationRequested();
                var chunk = buffer[..Math.Min(16384, buffer.Length)].ToArray();
                await Task.Run(() =>
                {
                    var error = Native.RAL_Write(_handle, chunk, (uint)chunk.Length, out var written);
                    if (error != 0 || written != chunk.Length)
                        throw new IOException("RDP channel write did not complete.", new Win32Exception((int)error));
                }, linked.Token);
                buffer = buffer[chunk.Length..];
            }
        }
        finally { _write.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed.IsCancellationRequested)
        {
            _closed.Cancel();
            // P/Invoke retains this SafeHandle until all native reads/writes
            // return; RAL_Close is not called concurrently with those operations.
            _handle.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class WtsHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WtsHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() { Native.RAL_Close(handle); return true; }
    }

    private static class Native
    {
        [DllImport("AgentLink.Transport.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr RAL_Open(string channelName, out uint error);
        [DllImport("AgentLink.Transport.dll", ExactSpelling = true)]
        public static extern uint RAL_Read(WtsHandle context, [Out] byte[] buffer, uint capacity, uint timeoutMs, out uint bytesRead);
        [DllImport("AgentLink.Transport.dll", ExactSpelling = true)]
        public static extern uint RAL_Write(WtsHandle context, byte[] buffer, uint length, out uint bytesWritten);
        [DllImport("AgentLink.Transport.dll", ExactSpelling = true)]
        public static extern void RAL_Close(IntPtr context);
    }
}
