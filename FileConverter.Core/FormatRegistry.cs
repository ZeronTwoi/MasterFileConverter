using System.IO;

namespace FileConverter.Core;

public enum FileCategory
{
    Image,
    Audio,
    Video,
    Document,
    Archive,
    Other
}

/// <summary>What a document is made of, which decides what it can be turned into.</summary>
public enum DocumentKind
{
    None,
    /// <summary>Fixed pages: a PDF.</summary>
    Pdf,
    /// <summary>Running text: Word, OpenDocument text, RTF, HTML, Markdown, e-books, plain text.</summary>
    Text,
    /// <summary>Tables of cells: Excel, OpenDocument spreadsheets, comma-separated text.</summary>
    Sheet,
    /// <summary>Slides: PowerPoint, OpenDocument presentations.</summary>
    Slides
}

/// <summary>
/// Knows which file extensions belong to which category and which target
/// formats a given source file can be converted to.
/// </summary>
public static class FormatRegistry
{
    private static readonly string[] ImageInputs = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "heic", "ico"];
    private static readonly string[] ImageOutputs = ["jpg", "png", "gif", "webp", "bmp", "tiff"];

    private static readonly string[] AudioInputs = ["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus"];
    private static readonly string[] AudioOutputs = ["mp3", "wav", "flac", "aac", "m4a", "ogg", "opus"];

    private static readonly string[] VideoInputs = ["mp4", "mkv", "avi", "webm", "mov", "wmv", "flv", "ts", "m4v"];
    private static readonly string[] VideoOutputs = ["mp4", "mkv", "avi", "webm", "mov"];

    private static readonly string[] TextDocuments = ["txt", "md", "log", "rtf", "html", "htm", "docx", "doc", "odt", "epub"];
    private static readonly string[] Spreadsheets = ["xlsx", "xls", "ods", "csv"];
    private static readonly string[] Presentations = ["pptx", "ppt", "odp"];
    private static readonly string[] DocumentInputs = ["pdf", .. TextDocuments, .. Spreadsheets, .. Presentations];

    /// <summary>What running text can be written as, the most asked-for first (the first is what a new file is set to).</summary>
    private static readonly string[] TextOutputs = ["pdf", "docx", "odt", "rtf", "txt", "html", "md", "epub", "doc"];

    private static readonly string[] ArchiveInputs = ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2"];

    /// <summary>Every file type the app reads: what the Windows installer lists it under "Open with" for.</summary>
    public static IReadOnlyList<string> Inputs { get; } =
        [.. ImageInputs, .. AudioInputs, .. VideoInputs, .. DocumentInputs, .. ArchiveInputs];

    public static string GetExtension(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileCategory GetCategory(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (ImageInputs.Contains(ext)) return FileCategory.Image;
        if (AudioInputs.Contains(ext)) return FileCategory.Audio;
        if (VideoInputs.Contains(ext)) return FileCategory.Video;
        if (DocumentInputs.Contains(ext)) return FileCategory.Document;
        if (ArchiveInputs.Contains(ext)) return FileCategory.Archive;
        return FileCategory.Other;
    }

    public static DocumentKind GetDocumentKind(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (ext == "pdf") return DocumentKind.Pdf;
        if (TextDocuments.Contains(ext)) return DocumentKind.Text;
        if (Spreadsheets.Contains(ext)) return DocumentKind.Sheet;
        if (Presentations.Contains(ext)) return DocumentKind.Slides;
        return DocumentKind.None;
    }

    /// <summary>jpg and jpeg (tif/tiff, htm/html) are the same format under two names: the one it is known by here.</summary>
    public static string Canonical(string ext) => ext switch
    {
        "jpeg" => "jpg",
        "tif" => "tiff",
        "tgz" => "gz",
        "htm" => "html",
        _ => ext
    };

    public static List<string> GetTargets(string sourceExt, bool rarOutputAvailable)
    {
        var ext = Canonical(sourceExt.ToLowerInvariant());
        var category = GetCategory(ext);
        var targets = new List<string>();

        switch (category)
        {
            case FileCategory.Image:
                targets.AddRange(ImageOutputs.Where(t => t != ext));
                targets.Add("pdf"); // one page per image; multiple images combine into one PDF
                if (ext == "gif")
                    targets.AddRange(["mp4", "mkv", "webm"]);
                break;

            case FileCategory.Audio:
                targets.AddRange(AudioOutputs.Where(t => t != ext));
                break;

            case FileCategory.Video:
                targets.AddRange(VideoOutputs.Where(t => t != ext));
                targets.AddRange(["mp3", "wav", "flac", "aac", "m4a"]); // extract audio
                targets.Add("gif");
                targets.AddRange(["png", "jpg"]); // frame sequence
                break;

            case FileCategory.Document:
                targets.AddRange(DocumentTargets(ext));
                break;

            case FileCategory.Archive:
                targets.AddRange(new[] { "zip", "7z", "tar" }.Where(t => t != ext));
                if (rarOutputAvailable && ext != "rar") targets.Add("rar");
                break;
        }

        // Anything can additionally be packed into an archive.
        if (category != FileCategory.Archive)
        {
            if (!targets.Contains("zip")) targets.Add("zip");
            if (!targets.Contains("7z")) targets.Add("7z");
        }

        return targets;
    }

    private static IEnumerable<string> DocumentTargets(string ext)
    {
        switch (GetDocumentKind(ext))
        {
            case DocumentKind.Pdf:
                // back into something that can be edited, its text and structure in another form, or its tables
                foreach (var target in new[] { "docx", "xlsx", "pptx", "txt", "html", "md", "rtf", "odt", "ods", "odp", "csv", "epub", "doc" }) yield return target;
                break;

            case DocumentKind.Slides:
                // (the old binary .ppt is read, not written)
                foreach (var target in new[] { "pdf", "pptx", "odp" })
                    if (target != ext) yield return target;
                break;

            case DocumentKind.Sheet:
                // (the old binary .xls is read, not written)
                foreach (var target in new[] { "pdf", "xlsx", "ods", "csv", "html" })
                    if (target != ext) yield return target;
                break;

            case DocumentKind.Text:
                foreach (var target in TextOutputs)
                {
                    if (target == ext) continue;
                    // plain text is plain text under either name, and already is the Markdown it would become
                    if (ext is "txt" or "log" && target is "txt" or "md") continue;
                    yield return target;
                }
                break;
        }

        // every document's pages as pictures
        yield return "png";
        yield return "jpg";
    }

    /// <summary>Which conversion engine handles a given source → target pair.</summary>
    public static FileCategory GetEngine(string sourceExt, string targetExt)
    {
        var src = Canonical(sourceExt.ToLowerInvariant());
        var dst = Canonical(targetExt.ToLowerInvariant());

        if (ArchiveInputs.Contains(dst)) return FileCategory.Archive;

        var srcCat = GetCategory(src);
        if (srcCat == FileCategory.Image)
            return src == "gif" && VideoInputs.Contains(dst) ? FileCategory.Video : FileCategory.Image;
        if (srcCat is FileCategory.Audio or FileCategory.Video) return FileCategory.Video; // ffmpeg handles both
        if (srcCat == FileCategory.Document) return FileCategory.Document;
        return FileCategory.Other;
    }
}
