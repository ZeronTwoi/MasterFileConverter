using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FileConverter.Core;
using FileConverter.Core.Converters;
using FileConverter.Core.Settings;
using FileConverter.ViewModels;
using FileConverter.Views;
using ImageMagick;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// A file set to its own format to be compressed; a File size of its own; several files selected
/// and set at once, in coloured groups; and the Result slider in place of Speed, Auto and Use GPU.
/// </summary>
public class CompressAndGroupTests(Samples samples) : IClassFixture<Samples>
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
    private static readonly ConversionOptions Fast = new() { UseGpu = false, Speed = SpeedPreset.Fast };

    private static MainViewModel NewViewModel(TempFolder folder) =>
        new(new SettingsStore(folder.File("settings.json")), findEngines: false);

    private static string[] Files(TempFolder folder, params string[] names)
    {
        foreach (var name in names) File.WriteAllBytes(folder.File(name), new byte[2000]);
        return names.Select(folder.File).ToArray();
    }

    private static SettingRowViewModel Row(MainViewModel viewModel, string key) =>
        viewModel.Detail.Groups.SelectMany(g => g.Rows).Single(r => r.Definition.Key == key);

    private static TextSettingRow Box(MainViewModel viewModel, string key) => (TextSettingRow)Row(viewModel, key);

    private static void Way(MainViewModel viewModel, string mode)
    {
        var choice = (ChoiceSettingRow)Row(viewModel, K.SizeMode);
        choice.Selected = choice.Options.Single(o => o.Value == mode);
    }

    // ---------- the same format, only compressed ----------

    [Fact]
    public void AFileCanBeSetToItsOwnFormatToBeCompressed()
    {
        // offered last, so it is never what a new file is set to
        Assert.Equal("jpg", FormatRegistry.GetTargets("jpg", false)[^1]);
        Assert.Equal("jpg", FormatRegistry.GetTargets("jpeg", false)[^1]);
        Assert.NotEqual("jpg", FormatRegistry.GetTargets("jpg", false)[0]);
        Assert.Equal("png", FormatRegistry.GetTargets("png", false)[^1]);
        Assert.Equal("mp4", FormatRegistry.GetTargets("mp4", false)[^1]);
        Assert.Equal("mp3", FormatRegistry.GetTargets("mp3", false)[^1]);
        Assert.Equal("zip", FormatRegistry.GetTargets("zip", false)[^1]);
        Assert.Contains("rar", FormatRegistry.GetTargets("rar", rarOutputAvailable: true));
        Assert.DoesNotContain("rar", FormatRegistry.GetTargets("rar", rarOutputAvailable: false));

        // nothing to compress it with, or nothing to compress
        Assert.DoesNotContain("pdf", FormatRegistry.GetTargets("pdf", false));
        Assert.DoesNotContain("docx", FormatRegistry.GetTargets("docx", false));
        Assert.DoesNotContain("tar", FormatRegistry.GetTargets("tar", false));
        Assert.DoesNotContain("heic", FormatRegistry.GetTargets("heic", false)); // not a format the app writes

        // a file only compressed is never combined with others, even into a format that combines
        Assert.False(MergeConversion.CanMerge(FileCategory.Image, "gif", "gif"));
        Assert.False(MergeConversion.CanMerge(FileCategory.Image, "tiff", "tif"));
        Assert.True(MergeConversion.CanMerge(FileCategory.Image, "gif", "png"));

        // and its result never takes the source's own name, even when results are replaced
        var output = ConversionService.BuildOutputPath(samples.Jpg, "jpg", null, null, ExistingFileAction.Overwrite);
        Assert.NotEqual(samples.Jpg, output, StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith("photo (1).jpg", output);
    }

    [Fact]
    public async Task CompressingAPictureAsItIsNeverMakesItLarger()
    {
        // a JPEG saved again at top quality would grow: then it is kept as it was
        var same = await samples.ConvertAsync(samples.Jpg, "jpg", Fast);
        Assert.True(Samples.Size(same.OutputPath) <= Samples.Size(samples.Jpg));

        // at a smaller File size it is made smaller — by the slider, or by a size of its own
        var bySlider = await samples.ConvertAsync(samples.Jpg, "jpg", Fast with { SizePercent = 50 });
        Assert.True(Samples.Size(bySlider.OutputPath) < Samples.Size(samples.Jpg) * 0.75);
        var byOwnSize = await samples.ConvertAsync(samples.Jpg, "jpg", Fast, (K.SizePercent, "50"));
        Assert.True(Samples.Size(byOwnSize.OutputPath) < Samples.Size(samples.Jpg) * 0.75);

        // a PNG is compressed again without a pixel changing
        var png = await samples.ConvertAsync(samples.Png, "png", Fast);
        Assert.True(Samples.Size(png.OutputPath) <= Samples.Size(samples.Png));
        using (var before = new MagickImage(samples.Png))
        using (var after = new MagickImage(png.OutputPath))
            Assert.Equal(0, before.Compare(after, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task ATiffOfSeveralPagesStaysOneTiff()
    {
        // two plain pages, stored without compression: LZW makes them far smaller
        var scan = UncompressedTiff("scan.tiff", noisy: false);
        var result = await samples.ConvertAsync(scan, "tiff", Fast);
        Assert.True(File.Exists(result.OutputPath), $"{result.OutputPath} is not a file"); // one file, not a folder of pages
        Assert.Equal(2, ImageConversion.PageCount(result.OutputPath));
        Assert.True(Samples.Size(result.OutputPath) < Samples.Size(scan) / 2,
            $"{Samples.Size(result.OutputPath)} against {Samples.Size(scan)} ({result.Note})");

        // two pages of fine noise do not compress at all: they are kept as they were, not made larger
        var noise = UncompressedTiff("noise.tiff", noisy: true);
        var kept = await samples.ConvertAsync(noise, "tiff", Fast);
        Assert.Equal(Samples.Size(noise), Samples.Size(kept.OutputPath));
        Assert.Contains("kept as it was", kept.Note);
    }

    private string UncompressedTiff(string name, bool noisy)
    {
        var path = Path.Combine(samples.NewOutputFolder(), name);
        using var pages = new MagickImageCollection();
        foreach (var (width, height) in new (uint, uint)[] { (320, 240), (240, 320) })
        {
            var page = noisy ? Samples.Picture(width, height)
                             : new MagickImage("gradient:#2f6fd0-#f2c14e", new MagickReadSettings { Width = width, Height = height });
            page.Depth = 8;
            page.Settings.Compression = CompressionMethod.NoCompression;
            pages.Add(page);
        }
        pages.Write(path, MagickFormat.Tiff);
        return path;
    }

    [Fact]
    public async Task AnArchiveCanBePackedAgainSmaller()
    {
        var text = Path.Combine(samples.NewOutputFolder(), "notes.txt");
        await File.WriteAllTextAsync(text, string.Concat(Enumerable.Repeat("The same line, over and over again.\n", 8000)),
            TestContext.Current.CancellationToken);
        var stored = await samples.ConvertAsync(text, "zip", Fast, (K.Level, "0"));

        var packed = await samples.ConvertAsync(stored.OutputPath, "zip", Fast);
        Assert.True(Samples.Size(packed.OutputPath) < Samples.Size(stored.OutputPath) / 10);
    }

    [Fact]
    public async Task AVideoAndASoundCanBeMadeSmallerAsTheyAre()
    {
        Samples.RequireFfmpeg();
        var copied = await samples.ConvertAsync(samples.Clip, "mp4", Fast);
        Assert.True(Samples.Size(copied.OutputPath) <= Samples.Size(samples.Clip));

        var video = await samples.ConvertAsync(samples.Clip, "mp4", Fast, (K.SizePercent, "40"));
        Assert.True(Samples.Size(video.OutputPath) < Samples.Size(samples.Clip) * 0.75);

        var sound = await samples.ConvertAsync(samples.Mp3, "mp3", Fast with { SizePercent = 50 });
        Assert.True(Samples.Size(sound.OutputPath) < Samples.Size(samples.Mp3) * 0.75);
    }

    [AvaloniaFact]
    public void ARowSetToItsOwnFormatIsMarkedAndNeverSetSoByItself()
    {
        var row = new FileItemViewModel("C:\\x\\photo.jpg", 2000, false, preferredTarget: "jpg");
        Assert.NotEqual("jpg", row.SelectedTarget); // the format chosen last for others does not make it "compress"
        Assert.False(row.IsSameFormat);
        row.SelectedTarget = "jpg";
        Assert.True(row.IsSameFormat);
    }

    // ---------- a File size of its own ----------

    [AvaloniaFact]
    public async Task OneFileCanHaveAFileSizeOfItsOwn()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        await viewModel.AddPathsAsync(Files(folder, "a.png", "b.png"));
        viewModel.BulkTarget = "jpg";
        viewModel.CombineFiles = false;
        viewModel.DetailedSettingsOpen = true;
        viewModel.SelectedItem = viewModel.Items[0];

        viewModel.Detail.OnlyThisFile = true;
        Way(viewModel, SizeModes.Percent);
        Assert.Equal("50", Box(viewModel, K.SizePercent).Text); // a value to start from
        Box(viewModel, K.SizePercent).Text = "30";

        var own = viewModel.EffectiveSettings(viewModel.Items[0]);
        Assert.Equal(30, own.Int(K.SizePercent));
        Assert.Equal(30, new ConversionOptions { SizePercent = 100 }.WithDetail(own).SizePercent);
        Assert.True(viewModel.EffectiveSettings(viewModel.Items[1]).IsEmpty);
        // the slider at the bottom leaves that file alone, and says so
        Assert.Equal("Not for 1 of 2 JPG files: set in Format settings", viewModel.SliderExceptionText);
    }

    // ---------- several files at once, in groups ----------

    [AvaloniaFact]
    public async Task SelectedFilesAreSetTogetherAndFormGroups()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        await viewModel.AddPathsAsync(Files(folder, "a.png", "b.png", "c.png", "d.png"));
        viewModel.BulkTarget = "jpg";
        viewModel.CombineFiles = false;
        viewModel.DetailedSettingsOpen = true;
        var (a, b, c, d) = (viewModel.Items[0], viewModel.Items[1], viewModel.Items[2], viewModel.Items[3]);

        // a and b together, a quality of their own
        viewModel.SelectedItem = a;
        viewModel.SetSelection([a, b]);
        Assert.Equal("Only these 2 files", viewModel.Detail.ScopeFileText);
        Assert.Equal("2 files  →  jpg", viewModel.Detail.Subtitle);
        viewModel.Detail.OnlyThisFile = true;
        Way(viewModel, SizeModes.Quality);
        Box(viewModel, K.Quality).Text = "70";
        Assert.Equal("70", a.Overrides[K.Quality]);
        Assert.Equal("70", b.Overrides[K.Quality]);

        // c a size of its own: another group; d nothing: no group
        viewModel.SelectedItem = c;
        viewModel.SetSelection([c]);
        viewModel.Detail.OnlyThisFile = true;
        Way(viewModel, SizeModes.Percent);

        Assert.True(a.Group > 0);
        Assert.Equal(a.Group, b.Group);
        Assert.True(c.Group > 0 && c.Group != a.Group);
        Assert.Equal(0, d.Group);
        Assert.Contains("Quality: 70", a.GroupTip);
        Assert.Contains("File size (%): 50", c.GroupTip);

        // b and c are set different ways: the panel shows neither, until one is picked for both
        viewModel.SelectedItem = b;
        viewModel.SetSelection([b, c]);
        Assert.True(viewModel.Detail.OnlyThisFile);
        Assert.Null(((ChoiceSettingRow)Row(viewModel, K.SizeMode)).Selected);
        Assert.False(Row(viewModel, K.Quality).IsShown);
        Assert.False(Row(viewModel, K.SizePercent).IsShown);
        Way(viewModel, SizeModes.Quality);
        Assert.False(c.Overrides.ContainsKey(K.SizePercent));    // c's own size is gone: one way at a time
        Assert.Equal("", Box(viewModel, K.Quality).Text);        // b keeps its 70, c starts at 90 …
        Assert.Equal("Not the same in all 2", Box(viewModel, K.Quality).Watermark);
        Box(viewModel, K.Quality).Text = "60";                   // … until one value is typed for both
        Assert.Equal(b.Group, c.Group);                          // the same settings: one group
        Assert.NotEqual(a.Group, b.Group);

        viewModel.SelectedItem = a;
        viewModel.SetSelection([a, b]);
        Assert.Equal("", Box(viewModel, K.Quality).Text);
        Assert.Equal("Not the same in all 2", Box(viewModel, K.Quality).Watermark);
    }

    [AvaloniaFact]
    public async Task FilesConvertedInDifferentWaysAreNotSetTogether()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        await viewModel.AddPathsAsync(Files(folder, "a.png", "b.mov", "c.mkv", "d.mov"));
        viewModel.Items[0].SelectedTarget = "jpg";
        foreach (var item in viewModel.Items.Skip(1)) item.SelectedTarget = "mp4";
        viewModel.DetailedSettingsOpen = true;

        viewModel.SelectedItem = viewModel.Items[0];
        viewModel.SetSelection([viewModel.Items[0], viewModel.Items[1]]);
        Assert.False(viewModel.Detail.HasSettings);
        Assert.Contains("different ways", viewModel.Detail.EmptyText);

        // mov → mp4 and mkv → mp4 have the same settings: they can
        viewModel.SelectedItem = viewModel.Items[1];
        viewModel.SetSelection(viewModel.Items.Skip(1).ToList());
        Assert.True(viewModel.Detail.HasSettings);
        Assert.Equal("Only these 3 files", viewModel.Detail.ScopeFileText);
    }

    /// <summary>Shift-click, Ctrl-click, and both held while dragging over rows, in the real window.</summary>
    [AvaloniaFact]
    public async Task RowsAreSelectedWithShiftAndCtrlClickingAndDragging()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = new MainWindow { DataContext = viewModel, Width = 1180, Height = 760 };
        window.Show();
        await viewModel.AddPathsAsync(Files(folder, "a.png", "b.png", "c.png", "d.png", "e.png"));
        viewModel.BulkTarget = "jpg";
        viewModel.CombineFiles = false;
        viewModel.DetailedSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("FileList")!;

        Point On(int row)
        {
            var container = list.ContainerFromIndex(row)!;
            return container.TranslatePoint(new Point(130, container.Bounds.Height / 2), window)!.Value;
        }
        void Click(int row, RawInputModifiers keys = RawInputModifiers.None)
        {
            window.MouseDown(On(row), MouseButton.Left, keys);
            window.MouseUp(On(row), MouseButton.Left, keys);
            Dispatcher.UIThread.RunJobs();
        }
        void Drag(int from, int to, RawInputModifiers keys)
        {
            window.MouseDown(On(from), MouseButton.Left, keys);
            for (int row = from; row != to + Math.Sign(to - from); row += Math.Sign(to - from))
                window.MouseMove(On(row), keys | RawInputModifiers.LeftMouseButton);
            window.MouseUp(On(to), MouseButton.Left, keys);
            Dispatcher.UIThread.RunJobs();
        }
        int[] Selected() => viewModel.SelectedItems.Select(viewModel.Items.IndexOf).ToArray();

        Click(0);
        Assert.Equal([0], Selected());
        Click(2, RawInputModifiers.Shift);                 // a range
        Assert.Equal([0, 1, 2], Selected());
        Assert.Equal("Only these 3 files", viewModel.Detail.ScopeFileText);
        Click(1, RawInputModifiers.Control);               // Ctrl on a selected row takes it away
        Assert.Equal([0, 2], Selected());
        Click(4, RawInputModifiers.Control);               // and on another adds it
        Assert.Equal([0, 2, 4], Selected());

        Click(0);
        Drag(1, 3, RawInputModifiers.Shift);               // Shift held while dragging: from the start of the selection
        Assert.Equal([0, 1, 2, 3], Selected());

        Click(0);
        Drag(2, 4, RawInputModifiers.Control);             // Ctrl held while dragging: every row passed over
        Assert.Equal([0, 2, 3, 4], Selected());
        Drag(4, 3, RawInputModifiers.Control);             // starting on a selected one: taken away
        Assert.Equal([0, 2], Selected());

        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(ScreenshotDir, "11-several-selected.png"),
            Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
    }

    /// <summary>What it all looks like: a file only compressed, two groups, several files in the panel, the speed folded open.</summary>
    [AvaloniaFact]
    public async Task TheWindowShowsCompressionGroupsAndTheResultSlider()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = new MainWindow { DataContext = viewModel, Width = 1180, Height = 760 };
        window.Show();
        await viewModel.AddPathsAsync(Files(folder, "beach.png", "cliffs.png", "harbour.png", "sunset.png", "trailer.mp4"));
        var rows = viewModel.Items;
        foreach (var row in rows.Take(4)) row.SelectedTarget = "jpg";
        rows[4].SelectedTarget = "mp4"; // only compressed
        viewModel.CombineFiles = false;
        viewModel.DetailedSettingsOpen = true;

        viewModel.SelectedItem = rows[0];
        viewModel.SetSelection([rows[0], rows[1]]);
        viewModel.Detail.OnlyThisFile = true;
        Way(viewModel, SizeModes.Percent);
        Box(viewModel, K.SizePercent).Text = "40";
        viewModel.SelectedItem = rows[2];
        viewModel.SetSelection([rows[2]]);
        viewModel.Detail.OnlyThisFile = true;
        Way(viewModel, SizeModes.Quality);
        viewModel.SelectedItem = rows[0];
        viewModel.SetSelection([rows[0], rows[1]]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(rows[4].IsSameFormat);
        Assert.Equal("Not for 3 of 4 JPG files: set in Format settings", viewModel.SliderExceptionText);
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(ScreenshotDir, "12-groups-and-compression.png"),
            Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

        window.FindControl<ToggleButton>("SpeedDetails")!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(ScreenshotDir, "13-speed-details.png"),
            Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
    }

    // ---------- the Result slider ----------

    [AvaloniaFact]
    public void TheResultSliderIsTheSpeedInPlainWords()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);

        viewModel.AutoSpeed = true;                         // chosen for this PC: the slider shows that choice
        var chosen = viewModel.ResultLevel;
        Assert.Equal((int)viewModel.SpeedInForce, chosen);
        Assert.EndsWith("(chosen for this PC)", viewModel.ResultText);

        viewModel.ResultLevel = 0;                          // moved by hand: Auto is off, the speed is that one
        Assert.False(viewModel.AutoSpeed);
        Assert.Equal(SpeedPreset.Fastest, viewModel.SpeedInForce);
        Assert.StartsWith("Done soonest", viewModel.ResultText);
        viewModel.ResultLevel = 3;
        Assert.Equal(SpeedPreset.BestQuality, viewModel.SpeedInForce);
        Assert.Equal(3, viewModel.SpeedIndex);              // the Speed list in Speed details says the same
    }
}
