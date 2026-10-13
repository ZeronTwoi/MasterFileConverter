using System.Diagnostics;
using System.IO.Compression;
using FileConverter.Core.Converters;
using FileConverter.Core.Platform;
using ImageMagick;
using ImageMagick.Drawing;
using QuestPDF.Fluent;
using SharpCompress.Archives;

namespace FileConverter.Core.Diagnostics;

public enum SelfTestOutcome
{
    Passed,
    Failed,
    Skipped
}

public sealed record SelfTestResult(string Name, SelfTestOutcome Outcome, string Detail, TimeSpan Elapsed);

/// <summary>
/// Checks that every conversion the app offers actually works on this machine: makes one small
/// sample per input format, converts it to every target the format registry lists, and inspects
/// each result (an image must decode, a video must still have its video track and length, a PDF
/// must keep its text, an archive must contain the original files, …).
///
/// A conversion that needs something this machine doesn't have — FFmpeg, an office program, the
/// RAR encoder — is reported as skipped with the reason, not as a failure.
/// </summary>
public static class SelfTest
{
    private const string Marker = "Merhaba";
    private const string SampleText = "Merhaba dünya — çğıöşü ÇĞİÖŞÜ.\nThe quick brown fox jumps over the lazy dog.\n";
    private const double ClipSeconds = 2.0;

    /// <summary>
    /// Runs the whole matrix inside <paramref name="workDirectory"/> (created, and emptied first).
    /// <paramref name="filter"/> limits it to cases whose name contains the text, e.g. "mp4" or "→ pdf".
    /// </summary>
    /// <param name="engine">
    /// Has the document conversions done by this engine alone — "builtin", "libreoffice" or
    /// "office" — instead of the best one installed, which is what otherwise hides the others.
    /// </param>
    public static async Task<IReadOnlyList<SelfTestResult>> RunAsync(string workDirectory, string? filter,
        Action<SelfTestResult>? report, CancellationToken ct, string? engine = null)
    {
        if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
        var samplesDir = Path.Combine(workDirectory, "samples");
        var outputDir = Path.Combine(workDirectory, "out");
        Directory.CreateDirectory(samplesDir);
        Directory.CreateDirectory(outputDir);

        var samples = await CreateSamplesAsync(samplesDir, ct);
        var service = new ConversionService();
        var options = new ConversionOptions { Speed = SpeedPreset.Fast };
        if (engine != null)
            options = options.WithDetail(new Settings.DetailSettings(new Dictionary<string, string> { [Settings.SettingKeys.Engine] = engine }));
        bool rar = ToolLocator.RarPath != null;

        var results = new List<SelfTestResult>();
        void Add(SelfTestResult result)
        {
            lock (results) results.Add(result);
            report?.Invoke(result);
        }

        bool Wanted(string name) => filter == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var jobs = new List<Task>();
        foreach (var sample in samples)
        {
            var sourceExt = FormatRegistry.GetExtension(sample.Path ?? "x." + sample.Extension);
            foreach (var target in FormatRegistry.GetTargets(sourceExt, rar))
            {
                var name = $"{sample.Label} → {target}";
                if (!Wanted(name)) continue;

                if (sample.Path == null)
                {
                    Add(new SelfTestResult(name, SelfTestOutcome.Skipped, sample.Missing ?? "no sample", TimeSpan.Zero));
                    continue;
                }
                if (MissingEngine(sourceExt, target, engine) is { } reason)
                {
                    Add(new SelfTestResult(name, SelfTestOutcome.Skipped, reason, TimeSpan.Zero));
                    continue;
                }

                jobs.Add(Task.Run(async () =>
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var dir = Path.Combine(outputDir, Path.GetFileName(sample.Path));
                        Directory.CreateDirectory(dir);
                        var output = ConversionService.BuildOutputPath(sample.Path, target, dir);
                        var result = await service.ConvertAsync(sample.Path, output, options, null, ct);
                        var detail = await VerifyAsync(sample, target, result.OutputPath, ct);
                        if (result.Note != null) detail += $" · {result.Note}";
                        Add(new SelfTestResult(name, SelfTestOutcome.Passed, detail, watch.Elapsed));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Add(new SelfTestResult(name, SelfTestOutcome.Failed, Shorten(ex.Message), watch.Elapsed));
                    }
                }, ct));
            }
        }

        // several files → one file
        var stills = samples.Where(s => s.Label is "png" or "jpg" or "bmp" && s.Path != null).Select(s => s.Path!).ToList();
        if (stills.Count == 3)
        {
            foreach (var target in new[] { "pdf", "gif", "tiff", "zip", "7z", "tar" })
            {
                var name = $"3 images → one {target}";
                if (!Wanted(name)) continue;
                if (MissingEngine("png", target, engine) is { } reason)
                {
                    Add(new SelfTestResult(name, SelfTestOutcome.Skipped, reason, TimeSpan.Zero));
                    continue;
                }

                jobs.Add(Task.Run(async () =>
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var dir = Path.Combine(outputDir, "merge-" + target);
                        Directory.CreateDirectory(dir);
                        var produced = await service.ConvertManyAsync(stills, target, options, dir, null, ct);
                        Add(new SelfTestResult(name, SelfTestOutcome.Passed, VerifyMerged(produced!, target, stills.Count), watch.Elapsed));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Add(new SelfTestResult(name, SelfTestOutcome.Failed, Shorten(ex.Message), watch.Elapsed));
                    }
                }, ct));
            }
        }

        await Task.WhenAll(jobs);
        return results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    private static string Shorten(string message)
    {
        message = message.ReplaceLineEndings(" ").Trim();
        return message.Length > 300 ? message[..300] + "…" : message;
    }

    /// <summary>Why this conversion can't run here, or null when everything it needs is present.</summary>
    private static string? MissingEngine(string sourceExt, string target, string? chosenEngine)
    {
        var engine = FormatRegistry.GetEngine(sourceExt, target);
        if (engine == FileCategory.Video && (ToolLocator.FfmpegPath == null || ToolLocator.FfprobePath == null))
            return "FFmpeg is not installed";
        if (target == "7z" && ToolLocator.SevenZipPath == null)
            return "7-Zip is not installed";
        if (sourceExt == "7z" && ToolLocator.SevenZipPath == null)
            return "7-Zip is not installed";
        if (sourceExt == "doc" && ToolLocator.LibreOfficePath == null && !ToolLocator.OfficeAutomationAvailable)
            return "no office program (Word or LibreOffice) is installed";
        if (sourceExt == "doc" && engine == FileCategory.Document && chosenEngine == "builtin")
            return "only an office program reads the old .doc format";
        if (sourceExt == "ppt" && engine == FileCategory.Document && chosenEngine == "builtin")
            return "only an office program reads the old .ppt format";
        if (sourceExt is "odg" or "pub" && engine == FileCategory.Document && ToolLocator.LibreOfficePath == null)
            return "LibreOffice is not installed (only it reads drawings and Publisher files)";
        return null;
    }

    // ---------- samples ----------

    /// <param name="Label">How the sample is named in results ("png", "gif (animated)").</param>
    /// <param name="Path">The file, or null when it could not be made here.</param>
    /// <param name="Missing">Why it could not be made.</param>
    private sealed record Sample(string Label, string Extension, string? Path, string? Missing = null)
    {
        public bool Animated => Label.Contains("animated");
        public int Frames { get; init; } = 1;
        public IReadOnlyList<string> Contents { get; init; } = [];
    }

    private static async Task<List<Sample>> CreateSamplesAsync(string dir, CancellationToken ct)
    {
        var samples = new List<Sample>();
        samples.AddRange(CreateImageSamples(dir));
        samples.AddRange(await CreateMediaSamplesAsync(dir, ct));
        samples.AddRange(await CreateDocumentSamplesAsync(dir, ct));
        samples.AddRange(await CreateArchiveSamplesAsync(dir, ct));
        return samples;
    }

    private static MagickImage Picture(int shift = 0)
    {
        var image = new MagickImage("gradient:#2f6fd0-#f2c14e", new MagickReadSettings { Width = 320, Height = 240 });
        new Drawables()
            .FillColor(new MagickColor("#d0443c")).Rectangle(30 + shift, 40, 130 + shift, 130)
            .FillColor(new MagickColor("#3fb68b")).Ellipse(220, 150, 60, 45, 0, 360)
            .Draw(image);
        return image;
    }

    /// <summary>
    /// A 64×48 HEIC picture. Nothing the app carries can write the format (it only reads it), so
    /// this one sample is kept ready-made; it was encoded once with Windows' own HEIF encoder.
    /// </summary>
    private const string HeicPicture =
        "AAAAIGZ0eXBoZWljAAAAAG1pZjFoZWljbWlhZk1pSEIAAAE9bWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAAAA" +
        "AAAOcGl0bQAAAAAAAQAAACNpaW5mAAAAAAABAAAAFWluZmUCAAAAAAEAAGh2YzEAAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAAaUAAANr" +
        "AAAAwWlwcnAAAACjaXBjbwAAABRpc3BlAAAAAAAAAEAAAAAwAAAAd2h2Y0MBAUAAAACwAAAAAAAD8AD8/fj4AQALA6AAAQAYQAEMAf//" +
        "AUAAAAMAsAAAAwAAAwADKgJAoQABACpCAQEBQAAAAwCwAAADAAADAAOgIIMXKk7cuTcBDQIEAAADAZAAAAMBkCCiAAEAB0QBwHGAmyAA" +
        "AAAQcGl4aQAAAAADCAgIAAAAFmlwbWEAAAAAAAAAAQABAwGCAwAAAEBmcmVlAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANzbWRhdAAAA2coAa8LwOSXwBdnBB2JrmU4iIbkoML9Rlkq6nFFlwUSUmxl15Rq8poVxV8Z" +
        "EQFnN45FHZ0m7FFTZ9eat1WoHG3YNuLgHmJWI5S2tI/hCadKaxRxC4Qpb6eQPLxsNwfOpJfPbQ2KU/qxzM0M9ehP1Ymo3J7TRvn0nriu" +
        "WcX3SpGRGCycD3nyT5JISJLIviQV9YN7CSlR49RmwtM+eh9TTEfEfpeX9ljgj1C1LBPxanWkebHBuLYsrrCCVlEznYpRk7RTYE8EUbF6" +
        "JwfRgHWjFqelLbd0KjGth5gtLmitWK5jnxo5EwKgv66AoxBfSnoqsVuwBQNTX/mAu/4Hep6s3pjwpUwbOxNLs4OZOsHf4h4yFtnyRaVH" +
        "Cvet9KXe+GOMZPYdfjtv4R7dgbdka8Z2KxcS0N1x08xVFsjxGJrS4xyBay4MAoy9epVWDCPMzwpKsrTBMUluv2Dw00uE/frXQMvdjd+8" +
        "iaX4e/899XMPZceyB3FSEJkNAWVd5c5xXHwnDWq8+CxxVSINjapEa8JA9N5f9yJVqVVc3cN9HrSE/4ZP4Xgf5QLZV9o/kh6bVC2J0fWI" +
        "Yj7w11+Df3KOAAP4R35Jzyo+m66uxsdebN2EmOP6aft4yYc09RA+320EoSSVpl8soPvPSFZa3lwDiUeVf164UJUYgEZ4gDHQKZiUAP30" +
        "/qKvYrfa//E7629CJnoyvOdHpJi0v6FN4X5YDweuxf8ggwisfYHM9fFxEWf9HHtphd+49GI/nDa2CCgp+Dsl6BiXrNssYwbwPXabxuGO" +
        "lnYtN1gh3wclyVnU4crXb97wH/xoUyhvZID0b1oX2iOkQfuOBsZeKWDCU7CQ+UKfEI/KuVvSmsi6d90IstzLdTZe46G1yqMZUTOLjXQn" +
        "rlst+iHN5L/W+WKJFsWVYXjAnTY0MXhA7uDuJZ7mHde1WZ7ITzXiRJJS2UjuoY7SdALJR/QmYpNszZaVwTmB23/bdMmHqajOaG5Zj6//" +
        "fQs8HPBZlybtp7FgVaapJ1ki1P/I85Y+Jzv6LdT6f7h1fFzwdnI4AisDxf/tf8En1waZ7nq6G8MQMPWOer9rJ+aOnfuz0vsJZdp5iqWC" +
        "CP99YBM9bsVFrj0kTT7UuAXIhvx5FLZWvGAd7PAmdTQcmnOikQ8xUUEAcfsLq2ew";

    private static IEnumerable<Sample> CreateImageSamples(string dir)
    {
        Sample ReadyMade(string ext, string base64)
        {
            var path = Path.Combine(dir, $"photo.{ext}");
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return new Sample(ext, ext, path);
        }

        Sample Still(string ext, MagickFormat format, Action<MagickImage>? prepare = null)
        {
            var path = Path.Combine(dir, $"photo.{ext}");
            try
            {
                using var image = Picture();
                prepare?.Invoke(image);
                image.Write(path, format);
                return new Sample(ext, ext, path);
            }
            catch (Exception ex)
            {
                return new Sample(ext, ext, null, $"this build can't write a .{ext} sample ({Shorten(ex.Message)})");
            }
        }

        // the png carries transparency, so the "flatten onto white" path is exercised too
        yield return Still("png", MagickFormat.Png, image =>
        {
            image.Alpha(AlphaAction.Set);
            new Drawables().FillColor(MagickColors.Transparent).Rectangle(250, 10, 310, 60).Draw(image);
        });
        yield return Still("jpg", MagickFormat.Jpeg);
        yield return Still("webp", MagickFormat.WebP);
        yield return Still("bmp", MagickFormat.Bmp);
        yield return Still("tiff", MagickFormat.Tiff);
        yield return Still("gif", MagickFormat.Gif);
        yield return ReadyMade("heic", HeicPicture);

        // a drawing: shapes, a gradient, a line of text, and nothing behind them
        var drawing = Path.Combine(dir, "drawing.svg");
        File.WriteAllText(drawing, """
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" width="320" height="240" viewBox="0 0 320 240">
              <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2f6fd0"/><stop offset="1" stop-color="#f2c14e"/></linearGradient></defs>
              <rect x="20" y="20" width="180" height="120" rx="12" fill="url(#g)"/>
              <circle cx="240" cy="150" r="50" fill="#3fb68b" stroke="#1a1a1a" stroke-width="4"/>
              <path d="M30 200 Q 90 150 150 200 T 270 200" fill="none" stroke="#d0443c" stroke-width="6"/>
              <text x="30" y="230" font-family="sans-serif" font-size="18" fill="#1a1a1a">Merhaba</text>
            </svg>
            """);
        yield return new Sample("svg", "svg", drawing);

        Sample ico;
        try
        {
            var path = Path.Combine(dir, "icon.ico");
            using var sizes = new MagickImageCollection();
            foreach (uint size in new uint[] { 16, 48, 128 })
            {
                var entry = Picture();
                entry.Resize(new MagickGeometry(size, size) { IgnoreAspectRatio = true });
                sizes.Add(entry);
            }
            sizes.Write(path, MagickFormat.Ico);
            ico = new Sample("ico", "ico", path);
        }
        catch (Exception ex)
        {
            ico = new Sample("ico", "ico", null, Shorten(ex.Message));
        }
        yield return ico;

        foreach (var (ext, format) in new[] { ("gif", MagickFormat.Gif), ("webp", MagickFormat.WebP) })
        {
            Sample animated;
            try
            {
                var path = Path.Combine(dir, $"animation.{ext}");
                using var frames = new MagickImageCollection();
                for (int i = 0; i < 5; i++)
                {
                    var frame = Picture(shift: i * 25);
                    frame.AnimationDelay = 10;
                    frames.Add(frame);
                }
                frames.Write(path, format);
                animated = new Sample($"{ext} (animated)", ext, path) { Frames = 5 };
            }
            catch (Exception ex)
            {
                animated = new Sample($"{ext} (animated)", ext, null, Shorten(ex.Message));
            }
            yield return animated;
        }
    }

    private static async Task<List<Sample>> CreateMediaSamplesAsync(string dir, CancellationToken ct)
    {
        var samples = new List<Sample>();
        string[] audioFormats = ["wav", "mp3", "flac", "aac", "ogg", "m4a", "wma", "opus"];
        string[] videoFormats = ["mp4", "mkv", "avi", "webm", "mov", "wmv", "flv", "ts", "m4v"];

        if (ToolLocator.FfmpegPath == null)
        {
            samples.AddRange(audioFormats.Concat(videoFormats).Select(f => new Sample(f, f, null, "FFmpeg is not installed")));
            return samples;
        }

        string tone = $"sine=frequency=440:duration={ClipSeconds}";
        string picture = $"testsrc2=size=320x240:rate=25:duration={ClipSeconds}";

        async Task Make(string ext, params string[] args)
        {
            var path = Path.Combine(dir, $"clip.{ext}");
            var (ok, error, _) = await FfmpegRunner.TryRunAsync([.. args, path], TimeSpan.FromSeconds(120), ct);
            samples.Add(ok && File.Exists(path)
                ? new Sample(ext, ext, path)
                : new Sample(ext, ext, null, $"this FFmpeg can't write a .{ext} sample ({Shorten(error)})"));
        }

        string[] audioIn = ["-f", "lavfi", "-i", tone, "-ac", "2"];
        await Make("wav", [.. audioIn, "-c:a", "pcm_s16le"]);
        await Make("mp3", [.. audioIn, "-c:a", "libmp3lame", "-b:a", "128k"]);
        await Make("flac", [.. audioIn, "-c:a", "flac"]);
        await Make("aac", [.. audioIn, "-c:a", "aac", "-b:a", "128k"]);
        await Make("ogg", [.. audioIn, "-c:a", "libvorbis"]);
        await Make("m4a", [.. audioIn, "-c:a", "aac", "-b:a", "128k"]);
        await Make("wma", [.. audioIn, "-c:a", "wmav2", "-b:a", "128k"]);
        await Make("opus", [.. audioIn, "-ar", "48000", "-c:a", "libopus"]);

        string[] videoIn = ["-f", "lavfi", "-i", picture, "-f", "lavfi", "-i", tone, "-shortest", "-pix_fmt", "yuv420p"];
        string[] h264 = ["-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac", "-b:a", "96k"];
        await Make("mp4", [.. videoIn, .. h264]);
        await Make("mkv", [.. videoIn, .. h264]);
        await Make("mov", [.. videoIn, .. h264]);
        await Make("m4v", [.. videoIn, .. h264]);
        await Make("avi", [.. videoIn, "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "128k"]);
        await Make("webm", [.. videoIn, "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-crf", "30", "-b:v", "0",
            "-ar", "48000", "-c:a", "libopus"]);
        await Make("wmv", [.. videoIn, "-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "128k"]);
        await Make("flv", [.. videoIn, "-c:v", "flv", "-q:v", "4", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "128k"]);
        await Make("ts", [.. videoIn, "-c:v", "mpeg2video", "-q:v", "4", "-c:a", "mp2", "-b:a", "128k"]);

        // keep the registry's order: audio formats first, then video
        return samples;
    }

    private static async Task<List<Sample>> CreateDocumentSamplesAsync(string dir, CancellationToken ct)
    {
        var samples = new List<Sample>();
        foreach (var ext in new[] { "txt", "md", "log" })
        {
            var path = Path.Combine(dir, $"notes.{ext}");
            var text = ext == "md"
                ? "# Notes\n\n" + SampleText.Replace("quick", "**quick**") + "\n- one\n- two\n\n| Item | Count |\n|---|---|\n| fox | 1 |\n"
                : SampleText;
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false), ct);
            samples.Add(new Sample(ext, ext, path));
        }

        // The formats the built-in converter reads: a heading, formatted text, a list, a table and a picture in each.
        var rich = SampleDocument();
        Sample Written(string ext, Action<string> write)
        {
            var path = Path.Combine(dir, $"article.{ext}");
            write(path);
            return new Sample(ext, ext, path);
        }
        samples.Add(Written("rtf", path => Documents.RtfWriter.Write(rich, path)));
        samples.Add(Written("html", path => Documents.XhtmlWriter.Write(rich, path, "Article")));
        samples.Add(Written("odt", path => Documents.OdtWriter.Write(rich, path)));
        samples.Add(Written("epub", path => Documents.EpubWriter.Write(rich, path, "Article")));

        // Spreadsheets: text, a number, a date and a second sheet in each.
        var workbook = SampleWorkbook();
        Sample Table(string ext, Action<string> write)
        {
            var path = Path.Combine(dir, $"figures.{ext}");
            write(path);
            return new Sample(ext, ext, path);
        }
        samples.Add(Table("xlsx", path => Sheets.XlsxWriter.Write(workbook, path)));
        samples.Add(Table("ods", path => Sheets.OdsWriter.Write(workbook, path)));
        samples.Add(Table("xls", path =>
        {
            using var packed = new GZipStream(new MemoryStream(Convert.FromBase64String(OldExcelWorkbook)), CompressionMode.Decompress);
            using var file = File.Create(path);
            packed.CopyTo(file);
        }));
        samples.Add(Table("csv", path => File.WriteAllText(path, "Item,Count,When\r\nMerhaba çay,12.5,2026-10-13\r\nTotal,25,\r\n", new System.Text.UTF8Encoding(true))));

        var docx = Path.Combine(dir, "letter.docx");
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(docx,
                   DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new DocumentFormat.OpenXml.Wordprocessing.Body();
            foreach (var line in SampleText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                body.Append(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(line))));
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);
            main.Document.Save();
        }
        samples.Add(new Sample("docx", "docx", docx));

        // Presentations: a title slide, and a slide with bullets, a picture and a table.
        var deck = SampleDeck();
        var pptx = Path.Combine(dir, "talk.pptx");
        Slides.PptxWriter.Write(deck, pptx);
        samples.Add(new Sample("pptx", "pptx", pptx) { Frames = deck.Slides.Count });
        var odp = Path.Combine(dir, "talk.odp");
        Slides.OdpWriter.Write(deck, odp);
        samples.Add(new Sample("odp", "odp", odp) { Frames = deck.Slides.Count });

        // the old binary .ppt can only be written by an office program
        var pptSample = new Sample("ppt", "ppt", null, "no office program (PowerPoint or LibreOffice) to make a .ppt sample");
        if (ToolLocator.LibreOfficePath != null || PlatformServices.Current.OfficeAutomation?.CanConvert("pptx", "ppt") == true)
        {
            try
            {
                var pptPath = Path.Combine(dir, "talk.ppt");
                if (await DocumentConversion.WriteWithOfficeAsync(pptx, pptPath, ct) && IsOleDocument(pptPath))
                    pptSample = new Sample("ppt", "ppt", pptPath) { Frames = deck.Slides.Count };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pptSample = pptSample with { Missing = Shorten(ex.Message) };
            }
        }
        samples.Add(pptSample);

        // A drawing with a text frame, in the format only LibreOffice reads. (A Publisher file is one
        // only Microsoft Publisher writes, so there is no sample of it to make.)
        var odg = Path.Combine(dir, "poster.odg");
        using (var package = new Documents.OdfPackage(odg, "application/vnd.oasis.opendocument.graphics"))
        {
            package.Add("content.xml", System.Text.Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="UTF-8"?>
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                    xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                    xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:version="1.3">
                  <office:body><office:drawing><draw:page draw:name="page1">
                    <draw:frame svg:x="2cm" svg:y="2cm" svg:width="12cm" svg:height="2cm">
                      <draw:text-box><text:p>Merhaba from a drawing</text:p></draw:text-box>
                    </draw:frame>
                  </draw:page></office:drawing></office:body>
                </office:document-content>
                """), "text/xml");
        }
        samples.Add(new Sample("odg", "odg", odg));
        samples.Add(new Sample("pub", "pub", null, "only Microsoft Publisher writes .pub files, so there is no sample to try"));

        // five pages: enough for the page pictures to be shared out among worker processes
        const int pdfPages = 5;
        var pdf = Path.Combine(dir, "report.pdf");
        QuestPDF.Fluent.Document.Create(container =>
        {
            for (int pageNumber = 1; pageNumber <= pdfPages; pageNumber++)
            {
                int number = pageNumber;
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.A5);
                    page.Margin(36);
                    page.Content().Text($"{Marker} — page {number}. The quick brown fox jumps over the lazy dog.");
                });
            }
        }).GeneratePdf(pdf);
        samples.Add(new Sample("pdf", "pdf", pdf) { Frames = pdfPages });

        // a legacy binary .doc can only be written by an office program
        var docSample = new Sample("doc", "doc", null, "no office program (Word or LibreOffice) to make a .doc sample");
        if (ToolLocator.LibreOfficePath != null || ToolLocator.OfficeAutomationAvailable)
        {
            try
            {
                var docPath = Path.Combine(dir, "letter.doc");
                await DocumentConversion.ConvertAsync(docx, docPath, new ConversionOptions(), null, ct);
                if (File.Exists(docPath) && IsOleDocument(docPath)) docSample = new Sample("doc", "doc", docPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                docSample = docSample with { Missing = Shorten(ex.Message) };
            }
        }
        samples.Add(docSample);
        return samples;
    }

    private static Documents.RichDocument SampleDocument()
    {
        using var image = Picture();
        var picture = Documents.Picture.Load(image.ToByteArray(MagickFormat.Png), "A sample picture")!;

        var document = new Documents.RichDocument { Title = "Article", Author = "Self-test" };
        document.Blocks.Add(new Documents.Heading(1, [new Documents.TextRun("Article")]));
        document.Blocks.Add(new Documents.Paragraph(
        [
            new Documents.TextRun("Merhaba dünya — çğıöşü ÇĞİÖŞÜ. The "),
            new Documents.TextRun("quick", Documents.TextStyle.Bold),
            new Documents.TextRun(" brown fox jumps over the "),
            new Documents.TextRun("lazy", Documents.TextStyle.Italic),
            new Documents.TextRun(" dog.")
        ]));
        var list = new Documents.ListBlock(ordered: false);
        list.Items.Add([new Documents.Paragraph("one")]);
        list.Items.Add([new Documents.Paragraph("two")]);
        document.Blocks.Add(list);
        var table = new Documents.Table { HeaderRows = 1 };
        table.Rows.Add([new Documents.TableCell([new Documents.Paragraph("Item")]), new Documents.TableCell([new Documents.Paragraph("Count")])]);
        table.Rows.Add([new Documents.TableCell([new Documents.Paragraph("fox")]), new Documents.TableCell([new Documents.Paragraph("1")])]);
        document.Blocks.Add(table);
        document.Blocks.Add(picture);
        return document;
    }

    /// <summary>
    /// A workbook in Excel's old binary format (.xls): the same cells as <see cref="SampleWorkbook"/>.
    /// Nothing the app carries writes that format (it only reads it), so this sample is kept
    /// ready-made, compressed; it was saved once with LibreOffice.
    /// </summary>
    private const string OldExcelWorkbook =
        "H4sIAAAAAAACCu1YXWhcRRQ+c/cnd/O3N39aWxtvq23+S8UHI1oTm7Y2YGzRSIsE7GZzN4ndzZbdDRgwuk0tFLSgVvDBQqnEhyL4g6AP" +
        "Kibggw+K0tIHxYfUlyKK3YoihSTrN+fO7t6uG9ndRsGas3wzd878zzlzzpz95uu6hbPvr79EeXQ/uWg57SOvgycAX6ZgEGmKt5xOpzPs" +
        "9Br9p2hJ5VKGbsjPA0iZVwC6km2lytfo5qPHKIpfgkzaTRPIYzRFpdAt0BjneMX0WS6yXbG0Nn/580v7Le24Cyh0/6W9l/e/CqgGaoBa" +
        "wG+7AKoD6oEGoBFoYp0guhVYB9wGrAc2ALcDG4Fm4A7ABDYBm4E7gbuALWp9LSqXaMd3B9CpeNvW7NGqkU+HFL0e+rjmSylylv0laMR7" +
        "7nnWix+AIToidaMvEA6a/xbt5DUEhFzDHJR0h3BDVzU6DdTSu8z/hNMdrH+gXjK7lU4f1Hp59Sc53cxpLVJBH3Gf75hzNzTzC3l/nn+Z" +
        "r8Gb5BEPwQKOU4DC+bW0mrXN9L2jVlCF6OOacRrmVs3uapqVd/Bha8KKBcLN7jp6S96/KdBQVyQy1DUyssAXbpZ+T5uOyz1nSr4cl/m/" +
        "FcfXSuTT/44/i7MrdM4ddvtUPr9tBX7nCvz2Ffi+gnwP1IHXs5jjn9JwSZOUlrmRdHFel/RyXp90c96QrOC8MelJH+KrcBwm3ZRWH2M+" +
        "Hghbcck4UWEzHo0mrPhLJK+eoHmhA0SDJwX9BEWFC0g9yXN7U3wyuu1GNMqFBsLxfQ9VpeRV/QwNdToq4Fr0OdGLtJearxq2c9Gh5Dqc" +
        "h/4H/MsiDbJB8trOqT9hRbDSvujkRALFA2PWBNzSgBUbCwwHzMuBKVQORhOB8MYcNxSLRszEmGXGrWB0YsSMj1lWIg2XptM5t+3SgnoT" +
        "kJNsk8o16fds+2hcZx+rsaURbF/akzrenoHhFs9dPT8wvL/nKeYnKTfiFikYnMFR1My727lHB6cz3DbjN48x7wVOt6L/lV8kXe5pcXy3" +
        "4vtCy/T5lukrPW2O77OwcgKrIhaVRp2iU6yr9h6bP7TQk8kFPYEanaZ520Rv1Pg5l2XZS1Tkyhp+M9W5MgQrZnS/OplazlzZVNeMzLew" +
        "TbCfeYJ5jdfxlHpkeUv8vrBfFKRKAiWRLWl81nZJcEs556ew+4Jbvtggz+/VC3LNso3GbeSZP6dV8gn7KYTSz7RfjpTaa0sspSvFlE0x" +
        "Zmq73S/VynxfyijwdtKokvvJPvLWPKjV04de2/0QPcASkK8cvzodtU+iUWj1qO4cSGTeaze7noU2OfXsr3oksmlGj0RB/fDkydB0ylAv" +
        "Q4a6Q4Z04zIshwResq5K+w1v6Jptyu2qPfLNPDAejEXj0VDC3P1M0Aqb993bNRgYtsJhi1Vk53go1H0jsYOw7XPZtJwm1oMC49LC8TO/" +
        "Xts3Zrz9ik4dLR98K2XzmoothDpt2XOvijMOqnMcUfHGERVzPKvijhOq/Y9LdgwhJbFnfHQyZsW5LOu2q3EzVMz3P7F/tnIXv7p4etsG" +
        "49Tr2H/ntXd2SZ+axxtScVLGWRqOPa7EX01azfif15mnw4X6yFs1V2V/H8DsMTqM54Rcx+GS11/PnsqOXdMlxNWP+DJ62od5I9C1fVjD" +
        "02XN73LE4MX02Qr0Z+/JPrz7rbLlJ/1bqfF/q4rV1aOPJrH/COIPKft+aEGIZSI5CcQiUXD+bix7fk8J598GfJ6dfxdmCPIaLNbA0tbT" +
        "Xcb+u4DE2v8/TH8CgUgirQAYAAA=";

    private static Slides.Deck SampleDeck()
    {
        using var image = Picture();
        var picture = Documents.Picture.Load(image.ToByteArray(MagickFormat.Png), "A sample picture")!;

        Slides.SlideText Text(double x, double y, double width, double height, bool title, params (string Text, int Level, bool Bullet, double Size)[] lines)
        {
            var box = new Slides.SlideText { X = x, Y = y, Width = width, Height = height, IsTitle = title, Anchor = title ? Slides.VerticalAnchor.Middle : Slides.VerticalAnchor.Top };
            foreach (var line in lines)
            {
                var paragraph = new Slides.SlideParagraph { Level = line.Level, Bullet = line.Bullet, FontSize = line.Size, Align = title ? Documents.TextAlign.Center : Documents.TextAlign.Start };
                paragraph.Runs.Add(new Documents.TextRun(line.Text));
                box.Paragraphs.Add(paragraph);
            }
            return box;
        }

        var deck = new Slides.Deck { Title = "Talk" };
        var first = new Slides.Slide();
        first.Items.Add(Text(80, 160, 800, 110, true, ("Merhaba sunum", 0, false, 54)));
        first.Items.Add(Text(80, 290, 800, 60, true, ("çğıöşü ÇĞİÖŞÜ", 0, false, 24)));
        deck.Slides.Add(first);

        var second = new Slides.Slide();
        second.Items.Add(Text(50, 30, 860, 70, true, ("Merhaba again", 0, false, 40)));
        second.Items.Add(Text(50, 120, 420, 220, false, ("The quick brown fox", 0, true, 24), ("jumps over", 1, true, 20), ("the lazy dog", 0, true, 24)));
        second.Items.Add(new Slides.SlidePicture(picture) { X = 520, Y = 120, Width = 320, Height = 240 });
        var table = new Slides.SlideTable { X = 50, Y = 380, Width = 420, Height = 80, FontSize = 16 };
        table.Rows.Add(["Item", "Count"]);
        table.Rows.Add(["fox", "1"]);
        second.Items.Add(table);
        deck.Slides.Add(second);
        return deck;
    }

    private static Sheets.Workbook SampleWorkbook()
    {
        var workbook = new Sheets.Workbook { Title = "Figures" };
        var sales = new Sheets.Sheet("Sales");
        sales.Rows.Add([Sheets.Cell.OfText("Item"), Sheets.Cell.OfText("Count"), Sheets.Cell.OfText("When")]);
        sales.Rows.Add([Sheets.Cell.OfText("Merhaba çay"), Sheets.Cell.OfNumber(12.5), Sheets.Cell.OfDate(new DateTime(2026, 10, 13))]);
        sales.Rows.Add([Sheets.Cell.OfText("Total"), Sheets.Cell.OfNumber(25)]);
        var notes = new Sheets.Sheet("Notes");
        notes.Rows.Add([Sheets.Cell.OfText("Merhaba from the second sheet")]);
        workbook.Sheets.Add(sales);
        workbook.Sheets.Add(notes);
        return workbook;
    }

    private static bool IsOleDocument(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return stream.Read(head) == 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
    }

    private static readonly string[] ArchiveContents = ["a.txt", "sub/b.txt", "picture.png"];

    private static async Task<List<Sample>> CreateArchiveSamplesAsync(string dir, CancellationToken ct)
    {
        var samples = new List<Sample>();
        var content = Path.Combine(dir, "archive-content");
        Directory.CreateDirectory(Path.Combine(content, "sub"));
        await File.WriteAllTextAsync(Path.Combine(content, "a.txt"), SampleText, ct);
        await File.WriteAllTextAsync(Path.Combine(content, "sub", "b.txt"), "second file\n", ct);
        using (var image = Picture()) image.Write(Path.Combine(content, "picture.png"), MagickFormat.Png);

        var zip = Path.Combine(dir, "bundle.zip");
        ZipFile.CreateFromDirectory(content, zip);
        samples.Add(new Sample("zip", "zip", zip) { Contents = ArchiveContents });

        var tar = Path.Combine(dir, "bundle.tar");
        await using (var stream = File.Create(tar))
            await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(content, stream, includeBaseDirectory: false, ct);
        samples.Add(new Sample("tar", "tar", tar) { Contents = ArchiveContents });

        // a compressed tar under both of its usual names, and a single compressed file
        foreach (var (label, fileName) in new[] { ("gz (tar.gz)", "bundle.tar.gz"), ("tgz", "bundle.tgz") })
        {
            var path = Path.Combine(dir, fileName);
            await using (var source = File.OpenRead(tar))
            await using (var target = new GZipStream(File.Create(path), CompressionLevel.Fastest))
                await source.CopyToAsync(target, ct);
            samples.Add(new Sample(label, FormatRegistry.GetExtension(path), path) { Contents = ArchiveContents });
        }

        var gz = Path.Combine(dir, "a.txt.gz");
        await using (var source = File.OpenRead(Path.Combine(content, "a.txt")))
        await using (var target = new GZipStream(File.Create(gz), CompressionLevel.Fastest))
            await source.CopyToAsync(target, ct);
        samples.Add(new Sample("gz (single file)", "gz", gz) { Contents = ["a.txt"] });

        var bz2 = Path.Combine(dir, "bundle.tar.bz2");
        await using (var source = File.OpenRead(tar))
        await using (var target = new SharpCompress.Compressors.BZip2.BZip2Stream(File.Create(bz2),
                         SharpCompress.Compressors.CompressionMode.Compress, false))
            await source.CopyToAsync(target, ct);
        samples.Add(new Sample("bz2 (tar.bz2)", "bz2", bz2) { Contents = ArchiveContents });

        samples.Add(await ExternalArchiveAsync("7z", ToolLocator.SevenZipPath, ["a", "-t7z", "-y", "-bd"], content, dir,
            "7-Zip is not installed", ct));
        samples.Add(await ExternalArchiveAsync("rar", ToolLocator.RarPath, ["a", "-r", "-y"], content, dir,
            "no RAR encoder to make a .rar sample (reading .rar needs none)", ct));
        return samples;
    }

    private static async Task<Sample> ExternalArchiveAsync(string ext, string? tool, string[] arguments, string content,
        string dir, string missing, CancellationToken ct)
    {
        if (tool == null) return new Sample(ext, ext, null, missing);

        var path = Path.Combine(dir, $"bundle.{ext}");
        var psi = new ProcessStartInfo(tool)
        {
            WorkingDirectory = content,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("*");

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode == 0 && File.Exists(path)
            ? new Sample(ext, ext, path) { Contents = ArchiveContents }
            : new Sample(ext, ext, null, $"could not make a .{ext} sample ({Shorten(await stderr)})");
    }

    // ---------- checking results ----------

    private static async Task<string> VerifyAsync(Sample sample, string target, string produced, CancellationToken ct)
    {
        var sourceCategory = FormatRegistry.GetCategory(sample.Extension);

        if (target == "md")
        {
            // a document with pictures becomes a folder: the text, and the pictures it names beside it
            var file = Directory.Exists(produced) ? Directory.GetFiles(produced, "*.md").Single() : produced;
            var markdown = await File.ReadAllTextAsync(file, ct);
            RequireMarker(markdown);
            foreach (System.Text.RegularExpressions.Match picture in System.Text.RegularExpressions.Regex.Matches(markdown, @"!\[[^\]]*\]\(([^)]+)\)"))
                CheckImage(Path.Combine(Path.GetDirectoryName(file)!, picture.Groups[1].Value));
            return Size(new FileInfo(file).Length) + (Directory.Exists(produced) ? " · with pictures" : "");
        }

        if (target == "csv")
        {
            // a workbook of several sheets becomes a folder with a file for each
            var files = Directory.Exists(produced) ? Directory.GetFiles(produced, "*.csv") : [produced];
            if (files.Length == 0) throw new InvalidOperationException("the output folder is empty");
            var text = string.Join("\n", files.Select(File.ReadAllText));
            RequireMarker(text);
            return Directory.Exists(produced) ? $"{files.Length} files" : Size(new FileInfo(produced).Length);
        }

        if (Directory.Exists(produced))
        {
            var files = Directory.GetFiles(produced, "*." + target);
            if (files.Length == 0) throw new InvalidOperationException("the output folder is empty");
            foreach (var file in files) CheckImage(file);

            if (sample.Animated && files.Length != sample.Frames)
                throw new InvalidOperationException($"expected {sample.Frames} frames, got {files.Length}");
            if (sample.Extension == "pdf" && files.Length != sample.Frames)
                throw new InvalidOperationException($"expected {sample.Frames} pages, got {files.Length}");
            return $"{files.Length} files";
        }

        var size = new FileInfo(produced).Length;
        if (size == 0) throw new InvalidOperationException("the output file is empty");
        var detail = Size(size);

        switch (target)
        {
            case "zip" or "7z" or "tar" or "rar":
                var expected = sourceCategory == FileCategory.Archive ? sample.Contents : [Path.GetFileName(sample.Path!)];
                CheckArchive(produced, expected);
                break;

            case "jpg" or "png" or "webp" or "bmp" or "tiff" or "gif" when sourceCategory != FileCategory.Video || target == "gif":
                var (width, _) = CheckImage(produced);
                if (target is "gif" or "webp" && sample.Animated && FrameCount(produced) != sample.Frames)
                    throw new InvalidOperationException("the animation lost frames");
                if (target == "tiff" && sample.Extension == "pdf" && FrameCount(produced) != sample.Frames)
                    throw new InvalidOperationException($"expected {sample.Frames} pages, got {FrameCount(produced)}");
                detail += $" · {width} px wide";
                break;

            case "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "opus":
            {
                var info = await FfmpegRunner.ProbeAsync(produced, ct);
                if (!info.HasAudio) throw new InvalidOperationException("the output has no audio");
                CheckDuration(info.DurationSeconds);
                break;
            }

            case "mp4" or "mkv" or "avi" or "webm" or "mov":
            {
                var info = await FfmpegRunner.ProbeAsync(produced, ct);
                if (!info.HasVideo) throw new InvalidOperationException("the output has no video");
                if (sourceCategory == FileCategory.Video && !info.HasAudio)
                    throw new InvalidOperationException("the output lost its audio track");
                // a picture source plays for its frames (10 per second in the samples), a clip for its length
                CheckDuration(info.DurationSeconds, sourceCategory == FileCategory.Image ? sample.Frames * 0.1 : ClipSeconds);
                detail += $" · {info.Video!.Codec}";
                break;
            }

            case "pdf":
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(produced);
                if (pdf.NumberOfPages < 1) throw new InvalidOperationException("the PDF has no pages");
                if (sourceCategory == FileCategory.Document)
                    RequireMarker(string.Join(" ", pdf.GetPages().Select(p => p.Text)));
                detail += $" · {pdf.NumberOfPages} page(s)";
                break;
            }

            case "txt" or "html":
                RequireMarker(await File.ReadAllTextAsync(produced, ct));
                break;

            case "rtf":
            {
                var rtf = await File.ReadAllTextAsync(produced, ct);
                if (!rtf.StartsWith("{\\rtf", StringComparison.Ordinal)) throw new InvalidOperationException("the output is not RTF");
                RequireMarker(rtf);
                break;
            }

            case "odt":
                RequireMarker(ZippedText(produced, "application/vnd.oasis.opendocument.text", name => name == "content.xml"));
                break;

            case "ods":
                RequireMarker(ZippedText(produced, "application/vnd.oasis.opendocument.spreadsheet", name => name == "content.xml"));
                break;

            case "odp":
            {
                var slides = ZippedText(produced, "application/vnd.oasis.opendocument.presentation", name => name == "content.xml");
                // slides made from a PDF show its pages as pictures: there the pages are counted instead
                if (sample.Extension == "pdf")
                {
                    int pages = Slides.OdpReader.ReadFile(produced).Slides.Count;
                    if (pages != sample.Frames) throw new InvalidOperationException($"expected {sample.Frames} slides, got {pages}");
                }
                else RequireMarker(slides);
                break;
            }

            case "pptx":
            {
                var deck = Slides.PptxReader.ReadFile(produced);
                if (deck.Slides.Count != sample.Frames)
                    throw new InvalidOperationException($"expected {sample.Frames} slides, got {deck.Slides.Count}");
                // slides made from a PDF show its pages as pictures — unless LibreOffice made them, as text that can be edited
                if (sample.Extension != "pdf" || deck.Slides.Any(slide => !slide.Items.OfType<Slides.SlidePicture>().Any()))
                {
                    RequireMarker(string.Join(" ", deck.Slides.SelectMany(slide => slide.Items).OfType<Slides.SlideText>()
                        .SelectMany(text => text.Paragraphs).Select(paragraph => Documents.RichDocument.PlainText(paragraph.Runs))));
                }
                detail += $" · {deck.Slides.Count} slide(s)";
                break;
            }

            case "xlsx":
            {
                var cells = Sheets.ExcelReader.ReadFile(produced).Sheets.SelectMany(sheet => sheet.Rows).SelectMany(row => row).ToList();
                RequireMarker(string.Join(" ", cells.Select(cell => cell.Text)));
                // what was a number in a spreadsheet is still one
                if (sample.Extension is "ods" or "xls" or "csv" && !cells.Any(cell => cell.Kind == Sheets.CellKind.Number && cell.Number == 12.5))
                    throw new InvalidOperationException("the numbers became text");
                break;
            }

            case "epub":
                RequireMarker(ZippedText(produced, "application/epub+zip", name => name.EndsWith(".xhtml") || name.EndsWith(".html")));
                break;

            case "docx":
            {
                using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(produced, false);
                RequireMarker(doc.MainDocumentPart?.Document?.Body?.InnerText ?? "");
                break;
            }

            case "doc":
            {
                // a real binary document from an office program, or RTF from the built-in writer
                var head = new byte[5];
                await using (var stream = File.OpenRead(produced)) _ = await stream.ReadAsync(head, ct);
                bool rtf = System.Text.Encoding.ASCII.GetString(head) == "{\\rtf";
                if (!rtf && !IsOleDocument(produced)) throw new InvalidOperationException("the output is not a Word document");
                break;
            }
        }
        return detail;
    }

    private static string VerifyMerged(string produced, string target, int count)
    {
        switch (target)
        {
            case "pdf":
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(produced))
                    if (pdf.NumberOfPages != count)
                        throw new InvalidOperationException($"expected {count} pages, got {pdf.NumberOfPages}");
                break;
            case "gif" or "tiff":
                if (FrameCount(produced) != count)
                    throw new InvalidOperationException($"expected {count} pictures, got {FrameCount(produced)}");
                break;
            default:
                CheckArchive(produced, ["photo.png", "photo.jpg", "photo.bmp"]);
                break;
        }
        return Size(new FileInfo(produced).Length);
    }

    /// <summary>
    /// The text of the chosen files in a zipped document (OpenDocument, EPUB). Its first entry
    /// has to be the "mimetype" that says what the file is — programs refuse it otherwise.
    /// </summary>
    private static string ZippedText(string path, string mimeType, Func<string, bool> wanted)
    {
        using var zip = ZipFile.OpenRead(path);
        var first = zip.Entries.FirstOrDefault();
        if (first?.FullName != "mimetype") throw new InvalidOperationException("the file does not begin with its type");
        using (var reader = new StreamReader(first.Open()))
            if (reader.ReadToEnd() != mimeType) throw new InvalidOperationException("the file names another type than its own");

        var text = new System.Text.StringBuilder();
        foreach (var entry in zip.Entries.Where(e => wanted(e.FullName)))
        {
            using var reader = new StreamReader(entry.Open());
            // well-formed, or no program opens it
            text.Append(System.Xml.Linq.XDocument.Parse(reader.ReadToEnd()).Root!.Value).Append(' ');
        }
        return text.ToString();
    }

    private static string Size(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : $"{Math.Max(bytes / 1024.0, 0.1):0.#} KB";

    private static void RequireMarker(string text)
    {
        if (!text.Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("the text did not survive the conversion");
    }

    private static void CheckDuration(double seconds, double expected = ClipSeconds)
    {
        if (Math.Abs(seconds - expected) > Math.Max(0.5, expected * 0.25))
            throw new InvalidOperationException($"the output runs {seconds:0.##} s instead of {expected:0.##} s");
    }

    private static (uint Width, uint Height) CheckImage(string path)
    {
        var info = new MagickImageInfo(path);
        if (info.Width == 0 || info.Height == 0) throw new InvalidOperationException("the image has no size");
        return (info.Width, info.Height);
    }

    private static int FrameCount(string path) => MagickImageInfo.ReadCollection(path).Count();

    /// <summary>The archive must open and hold exactly the expected files — nothing missing, nothing extra.</summary>
    private static void CheckArchive(string path, IReadOnlyList<string> expected)
    {
        using var archive = ArchiveFactory.Open(path);
        var names = archive.Entries.Where(e => !e.IsDirectory)
            .Select(e => (e.Key ?? "").Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = expected.Where(name => !names.Contains(name)).ToList();
        var extra = names.Where(name => !expected.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"the archive is missing {string.Join(", ", missing)} (it holds: {string.Join(", ", names.Take(6))})");
        if (extra.Count > 0)
            throw new InvalidOperationException($"the archive holds files that were not in the source: {string.Join(", ", extra.Take(6))}");
    }
}
