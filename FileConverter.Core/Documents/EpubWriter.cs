using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace FileConverter.Core.Documents;

/// <summary>
/// Writes a document as an e-book (EPUB 3, with the older table of contents beside the new one
/// so that older readers find their way too). A new chapter starts at every top-level heading,
/// or — in a document without those — at every page break.
/// </summary>
internal static class EpubWriter
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Xhtml = XhtmlWriter.Xhtml;

    public static void Write(RichDocument document, string output, string fallbackTitle)
    {
        string title = document.Title ?? fallbackTitle;
        string language = document.Language ?? "en";
        string id = "urn:uuid:" + Guid.NewGuid();
        var chapters = SplitIntoChapters(document.Blocks);

        // every picture once, however often it is shown
        var pictures = new Dictionary<Picture, string>(ReferenceEqualityComparer.Instance);
        string Address(Picture picture)
        {
            if (!pictures.TryGetValue(picture, out var name))
            {
                name = $"images/image-{pictures.Count + 1:D3}.{picture.Format}";
                pictures[picture] = name;
            }
            return name;
        }

        var contents = new List<(string File, string Id, string Text, int Level)>();
        var files = new List<(string Name, string Xml)>();
        for (int i = 0; i < chapters.Count; i++)
        {
            string name = $"chapter-{i + 1:D3}.xhtml";
            var headings = new List<(Heading Heading, string Id)>();
            var body = new XElement(Xhtml + "body");
            XhtmlWriter.Append(body, chapters[i], Address, headings);

            foreach (var (heading, headingId) in headings.Where(h => h.Heading.Level <= 2))
                contents.Add((name, headingId, RichDocument.PlainText(heading.Inlines).Trim(), heading.Level));

            var page = new XElement(Xhtml + "html",
                new XAttribute(XNamespace.Xml + "lang", language), new XAttribute("lang", language),
                new XElement(Xhtml + "head",
                    new XElement(Xhtml + "title", headings.Count > 0 ? RichDocument.PlainText(headings[0].Heading.Inlines) : title),
                    new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"), new XAttribute("href", "style.css"))),
                body);
            files.Add((name, Document(page)));
        }
        contents.RemoveAll(entry => entry.Text.Length == 0);
        // a book without headings still needs somewhere for its table of contents to point
        if (contents.Count == 0) contents.Add((files[0].Name, "", title, 1));

        using var zip = new ZipArchive(new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None), ZipArchiveMode.Create);

        // first in the file and not compressed: this is how a reader knows what it has in hand
        Add(zip, "mimetype", "application/epub+zip"u8.ToArray(), CompressionLevel.NoCompression);
        Add(zip, "META-INF/container.xml", Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """));

        foreach (var (name, xml) in files) Add(zip, "OEBPS/" + name, Encoding.UTF8.GetBytes(xml));
        Add(zip, "OEBPS/style.css", Encoding.UTF8.GetBytes(EbookCss));
        foreach (var (picture, name) in pictures) Add(zip, "OEBPS/" + name, picture.Data, CompressionLevel.NoCompression);
        Add(zip, "OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(Navigation(contents, title, language)));
        Add(zip, "OEBPS/toc.ncx", Encoding.UTF8.GetBytes(OldContents(contents, title, id)));
        Add(zip, "OEBPS/content.opf", Encoding.UTF8.GetBytes(Package(document, title, language, id, files, pictures)));
    }

    /// <summary>Looser than a web page's: an e-book reader sets the font, the size and the margins itself.</summary>
    private const string EbookCss = """
        body { line-height: 1.4; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.2; margin: 1.2em 0 0.5em; page-break-after: avoid; }
        p { margin: 0 0 0.7em; }
        img { max-width: 100%; height: auto; }
        pre { white-space: pre-wrap; font-size: 0.85em; background: #f4f4f4; padding: 0.6em; }
        code { font-family: monospace; }
        blockquote { margin: 0 0 0.7em 0; padding-left: 1em; border-left: 3px solid #bbb; }
        table { border-collapse: collapse; margin: 0 0 1em; }
        th, td { border: 1px solid #999; padding: 0.3em 0.5em; vertical-align: top; text-align: left; }
        .page-break { page-break-after: always; }
        """;

    private static List<List<Block>> SplitIntoChapters(List<Block> blocks)
    {
        bool byHeading = blocks.Count(block => block is Heading { Level: 1 }) >= 2;
        var chapters = new List<List<Block>> { new() };
        foreach (var block in blocks)
        {
            bool starts = byHeading ? block is Heading { Level: 1 } : block is PageBreak;
            if (starts && chapters[^1].Count > 0) chapters.Add([]);
            if (block is PageBreak) continue; // every chapter begins on a new page as it is
            chapters[^1].Add(block);
        }
        chapters.RemoveAll(chapter => chapter.Count == 0);
        if (chapters.Count == 0) chapters.Add([new Paragraph("")]);
        return chapters;
    }

    private static void Add(ZipArchive zip, string name, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static string Document(XElement root, string? docType = "<!DOCTYPE html>") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + (docType == null ? "" : docType + "\n") + XhtmlWriter.Serialize(root, asXml: true) + "\n";

    private static string Navigation(List<(string File, string Id, string Text, int Level)> contents, string title, string language)
    {
        var list = new XElement(Xhtml + "ol");
        XElement? last = null;
        foreach (var entry in contents)
        {
            var item = new XElement(Xhtml + "li",
                new XElement(Xhtml + "a", new XAttribute("href", entry.Id.Length > 0 ? $"{entry.File}#{entry.Id}" : entry.File), entry.Text));
            if (entry.Level >= 2 && last != null)
            {
                // sub-headings hang under the chapter before them
                var inner = last.Element(Xhtml + "ol");
                if (inner == null) last.Add(inner = new XElement(Xhtml + "ol"));
                inner.Add(item);
            }
            else
            {
                list.Add(item);
                last = item;
            }
        }

        return Document(new XElement(Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", Ops), new XAttribute(XNamespace.Xml + "lang", language), new XAttribute("lang", language),
            new XElement(Xhtml + "head", new XElement(Xhtml + "title", title)),
            new XElement(Xhtml + "body",
                new XElement(Xhtml + "nav", new XAttribute(Ops + "type", "toc"), new XAttribute("id", "toc"),
                    new XElement(Xhtml + "h1", title), list))));
    }

    private static string OldContents(List<(string File, string Id, string Text, int Level)> contents, string title, string id)
    {
        var map = new XElement(Ncx + "navMap");
        for (int i = 0; i < contents.Count; i++)
        {
            var entry = contents[i];
            map.Add(new XElement(Ncx + "navPoint", new XAttribute("id", $"n{i + 1}"), new XAttribute("playOrder", i + 1),
                new XElement(Ncx + "navLabel", new XElement(Ncx + "text", entry.Text)),
                new XElement(Ncx + "content", new XAttribute("src", entry.Id.Length > 0 ? $"{entry.File}#{entry.Id}" : entry.File))));
        }
        return Document(new XElement(Ncx + "ncx", new XAttribute("version", "2005-1"),
            new XElement(Ncx + "head",
                new XElement(Ncx + "meta", new XAttribute("name", "dtb:uid"), new XAttribute("content", id)),
                new XElement(Ncx + "meta", new XAttribute("name", "dtb:depth"), new XAttribute("content", "1"))),
            new XElement(Ncx + "docTitle", new XElement(Ncx + "text", title)),
            map), docType: null);
    }

    private static string Package(RichDocument document, string title, string language, string id,
        List<(string Name, string Xml)> files, Dictionary<Picture, string> pictures)
    {
        var manifest = new XElement(Opf + "manifest",
            new XElement(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
            new XElement(Opf + "item", new XAttribute("id", "ncx"), new XAttribute("href", "toc.ncx"), new XAttribute("media-type", "application/x-dtbncx+xml")),
            new XElement(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "style.css"), new XAttribute("media-type", "text/css")));
        var spine = new XElement(Opf + "spine", new XAttribute("toc", "ncx"));
        for (int i = 0; i < files.Count; i++)
        {
            manifest.Add(new XElement(Opf + "item", new XAttribute("id", $"c{i + 1}"), new XAttribute("href", files[i].Name),
                new XAttribute("media-type", "application/xhtml+xml")));
            spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", $"c{i + 1}")));
        }
        int number = 0;
        foreach (var (picture, name) in pictures)
            manifest.Add(new XElement(Opf + "item", new XAttribute("id", $"i{++number}"), new XAttribute("href", name), new XAttribute("media-type", picture.Mime)));

        var metadata = new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "book-id"), id),
            new XElement(Dc + "title", title),
            new XElement(Dc + "language", language),
            document.Author == null ? null : new XElement(Dc + "creator", document.Author),
            new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));

        return Document(new XElement(Opf + "package", new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "book-id"),
            new XAttribute(XNamespace.Xml + "lang", language), metadata, manifest, spine), docType: null);
    }
}
