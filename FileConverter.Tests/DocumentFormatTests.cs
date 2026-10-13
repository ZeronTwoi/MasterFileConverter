using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using FileConverter.Core;
using FileConverter.Core.Documents;
using ImageMagick;
using QuestPDF.Fluent;
using UglyToad.PdfPig;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// The built-in document converter: what each format's reader recovers, what each writer
/// produces, and the conversions between them as the app runs them. Everything here is done
/// without an office program (the engine is named where one could step in), which is also how
/// a build machine runs it.
/// </summary>
public class DocumentFormatTests(Samples samples) : IClassFixture<Samples>
{
    private static readonly (string Key, string Value) BuiltIn = (K.Engine, "builtin");

    /// <summary>A heading, a paragraph with bold and italic words and a link, a nested list, a table and a picture.</summary>
    private static RichDocument Article()
    {
        using var image = Samples.Picture(120, 80);
        var document = new RichDocument { Title = "Kitchen notes", Author = "Ayşe" };
        document.Blocks.Add(new Heading(1, [new TextRun("Kitchen notes")]));
        document.Blocks.Add(new Paragraph(
        [
            new TextRun("Çay is "), new TextRun("always", TextStyle.Bold), new TextRun(" brewed "),
            new TextRun("slowly", TextStyle.Italic), new TextRun(" — see "), new TextRun("the guide", TextStyle.None, "https://example.org/tea"),
            new TextRun(".")
        ]));
        document.Blocks.Add(new Heading(2, [new TextRun("Shopping")]));

        var inner = new ListBlock(ordered: false);
        inner.Items.Add([new Paragraph("black")]);
        inner.Items.Add([new Paragraph("green")]);
        var list = new ListBlock(ordered: true);
        list.Items.Add([new Paragraph("tea"), inner]);
        list.Items.Add([new Paragraph("sugar")]);
        document.Blocks.Add(list);

        var table = new Table { HeaderRows = 1 };
        table.Rows.Add([new TableCell([new Paragraph("Item")]), new TableCell([new Paragraph("Grams")])]);
        table.Rows.Add([new TableCell([new Paragraph("Tea")]), new TableCell([new Paragraph("250")])]);
        document.Blocks.Add(table);
        document.Blocks.Add(Picture.Load(image.ToByteArray(MagickFormat.Png), "A picture")!);
        return document;
    }

    private string Write(RichDocument document, string extension)
    {
        var path = Path.Combine(samples.NewOutputFolder(), "article." + extension);
        switch (extension)
        {
            case "docx": DocxWriter.Write(document, path); break;
            case "odt": OdtWriter.Write(document, path); break;
            case "rtf": RtfWriter.Write(document, path); break;
            case "html": XhtmlWriter.Write(document, path, "Article"); break;
            case "epub": EpubWriter.Write(document, path, "Article"); break;
            case "md":
                foreach (var (name, picture) in MarkdownWriter.Write(document, path))
                {
                    var file = Path.Combine(Path.GetDirectoryName(path)!, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllBytes(file, picture.Data);
                }
                break;
            default: throw new ArgumentException(extension);
        }
        return path;
    }

    private static RichDocument Read(string path) => FormatRegistry.GetExtension(path) switch
    {
        "docx" => DocxReader.ReadFile(path),
        "odt" => OdtReader.ReadFile(path),
        "rtf" => RtfReader.ReadFile(path),
        "html" => HtmlReader.ReadFile(path),
        "epub" => EpubReader.ReadFile(path),
        "md" => MarkdownReader.ReadFile(path),
        var other => throw new ArgumentException(other)
    };

    private static string Text(Block block) => block switch
    {
        Paragraph paragraph => RichDocument.PlainText(paragraph.Inlines),
        Heading heading => RichDocument.PlainText(heading.Inlines),
        _ => ""
    };

    private static IEnumerable<TextRun> Runs(RichDocument document) =>
        RichDocument.Flatten(document.Blocks).OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<TextRun>());

    // ---------- every format: what is written is what is read ----------

    [Theory]
    [InlineData("docx")]
    [InlineData("odt")]
    [InlineData("rtf")]
    [InlineData("html")]
    [InlineData("epub")]
    [InlineData("md")]
    public void WhatAFormatIsWrittenWithIsReadBackFromIt(string extension)
    {
        var read = Read(Write(Article(), extension));
        var all = RichDocument.Flatten(read.Blocks).ToList();

        // the headings, at their levels
        var headings = all.OfType<Heading>().ToList();
        Assert.Contains(headings, h => h.Level == 1 && Text(h) == "Kitchen notes");
        Assert.Contains(headings, h => h.Level == 2 && Text(h) == "Shopping");

        // the paragraph, word for word, with its accented letters and its formatting
        var paragraph = all.OfType<Paragraph>().First(p => Text(p).Contains("brewed"));
        Assert.Equal("Çay is always brewed slowly — see the guide.", Text(paragraph));
        Assert.Contains(paragraph.Inlines.OfType<TextRun>(), r => r.Text.Trim() == "always" && r.Has(TextStyle.Bold));
        Assert.Contains(paragraph.Inlines.OfType<TextRun>(), r => r.Text.Trim() == "slowly" && r.Has(TextStyle.Italic));
        Assert.DoesNotContain(paragraph.Inlines.OfType<TextRun>(), r => r.Text.Contains("brewed") && r.Has(TextStyle.Bold));
        Assert.Contains(paragraph.Inlines.OfType<TextRun>(), r => r.Text.Contains("the guide") && r.Link == "https://example.org/tea");

        // a numbered list of two items, the first holding a bulleted one
        var numbered = all.OfType<ListBlock>().First(l => l.Ordered);
        Assert.Equal(2, numbered.Items.Count);
        Assert.Equal("tea", Text(numbered.Items[0][0]));
        var bulleted = numbered.Items[0].OfType<ListBlock>().Single();
        Assert.False(bulleted.Ordered);
        Assert.Equal(["black", "green"], bulleted.Items.Select(item => Text(item[0])));

        // the table, cell for cell
        var table = all.OfType<Table>().Single();
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Tea", "250"], table.Rows[1].Select(cell => Text(cell.Blocks[0])));
        Assert.Equal(1, table.HeaderRows);

        // the picture, pixel for pixel the size it was
        var picture = all.OfType<Picture>().Single();
        Assert.Equal((120, 80), (picture.PixelWidth, picture.PixelHeight));
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("odt")]
    [InlineData("epub")]
    public void TheTitleAndAuthorTravelWithTheDocument(string extension)
    {
        var read = Read(Write(Article(), extension));
        Assert.Equal("Kitchen notes", read.Title);
        Assert.Equal("Ayşe", read.Author);
    }

    // ---------- Markdown ----------

    [Fact]
    public void MarkdownIsReadWithItsStructure()
    {
        var read = new MarkdownReader().Read("""
            # Title

            Some *italic*, **bold**, ~~gone~~ and `code` text with a [link](https://example.org).
            A second line of the same paragraph.

            1. first
            2. second
               - inner

            > quoted

            ```
            let x = 1;
              indented
            ```

            | A | B |
            |---|---|
            | 1 | 2 |

            ---
            """);

        Assert.Equal("Title", read.Title);
        var paragraph = read.Blocks.OfType<Paragraph>().First();
        Assert.Equal("Some italic, bold, gone and code text with a link. A second line of the same paragraph.", Text(paragraph));
        var runs = paragraph.Inlines.OfType<TextRun>().ToList();
        Assert.Contains(runs, r => r.Text == "italic" && r.Style == TextStyle.Italic);
        Assert.Contains(runs, r => r.Text == "bold" && r.Style == TextStyle.Bold);
        Assert.Contains(runs, r => r.Text == "gone" && r.Style == TextStyle.Strike);
        Assert.Contains(runs, r => r.Text == "code" && r.Style == TextStyle.Code);
        Assert.Contains(runs, r => r.Text == "link" && r.Link == "https://example.org");

        var list = read.Blocks.OfType<ListBlock>().Single();
        Assert.True(list.Ordered);
        Assert.Single(list.Items[1].OfType<ListBlock>());
        Assert.Single(read.Blocks.OfType<Quote>());
        Assert.Equal("let x = 1;\n  indented", read.Blocks.OfType<CodeBlock>().Single().Text.Replace("\r\n", "\n"));
        Assert.Equal(1, read.Blocks.OfType<Table>().Single().HeaderRows);
        Assert.Single(read.Blocks.OfType<Rule>());
    }

    [Theory]
    [InlineData("2 * 3 * 4 = 24", @"2 \* 3 \* 4 = 24")]
    [InlineData("snake_case_name", "snake_case_name")]
    [InlineData("_underlined_", @"\_underlined\_")]
    [InlineData("a [note] <here>", @"a \[note\] \<here>")]
    [InlineData("# not a heading", @"\# not a heading")]
    [InlineData("1. not a list", @"1\. not a list")]
    [InlineData("- not a list", @"\- not a list")]
    [InlineData("C:\\folder", @"C:\\folder")]
    public void TextThatLooksLikeMarkdownIsWrittenSoThatItStaysText(string text, string expected)
    {
        Assert.Equal(expected, MarkdownWriter.Escape(text, atLineStart: true));

        // and read back, it is the text it was
        var path = Path.Combine(samples.NewOutputFolder(), "plain.md");
        var document = new RichDocument();
        document.Blocks.Add(new Paragraph(text));
        MarkdownWriter.Write(document, path);
        Assert.Equal(text, Text(MarkdownReader.ReadFile(path).Blocks.Single()));
    }

    // ---------- HTML ----------

    [Fact]
    public void HtmlIsReadAsABrowserShowsItsText()
    {
        var read = new HtmlReader().Read("""
            <html lang="tr"><head><title>Sayfa</title><style>p { color: red }</style><script>var hidden = 1;</script></head>
            <body>
              <h2>Bölüm&nbsp;1</h2>
              <p>One
                 two   <b>three</b><i> four</i>&amp; five<br>next line</p>
              <div>loose text <span style="font-weight: bold">styled</span></div>
              <ul><li>a<ul><li>b</li></ul></li><li><p>c</p></li></ul>
              <pre>  kept
                as is</pre>
            </body></html>
            """);

        Assert.Equal("Sayfa", read.Title);
        Assert.Equal("tr", read.Language);
        Assert.Equal("Bölüm\u00A01", Text(read.Blocks.OfType<Heading>().Single()));

        var paragraph = read.Blocks.OfType<Paragraph>().First();
        Assert.Equal("One two three four& five next line", Text(paragraph));
        Assert.Single(paragraph.Inlines.OfType<LineBreak>());
        Assert.Contains(Runs(read), r => r.Text.Trim() == "styled" && r.Has(TextStyle.Bold));
        Assert.DoesNotContain(RichDocument.Flatten(read.Blocks), b => Text(b).Contains("hidden") || Text(b).Contains("color"));

        var list = read.Blocks.OfType<ListBlock>().Single();
        Assert.Equal(2, list.Items.Count);
        Assert.Single(list.Items[0].OfType<ListBlock>());
        Assert.StartsWith("  kept", read.Blocks.OfType<CodeBlock>().Single().Text);
    }

    [Fact]
    public void PicturesComeFromTheFileOrFromBesideItAndNeverFromElsewhere()
    {
        using var folder = TestSetup.NewFolder();
        var site = Directory.CreateDirectory(folder.File("site")).FullName;
        Directory.CreateDirectory(Path.Combine(site, "page_files"));
        using (var image = Samples.Picture(40, 30))
        {
            image.Write(Path.Combine(site, "page_files", "beside.png"), MagickFormat.Png);
            image.Write(folder.File("outside.png"), MagickFormat.Png);
        }
        var inline = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(site, "page_files", "beside.png")));
        var page = Path.Combine(site, "page.html");
        File.WriteAllText(page, $"""
            <body>
              <img src="page_files/beside.png" alt="beside">
              <img src="data:image/png;base64,{inline}" alt="inside">
              <img src="../outside.png" alt="outside the page's folder">
              <img src="{new Uri(folder.File("outside.png")).AbsoluteUri}" alt="named by its full path">
              <img src="https://example.org/remote.png" alt="on the internet">
            </body>
            """);

        var pictures = HtmlReader.ReadFile(page).Pictures.Select(p => p.Alt).ToList();
        Assert.Equal(["beside", "inside"], pictures);
    }

    [Fact]
    public void AWebPageSavedInAnOlderEncodingKeepsItsLetters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(samples.NewOutputFolder(), "old.html");
        File.WriteAllBytes(path, Encoding.GetEncoding(1254).GetBytes(
            "<html><head><meta charset=\"windows-1254\"></head><body><p>Işık ve gölge</p></body></html>"));

        Assert.Equal("Işık ve gölge", Text(HtmlReader.ReadFile(path).Blocks.Single()));
    }

    // ---------- e-books ----------

    [Fact]
    public void AnEbookIsADocumentPerChapterInReadingOrder()
    {
        var document = new RichDocument { Title = "Two chapters" };
        document.Blocks.Add(new Heading(1, [new TextRun("First")]));
        document.Blocks.Add(new Paragraph("Opening."));
        document.Blocks.Add(new Heading(1, [new TextRun("Second")]));
        document.Blocks.Add(new Paragraph("Closing."));
        var path = Write(document, "epub");

        using (var zip = ZipFile.OpenRead(path))
        {
            // what readers require of the file: its type first and unpacked, a package list, a table of contents
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("OEBPS/chapter-")));
            using var nav = new StreamReader(zip.GetEntry("OEBPS/nav.xhtml")!.Open());
            var contents = nav.ReadToEnd();
            Assert.Contains("chapter-001.xhtml#h1", contents);
            Assert.Contains(">Second<", contents);
        }

        var read = EpubReader.ReadFile(path);
        Assert.Equal(["First", "Opening.", "", "Second", "Closing."], read.Blocks.Select(Text));
        Assert.IsType<PageBreak>(read.Blocks[2]);
    }

    [Fact]
    public void ACopyProtectedEbookIsSaidToBeOne()
    {
        var path = Write(Article(), "epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
            writer.Write("""
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData><enc:CipherData><enc:CipherReference URI="OEBPS/chapter-001.xhtml"/></enc:CipherData></enc:EncryptedData>
                </encryption>
                """);
        }

        var error = Assert.Throws<FriendlyException>(() => EpubReader.ReadFile(path));
        Assert.Contains("copy-protected", error.Message);
    }

    // ---------- PDF → a document again ----------

    private string LaidOutPdf()
    {
        var path = Path.Combine(samples.Folder, "laid-out.pdf");
        if (File.Exists(path)) return path;

        using var image = Samples.Picture(200, 120);
        var picture = image.ToByteArray(MagickFormat.Png);
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A5);
                page.Margin(40);
                page.DefaultTextStyle(style => style.FontSize(10));
                page.Header().Text("Annual report · confidential").FontSize(8);
                page.Content().Column(column =>
                {
                    column.Spacing(8);
                    column.Item().Text("The year in brief").FontSize(20).Bold();
                    column.Item().Text(text =>
                    {
                        text.Span("Sales rose in ");
                        text.Span("every").Bold();
                        text.Span(" region, and the new warehouse opened on time.");
                    });
                    column.Item().Width(150).Image(picture);
                    column.Item().Text("Outlook").FontSize(14).Bold();
                    // long enough to run over two page ends in the middle of a sentence
                    column.Item().Text("The outlook is that " +
                        string.Join(", and ", Enumerable.Repeat("the coming year remains good while the order books are full", 110)) + ".");
                });
                page.Footer().AlignCenter().Text(text => text.CurrentPageNumber());
            });
        }).GeneratePdf(path);
        return path;
    }

    [Fact]
    public void APdfBecomesHeadingsParagraphsAndPicturesAgain()
    {
        using (var pdf = PdfDocument.Open(LaidOutPdf())) Assert.True(pdf.NumberOfPages >= 3);
        var read = PdfTextReader.ReadFile(LaidOutPdf(), TestContext.Current.CancellationToken);

        var headings = read.Blocks.OfType<Heading>().ToList();
        Assert.Equal(["The year in brief", "Outlook"], headings.Select(Text));
        Assert.Equal([1, 2], headings.Select(h => h.Level));

        var paragraph = read.Blocks.OfType<Paragraph>().First();
        Assert.Equal("Sales rose in every region, and the new warehouse opened on time.", Text(paragraph));
        Assert.Contains(paragraph.Inlines.OfType<TextRun>(), r => r.Text.Trim() == "every" && r.Has(TextStyle.Bold));
        Assert.Single(read.Pictures);

        // what stands on every page — the running title, the page number — is not part of the text
        Assert.DoesNotContain(read.Blocks, b => Text(b).Contains("confidential"));
        Assert.DoesNotContain(read.Blocks, b => Text(b).Trim() is "1" or "2" or "3");

        // the paragraph that ran over the page ends is one paragraph, with nothing between its parts
        var longOne = read.Blocks.OfType<Paragraph>().Single(p => Text(p).StartsWith("The outlook"));
        Assert.Equal(110, Text(longOne).Split("order books are full").Length - 1);
        Assert.DoesNotContain(read.Blocks, b => b is PageBreak);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("odt")]
    [InlineData("rtf")]
    [InlineData("html")]
    [InlineData("epub")]
    [InlineData("md")]
    public async Task APdfIsConvertedIntoEveryDocumentFormat(string target)
    {
        var result = await samples.ConvertAsync(LaidOutPdf(), target, BuiltIn);

        var file = Directory.Exists(result.OutputPath) ? Directory.GetFiles(result.OutputPath, "*." + target).Single() : result.OutputPath;
        var read = Read(file);
        Assert.Contains(read.Blocks.OfType<Heading>(), h => Text(h) == "The year in brief");
        Assert.Contains(RichDocument.Flatten(read.Blocks), b => Text(b).StartsWith("Sales rose in every region"));
        Assert.Single(read.Pictures);
    }

    // ---------- conversions as the app runs them ----------

    private string MarkdownFile()
    {
        var path = Path.Combine(samples.Folder, "recipe.md");
        if (!File.Exists(path))
            File.WriteAllText(path, "# Menemen\n\nEggs with **tomatoes** and *peppers*.\n\n- 2 eggs\n- 3 tomatoes\n\n| Step | Minutes |\n|---|---|\n| Fry | 5 |\n");
        return path;
    }

    [Fact]
    public async Task MarkdownBecomesAFormattedPdf()
    {
        var result = await samples.ConvertAsync(MarkdownFile(), "pdf");

        using var pdf = PdfDocument.Open(result.OutputPath);
        var page = pdf.GetPage(1);
        Assert.Equal((595, 842), (Math.Round(page.Width), Math.Round(page.Height)));
        Assert.Contains("Menemen", page.Text);
        Assert.DoesNotContain("#", page.Text);   // the marks are gone: they became formatting
        Assert.DoesNotContain("**", page.Text);

        // the heading is set larger than the text, and the bold word in a bold font
        var heading = page.Letters.First(l => l.Value == "M");
        var body = page.Letters.First(l => l.Value == "E");
        Assert.True(heading.PointSize > body.PointSize * 1.5);
        var words = page.GetWords().ToList();
        Assert.Contains("Bold", words.First(w => w.Text == "tomatoes").FontName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bold", words.First(w => w.Text == "Eggs").FontName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheBuiltInPdfFollowsThePaperMarginAndTextSize()
    {
        var usual = await samples.ConvertAsync(MarkdownFile(), "pdf");
        var chosen = await samples.ConvertAsync(MarkdownFile(), "pdf", (K.PageSize, "letter"), (K.Margin, "30"), (K.FontSize, "16"));

        using var first = PdfDocument.Open(usual.OutputPath);
        using var second = PdfDocument.Open(chosen.OutputPath);
        Assert.Equal((612, 792), (Math.Round(second.GetPage(1).Width), Math.Round(second.GetPage(1).Height)));
        var small = first.GetPage(1).Letters.First(l => l.Value == "E");
        var large = second.GetPage(1).Letters.First(l => l.Value == "E");
        Assert.InRange(large.PointSize / small.PointSize, 1.45, 1.6);
        Assert.True(large.Location.X > small.Location.X + 30);
    }

    [Fact]
    public async Task MarkdownBecomesAWordDocumentWithRealHeadingsAndLists()
    {
        var result = await samples.ConvertAsync(MarkdownFile(), "docx");

        using var word = WordprocessingDocument.Open(result.OutputPath, false);
        var body = word.MainDocumentPart!.Document!.Body!;
        var paragraphs = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().ToList();
        Assert.Equal("Heading1", paragraphs[0].ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Equal(2, paragraphs.Count(p => p.ParagraphProperties?.NumberingProperties != null));
        Assert.Single(body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>());
        Assert.Contains(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>(), r => r.InnerText == "tomatoes" && r.RunProperties?.Bold != null);
        // what a validator would say of the file
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(word, TestContext.Current.CancellationToken).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(5)));
    }

    [Fact]
    public async Task ADocumentWithPicturesIsAValidWordFileToo()
    {
        var result = await samples.ConvertAsync(Write(Article(), "html"), "docx");

        using var word = WordprocessingDocument.Open(result.OutputPath, false);
        Assert.Single(word.MainDocumentPart!.ImageParts);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(word, TestContext.Current.CancellationToken).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(5)));
    }

    [Fact]
    public async Task MarkdownWithoutPicturesIsOneFileAndWithPicturesAFolder()
    {
        var plain = await samples.ConvertAsync(Write(new RichDocument { Blocks = { new Paragraph("Only words.") } }, "html"), "md");
        Assert.True(File.Exists(plain.OutputPath));
        Assert.Equal("Only words.\n", File.ReadAllText(plain.OutputPath));

        var illustrated = await samples.ConvertAsync(Write(Article(), "docx"), "md", BuiltIn);
        Assert.True(Directory.Exists(illustrated.OutputPath));
        Assert.EndsWith("article markdown", illustrated.OutputPath);
        var text = File.ReadAllText(Path.Combine(illustrated.OutputPath, "article.md"));
        Assert.Contains("![A picture](images/image-001.png)", text);
        Assert.Equal(120u, new MagickImageInfo(Path.Combine(illustrated.OutputPath, "images", "image-001.png")).Width);
    }

    [Fact]
    public async Task AWordDocumentBecomesAWebPageThatNeedsNothingBesideIt()
    {
        var result = await samples.ConvertAsync(Write(Article(), "docx"), "html", BuiltIn);

        var html = File.ReadAllText(result.OutputPath);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<h1>Kitchen notes</h1>", html);
        Assert.Contains("<strong>always</strong>", html);
        Assert.Contains("<a href=\"https://example.org/tea\">the guide</a>", html);
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("<p />", html); // an element a browser would not take as closed
    }

    [Theory]
    [InlineData("docx", "odt")]
    [InlineData("odt", "docx")]
    [InlineData("rtf", "odt")]
    [InlineData("odt", "rtf")]
    [InlineData("epub", "docx")]
    [InlineData("html", "epub")]
    [InlineData("md", "rtf")]
    [InlineData("docx", "txt")]
    public async Task DocumentsAreConvertedIntoOneAnotherWithoutAnOfficeProgram(string from, string to)
    {
        var result = await samples.ConvertAsync(Write(Article(), from), to, BuiltIn);

        if (to == "txt")
        {
            var text = File.ReadAllText(result.OutputPath);
            Assert.Contains("Çay is always brewed slowly", text);
            Assert.Contains("1. tea", text);
            Assert.Contains("Tea\t250", text);
            return;
        }
        var read = Read(result.OutputPath);
        Assert.Contains(read.Blocks.OfType<Heading>(), h => Text(h) == "Kitchen notes");
        Assert.Contains(Runs(read), r => r.Text.Trim() == "always" && r.Has(TextStyle.Bold));
        Assert.Single(RichDocument.Flatten(read.Blocks).OfType<Table>());
        Assert.Single(read.Pictures);
    }

    [Fact]
    public async Task ADocumentsPagesBecomePictures()
    {
        var result = await samples.ConvertAsync(MarkdownFile(), "png", (K.Dpi, "72"));

        // one page: one picture, the size of an A4 sheet at 72 dots per inch
        var info = new MagickImageInfo(result.OutputPath);
        Assert.Equal((595u, 842u), (info.Width, info.Height));
    }

    [Fact]
    public async Task WithoutAnOfficeProgramADocFileIsRichTextUnderThatName()
    {
        var result = await samples.ConvertAsync(MarkdownFile(), "doc", BuiltIn);

        var text = File.ReadAllText(result.OutputPath);
        Assert.StartsWith(@"{\rtf1", text);
        Assert.Contains("Menemen", text);
        Assert.Equal("Menemen", Text(RtfReader.ReadFile(result.OutputPath).Blocks.OfType<Heading>().Single()));
    }

    [Fact]
    public async Task PlainTextKeepsItsLinesInTheNewFormats()
    {
        var path = Path.Combine(samples.NewOutputFolder(), "poem.txt");
        File.WriteAllText(path, "Gül\nLale\n\nSecond verse\n");

        var html = File.ReadAllText((await samples.ConvertAsync(path, "html")).OutputPath);
        Assert.Contains("<p>Gül<br />Lale</p><p>Second verse</p>", html);

        var odt = OdtReader.ReadFile((await samples.ConvertAsync(path, "odt")).OutputPath);
        Assert.Equal(["Gül Lale", "Second verse"], odt.Blocks.Select(Text));
    }

    // ---------- what is offered ----------

    [Fact]
    public void EveryTextFormatCanBecomeEveryOther()
    {
        string[] formats = ["docx", "odt", "rtf", "html", "md", "epub", "pdf"];
        foreach (var source in formats)
        {
            var targets = FormatRegistry.GetTargets(source, rarOutputAvailable: false);
            Assert.Equal(formats.Where(f => f != source).Order(), targets.Where(formats.Contains).Order());
            Assert.Contains("png", targets);
            Assert.Contains("txt", targets);
        }

        // a new document is set to become a PDF, a PDF to become a Word document
        Assert.Equal("pdf", FormatRegistry.GetTargets("docx", false)[0]);
        Assert.Equal("docx", FormatRegistry.GetTargets("pdf", false)[0]);
        // a page saved as .htm is HTML
        Assert.DoesNotContain("html", FormatRegistry.GetTargets("htm", false));
        // text is not offered as the text it already is
        Assert.DoesNotContain("md", FormatRegistry.GetTargets("txt", false));
        Assert.Contains("txt", FormatRegistry.GetTargets("md", false));
    }

    [Theory]
    [InlineData(@"C:\out\report.fc-1a2b3c4d.md", "report.md")]
    [InlineData(@"C:\out\report.md", "report.md")]
    [InlineData(@"C:\out\notes.fc-bundle.md", "notes.fc-bundle.md")]
    public void AResultIsNamedAfterItsFinalNameNotItsStagingName(string output, string expected) =>
        Assert.Equal(expected, ConversionService.FinalName(output.Replace('\\', Path.DirectorySeparatorChar)));
}
