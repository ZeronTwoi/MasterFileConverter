using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FileConverter.Core;
using FileConverter.Core.Settings;
using FileConverter.ViewModels;
using FileConverter.Views;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// Being told about new versions: asked once, never connected for without a yes, looked up once
/// per start with one. No test here goes online — each puts its own answer in place of the lookup.
/// </summary>
public class UpdateNoticeTests
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    [Theory]
    [InlineData("https://github.com/someone/app/releases/tag/v2.3.0", "2.3.0")]
    [InlineData("https://github.com/someone/app/releases/tag/2.3", "2.3")]
    [InlineData("https://github.com/someone/app/releases/tag/v10.0.1/", "10.0.1")]
    [InlineData("https://github.com/someone/app/releases/tag/v2.3.0-beta.1", "2.3.0")]
    // no release yet: the address of the list of releases
    [InlineData("https://github.com/someone/app/releases", null)]
    [InlineData("https://github.com/someone/app/releases/tag/nightly", null)]
    [InlineData("https://github.com/login?return_to=%2Fsomeone%2Fapp", null)]
    [InlineData(null, null)]
    public void TheVersionIsReadFromTheAddressOfTheLatestRelease(string? address, string? version)
    {
        Assert.Equal(version, UpdateCheck.VersionInAddress(address)?.ToString());
    }

    [Theory]
    [InlineData("2.3.0", "2.2.0", true)]
    [InlineData("2.10.0", "2.9.0", true)]
    [InlineData("3.0", "2.9.9-beta", true)]
    [InlineData("2.2.0", "2.2.0", false)]
    [InlineData("2.2", "2.2.0", false)]
    [InlineData("2.1.9", "2.2.0", false)]
    [InlineData("2.3.0", "not a version", false)]
    public void OnlyANewerVersionCounts(string latest, string mine, bool newer)
    {
        Assert.Equal(newer, UpdateCheck.IsNewer(Version.Parse(latest), mine));
    }

    /// <summary>A view model whose "lookup" answers <paramref name="latest"/> and counts how often it was asked.</summary>
    private static MainViewModel NewViewModel(TempFolder folder, string? latest, Counter asked)
    {
        var viewModel = new MainViewModel(new SettingsStore(folder.File("settings.json")), findEngines: false);
        viewModel.FindLatestVersion = _ =>
        {
            asked.Count++;
            return Task.FromResult(latest == null ? null : Version.Parse(latest));
        };
        return viewModel;
    }

    private sealed class Counter
    {
        public int Count;
    }

    private static MainWindow Show(MainViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1100, Height = 720 };
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

    [AvaloniaFact]
    public void TheQuestionIsPutOnceAndNoMeansNoConnectionEver()
    {
        using var folder = TestSetup.NewFolder();
        var asked = new Counter();
        var viewModel = NewViewModel(folder, "99.0.0", asked);
        var window = Show(viewModel);

        viewModel.BeginUpdateNotices();
        Assert.True(viewModel.AskAboutUpdates);
        Assert.Equal(0, asked.Count); // not before an answer
        Capture(window, "08-update-question");

        viewModel.AnswerUpdateQuestionCommand.Execute("no");
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AskAboutUpdates);
        Assert.False(viewModel.NotifyAboutUpdates);
        Assert.False(viewModel.UpdateAvailable);
        Assert.Equal(0, asked.Count);
        viewModel.Flush();
        window.Close();

        // the next start: not asked again, and still nothing looked up
        var next = NewViewModel(folder, "99.0.0", asked);
        next.BeginUpdateNotices();
        Dispatcher.UIThread.RunJobs();
        Assert.False(next.AskAboutUpdates);
        Assert.False(next.UpdateAvailable);
        Assert.Equal(0, asked.Count);
    }

    [AvaloniaFact]
    public void YesLooksOncePerStartAndShowsTheWarningSign()
    {
        using var folder = TestSetup.NewFolder();
        var asked = new Counter();
        var viewModel = NewViewModel(folder, "99.0.0", asked);
        var window = Show(viewModel);
        viewModel.BeginUpdateNotices();

        viewModel.AnswerUpdateQuestionCommand.Execute("yes");
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.AskAboutUpdates);
        Assert.True(viewModel.NotifyAboutUpdates);
        Assert.Equal(1, asked.Count);
        Assert.True(viewModel.UpdateAvailable);
        Assert.Contains("99.0.0", viewModel.UpdateText);
        Capture(window, "09-update-available");

        // switched off in the menu: the sign goes; switched on again: no second look in this start
        viewModel.ToggleUpdateNoticesCommand.Execute(null);
        Assert.False(viewModel.UpdateAvailable);
        viewModel.ToggleUpdateNoticesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.NotifyAboutUpdates);
        Assert.Equal(1, asked.Count);
        viewModel.Flush();
        window.Close();

        // the next start looks again, once, without asking the user anything
        var next = NewViewModel(folder, "99.0.0", asked);
        next.BeginUpdateNotices();
        Dispatcher.UIThread.RunJobs();
        Assert.False(next.AskAboutUpdates);
        Assert.Equal(2, asked.Count);
        Assert.True(next.UpdateAvailable);
    }

    [AvaloniaTheory]
    [InlineData(null)]     // offline, or no answer
    [InlineData("0.0.1")]  // nothing newer than this version
    public void WithoutANewerVersionNothingIsShown(string? latest)
    {
        using var folder = TestSetup.NewFolder();
        var asked = new Counter();
        var viewModel = NewViewModel(folder, latest, asked);
        viewModel.BeginUpdateNotices();
        viewModel.AnswerUpdateQuestionCommand.Execute("yes");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, asked.Count);
        Assert.False(viewModel.UpdateAvailable);
    }
}
