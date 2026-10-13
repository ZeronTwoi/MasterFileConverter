namespace FileConverter.Core.Converters;

/// <summary>
/// Joins GIF animations end to end without touching their frames. A long clip is turned into a
/// GIF as several stretches at once (see <see cref="MediaConversion"/>); each stretch comes out as
/// a GIF of its own, with the same size and the same colour table, and a GIF is simply its
/// frames one after another behind a header — so the join is a copy.
/// </summary>
internal static class GifFiles
{
    private const byte ExtensionIntroducer = 0x21, GraphicControl = 0xF9, ImageDescriptor = 0x2C, Trailer = 0x3B;

    /// <summary>
    /// Writes the parts as one animation. Returns false, having written nothing usable, when
    /// the parts do not fit together (different size or colours) or are not complete GIF files.
    /// </summary>
    public static bool TryJoin(IReadOnlyList<string> parts, string output, CancellationToken ct)
    {
        byte[]? shared = null; // header, screen descriptor and colour table of the first part
        using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        int filled = 0;

        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            using var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            if (source.Length < 14) return false;

            var head = new byte[13];
            source.ReadExactly(head);
            if (head[0] != 'G' || head[1] != 'I' || head[2] != 'F') return false;
            int tableBytes = (head[10] & 0x80) != 0 ? 3 << ((head[10] & 0x07) + 1) : 0;
            var start = new byte[13 + tableBytes];
            head.CopyTo(start, 0);
            source.ReadExactly(start.AsSpan(13));

            // everything up to the trailer byte that ends a GIF
            source.Position = source.Length - 1;
            if (source.ReadByte() != Trailer) return false;
            source.Position = start.Length;
            long end = source.Length - 1;

            if (shared == null)
            {
                shared = start;
                target.Write(start);
            }
            else
            {
                if (!start.AsSpan().SequenceEqual(shared)) return false;
                // the looping instruction and comments belong to the animation as a whole: once is enough
                if (!SkipToFirstFrame(source, end)) continue;
            }

            if (source.Position < end) filled++;
            long left = end - source.Position;
            while (left > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) return false;
                target.Write(buffer, 0, read);
                left -= read;
            }
        }

        target.WriteByte(Trailer);
        return shared != null && filled > 0;
    }

    /// <summary>Moves past the blocks in front of a part's first frame; false when it has no frame at all.</summary>
    private static bool SkipToFirstFrame(Stream source, long end)
    {
        while (source.Position < end)
        {
            long blockStart = source.Position;
            int introducer = source.ReadByte();
            if (introducer == ImageDescriptor) { source.Position = blockStart; return true; }
            if (introducer != ExtensionIntroducer) return false;

            int label = source.ReadByte();
            if (label == GraphicControl) { source.Position = blockStart; return true; } // a frame's timing: the frame starts here
            // any other extension: a chain of length-prefixed blocks, ended by a zero length
            for (int size = source.ReadByte(); size > 0; size = source.ReadByte())
                source.Position += size;
        }
        return false;
    }
}
