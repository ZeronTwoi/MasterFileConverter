namespace FileConverter.Core;

/// <summary>
/// The checksum of zip archives and PNG chunks (CRC-32, polynomial 0xEDB88320), eight bytes at a
/// time — and the arithmetic that joins the checksums of two halves into that of the whole, so a
/// file read in pieces on several threads still gets its one checksum.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Table[n * 256 + b]: the checksum of byte b followed by n zero bytes.</summary>
    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (uint b = 0; b < 256; b++)
        {
            uint c = b;
            for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[b] = c;
        }
        for (uint b = 0; b < 256; b++)
        {
            uint c = table[b];
            for (int n = 1; n < 8; n++)
            {
                c = table[c & 0xFF] ^ (c >> 8);
                table[n * 256 + b] = c;
            }
        }
        return table;
    }

    /// <summary>The checksum of <paramref name="data"/>, continuing from <paramref name="previous"/> (0 to start).</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint previous = 0)
    {
        var table = Table;
        uint crc = ~previous;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint low = crc ^ (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            crc = table[7 * 256 + (low & 0xFF)] ^ table[6 * 256 + ((low >> 8) & 0xFF)] ^
                  table[5 * 256 + ((low >> 16) & 0xFF)] ^ table[4 * 256 + (low >> 24)] ^
                  table[3 * 256 + data[i + 4]] ^ table[2 * 256 + data[i + 5]] ^
                  table[1 * 256 + data[i + 6]] ^ table[data[i + 7]];
        }
        for (; i < data.Length; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>
    /// The checksum of two runs of bytes laid end to end, from the checksum of each and the length
    /// of the second. (Appending <paramref name="secondLength"/> zero bytes to the first run is a
    /// linear operation on its checksum; it is applied by repeated squaring, as zlib does.)
    /// </summary>
    public static uint Combine(uint first, uint second, long secondLength)
    {
        if (secondLength <= 0) return first;

        Span<uint> even = stackalloc uint[32], odd = stackalloc uint[32];
        // the operator for one zero bit …
        odd[0] = 0xEDB88320u;
        for (int n = 1; n < 32; n++) odd[n] = 1u << (n - 1);
        // … squared to two bits, then four: one zero half-byte
        Square(even, odd);
        Square(odd, even);

        // from there one squaring per bit of the length: a byte, two bytes, four …
        do
        {
            Square(even, odd);
            if ((secondLength & 1) != 0) first = Times(even, first);
            secondLength >>= 1;
            if (secondLength == 0) break;

            Square(odd, even);
            if ((secondLength & 1) != 0) first = Times(odd, first);
            secondLength >>= 1;
        } while (secondLength != 0);

        return first ^ second;
    }

    private static uint Times(ReadOnlySpan<uint> matrix, uint vector)
    {
        uint sum = 0;
        for (int n = 0; vector != 0; vector >>= 1, n++)
            if ((vector & 1) != 0) sum ^= matrix[n];
        return sum;
    }

    private static void Square(Span<uint> square, ReadOnlySpan<uint> matrix)
    {
        for (int n = 0; n < 32; n++) square[n] = Times(matrix, matrix[n]);
    }
}
