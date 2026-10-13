using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FileConverter.Core;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// Replacing the app with a newer version: which of a release's files is taken, that only what
/// matches its checksum is used, and that putting it in place either happens in full or not at
/// all. Nothing here goes online or touches the copy of the app that runs the tests.
/// </summary>
public class AppUpdaterTests
{
    private const string Feed = "https://api.github.com/repos/someone/app/releases/latest";
    private const string Sha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    private static readonly UpdateRoute Installed = new(@"C:\Programs\App", "-win-x64-setup.exe", UpdateKind.Installer);
    private static readonly UpdateRoute Unzipped = new(@"D:\Tools\App", "-win-x64.zip", UpdateKind.Archive);
    private static readonly UpdateRoute OnLinux = new("/home/me/.local/lib/app", "-linux-x64.tar.gz", UpdateKind.Archive);

    private static ReleaseFile[] Release(string version, string? sha = Sha, string scheme = "https") =>
    [
        new($"App-{version}-linux-x64.tar.gz", $"{scheme}://example.org/App-{version}-linux-x64.tar.gz", sha),
        new($"App-{version}-win-x64-setup.exe", $"{scheme}://example.org/App-{version}-win-x64-setup.exe", sha),
        new($"App-{version}-win-x64.zip", $"{scheme}://example.org/App-{version}-win-x64.zip", sha)
    ];

    // ---------- which file ----------

    [Fact]
    public void EachKindOfCopyGetsItsOwnFileOfTheRelease()
    {
        var files = Release("2.5.0");

        Assert.Equal("App-2.5.0-win-x64-setup.exe", AppUpdater.Choose("v2.5.0", files, "2.4.0", "App", Installed, Feed)!.File.Name);
        Assert.Equal("App-2.5.0-win-x64.zip", AppUpdater.Choose("v2.5.0", files, "2.4.0", "App", Unzipped, Feed)!.File.Name);

        var linux = AppUpdater.Choose("v2.5.0", files, "2.4.0", "App", OnLinux, Feed)!;
        Assert.Equal("App-2.5.0-linux-x64.tar.gz", linux.File.Name);
        Assert.Equal(new Version(2, 5, 0), linux.Version);
        Assert.Same(OnLinux, linux.Route);
    }

    [Theory]
    [InlineData("v2.4.0")] // this very version
    [InlineData("v2.3.9")] // an older one
    [InlineData("nightly")]
    [InlineData(null)]
    public void WithoutANewerReleaseThereIsNothingToFetch(string? tag)
    {
        Assert.Null(AppUpdater.Choose(tag, Release("2.4.0"), "2.4.0", "App", Installed, Feed));
    }

    [Fact]
    public void AnotherProgramsFileIsNeverTaken()
    {
        // the same release address, but the files carry another name: not this app's
        var refused = Assert.Throws<FriendlyException>(() => AppUpdater.Choose("v2.5.0", Release("2.5.0"), "2.4.0", "OtherApp", Installed, Feed));
        Assert.Contains("no download", refused.Message);
    }

    [Fact]
    public void AFileWithoutAChecksumIsNotFetched()
    {
        var refused = Assert.Throws<FriendlyException>(() => AppUpdater.Choose("v2.5.0", Release("2.5.0", sha: null), "2.4.0", "App", Installed, Feed));
        Assert.Contains("checksum", refused.Message);
    }

    [Fact]
    public void AFileOfferedLessProtectedThanTheAnswerNamingItIsNotFetched()
    {
        var refused = Assert.Throws<FriendlyException>(() => AppUpdater.Choose("v2.5.0", Release("2.5.0", scheme: "http"), "2.4.0", "App", Installed, Feed));
        Assert.Contains("protected connection", refused.Message);
    }

    [Theory]
    [InlineData("App-2.5.0-win-x64-setup.exe", "2.5.0")]
    [InlineData("App-2.5-win-x64-setup.exe", "2.5")]
    [InlineData("App-2.5.0-beta.1-win-x64-setup.exe", "2.5.0")]
    // this app's download, but for another kind of copy; another app's; no version; a renamed file
    [InlineData("App-2.5.0-win-x64.zip", null)]
    [InlineData("OtherApp-2.5.0-win-x64-setup.exe", null)]
    [InlineData("App-win-x64-setup.exe", null)]
    [InlineData("setup.exe", null)]
    public void ADownloadGivenByHandHasToBeThisCopysByItsName(string fileName, string? version)
    {
        Assert.Equal(version, AppUpdater.VersionOf(fileName, "App", Installed)?.ToString());
    }

    [Fact]
    public void AReleaseIsReadFromWhatGitHubAnswers()
    {
        var (tag, files) = Downloads.ReadRelease($$"""
            { "tag_name": "v2.5.0", "assets": [
                { "name": "App-2.5.0-win-x64.zip", "browser_download_url": "https://example.org/a.zip", "digest": "sha256:{{Sha}}" },
                { "name": "notes.txt", "browser_download_url": "https://example.org/notes.txt", "digest": null } ] }
            """);

        Assert.Equal("v2.5.0", tag);
        Assert.Equal(2, files.Count);
        Assert.Equal(Sha, files[0].Sha256);
        Assert.Null(files[1].Sha256);
    }

    // ---------- fetching ----------

    /// <summary>A copy of "the app" as a download holds it: the launcher on top, the program in "app".</summary>
    private static string MakeApp(string folder, string version)
    {
        Directory.CreateDirectory(Path.Combine(folder, "app", "licenses"));
        File.WriteAllText(Path.Combine(folder, "launcher"), "launcher " + version);
        File.WriteAllText(Path.Combine(folder, "app", "program.dll"), "program " + version);
        File.WriteAllText(Path.Combine(folder, "app", "licenses", "LICENSE.txt"), "licence " + version);
        return folder;
    }

    private static string Snapshot(string folder) => string.Join("\n",
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => $"{Path.GetRelativePath(folder, file).Replace('\\', '/')}={File.ReadAllText(file)}"));

    private static byte[] ZipOf(string folder, string top)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, top + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
        return memory.ToArray();
    }

    private static byte[] TarGzOf(string folder, string top)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                tar.WriteEntry(file, top + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
        return memory.ToArray();
    }

    /// <summary>Hands out one file over HTTP on this machine, as often as it is asked for.</summary>
    private sealed class OneFileServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public string Url { get; }

        public OneFileServer(string name, byte[] content)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{name}";
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        await using var stream = client.GetStream();
                        // the request, up to the empty line that ends it
                        var request = new StringBuilder();
                        var one = new byte[1];
                        while (!request.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(one, _stop.Token) == 1)
                            request.Append((char)one[0]);

                        var head = $"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                        await stream.WriteAsync(content, _stop.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                    {
                        // the test is over, or a client went away
                    }
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static AvailableUpdate UpdateFrom(OneFileServer server, byte[] content, UpdateRoute route, string? sha = null) =>
        new(new Version(2, 5, 0),
            new ReleaseFile(Path.GetFileName(new Uri(server.Url).LocalPath), server.Url, sha ?? Convert.ToHexString(SHA256.HashData(content))),
            route);

    [Theory]
    [InlineData(".zip")]
    [InlineData(".tar.gz")]
    public async Task AnArchiveIsDownloadedCheckedAndUnpacked(string kind)
    {
        using var folder = TestSetup.NewFolder();
        var app = MakeApp(folder.File("made"), "2.5.0");
        var content = kind == ".zip" ? ZipOf(app, "App-2.5.0") : TarGzOf(app, "App-2.5.0");
        using var server = new OneFileServer("App-2.5.0" + kind, content);
        var work = Directory.CreateDirectory(folder.File("update")).FullName;
        File.WriteAllText(Path.Combine(work, "left-from-last-time.tmp"), "old");
        double reported = 0;

        var prepared = await AppUpdater.FetchAsync(UpdateFrom(server, content, new UpdateRoute(folder.File("here"), kind, UpdateKind.Archive)),
            work, "launcher", new InlineProgress(p => reported = p), CancellationToken.None);

        Assert.Equal(Path.Combine(work, "new", "App-2.5.0"), prepared.Path);
        Assert.Equal(Snapshot(app), Snapshot(prepared.Path));
        Assert.Equal(1, reported);
        // the archive itself is gone again, and so is what an earlier update left
        Assert.Equal(new[] { "new" }, Directory.EnumerateFileSystemEntries(work).Select(entry => Path.GetFileName(entry)));
    }

    [Fact]
    public async Task AnInstallerIsDownloadedAndCheckedButNotRun()
    {
        using var folder = TestSetup.NewFolder();
        var content = Encoding.ASCII.GetBytes("not really an installer");
        using var server = new OneFileServer("App-2.5.0-win-x64-setup.exe", content);
        var work = Directory.CreateDirectory(folder.File("update")).FullName;

        var prepared = await AppUpdater.FetchAsync(UpdateFrom(server, content, Installed), work, "launcher", null, CancellationToken.None);

        Assert.Equal(Path.Combine(work, "App-2.5.0-win-x64-setup.exe"), prepared.Path);
        Assert.Equal(content, File.ReadAllBytes(prepared.Path));
    }

    [Fact]
    public async Task ADownloadThatIsNotWhatWasPublishedIsDeletedAndRefused()
    {
        using var folder = TestSetup.NewFolder();
        var content = Encoding.ASCII.GetBytes("changed on the way");
        using var server = new OneFileServer("App-2.5.0-win-x64-setup.exe", content);
        var work = Directory.CreateDirectory(folder.File("update")).FullName;

        await Assert.ThrowsAsync<FriendlyException>(() =>
            AppUpdater.FetchAsync(UpdateFrom(server, content, Installed, sha: Sha), work, "launcher", null, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task AnArchiveThatDoesNotHoldTheAppIsRefused()
    {
        using var folder = TestSetup.NewFolder();
        var other = Directory.CreateDirectory(folder.File("made")).FullName;
        File.WriteAllText(Path.Combine(other, "readme.txt"), "something else");
        var content = ZipOf(other, "App-2.5.0");
        using var server = new OneFileServer("App-2.5.0.zip", content);
        var work = Directory.CreateDirectory(folder.File("update")).FullName;

        var refused = await Assert.ThrowsAsync<FriendlyException>(() =>
            AppUpdater.FetchAsync(UpdateFrom(server, content, Unzipped), work, "launcher", null, CancellationToken.None));

        Assert.Contains("does not hold the app", refused.Message);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // ---------- putting in place ----------

    [Fact]
    public void TheNewCopyTakesTheOldOnesPlaceAndNothingElseIsTouched()
    {
        using var folder = TestSetup.NewFolder();
        var staged = MakeApp(folder.File("staged"), "2.5.0");
        var target = MakeApp(folder.File("target"), "2.4.0");
        // gone in the new version, and must not outlive the old one
        File.WriteAllText(Path.Combine(target, "app", "dropped.dll"), "old");
        // the user's own, beside the app: a folder unzipped into Downloads, say
        File.WriteAllText(Path.Combine(target, "holiday.jpg"), "a picture");
        Directory.CreateDirectory(Path.Combine(target, "my files"));
        File.WriteAllText(Path.Combine(target, "my files", "notes.txt"), "notes");

        AppUpdater.Replace(staged, target);

        var expected = Snapshot(staged).Split('\n').Append("holiday.jpg=a picture").Append("my files/notes.txt=notes");
        Assert.Equal(expected.Order(StringComparer.Ordinal), Snapshot(target).Split('\n').Order(StringComparer.Ordinal));
        // and nothing of the swap is left
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(target), entry => entry.EndsWith(".old") || entry.EndsWith(".new"));
        // the copy it came from is whole, to be removed by whoever unpacked it
        Assert.Equal("program 2.5.0", File.ReadAllText(Path.Combine(staged, "app", "program.dll")));
    }

    [Fact]
    public void ACopyWithAFileTheOldOneDidNotHaveGetsItToo()
    {
        using var folder = TestSetup.NewFolder();
        var staged = MakeApp(folder.File("staged"), "2.5.0");
        File.WriteAllText(Path.Combine(staged, "install.sh"), "new script");
        var target = MakeApp(folder.File("target"), "2.4.0");

        AppUpdater.Replace(staged, target);

        Assert.Equal("new script", File.ReadAllText(Path.Combine(target, "install.sh")));
        Assert.Equal(Snapshot(staged), Snapshot(target));
    }

    [Fact]
    public void WhatASwapThatFailedLeftBehindDoesNotStopTheNextOne()
    {
        using var folder = TestSetup.NewFolder();
        var staged = MakeApp(folder.File("staged"), "2.5.0");
        var target = MakeApp(folder.File("target"), "2.4.0");
        Directory.CreateDirectory(Path.Combine(target, "app.old"));
        File.WriteAllText(Path.Combine(target, "app.old", "stale.dll"), "older still");
        File.WriteAllText(Path.Combine(target, "launcher.new"), "half a copy");

        AppUpdater.Replace(staged, target);

        Assert.Equal(Snapshot(staged), Snapshot(target));
    }

    [Fact]
    public void WhenTheOldCopyCannotBeMovedAsideItIsLeftExactlyAsItWas()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows refuses to rename a folder with a file open in it");

        using var folder = TestSetup.NewFolder();
        var staged = MakeApp(folder.File("staged"), "2.5.0");
        var target = MakeApp(folder.File("target"), "2.4.0");
        var before = Snapshot(target);

        // the app still running: one of its files is open
        using (File.Open(Path.Combine(target, "app", "program.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => AppUpdater.Replace(staged, target, tries: 2));

        Assert.Equal(before, Snapshot(target));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(target), entry => entry.EndsWith(".old") || entry.EndsWith(".new"));
    }

    [Fact]
    public void AFailureAfterTheFirstNameIsUndoneForThatOneToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows refuses to rename a file that is open");

        using var folder = TestSetup.NewFolder();
        var staged = MakeApp(folder.File("staged"), "2.5.0");
        var target = MakeApp(folder.File("target"), "2.4.0");
        var before = Snapshot(target);

        // "app" goes first and is swapped; then the launcher cannot be moved aside
        using (File.Open(Path.Combine(target, "launcher"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => AppUpdater.Replace(staged, target, tries: 2));

        Assert.Equal(before, Snapshot(target));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(target), entry => entry.EndsWith(".old") || entry.EndsWith(".new"));
    }

    // ---------- what became of it ----------

    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The two notes an update leaves, as the start after it finds them.</summary>
    private static (string Pending, string Failure) Notes(TempFolder folder, string? pending, string? failure, int minutesAgo = 0)
    {
        var notes = (Pending: folder.File("update-pending.txt"), Failure: folder.File("update-failed.txt"));
        if (pending != null)
        {
            File.WriteAllText(notes.Pending, pending);
            File.SetLastWriteTimeUtc(notes.Pending, Now.AddMinutes(-minutesAgo));
        }
        if (failure != null) File.WriteAllText(notes.Failure, failure);
        return notes;
    }

    [Fact]
    public void TheStartAfterAnUpdateSaysSoOnce()
    {
        using var folder = TestSetup.NewFolder();
        var notes = Notes(folder, pending: "2.5.0", failure: null);

        Assert.Equal(("2.5.0", null), AppUpdater.TakeOutcome(notes.Pending, notes.Failure, "2.5.0", Now));
        Assert.Equal((null, null), AppUpdater.TakeOutcome(notes.Pending, notes.Failure, "2.5.0", Now));
        Assert.False(File.Exists(notes.Pending));
    }

    [Fact]
    public void AnInstallerThatNeverFinishedIsSaidOnceItHasHadItsTime()
    {
        using var folder = TestSetup.NewFolder();

        // started a moment ago, and the old version is running again already: it may still be at work
        var early = Notes(folder, pending: "2.5.0", failure: null, minutesAgo: 1);
        Assert.Equal((null, null), AppUpdater.TakeOutcome(early.Pending, early.Failure, "2.4.0", Now));
        Assert.True(File.Exists(early.Pending));

        var late = Notes(folder, pending: "2.5.0", failure: null, minutesAgo: 10);
        var (updatedTo, failure) = AppUpdater.TakeOutcome(late.Pending, late.Failure, "2.4.0", Now);
        Assert.Null(updatedTo);
        Assert.Contains("2.5.0", failure);
        Assert.Contains("did not finish", failure);
        // said once
        Assert.Equal((null, null), AppUpdater.TakeOutcome(late.Pending, late.Failure, "2.4.0", Now));
    }

    [Fact]
    public void TheReasonTheNewCopyWroteDownIsPassedOnAtOnce()
    {
        using var folder = TestSetup.NewFolder();
        var notes = Notes(folder, pending: "2.5.0", failure: "the app did not close.", minutesAgo: 0);

        Assert.Equal((null, "the app did not close."), AppUpdater.TakeOutcome(notes.Pending, notes.Failure, "2.4.0", Now));
        Assert.False(File.Exists(notes.Pending));
        Assert.False(File.Exists(notes.Failure));
    }

    [Fact]
    public void WithoutAnUpdateBehindItAStartSaysNothing()
    {
        using var folder = TestSetup.NewFolder();
        var notes = Notes(folder, pending: null, failure: null);

        Assert.Equal((null, null), AppUpdater.TakeOutcome(notes.Pending, notes.Failure, "2.4.0", Now));
    }

    // ---------- work files ----------

    [Fact]
    public void WorkFilesCarryTheAppsNameAndOldOnesAreClearedAway()
    {
        Assert.StartsWith(AppInfo.ProgramName + "-pack-", Path.GetFileName(WorkFiles.NewPath("pack")));
        Assert.EndsWith(".png", WorkFiles.NewPath("palette", ".png"));
        Assert.NotEqual(WorkFiles.NewPath("pack"), WorkFiles.NewPath("pack"));

        using var folder = TestSetup.NewFolder();
        var longAgo = DateTime.UtcNow.AddDays(-10);
        var oldFile = folder.File(AppInfo.ProgramName + "-palette-1.png");
        var oldFolder = Directory.CreateDirectory(folder.File(AppInfo.ProgramName + "-unpack-2")).FullName;
        var inUse = folder.File(AppInfo.ProgramName + "-pass-3");
        var someoneElses = folder.File("another-program.tmp");
        File.WriteAllText(oldFile, "x");
        File.WriteAllText(Path.Combine(oldFolder, "inside.bin"), "x");
        File.WriteAllText(inUse, "x");
        File.WriteAllText(someoneElses, "x");
        File.SetLastWriteTimeUtc(oldFile, longAgo);
        Directory.SetLastWriteTimeUtc(oldFolder, longAgo);
        File.SetLastWriteTimeUtc(someoneElses, longAgo);

        WorkFiles.RemoveLeftovers(folder.Path, TimeSpan.FromDays(3));

        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(oldFolder));
        Assert.True(File.Exists(inUse));
        Assert.True(File.Exists(someoneElses));
    }
}
