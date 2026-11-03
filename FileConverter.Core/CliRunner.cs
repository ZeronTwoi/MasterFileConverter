using System.Diagnostics;
using System.Text.Json.Nodes;
using FileConverter.Core.Automation;
using FileConverter.Core.Diagnostics;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;

namespace FileConverter.Core;

/// <summary>
/// Headless command-line mode:
///   --cli &lt;input&gt; [&lt;input2&gt; …] &lt;target-ext&gt; [--out &lt;dir&gt;] [--result …] [--size &lt;10-100&gt;] [--speed …] [--set key=value …] [--each] [--no-gpu] [--json] [--dry-run]
///   --list-formats [&lt;source-ext&gt;]   what can be read and what it can become
///   --list-settings &lt;source-ext&gt; &lt;target-ext&gt;   the precise settings --set accepts for a conversion
///   --status        the version and which engines are there
///   --mcp           the app as a tool for AI agents (see <see cref="McpServer"/>)
///   --agent-setup   what an MCP client needs in order to start it
///   --setup-tools   downloads FFmpeg and 7-Zip
///   --self-test     converts a generated sample of every format to every target and checks the results
///   --update        looks for a newer version of the app and installs it (--from &lt;file&gt;: from a download already here)
///   --uninstall     takes an installed copy off the computer again (Linux; on Windows that is "Installed apps")
/// Several inputs with a combinable target (images → pdf/gif/tiff, anything → an
/// archive) merge into ONE output unless --each is given; otherwise every input is
/// converted on its own, in parallel.
/// With --json, stdout holds one JSON document and nothing else (see <see cref="AgentJson"/>).
/// Exit codes: 0 success, 1 conversion error, 2 bad arguments, 3 an engine it needs is not installed.
/// </summary>
public static class CliRunner
{
    private static readonly string[] Commands =
        ["--cli", "--setup-tools", "--self-test", "--list-formats", "--list-settings", "--status", "--mcp", "--agent-setup", "--update", "--uninstall", "--help", "-h", "--version",
            Converters.PdfPageExport.WorkerCommand];

    /// <summary>Do these arguments ask for the command line rather than the window?</summary>
    public static bool Handles(string[] args) => args.Any(a => Commands.Contains(a));

    public static string Version => AppInfo.Version;

    private const string Usage = AppInfo.DisplayName + """
         — command line

          --cli <input> [<input2> …] <target-ext> [options]   convert files
              --out <dir>         write results to this folder (default: next to each source)
              --result smallest|small|good|full|best   how far files are taken, as the window's Result slider (default: full)
              --size <10-100>     an exact target size as a percentage of the source, instead of the result's
              --speed fastest|fast|balanced|best     an encoder speed, instead of the result's
              --set <key>=<value> a precise setting, e.g. --set quality=85 --set maxWidth=1920 (repeatable)
              --when-exists keep|replace|skip   what to do when the result's name is taken (default: keep both)
              --each              convert every input separately instead of combining them
              --no-gpu            keep video encoding on the CPU
              --dry-run           check everything and say what would be written, without writing
              --json              stdout is one JSON document: every result's path, size and status
          --list-formats [<source-ext>] [--json]      what can be read and what it can become
          --list-settings <source-ext> <target-ext> [--json]   show the settings --set accepts for a conversion
          --status [--json]       the version, and which engines this computer has
          --mcp                   run as a tool server for AI agents (Model Context Protocol over stdin/stdout)
          --agent-setup           print what an MCP client needs to start this copy of the app
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

        Exit codes: 0 success, 1 a conversion failed, 2 bad arguments, 3 an engine it needs is not installed.
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
        catch (Exception ex)
        {
            var message = ex is FriendlyException ? ex.Message : ex.ToString();
            if (args.Contains("--json")) PrintJson(AgentJson.Failure(new JobProblem(JobErrors.ConversionFailed, message)));
            else Console.Error.WriteLine("ERROR: " + message);
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
        if (args.Contains("--mcp"))
        {
            // stdout is the protocol's: whatever else wants to print goes to stderr
            var protocol = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            Console.SetOut(Console.Error);
            using var requests = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            await new McpServer(requests, protocol).RunAsync();
            return 0;
        }
        if (args.Contains("--agent-setup"))
        {
            Console.WriteLine(AgentSetup.McpClientConfig());
            return 0;
        }
        if (args.Contains("--status"))
            return Status(args);
        if (args.Contains("--list-formats"))
            return ListFormats(args);
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

        return await ConvertAsync(args);
    }

    private static async Task<int> ConvertAsync(string[] args)
    {
        bool json = args.Contains("--json");
        bool dryRun = args.Contains("--dry-run");

        int Refuse(JobProblem problem)
        {
            if (json) PrintJson(AgentJson.Failure(problem));
            else Console.Error.WriteLine("ERROR: " + problem.Message);
            return problem.Code == JobErrors.EngineMissing ? 3 : 2;
        }
        int BadValue(string option, string expected) =>
            Refuse(new JobProblem(JobErrors.BadArguments, $"{option} expects {expected}."));

        var positional = new List<string>();
        var sets = new List<string>();
        string? outDir = null;
        ResultLevel? result = null;
        int? size = null;
        SpeedPreset? speed = null;
        var whenExists = ExistingFileAction.KeepBoth;
        bool each = false;
        bool gpu = true;

        for (int i = 0; i < args.Length; i++)
        {
            // an option that takes a value and has none is an answer the caller needs, not a file name
            string? Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            switch (args[i])
            {
                case "--cli" or "--json" or "--dry-run": break;
                case "--each": each = true; break;
                case "--no-gpu": gpu = false; break;
                case "--out":
                    outDir = Value();
                    if (outDir == null) return BadValue("--out", "a folder");
                    break;
                case "--set":
                    if (Value() is not { } pair) return BadValue("--set", "key=value");
                    sets.Add(pair);
                    break;
                case "--result":
                    result = Value() is { } name ? ResultLevels.Find(name) : null;
                    if (result == null) return BadValue("--result", string.Join(", ", ResultLevels.All.Select(l => l.Name)));
                    break;
                case "--size":
                    if (!int.TryParse(Value(), out var parsed)) return BadValue("--size", "a number from 10 to 100");
                    size = Math.Clamp(parsed, 10, 100);
                    break;
                case "--speed":
                    speed = Value() is { } speedName ? McpServer.ParseSpeed(speedName) : null;
                    if (speed == null) return BadValue("--speed", "fastest, fast, balanced or best");
                    break;
                case "--when-exists":
                    if ((Value() is { } whenName ? McpServer.ParseWhenExists(whenName) : null) is not { } action)
                        return BadValue("--when-exists", "keep, replace or skip");
                    whenExists = action;
                    break;
                default:
                    if (args[i].StartsWith("--"))
                        return Refuse(new JobProblem(JobErrors.BadArguments, $"unknown option {args[i]}. See --help."));
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count < 2)
        {
            if (json) return Refuse(new JobProblem(JobErrors.BadArguments, "expected: <input> [<input2> …] <target-ext>. See --help."));
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var level = result ?? ResultLevels.All[ResultLevels.Default];
        var (job, problem) = new ConversionJob
        {
            Inputs = positional[..^1],
            Target = positional[^1],
            OutputDirectory = outDir,
            SizePercent = size ?? level.SizePercent,
            Speed = speed ?? level.Speed,
            UseGpu = gpu,
            WhenExists = whenExists,
            Each = each,
            Settings = sets
        }.Check();
        if (problem != null) return Refuse(problem);

        if (dryRun)
        {
            var plan = JobRunner.Plan(job!);
            if (json) PrintJson(AgentJson.Outcome(job!, plan, dryRun: true));
            else
                foreach (var file in plan)
                    Console.WriteLine($"{(file.Status == JobStatus.Skipped ? "WOULD SKIP" : file.Replaces ? "WOULD REPLACE" : "WOULD WRITE")} {file.Output}" +
                                      (file.MayBecomeFolder ? "  (or a folder of that name: several files)" : ""));
            return 0;
        }

        if (job!.Merge && !json)
            Console.WriteLine($"Combining {job.Inputs.Count} files into one .{job.Target} (use --each for separate outputs) …");

        var files = await new JobRunner().RunAsync(job, json ? null : PrintFile, PercentPrinter(json ? Console.Error : Console.Out), CancellationToken.None);
        if (json) PrintJson(AgentJson.Outcome(job, files, dryRun: false));
        return files.Any(f => f.Status is JobStatus.Failed or JobStatus.Cancelled) ? 1 : 0;
    }

    /// <summary>A result as a person reads it.</summary>
    private static void PrintFile(JobFile file)
    {
        switch (file.Status)
        {
            case JobStatus.Converted:
                Console.WriteLine("OK " + file.Output + (file.Note != null ? $"  [{file.Note}]" : ""));
                break;
            case JobStatus.Skipped:
                Console.WriteLine(file.Output.Length > 0 ? "SKIPPED " + file.Output + " already exists" : "SKIPPED the combined file already exists");
                break;
            default:
                Console.Error.WriteLine($"ERROR: {(file.Inputs.Count == 1 ? Path.GetFileName(file.Inputs[0]) : file.Output)}: {file.Error}");
                break;
        }
    }

    private static void PrintJson(JsonObject document) => Console.WriteLine(document.ToJsonString(AgentJson.Indented));

    private static int Status(string[] args)
    {
        var status = AgentJson.Status();
        if (args.Contains("--json"))
        {
            PrintJson(status);
            return 0;
        }
        Console.WriteLine($"{AppInfo.DisplayName} {Version} ({PlatformServices.Current.Name})");
        foreach (var (name, engine) in status["engines"]!.AsObject())
            Console.WriteLine($"  {name,-16} {engine!["path"]?.GetValue<string>() ?? "not found"}");
        return 0;
    }

    private static int ListFormats(string[] args)
    {
        var names = args.Where(a => !a.StartsWith("--")).Select(a => a.TrimStart('.').ToLowerInvariant()).ToList();
        bool json = args.Contains("--json");
        if (names.Count > 1 || (names.Count == 1 && !FormatRegistry.Inputs.Contains(names[0])))
        {
            var problem = new JobProblem(JobErrors.UnsupportedConversion, names.Count > 1
                ? "Usage: --list-formats [<source-ext>], e.g. --list-formats mov"
                : $".{names[0]} is not a format this app reads.");
            if (json) PrintJson(AgentJson.Failure(problem));
            else Console.Error.WriteLine("ERROR: " + problem.Message);
            return 2;
        }

        var formats = AgentJson.Formats(names.Count == 1 ? names[0] : null);
        if (json)
        {
            PrintJson(formats);
            return 0;
        }
        foreach (var format in formats["formats"]!.AsArray())
            Console.WriteLine($"{format!["extension"],-10} {format["category"],-10} {string.Join(", ", format["targets"]!.AsArray().Select(t => t!.GetValue<string>()))}");
        return 0;
    }

    private static int ListSettings(string[] args)
    {
        var names = args.Where(a => !a.StartsWith("--")).Select(a => a.TrimStart('.').ToLowerInvariant()).ToList();
        bool json = args.Contains("--json");
        int Refuse(JobProblem problem)
        {
            if (json) PrintJson(AgentJson.Failure(problem));
            else Console.Error.WriteLine((problem.Code == JobErrors.BadArguments ? "" : "ERROR: ") + problem.Message);
            return 2;
        }
        if (names.Count != 2)
            return Refuse(new JobProblem(JobErrors.BadArguments, "Usage: --list-settings <source-ext> <target-ext>, e.g. --list-settings mov mp4"));
        var targets = FormatRegistry.GetTargets(names[0], rarOutputAvailable: true);
        if (!targets.Contains(names[1]))
            return Refuse(new JobProblem(JobErrors.UnsupportedConversion,
                $"cannot convert .{names[0]} to .{names[1]}. Valid targets: {string.Join(", ", targets)}", targets));
        if (json)
        {
            PrintJson(AgentJson.SettingsOf(names[0], names[1]));
            return 0;
        }

        var definitions = ConversionJob.SettableSettings(names[0], names[1]);
        Console.WriteLine($"Settings for .{names[0]} → .{names[1]} (use with --set key=value; anything not set is automatic):");
        if (definitions.Count == 0) Console.WriteLine("  none — this conversion has only one way of being done.");
        foreach (var definition in definitions)
        {
            // the size and quality settings have no default of their own: without one, --result decides
            var fallback = definition.Mode != null && definition.InPanel ? "not set" : definition.Automatic;
            Console.WriteLine($"  {definition.Key,-14} {definition.Label} — {ConversionJob.Describe(definition)}. Default: {fallback}");
            Console.WriteLine($"  {"",-14} {AgentJson.ForPrograms(definition.Help)}");
        }

        // in the window only one of these can be chosen; here the converters' order decides
        var competing = SizeModes.Precedence
            .SelectMany(mode => definitions.Where(d => d.Mode == mode && d.Key != SettingKeys.Lossless))
            .Select(d => d.Key).ToList();
        if (competing.Count > 1)
            Console.WriteLine($"Of {string.Join(", ", competing)} only one is used: the first one set, in that order.");
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
    private static IProgress<double> PercentPrinter(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        int lastPercent = -1;
        return new Progress<double>(p =>
        {
            int percent = (int)(p * 100);
            if (percent >= lastPercent + 20) { lastPercent = percent; writer.WriteLine($"progress {percent}%"); }
        });
    }
}
