using System;
using System.IO;
using System.IO.Compression;

namespace Squish.Core.Documents;

/// <summary>
/// Zip readers in .NET inflate an entry without comparing it to the CRC stored in the archive, so a damaged entry reads as
/// plausible garbage. Writing that garbage into a new, valid-looking archive would launder the damage; instead a mismatch aborts
/// the run and the original stays as it is.
/// </summary>
internal static class ZipEntryChecksum
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }

    public static void Verify(ZipArchiveEntry entry, uint computed)
    {
        if (computed != entry.Crc32)
        {
            throw new InvalidDataException($"Archive entry {entry.FullName} is damaged (checksum mismatch).");
        }
    }
}
