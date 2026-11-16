using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace TH12Coop;

internal static class OnlineSession
{
    public static Task Host(string gameDirectory, int port, RoomIdentity identity, int delay, Action<string> status, CancellationToken token,
        string? testNativePath = null, bool testInstance = false, Func<uint, uint>? testInput = null, Action<LockstepPeer>? observe = null, int? localCharacter = null)
    {
        async Task<SecureChannel> Connect()
        {
            using var listener = Listen(port);
            status($"接続待ち · TCP {port}");
            return await AcceptGuest(listener, identity, token);
        }
        return Play(gameDirectory, Connect, null, 1, delay, status, token, testNativePath, testInstance, testInput, observe, localCharacter);
    }

    public static Task Join(string gameDirectory, string host, int port, string invite, Action<string> status, CancellationToken token,
        string? testNativePath = null, bool testInstance = false, Func<uint, uint>? testInput = null, Action<LockstepPeer>? observe = null, int? localCharacter = null)
        => Play(gameDirectory, () => SecureChannel.Connect(host, port, invite, token), null, 2, 0, status, token,
            testNativePath, testInstance, testInput, observe, localCharacter);

    public static async Task HostNumber(string gameDirectory, RoomIdentity identity, int delay, Action<string> roomIssued,
        Func<string, bool, CancellationToken, Task<bool>> confirm, Action<string> status, CancellationToken token,
        RendezvousClient? service = null, string? testNativePath = null, bool testInstance = false, Func<uint, uint>? testInput = null,
        Action<LockstepPeer>? observe = null, Func<IPEndPoint, CancellationToken, Task<IAsyncDisposable?>>? openMapping = null, int? localCharacter = null)
    {
        using var configured = service == null ? RendezvousClient.Configured() : null;
        await using var transport = new NumberedTransport(service ?? configured!, openMapping);
        await Play(gameDirectory, () => transport.Host(identity, roomIssued, status, token),
            (code, ct) => confirm(code, transport.UsesRelay, ct), 1, delay, status, token, testNativePath, testInstance,
            testInput, observe, localCharacter);
    }

    public static async Task JoinNumber(string gameDirectory, string number, Func<string, bool, CancellationToken, Task<bool>> confirm,
        Action<string> status, CancellationToken token, RendezvousClient? service = null, string? testNativePath = null,
        bool testInstance = false, Func<uint, uint>? testInput = null, Action<LockstepPeer>? observe = null, int? localCharacter = null)
    {
        using var configured = service == null ? RendezvousClient.Configured() : null;
        await using var transport = new NumberedTransport(service ?? configured!);
        await Play(gameDirectory, () => transport.Join(number, status, token),
            (code, ct) => confirm(code, transport.UsesRelay, ct), 2, 0, status, token, testNativePath, testInstance,
            testInput, observe, localCharacter);
    }

    private static TcpListener Listen(int port, IPAddress? address = null)
    {
        var listener = new TcpListener(address ?? IPAddress.IPv6Any, port);
        try
        {
            if (address == null) listener.Server.DualMode = true;
            listener.Start(8);
            return listener;
        }
        catch { listener.Dispose(); throw; }
    }

    private static async Task Play(string gameDirectory, Func<Task<SecureChannel>> connect,
        Func<string, CancellationToken, Task<bool>>? confirm, byte role, int delay, Action<string> status, CancellationToken token,
        string? testNativePath, bool testInstance, Func<uint, uint>? testInput, Action<LockstepPeer>? observe, int? localCharacter)
    {
        if (role == 1 && delay is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(delay));
        ValidateCharacter(localCharacter);
        status("ゲームとMODのバージョンを確認しています");
        var profile = await OnlineProfile.Read(gameDirectory, token, testNativePath);
        using var channel = await connect();
        if (confirm != null) await ConfirmPeer(channel, confirm, token);
        var manifest = role == 1
            ? await NegotiateHost(channel, profile.Assets, delay, localCharacter, token)
            : await NegotiateGuest(channel, profile.Assets, localCharacter, token);
        var peer = new LockstepPeer(channel, manifest, role);
        observe?.Invoke(peer);
        await peer.Run(profile, testInstance, testInput, status, token);
    }

    internal static async Task ConfirmPeer(SecureChannel channel, Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        var code = channel.VerificationCode ?? throw new IOException("照合が必要な接続です。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        async Task WaitForPeer()
        {
            var reply = await channel.Receive(deadline.Token);
            if (reply.Length != 2 || reply[0] != 5 || reply[1] != 1)
                throw new IOException("相手が照合を承認しませんでした。");
        }
        var remote = WaitForPeer();
        Task<bool>? local = null;
        try
        {
            local = confirm(code, deadline.Token);
            var guarded = local.WaitAsync(deadline.Token);
            if (await Task.WhenAny(remote, guarded) == remote) await remote;
            bool matches = await guarded;
            await channel.Send([5, matches ? (byte)1 : (byte)0], deadline.Token);
            if (!matches) throw new IOException("照合を中止しました。");
            await remote;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("相手との照合が40秒以内に完了しませんでした。"); }
        finally
        {
            deadline.Cancel();
            await remote.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (local != null) await ((Task)local).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    internal static async Task<SessionManifest> NegotiateHost(SecureChannel channel, AssetProfile assets, int delay, int? localCharacter, CancellationToken token)
    {
        if (delay is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(delay));
        ValidateCharacter(localCharacter);
        var guestCharacter = DecodeCharacter(await channel.Receive(token));
        if (localCharacter.HasValue != guestCharacter.HasValue) throw new IOException("機体選択方式が一致しません。双方で同じランチャーから開始してください。");
        var players = localCharacter.HasValue ? new PlayerSelection(localCharacter.Value, guestCharacter!.Value) : null;
        var manifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, delay,
            (uint)RandomNumberGenerator.GetInt32(1, 65536), Convert.ToHexString(channel.Session), assets, players);
        var offer = OnlineProfile.Encode(manifest);
        await channel.Send(offer, token);
        var acknowledgement = await channel.Receive(token);
        if (acknowledgement.Length != 33 || acknowledgement[0] != 2 ||
            !CryptographicOperations.FixedTimeEquals(acknowledgement.AsSpan(1), SHA256.HashData(offer)))
            throw new IOException("相手のゲーム・MOD確認に失敗しました。");
        await channel.Send([3], token);
        return manifest;
    }

    internal static async Task<SessionManifest> NegotiateGuest(SecureChannel channel, AssetProfile assets, int? localCharacter, CancellationToken token)
    {
        ValidateCharacter(localCharacter);
        await channel.Send(EncodeCharacter(localCharacter), token);
        var offer = await channel.Receive(token);
        if (offer.Length < 2 || offer[0] != 1) throw new IOException("ゲーム開始情報が不正です。");
        SessionManifest manifest;
        try { manifest = JsonSerializer.Deserialize<SessionManifest>(offer.AsSpan(1)) ?? throw new JsonException(); }
        catch (JsonException) { throw new IOException("ゲーム開始情報を読み取れません。"); }
        OnlineProfile.ValidateRules(manifest, channel.Session);
        OnlineProfile.ValidateAssets(assets, manifest.Assets);
        if (manifest.Players?.PlayerTwo != localCharacter) throw new IOException("相手が確認した自分の機体選択が一致しません。");
        await channel.Send(new byte[] { 2 }.Concat(SHA256.HashData(offer)).ToArray(), token);
        var start = await channel.Receive(token);
        if (start.Length != 1 || start[0] != 3) throw new IOException("ゲーム開始の確認に失敗しました。");
        return manifest;
    }

    internal static void ValidateCharacter(int? character)
    {
        if (character is < 0 or > 5) throw new IOException("機体・装備の選択が正しくありません。");
    }

    internal static byte[] EncodeCharacter(int? character)
    {
        ValidateCharacter(character);
        return [4, character.HasValue ? (byte)character.Value : (byte)255];
    }

    internal static int? DecodeCharacter(byte[] selection)
    {
        if (selection.Length != 2 || selection[0] != 4 || (selection[1] > 5 && selection[1] != 255))
            throw new IOException("相手の機体選択が正しくありません。");
        return selection[1] == 255 ? null : selection[1];
    }

    internal static async Task<SecureChannel> AcceptGuest(TcpListener listener, RoomIdentity identity, CancellationToken token, bool numbered = false)
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
                        var peer = await SecureChannel.Accept(raw, identity, lifetime.Token, numbered);
                        if (!authenticated.Writer.TryWrite(peer)) peer.Dispose();
                    }
                    catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or TimeoutException)
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
