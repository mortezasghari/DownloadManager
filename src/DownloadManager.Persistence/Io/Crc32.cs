namespace DownloadManager.Persistence.Io;

/// <summary>
/// Standard IEEE 802.3 CRC-32 (reflected, polynomial 0xEDB88320). Hand-rolled to avoid taking a
/// dependency for ~30 lines (spec §1: the BCL can't, but this is trivial). Used to detect torn or
/// corrupt progress-log records (spec §6b).
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Table[(byte)(crc ^ b)];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}