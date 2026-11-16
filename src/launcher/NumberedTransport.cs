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
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
            waiting.CancelAfter(TimeSpan.FromMinutes(10));
            IceDescription offer;
            try { offer = await service.WaitOffer(room, waiting.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("部屋番号が期限切れになりました。新しい部屋を作成してください。"); }

            using var deadline = ConnectionDeadline(token);
            status("UDP の接続経路を確認しています");
            ice = await UdpIcePeer.GatherAsync(true, service.Origin, deadline.Token, mapPort: openMapping);
            await service.Answer(room, ice.Description, deadline.Token);
            delivered = true;
            var path = await ice.ConnectAsync(offer, deadline.Token);
            quic = new();
            return await quic.Accept(path, identity, deadline.Token);
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
        using var deadline = ConnectionDeadline(token);
        try
        {
            ice = await UdpIcePeer.GatherAsync(false, service.Origin, deadline.Token);
            var claim = await service.Join(number, ice.Description, deadline.Token);
            var answer = await service.WaitAnswer(number, claim.GuestToken, deadline.Token);
            var path = await ice.ConnectAsync(answer, deadline.Token);
            quic = new();
            return await quic.Connect(path, claim.Invite, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw NoPath(); }
    }

    private static CancellationTokenSource ConnectionDeadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        return deadline;
    }

    private static TimeoutException NoPath() => new("UDP で接続できませんでした。新しい部屋番号で再試行するか、LAN・VPN で手入力の直接接続を使用してください。");

    public async ValueTask DisposeAsync()
    {
        try { if (quic != null) await quic.DisposeAsync(); }
        finally { if (ice != null) await ice.DisposeAsync(); }
    }
}
