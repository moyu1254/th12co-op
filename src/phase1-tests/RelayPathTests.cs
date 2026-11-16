using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DirectoryServer = TH12Coop.Rendezvous.Program;

namespace TH12Coop.Phase1Tests;

// Change only the local fixture's signaling responses. The production launcher
// still gathers candidates, selects its route, opens WSS and authenticates TLS.
internal sealed class RouteProbe(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal bool MissingProof { get; init; }
    internal int UnreachablePort { get; init; }
    private int closes, upgrades, rewritten;
    internal int Closes => Volatile.Read(ref closes);
    internal int Upgrades => Volatile.Read(ref upgrades);
    internal int Rewritten => Volatile.Read(ref rewritten);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/v3/rooms/close") Interlocked.Increment(ref closes);
        if (path is "/v3/relay/host" or "/v3/relay/guest") Interlocked.Increment(ref upgrades);
        if (MissingProof && path == "/v3/rooms/proof")
        {
            Interlocked.Increment(ref rewritten);
            return new(HttpStatusCode.ServiceUnavailable);
        }
        var response = await base.SendAsync(request, token);
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
    internal static async Task Run(X509Certificate2 cert, CancellationToken token)
    {
        await using var app = DirectoryServer.Build(cert, new(), IPAddress.Loopback, 0, TimeProvider.System);
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
        var observedHost = new RouteProbe(Program.TestHandler(cert)) { MissingProof = missingHostProof, UnreachablePort = deadPort };
        var observedGuest = new RouteProbe(Program.TestHandler(cert)) { UnreachablePort = deadPort };
        using var hostService = new RendezvousClient(url, observedHost);
        using var guestService = new RendezvousClient(url, observedGuest);
        using var identity = RoomIdentity.Create();
        await using var host = new NumberedTransport(hostService);
        await using var guest = new NumberedTransport(guestService);
        var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
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
            if (!(await hostPeer.Receive(ct)).SequenceEqual(payload)) throw new IOException("Relayed payload mismatch");
            await hostPeer.Send([42], ct);
            if (!(await guestPeer.Receive(ct)).SequenceEqual(new byte[] { 42 })) throw new IOException("Relayed reply mismatch");
            await guestPeer.Send([43], ct);
            guestPeer.Dispose();
            if (!(await hostPeer.Receive(ct)).SequenceEqual(new byte[] { 43 })) throw new IOException("Lost final relayed record");
            try { await hostPeer.Receive(ct); throw new InvalidOperationException("Relay stayed open after peer closed"); }
            catch (EndOfStreamException) { }
            Console.WriteLine(missingHostProof ? "PASS one peer without a proved UDP candidate uses WSS and both confirm before data" :
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
        var ice = new IceDescription("abcd", new string('B', 24), [new("127.0.0.1", 33000)]);
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
