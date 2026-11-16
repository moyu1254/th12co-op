using System.Buffers.Text;
using System.Security.Authentication;
using System.Security.Cryptography;
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
