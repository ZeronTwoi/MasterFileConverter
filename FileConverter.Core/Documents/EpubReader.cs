using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace FileConverter.Core.Documents;

/// <summary>
/// Reads an e-book (.epub): a zip of HTML chapters with a list that says in which order they are
/// read. The chapters follow one another, each starting on a new page. Copy-protected books
/// cannot be read — their chapters are encrypted.
/// </summary>
internal static class EpubReader
{
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Encryption = "http://www.w3.org/2001/04/xmlenc#";

    public static RichDocument ReadFile(string path)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new FriendlyException("This file is not an e-book in EPUB format, or it is damaged.");
        }

        using (zip)
        {
            var packagePath = Odf.ReadXml(zip, "META-INF/container.xml")?.Descendants(Container + "rootfile")
                .Select(r => r.Attribute("full-path")?.Value).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            var package = packagePath == null ? null : Odf.ReadXml(zip, packagePath);
            if (package?.Root == null)
                throw new FriendlyException("This file is not an e-book in EPUB format, or it is damaged.");

            var folder = packagePath!.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";
            var items = package.Root.Element(Opf + "manifest")?.Elements(Opf + "item")
                .Where(i => i.Attribute("id") != null && i.Attribute("href") != null)
                .GroupBy(i => i.Attribute("id")!.Value).ToDictionary(g => g.Key, g => g.First()) ?? [];
            var chapters = package.Root.Element(Opf + "spine")?.Elements(Opf + "itemref")
                .Where(r => r.Attribute("linear")?.Value != "no")
                .Select(r => r.Attribute("idref")?.Value)
                .Where(id => id != null && items.ContainsKey(id))
                .Select(id => Resolve(folder, items[id!].Attribute("href")!.Value))
                .ToList() ?? [];
            if (chapters.Count == 0) throw new FriendlyException("This e-book names no chapters to read.");

            // Fonts are often scrambled, which is harmless; scrambled chapters mean copy protection.
            var encrypted = Odf.ReadXml(zip, "META-INF/encryption.xml")?.Descendants(Encryption + "CipherReference")
                .Select(c => Uri.UnescapeDataString(c.Attribute("URI")?.Value ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            if (chapters.Any(encrypted.Contains))
                throw new FriendlyException("This e-book is copy-protected (DRM); its text cannot be read.");

            var metadata = package.Root.Element(Opf + "metadata");
            var document = new RichDocument
            {
                Title = Value(metadata?.Element(Dc + "title")),
                Author = Value(metadata?.Element(Dc + "creator")),
                Language = Value(metadata?.Element(Dc + "language"))
            };

            foreach (var chapter in chapters)
            {
                var bytes = Odf.ReadBytes(zip, chapter);
                if (bytes == null) continue;

                var chapterFolder = chapter.Contains('/') ? chapter[..(chapter.LastIndexOf('/') + 1)] : "";
                var reader = new HtmlReader(address => Odf.ReadBytes(zip, Resolve(chapterFolder, address)));
                var blocks = reader.Read(HtmlReader.Decode(bytes)).Blocks;
                if (blocks.Count == 0) continue;

                if (document.Blocks.Count > 0) document.Blocks.Add(new PageBreak());
                document.Blocks.AddRange(blocks);
            }
            return document;
        }
    }

    private static string? Value(XElement? element) => string.IsNullOrWhiteSpace(element?.Value) ? null : element.Value.Trim();

    /// <summary>"../images/a%20b.png" as seen from "OEBPS/text/" → "OEBPS/images/a b.png".</summary>
    private static string Resolve(string folder, string address)
    {
        address = Uri.UnescapeDataString(address.Split('#', '?')[0]);
        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (address.StartsWith('/')) parts.Clear();
        foreach (var part in address.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return new StringBuilder().AppendJoin('/', parts).ToString();
    }
}
