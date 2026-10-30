using System.IO.MemoryMappedFiles;

namespace TH12Coop;

internal sealed record PlayerSelection(int PlayerOne, int PlayerTwo)
{
    internal void Validate()
    {
        if (PlayerOne is < 0 or > 5 || PlayerTwo is < 0 or > 5)
            throw new IOException("機体・装備の選択が正しくありません。");
    }

    internal MemoryMappedFile Publish(int pid)
    {
        Validate();
        var mapping = MemoryMappedFile.CreateNew($"Local\\TH12CoopPlayers_{pid}", 16);
        try
        {
            using var memory = mapping.CreateViewAccessor(0, 16);
            memory.Write(0, 0x43323154u); memory.Write(4, 1u);
            memory.Write(8, (uint)PlayerOne); memory.Write(12, (uint)PlayerTwo);
            return mapping;
        }
        catch { mapping.Dispose(); throw; }
    }
}
