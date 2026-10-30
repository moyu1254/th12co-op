using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;

namespace TH12Coop;

internal static partial class SelfTests
{
    internal static async Task LocalConfigurationTests(List<string> passed, CancellationToken token)
    {
        var template = await GameConfiguration.Template(token);
        var local = (byte[])template.Clone();
        local[4] = 19; local[0x1d] = 3; local[0x20] = 85; local[0x21] = 0;
        local[0x28] = 82; local[0x39] ^= 1;
        var merged = GameConfiguration.MergeLocal(template, local);
        Expect(merged.SequenceEqual(local), "local options survive normalization");
        Expect(GameConfiguration.SharedDigest(merged) == GameConfiguration.SharedDigest(template), "local options are not network compatibility");
        foreach (var offset in new[] { 0x1e, 0x1f, 0x22, 0x23, 0x30, 0x38, 0x3a })
        {
            var modified = (byte[])local.Clone(); modified[offset] ^= 1;
            Expect(GameConfiguration.SharedDigest(modified) != GameConfiguration.SharedDigest(local), "simulation/unknown field is shared");
            Expect(GameConfiguration.MergeLocal(template, modified).SequenceEqual(merged), "simulation field normalized");
        }
        var holdFocus = (byte[])local.Clone(); holdFocus[0x39] ^= 2;
        Expect(GameConfiguration.SharedDigest(holdFocus) != GameConfiguration.SharedDigest(local), "hold-shot focus is a shared simulation option");
        Expect(GameConfiguration.MergeLocal(template, holdFocus).SequenceEqual(merged), "hold-shot focus normalized");
        Expect(template[0x20] == 20, "local merging did not mutate template");
        var invalid = (byte[])template.Clone(); invalid[0x20] = 101;
        ExpectThrows<IOException>(() => GameConfiguration.MergeLocal(template, invalid));
        ExpectThrows<IOException>(() => GameConfiguration.MergeLocal(template, []));
        var disabledAudio = (byte[])template.Clone(); disabledAudio[0x1b] = 0; disabledAudio[0x1c] = 0;
        disabledAudio[0x20] = 80; disabledAudio[0x21] = 65;
        Expect(GameConfiguration.EffectiveVolume(disabledAudio, true) == 0 && GameConfiguration.EffectiveVolume(disabledAudio, false) == 0,
            "disabled native audio displays as muted even with stored volume");
        var savedDisabled = GameConfiguration.MergeLocal(template, disabledAudio);
        Expect(savedDisabled[0x1b] == 0 && savedDisabled[0x20] == 80 && savedDisabled[0x1c] == 0 && savedDisabled[0x21] == 65,
            "unchanged muted mode and stored volume survive settings persistence");
        GameConfiguration.ChangeVolume(disabledAudio, true, 35);
        Expect(disabledAudio[0x1b] == 1 && disabledAudio[0x20] == 35 && disabledAudio[0x1c] == 0 && disabledAudio[0x21] == 65,
            "explicit music volume change does not enable effects");
        disabledAudio[0x1b] = 2; GameConfiguration.ChangeVolume(disabledAudio, true, 50);
        Expect(disabledAudio[0x1b] == 2, "volume changes preserve enabled music mode");
        GameConfiguration.ChangeVolume(disabledAudio, true, 0);
        Expect(disabledAudio[0x1b] == 0 && disabledAudio[0x20] == 0, "explicit mute");
        ExpectThrows<IOException>(() => GameConfiguration.ChangeVolume(disabledAudio, false, 101));
        for (var one = 0; one < 6; ++one)
        {
            Expect(OnlineSession.DecodeCharacter(OnlineSession.EncodeCharacter(one)) == one, "character wire encoding");
            for (var two = 0; two < 6; ++two)
            {
                var selection = new PlayerSelection(one, two);
                using var published = selection.Publish(Environment.ProcessId);
                using var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopPlayers_{Environment.ProcessId}");
                using var memory = mapping.CreateViewAccessor(0, 16);
                Expect(memory.ReadUInt32(0) == 0x43323154 && memory.ReadUInt32(4) == 1 && memory.ReadUInt32(8) == one && memory.ReadUInt32(12) == two, "36 character combinations reach native contract");
            }
        }
        Expect(OnlineSession.DecodeCharacter(OnlineSession.EncodeCharacter(null)) == null, "legacy menu selection");
        foreach (var packet in new byte[][] { [], [4], [4, 6], [4, 0, 0], [3, 1] })
            ExpectThrows<IOException>(() => OnlineSession.DecodeCharacter(packet));
        ExpectThrows<IOException>(() => new PlayerSelection(-1, 0).Validate());
        ExpectThrows<IOException>(() => new PlayerSelection(0, 6).Validate());
        passed.Add("Local display/audio/input separated from shared simulation settings; invalid settings rejected");
        passed.Add("36 independent player selections published; malformed selection packets rejected");
        await LocalPersistenceTests(template, passed, token);
        await PlayerNegotiationTests(template, passed);
    }

    private static async Task LocalPersistenceTests(byte[] template, List<string> passed, CancellationToken token)
    {
        using var fixture = new PreparationFixture();
        var offline = (byte[])template.Clone();
        offline[0x1e] = 2; offline[0x22] = 1; offline[0x39] |= 2;
        offline[0x20] = 90; offline[0x21] = 15;
        var offlinePath = Path.Combine(fixture.GameDirectory, "th12.cfg");
        await File.WriteAllBytesAsync(offlinePath, offline, token);
        var scores = new byte[] { 1, 4, 9, 16 };
        await File.WriteAllBytesAsync(Path.Combine(fixture.GameDirectory, "scoreth12.dat"), scores, token);
        var session = await fixture.Profile.Prepare(fixture.Session, 1, token);
        Expect(!File.Exists(Path.Combine(session, "scoreth12.dat")), "scores are not imported into the session");
        var local = GameConfiguration.MergeLocal(template, offline);
        local[0x1d] = 3; local[0x20] = 30; local[0x21] = 45; local[0x28] = 42;
        await File.WriteAllBytesAsync(Path.Combine(session, "th12.cfg"), local, token);
        await fixture.Profile.PersistLocal(session, token);
        var saved = await File.ReadAllBytesAsync(offlinePath, token);
        Expect(saved.SequenceEqual(GameConfiguration.MergeLocal(offline, local)), "only local session fields persist");
        Expect(saved[0x1e] == 2 && saved[0x22] == 1 && (saved[0x39] & 2) != 0, "offline simulation settings preserved");
        Expect((await File.ReadAllBytesAsync(Path.Combine(fixture.GameDirectory, "scoreth12.dat"), token)).SequenceEqual(scores), "score data preserved");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(() => GameConfiguration.Save(offlinePath, template, cancelled.Token));
        Expect((await File.ReadAllBytesAsync(offlinePath, token)).SequenceEqual(saved), "cancelled settings save preserves existing file");
        await File.WriteAllBytesAsync(Path.Combine(session, "th12.cfg"), [1, 2, 3], token);
        await ExpectThrowsAsync<IOException>(() => fixture.Profile.PersistLocal(session, token));
        Expect((await File.ReadAllBytesAsync(offlinePath, token)).SequenceEqual(saved), "malformed session settings do not replace local settings");
        passed.Add("Local settings persist without importing scores or replacing offline simulation options; cancelled/corrupt writes preserve prior files and muted audio");
    }

    private static async Task PlayerNegotiationTests(byte[] template, List<string> passed)
    {
        AssetProfile Assets(string buildName, byte[] config)
        {
            var build = GameBuild.Supported.Single(value => value.Id == buildName);
            return new AssetProfile(build.ExeHash, build.DatHash, GameBuild.MusicHash, "native", GameConfiguration.SharedDigest(config), build.Id, "dependencies");
        }
        var local = (byte[])template.Clone(); local[0x1d] = 3; local[0x20] = 95; local[0x28] = 51;
        var hostAssets = Assets("retail-1.00b", template);
        var guestAssets = Assets("steam-1.00b", local);
        await WithChannels(async (guest, host, token) =>
        {
            for (var one = 0; one < 6; one++) for (var two = 0; two < 6; two++)
            {
                var offering = OnlineSession.NegotiateHost(host, hostAssets, 3, one, token);
                var accepting = OnlineSession.NegotiateGuest(guest, guestAssets, two, token);
                var agreements = await Task.WhenAll(offering, accepting);
                Expect(agreements[0] == agreements[1] && agreements[0].Players == new PlayerSelection(one, two),
                    "actual TLS agrees on distinct player selections and local settings");
            }
            var legacyHost = OnlineSession.NegotiateHost(host, hostAssets, 3, null, token);
            var legacyGuest = OnlineSession.NegotiateGuest(guest, guestAssets, null, token);
            var legacy = await Task.WhenAll(legacyHost, legacyGuest);
            Expect(legacy[0] == legacy[1] && legacy[0].Players == null, "legacy menu peers still negotiate");
        });
        await WithChannels(async (guest, host, token) =>
        {
            var offering = OnlineSession.NegotiateHost(host, hostAssets, 3, 1, token);
            await guest.Send(OnlineSession.EncodeCharacter(null), token);
            await ExpectThrowsAsync<IOException>(() => offering);
        });
        await WithChannels(async (guest, host, token) =>
        {
            var accepting = OnlineSession.NegotiateGuest(guest, guestAssets, 4, token);
            Expect(OnlineSession.DecodeCharacter(await host.Receive(token)) == 4, "guest selects before receiving offer");
            var forged = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, 3, 123,
                Convert.ToHexString(host.Session), hostAssets, new PlayerSelection(0, 5));
            await host.Send(OnlineProfile.Encode(forged), token);
            await ExpectThrowsAsync<IOException>(() => accepting);
        });
        await WithChannels(async (guest, host, token) =>
        {
            var offering = OnlineSession.NegotiateHost(host, hostAssets, 3, 1, token);
            await guest.Send(OnlineSession.EncodeCharacter(2), token);
            var offer = await guest.Receive(token);
            var acknowledgement = new byte[] { 2 }.Concat(SHA256.HashData(offer)).ToArray(); acknowledgement[1] ^= 1;
            await guest.Send(acknowledgement, token);
            await ExpectThrowsAsync<IOException>(() => offering);
        });
        passed.Add("Actual TLS negotiation: all 36 player pairs, differing local settings and retail/Steam identities; substituted guest selection, mixed legacy mode and altered offer acknowledgement rejected");
    }
}
