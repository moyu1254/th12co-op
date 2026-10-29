using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TH12Coop;

internal sealed record GameBuild(string Id, string Label, string ExeHash, string DatHash, string Folder, bool Steam = false)
{
    internal const string MusicHash = "052a6f448a99ac829e23e4e724400129bd04a7201667a7447237ed7cd5ad5793";
    internal static readonly GameBuild[] Supported =
    [
        new("retail-1.00b", "通常版 1.00b", "d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628", "dca82df072ba51c4b74c3d41ea1cbdcb91f709ce7cfea23599ee52caa34f2d72", "game-retail-1.00b"),
        new("steam-1.00b", "Steam版 1.00b", "eb275a8c285fd8450fa5ac79fa730a759aeca8b2904aeb16d30e65859372a459", "dca82df072ba51c4b74c3d41ea1cbdcb91f709ce7cfea23599ee52caa34f2d72", "game-steam-1.00b", true),
        new("retail-1.00a", "通常版 1.00a", "e2939ba3f92e427216a619191a682a8197cc59a64eab99302a7b98811d6120a9", "086cd832c73fe170f9e1de467beabe53b73caf04bf94905acb081e3a70137ab7", "game"),
    ];
    // These names come only from this local allowlist, never from a peer.
    internal static readonly string[] OptionalDependencies = ["d3dx9_40.dll"];
    internal const string SteamAppId = "1100160";
    internal string CompatibilityGroup => Id switch
    {
        "retail-1.00a" => "th12-1.00a",
        "retail-1.00b" or "steam-1.00b" => "th12-1.00b",
        _ => throw new IOException("未対応のゲームバージョンです。"),
    };
    internal static GameBuild FromHash(string hash) => Supported.FirstOrDefault(build => build.ExeHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
        ?? throw new IOException("未対応または改変されたTH12です。通常版1.00a・1.00b、Steam版1.00bの正規の本体を選択してください。");
    internal static GameBuild FromAssets(AssetProfile? assets)
    {
        if (assets == null) throw new IOException("ゲームの識別情報がありません。");
        var build = FromHash(assets.Exe);
        if (assets.Build != build.Id || !build.DatHash.Equals(assets.Dat, StringComparison.OrdinalIgnoreCase) ||
            !MusicHash.Equals(assets.Bgm, StringComparison.OrdinalIgnoreCase))
            throw new IOException("ゲームの識別情報とデータが一致しません。");
        return build;
    }
    internal static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
    }
    internal static async Task<GameBuild> Identify(string directory, CancellationToken token)
        => FromHash(await Hash(Path.Combine(directory, "th12.exe"), token));
    internal static async Task<Dictionary<string, string>> Assets(string directory, GameBuild build, CancellationToken token)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "th12.exe", "th12.dat", "thbgm.dat" }.Concat(OptionalDependencies.Where(name => File.Exists(Path.Combine(directory, name)))))
            files.Add(name, await Hash(Path.Combine(directory, name), token));
        if (files["th12.exe"] != build.ExeHash || files["th12.dat"] != build.DatHash || files["thbgm.dat"] != MusicHash)
            throw new IOException("ゲーム本体とデータの組み合わせが一致しません。正規の同じ版のファイルを選択してください。");
        return files;
    }
    internal static string DependencyDigest(IReadOnlyDictionary<string, string> assets)
    {
        var canonical = string.Join("\n", OptionalDependencies.Select(name => name + ":" + (assets.TryGetValue(name, out var hash) ? hash : "system")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    internal static async Task PrepareSteamIdentity(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "steam_appid.txt");
        if (File.Exists(path))
        {
            if ((await File.ReadAllTextAsync(path, token)).Trim() != SteamAppId) throw new IOException("Steam App IDがTH12と一致しません。");
            return;
        }
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(Encoding.ASCII.GetBytes(SteamAppId), token);
    }
}

internal sealed record GameChoice(string Directory, GameBuild Build)
{
    public override string ToString() => Build.Label;
}

internal static class GameLibrary
{
    internal static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    internal static async Task<List<GameChoice>> Discover(string? selected, CancellationToken token)
    {
        var candidates = new List<string>();
        if (selected != null) candidates.Add(Path.GetFullPath(selected));
        candidates.AddRange(GameBuild.Supported.Select(build => Path.Combine(Root, build.Folder)));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "上海アリス幻樂団", "東方星蓮船"));
        string? steam = null;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) steam = key?.GetValue("SteamPath") as string;
        if (!string.IsNullOrWhiteSpace(steam))
        {
            var libraries = new List<string> { steam };
            var path = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(path) && new FileInfo(path).Length < 1024 * 1024)
            {
                var text = await File.ReadAllTextAsync(path, token);
                foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            candidates.AddRange(libraries.Select(library => Path.Combine(library, "steamapps", "common", "th12")));
        }
        var choices = new List<GameChoice>();
        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(directory, "th12.exe"))) continue;
            try
            {
                var build = await GameBuild.Identify(directory, token);
                if (choices.All(choice => choice.Build.Id != build.Id)) choices.Add(new GameChoice(directory, build));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return choices;
    }

    internal static async Task<string> Prepare(GameChoice choice, CancellationToken token)
    {
        var source = Path.GetFullPath(choice.Directory);
        var destination = Path.GetFullPath(Path.Combine(Root, choice.Build.Folder));
        var files = await GameBuild.Assets(source, choice.Build, token);
        // Preserve installed files and existing MOD saves. Reject redirected
        // destinations and conflicting assets rather than updating them in place.
        for (var current = new DirectoryInfo(destination); current != null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("MOD用フォルダーにリンクを使用できません。");
            if (current.FullName.Equals(Root, StringComparison.OrdinalIgnoreCase)) break;
        }
        foreach (var name in files.Keys.Concat(["th12.cfg", "steam_appid.txt"]))
        {
            var path = Path.Combine(destination, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("MOD用ファイルにリンクを使用できません。");
        }
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            if (choice.Build.Steam) await GameBuild.PrepareSteamIdentity(destination, token);
            return source;
        }
        var missing = new List<string>();
        foreach (var (name, hash) in files)
        {
            var target = Path.Combine(destination, name);
            if (!File.Exists(target)) missing.Add(name);
            else if (await GameBuild.Hash(target, token) != hash) throw new IOException($"MOD用コピーのファイルが一致しません: {target}");
        }
        var stage = Path.Combine(Root, ".prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var name in missing)
            {
                await using (var input = File.OpenRead(Path.Combine(source, name)))
                await using (var output = new FileStream(Path.Combine(stage, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    await input.CopyToAsync(output, token);
                if (await GameBuild.Hash(Path.Combine(stage, name), token) != files[name]) throw new IOException("ゲームコピーの検証に失敗しました。");
            }
            Directory.CreateDirectory(destination);
            foreach (var name in missing) File.Move(Path.Combine(stage, name), Path.Combine(destination, name));
            var configPath = Path.Combine(destination, "th12.cfg");
            if (!File.Exists(configPath))
            {
                var config = Path.Combine(source, "th12.cfg");
                var contents = File.Exists(config) ? await File.ReadAllBytesAsync(config, token) : [];
                if (contents.Length != 60 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(contents) != 0x00120001)
                    contents = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "th12-online.cfg"), token);
                await using var output = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await output.WriteAsync(contents, token);
            }
            if (choice.Build.Steam) await GameBuild.PrepareSteamIdentity(destination, token);
            return destination;
        }
        finally
        {
            foreach (var name in missing) { var file = Path.Combine(stage, name); if (File.Exists(file)) File.Delete(file); }
            Directory.Delete(stage);
        }
    }
}
