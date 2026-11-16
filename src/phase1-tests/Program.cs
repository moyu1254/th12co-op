using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TH12Coop;
using DirectoryServer = TH12Coop.Rendezvous.Program;

namespace TH12Coop.Phase1Tests;

internal static class Program
{
    private sealed class WaitingMapping : IAsyncDisposable
    {
        internal bool Opened { get; private set; }
        internal bool Closed { get; private set; }

        internal Task<IAsyncDisposable?> Open(IPEndPoint endpoint, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(!Opened && endpoint.Port >= 1024, "Unexpected mapping endpoint");
            Opened = true;
            return Task.FromResult<IAsyncDisposable?>(this);
        }
        public ValueTask DisposeAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Confirmation(WaitingMapping mapping, bool relay)
    {
        private readonly TaskCompletionSource<string> host = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> guest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<bool> Compare(bool isHost, string phrase, CancellationToken token)
        {
            if (isHost) Check(relay ? !mapping.Opened || mapping.Closed : mapping.Opened && !mapping.Closed,
                "UDP mapping must be held only while the direct route is used");
            Check(phrase.Length != 0 && (isHost ? host : guest).TrySetResult(phrase), "Missing or duplicate confirmation");
            var other = await (isHost ? guest : host).Task.WaitAsync(token);
            Check(other == phrase, "Peer confirmation mismatch");
            return true;
        }
        internal bool BothConfirmed => host.Task.IsCompletedSuccessfully && guest.Task.IsCompletedSuccessfully;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static string TestFolder(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        QuietTestEnvironment.ValidateDirectory(info.FullName);
        Check(info.Exists && info.Name.StartsWith("game-test-", StringComparison.OrdinalIgnoreCase) &&
            !info.Attributes.HasFlag(FileAttributes.ReparsePoint), "Expected a workspace game-test-* directory");
        return info.FullName;
    }

    private static async Task<int> Main(string[] args)
    {
        (string Host, string Guest)? games = null;
        bool relayGame = args is ["--relay-games", _, _];
        if (args is ["--games" or "--relay-games", var host, var guest])
        {
            Native.SetErrorMode(0x8003);
            games = (TestFolder(host), TestFolder(guest));
        }
        else if (args.Length != 0) return 2;

        using var cert = Rendezvous.ServerTests.TestCertificate();
        await using var app = DirectoryServer.Build(cert, new(), IPAddress.Loopback, 0, TimeProvider.System);
        await app.StartAsync();
        try
        {
            var url = new Uri(app.Urls.Single());
            var hostRequests = new RouteProbe(TestHandler(cert));
            var guestRequests = new RouteProbe(TestHandler(cert)) { MissingProof = relayGame };
            using var service = new RendezvousClient(url.AbsoluteUri, hostRequests);
            using var guestService = new RendezvousClient(url.AbsoluteUri, guestRequests);
            using var identity = RoomIdentity.Create();
            var mapping = new WaitingMapping();
            var confirmation = new Confirmation(mapping, relayGame);
            var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (games is { } paths)
            {
                async Task Start(bool isHost, Action<string> status, CancellationToken token,
                    Func<uint, uint> input, Action<LockstepPeer> observe, int? character)
                {
                    Task<bool> Confirm(string phrase, CancellationToken ct) => confirmation.Compare(isHost, phrase, ct);
                    void Observe(LockstepPeer peer)
                    {
                        Check(confirmation.BothConfirmed, "Game preparation preceded peer confirmation");
                        observe(peer);
                    }
                    if (isHost)
                        await OnlineSession.HostNumber(paths.Host, identity, 3, issued.SetResult, Confirm, status, token, service,
                            testInstance: true, testInput: input, observe: Observe, openMapping: mapping.Open,
                            localCharacter: character);
                    else
                        await OnlineSession.JoinNumber(paths.Guest, await issued.Task.WaitAsync(token), Confirm, status, token, guestService,
                            testInstance: true, testInput: input, observe: Observe, localCharacter: character);
                }
                var result = await SelfTests.Lockstep(paths.Host, null, 60, guestGameDirectory: paths.Guest,
                    playerOne: 1, playerTwo: 4, numbered: Start);
                if (result != 0) return result;
            }
            else
            {
                using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var token = lifetime.Token;
                async Task Host()
                {
                    await using var transport = new NumberedTransport(service, mapping.Open);
                    using var peer = await transport.Host(identity, issued.SetResult, _ => { }, token);
                    await OnlineSession.ConfirmPeer(peer, (phrase, ct) => confirmation.Compare(true, phrase, ct), token);
                    Check((await peer.Receive(token)).SequenceEqual(new byte[] { 42 }), "Peer data after confirmation");
                    await peer.Send([43], token);
                    Check((await peer.Receive(token)).SequenceEqual(new byte[] { 44 }), "Peer round trip");
                }
                var accept = Host();
                try
                {
                    if (await Task.WhenAny(accept, issued.Task) == accept) await accept;
                    await using var transport = new NumberedTransport(guestService);
                    using var guestPeer = await transport.Join(await issued.Task.WaitAsync(token), _ => { }, token);
                    await OnlineSession.ConfirmPeer(guestPeer, (phrase, ct) => confirmation.Compare(false, phrase, ct), token);
                    await guestPeer.Send([42], token);
                    Check((await guestPeer.Receive(token)).SequenceEqual(new byte[] { 43 }), "QUIC duplex");
                    await guestPeer.Send([44], token);
                    await accept;
                }
                finally
                {
                    lifetime.Cancel();
                    await accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            Check(confirmation.BothConfirmed && (!mapping.Opened || mapping.Closed), "Missing cleanup or peer confirmation");
            Check(hostRequests.Upgrades == (relayGame ? 1 : 0) && guestRequests.Upgrades == (relayGame ? 1 : 0),
                "Unexpected relay use on the numbered path");
            Check(hostRequests.Closes == 0, "An authenticated guest's claim caused another request to close the room");
            if (games == null)
            {
                await CancelWaitingHost(service);
                Check(hostRequests.Closes == 1 && hostRequests.Upgrades == 0, "Cancelling an unclaimed room must close without relay");
                using var tests = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await RelayShutdownTest.ProcessExit(tests.Token);
                await Task.WhenAll(DelayedHostOfferProcessing(service, tests.Token),
                    QuicIdentityTests.Run(tests.Token), UdpPathTests.Run(tests.Token), RelayPathTests.Run(cert, tests.Token), RelayShutdownTest.Run(tests.Token),
                    RelayPathTests.AsymmetricRoute(service, url, cert, tests.Token), RelayPathTests.GuestAbortsSetup(cert, tests.Token));
            }
            Console.WriteLine(games == null ? "PASS numbered HTTPS/ICE/QUIC loopback, confirmation and cleanup" :
                $"PASS numbered {(relayGame ? "WSS relay" : "QUIC direct")} loopback game, lockstep, quiet operation and cleanup");
            return 0;
        }
        finally { await app.StopAsync(); }
    }

    private static async Task DelayedHostOfferProcessing(RendezvousClient service, CancellationToken token)
    {
        using var identity = RoomIdentity.Create();
        var room = await service.Create(identity.Invite, token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Guest()
        {
            await using var transport = new NumberedTransport(service);
            using var channel = await transport.Join(room.Number, _ => { }, stop.Token);
            await channel.Send([61], stop.Token);
            Check((await channel.Receive(stop.Token)).SequenceEqual(new byte[] { 62 }), "Late host reply");
        }
        var guest = Guest();
        try
        {
            var offer = await service.WaitOffer(room, stop.Token);
            // The old deadline started before the guest even gathered candidates.
            // Let the host resume after it, then exercise both real transport handshakes.
            var resume = Task.Delay(TimeSpan.FromSeconds(46), stop.Token);
            if (await Task.WhenAny(guest, resume) == guest) await guest;
            await resume;
            await using var ice = await UdpIcePeer.GatherAsync(true, service.Origin, stop.Token);
            await service.Answer(room, ice.Description, stop.Token);
            var path = await ice.ConnectAsync(offer, stop.Token);
            await using var quic = new IceQuicConnection();
            using var channel = await quic.Accept(path, identity, stop.Token);
            Check(await service.AgreeDirect(room.Number, room.OwnerToken, true, true, stop.Token),
                "Guest failed to agree on the late host's authenticated direct route");
            Check((await channel.Receive(stop.Token)).SequenceEqual(new byte[] { 61 }), "Late host admission");
            await channel.Send([62], stop.Token);
            await guest;
            Console.WriteLine("PASS guest authenticated and exchanged data after a 46-second host delay");
        }
        finally
        {
            stop.Cancel();
            await guest.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task CancelWaitingHost(RendezvousClient service)
    {
        using var identity = RoomIdentity.Create();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapping = new WaitingMapping();
        await using var transport = new NumberedTransport(service, mapping.Open);
        var accept = transport.Host(identity, issued.SetResult, _ => { }, lifetime.Token);
        try
        {
            if (await Task.WhenAny(accept, issued.Task) == accept) await accept;
            await issued.Task.WaitAsync(lifetime.Token);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                using var unexpected = await accept;
                throw new InvalidOperationException("Waiting host unexpectedly authenticated a peer");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        Check(!mapping.Opened, "Idle room unnecessarily opened a UDP mapping");
    }

    internal static HttpClientHandler TestHandler(X509Certificate2 cert) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, SslProtocols = SslProtocols.Tls13,
        ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(cert.RawData)
    };
}
