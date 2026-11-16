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

        internal Task<IAsyncDisposable?> Open(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(!Opened, "Unexpected second mapping");
            Opened = true;
            return Task.FromResult<IAsyncDisposable?>(this);
        }
        public ValueTask DisposeAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Confirmation(WaitingMapping mapping)
    {
        private readonly TaskCompletionSource<string> host = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> guest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<bool> Compare(bool isHost, string phrase, CancellationToken token)
        {
            if (isHost) Check(mapping.Opened && mapping.Closed, "Mapping was open at host confirmation");
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
        if (args is ["--games", var host, var guest])
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
            using var service = new RendezvousClient(url.AbsoluteUri, TestHandler(cert));
            using var identity = RoomIdentity.Create();
            var mapping = new WaitingMapping();
            var confirmation = new Confirmation(mapping);
            var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (games is { } paths)
            {
                async Task Start(bool isHost, Action<string> status, CancellationToken token,
                    Func<uint, uint> input, Action<LockstepPeer> observe, int? character)
                {
                    Task<bool> Confirm(string phrase, CancellationToken ct) => confirmation.Compare(isHost, phrase, ct);
                    void Observe(LockstepPeer peer)
                    {
                        Check(mapping.Closed && confirmation.BothConfirmed, "Game preparation preceded confirmation");
                        observe(peer);
                    }
                    if (isHost)
                        await OnlineSession.HostNumber(paths.Host, identity, 3, issued.SetResult, Confirm, status, token, service,
                            testInstance: true, testInput: input, observe: Observe, openMapping: mapping.Open,
                            localCharacter: character);
                    else
                        await OnlineSession.JoinNumber(paths.Guest, await issued.Task.WaitAsync(token), Confirm, status, token, service,
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
                using var listener = new TcpListener(IPAddress.Loopback, RendezvousClient.GamePort);
                listener.Start();
                async Task Host()
                {
                    using var peer = await OnlineSession.AcceptNumber(listener, service, identity, issued.SetResult,
                        _ => { }, token, mapping.Open);
                    await OnlineSession.ConfirmPeer(peer, (phrase, ct) => confirmation.Compare(true, phrase, ct), token);
                    Check((await peer.Receive(token)).SequenceEqual(new byte[] { 42 }), "Peer data after confirmation");
                }
                var accept = Host();
                try
                {
                    if (await Task.WhenAny(accept, issued.Task) == accept) await accept;
                    var candidate = await service.Join(await issued.Task.WaitAsync(token), token);
                    using var guestPeer = await SecureChannel.Connect(candidate.Address, candidate.Port, candidate.Invite, token, numbered: true);
                    await OnlineSession.ConfirmPeer(guestPeer, (phrase, ct) => confirmation.Compare(false, phrase, ct), token);
                    await guestPeer.Send([42], token);
                    await accept;
                }
                finally
                {
                    lifetime.Cancel();
                    await accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            Check(confirmation.BothConfirmed && mapping.Closed, "Missing waiting cleanup or peer confirmation");
            using var auditClient = new HttpClient(TestHandler(cert)) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(5) };
            using var second = await auditClient.PostAsJsonAsync("/v1/rooms/join", new { number = await issued.Task });
            Check(second.StatusCode == HttpStatusCode.NotFound, "Room remained joinable");
            if (games == null) await CancelWaitingHost(service, auditClient);
            Console.WriteLine(games == null ? "PASS numbered HTTPS/TLS loopback, confirmation and cleanup" :
                "PASS numbered loopback game, lockstep, quiet operation and cleanup");
            return 0;
        }
        finally { await app.StopAsync(); }
    }

    private static async Task CancelWaitingHost(RendezvousClient service, HttpClient auditClient)
    {
        using var identity = RoomIdentity.Create();
        using var listener = new TcpListener(IPAddress.Loopback, RendezvousClient.GamePort);
        listener.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapping = new WaitingMapping();
        var accept = OnlineSession.AcceptNumber(listener, service, identity, issued.SetResult, _ => { }, lifetime.Token, mapping.Open);
        string number;
        try
        {
            if (await Task.WhenAny(accept, issued.Task) == accept) await accept;
            number = await issued.Task.WaitAsync(lifetime.Token);
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
        Check(mapping.Opened && mapping.Closed, "Cancelled host leaked its mapping");
        using var response = await auditClient.PostAsJsonAsync("/v1/rooms/join", new { number });
        Check(response.StatusCode == HttpStatusCode.NotFound, "Cancelled host leaked a joinable room");
    }

    private static HttpClientHandler TestHandler(X509Certificate2 cert) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, SslProtocols = SslProtocols.Tls13,
        ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(cert.RawData)
    };
}
