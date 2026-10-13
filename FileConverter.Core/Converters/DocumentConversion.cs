using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileConverter.Core.Platform;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using K = FileConverter.Core.Settings.SettingKeys;
using QDocument = QuestPDF.Fluent.Document;
using Document = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace FileConverter.Core.Converters;

/// <summary>
/// Document conversions between txt/pdf/doc/docx (md and log are treated as text).
/// Engine priority for fidelity: LibreOffice → Microsoft Word (where installed) → built-in
/// text-level converters (PdfPig for PDF text extraction, QuestPDF for PDF
/// creation, OpenXml for docx, RTF for legacy .doc output).
/// </summary>
public static class DocumentConversion
{
    public static Task<ConversionResult> ConvertAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(async () => await ConvertCoreAsync(input, output, options, progress, ct), ct);

    private static async Task<ConversionResult> ConvertCoreAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var src = FormatRegistry.GetExtension(input);
        if (src is "md" or "log") src = "txt";
        var dst = FormatRegistry.GetExtension(output);

        progress?.Report(0.1);

        switch (src)
        {
            case "txt":
                switch (dst)
                {
                    case "pdf": TextToPdf(TextFiles.Read(input), output, options); break;
                    case "docx": TextToDocx(TextFiles.Read(input), output); break;
                    case "doc":
                    {
                        // The office engines are handed a docx made here rather than the text file:
                        // its encoding has been worked out already, and they would only guess at it.
                        var text = TextFiles.Read(input);
                        var staged = Path.Combine(Path.GetTempPath(), $"fc-text-{Guid.NewGuid():N}.docx");
                        try
                        {
                            TextToDocx(text, staged);
                            if (!await TryOfficeEnginesAsync(staged, output, dst, options, progress, ct))
                                TextToDocRtf(text, output);
                        }
                        finally
                        {
                            try { File.Delete(staged); } catch { /* best effort */ }
                        }
                        break;
                    }
                    default: throw Unsupported(src, dst);
                }
                break;

            case "pdf":
                switch (dst)
                {
                    case "png":
                    case "jpg":
                        // one image per page; multi-page PDFs get a "name pages" folder
                        return new ConversionResult(
                            await PdfPageExport.ToImagesAsync(input, output, dst, options, progress, ct));
                    case "txt":
                        await File.WriteAllTextAsync(output, PdfToText(input, ct), Encoding.UTF8, ct);
                        break;
                    case "docx":
                        // Word imports PDFs with layout, LibreOffice as positioned text; the fallback is text-only.
                        if (!await TryPdfImportAsync(input, output, dst, options, ct))
                            TextToDocx(PdfToText(input, ct), output);
                        break;
                    case "doc":
                        if (!await TryPdfImportAsync(input, output, dst, options, ct))
                            TextToDocRtf(PdfToText(input, ct), output);
                        break;
                    default: throw Unsupported(src, dst);
                }
                break;

            case "docx":
                if (!await TryOfficeEnginesAsync(input, output, dst, options, progress, ct))
                {
                    var text = DocxToText(input);
                    switch (dst)
                    {
                        case "txt": await File.WriteAllTextAsync(output, text, Encoding.UTF8, ct); break;
                        case "pdf": TextToPdf(text, output, options); break;
                        case "doc": TextToDocRtf(text, output); break;
                        default: throw Unsupported(src, dst);
                    }
                }
                break;

            case "doc":
                // Binary .doc needs a real Office engine to parse.
                if (!await TryOfficeEnginesAsync(input, output, dst, options, progress, ct))
                    throw new FriendlyException(
                        "Converting legacy .doc files requires Microsoft Word or LibreOffice (free, libreoffice.org). Install one and try again.");
                break;

            default:
                throw Unsupported(src, dst);
        }

        progress?.Report(1.0);
        return new ConversionResult(output);
    }

    private static FriendlyException Unsupported(string src, string dst) =>
        new($"Converting .{src} to .{dst} is not supported.");

    // ---------- office engines ----------

    /// <summary>
    /// Which office engines may be tried, in the order given. Automatic tries every one that is
    /// installed; a chosen engine is the only one used, and it is an error for it to be missing —
    /// silently using another would not be what was asked for.
    /// </summary>
    private static (bool LibreOffice, bool Office) AllowedEngines(ConversionOptions options)
    {
        switch (options.Detail.Text(K.Engine))
        {
            case "builtin":
                return (false, false);
            case "libreoffice":
                if (ToolLocator.LibreOfficePath == null)
                    throw new FriendlyException("LibreOffice is selected under Detailed settings but is not installed (libreoffice.org).");
                return (true, false);
            case "office":
                if (!ToolLocator.OfficeAutomationAvailable)
                    throw new FriendlyException("The office program selected under Detailed settings is not installed.");
                return (false, true);
            default:
                return (true, true);
        }
    }

    private static async Task<bool> TryOfficeEnginesAsync(string input, string output, string dst,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var allowed = AllowedEngines(options);
        if (allowed.LibreOffice && await TryLibreOfficeAsync(input, output, dst, inputFilter: null, ct)) { progress?.Report(0.9); return true; }
        if (allowed.Office && await TryOfficeAutomationAsync(input, output, dst, ct)) { progress?.Report(0.9); return true; }
        return false;
    }

    /// <summary>PDF → editable document: Word reflows the text best, LibreOffice keeps the look.</summary>
    private static async Task<bool> TryPdfImportAsync(string input, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        var allowed = AllowedEngines(options);
        return (allowed.Office && await TryOfficeAutomationAsync(input, output, dst, ct)) ||
               (allowed.LibreOffice && await TryLibreOfficeAsync(input, output, dst, inputFilter: "writer_pdf_import", ct));
    }

    private static Task<bool> TryOfficeAutomationAsync(string input, string output, string dst, CancellationToken ct) =>
        PlatformServices.Current.OfficeAutomation is { IsAvailable: true } office
            ? office.ConvertAsync(input, output, dst, ct)
            : Task.FromResult(false);

    private static async Task<bool> TryLibreOfficeAsync(string input, string output, string dst, string? inputFilter,
        CancellationToken ct)
    {
        var soffice = ToolLocator.LibreOfficePath;
        if (soffice == null) return false;

        var outDir = Path.Combine(Path.GetTempPath(), $"fc-lo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        // A profile of its own, so that a LibreOffice the user has open doesn't block headless mode.
        using var profile = OfficeProfile.Rent(Path.Combine(outDir, "profile"));
        try
        {
            var profileDir = profile.Folder;
            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // ArgumentList quotes each argument itself, so any file name survives intact on every system
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add($"-env:UserInstallation={new Uri(profileDir).AbsoluteUri}");
            if (inputFilter != null) psi.ArgumentList.Add($"--infilter={inputFilter}");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add(dst == "txt" ? "txt:Text (encoded):UTF8" : dst);
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(input);

            using var process = Process.Start(psi)!;
            // both pipes are drained, or a chatty run would block on a full buffer
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                profile.Spoiled = true; // stopped in the middle of whatever it was doing
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* it will go eventually */ }
                ct.ThrowIfCancellationRequested();
                return false;
            }
            await Task.WhenAll(stdout, stderr);

            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "." + dst);
            if (!File.Exists(produced))
            {
                profile.Spoiled = true;
                return false;
            }

            File.Copy(produced, output, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The settings folder a headless LibreOffice runs with. Setting one up is a part of every
    /// conversion that can be saved — on an emulated Linux machine a one-page document took 75 s
    /// when the folder had to be made and 51 and 64 s the next two times — so a few are kept
    /// between conversions and between runs of the app, each used by one LibreOffice at a time.
    /// When all are taken — a large batch runs many at once — the conversion gets a throw-away
    /// folder, as every one used to.
    /// </summary>
    private sealed class OfficeProfile : IDisposable
    {
        private const int Kept = 4;

        private readonly FileStream? _claim; // held open while the profile is in use, also against other copies of the app

        public string Folder { get; }

        /// <summary>The run did not end well: the profile is not trusted again and is made afresh next time.</summary>
        public bool Spoiled { get; set; }

        private OfficeProfile(string folder, FileStream? claim)
        {
            Folder = folder;
            _claim = claim;
        }

        public static OfficeProfile Rent(string throwAwayFolder)
        {
            try
            {
                var home = Path.Combine(PlatformServices.Current.DataDirectory, "libreoffice");
                Directory.CreateDirectory(home);
                for (int n = 1; n <= Kept; n++)
                {
                    try
                    {
                        var claim = new FileStream(Path.Combine(home, $"profile-{n}.inuse"), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None);
                        return new OfficeProfile(Path.Combine(home, $"profile-{n}"), claim);
                    }
                    catch (IOException)
                    {
                        // taken — try the next
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nowhere to keep one: a throw-away profile works as well, only slower
            }
            return new OfficeProfile(throwAwayFolder, null);
        }

        public void Dispose()
        {
            if (_claim == null) return; // a throw-away folder goes with the conversion's temp folder
            if (Spoiled)
                try { Directory.Delete(Folder, recursive: true); } catch { /* it is made afresh over what is left */ }
            _claim.Dispose();
        }
    }

    // ---------- built-in fallbacks ----------

    private static string PdfToText(string input, CancellationToken ct)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(input);
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            // layout-aware: rebuilds words, lines and paragraphs from glyph positions
            // (the raw page text has no line breaks and runs words together)
            sb.AppendLine(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void TextToPdf(string text, string output, ConversionOptions options)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split('\n');
        var detail = options.Detail;
        var paper = MergeConversion.PaperSize(detail.Text(K.PageSize)) ?? PageSizes.A4;
        // millimetres → points; 48 pt (17 mm) when nothing is set
        float margin = detail.Number(K.Margin) is { } millimetres ? (float)(Math.Clamp(millimetres, 0, 100) * 72.0 / 25.4) : 48f;
        float fontSize = (float)Math.Clamp(detail.Number(K.FontSize) ?? 10.5, 5, 72);

        QDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(paper);
                page.Margin(margin);
                // Lato ships with QuestPDF; installed system fonts cover the glyphs it lacks (CJK and others)
                page.DefaultTextStyle(x => x.FontSize(fontSize).LineHeight(1.35f)
                    .FontFamily(["Lato", .. PlatformServices.Current.PdfFallbackFonts]));

                page.Content().Column(column =>
                {
                    foreach (var paragraph in paragraphs)
                        column.Item().Text(paragraph.Length == 0 ? " " : paragraph);
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Medium));
                    x.CurrentPageNumber();
                    x.Span(" / ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf(output);
    }

    private static void TextToDocx(string text, string output)
    {
        using var doc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var run = new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve });
            body.Append(new Paragraph(run));
        }
        main.Document = new Document(body);
        main.Document.Save();
    }

    private static string DocxToText(string input)
    {
        using var doc = WordprocessingDocument.Open(input, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "";
        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
            sb.AppendLine(paragraph.InnerText);
        return sb.ToString();
    }

    /// <summary>
    /// Writes RTF content with a .doc extension — opens natively in Word/WordPad/LibreOffice.
    /// Used only when no Office engine is installed to produce a true binary .doc.
    /// </summary>
    private static void TextToDocRtf(string text, string output)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22 ");
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\n': sb.Append("\\par\r\n"); break;
                default:
                    if (ch < 128) sb.Append(ch);
                    else sb.Append(@"\u").Append((int)(short)ch).Append('?');
                    break;
            }
        }
        sb.Append('}');
        File.WriteAllText(output, sb.ToString(), Encoding.ASCII);
    }
}

/// <summary>Reads text files without mangling non-English characters.</summary>
internal static class TextFiles
{
    private static readonly Lazy<Encoding> Legacy = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = PlatformServices.Current.LegacyCodePage;
        try { return Encoding.GetEncoding(codePage > 0 ? codePage : 1252); }
        catch { return Encoding.Latin1; }
    });

    /// <summary>
    /// Decodes by byte-order mark when there is one, as UTF-8 when the bytes are valid UTF-8,
    /// and otherwise as the user's legacy code page — which is how Notepad's "ANSI" files
    /// (Windows-1254 Turkish, 1252 Western, …) were saved. Assuming UTF-8 for those turns
    /// every accented letter into "�".
    /// </summary>
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Legacy.Value.GetString(bytes);
        }
    }
}
