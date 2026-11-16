using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Buffers.Binary;

namespace TH12Coop;

internal static partial class SelfTests
{
    private sealed class RoomReply(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Expect(request.RequestUri?.AbsolutePath == "/v2/rooms/receive" && request.Method == HttpMethod.Post,
                "fixed rendezvous endpoint");
            return Task.FromResult(respond());
        }
    }

    private static async Task RendezvousTests(List<string> passed, CancellationToken token)
    {
        foreach (var origin in new[] { null, "http://localhost", "https://user@localhost", "https://localhost/?x",
            "https://localhost/#part", "https://localhost/rooms" })
            ExpectThrows<IOException>(() => new RendezvousClient(origin).Dispose());
        foreach (var address in new[] { "10.0.0.1", "169.254.1.2", "255.255.255.255", "127.0.0.1", "::ffff:127.0.0.1",
            "224.0.0.1", "100.64.1.1", "fd00::1", "fe80::1", "ff01::1", "2002:7f00:1::1", "2001:db8::1" })
            Expect(!IceDescription.AddressAllowed(IPAddress.Parse(address), false), "non-public room candidate");
        foreach (var address in new[] { "1.1.1.1", "2606:4700:4700::1111" })
            Expect(IceDescription.AddressAllowed(IPAddress.Parse(address), false), "public unicast room candidate");
        using var identity = RoomIdentity.Create();
        static IceDescription Ice(string address, int port) => new("abcd", new string('A', 24), [new(address, port)]);
        HttpResponseMessage JsonReply(string address, int port) => new(HttpStatusCode.OK) { Content = JsonContent.Create(Ice(address, port)) };
        async Task RejectReply(Func<HttpResponseMessage> reply)
        {
            using var service = new RendezvousClient("https://rooms.invalid", new RoomReply(reply));
            await ExpectThrowsAsync<IOException>(() => service.WaitAnswer("000123", new string('A', 64), token));
        }
        await RejectReply(() => JsonReply("127.0.0.1", 3012));
        await RejectReply(() => JsonReply("example.invalid", 3012));
        await RejectReply(() => JsonReply("1.1.1.1", 443));
        await RejectReply(() => new(HttpStatusCode.OK) { Content = JsonContent.Create(Ice("1.1.1.1", 3012) with { Password = "short" }) });
        await RejectReply(() => new(HttpStatusCode.OK) { Content = JsonContent.Create(Ice("1.1.1.1", 3012) with
            { Candidates = Enumerable.Range(0, 5).Select(i => new IceEndpoint("1.1.1.1", 3012 + i)).ToArray() }) });
        await RejectReply(() => new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.invalid") } });
        await RejectReply(() => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 4097)) });
        using (var service = new RendezvousClient("https://localhost", new RoomReply(() => JsonReply("127.0.0.1", 53012))))
            Expect((await service.WaitAnswer("000123", new string('A', 64), token)).Candidates.Single().Port == 53012, "explicit loopback fixture and remapped UDP port");
        passed.Add("ICE signaling restricts HTTPS origin, response size, ICE credentials and numeric public UDP candidates");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task<(SecureChannel Guest, SecureChannel Host)> Pair()
        {
            var connecting = SecureChannel.Connect("127.0.0.1", port, identity.Invite, token, numbered: true);
            var accepting = SecureChannel.Accept(await listener.AcceptTcpClientAsync(token), identity, token, numbered: true);
            try { await Task.WhenAll(connecting, accepting); }
            catch
            {
                if (connecting.IsCompletedSuccessfully) connecting.Result.Dispose();
                if (accepting.IsCompletedSuccessfully) accepting.Result.Dispose();
                throw;
            }
            return (await connecting, await accepting);
        }
        var pair = await Pair();
        using (var guest = pair.Guest)
        using (var host = pair.Host)
        {
            Expect(guest.VerificationCode?.Length == 9 && guest.VerificationCode[4] == '-' &&
                guest.VerificationCode == host.VerificationCode, "TLS room comparison");
            var hostClick = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostGate = OnlineSession.ConfirmPeer(host, (_, ct) => hostClick.Task.WaitAsync(ct), token);
            var guestGate = OnlineSession.ConfirmPeer(guest, (_, _) => Task.FromResult(true), token);
            Expect(!guestGate.IsCompleted && !hostGate.IsCompleted, "one approval cannot start either game");
            hostClick.SetResult(true);
            await Task.WhenAll(guestGate, hostGate);
        }
        pair = await Pair();
        using (var guest = pair.Guest)
        using (var host = pair.Host)
        {
            bool closed = false;
            async Task<bool> WaitForClick(string _, CancellationToken ct)
            {
                try { await Task.Delay(Timeout.Infinite, ct); return true; }
                finally { await Task.Yield(); closed = true; }
            }
            var hostGate = OnlineSession.ConfirmPeer(host, WaitForClick, token);
            var guestGate = OnlineSession.ConfirmPeer(guest, (_, _) => Task.FromResult(false), token);
            await ExpectThrowsAsync<IOException>(() => guestGate);
            try { await hostGate; throw new Exception("Peer accepted a declined comparison"); }
            catch (IOException e) when (e.Message == "相手が照合を承認しませんでした。") { }
            Expect(closed, "remote refusal dismisses the local comparison before return");
        }
        passed.Add("Pinned TLS numbered peers derive the same short code; both approvals required and refusal dismisses the peer prompt");

        async Task RejectHello(bool requestNumber, bool sendWrongOpening)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, token);
            var accepting = SecureChannel.Accept(await listener.AcceptTcpClientAsync(token), identity, token, numbered: true);
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
                cert != null && cert.GetRawCertData().AsSpan().SequenceEqual(identity.Certificate.RawData));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = "th12coop.invalid", EnabledSslProtocols = SslProtocols.Tls13 }, token);
            var hello = new byte[40];
            "T12L"u8.CopyTo(hello);
            BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(4), SecureChannel.ProtocolVersion);
            hello[6] = 2; hello[7] = requestNumber ? (byte)1 : (byte)0;
            identity.Capability.CopyTo(hello, 8);
            await tls.WriteAsync(hello, token);
            if (sendWrongOpening)
            {
                await tls.WriteAsync(new byte[32], token);
                await tls.ReadExactlyAsync(new byte[24], token);
                await tls.WriteAsync(new byte[32], token);
            }
            await ExpectThrowsAsync<AuthenticationException>(async () => { using var unexpected = await accepting; });
        }
        await RejectHello(false, false);
        await RejectHello(true, true);
        passed.Add("Numbered TLS admission rejects manual mode and an invalid guest nonce opening");
    }
}
