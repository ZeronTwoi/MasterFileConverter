using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using FileConverter.Core.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using K = FileConverter.Core.Settings.SettingKeys;
using QDocument = QuestPDF.Fluent.Document;

namespace FileConverter.Core.Converters;

/// <summary>
/// SVG → PDF without turning the drawing into pixels: its lines, shapes and letters go into the
/// PDF as lines, shapes and letters, so the page is sharp at any size and its text is still text.
/// The page is as large as the drawing says it is — or a sheet of the chosen paper with the
/// drawing fitted onto it.
/// </summary>
internal static partial class DrawingPdf
{
    public static void Write(string input, string output, ConversionOptions options)
    {
        var svg = HtmlReader.Decode(File.ReadAllBytes(input));
        var (width, height) = NaturalSize(svg);

        // keep pages inside PDF limits and above a readable minimum, as for pictures
        double scale = Math.Min(1.0, 14000.0 / Math.Max(width, height));
        if (Math.Max(width, height) < 144) scale = 144.0 / Math.Max(Math.Max(width, height), 1);
        width *= scale;
        height *= scale;

        var paper = MergeConversion.PaperSize(options.Detail.Text(K.PageSize));
        float margin = (float)(Math.Clamp(options.Detail.Number(K.Margin) ?? 0, 0, 100) * 72.0 / 25.4);

        try
        {
            QDocument.Create(container => container.Page(page =>
            {
                page.Margin(margin);
                if (paper is { } sheet)
                {
                    page.Size(width > height ? sheet.Landscape() : sheet.Portrait());
                    page.Content().AlignCenter().AlignMiddle().Svg(svg).FitArea();
                }
                else
                {
                    page.Size(new PageSize((float)width + 2 * margin, (float)height + 2 * margin));
                    page.Content().Svg(svg).FitArea();
                }
            }))
            .WithMetadata(new DocumentMetadata
            {
                Title = Path.GetFileNameWithoutExtension(input),
                Creator = $"{AppInfo.DisplayName} {AppInfo.Version}",
                Producer = $"{AppInfo.DisplayName} {AppInfo.Version}"
            })
            .GeneratePdf(output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not FriendlyException)
        {
            throw new FriendlyException("This SVG could not be read: it is damaged, or not an SVG drawing.");
        }
    }

    /// <summary>
    /// The size the drawing says it has, in points: its width and height, or — where it gives
    /// none, or only a share of whatever it is placed in — the size of its view box.
    /// </summary>
    internal static (double Width, double Height) NaturalSize(string svg)
    {
        string? width = null, height = null, viewBox = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "svg")
                throw new FriendlyException("This file is not an SVG drawing.");
            width = reader.GetAttribute("width");
            height = reader.GetAttribute("height");
            viewBox = reader.GetAttribute("viewBox");
        }
        catch (XmlException)
        {
            throw new FriendlyException("This SVG could not be read: it is damaged, or not an SVG drawing.");
        }

        double? w = Points(width), h = Points(height);
        var box = viewBox == null ? [] : Separators().Split(viewBox.Trim()).Select(part =>
            double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN).ToArray();
        bool hasBox = box.Length == 4 && box.All(double.IsFinite) && box[2] > 0 && box[3] > 0;

        if (w is > 0 && h is > 0) return (w.Value, h.Value);
        if (hasBox)
        {
            // one side given: the other follows from the view box's proportions; none: its units are pixels
            if (w is > 0) return (w.Value, w.Value * box[3] / box[2]);
            if (h is > 0) return (h.Value * box[2] / box[3], h.Value);
            return (box[2] * 0.75, box[3] * 0.75);
        }
        return (300 * 0.75, 150 * 0.75); // what a browser gives a drawing that says nothing
    }

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex Separators();

    /// <summary>A length as SVG writes it — pixels unless it names a unit — in points; null for a percentage or nothing.</summary>
    private static double? Points(string? length)
    {
        if (string.IsNullOrWhiteSpace(length)) return null;
        length = length.Trim();
        (string unit, double factor) = length switch
        {
            _ when length.EndsWith("px") => ("px", 0.75),
            _ when length.EndsWith("pt") => ("pt", 1.0),
            _ when length.EndsWith("pc") => ("pc", 12.0),
            _ when length.EndsWith("mm") => ("mm", 72 / 25.4),
            _ when length.EndsWith("cm") => ("cm", 72 / 2.54),
            _ when length.EndsWith("in") => ("in", 72.0),
            _ when length.EndsWith('%') || length.EndsWith("em") || length.EndsWith("ex") => ("", 0.0),
            _ => ("", 0.75)
        };
        if (factor == 0) return null;
        return double.TryParse(length[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0 ? value * factor : null;
    }
}
