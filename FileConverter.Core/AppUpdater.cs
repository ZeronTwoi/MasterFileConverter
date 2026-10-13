using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using FileConverter.Core.Platform;

namespace FileConverter.Core;

/// <summary>What a newer version is put in place with.</summary>
public enum UpdateKind
{
    /// <summary>The Windows installer, run without its pages: it knows what an installation consists of.</summary>
    Installer,

    /// <summary>The download itself (.zip, .tar.gz): unpacked, and the new copy then moves in where the old one lies.</summary>
    Archive
}

/// <summary>
/// How this copy of the app can be replaced by a newer one: the folder it lies in (what a person
/// starts on top, the program in "app" beside it) and which file of a release is the one for it.
/// </summary>
public sealed record UpdateRoute(string Folder, string FileEnding, UpdateKind Kind);

/// <summary>A newer version, and the file of its release this copy of the app is updated with.</summary>
public sealed record AvailableUpdate(Version Version, ReleaseFile File, UpdateRoute Route);

/// <summary>A newer version, downloaded and checked: the installer, or the folder the archive was unpacked into.</summary>
public sealed record PreparedUpdate(Version Version, UpdateRoute Route, string Path);

/// <summary>
/// Replaces the app with a newer version — when the user asks for it, never by itself.
///
/// GitHub is asked for the newest release and its files. The one for this copy (the installer for
/// an installed copy on Windows, otherwise the download as an archive) is fetched into the app's
/// own data folder and has to have the SHA-256 checksum GitHub states for it. An installer then
/// runs without its pages. An archive is unpacked, and the new copy is started to put itself where
/// the old one lies as soon as that has closed: a program cannot replace its own files while it
/// runs. The old version stays whole until the new one lies complete beside it, and is put back if
/// anything fails after that.
/// </summary>
public static class AppUpdater
{
    /// <summary>What the old copy says to the new one to make it put itself in place (never typed by a person).</summary>
    public const string ApplyCommand = "--apply-update";

    private static IPlatform Os => PlatformServices.Current;

    private static string WorkFolder => Path.Combine(Os.DataDirectory, "update");
    private static string LockFile => Path.Combine(WorkFolder, ".lock");
    private static string FailureNote => Path.Combine(Os.DataDirectory, "update-failed.txt");
    private static string PendingNote => Path.Combine(Os.DataDirectory, "update-pending.txt");

    /// <summary>Can this copy of the app replace itself with a newer one? (A build run out of its build folder cannot.)</summary>
    public static bool CanUpdate => Os.UpdateRoute != null;

    /// <summary>
    /// The folder this copy of the app lies in, if it is laid out the way a download is: the
    /// launcher on top, the program in "app" beside it. Null for anything else.
    /// </summary>
    public static string? FolderOfThisCopy(string launcherName)
    {
        var app = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Path.GetFileName(app) != "app" || Path.GetDirectoryName(app) is not { } folder) return null;
        return File.Exists(Path.Combine(folder, launcherName)) ? folder : null;
    }

    // ---------- finding ----------

    /// <summary>The newest release, if it is newer than this version; null when this is the newest.</summary>
    public static async Task<AvailableUpdate?> FindAsync(CancellationToken ct)
    {
        var route = Os.UpdateRoute
            ?? throw new FriendlyException("This copy of the app cannot update itself. The download page has the newest version.");
        try
        {
            var (tag, files) = Downloads.ReadRelease(await Downloads.GetStringAsync(AppInfo.ReleaseFeed, ct));
            return Choose(tag, files, AppInfo.Version, AppInfo.ProgramName, route, AppInfo.ReleaseFeed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException or JsonException
                                       or KeyNotFoundException or InvalidOperationException
                                   || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FriendlyException("The newest version could not be looked up: " + ex.Message);
        }
    }

    /// <summary>
    /// Picks this copy's file out of a release — "&lt;name&gt;-&lt;version&gt;-win-x64-setup.exe": the
    /// app's own, for this system and this kind of copy. A file without a stated checksum, or one
    /// that would be fetched less protected than the answer naming it, is not taken.
    /// </summary>
    internal static AvailableUpdate? Choose(string? tag, IReadOnlyList<ReleaseFile> files, string currentVersion,
        string programName, UpdateRoute route, string feed)
    {
        if (UpdateCheck.VersionInTag(tag) is not { } version || !UpdateCheck.IsNewer(version, currentVersion)) return null;

        var file = files.FirstOrDefault(f =>
            f.Name.StartsWith(programName + "-", StringComparison.Ordinal) &&
            f.Name.EndsWith(route.FileEnding, StringComparison.Ordinal) &&
            f.Name == Path.GetFileName(f.Name));
        if (file.Name == null)
            throw new FriendlyException($"Version {version} has no download for this copy of the app.");
        if (file.Sha256 == null)
            throw new FriendlyException($"The download of version {version} comes without a checksum to check it against, so it was not fetched.");
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps && new Uri(feed).Scheme == Uri.UriSchemeHttps)
            throw new FriendlyException($"The download of version {version} is not offered over a protected connection, so it was not fetched.");

        return new AvailableUpdate(version, file, route);
    }

    // ---------- fetching ----------

    private static FileStream? _hold;

    /// <summary>
    /// Downloads the update and checks it; an archive is unpacked as well. Nothing of the running
    /// app is touched yet. Whatever was fetched is removed again if this fails or is cancelled.
    /// </summary>
    public static async Task<PreparedUpdate> PrepareAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        // said before the download, not after it
        if (RunsElsewhere(update.Route.Folder)) throw new FriendlyException(RunningTwice);
        try
        {
            Directory.CreateDirectory(WorkFolder);
            // one copy of the app at a time; and a copy that starts meanwhile leaves the folder alone
            _hold ??= new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FriendlyException("An update is already being fetched by another window of the app.");
        }

        try
        {
            return await FetchAsync(update, WorkFolder, Os.LauncherName, progress, ct);
        }
        catch
        {
            Discard();
            throw;
        }
    }

    /// <summary>
    /// The same from a file that is already on the computer — the download for this copy, fetched
    /// by hand, for a machine that is not online. Whoever gives it vouches for it: there is no
    /// checksum to check it against. It has to carry its published name, which says what it is for.
    /// </summary>
    public static async Task<PreparedUpdate> PrepareFromFileAsync(string file, CancellationToken ct)
    {
        var route = Os.UpdateRoute
            ?? throw new FriendlyException("This copy of the app cannot update itself. Unpack or install the download in its place.");
        if (!File.Exists(file)) throw new FriendlyException("There is no file " + file);
        var version = VersionOf(Path.GetFileName(file), AppInfo.ProgramName, route)
            ?? throw new FriendlyException(
                $"This copy of the app is updated with the download named {AppInfo.ProgramName}-<version>{route.FileEnding}; {Path.GetFileName(file)} is not that.");
        if (RunsElsewhere(route.Folder)) throw new FriendlyException(RunningTwice);

        // an installer runs from where it lies; an archive is unpacked into the data folder
        if (route.Kind == UpdateKind.Installer) return new PreparedUpdate(version, route, Path.GetFullPath(file));
        try
        {
            Directory.CreateDirectory(WorkFolder);
            _hold ??= new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FriendlyException("An update is already being fetched by another window of the app.");
        }

        try
        {
            Clear(WorkFolder);
            var unpacked = Path.Combine(WorkFolder, "new");
            await Task.Run(() => Unpack(file, unpacked), ct);
            return new PreparedUpdate(version, route, AppIn(unpacked, Os.LauncherName));
        }
        catch (Exception ex)
        {
            Discard();
            if (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                throw new FriendlyException($"{Path.GetFileName(file)} could not be unpacked: {ex.Message}");
            throw;
        }
    }

    /// <summary>The version in a download's name — "&lt;name&gt;-2.5.0-linux-x64.tar.gz" — if it is this copy's kind of download.</summary>
    internal static Version? VersionOf(string fileName, string programName, UpdateRoute route)
    {
        if (!fileName.StartsWith(programName + "-", StringComparison.Ordinal) ||
            !fileName.EndsWith(route.FileEnding, StringComparison.Ordinal) ||
            fileName.Length <= programName.Length + 1 + route.FileEnding.Length) return null;
        var middle = fileName[(programName.Length + 1)..^route.FileEnding.Length];
        return Version.TryParse(middle.Split('-', '+')[0], out var version) ? version : null;
    }

    /// <summary>The download, the check and the unpacking, into <paramref name="folder"/>.</summary>
    internal static async Task<PreparedUpdate> FetchAsync(AvailableUpdate update, string folder, string launcherName,
        IProgress<double>? progress, CancellationToken ct)
    {
        Clear(folder);
        var file = Path.Combine(folder, update.File.Name);
        await Downloads.DownloadFileAsync(update.File, file, progress, ct);
        if (update.Route.Kind == UpdateKind.Installer) return new PreparedUpdate(update.Version, update.Route, file);

        var unpacked = Path.Combine(folder, "new");
        await Task.Run(() => Unpack(file, unpacked), ct);
        File.Delete(file);
        return new PreparedUpdate(update.Version, update.Route, AppIn(unpacked, launcherName));
    }

    /// <summary>Removes an update that was fetched and is not wanted after all.</summary>
    public static void Discard()
    {
        try { Clear(WorkFolder); } catch { /* removed at the next start */ }
        _hold?.Dispose();
        _hold = null;
        try { Directory.Delete(WorkFolder, recursive: true); } catch { /* removed at the next start */ }
    }

    /// <summary>Everything in the work folder but the mark that it is in use.</summary>
    private static void Clear(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (Path.GetFileName(entry) == Path.GetFileName(LockFile)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    private static void Unpack(string archive, string folder)
    {
        Directory.CreateDirectory(folder);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, folder, overwriteFiles: true);
            return;
        }
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: true);
    }

    /// <summary>The app within what was unpacked: a download holds one folder, and that the launcher and "app".</summary>
    internal static string AppIn(string unpacked, string launcherName)
    {
        foreach (var folder in Directory.EnumerateDirectories(unpacked).Prepend(unpacked))
            if (File.Exists(Path.Combine(folder, launcherName)) && Directory.Exists(Path.Combine(folder, "app")))
                return folder;
        throw new FriendlyException("The download does not hold the app the way it should, so it was not used.");
    }

    /// <summary>
    /// What an update left in the data folder — the installer, the unpacked copy — once it is
    /// through. Called a little after the app has started; a folder another copy of the app is
    /// fetching into at that moment is left alone.
    /// </summary>
    public static void RemoveLeftovers()
    {
        try
        {
            if (_hold != null || !Directory.Exists(WorkFolder)) return;
            using (new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Clear(WorkFolder);
            Directory.Delete(WorkFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use for a moment longer: the next start removes it
        }
    }

    // ---------- putting in place ----------

    /// <summary>
    /// Starts what puts the update in place — the installer, or the new copy — and returns at once.
    /// The caller then closes the app: nothing is replaced before it has.
    /// </summary>
    /// <param name="restart">Start the app again when the new version is in place.</param>
    /// <param name="unattended">Nobody is watching (a script): an installer keeps even its error messages to itself.</param>
    public static void Start(PreparedUpdate update, bool restart, bool unattended = false)
    {
        if (RunsElsewhere(update.Route.Folder)) throw new FriendlyException(RunningTwice);

        ProcessStartInfo command;
        if (update.Route.Kind == UpdateKind.Installer)
        {
            command = Os.InstallerCommand(update.Path, restart, unattended);
        }
        else
        {
            command = new ProcessStartInfo(Path.Combine(update.Path, Os.LauncherName)) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { ApplyCommand, update.Route.Folder, Environment.ProcessId.ToString(), restart ? "restart" : "close" })
                command.ArgumentList.Add(argument);
        }
        command.WorkingDirectory = Path.GetDirectoryName(update.Path)!;

        using var started = Process.Start(command)
            ?? throw new FriendlyException("The update could not be started.");
        // from here on the folder is the new copy's
        _hold?.Dispose();
        _hold = null;
        // for the next start to tell whether it went through, see TakeOutcome
        try
        {
            Directory.CreateDirectory(Os.DataDirectory);
            File.WriteAllText(PendingNote, update.Version.ToString());
        }
        catch
        {
            // then that start says nothing
        }
    }

    private const string RunningTwice = "Another copy of the app is running. Close it, then update again.";

    /// <summary>Is the app in this folder running a second time — another window, a conversion from the command line?</summary>
    internal static bool RunsElsewhere(string folder)
    {
        var name = Path.GetFileNameWithoutExtension(Os.LauncherName);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    // the launcher or its console twin; Linux cuts a process's name off after 15 letters
                    var running = process.ProcessName;
                    if (!running.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                        !(running.Length >= 15 && name.StartsWith(running, StringComparison.Ordinal))) continue;

                    if (process.MainModule?.FileName is { } path &&
                        string.Equals(Path.GetDirectoryName(path), folder, Os.PathComparison)) return true;
                }
                catch
                {
                    // gone in the meantime, or not this user's to look at
                }
            }
        }
        return false;
    }

    /// <summary>
    /// What the new copy does when the old one has started it with <see cref="ApplyCommand"/>:
    /// waits for the old one to close, puts itself where that lies, and starts the app again if
    /// that was asked for. When it cannot, the old version is left as it was and the reason is
    /// written down for the app to show.
    /// </summary>
    /// <param name="args">The command, the folder to update, the old copy's process id, "restart" or "close".</param>
    public static int ApplyStaged(IPlatform platform, string[] args)
    {
        PlatformServices.Use(platform);
        if (args.Length < 4 || args[0] != ApplyCommand || !int.TryParse(args[2], out var oldProcess)) return 2;

        var target = Path.GetFullPath(args[1]);
        var launcher = Path.Combine(target, platform.LauncherName);
        bool restart = args[3] == "restart";
        bool done = false;
        try
        {
            var staged = FolderOfThisCopy(platform.LauncherName)
                ?? throw new FriendlyException("The new version was not unpacked the way it should be.");
            // only ever over a copy of the app, wherever the folder named may point
            if (!File.Exists(launcher) || !Directory.Exists(Path.Combine(target, "app")))
                throw new FriendlyException("There is no copy of the app in " + target + ".");
            if (!WaitForExit(oldProcess, TimeSpan.FromSeconds(60)))
                throw new FriendlyException("The app did not close, so its files could not be replaced. The version you had is still in place.");

            Directory.CreateDirectory(WorkFolder);
            using (Retry(() => new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)))
                Replace(staged, target);
            done = true;

            // the app is in place; what else belongs to an installation is a courtesy
            try { platform.AfterUpdate(target); } catch { /* links and menu entry stay as they were */ }
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FailureNote)!);
                File.WriteAllText(FailureNote, ex switch
                {
                    FriendlyException => ex.Message,
                    IOException or UnauthorizedAccessException =>
                        $"The app's files could not be replaced: {ex.Message} The version you had is still in place.",
                    _ => ex.ToString()
                });
            }
            catch
            {
                // then it fails without a word; the old version is still there
            }
        }

        if (restart && File.Exists(launcher))
        {
            try
            {
                Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = target })?.Dispose();
            }
            catch
            {
                // the user starts it
            }
        }
        return done ? 0 : 1;
    }

    private static bool WaitForExit(int processId, TimeSpan patience)
    {
        if (processId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(patience);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true; // closed already
        }
    }

    /// <summary>
    /// A file that was in use a moment ago is often still held for an instant — by the process
    /// that is just closing, or by a virus scanner looking at it. A few tries get past that.
    /// </summary>
    private static T Retry<T>(Func<T> action, int tries = 20)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (attempt < tries && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// Puts the copy of the app in <paramref name="staged"/> where the one in <paramref name="target"/>
    /// lies. First the new copy is written out in full beside the old one, under other names; only
    /// then are the two swapped, name by name — each a rename, which either happens or does not. If
    /// one fails, those already made are undone. Nothing else in the folder is touched.
    /// </summary>
    /// <param name="tries">How often a rename that is refused is tried again, a quarter of a second apart.</param>
    internal static void Replace(string staged, string target, int tries = 20)
    {
        var names = Directory.EnumerateFileSystemEntries(staged).Select(entry => Path.GetFileName(entry)!).ToList();
        string Place(string name) => Path.Combine(target, name);
        string New(string name) => Place(name) + ".new";
        string Old(string name) => Place(name) + ".old";

        try
        {
            foreach (var name in names)
            {
                Remove(New(name));
                Copy(Path.Combine(staged, name), New(name));
            }
        }
        catch
        {
            foreach (var name in names) TryRemove(New(name));
            throw;
        }

        var swapped = new List<string>();
        try
        {
            foreach (var name in names)
            {
                Remove(Old(name));
                if (Exists(Place(name)))
                {
                    Retry(() =>
                    {
                        Move(Place(name), Old(name));
                        return true;
                    }, tries);
                }
                swapped.Add(name);
                Move(New(name), Place(name));
            }
        }
        catch
        {
            foreach (var name in Enumerable.Reverse(swapped))
            {
                try
                {
                    if (!Exists(Old(name))) continue;
                    Remove(Place(name));
                    Move(Old(name), Place(name));
                }
                catch
                {
                    // what could be put back has been
                }
            }
            foreach (var name in names) TryRemove(New(name));
            throw;
        }

        foreach (var name in names) TryRemove(Old(name));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void Remove(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private static void TryRemove(string path)
    {
        try { Remove(path); } catch { /* a leftover beside the app, harmless; the next update removes it */ }
    }

    /// <summary>A file, or a folder with everything in it. (On Linux a copy keeps the mark that makes a program runnable.)</summary>
    private static void Copy(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Copy(from, to, overwrite: true);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    // ---------- afterwards ----------

    /// <summary>
    /// What became of the last update, for the app to say when it next starts — once, then it is
    /// forgotten. An update that was started leaves a note of the version it was to bring; the
    /// start after it finds itself to be that version, or not. (Nothing is said while the note is
    /// only minutes old and the version still the old one: then the installer may still be at work.)
    /// </summary>
    /// <returns>The version the app was just updated to; or why the update did not go through; or neither.</returns>
    public static (string? UpdatedTo, string? Failure) TakeOutcome() =>
        TakeOutcome(PendingNote, FailureNote, AppInfo.Version, DateTime.UtcNow);

    internal static (string? UpdatedTo, string? Failure) TakeOutcome(string pendingNote, string failureNote, string currentVersion, DateTime now)
    {
        try
        {
            string? reason = null;
            if (File.Exists(failureNote))
            {
                reason = File.ReadAllText(failureNote).Trim();
                File.Delete(failureNote);
            }
            if (reason is { Length: 0 }) reason = null;
            if (!File.Exists(pendingNote)) return (null, reason);

            var wanted = File.ReadAllText(pendingNote).Trim();
            if (!Version.TryParse(wanted, out var version))
            {
                File.Delete(pendingNote);
                return (null, reason);
            }
            if (!UpdateCheck.IsNewer(version, currentVersion))
            {
                File.Delete(pendingNote);
                return (currentVersion, null);
            }
            if (reason == null && now - File.GetLastWriteTimeUtc(pendingNote) < TimeSpan.FromMinutes(3)) return (null, null);

            File.Delete(pendingNote);
            return (null, reason ?? $"Version {wanted} was downloaded, but installing it did not finish.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
