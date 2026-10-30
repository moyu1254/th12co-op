using System.Runtime.InteropServices;
using System.Text.Json;

namespace TH12Coop;

internal sealed record InputBindingProfile
{
    public int[] Keys { get; init; } = [(int)System.Windows.Forms.Keys.Up, (int)System.Windows.Forms.Keys.Down, (int)System.Windows.Forms.Keys.Left,
        (int)System.Windows.Forms.Keys.Right, (int)System.Windows.Forms.Keys.Z, (int)System.Windows.Forms.Keys.X,
        (int)System.Windows.Forms.Keys.LShiftKey, (int)System.Windows.Forms.Keys.Escape];
    public string Device { get; init; } = "";
    public int HorizontalAxis { get; init; }
    public int VerticalAxis { get; init; } = 1;
    public bool InvertHorizontal { get; init; }
    public bool InvertVertical { get; init; }
    public int DeadZone { get; init; } = 25;
    public bool UsePov { get; init; } = true;
    public int[] Buttons { get; init; } = [-1, -1, -1, -1, 0, 1, 2, 7];

    internal static InputBindingProfile Second => new() { Keys = [(int)System.Windows.Forms.Keys.I, (int)System.Windows.Forms.Keys.K,
        (int)System.Windows.Forms.Keys.J, (int)System.Windows.Forms.Keys.L, (int)System.Windows.Forms.Keys.F,
        (int)System.Windows.Forms.Keys.G, (int)System.Windows.Forms.Keys.D, 0] };

    internal void Validate()
    {
        if (Keys == null || Keys.Length != 8 || Keys.Any(key => key is < 0 or > 254) ||
            Buttons == null || Buttons.Length != 8 || Buttons.Any(button => button is < -1 or > 31) ||
            HorizontalAxis is < -1 or > 5 || VerticalAxis is < -1 or > 5 || DeadZone is < 1 or > 90 ||
            Device == null || Device.Length > 256 || (Device.Length != 0 && !GamepadDevices.ValidId(Device)))
            throw new IOException("入力設定が正しくありません。入力設定から設定し直してください。");
    }
}

internal sealed record InputSettings
{
    public int Version { get; init; } = 1;
    public InputBindingProfile PlayerOne { get; init; } = new();
    public InputBindingProfile PlayerTwo { get; init; } = InputBindingProfile.Second;
    public int CharacterOne { get; init; }
    public int CharacterTwo { get; init; }

    internal void Validate()
    {
        if (Version != 1 || PlayerOne == null || PlayerTwo == null || CharacterOne is < 0 or > 5 || CharacterTwo is < 0 or > 5)
            throw new IOException("入力・自機設定の形式が正しくありません。");
        PlayerOne.Validate(); PlayerTwo.Validate();
    }
}

internal static class InputSettingsStore
{
    internal static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TH12Coop", "input.json");
    internal static InputSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<InputSettings>(File.ReadAllBytes(path)) ?? throw new JsonException();
            settings.Validate();
            return settings;
        }
        catch (JsonException error) { throw new IOException($"入力設定を読み取れません: {path}", error); }
    }
    internal static void Save(InputSettings settings, string? path = null)
    {
        settings.Validate();
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }));
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal readonly record struct PadSnapshot(int[] Axes, uint Buttons, uint Directions);
internal sealed record GamepadDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class InputComposer
{
    internal static readonly uint[] Masks = [0x10, 0x20, 0x40, 0x80, 1, 2, 8, 0x100];
    internal static uint Compose(InputBindingProfile profile, bool focused, Func<int, bool> keyDown, PadSnapshot? pad)
    {
        if (!focused) return 0;
        uint mask = 0;
        for (var i = 0; i < Masks.Length; i++)
            if (profile.Keys[i] != 0 && keyDown(profile.Keys[i])) mask |= Masks[i];
        if (pad is { } current)
        {
            for (var i = 0; i < Masks.Length; i++)
                if (profile.Buttons[i] >= 0 && (current.Buttons & (1u << profile.Buttons[i])) != 0) mask |= Masks[i];
            if (profile.UsePov) mask |= current.Directions & 0xf0;
            var threshold = 32767 * profile.DeadZone / 100;
            int Axis(int index, bool invert) => index >= 0 && index < current.Axes.Length ? current.Axes[index] * (invert ? -1 : 1) : 0;
            var x = Axis(profile.HorizontalAxis, profile.InvertHorizontal);
            var y = Axis(profile.VerticalAxis, profile.InvertVertical);
            if (x < -threshold) mask |= 0x40;
            if (x > threshold) mask |= 0x80;
            if (y < -threshold) mask |= 0x10;
            if (y > threshold) mask |= 0x20;
        }
        // Resolve opposite directions before the input enters the shared frame.
        if ((mask & 0x30) == 0x30) mask &= ~0x30u;
        if ((mask & 0xc0) == 0xc0) mask &= ~0xc0u;
        return mask;
    }

    internal static uint Pov(uint value)
    {
        if (value >= 36000) return 0;
        return ((value + 2250) / 4500 % 8) switch
        {
            0 => 0x10, 1 => 0x90, 2 => 0x80, 3 => 0xa0,
            4 => 0x20, 5 => 0x60, 6 => 0x40, _ => 0x50,
        };
    }

    internal static int Axis(uint value, uint minimum, uint maximum)
        => maximum <= minimum ? 0 : (int)Math.Clamp(((long)value - minimum) * 65534 / (maximum - minimum) - 32767, -32767, 32767);
}

internal sealed class LocalInputReader
{
    private readonly InputBindingProfile profile;
    private readonly int processId;
    private long reconnectAt;
    internal LocalInputReader(InputBindingProfile profile, int processId)
    {
        profile.Validate();
        this.profile = profile with { Keys = [.. profile.Keys], Buttons = [.. profile.Buttons] };
        this.processId = processId;
    }
    internal uint Read()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foreground);
        if (foreground != processId) return 0;
        PadSnapshot? pad = null;
        if (profile.Device.Length != 0 && Environment.TickCount64 >= reconnectAt)
        {
            pad = GamepadDevices.Read(profile.Device);
            if (pad == null) reconnectAt = Environment.TickCount64 + 1000;
        }
        return InputComposer.Compose(profile, true, key => (Native.GetAsyncKeyState(key) & 0x8000) != 0, pad);
    }
}

internal static class GamepadDevices
{
    internal static bool ValidId(string id)
    {
        var parts = id.Split(':');
        return parts.Length == 2 && parts[0] == "xinput" && uint.TryParse(parts[1], out var x) && x < 4 ||
            parts.Length == 5 && parts[0] == "winmm" && uint.TryParse(parts[1], out var w) && w < 16 &&
            ushort.TryParse(parts[2], out _) && ushort.TryParse(parts[3], out _);
    }
    internal static List<GamepadDevice> Enumerate()
    {
        var devices = new List<GamepadDevice>();
        for (uint id = 0; id < 4; id++)
            if (XInputGetState(id, out _) == 0) devices.Add(new($"xinput:{id}", $"Xbox / XInput {id + 1}"));
        for (uint id = 0; id < Math.Min(joyGetNumDevs(), 16); id++)
        {
            if (joyGetDevCaps((UIntPtr)id, out var caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0) continue;
            var info = new JoyInfo { Size = (uint)Marshal.SizeOf<JoyInfo>(), Flags = 0x2ff };
            if (joyGetPosEx(id, ref info) == 0) devices.Add(new(Id(id, caps), $"{caps.Name} · Windows {id + 1}"));
        }
        return devices;
    }
    private static string Id(uint id, JoyCaps caps) => $"winmm:{id}:{caps.Manufacturer}:{caps.Product}:{caps.RegistryKey}";
    internal static PadSnapshot? Read(string device)
    {
        if (!ValidId(device)) return null;
        var parts = device.Split(':'); var id = uint.Parse(parts[1]);
        if (parts[0] == "xinput")
        {
            if (XInputGetState(id, out var state) != 0) return null;
            var p = state.Pad;
            ushort[] buttons = [0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0x20, 0x10, 0x40, 0x80];
            uint pressed = 0;
            for (var i = 0; i < buttons.Length; i++) if ((p.Buttons & buttons[i]) != 0) pressed |= 1u << i;
            if (p.LeftTrigger > 30) pressed |= 1u << 10;
            if (p.RightTrigger > 30) pressed |= 1u << 11;
            var directions = ((p.Buttons & 1) != 0 ? 0x10u : 0) | ((p.Buttons & 2) != 0 ? 0x20u : 0) |
                ((p.Buttons & 4) != 0 ? 0x40u : 0) | ((p.Buttons & 8) != 0 ? 0x80u : 0);
            return new([p.LeftX, -p.LeftY, p.RightX, -p.RightY, 0, 0], pressed, directions);
        }
        if (joyGetDevCaps((UIntPtr)id, out var caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0 || Id(id, caps) != device) return null;
        var info = new JoyInfo { Size = (uint)Marshal.SizeOf<JoyInfo>(), Flags = 0x2ff };
        if (joyGetPosEx(id, ref info) != 0) return null;
        int Axis(uint value, uint min, uint max) => InputComposer.Axis(value, min, max);
        return new([Axis(info.X, caps.XMin, caps.XMax), Axis(info.Y, caps.YMin, caps.YMax),
            (caps.Capabilities & 1) != 0 ? Axis(info.Z, caps.ZMin, caps.ZMax) : 0,
            (caps.Capabilities & 2) != 0 ? Axis(info.R, caps.RMin, caps.RMax) : 0,
            (caps.Capabilities & 4) != 0 ? Axis(info.U, caps.UMin, caps.UMax) : 0,
            (caps.Capabilities & 8) != 0 ? Axis(info.V, caps.VMin, caps.VMax) : 0], info.Buttons,
            (caps.Capabilities & 16) != 0 ? InputComposer.Pov(info.Pov) : 0);
    }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint id, out XInputState state);
    [StructLayout(LayoutKind.Sequential)] private struct XInputState { internal uint Packet; internal XInputPad Pad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputPad
    { internal ushort Buttons; internal byte LeftTrigger, RightTrigger; internal short LeftX, LeftY, RightX, RightY; }
    [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)] private static extern uint joyGetDevCaps(UIntPtr id, out JoyCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint id, ref JoyInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct JoyInfo
    { internal uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct JoyCaps
    {
        internal ushort Manufacturer, Product;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        internal uint XMin, XMax, YMin, YMax, ZMin, ZMax, Buttons, PeriodMin, PeriodMax;
        internal uint RMin, RMax, UMin, UMax, VMin, VMax, Capabilities, MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Oem;
    }
}
