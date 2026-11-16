using System.Net;
using System.Net.Quic;

namespace TH12Coop;

// One instance per game: the ICE socket and any UDP mapping must outlive QUIC.
internal sealed class NumberedTransport(RendezvousClient service,
    Func<IPEndPoint, CancellationToken, Task<IAsyncDisposable?>>? openMapping = null) : IAsyncDisposable
{
    private UdpIcePeer? ice;
    private IceQuicConnection? quic;

    internal async Task<SecureChannel> Host(RoomIdentity identity, Action<string> roomIssued,
        Action<string> status, CancellationToken token)
    {
        if (!QuicListener.IsSupported) throw IceQuicConnection.Unavailable();
        status("部屋を作成しています");
        var room = await service.Create(identity.Invite, token);
        bool delivered = false;
        try
        {
            roomIssued(room.Number);
            status("ゲストの参加を待っています");
            IceDescription offer;
            try { offer = await service.WaitOffer(room, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("部屋番号が期限切れになりました。新しい部屋を作成してください。"); }

            status("UDP の接続経路を確認しています");
            ice = await UdpIcePeer.GatherAsync(true, service.Origin, token, mapPort: openMapping);
            await service.Answer(room, ice.Description, token);
            delivered = true;
            var path = await ice.ConnectAsync(offer, token);
            quic = new();
            return await quic.Accept(path, identity, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw NoPath(); }
        finally
        {
            // Once the answer is published, the guest alone may still need to read it.
            if (!delivered)
            {
                try { await service.Close(room); }
                catch (IOException) { status("部屋を取り消せませんでした。番号は10分で失効します。"); }
            }
        }
    }

    internal async Task<SecureChannel> Join(string number, Action<string> status, CancellationToken token)
    {
        if (!QuicConnection.IsSupported) throw IceQuicConnection.Unavailable();
        status("UDP の接続経路を確認しています");
        try
        {
            ice = await UdpIcePeer.GatherAsync(false, service.Origin, token);
            var claim = await service.Join(number, ice.Description, token);
            IceDescription answer;
            // The host may still be gathering and proving its addresses. Waiting
            // for its answer has a separate deadline from our ICE/QUIC handshake.
            try { answer = await service.WaitAnswer(number, claim.GuestToken, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("ホストの UDP 接続候補が90秒以内に届きませんでした。"); }
            var path = await ice.ConnectAsync(answer, token);
            quic = new();
            return await quic.Connect(path, claim.Invite, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw NoPath(); }
    }

    private static TimeoutException NoPath() => new("UDP で接続できませんでした。新しい部屋番号で再試行するか、LAN・VPN で手入力の直接接続を使用してください。");

    public async ValueTask DisposeAsync()
    {
        try { if (quic != null) await quic.DisposeAsync(); }
        finally { if (ice != null) await ice.DisposeAsync(); }
    }
}
