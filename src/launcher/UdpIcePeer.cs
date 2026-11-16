using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Keryx.Ice;

namespace TH12Coop;

internal sealed class UdpIcePeer : IAsyncDisposable
{
    private readonly IceAgent agent;
    private readonly bool loopback;
    private IAsyncDisposable? mapping;
    private IPAddress[]? allowedPeerAddresses;
    private IcePath? path;
    private int started;
    internal IceDescription Description { get; private set; } = null!;

    internal UdpIcePeer(IceAgent agent, bool loopback)
    {
        this.agent = agent;
        this.loopback = loopback;
        agent.OnRemoteCandidate += (_, candidate) =>
        {
            // A peer-reflexive address is learned only from a valid authenticated check. Its
            // port may change behind NAT. Restrict it to a proven peer IP, and cap these
            // dynamic pairs too (the library's MaxRemoteCandidates caps signaled ones).
            if (candidate.Port < 1024 || !IceDescription.AddressAllowed(candidate.Address, loopback) ||
                agent.RemoteCandidates.Count > 2 * IceDescription.MaxCandidates ||
                Volatile.Read(ref allowedPeerAddresses) is { } allowed && !allowed.Contains(candidate.Address))
                agent.Close();
        };
    }

    internal static async Task<UdpIcePeer> GatherAsync(bool host, Uri rendezvousOrigin, CancellationToken token,
        IReadOnlyList<IPEndPoint>? stunServers = null,
        Func<IPEndPoint, CancellationToken, Task<IAsyncDisposable?>>? mapPort = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        bool local = rendezvousOrigin.IsLoopback;
        var options = new IceAgentOptions
        {
            Role = host ? IceRole.Controlling : IceRole.Controlled,
            BindAddress = local ? (rendezvousOrigin.HostNameType == UriHostNameType.IPv6
                ? IPAddress.IPv6Loopback : IPAddress.Loopback) : null,
            ResolveMdnsCandidates = false,
            MaxRemoteCandidates = 2 * IceDescription.MaxCandidates,
            ConnectivityTimeout = TimeSpan.FromSeconds(10),
            StunClientOptions = new() { MaxTransmissions = 3, FinalWaitMultiplier = 2 }
        };
        // STUN is provided by the configured operator, on UDP 3478 at the same DNS name.
        // Probe at most one A and one AAAA record; the signaling peer cannot set these.
        if (stunServers == null && !local)
            stunServers = (await Dns.GetHostAddressesAsync(rendezvousOrigin.IdnHost, ct))
                .Where(ip => IceDescription.AddressAllowed(ip, false))
                .DistinctBy(ip => ip.AddressFamily).Select(ip => new IPEndPoint(ip, 3478)).ToArray();
        foreach (var endpoint in stunServers ?? []) options.StunServers.Add(endpoint);

        var peer = new UdpIcePeer(new IceAgent(options), local);
        try
        {
            await peer.agent.StartGatheringAsync(ct);
            ct.ThrowIfCancellationRequested();
            var bound = peer.agent.LocalEndPoint!;
            if (host && (mapPort != null || !local))
                peer.mapping = mapPort == null ? await UpnpPortMapping.TryOpenUdpAsync(bound, ct) : await mapPort(bound, ct);
            ct.ThrowIfCancellationRequested();
            var candidates = peer.agent.LocalCandidates
                .OrderByDescending(c => c.Type == IceCandidateType.ServerReflexive)
                .Where(c => c.Port >= 1024 && IceDescription.AddressAllowed(c.Address, local))
                .Select(c => c.EndPoint).Distinct().ToList();
            if (peer.mapping is UpnpPortMapping { ExternalAddress: { } external } &&
                IceDescription.AddressAllowed(external, local))
                candidates.Insert(0, new IPEndPoint(external, bound.Port));
            var offer = candidates.Distinct().GroupBy(ep => ep.AddressFamily)
                .OrderByDescending(group => group.Key == AddressFamily.InterNetworkV6)
                .SelectMany(group => group.Take(2))
                .Select(ep => new IceEndpoint(ep.Address.ToString(), ep.Port)).ToArray();
            if (offer.Length == 0)
                throw new IOException("公開 UDP 候補を取得できません。合流先の STUN (UDP 3478) とルーターを確認してください。");
            peer.Description = new(peer.agent.LocalUfrag, peer.agent.LocalPassword, offer);
            return peer;
        }
        catch
        {
            await peer.DisposeAsync();
            throw;
        }
    }

    internal async Task<IcePath> ConnectAsync(IceDescription remote, CancellationToken token)
    {
        if (!remote.IsValid(loopback)) throw new IOException("相手の UDP 候補が正しくありません。");
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var endpoints = remote.Candidates.Select(c => new IPEndPoint(IPAddress.Parse(c.Address), c.Port)).ToArray();
            var allowed = endpoints.Select(ep => ep.Address).Distinct().ToArray();
            Volatile.Write(ref allowedPeerAddresses, allowed);
            if (agent.RemoteCandidates.Any(c => !allowed.Contains(c.Address))) throw new IOException("相手の UDP 候補を確認できません。");
            agent.SetRemoteCredentials(remote.Ufrag, remote.Password);
            var ipv6 = endpoints.Where(e => e.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
            bool tryV6 = ipv6.Length > 0 && agent.LocalCandidates.Any(c => c.Address.AddressFamily == AddressFamily.InterNetworkV6);
            if (tryV6)
            {
                AddCandidates(ipv6);
                // Give IPv6 several check intervals before admitting IPv4. Regular nomination
                // freezes the first working pair, so changing SDP order alone is insufficient.
                if (endpoints.Length > ipv6.Length) await Task.Delay(350, deadline.Token);
            }
            AddCandidates(endpoints.Where(e => e.AddressFamily == AddressFamily.InterNetwork));
            while (agent.SelectedPair is not { Nominated: true })
            {
                if (agent.State is IceAgentState.Failed or IceAgentState.Closed)
                    throw new IOException("UDP の経路を確立できませんでした。");
                await Task.Delay(25, deadline.Token);
            }
            var selected = agent.SelectedPair;
            if (selected is not { Nominated: true } || !allowed.Contains(selected.Remote.Address))
                throw new IOException("相手の UDP 経路を確認できません。");
            return path = new IcePath(agent, selected.RemoteEndPoint);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("UDP の接続性チェックがタイムアウトしました。");
        }
    }

    private void AddCandidates(IEnumerable<IPEndPoint> endpoints)
    {
        int index = 0;
        foreach (var ep in endpoints)
        {
            bool v6 = ep.AddressFamily == AddressFamily.InterNetworkV6;
            // Both sides rebuild priorities from the bounded numeric offer. The type/priority
            // only schedules checks; connectivity and peer credentials decide admission.
            var type = IceCandidateType.ServerReflexive;
            // The directory has no base/reflexive metadata. Assume distinct foundations so a
            // dead UPnP port cannot freeze a working STUN port on the same address.
            var foundation = (v6 ? "v6" : "v4") + (++index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            agent.AddRemoteCandidate(new IceCandidate(foundation, 1, IceCandidate.UdpTransport,
                IcePriority.Compute(type, v6 ? 65535 : 32767), ep.Address, ep.Port, type));
        }
    }

    public async ValueTask DisposeAsync()
    {
        path?.Close();
        try
        {
            // Keep the mapped port bound until the router has processed our release.
            if (Interlocked.Exchange(ref mapping, null) is { } lease) await lease.DisposeAsync();
        }
        finally { agent.Dispose(); }
    }
}

internal sealed class IcePath
{
    private readonly IceAgent agent;
    // One producer at the ICE receive boundary. QUIC recovers dropped datagrams.
    private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(64);
    internal IPEndPoint RemoteEndPoint { get; }
    internal int MaxDatagramSize => agent.Transport.MaxDatagramSize;

    internal IcePath(IceAgent agent, IPEndPoint remote)
    {
        this.agent = agent;
        RemoteEndPoint = remote;
        agent.Transport.OnReceived += Receive;
        agent.OnStateChanged += StateChanged;
    }

    private bool IsNominated => agent.SelectedPair is { Nominated: true } pair &&
        pair.RemoteEndPoint.Equals(RemoteEndPoint) && agent.State == IceAgentState.Connected;

    private void Receive(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is > 0 && datagram.Length <= MaxDatagramSize && IsNominated)
            incoming.Writer.TryWrite(datagram.ToArray());
    }

    private void StateChanged(object? sender, IceAgentState state)
    {
        if (state is IceAgentState.Failed or IceAgentState.Closed)
            incoming.Writer.TryComplete(new IOException("UDP 経路が閉じました。"));
    }

    internal ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (datagram.Length <= MaxDatagramSize && IsNominated) agent.Transport.Send(datagram.Span);
        return ValueTask.CompletedTask;
    }

    internal async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
    {
        var datagram = await incoming.Reader.ReadAsync(token);
        datagram.CopyTo(buffer);
        return datagram.Length;
    }

    internal void Close()
    {
        agent.Transport.OnReceived -= Receive;
        agent.OnStateChanged -= StateChanged;
        incoming.Writer.TryComplete();
    }
}
