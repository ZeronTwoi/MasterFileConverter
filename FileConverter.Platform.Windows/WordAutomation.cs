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
/// One hidden Microsoft Word instance shared by every document job. Starting Word takes a
/// couple of seconds, so a batch pays that once instead of per file. A dedicated STA thread
/// owns the COM object and works through the jobs in order; Word is closed again after a few
/// idle seconds and when the app exits.
///
/// A hidden Word must never be left waiting for an answer nobody can give:
/// • its "this PDF will be converted" prompt is switched off for the session through Word's own
///   "don't show this again" option, and put back as it was afterwards;
/// • a password-protected document fails at once instead of asking for the password;
/// • a job that still takes longer than <see cref="JobTimeout"/> has its Word closed and reports
///   failure, so the caller falls back to the built-in converter and the batch moves on.
///
/// Word is driven through late-bound calls (IDispatch) rather than C#'s "dynamic", which a
/// trimmed build cannot support.
/// </summary>
internal sealed class WordAutomation : IOfficeAutomation
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(4);

    private readonly object _sync = new();
    private BlockingCollection<Job>? _queue;
    private Thread? _thread;
    private bool? _available;
    private int _wordProcessId;

    private sealed record Job(string Input, string Output, string Target, TaskCompletionSource<bool> Done, CancellationToken Token)
    {
        public volatile bool TimedOut;
    }

    public WordAutomation() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

    public string Name => "Microsoft Word";

    public bool IsAvailable => _available ??= Type.GetTypeFromProgID("Word.Application") != null;

    /// <summary>Converts through Word. Returns false when Word is unavailable or the conversion failed.</summary>
    public async Task<bool> ConvertAsync(string input, string output, string targetExtension, CancellationToken ct)
    {
        if (targetExtension is not ("pdf" or "docx" or "doc" or "txt") || !IsAvailable)
            return false;

        var job = new Job(Path.GetFullPath(input), Path.GetFullPath(output), targetExtension,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        lock (_sync)
        {
            try
            {
                if (_thread == null)
                {
                    _queue = new BlockingCollection<Job>();
                    _thread = new Thread(Run) { IsBackground = true, Name = "Word automation" };
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

        // a cancelled batch doesn't wait for Word to finish the document it is on
        await using var cancellation = ct.Register(() => job.Done.TrySetCanceled(ct));
        return await job.Done.Task;
    }

    /// <summary>Closes Word if it is running; called at process exit so no hidden WINWORD.EXE is left behind.</summary>
    private void Shutdown()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
            _queue?.CompleteAdding();
        }
        thread?.Join(TimeSpan.FromSeconds(10));
    }

    private void Run(object? state)
    {
        var queue = (BlockingCollection<Job>)state!;
        object? word = null;
        try
        {
            while (true)
            {
                if (!queue.TryTake(out var job, IdleTimeout))
                {
                    lock (_sync)
                    {
                        if (queue.Count > 0) continue; // a job arrived just as the idle timer ran out
                        _thread = null;                // the next request starts a fresh session
                        _queue = null;
                    }
                    return;
                }

                if (job.Token.IsCancellationRequested)
                {
                    job.Done.TrySetCanceled(job.Token);
                    continue;
                }

                bool ok = Convert(ref word, job);
                job.Done.TrySetResult(ok);
            }
        }
        finally
        {
            Quit(word);
            PdfPrompt.Restore();
        }
    }

    private bool Convert(ref object? word, Job job)
    {
        object? doc = null;
        // Word answers on its own time; if it stops answering, closing it makes the pending call fail
        using var watchdog = new Timer(_ =>
        {
            job.TimedOut = true;
            KillWord();
        }, null, JobTimeout, Timeout.InfiniteTimeSpan);

        try
        {
            word ??= StartWord();
            if (word == null) return false;

            var documents = Get(word, "Documents")!;
            // the dummy password is ignored by unprotected files and makes protected ones fail instead of prompting
            doc = Call(documents, "Open",
                ("FileName", job.Input), ("ConfirmConversions", false), ("ReadOnly", true), ("AddToRecentFiles", false),
                ("PasswordDocument", "\u0001"), ("Visible", false), ("NoEncodingDialog", true));
            if (doc == null) return false;

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
                case "docx":
                    Call(doc, "SaveAs2", ("FileName", job.Output), ("FileFormat", 16 /* wdFormatDocumentDefault */),
                        ("AddToRecentFiles", false));
                    break;
                default:
                    Call(doc, "SaveAs2", ("FileName", job.Output), ("FileFormat", 0 /* wdFormatDocument97 */),
                        ("AddToRecentFiles", false));
                    break;
            }
            return File.Exists(job.Output);
        }
        catch
        {
            // Word may be gone (crashed, closed from Task Manager, stopped by the watchdog) or
            // stuck on this file: drop the instance — the next job starts a clean one.
            Close(ref doc);
            Quit(word);
            word = null;
            if (job.TimedOut) Resiliency.Forget(job.Input);
            return false;
        }
        finally
        {
            Close(ref doc);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Word is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    private object? StartWord()
    {
        var type = Type.GetTypeFromProgID("Word.Application");
        if (type == null) return null;

        PdfPrompt.Suppress();
        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var word = Activator.CreateInstance(type);
        if (word == null) return null;

        // the instance just started for us, so the watchdog can close exactly that one
        _wordProcessId = Process.GetProcessesByName("WINWORD").Select(p => p.Id).FirstOrDefault(id => !before.Contains(id));
        Set(word, "Visible", false);
        Set(word, "DisplayAlerts", 0); // wdAlertsNone
        // nothing is on screen to be kept up to date (a property of this instance, not a saved setting)
        try { Set(word, "ScreenUpdating", false); } catch { /* not vital */ }
        return word;
    }

    private void KillWord()
    {
        int id = _wordProcessId;
        if (id == 0) return;
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)) process.Kill();
        }
        catch
        {
            // already gone
        }
    }

    private static void Close(ref object? doc)
    {
        if (doc == null) return;
        try { Call(doc, "Close", ("SaveChanges", false)); } catch { /* Word is gone */ }
        try { Marshal.FinalReleaseComObject(doc); } catch { /* not a COM wrapper any more */ }
        doc = null;
    }

    private void Quit(object? word)
    {
        if (word == null) return;
        try { Call(word, "Quit", ("SaveChanges", false)); } catch { /* already gone */ }
        try { Marshal.FinalReleaseComObject(word); } catch { /* not a COM wrapper any more */ }
        _wordProcessId = 0;
    }

    // ---------- late binding ----------

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Word is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Word is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    private static void Set(object target, string property, object value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, [value]);

    /// <summary>Calls a method with every argument passed by name, as the Word object model documents them.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Word is a COM object reached through IDispatch; none of its members are .NET code a trimmer could remove.")]
    private static object? Call(object target, string method, params (string Name, object Value)[] arguments) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target,
            arguments.Select(a => a.Value).ToArray(), null, null, arguments.Select(a => a.Name).ToArray());

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
