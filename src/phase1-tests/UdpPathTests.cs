using System.Net;
using System.Net.Sockets;
using Keryx.Ice;
using Keryx.Stun;

namespace TH12Coop.Phase1Tests;

internal static class UdpPathTests
{
    private static readonly Uri Loopback = new("https://127.0.0.1/");

    internal static async Task Run(CancellationToken token)
    {
        await TranslatedPath(token);
        await TranslatedPath(token, blockQuic: true);
        await FilteredPeerReflexiveChecks(token);
        await WrongIcePasswords(token);
        if (Socket.OSSupportsIPv6)
        {
            await Ipv6Path(token);
            await DualStackAddressSelection(token);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Each endpoint has a separate public port and a STUN listener. The fixture only
    // forwards packets between the two loopback sockets that issued the Binding requests.
    private sealed class Translation : IDisposable
    {
        private readonly UdpClient stun = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient external = new(new IPEndPoint(IPAddress.Loopback, 0));
        internal IPEndPoint Stun => (IPEndPoint)stun.Client.LocalEndPoint!;
        internal IPEndPoint External => (IPEndPoint)external.Client.LocalEndPoint!;
        internal IPEndPoint Internal { get; private set; } = null!;
        internal bool DroppedInitial { get; private set; }

        internal async Task Reflect(CancellationToken token)
        {
            var packet = await stun.ReceiveAsync(token);
            var request = StunMessage.Decode(packet.Buffer);
            Check(packet.RemoteEndPoint.Address.Equals(IPAddress.Loopback) &&
                request.Method == StunMethod.Binding && request.Class == StunClass.Request, "Expected a loopback STUN Binding request");
            Internal = packet.RemoteEndPoint;
            var response = StunMessage.CreateSuccessResponse(request).Add(new StunXorMappedAddressAttribute(External));
            await stun.SendAsync(response.Encode(appendFingerprint: true), Internal, token);
        }

        internal async Task ForwardFrom(Translation sender, bool dropFirstQuic, CancellationToken token, bool blockQuic = false)
        {
            while (true)
            {
                var packet = await external.ReceiveAsync(token);
                if (!packet.RemoteEndPoint.Equals(sender.Internal)) continue;
                if ((blockQuic || dropFirstQuic && !DroppedInitial) && packet.Buffer.Length >= 1200 && (packet.Buffer[0] & 0xc0) == 0xc0)
                {
                    DroppedInitial = true;
                    continue;
                }
                await sender.external.SendAsync(packet.Buffer, Internal, token);
            }
        }

        internal IceDescription OnlyMapped(IceDescription description)
        {
            var candidate = new IceEndpoint(External.Address.ToString(), External.Port);
            Check(External.Port != Internal.Port && description.Candidates.Contains(candidate), "STUN did not discover translated UDP port");
            return description with { Candidates = [candidate] };
        }

        public void Dispose()
        {
            stun.Dispose();
            external.Dispose();
        }
    }

    private static async Task TranslatedPath(CancellationToken token, bool blockQuic = false)
    {
        using var hostNat = new Translation();
        using var guestNat = new Translation();
        using var network = CancellationTokenSource.CreateLinkedTokenSource(token);
        network.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = network.Token;
        var hostStun = hostNat.Reflect(ct);
        var guestStun = guestNat.Reflect(ct);
        await using var host = await UdpIcePeer.GatherAsync(true, Loopback, ct, [hostNat.Stun]);
        await using var guest = await UdpIcePeer.GatherAsync(false, Loopback, ct, [guestNat.Stun]);
        await Task.WhenAll(hostStun, guestStun);
        var hostOffer = hostNat.OnlyMapped(host.Description);
        var guestOffer = guestNat.OnlyMapped(guest.Description);
        // Both sockets are IPv4 only. Include an IPv6 candidate to exercise family fallback.
        using var blackhole = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var deadPort = ((IPEndPoint)blackhole.Client.LocalEndPoint!).Port;
        IceDescription WithDeadPorts(IceDescription offer) => offer with
        {
            Candidates = [new IceEndpoint("::1", deadPort), new IceEndpoint("127.0.0.1", deadPort), ..offer.Candidates]
        };
        var forwarding = Task.WhenAll(hostNat.ForwardFrom(guestNat, true, ct, blockQuic), guestNat.ForwardFrom(hostNat, false, ct));
        try
        {
            var paths = await Task.WhenAll(host.ConnectAsync(WithDeadPorts(guestOffer), ct),
                guest.ConnectAsync(WithDeadPorts(hostOffer), ct));
            Check(paths[0].RemoteEndPoint.Equals(guestNat.External) && paths[1].RemoteEndPoint.Equals(hostNat.External),
                "ICE nominated an endpoint other than the STUN mapping");
            using var identity = RoomIdentity.Create();
            await using var server = new IceQuicConnection();
            await using var client = new IceQuicConnection();
            var accept = server.Accept(paths[0], identity, ct);
            var connect = client.Connect(paths[1], identity.Invite, ct);
            if (blockQuic)
            {
                static async Task NoPath(Task<SecureChannel> handshake)
                {
                    try { using var unexpected = await handshake; }
                    catch (DirectPathUnavailableException) { return; }
                    throw new InvalidOperationException("An unavailable QUIC path established a channel");
                }
                await Task.WhenAll(NoPath(accept), NoPath(connect));
                Check(hostNat.DroppedInitial, "No QUIC Initial reached the nominated path");
                Console.WriteLine("PASS ICE nominated the pair but blocked QUIC selected fallback on both peers");
                return;
            }
            var channels = await Task.WhenAll(accept, connect);
            using var accepted = channels[0];
            using var connected = channels[1];
            var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            await connected.Send(payload, ct);
            Check((await accepted.Receive(ct)).SequenceEqual(payload), "QUIC record changed across translated path");
            await accepted.Send([42], ct);
            Check((await connected.Receive(ct)).SequenceEqual(new byte[] { 42 }) && hostNat.DroppedInitial,
                "Expected bidirectional QUIC after losing the first Initial");
        }
        finally
        {
            network.Cancel();
            try { await forwarding; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        Console.WriteLine("PASS loopback STUN translated both ports, negotiated the reflected pair, and carried a 4096-byte QUIC record after dropping the first Initial");
    }

    private static async Task FilteredPeerReflexiveChecks(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        // Let non-STUN data through the underlying socket filter in this fixture to
        // exercise IcePath's own source check even when the library hands it a packet.
        var hostAgent = new IceAgent(new IceAgentOptions
        {
            BindAddress = IPAddress.Loopback, StrictInboundSourceValidation = false,
            MaxRemoteCandidates = 4, MaxPeerReflexiveCandidates = 1
        });
        await using var host = new UdpIcePeer(hostAgent, loopback: true);
        await hostAgent.StartGatheringAsync(ct);
        var hostSocket = hostAgent.LocalEndPoint!;
        var hostOffer = new IceDescription(hostAgent.LocalUfrag, hostAgent.LocalPassword,
            [new(hostSocket.Address.ToString(), hostSocket.Port)]);
        await using var guest = await UdpIcePeer.GatherAsync(false, Loopback, ct);

        // A second interface for the same guest sends ordinary ICE checks from a
        // loopback address absent from its verified offer, before and after signaling.
        IceAgent GuestInterface(IPAddress address) => new(new IceAgentOptions
        {
            Role = IceRole.Controlled, BindAddress = address,
            LocalUfrag = guest.Description.Ufrag, LocalPassword = guest.Description.Password
        });
        using var alternate = GuestInterface(IPAddress.Parse("127.0.0.2"));
        using var extraPort = GuestInterface(IPAddress.Loopback);
        await Task.WhenAll(alternate.StartGatheringAsync(ct), extraPort.StartGatheringAsync(ct));
        var alternateSocket = alternate.LocalEndPoint!;
        var beforeOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extraCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accept = hostAgent.AcceptRemoteCheck!;
        hostAgent.AcceptRemoteCheck = source =>
        {
            var admitted = accept(source);
            if (source.Equals(extraPort.LocalEndPoint) && admitted) extraCheck.TrySetResult();
            if (source.Equals(alternateSocket) && !admitted)
            {
                // The proven loopback endpoint becomes admissible only after ConnectAsync publishes it.
                (accept(hostSocket) ? afterOffer : beforeOffer).TrySetResult();
            }
            return admitted;
        };
        void StartChecks(IceAgent sender)
        {
            sender.SetRemoteCredentials(hostOffer.Ufrag, hostOffer.Password);
            sender.AddRemoteCandidate(hostAgent.LocalCandidates.Single());
        }
        StartChecks(alternate);
        await beforeOffer.Task.WaitAsync(ct);
        Check(hostAgent.RemoteCandidates.Count == 0, "Learned a remote candidate before signaling");

        // This listener stands in for an earlier NAT mapping. The same proven IP
        // checks back from its current port, which must still be eligible for nomination.
        using var oldMapping = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var oldSocket = (IPEndPoint)oldMapping.Client.LocalEndPoint!;
        var guestOffer = guest.Description with { Candidates = [new(oldSocket.Address.ToString(), oldSocket.Port)] };
        var hostConnection = host.ConnectAsync(guestOffer, ct);
        await afterOffer.Task.WaitAsync(ct);
        var paths = await Task.WhenAll(hostConnection, guest.ConnectAsync(hostOffer, ct));
        Check(hostAgent.RemoteCandidates.All(c => !c.EndPoint.Equals(alternateSocket)) &&
            hostAgent.CheckList.All(p => !p.RemoteEndPoint.Equals(alternateSocket)), "Unapproved source entered the checklist");
        Check(hostAgent.SelectedPair?.Remote.Type == IceCandidateType.PeerReflexive &&
            paths[0].RemoteEndPoint.Port == guest.Description.Candidates.Single().Port, "Expected the peer's current NAT port");

        // The only learned slot is occupied by the current port. Another port on the
        // proven IP must leave both the candidate list and established connection intact.
        StartChecks(extraPort);
        await extraCheck.Task.WaitAsync(ct);
        var offPairDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostAgent.OnDatagram += (from, _) => { if (from.Equals(oldSocket)) offPairDelivered.TrySetResult(); };
        await oldMapping.SendAsync(new byte[] { 0xf1, 1 }, hostSocket, ct);
        await offPairDelivered.Task.WaitAsync(ct);
        await paths[1].SendAsync(new byte[] { 0xf1, 2 }, ct);
        var buffer = new byte[32];
        Check(await paths[0].ReceiveAsync(buffer, ct) == 2 && buffer[1] == 2,
            "Data from the other port reached the nominated application path");
        Check(hostAgent.RemoteCandidates.Count == 2 && hostAgent.CheckList.All(p => !p.RemoteEndPoint.Equals(extraPort.LocalEndPoint)),
            "Exceeded the peer-reflexive cap");
        Console.WriteLine("PASS early and unproved loopback sources were ignored, NAT port changes respected the cap, and only nominated data reached the channel");
    }

    private static async Task WrongIcePasswords(CancellationToken token)
    {
        await using var host = await UdpIcePeer.GatherAsync(true, Loopback, token);
        await using var guest = await UdpIcePeer.GatherAsync(false, Loopback, token);
        using var checks = CancellationTokenSource.CreateLinkedTokenSource(token);
        checks.CancelAfter(TimeSpan.FromSeconds(1));
        static IceDescription Wrong(IceDescription description) => description with
        {
            Password = (description.Password[0] == 'A' ? "B" : "A") + description.Password[1..]
        };
        async Task Rejected(UdpIcePeer peer, IceDescription remote)
        {
            try { await peer.ConnectAsync(Wrong(remote), checks.Token); }
            catch (OperationCanceledException) when (checks.IsCancellationRequested && !token.IsCancellationRequested) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("ICE accepted an incorrect password");
        }
        await Task.WhenAll(Rejected(host, guest.Description), Rejected(guest, host.Description));
        token.ThrowIfCancellationRequested();
        Console.WriteLine("PASS wrong ICE passwords did not nominate a pair; pending checks cancelled");
    }

    private static async Task Ipv6Path(CancellationToken token)
    {
        var origin = new Uri("https://[::1]/");
        await using var host = await UdpIcePeer.GatherAsync(true, origin, token);
        await using var guest = await UdpIcePeer.GatherAsync(false, origin, token);
        static IceDescription WithV4(IceDescription description) => description with
        {
            Candidates = [new IceEndpoint("127.0.0.1", description.Candidates[0].Port), ..description.Candidates]
        };
        var paths = await Task.WhenAll(host.ConnectAsync(WithV4(guest.Description), token),
            guest.ConnectAsync(WithV4(host.Description), token));
        Check(paths.All(p => p.RemoteEndPoint.Address.Equals(IPAddress.IPv6Loopback)), "Expected an IPv6 nomination");
        await paths[0].SendAsync(new byte[] { 0xf1, 42 }, token);
        var buffer = new byte[32];
        Check(await paths[1].ReceiveAsync(buffer, token) == 2 && buffer[0] == 0xf1 && buffer[1] == 42,
            "Datagram did not cross nominated IPv6 pair");
        Console.WriteLine("PASS IPv6 loopback pair nominated with IPv4 listed first");
    }

    private static async Task DualStackAddressSelection(CancellationToken token)
    {
        // Wildcard gathering is needed for Keryx's dual-mode socket. No STUN/IGD server
        // is configured. Signal only the two loopback destinations on that socket.
        foreach (var blockIpv6 in new[] { false, true })
        {
            var hostAgent = new IceAgent(new IceAgentOptions { Role = IceRole.Controlling });
            var guestAgent = new IceAgent(new IceAgentOptions { Role = IceRole.Controlled });
            await using var host = new UdpIcePeer(hostAgent, loopback: true);
            await using var guest = new UdpIcePeer(guestAgent, loopback: true);
            await Task.WhenAll(hostAgent.StartGatheringAsync(token), guestAgent.StartGatheringAsync(token));
            if (hostAgent.LocalCandidates.Select(c => c.Address.AddressFamily).Distinct().Count() < 2)
            {
                Console.WriteLine("SKIP dual-stack competition: Keryx needs host interface candidates for both families");
                return;
            }
            using var hostBlackhole = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            using var guestBlackhole = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            IceDescription Offer(IceAgent agent, UdpClient silent)
            {
                int port = agent.LocalEndPoint!.Port;
                int v6 = blockIpv6 ? ((IPEndPoint)silent.Client.LocalEndPoint!).Port : port;
                return new(agent.LocalUfrag, agent.LocalPassword, [new("127.0.0.1", port), new("::1", v6)]);
            }
            var paths = await Task.WhenAll(host.ConnectAsync(Offer(guestAgent, guestBlackhole), token),
                guest.ConnectAsync(Offer(hostAgent, hostBlackhole), token));
            var expected = blockIpv6 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            Check(paths.All(p => p.RemoteEndPoint.Address.Equals(expected)), "Incorrect dual-stack nomination");
            if (blockIpv6)
            {
                foreach (var silent in new[] { hostBlackhole, guestBlackhole })
                {
                    var ignored = await silent.ReceiveAsync(token);
                    Check(ignored.RemoteEndPoint.Address.Equals(IPAddress.IPv6Loopback) &&
                        StunMessage.Decode(ignored.Buffer).Method == StunMethod.Binding, "Missing IPv6 connectivity check");
                }
            }
            await paths[0].SendAsync(new byte[] { 0xf1, 7 }, token);
            var buffer = new byte[32];
            Check(await paths[1].ReceiveAsync(buffer, token) == 2 && buffer[0] == 0xf1 && buffer[1] == 7,
                "Dual-stack selected path failed to deliver");
        }
        Console.WriteLine("PASS dual-stack sockets prefer reachable IPv6 and carry IPv4 traffic when both peers' IPv6 checks are dropped");
    }
}
