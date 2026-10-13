using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    [AvaloniaFact]
    public async Task TheLightThemeRecoloursTheWholeWindow()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var app = (App)Avalonia.Application.Current!;
        try
        {
            var window = Show(viewModel, width: 1180, height: 720);
            viewModel.SetThemeCommand.Execute("light");
            Assert.True(viewModel.IsLightTheme);
            Assert.Equal("light", viewModel.Settings.Theme); // and it is remembered
            await viewModel.AddPathsAsync(SampleFiles(folder, "holiday.jpg", "trailer.mkv", "report.docx"));
            viewModel.Items[1].SelectedTarget = "mp4";
            viewModel.DetailedSettingsOpen = true;
            viewModel.Items[2].State = ItemState.Failed;
            viewModel.Items[2].StatusText = "Converting legacy .doc files requires Microsoft Word or LibreOffice.";

            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, window.ActualThemeVariant);
            Capture(window, "07-light");
            window.Close();
        }
        finally
        {
            app.ApplyTheme("dark");
        }
    }

    [AvaloniaFact]
    public async Task SettingsAreRememberedBetweenRuns()
    {
        using var folder = TestSetup.NewFolder();
        var store = new SettingsStore(folder.File("settings.json"));

        var first = new MainViewModel(store, findEngines: false);
        first.SizePercent = 60;
        first.SpeedIndex = 0;
        first.UseGpu = false;
        first.ExistingFileIndex = (int)ExistingFileAction.Overwrite;
        first.CustomOutputDirectory = folder.Path;
        await first.AddPathsAsync(SampleFiles(folder, "photo.png"));
        first.Items[0].SelectedTarget = "webp";
        first.Flush();

        var second = new MainViewModel(store, findEngines: false);
        Assert.Equal(60, second.SizePercent);
        Assert.Equal(0, second.SpeedIndex);
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
