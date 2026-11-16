using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace TH12Coop.Phase1Tests;

internal static class RelayShutdownTest
{
    // Hold the close_notify write until the caller returns. The outer WebSockets are real.
    private sealed class GatedShutdown(Stream transport) : SslStream(transport)
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task ShutdownAsync()
        {
            Started.TrySetResult();
            return Written.Task;
        }
    }

    private enum Reader { None, Pending, Cancelled, Stalled }

    internal static async Task Run(CancellationToken token)
    {
        await Task.WhenAll(Enum.GetValues<Reader>().Select(reader => DelayedPeer(reader, token)).Append(CancelWrite(token)));
        Console.WriteLine("PASS relay close returns before the peer replies, drains pending and cancelled reads, and releases a stalled socket");
    }

    // Hold the real WebSocket's first network write after it has accepted the ciphertext.
    private sealed class GatedWrite(Socket socket) : NetworkStream(socket, ownsSocket: false)
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Writable = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Writable.Task.WaitAsync(token);
            await base.WriteAsync(buffer, token);
        }
    }

    private static async Task CancelWrite(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var network = new GatedWrite(client.Client);
        using var outgoing = WebSocket.CreateFromStream(network, false, null, Timeout.InfiniteTimeSpan);
        using var incoming = WebSocket.CreateFromStream(peer.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        using var transport = new RelayStream(outgoing);
        using var tls = new GatedShutdown(transport);
        using var app = CancellationTokenSource.CreateLinkedTokenSource(token);
        var data = new byte[RelayMessages.MaxBytes + 7];
        Random.Shared.NextBytes(data);
        var writing = transport.WriteAsync(data.AsMemory(), app.Token).AsTask();
        await network.Started.Task.WaitAsync(token);
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(token);
        var next = transport.WriteAsync(new byte[1].AsMemory(), queued.Token).AsTask();
        queued.Cancel();
        try { await next.WaitAsync(TimeSpan.FromSeconds(3), token); throw new IOException("Queued write ignored cancellation"); }
        catch (OperationCanceledException) when (queued.IsCancellationRequested) { }
        app.Cancel();
        transport.FinishTls(tls);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
            if (writing.IsCompleted || tls.Started.Task.IsCompleted)
                throw new IOException("Cancelling an admitted TLS write released its buffer or started shutdown mid-record");
            network.Writable.TrySetResult();
            await writing.WaitAsync(token);
            await tls.Started.Task.WaitAsync(token);
        }
        finally { network.Writable.TrySetResult(); tls.Written.TrySetResult(); }
        var messages = new RelayMessages();
        var first = await messages.Read(incoming, token);
        if (!first.Span.SequenceEqual(data.AsSpan(0, RelayMessages.MaxBytes))) throw new IOException("Missing first ciphertext chunk");
        var last = await messages.Read(incoming, token);
        if (!last.Span.SequenceEqual(data.AsSpan(RelayMessages.MaxBytes))) throw new IOException("Missing final ciphertext chunk");
        if (!(await messages.Read(incoming, token)).IsEmpty) throw new IOException("Expected close after the complete write");
        await incoming.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
        Console.WriteLine("PASS queued cancellation and in-flight cancellation preserve the admitted ciphertext through close");
    }

    internal static async Task ProcessExit(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var raw = new TcpClient(AddressFamily.InterNetwork);
        using var secure = new TcpClient(AddressFamily.InterNetwork);
        await raw.ConnectAsync(endpoint, token);
        using var rawPeer = await listener.AcceptTcpClientAsync(token);
        await secure.ConnectAsync(endpoint, token);
        using var securePeer = await listener.AcceptTcpClientAsync(token);
        using var rawWriter = new GatedWrite(raw.Client);
        using var secureWriter = new GatedWrite(secure.Client);
        using var rawSocket = WebSocket.CreateFromStream(rawWriter, false, null, Timeout.InfiniteTimeSpan);
        using var secureSocket = WebSocket.CreateFromStream(secureWriter, false, null, Timeout.InfiniteTimeSpan);
        using var rawReader = WebSocket.CreateFromStream(rawPeer.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        using var secureReader = WebSocket.CreateFromStream(securePeer.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        using var rawStream = new RelayStream(rawSocket);
        using var secureStream = new RelayStream(secureSocket);
        using var tls = new GatedShutdown(secureStream);
        try
        {
            rawStream.Dispose(); // A peer TLS handshake can fail before a channel exists.
            secureStream.FinishTls(tls);
            await Task.WhenAll(tls.Started.Task, rawWriter.Started.Task).WaitAsync(token);
            if (RelayStream.WaitForCloseOutput(TimeSpan.FromMilliseconds(50)))
                throw new IOException("Process exit ignored pending TLS and WSS output");
            var exiting = Task.Run(() => RelayStream.WaitForCloseOutput(TimeSpan.FromSeconds(3)), token);
            rawWriter.Writable.SetResult();
            if ((await rawReader.ReceiveAsync(new byte[1].AsMemory(), token)).MessageType != WebSocketMessageType.Close)
                throw new IOException("Expected the unauthenticated connection's Close");
            if (RelayStream.WaitForCloseOutput(TimeSpan.Zero))
                throw new IOException("Process exit ignored the other connection's TLS shutdown");
            tls.Written.SetResult();
            await secureWriter.Started.Task.WaitAsync(token);
            if (RelayStream.WaitForCloseOutput(TimeSpan.Zero))
                throw new IOException("Lost pending output during the handoff from TLS to WSS");
            secureWriter.Writable.SetResult();
            if (!await exiting || (await secureReader.ReceiveAsync(new byte[1].AsMemory(), token)).MessageType != WebSocketMessageType.Close)
                throw new IOException("Process exit did not finish sending Close before a peer reply");
        }
        finally
        {
            rawWriter.Writable.TrySetResult();
            tls.Written.TrySetResult();
            secureWriter.Writable.TrySetResult();
        }
        await Task.WhenAll(rawReader.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token),
            secureReader.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token));
        Console.WriteLine("PASS process exit waits for all local TLS/WSS close output under one deadline without waiting for peer replies");
    }

    private static async Task DelayedPeer(Reader reader, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var outgoing = WebSocket.CreateFromStream(client.GetStream(), false, null, Timeout.InfiniteTimeSpan);
        using var incoming = WebSocket.CreateFromStream(peer.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        using var transport = new RelayStream(outgoing);
        using var tls = new GatedShutdown(transport);
        await incoming.SendAsync(new byte[] { 23 }.AsMemory(), WebSocketMessageType.Binary, false, token);
        using var cancelRead = CancellationTokenSource.CreateLinkedTokenSource(token);
        byte[] buffer = new byte[2];
        var pending = reader == Reader.None ? null : transport.ReadAsync(buffer.AsMemory(), cancelRead.Token).AsTask();
        if (reader == Reader.Cancelled)
        {
            cancelRead.Cancel();
            try { await pending!; throw new IOException("Cancellation failed to release the application reader"); }
            catch (OperationCanceledException) when (cancelRead.IsCancellationRequested) { }
        }
        var shutdown = Task.Run(() => transport.FinishTls(tls), token);
        await tls.Started.Task.WaitAsync(token);
        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(3), token); }
        finally { tls.Written.TrySetResult(); }
        var close = await incoming.ReceiveAsync(new byte[1].AsMemory(), token);
        if (close.MessageType != WebSocketMessageType.Close) throw new IOException("Expected outer WebSocket close");
        // Hold the reverse close until Dispose returns. Neither the TLS write
        // deadline nor the Close output deadline may abort the receive half.
        while (outgoing.State == WebSocketState.Open) await Task.Delay(10, token);
        if (outgoing.State != WebSocketState.CloseSent) throw new IOException("Sender discarded the socket before the peer replied");
        try
        {
            await transport.ReadAsync(new byte[1].AsMemory(), token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
            throw new InvalidOperationException("Closed transport accepted a new reader");
        }
        catch (ObjectDisposedException) { }
        if (reader == Reader.Stalled)
        {
            try
            {
                await pending!.WaitAsync(TimeSpan.FromSeconds(48), token);
                throw new InvalidOperationException("A silent peer completed a partial message");
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is WebSocketException or IOException or OperationCanceledException) { }
            if (outgoing.State != WebSocketState.Aborted) throw new IOException("Silent peer was not released at the drain deadline");
            return;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
        await incoming.SendAsync(new byte[] { 24 }.AsMemory(), WebSocketMessageType.Binary, true, token);
        if (reader == Reader.Pending && (await pending! != 2 || !buffer.SequenceEqual(new byte[] { 23, 24 })))
            throw new IOException("The existing reader lost the reverse message");
        await incoming.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
        while (outgoing.State == WebSocketState.CloseSent) await Task.Delay(10, token);
        if (outgoing.State != WebSocketState.Closed || outgoing.CloseStatus != WebSocketCloseStatus.NormalClosure)
            throw new IOException("The delayed WebSocket close was not acknowledged");
    }
}
