using System.Diagnostics;
using System.IO.Compression;
using FileConverter.Core.Platform;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Converters;

/// <summary>
/// Archive conversions: repack zip/rar/7z/tar/gz into zip/7z/tar (and rar when
/// the RAR encoder is installed), plus packing any single file into an archive.
/// Zip archives are written by <see cref="ZipWriter"/> and read by .NET's own reader, both on
/// several cores; other formats are read by SharpCompress; 7z creation uses the 7-Zip CLI and
/// rar creation uses RARLAB's rar command (the only legal rar encoder).
/// </summary>
public static class ArchiveConversion
{
    public static Task ConvertAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(async () => await ConvertCoreAsync(input, output, options, progress, ct), ct);

    private static async Task ConvertCoreAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var srcExt = FormatRegistry.GetExtension(input);
        var dst = FormatRegistry.GetExtension(output);
        bool sourceIsArchive = FormatRegistry.GetCategory(srcExt) == FileCategory.Archive;

        string? tempDir = null;
        try
        {
            string contentRoot;
            bool extractedBy7Zip = false;
            if (sourceIsArchive)
            {
                tempDir = Path.Combine(Path.GetTempPath(), $"fc-arc-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                extractedBy7Zip = await ExtractArchiveAsync(input, tempDir, progress, ct);
                contentRoot = tempDir;
            }
            else
            {
                contentRoot = input; // pack a single file
            }

            progress?.Report(0.45);

            // Files that 7-Zip has just extracted are packed by 7-Zip as well when it can write the
            // target. Opening them from this process instead makes the antivirus scan each one first
            // (measured with Windows Defender: ~40 ms per file — 25 s for 600 small files, against
            // half a second when 7-Zip re-reads its own output).
            bool repackWith7Zip = extractedBy7Zip && ToolLocator.SevenZipWritesZip;

            switch (dst)
            {
                case "zip":
                    // the built-in zip writer cannot encrypt; a password goes through 7-Zip
                    if (repackWith7Zip || Password(options) != null) await CreateWith7ZipAsync(contentRoot, output, options, ct, "zip");
                    else CreateZip(contentRoot, output, options, progress, ct);
                    break;
                case "7z":
                    await CreateWith7ZipAsync(contentRoot, output, options, ct);
                    break;
                case "rar":
                    await CreateWithWinRarAsync(contentRoot, output, options, ct);
                    break;
                case "tar":
                    if (repackWith7Zip) await CreateWith7ZipAsync(contentRoot, output, options, ct, "tar");
                    else CreateTar(contentRoot, output, ct);
                    break;
                default:
                    throw new FriendlyException($"Unsupported archive format: {dst}");
            }

            progress?.Report(1.0);
        }
        finally
        {
            if (tempDir != null)
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------- packing many files into one archive ----------

    /// <summary>Packs several files into a single zip/7z/tar/rar archive.</summary>
    public static Task PackManyAsync(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(async () => await PackManyCoreAsync(inputs, output, options, progress, ct), ct);

    private static async Task PackManyCoreAsync(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var dst = FormatRegistry.GetExtension(output);
        var entries = UniqueEntryNames(inputs);
        bool encrypted = Password(options) != null;

        switch (dst)
        {
            case "zip" when !encrypted:
                ZipWriter.Write(output, entries.Select(entry => new ZipWriter.Entry(entry.Path, entry.Name)).ToList(),
                    GetZipLevel(options), new Share(progress, 0, 0.95), ct);
                break;
            case "tar":
            {
                using var stream = File.Create(output);
                using var writer = WriterFactory.Open(stream, ArchiveType.Tar, new WriterOptions(CompressionType.None));
                int done = 0;
                foreach (var (path, name) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write(name, path);
                    progress?.Report(0.95 * ++done / entries.Count);
                }
                break;
            }
            case "zip":
            case "7z":
            case "rar":
            {
                // The CLI tools take a directory, so stage the files (hardlink when
                // possible — no copy cost — else copy) under their archive names.
                var stage = Path.Combine(Path.GetTempPath(), $"fc-pack-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stage);
                try
                {
                    int done = 0;
                    foreach (var (path, name) in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = Path.Combine(stage, name);
                        if (!PlatformServices.Current.TryCreateHardLink(target, path))
                            File.Copy(path, target);
                        progress?.Report(0.3 * ++done / entries.Count);
                    }

                    if (dst == "rar") await CreateWithWinRarAsync(stage, output, options, ct);
                    else await CreateWith7ZipAsync(stage, output, options, ct, dst);
                }
                finally
                {
                    try { Directory.Delete(stage, recursive: true); } catch { /* best effort */ }
                }
                break;
            }
            default:
                throw new FriendlyException($"Unsupported archive format: {dst}");
        }

        progress?.Report(1.0);
    }

    /// <summary>Same file name from different folders → "name (1).ext" inside the archive.</summary>
    private static List<(string Path, string Name)> UniqueEntryNames(IReadOnlyList<string> inputs)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>(inputs.Count);
        foreach (var path in inputs)
        {
            var name = Path.GetFileName(path);
            if (!used.Add(name))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                int i = 1;
                do { name = $"{stem} ({i++}){ext}"; } while (!used.Add(name));
            }
            result.Add((path, name));
        }
        return result;
    }

    // ---------- reading ----------

    /// <summary>Unpacks an archive into <paramref name="destination"/>; returns true when 7-Zip did it.</summary>
    private static async Task<bool> ExtractArchiveAsync(string input, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        // .7z goes through 7-Zip itself when it is available. SharpCompress re-decompresses a solid
        // archive from the start for every file in it — measured on 600 small files: 520 s against
        // 0.7 s — and solid is what 7-Zip produces by default. A full, current 7-Zip also takes
        // zip/rar/tar, so the whole repack stays inside one tool (see ConvertCoreAsync).
        var sourceExt = FormatRegistry.GetExtension(input);
        bool sevenZipReadsIt = ToolLocator.SevenZipReads(sourceExt) &&
            (sourceExt == "7z" || ToolLocator.SevenZipExtractsSafely);
        if (sevenZipReadsIt && ToolLocator.SevenZipPath is { } sevenZip)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true // closed at once, so a password prompt fails instead of hanging
            };
            foreach (var arg in new[] { "x", Path.GetFullPath(input), "-o" + Path.GetFullPath(destination), "-y", "-bd" })
                psi.ArgumentList.Add(arg);
            try
            {
                await RunToolAsync(psi, "7-Zip", ct);
                if (Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any())
                {
                    progress?.Report(0.4);
                    return true;
                }
            }
            catch (FriendlyException)
            {
                // let the built-in reader have a go (and produce the error message if it fails too)
            }
        }

        if (sourceExt == "zip" && await Task.Run(() => TryExtractZip(input, destination, progress, ct), ct))
            return false;

        if (sourceExt is "gz" or "tgz" or "bz2")
        {
            await Task.Run(() => ExtractCompressedFile(input, sourceExt, destination, progress, ct), ct);
            return false;
        }
        if (sourceExt == "tar")
        {
            await Task.Run(() => ExtractTar(input, destination, progress, ct), ct);
            return false;
        }

        await Task.Run(() => ExtractWithSharpCompress(input, destination, progress, ct), ct);
        return false;
    }

    /// <summary>
    /// Unpacks a tar with .NET's own reader, which understands the pax and GNU extensions modern
    /// tools write (long names, extended headers) — a simpler reader turns those into stray
    /// "PaxHeaders" files. Only ordinary files are written; links and device nodes are left out.
    /// </summary>
    private static void ExtractTar(string input, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        long total = Math.Max(new FileInfo(input).Length, 1);
        int files = 0;
        try
        {
            using var stream = File.OpenRead(input);
            using var reader = new System.Formats.Tar.TarReader(stream);
            while (reader.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile
                    or System.Formats.Tar.TarEntryType.V7RegularFile
                    or System.Formats.Tar.TarEntryType.ContiguousFile))
                    continue;

                var target = Path.GetFullPath(Path.Combine(root, entry.Name));
                // an entry whose path tries to leave the extraction folder is not written
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, PlatformServices.Current.PathComparison)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                files++;
                progress?.Report(0.2 + 0.2 * Math.Min(1.0, (double)stream.Position / total));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw new FriendlyException("The tar archive is damaged or uses a format that can't be read.");
        }
        if (files == 0)
            throw new FriendlyException("The archive is empty or could not be read.");
    }

    /// <summary>
    /// gzip and bzip2 compress one file. Usually that file is a tar (name.tar.gz, name.tgz), and
    /// what the user means is the files inside it — so the outer layer comes off first and a tar
    /// found underneath is unpacked too. Anything else is the payload itself ("notes.txt.gz" → "notes.txt").
    /// </summary>
    private static void ExtractCompressedFile(string input, string sourceExt, string destination,
        IProgress<double>? progress, CancellationToken ct)
    {
        var innerName = Path.GetFileNameWithoutExtension(input);
        if (sourceExt == "tgz") innerName += ".tar";
        if (innerName.Length == 0) innerName = "content";

        var inner = Path.Combine(destination, innerName);
        try
        {
            using var source = File.OpenRead(input);
            using Stream decompressed = sourceExt == "bz2"
                ? new SharpCompress.Compressors.BZip2.BZip2Stream(source, SharpCompress.Compressors.CompressionMode.Decompress, false)
                : new GZipStream(source, CompressionMode.Decompress);
            using var target = File.Create(inner);

            var buffer = new byte[1 << 16];
            int read;
            while ((read = decompressed.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SharpCompress.Common.ArchiveException)
        {
            throw new FriendlyException("The archive is damaged or is not a gzip/bzip2 file.");
        }
        progress?.Report(0.2);

        if (!IsTar(inner)) return;

        // unpack next to a renamed copy, then drop the tar itself
        var tar = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".inner.tar");
        File.Move(inner, tar, overwrite: true);
        try
        {
            ExtractTar(tar, destination, progress, ct);
        }
        finally
        {
            try { File.Delete(tar); } catch { /* best effort */ }
        }
    }

    private static bool IsTar(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return SharpCompress.Archives.Tar.TarArchive.IsTarFile(stream);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks a zip with .NET's reader (the same native code that compresses) on several threads:
    /// every thread opens the archive for itself and takes its share of the files. Dates and, on
    /// Linux, permissions are restored. Returns false, leaving the folder as it was, for an archive
    /// that needs the other reader: one that is encrypted, uses a compression other than deflate,
    /// or cannot be read as a zip at all.
    /// </summary>
    private static bool TryExtractZip(string input, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        var names = ZipNameEncoding(input);
        var targets = new Dictionary<string, int>(PlatformServices.PathComparer); // where each file goes → its place in the archive
        try
        {
            using var archive = ZipFile.Open(input, ZipArchiveMode.Read, names);
            for (int index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a folder
                if (entry.IsEncrypted) return false;
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                // an entry whose path tries to leave the extraction folder is not written
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, PlatformServices.Current.PathComparison)) continue;
                targets[target] = index; // the same name twice: the later one counts, as when unpacking in order
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return false;
        }
        if (targets.Count == 0)
            throw new FriendlyException("The archive is empty or could not be read.");

        var files = targets.OrderBy(pair => pair.Value).ToList();
        foreach (var folder in files.Select(pair => Path.GetDirectoryName(pair.Key)!).Distinct(PlatformServices.PathComparer))
            Directory.CreateDirectory(folder);

        int workers = Math.Clamp(Math.Min(Environment.ProcessorCount / 2, files.Count / 4), 1, 8);
        int done = 0;
        try
        {
            Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct }, worker =>
            {
                using var archive = ZipFile.Open(input, ZipArchiveMode.Read, names);
                for (int n = worker; n < files.Count; n += workers)
                {
                    ct.ThrowIfCancellationRequested();
                    archive.Entries[files[n].Value].ExtractToFile(files[n].Key, overwrite: true);
                    progress?.Report(0.4 * Interlocked.Increment(ref done) / files.Count);
                }
            });
            return true;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is InvalidDataException or NotSupportedException))
        {
            // a kind of compression this reader does not know: start over with the other one
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // the other reader overwrites what is left
                }
            }
            return false;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>
    /// How the file names of a zip are to be read where it does not say: null for UTF-8, which is
    /// what current tools write. Names that are not valid UTF-8 come from older Windows tools,
    /// which wrote them in the system's "OEM" code page.
    /// </summary>
    private static System.Text.Encoding? ZipNameEncoding(string input)
    {
        try
        {
            using var archive = ZipFile.OpenRead(input);
            if (!archive.Entries.Any(entry => entry.FullName.Contains('\uFFFD'))) return null;
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(PlatformServices.Current.ArchiveNameCodePage);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static void ExtractWithSharpCompress(string input, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        using var archive = ArchiveFactory.Open(input);
        int total = archive.Entries.Count(e => !e.IsDirectory);
        if (total == 0)
            throw new FriendlyException("The archive is empty or could not be read.");

        int done = 0;

        // Where an entry may be written, or null when its path tries to leave the extraction root (zip-slip).
        string? SafeTarget(string? key)
        {
            var target = Path.GetFullPath(Path.Combine(root, key ?? $"entry{done}"));
            // compared the way the file system compares: on Linux "Root" and "root" are different folders
            return target.StartsWith(root + Path.DirectorySeparatorChar, PlatformServices.Current.PathComparison) ? target : null;
        }

        void Extracted()
        {
            done++;
            progress?.Report(0.4 * done / total);
        }

        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // solid data can only be read front to back: one pass with the sequential reader
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory || SafeTarget(reader.Entry.Key) is not { } target) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var fileStream = File.Create(target))
                    reader.WriteEntryTo(fileStream);
                Extracted();
            }
            return;
        }

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || SafeTarget(entry.Key) is not { } target) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var entryStream = entry.OpenEntryStream())
            using (var fileStream = File.Create(target))
                entryStream.CopyTo(fileStream);
            Extracted();
        }
    }

    // ---------- writing ----------

    private static void CreateZip(string contentRoot, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        List<ZipWriter.Entry> entries = File.Exists(contentRoot)
            ? [new ZipWriter.Entry(contentRoot, Path.GetFileName(contentRoot))]
            : Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories)
                .Select(file => new ZipWriter.Entry(file, Path.GetRelativePath(contentRoot, file).Replace('\\', '/')))
                .ToList();
        ZipWriter.Write(output, entries, GetZipLevel(options), new Share(progress, 0.45, 0.55), ct);
    }

    /// <summary>A part of a job's progress: 0…1 of the part becomes offset…offset + scale of the whole.</summary>
    private sealed class Share(IProgress<double>? whole, double offset, double scale) : IProgress<double>
    {
        public void Report(double value) => whole?.Report(offset + scale * value);
    }

    /// <summary>The password an archive is to be protected with, if any.</summary>
    private static string? Password(ConversionOptions options) =>
        options.Detail.Text(K.Password) is { Length: > 0 } password ? password : null;

    /// <summary>
    /// The deflate level of a zip, 0 (store) to 9. Measured on audio and on program code, level 3
    /// comes within a percent or two of level 6 in half the time or less, and level 1 is far off
    /// both (95 % against 72 % of the original for a WAV file) — so "Fast" is 3, not 1.
    /// </summary>
    private static int GetZipLevel(ConversionOptions options)
    {
        if (options.Detail.Int(K.Level, 0, 9) is { } level) return level;
        if (options.HasSizeTarget) return 9;
        return options.Speed switch
        {
            SpeedPreset.Fastest => 1,
            SpeedPreset.Fast => 3,
            SpeedPreset.Balanced => 6,
            _ => 9
        };
    }

    /// <summary>Creates an archive with the 7-Zip CLI: always "7z"; "zip" and "tar" need more than the reduced 7zr build.</summary>
    private static async Task CreateWith7ZipAsync(string contentRoot, string output, ConversionOptions options,
        CancellationToken ct, string type = "7z")
    {
        var sevenZip = ToolLocator.SevenZipPath
            ?? throw new FriendlyException("Creating .7z files needs the 7-Zip engine — use \"Set up engines\" first.");
        if (type != "7z" && !ToolLocator.SevenZipWritesZip)
            throw new FriendlyException(
                $"Protecting a .{type} with a password needs a full 7-Zip — use \"Set up engines\", or choose .7z instead.");

        // -mx: 0 store, 1 fastest … 9 max compression
        int level = options.Speed switch
        {
            SpeedPreset.Fastest => 1,
            SpeedPreset.Fast => 3,
            SpeedPreset.Balanced => 5,
            _ => 7
        };
        if (options.HasSizeTarget) level = options.SizePercent <= 40 ? 9 : Math.Max(level, 7);
        level = options.Detail.Int(K.Level, 0, 9) ?? level;
        if (StoresAsWell(contentRoot, options)) level = 0;

        File.Delete(output); // "a" would otherwise add to an existing archive
        bool isDir = Directory.Exists(contentRoot);
        var psi = ToolProcess(sevenZip, isDir ? contentRoot : Path.GetDirectoryName(contentRoot)!);
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add($"-t{type}");
        if (type != "tar") psi.ArgumentList.Add($"-mx{level}"); // tar only stores
        if (type == "7z" && options.Detail.Flag(K.Solid) is { } solid) psi.ArgumentList.Add(solid ? "-ms=on" : "-ms=off");
        if (type != "tar" && Password(options) is { } password)
        {
            psi.ArgumentList.Add("-p" + password);
            // 7z can hide the file names too; zip gets AES-256 instead of its weak legacy cipher
            psi.ArgumentList.Add(type == "7z" ? "-mhe=on" : "-mem=AES256");
        }
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bd");
        psi.ArgumentList.Add(Path.GetFullPath(output));
        // the tool expands "*" itself (no shell is involved), taking everything in the working folder
        psi.ArgumentList.Add(isDir ? "*" : Path.GetFullPath(contentRoot));

        await RunToolAsync(psi, "7-Zip", ct);
    }

    private static async Task CreateWithWinRarAsync(string contentRoot, string output, ConversionOptions options, CancellationToken ct)
    {
        var rar = ToolLocator.RarPath
            ?? throw new FriendlyException("Creating .rar files requires the RAR encoder (WinRAR / RAR for Linux) to be installed — RAR is a proprietary format.");

        int level = options.Speed switch
        {
            SpeedPreset.Fastest => 1,
            SpeedPreset.Fast => 2,
            SpeedPreset.Balanced => 3,
            _ => 5
        };
        if (options.HasSizeTarget) level = 5;
        // RAR's own scale runs 0 (store) to 5 (best); the setting uses the 0–9 of the other formats
        if (options.Detail.Int(K.Level, 0, 9) is { } chosen) level = (int)Math.Round(chosen * 5 / 9.0);
        if (StoresAsWell(contentRoot, options)) level = 0;

        bool isDir = Directory.Exists(contentRoot);
        var psi = ToolProcess(rar, isDir ? contentRoot : Path.GetDirectoryName(contentRoot)!);
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add("-r");   // into subfolders — without it everything below the top level is left out
        psi.ArgumentList.Add("-ep1");
        psi.ArgumentList.Add($"-m{level}");
        if (Password(options) is { } password) psi.ArgumentList.Add("-hp" + password); // contents and file names
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(Path.GetFullPath(output));
        psi.ArgumentList.Add(isDir ? "*" : Path.GetFullPath(contentRoot));

        await RunToolAsync(psi, "RAR", ct);
    }

    /// <summary>
    /// A single file that does not compress — a film, a photo, another archive — is put into a
    /// 7z or rar unchanged: their compressors would work through it at a few megabytes a second
    /// to save nothing. Only when no level was asked for; and only for one file, because several
    /// files that each do not compress may still resemble each other, which those formats use.
    /// </summary>
    private static bool StoresAsWell(string content, ConversionOptions options)
    {
        if (options.Detail.Has(K.Level) || options.HasSizeTarget || !File.Exists(content)) return false;
        try
        {
            using var source = File.OpenRead(content);
            return source.Length > 0 && !ZipWriter.Compressible(source, 6);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ProcessStartInfo ToolProcess(string executable, string workingDirectory) => new()
    {
        FileName = executable,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void CreateTar(string contentRoot, string output, CancellationToken ct)
    {
        File.Delete(output);
        using var stream = File.Create(output);
        using var writer = WriterFactory.Open(stream, ArchiveType.Tar, new WriterOptions(CompressionType.None));

        if (File.Exists(contentRoot))
        {
            writer.Write(Path.GetFileName(contentRoot), contentRoot);
            return;
        }

        foreach (var file in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(Path.GetRelativePath(contentRoot, file).Replace('\\', '/'), file);
        }
    }

    private static async Task RunToolAsync(ProcessStartInfo psi, string toolName, CancellationToken ct)
    {
        using var process = Process.Start(psi)!;
        await using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });
        if (psi.RedirectStandardInput) process.StandardInput.Close();

        // both pipes are drained, or a tool listing thousands of files would block on a full buffer
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await stdoutTask;
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            if (detail.Length > 400) detail = detail[..400];
            throw new FriendlyException($"{toolName} failed{(detail.Length > 0 ? ": " + detail : $" (exit code {process.ExitCode})")}");
        }
    }
}
