using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace TH12Coop;

internal sealed record QuietTestEvidence(uint Devices, uint Buffers, uint Plays, uint Failures,
    uint CreatedWindows, uint BlockedDialogs, uint GraphicsDevices, int VisibleOwnedWindows, bool OwnsForeground)
{
    internal bool Passed => Devices > 0 && Buffers > 0 && GraphicsDevices > 0 && Failures == 0 && VisibleOwnedWindows == 0 && !OwnsForeground;
}

// The pre-entry guard creates only invisible, nonactivating windows and prevents
// D3D9 from changing focus. The current desktop is needed by hardware D3D9;
// an inactive desktop can block inside the graphics driver during CreateDevice.
internal sealed class QuietTestEnvironment : IDisposable
{
    internal string DesktopName { get; }

    internal QuietTestEnvironment()
    {
        var guard = Path.Combine(AppContext.BaseDirectory, "th12coop-quiet-test.dll");
        if (!File.Exists(guard)) throw new FileNotFoundException("無音テスト用DLLが必要です。build.ps1 -SmokeTestを実行してください。", guard);
        var name = new System.Text.StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "テスト用デスクトップを確認できませんでした。");
        DesktopName = name.ToString();
    }

    internal static void ValidateDirectory(string directory)
    {
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        // The launcher is in <workspace>/dist; packaged diagnostic launches
        // cannot accidentally use a stock installation or ordinary game copy.
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var directTest = folder.Parent?.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) == true &&
            folder.Name.StartsWith("game-test-", StringComparison.OrdinalIgnoreCase);
        var session = folder.Parent;
        var sessionTest = folder.Name is "host" or "guest" && session?.Name.Length == 32 &&
            session.Name.All(Uri.IsHexDigit) && session.Parent?.Name == "sessions" &&
            session.Parent.Parent?.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) == true;
        if (!directTest && !sessionTest)
            throw new IOException("実ゲームテストにはこの作業フォルダーのgame-test-*または隔離セッションを使用してください。");
    }

    internal static QuietTestEvidence ReadEvidence(int processId)
    {
        using var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopQuiet_{processId}", MemoryMappedFileRights.Read);
        using var data = mapping.CreateViewAccessor(0, 36, MemoryMappedFileAccess.Read);
        if (data.ReadUInt32(0) != 0x51323154 || data.ReadUInt32(4) != 1)
            throw new IOException("無音テストの検証情報が正しくありません。");
        var visible = 0;
        if (!EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window)) visible++;
            return true;
        }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "テスト用ウィンドウを検証できませんでした。");
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foregroundOwner);
        return new(data.ReadUInt32(8), data.ReadUInt32(12), data.ReadUInt32(16), data.ReadUInt32(20),
            data.ReadUInt32(24), data.ReadUInt32(28), data.ReadUInt32(32), visible, foregroundOwner == processId);
    }

    public void Dispose() { }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, System.Text.StringBuilder information, int length, out int needed);
}
