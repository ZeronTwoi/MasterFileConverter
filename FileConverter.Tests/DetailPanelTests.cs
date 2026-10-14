using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FileConverter.Core.Settings;
using FileConverter.ViewModels;
using FileConverter.Views;
using ImageMagick;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>The Format settings panel, driven through its view model with the real window open off-screen.</summary>
public class DetailPanelTests(Samples samples) : IClassFixture<Samples>
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static MainViewModel NewViewModel(TempFolder folder) =>
        new(new SettingsStore(folder.File("settings.json")), findEngines: false);

    private static MainWindow Show(MainViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1180, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Capture(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(ScreenshotDir, name + ".png"),
            Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static string[] Files(TempFolder folder, params string[] names)
    {
        foreach (var name in names) File.WriteAllBytes(folder.File(name), new byte[2000]);
        return names.Select(folder.File).ToArray();
    }

    private static TextSettingRow Box(MainViewModel viewModel, string key) =>
        (TextSettingRow)viewModel.Detail.Groups.SelectMany(g => g.Rows).Single(r => r.Definition.Key == key);

    private static ChoiceSettingRow Choice(MainViewModel viewModel, string key) =>
        (ChoiceSettingRow)viewModel.Detail.Groups.SelectMany(g => g.Rows).Single(r => r.Definition.Key == key);

    /// <summary>Picks a way of setting size and quality, as a click in the panel's "Set by" list does.</summary>
    private static void Way(MainViewModel viewModel, string mode) =>
        Choice(viewModel, K.SizeMode).Selected = Choice(viewModel, K.SizeMode).Options.Single(o => o.Value == mode);

    private static string[] ShownKeys(MainViewModel viewModel) =>
        viewModel.Detail.Groups.SelectMany(g => g.Rows).Where(r => r.IsShown).Select(r => r.Definition.Key).ToArray();

    [AvaloniaFact]
    public async Task ThePanelIsOffUntilAskedForAndThenShowsTheSelectedConversion()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        await viewModel.AddPathsAsync(Files(folder, "trailer.mkv", "holiday.png", "report.pdf", "notes.txt"));
        viewModel.Items[0].SelectedTarget = "mp4";

        Assert.False(viewModel.DetailedSettingsOpen);

        viewModel.DetailedSettingsOpen = true;
        Assert.Same(viewModel.Items[0], viewModel.SelectedItem); // something is selected, so the panel is not empty
        Assert.True(viewModel.Detail.HasSettings);
        Assert.Equal("MP4 settings", viewModel.Detail.Title);
        Assert.Equal("Size and quality", viewModel.Detail.Groups[0].Name);
        Assert.Contains(viewModel.Detail.Groups, g => g.Name == "Video");
        Assert.Contains(viewModel.Detail.Groups, g => g.Name == "Audio");
        Assert.Null(viewModel.Detail.Warning);
        Assert.Null(viewModel.SliderExceptionText);

        // a setting for every MP4
        Way(viewModel, SizeModes.Quality);
        Box(viewModel, K.Crf).Text = "23";
        Choice(viewModel, K.VideoCodec).Selected = Choice(viewModel, K.VideoCodec).Options.Single(o => o.Value == "hevc");
        Assert.Equal("23", viewModel.Settings.FormatSettings["mp4"][K.Crf]);
        Assert.Equal("hevc", viewModel.Settings.FormatSettings["mp4"][K.VideoCodec]);
        Assert.True(viewModel.Items[0].HasCustomSettings);
        Assert.False(viewModel.Items[1].HasCustomSettings); // a png is not an mp4
        Assert.Contains("encoded again", viewModel.Detail.Warning);
        Assert.Equal("Not for MP4: set in Format settings", viewModel.SliderExceptionText);
        Capture(window, "04-detail-video");

        // the same panel for a picture has different settings
        viewModel.SelectedItem = viewModel.Items[1];
        viewModel.Items[1].SelectedTarget = "jpg";
        Assert.Equal("JPG settings", viewModel.Detail.Title);
        Assert.Contains(viewModel.Detail.Groups.SelectMany(g => g.Rows), r => r.Definition.Key == K.Chroma);
        Way(viewModel, SizeModes.Quality);
        Box(viewModel, K.Quality).Text = "85";
        Box(viewModel, K.MaxWidth).Text = "1920";
        Capture(window, "05-detail-image");

        // …and for a PDF going to the same format, a resolution and a page list on top
        viewModel.SelectedItem = viewModel.Items[2];
        viewModel.Items[2].SelectedTarget = "jpg";
        Assert.Contains(viewModel.Detail.Groups.SelectMany(g => g.Rows), r => r.Definition.Key == K.Dpi);
        Assert.Equal("85", Box(viewModel, K.Quality).Text); // the JPG quality set a moment ago applies here too

        // a conversion with nothing to set says so
        viewModel.SelectedItem = viewModel.Items[3];
        viewModel.Items[3].SelectedTarget = "docx";
        Assert.False(viewModel.Detail.HasSettings);
        Capture(window, "06-detail-nothing");
        window.Close();
    }

    [AvaloniaFact]
    public async Task OneFileCanDifferFromItsFormat()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        await viewModel.AddPathsAsync(Files(folder, "a.png", "b.png"));
        viewModel.BulkTarget = "jpg";
        viewModel.CombineFiles = false;
        viewModel.DetailedSettingsOpen = true;
        viewModel.SelectedItem = viewModel.Items[0];

        Way(viewModel, SizeModes.Quality);
        Box(viewModel, K.Quality).Text = "80";              // for every JPG
        viewModel.Detail.OnlyThisFile = true;
        Assert.Equal("", Box(viewModel, K.Quality).Text);     // nothing of its own yet…
        Assert.Contains("80", Box(viewModel, K.Quality).Watermark); // …and the box says what it follows
        Box(viewModel, K.Quality).Text = "55";              // for a.png alone
        Box(viewModel, K.MaxWidth).Text = "640";

        Assert.Equal(55, viewModel.EffectiveSettings(viewModel.Items[0]).Int(K.Quality));
        Assert.Equal(640, viewModel.EffectiveSettings(viewModel.Items[0]).Int(K.MaxWidth));
        Assert.Equal(80, viewModel.EffectiveSettings(viewModel.Items[1]).Int(K.Quality));
        Assert.False(viewModel.EffectiveSettings(viewModel.Items[1]).Has(K.MaxWidth));
        Assert.Equal("80", viewModel.Settings.FormatSettings["jpg"][K.Quality]);

        // coming back to the file, the panel opens on its own settings
        viewModel.SelectedItem = viewModel.Items[1];
        Assert.False(viewModel.Detail.OnlyThisFile);
        viewModel.SelectedItem = viewModel.Items[0];
        Assert.True(viewModel.Detail.OnlyThisFile);
        Assert.Equal("55", Box(viewModel, K.Quality).Text);

        // Reset clears the scope being shown, not the other one
        viewModel.Detail.ResetCommand.Execute(null);
        Assert.Empty(viewModel.Items[0].Overrides);
        Assert.Equal(80, viewModel.EffectiveSettings(viewModel.Items[0]).Int(K.Quality));
    }

    /// <summary>
    /// Size and quality are set in one place at a time: the File size slider at the bottom, or
    /// one way chosen for a format in the panel — never several that override each other.
    /// </summary>
    [AvaloniaFact]
    public async Task SizeAndQualityAreSetOneWayAtATime()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var window = Show(viewModel);
        await viewModel.AddPathsAsync(Files(folder, "clip.mov", "song.wav"));
        viewModel.Items[0].SelectedTarget = "mp4";
        viewModel.Items[1].SelectedTarget = "mp3";
        viewModel.DetailedSettingsOpen = true;
        viewModel.SelectedItem = viewModel.Items[0];

        // at first the slider decides, and none of the ways' settings is shown — nor a second speed
        Assert.Equal(SizeModes.Slider, Choice(viewModel, K.SizeMode).Selected!.Value);
        Assert.Equal(4, Choice(viewModel, K.SizeMode).Options.Count); // no separate "automatic" entry
        Assert.DoesNotContain(K.Crf, ShownKeys(viewModel));
        Assert.DoesNotContain(K.VideoBitrate, ShownKeys(viewModel));
        Assert.DoesNotContain(K.TargetSizeMb, ShownKeys(viewModel));
        Assert.DoesNotContain(viewModel.Detail.Groups.SelectMany(g => g.Rows), r => r.Definition.Key == K.Speed);

        // a fixed quality: its box alone, with a value to start from
        Way(viewModel, SizeModes.Quality);
        Assert.Contains(K.Crf, ShownKeys(viewModel));
        Assert.DoesNotContain(K.TargetSizeMb, ShownKeys(viewModel));
        Assert.Equal("20", Box(viewModel, K.Crf).Text);
        Assert.Equal(20, viewModel.EffectiveSettings(viewModel.Items[0]).Int(K.Crf));
        Assert.Equal("Not for MP4: set in Format settings", viewModel.SliderExceptionText);

        // an exact size instead: the quality is gone, not left to compete with it
        Way(viewModel, SizeModes.Size);
        Assert.False(viewModel.Settings.FormatSettings["mp4"].ContainsKey(K.Crf));
        Assert.Equal("", Box(viewModel, K.TargetSizeMb).Text); // nothing typed yet…
        Assert.Null(viewModel.SliderExceptionText);            // …so the slider still decides
        Box(viewModel, K.TargetSizeMb).Text = "8";
        Assert.Equal(8, viewModel.EffectiveSettings(viewModel.Items[0]).Number(K.TargetSizeMb));
        Assert.False(viewModel.EffectiveSettings(viewModel.Items[0]).Has(K.SizeMode)); // no converter reads the choice
        Capture(window, "08-detail-one-way");

        // one file can go back to the slider while every other MP4 keeps its size
        viewModel.Detail.OnlyThisFile = true;
        Assert.Equal(SizeModes.Size, Choice(viewModel, K.SizeMode).Selected!.Value); // it follows its format's at first
        Way(viewModel, SizeModes.Slider);
        Assert.True(viewModel.EffectiveSettings(viewModel.Items[0]).IsEmpty);
        viewModel.Detail.OnlyThisFile = false;
        Assert.Equal(SizeModes.Size, Choice(viewModel, K.SizeMode).Selected!.Value);

        // the slider again for every MP4: nothing of the size is left behind
        Way(viewModel, SizeModes.Slider);
        Assert.False(viewModel.Settings.FormatSettings.ContainsKey("mp4"));

        // sound has a quality of its own (a variable bitrate), a bitrate and a size
        viewModel.SelectedItem = viewModel.Items[1];
        Way(viewModel, SizeModes.Bitrate);
        Assert.Equal("256", Box(viewModel, K.AudioBitrate).Text);
        Assert.DoesNotContain(K.VbrQuality, ShownKeys(viewModel));
        Assert.Equal("Not for MP3: set in Format settings", viewModel.SliderExceptionText);
        Capture(window, "09-slider-exception");
        window.Close();
    }

    /// <summary>Settings saved by 2.5.0, before there was a choice of way, keep doing what they did.</summary>
    [AvaloniaFact]
    public async Task SettingsSavedBeforeTheChoiceKeepWorking()
    {
        using var folder = TestSetup.NewFolder();
        var store = new SettingsStore(folder.File("settings.json"));
        var old = store.Load();
        old.FormatSettings["mp4"] = new() { [K.Crf] = "26", [K.TargetSizeMb] = "50", [K.Speed] = "fastest" };
        store.Save(old);

        var viewModel = new MainViewModel(store, findEngines: false);
        await viewModel.AddPathsAsync(Files(folder, "clip.mov"));
        viewModel.Items[0].SelectedTarget = "mp4";
        viewModel.DetailedSettingsOpen = true;

        // the exact size beat the quality level then, and is what the panel shows now
        Assert.Equal(SizeModes.Size, Choice(viewModel, K.SizeMode).Selected!.Value);
        Assert.Equal("50", Box(viewModel, K.TargetSizeMb).Text);
        var effective = viewModel.EffectiveSettings(viewModel.Items[0]);
        Assert.Equal(50, effective.Number(K.TargetSizeMb));
        Assert.False(effective.Has(K.Crf));
        // a speed of its own is the bar's to decide now: no longer applied unseen
        Assert.False(effective.Has(K.Speed));
    }

    [AvaloniaFact]
    public async Task TypedValuesAreTidiedAndSavedAndPasswordsAreNot()
    {
        using var folder = TestSetup.NewFolder();
        var store = new SettingsStore(folder.File("settings.json"));
        var viewModel = new MainViewModel(store, findEngines: false);
        await viewModel.AddPathsAsync(Files(folder, "photo.png", "notes.txt"));
        viewModel.Items[0].SelectedTarget = "jpg";
        viewModel.Items[1].SelectedTarget = "7z";
        viewModel.DetailedSettingsOpen = true;

        viewModel.SelectedItem = viewModel.Items[0];
        Way(viewModel, SizeModes.Quality);
        Box(viewModel, K.Quality).Text = "250";
        Assert.Equal("100", Box(viewModel, K.Quality).Text);      // out of range → the nearest allowed value
        Box(viewModel, K.MaxWidth).Text = "wide";
        Assert.Equal("", Box(viewModel, K.MaxWidth).Text);        // not a number → automatic
        Assert.False(viewModel.Settings.FormatSettings["jpg"].ContainsKey(K.MaxWidth));

        viewModel.SelectedItem = viewModel.Items[1];
        Box(viewModel, K.Password).Text = "open sesame";
        Way(viewModel, SizeModes.Level);
        Box(viewModel, K.Level).Text = "7";
        Assert.Equal("open sesame", viewModel.EffectiveSettings(viewModel.Items[1]).Text(K.Password));
        viewModel.Flush();

        var saved = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("open sesame", saved);
        Assert.DoesNotContain("password", saved);

        // a new session: the settings are back, the password is not
        var next = new MainViewModel(store, findEngines: false);
        Assert.True(next.DetailedSettingsOpen);
        await next.AddPathsAsync(Files(folder, "photo.png", "notes.txt"));
        next.Items[0].SelectedTarget = "jpg";
        next.Items[1].SelectedTarget = "7z";
        Assert.Equal(100, next.EffectiveSettings(next.Items[0]).Int(K.Quality));
        Assert.Equal(7, next.EffectiveSettings(next.Items[1]).Int(K.Level));
        Assert.False(next.EffectiveSettings(next.Items[1]).Has(K.Password));
    }

    [AvaloniaFact]
    public async Task PresetsFillInAWholeSetAndCanBeSavedAndDeleted()
    {
        using var folder = TestSetup.NewFolder();
        var store = new SettingsStore(folder.File("settings.json"));
        var viewModel = new MainViewModel(store, findEngines: false);
        await viewModel.AddPathsAsync(Files(folder, "clip.mov"));
        viewModel.Items[0].SelectedTarget = "mp4";
        viewModel.DetailedSettingsOpen = true;

        Way(viewModel, SizeModes.Quality);
        Box(viewModel, K.Crf).Text = "30";
        viewModel.Detail.SelectedPreset = viewModel.Detail.Presets.Single(p => p.Name == "Fit in 10 MB");

        // a preset is a whole state: the quality level set before is gone, and the way is the preset's
        Assert.Equal("10", viewModel.Settings.FormatSettings["mp4"][K.TargetSizeMb]);
        Assert.False(viewModel.Settings.FormatSettings["mp4"].ContainsKey(K.Crf));
        Assert.Equal(SizeModes.Size, Choice(viewModel, K.SizeMode).Selected!.Value);
        Assert.Equal("10", Box(viewModel, K.TargetSizeMb).Text);
        Assert.Contains(K.TargetSizeMb, ShownKeys(viewModel));
        Assert.DoesNotContain(K.Crf, ShownKeys(viewModel));

        // changing something by hand: it is no longer "the preset"
        Box(viewModel, K.MaxHeight).Text = "720";
        Assert.Null(viewModel.Detail.SelectedPreset);

        // save what is set now under a name of the user's own
        Assert.False(viewModel.Detail.SavePresetCommand.CanExecute(null));
        viewModel.Detail.NewPresetName = "Phone upload";
        viewModel.Detail.SavePresetCommand.Execute(null);
        var mine = viewModel.Detail.Presets.Single(p => p.Name == "Phone upload");
        Assert.False(mine.IsBuiltIn);
        Assert.Equal("720", mine.Preset.Values[K.MaxHeight]);
        Assert.True(viewModel.Detail.CanDeletePreset);
        viewModel.Flush();

        // it is there in the next session, and can be deleted; built-in ones cannot
        var next = new MainViewModel(store, findEngines: false);
        await next.AddPathsAsync(Files(folder, "clip.mov"));
        next.Items[0].SelectedTarget = "mp4";
        next.SelectedItem = next.Items[0];
        next.Detail.ResetCommand.Execute(null);
        next.Detail.SelectedPreset = next.Detail.Presets.Single(p => p.Name == "Phone upload");
        Assert.Equal("720", next.Settings.FormatSettings["mp4"][K.MaxHeight]);
        next.Detail.DeletePresetCommand.Execute(null);
        Assert.DoesNotContain(next.Detail.Presets, p => p.Name == "Phone upload");
        next.Detail.SelectedPreset = next.Detail.Presets.First();
        Assert.False(next.Detail.CanDeletePreset);
    }

    [AvaloniaFact]
    public async Task SettingsFromThePanelReachTheConversion()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var first = folder.File("one.png");
        var second = folder.File("two.png");
        File.Copy(samples.Png, first);
        File.Copy(samples.Png, second);
        await viewModel.AddPathsAsync([first, second]);
        viewModel.BulkTarget = "jpg";
        viewModel.CombineFiles = false;
        viewModel.DetailedSettingsOpen = true;

        // every JPG at most 200 wide; the first file at most 100
        viewModel.SelectedItem = viewModel.Items[0];
        Box(viewModel, K.MaxWidth).Text = "200";
        viewModel.Detail.OnlyThisFile = true;
        Box(viewModel, K.MaxWidth).Text = "100";

        await viewModel.ConvertAllCommand.ExecuteAsync(null);

        Assert.All(viewModel.Items, item => Assert.Equal(ItemState.Done, item.State));
        Assert.Equal(100u, new MagickImageInfo(viewModel.Items[0].OutputPath!).Width);
        Assert.Equal(200u, new MagickImageInfo(viewModel.Items[1].OutputPath!).Width);
        Assert.Equal("Convert again", viewModel.ConvertButtonText);
    }

    [AvaloniaFact]
    public async Task AnExistingResultIsKeptReplacedOrSkippedAsChosen()
    {
        using var folder = TestSetup.NewFolder();
        var viewModel = NewViewModel(folder);
        var source = folder.File("photo.png");
        File.Copy(samples.Png, source);
        await viewModel.AddPathsAsync([source]);
        viewModel.Items[0].SelectedTarget = "jpg";

        await viewModel.ConvertAllCommand.ExecuteAsync(null);
        Assert.Equal(folder.File("photo.jpg"), viewModel.Items[0].OutputPath);
        var firstWrite = File.GetLastWriteTimeUtc(folder.File("photo.jpg"));

        // keep both: a numbered second file
        await viewModel.ConvertAllCommand.ExecuteAsync(null);
        Assert.Equal(folder.File("photo (1).jpg"), viewModel.Items[0].OutputPath);

        // skip: nothing is written and the row says why
        viewModel.ExistingFileIndex = (int)ExistingFileAction.Skip;
        await viewModel.ConvertAllCommand.ExecuteAsync(null);
        Assert.Equal(ItemState.Skipped, viewModel.Items[0].State);
        Assert.Contains("already exists", viewModel.Items[0].StatusText);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(folder.File("photo.jpg")));
        Assert.Contains("1 skipped", viewModel.OverallStatus);

        // replace: the same name, written again, and no stray temporary file left behind
        viewModel.ExistingFileIndex = (int)ExistingFileAction.Overwrite;
        await Task.Delay(20, TestContext.Current.CancellationToken);
        await viewModel.ConvertAllCommand.ExecuteAsync(null);
        Assert.Equal(ItemState.Done, viewModel.Items[0].State);
        Assert.Equal(folder.File("photo.jpg"), viewModel.Items[0].OutputPath);
        Assert.True(File.GetLastWriteTimeUtc(folder.File("photo.jpg")) > firstWrite);
        Assert.Equal(["photo (1).jpg", "photo.jpg", "photo.png"],
            Directory.GetFiles(folder.Path).Select(Path.GetFileName).Where(n => n != "settings.json").Order(StringComparer.Ordinal));
    }
}
