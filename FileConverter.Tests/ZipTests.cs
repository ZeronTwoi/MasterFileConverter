using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FileConverter.Core;
using FileConverter.Core.Converters;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// Zip archives are written by the app's own writer (pieces compressed side by side, files that
/// do not compress stored as they are) and unpacked on several threads. What it writes has to be
/// a zip every other program reads; what it unpacks has to be the files that went in.
/// </summary>
public class ZipTests(Samples samples) : IClassFixture<Samples>
{
    private static byte[] Text(int megabytes)
    {
        var builder = new StringBuilder();
        for (int line = 0; builder.Length < megabytes << 20; line++)
            builder.Append("line ").Append(line).Append(": the quick brown fox jumps over the lazy dog\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] Noise(int bytes, int seed)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A folder holding one of everything the writer treats differently.</summary>
    private (string Folder, Dictionary<string, byte[]> Files) Mixed()
    {
        var folder = Path.Combine(samples.Folder, "mixed");
        var files = new Dictionary<string, byte[]>
        {
            ["long text.txt"] = Text(5),                 // three pieces, compressed side by side
            ["film.bin"] = Noise(5 << 20, 1),            // sampled, found not to compress, stored
            ["short.txt"] = Text(1)[..3000],             // one piece, compressed
            ["photo.bin"] = Noise(40_000, 2),            // one piece, not worth compressing: stored
            ["empty.txt"] = [],
            ["klasör/çığ öşü.txt"] = "Merhaba dünya\n"u8.ToArray(),
            ["exactly two megabytes.bin"] = Text(3)[..(2 << 20)]
        };
        if (!Directory.Exists(folder))
        {
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }
        }
        return (folder, files);
    }

    private static List<ZipWriter.Entry> Entries(string folder, IEnumerable<string> names) =>
        names.Select(name => new ZipWriter.Entry(Path.Combine(folder, name), name)).ToList();

    private static byte[] Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The archive as three independent readers see it: .NET's, SharpCompress, and 7-Zip where it is installed.</summary>
    private static void AssertHolds(string zip, Dictionary<string, byte[]> files)
    {
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(files.Keys.Order(), archive.Entries.Select(e => e.FullName).Order());
            foreach (var entry in archive.Entries)
            {
                using var content = entry.Open();
                Assert.True(files[entry.FullName].AsSpan().SequenceEqual(Read(content)), $".NET reads {entry.FullName} differently");
            }
        }

        using (var archive = ArchiveFactory.Open(zip))
        {
            foreach (var entry in archive.Entries)
            {
                using var content = entry.OpenEntryStream();
                Assert.True(files[entry.Key!].AsSpan().SequenceEqual(Read(content)), $"SharpCompress reads {entry.Key} differently");
            }
        }

        if (ToolLocator.SevenZipPath is { } sevenZip && ToolLocator.SevenZipWritesZip)
        {
            var psi = new ProcessStartInfo(sevenZip) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("t");
            psi.ArgumentList.Add(zip);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "7-Zip's test of the archive failed: " + output);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void WhatTheWriterWritesEveryReaderReads(int level)
    {
        var (folder, files) = Mixed();
        var zip = Path.Combine(samples.NewOutputFolder(), "mixed.zip");

        ZipWriter.Write(zip, Entries(folder, files.Keys), level, null, TestContext.Current.CancellationToken);

        AssertHolds(zip, files);
        using var archive = ZipFile.OpenRead(zip);
        ZipArchiveEntry Entry(string name) => archive.Entries.Single(e => e.FullName == name);
        // text shrinks to a fraction; what does not compress is stored byte for byte, not made larger
        Assert.True(Entry("long text.txt").CompressedLength * 5 < Entry("long text.txt").Length);
        Assert.Equal(Entry("film.bin").Length, Entry("film.bin").CompressedLength);
        Assert.Equal(Entry("photo.bin").Length, Entry("photo.bin").CompressedLength);
        Assert.True(Entry("short.txt").CompressedLength < Entry("short.txt").Length);
        // the files' own dates, to the two seconds a zip can tell apart
        var written = File.GetLastWriteTime(Path.Combine(folder, "short.txt"));
        Assert.InRange((Entry("short.txt").LastWriteTime.DateTime - written).TotalSeconds, -2.1, 2.1);
    }

    [Fact]
    public void LevelZeroStoresEverything()
    {
        var (folder, files) = Mixed();
        var zip = Path.Combine(samples.NewOutputFolder(), "stored.zip");

        ZipWriter.Write(zip, Entries(folder, files.Keys), 0, null, TestContext.Current.CancellationToken);

        AssertHolds(zip, files);
        using var archive = ZipFile.OpenRead(zip);
        Assert.All(archive.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
    }

    /// <summary>
    /// Files of 4 GB and more need the format's 64-bit extension. Nobody wants a 4 GB test file, so
    /// the writer is told to use the extension from one byte on, and the readers have to cope.
    /// </summary>
    [Fact]
    public void TheSixtyFourBitExtensionIsReadableToo()
    {
        var (folder, files) = Mixed();
        var zip = Path.Combine(samples.NewOutputFolder(), "big.zip");

        ZipWriter.Write(zip, Entries(folder, files.Keys), 6, null, TestContext.Current.CancellationToken, zip64From: 1);

        AssertHolds(zip, files);
    }

    [Fact]
    public void ManySmallFilesKeepTheirOrder()
    {
        var folder = Path.Combine(samples.Folder, "many");
        Directory.CreateDirectory(folder);
        var files = new Dictionary<string, byte[]>();
        var names = new List<string>();
        for (int i = 0; i < 600; i++)
        {
            var name = $"note {i:D3}.txt";
            files[name] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat($"note number {i}\n", 1 + i % 50)));
            File.WriteAllBytes(Path.Combine(folder, name), files[name]);
            names.Add(name);
        }
        var zip = Path.Combine(samples.NewOutputFolder(), "many.zip");

        ZipWriter.Write(zip, Entries(folder, names), 6, null, TestContext.Current.CancellationToken);

        AssertHolds(zip, files);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(names, archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void TheChecksumOfPiecesIsTheChecksumOfTheWhole()
    {
        // .NET's gzip writer ends its stream with the same CRC-32, which makes it the reference
        static uint Reference(ReadOnlySpan<byte> data)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.NoCompression, leaveOpen: true))
                gzip.Write(data);
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer.ToArray().AsSpan((int)buffer.Length - 8));
        }

        var data = Noise(300_007, 3);
        Assert.Equal(Reference(data), Crc32.Compute(data));
        Assert.Equal(0u, Crc32.Compute([]));
        Assert.Equal(Reference(data.AsSpan(0, 5)), Crc32.Compute(data.AsSpan(0, 5)));

        foreach (int cut in new[] { 0, 1, 7, 4096, 123_456, 300_006, 300_007 })
        {
            uint first = Crc32.Compute(data.AsSpan(0, cut)), second = Crc32.Compute(data.AsSpan(cut));
            Assert.Equal(Reference(data), Crc32.Combine(first, second, data.Length - cut));
            // and continuing one checksum over both halves gives the same
            Assert.Equal(Reference(data), Crc32.Compute(data.AsSpan(cut), first));
        }
    }

    // ---------- through the converter ----------

    private static Dictionary<string, (byte[] Content, DateTime Modified)> TarContents(string tar)
    {
        var result = new Dictionary<string, (byte[], DateTime)>();
        using var stream = File.OpenRead(tar);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            // an empty file has no data stream at all
            result[entry.Name] = (entry.DataStream == null ? [] : Read(entry.DataStream), entry.ModificationTime.LocalDateTime);
        }
        return result;
    }

    [Fact]
    public async Task AZipIsUnpackedWithItsNamesAndDates()
    {
        var (folder, files) = Mixed();
        var zip = Path.Combine(samples.Folder, "to-unpack.zip");
        var when = new DateTime(2021, 3, 4, 5, 6, 8);
        File.SetLastWriteTime(Path.Combine(folder, "short.txt"), when);
        ZipWriter.Write(zip, Entries(folder, files.Keys), 6, null, TestContext.Current.CancellationToken);

        var result = await samples.ConvertAsync(zip, "tar");

        var unpacked = TarContents(result.OutputPath);
        Assert.Equal(files.Keys.Order(), unpacked.Keys.Order());
        foreach (var (name, content) in files)
            Assert.True(content.AsSpan().SequenceEqual(unpacked[name].Content), $"{name} came out differently");
        Assert.InRange((unpacked["short.txt"].Modified - when).TotalSeconds, -2.1, 2.1);
    }

    [Fact]
    public async Task NamesFromOlderWindowsToolsAreReadInTheirCodePage()
    {
        // such tools wrote names in the system's "OEM" code page and did not say which: 437 on an
        // English Windows (and what the app assumes where the system does not tell it), 850 on a
        // Western European one, 857 on a Turkish one — all three have the letters used here
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = FileConverter.Core.Platform.PlatformServices.Current.ArchiveNameCodePage;
        Assert.SkipUnless(codePage is 437 or 850 or 857 or 858, $"this system's OEM code page ({codePage}) lacks the test's letters");
        var zip = Path.Combine(samples.Folder, "old-names.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create, Encoding.GetEncoding(codePage)))
        {
            using var writer = new StreamWriter(archive.CreateEntry("résumé/Übersicht.txt").Open());
            writer.Write("content");
        }

        var result = await samples.ConvertAsync(zip, "tar");

        Assert.Equal(["résumé/Übersicht.txt"], TarContents(result.OutputPath).Keys);
    }

    [Fact]
    public async Task AZipWithAnotherKindOfCompressionStillConverts()
    {
        // bzip2 inside a zip is rare, and .NET's reader does not know it: the other reader takes over
        var zip = Path.Combine(samples.Folder, "bzip2.zip");
        var text = Text(1);
        var source = Path.Combine(samples.Folder, "bzip2-source.txt");
        File.WriteAllBytes(source, text);
        using (var stream = File.Create(zip))
        using (var writer = WriterFactory.Open(stream, ArchiveType.Zip, new WriterOptions(CompressionType.BZip2)))
            writer.Write("inside/text.txt", source);

        var result = await samples.ConvertAsync(zip, "tar");

        var unpacked = TarContents(result.OutputPath);
        Assert.Equal(["inside/text.txt"], unpacked.Keys);
        Assert.True(text.AsSpan().SequenceEqual(unpacked["inside/text.txt"].Content));
    }

    [Fact]
    public async Task PackingFilesGoesThroughTheSameWriter()
    {
        var (folder, files) = Mixed();
        var inputs = new[] { "long text.txt", "film.bin", "short.txt" }.Select(name => Path.Combine(folder, name)).ToList();

        var zip = await samples.CombineAsync(inputs, "zip");

        AssertHolds(zip, inputs.ToDictionary(path => Path.GetFileName(path), path => files[Path.GetFileName(path)]));
    }
}
