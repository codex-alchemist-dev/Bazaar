namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>CRC-32 (IEEE 802.3, as PNG uses it), table driven.</summary>
internal static class Crc32
{
    private static readonly uint[] s_table = BuildTable();

    /// <summary>The CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
