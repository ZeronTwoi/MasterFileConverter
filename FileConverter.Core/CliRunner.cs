using System.Diagnostics;
using FileConverter.Core.Diagnostics;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;

namespace FileConverter.Core;

/// <summary>
/// Headless command-line mode:
///   --cli &lt;input&gt; [&lt;input2&gt; …] &lt;target-ext&gt; [--out &lt;dir&gt;] [--size &lt;10-100&gt;] [--speed …] [--set key=value …] [--each] [--no-gpu]
///   --list-settings &lt;source-ext&gt; &lt;target-ext&gt;   the precise settings --set accepts for a conversion
///   --setup-tools   downloads FFmpeg and 7-Zip
///   --self-test     converts a generated sample of every format to every target and checks the results
///   --update        looks for a newer version of the app and installs it (--from &lt;file&gt;: from a download already here)
///   --uninstall     takes an installed copy off the computer again (Linux; on Windows that is "Installed apps")
/// Several inputs with a combinable target (images → pdf/gif/tiff, anything → an
/// archive) merge into ONE output unless --each is given; otherwise every input is
/// converted on its own, in parallel.
/// Exit codes: 0 success, 1 conversion error, 2 bad arguments.
/// </summary>
public static class CliRunner
{
    private static readonly string[] Commands =
        ["--cli", "--setup-tools", "--self-test", "--list-settings", "--update", "--uninstall", "--help", "-h", "--version",
            Converters.PdfPageExport.WorkerCommand];

    /// <summary>Do these arguments ask for the command line rather than the window?</summary>
    public static bool Handles(string[] args) => args.Any(a => Commands.Contains(a));

    public static string Version => AppInfo.Version;

    private const string Usage = AppInfo.DisplayName + """
         — command line

          --cli <input> [<input2> …] <target-ext> [options]   convert files
              --out <dir>         write results to this folder (default: next to each source)
              --size <10-100>     target size as a percentage of the source (100 = best quality)
              --speed fastest|fast|balanced|best
              --set <key>=<value> a precise setting, e.g. --set quality=85 --set maxWidth=1920 (repeatable)
              --when-exists keep|replace|skip   what to do when the result's name is taken (default: keep both)
              --each              convert every input separately instead of combining them
              --no-gpu            keep video encoding on the CPU
          --list-settings <source-ext> <target-ext>   show the settings --set accepts for a conversion
          --setup-tools           download FFmpeg and 7-Zip
          --self-test             try every conversion on generated samples and report what works
              --filter <text>     only the cases whose name contains the text, e.g. "mp4" or "→ pdf"
              --keep              leave the samples and results in the temp folder afterwards
              --engine builtin|libreoffice|office   convert documents with this engine only, not the best installed
          --update               look for a newer version of the app and install it
              --from <file>       install it from a download that is already here, without going online
          --uninstall             remove the installed app, the engines it downloaded and its settings (Linux)
              --keep-settings     leave the settings for a later installation
          --version
        """;

    public static int Run(string[] args)
    {
        PlatformServices.Current.AttachToParentConsole(); // a windowed program has no console of its own
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* output is redirected somewhere that decides for itself */ }
        // what an update left in the data folder, for those who never open the window (it does the same)
        AppUpdater.RemoveLeftovers();
        try
        {
            return RunCoreAsync(args).GetAwaiter().GetResult();
        }
        catch (FriendlyException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        // started by the app itself, to draw its share of a PDF's pages (never typed by a person)
        if (args.Contains(Converters.PdfPageExport.WorkerCommand))
        {
            using var request = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            return await Converters.PdfPageExport.RunWorker(request, Console.Out);
        }
        if (args.Contains("--version"))
        {
            Console.WriteLine($"{AppInfo.DisplayName} {Version} ({PlatformServices.Current.Name})");
            return 0;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Contains("--setup-tools"))
        {
            Console.WriteLine("Downloading 7-Zip …");
            await ToolLocator.DownloadSevenZipAsync(CancellationToken.None);
            Console.WriteLine($"Downloading {PlatformServices.Current.FfmpegDownloadSummary} …");
            await ToolLocator.DownloadFfmpegAsync(null, CancellationToken.None);
            Console.WriteLine("Tools installed to " + ToolLocator.ToolsDir);
            return 0;
        }
        if (args.Contains("--self-test"))
            return await RunSelfTestAsync(args);
        if (args.Contains("--update"))
            return await UpdateAsync(args);
        if (args.Contains("--uninstall"))
            return Uninstall(keepSettings: args.Contains("--keep-settings"));
        if (args.Contains("--list-settings"))
            return ListSettings(args);

        var positional = new List<string>();
        var sets = new List<string>();
        string? outDir = null;
        int size = 100;
        var speed = SpeedPreset.Balanced;
        var whenExists = ExistingFileAction.KeepBoth;
        bool each = false;
        bool gpu = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cli": break;
                case "--each": each = true; break;
                case "--no-gpu": gpu = false; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--set" when i + 1 < args.Length: sets.Add(args[++i]); break;
                case "--size" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
                    size = Math.Clamp(parsed, 10, 100);
                    i++;
                    break;
                case "--speed" when i + 1 < args.Length:
                    speed = args[++i].ToLowerInvariant() switch
                    {
                        "fastest" => SpeedPreset.Fastest,
                        "fast" => SpeedPreset.Fast,
                        "balanced" => SpeedPreset.Balanced,
                        "best" => SpeedPreset.BestQuality,
                        _ => SpeedPreset.Balanced
                    };
                    break;
                case "--when-exists" when i + 1 < args.Length:
                    whenExists = args[++i].ToLowerInvariant() switch
                    {
                        "replace" or "overwrite" => ExistingFileAction.Overwrite,
                        "skip" => ExistingFileAction.Skip,
                        _ => ExistingFileAction.KeepBoth
                    };
                    break;
                default: positional.Add(args[i]); break;
            }
        }

        if (positional.Count < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var targetExt = positional[^1].TrimStart('.').ToLowerInvariant();
        var inputs = positional[..^1].Select(Path.GetFullPath).ToList();

        foreach (var input in inputs)
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine("ERROR: input file not found: " + input);
                return 2;
            }
            var targets = FormatRegistry.GetTargets(FormatRegistry.GetExtension(input), ToolLocator.RarPath != null);
            if (!targets.Contains(targetExt))
            {
                Console.Error.WriteLine($"ERROR: cannot convert .{FormatRegistry.GetExtension(input)} to .{targetExt}. Valid targets: {string.Join(", ", targets)}");
                return 2;
            }
        }

        bool mergeAll = inputs.Count > 1 && !each && inputs.All(i =>
            Converters.MergeConversion.CanMerge(FormatRegistry.GetCategory(FormatRegistry.GetExtension(i)), targetExt));

        // --set values are checked against what each conversion understands before anything runs.
        // In a mixed batch a setting only some of the files know (a resolution for the PDFs among
        // them) applies to those and is left out for the rest.
        var extensions = inputs.Select(FormatRegistry.GetExtension).Distinct().ToList();
        var knownSomewhere = extensions.SelectMany(e => SettingCatalog.For(e, targetExt, mergeAll)).Select(d => d.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<string, DetailSettings>();
        foreach (var extension in extensions)
        {
            if (ReadSettings(sets, extension, targetExt, mergeAll, knownSomewhere, out var detail) is { } problem)
            {
                Console.Error.WriteLine("ERROR: " + problem);
                return 2;
            }
            details[extension] = detail;
        }

        if (outDir != null) Directory.CreateDirectory(outDir);
        var options = new ConversionOptions { SizePercent = size, Speed = speed, UseGpu = gpu };
        var service = new ConversionService();

        if (mergeAll)
        {
            Console.WriteLine($"Combining {inputs.Count} files into one .{targetExt} (use --each for separate outputs) …");
            var merged = options.WithDetail(details[FormatRegistry.GetExtension(inputs[0])]);
            var produced = await service.ConvertManyAsync(inputs, targetExt, merged, outDir, PercentPrinter(), CancellationToken.None, whenExists);
            Console.WriteLine(produced != null ? "OK " + produced : "SKIPPED the combined file already exists");
            return 0;
        }

        // Every input starts at once; the service's queues decide how many actually run side by side.
        var reserved = new HashSet<string>(PlatformServices.PathComparer);
        var outputs = inputs.Select(input => ConversionService.BuildOutputPath(input, targetExt, outDir, reserved, whenExists)).ToList();
        var progress = inputs.Count == 1 ? PercentPrinter() : null; // interleaved percentages would be noise
        int failures = 0;

        await Task.WhenAll(inputs.Select(async (input, index) =>
        {
            try
            {
                if (whenExists == ExistingFileAction.Skip && File.Exists(outputs[index]))
                {
                    Console.WriteLine("SKIPPED " + outputs[index] + " already exists");
                    return;
                }
                var result = await service.ConvertAsync(input, outputs[index],
                    options.WithDetail(details[FormatRegistry.GetExtension(input)]), progress, CancellationToken.None);
                Console.WriteLine("OK " + result.OutputPath + (result.Note != null ? $"  [{result.Note}]" : ""));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                var message = ex is FriendlyException ? ex.Message : ex.ToString();
                Console.Error.WriteLine($"ERROR: {Path.GetFileName(input)}: {message}");
            }
        }));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Turns "--set key=value" pairs into the settings of one conversion. Returns what is wrong
    /// with them, or null: an unknown key or a value outside its range is a mistake to report,
    /// not something to drop quietly.
    /// </summary>
    private static string? ReadSettings(List<string> sets, string sourceExt, string targetExt, bool merging,
        HashSet<string> knownSomewhere, out DetailSettings detail)
    {
        detail = DetailSettings.None;
        if (sets.Count == 0) return null;

        var definitions = SettingCatalog.For(sourceExt, targetExt, merging);
        var values = new Dictionary<string, string>();
        foreach (var pair in sets)
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

    private static string Describe(SettingDefinition definition) => definition.Kind switch
    {
        SettingKind.Number => $"a number from {definition.Min:0.##} to {definition.Max:0.##}",
        SettingKind.Choice => "one of: " + string.Join(", ", definition.Choices.Select(c => c.Value)),
        SettingKind.Secret => "text",
        _ => "text"
    };

    private static int ListSettings(string[] args)
    {
        var names = args.Where(a => !a.StartsWith("--")).Select(a => a.TrimStart('.').ToLowerInvariant()).ToList();
        if (names.Count != 2)
        {
            Console.Error.WriteLine("Usage: --list-settings <source-ext> <target-ext>, e.g. --list-settings mov mp4");
            return 2;
        }
        var targets = FormatRegistry.GetTargets(names[0], rarOutputAvailable: true);
        if (!targets.Contains(names[1]))
        {
            Console.Error.WriteLine($"ERROR: cannot convert .{names[0]} to .{names[1]}. Valid targets: {string.Join(", ", targets)}");
            return 2;
        }

        var definitions = SettingCatalog.For(names[0], names[1]);
        Console.WriteLine($"Settings for .{names[0]} → .{names[1]} (use with --set key=value; anything not set is automatic):");
        if (definitions.Count == 0) Console.WriteLine("  none — this conversion has only one way of being done.");
        foreach (var definition in definitions)
        {
            Console.WriteLine($"  {definition.Key,-14} {definition.Label} — {Describe(definition)}. Default: {definition.Automatic}");
            Console.WriteLine($"  {"",-14} {definition.Help}");
        }
        return 0;
    }

    private static async Task<int> RunSelfTestAsync(string[] args)
    {
        string? filter = null;
        int filterIndex = Array.IndexOf(args, "--filter");
        if (filterIndex >= 0 && filterIndex + 1 < args.Length) filter = args[filterIndex + 1];
        bool keep = args.Contains("--keep");
        string? engine = null;
        int engineIndex = Array.IndexOf(args, "--engine");
        if (engineIndex >= 0)
        {
            engine = engineIndex + 1 < args.Length ? args[engineIndex + 1].ToLowerInvariant() : "";
            if (engine is not ("builtin" or "libreoffice" or "office"))
            {
                Console.Error.WriteLine("--engine expects builtin, libreoffice or office.");
                return 2;
            }
        }

        Console.WriteLine($"{AppInfo.DisplayName} {Version} self-test on {PlatformServices.Current.Name}");
        if (engine != null) Console.WriteLine($"  Documents are converted with: {engine}");
        Console.WriteLine("  FFmpeg       " + (ToolLocator.FfmpegPath ?? "not found"));
        if (ToolLocator.FfmpegPath == null && ToolLocator.OutdatedFfmpeg is { } outdated)
            Console.WriteLine($"               passed over {outdated}: FFmpeg {ToolLocator.MinimumFfmpegMajor} or newer is needed — --setup-tools downloads it");
        Console.WriteLine("  7-Zip        " + (ToolLocator.SevenZipPath ?? "not found"));
        Console.WriteLine("  RAR encoder  " + (ToolLocator.RarPath ?? "not found"));
        Console.WriteLine("  LibreOffice  " + (ToolLocator.LibreOfficePath ?? "not found"));
        Console.WriteLine("  Office       " + (ToolLocator.OfficeAutomationAvailable ? PlatformServices.Current.OfficeAutomation!.Name : "not found"));
        if (FfmpegCapabilities.MissingEssentials is { Count: > 0 } missing)
            Console.WriteLine("  This FFmpeg build cannot do: " + string.Join("; ", missing));
        Console.WriteLine();

        // a fresh folder every time: office programs remember files they were interrupted on
        var workDir = WorkFiles.NewPath("selftest");
        var watch = Stopwatch.StartNew();
        var results = await SelfTest.RunAsync(workDir, filter, result =>
        {
            // failures and skips as they happen; passes are summarised at the end
            if (result.Outcome != SelfTestOutcome.Passed) Console.WriteLine(Line(result));
        }, CancellationToken.None, engine);

        Console.WriteLine();
        foreach (var result in results.Where(r => r.Outcome == SelfTestOutcome.Passed)) Console.WriteLine(Line(result));

        int passed = results.Count(r => r.Outcome == SelfTestOutcome.Passed);
        int failed = results.Count(r => r.Outcome == SelfTestOutcome.Failed);
        int skipped = results.Count(r => r.Outcome == SelfTestOutcome.Skipped);
        Console.WriteLine();
        Console.WriteLine($"Self-test: {passed} passed, {failed} failed, {skipped} skipped in {watch.Elapsed.TotalSeconds:0} s");
        if (failed > 0)
        {
            Console.WriteLine("Failed:");
            foreach (var result in results.Where(r => r.Outcome == SelfTestOutcome.Failed)) Console.WriteLine(Line(result));
        }

        if (keep) Console.WriteLine("Files kept in " + workDir);
        else try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        return failed == 0 ? 0 : 1;
    }

    private static string Line(SelfTestResult result)
    {
        var tag = result.Outcome switch
        {
            SelfTestOutcome.Passed => "PASS",
            SelfTestOutcome.Failed => "FAIL",
            _ => "SKIP"
        };
        var time = result.Outcome == SelfTestOutcome.Skipped ? "" : $"  ({result.Elapsed.TotalSeconds:0.0} s)";
        return $"{tag}  {result.Name,-34} {result.Detail}{time}";
    }

    /// <summary>
    /// "--update": looks for a newer version and installs it. It is the one thing that makes the
    /// command line look — asked for by name, never by itself.
    /// </summary>
    private static async Task<int> UpdateAsync(string[] args)
    {
        if (!AppUpdater.CanUpdate)
        {
            Console.Error.WriteLine("This copy of the app cannot update itself. The newest version is at " + UpdateCheck.DownloadPage);
            return 1;
        }

        PreparedUpdate prepared;
        int from = Array.IndexOf(args, "--from");
        if (from >= 0)
        {
            // a download fetched by hand, for a machine that is not online
            if (from + 1 >= args.Length)
            {
                Console.Error.WriteLine("--from needs the file: the download for this system, as it was published.");
                return 2;
            }
            prepared = await AppUpdater.PrepareFromFileAsync(args[from + 1], CancellationToken.None);
            Console.WriteLine($"{AppInfo.DisplayName} {Version} — updating to version {prepared.Version} from {Path.GetFileName(args[from + 1])} …");
        }
        else
        {
            Console.WriteLine($"{AppInfo.DisplayName} {Version} — looking for a newer version …");
            var update = await AppUpdater.FindAsync(CancellationToken.None);
            if (update == null)
            {
                Console.WriteLine("This is the newest version.");
                return 0;
            }

            Console.WriteLine($"Downloading version {update.Version} ({update.File.Name}) …");
            prepared = await AppUpdater.PrepareAsync(update, PercentPrinter(), CancellationToken.None);
        }

        try
        {
            AppUpdater.Start(prepared, restart: false, unattended: true);
        }
        catch
        {
            AppUpdater.Discard();
            throw;
        }
        Console.WriteLine((from >= 0 ? $"Version {prepared.Version} is" : $"Version {prepared.Version} is downloaded and checked. It is") +
                          " put in place as soon as this command has ended — give it a few seconds.");
        return 0;
    }

    /// <summary>"--uninstall", where the app takes itself off the computer (Linux).</summary>
    private static int Uninstall(bool keepSettings)
    {
        var platform = PlatformServices.Current;
        if (platform.Uninstall(keepSettings)) return 0;

        Console.Error.WriteLine(platform.Name == "Windows"
            ? "On Windows, uninstall the app under Settings → Apps → Installed apps. A copy unpacked from the .zip is removed by deleting its folder."
            : "This copy was not installed with install.sh, so there is nothing to uninstall: delete its folder.");
        Console.Error.WriteLine($"Settings are kept in {platform.ConfigDirectory}" +
            (platform.DataDirectory == platform.ConfigDirectory ? ", with the downloaded engines." : $", downloaded engines in {platform.DataDirectory}."));
        return 1;
    }

    /// <summary>Prints "progress N%" in 20 % steps.</summary>
    private static IProgress<double> PercentPrinter()
    {
        int lastPercent = -1;
        return new Progress<double>(p =>
        {
            int percent = (int)(p * 100);
            if (percent >= lastPercent + 20) { lastPercent = percent; Console.WriteLine($"progress {percent}%"); }
        });
    }
}
