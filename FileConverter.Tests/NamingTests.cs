using FileConverter.Core;
using FileConverter.Core.Platform;
using Xunit;

namespace FileConverter.Tests;

/// <summary>The app's name is said once (Directory.Build.props); these check that what is built agrees with it.</summary>
public class NamingTests
{
    [Fact]
    public void TheProgramsCarryTheAppsName()
    {
        Assert.Equal(AppInfo.ProgramName, typeof(App).Assembly.GetName().Name);
        Assert.Equal(AppInfo.ProgramName + ".Core", typeof(AppInfo).Assembly.GetName().Name);
        Assert.StartsWith(AppInfo.ProgramName + ".Platform.", PlatformServices.Current.GetType().Assembly.GetName().Name);
    }

    [Fact]
    public void TheNamesAreUsableWhereTheyAreUsed()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.DisplayName));
        // a file name on both systems, and a word in a command line
        Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", AppInfo.ProgramName);
        // a command, a menu entry's file name and an icon name on Linux
        Assert.Matches("^[a-z][a-z0-9-]*$", AppInfo.UnixName);
        Assert.True(Version.TryParse(AppInfo.Version.Split('-')[0], out _), AppInfo.Version);
    }

    [Fact]
    public void SettingsAndEnginesAreKeptUnderTheAppsName()
    {
        var platform = PlatformServices.Current;
        Assert.Contains(Path.GetFileName(platform.DataDirectory), new[] { AppInfo.ProgramName, AppInfo.UnixName });
        Assert.Contains(Path.GetFileName(platform.ConfigDirectory), new[] { AppInfo.ProgramName, AppInfo.UnixName });
        // and not where the earlier name kept them
        Assert.NotNull(platform.EarlierDirectories);
        Assert.NotEqual(platform.DataDirectory, platform.EarlierDirectories!.Value.Data, PlatformServices.PathComparer);
        Assert.NotEqual(platform.ConfigDirectory, platform.EarlierDirectories!.Value.Config, PlatformServices.PathComparer);
    }

    [Fact]
    public void TheLicencesComeWithTheProgram()
    {
        // the folder the window's menu opens
        var licences = Path.Combine(AppContext.BaseDirectory, "licenses");
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(licences, "LICENSE.txt")));

        // and every text the list of libraries points to is there with it
        var notices = File.ReadAllText(Path.Combine(licences, "THIRD-PARTY-NOTICES.md"));
        var named = System.Text.RegularExpressions.Regex.Matches(notices, @"\| `([^`*]+\.(?:txt|md))`")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(named);
        foreach (var file in named.Where(f => OperatingSystem.IsLinux() || !f.Contains("Inter")))
            Assert.True(File.Exists(Path.Combine(licences, file)), file);
    }

    [Fact]
    public void TheWindowsInstallerRegistersTheFileTypesTheAppReads()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "installer.iss"));
        var types = System.Text.RegularExpressions.Regex.Match(script, "#define Types \"([^\"]+)\"").Groups[1].Value.Split('|');
        Assert.Equal(FormatRegistry.Inputs.Order(), types.Order());
    }
}

/// <summary>Taking over what a version under the earlier name left behind.</summary>
public class EarlierVersionTests
{
    private static bool Link(string link, string existing) => PlatformServices.Current.TryCreateHardLink(link, existing);

    [Fact]
    public void SettingsAndEnginesAreTakenOverAndTheOldFolderIsLeftAsItWas()
    {
        using var folder = TestSetup.NewFolder();
        var old = Directory.CreateDirectory(folder.File("FileConverter")).FullName;
        Directory.CreateDirectory(Path.Combine(old, "tools"));
        File.WriteAllText(Path.Combine(old, "settings.json"), """{ "theme": "light" }""");
        File.WriteAllText(Path.Combine(old, "tools", "ffmpeg"), "an engine");
        File.WriteAllText(Path.Combine(old, "tools", "ffmpeg-download.tmp"), "half a download");
        File.WriteAllText(Path.Combine(old, "Settings.user.xml"), "another program's");
        var before = Snapshot(old);

        var now = folder.File("MasterFileConverter");
        EarlierVersion.Adopt(old, old, now, now, Link);

        Assert.Equal("""{ "theme": "light" }""", File.ReadAllText(Path.Combine(now, "settings.json")));
        Assert.Equal("an engine", File.ReadAllText(Path.Combine(now, "tools", "ffmpeg")));
        Assert.False(File.Exists(Path.Combine(now, "tools", "ffmpeg-download.tmp")));
        Assert.False(File.Exists(Path.Combine(now, "Settings.user.xml")));
        Assert.Equal(before, Snapshot(old));
    }

    [Fact]
    public void SeparateFoldersForSettingsAndEngines()
    {
        using var folder = TestSetup.NewFolder();
        var oldConfig = Directory.CreateDirectory(folder.File("config-old")).FullName;
        var oldData = Directory.CreateDirectory(folder.File("data-old")).FullName;
        File.WriteAllText(Path.Combine(oldConfig, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(oldData, "tools"));
        File.WriteAllText(Path.Combine(oldData, "tools", "7zz"), "an engine");

        EarlierVersion.Adopt(oldData, oldConfig, folder.File("data"), folder.File("config"), Link);

        Assert.True(File.Exists(Path.Combine(folder.File("config"), "settings.json")));
        Assert.Equal("an engine", File.ReadAllText(Path.Combine(folder.File("data"), "tools", "7zz")));
        Assert.False(Directory.Exists(Path.Combine(folder.File("config"), "tools")));
    }

    [Fact]
    public void AnEngineThatCannotGetASecondNameIsCopied()
    {
        using var folder = TestSetup.NewFolder();
        var old = Directory.CreateDirectory(folder.File("old")).FullName;
        Directory.CreateDirectory(Path.Combine(old, "tools"));
        File.WriteAllText(Path.Combine(old, "tools", "ffmpeg"), "an engine");

        EarlierVersion.Adopt(old, old, folder.File("new"), folder.File("new"), (_, _) => false);

        Assert.Equal("an engine", File.ReadAllText(Path.Combine(folder.File("new"), "tools", "ffmpeg")));
    }

    [Fact]
    public void AFolderThisVersionAlreadyHasIsNotTouched()
    {
        using var folder = TestSetup.NewFolder();
        var old = Directory.CreateDirectory(folder.File("old")).FullName;
        Directory.CreateDirectory(Path.Combine(old, "tools"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "old settings");
        File.WriteAllText(Path.Combine(old, "tools", "ffmpeg"), "an engine");
        var now = Directory.CreateDirectory(folder.File("new")).FullName;
        File.WriteAllText(Path.Combine(now, "settings.json"), "new settings");

        EarlierVersion.Adopt(old, old, now, now, Link);

        Assert.Equal("new settings", File.ReadAllText(Path.Combine(now, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(now, "tools")));
    }

    [Fact]
    public void NothingToTakeOverMakesNothing()
    {
        using var folder = TestSetup.NewFolder();
        EarlierVersion.Adopt(folder.File("old"), folder.File("old"), folder.File("new"), folder.File("new"), Link);
        Assert.False(Directory.Exists(folder.File("new")));
    }

    private static string Snapshot(string folder) => string.Join("\n",
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => $"{Path.GetRelativePath(folder, file)}={File.ReadAllText(file)}"));
}
