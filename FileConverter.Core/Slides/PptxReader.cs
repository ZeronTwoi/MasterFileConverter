using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileConverter.Core.Documents;
using SkiaSharp;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace FileConverter.Core.Slides;

/// <summary>
/// Reads a PowerPoint presentation (.pptx): every slide's text boxes, pictures and tables with
/// their places on the slide. Much of a slide is not written on the slide itself but inherited —
/// where a title stands, how large its type is, whether a line has a bullet come from the
/// slide's layout and from the master behind that — so each of these is looked up along that
/// chain, as PowerPoint does.
/// </summary>
internal sealed class PptxReader
{
    private const double PointsPerUnit = 1 / 12700.0; // the format measures in EMU: 914 400 to an inch

    /// <summary>Where a box of a group's inner coordinates lies on the slide.</summary>
    private delegate (double X, double Y, double Width, double Height) Placement(double x, double y, double width, double height);

    public static Deck ReadFile(string path)
    {
        PresentationDocument file;
        try
        {
            file = PresentationDocument.Open(path, isEditable: false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new FriendlyException("This file is not a PowerPoint presentation, is damaged, or is protected by a password.");
        }

        using (file)
        {
            var deck = new Deck { Title = string.IsNullOrWhiteSpace(file.PackageProperties.Title) ? null : file.PackageProperties.Title.Trim() };
            var presentation = file.PresentationPart?.Presentation;
            if (presentation == null) return deck;

            if (presentation.SlideSize is { Cx: not null, Cy: not null } size && size.Cx.Value > 0 && size.Cy.Value > 0)
            {
                deck.Width = size.Cx.Value * PointsPerUnit;
                deck.Height = size.Cy.Value * PointsPerUnit;
            }

            var reader = new PptxReader();
            foreach (var id in presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
            {
                if (id.RelationshipId?.Value is not { } relationship) continue;
                if (file.PresentationPart!.GetPartById(relationship) is SlidePart part) deck.Slides.Add(reader.ReadSlide(part));
            }
            return deck;
        }
    }

    private Slide ReadSlide(SlidePart part)
    {
        var slide = new Slide();
        var tree = part.Slide?.CommonSlideData?.ShapeTree;
        if (tree != null) ReadShapes(tree, part, slide, (x, y, width, height) => (x, y, width, height));
        return slide;
    }

    private void ReadShapes(OpenXmlElement container, SlidePart part, Slide slide, Placement place)
    {
        foreach (var element in container.ChildElements)
        {
            switch (element)
            {
                case P.Shape shape:
                    ReadShape(shape, part, slide, place);
                    break;

                case P.Picture picture:
                {
                    var box = Box(picture.ShapeProperties?.Transform2D) ?? InheritedBox(Placeholder(picture.NonVisualPictureProperties?.ApplicationNonVisualDrawingProperties), part);
                    if (box is not { } frame) break;
                    var loaded = LoadPicture(picture.BlipFill, part);
                    if (loaded != null) slide.Items.Add(Placed(new SlidePicture(loaded), place, frame));
                    break;
                }

                case P.GroupShape group:
                {
                    // a group has its own inner coordinates, which its transform maps onto the slide
                    var transform = group.GroupShapeProperties?.TransformGroup;
                    double offsetX = (transform?.Offset?.X?.Value ?? 0) * PointsPerUnit, offsetY = (transform?.Offset?.Y?.Value ?? 0) * PointsPerUnit;
                    double width = (transform?.Extents?.Cx?.Value ?? 0) * PointsPerUnit, height = (transform?.Extents?.Cy?.Value ?? 0) * PointsPerUnit;
                    double innerX = (transform?.ChildOffset?.X?.Value ?? 0) * PointsPerUnit, innerY = (transform?.ChildOffset?.Y?.Value ?? 0) * PointsPerUnit;
                    double innerWidth = (transform?.ChildExtents?.Cx?.Value ?? 0) * PointsPerUnit, innerHeight = (transform?.ChildExtents?.Cy?.Value ?? 0) * PointsPerUnit;
                    double scaleX = innerWidth > 0 ? width / innerWidth : 1, scaleY = innerHeight > 0 ? height / innerHeight : 1;

                    ReadShapes(group, part, slide, (x, y, w, h) =>
                        place(offsetX + (x - innerX) * scaleX, offsetY + (y - innerY) * scaleY, w * scaleX, h * scaleY));
                    break;
                }

                case P.GraphicFrame frame:
                    ReadTable(frame, slide, place);
                    break;
            }
        }
    }

    private static T Placed<T>(T item, Placement place, (double X, double Y, double Width, double Height) box) where T : SlideItem
    {
        (item.X, item.Y, item.Width, item.Height) = place(box.X, box.Y, box.Width, box.Height);
        return item;
    }

    private static (double X, double Y, double Width, double Height)? Box(A.Transform2D? transform)
    {
        if (transform?.Offset is not { } offset || transform.Extents is not { } extents) return null;
        return ((offset.X?.Value ?? 0) * PointsPerUnit, (offset.Y?.Value ?? 0) * PointsPerUnit,
            (extents.Cx?.Value ?? 0) * PointsPerUnit, (extents.Cy?.Value ?? 0) * PointsPerUnit);
    }

    // ---------- placeholders: what a slide inherits ----------

    private static P.PlaceholderShape? Placeholder(P.ApplicationNonVisualDrawingProperties? properties) =>
        properties?.GetFirstChild<P.PlaceholderShape>();

    /// <summary>"title", "body" or "other": the three kinds of text a master gives default styles for.</summary>
    private static string Kind(P.PlaceholderShape? placeholder)
    {
        if (placeholder == null) return "other";
        var type = placeholder.Type?.Value;
        if (type == null) return "body"; // a placeholder that names no type holds content
        if (type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle) return "title";
        if (type == P.PlaceholderValues.Body || type == P.PlaceholderValues.SubTitle || type == P.PlaceholderValues.Object) return "body";
        return "other";
    }

    private static bool IsFurniture(P.PlaceholderShape? placeholder)
    {
        var type = placeholder?.Type?.Value;
        return type != null && (type == P.PlaceholderValues.DateAndTime || type == P.PlaceholderValues.Footer ||
                                type == P.PlaceholderValues.SlideNumber || type == P.PlaceholderValues.Header);
    }

    /// <summary>The shape on the layout (or the master) that a slide's placeholder stands for.</summary>
    private static P.Shape? Matching(P.PlaceholderShape placeholder, OpenXmlElement? tree, bool byKindOnly)
    {
        if (tree == null) return null;
        var candidates = tree.Descendants<P.Shape>()
            .Select(shape => (Shape: shape, Holder: Placeholder(shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties)))
            .Where(candidate => candidate.Holder != null).ToList();

        if (!byKindOnly && placeholder.Index?.Value is { } index)
        {
            var same = candidates.FirstOrDefault(candidate => candidate.Holder!.Index?.Value == index);
            if (same.Shape != null) return same.Shape;
        }
        if (placeholder.Type?.Value is { } type)
        {
            var same = candidates.FirstOrDefault(candidate => candidate.Holder!.Type?.Value == type);
            if (same.Shape != null) return same.Shape;
        }
        string kind = Kind(placeholder);
        return candidates.FirstOrDefault(candidate => Kind(candidate.Holder) == kind).Shape;
    }

    private static (P.Shape? Layout, P.Shape? Master) Ancestors(P.PlaceholderShape? placeholder, SlidePart part)
    {
        if (placeholder == null) return (null, null);
        var layout = part.SlideLayoutPart;
        return (Matching(placeholder, layout?.SlideLayout?.CommonSlideData?.ShapeTree, byKindOnly: false),
            Matching(placeholder, layout?.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree, byKindOnly: true));
    }

    private static (double X, double Y, double Width, double Height)? InheritedBox(P.PlaceholderShape? placeholder, SlidePart part)
    {
        var (layout, master) = Ancestors(placeholder, part);
        return Box(layout?.ShapeProperties?.Transform2D) ?? Box(master?.ShapeProperties?.Transform2D);
    }

    /// <summary>
    /// The places a paragraph's defaults can come from, nearest first: the shape's own list style,
    /// the layout's, the master's, and the master's style for this kind of text.
    /// </summary>
    private static List<OpenXmlElement> StyleSources(P.Shape shape, P.PlaceholderShape? placeholder, SlidePart part)
    {
        var sources = new List<OpenXmlElement?> { shape.TextBody?.GetFirstChild<A.ListStyle>() };
        var (layout, master) = Ancestors(placeholder, part);
        sources.Add(layout?.TextBody?.GetFirstChild<A.ListStyle>());
        sources.Add(master?.TextBody?.GetFirstChild<A.ListStyle>());

        var styles = part.SlideLayoutPart?.SlideMasterPart?.SlideMaster?.TextStyles;
        sources.Add(Kind(placeholder) switch
        {
            "title" => styles?.TitleStyle,
            "body" => styles?.BodyStyle,
            _ => styles?.OtherStyle
        });
        return sources.Where(source => source != null).ToList()!;
    }

    /// <summary>The "lvl3pPr" of a list style: the paragraph defaults of its third level.</summary>
    private static IEnumerable<OpenXmlElement> LevelDefaults(List<OpenXmlElement> sources, int level)
    {
        string name = $"lvl{Math.Clamp(level, 0, 8) + 1}pPr";
        foreach (var source in sources)
        {
            var defaults = source.ChildElements.FirstOrDefault(child => child.LocalName == name);
            if (defaults != null) yield return defaults;
        }
    }

    private static string? Attribute(OpenXmlElement element, string name) =>
        element.GetAttributes().FirstOrDefault(attribute => attribute.LocalName == name && string.IsNullOrEmpty(attribute.NamespaceUri)).Value;

    // ---------- text ----------

    private void ReadShape(P.Shape shape, SlidePart part, Slide slide, Placement place)
    {
        var placeholder = Placeholder(shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties);
        if (IsFurniture(placeholder)) return;

        var box = Box(shape.ShapeProperties?.Transform2D) ?? InheritedBox(placeholder, part);
        if (box is not { } frame) return;

        // a shape filled with a picture is a picture
        if (shape.ShapeProperties?.GetFirstChild<A.BlipFill>() is { } fill && LoadPicture(fill.Blip, fill.GetFirstChild<A.SourceRectangle>(), part) is { } filled)
            slide.Items.Add(Placed(new SlidePicture(filled), place, frame));

        if (shape.TextBody is not { } body) return;
        var sources = StyleSources(shape, placeholder, part);
        string kind = Kind(placeholder);
        var (layout, master) = Ancestors(placeholder, part);

        // text that was shrunk to fit its box says by how much
        double shrink = body.BodyProperties?.GetFirstChild<A.NormalAutoFit>()?.FontScale?.Value is { } scale and > 0 ? scale / 100000.0 : 1;

        var anchor = body.BodyProperties?.Anchor?.Value ?? layout?.TextBody?.BodyProperties?.Anchor?.Value ?? master?.TextBody?.BodyProperties?.Anchor?.Value;
        var text = new SlideText
        {
            IsTitle = kind == "title",
            Anchor = anchor == A.TextAnchoringTypeValues.Center ? VerticalAnchor.Middle
                : anchor == A.TextAnchoringTypeValues.Bottom ? VerticalAnchor.Bottom
                : anchor == null && kind == "title" ? VerticalAnchor.Middle
                : VerticalAnchor.Top
        };

        bool subtitle = placeholder?.Type?.Value == P.PlaceholderValues.SubTitle;
        foreach (var paragraph in body.Elements<A.Paragraph>())
        {
            var properties = paragraph.ParagraphProperties;
            int level = properties?.Level?.Value ?? 0;
            var defaults = LevelDefaults(sources, level).ToList();

            bool bullet;
            if (properties?.GetFirstChild<A.NoBullet>() != null) bullet = false;
            else if (properties?.GetFirstChild<A.CharacterBullet>() != null || properties?.GetFirstChild<A.AutoNumberedBullet>() != null) bullet = true;
            else
            {
                // the nearest default that says either way; a content placeholder has bullets where none does
                var said = defaults.Select(d => d.ChildElements.FirstOrDefault(child => child.LocalName is "buNone" or "buChar" or "buAutoNum")).FirstOrDefault(child => child != null);
                bullet = said != null ? said.LocalName != "buNone" : kind == "body" && !subtitle;
            }

            string? alignment = properties?.Alignment?.InnerText ?? defaults.Select(d => Attribute(d, "algn")).FirstOrDefault(value => value != null);
            double defaultSize = defaults
                .Select(d => d.ChildElements.FirstOrDefault(child => child.LocalName == "defRPr"))
                .Select(run => run == null ? null : Attribute(run, "sz"))
                .Select(value => int.TryParse(value, out int hundredths) ? hundredths / 100.0 : (double?)null)
                .FirstOrDefault(value => value != null) ?? (kind == "title" ? 40 : 18);

            var builder = new BlockBuilder();
            double size = 0;
            foreach (var child in paragraph.ChildElements)
            {
                switch (child)
                {
                    case A.Run run:
                        builder.Text(XhtmlWriter.Clean(run.Text?.Text ?? ""), StyleOf(run.RunProperties));
                        if (size == 0 && run.RunProperties?.FontSize?.Value is { } own and > 0) size = own / 100.0;
                        break;
                    case A.Field field:
                        builder.Text(XhtmlWriter.Clean(field.Text?.Text ?? ""), StyleOf(field.RunProperties));
                        break;
                    case A.Break:
                        builder.Break();
                        break;
                }
            }

            var result = new SlideParagraph
            {
                Level = level,
                Bullet = bullet,
                FontSize = Math.Clamp((size > 0 ? size : defaultSize) * shrink, 4, 400),
                Align = alignment switch
                {
                    "ctr" => TextAlign.Center,
                    "r" => TextAlign.End,
                    "just" or "dist" => TextAlign.Justify,
                    _ => TextAlign.Start
                }
            };
            result.Runs.AddRange(builder.TakeInlines());
            text.Paragraphs.Add(result);
        }

        // empty paragraphs at either end are only space; a box of nothing else is no box
        while (text.Paragraphs.Count > 0 && text.Paragraphs[^1].Runs.Count == 0) text.Paragraphs.RemoveAt(text.Paragraphs.Count - 1);
        while (text.Paragraphs.Count > 0 && text.Paragraphs[0].Runs.Count == 0) text.Paragraphs.RemoveAt(0);
        if (!text.IsEmpty) slide.Items.Add(Placed(text, place, frame));
    }

    private static TextStyle StyleOf(OpenXmlElement? properties)
    {
        if (properties == null) return TextStyle.None;
        var style = TextStyle.None;
        if (Attribute(properties, "b") is "1" or "true") style |= TextStyle.Bold;
        if (Attribute(properties, "i") is "1" or "true") style |= TextStyle.Italic;
        if (Attribute(properties, "u") is { } underline and not "none") style |= TextStyle.Underline;
        if (Attribute(properties, "strike") is { } strike and not "noStrike") style |= TextStyle.Strike;
        if (int.TryParse(Attribute(properties, "baseline"), out int baseline) && baseline != 0)
            style |= baseline > 0 ? TextStyle.Superscript : TextStyle.Subscript;
        return style;
    }

    private static void ReadTable(P.GraphicFrame frame, Slide slide, Placement place)
    {
        var source = frame.Descendants<A.Table>().FirstOrDefault();
        var transform = frame.Transform;
        if (source == null || transform?.Offset == null || transform.Extents == null) return;

        double size = 0;
        var table = new SlideTable();
        foreach (var row in source.Elements<A.TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<A.TableCell>())
            {
                var lines = cell.TextBody?.Elements<A.Paragraph>().Select(paragraph => string.Concat(paragraph.ChildElements.Select(child => child switch
                {
                    A.Run run => run.Text?.Text ?? "",
                    A.Field field => field.Text?.Text ?? "",
                    A.Break => "\n",
                    _ => ""
                }))) ?? [];
                cells.Add(XhtmlWriter.Clean(string.Join("\n", lines).Trim()));
                if (size == 0 && cell.Descendants<A.RunProperties>().FirstOrDefault()?.FontSize?.Value is { } own and > 0) size = own / 100.0;
            }
            table.Rows.Add(cells);
        }
        if (table.Rows.Count == 0) return;

        var placed = new SlideTable { FontSize = size > 0 ? size : 14 };
        placed.Rows.AddRange(table.Rows);
        slide.Items.Add(Placed(placed, place, ((transform.Offset.X?.Value ?? 0) * PointsPerUnit, (transform.Offset.Y?.Value ?? 0) * PointsPerUnit,
            (transform.Extents.Cx?.Value ?? 0) * PointsPerUnit, (transform.Extents.Cy?.Value ?? 0) * PointsPerUnit)));
    }

    // ---------- pictures ----------

    private static Picture? LoadPicture(P.BlipFill? fill, SlidePart part) =>
        fill == null ? null : LoadPicture(fill.Blip, fill.SourceRectangle, part);

    private static Picture? LoadPicture(A.Blip? blip, A.SourceRectangle? crop, SlidePart part)
    {
        if (blip?.Embed?.Value is not { Length: > 0 } relationship) return null;
        byte[] data;
        try
        {
            if (part.GetPartById(relationship) is not ImagePart image) return null;
            using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or IOException or InvalidOperationException)
        {
            return null; // a picture that is linked rather than stored, or a broken reference
        }

        var picture = Picture.Load(data);
        if (picture == null || crop == null) return picture;

        // The part of the picture that is shown, given as what is cut off each side in thousandths of a percent.
        double left = (crop.Left?.Value ?? 0) / 100000.0, top = (crop.Top?.Value ?? 0) / 100000.0;
        double right = (crop.Right?.Value ?? 0) / 100000.0, bottom = (crop.Bottom?.Value ?? 0) / 100000.0;
        if (left <= 0 && top <= 0 && right <= 0 && bottom <= 0) return picture;
        if (left < 0 || top < 0 || right < 0 || bottom < 0 || left + right >= 0.99 || top + bottom >= 0.99) return picture;

        try
        {
            using var bitmap = SKBitmap.Decode(picture.Data);
            if (bitmap == null) return picture;
            var area = SKRectI.Create((int)(bitmap.Width * left), (int)(bitmap.Height * top),
                Math.Max(1, (int)(bitmap.Width * (1 - left - right))), Math.Max(1, (int)(bitmap.Height * (1 - top - bottom))));
            using var part2 = new SKBitmap();
            if (!bitmap.ExtractSubset(part2, area)) return picture;
            // a photo stays a photo: cut again as JPEG at a quality that adds nothing visible; anything else exactly
            using var encoded = picture.Format == "jpg" ? part2.Encode(SKEncodedImageFormat.Jpeg, 95) : part2.Encode(SKEncodedImageFormat.Png, 100);
            return Picture.Load(encoded.ToArray()) ?? picture;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return picture;
        }
    }
}
