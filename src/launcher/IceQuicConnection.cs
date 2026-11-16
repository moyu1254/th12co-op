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
    private static readonly SslApplicationProtocol Alpn = new($"th12coop/{SecureChannel.ProtocolVersion}");
    // QUIC aborts a pending stream operation when its cancellation token fires.
    // Keep an expired path check distinct from the explicit authentication rejection (1).
    private const long PathUnavailable = 2;
    private readonly Socket local;
    private readonly CancellationTokenSource stopping = new();
    private Task pumps = Task.CompletedTask;
    private IPEndPoint? quicEndpoint;
    private QuicConnection? connection;
    private QuicStream? stream;
    private IOException? pathFailure;
    private readonly object closeGate = new();
    private Task? closing;
    private bool authenticated;
    private int disposed;

    internal IceQuicConnection()
    {
        Socket? socket = null;
        try
        {
            socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            local = socket;
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException)
        {
            socket?.Dispose();
            stopping.Dispose();
            throw new DirectPathUnavailableException("QUIC のローカル UDP ポートを開けませんでした。", e);
        }
    }
    private IPEndPoint LocalEndpoint => (IPEndPoint)local.LocalEndPoint!;

    private async ValueTask<QuicListener> Listen(RoomIdentity identity, CancellationToken token)
    {
        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), ListenBacklog = 1,
            ApplicationProtocols = [Alpn],
            ConnectionOptionsCallback = (connection, _, _) =>
            {
                if (!connection.RemoteEndPoint.Equals(LocalEndpoint)) throw new AuthenticationException();
                return ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = PathUnavailable, DefaultCloseErrorCode = 1,
                    MaxInboundBidirectionalStreams = 1, MaxInboundUnidirectionalStreams = 0,
                    IdleTimeout = TimeSpan.FromSeconds(45), KeepAliveInterval = TimeSpan.FromSeconds(10),
                    ServerAuthenticationOptions = new()
                    {
                        ApplicationProtocols = [Alpn], ServerCertificate = identity.Certificate,
                        EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false
                    }
                });
            }
        };
        try { return await QuicListener.ListenAsync(options, token); }
        catch (Exception e) when (!token.IsCancellationRequested && e is SocketException or QuicException or PlatformNotSupportedException)
        { throw new DirectPathUnavailableException("QUIC のローカル待受を開始できませんでした。", e); }
    }

    internal async Task<SecureChannel> Accept(IcePath path, RoomIdentity identity, CancellationToken token)
    {
        if (!QuicListener.IsSupported) throw Unavailable();
        await using var listener = await Listen(identity, token);
        quicEndpoint = listener.LocalEndPoint;
        StartPumps(path);
        using var handshake = HandshakeDeadline(token);
        try
        {
            connection = await listener.AcceptConnectionAsync(handshake.Token);
            stream = await connection.AcceptInboundStreamAsync(handshake.Token);
            var channel = await SecureChannel.AuthenticateHost(stream, identity, true, handshake.Token, FinishWrites, GetPathFailure);
            authenticated = true;
            return channel;
        }
        catch (Exception e) when (!token.IsCancellationRequested && NoQuicPath(e))
        { throw await AbandonPath(e); }
        catch { stream?.Abort(QuicAbortDirection.Both, 1); throw; }
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
                DefaultStreamErrorCode = PathUnavailable, DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 0, MaxInboundUnidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromSeconds(45), KeepAliveInterval = TimeSpan.FromSeconds(10),
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
            stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, handshake.Token);
            var channel = await SecureChannel.AuthenticateGuest(stream, pin, capability, true, handshake.Token, FinishWrites, GetPathFailure);
            authenticated = true;
            return channel;
        }
        catch (Exception e) when (!token.IsCancellationRequested && NoQuicPath(e))
        { throw await AbandonPath(e); }
        catch { stream?.Abort(QuicAbortDirection.Both, 1); throw; }
        finally { CryptographicOperations.ZeroMemory(capability); }
    }

    // These catches cover setup through application authentication. In particular,
    // ConnectAsync can complete before the host has received the client's Finished.
    private bool NoQuicPath(Exception error) =>
        error is OperationCanceledException or SocketException or PlatformNotSupportedException ||
        error is QuicException { QuicError: QuicError.ConnectionTimeout or QuicError.ConnectionIdle or QuicError.ConnectionRefused } ||
        error is QuicException { QuicError: QuicError.StreamAborted or QuicError.ConnectionAborted, ApplicationErrorCode: PathUnavailable } ||
        (error is QuicException { QuicError: QuicError.OperationAborted } && GetPathFailure() != null);

    private async Task<DirectPathUnavailableException> AbandonPath(Exception error)
    {
        stream?.Abort(QuicAbortDirection.Both, PathUnavailable);
        await CloseConnection(PathUnavailable).ConfigureAwait(false);
        return new DirectPathUnavailableException("UDP で QUIC に接続できませんでした。", GetPathFailure() ?? error);
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
            var ended = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
            try { await ended.ConfigureAwait(false); }
            catch (Exception cause) when (!token.IsCancellationRequested)
            {
                // Publish the cause before waking the handshake or a blocked record read.
                Volatile.Write(ref pathFailure, new IOException("UDP 経路が閉じたため、相手との通信を終了しました。", cause));
                stopping.Cancel();
                Volatile.Read(ref stream)?.Abort(QuicAbortDirection.Both, PathUnavailable);
                await CloseConnection(PathUnavailable).ConfigureAwait(false);
            }
            finally
            {
                stopping.Cancel();
                await Task.WhenAll(outbound, inbound).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        pumps = Run();
    }

    private IOException? GetPathFailure() => Volatile.Read(ref pathFailure);

    private void FinishWrites()
    {
        try { stream?.CompleteWrites(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { } // Already disconnected.
    }

    // The pump can fail while disposal is draining the stream. Only start one close.
    private Task CloseConnection(long code)
    {
        lock (closeGate)
            return connection == null ? Task.CompletedTask : closing ??= Close(connection, code);
        static async Task Close(QuicConnection connection, long code)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await connection.CloseAsync(code, limit.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            long code = 1;
            if (authenticated && stream != null && GetPathFailure() == null)
            {
                // WriteAsync only hands bytes to MsQuic. Leave both packet pumps running
                // until the peer acknowledges our stream FIN, including the last record.
                FinishWrites();
                try
                {
                    await stream.WritesClosed.WaitAsync(TimeSpan.FromSeconds(3), stopping.Token).ConfigureAwait(false);
                    code = 0;
                }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            }
            await CloseConnection(code).ConfigureAwait(false);
        }
        finally
        {
            stopping.Cancel();
            await pumps.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            try { if (connection != null) await connection.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); }
                finally { local.Dispose(); stopping.Dispose(); }
            }
        }
    }

    private static DirectPathUnavailableException Unavailable() => new("この PC では QUIC の直接接続を使用できません。");
}
