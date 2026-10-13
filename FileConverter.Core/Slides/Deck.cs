using FileConverter.Core.Documents;

namespace FileConverter.Core.Slides;

/// <summary>
/// A presentation as the built-in converter keeps it: slides of one size, each with its text
/// boxes, pictures and tables at the places they stand. What makes up a presentation's look
/// beyond that — the theme's colours and fonts, backgrounds, drawn shapes, animations — is an
/// office program's to reproduce; here the content is carried over, where it was on the slide.
/// All measures are in points, from the slide's top left corner.
/// </summary>
internal sealed class Deck
{
    public string? Title { get; set; }

    /// <summary>16:9 at PowerPoint's usual size unless the file says otherwise.</summary>
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 540;

    public List<Slide> Slides { get; } = [];

    /// <summary>
    /// Slides that are made one at a time as a writer asks for them (the pages of a long PDF,
    /// each a large picture), instead of being held all at once in <see cref="Slides"/>.
    /// </summary>
    public IEnumerable<Slide>? Stream { get; set; }

    /// <summary>The slides, whichever way the deck has them.</summary>
    public IEnumerable<Slide> AllSlides => Stream ?? Slides;

    /// <summary>How many slides there are, where that is known without making them.</summary>
    public int SlideCount { get; set; }

    /// <summary>Releases what streamed slides are made from; called by whoever asked for the deck, once it is written.</summary>
    public Action? Close { get; set; }
}

internal sealed class Slide
{
    public List<SlideItem> Items { get; } = [];
}

internal abstract class SlideItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

internal enum VerticalAnchor
{
    Top,
    Middle,
    Bottom
}

internal sealed class SlideText : SlideItem
{
    public List<SlideParagraph> Paragraphs { get; } = [];

    /// <summary>The slide's title (or the title slide's subtitle): set larger, and what a slide is named by.</summary>
    public bool IsTitle { get; init; }

    public VerticalAnchor Anchor { get; init; }

    public bool IsEmpty => Paragraphs.All(paragraph => paragraph.Runs.All(run => run is not TextRun { Text.Length: > 0 }));
}

internal sealed class SlideParagraph
{
    public List<Inline> Runs { get; } = [];

    /// <summary>How far in the paragraph is set: 0 for the outermost.</summary>
    public int Level { get; init; }

    public bool Bullet { get; init; }
    public TextAlign Align { get; init; }

    /// <summary>The size of its type, in points.</summary>
    public double FontSize { get; init; } = 18;
}

internal sealed class SlidePicture(Picture picture) : SlideItem
{
    public Picture Picture { get; } = picture;
}

internal sealed class SlideTable : SlideItem
{
    /// <summary>The cells' text, row by row.</summary>
    public List<List<string>> Rows { get; } = [];

    public double FontSize { get; init; } = 14;
}
