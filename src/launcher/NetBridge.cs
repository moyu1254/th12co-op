using System.IO.MemoryMappedFiles;

namespace TH12Coop;

internal readonly record struct GameDigest(uint Frame, uint Low, uint High, uint Scene, uint Stage, uint Players, uint GameFrame, uint Rng);

internal sealed class NetBridge : IDisposable
{
    internal const uint Magic = 0x4e323154, Version = 1, Size = 12544, Capacity = 256, RulesVersion = 4;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor memory;
    private readonly System.Threading.Timer heartbeat;
    private readonly EventWaitHandle request;
    private readonly EventWaitHandle commit;
    private readonly object abortGate = new();
    private readonly LocalInputReader? localInput;
    private int disposed;
    private uint lastCommitted;
    public uint RequestedFrame => memory.ReadUInt32(36);
    public uint ConsumedFrame => memory.ReadUInt32(40);
    public uint CompletedFrame => memory.ReadUInt32(44);
    public uint Status => memory.ReadUInt32(32);
    public uint Error => memory.ReadUInt32(56);
    public uint[] Components() => Enumerable.Range(0, 6).Select(index => memory.ReadUInt32(96 + index * 4)).ToArray();
    public bool DeviceReady => memory.ReadUInt32(88) == 1;

    public NetBridge(int pid, uint seed, bool testInstance, uint primaryThreadId = 0, InputBindingProfile? input = null)
    {
        if (input != null) localInput = new LocalInputReader(input, pid);
        mapping = MemoryMappedFile.CreateNew($"Local\\TH12CoopNet_{pid}", Size);
        memory = mapping.CreateViewAccessor(0, Size);
        request = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\TH12CoopNetRequest_{pid}");
        commit = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\TH12CoopNetCommit_{pid}");
        memory.Write(0, Magic); memory.Write(4, Version); memory.Write(8, Size); memory.Write(12, Capacity);
        memory.Write(16, testInstance ? 3u : 1u); memory.Write(20, seed); memory.Write(76, RulesVersion);
        memory.Write(92, primaryThreadId);
        memory.Write(24, unchecked((uint)Environment.TickCount));
        heartbeat = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref disposed) == 0) memory.Write(24, unchecked((uint)Environment.TickCount));
        }, null, 0, 100);
    }

    public void WaitRequested(uint frame, CancellationToken token)
    {
        if (Status == 4) throw new IOException($"ゲームの同期処理が停止しました（コード {Error}）。");
        var deadline = Environment.TickCount64 + 35000;
        while (RequestedFrame < frame)
        {
            token.ThrowIfCancellationRequested();
            if (Status == 4) throw new IOException($"ゲームの同期処理が停止しました（コード {Error}）。");
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"ゲームのフレーム {frame} を待機中にタイムアウトしました。");
            WaitHandle.WaitAny([request, token.WaitHandle], 100);
        }
        if (Status == 4) throw new IOException($"ゲームの同期処理が停止しました（コード {Error}）。");
        if (RequestedFrame != frame) throw new IOException("ゲームのフレーム番号が連続していません。");
    }

    public uint LocalInput(uint frame)
    {
        var sequence = memory.ReadUInt32(84);
        Thread.MemoryBarrier();
        var mask = memory.ReadUInt32(80);
        Thread.MemoryBarrier();
        if (sequence != frame || memory.ReadUInt32(84) != sequence) throw new IOException("ゲーム入力のフレーム番号が一致しません。");
        if ((mask & ~0x1fbu) != 0) throw new IOException("ゲームから未対応の入力を受信しました。");
        return localInput?.Read() ?? mask;
    }

    public void Commit(uint frame, uint p1, uint p2)
    {
        if (Status == 4 || memory.ReadUInt32(28) != 0 || frame == 0 || frame != lastCommitted + 1 || frame != RequestedFrame || frame != ConsumedFrame + 1 ||
            (p1 & ~0x1fbu) != 0 || (p2 & ~0xfbu) != 0)
            throw new IOException("ゲームへ渡す同期入力が不正です。");
        var offset = 256 + (frame % Capacity) * 16;
        memory.Write(offset + 4, p1); memory.Write(offset + 8, p2); memory.Write(offset + 12, 0u);
        Thread.MemoryBarrier();
        memory.Write(offset, frame);
        lastCommitted = frame;
        commit.Set();
    }

    public GameDigest Digest(uint frame)
    {
        if (frame == 0) throw new ArgumentOutOfRangeException(nameof(frame));
        var offset = 4352 + (frame % Capacity) * 32;
        var sequence = memory.ReadUInt32(offset);
        Thread.MemoryBarrier();
        var digest = new GameDigest(sequence, memory.ReadUInt32(offset + 4), memory.ReadUInt32(offset + 8),
            memory.ReadUInt32(offset + 12), memory.ReadUInt32(offset + 16), memory.ReadUInt32(offset + 20),
            memory.ReadUInt32(offset + 24), memory.ReadUInt32(offset + 28));
        Thread.MemoryBarrier();
        if (sequence != frame || sequence != memory.ReadUInt32(offset)) throw new IOException("ゲーム状態のフレーム番号が一致しません。");
        return digest;
    }

    public void Abort(uint code = 1)
    {
        lock (abortGate)
        {
            if (disposed != 0) return;
            // A generic cleanup must not erase the original desync/error code.
            if (memory.ReadUInt32(28) == 0) memory.Write(28, code == 0 ? 1u : code);
            commit.Set(); request.Set();
        }
    }

    public void Dispose()
    {
        lock (abortGate)
        {
            if (disposed != 0) return;
            Abort();
            Volatile.Write(ref disposed, 1);
        }
        using var stopped = new ManualResetEvent(false);
        if (heartbeat.Dispose(stopped)) stopped.WaitOne();
        memory.Dispose(); mapping.Dispose(); request.Dispose(); commit.Dispose();
    }
}
