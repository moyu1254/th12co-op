namespace TH12Coop;

internal static partial class SelfTests
{
    internal static void RunInputTests(List<string> passed)
    {
        var profile = new InputBindingProfile();
        bool None(int _) => false;
        var center = new PadSnapshot([0, 0, 0, 0, 0, 0], 0, 0);
        Expect(InputComposer.Compose(profile, true, None, center) == 0, "centered pad");
        Expect(InputComposer.Compose(profile, false, _ => throw new Exception("background keyboard read"), center) == 0, "background input gate");
        Expect(InputComposer.Compose(profile, true, key => key == (int)Keys.Z, null) == 1, "disconnected pad preserves keyboard");
        Expect(InputComposer.Compose(profile, true, None, center with { Buttons = 1 | 4 | 128 }) == 0x109, "shot focus pause buttons");
        Expect(InputComposer.Compose(profile, true, key => key == (int)Keys.Z, center with { Buttons = 2 }) == 3, "keyboard and pad merge");
        var threshold = 32767 * profile.DeadZone / 100;
        Expect(InputComposer.Compose(profile, true, None, center with { Axes = [threshold, -threshold] }) == 0, "dead zone inclusive boundary");
        Expect(InputComposer.Compose(profile, true, None, center with { Axes = [threshold + 1, -threshold - 1] }) == 0x90, "diagonal stick movement");
        Expect(InputComposer.Compose(profile with { InvertHorizontal = true, InvertVertical = true }, true, None,
            center with { Axes = [32767, -32768] }) == 0x60, "inverted axes and signed minimum");
        Expect(InputComposer.Compose(profile with { HorizontalAxis = 4, VerticalAxis = 5 }, true, None,
            center with { Axes = [0, 0, 0, 0, -32767, 32767] }) == 0x60, "six configurable axes");
        Expect(InputComposer.Compose(profile with { HorizontalAxis = -1, VerticalAxis = -1 }, true, None,
            center with { Axes = [32767, -32767] }) == 0, "disabled analog axes");
        Expect(InputComposer.Compose(profile, true, key => key == (int)Keys.Left,
            center with { Axes = [32767, 0] }) == 0, "opposite keyboard and stick directions neutralize");
        Expect(InputComposer.Compose(profile, true, None, center with { Directions = 0x13f }) == 0,
            "opposite POV directions neutralize and non-direction bits excluded");
        Expect(InputComposer.Compose(profile with { UsePov = false }, true, None, center with { Directions = 0x50 }) == 0, "disabled POV");
        uint[] bearings = [0x10, 0x90, 0x80, 0xa0, 0x20, 0x60, 0x40, 0x50];
        for (uint i = 0; i < 8; i++) Expect(InputComposer.Pov(i * 4500) == bearings[i], "POV bearing " + i);
        Expect(InputComposer.Pov(0xffff) == 0 && InputComposer.Pov(uint.MaxValue) == 0, "centered POV");
        Expect(InputComposer.Pov(35999) == 0x10, "POV wrap");
        Expect(InputComposer.Axis(100, 100, 900) == -32767 && InputComposer.Axis(900, 100, 900) == 32767 &&
            InputComposer.Axis(500, 100, 900) == 0 && InputComposer.Axis(0, 100, 100) == 0, "axis capability calibration");
        Expect(InputComposer.Axis(0, 100, 900) == -32767 && InputComposer.Axis(uint.MaxValue, 0, uint.MaxValue) == 32767,
            "axis arithmetic bounds");
        var remapped = profile with { Keys = [0, 0, 0, 0, (int)Keys.A, 0, 0, 0], Buttons = [-1, -1, -1, -1, 31, -1, -1, -1] };
        remapped.Validate();
        Expect(InputComposer.Compose(remapped, true, key => key == (int)Keys.Z, null) == 0, "old key stops after rebind");
        Expect(InputComposer.Compose(remapped, true, key => key == (int)Keys.A, null) == 1, "rebound key applies");
        Expect(InputComposer.Compose(remapped, true, None, center with { Buttons = 0x80000000 }) == 1, "button 32 maps");
        Expect(GamepadDevices.ValidId("xinput:3") && !GamepadDevices.ValidId("xinput:4") &&
            GamepadDevices.ValidId("winmm:15:65535:65535:driver") && !GamepadDevices.ValidId("winmm:16:0:0:driver"), "device identity bounds");
        ExpectThrows<IOException>(() => (profile with { Keys = [1] }).Validate());
        ExpectThrows<IOException>(() => (profile with { DeadZone = 0 }).Validate());
        ExpectThrows<IOException>(() => (profile with { HorizontalAxis = 6 }).Validate());
        ExpectThrows<IOException>(() => (profile with { Device = "automatic-first-device" }).Validate());
        ExpectThrows<IOException>(() => (profile with { Buttons = [-1, -1, -1, -1, 32, -1, -1, -1] }).Validate());
        passed.Add("Local input: keyboard + gamepad merge, rebinding, 32 buttons, six axes/dead zones/inversion, eight POV directions, opposing directions and unfocused/disconnected behavior");

        var directory = Path.Combine(Path.GetTempPath(), "TH12Coop-input-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "input.json");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new InputSettings { PlayerOne = remapped with { Device = "xinput:1" }, PlayerTwo = InputBindingProfile.Second, CharacterOne = 5, CharacterTwo = 2 };
            InputSettingsStore.Save(settings, path);
            var loaded = InputSettingsStore.Load(path);
            Expect(loaded.PlayerOne.Keys.SequenceEqual(settings.PlayerOne.Keys) && loaded.PlayerOne.Buttons.SequenceEqual(settings.PlayerOne.Buttons) &&
                loaded.PlayerOne.Device == "xinput:1" && loaded.PlayerTwo.Keys.SequenceEqual(InputBindingProfile.Second.Keys) &&
                loaded.CharacterOne == 5 && loaded.CharacterTwo == 2, "per-player settings round trip");
            var before = File.ReadAllBytes(path);
            ExpectThrows<IOException>(() => InputSettingsStore.Save(settings with { CharacterOne = 6 }, path));
            Expect(File.ReadAllBytes(path).SequenceEqual(before), "invalid update preserves existing settings");
            File.WriteAllText(path, "{bad");
            ExpectThrows<IOException>(() => InputSettingsStore.Load(path));
            Expect(File.ReadAllText(path) == "{bad", "corrupt settings not overwritten during load");
        }
        finally { Directory.Delete(directory, true); }
        passed.Add("Local input settings: independent profiles and characters persist atomically; malformed settings rejected without overwriting");
    }
}
