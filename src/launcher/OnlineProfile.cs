using System.Security.Cryptography;
using System.Text.Json;

namespace TH12Coop;

internal sealed record AssetProfile(string Exe, string Dat, string Bgm, string Native, string Configuration, string Build, string Dependencies);
internal sealed record SessionManifest(int Protocol, uint Rules, int Delay, uint Seed, string Session, AssetProfile Assets);

internal sealed class OnlineProfile
{
    public string GameDirectory { get; }
    public string NativePath { get; }
    public byte[] Configuration { get; }
    public AssetProfile Assets { get; }
    private readonly string[] gameFiles;

    private OnlineProfile(string gameDirectory, string nativePath, byte[] configuration, AssetProfile assets, string[] gameFiles)
    { GameDirectory = gameDirectory; NativePath = nativePath; Configuration = configuration; Assets = assets; this.gameFiles = gameFiles; }

    public static async Task<OnlineProfile> Read(string gameDirectory, CancellationToken token, string? nativePath = null)
    {
        nativePath ??= Path.Combine(AppContext.BaseDirectory, "th12coop-native.dll");
        var configuration = await GameConfiguration.Template(token);
        var build = await GameBuild.Identify(gameDirectory, token);
        var files = await GameBuild.Assets(gameDirectory, build, token);
        var assets = new AssetProfile(files["th12.exe"], files["th12.dat"], files["thbgm.dat"], await GameBuild.Hash(nativePath, token),
            Convert.ToHexString(SHA256.HashData(configuration)), build.Id, GameBuild.DependencyDigest(files));
        return new OnlineProfile(Path.GetFullPath(gameDirectory), Path.GetFullPath(nativePath), configuration, assets, files.Keys.ToArray());
    }

    public async Task<string> Prepare(byte[] session, byte role, CancellationToken token)
    {
        // No scores, replays, local input history or offline settings enter a
        // network session. All peers use this same verified template and assets.
        token.ThrowIfCancellationRequested();
        if (session.Length != 16 || role is not (1 or 2)) throw new IOException("セッションの識別情報が正しくありません。");
        var parent = Path.Combine(Path.GetDirectoryName(GameDirectory)!, "sessions", Convert.ToHexString(session));
        var destination = Path.Combine(parent, role == 1 ? "host" : "guest");
        if (Directory.Exists(destination)) throw new IOException("同じセッションのゲームフォルダーが既に存在します。");
        var stage = Path.Combine(parent, ".prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var name in gameFiles)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(GameDirectory, name);
                var target = Path.Combine(stage, name);
                if (!Native.CreateHardLink(target, source, IntPtr.Zero))
                {
                    await using var input = File.OpenRead(source);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await input.CopyToAsync(output, token);
                }
            }
            await File.WriteAllBytesAsync(Path.Combine(stage, "th12.cfg"), Configuration, token);
            if (Assets.Build == "steam-1.00b") await GameBuild.PrepareSteamIdentity(stage, token);
            await VerifyPrepared(stage, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return destination;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private async Task VerifyPrepared(string directory, CancellationToken token)
    {
        // The room can wait for an invite for a long time. Hash the actual
        // prepared files again so a local update cannot launch under an old offer.
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in gameFiles) hashes.Add(name, await GameBuild.Hash(Path.Combine(directory, name), token));
        if (hashes["th12.exe"] != Assets.Exe || hashes["th12.dat"] != Assets.Dat || hashes["thbgm.dat"] != Assets.Bgm ||
            GameBuild.DependencyDigest(hashes) != Assets.Dependencies || await GameBuild.Hash(NativePath, token) != Assets.Native ||
            !Assets.Configuration.Equals(await GameBuild.Hash(Path.Combine(directory, "th12.cfg"), token), StringComparison.OrdinalIgnoreCase))
            throw new IOException("接続準備中にゲーム・MOD・設定が変更されました。双方で部屋を作り直してください。");
    }

    public void Validate(SessionManifest manifest, byte[] session)
    {
        ValidateRules(manifest, session);
        ValidateAssets(Assets, manifest.Assets);
    }

    internal static void ValidateRules(SessionManifest manifest, byte[] session)
    {
        if (manifest.Protocol != SecureChannel.ProtocolVersion || manifest.Rules != NetBridge.RulesVersion || manifest.Delay is < 1 or > 12 ||
            manifest.Seed is < 1 or > 65535 || manifest.Session != Convert.ToHexString(session))
            throw new IOException("通信ルールまたはセッション情報が一致しません。");
    }

    internal static void ValidateAssets(AssetProfile expected, AssetProfile? actual)
    {
        // Resolve both identities against local known hashes before grouping
        // Steam's verified 1.00b engine with the retail 1.00b engine.
        var local = GameBuild.FromAssets(expected);
        var remote = GameBuild.FromAssets(actual);
        if (local.CompatibilityGroup != remote.CompatibilityGroup || actual!.Native != expected.Native ||
            actual.Configuration != expected.Configuration || actual.Dependencies != expected.Dependencies)
            throw new IOException("ゲーム・MOD・設定が一致しません。2人とも同じバージョンとMODを選択してください。1.00bは通常版とSteam版で接続できます。");
    }

    internal static byte[] Encode(SessionManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        return new byte[] { 1 }.Concat(json).ToArray();
    }
}
