using System.Diagnostics;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace TH12Coop;

internal sealed record NetworkLaunch(uint Seed, bool TestInstance, Action<NetBridge> BridgeCreated);

internal sealed class GameSession : IDisposable
{
    public Process Process { get; }
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor memory;
    private readonly System.Threading.Timer? inputTimer;
    private readonly OwnedGameJob ownership;
    public NetBridge? Network { get; }
    private int disposed;
    public IntPtr Window
    {
        get
        {
            var foreground = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(foreground, out var owner);
            if (owner == Process.Id) return foreground;
            Process.Refresh();
            return Process.MainWindowHandle;
        }
    }
    public uint Status => memory.ReadUInt32(20);
    public bool HasExited => Process.HasExited;
    internal (uint Status, uint Frame, float P1X, float P1Y, float P2X, float P2Y, uint P1Shots, uint P2Shots) Snapshot()
        => (memory.ReadUInt32(20), memory.ReadUInt32(24), memory.ReadSingle(28), memory.ReadSingle(32), memory.ReadSingle(36), memory.ReadSingle(40), memory.ReadUInt32(56), memory.ReadUInt32(60));

    private GameSession(Process process, MemoryMappedFile mapping, NetBridge? network, OwnedGameJob ownership)
    {
        Process = process;
        this.mapping = mapping;
        this.ownership = ownership;
        Network = network;
        memory = mapping.CreateViewAccessor(0, 64);
        memory.Write(16, network != null ? 0u : 1u);
        if (network == null) inputTimer = new System.Threading.Timer(UpdateInputs, null, 0, 8);
    }

    internal sealed class AlreadyRunningException(string message) : IOException(message);

    internal static int[] RunningGameIds()
    {
        var running = new List<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("th12"))
        {
            using (process)
            {
                try { if (!process.HasExited) running.Add(process.Id); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { running.Add(process.Id); }
            }
        }
        return running.ToArray();
    }

    private static void RequireNoRunningGame()
    {
        // Retail startup uses this exact named mutex before loading th12.cfg.
        // Open it read-only and close immediately: creating or retaining it
        // would itself make the original game reject startup.
        var mutex = Native.OpenMutex(0x00100000, false, "Touhou 12 App");
        var mutexError = mutex == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        var gameMutexExists = mutex != IntPtr.Zero || mutexError == 5;
        if (mutex != IntPtr.Zero) Native.CloseHandle(mutex);
        var existing = RunningGameIds();
        if (gameMutexExists || existing.Length != 0)
        {
            var processDetails = existing.Length == 0 ? "" : $"（PID: {string.Join(", ", existing)}）";
            throw new AlreadyRunningException($"TH12がすでに起動しています{processDetails}。開いているTH12を終了してから、もう一度開始してください。");
        }
    }

    public static async Task<GameSession> Start(string gameDirectory, bool online, CancellationToken token, string? testNativePath = null,
        Action<string>? startupStatus = null, NetworkLaunch? network = null)
    {
        // Serialize our launchers across processes. A semaphore can be released
        // after asynchronous waits without the thread affinity of a mutex.
        using var startup = new Semaphore(1, 1, network?.TestInstance == true ? null : "Local\\TH12CoopStartup");
        if (!startup.WaitOne(0)) throw new AlreadyRunningException("別のTH12 CO-OPがゲームを起動中です。起動が完了するまでお待ちください。");
        try { return await StartCore(gameDirectory, online, token, testNativePath, startupStatus, network); }
        finally { startup.Release(); }
    }

    private static async Task<GameSession> StartCore(string gameDirectory, bool online, CancellationToken token, string? testNativePath,
        Action<string>? startupStatus, NetworkLaunch? network)
    {
        if (network?.TestInstance != true) RequireNoRunningGame();
        var configuration = ConfigurationSnapshot.Read(Path.Combine(gameDirectory, "th12.cfg"));
        var exe = Path.Combine(gameDirectory, "th12.exe");
        var dll = testNativePath ?? Path.Combine(AppContext.BaseDirectory, "th12coop-native.dll");
        var injector = Path.Combine(AppContext.BaseDirectory, "injector.exe");
        foreach (var file in new[] { exe, dll, injector })
            if (!File.Exists(file)) throw new FileNotFoundException("必要なファイルが見つかりません。build.ps1を実行してください。", file);
        var build = await GameBuild.Identify(gameDirectory, token);
        await GameBuild.Assets(gameDirectory, build, token);
        if (build.Steam)
        {
            var appIdPath = Path.Combine(gameDirectory, "steam_appid.txt");
            if (!File.Exists(appIdPath) || (await File.ReadAllTextAsync(appIdPath, token)).Trim() != GameBuild.SteamAppId)
                throw new IOException("Steam版のMOD用コピーを準備してください。Steamにログインしてから起動します。");
        }
        token.ThrowIfCancellationRequested();
        if (network?.TestInstance != true) RequireNoRunningGame();
        startupStatus?.Invoke("ゲームを起動しています");
        IntPtr primaryThread = IntPtr.Zero;
        uint primaryThreadId = 0;
        // All modes install hooks before any game/CRT instruction executes.
        // Waiting for a title window would leave the primary thread racing
        // multi-byte native patch writes, even when no stage has started.
        var startupInfo = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>() };
        if (!Native.CreateProcess(exe, new StringBuilder('"' + exe + '"'), IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero,
            gameDirectory, ref startupInfo, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error(), "ゲームを停止状態で起動できませんでした。");
        Process process;
        OwnedGameJob? ownership = null;
        try
        {
            ownership = OwnedGameJob.AssignSuspended(information.Process);
            process = System.Diagnostics.Process.GetProcessById((int)information.ProcessId);
        }
        catch
        {
            Native.TerminateProcess(information.Process, 1);
            ownership?.Dispose();
            Native.CloseHandle(information.Thread);
            throw;
        }
        finally { Native.CloseHandle(information.Process); }
        primaryThread = information.Thread;
        primaryThreadId = information.ThreadId;
        MemoryMappedFile? map = null;
        MemoryMappedFile? boot = null;
        NetBridge? bridge = null;
        var initialized = false;
        try
        {
            var deadline = Stopwatch.StartNew();
            boot = MemoryMappedFile.CreateNew($"Local\\TH12CoopBoot_{process.Id}", 16);
            using (var bootHeader = boot.CreateViewAccessor(0, 16))
            {
                bootHeader.Write(0, 0x42323154u); bootHeader.Write(4, 1u);
                bootHeader.Write(8, primaryThreadId); bootHeader.Write(12, 1u);
            }
            if (network != null)
            {
                bridge = new NetBridge(process.Id, network.Seed, network.TestInstance, primaryThreadId);
                network.BridgeCreated(bridge);
            }
            var injection = new ProcessStartInfo(injector)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            injection.ArgumentList.Add("--early"); injection.ArgumentList.Add(process.Id.ToString()); injection.ArgumentList.Add(primaryThreadId.ToString());
            injection.ArgumentList.Add(dll);
            using var inject = System.Diagnostics.Process.Start(injection) ?? throw new IOException("MODローダーを起動できませんでした。");
            var errors = inject.StandardError.ReadToEndAsync(token);
            await inject.WaitForExitAsync(token);
            if (inject.ExitCode != 0) throw new IOException("MODの読み込みに失敗しました。" + await errors);
            deadline.Restart();
            while (map == null)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("MOD初期化中にゲームが終了しました。");
                try { map = MemoryMappedFile.OpenExisting($"Local\\TH12Coop_{process.Id}"); }
                catch (FileNotFoundException)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("MODからの応答がありません。");
                    await Task.Delay(50, token);
                }
            }
            using (var check = map.CreateViewAccessor(0, 64))
            {
                while (check.ReadUInt32(20) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("MOD初期化中にゲームが終了しました。");
                    if (deadline.Elapsed > TimeSpan.FromSeconds(10)) { map.Dispose(); throw new IOException("MODの初期化がタイムアウトしました。"); }
                    await Task.Delay(50, token);
                }
                if (check.ReadUInt32(0) != 0x50323154 || check.ReadUInt32(4) != 1 || check.ReadUInt32(20) is not (1 or 5))
                {
                    map.Dispose();
                    throw new IOException("このゲームのコードにはMODを適用できません。元の実行ファイルと一致する版が必要です。");
                }
            }
            if (bridge != null)
            {
                while (bridge.Status == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("同期MODの起動準備が完了しませんでした。");
                    await Task.Delay(10, token);
                }
                if (bridge.Status is not (1 or 5)) throw new IOException($"同期MODを初期化できませんでした（状態 {bridge.Status}、コード {bridge.Error}）。");
            }
            if (Native.ResumeThread(primaryThread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "ゲームの起動を再開できませんでした。");
            Native.CloseHandle(primaryThread); primaryThread = IntPtr.Zero;
            startupStatus?.Invoke("ゲームの初期化を待っています。表示モードの画面が開いた場合は選択してください。");
            deadline.Restart();
            using (var check = map.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (bridge?.Status == 4) throw new IOException($"同期MODの初期化に失敗しました（コード {bridge.Error}）。");
                    var nativeStatus = check.ReadUInt32(20);
                    if ((nativeStatus & 0x80000000) != 0)
                        throw new IOException($"MODの初期化に失敗しました（状態 0x{nativeStatus:X8}）。");
                    if (process.HasExited) throw new IOException(build.Steam ? $"Steam版が初期化中に終了しました（0x{process.ExitCode:X8}）。SteamのログインとTH12の利用権を確認してください。" : $"ゲームが初期化中に終了しました（終了コード: 0x{process.ExitCode:X8}）。");
                    if (bridge?.DeviceReady ?? DeviceInitialized(process.Id)) break;
                    if (deadline.Elapsed > TimeSpan.FromSeconds(120)) throw new IOException("ゲームの初期化がタイムアウトしました。表示モードの選択やゲームのエラー画面を確認してください。");
                    await Task.Delay(100, token);
                }
            }
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("ゲームが初期化中に終了しました。");
            initialized = true;
            var session = new GameSession(process, map, bridge, ownership);
            map = null;
            bridge = null;
            ownership = null;
            return session;
        }
        catch (Exception error)
        {
            map?.Dispose();
            bridge?.Abort();
            try
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (!initialized) configuration.RestoreAfterFailedStartup();
            }
            catch (Exception restoreError)
            {
                throw new IOException("ゲームの起動に失敗し、起動前の設定への復元も完了できませんでした。" + restoreError.Message,
                    new AggregateException(error, restoreError));
            }
            finally { bridge?.Dispose(); process.Dispose(); }
            throw;
        }
        finally
        {
            boot?.Dispose();
            ownership?.Dispose();
            if (primaryThread != IntPtr.Zero) Native.CloseHandle(primaryThread);
        }
    }

    private static bool DeviceInitialized(int processId)
    {
        try
        {
            using var readiness = MemoryMappedFile.OpenExisting($"Local\\TH12CoopReady_{processId}", MemoryMappedFileRights.Read);
            using var header = readiness.CreateViewAccessor(0, 16, MemoryMappedFileAccess.Read);
            return header.ReadUInt32(0) == 0x52323154 && header.ReadUInt32(4) == 1 && header.ReadUInt32(8) == 1;
        }
        catch (FileNotFoundException) { return false; }
    }

    private sealed class ConfigurationSnapshot(string path, byte[]? contents)
    {
        private static bool Valid(byte[] bytes) => bytes.Length == 60 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x00120001;

        internal static ConfigurationSnapshot Read(string path)
        {
            if (!File.Exists(path)) return new ConfigurationSnapshot(path, null);
            var contents = File.ReadAllBytes(path);
            if (!Valid(contents))
                throw new IOException($"設定ファイルが破損しているため起動できません。ゲームを終了し、正常な製品版のth12.cfgを次の場所へ復元してください: {path}");
            return new ConfigurationSnapshot(path, contents);
        }

        internal void RestoreAfterFailedStartup()
        {
            var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (contents == null)
            {
                // A fresh game can create its defaults. Remove only a new,
                // invalid configuration produced by a failed initialization.
                if (current != null && !Valid(current)) File.Delete(path);
                return;
            }
            if (current != null && current.AsSpan().SequenceEqual(contents)) return;
            var temporary = path + ".coop-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(contents); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private void UpdateInputs(object? _)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foregroundProcess);
            var focused = foregroundProcess == Process.Id;
            memory.Write(8, focused ? ReadPlayerOne() : 0u);
            memory.Write(12, focused ? ReadPlayerTwo() : 0u);
            memory.Write(44, unchecked((uint)Environment.TickCount));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public static uint ReadPlayerOne() => KeysToMask(Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Z, Keys.X, Keys.LShiftKey);
    private static uint ReadPlayerTwo() => KeysToMask(Keys.I, Keys.K, Keys.J, Keys.L, Keys.F, Keys.G, Keys.D);
    private static uint KeysToMask(params Keys[] keys)
    {
        uint result = 0;
        uint[] masks = [0x10, 0x20, 0x40, 0x80, 1, 2, 8];
        for (var i = 0; i < keys.Length; ++i) if ((Native.GetAsyncKeyState((int)keys[i]) & 0x8000) != 0) result |= masks[i];
        return result;
    }

    public void StopGame()
    {
        Network?.Abort();
        try { if (!Process.HasExited) Process.Kill(); }
        finally { ownership.Dispose(); }
    }

    public async Task StopGameAsync()
    {
        StopGame();
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ownership.Dispose();
        using var stopped = new ManualResetEvent(false);
        if (inputTimer?.Dispose(stopped) == true) stopped.WaitOne(2000);
        memory.Write(8, 0u);
        memory.Write(12, 0u);
        memory.Write(16, 0u);
        memory.Dispose();
        mapping.Dispose();
        Network?.Dispose();
        Process.Dispose();
    }
}

internal static class Native
{
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr OpenMutex(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcess(string application, StringBuilder command,
        IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, XCharacters, YCharacters, Fill, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, Input, Output, Error;
    }
}
