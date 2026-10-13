using System.Text;
using RtfPipe;

namespace FileConverter.Core.Documents;

/// <summary>
/// Reads Rich Text Format (.rtf). The format itself is taken apart by RtfPipe, which hands on
/// HTML; from there it is read like any other HTML — headings, formatting, lists, tables and
/// the pictures stored in the file.
/// </summary>
internal static class RtfReader
{
    public static RichDocument ReadFile(string path)
    {
        // an RTF file names the code page its accented letters are written in
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6 || !bytes.AsSpan(0, 5).SequenceEqual("{\\rtf"u8))
            throw new FriendlyException("This file is not in Rich Text Format.");

        string html;
        try
        {
            using var stream = new MemoryStream(bytes);
            html = Rtf.ToHtml(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FriendlyException("This RTF file could not be read: it is damaged or uses something unusual.");
        }
        return new HtmlReader().Read(html);
    }
}
