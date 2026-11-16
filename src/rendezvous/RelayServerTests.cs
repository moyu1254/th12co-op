using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace TH12Coop.Rendezvous;

internal static class RelayServerTests
{
    private sealed record Peers(Registration Room, Claim Guest, VerifiedIce Ice);

    private sealed class Fixture(HttpClient client, string invite, CancellationToken ct)
    {
        internal async Task<Peers> Room(bool answered = true)
        {
            var ice = new VerifiedIce(IceDescription.NoDirect, []);
            using var create = await client.PostAsJsonAsync("/v3/rooms/create", new { invite }, ct);
            var room = (await create.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Registration>(ct))!;
            using var join = await client.PostAsJsonAsync("/v3/rooms/join", new { room.Number, offer = ice }, ct);
            var claim = (await join.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Claim>(ct))!;
            var peers = new Peers(room, claim, ice);
            if (answered) await Answer(peers);
            return peers;
        }

        internal async Task Answer(Peers peers)
        {
            using var answer = await client.PostAsJsonAsync("/v3/rooms/answer", new { peers.Room.Number, peers.Room.OwnerToken, peers.Ice }, ct);
            answer.EnsureSuccessStatusCode();
        }

        internal async Task CancelGuestSetup(Peers peers)
        {
            using var response = await client.PostAsJsonAsync("/v3/rooms/close", new { peers.Room.Number, peers.Guest.GuestToken }, ct);
            response.EnsureSuccessStatusCode();
            // An already paired room retains its signaling and the owner's control.
            using var receive = await client.PostAsJsonAsync("/v3/rooms/receive", new { peers.Room.Number, peers.Guest.GuestToken }, ct);
            receive.EnsureSuccessStatusCode();
        }

        internal Task Connect(ClientWebSocket socket, Peers peers, bool host, string? key = null, CancellationToken? cancel = null)
        {
            socket.Options.SetRequestHeader("X-TH12-Room", peers.Room.Number);
            socket.Options.SetRequestHeader("Authorization", "Bearer " + (key ?? (host ? peers.Room.OwnerToken : peers.Guest.GuestToken)));
            socket.Options.CollectHttpResponseDetails = true;
            var endpoint = new UriBuilder(client.BaseAddress!) { Scheme = "wss", Path = host ? "/v3/relay/host" : "/v3/relay/guest" };
            return socket.ConnectAsync(endpoint.Uri, client, cancel ?? ct);
        }

        internal async Task Denied(Peers peers, bool host, HttpStatusCode status, string? key = null)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await Connect(socket, peers, host, key);
                throw new InvalidOperationException("Relay admitted an ineligible participant");
            }
            catch (WebSocketException) when (socket.HttpStatusCode == status) { }
        }

        // Both clients can observe EOF before Kestrel has unwound both request handlers.
        // A 503 is retryable while that pair still owns the single capacity slot.
        internal async Task<ClientWebSocket> AfterCleanup(Peers peers, bool host)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(3));
            for (var attempt = 0; ; attempt++)
            {
                var socket = new ClientWebSocket();
                try
                {
                    await Connect(socket, peers, host, cancel: window.Token);
                    return socket;
                }
                catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.ServiceUnavailable && attempt < 5)
                {
                    socket.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(100), window.Token);
                }
                catch { socket.Dispose(); throw; }
            }
        }
    }

    internal static async Task Run(string invite)
    {
        using var cert = ServerTests.TestCertificate();
        async Task Serve(RelayLimits limits, Func<Fixture, CancellationToken, Task> test)
        {
            await using var app = Program.Build(cert, new(), IPAddress.Loopback, 0, TimeProvider.System, relayLimits: limits);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await app.StartAsync(lifetime.Token);
            try
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false, UseProxy = false, SslProtocols = SslProtocols.Tls13,
                    ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(cert.RawData)
                };
                using var client = new HttpClient(handler) { BaseAddress = new(app.Urls.Single()) };
                await test(new(client, invite, lifetime.Token), lifetime.Token);
            }
            finally { await app.StopAsync(); }
        }
        await Serve(RelayLimits.Default with { Pairs = 1, Bytes = 2 * 16384 + 2 }, Bounds);
        await Serve(RelayLimits.Default with { Pairs = 1, Wait = TimeSpan.FromSeconds(2), Lifetime = TimeSpan.FromSeconds(3) }, Expiry);
        await Serve(RelayLimits.Default with { Pairs = 3, Idle = TimeSpan.FromSeconds(2) }, SharedAddress);
    }

    private static async Task Bounds(Fixture fx, CancellationToken ct)
    {
        var first = await fx.Room(answered: false);
        await fx.Denied(first, true, HttpStatusCode.NotFound);
        await fx.Answer(first);
        var second = await fx.Room();
        await fx.Denied(first, true, HttpStatusCode.NotFound, first.Guest.GuestToken);
        await fx.Denied(first, false, HttpStatusCode.NotFound, first.Room.OwnerToken);
        using var host = new ClientWebSocket();
        using var guest = new ClientWebSocket();
        host.Options.AddSubProtocol(RelayMessages.HeartbeatProtocol);
        await Task.WhenAll(fx.Connect(host, first, true), fx.Connect(guest, first, false));
        if (host.SubProtocol != RelayMessages.HeartbeatProtocol || !string.IsNullOrEmpty(guest.SubProtocol))
            throw new InvalidOperationException("Each relay endpoint must negotiate heartbeat support independently");
        await fx.Denied(first, true, HttpStatusCode.NotFound);
        await fx.Denied(second, true, HttpStatusCode.ServiceUnavailable);
        await fx.CancelGuestSetup(first); // Releasing signaling with a guest token must preserve this duplex relay.

        var payload = RandomNumberGenerator.GetBytes(16384);
        // The legacy guest receives only TLS data, even when its peer sends a heartbeat.
        await host.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, ct);
        await host.SendAsync(payload.AsMemory(0, 123), WebSocketMessageType.Binary, false, ct);
        await host.SendAsync(payload.AsMemory(123), WebSocketMessageType.Binary, true, ct);
        await Receive(guest, payload, ct);
        await guest.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, ct);
        await Receive(host, payload, ct);
        byte[] last = [0x13, 0x37];
        await host.SendAsync(last.AsMemory(), WebSocketMessageType.Binary, true, ct);
        await Receive(guest, last, ct);
        await guest.SendAsync(last.AsMemory(0, 1), WebSocketMessageType.Binary, true, ct);
        await Closed(host, ct);
        await Closed(guest, ct);
        await fx.Denied(first, false, HttpStatusCode.NotFound);

        var next = await Task.WhenAll(fx.AfterCleanup(second, true), fx.AfterCleanup(second, false));
        using var h2 = next[0];
        using var g2 = next[1];
        await h2.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, false, ct);
        await h2.SendAsync(last.AsMemory(0, 1), WebSocketMessageType.Binary, true, ct);
        await Closed(g2, ct);  // No prefix of an oversized fragmented message reaches the other side.
        await Closed(h2, ct);
        Console.WriteLine("PASS relay rejects pre-answer, swapped roles, duplicate and replayed sockets; enforces pair, byte and fragmented message limits");

        var final = await fx.Room();
        var sockets = await Task.WhenAll(fx.AfterCleanup(final, true), fx.AfterCleanup(final, false));
        using var sender = sockets[0];
        using var recipient = sockets[1];
        byte[] reply = [0x24, 0x68];
        await recipient.SendAsync(reply.AsMemory(0, 1), WebSocketMessageType.Binary, false, ct);
        await sender.SendAsync(last.AsMemory(), WebSocketMessageType.Binary, true, ct);
        await sender.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        await Receive(recipient, last, ct);
        // The sender is still reading. The peer hasn't read its Close yet and
        // can finish a write that was in progress before the closing handshake.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        await recipient.SendAsync(reply.AsMemory(1), WebSocketMessageType.Binary, true, ct);
        await Receive(sender, reply, ct);
        var close = await recipient.ReceiveAsync(new byte[1].AsMemory(), ct);
        if (close.MessageType != WebSocketMessageType.Close || recipient.CloseStatus != WebSocketCloseStatus.NormalClosure)
            throw new InvalidDataException("Expected an orderly relay close after the last message");
        await recipient.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        close = await sender.ReceiveAsync(new byte[1].AsMemory(), ct);
        if (close.MessageType != WebSocketMessageType.Close || sender.CloseStatus != WebSocketCloseStatus.NormalClosure)
            throw new InvalidDataException("Expected the delayed peer's orderly close after its final reply");
        Console.WriteLine("PASS relay drains final records and close in both directions with a three-second delayed peer");
    }

    private static async Task Expiry(Fixture fx, CancellationToken ct)
    {
        var waiting = await fx.Room();
        using var alone = new ClientWebSocket();
        var elapsed = Stopwatch.StartNew();
        try
        {
            await fx.Connect(alone, waiting, true).WaitAsync(TimeSpan.FromSeconds(6), ct);
            throw new InvalidOperationException("An unpaired socket received a WebSocket upgrade");
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException) { }
        if (elapsed.Elapsed < TimeSpan.FromSeconds(1.9)) throw new InvalidOperationException("Waiter was dropped before pairing deadline");
        await fx.Denied(waiting, true, HttpStatusCode.NotFound);
        var playing = await fx.Room();
        using var host = new ClientWebSocket();
        using var guest = new ClientWebSocket();
        elapsed.Restart();
        await Task.WhenAll(fx.Connect(host, playing, true), fx.Connect(guest, playing, false));
        byte[] data = [42];
        await host.SendAsync(data.AsMemory(), WebSocketMessageType.Binary, true, ct);
        await Receive(guest, data, ct);
        await Task.WhenAll(Closed(host, ct), Closed(guest, ct)).WaitAsync(TimeSpan.FromSeconds(8), ct);
        if (elapsed.Elapsed < TimeSpan.FromSeconds(2.9)) throw new InvalidOperationException("Pair was dropped before session deadline");
        await fx.Denied(playing, false, HttpStatusCode.NotFound);
        var fresh = await fx.Room();
        var next = await Task.WhenAll(fx.AfterCleanup(fresh, true), fx.AfterCleanup(fresh, false));
        using var nextHost = next[0];
        using var nextGuest = next[1];
        nextGuest.Abort();
        await Closed(nextHost, ct);
        Console.WriteLine("PASS unpaired relay deadline, active session TTL and peer disconnect release capacity");
    }

    private static async Task SharedAddress(Fixture fx, CancellationToken ct)
    {
        // All six clients use the same observed IPv4, as distinct players behind a CGNAT can.
        using var active = new ClientWebSocket();
        using var activePeer = new ClientWebSocket();
        using var quiet = new ClientWebSocket();
        using var quietPeer = new ClientWebSocket();
        using var oneWay = new ClientWebSocket();
        using var silentPeer = new ClientWebSocket();
        oneWay.Options.AddSubProtocol(RelayMessages.HeartbeatProtocol);
        silentPeer.Options.AddSubProtocol(RelayMessages.HeartbeatProtocol);
        async Task Pair(ClientWebSocket host, ClientWebSocket guest)
        {
            var room = await fx.Room();
            await Task.WhenAll(fx.Connect(host, room, true), fx.Connect(guest, room, false));
        }
        await Pair(active, activePeer);
        await Pair(quiet, quietPeer);
        await Pair(oneWay, silentPeer);
        var waiting = await fx.Room();
        await fx.Denied(waiting, true, HttpStatusCode.ServiceUnavailable);

        var quietClosed = Closed(quiet, ct);
        var oneWayClosed = Closed(oneWay, ct);
        byte[] payload = [42];
        async Task Exchange(WebSocket from, WebSocket to)
        {
            await from.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, ct);
            await Receive(to, payload, ct);
        }
        var elapsed = Stopwatch.StartNew();
        while (!quietClosed.IsCompleted || !oneWayClosed.IsCompleted)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(6))
                throw new InvalidOperationException("Relay did not release an idle direction");
            await Exchange(active, activePeer);
            await Exchange(activePeer, active);
            if (!oneWayClosed.IsCompleted)
            {
                // Neither a heartbeat nor data sent toward a silent participant renews its lease.
                try
                {
                    await oneWay.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, ct);
                    await oneWay.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, ct);
                }
                catch (Exception e) when (e is WebSocketException or InvalidOperationException)
                { await oneWayClosed.WaitAsync(TimeSpan.FromSeconds(1), ct); }
            }
            await Task.Delay(200, ct);
        }
        await Task.WhenAll(quietClosed, oneWayClosed);
        await Exchange(active, activePeer);
        var next = await Task.WhenAll(fx.AfterCleanup(waiting, true), fx.AfterCleanup(waiting, false));
        using var nextHost = next[0];
        using var nextGuest = next[1];
        await Exchange(nextGuest, nextHost);
        Console.WriteLine("PASS three pairs sharing one IPv4, global capacity, idle and silent-direction release, and continuing bidirectional traffic");
    }

    private static async Task Receive(WebSocket socket, byte[] expected, CancellationToken ct)
    {
        var bytes = new byte[expected.Length];
        int count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes.AsMemory(count), ct);
            if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
                throw new InvalidDataException("Missing relayed binary data");
            count += result.Count;
        } while (!result.EndOfMessage && count < bytes.Length);
        if (!result.EndOfMessage || count != bytes.Length || !bytes.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Relayed message differs from sender");
    }

    private static async Task Closed(WebSocket socket, CancellationToken ct)
    {
        try
        {
            var result = await socket.ReceiveAsync(new byte[1].AsMemory(), ct);
            if (result.MessageType != WebSocketMessageType.Close)
                throw new InvalidOperationException("Unexpected data after relay termination");
        }
        catch (WebSocketException) { }
    }
}
