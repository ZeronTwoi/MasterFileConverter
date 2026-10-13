using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FileConverter.Core.Pdf;
using ImageMagick;
using ImageMagick.Formats;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Converters;

/// <summary>
/// PDF → one image per page, rendered by PDFium at print resolution (300 dpi) unless a
/// resolution is set. One page becomes one image file; several get a "name pages" folder.
///
/// PDFium draws one page at a time per process, whatever the number of cores. A document of
/// more than a few pages is therefore shared out among several copies of the app, each
/// drawing and encoding its own pages; a short one is done right here. The pixels go straight
/// from PDFium to the PNG or JPEG encoder (<see cref="PixelEncoder"/>).
/// </summary>
public static class PdfPageExport
{
    /// <summary>The command line that makes the app a page worker (<see cref="RunWorker"/>).</summary>
    public const string WorkerCommand = "--render-pages";

    /// <summary>Most processes drawing pages at once, and encoder threads when it all happens in one.</summary>
    private static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 1, 12);

    /// <summary>From this many pages on, starting other processes (about 0.1 s each) pays for itself.</summary>
    private const int PagesWorthSharing = 4;

    private static readonly SemaphoreSlim WorkerSlots = new(Workers);
    private static int _renderingHere;

    internal sealed record PageJob(int Index, int Width, int Height, double Dpi, string Output);

    internal sealed record PageEncoding(string Format, int PngLevel, int JpegQuality, string Chroma, bool Progressive, bool Optimize);

    internal sealed record WorkerRequest(string Input, PageEncoding Encoding, List<PageJob> Pages, int EncodeThreads);

    /// <summary>
    /// Print resolution (300 dpi) for ordinary pages up to A3. Larger pages are almost always
    /// photos or scans placed on an oversized sheet, where 300 dpi would only blow the picture
    /// up past its real detail (and to hundreds of megapixels), so those are held to about
    /// 5000 pixels on the long side — but never below the 96 dpi a screen-sized page is drawn at.
    /// Page sizes are in points (1/72 inch).
    /// </summary>
    private static double RenderDpi(double pageWidth, double pageHeight)
    {
        double longSideInches = Math.Max(pageWidth, pageHeight) / 72.0;
        return Math.Clamp(5100.0 / Math.Max(longSideInches, 0.1), 96.0, 300.0);
    }

    /// <summary>The most pixels one page is rendered at, whatever resolution was asked for (about 1 GB of raw pixels).</summary>
    private const double MaxPagePixels = 250_000_000;

    /// <summary>
    /// "1-3, 7, 10-" → zero-based page numbers in document order. Anything unreadable in the
    /// list is an error the user can fix, not something to guess at.
    /// </summary>
    internal static List<int> ParsePages(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Range(0, pageCount).ToList();

        var selected = new SortedSet<int>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-');
            bool ok = bounds.Length is 1 or 2;
            int first = 1, last = pageCount;
            if (ok && bounds[0].Length > 0) ok = int.TryParse(bounds[0], out first);
            if (ok && bounds.Length == 1) last = first;
            else if (ok && bounds[1].Length > 0) ok = int.TryParse(bounds[1], out last);
            if (!ok || first < 1 || last < first)
                throw new FriendlyException($"\"{part}\" is not a page or a page range. Write pages like 1-3, 7, 10-");

            for (int page = first; page <= Math.Min(last, pageCount); page++) selected.Add(page - 1);
        }
        if (selected.Count == 0)
            throw new FriendlyException($"None of the pages \"{text.Trim()}\" exist — the PDF has {pageCount} page(s).");
        return selected.ToList();
    }

    /// <summary>Renders the chosen pages to png/jpg; returns the image file, or the folder holding several.</summary>
    public static async Task<string> ToImagesAsync(string input, string outputFilePath, string dst,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var detail = options.Detail;
        bool sized = options.HasSizeTarget && !detail.Has(K.Quality);
        var encoding = EncodingFor(dst, options, sized);

        string? folder = null;
        try
        {
            List<PageJob> jobs;
            using (var pdf = new PdfRenderer(input))
            {
                if (pdf.PageCount == 0)
                    throw new FriendlyException("The PDF contains no pages.");

                var pages = ParsePages(detail.Text(K.Pages), pdf.PageCount);
                if (pages.Count > 1) folder = FrameExtraction.CreateOutputFolder(outputFilePath, "pages");
                jobs = Plan(pdf, pages, outputFilePath, folder, dst, options);

                int done = 0;
                void PageDone() => progress?.Report((double)Interlocked.Increment(ref done) / jobs.Count);

                int lanes = WorkersFor(jobs.Count);
                if (lanes == 0)
                {
                    Interlocked.Increment(ref _renderingHere);
                    try
                    {
                        await RenderAsync(pdf, jobs, encoding, Workers, _ => PageDone(), ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _renderingHere);
                    }
                }
                else
                {
                    var left = await RenderInWorkersAsync(input, jobs, encoding, lanes, PageDone, ct);
                    // a worker that could not be started, or stopped half-way: its pages are drawn here instead
                    if (left.Count > 0) await RenderAsync(pdf, left, encoding, Workers, _ => PageDone(), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            return folder ?? outputFilePath;
        }
        catch
        {
            if (folder != null) FrameExtraction.DeleteFolderQuiet(folder);
            throw;
        }
    }

    /// <summary>How many other processes to share this document's pages among; 0 to draw them in this one.</summary>
    private static int WorkersFor(int pageCount)
    {
        if (Engines.SelfCommand == null) return 0;
        if (pageCount >= PagesWorthSharing) return Math.Min(Workers, (pageCount + 1) / 2);
        // another document is being drawn in this process right now, and would only be waited for
        return Volatile.Read(ref _renderingHere) > 0 ? 1 : 0;
    }

    // ---------- what to draw, and how to store it ----------

    /// <summary>Pixel size, resolution and file name of every page to be made.</summary>
    private static List<PageJob> Plan(PdfRenderer pdf, List<int> pages, string outputFilePath, string? folder,
        string dst, ConversionOptions options)
    {
        var detail = options.Detail;
        int? chosenDpi = detail.Int(K.Dpi, 36, 1200);
        int? maxWidth = detail.Int(K.MaxWidth, 1, 65535), maxHeight = detail.Int(K.MaxHeight, 1, 65535);
        // The size slider lowers the render resolution (unless one was set precisely).
        double sizeFactor = options.HasSizeTarget && chosenDpi == null ? Math.Sqrt(options.SizePercent / 100.0) : 1.0;

        var baseName = Path.GetFileNameWithoutExtension(outputFilePath);
        int digits = Math.Max(2, pdf.PageCount.ToString().Length);

        var jobs = new List<PageJob>(pages.Count);
        foreach (int index in pages)
        {
            var (widthPt, heightPt) = pdf.GetPageSize(index);
            double dpi = chosenDpi ?? Math.Max(0.7 * 96.0, RenderDpi(widthPt, heightPt) * sizeFactor);
            double scale = dpi / 72.0;
            // a huge sheet at a high resolution: hold the page to a size that fits in memory
            double pixels = widthPt * scale * heightPt * scale;
            if (pixels > MaxPagePixels) scale *= Math.Sqrt(MaxPagePixels / pixels);

            // a size limit is met by drawing the page smaller, which is sharper than shrinking it afterwards
            double fit = Math.Min(
                maxWidth is { } w ? w / (widthPt * scale) : 1.0,
                maxHeight is { } h ? h / (heightPt * scale) : 1.0);
            if (fit < 1) scale *= fit;

            int width = (int)Math.Max(1, Math.Round(widthPt * scale));
            int height = (int)Math.Max(1, Math.Round(heightPt * scale));
            var target = folder == null
                ? outputFilePath
                : Path.Combine(folder, $"{baseName} {(index + 1).ToString($"D{digits}")}.{dst}");
            jobs.Add(new PageJob(index, width, height, scale * 72.0, target));
        }
        return jobs;
    }

    private static PageEncoding EncodingFor(string dst, ConversionOptions options, bool sized)
    {
        var detail = options.Detail;
        int quality = detail.Int(K.Quality, 1, 100) ?? (sized ? 88 : ImageConversion.JpegTopQuality);
        // full-resolution colour unless asked otherwise; under a size target, what the encoder itself
        // would choose: half-resolution colour below quality 90
        string chroma = detail.Text(K.Chroma) is "444" or "422" or "420"
            ? detail.Text(K.Chroma)!
            : sized && quality < 90 ? "420" : "444";
        return new PageEncoding(dst, ImageConversion.PngLevelFor(options), quality, chroma,
            Progressive: detail.Flag(K.Progressive) == true, Optimize: options.Speed != SpeedPreset.Fastest);
    }

    // ---------- drawing and encoding in this process ----------

    /// <summary>
    /// Draws the pages one after another and encodes them on several threads — turning the pixels
    /// into png/jpg is the slower part. The queue between the two is short, so rendered pages
    /// (35 MB each at 300 dpi A4) can't pile up faster than they are encoded.
    /// </summary>
    internal static async Task RenderAsync(PdfRenderer pdf, IReadOnlyList<PageJob> jobs, PageEncoding encoding,
        int encodeThreads, Action<PageJob>? pageDone, CancellationToken ct)
    {
        var rendered = Channel.CreateBounded<(PageJob Job, PdfPageImage Page)>(encodeThreads);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var render = Task.Run(async () =>
        {
            try
            {
                foreach (var job in jobs)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var page = pdf.Render(job.Index, job.Width, job.Height);
                    try
                    {
                        await rendered.Writer.WriteAsync((job, page), stop.Token);
                    }
                    catch
                    {
                        page.Dispose();
                        throw;
                    }
                }
                rendered.Writer.Complete();
            }
            catch (Exception ex)
            {
                rendered.Writer.TryComplete(ex); // the encoders see the same error and stop
            }
        }, CancellationToken.None);

        var encode = Parallel.ForEachAsync(rendered.Reader.ReadAllAsync(stop.Token),
            new ParallelOptions { MaxDegreeOfParallelism = encodeThreads, CancellationToken = stop.Token },
            (item, _) =>
            {
                using (item.Page) Write(item.Page, item.Job, encoding);
                pageDone?.Invoke(item.Job);
                return ValueTask.CompletedTask;
            });

        try
        {
            await encode;
        }
        finally
        {
            // whatever happened to the encoders, the renderer must be finished before the PDF is closed
            stop.Cancel();
            await render;
            while (rendered.Reader.TryRead(out var orphan)) orphan.Page.Dispose();
        }
    }

    private static void Write(PdfPageImage page, PageJob job, PageEncoding encoding)
    {
        byte[]? bytes = encoding.Format == "png"
            ? PixelEncoder.TryPngFromBgra(page.Bgra, page.Width, page.Height, encoding.PngLevel, job.Dpi)
            : encoding.Progressive
                ? null
                : PixelEncoder.TryJpegFromBgra(page.Bgra, page.Width, page.Height, encoding.JpegQuality, encoding.Chroma, job.Dpi);
        if (bytes != null)
        {
            File.WriteAllBytes(job.Output, bytes);
            return;
        }

        // ImageMagick: a progressive JPEG, or a machine on which Skia cannot be loaded
        ImageEngine.EnsureInitialized();
        using var image = new MagickImage();
        image.ReadPixels(page.Bgra, 0, (uint)(page.Width * page.Height * 4),
            new PixelReadSettings((uint)page.Width, (uint)page.Height, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaAction.Off); // the page was drawn on white; an alpha channel would only cost bytes
        image.Density = new Density(job.Dpi, DensityUnit.PixelsPerInch);
        if (encoding.Format == "png")
        {
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", encoding.PngLevel.ToString());
        }
        else
        {
            image.Quality = (uint)encoding.JpegQuality;
            image.Settings.SetDefines(new JpegWriteDefines
            {
                OptimizeCoding = encoding.Optimize,
                SamplingFactor = encoding.Chroma switch
                {
                    "420" => JpegSamplingFactor.Ratio420,
                    "422" => JpegSamplingFactor.Ratio422,
                    _ => JpegSamplingFactor.Ratio444
                }
            });
            if (encoding.Progressive) image.Settings.Interlace = Interlace.Jpeg;
        }
        image.Write(job.Output, ImageConversion.ToFormat(encoding.Format));
    }

    // ---------- sharing the pages out among other processes ----------

    /// <summary>
    /// Gives every worker process its share of the pages (dealt out in turn, so that a run of
    /// heavy pages is spread over all of them). Returns the pages that still have to be made
    /// because a worker could not do its part; an error the user can act on is thrown as such.
    /// </summary>
    private static async Task<List<PageJob>> RenderInWorkersAsync(string input, IReadOnlyList<PageJob> jobs,
        PageEncoding encoding, int lanes, Action pageDone, CancellationToken ct)
    {
        var shares = Enumerable.Range(0, lanes).Select(_ => new List<PageJob>()).ToList();
        for (int i = 0; i < jobs.Count; i++) shares[i % lanes].Add(jobs[i]);
        int encodeThreads = Math.Clamp(Environment.ProcessorCount / lanes, 1, 3);

        var results = await Task.WhenAll(shares.Select(share =>
            RunWorkerProcessAsync(new WorkerRequest(input, encoding, share, encodeThreads), pageDone, ct)));
        ct.ThrowIfCancellationRequested();
        return results.SelectMany(left => left).ToList();
    }

    private static async Task<List<PageJob>> RunWorkerProcessAsync(WorkerRequest request, Action pageDone, CancellationToken ct)
    {
        var open = request.Pages.ToDictionary(job => job.Index);
        string? refusal = null;

        await WorkerSlots.WaitAsync(ct);
        try
        {
            var command = Engines.SelfCommand!;
            var psi = new ProcessStartInfo(command[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var argument in command.Skip(1)) psi.ArgumentList.Add(argument);
            psi.ArgumentList.Add(WorkerCommand);

            using var process = Process.Start(psi)!;
            await using var registration = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            });

            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None); // drained, never shown
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, PdfPageJson.Default.WorkerRequest));
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (line.StartsWith("page ") && int.TryParse(line.AsSpan(5), out int index) && open.Remove(index))
                    pageDone();
                else if (line.StartsWith("refused "))
                    refusal = line[8..];
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the worker could not be started or talked to: whatever it left open is drawn by the caller
        }
        finally
        {
            WorkerSlots.Release();
        }

        ct.ThrowIfCancellationRequested();
        if (refusal != null) throw new FriendlyException(refusal);
        return open.Values.ToList();
    }

    /// <summary>
    /// The other side: reads one request, makes its pages, and reports each on a line of its own.
    /// "refused …" carries a message meant for the user; any other failure ends the process with
    /// an error code, and the pages it had not reported are made by the process that asked.
    /// </summary>
    public static async Task<int> RunWorker(TextReader input, TextWriter output)
    {
        try
        {
            var request = JsonSerializer.Deserialize(await input.ReadToEndAsync(), PdfPageJson.Default.WorkerRequest)
                ?? throw new InvalidDataException("empty request");
            using var pdf = new PdfRenderer(request.Input);
            await RenderAsync(pdf, request.Pages, request.Encoding, Math.Clamp(request.EncodeThreads, 1, 16), job =>
            {
                lock (output)
                {
                    output.WriteLine($"page {job.Index}");
                    output.Flush();
                }
            }, CancellationToken.None);
            return 0;
        }
        catch (FriendlyException ex)
        {
            output.WriteLine("refused " + ex.Message.ReplaceLineEndings(" "));
            output.Flush();
            return 1;
        }
    }
}

/// <summary>Source-generated JSON for the page worker's request, so it keeps working in a trimmed build.</summary>
[JsonSerializable(typeof(PdfPageExport.WorkerRequest))]
internal sealed partial class PdfPageJson : JsonSerializerContext;
