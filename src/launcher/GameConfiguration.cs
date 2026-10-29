using System.Buffers.Binary;

namespace TH12Coop;

internal static class GameConfiguration
{
    internal static bool Valid(byte[] bytes) => bytes.Length == 60 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x00120001;

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
