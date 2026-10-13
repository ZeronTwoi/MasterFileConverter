using System.Buffers.Binary;

namespace FileConverter.Core.Converters;

/// <summary>
/// Joins single-page TIFF files into one file of several pages. Writing a multi-page TIFF the
/// ordinary way compresses the pages one after another on one core; this way every page is
/// compressed as a file of its own, all at once, and the files are then laid end to end.
///
/// A TIFF is a set of directories that point at their data by position in the file, and the
/// pages are a chain of them. So a file moved to a later place needs every position in it raised
/// by the distance moved, and the last directory of each file pointed at the first of the next.
/// </summary>
internal static class TiffFiles
{
    /// <summary>Tags whose values are themselves positions in the file.</summary>
    private static bool HoldsPositions(ushort tag) => tag is
        273 or      // StripOffsets
        324 or      // TileOffsets
        288 or      // FreeOffsets
        513 or      // JPEGInterchangeFormat
        519 or 520 or 521 || // JPEG tables
        PointsToDirectory(tag);

    /// <summary>Tags that point at further directories, which have positions of their own.</summary>
    private static bool PointsToDirectory(ushort tag) => tag is
        330 or      // SubIFDs
        34665 or    // EXIF
        34853 or    // GPS
        40965;      // Interoperability

    /// <summary>Bytes per value of each TIFF field type (index = type number); 0 = unknown.</summary>
    private static readonly int[] TypeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4];

    /// <summary>
    /// Writes the files as one TIFF, in order. Returns false — the output is then not usable — when
    /// one of them is not a TIFF this can move: a BigTIFF, a damaged file, or one in the other byte order.
    /// The files' bytes are changed in the process.
    /// </summary>
    public static bool TryJoin(IReadOnlyList<byte[]> files, string output)
    {
        if (files.Count == 0) return false;
        long total = files.Sum(file => (long)file.Length + 1);
        if (total >= uint.MaxValue) return false; // positions in a TIFF are 32 bits wide

        bool? littleEndian = null;
        var first = new uint[files.Count];      // each file's first directory, as a position in the output
        var linkAt = new int[files.Count];      // inside the file: the "next directory" field of its last page

        long position = 0;
        for (int k = 0; k < files.Count; k++)
        {
            var file = files[k];
            if (file.Length < 8) return false;
            bool little = file[0] == (byte)'I' && file[1] == (byte)'I';
            if (!little && !(file[0] == (byte)'M' && file[1] == (byte)'M')) return false;
            littleEndian ??= little;
            if (littleEndian != little) return false;

            var mover = new Mover(file, little, (uint)position);
            if (mover.U16(2) != 42) return false;
            uint directory = mover.U32(4);
            if (!mover.MoveChain(directory, out linkAt[k])) return false;

            first[k] = directory + (uint)position;
            position += file.Length + (file.Length & 1); // directories start on even positions
        }

        // each file's last page points at the next file's first
        for (int k = 0; k + 1 < files.Count; k++)
            Write32(files[k].AsSpan(linkAt[k]), first[k + 1], littleEndian!.Value);

        using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        for (int k = 0; k < files.Count; k++)
        {
            target.Write(files[k]);
            if ((files[k].Length & 1) != 0) target.WriteByte(0);
        }
        return true;
    }

    private static void Write32(Span<byte> at, uint value, bool little)
    {
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(at, value);
        else BinaryPrimitives.WriteUInt32BigEndian(at, value);
    }

    /// <summary>Raises every position inside one file by the distance the file is moved.</summary>
    private sealed class Mover(byte[] file, bool little, uint distance)
    {
        private readonly HashSet<uint> _seen = [];

        public ushort U16(long at) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)at))
            : BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan((int)at));

        public uint U32(long at) => little
            ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)at))
            : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)at));

        private void Put16(long at, ushort value)
        {
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan((int)at), value);
            else BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan((int)at), value);
        }

        private void Put32(long at, uint value) => Write32(file.AsSpan((int)at), value, little);

        /// <summary>
        /// Moves a chain of directories (pages, or the directories a tag points to) and all they
        /// point at. <paramref name="lastLink"/> is where the chain's final "next" field lies.
        /// </summary>
        public bool MoveChain(uint directory, out int lastLink)
        {
            lastLink = 0;
            while (true)
            {
                if (directory == 0 || (long)directory + 6 > file.Length || !_seen.Add(directory)) return false;
                int entries = U16(directory);
                long end = directory + 2 + entries * 12L;
                if (end + 4 > file.Length) return false;

                for (int i = 0; i < entries; i++)
                {
                    long entry = directory + 2 + i * 12L;
                    ushort tag = U16(entry), type = U16(entry + 2);
                    uint count = U32(entry + 4);
                    if (type >= TypeSize.Length || TypeSize[type] == 0) return false;
                    long size = (long)TypeSize[type] * count;

                    // values of more than four bytes lie elsewhere, and the entry holds their position
                    long values = entry + 8;
                    if (size > 4)
                    {
                        values = U32(entry + 8);
                        if (values + size > file.Length) return false;
                        Put32(entry + 8, (uint)values + distance);
                    }
                    if (!HoldsPositions(tag)) continue;

                    for (uint n = 0; n < count; n++)
                    {
                        if (type is 4 or 13)
                        {
                            uint value = U32(values + n * 4);
                            if (PointsToDirectory(tag) && !MoveChain(value, out _)) return false;
                            Put32(values + n * 4, value + distance);
                        }
                        else if (type == 3)
                        {
                            // small files store positions in two bytes; moved, they may no longer fit
                            long moved = U16(values + n * 2) + (long)distance;
                            if (moved > ushort.MaxValue || PointsToDirectory(tag)) return false;
                            Put16(values + n * 2, (ushort)moved);
                        }
                        else
                        {
                            return false;
                        }
                    }
                }

                uint next = U32(end);
                if (next == 0)
                {
                    lastLink = (int)end;
                    return true;
                }
                Put32(end, next + distance);
                directory = next;
            }
        }
    }
}
