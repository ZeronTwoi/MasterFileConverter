using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileConverter.Core.Documents;
using FileConverter.Core.Platform;
using FileConverter.Core.Sheets;
using FileConverter.Core.Slides;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using K = FileConverter.Core.Settings.SettingKeys;
using QDocument = QuestPDF.Fluent.Document;
using Document = DocumentFormat.OpenXml.Wordprocessing.Document;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace FileConverter.Core.Converters;

/// <summary>
/// Document conversions: PDF, Word (.docx, .doc), OpenDocument text, RTF, HTML, Markdown,
/// e-books (EPUB) and plain text, each into the others; spreadsheets (Excel, OpenDocument, CSV)
/// into each other, into PDF and out of a PDF's tables; presentations (PowerPoint, OpenDocument)
/// into each other, into PDF and out of a PDF's pages; and any of them into pictures of its pages.
///
/// There are two ways from one format to another, and which is taken depends on what is asked:
/// • An office program — LibreOffice, or Microsoft Word where it is installed — keeps a document's
///   layout: fonts, columns, headers, everything on the page. It is used whenever both ends are
///   formats such a program reads and writes (Word, OpenDocument, RTF, and PDF as a result).
/// • The built-in converter keeps what a document says and how it is built: headings, bold and
///   italic, lists, tables, pictures, links (see <see cref="RichDocument"/>). It needs nothing
///   installed, is the only way for the formats office programs do not handle (Markdown, e-books,
///   HTML read without going online), and stands in wherever no office program is there.
/// </summary>
public static class DocumentConversion
{
    public static Task<ConversionResult> ConvertAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(async () => await ConvertCoreAsync(input, output, options, progress, ct), ct);

    private static async Task<ConversionResult> ConvertCoreAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var src = FormatRegistry.Canonical(FormatRegistry.GetExtension(input));
        var dst = FormatRegistry.Canonical(FormatRegistry.GetExtension(output));

        progress?.Report(0.05);

        if (dst == "svg" && src == "pdf")
        {
            // a page a drawing: shapes as shapes, letters as their outlines
            return new ConversionResult(await Pdf.PdfVectorExport.ExportAsync(input, output, options, progress, ct), "vector");
        }

        if (dst == "tiff" && src == "pdf")
        {
            // every page in one file, the way scanned documents are kept
            await PdfPageExport.ToTiffAsync(input, output, options, progress, ct);
            return new ConversionResult(output);
        }

        if (dst is "png" or "jpg")
        {
            // one image per page; several pages get a "name pages" folder
            if (src == "pdf")
                return new ConversionResult(await PdfPageExport.ToImagesAsync(input, output, dst, options, progress, ct));

            // any other document is first made into a PDF — the one form in which it has pages
            var pages = WorkFiles.NewPath("pages", ".pdf");
            try
            {
                await ConvertFileAsync(input, src, pages, "pdf", options, ct);
                progress?.Report(0.5);
                var half = progress == null ? null : new Progress<double>(p => progress.Report(0.5 + p / 2));
                return new ConversionResult(await PdfPageExport.ToImagesAsync(pages, output, dst, options, half, ct));
            }
            finally
            {
                try { File.Delete(pages); } catch { /* best effort */ }
            }
        }

        var produced = await ConvertFileAsync(input, src, output, dst, options, ct);
        progress?.Report(1.0);
        return new ConversionResult(produced);
    }

    private static FriendlyException Unsupported(string src, string dst) =>
        new($"Converting .{src} to .{dst} is not supported.");

    /// <summary>The formats an office program reads with their layout, and writes.</summary>
    private static bool IsOfficeText(string ext) => ext is "docx" or "doc" or "odt" or "rtf";

    /// <summary>What a spreadsheet program writes from a workbook with everything in it: its formats, and the printed page.</summary>
    private static bool IsOfficeSheetTarget(string ext) => ext is "pdf" or "xlsx" or "ods";

    /// <summary>
    /// May an office program do this conversion, or a part of it? (The Detailed settings offer the
    /// choice of engine for exactly these, and the self-test knows from it what needs one.)
    /// </summary>
    public static bool OfficeMayConvert(string sourceExtension, string targetExtension)
    {
        var src = FormatRegistry.Canonical(sourceExtension);
        var dst = FormatRegistry.Canonical(targetExtension);
        if (dst is "png" or "jpg") dst = "pdf"; // pages are drawn from a PDF
        switch (FormatRegistry.GetDocumentKind(src))
        {
            case DocumentKind.Pdf:
                // (and LibreOffice can open a PDF as a presentation — on request, see FromPdfAsync)
                return IsOfficeText(dst);
            case DocumentKind.Text:
                if (src == "doc" || dst == "doc") return true; // the binary format is an office program's to read and to write
                return IsOfficeText(src) && (IsOfficeText(dst) || dst is "pdf" or "txt");
            case DocumentKind.Sheet:
                return src != "csv" && IsOfficeSheetTarget(dst);
            case DocumentKind.Slides:
                return dst is "pdf" or "pptx" or "odp";
            default:
                return false;
        }
    }

    /// <summary>Converts one document file into another; returns what was produced (a folder, for Markdown with pictures).</summary>
    private static async Task<string> ConvertFileAsync(string input, string src, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        switch (FormatRegistry.GetDocumentKind(src))
        {
            case DocumentKind.Pdf:
                return await FromPdfAsync(input, output, dst, options, ct);
            case DocumentKind.Text:
                return await FromTextDocumentAsync(input, src, output, dst, options, ct);
            case DocumentKind.Sheet:
                return await FromSpreadsheetAsync(input, src, output, dst, options, ct);
            case DocumentKind.Slides:
                return await FromPresentationAsync(input, src, output, dst, options, ct);
            case DocumentKind.Drawing:
                return await FromDrawingAsync(input, src, output, dst, ct);
            default:
                throw Unsupported(src, dst);
        }
    }

    // ---------- PDF → … ----------

    private static async Task<string> FromPdfAsync(string input, string output, string dst, ConversionOptions options, CancellationToken ct)
    {
        switch (dst)
        {
            case "txt":
                await File.WriteAllTextAsync(output, PdfToText(input, ct), Encoding.UTF8, ct);
                return output;

            case "docx" or "doc" or "odt" or "rtf":
                // Word imports PDFs with layout, LibreOffice as positioned text; the built-in way rebuilds
                // headings, paragraphs and pictures from where the letters stand.
                if (await TryPdfImportAsync(input, output, dst, options, ct)) return output;
                return WriteBuiltIn(PdfTextReader.ReadFile(input, ct), input, output, dst, options);

            case "html" or "md" or "epub":
                return WriteBuiltIn(PdfTextReader.ReadFile(input, ct), input, output, dst, options);

            case "pptx" or "odp":
            {
                // On request LibreOffice opens the PDF as a presentation with text that can be edited — close
                // to the pages in look, not exact. Left to itself the app makes slides that show the pages.
                if (options.Detail.Is(K.Engine, "libreoffice"))
                {
                    if (ToolLocator.LibreOfficePath == null)
                        throw new FriendlyException("LibreOffice is selected under Detailed settings but is not installed (libreoffice.org).");
                    if (await TryLibreOfficeAsync(input, output, dst, inputFilter: "impress_pdf_import", ct)) return output;
                    throw new FriendlyException("LibreOffice could not open this PDF as a presentation.");
                }

                var deck = PdfSlides.Read(input, options, ct);
                try
                {
                    if (dst == "pptx") PptxWriter.Write(deck, output);
                    else OdpWriter.Write(deck, output);
                    return output;
                }
                finally
                {
                    deck.Close?.Invoke();
                }
            }

            case "xlsx" or "ods" or "csv":
            {
                // a workbook gets a sheet for every page unless told otherwise; a CSV file is one table, so one sheet
                bool sheetPerPage = options.Detail.Text(K.Sheets) switch
                {
                    "pages" => true,
                    "one" => false,
                    _ => dst != "csv"
                };
                return WriteSheets(PdfTableReader.ReadFile(input, sheetPerPage, ct), input, output, dst, options);
            }

            default:
                throw Unsupported("pdf", dst);
        }
    }

    // ---------- spreadsheets ----------

    private static async Task<string> FromSpreadsheetAsync(string input, string src, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        // A spreadsheet program prints a workbook the way it is set up to be printed, and carries its
        // formatting, formulas and charts from one format to the other. (Not for CSV: how its numbers
        // and letters are to be read is worked out here, where a program would go by the system's settings.)
        if (src != "csv" && IsOfficeSheetTarget(dst) && await TryOfficeEnginesAsync(input, output, src, dst, options, ct))
            return output;

        var workbook = src switch
        {
            "xlsx" or "xls" => ExcelReader.ReadFile(input),
            "ods" => OdsReader.ReadFile(input),
            "csv" => CsvReader.ReadFile(input),
            _ => throw Unsupported(src, dst)
        };
        ct.ThrowIfCancellationRequested();
        return WriteSheets(workbook, input, output, dst, options);
    }

    /// <summary>Writes a workbook with the built-in writers; returns the file, or the folder of a CSV file per sheet.</summary>
    private static string WriteSheets(Workbook workbook, string input, string output, string dst, ConversionOptions options)
    {
        var title = Path.GetFileNameWithoutExtension(input);
        switch (dst)
        {
            case "xlsx": XlsxWriter.Write(workbook, output); break;
            case "ods": OdsWriter.Write(workbook, output); break;
            case "csv": return CsvWriter.Write(workbook, output, options);
            case "html": SheetHtmlWriter.Write(workbook, output, title); break;
            case "pdf": SheetPdfWriter.Write(workbook, output, options, title); break;
            default: throw Unsupported(FormatRegistry.GetExtension(input), dst);
        }
        return output;
    }

    // ---------- drawings ----------

    /// <summary>
    /// OpenDocument drawings and Microsoft Publisher files: pages of shapes, text frames and pictures
    /// laid out freely. Nothing here reads them; LibreOffice does, and draws them into a PDF.
    /// </summary>
    private static async Task<string> FromDrawingAsync(string input, string src, string output, string dst, CancellationToken ct)
    {
        if (dst != "pdf") throw Unsupported(src, dst);
        string kind = src == "pub" ? "Publisher files" : "OpenDocument drawings";
        if (ToolLocator.LibreOfficePath == null)
            throw new FriendlyException($"Converting {kind} requires LibreOffice (free, libreoffice.org). Install it and try again.");
        if (await TryLibreOfficeAsync(input, output, "pdf", inputFilter: null, ct)) return output;
        throw new FriendlyException("LibreOffice could not convert this file: it may be damaged, or of a kind it does not read.");
    }

    // ---------- presentations ----------

    private static async Task<string> FromPresentationAsync(string input, string src, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        // A presentation program draws the slides with their design: theme, backgrounds, shapes, fonts.
        if (await TryOfficeEnginesAsync(input, output, src, dst, options, ct)) return output;

        if (src == "ppt")
            throw new FriendlyException(
                "Converting legacy .ppt files requires Microsoft PowerPoint or LibreOffice (free, libreoffice.org). Install one and try again.");

        var deck = src == "pptx" ? PptxReader.ReadFile(input) : OdpReader.ReadFile(input);
        ct.ThrowIfCancellationRequested();
        deck.Title ??= Path.GetFileNameWithoutExtension(input);
        switch (dst)
        {
            case "pdf": SlidePdfWriter.Write(deck, output, Path.GetFileNameWithoutExtension(input)); break;
            case "pptx": PptxWriter.Write(deck, output); break;
            case "odp": OdpWriter.Write(deck, output); break;
            default: throw Unsupported(src, dst);
        }
        return output;
    }

    // ---------- text documents ----------

    private static async Task<string> FromTextDocumentAsync(string input, string src, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        // Both ends are an office program's formats: it keeps the layout.
        if (IsOfficeText(src) && (IsOfficeText(dst) || dst is "pdf" or "txt") &&
            await TryOfficeEnginesAsync(input, output, src, dst, options, ct))
            return output;

        if (src == "doc")
        {
            // Only an office program reads the binary format. It is asked for a .docx, which is read here.
            var staged = WorkFiles.NewPath("doc", ".docx");
            try
            {
                if (!await TryOfficeEnginesAsync(input, staged, "doc", "docx", options, ct))
                    throw new FriendlyException(
                        "Converting legacy .doc files requires Microsoft Word or LibreOffice (free, libreoffice.org). Install one and try again.");
                return await WriteAsync(DocxReader.ReadFile(staged), input, output, dst, options, ct);
            }
            finally
            {
                try { File.Delete(staged); } catch { /* best effort */ }
            }
        }

        if (src is "txt" or "log")
        {
            // plain text has nothing to be laid out by: it keeps its own, simplest writers
            var text = TextFiles.Read(input);
            switch (dst)
            {
                case "pdf":
                    TextToPdf(text, output, options);
                    return output;
                case "docx":
                    TextToDocx(text, output);
                    return output;
                case "doc":
                {
                    // The office engines are handed a docx made here rather than the text file:
                    // its encoding has been worked out already, and they would only guess at it.
                    var staged = WorkFiles.NewPath("text", ".docx");
                    try
                    {
                        TextToDocx(text, staged);
                        if (!await TryOfficeEnginesAsync(staged, output, "docx", dst, options, ct))
                            TextToDocRtf(text, output);
                    }
                    finally
                    {
                        try { File.Delete(staged); } catch { /* best effort */ }
                    }
                    return output;
                }
                default:
                    return await WriteAsync(PlainText(text, Path.GetFileNameWithoutExtension(input)), input, output, dst, options, ct);
            }
        }

        var document = src switch
        {
            "md" => MarkdownReader.ReadFile(input),
            "html" => HtmlReader.ReadFile(input),
            "rtf" => RtfReader.ReadFile(input),
            "docx" => DocxReader.ReadFile(input),
            "odt" => OdtReader.ReadFile(input),
            "epub" => EpubReader.ReadFile(input),
            _ => throw Unsupported(src, dst)
        };
        ct.ThrowIfCancellationRequested();
        return await WriteAsync(document, input, output, dst, options, ct);
    }

    /// <summary>Plain text as a document: lines that follow one another are a paragraph, an empty line ends it.</summary>
    private static RichDocument PlainText(string text, string title)
    {
        var document = new RichDocument { Title = title };
        var lines = new List<Inline>();
        void End()
        {
            if (lines.Count > 0) document.Blocks.Add(new Documents.Paragraph(lines));
            lines = [];
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                End();
                continue;
            }
            if (lines.Count > 0) lines.Add(new Documents.LineBreak());
            lines.Add(new TextRun(line));
        }
        End();
        return document;
    }

    /// <summary>
    /// Writes a document read by the built-in converter. A real ".doc" takes an office program — it
    /// is handed a .docx made here; without one the result is RTF under the .doc name, which Word,
    /// WordPad and LibreOffice open all the same.
    /// </summary>
    private static async Task<string> WriteAsync(RichDocument document, string input, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        if (dst != "doc") return WriteBuiltIn(document, input, output, dst, options);

        var staged = WorkFiles.NewPath("doc", ".docx");
        try
        {
            DocxWriter.Write(document, staged);
            if (!await TryOfficeEnginesAsync(staged, output, "docx", "doc", options, ct))
                RtfWriter.Write(document, output);
            return output;
        }
        finally
        {
            try { File.Delete(staged); } catch { /* best effort */ }
        }
    }

    private static string WriteBuiltIn(RichDocument document, string input, string output, string dst, ConversionOptions options)
    {
        var title = Path.GetFileNameWithoutExtension(input);
        switch (dst)
        {
            case "pdf": PdfWriter.Write(document, output, options, title); break;
            case "docx": DocxWriter.Write(document, output); break;
            case "odt": OdtWriter.Write(document, output); break;
            case "rtf" or "doc": RtfWriter.Write(document, output); break;
            case "html": XhtmlWriter.Write(document, output, title); break;
            case "epub": EpubWriter.Write(document, output, title); break;
            case "txt": PlainTextWriter.Write(document, output); break;
            case "md": return WriteMarkdown(document, output);
            default: throw Unsupported(FormatRegistry.GetExtension(input), dst);
        }
        return output;
    }

    /// <summary>
    /// A Markdown file cannot hold pictures, only name them. A document with pictures therefore
    /// becomes a folder: the text, and an "images" folder beside it that the text refers to.
    /// </summary>
    private static string WriteMarkdown(RichDocument document, string output)
    {
        if (!document.Pictures.Any())
        {
            MarkdownWriter.Write(document, output);
            return output;
        }

        var folder = FrameExtraction.CreateOutputFolder(output, "markdown");
        try
        {
            var name = ConversionService.FinalName(output);
            var pictures = MarkdownWriter.Write(document, Path.Combine(folder, name));
            Directory.CreateDirectory(Path.Combine(folder, "images"));
            foreach (var (path, picture) in pictures)
                File.WriteAllBytes(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)), picture.Data);
            return folder;
        }
        catch
        {
            FrameExtraction.DeleteFolderQuiet(folder);
            throw;
        }
    }

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

    private static async Task<bool> TryOfficeEnginesAsync(string input, string output, string src, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        var allowed = AllowedEngines(options);
        if (allowed.LibreOffice && await TryLibreOfficeAsync(input, output, dst, inputFilter: null, ct)) return true;
        if (allowed.Office && await TryOfficeAutomationAsync(input, output, src, dst, ct)) return true;
        return false;
    }

    /// <summary>
    /// Has an office program write a file in one of the old binary formats, which nothing here
    /// writes: how the self-test comes by a sample of a format the app only reads.
    /// </summary>
    internal static Task<bool> WriteWithOfficeAsync(string input, string output, CancellationToken ct) =>
        TryOfficeEnginesAsync(input, output, FormatRegistry.GetExtension(input), FormatRegistry.GetExtension(output), new ConversionOptions(), ct);

    /// <summary>PDF → editable document: Word reflows the text best, LibreOffice keeps the look.</summary>
    private static async Task<bool> TryPdfImportAsync(string input, string output, string dst,
        ConversionOptions options, CancellationToken ct)
    {
        var allowed = AllowedEngines(options);
        return (allowed.Office && await TryOfficeAutomationAsync(input, output, "pdf", dst, ct)) ||
               (allowed.LibreOffice && await TryLibreOfficeAsync(input, output, dst, inputFilter: "writer_pdf_import", ct));
    }

    private static Task<bool> TryOfficeAutomationAsync(string input, string output, string src, string dst, CancellationToken ct) =>
        PlatformServices.Current.OfficeAutomation is { IsAvailable: true } office && office.CanConvert(src, dst)
            ? office.ConvertAsync(input, output, dst, ct)
            : Task.FromResult(false);

    private static async Task<bool> TryLibreOfficeAsync(string input, string output, string dst, string? inputFilter,
        CancellationToken ct)
    {
        var soffice = ToolLocator.LibreOfficePath;
        if (soffice == null) return false;

        var outDir = WorkFiles.NewPath("office");
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

    // ---------- plain text ----------

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
