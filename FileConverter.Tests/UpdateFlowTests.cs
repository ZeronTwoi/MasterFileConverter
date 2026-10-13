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
/// Updating from the window: one click fetches, checks and installs; the app only closes by itself
/// when nothing would be lost; and closing the window never leaves a conversion running. The
/// updater here is a stand-in that notes what it was asked to do — no test goes online, and none
/// replaces anything.
/// </summary>
public class UpdateFlowTests
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
    private static readonly UpdateRoute Route = new("here", "-setup.exe", UpdateKind.Installer);

    private sealed class FakeUpdates : IAppUpdates
    {
        public bool CanUpdate { get; set; } = true;
        public Version? Newest { get; set; } = new(99, 0, 0);
        public Exception? DownloadFails { get; set; }
        public Exception? StartFails { get; set; }

        /// <summary>A download that waits here until the test lets it through.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<string> Asked { get; } = [];

        public Task<AvailableUpdate?> FindAsync(CancellationToken ct)
        {
            Asked.Add("find");
            return Task.FromResult(Newest == null ? null : new AvailableUpdate(Newest, new ReleaseFile("App-setup.exe", "https://example.org/a", "00"), Route));
        }

        public async Task<PreparedUpdate> PrepareAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct)
        {
            Asked.Add("fetch");
            progress?.Report(0.45);
            if (Gate != null) await Gate.Task.WaitAsync(ct);
            if (DownloadFails != null) throw DownloadFails;
            progress?.Report(1);
            return new PreparedUpdate(update.Version, update.Route, "the download");
        }

        public void Start(PreparedUpdate update, bool restart)
        {
            if (StartFails != null) throw StartFails;
            Asked.Add(restart ? "install and restart" : "install");
        }

        public void Discard() => Asked.Add("discard");
    }

    private sealed class Host : IAppHost
    {
        public int Closed;
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public void Close() => Closed++;
    }

    /// <summary>A view model that has been told of version 99.0.0, with the stand-in updater and a host that counts.</summary>
    private static (MainViewModel ViewModel, FakeUpdates Updates, Host Host) NewViewModel(TempFolder folder)
    {
        var updates = new FakeUpdates();
        var host = new Host();
        var viewModel = new MainViewModel(new SettingsStore(folder.File("settings.json")), findEngines: false)
        {
            Updates = updates,
            Host = host,
            FindLatestVersion = _ => Task.FromResult<Version?>(new Version(99, 0, 0)),
            NewVersion = "99.0.0"
        };
        return (viewModel, updates, host);
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

    private static string[] SampleFiles(TempFolder folder, params string[] names)
    {
        foreach (var name in names) File.WriteAllBytes(folder.File(name), new byte[1500]);
        return names.Select(folder.File).ToArray();
    }

    // ---------- one click ----------

    [AvaloniaFact]
    public async Task OneClickFetchesInstallsAndRestartsWhenNothingWouldBeLost()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        Assert.Contains("update now", viewModel.UpdateText);

        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["find", "fetch", "install and restart"], updates.Asked);
        Assert.Equal(1, host.Closed);
        Assert.False(viewModel.UpdateBannerVisible);
    }

    [AvaloniaFact]
    public async Task WithFilesInTheListTheUpdateWaitsToBeAskedFor()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        var window = Show(viewModel);
        viewModel.Host = host;
        await viewModel.AddPathsAsync(SampleFiles(folder, "holiday.jpg", "trailer.mkv"));

        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["find", "fetch"], updates.Asked);
        Assert.Equal(0, host.Closed);
        Assert.True(viewModel.UpdateReady);
        Assert.Contains("99.0.0", viewModel.UpdateTitle);
        Assert.True(viewModel.RestartForUpdateCommand.CanExecute(null));
        Capture(window, "11-update-ready");

        viewModel.RestartForUpdateCommand.Execute(null);

        Assert.Equal(["find", "fetch", "install and restart"], updates.Asked);
        Assert.Equal(1, host.Closed);
    }

    [AvaloniaFact]
    public async Task WhileFilesAreConvertingTheAppIsNotRestarted()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        viewModel.IsConverting = true;

        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.UpdateReady);
        Assert.False(viewModel.RestartForUpdateCommand.CanExecute(null));
        // a second click on the warning sign is no way round that
        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Assert.Equal(["find", "fetch"], updates.Asked);
        Assert.Equal(0, host.Closed);

        viewModel.IsConverting = false;
        Assert.True(viewModel.RestartForUpdateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ClosingTheAppInstallsAnUpdateThatIsReadyWithoutStartingItAgain()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, _) = NewViewModel(folder);
        var window = Show(viewModel);
        await viewModel.AddPathsAsync(SampleFiles(folder, "holiday.jpg"));
        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.UpdateReady);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["find", "fetch", "install"], updates.Asked);
    }

    [AvaloniaFact]
    public async Task AnUpdateThatIsNotWantedAfterAllIsThrownAway()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, _) = NewViewModel(folder);
        var window = Show(viewModel);
        await viewModel.AddPathsAsync(SampleFiles(folder, "holiday.jpg"));
        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        viewModel.DismissUpdateCommand.Execute(null);
        Assert.False(viewModel.UpdateBannerVisible);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["find", "fetch", "discard"], updates.Asked);
        // the newer version is still there to be had
        Assert.True(viewModel.UpdateAvailable);
    }

    // ---------- when it does not go through ----------

    [AvaloniaFact]
    public async Task ADownloadShowsItsProgressAndCanBeCancelled()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        updates.Gate = new TaskCompletionSource();
        var window = Show(viewModel);
        viewModel.Host = host;

        var running = viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.UpdateFetching);
        Assert.Equal(45, viewModel.UpdateProgress, 3);
        Assert.Contains("45%", viewModel.UpdateDetail);
        Capture(window, "10-update-downloading");

        viewModel.CancelUpdateCommand.Execute(null);
        await running;
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.UpdateBannerVisible);
        Assert.Equal(["find", "fetch"], updates.Asked);
        Assert.Equal(0, host.Closed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ADownloadThatFailsIsSaidAndNothingIsInstalled()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        updates.DownloadFails = new FriendlyException("The download is not the file its publisher made (its checksum differs), so it was not used.");
        var window = Show(viewModel);
        viewModel.Host = host;

        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.UpdateFailed);
        Assert.Contains("checksum", viewModel.UpdateDetail);
        Assert.Equal(["find", "fetch"], updates.Asked);
        Assert.Equal(0, host.Closed);
        Capture(window, "12-update-failed");

        viewModel.DismissUpdateCommand.Execute(null);
        Assert.False(viewModel.UpdateBannerVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnInstallThatCannotStartLeavesTheAppOpenAndTheDownloadRemoved()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        updates.StartFails = new FriendlyException("The app is open a second time. Close its other windows, then update again.");

        await viewModel.UpdateNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.UpdateFailed);
        Assert.Contains("open a second time", viewModel.UpdateDetail);
        Assert.Equal(["find", "fetch", "discard"], updates.Asked);
        Assert.Equal(0, host.Closed);
    }

    [AvaloniaFact]
    public async Task WhenTheNewestVersionIsThisOneTheSignGoesAway()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, host) = NewViewModel(folder);
        updates.Newest = null;

        await viewModel.UpdateNowCommand.ExecuteAsync(null);

        Assert.False(viewModel.UpdateAvailable);
        Assert.False(viewModel.UpdateBannerVisible);
        Assert.Equal(["find"], updates.Asked);
        Assert.Equal(0, host.Closed);
    }

    [AvaloniaFact]
    public void ACopyThatCannotReplaceItselfPointsToTheDownloadPage()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, updates, _) = NewViewModel(folder);
        updates.CanUpdate = false;

        Assert.Contains("open the download page", viewModel.UpdateText);
    }

    // ---------- after the restart ----------

    [AvaloniaFact]
    public void TheAppSaysWhenItHasJustBeenUpdatedAndWhyAnUpdateFailed()
    {
        using var folder = TestSetup.NewFolder();
        var (updated, _, _) = NewViewModel(folder);
        updated.AfterStart(updatedTo: "2.5.0", updateFailure: null);
        Assert.Equal("Updated to version 2.5.0.", updated.OverallStatus);
        Assert.False(updated.UpdateBannerVisible);

        var (failed, _, _) = NewViewModel(folder);
        failed.AfterStart(updatedTo: null, updateFailure: "the app did not close.");
        Assert.True(failed.UpdateFailed);
        Assert.Equal("the app did not close.", failed.UpdateDetail);

        var (asEver, _, _) = NewViewModel(folder);
        asEver.AfterStart(updatedTo: null, updateFailure: null);
        Assert.Equal("", asEver.OverallStatus);
        Assert.False(asEver.UpdateBannerVisible);
    }

    // ---------- a look asked for by hand ----------

    [AvaloniaFact]
    public async Task ALookAskedForFromTheMenuWorksWithoutTheNoticesSwitchedOn()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, _, _) = NewViewModel(folder);
        viewModel.NewVersion = null;
        Assert.False(viewModel.NotifyAboutUpdates);

        await viewModel.LookForUpdateNowCommand.ExecuteAsync(null);
        Assert.Equal("99.0.0", viewModel.NewVersion);
        Assert.Contains("99.0.0", viewModel.OverallStatus);
        Assert.False(viewModel.NotifyAboutUpdates); // one look is not a standing yes

        viewModel.NewVersion = null;
        viewModel.FindLatestVersion = _ => Task.FromResult<Version?>(Version.Parse(AppInfo.Version.Split('-')[0]));
        await viewModel.LookForUpdateNowCommand.ExecuteAsync(null);
        Assert.Null(viewModel.NewVersion);
        Assert.Contains("newest version", viewModel.OverallStatus);

        viewModel.FindLatestVersion = _ => Task.FromResult<Version?>(null);
        await viewModel.LookForUpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("could not be looked up", viewModel.OverallStatus);
    }

    // ---------- closing on a running batch ----------

    [AvaloniaFact]
    public void ClosingTheWindowOnARunningBatchAsksFirstAndNoKeepsItOpen()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, _, _) = NewViewModel(folder);
        var window = Show(viewModel);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        int asked = 0;
        bool answer = false;
        window.ConfirmStop = () =>
        {
            asked++;
            return Task.FromResult(answer);
        };
        viewModel.IsConverting = true;

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, asked);
        Assert.False(closed);

        answer = true;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, asked);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void AnIdleWindowClosesWithoutAQuestion()
    {
        using var folder = TestSetup.NewFolder();
        var (viewModel, _, _) = NewViewModel(folder);
        var window = Show(viewModel);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.ConfirmStop = () => throw new InvalidOperationException("nothing to ask about");

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
    }

    [AvaloniaFact]
    public void TheQuestionOnClosingLooksLikeThis()
    {
        var dialog = new ConfirmDialog(
            "Files are still being converted",
            "Closing now stops them. What is finished stays; a file that is not finished yet is not kept.",
            yes: "Stop and close", no: "Keep converting");
        dialog.Show();
        Capture(dialog, "13-close-question");
        dialog.Close();
    }
}
