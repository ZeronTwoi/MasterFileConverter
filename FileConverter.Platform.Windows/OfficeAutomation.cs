using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FileConverter.Core.Platform;
using Microsoft.Win32;

namespace FileConverter.Platform;

/// <summary>
/// Microsoft Office as a conversion engine: Word for text documents, Excel for workbooks,
/// PowerPoint for presentations — whichever of them are installed. Each keeps a document's own
/// layout, which is what makes it worth asking before the built-in converter.
/// </summary>
internal sealed class MicrosoftOffice : IOfficeAutomation
{
    private readonly OfficeProgram[] _programs = [new WordProgram(), new ExcelProgram(), new PowerPointProgram()];

    public string Name => "Microsoft Office";

    public bool IsAvailable => _programs.Any(program => program.IsInstalled);

    public bool CanConvert(string sourceExtension, string targetExtension) =>
        ProgramFor(sourceExtension, targetExtension) != null;

    /// <summary>Returns false when no installed program does this conversion, or the conversion failed.</summary>
    public Task<bool> ConvertAsync(string input, string output, string targetExtension, CancellationToken ct)
    {
        var source = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        return ProgramFor(source, targetExtension) is { } program
            ? program.ConvertAsync(input, output, targetExtension, ct)
            : Task.FromResult(false);
    }

    private OfficeProgram? ProgramFor(string source, string target) =>
        source == target ? null : _programs.FirstOrDefault(program => program.IsInstalled && program.Converts(source, target));
}

/// <summary>
/// One hidden instance of an Office program, shared by every job for it. Starting the program
/// takes a couple of seconds, so a batch pays that once instead of per file. A dedicated STA
/// thread owns the COM object and works through the jobs in order; the program is closed again
/// after a few idle seconds and when the app exits.
///
/// A hidden program must never be left waiting for an answer nobody can give:
/// • a password-protected file fails at once instead of asking for the password;
/// • macros in the files are never run — nor asked about;
/// • a job that still takes longer than <see cref="JobTimeout"/> has its program closed and
///   reports failure, so the caller falls back to the built-in converter and the batch moves on.
///
/// The program is driven through late-bound calls (IDispatch) rather than C#'s "dynamic", which
/// a trimmed build cannot support.
/// </summary>
internal abstract class OfficeProgram
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(4);

    /// <summary>msoAutomationSecurityForceDisable: macros in a file opened here are off, without a question.</summary>
    protected const int MacrosOff = 3;

    private readonly object _sync = new();
    private BlockingCollection<Job>? _queue;
    private Thread? _thread;
    private bool? _installed;
    private int _processId;      // the instance started for us; 0 when there is none of our own to close
    private volatile bool _stuck; // an instance that is not ours to close has stopped answering

    protected sealed record Job(string Input, string Output, string Target, TaskCompletionSource<bool> Done, CancellationToken Token)
    {
        public volatile bool TimedOut;
    }

    protected OfficeProgram() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

    /// <summary>The name Windows starts the program by, such as "Word.Application".</summary>
    protected abstract string ProgId { get; }

    /// <summary>The program's process name, such as "WINWORD".</summary>
    protected abstract string ProcessName { get; }

    /// <summary>
    /// True for a program that runs once per user (PowerPoint): asking for it hands back the one
    /// the user already has open. That one is used, but never configured for good, closed or ended.
    /// </summary>
    protected virtual bool JoinsRunningInstance => false;

    /// <summary>Does the program read the one format and write the other?</summary>
    public abstract bool Converts(string sourceExtension, string targetExtension);

    /// <summary>Settings of a freshly started instance.</summary>
    protected abstract void Configure(object application);

    /// <summary>Opens the job's file, saves it in the target format and closes it again.</summary>
    protected abstract void Convert(object application, Job job);

    /// <summary>Tells the program to quit. False when it was left running, because the user is in it by now.</summary>
    protected abstract bool Quit(object application);

    /// <summary>Before the program is started, and after a session with it has ended.</summary>
    protected virtual void BeforeStart() { }
    protected virtual void AfterSession() { }

    /// <summary>The program had to be ended while it was opening this file.</summary>
    protected virtual void ForgetCrash(string documentPath) { }

    public bool IsInstalled => (_installed ??= Type.GetTypeFromProgID(ProgId) != null) && !_stuck;

    /// <summary>Converts through the program. Returns false when it is unavailable or the conversion failed.</summary>
    public async Task<bool> ConvertAsync(string input, string output, string targetExtension, CancellationToken ct)
    {
        if (!IsInstalled) return false;

        var job = new Job(Path.GetFullPath(input), Path.GetFullPath(output), targetExtension,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        lock (_sync)
        {
            try
            {
                if (_thread == null)
                {
                    _queue = new BlockingCollection<Job>();
                    _thread = new Thread(Run) { IsBackground = true, Name = ProgId + " automation" };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start(_queue);
                }
                _queue!.Add(job);
            }
            catch (InvalidOperationException)
            {
                return false; // shutting down
            }
        }

        // a cancelled batch doesn't wait for the program to finish the document it is on
        await using var cancellation = ct.Register(() => job.Done.TrySetCanceled(ct));
        return await job.Done.Task;
    }

    /// <summary>Closes the program if it is running; called at process exit so no hidden instance is left behind.</summary>
    private void Shutdown()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
            _queue?.CompleteAdding();
        }
        thread?.Join(TimeSpan.FromSeconds(10));

        // A session that ended by itself moments ago may still be seeing its program out — told to
        // quit, and ended if it lingers. That has to be through before the app goes, or the
        // program is left running hidden.
        if (Monitor.TryEnter(_session, TimeSpan.FromSeconds(12))) Monitor.Exit(_session);
    }

    /// <summary>
    /// Held for the whole of a session, tidying up included. A session that follows closely on
    /// another waits here until that one is through: otherwise the earlier one's tidying up — Word's
    /// PDF prompt being put back as it was, for one — would undo what the new one has just set.
    /// </summary>
    private readonly object _session = new();

    private void Run(object? state)
    {
        lock (_session) RunSession((BlockingCollection<Job>)state!);
    }

    private void RunSession(BlockingCollection<Job> queue)
    {
        object? application = null;
        try
        {
            while (true)
            {
                if (!queue.TryTake(out var job, IdleTimeout))
                {
                    lock (_sync)
                    {
                        if (queue.Count > 0) continue; // a job arrived just as the idle timer ran out
                        if (ReferenceEquals(_queue, queue))
                        {
                            _thread = null;            // the next request starts a fresh session
                            _queue = null;
                        }
                    }
                    return;
                }

                if (job.Token.IsCancellationRequested)
                {
                    job.Done.TrySetCanceled(job.Token);
                    continue;
                }

                bool ok = RunJob(ref application, job, queue);
                job.Done.TrySetResult(ok);
            }
        }
        finally
        {
            Close(ref application);
            AfterSession();
            _stuck = false; // whatever held this thread up has let go of it
        }
    }

    private bool RunJob(ref object? application, Job job, BlockingCollection<Job> queue)
    {
        // The program answers on its own time; if it stops answering, ending it makes the pending call fail.
        using var watchdog = new Timer(_ =>
        {
            job.TimedOut = true;
            if (!KillOwnInstance()) Abandon(queue);
            job.Done.TrySetResult(false);
        }, null, JobTimeout, Timeout.InfiniteTimeSpan);

        try
        {
            application ??= Start();
            if (application == null) return false;
            Convert(application, job);
            return File.Exists(job.Output);
        }
        catch
        {
            // The program may be gone (crashed, closed from Task Manager, stopped by the watchdog) or
            // stuck on this file: drop the instance — the next job starts a clean one.
            Close(ref application);
            if (job.TimedOut) ForgetCrash(job.Input);
            return false;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "An Office program is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    private object? Start()
    {
        var type = Type.GetTypeFromProgID(ProgId);
        if (type == null) return null;

        BeforeStart();
        var before = Process.GetProcessesByName(ProcessName).Select(p => p.Id).ToHashSet();
        var application = Activator.CreateInstance(type);
        if (application == null) return null;

        // The instance just started for us, so the watchdog can end exactly that one. Where asking for
        // the program started none — it was open already and we were handed that one — it is the
        // user's: it has no number here, and is never closed or ended.
        _processId = Process.GetProcessesByName(ProcessName).Select(p => p.Id).FirstOrDefault(id => !before.Contains(id));
        if (_processId != 0 || !JoinsRunningInstance) Configure(application);
        return application;
    }

    /// <summary>Ends the instance started for us. False when there is none: the program belongs to the user.</summary>
    private bool KillOwnInstance()
    {
        int id = _processId;
        if (id == 0) return false;
        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
            // a program that runs once per user and now shows a window: the user has opened it since. It is theirs.
            if (JoinsRunningInstance && process.MainWindowHandle != IntPtr.Zero)
            {
                _processId = 0;
                return false;
            }
            process.Kill();
        }
        catch
        {
            // already gone
        }
        return true;
    }

    /// <summary>
    /// The user's own instance has stopped answering — it may be showing them a question. Its
    /// thread is left to wait for it; the jobs behind it fail now, and the program counts as
    /// not there until that thread comes back.
    /// </summary>
    private void Abandon(BlockingCollection<Job> queue)
    {
        _stuck = true;
        lock (_sync)
        {
            if (ReferenceEquals(_queue, queue))
            {
                _thread = null;
                _queue = null;
            }
        }
        queue.CompleteAdding();
        while (queue.TryTake(out var waiting)) waiting.Done.TrySetResult(false);
    }

    private void Close(ref object? application)
    {
        if (application == null) return;
        int id = _processId;
        bool quit = false;
        // only an instance of our own is told to quit
        if (id != 0 || !JoinsRunningInstance)
            try { quit = Quit(application); } catch { quit = true; /* already gone */ }
        try { Marshal.FinalReleaseComObject(application); } catch { /* not a COM wrapper any more */ }
        application = null;
        _processId = 0;

        if (id == 0 || !quit) return;
        try
        {
            // told to quit, it goes within moments; one that lingers hidden is ended
            using var process = Process.GetProcessById(id);
            if (process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) && !process.WaitForExit(5000)) process.Kill();
        }
        catch
        {
            // gone already
        }
    }

    /// <summary>Hands back what was borrowed from the program: while anything of it is held here, it does not close.</summary>
    protected static void Release(ref object? document)
    {
        if (document == null) return;
        try { Marshal.FinalReleaseComObject(document); } catch { /* not a COM wrapper any more */ }
        document = null;
    }

    // ---------- late binding ----------

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "An Office program is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    protected static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "An Office program is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    protected static void Set(object target, string property, object value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, [value]);

    /// <summary>Calls a method with every argument passed by name, as the Office object models document them.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "An Office program is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    protected static object? Call(object target, string method, params (string Name, object Value)[] arguments) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target,
            arguments.Select(a => a.Value).ToArray(), null, null, arguments.Select(a => a.Name).ToArray());

    /// <summary>A password no file has: an unprotected file ignores it, a protected one fails on it instead of asking.</summary>
    protected static readonly string NoPassword = ((char)1).ToString();
}

/// <summary>
/// Word: .docx, .doc, OpenDocument text and RTF into each other and into PDF, and PDF back into
/// those. Its "this PDF will be converted" prompt is switched off for the session through Word's
/// own "don't show this again" option, and put back as it was afterwards.
/// </summary>
internal sealed class WordProgram : OfficeProgram
{
    protected override string ProgId => "Word.Application";
    protected override string ProcessName => "WINWORD";

    public override bool Converts(string sourceExtension, string targetExtension) =>
        sourceExtension is "docx" or "doc" or "odt" or "rtf" or "pdf" &&
        targetExtension is "pdf" or "docx" or "doc" or "odt" or "rtf" or "txt";

    protected override void BeforeStart() => PdfPrompt.Suppress();
    protected override void AfterSession() => PdfPrompt.Restore();
    protected override void ForgetCrash(string documentPath) => Resiliency.Forget(documentPath);

    protected override void Configure(object word)
    {
        Set(word, "Visible", false);
        Set(word, "DisplayAlerts", 0); // wdAlertsNone
        try { Set(word, "AutomationSecurity", MacrosOff); } catch { /* an edition without the setting */ }
        // nothing is on screen to be kept up to date (a property of this instance, not a saved setting)
        try { Set(word, "ScreenUpdating", false); } catch { /* not vital */ }
    }

    protected override bool Quit(object word)
    {
        Call(word, "Quit", ("SaveChanges", false));
        return true;
    }

    protected override void Convert(object word, Job job)
    {
        object? doc = null, documents = null;
        try
        {
            documents = Get(word, "Documents")!;
            doc = Call(documents, "Open",
                ("FileName", job.Input), ("ConfirmConversions", false), ("ReadOnly", true), ("AddToRecentFiles", false),
                ("PasswordDocument", NoPassword), ("Visible", false), ("NoEncodingDialog", true));
            if (doc == null) return;

            switch (job.Target)
            {
                case "pdf":
                    // print-quality export with the heading outline as PDF bookmarks and accessibility tags
                    Call(doc, "ExportAsFixedFormat",
                        ("OutputFileName", job.Output), ("ExportFormat", 17 /* wdExportFormatPDF */),
                        ("OpenAfterExport", false), ("OptimizeFor", 0 /* wdExportOptimizeForPrint */),
                        ("CreateBookmarks", 1 /* headings */), ("DocStructureTags", true), ("BitmapMissingFonts", true));
                    break;
                case "txt":
                    // wdFormatEncodedText + UTF-8 so non-Latin characters survive
                    Call(doc, "SaveAs2", ("FileName", job.Output), ("FileFormat", 7), ("Encoding", 65001),
                        ("AllowSubstitutions", false), ("AddToRecentFiles", false));
                    break;
                default:
                    Call(doc, "SaveAs2", ("FileName", job.Output), ("AddToRecentFiles", false), ("FileFormat", job.Target switch
                    {
                        "docx" => 16, // wdFormatDocumentDefault
                        "odt" => 23,  // wdFormatOpenDocumentText
                        "rtf" => 6,   // wdFormatRTF
                        _ => 0        // wdFormatDocument97
                    }));
                    break;
            }
        }
        finally
        {
            if (doc != null)
            {
                try { Call(doc, "Close", ("SaveChanges", false)); } catch { /* Word is gone */ }
                Release(ref doc);
            }
            Release(ref documents);
        }
    }

    private static string? OfficeKey(string leaf)
    {
        // "Word.Application.16" → the settings of Office 16.0 (2016 through 2024 and Microsoft 365)
        using var current = Registry.ClassesRoot.OpenSubKey(@"Word.Application\CurVer");
        var version = (current?.GetValue(null) as string)?.Split('.').LastOrDefault();
        return int.TryParse(version, out var major) ? $@"Software\Microsoft\Office\{major}.0\Word\{leaf}" : null;
    }

    /// <summary>
    /// Opening a PDF makes Word ask "Word will now convert your PDF to an editable document" — on
    /// a hidden instance that question is never seen and the conversion would wait forever. Word's
    /// "don't show this message again" option turns it off; it is set for the session only. What
    /// it was before is written to a small file first, so even a crash is undone at the next start.
    /// </summary>
    private static class PdfPrompt
    {
        private const string ValueName = "DisableConvertPdfWarning";
        private static readonly object Sync = new();

        private static string MarkerPath => Path.Combine(PlatformServices.Current.DataDirectory, "word-pdf-prompt.restore");

        public static void Suppress()
        {
            lock (Sync)
            {
                try
                {
                    Restore(); // left over from a run that ended abruptly
                    if (OfficeKey("Options") is not { } path) return;

                    using var key = Registry.CurrentUser.CreateSubKey(path);
                    var previous = key.GetValue(ValueName);
                    if (previous is 1) return; // the user already turned the prompt off

                    Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                    File.WriteAllText(MarkerPath, previous is int value ? value.ToString() : "");
                    key.SetValue(ValueName, 1, RegistryValueKind.DWord);
                }
                catch
                {
                    // registry not writable: PDF imports may time out, everything else is unaffected
                }
            }
        }

        public static void Restore()
        {
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(MarkerPath) || OfficeKey("Options") is not { } path) return;

                    var previous = File.ReadAllText(MarkerPath).Trim();
                    using var key = Registry.CurrentUser.CreateSubKey(path);
                    if (int.TryParse(previous, out var value)) key.SetValue(ValueName, value, RegistryValueKind.DWord);
                    else key.DeleteValue(ValueName, throwOnMissingValue: false);
                    File.Delete(MarkerPath);
                }
                catch
                {
                    // tried again at the next start
                }
            }
        }
    }

    /// <summary>
    /// Word remembers a document it was closed on while opening, and the next time greets it with
    /// "this document caused a serious error" — one more question a hidden Word can't get answered.
    /// After the watchdog has had to close Word, the note about that one file is removed again.
    /// </summary>
    private static class Resiliency
    {
        public static void Forget(string documentPath)
        {
            try
            {
                foreach (var list in new[] { "StartupItems", "DisabledItems" })
                {
                    if (OfficeKey($@"Resiliency\{list}") is not { } path) return;
                    using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                    if (key == null) continue;

                    foreach (var name in key.GetValueNames())
                    {
                        if (key.GetValue(name) is byte[] data &&
                            Encoding.Unicode.GetString(data).Contains(documentPath, StringComparison.OrdinalIgnoreCase))
                            key.DeleteValue(name, throwOnMissingValue: false);
                    }
                }
            }
            catch
            {
                // Word will ask about the file itself next time
            }
        }
    }
}

/// <summary>Excel: .xlsx, .xls and OpenDocument spreadsheets into each other, and into PDF as Excel would print them.</summary>
internal sealed class ExcelProgram : OfficeProgram
{
    protected override string ProgId => "Excel.Application";
    protected override string ProcessName => "EXCEL";

    public override bool Converts(string sourceExtension, string targetExtension) =>
        sourceExtension is "xlsx" or "xls" or "ods" && targetExtension is "pdf" or "xlsx" or "ods";

    protected override void Configure(object excel)
    {
        Set(excel, "Visible", false);
        Set(excel, "DisplayAlerts", false);
        try { Set(excel, "AutomationSecurity", MacrosOff); } catch { /* an edition without the setting */ }
        // a workbook that refers to other files is converted with the values it was saved with
        try { Set(excel, "AskToUpdateLinks", false); } catch { /* not vital */ }
        try { Set(excel, "EnableEvents", false); } catch { /* not vital */ }
        try { Set(excel, "ScreenUpdating", false); } catch { /* not vital */ }
    }

    protected override bool Quit(object excel)
    {
        Call(excel, "Quit");
        return true;
    }

    protected override void Convert(object excel, Job job)
    {
        object? book = null, books = null;
        try
        {
            books = Get(excel, "Workbooks")!;
            book = Call(books, "Open",
                ("Filename", job.Input), ("UpdateLinks", 0), ("ReadOnly", true), ("Password", NoPassword),
                ("IgnoreReadOnlyRecommended", true), ("AddToMru", false));
            if (book == null) return;

            if (job.Target == "pdf")
            {
                // the whole workbook, every sheet as its print settings lay it out
                Call(book, "ExportAsFixedFormat",
                    ("Type", 0 /* xlTypePDF */), ("Filename", job.Output), ("Quality", 0 /* xlQualityStandard */),
                    ("IncludeDocProperties", true), ("IgnorePrintAreas", false), ("OpenAfterPublish", false));
            }
            else
            {
                Call(book, "SaveAs", ("Filename", job.Output),
                    ("FileFormat", job.Target == "ods" ? 60 /* xlOpenDocumentSpreadsheet */ : 51 /* xlOpenXMLWorkbook */));
            }
        }
        finally
        {
            if (book != null)
            {
                try { Call(book, "Close", ("SaveChanges", false)); } catch { /* Excel is gone */ }
                Release(ref book);
            }
            Release(ref books);
        }
    }
}

/// <summary>
/// PowerPoint: .pptx, .ppt and OpenDocument presentations into each other, and into PDF. It runs
/// once per user, so when it is open already the conversion goes through that very program —
/// without a window, and without anything of the user's being touched: their PowerPoint is not
/// closed afterwards, and if it stops answering it is left alone.
/// </summary>
internal sealed class PowerPointProgram : OfficeProgram
{
    protected override string ProgId => "PowerPoint.Application";
    protected override string ProcessName => "POWERPNT";
    protected override bool JoinsRunningInstance => true;

    public override bool Converts(string sourceExtension, string targetExtension) =>
        sourceExtension is "pptx" or "ppt" or "odp" && targetExtension is "pdf" or "pptx" or "odp" or "ppt";

    protected override void Configure(object powerPoint)
    {
        // (PowerPoint cannot be told to hide itself; without a window of its own nothing shows)
        try { Set(powerPoint, "DisplayAlerts", 1 /* ppAlertsNone */); } catch { /* not vital */ }
    }

    protected override bool Quit(object powerPoint)
    {
        // Started for us, it has nothing open once our file is closed. If it has, the user has
        // started PowerPoint in the meantime and been handed this very instance: it stays.
        var presentations = Get(powerPoint, "Presentations");
        bool inUse = presentations != null && Get(presentations, "Count") is int open && open > 0;
        Release(ref presentations);
        if (inUse) return false;
        Call(powerPoint, "Quit");
        return true;
    }

    protected override void Convert(object powerPoint, Job job)
    {
        object? deck = null, presentations = null;
        // Macros are off while our file is opened — and as they were again right after, since
        // this may be the PowerPoint the user is working in.
        object? security = null;
        try { security = Get(powerPoint, "AutomationSecurity"); Set(powerPoint, "AutomationSecurity", MacrosOff); } catch { /* an edition without the setting */ }
        try
        {
            presentations = Get(powerPoint, "Presentations")!;
            // "name::password::" hands a password along with the name: an unprotected file ignores it,
            // a protected one fails on it instead of asking.
            deck = Call(presentations, "Open",
                ("FileName", job.Input + "::not-the-password::"), ("ReadOnly", -1 /* msoTrue */), ("Untitled", 0), ("WithWindow", 0));
            if (deck == null) return;

            Call(deck, "SaveAs", ("FileName", job.Output), ("FileFormat", job.Target switch
            {
                "pdf" => 32, // ppSaveAsPDF
                "odp" => 35, // ppSaveAsOpenDocumentPresentation
                "ppt" => 1,  // ppSaveAsPresentation: the old binary format (only the self-test asks for it, to have a sample)
                _ => 24      // ppSaveAsOpenXMLPresentation
            }));
        }
        finally
        {
            if (deck != null)
            {
                try { Call(deck, "Close"); } catch { /* PowerPoint is gone */ }
                Release(ref deck);
            }
            Release(ref presentations);
            if (security != null)
                try { Set(powerPoint, "AutomationSecurity", security); } catch { /* PowerPoint is gone */ }
        }
    }
}
