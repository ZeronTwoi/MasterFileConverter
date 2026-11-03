using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FileConverter.Core;
using FileConverter.Core.Settings;
using FileConverter.ViewModels;
using FileConverter.Views;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// Opens the real window off-screen. Each test also saves what it drew to
/// "screenshots" beside the test binaries, so a change to the UI can be looked at.
/// </summary>
public class WindowTests
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static MainViewModel NewViewModel(TempFolder folder) =>
        new(new SettingsStore(folder.File("settings.json")), findEngines: false);

    private static MainWindow Show(MainViewModel viewModel, double width = 1100, double height = 720)
    {
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDir);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ScreenshotDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static string[] SampleFiles(TempFolder folder, params string[] names)
    {
        foreach (var name in names) File.WriteAllBytes(folder.File(name), new byte[1500]);
        return names.Select(folder.File).ToArray();
    }

    [AvaloniaFact]
    public void EmptyWindowInvitesAFileDrop()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);

        Assert.False(viewModel.HasItems);
        Assert.False(viewModel.ConvertAllCommand.CanExecute(null));
        Capture(window, "01-empty");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AddedFilesAppearWithTargetsAndTheCombineBanner()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);

        await viewModel.AddPathsAsync(SampleFiles(folder,
            "holiday_01.jpg", "holiday_02.jpg", "holiday_10.png", "interview.mp3", "trailer.mkv", "report.docx", "backup.zip", "notes.xyz"));

        Assert.Equal(8, viewModel.Items.Count);
        // a file of an unknown type, added by itself, can still be packed into an archive
        Assert.Equal("zip", viewModel.Items.Single(i => i.Extension == "xyz").SelectedTarget);
        Assert.True(viewModel.ShowBulkTarget);
        Assert.True(viewModel.ConvertAllCommand.CanExecute(null));

        // three images heading for the same PDF are offered as one combined file
        viewModel.BulkTarget = "pdf";
        Assert.True(viewModel.HasMergeableGroups);
        Assert.Contains("3 files → one PDF", viewModel.MergeHint);

        // rows in the states a batch goes through
        var rows = viewModel.Items;
        rows[3].State = ItemState.Converting; rows[3].Progress = 45; rows[3].StatusText = "Converting…";
        rows[4].OutputPath = rows[4].SourcePath; rows[4].State = ItemState.Done; rows[4].Progress = 100;
        rows[4].StatusText = "Done — 48.2 MB · lossless copy (no re-encoding)";
        rows[5].State = ItemState.Failed; rows[5].StatusText = "Converting legacy .doc files requires Microsoft Word or LibreOffice.";
        viewModel.SelectedItem = rows[1];

        Capture(window, "02-files");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ANarrowWindowStillShowsEverything()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel, width: 980, height: 580);
        await viewModel.AddPathsAsync(SampleFiles(folder, "a.png", "b.wav", "c.mp4"));
        viewModel.AutoSpeed = true;
        viewModel.CustomOutputDirectory = folder.Path;

        Capture(window, "03-narrow");
        window.Close();
    }

    /// <summary>
    /// The Format settings panel and the Settings for all files bar each keep their own space:
    /// in the smallest window, with every banner showing, the panel ends above the bar and
    /// draws nothing over it.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTwoSettingsPanelsNeverOverlap()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel, width: 980, height: 580);
        await viewModel.AddPathsAsync(SampleFiles(folder, "a.png", "b.png", "c.mov"));
        viewModel.BulkTarget = null;
        viewModel.Items[0].SelectedTarget = "pdf";
        viewModel.Items[1].SelectedTarget = "pdf"; // two pictures for one PDF: the combine banner
        viewModel.Items[2].SelectedTarget = "mp4";
        viewModel.SelectedItem = viewModel.Items[2];
        viewModel.AskAboutUpdates = true;
        viewModel.SetupNeeded = true;
        viewModel.UpdateStage = UpdateStage.Failed;
        viewModel.UpdateTitle = "The update could not be fetched";
        viewModel.DetailedSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.HasMergeableGroups);

        var panel = window.FindControl<Border>("FormatSettingsPanel")!;
        var bar = window.FindControl<Border>("AllFilesSettings")!;
        Assert.True(panel.IsVisible);
        Assert.True(panel.ClipToBounds); // whatever does not fit is cut off, not drawn over the bar
        var panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), window)!.Value.Y;
        var barTop = bar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(panelBottom <= barTop, $"the panel ends at {panelBottom}, below the top of the bar at {barTop}");

        Capture(window, "10-smallest-with-panel");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheBorderColourRecoloursTheWholeWindowAndIsRemembered()
    {
        using var folder = TestSetup.NewFolder();
        var store = new SettingsStore(folder.File("settings.json"));
        var viewModel = new MainViewModel(store, findEngines: false);
        var app = (App)Avalonia.Application.Current!;
        Avalonia.Media.Color Brush(string key) =>
            app.Resources.TryGetResource(key, Avalonia.Styling.ThemeVariant.Dark, out var found)
                ? ((Avalonia.Media.SolidColorBrush)found!).Color
                : default;
        Avalonia.Media.Color Line() =>
            app.Resources.TryGetResource("LineBrush", Avalonia.Styling.ThemeVariant.Dark, out var found)
                ? ((Avalonia.Media.SolidColorBrush)found!).Color
                : default;
        try
        {
            var window = Show(viewModel, width: 1180, height: 720);
            Assert.Equal("white", viewModel.Colours);
            await viewModel.AddPathsAsync(SampleFiles(folder, "holiday.jpg", "trailer.mkv", "report.docx"));
            viewModel.Items[1].SelectedTarget = "mp4";
            viewModel.SelectedItem = viewModel.Items[1];
            viewModel.DetailedSettingsOpen = true;
            viewModel.Items[2].State = ItemState.Failed;
            viewModel.Items[2].StatusText = "Converting legacy .doc files requires Microsoft Word or LibreOffice.";

            foreach (var scheme in ColourSchemes.All)
            {
                viewModel.SetColoursCommand.Execute(scheme.Id);
                Capture(window, "scheme-" + scheme.Id);
            }

            // a scheme is more than its line: the background and the text change with it
            var tokyo = ColourSchemes.Find("tokyo-night");
            viewModel.SetColoursCommand.Execute(tokyo.Id);
            Assert.Equal(Avalonia.Media.Color.Parse(tokyo.Line), Line());
            Assert.Equal(Avalonia.Media.Color.Parse(tokyo.Background), Brush("BgBrush"));
            Assert.Equal(Avalonia.Media.Color.Parse(tokyo.Text), Brush("TextBrush"));
            Capture(window, "07-tokyo-night");

            viewModel.SetColoursCommand.Execute("no such colours");   // nothing changes
            Assert.Equal(tokyo.Id, viewModel.Colours);

            viewModel.Flush();
            Assert.Equal(tokyo.Id, new MainViewModel(store, findEngines: false).Colours);
            window.Close();
        }
        finally
        {
            app.ApplyColours(ColourSchemes.Default);
        }
    }

    /// <summary>The menu offers exactly the schemes there are, each in its own colour, and choosing one works.</summary>
    [AvaloniaFact]
    public void TheMenuOffersTheColours()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var app = (App)Avalonia.Application.Current!;
        var window = Show(viewModel);
        try
        {
            var button = window.FindControl<Button>("MenuButton")!;
            var flyout = (MenuFlyout)button.Flyout!;
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();

            var colours = flyout.Items.OfType<MenuItem>().Single(item => item.Header as string == "Colours");
            colours.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var items = colours.Items.OfType<MenuItem>().ToList();
            Assert.Equal(ColourSchemes.All.Select(c => c.Id), items.Select(item => (string)item.CommandParameter!));
            Assert.Equal(ColourSchemes.All.Select(c => c.Name), items.Select(item => ((TextBlock)item.Header!).Text));
            Assert.Equal(ColourSchemes.All.Select(c => Avalonia.Media.Color.Parse(c.Line)),
                items.Select(item => ((Avalonia.Media.ISolidColorBrush)((TextBlock)item.Header!).Foreground!).Color));
            Assert.True(items[0].IsChecked); // white, to begin with

            Directory.CreateDirectory(ScreenshotDir);
            if (TopLevel.GetTopLevel(colours) is { } menu)
                menu.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, "14-menu.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            if (TopLevel.GetTopLevel(items[0]) is { } submenu)
                submenu.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, "15-colours.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

            items[2].Command!.Execute(items[2].CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("amber", viewModel.Colours);
            Assert.True(items[2].IsChecked);
            Assert.False(items[0].IsChecked);
            flyout.Hide();
        }
        finally
        {
            app.ApplyColours(ColourSchemes.Default);
            window.Close();
        }
    }

    /// <summary>A settings file from before the schemes, or with one nobody offers, starts white.</summary>
    [Fact]
    public void AnOldOrWrongColourStartsWhite()
    {
        using var folder = TestSetup.NewFolder();
        File.WriteAllText(folder.File("settings.json"), """{ "theme": "light", "colours": "tartan" }""");
        Assert.Equal("white", new SettingsStore(folder.File("settings.json")).Load().Colours);
    }

    /// <summary>Every scheme can be read: text and dim text, status colours and the line, on its background.</summary>
    [Fact]
    public void EveryColourSchemeCanBeRead()
    {
        static double Luminance(string hex)
        {
            var c = Avalonia.Media.Color.Parse(hex);
            static double Part(byte value)
            {
                double share = value / 255.0;
                return share <= 0.03928 ? share / 12.92 : Math.Pow((share + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Part(c.R) + 0.7152 * Part(c.G) + 0.0722 * Part(c.B);
        }
        static double Contrast(string a, string b)
        {
            double one = Luminance(a), two = Luminance(b);
            return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);
        }

        foreach (var scheme in ColourSchemes.All)
        {
            // 4.5 is what text of this size is asked for; it holds on the status line's surface too
            foreach (var (what, colour) in new[]
                     {
                         ("text", scheme.Text), ("dim", scheme.Dim), ("success", scheme.Success),
                         ("warning", scheme.Warning), ("error", scheme.Error), ("line", scheme.Line)
                     })
            {
                Assert.True(Contrast(colour, scheme.Background) >= 4.5, $"{scheme.Id}: {what} on the background");
                Assert.True(Contrast(colour, scheme.Surface) >= 3.5, $"{scheme.Id}: {what} on the surface");
            }
            // the big button and the mode block: the background's colour on the line's
            Assert.True(Contrast(scheme.Background, scheme.Line) >= 4.5, $"{scheme.Id}: on the accent");
        }
    }

    /// <summary>The names under the slider are the levels, in their order, and a click on one sets it.</summary>
    [AvaloniaFact]
    public void TheNamesUnderTheSliderSetTheResult()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        var stops = window.FindControl<Avalonia.Controls.Primitives.UniformGrid>("ResultStops")!.Children.OfType<Button>().ToList();

        Assert.Equal(ResultLevels.All.Select(level => level.Name), stops.Select(stop => (string)stop.Content!));
        stops[1].Command!.Execute(stops[1].CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, viewModel.ResultLevel);
        Assert.Contains("on", stops[1].Classes);
        Assert.DoesNotContain("on", stops[ResultLevels.Default].Classes);
        window.Close();
    }

    /// <summary>The function keys do what the bottom line says, and leave a key with Ctrl alone.</summary>
    [AvaloniaFact]
    public async Task TheFunctionKeysWork()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        viewModel.DetailedSettingsOpen = false;
        await viewModel.AddPathsAsync(SampleFiles(folder, "a.png", "b.wav"));

        window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.DetailedSettingsOpen);

        window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.DetailedSettingsOpen);

        var menu = (MenuFlyout)window.FindControl<Button>("MenuButton")!.Flyout!;
        window.KeyPressQwerty(PhysicalKey.F9, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);
        menu.Hide();

        var clear = (MenuFlyout)window.FindControl<Button>("ClearButton")!.Flyout!;
        window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(clear.IsOpen);
        clear.Hide();
        window.Close();
    }

    /// <summary>With no files, the start screen has the whole window, whatever was left switched on.</summary>
    [AvaloniaFact]
    public async Task WithNoFilesTheFormatSettingsMakeRoom()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        viewModel.DetailedSettingsOpen = true;
        var pane = window.FindControl<Border>("FormatSettingsPanel")!.Parent!;
        Dispatcher.UIThread.RunJobs();
        Assert.False(((Panel)pane).IsVisible);
        Capture(window, "00-start");

        await viewModel.AddPathsAsync(SampleFiles(folder, "a.png"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(((Panel)pane).IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsAreRememberedBetweenRuns()
    {
        using var folder = TestSetup.NewFolder();
        var store = new SettingsStore(folder.File("settings.json"));

        var first = new MainViewModel(store, findEngines: false);
        first.ResultLevel = 1;
        first.UseGpu = false;
        first.ExistingFileIndex = (int)ExistingFileAction.Overwrite;
        first.CustomOutputDirectory = folder.Path;
        await first.AddPathsAsync(SampleFiles(folder, "photo.png"));
        first.Items[0].SelectedTarget = "webp";
        first.Flush();

        var second = new MainViewModel(store, findEngines: false);
        Assert.Equal(1, second.ResultLevel);
        Assert.Equal(50, second.SizePercent);
        Assert.False(second.UseGpu);
        Assert.Equal((int)ExistingFileAction.Overwrite, second.ExistingFileIndex);
        Assert.Equal(folder.Path, second.CustomOutputDirectory);

        // the next png starts on the target the last one ended on
        await second.AddPathsAsync(SampleFiles(folder, "another.png"));
        Assert.Equal("webp", second.Items[0].SelectedTarget);
    }

    private sealed class NoDialogs : IAppHost
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => throw new PlatformNotSupportedException("no dialog service");
        public Task<string?> PickFolderAsync(string title) => throw new PlatformNotSupportedException("no dialog service");
        public void Close() { }
    }

    [AvaloniaFact]
    public async Task ADesktopWithoutFileDialogsGetsAMessageNotACrash()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        viewModel.Host = new NoDialogs();

        await viewModel.AddFilesCommand.ExecuteAsync(null);
        Assert.Contains("Drop files onto the window", viewModel.OverallStatus);

        viewModel.OverallStatus = "";
        await viewModel.AddFolderCommand.ExecuteAsync(null);
        await viewModel.ChooseOutputCommand.ExecuteAsync(null);
        Assert.Contains("no dialog service", viewModel.OverallStatus);
        Assert.Null(viewModel.CustomOutputDirectory);
    }

    [AvaloniaFact]
    public async Task AFolderIsAddedWithEverythingInside()
    {
        using var folder = TestSetup.NewFolder();
        Directory.CreateDirectory(folder.File("album"));
        Directory.CreateDirectory(folder.File(Path.Combine("album", "raw")));
        SampleFiles(folder, Path.Combine("album", "one.jpg"), Path.Combine("album", "two.jpg"),
            Path.Combine("album", "raw", "three.png"), Path.Combine("album", "thumbs.db"));

        var viewModel = NewViewModel(folder);
        await viewModel.AddPathsAsync([folder.File("album")]);

        // the unconvertible file inside the folder is left out rather than listed as an error
        Assert.Equal(3, viewModel.Items.Count);
        Assert.All(viewModel.Items, item => Assert.NotNull(item.SelectedTarget));
        Assert.Contains("left out", viewModel.OverallStatus);
    }

    [AvaloniaFact]
    public async Task AfterSetAllToASingleRowCanStillBeChanged()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        await viewModel.AddPathsAsync(SampleFiles(folder, "a.png", "b.png", "c.jpg"));
        Dispatcher.UIThread.RunJobs();

        viewModel.BulkTarget = "zip";
        Dispatcher.UIThread.RunJobs();
        Assert.All(viewModel.Items, item => Assert.Equal("zip", item.SelectedTarget));
        // the box has given its order and shows its placeholder again
        Assert.Null(viewModel.BulkTarget);

        // this change used to be undone at once: the box re-selected "zip" and applied it to every row again
        viewModel.Items[1].SelectedTarget = "7z";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["zip", "7z", "zip"], viewModel.Items.Select(item => item.SelectedTarget));

        // and the same order can be given a second time
        viewModel.BulkTarget = "zip";
        Dispatcher.UIThread.RunJobs();
        Assert.All(viewModel.Items, item => Assert.Equal("zip", item.SelectedTarget));
        window.Close();
    }

    /// <summary>
    /// Thousands of files in the list must not make the window stall. Each of these steps once
    /// looked at every row for every row, which with 6,000 files took several seconds apiece.
    /// They are not held against a clock here — machines differ twenty-fold, and a laptop is
    /// several times slower a few seconds into a job than at its start — but against themselves:
    /// a list of 6,000 and one of 1,500 take turns, step by step, and four times the files may
    /// take about four times as long, not sixteen.
    /// </summary>
    [AvaloniaFact]
    public async Task AVeryLongListStaysQuick()
    {
        const int few = 1500, many = 6000;
        using var folder = TestSetup.NewFolder();
        using var otherFolder = TestSetup.NewFolder();
        var (shortList, shortWindow) = await OpenList(folder, few);
        var (longList, longWindow) = await OpenList(otherFolder, many);
        var zipLevel = SettingCatalog.For("png", "zip").First(d => d.Key == SettingKeys.Level);

        static double Seconds(Action action)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            action();
            return watch.Elapsed.TotalSeconds;
        }

        // the steps that touch every row, and what each must have done
        var steps = new (string Name, Action<MainViewModel, int> Do, Action<MainViewModel> Check)[]
        {
            ("setting every target",
                (list, _) => list.BulkTarget = "zip",
                list =>
                {
                    Assert.All(list.Items, item => Assert.Equal("zip", item.SelectedTarget));
                    Assert.Contains($"{list.Items.Count} files → one ZIP", list.MergeHint);
                }),
            // a setting for the format marks every row that converts with it
            ("changing a setting",
                (list, round) =>
                {
                    list.SetFormatValue("zip", zipLevel, (5 + round).ToString());
                    list.OnDetailSettingsChanged();
                },
                list => Assert.All(list.Items, item => Assert.True(item.HasCustomSettings))),
            ("changing one target",
                (list, _) => list.Items[9].SelectedTarget = "7z",
                list =>
                {
                    Assert.Equal("7z", list.Items[9].SelectedTarget);
                    Assert.False(list.Items[9].HasCustomSettings);
                })
        };

        var growth = steps.ToDictionary(step => step.Name, _ => new List<double>());
        var slowest = steps.ToDictionary(step => step.Name, _ => 0.0);
        for (int round = 0; round < 5; round++)
        {
            foreach (var (name, act, check) in steps)
            {
                double withFew = Seconds(() => act(shortList, round));
                double withMany = Seconds(() => act(longList, round));
                check(shortList);
                check(longList);
                growth[name].Add(withMany / Math.Max(withFew, 1e-6));
                slowest[name] = Math.Max(slowest[name], withMany);
            }

            // back to where the round began, for the next one
            foreach (var list in new[] { shortList, longList })
            {
                list.SetFormatValue("zip", zipLevel, null);
                list.OnDetailSettingsChanged();
                list.BulkTarget = "tar";
            }
            Dispatcher.UIThread.RunJobs();
        }
        shortWindow.Close();
        longWindow.Close();

        foreach (var (name, _, _) in steps)
        {
            // the middle one of the five rounds: one interrupted measurement decides nothing
            double typical = growth[name].Order().ElementAt(2);
            // nine times leaves room for a busy machine; a look at every row for every row would be sixteen.
            // (A step that is over in a few hundredths of a second even with the long list has nothing to prove.)
            Assert.True(typical < 9 || slowest[name] < 0.03,
                $"{name}: {many:N0} files took {typical:0.0} times as long as {few:N0} — that grows faster than the list");
        }
    }

    /// <summary>A window with this many files in its list.</summary>
    private static async Task<(MainViewModel List, MainWindow Window)> OpenList(TempFolder folder, int count)
    {
        var files = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            files.Add(folder.File($"file {i:D5}.{(i % 3 == 0 ? "png" : i % 3 == 1 ? "jpg" : "txt")}"));
            File.WriteAllBytes(files[^1], new byte[64]);
        }
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        await viewModel.AddPathsAsync(files);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(count, viewModel.Items.Count);
        return (viewModel, window);
    }
}
