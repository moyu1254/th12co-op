using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace TH12Coop;

internal static class OnlineSession
{
    public static async Task Host(string gameDirectory, int port, RoomIdentity identity, int delay, Action<string> status, CancellationToken token,
        string? testNativePath = null, bool testInstance = false, Func<uint, uint>? testInput = null, Action<LockstepPeer>? observe = null)
    {
        if (delay is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(delay));
        status("ゲームとMODのバージョンを確認しています");
        var profile = await OnlineProfile.Read(gameDirectory, token, testNativePath);
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start(8);
        try
        {
            status($"接続待ち · TCP {port}");
            using var channel = await AcceptGuest(listener, identity, token);
            listener.Stop();
            var manifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, delay,
                (uint)RandomNumberGenerator.GetInt32(1, 65536), Convert.ToHexString(channel.Session), profile.Assets);
            var offer = OnlineProfile.Encode(manifest);
            await channel.Send(offer, token);
            var acknowledgement = await channel.Receive(token);
            if (acknowledgement.Length != 33 || acknowledgement[0] != 2 ||
                !CryptographicOperations.FixedTimeEquals(acknowledgement.AsSpan(1), SHA256.HashData(offer)))
                throw new IOException("相手のゲーム・MOD確認に失敗しました。");
            await channel.Send([3], token);
            var peer = new LockstepPeer(channel, manifest, 1);
            observe?.Invoke(peer);
            await peer.Run(profile, testInstance, testInput, status, token);
        }
        finally { listener.Stop(); }
    }

    public static async Task Join(string gameDirectory, string host, int port, string invite, Action<string> status, CancellationToken token,
        string? testNativePath = null, bool testInstance = false, Func<uint, uint>? testInput = null, Action<LockstepPeer>? observe = null)
    {
        status("ゲームとMODのバージョンを確認しています");
        var profile = await OnlineProfile.Read(gameDirectory, token, testNativePath);
        using var channel = await SecureChannel.Connect(host, port, invite, token);
        var offer = await channel.Receive(token);
        if (offer.Length < 2 || offer[0] != 1) throw new IOException("ゲーム開始情報が不正です。");
        SessionManifest manifest;
        try { manifest = JsonSerializer.Deserialize<SessionManifest>(offer.AsSpan(1)) ?? throw new JsonException(); }
        catch (JsonException) { throw new IOException("ゲーム開始情報を読み取れません。"); }
        profile.Validate(manifest, channel.Session);
        await channel.Send(new byte[] { 2 }.Concat(SHA256.HashData(offer)).ToArray(), token);
        var start = await channel.Receive(token);
        if (start.Length != 1 || start[0] != 3) throw new IOException("ゲーム開始の確認に失敗しました。");
        var peer = new LockstepPeer(channel, manifest, 2);
        observe?.Invoke(peer);
        await peer.Run(profile, testInstance, testInput, status, token);
    }

    internal static async Task<SecureChannel> AcceptGuest(TcpListener listener, RoomIdentity identity, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var slots = new SemaphoreSlim(8);
        var authenticated = Channel.CreateBounded<SecureChannel>(1);
        var attempts = new List<Task>();
        var accept = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                var raw = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (!slots.Wait(0)) { raw.Dispose(); continue; }
                var attempt = Task.Run(async () =>
                {
                    try
                    {
                        var peer = await SecureChannel.Accept(raw, identity, lifetime.Token);
                        if (!authenticated.Writer.TryWrite(peer)) peer.Dispose();
                    }
                    catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException)
                    { raw.Dispose(); }
                    finally { slots.Release(); }
                });
                attempts.RemoveAll(task => task.IsCompletedSuccessfully);
                attempts.Add(attempt);
            }
        });
        try
        {
            var incoming = authenticated.Reader.ReadAsync(token).AsTask();
            if (await Task.WhenAny(incoming, accept) == accept) await accept;
            return await incoming;
        }
        finally
        {
            lifetime.Cancel();
            try { await accept; } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
            await Task.WhenAll(attempts);
            while (authenticated.Reader.TryRead(out var unused)) unused.Dispose();
        }
    }
}
