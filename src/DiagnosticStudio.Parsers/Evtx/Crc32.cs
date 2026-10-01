namespace DiagnosticStudio.Parsers.Evtx;

/// <summary>Standard CRC-32 (IEEE 802.3), as used by the EVTX file and chunk headers.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var crc = ~seed;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
