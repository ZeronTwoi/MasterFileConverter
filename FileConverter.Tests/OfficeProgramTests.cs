using System.Diagnostics;
using FileConverter.Core;
using FileConverter.Core.Documents;
using FileConverter.Core.Platform;
using FileConverter.Core.Sheets;
using FileConverter.Core.Slides;
using UglyToad.PdfPig;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// The office programs as conversion engines, on the machines that have them: Microsoft Word,
/// Excel and PowerPoint (Windows) and LibreOffice. Each test is skipped where its program is
/// not installed — a build machine has none, and converts with the built-in engine throughout.
/// </summary>
public class OfficeProgramTests(Samples samples) : IClassFixture<Samples>
{
    private static readonly (string Key, string Value) Office = (K.Engine, "office");
    private static readonly (string Key, string Value) LibreOffice = (K.Engine, "libreoffice");

    private static void RequireOffice(string source, string target) =>
        Assert.SkipUnless(PlatformServices.Current.OfficeAutomation?.CanConvert(source, target) == true,
            $"No installed office program converts .{source} to .{target} on this machine.");

    private static void RequireLibreOffice() =>
        Assert.SkipUnless(ToolLocator.LibreOfficePath != null, "LibreOffice is not installed on this machine.");

    private string File(string name) => Path.Combine(samples.NewOutputFolder(), name);

    private string Workbook()
    {
        var path = File("figures.xlsx");
        var workbook = new Workbook();
        var sheet = new Sheet("Sales");
        sheet.Rows.Add([Cell.OfText("Item"), Cell.OfText("Count")]);
        sheet.Rows.Add([Cell.OfText("Çay"), Cell.OfNumber(12.5)]);
        workbook.Sheets.Add(sheet);
        XlsxWriter.Write(workbook, path);
        return path;
    }

    private string Presentation(string extension = "pptx")
    {
        var path = File("talk." + extension);
        var deck = new Deck();
        var slide = new Slide();
        var title = new SlideText { X = 60, Y = 60, Width = 840, Height = 100, IsTitle = true };
        var paragraph = new SlideParagraph { FontSize = 40 };
        paragraph.Runs.Add(new TextRun("Çay nasıl demlenir"));
        title.Paragraphs.Add(paragraph);
        slide.Items.Add(title);
        deck.Slides.Add(slide);
        if (extension == "pptx") PptxWriter.Write(deck, path);
        else OdpWriter.Write(deck, path);
        return path;
    }

    private string Document(string extension)
    {
        var path = File("letter." + extension);
        var document = new RichDocument();
        document.Blocks.Add(new Heading(1, [new TextRun("Mektup")]));
        document.Blocks.Add(new Paragraph("Sevgili Ayşe, çay hazır."));
        if (extension == "docx") DocxWriter.Write(document, path);
        else if (extension == "odt") OdtWriter.Write(document, path);
        else RtfWriter.Write(document, path);
        return path;
    }

    private static string PdfText(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return string.Join(" ", pdf.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
    }

    // ---------- Microsoft Office ----------

    [Fact]
    public async Task ExcelPrintsAWorkbookToPdfAndWritesTheOtherFormats()
    {
        RequireOffice("xlsx", "pdf");
        var workbook = Workbook();

        Assert.Contains("Çay", PdfText((await samples.ConvertAsync(workbook, "pdf", Office)).OutputPath));

        var ods = OdsReader.ReadFile((await samples.ConvertAsync(workbook, "ods", Office)).OutputPath);
        Assert.Equal((CellKind.Number, 12.5), (ods.Sheets[0][1, 1].Kind, ods.Sheets[0][1, 1].Number));
    }

    [Fact]
    public async Task PowerPointDrawsAPresentationToPdfAndWritesTheOtherFormats()
    {
        RequireOffice("pptx", "pdf");

        Assert.Contains("demlenir", PdfText((await samples.ConvertAsync(Presentation(), "pdf", Office)).OutputPath));

        var odp = OdpReader.ReadFile((await samples.ConvertAsync(Presentation(), "odp", Office)).OutputPath);
        Assert.Contains(odp.Slides.Single().Items.OfType<SlideText>(), text => RichDocument.PlainText(text.Paragraphs[0].Runs) == "Çay nasıl demlenir");

        // and it opens the OpenDocument presentation the app writes
        var pptx = PptxReader.ReadFile((await samples.ConvertAsync(Presentation("odp"), "pptx", Office)).OutputPath);
        Assert.Single(pptx.Slides);
    }

    [Theory]
    [InlineData("docx", "odt")]
    [InlineData("odt", "docx")]
    [InlineData("rtf", "pdf")]
    [InlineData("docx", "rtf")]
    public async Task WordConvertsBetweenItsFormatsOpenDocumentAndRtf(string from, string to)
    {
        RequireOffice(from, to);
        var result = await samples.ConvertAsync(Document(from), to, Office);

        var text = to switch
        {
            "pdf" => PdfText(result.OutputPath),
            "odt" => string.Join(" ", OdtReader.ReadFile(result.OutputPath).Blocks.Select(Words)),
            "docx" => string.Join(" ", DocxReader.ReadFile(result.OutputPath).Blocks.Select(Words)),
            _ => string.Join(" ", RtfReader.ReadFile(result.OutputPath).Blocks.Select(Words))
        };
        Assert.Contains("Sevgili Ayşe, çay hazır.", text);

        static string Words(Block block) => block switch
        {
            Paragraph paragraph => RichDocument.PlainText(paragraph.Inlines),
            Heading heading => RichDocument.PlainText(heading.Inlines),
            _ => ""
        };
    }

    /// <summary>
    /// A hidden office program must not outlive its work. PowerPoint runs once per user: when the
    /// user has it open, that one is used and must stay; one started for the conversion must go.
    /// </summary>
    [Fact]
    public async Task NoHiddenOfficeProgramIsLeftBehind()
    {
        RequireOffice("pptx", "pdf");
        RequireOffice("xlsx", "pdf");
        // a program the user has open would be theirs: only a machine without one can show that ours are gone
        string[] names = ["POWERPNT", "EXCEL"];
        Assert.SkipWhen(names.Any(name => Process.GetProcessesByName(name).Length > 0), "An office program is open on this machine.");

        await samples.ConvertAsync(Presentation(), "pdf", Office);
        await samples.ConvertAsync(Workbook(), "pdf", Office);

        // each is closed a few seconds after its last job
        var until = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < until && names.Any(name => Process.GetProcessesByName(name).Length > 0))
            await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.All(names, name => Assert.Empty(Process.GetProcessesByName(name)));
    }

    // ---------- LibreOffice ----------

    [Theory]
    [InlineData("xlsx", "pdf")]
    [InlineData("pptx", "pdf")]
    [InlineData("odt", "docx")]
    public async Task LibreOfficeConvertsWhatTheAppWrites(string from, string to)
    {
        RequireLibreOffice();
        var source = from switch
        {
            "xlsx" => Workbook(),
            "pptx" => Presentation(),
            _ => Document(from)
        };
        var result = await samples.ConvertAsync(source, to, LibreOffice);

        var text = to == "pdf"
            ? PdfText(result.OutputPath)
            : string.Join(" ", DocxReader.ReadFile(result.OutputPath).Blocks.OfType<Paragraph>().Select(p => RichDocument.PlainText(p.Inlines)));
        Assert.Contains(from switch { "xlsx" => "Çay", "pptx" => "demlenir", _ => "çay hazır" }, text);
    }

    [Fact]
    public async Task APdfBecomesAnEditablePresentationWhenLibreOfficeIsAskedFor()
    {
        RequireLibreOffice();
        var pdf = (await samples.ConvertAsync(Document("docx"), "pdf", (K.Engine, "builtin"))).OutputPath;

        var deck = PptxReader.ReadFile((await samples.ConvertAsync(pdf, "pptx", LibreOffice)).OutputPath);
        // text that can be edited, where the app's own way gives a picture of the page
        Assert.Contains(deck.Slides.SelectMany(slide => slide.Items).OfType<SlideText>(),
            text => text.Paragraphs.Any(paragraph => RichDocument.PlainText(paragraph.Runs).Contains("Sevgili")));
    }
}
