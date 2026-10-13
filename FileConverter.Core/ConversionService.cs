using FileConverter.Core.Converters;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;

namespace FileConverter.Core;

/// <summary>
/// Routes a conversion to the right engine and manages parallelism (see <see cref="WorkGates"/>):
/// single-threaded jobs — images, audio, documents, stream copies — run one per core, while
/// video encodes, which saturate the CPU on their own, are kept to a few at a time.
/// </summary>
public sealed class ConversionService
{
    private readonly WorkGates _gates = new();

    /// <summary>
    /// Output path next to the source (or in <paramref name="outputDirectory"/>). When the name is
    /// taken it is numbered — unless <paramref name="whenExists"/> says an existing file may be
    /// replaced (or the job skipped: the caller checks <see cref="File.Exists"/> on the result).
    /// A batch passes one shared <paramref name="reserved"/> set so that files converting at the
    /// same time (photo.png and photo.jpg → photo.webp) can't both claim the same name.
    /// </summary>
    public static string BuildOutputPath(string input, string targetExt, string? outputDirectory,
        ISet<string>? reserved = null, ExistingFileAction whenExists = ExistingFileAction.KeepBoth)
    {
        var dir = outputDirectory ?? Path.GetDirectoryName(input)!;
        var baseName = Path.GetFileNameWithoutExtension(input);
        var candidate = Path.Combine(dir, $"{baseName}.{targetExt}");

        // the source itself and names claimed by this batch are never up for grabs
        bool Taken(string path) =>
            string.Equals(path, input, PlatformServices.Current.PathComparison) ||
            reserved?.Contains(path) == true ||
            (whenExists == ExistingFileAction.KeepBoth && File.Exists(path));

        for (int i = 1; Taken(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}).{targetExt}");

        reserved?.Add(candidate);
        return candidate;
    }

    public static string BuildNamedOutputPath(string directory, string baseName, string targetExt,
        ExistingFileAction whenExists = ExistingFileAction.KeepBoth)
    {
        var candidate = Path.Combine(directory, $"{baseName}.{targetExt}");
        if (whenExists != ExistingFileAction.KeepBoth) return candidate;
        for (int i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{baseName} ({i}).{targetExt}");
        return candidate;
    }

    /// <summary>"photo.jpg" → "photo.fc-1a2b3c4d.jpg" in the same folder: same extension, so every engine treats it alike.</summary>
    private static string StagingPath(string output) => Path.Combine(Path.GetDirectoryName(output)!,
        $"{Path.GetFileNameWithoutExtension(output)}.fc-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(output)}");

    /// <summary>
    /// The file name a result finally has. A conversion that replaces an existing file is handed a
    /// staging name; what it names after its output — a folder of pages, a file inside one — is
    /// named after this instead.
    /// </summary>
    internal static string FinalName(string output) =>
        System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(output), @"\.fc-[0-9a-f]{8}(?=\.[^.]+$)", "");

    /// <summary>
    /// Converts one file. The result's path is usually <paramref name="output"/>, but a
    /// folder when the conversion fans out (animated gif → frames, video → frame sequence,
    /// multi-page pdf → images).
    /// </summary>
    public async Task<ConversionResult> ConvertAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        // Replacing an existing file: write beside it first, so a failed or cancelled
        // conversion leaves the old file as it was.
        if (File.Exists(output))
        {
            var staging = StagingPath(output);
            var result = await ConvertCoreAsync(input, staging, options, progress, ct);
            if (!string.Equals(result.OutputPath, staging, PlatformServices.Current.PathComparison))
                return result; // fanned out into a folder of its own
            File.Move(staging, output, overwrite: true);
            return result with { OutputPath = output };
        }
        return await ConvertCoreAsync(input, output, options, progress, ct);
    }

    private async Task<ConversionResult> ConvertCoreAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var srcExt = FormatRegistry.GetExtension(input);
        var dstExt = FormatRegistry.GetExtension(output);
        var engine = FormatRegistry.GetEngine(srcExt, dstExt);

        try
        {
            switch (engine)
            {
                case FileCategory.Image:
                    using (await _gates.EnterAsync(JobWeight.Image, ct))
                        return await ImageConversion.ConvertAsync(input, output, options, progress, ct);

                case FileCategory.Video:
                    // media jobs choose their own queue once they know whether they copy or encode
                    return await MediaConversion.ConvertAsync(input, output, options, _gates, progress, ct);

                case FileCategory.Document:
                    using (await _gates.EnterAsync(JobWeight.Light, ct))
                        return await DocumentConversion.ConvertAsync(input, output, options, progress, ct);

                case FileCategory.Archive:
                    using (await _gates.EnterAsync(JobWeight.Archive, ct))
                    {
                        await ArchiveConversion.ConvertAsync(input, output, options, progress, ct);
                        return new ConversionResult(output);
                    }

                default:
                    throw new FriendlyException($"No conversion available from .{srcExt} to .{dstExt}.");
            }
        }
        catch
        {
            // don't leave half-written outputs behind
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Combines several files into one output (images → one PDF/GIF/TIFF, any
    /// files → one archive). Inputs are ordered by natural filename order and the
    /// output is named from their common prefix or the containing folder.
    /// Returns the produced file's path, or null when it exists and the user chose to skip.
    /// </summary>
    public async Task<string?> ConvertManyAsync(IReadOnlyList<string> inputs, string targetExt,
        ConversionOptions options, string? outputDirectory, IProgress<double>? progress, CancellationToken ct,
        ExistingFileAction whenExists = ExistingFileAction.KeepBoth)
    {
        if (inputs.Count == 0) throw new ArgumentException("No input files.", nameof(inputs));

        var sorted = NaturalSort.SortPathsByFileName(inputs);
        var dir = outputDirectory ?? Path.GetDirectoryName(sorted[0])!;
        var output = BuildNamedOutputPath(dir, MergeConversion.SuggestBaseName(sorted), targetExt, whenExists);
        bool replacing = File.Exists(output);
        if (replacing && whenExists == ExistingFileAction.Skip) return null;

        // a merge decodes its sources in parallel by itself, so it queues with the heavy jobs
        bool archive = FormatRegistry.GetCategory(targetExt) == FileCategory.Archive;
        using var slot = await _gates.EnterAsync(archive ? JobWeight.Archive : JobWeight.Heavy, ct);

        var writeTo = replacing ? StagingPath(output) : output;
        try
        {
            await MergeConversion.ConvertManyAsync(sorted, writeTo, options, progress, ct);
            if (replacing) File.Move(writeTo, output, overwrite: true);
            return output;
        }
        catch
        {
            try { if (File.Exists(writeTo)) File.Delete(writeTo); } catch { }
            throw;
        }
    }
}
