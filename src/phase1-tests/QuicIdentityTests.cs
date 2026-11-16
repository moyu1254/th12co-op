using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net.Quic;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;
using TH12Coop;

namespace TH12Coop.Phase1Tests;

internal static class QuicIdentityTests
{
    internal static async Task Run(CancellationToken token)
    {
        using var identity = RoomIdentity.Create();
        using var other = RoomIdentity.Create();
        var (pin, key) = RoomIdentity.Parse(identity.Invite);
        key[0] ^= 1;
        var wrongKey = "T12L6." + Base64Url.EncodeToString([..pin, ..key]);
        CryptographicOperations.ZeroMemory(key);
        await Rejected(identity, other.Invite, token);
        await Rejected(identity, wrongKey, token);
        Console.WriteLine("PASS QUIC rejects substituted certificate and wrong guest capability");
        await ExercisePath(identity, expectFailure: true, token);
        await ExercisePath(identity, expectFailure: false, token);
    }

    private static async Task ExercisePath(RoomIdentity identity, bool expectFailure, CancellationToken token)
    {
        var loopback = new Uri("https://127.0.0.1");
        await using var host = await UdpIcePeer.GatherAsync(true, loopback, token);
        await using var guest = await UdpIcePeer.GatherAsync(false, loopback, token);
        var paths = await Task.WhenAll(host.ConnectAsync(guest.Description, token), guest.ConnectAsync(host.Description, token));
        await using var hostQuic = new IceQuicConnection();
        await using var guestQuic = new IceQuicConnection();
        var accepting = hostQuic.Accept(paths[0], identity, token);
        using var guestChannel = await guestQuic.Connect(paths[1], identity.Invite, token);
        using var hostChannel = await accepting;
        if (expectFailure)
        {
            var blocked = hostChannel.Receive(token);
            paths[0].Close(); // The ICE reader ends while an application read is pending.
            try
            {
                await blocked.WaitAsync(TimeSpan.FromSeconds(3), token);
                throw new InvalidOperationException("Pending QUIC read survived the loss of its ICE path");
            }
            catch (IOException failure) when (failure.InnerException is ChannelClosedException) { }
            Console.WriteLine("PASS ICE pump failure immediately reaches the pending QUIC read with its cause");
            return;
        }

        // Exceed the 45-second transport idle timeout before starting an application read.
        await Task.Delay(TimeSpan.FromSeconds(47), token);
        var goodbye = new byte[20];
        goodbye[0] = 0x14;
        goodbye[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(goodbye.AsSpan(2), SecureChannel.ProtocolVersion);
        hostChannel.Session.AsSpan(0, 8).CopyTo(goodbye.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(goodbye.AsSpan(16), 1);
        var received = guestChannel.Receive(token);
        await hostChannel.Send(goodbye, token);
        hostChannel.Dispose();
        var closing = hostQuic.DisposeAsync().AsTask();
        if (!(await received.WaitAsync(TimeSpan.FromSeconds(3), token)).SequenceEqual(goodbye))
            throw new InvalidOperationException("Peer lost the final goodbye record");
        await closing.WaitAsync(TimeSpan.FromSeconds(6), token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                await guestChannel.Send([1], limit.Token);
                await Task.Delay(50, limit.Token);
            }
        }
        catch (QuicException closed) when (closed.QuicError == QuicError.ConnectionAborted && closed.ApplicationErrorCode == 0) { }
        Console.WriteLine("PASS QUIC survives 47 seconds idle and delivers goodbye before a code-zero connection close");
    }

    private static async Task Rejected(RoomIdentity identity, string invite, CancellationToken token)
    {
        var loopback = new Uri("https://127.0.0.1");
        await using var host = await UdpIcePeer.GatherAsync(true, loopback, token);
        await using var guest = await UdpIcePeer.GatherAsync(false, loopback, token);
        var paths = await Task.WhenAll(host.ConnectAsync(guest.Description, token), guest.ConnectAsync(host.Description, token));
        await using var hostQuic = new IceQuicConnection();
        await using var guestQuic = new IceQuicConnection();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var accepting = hostQuic.Accept(paths[0], identity, stop.Token);
        try
        {
            using var unexpected = await guestQuic.Connect(paths[1], invite, token);
            throw new InvalidOperationException("Guest accepted an invalid QUIC identity");
        }
        catch (Exception e) when (e is AuthenticationException or IOException) { }
        finally { stop.Cancel(); }
        try
        {
            using var unexpected = await accepting;
            throw new InvalidOperationException("Host admitted an unauthenticated guest");
        }
        catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException) { }
    }
}
