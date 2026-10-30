using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace TH12Coop;

internal sealed class LockstepPeer
{
    private readonly SecureChannel channel;
    private readonly SessionManifest manifest;
    private readonly byte role, otherRole;
    private readonly ulong epoch;
    private readonly object gate = new();
    private readonly Dictionary<uint, uint> remoteInputs = new();
    private readonly Dictionary<uint, GameDigest> localDigests = new(), remoteDigests = new();
    private sealed record DigestTrace(GameDigest Digest, uint[] Components);
    private readonly Queue<DigestTrace> trace = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NetBridge? bridge;
    private uint nextRemoteInput, nextRemoteDigest = 1;
    private uint confirmed;
    private string? sessionDirectory;
    private object? syncFailure;
    private bool gotReady;
    private double recordTokens = 1024, pingTokens = 8;
    private long budgetTimestamp = Stopwatch.GetTimestamp();
    public uint ConfirmedFrame { get { lock (gate) return confirmed; } }
    public uint CommittedFrame { get; private set; }
    public uint LastLocalMask { get; private set; }
    public int? GameProcessId { get; private set; }
    public bool StageObserved { get; private set; }
    public uint MaximumP1Shots { get; private set; }
    public uint MaximumP2Shots { get; private set; }
    public float MinimumP2X { get; private set; } = float.PositiveInfinity;
    public float MaximumP2X { get; private set; } = float.NegativeInfinity;

    public LockstepPeer(SecureChannel channel, SessionManifest manifest, byte role)
    {
        this.channel = channel; this.manifest = manifest; this.role = role; otherRole = role == 1 ? (byte)2 : (byte)1;
        epoch = BinaryPrimitives.ReadUInt64LittleEndian(channel.Session);
        nextRemoteInput = checked((uint)manifest.Delay + 1);
    }

    internal static void ValidateHeader(byte[] record, byte otherRole, ulong epoch)
    {
        if (record.Length < 16 || record[1] != otherRole || BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(2)) != SecureChannel.ProtocolVersion ||
            BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(4)) != epoch) throw new IOException("通信のセッションまたは役割が一致しません。");
    }

    private byte[] Record(byte kind, uint frame, int length)
    {
        var data = new byte[length]; data[0] = kind; data[1] = role;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), SecureChannel.ProtocolVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), frame);
        return data;
    }

    private void Compare(uint frame)
    {
        if (!localDigests.TryGetValue(frame, out var local) || !remoteDigests.TryGetValue(frame, out var remote)) return;
        if (local != remote)
        {
            syncFailure = new { frame, role, local, remote, localComponents = trace.FirstOrDefault(item => item.Digest.Frame == frame)?.Components };
            bridge?.Abort(3);
            throw new IOException($"フレーム {frame} でゲーム状態が一致しません。同期を停止しました。");
        }
        localDigests.Remove(frame); remoteDigests.Remove(frame);
        if (frame != confirmed + 1) throw new IOException("状態検証のフレーム番号が連続していません。");
        confirmed = frame;
    }

    private async Task WriteDiagnostic(string name, object value)
    {
        if (sessionDirectory == null) return;
        // Diagnostics are fixed-size summaries, written only after owned-game
        // cleanup. Disk failures cannot replace the primary stop reason.
        using var deadline = new CancellationTokenSource(500);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sessionDirectory, name),
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    internal void AcceptRecord(byte[] data)
    {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - budgetTimestamp) / (double)Stopwatch.Frequency;
            budgetTimestamp = now;
            recordTokens = Math.Min(1024, recordTokens + elapsed * 512) - 1;
            pingTokens = Math.Min(8, pingTokens + elapsed * 4);
            if (recordTokens < 0) throw new IOException("相手からの通信が頻度上限を超えました。");
            ValidateHeader(data, otherRole, epoch);
            var frame = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            switch (data[0])
            {
                case 0x10:
                    if (!gotReady || data.Length != 20 || frame != nextRemoteInput || frame > (bridge?.RequestedFrame ?? 1) + 120)
                        throw new IOException("入力の重複・順序違い・範囲外フレームを検出しました。");
                    var input = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
                    const uint allowed = 0x1fb;
                    if ((input & ~allowed) != 0) throw new IOException("未対応の入力を受信しました。");
                    lock (gate)
                    {
                        if (remoteInputs.Count >= 120 || !remoteInputs.TryAdd(frame, input)) throw new IOException("同期入力の保存範囲を超えました。");
                    }
                    nextRemoteInput++;
                    if (changed.CurrentCount == 0) try { changed.Release(); } catch (SemaphoreFullException) { }
                    break;
                case 0x11:
                    if (!gotReady || data.Length != 44 || frame != nextRemoteDigest || frame > (bridge?.CompletedFrame ?? 0) + 120)
                        throw new IOException("状態検証のフレームが不正です。");
                    var digest = new GameDigest(frame, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)),
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32)),
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40)));
                    lock (gate)
                    {
                        if (remoteDigests.Count >= 120 || !remoteDigests.TryAdd(frame, digest)) throw new IOException("状態検証の保存範囲を超えました。");
                        Compare(frame);
                    }
                    nextRemoteDigest++;
                    break;
                case 0x12:
                    if (data.Length != 16 || frame != 0 || gotReady) throw new IOException("開始メッセージが重複または不正です。");
                    gotReady = true; ready.TrySetResult();
                    break;
                case 0x13:
                    if (data.Length != 16 || frame != 0) throw new IOException("接続確認メッセージが不正です。");
                    if (--pingTokens < 0) throw new IOException("接続確認メッセージの頻度が高すぎます。");
                    break;
                case 0x14:
                    if (data.Length != 20 || frame != 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) is < 1 or > 5)
                        throw new IOException("終了メッセージが不正です。");
                    throw new IOException("相手がセッションを終了しました。");
                default: throw new IOException("未対応の同期メッセージです。");
            }
    }

    private async Task Receive(CancellationToken token)
    {
        while (true) AcceptRecord(await channel.Receive(token));
    }

    internal void AddLocalDigest(GameDigest digest)
    {
        lock (gate)
        {
            localDigests.Add(digest.Frame, digest);
            Compare(digest.Frame);
            if (digest.Frame + 1 - confirmed > 120) throw new IOException("相手のゲーム状態を確認できないため停止しました。");
        }
    }

    private void Pump(GameSession game, Func<uint, uint>? testInput, CancellationToken token)
    {
        var native = bridge ?? throw new InvalidOperationException();
        var scheduled = new Dictionary<uint, uint>();
        for (uint frame = 1; frame < uint.MaxValue - 256; ++frame)
        {
            token.ThrowIfCancellationRequested();
            if (game.HasExited) return;
            native.WaitRequested(frame, token);
            var cooperativeFrame = false;
            if (frame > 1)
            {
                var digest = native.Digest(frame - 1);
                cooperativeFrame = ((digest.Players >> 8) & 0xff) != 0xff;
                lock (gate)
                {
                    trace.Enqueue(new DigestTrace(digest, native.Components()));
                    while (trace.Count > 128) trace.Dequeue();
                }
                AddLocalDigest(digest);
                var message = Record(0x11, digest.Frame, 44);
                uint[] words = [digest.Low, digest.High, digest.Scene, digest.Stage, digest.Players, digest.GameFrame, digest.Rng];
                for (var index = 0; index < words.Length; ++index) BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16 + index * 4), words[index]);
                channel.Send(message, token).GetAwaiter().GetResult();
            }
            var future = checked(frame + (uint)manifest.Delay);
            var localInput = testInput?.Invoke(frame) ?? native.LocalInput(frame);
            if ((localInput & ~0x1fbu) != 0) throw new IOException("ゲームから未対応の入力を受信しました。");
            LastLocalMask = localInput;
            scheduled.Add(future, localInput);
            var inputMessage = Record(0x10, future, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(inputMessage.AsSpan(16), localInput);
            channel.Send(inputMessage, token).GetAwaiter().GetResult();
            uint local = 0, remote = 0;
            if (frame > manifest.Delay)
            {
                local = scheduled[frame]; scheduled.Remove(frame);
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    lock (gate) if (remoteInputs.Remove(frame, out remote)) break;
                    token.ThrowIfCancellationRequested();
                    // The other game's current frame may join a resource loader
                    // (30s bound). Five additional seconds cover input delivery.
                    if (wait.Elapsed > TimeSpan.FromSeconds(35)) throw new IOException($"フレーム {frame} の相手の入力が届かないため停止しました。");
                    changed.Wait(100, token);
                }
            }
            var snapshot = game.Snapshot();
            var hostInput = role == 1 ? local : remote;
            var guestInput = role == 1 ? remote : local;
            if (cooperativeFrame) hostInput |= guestInput & 0x100; // Membership is from the same completed simulation frame on both peers.
            native.Commit(frame, hostInput, guestInput & 0xfb);
            CommittedFrame = frame;
            if (snapshot.Status == 2)
            {
                StageObserved = true;
                MaximumP1Shots = Math.Max(MaximumP1Shots, snapshot.P1Shots);
                MaximumP2Shots = Math.Max(MaximumP2Shots, snapshot.P2Shots);
                MinimumP2X = Math.Min(MinimumP2X, snapshot.P2X);
                MaximumP2X = Math.Max(MaximumP2X, snapshot.P2X);
            }
        }
        throw new IOException("セッションのフレーム番号上限に達しました。");
    }

    public async Task Run(OnlineProfile profile, bool testInstance, Func<uint, uint>? testInput, Action<string> status, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(120));
        var started = false;
        GameSession? game = null;
        Task<GameSession>? starting = null;
        Task? pump = null;
        Exception? transportFailure = null;
        // A peer can disconnect while assets are still being prepared, before
        // the start/ready waits exist. Cancel that work as soon as receive ends.
        async Task ReceiveLifetime()
        {
            try { await Receive(lifetime.Token); }
            catch (Exception error)
            {
                if (!lifetime.IsCancellationRequested) Interlocked.CompareExchange(ref transportFailure, error, null);
                lifetime.Cancel();
                throw;
            }
        }
        var receiver = ReceiveLifetime();
        var ping = Task.Run(async () =>
        {
            try
            {
                while (!startup.IsCancellationRequested)
                { await channel.Send(Record(0x13, 0, 16), startup.Token); await Task.Delay(500, startup.Token); }
            }
            catch (OperationCanceledException) when (startup.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref transportFailure, error, null);
                lifetime.Cancel();
                throw;
            }
        });
        try
        {
            sessionDirectory = await profile.Prepare(channel.Session, role, startup.Token);
            await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "session.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), startup.Token);
            starting = GameSession.Start(sessionDirectory, true, startup.Token, profile.NativePath, status,
                new NetworkLaunch(manifest.Seed, testInstance, created => bridge = created), players: manifest.Players);
            if (await Task.WhenAny(starting, receiver) == receiver) await receiver;
            game = await starting;
            GameProcessId = game.Process.Id;
            if (!testInstance) Native.SetForegroundWindow(game.Window);
            await channel.Send(Record(0x12, 0, 16), lifetime.Token);
            var readyWait = ready.Task.WaitAsync(startup.Token);
            if (await Task.WhenAny(readyWait, receiver) == receiver) await receiver;
            await readyWait;
            started = true;
            startup.Cancel();
            try { await ping; } catch (OperationCanceledException) { }
            status($"2人でプレイ中 · 入力遅延 {manifest.Delay} フレーム");
            pump = Task.Factory.StartNew(() => Pump(game, testInput, lifetime.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var exited = game.Process.WaitForExitAsync(lifetime.Token);
            var finished = await Task.WhenAny(pump, receiver, exited);
            await finished;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref transportFailure) != null)
        {
            // Linked cancellation stops dependent work; preserve the transport
            // failure as the user-visible cause rather than calling it "cancel".
            ExceptionDispatchInfo.Capture(Volatile.Read(ref transportFailure)!).Throw();
            throw;
        }
        catch (OperationCanceledException) when (!started && startup.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("双方のゲームの開始準備が120秒以内に完了しませんでした。"); }
        finally
        {
            bridge?.Abort();
            using (var goodbye = new CancellationTokenSource(500))
            {
                try { var message = Record(0x14, 0, 20); BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), 1); await channel.Send(message, goodbye.Token); }
                catch (Exception) { }
            }
            lifetime.Cancel(); startup.Cancel(); channel.Dispose();
            if (game == null && starting != null)
            {
                try { game = await starting; } catch (Exception) when (lifetime.IsCancellationRequested) { }
            }
            try { if (game != null) await game.StopGameAsync(); }
            finally
            {
                foreach (var task in new[] { receiver, ping, pump })
                {
                    if (task == null) continue;
                    try { await task; } catch (Exception) when (lifetime.IsCancellationRequested) { }
                }
                game?.Dispose();
                if (started && !testInstance && sessionDirectory != null)
                {
                    try { await profile.PersistLocal(sessionDirectory, CancellationToken.None); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        status("このPCの設定を保存できませんでした。" + error.Message);
                        await WriteDiagnostic("local-settings-error.json", new { error = error.Message });
                    }
                }
                if (sessionDirectory != null)
                {
                    DigestTrace[] tail;
                    lock (gate) tail = trace.ToArray();
                    if (syncFailure != null) await WriteDiagnostic("sync-failure.json", syncFailure);
                    await WriteDiagnostic("digest-tail.json", tail);
                }
            }
        }
    }
}
