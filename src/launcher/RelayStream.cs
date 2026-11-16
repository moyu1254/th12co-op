using System.Diagnostics;
using System.Net.Security;
using System.Net.WebSockets;

namespace TH12Coop;

// SslStream supplies ciphertext here. The writer also serializes transport heartbeats.
internal sealed class RelayStream : Stream
{
    private static readonly object closeGate = new();
    private static int pendingCloseOutputs;

    private static void BeginCloseOutput()
    {
        lock (closeGate) pendingCloseOutputs++;
    }

    private static void EndCloseOutput()
    {
        lock (closeGate)
            if (--pendingCloseOutputs == 0) Monitor.PulseAll(closeGate);
    }

    internal static bool WaitForCloseOutput(TimeSpan timeout)
    {
        var start = Stopwatch.GetTimestamp();
        lock (closeGate)
        {
            while (pendingCloseOutputs != 0)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero) return false;
                Monitor.Wait(closeGate, remaining);
            }
            return true;
        }
    }
    private readonly RelayMessages messages = new();
    private readonly WebSocket socket;
    private readonly SemaphoreSlim reader = new(1, 1);
    private readonly SemaphoreSlim writer = new(1, 1);
    private PeriodicTimer? heartbeat;
    private Task<ReadOnlyMemory<byte>>? pending;
    private ReadOnlyMemory<byte> remaining;
    private bool eof;
    private int disposed;

    internal RelayStream(WebSocket socket)
    {
        this.socket = socket;
        if (socket.SubProtocol == RelayMessages.HeartbeatProtocol)
        {
            heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(10));
            _ = SendHeartbeats(heartbeat);
        }
    }

    private async Task SendHeartbeats(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                // An admitted ciphertext write already provides transport activity.
                if (!writer.Wait(0)) continue;
                try
                {
                    if (Volatile.Read(ref heartbeat) == null || socket.State != WebSocketState.Open) return;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, deadline.Token)
                        .ConfigureAwait(false);
                }
                finally { writer.Release(); }
            }
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or InvalidOperationException)
        { socket.Abort(); }
        finally { timer.Dispose(); }
    }

    private void StopHeartbeats() => Interlocked.Exchange(ref heartbeat, null)?.Dispose();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(!CanRead, this);
        await reader.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(!CanRead, this);
            token.ThrowIfCancellationRequested();
            if (eof || buffer.IsEmpty) return 0;
            if (remaining.IsEmpty)
            {
                remaining = await ReadMessage(token).ConfigureAwait(false);
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
        finally { reader.Release(); }
    }

    // Cancelling a ManagedWebSocket receive aborts both halves of the socket.
    // Cancel only the caller's wait and keep the one physical read (including
    // any partial message) for the next reader or the closing handshake.
    private async ValueTask<ReadOnlyMemory<byte>> ReadMessage(CancellationToken token)
    {
        pending ??= messages.Read(socket, CancellationToken.None).AsTask();
        var payload = await pending.WaitAsync(token).ConfigureAwait(false);
        pending = null;
        return payload;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(!CanWrite, this);
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(!CanWrite, this);
            token.ThrowIfCancellationRequested();
            // After admission SslStream owns this buffer until our write completes.
            // Finish every chunk before another write or the close can overtake it.
            // Cancelling ManagedWebSocket.SendAsync would abort the receive half too.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, RelayMessages.MaxBytes);
                await socket.SendAsync(buffer[..count], WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
                buffer = buffer[count..];
            }
        }
        finally { writer.Release(); }
    }

    private async Task DrainWriter()
    {
        await writer.WaitAsync().ConfigureAwait(false);
        writer.Release();
    }

    // A TLS close_notify follows the final record before we close the outer WSS.
    // ShutdownAsync has no token; aborting the socket bounds a stalled final write.
    internal void FinishTls(SslStream tls) => _ = ShutdownTls(tls);

    private async Task ShutdownTls(SslStream tls)
    {
        BeginCloseOutput();
        try
        {
            StopHeartbeats();
            await DrainWriter().ConfigureAwait(false);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var interrupt = limit.Token.Register(socket.Abort);
            await tls.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or WebSocketException or InvalidOperationException or OperationCanceledException) { }
        finally
        {
            // Disposing the TLS stream starts its outer CloseOutput before this
            // registration ends, so process exit observes both writes as one chain.
            try { tls.Dispose(); }
            finally { EndCloseOutput(); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            StopHeartbeats();
            _ = CloseSocket();
        }
        base.Dispose(disposing);
    }

    private async Task CloseSocket()
    {
        try
        {
            BeginCloseOutput();
            try
            {
                await DrainWriter().ConfigureAwait(false);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException or IOException)
            { socket.Abort(); }
            finally { EndCloseOutput(); }

            // Let the existing reader finish before consuming the reverse path.
            // Even when its caller cancelled, observe the retained physical read.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var interrupt = grace.Token.Register(socket.Abort);
            await reader.WaitAsync().ConfigureAwait(false);
            try
            {
                while (pending != null || socket.State == WebSocketState.CloseSent)
                    if ((await ReadMessage(CancellationToken.None).ConfigureAwait(false)).IsEmpty) break;
            }
            finally { reader.Release(); }
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or InvalidOperationException) { }
        finally { socket.Dispose(); }
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
