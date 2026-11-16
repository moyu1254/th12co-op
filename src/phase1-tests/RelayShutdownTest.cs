using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace TH12Coop.Phase1Tests;

internal static class RelayShutdownTest
{
    // Simulate a slow close_notify write; the outer WebSockets below are real.
    private sealed class SlowShutdown(Stream transport) : SslStream(transport)
    {
        public override Task ShutdownAsync() => Task.Delay(TimeSpan.FromMilliseconds(650));
    }

    internal static async Task Run(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var outgoing = WebSocket.CreateFromStream(client.GetStream(), false, null, Timeout.InfiniteTimeSpan);
        using var incoming = WebSocket.CreateFromStream(peer.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        using var transport = new RelayStream(outgoing);
        using var tls = new SlowShutdown(transport);
        var shutdown = Task.Run(() => transport.FinishTls(tls), token);
        var close = await incoming.ReceiveAsync(new byte[1].AsMemory(), token);
        if (close.MessageType != WebSocketMessageType.Close) throw new IOException("Expected outer WebSocket close");
        // Each operation fits its one-second budget, their sum exceeds one second.
        await Task.Delay(TimeSpan.FromMilliseconds(650), token);
        await incoming.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
        await shutdown;
        if (outgoing.State != WebSocketState.Closed || outgoing.CloseStatus != WebSocketCloseStatus.NormalClosure)
            throw new IOException("TLS shutdown deadline interrupted the WebSocket close handshake");
        Console.WriteLine("PASS slow TLS shutdown and delayed WebSocket close acknowledgement have separate deadlines");
    }
}
