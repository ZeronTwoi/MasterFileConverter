using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Settings;

public enum SettingKind
{
    /// <summary>A number within a range; empty means automatic.</summary>
    Number,
    /// <summary>One of a few named values; the first entry shown is "automatic".</summary>
    Choice,
    Text,
    /// <summary>Text that is hidden while typed and never written to disk (a password).</summary>
    Secret
}

public sealed record SettingChoice(string Value, string Label);

/// <summary>One precise setting as the Detailed Settings panel shows it.</summary>
public sealed record SettingDefinition(string Key, string Label, SettingKind Kind)
{
    /// <summary>Heading the setting is listed under.</summary>
    public string Group { get; init; } = "General";

    /// <summary>What the setting does, shown as a tooltip.</summary>
    public string Help { get; init; } = "";

    /// <summary>What happens when it is left alone, e.g. "Auto (97)".</summary>
    public string Automatic { get; init; } = "Automatic";

    public double Min { get; init; }
    public double Max { get; init; } = double.MaxValue;
    public bool AllowDecimals { get; init; }
    public IReadOnlyList<SettingChoice> Choices { get; init; } = [];

    /// <summary>
    /// Setting this means the video or audio can no longer be copied as it is: it has to be
    /// decoded and encoded again, so "100 %" stops meaning a bit-for-bit copy.
    /// </summary>
    public bool Reencodes { get; init; }

    public bool IsSecret => Kind == SettingKind.Secret;

    /// <summary>Tidies what the user typed; null when it is empty or not usable (the setting is then automatic).</summary>
    public string? Normalize(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        switch (Kind)
        {
            case SettingKind.Number:
                if (!double.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    return null;
                number = Math.Clamp(number, Min, Max);
                if (!AllowDecimals) number = Math.Round(number);
                return number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            case SettingKind.Choice:
                return Choices.Any(c => c.Value == raw) ? raw : null;
            default:
                return raw;
        }
    }
}

/// <summary>
/// Which precise settings exist for a conversion. They are grouped by the kind of file that is
/// produced, and saved per target format — "everything I turn into MP4" — so the list for a
/// conversion depends on what goes in as well as on what comes out (PDF → JPG has a resolution,
/// video → JPG a frame rate, photo → JPG neither).
/// </summary>
public static class SettingCatalog
{
    private static readonly string[] ImageTargets = ["jpg", "png", "webp", "bmp", "tiff"];
    private static readonly string[] VideoTargets = ["mp4", "mkv", "mov", "webm", "avi"];
    private static readonly string[] AudioTargets = ["mp3", "wav", "flac", "aac", "m4a", "ogg", "opus"];
    private static readonly string[] ArchiveTargets = ["zip", "7z", "tar", "rar"];

    /// <summary>The kind of result a target format is; presets are kept per kind.</summary>
    public static string KindOf(string target) => target switch
    {
        _ when ImageTargets.Contains(target) => "image",
        "gif" => "gif",
        _ when VideoTargets.Contains(target) => "video",
        _ when AudioTargets.Contains(target) => "audio",
        _ when ArchiveTargets.Contains(target) => "archive",
        "pdf" => "pdf",
        _ => "document"
    };

    /// <param name="merging">Several files are being combined into this one target.</param>
    public static IReadOnlyList<SettingDefinition> For(string sourceExtension, string target, bool merging = false)
    {
        var source = FormatRegistry.GetCategory(sourceExtension);
        var list = new List<SettingDefinition>();

        if (ArchiveTargets.Contains(target))
        {
            Archive(list, target);
        }
        else if (ImageTargets.Contains(target))
        {
            if (source == FileCategory.Video) FramesFromVideo(list, target);
            else if (source == FileCategory.Document)
            {
                // a document's pages as pictures: drawn from a PDF, which anything but a PDF is made into first
                PagesFromPdf(list, target);
                if (Converters.DocumentConversion.OfficeMayConvert(sourceExtension, "pdf")) DocumentEngine(list);
                else if (sourceExtension != "pdf") PdfFromText(list);
            }
            else Picture(list, target, resize: true, metadata: true);
        }
        else if (target == "gif")
        {
            if (source == FileCategory.Video) GifFromVideo(list);
            else GifFromPictures(list, merging);
        }
        else if (VideoTargets.Contains(target))
        {
            Video(list, target, hasAudio: source == FileCategory.Video);
        }
        else if (AudioTargets.Contains(target))
        {
            Audio(list, target);
        }
        else if (target == "pdf")
        {
            if (source == FileCategory.Image) PdfFromPictures(list);
            // what an office program lays out follows the document's own page setup; the built-in converter asks
            else if (Converters.DocumentConversion.OfficeMayConvert(sourceExtension, target)) DocumentEngine(list);
            else PdfFromText(list);
        }
        else if (source == FileCategory.Document && Converters.DocumentConversion.OfficeMayConvert(sourceExtension, target))
        {
            DocumentEngine(list);
        }
        return list;
    }

    // ---------- pictures ----------

    private static void Picture(List<SettingDefinition> list, string target, bool resize, bool metadata)
    {
        const string group = "Quality";
        if (target is "jpg" or "webp")
        {
            list.Add(new(K.Quality, "Quality", SettingKind.Number)
            {
                Group = group, Min = 1, Max = 100,
                Automatic = target == "jpg" ? "Auto (97)" : "Auto (lossless, or 96)",
                Help = "Encoder quality from 1 (smallest file) to 100 (best picture). " +
                       "A value here replaces the File size slider for these files."
            });
        }
        if (target == "webp")
        {
            list.Add(Choice(K.Lossless, "Lossless", group, "Auto (from the source)",
                "Lossless WebP keeps every pixel exactly, like PNG, and is usually smaller than PNG. Lossy WebP is far smaller for photos.",
                ("on", "Always lossless"), ("off", "Always lossy")));
        }
        if (target == "jpg")
        {
            list.Add(Choice(K.Chroma, "Colour detail", group, "Auto (full, 4:4:4)",
                "How much colour resolution is kept. 4:4:4 keeps coloured edges and text sharp; 4:2:0 makes smaller files and is what cameras use.",
                ("444", "Full (4:4:4)"), ("422", "Half (4:2:2)"), ("420", "Quarter (4:2:0)")));
            list.Add(Choice(K.Progressive, "Progressive", group, "Auto (off)",
                "A progressive JPEG appears blurry first and sharpens as it loads on a web page; it is often a little smaller.",
                ("on", "On"), ("off", "Off")));
        }
        if (target == "png")
        {
            list.Add(new(K.Compression, "Compression effort", SettingKind.Number)
            {
                Group = group, Min = 0, Max = 9, Automatic = "Auto (from Speed)",
                Help = "0 (fastest, largest) to 9 (slowest, smallest). PNG is always lossless: this never changes the picture."
            });
        }
        if (target == "tiff")
        {
            list.Add(Choice(K.Compression, "Compression", group, "Auto (LZW)",
                "All three keep the picture exactly. Uncompressed opens in anything; ZIP is smallest.",
                ("none", "Uncompressed"), ("lzw", "LZW"), ("zip", "ZIP (deflate)")));
        }
        if (target is "jpg" or "webp")
        {
            list.Add(new(K.TargetSizeKb, "Target size (KB)", SettingKind.Number)
            {
                Group = group, Min = 1, Max = 1_000_000, Automatic = "No size target",
                Help = "The quality is lowered until the file fits into this many kilobytes. Overrides Quality and the File size slider."
            });
        }

        if (resize) Resize(list, upscale: true);

        if (metadata && target is "jpg" or "png" or "webp" or "tiff")
        {
            list.Add(Choice(K.Metadata, "Photo information", "Other", "Auto (keep)",
                "Camera, date and location data stored inside the picture (EXIF/XMP). The colour profile is handled either way.",
                ("keep", "Keep everything"), ("nogps", "Remove location only"), ("strip", "Remove everything")));
        }
        if (target is "jpg" or "bmp")
        {
            list.Add(new(K.Background, "Background colour", SettingKind.Text)
            {
                Group = "Other", Automatic = "Auto (white)",
                Help = $"{target.ToUpperInvariant()} cannot store transparency; transparent areas are filled with this colour. " +
                       "Type a colour name or a hex value such as #000000."
            });
        }
    }

    private static void Resize(List<SettingDefinition> list, bool upscale)
    {
        const string group = "Size";
        list.Add(new(K.MaxWidth, "Maximum width (px)", SettingKind.Number)
        {
            Group = group, Min = 16, Max = 16384, Automatic = "Keep original",
            Help = "Pictures wider than this are scaled down, keeping their proportions. Smaller ones are never enlarged."
        });
        list.Add(new(K.MaxHeight, "Maximum height (px)", SettingKind.Number)
        {
            Group = group, Min = 16, Max = 16384, Automatic = "Keep original",
            Help = "Pictures taller than this are scaled down, keeping their proportions. Smaller ones are never enlarged."
        });
        if (upscale)
        {
            list.Add(new(K.Scale, "Scale (%)", SettingKind.Number)
            {
                Group = group, Min = 1, Max = 400, AllowDecimals = true, Automatic = "100",
                Help = "Resize to this percentage of the original. Above 100 enlarges (with a high-quality filter); the maximum width and height still apply."
            });
        }
    }

    private static void FramesFromVideo(List<SettingDefinition> list, string target)
    {
        const string group = "Frames";
        list.Add(new(K.Fps, "Frames per second", SettingKind.Number)
        {
            Group = group, Min = 0.01, Max = 240, AllowDecimals = true, Automatic = "Auto (about 300 frames)",
            Help = "How many pictures to take from each second of video: 1 = one per second, 0.1 = one every ten seconds. Never more than the video has."
        });
        list.Add(new(K.MaxFrames, "Maximum number of frames", SettingKind.Number)
        {
            Group = group, Min = 1, Max = 100_000, Automatic = "No limit",
            Help = "Stop after this many pictures."
        });
        if (target == "jpg")
        {
            list.Add(new(K.Quality, "Quality", SettingKind.Number)
            {
                Group = "Quality", Min = 1, Max = 100, Automatic = "Auto (best)",
                Help = "JPEG quality of each frame, 1 (smallest) to 100 (best)."
            });
        }
        Resize(list, upscale: false);
    }

    private static void PagesFromPdf(List<SettingDefinition> list, string target)
    {
        const string group = "Pages";
        list.Add(new(K.Dpi, "Resolution (dpi)", SettingKind.Number)
        {
            Group = group, Min = 36, Max = 1200, Automatic = "Auto (300)",
            Help = "Dots per inch each page is drawn at: 96–150 for screens, 300 for print, 600 for fine detail. An A4 page at 300 dpi is 2480 × 3508 pixels."
        });
        list.Add(new(K.Pages, "Pages", SettingKind.Text)
        {
            Group = group, Automatic = "All pages",
            Help = "Which pages to turn into images, for example 1-3, 7, 10- (page 10 to the end)."
        });
        Picture(list, target, resize: false, metadata: false);
        Resize(list, upscale: false);
    }

    // ---------- animations ----------

    private static void GifFromVideo(List<SettingDefinition> list)
    {
        const string group = "Animation";
        list.Add(new(K.Fps, "Frames per second", SettingKind.Number)
        {
            Group = group, Min = 1, Max = 50, AllowDecimals = true, Automatic = "Auto (up to 30)",
            Help = "Fewer frames per second make a much smaller GIF. 10–15 is typical; GIF cannot play faster than 50."
        });
        list.Add(new(K.MaxWidth, "Maximum width (px)", SettingKind.Number)
        {
            Group = group, Min = 16, Max = 4096, Automatic = "Auto (up to 960)",
            Help = "GIF stores frames almost uncompressed, so size grows quickly with width. The video is never enlarged."
        });
        GifPalette(list, group);
        list.Add(Choice(K.Dither, "Shading", group, "Auto (ordered pattern)",
            "How the 256-colour limit is hidden. An ordered pattern keeps files small; diffusion looks smoother on gradients but makes files about 40% larger.",
            ("bayer", "Ordered pattern"), ("sierra", "Diffusion (smoother)"), ("none", "None (flat colours)")));
        list.Add(Loop(group));
    }

    private static void GifFromPictures(List<SettingDefinition> list, bool merging)
    {
        const string group = "Animation";
        if (merging)
        {
            list.Add(new(K.FrameSeconds, "Seconds per picture", SettingKind.Number)
            {
                Group = group, Min = 0.02, Max = 60, AllowDecimals = true, Automatic = "Auto (1)",
                Help = "How long each picture of the slideshow stays on screen."
            });
            list.Add(Loop(group));
        }
        GifPalette(list, group);
        Resize(list, upscale: false);
    }

    private static void GifPalette(List<SettingDefinition> list, string group) =>
        list.Add(new(K.Colors, "Colours", SettingKind.Number)
        {
            Group = group, Min = 2, Max = 256, Automatic = "Auto (256)",
            Help = "A GIF holds at most 256 colours. Fewer colours make a smaller file with more visible banding."
        });

    private static SettingDefinition Loop(string group) =>
        Choice(K.Loop, "Repeat", group, "Auto (forever)", "Whether the animation starts again when it reaches the end.",
            ("forever", "Forever"), ("once", "Play once"));

    // ---------- video ----------

    private static void Video(List<SettingDefinition> list, string target, bool hasAudio)
    {
        const string picture = "Video";
        var codecs = new List<(string, string)>();
        if (target is "mp4" or "mkv" or "mov") codecs.Add(("h264", "H.264 (plays everywhere)"));
        if (target is "mp4" or "mkv" or "mov") codecs.Add(("hevc", "H.265 / HEVC (about half the size)"));
        if (target is "mkv" or "webm") codecs.Add(("vp9", "VP9"));
        if (target is "mp4" or "mkv" or "webm" && VideoEncoding.Av1Encoder != null) codecs.Add(("av1", "AV1 (smallest, slow to encode)"));
        if (codecs.Count > 1)
        {
            list.Add(Choice(K.VideoCodec, "Video format", picture,
                target == "webm" ? "Auto (VP9)" : "Auto (keep, or H.264)",
                "The compression format of the picture. Automatic copies the video untouched when the container can hold it and otherwise uses the most compatible format.",
                codecs.ToArray()) with { Reencodes = true });
        }

        // avi is written with MPEG-4, which has no such scale — a bitrate or target size is the way there
        if (target != "avi")
        {
            list.Add(new(K.Crf, "Quality (CRF)", SettingKind.Number)
            {
                Group = picture, Min = 0, Max = 63, Reencodes = true, Automatic = "Auto (visually lossless)",
                Help = "Constant-quality level: lower is better and larger. For H.264 about 18 is visually lossless, 23 is good, 28 is small; H.265 looks the same about 4–6 higher. Ignored when a bitrate or target size is set."
            });
        }
        list.Add(new(K.VideoBitrate, "Video bitrate (kbps)", SettingKind.Number)
        {
            Group = picture, Min = 50, Max = 400_000, Reencodes = true, Automatic = "Not fixed",
            Help = "Average bitrate of the picture. 1080p video looks good at 4000–8000 kbps with H.264. Ignored when a target size is set."
        });
        list.Add(new(K.TargetSizeMb, "Target size (MB)", SettingKind.Number)
        {
            Group = picture, Min = 0.1, Max = 1_000_000, AllowDecimals = true, Reencodes = true, Automatic = "No size target",
            Help = "The bitrate is worked out so the whole file — video and audio — fits into this many megabytes. Overrides Quality, Bitrate and the File size slider."
        });
        list.Add(new(K.MaxWidth, "Maximum width (px)", SettingKind.Number)
        {
            Group = picture, Min = 64, Max = 8192, Reencodes = true, Automatic = "Keep original",
            Help = "Video wider than this is scaled down, keeping its proportions (1920 for 1080p, 1280 for 720p). It is never enlarged."
        });
        list.Add(new(K.MaxHeight, "Maximum height (px)", SettingKind.Number)
        {
            Group = picture, Min = 64, Max = 8192, Reencodes = true, Automatic = "Keep original",
            Help = "Video taller than this is scaled down, keeping its proportions (1080, 720, 480 …). It is never enlarged."
        });
        list.Add(new(K.Fps, "Maximum frames per second", SettingKind.Number)
        {
            Group = picture, Min = 1, Max = 240, AllowDecimals = true, Reencodes = true, Automatic = "Keep original",
            Help = "Video with a higher frame rate is reduced to this (60 → 30 halves the work and the size). Lower rates are left alone."
        });
        list.Add(Choice(K.Speed, "Encoding speed", picture, "Auto (the Speed setting)",
            "Overrides the Speed setting for these files only.",
            ("fastest", "Fastest"), ("fast", "Fast"), ("balanced", "Balanced"), ("best", "Best quality")));

        if (hasAudio)
        {
            const string sound = "Audio";
            var audio = target switch
            {
                "mp4" => new[] { ("aac", "AAC"), ("mp3", "MP3"), ("none", "Remove the audio") },
                "mov" => [("aac", "AAC"), ("none", "Remove the audio")],
                "mkv" => [("aac", "AAC"), ("opus", "Opus"), ("mp3", "MP3"), ("flac", "FLAC (lossless)"), ("none", "Remove the audio")],
                "webm" => [("opus", "Opus"), ("none", "Remove the audio")],
                _ => [("mp3", "MP3"), ("none", "Remove the audio")]
            };
            list.Add(Choice(K.AudioCodec, "Audio format", sound, "Auto (keep if possible)",
                "Automatic copies each audio track untouched when the container can hold it. Choosing a format re-encodes the sound; the picture is not affected.",
                audio));
            list.Add(new(K.AudioBitrate, "Audio bitrate (kbps)", SettingKind.Number)
            {
                Group = sound, Min = 16, Max = 640, Automatic = "Auto (transparent)",
                Help = "Bitrate of each re-encoded audio track. 128 is fine for speech, 192–256 for music."
            });
            list.Add(Choice(K.Channels, "Channels", sound, "Auto (keep)", "Mix the sound down to stereo or mono.",
                ("stereo", "Stereo"), ("mono", "Mono")));
        }

        const string other = "Other";
        if (target != "avi")
        {
            list.Add(Choice(K.Subtitles, "Subtitles", other, "Auto (keep)", "Subtitle tracks stored inside the file.",
                ("keep", "Keep"), ("remove", "Remove")));
        }
        list.Add(Choice(K.Metadata, "Tags and chapters", other, "Auto (keep)",
            "Title, creation date, chapter marks and similar information stored in the file.",
            ("keep", "Keep"), ("strip", "Remove")));
    }

    // ---------- audio ----------

    private static void Audio(List<SettingDefinition> list, string target)
    {
        const string group = "Audio";
        bool lossy = target is "mp3" or "aac" or "m4a" or "ogg" or "opus";
        if (lossy)
        {
            list.Add(new(K.AudioBitrate, "Bitrate (kbps)", SettingKind.Number)
            {
                Group = group, Min = 8, Max = 640, Reencodes = true,
                Automatic = target switch { "aac" or "m4a" => "Auto (256)", "opus" => "Auto (160)", _ => "Auto (best VBR)" },
                Help = "A fixed average bitrate: 320 is the most MP3 allows, 192 is transparent for most music, 64–96 is enough for speech."
            });
        }
        if (target == "mp3")
        {
            list.Add(new(K.VbrQuality, "Variable-bitrate quality", SettingKind.Number)
            {
                Group = group, Min = 0, Max = 9, Reencodes = true, Automatic = "Auto (0, the best)",
                Help = "LAME's V scale: 0 is the best (about 245 kbps), 2 is about 190 kbps, 4 about 165, 9 the smallest. Ignored when a bitrate is set."
            });
        }
        if (target == "ogg")
        {
            list.Add(new(K.VbrQuality, "Variable-bitrate quality", SettingKind.Number)
            {
                Group = group, Min = -1, Max = 10, AllowDecimals = true, Reencodes = true, Automatic = "Auto (8)",
                Help = "Vorbis quality: 10 is the best, 5 is about 160 kbps, 3 about 112, -1 the smallest. Ignored when a bitrate is set."
            });
        }
        if (target == "flac")
        {
            list.Add(new(K.Compression, "Compression effort", SettingKind.Number)
            {
                Group = group, Min = 0, Max = 12, Reencodes = true, Automatic = "Auto (from Speed)",
                Help = "0 (fastest) to 12 (smallest). FLAC is always lossless: this never changes the sound."
            });
        }
        if (target is "wav" or "flac")
        {
            var depths = target == "wav"
                ? new[] { ("16", "16-bit"), ("24", "24-bit"), ("32", "32-bit float") }
                : [("16", "16-bit"), ("24", "24-bit")];
            list.Add(Choice(K.BitDepth, "Bit depth", group, "Auto (as the source)",
                "16-bit is CD quality and plays everywhere; 24-bit keeps studio recordings exact.", depths) with { Reencodes = true });
        }
        // Opus has no sample rate to choose: it always works at 48 kHz
        if (target != "opus")
        {
            list.Add(Choice(K.SampleRate, "Sample rate", group, "Auto (as the source)",
                "44100 Hz is CD quality, 48000 Hz is the standard for video. Lower rates make speech recordings smaller.",
                ("8000", "8 000 Hz"), ("16000", "16 000 Hz"), ("22050", "22 050 Hz"), ("32000", "32 000 Hz"),
                ("44100", "44 100 Hz"), ("48000", "48 000 Hz"), ("96000", "96 000 Hz")) with { Reencodes = true });
        }
        list.Add(Choice(K.Channels, "Channels", group, "Auto (keep)", "Mix the sound down to stereo or mono.",
            ("stereo", "Stereo"), ("mono", "Mono")) with { Reencodes = true });
        if (lossy)
        {
            list.Add(new(K.TargetSizeMb, "Target size (MB)", SettingKind.Number)
            {
                Group = group, Min = 0.05, Max = 100_000, AllowDecimals = true, Reencodes = true, Automatic = "No size target",
                Help = "The bitrate is worked out so the file fits into this many megabytes. Overrides Bitrate and the File size slider."
            });
        }
        list.Add(Choice(K.Metadata, "Tags and cover art", "Other", "Auto (keep)",
            "Title, artist, album and the cover picture stored in the file.", ("keep", "Keep"), ("strip", "Remove")));
    }

    // ---------- documents ----------

    private static readonly (string, string)[] PageSizes =
        [("a4", "A4"), ("letter", "US Letter"), ("legal", "US Legal"), ("a3", "A3"), ("a5", "A5")];

    private static void PdfFromPictures(List<SettingDefinition> list)
    {
        const string group = "Pages";
        list.Add(Choice(K.PageSize, "Page size", group, "Auto (fit the picture)",
            "With a paper size every picture is placed on a page of that size, turned to landscape for wide pictures.",
            PageSizes));
        list.Add(new(K.Margin, "Margin (mm)", SettingKind.Number)
        {
            Group = group, Min = 0, Max = 100, AllowDecimals = true, Automatic = "Auto (none)",
            Help = "White space left around each picture."
        });
    }

    private static void PdfFromText(List<SettingDefinition> list)
    {
        const string group = "Pages";
        list.Add(Choice(K.PageSize, "Page size", group, "Auto (A4)", "The paper size of the PDF.", PageSizes));
        list.Add(new(K.Margin, "Margin (mm)", SettingKind.Number)
        {
            Group = group, Min = 0, Max = 100, AllowDecimals = true, Automatic = "Auto (17)",
            Help = "White space around the text on every page."
        });
        list.Add(new(K.FontSize, "Text size (pt)", SettingKind.Number)
        {
            Group = group, Min = 5, Max = 72, AllowDecimals = true, Automatic = "Auto (10.5)",
            Help = "Size of the text in points."
        });
    }

    private static void DocumentEngine(List<SettingDefinition> list)
    {
        var engines = new List<(string, string)> { ("libreoffice", "LibreOffice") };
        if (Platform.PlatformServices.Current.OfficeAutomation is { } office) engines.Add(("office", office.Name));
        engines.Add(("builtin", "Built-in"));
        list.Add(Choice(K.Engine, "Convert with", "Document", "Auto (best installed)",
            "An office program keeps a document's page layout, fonts and all. The built-in converter keeps its headings, " +
            "formatting, lists, tables and pictures, and needs nothing installed.",
            engines.ToArray()));
    }

    // ---------- archives ----------

    private static void Archive(List<SettingDefinition> list, string target)
    {
        const string group = "Archive";
        if (target != "tar")
        {
            list.Add(new(K.Level, "Compression level", SettingKind.Number)
            {
                Group = group, Min = 0, Max = 9, Automatic = "Auto (from Speed)",
                Help = "0 stores the files without compressing them (fastest); 9 compresses hardest (slowest, smallest)."
            });
            list.Add(new(K.Password, "Password", SettingKind.Secret)
            {
                Group = group, Automatic = "None",
                Help = "Protects the archive with AES-256 encryption. The password is used for this session only and never saved."
            });
        }
        if (target == "7z")
        {
            list.Add(Choice(K.Solid, "Solid archive", group, "Auto (on)",
                "A solid archive compresses all files as one block: much smaller for many similar files, but a single file can't be taken out as quickly.",
                ("on", "On"), ("off", "Off")));
        }
    }

    private static SettingDefinition Choice(string key, string label, string group, string automatic, string help,
        params (string Value, string Label)[] choices) =>
        new(key, label, SettingKind.Choice)
        {
            Group = group,
            Automatic = automatic,
            Help = help,
            Choices = choices.Select(c => new SettingChoice(c.Value, c.Label)).ToList()
        };

    // ---------- presets ----------

    /// <summary>Ready-made bundles of settings, per kind of result.</summary>
    public static IReadOnlyList<SettingsPreset> BuiltInPresets { get; } =
    [
        Preset("image", "Web · 1920 px", (K.MaxWidth, "1920"), (K.MaxHeight, "1920"), (K.Quality, "85")),
        Preset("image", "Email · 1280 px, small", (K.MaxWidth, "1280"), (K.MaxHeight, "1280"), (K.Quality, "75"), (K.Metadata, "strip")),
        Preset("image", "Thumbnail · 512 px", (K.MaxWidth, "512"), (K.MaxHeight, "512"), (K.Quality, "80"), (K.Metadata, "strip")),
        Preset("image", "Remove location data", (K.Metadata, "nogps")),
        Preset("image", "Pages for screen · 150 dpi", (K.Dpi, "150")),
        Preset("image", "Pages in fine detail · 600 dpi", (K.Dpi, "600")),

        Preset("video", "Fit in 10 MB", (K.TargetSizeMb, "10")),
        Preset("video", "Fit in 25 MB", (K.TargetSizeMb, "25")),
        Preset("video", "1080p · small (H.265)", (K.VideoCodec, "hevc"), (K.MaxWidth, "1920"), (K.MaxHeight, "1080"), (K.Crf, "24")),
        Preset("video", "720p · small", (K.MaxWidth, "1280"), (K.MaxHeight, "720"), (K.Crf, "24")),
        Preset("video", "Most compatible (H.264 + AAC)", (K.VideoCodec, "h264"), (K.AudioCodec, "aac")),

        Preset("audio", "Music · 320 kbps", (K.AudioBitrate, "320")),
        Preset("audio", "Podcast · mono 64 kbps", (K.AudioBitrate, "64"), (K.Channels, "mono")),
        Preset("audio", "Voice note · small", (K.AudioBitrate, "32"), (K.Channels, "mono"), (K.SampleRate, "22050")),

        Preset("gif", "Small · 480 px, 12 fps", (K.MaxWidth, "480"), (K.Fps, "12"), (K.Colors, "128")),
        Preset("gif", "Smooth · 30 fps", (K.Fps, "30")),

        Preset("pdf", "A4 pages with a margin", (K.PageSize, "a4"), (K.Margin, "10")),

        Preset("archive", "Smallest (level 9)", (K.Level, "9")),
        Preset("archive", "Store only (no compression)", (K.Level, "0"))
    ];

    private static SettingsPreset Preset(string kind, string name, params (string Key, string Value)[] values) => new()
    {
        Kind = kind,
        Name = name,
        Values = values.ToDictionary(v => v.Key, v => v.Value)
    };
}
