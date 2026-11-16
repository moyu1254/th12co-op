using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

namespace TH12Coop;

internal sealed class DirectPathUnavailableException(string message, Exception? cause = null) : IOException(message, cause);

// One instance per game: the ICE socket and any UDP mapping must outlive QUIC.
internal sealed class NumberedTransport(RendezvousClient service,
    Func<IPEndPoint, CancellationToken, Task<IAsyncDisposable?>>? openMapping = null) : IAsyncDisposable
{
    private UdpIcePeer? ice;
    private IceQuicConnection? quic;
    internal bool UsesRelay { get; private set; }

    internal async Task<SecureChannel> Host(RoomIdentity identity, Action<string> roomIssued,
        Action<string> status, CancellationToken token)
    {
        status("部屋を作成しています");
        var room = await service.Create(identity.Invite, token);
        try
        {
            roomIssued(room.Number);
            status("ゲストの参加を待っています");
            IceDescription offer;
            try { offer = await service.WaitOffer(room, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("部屋番号が期限切れになりました。新しい部屋を作成してください。"); }

            status("UDP の接続経路を確認しています");
            if (!offer.IsNoDirect) await Gather(true, token);
            var published = await service.Answer(room, ice?.Description ?? IceDescription.NoDirect, token);
            return await Connect(published.IsNoDirect ? IceDescription.NoDirect : offer,
                (q, path) => q.Accept(path, identity, token),
                ready => service.AgreeDirect(room.Number, room.OwnerToken, true, ready, token),
                () => service.AcceptRelay(room, identity, token), status, token);
        }
        catch
        {
            await service.CancelSetup(room.Number, room.OwnerToken, host: true, status);
            throw;
        }
    }

    internal async Task<SecureChannel> Join(string number, Action<string> status, CancellationToken token)
    {
        status("UDP の接続経路を確認しています");
        await Gather(false, token);
        var claim = await service.Join(number, ice?.Description ?? IceDescription.NoDirect, token);
        try
        {
            IceDescription answer;
            try { answer = await service.WaitAnswer(number, claim.GuestToken, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("ホストの接続候補が90秒以内に届きませんでした。"); }
            return await Connect(answer, (q, path) => q.Connect(path, claim.Invite, token),
                ready => service.AgreeDirect(number, claim.GuestToken, false, ready, token),
                () => service.ConnectRelay(number, claim, token), status, token);
        }
        catch
        {
            await service.CancelSetup(number, claim.GuestToken, host: false, status);
            throw;
        }
    }

    private async Task Gather(bool host, CancellationToken token)
    {
        if (!(host ? QuicListener.IsSupported : QuicConnection.IsSupported)) return;
        try { ice = await UdpIcePeer.GatherAsync(host, service.Origin, token, mapPort: host ? openMapping : null); }
        catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException)
        { /* No local UDP path to advertise. Signal this to the other peer. */ }
    }

    private async Task<SecureChannel> Connect(IceDescription remote, Func<IceQuicConnection, IcePath, Task<SecureChannel>> direct,
        Func<bool, Task<bool>> agreeDirect, Func<Task<SecureChannel>> relay, Action<string> status, CancellationToken token)
    {
        if (ice != null && !remote.IsNoDirect)
        {
            SecureChannel? channel = null;
            try
            {
                var path = await ice.ConnectAsync(remote, token);
                quic = new();
                channel = await direct(quic, path);
            }
            catch (DirectPathUnavailableException) when (!token.IsCancellationRequested) { }
            // The peer's last application handshake message may be lost even if
            // our own handshake returned. Both peers must agree before showing the code.
            try
            {
                if (await agreeDirect(channel != null)) return channel!;
            }
            catch { channel?.Dispose(); throw; }
            channel?.Dispose();
        }
        await ReleaseDirect();
        token.ThrowIfCancellationRequested();
        status("直接接続できないため、中継に接続しています");
        var relayedChannel = await relay();
        UsesRelay = true;
        return relayedChannel;
    }

    private async ValueTask ReleaseDirect()
    {
        try { if (Interlocked.Exchange(ref quic, null) is { } connection) await connection.DisposeAsync(); }
        finally { if (Interlocked.Exchange(ref ice, null) is { } peer) await peer.DisposeAsync(); }
    }

    public ValueTask DisposeAsync() => ReleaseDirect();
}
