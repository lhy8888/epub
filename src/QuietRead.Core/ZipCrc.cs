namespace QuietRead.Core;

internal static class ZipCrc
{
    private static readonly uint[] Table = CreateTable();

    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) crc = Table[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }
}
