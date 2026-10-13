using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using FileConverter.Core;
using FileConverter.Core.Documents;
using FileConverter.Core.Settings;
using FileConverter.Core.Slides;
using ImageMagick;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using UglyToad.PdfPig;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// Presentations: what is read from a PowerPoint or OpenDocument file, what is written to one,
/// slides laid out as PDF pages and PDF pages made into slides — all without an office program.
/// </summary>
public class PresentationTests(Samples samples) : IClassFixture<Samples>
{
    private static readonly (string Key, string Value) BuiltIn = (K.Engine, "builtin");

    private string NewFile(string name) => Path.Combine(samples.NewOutputFolder(), name);

    private static SlideText Box(double x, double y, double width, double height, bool title, params (string Text, int Level, bool Bullet, double Size)[] lines)
    {
        var box = new SlideText { X = x, Y = y, Width = width, Height = height, IsTitle = title, Anchor = title ? VerticalAnchor.Middle : VerticalAnchor.Top };
        foreach (var line in lines)
        {
            var paragraph = new SlideParagraph { Level = line.Level, Bullet = line.Bullet, FontSize = line.Size, Align = title ? TextAlign.Center : TextAlign.Start };
            paragraph.Runs.Add(new TextRun(line.Text));
            box.Paragraphs.Add(paragraph);
        }
        return box;
    }

    /// <summary>A title slide, and a slide with a title, bullets on two levels with a bold word, a picture and a table.</summary>
    private static Deck Talk()
    {
        using var image = Samples.Picture(160, 120);
        var deck = new Deck { Title = "Çay talk", Width = 720, Height = 540 }; // 4:3
        var first = new Slide();
        first.Items.Add(Box(60, 180, 600, 100, true, ("Çay nasıl demlenir", 0, false, 44)));
        deck.Slides.Add(first);

        var second = new Slide();
        second.Items.Add(Box(40, 30, 640, 60, true, ("Steps", 0, false, 36)));
        var body = Box(40, 110, 360, 240, false, ("Boil the water", 0, true, 24), ("fresh, not twice", 1, true, 20), ("Wait", 0, true, 24));
        body.Paragraphs[2].Runs.Add(new TextRun(" ten minutes", TextStyle.Bold));
        second.Items.Add(body);
        second.Items.Add(new SlidePicture(Picture.Load(image.ToByteArray(MagickFormat.Png), "A glass")!) { X = 440, Y = 120, Width = 240, Height = 180 });
        var table = new SlideTable { X = 40, Y = 380, Width = 360, Height = 80, FontSize = 14 };
        table.Rows.Add(["Tea", "Minutes"]);
        table.Rows.Add(["Black", "10"]);
        second.Items.Add(table);
        deck.Slides.Add(second);
        return deck;
    }

    private static string Text(SlideParagraph paragraph) => RichDocument.PlainText(paragraph.Runs);

    private static void AssertTalk(Deck read, bool tables = true)
    {
        Assert.Equal((720, 540), (Math.Round(read.Width), Math.Round(read.Height)));
        Assert.Equal(2, read.Slides.Count);

        var title = Assert.Single(read.Slides[0].Items.OfType<SlideText>());
        Assert.Equal("Çay nasıl demlenir", Text(title.Paragraphs.Single()));
        Assert.Equal(44, title.Paragraphs[0].FontSize, 1);
        Assert.Equal(TextAlign.Center, title.Paragraphs[0].Align);
        // where it stood on the slide, to the point
        Assert.Equal((60, 180, 600, 100), (Math.Round(title.X), Math.Round(title.Y), Math.Round(title.Width), Math.Round(title.Height)));

        var body = read.Slides[1].Items.OfType<SlideText>().Single(text => text.Paragraphs.Count == 3);
        Assert.Equal(["Boil the water", "fresh, not twice", "Wait ten minutes"], body.Paragraphs.Select(Text));
        Assert.Equal([0, 1, 0], body.Paragraphs.Select(p => p.Level));
        Assert.All(body.Paragraphs, paragraph => Assert.True(paragraph.Bullet));
        Assert.Equal(20, body.Paragraphs[1].FontSize, 1);
        Assert.Contains(body.Paragraphs[2].Runs.OfType<TextRun>(), run => run.Text.Trim() == "ten minutes" && run.Has(TextStyle.Bold));

        var picture = Assert.Single(read.Slides[1].Items.OfType<SlidePicture>());
        Assert.Equal((160, 120), (picture.Picture.PixelWidth, picture.Picture.PixelHeight));
        Assert.Equal((440, 120, 240, 180), (Math.Round(picture.X), Math.Round(picture.Y), Math.Round(picture.Width), Math.Round(picture.Height)));

        if (tables)
        {
            var table = Assert.Single(read.Slides[1].Items.OfType<SlideTable>());
            Assert.Equal(["Black", "10"], table.Rows[1]);
        }
    }

    // ---------- reading and writing ----------

    [Fact]
    public void APowerPointFileKeepsItsSlidesTextPicturesAndTables()
    {
        var path = NewFile("talk.pptx");
        PptxWriter.Write(Talk(), path);

        AssertTalk(PptxReader.ReadFile(path));
        // what a validator would say of the file
        using var file = PresentationDocument.Open(path, false);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(file, TestContext.Current.CancellationToken).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(5)));
    }

    [Fact]
    public void AnOpenDocumentPresentationKeepsThemToo()
    {
        var path = NewFile("talk.odp");
        OdpWriter.Write(Talk(), path);

        // (this format's writer puts a table down as lines of text)
        AssertTalk(OdpReader.ReadFile(path), tables: false);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
    }

    /// <summary>
    /// Much of a slide is not said on the slide: where its title stands, how large the type is and
    /// whether a line has a bullet come from the layout and the master behind it.
    /// </summary>
    [Fact]
    public void WhatASlideDoesNotSayItselfIsTakenFromItsLayoutAndMaster()
    {
        var path = NewFile("inherited.pptx");
        PptxWriter.Write(new Deck { Slides = { new Slide() } }, path);

        const string a = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        const string p = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
        const string r = "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        const string group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";
        static string Place(string type, int index, string frame, string body) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"{index + 2}\" name=\"x\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"{type}\" idx=\"{index}\"/></p:nvPr></p:nvSpPr><p:spPr>{frame}</p:spPr>{body}</p:sp>";

        using (var file = PresentationDocument.Open(path, true))
        {
            var slide = file.PresentationPart!.SlideParts.Single();
            var layout = slide.SlideLayoutPart!;
            var master = layout.SlideMasterPart!;

            void Feed(OpenXmlPart part, string xml)
            {
                using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(xml);
            }

            // the master: the type sizes of titles and of the two levels of body text, bullets for body text
            Feed(master, $"""
                <p:sldMaster {a} {p} {r}><p:cSld><p:spTree>{group}
                  {Place("title", 0, "<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1270000\" cy=\"1270000\"/></a:xfrm>", "<p:txBody><a:bodyPr anchor=\"b\"/><a:lstStyle/><a:p/></p:txBody>")}
                </p:spTree></p:cSld>
                <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
                <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>
                <p:txStyles>
                  <p:titleStyle><a:lvl1pPr algn="ctr"><a:buNone/><a:defRPr sz="4400"/></a:lvl1pPr></p:titleStyle>
                  <p:bodyStyle><a:lvl1pPr><a:buChar char="•"/><a:defRPr sz="2800"/></a:lvl1pPr><a:lvl2pPr><a:buChar char="–"/><a:defRPr sz="2400"/></a:lvl2pPr></p:bodyStyle>
                  <p:otherStyle/>
                </p:txStyles></p:sldMaster>
                """);
            // the layout: where the title and the body stand; its own, smaller size for the second level
            Feed(layout, $"""
                <p:sldLayout {a} {p} {r}><p:cSld><p:spTree>{group}
                  {Place("title", 0, "<a:xfrm><a:off x=\"635000\" y=\"254000\"/><a:ext cx=\"7620000\" cy=\"1016000\"/></a:xfrm>", "<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody>")}
                  {Place("body", 1, "<a:xfrm><a:off x=\"635000\" y=\"1524000\"/><a:ext cx=\"7620000\" cy=\"3810000\"/></a:xfrm>", "<p:txBody><a:bodyPr/><a:lstStyle><a:lvl2pPr><a:defRPr sz=\"2000\"/></a:lvl2pPr></a:lstStyle><a:p/></p:txBody>")}
                </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>
                """);
            // the slide: nothing but the words, and one line that says it has no bullet
            Feed(slide, $"""
                <p:sld {a} {p} {r}><p:cSld><p:spTree>{group}
                  {Place("title", 0, "", "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Inherited</a:t></a:r></a:p></p:txBody>")}
                  {Place("body", 1, "", "<p:txBody><a:bodyPr><a:normAutofit fontScale=\"50000\"/></a:bodyPr><a:lstStyle/><a:p><a:r><a:t>first</a:t></a:r></a:p><a:p><a:pPr lvl=\"1\"/><a:r><a:t>second</a:t></a:r></a:p><a:p><a:pPr><a:buNone/></a:pPr><a:r><a:rPr sz=\"1200\"/><a:t>third</a:t></a:r></a:p></p:txBody>")}
                </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>
                """);
        }

        var texts = PptxReader.ReadFile(path).Slides.Single().Items.OfType<SlideText>().ToList();
        var title = texts.Single(text => text.IsTitle);
        Assert.Equal((50, 20, 600, 80), (Math.Round(title.X), Math.Round(title.Y), Math.Round(title.Width), Math.Round(title.Height))); // from the layout
        Assert.Equal(44, title.Paragraphs[0].FontSize);          // from the master's title style
        Assert.Equal(TextAlign.Center, title.Paragraphs[0].Align);
        Assert.False(title.Paragraphs[0].Bullet);
        Assert.Equal(VerticalAnchor.Bottom, title.Anchor);       // from the master's own title shape

        var body = texts.Single(text => !text.IsTitle);
        Assert.Equal((50, 120), (Math.Round(body.X), Math.Round(body.Y)));
        // sizes: the master's 28, the layout's 20 for the second level, the run's own 12 — each halved, as the box shrank its text
        Assert.Equal([14, 10, 6], body.Paragraphs.Select(paragraph => paragraph.FontSize));
        Assert.Equal([true, true, false], body.Paragraphs.Select(paragraph => paragraph.Bullet));
    }

    // ---------- slides as pages, pages as slides ----------

    private string TalkFile(string extension)
    {
        var path = Path.Combine(samples.Folder, "talk." + extension);
        if (File.Exists(path)) return path;
        if (extension == "pptx") PptxWriter.Write(Talk(), path);
        else OdpWriter.Write(Talk(), path);
        return path;
    }

    [Theory]
    [InlineData("pptx")]
    [InlineData("odp")]
    public async Task SlidesBecomePdfPagesOfTheSlidesSizeWithEverythingInPlace(string format)
    {
        var result = await samples.ConvertAsync(TalkFile(format), "pdf", BuiltIn);

        using var pdf = PdfDocument.Open(result.OutputPath);
        Assert.Equal(2, pdf.NumberOfPages);
        var page = pdf.GetPage(2);
        Assert.Equal((720, 540), (Math.Round(page.Width), Math.Round(page.Height)));
        Assert.Equal("Çay nasıl demlenir", string.Join(" ", pdf.GetPage(1).GetWords().Select(w => w.Text)));

        var words = page.GetWords().ToList();
        // the title above the body, the second-level line set further in and smaller than the first
        var steps = words.First(w => w.Text == "Steps");
        var boil = words.First(w => w.Text == "Boil");
        var fresh = words.First(w => w.Text == "fresh,");
        Assert.True(steps.BoundingBox.Bottom > boil.BoundingBox.Top);
        Assert.True(fresh.BoundingBox.Left > boil.BoundingBox.Left + 10);
        Assert.True(fresh.Letters[0].PointSize < boil.Letters[0].PointSize);
        // the body text begins where its box does: 40 points from the left, 110 from the top (plus its bullet)
        Assert.InRange(boil.BoundingBox.Left, 40, 80);
        Assert.InRange(540 - boil.BoundingBox.Top, 105, 135);
        Assert.Contains("Bold", words.First(w => w.Text == "ten").FontName, StringComparison.OrdinalIgnoreCase);
        Assert.Single(page.GetImages());
        Assert.Contains(words, w => w.Text == "Black");
    }

    [Fact]
    public async Task TextTooLongForItsBoxIsMadeSmallerNotCutOff()
    {
        var deck = new Deck();
        var slide = new Slide();
        // forty lines at 24 points into a box that holds four
        slide.Items.Add(Box(100, 100, 400, 120, false, Enumerable.Range(1, 40).Select(i => ($"line number {i}", 0, false, 24.0)).ToArray()));
        deck.Slides.Add(slide);
        var path = NewFile("crowded.pptx");
        PptxWriter.Write(deck, path);

        var result = await samples.ConvertAsync(path, "pdf", BuiltIn);
        using var pdf = PdfDocument.Open(result.OutputPath);
        var words = pdf.GetPage(1).GetWords().ToList();
        Assert.Equal(40, words.Count(w => w.Text == "line"));
        Assert.All(words, w => Assert.InRange(540 - w.BoundingBox.Bottom, 99, 221)); // every line inside the box
        Assert.True(words[0].Letters[0].PointSize < 8);
    }

    [Theory]
    [InlineData("pptx", "odp")]
    [InlineData("odp", "pptx")]
    public async Task PresentationsAreConvertedIntoOneAnotherWithoutAnOfficeProgram(string from, string to)
    {
        var result = await samples.ConvertAsync(TalkFile(from), to, BuiltIn);
        // a table only survives where both formats' ways of writing one meet
        AssertTalk(to == "pptx" ? PptxReader.ReadFile(result.OutputPath) : OdpReader.ReadFile(result.OutputPath), tables: false);
    }

    [Fact]
    public async Task SlidesBecomePictures()
    {
        var result = await samples.ConvertAsync(TalkFile("pptx"), "png", BuiltIn, (K.Dpi, "72"));

        var files = Directory.GetFiles(result.OutputPath, "*.png").Order().ToList();
        Assert.Equal(2, files.Count);
        var info = new MagickImageInfo(files[0]);
        Assert.Equal((720u, 540u), (info.Width, info.Height));
    }

    private string TwoPagePdf()
    {
        var path = Path.Combine(samples.Folder, "two-sizes.pdf");
        if (File.Exists(path)) return path;
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A5.Landscape()); // 595 × 420 points
                page.Margin(30);
                page.Content().Text("First page, wide").FontSize(30);
            });
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A5); // upright: fitted onto a slide of the first page's size
                page.Margin(30);
                page.Content().Text("Second page, upright").FontSize(30);
            });
        }).GeneratePdf(path);
        return path;
    }

    [Theory]
    [InlineData("pptx")]
    [InlineData("odp")]
    public async Task APdfBecomesSlidesThatShowItsPagesExactly(string format)
    {
        var result = await samples.ConvertAsync(TwoPagePdf(), format, (K.Dpi, "100"));
        var deck = format == "pptx" ? PptxReader.ReadFile(result.OutputPath) : OdpReader.ReadFile(result.OutputPath);

        // slides of the first page's size, each one picture
        Assert.Equal((595, 420), (Math.Round(deck.Width), Math.Round(deck.Height)));
        Assert.Equal(2, deck.Slides.Count);
        var first = Assert.Single(deck.Slides[0].Items.OfType<SlidePicture>());
        Assert.Equal((0, 0, 595, 420), (Math.Round(first.X), Math.Round(first.Y), Math.Round(first.Width), Math.Round(first.Height)));
        // drawn at the resolution asked for, and stored without loss
        Assert.InRange(first.Picture.PixelWidth, 826, 827); // 595.3 points at 100 dots per inch
        Assert.Equal(583, first.Picture.PixelHeight);
        Assert.Equal("png", first.Picture.Format);

        // the upright page stands in the middle of its slide, as tall as the slide, not stretched
        var second = Assert.Single(deck.Slides[1].Items.OfType<SlidePicture>());
        Assert.Equal(420, Math.Round(second.Height));
        Assert.Equal(Math.Round(420 * 420 / 595.0), Math.Round(second.Width));
        Assert.Equal(Math.Round((595 - second.Width) / 2), Math.Round(second.X));

        // the page really is what the slide shows: dark letters on white where the text stands
        using var image = new MagickImage(first.Picture.Data);
        Assert.Equal(MagickColors.White.ToHexString(), image.GetPixels().GetPixel(5, 5).ToColor()!.ToHexString());
        using var text = image.CloneArea(new MagickGeometry(40, 40, 400, 60));
        Assert.True(text.Statistics().GetChannel(PixelChannel.Red)!.Minimum < 20000);
    }

    [Fact]
    public async Task OnlyTheChosenPagesBecomeSlides()
    {
        var result = await samples.ConvertAsync(TwoPagePdf(), "pptx", (K.Pages, "2"), (K.Dpi, "72"));
        var deck = PptxReader.ReadFile(result.OutputPath);

        Assert.Single(deck.Slides);
        Assert.Equal((420, 595), (Math.Round(deck.Width), Math.Round(deck.Height)));
    }

    // ---------- what is offered ----------

    [Fact]
    public void PresentationsAreOfferedTheirConversionsAndTheirSettings()
    {
        Assert.Equal(["pdf", "odp", "png", "jpg", "zip", "7z"], FormatRegistry.GetTargets("pptx", false));
        Assert.Equal(["pdf", "pptx", "odp", "png", "jpg", "zip", "7z"], FormatRegistry.GetTargets("ppt", false));
        Assert.Contains("pptx", FormatRegistry.GetTargets("pdf", false));
        Assert.Contains("odp", FormatRegistry.GetTargets("pdf", false));

        static List<string> Keys(string source, string target) => SettingCatalog.For(source, target).Select(d => d.Key).ToList();
        Assert.Equal([K.Engine], Keys("pptx", "pdf"));
        Assert.Equal([K.Engine], Keys("odp", "pptx"));
        Assert.Equal([K.Engine, K.Dpi, K.Pages], Keys("pdf", "pptx"));
        // for PDF → slides the choice is another one: exact pictures, or LibreOffice's editable text
        var engine = SettingCatalog.For("pdf", "pptx")[0];
        Assert.Equal(["builtin", "libreoffice"], engine.Choices.Select(choice => choice.Value));
    }
}
