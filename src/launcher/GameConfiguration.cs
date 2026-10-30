using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TH12Coop;

internal static class GameConfiguration
{
    internal static bool Valid(byte[] bytes) => bytes.Length == 60 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x00120001;

    // Verified against Supervisor::load_game_config and the display/audio/input
    // consumers in the three supported binaries. Unknown/gameplay fields are
    // deliberately not local (notably frame skip, replay mode and hold-to-focus).
    internal static bool IsLocalByte(int offset) => offset is >= 4 and <= 0x1d or 0x20 or 0x21 or >= 0x28 and <= 0x2f;

    internal static byte[] MergeLocal(byte[] shared, byte[] local)
    {
        if (!Valid(shared) || !Valid(local)) throw new IOException("TH12の設定ファイルが正しくありません。");
        var result = (byte[])shared.Clone();
        for (var index = 0; index < result.Length; ++index)
            if (IsLocalByte(index)) result[index] = local[index];
        // Only the display-mode prompt flag is local in the mixed flags word.
        result[0x39] = (byte)((result[0x39] & ~1) | (local[0x39] & 1));
        if (result[0x1a] >= 2 || result[0x1b] >= 3 || result[0x1c] >= 2 || result[0x1d] >= 4 || result[0x20] > 100 || result[0x21] > 100)
            throw new IOException("画面または音量の設定値が範囲外です。");
        return result;
    }

    internal static string SharedDigest(byte[] configuration)
    {
        if (!Valid(configuration)) throw new IOException("TH12の設定ファイルが正しくありません。");
        var shared = (byte[])configuration.Clone();
        for (var index = 0; index < shared.Length; ++index) if (IsLocalByte(index)) shared[index] = 0;
        shared[0x39] &= 0xfe;
        return Convert.ToHexString(SHA256.HashData(shared));
    }

    internal static async Task<byte[]> ReadLocal(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "th12.cfg");
        var contents = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : await Template(token);
        if (!Valid(contents)) throw new IOException("TH12の設定ファイルが破損しています。");
        return contents;
    }

    internal static int EffectiveVolume(byte[] configuration, bool music)
    {
        if (!Valid(configuration)) throw new IOException("TH12の設定ファイルが正しくありません。");
        return configuration[music ? 0x1b : 0x1c] == 0 ? 0 : Math.Min((int)configuration[music ? 0x20 : 0x21], 100);
    }

    internal static void ChangeVolume(byte[] configuration, bool music, int volume)
    {
        if (!Valid(configuration) || volume is < 0 or > 100) throw new IOException("音量の設定値が範囲外です。");
        var mode = music ? 0x1b : 0x1c;
        configuration[music ? 0x20 : 0x21] = (byte)volume;
        configuration[mode] = volume == 0 ? (byte)0 : configuration[mode] == 0 ? (byte)1 : configuration[mode];
    }

    internal static async Task Save(string path, byte[] contents, CancellationToken token)
    {
        if (!Valid(contents)) throw new IOException("TH12の設定ファイルが正しくありません。");
        var temporary = path + ".coop-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, contents, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<byte[]> Template(CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "th12-online.cfg"), token);
        if (!Valid(bytes)) throw new IOException("設定テンプレートが正しくありません。配布ファイルを復元してください。");
        return bytes;
    }

    internal static async Task Prepare(string destination, string source, CancellationToken token)
    {
        var path = Path.Combine(destination, "th12.cfg");
        if (File.Exists(path)) return; // Startup validates existing user settings without replacing them.
        var sourcePath = Path.Combine(source, "th12.cfg");
        var bytes = File.Exists(sourcePath) ? await File.ReadAllBytesAsync(sourcePath, token) : [];
        if (!Valid(bytes)) bytes = await Template(token);
        await WriteNew(path, bytes, token);
    }

    internal static async Task WriteNew(string path, byte[] contents, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Publish only complete files. Cancellation or an I/O failure must not
        // leave an empty th12.cfg/steam_appid.txt that blocks the next startup.
        var temporary = path + ".coop-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.WriteAsync(contents, token);
                await file.FlushAsync(token);
                file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path); // Never overwrite an existing user's file.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
