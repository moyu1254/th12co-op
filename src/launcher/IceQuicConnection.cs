using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TH12Coop;

// MsQuic owns its UDP socket. Keep ICE's nominated socket (and NAT binding) alive:
// copy QUIC datagrams between that one path and a private loopback endpoint.
internal sealed class IceQuicConnection : IAsyncDisposable
{
    private static readonly SslApplicationProtocol Alpn = new("th12coop/6");
    private readonly Socket local = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource stopping = new();
    private Task pumps = Task.CompletedTask;
    private IPEndPoint? quicEndpoint;
    private QuicConnection? connection;

    internal IceQuicConnection() => local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    private IPEndPoint LocalEndpoint => (IPEndPoint)local.LocalEndPoint!;

    internal async Task<SecureChannel> Accept(IcePath path, RoomIdentity identity, CancellationToken token)
    {
        if (!QuicListener.IsSupported) throw Unavailable();
        await using var listener = await QuicListener.ListenAsync(new()
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), ListenBacklog = 1,
            ApplicationProtocols = [Alpn],
            ConnectionOptionsCallback = (connection, _, _) =>
            {
                if (!connection.RemoteEndPoint.Equals(LocalEndpoint)) throw new AuthenticationException();
                return ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 1, DefaultCloseErrorCode = 1,
                    MaxInboundBidirectionalStreams = 1, MaxInboundUnidirectionalStreams = 0,
                    IdleTimeout = TimeSpan.FromSeconds(45),
                    ServerAuthenticationOptions = new()
                    {
                        ApplicationProtocols = [Alpn], ServerCertificate = identity.Certificate,
                        EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false
                    }
                });
            }
        }, token);
        quicEndpoint = listener.LocalEndPoint;
        StartPumps(path);
        using var handshake = HandshakeDeadline(token);
        connection = await listener.AcceptConnectionAsync(handshake.Token);
        var stream = await connection.AcceptInboundStreamAsync(handshake.Token);
        try { return await SecureChannel.AuthenticateHost(stream, identity, true, handshake.Token); }
        catch { await stream.DisposeAsync(); throw; }
    }

    internal async Task<SecureChannel> Connect(IcePath path, string invite, CancellationToken token)
    {
        if (!QuicConnection.IsSupported) throw Unavailable();
        var (pin, capability) = RoomIdentity.Parse(invite);
        try
        {
            StartPumps(path);
            using var handshake = HandshakeDeadline(token);
            connection = await QuicConnection.ConnectAsync(new()
            {
                RemoteEndPoint = LocalEndpoint, LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                DefaultStreamErrorCode = 1, DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 0, MaxInboundUnidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromSeconds(45),
                ClientAuthenticationOptions = new()
                {
                    ApplicationProtocols = [Alpn], TargetHost = "th12coop.invalid",
                    EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null &&
                        CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), pin)
                }
            }, handshake.Token);
            if (!connection.LocalEndPoint.Equals(Volatile.Read(ref quicEndpoint))) throw new AuthenticationException();
            var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, handshake.Token);
            try { return await SecureChannel.AuthenticateGuest(stream, pin, capability, true, handshake.Token); }
            catch { await stream.DisposeAsync(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(capability); }
    }

    private CancellationTokenSource HandshakeDeadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return deadline;
    }

    private void StartPumps(IcePath path)
    {
        var token = stopping.Token;
        async Task Outbound()
        {
            // A loopback PMTU probe can be larger than the path MTU. Read whole
            // datagrams and drop oversized ones rather than forwarding a truncation.
            var buffer = new byte[ushort.MaxValue];
            EndPoint anyLocal = new IPEndPoint(IPAddress.Loopback, 0);
            while (true)
            {
                var packet = await local.ReceiveFromAsync(buffer, SocketFlags.None, anyLocal, token).ConfigureAwait(false);
                var from = (IPEndPoint)packet.RemoteEndPoint;
                if (!from.Address.Equals(IPAddress.Loopback) || packet.ReceivedBytes == 0 || packet.ReceivedBytes > path.MaxDatagramSize) continue;
                // The client's ephemeral port is known only after ConnectAsync
                // completes. Latch the first local Initial; verify the port once connected.
                if (Volatile.Read(ref quicEndpoint) == null && packet.ReceivedBytes >= 1200 && (buffer[0] & 0xc0) == 0xc0)
                    Volatile.Write(ref quicEndpoint, from);
                if (from.Equals(Volatile.Read(ref quicEndpoint)))
                    await path.SendAsync(buffer.AsMemory(0, packet.ReceivedBytes), token).ConfigureAwait(false);
            }
        }
        async Task Inbound()
        {
            var buffer = new byte[path.MaxDatagramSize];
            while (true)
            {
                int size = await path.ReceiveAsync(buffer, token).ConfigureAwait(false);
                var to = Volatile.Read(ref quicEndpoint);
                if (to != null && size > 0)
                    await local.SendToAsync(buffer.AsMemory(0, size), SocketFlags.None, to, token).ConfigureAwait(false);
            }
        }
        async Task Run()
        {
            var outbound = Outbound();
            var inbound = Inbound();
            await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
            stopping.Cancel();
            await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
        }
        pumps = Run();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            stopping.Cancel();
            await pumps.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            local.Dispose();
            stopping.Dispose();
        }
    }

    internal static IOException Unavailable() => new("部屋番号での接続には QUIC 対応の Windows 11 以降が必要です。");
}
