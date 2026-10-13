using FileConverter.Core.Converters;
using FileConverter.Core.Documents;
using FileConverter.Core.Pdf;
using ImageMagick;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Slides;

/// <summary>
/// A PDF as a presentation: every page becomes a slide of the page's size that shows the page —
/// drawn by PDFium, the way it is for PDF → images, and stored without loss. The slides look
/// exactly like the pages; their text is part of the picture and cannot be edited. (Editable
/// slides are what LibreOffice's PDF import gives, with a look that is close rather than exact.)
/// </summary>
internal static class PdfSlides
{
    /// <summary>What the pages are drawn at unless a resolution is set: sharp on a projector and in print.</summary>
    private const double DefaultDpi = 200;

    /// <summary>The most pixels one slide's picture gets, whatever resolution was asked for.</summary>
    private const double MaxPixels = 40_000_000;

    /// <summary>
    /// Opens the PDF and describes the presentation. The slides are drawn one at a time as the
    /// writer asks for them, so a long document never has all its pages in memory; the PDF stays
    /// open until the returned deck's <see cref="Deck.Close"/> is called.
    /// </summary>
    public static Deck Read(string input, ConversionOptions options, CancellationToken ct)
    {
        var pdf = new PdfRenderer(input);
        try
        {
            if (pdf.PageCount == 0) throw new FriendlyException("The PDF contains no pages.");
            var pages = PdfPageExport.ParsePages(options.Detail.Text(K.Pages), pdf.PageCount);
            double dpi = options.Detail.Int(K.Dpi, 36, 1200) ?? DefaultDpi;
            int pngLevel = ImageConversion.PngLevelFor(options);

            // the slides are as large as the first page; a page of another size is fitted onto them
            var (width, height) = pdf.GetPageSize(pages[0]);
            var deck = new Deck { Width = Math.Max(width, 1), Height = Math.Max(height, 1), Close = pdf.Dispose };
            deck.Stream = Slides(pdf, pages, deck, dpi, pngLevel, ct);
            deck.SlideCount = pages.Count;
            return deck;
        }
        catch
        {
            pdf.Dispose();
            throw;
        }
    }

    private static IEnumerable<Slide> Slides(PdfRenderer pdf, List<int> pages, Deck deck, double dpi, int pngLevel, CancellationToken ct)
    {
        foreach (int index in pages)
        {
            ct.ThrowIfCancellationRequested();
            var (pageWidth, pageHeight) = pdf.GetPageSize(index);
            double scale = dpi / 72.0;
            double pixels = pageWidth * scale * pageHeight * scale;
            if (pixels > MaxPixels) scale *= Math.Sqrt(MaxPixels / pixels);
            int width = (int)Math.Max(1, Math.Round(pageWidth * scale)), height = (int)Math.Max(1, Math.Round(pageHeight * scale));

            byte[] png;
            using (var page = pdf.Render(index, width, height))
                png = PixelEncoder.TryPngFromBgra(page.Bgra, page.Width, page.Height, pngLevel, scale * 72.0) ?? EncodeWithMagick(page);

            var picture = Picture.Load(png, $"Page {index + 1}") ?? throw new FriendlyException($"Page {index + 1} of the PDF could not be drawn.");

            // centred on the slide, as large as fits
            double fit = Math.Min(deck.Width / Math.Max(pageWidth, 1), deck.Height / Math.Max(pageHeight, 1));
            var slide = new Slide();
            slide.Items.Add(new SlidePicture(picture)
            {
                Width = pageWidth * fit,
                Height = pageHeight * fit,
                X = (deck.Width - pageWidth * fit) / 2,
                Y = (deck.Height - pageHeight * fit) / 2
            });
            yield return slide;
        }
    }

    /// <summary>For a machine on which the fast encoder cannot be loaded.</summary>
    private static byte[] EncodeWithMagick(PdfPageImage page)
    {
        ImageEngine.EnsureInitialized();
        using var image = new MagickImage();
        image.ReadPixels(page.Bgra, 0, (uint)(page.Width * page.Height * 4),
            new PixelReadSettings((uint)page.Width, (uint)page.Height, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaAction.Off); // the page was drawn on white
        return image.ToByteArray(MagickFormat.Png);
    }
}
