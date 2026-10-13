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

    private static readonly string[] DocumentInputs = ["txt", "pdf", "doc", "docx", "md", "log"];
    private static readonly string[] DocumentOutputs = ["txt", "pdf", "doc", "docx"];

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

    /// <summary>jpg and jpeg (and tif/tiff) are the same format; collapse for self-exclusion.</summary>
    private static string Canonical(string ext) => ext switch
    {
        "jpeg" => "jpg",
        "tif" => "tiff",
        "tgz" => "gz",
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
                // md/log behave as plain-text sources
                var docSelf = ext is "md" or "log" ? "txt" : ext;
                targets.AddRange(DocumentOutputs.Where(t => t != docSelf));
                if (ext == "pdf")
                    targets.AddRange(["png", "jpg"]); // one image per page
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
