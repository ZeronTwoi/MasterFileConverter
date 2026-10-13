using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileConverter.Core.Converters;

/// <summary>
/// Writes zip archives on every core. Deflate, the compression inside a zip, works through a
/// file from front to back on one core; but it may be restarted at any point, so the files are
/// cut into pieces of two megabytes that are compressed side by side and written in order. One
/// large file gains as much from that as many small ones.
///
/// Files that do not compress — photos, music, video, other archives — are recognised from three
/// samples and stored as they are, at the speed of the disk, instead of being worked through
/// for a saving of nothing.
/// </summary>
internal static class ZipWriter
{
    public sealed record Entry(string Path, string Name);

    private const int PieceBytes = 2 << 20;

    /// <summary>From this size on, a file's lengths no longer fit the format's 32-bit fields and take the 64-bit extension.</summary>
    private const long Zip64From = 0xFF00_0000;

    private sealed class Piece
    {
        public required int EntryIndex;
        public required bool First, Last;
        public required bool Stored;
        public required byte[] Buffer;      // rented; holds the bytes to write
        public required int Length;
        public required int RawLength;
        public required uint Crc;
    }

    private sealed class Written
    {
        public required Entry Entry;
        public required bool Zip64;
        public required ushort Time, Date;
        public required uint Attributes;
        public bool Stored;
        public long HeaderOffset;
        public uint Crc;
        public long Size, PackedSize;
    }

    /// <summary>A result that saves less than this share of the original is not worth being compressed for.</summary>
    private const double WorthIt = 0.98;

    /// <param name="level">0 (store everything) to 9; the zlib levels.</param>
    /// <param name="zip64From">For tests: the file size from which the 64-bit extension is used.</param>
    public static void Write(string output, IReadOnlyList<Entry> entries, int level, IProgress<double>? progress,
        CancellationToken ct, long zip64From = Zip64From)
    {
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 32);
        long totalBytes = Math.Max(1, entries.Sum(entry => new FileInfo(entry.Path).Length));
        long doneBytes = 0;

        using var file = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        var written = new List<Written>(entries.Count);
        var pending = new Queue<Task<Piece>>();

        void Finish(Task<Piece> task)
        {
            var piece = task.GetAwaiter().GetResult();
            try
            {
                // A file's header goes in right before its first piece: by then everything in front
                // of it is written, and it is known whether the file is stored or compressed.
                var entry = written[piece.EntryIndex];
                if (piece.First)
                {
                    entry.Stored = piece.Stored;
                    WriteHeader(file, entry);
                }
                file.Write(piece.Buffer, 0, piece.Length);
                entry.Crc = piece.First ? piece.Crc : Crc32.Combine(entry.Crc, piece.Crc, piece.RawLength);
                entry.Size += piece.RawLength;
                entry.PackedSize += piece.Length;
                if (piece.Last) CompleteHeader(file, entry);
                progress?.Report(0.98 * (doneBytes += piece.RawLength) / totalBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(piece.Buffer);
            }
        }

        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[index];
                using var source = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                long length = source.Length;
                written.Add(Describe(entry, zip64: length >= zip64From));

                // A file of one piece finds out by being compressed whether that was worth it.
                // A longer one is sampled first, so that a film is not worked through for nothing.
                bool onePiece = length <= PieceBytes;
                bool stored = level <= 0 || length == 0 || (!onePiece && !Compressible(source, level));

                bool first = true;
                long left = length;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var raw = ArrayPool<byte>.Shared.Rent(PieceBytes);
                    int read = source.ReadAtLeast(raw.AsSpan(0, PieceBytes), PieceBytes, throwOnEndOfStream: false);
                    left -= read;
                    bool last = left <= 0 || read < PieceBytes;
                    int entryIndex = index;
                    bool isFirst = first;
                    first = false;

                    while (pending.Count >= workers * 2) Finish(pending.Dequeue());
                    pending.Enqueue(Task.Run(() => stored
                        ? Store(raw, read, entryIndex, isFirst, last)
                        : Deflate(raw, read, level, entryIndex, isFirst, last, storeIfNotWorthIt: onePiece), ct));
                    if (last) break;
                }
            }
            while (pending.Count > 0) Finish(pending.Dequeue());
        }
        catch
        {
            // pieces still being made hold rented buffers: let them finish before the archive is given up
            foreach (var task in pending)
            {
                try { ArrayPool<byte>.Shared.Return(task.GetAwaiter().GetResult().Buffer); } catch { /* cancelled or failed */ }
            }
            throw;
        }

        WriteDirectory(file, written);
        progress?.Report(1.0);
    }

    /// <summary>
    /// Is the file worth compressing? Three samples of 64 KB — start, middle, end — are deflated;
    /// when together they lose less than 2 %, the file would not lose more.
    /// </summary>
    internal static bool Compressible(FileStream source, int level)
    {
        const int sample = 64 << 10;
        long length = source.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(sample);
        try
        {
            long raw = 0, packed = 0;
            foreach (long position in new[] { 0, (length - sample) / 2, length - sample })
            {
                if (position < 0) continue;
                source.Position = position;
                int read = source.ReadAtLeast(buffer.AsSpan(0, sample), sample, throwOnEndOfStream: false);
                using var sink = new CountingStream();
                using (var deflate = new DeflateStream(sink, new ZLibCompressionOptions { CompressionLevel = Math.Clamp(level, 3, 6) }, leaveOpen: true))
                    deflate.Write(buffer, 0, read);
                raw += read;
                packed += sink.Length;
            }
            return packed < raw * WorthIt;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            source.Position = 0;
        }
    }

    private static Piece Store(byte[] raw, int length, int entryIndex, bool first, bool last) => new()
    {
        EntryIndex = entryIndex, First = first, Last = last, Stored = true,
        Buffer = raw, Length = length, RawLength = length, Crc = Crc32.Compute(raw.AsSpan(0, length))
    };

    /// <summary>
    /// Compresses one piece so that it can be laid behind the piece before it: every piece but a
    /// file's last ends on a byte boundary without closing the stream ("sync flush").
    /// </summary>
    /// <param name="storeIfNotWorthIt">The piece is a whole file: when compressing saved next to nothing, it is stored instead.</param>
    private static Piece Deflate(byte[] raw, int length, int level, int entryIndex, bool first, bool last, bool storeIfNotWorthIt)
    {
        // deflate never grows data by more than a few bytes per 16 KB
        var packed = ArrayPool<byte>.Shared.Rent(length + (length >> 10) + 64);
        bool keepRaw = false;
        try
        {
            uint crc = Crc32.Compute(raw.AsSpan(0, length));
            var sink = new MemoryStream(packed, 0, packed.Length, writable: true);
            int packedLength;
            using (var deflate = new DeflateStream(sink, new ZLibCompressionOptions { CompressionLevel = Math.Clamp(level, 1, 9) }, leaveOpen: true))
            {
                deflate.Write(raw, 0, length);
                deflate.Flush();
                // what closing the stream adds — the end-of-data marker — belongs to the last piece only
                packedLength = (int)sink.Position;
                if (last)
                {
                    deflate.Dispose();
                    packedLength = (int)sink.Position;
                }
            }

            if (storeIfNotWorthIt && packedLength >= length * WorthIt)
            {
                keepRaw = true;
                return new Piece { EntryIndex = entryIndex, First = first, Last = last, Stored = true, Buffer = raw, Length = length, RawLength = length, Crc = crc };
            }
            return new Piece { EntryIndex = entryIndex, First = first, Last = last, Stored = false, Buffer = packed, Length = packedLength, RawLength = length, Crc = crc };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(keepRaw ? packed : raw);
        }
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }

    // ---------- the zip format ----------

    private const ushort Utf8Names = 1 << 11;

    private static bool IsAscii(string name) => name.All(c => c < 128);

    /// <summary>What is known about a file before any of it is read.</summary>
    private static Written Describe(Entry entry, bool zip64)
    {
        var time = File.GetLastWriteTime(entry.Path);
        if (time.Year < 1980) time = new DateTime(1980, 1, 1);
        if (time.Year > 2107) time = new DateTime(2107, 12, 31, 23, 59, 58);

        return new Written
        {
            Entry = entry,
            Zip64 = zip64,
            Time = (ushort)(time.Hour << 11 | time.Minute << 5 | time.Second / 2),
            Date = (ushort)((time.Year - 1980) << 9 | time.Month << 5 | time.Day),
            // on Linux the file's permissions travel along, so that a program is still one after unpacking
            Attributes = OperatingSystem.IsWindows() ? 0 : (0x8000u | (uint)File.GetUnixFileMode(entry.Path)) << 16
        };
    }

    /// <summary>Writes a file's header with its checksum and sizes left open; <see cref="CompleteHeader"/> fills them in.</summary>
    private static void WriteHeader(FileStream file, Written entry)
    {
        entry.HeaderOffset = file.Position;
        var name = Encoding.UTF8.GetBytes(entry.Entry.Name);
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034B50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)(entry.Zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], IsAscii(entry.Entry.Name) ? (ushort)0 : Utf8Names);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)(entry.Stored ? 0 : 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], entry.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], entry.Date);
        // 14: checksum, 18: packed size, 22: size — filled in later
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)(entry.Zip64 ? 20 : 0));
        file.Write(header);
        file.Write(name);
        if (entry.Zip64) file.Write(stackalloc byte[20]); // the 64-bit sizes, also filled in later
    }

    private static void CompleteHeader(FileStream file, Written entry)
    {
        if (!entry.Zip64 && (entry.Size >= uint.MaxValue || entry.PackedSize >= uint.MaxValue))
            throw new FriendlyException($"{Path.GetFileName(entry.Entry.Path)} grew while it was being packed.");

        long end = file.Position;
        Span<byte> fields = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(fields, entry.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[4..], entry.Zip64 ? uint.MaxValue : (uint)entry.PackedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[8..], entry.Zip64 ? uint.MaxValue : (uint)entry.Size);
        file.Position = entry.HeaderOffset + 14;
        file.Write(fields);

        if (entry.Zip64)
        {
            Span<byte> extra = stackalloc byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], entry.Size);
            BinaryPrimitives.WriteInt64LittleEndian(extra[12..], entry.PackedSize);
            file.Position = entry.HeaderOffset + 30 + Encoding.UTF8.GetByteCount(entry.Entry.Name);
            file.Write(extra);
        }
        file.Position = end;
    }

    /// <summary>The list of contents at the end of the archive, which is what readers go by.</summary>
    private static void WriteDirectory(FileStream file, List<Written> entries)
    {
        long start = file.Position;
        Span<byte> record = stackalloc byte[46];
        Span<byte> extra = stackalloc byte[28];

        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Entry.Name);

            // 64-bit values for whichever of size, packed size and position do not fit 32 bits, in that order
            int extraLength = 0;
            bool bigSize = entry.Zip64, bigOffset = entry.HeaderOffset >= uint.MaxValue;
            if (bigSize || bigOffset)
            {
                int at = 4;
                if (bigSize)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(extra[at..], entry.Size);
                    BinaryPrimitives.WriteInt64LittleEndian(extra[(at + 8)..], entry.PackedSize);
                    at += 16;
                }
                if (bigOffset)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(extra[at..], entry.HeaderOffset);
                    at += 8;
                }
                BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], (ushort)(at - 4));
                extraLength = at;
            }

            ushort needed = (ushort)(bigSize || bigOffset ? 45 : 20);
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014B50);
            // made by: the same format version, on Unix (3) where the attributes are a file mode
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], (ushort)((OperatingSystem.IsWindows() ? 0 : 3) << 8 | needed));
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], needed);
            BinaryPrimitives.WriteUInt16LittleEndian(record[8..], IsAscii(entry.Entry.Name) ? (ushort)0 : Utf8Names);
            BinaryPrimitives.WriteUInt16LittleEndian(record[10..], (ushort)(entry.Stored ? 0 : 8));
            BinaryPrimitives.WriteUInt16LittleEndian(record[12..], entry.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(record[14..], entry.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], bigSize ? uint.MaxValue : (uint)entry.PackedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[24..], bigSize ? uint.MaxValue : (uint)entry.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(record[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[30..], (ushort)extraLength);
            record[32..38].Clear(); // no comment, first disk, no internal attributes
            BinaryPrimitives.WriteUInt32LittleEndian(record[38..], entry.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(record[42..], bigOffset ? uint.MaxValue : (uint)entry.HeaderOffset);
            file.Write(record);
            file.Write(name);
            file.Write(extra[..extraLength]);
        }

        long size = file.Position - start;
        bool big = entries.Count >= ushort.MaxValue || size >= uint.MaxValue || start >= uint.MaxValue;
        if (big)
        {
            // the 64-bit end record, and the locator that says where it is
            Span<byte> end64 = stackalloc byte[56 + 20];
            BinaryPrimitives.WriteUInt32LittleEndian(end64, 0x06064B50);
            BinaryPrimitives.WriteInt64LittleEndian(end64[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(end64[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(end64[14..], 45);
            BinaryPrimitives.WriteInt64LittleEndian(end64[24..], entries.Count);
            BinaryPrimitives.WriteInt64LittleEndian(end64[32..], entries.Count);
            BinaryPrimitives.WriteInt64LittleEndian(end64[40..], size);
            BinaryPrimitives.WriteInt64LittleEndian(end64[48..], start);
            BinaryPrimitives.WriteUInt32LittleEndian(end64[56..], 0x07064B50);
            BinaryPrimitives.WriteInt64LittleEndian(end64[64..], start + size);
            BinaryPrimitives.WriteUInt32LittleEndian(end64[72..], 1);
            file.Write(end64);
        }

        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
        ushort count = (ushort)Math.Min(entries.Count, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)Math.Min(size, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)Math.Min(start, uint.MaxValue));
        file.Write(end);
    }
}
