using System.Net.Security;
using System.Net.WebSockets;

namespace TH12Coop;

// SslStream supplies ciphertext here. Each direction has a single caller.
internal sealed class RelayStream(WebSocket socket) : Stream
{
    private readonly RelayMessages messages = new();
    private ReadOnlyMemory<byte> remaining;
    private bool eof;
    private int disposed, reading;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        Interlocked.Increment(ref reading);
        try
        {
            ObjectDisposedException.ThrowIf(!CanRead, this);
            token.ThrowIfCancellationRequested();
            if (eof || buffer.IsEmpty) return 0;
            if (remaining.IsEmpty)
            {
                remaining = await messages.Read(socket, token).ConfigureAwait(false);
                if (remaining.IsEmpty)
                {
                    eof = true;
                    return 0;
                }
            }
            int count = Math.Min(remaining.Length, buffer.Length);
            remaining[..count].CopyTo(buffer);
            remaining = remaining[count..];
            return count;
        }
        finally { Interlocked.Decrement(ref reading); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(!CanWrite, this);
        token.ThrowIfCancellationRequested();
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(buffer.Length, RelayMessages.MaxBytes);
            await socket.SendAsync(buffer[..count], WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            buffer = buffer[count..];
        }
    }

    // A TLS close_notify follows the final record before we close the outer WSS.
    // ShutdownAsync has no token; aborting the socket bounds a stalled final write.
    internal void FinishTls(SslStream tls)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var interrupt = limit.Token.Register(socket.Abort);
            tls.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or WebSocketException or InvalidOperationException or OperationCanceledException) { }
        finally { tls.Dispose(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Give the peer time to reply to the close. Once disposed is set no
                    // new reader can reach the socket. An existing reader owns that
                    // direction, so use a write-only close in that case.
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    var close = Volatile.Read(ref reading) == 0
                        ? socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token)
                        : socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token);
                    close.GetAwaiter().GetResult();
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { socket.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => Volatile.Read(ref disposed) == 0;
    public override bool CanWrite => Volatile.Read(ref disposed) == 0;
    public override bool CanSeek => false;
    public override void Flush() { }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
