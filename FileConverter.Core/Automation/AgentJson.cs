using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;

namespace FileConverter.Core.Automation;

/// <summary>
/// What the app says about itself and its work to programs, as JSON — the same from the command
/// line (--json) and the MCP server. Field names are part of the agent interface: add to them,
/// do not rename them. (Built from JSON nodes, not serialized by reflection: the download is trimmed.)
/// </summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions OneLine = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    public static JsonObject ResultLevelsNode(JsonObject into)
    {
        into["resultLevels"] = new JsonArray(ResultLevels.All.Select((level, position) => (JsonNode)new JsonObject
        {
            ["name"] = level.Name,
            ["position"] = position,
            ["sizePercent"] = level.SizePercent,
            ["speed"] = SpeedName(level.Speed),
            ["description"] = level.Text
        }).ToArray());
        into["defaultResult"] = ResultLevels.All[ResultLevels.Default].Name;
        return into;
    }

    public static string SpeedName(SpeedPreset speed) => speed switch
    {
        SpeedPreset.Fastest => "fastest",
        SpeedPreset.Fast => "fast",
        SpeedPreset.Balanced => "balanced",
        _ => "best"
    };

    /// <summary>The app, its version, and which engines this machine has.</summary>
    public static JsonObject Status()
    {
        static JsonObject Engine(string? path, string usedFor) => new()
        {
            ["available"] = path != null,
            ["path"] = path,
            ["usedFor"] = usedFor
        };

        return ResultLevelsNode(new JsonObject
        {
            ["app"] = AppInfo.DisplayName,
            ["version"] = AppInfo.Version,
            ["platform"] = PlatformServices.Current.Name,
            ["engines"] = new JsonObject
            {
                ["ffmpeg"] = Engine(ToolLocator.FfmpegPath, "audio and video; --setup-tools downloads it"),
                ["sevenZip"] = Engine(ToolLocator.SevenZipPath, "creating .7z; --setup-tools downloads it"),
                ["rar"] = Engine(ToolLocator.RarPath, "creating .rar; RARLAB's own program, never downloaded"),
                ["libreOffice"] = Engine(ToolLocator.LibreOfficePath, "documents with their page layout; optional"),
                ["microsoftOffice"] = Engine(ToolLocator.OfficeAutomationAvailable ? PlatformServices.Current.OfficeAutomation!.Name : null,
                    "documents with their page layout; optional")
            }
        });
    }

    /// <summary>Every format the app reads and what each becomes; one format's when <paramref name="source"/> is given.</summary>
    public static JsonObject Formats(string? source = null)
    {
        bool rar = ToolLocator.RarPath != null;
        var sources = source != null ? [source.TrimStart('.').ToLowerInvariant()] : FormatRegistry.Inputs;
        return ResultLevelsNode(new JsonObject
        {
            ["formats"] = new JsonArray(sources.Select(ext => (JsonNode)new JsonObject
            {
                ["extension"] = ext,
                ["category"] = FormatRegistry.GetCategory(ext).ToString().ToLowerInvariant(),
                ["targets"] = Strings(FormatRegistry.GetTargets(ext, rar)),
                ["canCompressInPlace"] = FormatRegistry.CanCompressAsItIs(ext, rar)
            }).ToArray())
        });
    }

    /// <summary>The settings a conversion accepts.</summary>
    public static JsonObject SettingsOf(string source, string target)
    {
        var definitions = ConversionJob.SettableSettings(source, target);
        // ways of setting size and quality: of these only one is used, the first one set in this order
        var competing = SizeModes.Precedence
            .SelectMany(mode => definitions.Where(d => d.Mode == mode && d.Key != SettingKeys.Lossless))
            .Select(d => d.Key).ToList();

        return new JsonObject
        {
            ["source"] = source,
            ["target"] = target,
            ["settings"] = new JsonArray(definitions.Select(d =>
            {
                var node = new JsonObject
                {
                    ["key"] = d.Key,
                    ["label"] = d.Label,
                    ["kind"] = d.Kind.ToString().ToLowerInvariant(),
                    ["group"] = d.Group,
                    ["help"] = ForPrograms(d.Help),
                    ["default"] = d.Mode != null && d.InPanel ? "not set" : ForPrograms(d.Automatic),
                    ["reencodes"] = d.Reencodes
                };
                if (d.Kind == SettingKind.Number)
                {
                    node["min"] = d.Min;
                    node["max"] = d.Max;
                }
                if (d.Choices.Count > 0)
                    node["choices"] = new JsonArray(d.Choices.Select(c => (JsonNode)new JsonObject { ["value"] = c.Value, ["label"] = c.Label }).ToArray());
                return (JsonNode)node;
            }).ToArray()),
            ["onlyOneOf"] = Strings(competing)
        };
    }

    /// <summary>A setting's explanation for those without a window: the slider there is an option here.</summary>
    public static string ForPrograms(string text) => text
        .Replace("the Result slider", "--result")
        .Replace("The Result slider", "--result");

    /// <summary>What a job did or would do. <c>ok</c> is true when nothing failed.</summary>
    public static JsonObject Outcome(ConversionJob.Checked job, IReadOnlyList<JobFile> files, bool dryRun) => new()
    {
        ["ok"] = files.All(f => f.Status is not (JobStatus.Failed or JobStatus.Cancelled)),
        ["dryRun"] = dryRun,
        ["version"] = AppInfo.Version,
        ["target"] = job.Target,
        ["combined"] = job.Merge,
        ["results"] = new JsonArray(files.Select(FileNode).ToArray())
    };

    private static JsonNode FileNode(JobFile file)
    {
        var node = new JsonObject
        {
            ["status"] = file.Status,
            ["inputs"] = Strings(file.Inputs),
            ["inputBytes"] = file.Inputs.Sum(i => File.Exists(i) ? new FileInfo(i).Length : 0),
            ["output"] = file.Output.Length > 0 ? file.Output : null
        };
        if (file.Status == JobStatus.Planned || file.Status == JobStatus.Skipped && file.Seconds == 0 && file.Note == null)
        {
            node["replaces"] = file.Replaces;
            node["mayBecomeFolder"] = file.MayBecomeFolder;
        }
        if (file.Status == JobStatus.Converted)
        {
            bool folder = Directory.Exists(file.Output);
            node["outputIsFolder"] = folder;
            if (folder)
            {
                var inside = Directory.GetFiles(file.Output, "*", SearchOption.AllDirectories);
                Array.Sort(inside, StringComparer.Ordinal);
                node["outputBytes"] = inside.Sum(f => new FileInfo(f).Length);
                node["outputFiles"] = Strings(inside);
            }
            else if (File.Exists(file.Output))
                node["outputBytes"] = new FileInfo(file.Output).Length;
            node["seconds"] = Math.Round(file.Seconds, 2);
        }
        if (file.Note != null) node["note"] = file.Note;
        if (file.Error != null) node["error"] = Error(file.ErrorCode ?? JobErrors.ConversionFailed, file.Error);
        return node;
    }

    public static JsonObject Error(string code, string message, IReadOnlyList<string>? validTargets = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (validTargets != null) error["validTargets"] = Strings(validTargets);
        return error;
    }

    public static JsonObject Failure(JobProblem problem) => new()
    {
        ["ok"] = false,
        ["version"] = AppInfo.Version,
        ["error"] = Error(problem.Code, problem.Message, problem.ValidTargets)
    };
}

/// <summary>How an agent reaches this app: the command an MCP client starts.</summary>
public static class AgentSetup
{
    /// <summary>The program to start, with what it needs before its own arguments.</summary>
    public static IReadOnlyList<string> Command()
    {
        var process = Environment.ProcessPath ?? AppInfo.UnixName;
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return Assembly.GetEntryAssembly()?.Location is { Length: > 0 } assembly ? [process, assembly] : [process];

        // on Windows the window's program does not keep a terminal or a pipe waiting; its console twin does
        if (OperatingSystem.IsWindows() && !Path.GetFileNameWithoutExtension(process).EndsWith("-cli", StringComparison.OrdinalIgnoreCase))
        {
            var console = Path.Combine(Path.GetDirectoryName(process)!, Path.GetFileNameWithoutExtension(process) + "-cli.exe");
            if (File.Exists(console)) return [console];
        }
        return [process];
    }

    /// <summary>The block MCP clients keep their servers in, for this copy of the app.</summary>
    public static string McpClientConfig()
    {
        var command = Command();
        var args = new JsonArray(command.Skip(1).Append("--mcp").Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [AppInfo.UnixName] = new JsonObject { ["command"] = command[0], ["args"] = args }
            }
        }.ToJsonString(AgentJson.Indented);
    }
}
