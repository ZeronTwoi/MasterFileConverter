using System.Text.Json.Nodes;
using FileConverter.Core;
using FileConverter.Core.Automation;
using FileConverter.Core.Settings;
using ImageMagick;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// What agents and scripts rely on: the MCP server spoken to as a client would, and the job layer
/// the command line shares with it. Field names and error codes tested here are a promise.
/// </summary>
public class AgentInterfaceTests
{
    private static string Picture(TempFolder folder, string name, uint side = 64)
    {
        var path = folder.File(name);
        using var image = new MagickImage(MagickColors.SteelBlue, side, side);
        image.Write(path);
        return path;
    }

    /// <summary>Sends these lines to a server, and gives back its answers once its input has ended.</summary>
    private static async Task<List<JsonObject>> Talk(params string[] lines)
    {
        using var input = new StringReader(string.Join("\n", lines) + "\n");
        var output = new StringWriter();
        await new McpServer(input, output).RunAsync();
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }

    private static string Call(int id, string tool, JsonObject arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
    }.ToJsonString();

    private static async Task<JsonObject> CallOne(string tool, JsonObject arguments)
    {
        var answers = await Talk(Call(1, tool, arguments));
        return Assert.Single(answers)["result"]!.AsObject();
    }

    private static JsonArray Paths(params string[] paths) => new(paths.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());

    [Fact]
    public async Task AClientIsGreetedInTheVersionItAskedFor()
    {
        var answers = await Talk(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        Assert.Equal(2, answers.Count); // the notification is not answered
        var greeting = answers.Single(a => a["id"]!.GetValue<int>() == 1)["result"]!;
        Assert.Equal("2025-06-18", greeting["protocolVersion"]!.GetValue<string>());
        Assert.Equal(AppInfo.Version, greeting["serverInfo"]!["version"]!.GetValue<string>());
        Assert.NotNull(greeting["capabilities"]!["tools"]);
        Assert.Contains("list_formats", greeting["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task EveryToolDescribesItself()
    {
        var answer = Assert.Single(await Talk("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var tools = answer["result"]!["tools"]!.AsArray();

        Assert.Equal(["convert", "list_formats", "list_settings", "setup_engines", "status"],
            tools.Select(t => t!["name"]!.GetValue<string>()).Order());
        Assert.All(tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["description"]!.GetValue<string>()));
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
        });
        // the levels the tool's schema offers are the slider's
        var offered = tools.Single(t => t!["name"]!.GetValue<string>() == "convert")!["inputSchema"]!["properties"]!["result"]!["enum"]!.AsArray();
        Assert.Equal(ResultLevels.All.Select(l => l.Name), offered.Select(o => o!.GetValue<string>()));
    }

    [Fact]
    public async Task MistakesInTheProtocolAreAnsweredNotFatal()
    {
        var answers = await Talk(
            "this is not json",
            """{"jsonrpc":"2.0","id":7,"method":"no/such/method"}""",
            """{"jsonrpc":"2.0","id":8,"method":"ping"}""");

        Assert.Equal(3, answers.Count);
        Assert.Contains(answers, a => a["error"]?["code"]?.GetValue<int>() == -32700);
        Assert.Contains(answers, a => a["id"]?.GetValue<int>() == 7 && a["error"]!["code"]!.GetValue<int>() == -32601);
        Assert.Contains(answers, a => a["id"]?.GetValue<int>() == 8 && a["result"] != null);
    }

    [Fact]
    public async Task ConvertWritesTheFileAndSaysWhere()
    {
        using var folder = TestSetup.NewFolder();
        var source = Picture(folder, "photo.png");

        var result = await CallOne("convert", new JsonObject { ["inputs"] = Paths(source), ["target"] = "jpg", ["out_dir"] = folder.File("out") });

        Assert.False(result["isError"]!.GetValue<bool>());
        var structured = result["structuredContent"]!;
        Assert.True(structured["ok"]!.GetValue<bool>());
        var file = Assert.Single(structured["results"]!.AsArray())!;
        Assert.Equal(JobStatus.Converted, file["status"]!.GetValue<string>());
        var output = file["output"]!.GetValue<string>();
        Assert.Equal(folder.File(Path.Combine("out", "photo.jpg")), output);
        Assert.True(File.Exists(output));
        Assert.Equal(new FileInfo(output).Length, file["outputBytes"]!.GetValue<long>());

        // what is in the text is what is in the structure: a client may read either
        Assert.Equal(structured.ToJsonString(), JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.ToJsonString());
    }

    [Fact]
    public async Task SettingsAreAppliedAndTheSourceIsLeftAlone()
    {
        using var folder = TestSetup.NewFolder();
        var source = Picture(folder, "photo.png", side: 200);
        var before = File.ReadAllBytes(source);

        // "modifying" a file: its own format, smaller — a new file beside it
        var result = await CallOne("convert", new JsonObject
        {
            ["inputs"] = Paths(source),
            ["target"] = "png",
            ["settings"] = new JsonObject { ["maxWidth"] = 50 }
        });

        var output = result["structuredContent"]!["results"]![0]!["output"]!.GetValue<string>();
        Assert.NotEqual(source, output);
        using var written = new MagickImage(output);
        Assert.Equal(50u, written.Width);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task ADryRunSaysWhatWouldHappenAndWritesNothing()
    {
        using var folder = TestSetup.NewFolder();
        var source = Picture(folder, "photo.png");
        Picture(folder, "photo.jpg"); // the name is taken

        var kept = await CallOne("convert", new JsonObject { ["inputs"] = Paths(source), ["target"] = "jpg", ["dry_run"] = true, ["out_dir"] = folder.File("never") });
        var plan = kept["structuredContent"]!;
        Assert.True(plan["dryRun"]!.GetValue<bool>());
        Assert.Equal(JobStatus.Planned, plan["results"]![0]!["status"]!.GetValue<string>());
        Assert.False(Directory.Exists(folder.File("never")));

        var numbered = await CallOne("convert", new JsonObject { ["inputs"] = Paths(source), ["target"] = "jpg", ["dry_run"] = true });
        Assert.Equal(folder.File("photo (1).jpg"), numbered["structuredContent"]!["results"]![0]!["output"]!.GetValue<string>());
        Assert.False(File.Exists(folder.File("photo (1).jpg")));

        var replacing = await CallOne("convert", new JsonObject { ["inputs"] = Paths(source), ["target"] = "jpg", ["dry_run"] = true, ["when_exists"] = "replace" });
        Assert.True(replacing["structuredContent"]!["results"]![0]!["replaces"]!.GetValue<bool>());

        var skipping = await CallOne("convert", new JsonObject { ["inputs"] = Paths(source), ["target"] = "jpg", ["dry_run"] = true, ["when_exists"] = "skip" });
        Assert.Equal(JobStatus.Skipped, skipping["structuredContent"]!["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SeveralPicturesBecomeOnePdfUnlessEachIsAsked()
    {
        using var folder = TestSetup.NewFolder();
        var a = Picture(folder, "page_1.png");
        var b = Picture(folder, "page_2.png");

        var combined = (await CallOne("convert", new JsonObject { ["inputs"] = Paths(b, a), ["target"] = "pdf" }))["structuredContent"]!;
        Assert.True(combined["combined"]!.GetValue<bool>());
        var one = Assert.Single(combined["results"]!.AsArray())!;
        Assert.Equal([a, b], one["inputs"]!.AsArray().Select(i => i!.GetValue<string>())); // in the order of their names
        Assert.True(File.Exists(one["output"]!.GetValue<string>()));

        var each = (await CallOne("convert", new JsonObject { ["inputs"] = Paths(a, b), ["target"] = "webp", ["each"] = true }))["structuredContent"]!;
        Assert.False(each["combined"]!.GetValue<bool>());
        Assert.Equal(2, each["results"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("""{ "inputs": ["__MISSING__"], "target": "jpg" }""", JobErrors.InputNotFound)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "mp3" }""", JobErrors.UnsupportedConversion)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "jpg", "settings": { "noSuchSetting": 1 } }""", JobErrors.InvalidSetting)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "jpg", "settings": { "metadata": "not a choice" } }""", JobErrors.InvalidSetting)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "jpg", "result": "enormous" }""", JobErrors.BadArguments)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "jpg", "size_percent": 500 }""", JobErrors.BadArguments)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "jpg", "speed": "warp" }""", JobErrors.BadArguments)]
    [InlineData("""{ "inputs": ["__PNG__"], "target": "jpg", "when_exists": "panic" }""", JobErrors.BadArguments)]
    [InlineData("""{ "inputs": [], "target": "jpg" }""", JobErrors.BadArguments)]
    [InlineData("""{ "target": "jpg" }""", JobErrors.BadArguments)]
    public async Task AWrongRequestIsAToolErrorWithACode(string arguments, string code)
    {
        using var folder = TestSetup.NewFolder();
        var source = Picture(folder, "photo.png");
        arguments = arguments.Replace("__PNG__", source.Replace("\\", "\\\\")).Replace("__MISSING__", folder.File("gone.png").Replace("\\", "\\\\"));

        var result = await CallOne("convert", JsonNode.Parse(arguments)!.AsObject());

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.False(result["structuredContent"]!["ok"]!.GetValue<bool>());
        Assert.Equal(code, result["structuredContent"]!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWrongTargetSaysWhichOnesAreRight()
    {
        using var folder = TestSetup.NewFolder();
        var result = await CallOne("convert", new JsonObject { ["inputs"] = Paths(Picture(folder, "photo.png")), ["target"] = "mp3" });
        var valid = result["structuredContent"]!["error"]!["validTargets"]!.AsArray().Select(t => t!.GetValue<string>());
        Assert.Contains("webp", valid);
    }

    [Fact]
    public async Task AFileThatFailsDoesNotStopTheOthers()
    {
        using var folder = TestSetup.NewFolder();
        var good = Picture(folder, "good.png");
        var bad = folder.File("bad.png");
        File.WriteAllText(bad, "not a picture at all");

        var result = await CallOne("convert", new JsonObject { ["inputs"] = Paths(good, bad), ["target"] = "webp", ["each"] = true });

        Assert.True(result["isError"]!.GetValue<bool>());
        var files = result["structuredContent"]!["results"]!.AsArray();
        Assert.False(result["structuredContent"]!["ok"]!.GetValue<bool>());
        Assert.Equal(JobStatus.Converted, files.Single(f => f!["inputs"]![0]!.GetValue<string>() == good)!["status"]!.GetValue<string>());
        var failed = files.Single(f => f!["inputs"]![0]!.GetValue<string>() == bad)!;
        Assert.Equal(JobStatus.Failed, failed["status"]!.GetValue<string>());
        Assert.Equal(JobErrors.ConversionFailed, failed["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResultLevelsGoByNameAndExactOptionsWinOverThem()
    {
        var (smallest, _) = McpServer.ReadJob(JsonNode.Parse("""{ "inputs": ["a.png"], "target": "jpg", "result": "smallest" }""")!.AsObject());
        Assert.Equal(25, smallest!.SizePercent);
        Assert.Equal(SpeedPreset.Fast, smallest.Speed);

        var (exact, _) = McpServer.ReadJob(JsonNode.Parse("""{ "inputs": ["a.png"], "target": "jpg", "result": "smallest", "size_percent": 60, "speed": "best" }""")!.AsObject());
        Assert.Equal(60, exact!.SizePercent);
        Assert.Equal(SpeedPreset.BestQuality, exact.Speed);

        var (plain, _) = McpServer.ReadJob(JsonNode.Parse("""{ "inputs": ["a.png"], "target": "jpg" }""")!.AsObject());
        Assert.Equal(100, plain!.SizePercent);
        Assert.Equal(SpeedPreset.Balanced, plain.Speed);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FormatsAndSettingsCanBeAskedFor()
    {
        var all = (await CallOne("list_formats", []))["structuredContent"]!;
        Assert.Contains(all["formats"]!.AsArray(), f => f!["extension"]!.GetValue<string>() == "png");
        Assert.Equal(ResultLevels.All.Count, all["resultLevels"]!.AsArray().Count);

        var one = (await CallOne("list_formats", new JsonObject { ["source"] = ".MOV" }))["structuredContent"]!;
        var mov = Assert.Single(one["formats"]!.AsArray())!;
        Assert.Equal("video", mov["category"]!.GetValue<string>());
        Assert.Contains("mp4", mov["targets"]!.AsArray().Select(t => t!.GetValue<string>()));

        Assert.True((await CallOne("list_formats", new JsonObject { ["source"] = "xyz" }))["isError"]!.GetValue<bool>());

        var settings = (await CallOne("list_settings", new JsonObject { ["source"] = "png", ["target"] = "jpg" }))["structuredContent"]!;
        var quality = settings["settings"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == SettingKeys.Quality)!;
        Assert.Equal("number", quality["kind"]!.GetValue<string>());
        Assert.True(quality["max"]!.GetValue<double>() <= 100);
        Assert.DoesNotContain("slider", quality["help"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase); // no window to speak of here

        var status = (await CallOne("status", []))["structuredContent"]!;
        Assert.Equal(AppInfo.Version, status["version"]!.GetValue<string>());
        Assert.NotNull(status["engines"]!["ffmpeg"]!["available"]);
    }

    [Fact]
    public async Task AnUnknownToolIsAToolError()
    {
        var result = await CallOne("format_the_disk", []);
        Assert.True(result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void TheClientConfigNamesAProgramAndTheServerSwitch()
    {
        var config = JsonNode.Parse(AgentSetup.McpClientConfig())!;
        var server = config["mcpServers"]![AppInfo.UnixName]!;
        Assert.False(string.IsNullOrEmpty(server["command"]!.GetValue<string>()));
        Assert.Equal("--mcp", server["args"]!.AsArray()[^1]!.GetValue<string>());
    }
}
