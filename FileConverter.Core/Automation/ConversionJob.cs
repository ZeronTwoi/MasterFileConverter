using FileConverter.Core.Converters;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;

namespace FileConverter.Core.Automation;

/// <summary>Why a job cannot run, in words and as a code a program can act on (see <see cref="JobErrors"/>).</summary>
public sealed record JobProblem(string Code, string Message, IReadOnlyList<string>? ValidTargets = null);

/// <summary>The error codes of <see cref="JobProblem"/> and <see cref="JobFile.ErrorCode"/>: part of the agent interface, not to be renamed.</summary>
public static class JobErrors
{
    public const string BadArguments = "bad_arguments";
    public const string InputNotFound = "input_not_found";
    public const string UnsupportedConversion = "unsupported_conversion";
    public const string InvalidSetting = "invalid_setting";
    public const string EngineMissing = "engine_missing";
    public const string ConversionFailed = "conversion_failed";
    public const string Cancelled = "cancelled";
}

/// <summary>What became of one result of a job.</summary>
public static class JobStatus
{
    public const string Converted = "converted";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Planned = "planned";
}

/// <summary>One result of a job: several inputs when they are combined into it.</summary>
public sealed record JobFile(IReadOnlyList<string> Inputs, string Output, string Status)
{
    public string? Note { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public double Seconds { get; init; }

    /// <summary>In a plan: the name is taken, and the file there would be replaced.</summary>
    public bool Replaces { get; init; }

    /// <summary>In a plan: this conversion can come out as a folder of files (a PDF's pages, a video's frames).</summary>
    public bool MayBecomeFolder { get; init; }
}

/// <summary>
/// One conversion as a script or an agent asks for it: the command line and the MCP server are two
/// ways of saying the same thing, so that they check it and carry it out the same way.
/// </summary>
public sealed record ConversionJob
{
    public required IReadOnlyList<string> Inputs { get; init; }
    public required string Target { get; init; }
    public string? OutputDirectory { get; init; }
    public int SizePercent { get; init; } = ResultLevels.All[ResultLevels.Default].SizePercent;
    public SpeedPreset Speed { get; init; } = ResultLevels.All[ResultLevels.Default].Speed;
    public bool UseGpu { get; init; } = true;
    public ExistingFileAction WhenExists { get; init; } = ExistingFileAction.KeepBoth;
    public bool Each { get; init; }

    /// <summary>Precise settings, as "key=value".</summary>
    public IReadOnlyList<string> Settings { get; init; } = [];

    /// <summary>A job that has been checked and can be planned or run.</summary>
    public sealed record Checked(ConversionJob Job, IReadOnlyList<string> Inputs, string Target, bool Merge,
        IReadOnlyDictionary<string, DetailSettings> Details);

    /// <summary>What --set accepts for a conversion: the panel's settings, without its own way of choosing among them.</summary>
    internal static List<SettingDefinition> SettableSettings(string sourceExt, string targetExt, bool merging = false) =>
        SettingCatalog.For(sourceExt, targetExt, merging).Where(d => d.OnCommandLine).ToList();

    internal static string Describe(SettingDefinition definition) => definition.Kind switch
    {
        SettingKind.Number => $"a number from {definition.Min:0.##} to {definition.Max:0.##}",
        SettingKind.Choice => "one of: " + string.Join(", ", definition.Choices.Select(c => c.Value)),
        _ => "text"
    };

    /// <summary>Checks everything that can be known before a file is touched. Nothing is written.</summary>
    public (Checked? Job, JobProblem? Problem) Check()
    {
        if (Inputs.Count == 0) return (null, new(JobErrors.BadArguments, "No input files."));

        var target = Target.TrimStart('.').ToLowerInvariant();
        var inputs = Inputs.Select(Path.GetFullPath).ToList();
        foreach (var input in inputs)
        {
            if (!File.Exists(input))
                return (null, new(JobErrors.InputNotFound, "input file not found: " + input));
            var extension = FormatRegistry.GetExtension(input);
            var targets = FormatRegistry.GetTargets(extension, ToolLocator.RarPath != null);
            if (!targets.Contains(target))
                return (null, new(JobErrors.UnsupportedConversion,
                    $"cannot convert .{extension} to .{target}. Valid targets: {string.Join(", ", targets)}", targets));
            if (MissingEngine(extension, target) is { } missing)
                return (null, new(JobErrors.EngineMissing, missing));
        }

        bool merge = inputs.Count > 1 && !Each && inputs.All(i =>
            MergeConversion.CanMerge(FormatRegistry.GetCategory(FormatRegistry.GetExtension(i)), target, FormatRegistry.GetExtension(i)));

        // Settings are checked against what each conversion understands. In a mixed batch a
        // setting only some of the files know (a resolution for the PDFs among them) applies to
        // those and is left out for the rest.
        var extensions = inputs.Select(FormatRegistry.GetExtension).Distinct().ToList();
        var knownSomewhere = extensions.SelectMany(e => SettableSettings(e, target, merge)).Select(d => d.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<string, DetailSettings>();
        foreach (var extension in extensions)
        {
            if (ReadSettings(extension, target, merge, knownSomewhere, out var detail) is { } problem)
                return (null, new(JobErrors.InvalidSetting, problem));
            details[extension] = detail;
        }
        return (new Checked(this, inputs, target, merge, details), null);
    }

    /// <summary>An engine this conversion cannot do without and that is not there; null when all is well.</summary>
    private static string? MissingEngine(string sourceExt, string target)
    {
        if (FormatRegistry.GetEngine(sourceExt, target) == FileCategory.Video && (ToolLocator.FfmpegPath == null || ToolLocator.FfprobePath == null))
            return $".{sourceExt} → .{target} needs FFmpeg, which is not installed. --setup-tools (MCP: setup_engines) downloads it.";
        if (target == "7z" && ToolLocator.SevenZipPath == null)
            return $".{sourceExt} → .{target} needs 7-Zip, which is not installed. --setup-tools (MCP: setup_engines) downloads it.";
        return null;
    }

    /// <summary>
    /// Turns "key=value" pairs into the settings of one conversion. Returns what is wrong with
    /// them, or null: an unknown key or a value outside its range is a mistake to report, not
    /// something to drop quietly.
    /// </summary>
    private string? ReadSettings(string sourceExt, string targetExt, bool merging, HashSet<string> knownSomewhere, out DetailSettings detail)
    {
        detail = DetailSettings.None;
        if (Settings.Count == 0) return null;

        var definitions = SettableSettings(sourceExt, targetExt, merging);
        var values = new Dictionary<string, string>();
        foreach (var pair in Settings)
        {
            int split = pair.IndexOf('=');
            if (split <= 0) return $"--set expects key=value, not \"{pair}\".";
            var key = pair[..split].Trim();
            var definition = definitions.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (definition == null)
            {
                if (knownSomewhere.Contains(key)) continue; // meant for another file type in this batch
                return $".{sourceExt} → .{targetExt} has no setting \"{key}\". It has: " +
                       (definitions.Count == 0 ? "none" : string.Join(", ", definitions.Select(d => d.Key))) +
                       $". See --list-settings {sourceExt} {targetExt}";
            }
            var value = definition.Normalize(pair[(split + 1)..]);
            if (value == null) return $"\"{pair[(split + 1)..]}\" is not a valid value for {definition.Key} ({Describe(definition)}).";
            values[definition.Key] = value;
        }
        detail = new DetailSettings(values);
        return null;
    }
}

/// <summary>Plans and runs jobs that have been checked. One runner shares one set of work queues.</summary>
public sealed class JobRunner
{
    private readonly ConversionService _service = new();

    /// <summary>What running the job would write, without writing it.</summary>
    public static IReadOnlyList<JobFile> Plan(ConversionJob.Checked job)
    {
        var settings = job.Job;
        var when = settings.WhenExists;
        JobFile Planned(IReadOnlyList<string> inputs, string output, bool mayBecomeFolder) =>
            new(inputs, output, when == ExistingFileAction.Skip && File.Exists(output) ? JobStatus.Skipped : JobStatus.Planned)
            {
                Replaces = when == ExistingFileAction.Overwrite && File.Exists(output),
                MayBecomeFolder = mayBecomeFolder
            };

        if (job.Merge)
        {
            var sorted = NaturalSort.SortPathsByFileName(job.Inputs);
            var dir = settings.OutputDirectory != null ? Path.GetFullPath(settings.OutputDirectory) : Path.GetDirectoryName(sorted[0])!;
            return [Planned(sorted, ConversionService.BuildNamedOutputPath(dir, MergeConversion.SuggestBaseName(sorted), job.Target, when), false)];
        }

        var reserved = new HashSet<string>(PlatformServices.PathComparer);
        return job.Inputs.Select(input => Planned([input],
            ConversionService.BuildOutputPath(input, job.Target, FullPathOrNull(settings.OutputDirectory), reserved, when),
            MayBecomeFolder(FormatRegistry.GetExtension(input), job.Target))).ToList();
    }

    private static string? FullPathOrNull(string? path) => path == null ? null : Path.GetFullPath(path);

    /// <summary>Conversions whose result can be a folder of files rather than one file.</summary>
    private static bool MayBecomeFolder(string source, string target)
    {
        var category = FormatRegistry.GetCategory(source);
        bool toPictures = target is "png" or "jpg" or "tiff" or "webp" or "bmp";
        return category switch
        {
            FileCategory.Video => toPictures,
            FileCategory.Document => toPictures || target is "md" or "csv" or "svg",
            FileCategory.Image => toPictures && source is "gif" or "webp" or "tif" or "tiff",
            _ => false
        };
    }

    /// <summary>
    /// Carries the job out and says what became of each result, also when some of them failed.
    /// Every input starts at once; the service's queues decide how many run side by side.
    /// </summary>
    /// <param name="onFile">Told of each result as it is finished.</param>
    /// <param name="progress">How far along a job of one result is, from 0 to 1.</param>
    public async Task<IReadOnlyList<JobFile>> RunAsync(ConversionJob.Checked job, Action<JobFile>? onFile,
        IProgress<double>? progress, CancellationToken ct)
    {
        var settings = job.Job;
        var outDir = FullPathOrNull(settings.OutputDirectory);
        if (outDir != null) Directory.CreateDirectory(outDir);
        var options = new ConversionOptions { SizePercent = settings.SizePercent, Speed = settings.Speed, UseGpu = settings.UseGpu };

        JobFile Tell(JobFile file)
        {
            onFile?.Invoke(file);
            return file;
        }

        if (job.Merge)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var merged = options.WithDetail(job.Details[FormatRegistry.GetExtension(job.Inputs[0])]);
            var inOrder = NaturalSort.SortPathsByFileName(job.Inputs); // the order they go in, which the result says
            try
            {
                var produced = await _service.ConvertManyAsync(inOrder, job.Target, merged, outDir, progress, ct, settings.WhenExists);
                return [Tell(new JobFile(inOrder, produced ?? "", produced != null ? JobStatus.Converted : JobStatus.Skipped) { Seconds = watch.Elapsed.TotalSeconds })];
            }
            catch (Exception ex)
            {
                return [Tell(Failure(inOrder, "", ex, watch))];
            }
        }

        var reserved = new HashSet<string>(PlatformServices.PathComparer);
        var outputs = job.Inputs.Select(input => ConversionService.BuildOutputPath(input, job.Target, outDir, reserved, settings.WhenExists)).ToList();
        var oneProgress = job.Inputs.Count == 1 ? progress : null; // interleaved percentages would be noise

        return await Task.WhenAll(job.Inputs.Select(async (input, index) =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (settings.WhenExists == ExistingFileAction.Skip && File.Exists(outputs[index]))
                    return Tell(new JobFile([input], outputs[index], JobStatus.Skipped) { Note = "already exists" });
                var result = await _service.ConvertAsync(input, outputs[index],
                    options.WithDetail(job.Details[FormatRegistry.GetExtension(input)]), oneProgress, ct);
                return Tell(new JobFile([input], result.OutputPath, JobStatus.Converted) { Note = result.Note, Seconds = watch.Elapsed.TotalSeconds });
            }
            catch (Exception ex)
            {
                return Tell(Failure([input], outputs[index], ex, watch));
            }
        }));
    }

    private static JobFile Failure(IReadOnlyList<string> inputs, string output, Exception ex, System.Diagnostics.Stopwatch watch)
    {
        bool cancelled = ex is OperationCanceledException;
        return new JobFile(inputs, output, cancelled ? JobStatus.Cancelled : JobStatus.Failed)
        {
            Error = cancelled ? "Cancelled." : ex is FriendlyException ? ex.Message : ex.ToString(),
            ErrorCode = cancelled ? JobErrors.Cancelled : JobErrors.ConversionFailed,
            Seconds = watch.Elapsed.TotalSeconds
        };
    }
}
