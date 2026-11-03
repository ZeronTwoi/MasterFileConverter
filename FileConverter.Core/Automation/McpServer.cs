using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileConverter.Core.Settings;

namespace FileConverter.Core.Automation;

/// <summary>
/// The app as a tool for AI agents: a Model Context Protocol server over stdin and stdout
/// (JSON-RPC 2.0, one message per line), started with --mcp. It needs no library and no network:
/// the client starts it, it does what it is asked and ends when its input ends.
/// </summary>
public sealed class McpServer(TextReader input, TextWriter output)
{
    /// <summary>The protocol's versions this server speaks, newest first. A client asking for one of them gets it.</summary>
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const string Instructions = """
        Converts files between formats on this computer: images, audio, video, documents, spreadsheets, presentations, PDF and archives.
        Everything works on local paths; use absolute ones. A source file is never changed or replaced.
        Typical use: list_formats (what can become what), list_settings (what can be adjusted for a conversion), then convert.
        To modify a file (compress it, resize it, strip its metadata) convert it to its own format with settings: the result is a new file beside it.
        convert with dry_run=true says what would be written, without writing.
        status says which engines exist; audio and video need FFmpeg, which setup_engines downloads.
        """;

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly JobRunner _runner = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>Answers until the input ends; what is still running then is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var work = new List<Task>();
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? message = null;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { /* answered below */ }

            if (message == null)
            {
                await SendAsync(ErrorMessage(null, -32700, "Parse error: a message must be one JSON object on one line."));
                continue;
            }
            // a batch of requests may be handled side by side, and a cancellation must get through while a conversion runs
            work.Add(Task.Run(() => HandleAsync(message), CancellationToken.None));
            work.RemoveAll(task => task.IsCompleted);
        }

        foreach (var running in _running.Values) await running.CancelAsync();
        await Task.WhenAll(work);
    }

    private async Task HandleAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        var parameters = message["params"] as JsonObject;
        try
        {
            switch (method)
            {
                case "initialize":
                    await RespondAsync(id, Initialize(parameters));
                    break;
                case "ping":
                    await RespondAsync(id, new JsonObject());
                    break;
                case "tools/list":
                    await RespondAsync(id, new JsonObject { ["tools"] = Tools() });
                    break;
                case "tools/call":
                    await RespondAsync(id, await CallToolAsync(id, parameters));
                    break;
                case "notifications/cancelled":
                    if (_running.TryGetValue(IdKey(parameters?["requestId"]), out var cancelling)) await cancelling.CancelAsync();
                    break;
                case null when id != null:
                    await SendAsync(ErrorMessage(id, -32600, "Invalid request: no method."));
                    break;
                default:
                    // notifications ("notifications/initialized" and the like) are not answered
                    if (id != null) await SendAsync(ErrorMessage(id, -32601, "Method not found: " + method));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (id != null) await SendAsync(ErrorMessage(id, -32603, "Internal error: " + ex.Message));
        }
    }

    private static string IdKey(JsonNode? id) => id?.ToJsonString() ?? "";

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var asked = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = AppInfo.UnixName, ["title"] = AppInfo.DisplayName, ["version"] = AppInfo.Version },
            ["instructions"] = Instructions
        };
    }

    // ---------- the tools ----------

    private static JsonArray Tools() => (JsonArray)JsonNode.Parse("""
        [
          {
            "name": "convert",
            "title": "Convert files",
            "description": "Converts one or more local files to a target format and returns each result's path and size. Several inputs with a combinable target (pictures to pdf/gif/tiff, anything to an archive) become ONE file unless each=true. A file set to its own format is compressed or modified according to result and settings, written as a new file; the source is never changed. A failure of one file does not stop the others: read each result's status.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "inputs": { "type": "array", "items": { "type": "string" }, "minItems": 1, "description": "Paths of the files to convert; absolute paths are safest." },
                "target": { "type": "string", "description": "Target format as a file extension, e.g. webp, mp4, pdf. list_formats says which are possible." },
                "out_dir": { "type": "string", "description": "Folder for the results, made if missing. Default: next to each source." },
                "result": { "type": "string", "enum": ["smallest", "small", "good", "full", "best"], "description": "How far files are taken: smallest (about 25% of the size, quick), small (50%), good (75%), full (the default: full quality), best (full quality, smallest file, slowest)." },
                "size_percent": { "type": "integer", "minimum": 10, "maximum": 100, "description": "An exact target size as a percentage of the source, instead of the size that result gives." },
                "speed": { "type": "string", "enum": ["fastest", "fast", "balanced", "best"], "description": "Encoder speed, instead of the speed that result gives." },
                "settings": { "type": "object", "additionalProperties": { "type": ["string", "number", "boolean"] }, "description": "Precise settings by key, e.g. {\"maxWidth\": 1920, \"quality\": 85}. list_settings says which exist for a conversion." },
                "when_exists": { "type": "string", "enum": ["keep", "replace", "skip"], "description": "When the result's name is taken: keep both by numbering the new file (the default), replace the old result, or skip the file." },
                "each": { "type": "boolean", "description": "Convert every input by itself instead of combining them." },
                "use_gpu": { "type": "boolean", "description": "Allow the graphics card to encode video (default true)." },
                "dry_run": { "type": "boolean", "description": "Check everything and say what would be written, without writing." }
              },
              "required": ["inputs", "target"],
              "additionalProperties": false
            },
            "annotations": { "readOnlyHint": false, "destructiveHint": false, "idempotentHint": false, "openWorldHint": false }
          },
          {
            "name": "list_formats",
            "title": "List formats",
            "description": "Which file formats can be read and what each can become, with the result levels convert accepts. Give source to ask about one format.",
            "inputSchema": {
              "type": "object",
              "properties": { "source": { "type": "string", "description": "A source format as a file extension, e.g. mov." } },
              "additionalProperties": false
            },
            "annotations": { "readOnlyHint": true, "openWorldHint": false }
          },
          {
            "name": "list_settings",
            "title": "List settings",
            "description": "The precise settings a conversion accepts: key, kind, range or choices, default and what it does.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "source": { "type": "string", "description": "Source format as a file extension, e.g. mov." },
                "target": { "type": "string", "description": "Target format as a file extension, e.g. mp4." }
              },
              "required": ["source", "target"],
              "additionalProperties": false
            },
            "annotations": { "readOnlyHint": true, "openWorldHint": false }
          },
          {
            "name": "status",
            "title": "Status",
            "description": "The app's version and platform, and which conversion engines this computer has.",
            "inputSchema": { "type": "object", "additionalProperties": false },
            "annotations": { "readOnlyHint": true, "openWorldHint": false }
          },
          {
            "name": "setup_engines",
            "title": "Download engines",
            "description": "Downloads FFmpeg and 7-Zip from their publishers (about 100-150 MB) into the app's data folder and checks them against the published checksums. Audio, video and .7z conversions need this once. Ask the user first if downloads matter to them.",
            "inputSchema": { "type": "object", "additionalProperties": false },
            "annotations": { "readOnlyHint": false, "destructiveHint": false, "idempotentHint": true, "openWorldHint": true }
          }
        ]
        """)!;

    private async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? [];
        var progressToken = parameters?["_meta"]?["progressToken"]?.DeepClone();

        using var cancel = new CancellationTokenSource();
        var key = IdKey(id);
        _running[key] = cancel;
        try
        {
            return name switch
            {
                "convert" => await ConvertAsync(arguments, progressToken, cancel.Token),
                "list_formats" => ListFormats(arguments),
                "list_settings" => ListSettings(arguments),
                "status" => ToolResult(AgentJson.Status()),
                "setup_engines" => await SetupEnginesAsync(cancel.Token),
                _ => ToolFailure(new JobProblem(JobErrors.BadArguments, "No such tool: " + name))
            };
        }
        catch (Exception ex) when (ex is FriendlyException or InvalidOperationException or FormatException or HttpRequestException or IOException)
        {
            // an argument of the wrong type, or a download that failed: the agent can read this and try otherwise
            return ToolFailure(new JobProblem(JobErrors.BadArguments, ex.Message));
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private static JsonObject ListFormats(JsonObject arguments)
    {
        var source = arguments["source"]?.GetValue<string>();
        if (source != null && !FormatRegistry.Inputs.Contains(source.TrimStart('.').ToLowerInvariant()))
            return ToolFailure(new JobProblem(JobErrors.UnsupportedConversion, $".{source.TrimStart('.')} is not a format this app reads."));
        return ToolResult(AgentJson.Formats(source));
    }

    private static JsonObject ListSettings(JsonObject arguments)
    {
        var source = Required(arguments, "source").TrimStart('.').ToLowerInvariant();
        var target = Required(arguments, "target").TrimStart('.').ToLowerInvariant();
        var targets = FormatRegistry.GetTargets(source, rarOutputAvailable: true);
        return targets.Contains(target)
            ? ToolResult(AgentJson.SettingsOf(source, target))
            : ToolFailure(new JobProblem(JobErrors.UnsupportedConversion,
                $"cannot convert .{source} to .{target}. Valid targets: {string.Join(", ", targets)}", targets));
    }

    private static string Required(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() ?? throw new FormatException($"\"{name}\" is required.");

    private async Task<JsonObject> ConvertAsync(JsonObject arguments, JsonNode? progressToken, CancellationToken ct)
    {
        var (job, problem) = ReadJob(arguments);
        if (problem != null) return ToolFailure(problem);
        var (ready, notReady) = job!.Check();
        if (notReady != null) return ToolFailure(notReady);

        if (arguments["dry_run"]?.GetValue<bool>() == true)
            return ToolResult(AgentJson.Outcome(ready!, JobRunner.Plan(ready!), dryRun: true));

        IProgress<double>? progress = null;
        if (progressToken != null)
        {
            int last = -1;
            // the callback runs where the conversion does; messages go out one at a time through SendAsync
            progress = new InlineProgress(p =>
            {
                int percent = (int)(p * 100);
                if (percent < last + 5) return;
                last = percent;
                _ = SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/progress",
                    ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = percent, ["total"] = 100 }
                });
            });
        }

        var files = await _runner.RunAsync(ready!, null, progress, ct);
        var outcome = AgentJson.Outcome(ready!, files, dryRun: false);
        return ToolResult(outcome, isError: outcome["ok"]!.GetValue<bool>() == false);
    }

    /// <summary>The tool's arguments as a job; reads them by the same rules as the command line's options.</summary>
    internal static (ConversionJob? Job, JobProblem? Problem) ReadJob(JsonObject arguments)
    {
        static JobProblem Bad(string message) => new(JobErrors.BadArguments, message);

        if (arguments["inputs"] is not JsonArray inputs || inputs.Count == 0) return (null, Bad("\"inputs\" must be a list of file paths."));
        var target = arguments["target"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(target)) return (null, Bad("\"target\" must be a file extension such as webp."));

        var level = ResultLevels.All[ResultLevels.Default];
        if (arguments["result"]?.GetValue<string>() is { } result)
        {
            level = ResultLevels.Find(result)!;
            if (level == null) return (null, Bad($"\"result\" must be one of: {string.Join(", ", ResultLevels.All.Select(l => l.Name))}."));
        }

        var speed = level.Speed;
        if (arguments["speed"]?.GetValue<string>() is { } speedName)
        {
            if (ParseSpeed(speedName) is not { } parsedSpeed) return (null, Bad("\"speed\" must be one of: fastest, fast, balanced, best."));
            speed = parsedSpeed;
        }

        int size = level.SizePercent;
        if (arguments["size_percent"] is { } sizeNode)
        {
            size = sizeNode.GetValue<int>();
            if (size is < 10 or > 100) return (null, Bad("\"size_percent\" must be from 10 to 100."));
        }

        var whenExists = ExistingFileAction.KeepBoth;
        if (arguments["when_exists"]?.GetValue<string>() is { } whenName)
        {
            if (ParseWhenExists(whenName) is not { } parsedWhen) return (null, Bad("\"when_exists\" must be one of: keep, replace, skip."));
            whenExists = parsedWhen;
        }

        var settings = new List<string>();
        if (arguments["settings"] is JsonObject values)
            foreach (var (key, value) in values)
                settings.Add($"{key}={(value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value?.ToJsonString())}");

        return (new ConversionJob
        {
            Inputs = inputs.Select(i => i!.GetValue<string>()).ToList(),
            Target = target,
            OutputDirectory = arguments["out_dir"]?.GetValue<string>(),
            SizePercent = size,
            Speed = speed,
            UseGpu = arguments["use_gpu"]?.GetValue<bool>() ?? true,
            WhenExists = whenExists,
            Each = arguments["each"]?.GetValue<bool>() ?? false,
            Settings = settings
        }, null);
    }

    public static SpeedPreset? ParseSpeed(string name) => name.ToLowerInvariant() switch
    {
        "fastest" => SpeedPreset.Fastest,
        "fast" => SpeedPreset.Fast,
        "balanced" => SpeedPreset.Balanced,
        "best" => SpeedPreset.BestQuality,
        _ => null
    };

    public static ExistingFileAction? ParseWhenExists(string name) => name.ToLowerInvariant() switch
    {
        "keep" => ExistingFileAction.KeepBoth,
        "replace" or "overwrite" => ExistingFileAction.Overwrite,
        "skip" => ExistingFileAction.Skip,
        _ => null
    };

    private static async Task<JsonObject> SetupEnginesAsync(CancellationToken ct)
    {
        await ToolLocator.DownloadSevenZipAsync(ct);
        await ToolLocator.DownloadFfmpegAsync(null, ct);
        ToolLocator.Refresh();
        return ToolResult(AgentJson.Status());
    }

    private static JsonObject ToolResult(JsonObject structured, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(AgentJson.OneLine) }),
        ["structuredContent"] = structured,
        ["isError"] = isError
    };

    /// <summary>A failure the agent is to read and act on: a tool's result, not a protocol error.</summary>
    private static JsonObject ToolFailure(JobProblem problem) => ToolResult(AgentJson.Failure(problem), isError: true);

    // ---------- the wire ----------

    private Task RespondAsync(JsonNode? id, JsonObject result) => id == null
        ? Task.CompletedTask
        : SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private static JsonObject ErrorMessage(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private async Task SendAsync(JsonObject message)
    {
        var line = message.ToJsonString(AgentJson.OneLine);
        await _writing.WaitAsync();
        try
        {
            await output.WriteLineAsync(line);
            await output.FlushAsync();
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Calls back at once, not on a context captured at its creation: there is none here, and order matters.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
