using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DirectoryServer = TH12Coop.Rendezvous.Program;

namespace TH12Coop.Phase1Tests;

// Change only the local fixture's signaling responses. The production launcher
// still gathers candidates, selects its route, opens WSS and authenticates TLS.
internal sealed class RouteProbe(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal bool MissingProof { get; init; }
    internal bool LegacyRelay { get; init; }
    internal int UnreachablePort { get; init; }
    internal Action? OnWaitAnswer { get; init; }
    private int closes, upgrades, rewritten;
    internal int Closes => Volatile.Read(ref closes);
    internal int Upgrades => Volatile.Read(ref upgrades);
    internal int Rewritten => Volatile.Read(ref rewritten);
    internal TaskCompletionSource<string> FirstRouteReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/v3/rooms/receive") OnWaitAnswer?.Invoke();
        if (path == "/v3/rooms/close") Interlocked.Increment(ref closes);
        if (path is "/v3/relay/host" or "/v3/relay/guest")
        {
            Interlocked.Increment(ref upgrades);
            // Exercise an upgrade that succeeds without negotiating heartbeats.
            if (LegacyRelay && !request.Headers.Remove("Sec-WebSocket-Protocol"))
                throw new InvalidOperationException("The launcher did not offer a relay subprotocol");
        }
        if (MissingProof && path == "/v3/rooms/proof")
        {
            Interlocked.Increment(ref rewritten);
            return new(HttpStatusCode.ServiceUnavailable);
        }
        var response = await base.SendAsync(request, token);
        if (path == "/v3/rooms/route" && response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync(token);
            FirstRouteReply.TrySetResult(JsonSerializer.Deserialize<string>(json) ?? throw new InvalidDataException());
        }
        if (UnreachablePort != 0 && path is ("/v3/rooms/offer" or "/v3/rooms/receive") && response.StatusCode == HttpStatusCode.OK)
        {
            using var original = response.Content;
            var ice = await original.ReadFromJsonAsync<IceDescription>(token) ?? throw new InvalidDataException();
            response.Content = JsonContent.Create(ice with
                { Candidates = ice.Candidates.Select(c => c with { Port = UnreachablePort }).ToArray() });
            Interlocked.Increment(ref rewritten);
        }
        return response;
    }
}

internal static class RelayPathTests
{
    internal static async Task GuestAbortsSetup(X509Certificate2 cert, CancellationToken token)
    {
        await using var app = DirectoryServer.Build(cert, new(), IPAddress.Loopback, 0, TimeProvider.System);
        await app.StartAsync(token);
        try
        {
            var url = app.Urls.Single();
            var hostProbe = new RouteProbe(Program.TestHandler(cert));
            var guestProbe = new RouteProbe(Program.TestHandler(cert));
            using var host = new RendezvousClient(url, hostProbe);
            using var guest = new RendezvousClient(url, guestProbe);
            using var identity = RoomIdentity.Create();
            using var differentIdentity = RoomIdentity.Create();
            var room = await host.Create(identity.Invite, token);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ct = stop.Token;
            await using var transport = new NumberedTransport(guest);
            var joining = transport.Join(room.Number, _ => { }, ct);
            var offer = await host.WaitOffer(room, ct);
            await using var ice = await UdpIcePeer.GatherAsync(true, host.Origin, ct);
            await host.Answer(room, ice.Description, ct);
            var path = await ice.ConnectAsync(offer, ct);
            // Hold an actual host route request pending while the guest hits a terminal TLS error.
            var vote = host.AgreeDirect(room.Number, room.OwnerToken, true, true, ct);
            if (await hostProbe.FirstRouteReply.Task.WaitAsync(ct) != "pending")
                throw new InvalidOperationException("Expected the host to wait for the guest's vote");
            await using var quic = new IceQuicConnection();
            var accepting = quic.Accept(path, differentIdentity, ct);
            try
            {
                try { using var unexpected = await joining; throw new InvalidOperationException("Accepted the wrong certificate"); }
                catch (AuthenticationException) { }
                try { await vote.WaitAsync(TimeSpan.FromSeconds(5), ct); throw new InvalidOperationException("Voted on a revoked room"); }
                catch (IOException) { }
                if (guestProbe.Closes != 1 || guestProbe.Upgrades != 0 || hostProbe.Upgrades != 0)
                    throw new InvalidOperationException("Expected exactly one cancellation and no relay after rejection");
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll((Task)vote, joining, accepting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            Console.WriteLine("PASS guest certificate rejection preserves its error and ends the host's pending route without relay");

            var cancelledRoom = await host.Create(identity.Invite, token);
            using var user = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancellingProbe = new RouteProbe(Program.TestHandler(cert)) { OnWaitAnswer = user.Cancel };
            using var cancellingGuest = new RendezvousClient(url, cancellingProbe);
            await using var cancelling = new NumberedTransport(cancellingGuest);
            try
            {
                using var unexpected = await cancelling.Join(cancelledRoom.Number, _ => { }, user.Token);
                throw new InvalidOperationException("Cancelled guest returned a channel");
            }
            catch (OperationCanceledException) when (user.IsCancellationRequested && !token.IsCancellationRequested) { }
            if (cancellingProbe.Closes != 1 || cancellingProbe.Upgrades != 0)
                throw new InvalidOperationException("A cancelled guest did not revoke its claim");
            try { await host.WaitOffer(cancelledRoom, token); throw new InvalidOperationException("Cancelled guest left its offer in the room"); }
            catch (IOException) { }
            Console.WriteLine("PASS a cancelled guest revokes its claim while waiting for the host's answer");
        }
        finally { await app.StopAsync(); }
    }

    internal static async Task AsymmetricRoute(RendezvousClient service, Uri url, X509Certificate2 cert, CancellationToken token)
    {
        using var identity = RoomIdentity.Create();
        var room = await service.Create(identity.Invite, token);
        var probe = new RouteProbe(Program.TestHandler(cert));
        using var guestService = new RendezvousClient(url.AbsoluteUri, probe);
        await using var guestTransport = new NumberedTransport(guestService);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = stop.Token;
        var join = guestTransport.Join(room.Number, _ => { }, ct);
        try
        {
            var offer = await service.WaitOffer(room, ct);
            await using var ice = await UdpIcePeer.GatherAsync(true, url, ct);
            await service.Answer(room, ice.Description, ct);
            var path = await ice.ConnectAsync(offer, ct);
            await using var quic = new IceQuicConnection();
            using var original = await quic.Accept(path, identity, ct);
            // The guest really authenticated QUIC; model a host that couldn't use
            // the final handshake bytes and reports failure after the guest's proposal.
            if (await probe.FirstRouteReply.Task.WaitAsync(ct) != "pending" || join.IsCompleted)
                throw new IOException("Guest exposed an unagreed direct channel");
            if (await service.AgreeDirect(room.Number, room.OwnerToken, true, false, ct))
                throw new IOException("Host failed to select the relay after direct path loss");
            using var relay = await service.AcceptRelay(room, identity, ct);
            using var guest = await join;
            if (probe.Upgrades != 1 || original.Session.SequenceEqual(relay.Session) || !relay.Session.SequenceEqual(guest.Session))
                throw new IOException("Peers did not reauthenticate on the agreed relay");
            await guest.Send([49], ct);
            if (!(await relay.Receive(ct)).SequenceEqual(new byte[] { 49 })) throw new IOException("Relay did not carry the record");
            Console.WriteLine("PASS guest with authenticated direct QUIC follows the host's relay decision before confirmation");
        }
        finally
        {
            stop.Cancel();
            await ((Task)join).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    internal static async Task Run(X509Certificate2 cert, CancellationToken token)
    {
        var limits = Rendezvous.RelayLimits.Default with { Idle = TimeSpan.FromSeconds(15) };
        await using var app = DirectoryServer.Build(cert, new(), IPAddress.Loopback, 0, TimeProvider.System, relayLimits: limits);
        await app.StartAsync(token);
        try
        {
            var url = app.Urls.Single();
            using var service = new RendezvousClient(url, Program.TestHandler(cert));
            await RejectIdentity(service, wrongPin: true, token);
            await RejectIdentity(service, wrongPin: false, token);
            Console.WriteLine("PASS relay rejects substituted peer certificate and wrong join key on a valid outer WSS connection");
            await Session(url, cert, missingHostProof: true, token);
            await Session(url, cert, missingHostProof: false, token);
        }
        finally { await app.StopAsync(); }
    }

    private static async Task Session(string url, X509Certificate2 cert, bool missingHostProof, CancellationToken token)
    {
        // Keep the unreachable UDP destination bound so another process cannot answer ICE checks there.
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var deadPort = missingHostProof ? 0 : ((IPEndPoint)sink.Client.LocalEndPoint!).Port;
        var observedHost = new RouteProbe(Program.TestHandler(cert))
            { MissingProof = missingHostProof, UnreachablePort = deadPort, LegacyRelay = !missingHostProof };
        var observedGuest = new RouteProbe(Program.TestHandler(cert)) { UnreachablePort = deadPort };
        using var hostService = new RendezvousClient(url, observedHost);
        using var guestService = new RendezvousClient(url, observedGuest);
        using var identity = RoomIdentity.Create();
        await using var host = new NumberedTransport(hostService);
        await using var guest = new NumberedTransport(guestService);
        var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(missingHostProof ? 55 : 35));
        var ct = deadline.Token;
        var accepting = host.Host(identity, issued.SetResult, _ => { }, ct);
        try
        {
            if (await Task.WhenAny(issued.Task, accepting) == accepting) await accepting;
            var connecting = guest.Join(await issued.Task.WaitAsync(ct), _ => { }, ct);
            using var guestPeer = await connecting;
            using var hostPeer = await accepting;
            if (observedHost.Upgrades != 1 || observedGuest.Upgrades != 1 || observedHost.Rewritten == 0 ||
                (!missingHostProof && observedGuest.Rewritten == 0) ||
                guestPeer.VerificationCode == null || guestPeer.VerificationCode != hostPeer.VerificationCode)
                throw new InvalidOperationException("Both peers must authenticate and derive the same code after direct failure");
            var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostGate = OnlineSession.ConfirmPeer(hostPeer, (_, stop) => approve.Task.WaitAsync(stop), ct);
            var guestGate = OnlineSession.ConfirmPeer(guestPeer, (_, _) => Task.FromResult(true), ct);
            if (guestGate.IsCompleted || hostGate.IsCompleted) throw new InvalidOperationException("A single approval started the session");
            approve.SetResult(true);
            await Task.WhenAll(hostGate, guestGate);
            var payload = RandomNumberGenerator.GetBytes(SecureChannel.MaximumPayload);
            await guestPeer.Send(payload, ct);
            // Neither launcher issues a read or write during this pause. Leave actual
            // ciphertext unread at the host, longer than this relay's inactivity limit.
            if (missingHostProof) await Task.Delay(TimeSpan.FromSeconds(18), ct);
            if (!(await hostPeer.Receive(ct)).SequenceEqual(payload)) throw new IOException("Relayed payload mismatch");
            await hostPeer.Send([42], ct);
            if (!(await guestPeer.Receive(ct)).SequenceEqual(new byte[] { 42 })) throw new IOException("Relayed reply mismatch");
            await guestPeer.Send([43], ct);
            guestPeer.Dispose();
            guestService.Dispose(); // OnlineSession releases its owned HTTP handler as soon as the game exits.
            if (missingHostProof)
            {
                // The remote application still has unread final TLS data and a pending write.
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                await hostPeer.Send([44], ct);
            }
            if (!(await hostPeer.Receive(ct)).SequenceEqual(new byte[] { 43 })) throw new IOException("Lost final relayed record");
            try { await hostPeer.Receive(ct); throw new InvalidOperationException("Relay stayed open after peer closed"); }
            catch (EndOfStreamException) { }
            Console.WriteLine(missingHostProof ? "PASS confirmed WSS peers with no application I/O survive the relay idle deadline and resume in order" :
                "PASS two proved but unreachable UDP endpoints fall back to WSS and exchange authenticated records");
        }
        finally
        {
            deadline.Cancel();
            await ((Task)accepting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task RejectIdentity(RendezvousClient service, bool wrongPin, CancellationToken token)
    {
        using var identity = RoomIdentity.Create();
        var ice = IceDescription.NoDirect;
        var room = await service.Create(identity.Invite, token);
        var guest = await service.Join(room.Number, ice, token);
        await service.Answer(room, ice, token);
        var (pin, key) = RoomIdentity.Parse(guest.Invite);
        (wrongPin ? pin : key)[0] ^= 1;
        var invalidInvite = "T12L7." + Base64Url.EncodeToString([..pin, ..key]);
        CryptographicOperations.ZeroMemory(key);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var accepting = service.AcceptRelay(room, identity, stop.Token);
        var connecting = service.ConnectRelay(room.Number, guest with { Invite = invalidInvite }, stop.Token);
        // Check the side responsible for the rejection. An unavailable relay or
        // an interrupted WSS upgrade cannot satisfy this authentication assertion.
        try
        {
            using var unexpected = await (wrongPin ? connecting : accepting);
            throw new InvalidOperationException("Invalid inner peer identity was accepted");
        }
        catch (AuthenticationException) { }
        finally
        {
            stop.Cancel();
            await ((Task)Task.WhenAll(connecting, accepting)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        if (connecting.IsCompletedSuccessfully || accepting.IsCompletedSuccessfully)
            throw new InvalidOperationException("Invalid peer established a channel");
    }
}
