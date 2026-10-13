using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FileConverter.Core.Converters;
using SkiaSharp;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Pdf;

/// <summary>
/// PDF → SVG, a page a file, without turning the page into pixels. PDFium — the engine that
/// draws the pages for PDF → images — is asked for what a page is made of, in the order it is
/// painted, and each thing is written as what it is:
/// • a shape as a path, with its fill, its outline, its dashes and what it is clipped to;
/// • a letter as its outline, taken from the font inside the PDF — so the text looks exactly as
///   it does in the PDF on any computer, whether or not the font is installed there. (It is a
///   drawing of the text: it cannot be selected or searched.) Every letter shape is written
///   once and placed wherever it occurs;
/// • a picture as a picture, in its own resolution: a photo as the JPEG it is in the PDF, the
///   rest stored without loss.
/// What cannot be said in those terms — a colour gradient, an object blended with what lies
/// under it, a letter whose outline the font does not give — is drawn by PDFium as a small
/// picture with a transparent background and placed in the same spot and the same layer, so the
/// page looks right either way. Filled-in form fields and other annotations are added on top the
/// same way.
///
/// One thing PDFium does not say: whether a shape is filled with a colour or with a pattern (a
/// gradient, a hatching) — it reports a pattern as white. So a page is first asked, through the
/// other PDF reader in the app, whether it uses patterns at all; where it does, every shape and
/// letter reported in one of the colours a pattern is reported as is drawn by itself, small, and
/// looked at: if it is not that one colour throughout, it is a pattern, and goes in as a picture.
/// </summary>
internal static unsafe class PdfVectorExport
{
    /// <summary>The resolution of the pieces that have to be pictures, in dots per inch.</summary>
    private const double PatchDpi = 300;

    /// <summary>The most pixels one such piece gets.</summary>
    private const double MaxPatchPixels = 24_000_000;

    /// <summary>x' = A·x + C·y + E, y' = B·x + D·y + F — as both PDF and SVG write a transformation.</summary>
    private readonly record struct Transform(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Transform Identity = new(1, 0, 0, 1, 0, 0);

        /// <summary>This transformation, then <paramref name="next"/>.</summary>
        public Transform Then(Transform next) => new(
            A * next.A + B * next.C, A * next.B + B * next.D,
            C * next.A + D * next.C, C * next.B + D * next.D,
            E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

        public bool IsIdentity => this == Identity;

        public override string ToString() => $"matrix({N(A)} {N(B)} {N(C)} {N(D)} {N(E)} {N(F)})";
    }

    /// <summary>A number as SVG takes it: with a point, and no more digits than can be seen.</summary>
    private static string N(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
        var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>
    /// Writes the chosen pages as SVG files: one page as the file asked for, several into a
    /// "name pages" folder. Returns the file, or the folder.
    /// </summary>
    public static Task<string> ExportAsync(string input, string outputFilePath, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        using var pdf = new PdfRenderer(input);
        if (pdf.PageCount == 0) throw new FriendlyException("The PDF contains no pages.");
        var pages = PdfPageExport.ParsePages(options.Detail.Text(K.Pages), pdf.PageCount);

        // the second reader, asked only which pages use patterns; if it cannot open the file, every page is checked
        UglyToad.PdfPig.PdfDocument? reader = null;
        try { reader = UglyToad.PdfPig.PdfDocument.Open(input); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        var scratch = pdf.OpenAgain();
        string? folder = null;
        try
        {
            if (pages.Count > 1) folder = FrameExtraction.CreateOutputFolder(outputFilePath, "pages");
            var baseName = Path.GetFileNameWithoutExtension(ConversionService.FinalName(outputFilePath));
            int digits = Math.Max(2, pdf.PageCount.ToString().Length);
            for (int n = 0; n < pages.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var target = folder == null
                    ? outputFilePath
                    : Path.Combine(folder, $"{baseName} {(pages[n] + 1).ToString($"D{digits}")}.svg");
                WritePage(pdf, scratch, pages[n], target, MayUsePatterns(reader, pages[n]));
                progress?.Report((n + 1.0) / pages.Count);
            }
            return folder ?? outputFilePath;
        }
        catch
        {
            if (folder != null) FrameExtraction.DeleteFolderQuiet(folder);
            throw;
        }
        finally
        {
            PdfRenderer.Close(scratch);
            reader?.Dispose();
        }
    }, ct);

    /// <summary>Does anything on the page — in the forms it places, too — have a pattern for its colour?</summary>
    private static bool MayUsePatterns(UglyToad.PdfPig.PdfDocument? reader, int index)
    {
        if (reader == null) return true;
        try
        {
            var page = reader.GetPage(index + 1);
            return page.Paths.Any(shape => shape.FillColor is UglyToad.PdfPig.Graphics.Colors.PatternColor ||
                                           shape.StrokeColor is UglyToad.PdfPig.Graphics.Colors.PatternColor) ||
                   page.Letters.Any(letter => letter.FillColor is UglyToad.PdfPig.Graphics.Colors.PatternColor ||
                                              letter.StrokeColor is UglyToad.PdfPig.Graphics.Colors.PatternColor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // a page it cannot read is checked the careful way
        }
    }

    /// <summary>Writes one page of the open PDF as an SVG file.</summary>
    /// <param name="scratch">The same PDF opened a second time (<see cref="PdfRenderer.OpenAgain"/>): its pages are taken apart to draw single objects.</param>
    /// <param name="mayUsePatterns">False when it is known that nothing on the page is filled or outlined with a pattern.</param>
    public static void WritePage(PdfRenderer pdf, IntPtr scratch, int index, string output, bool mayUsePatterns = true)
    {
        string svg;
        lock (PdfRenderer.Engine)
        {
            var page = Native.FPDF_LoadPage(pdf.Handle, index);
            if (page == IntPtr.Zero) throw new FriendlyException($"Page {index + 1} of the PDF could not be read.");
            try
            {
                svg = new PageWriter(pdf.Handle, scratch, page, index, mayUsePatterns).Write();
            }
            finally
            {
                Native.FPDF_ClosePage(page);
            }
        }
        File.WriteAllText(output, svg, new UTF8Encoding(false));
    }

    private sealed class PageWriter
    {
        private readonly IntPtr _document, _scratch, _page;
        private readonly int _index;
        private readonly double _width, _height;
        private readonly Transform _toSvg;

        private readonly StringBuilder _defs = new();
        private readonly StringBuilder _body = new();
        private readonly Dictionary<(IntPtr Font, uint Character), string?> _glyphs = [];
        private readonly Dictionary<string, string> _clips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pictures = new(StringComparer.Ordinal);
        private readonly Dictionary<IntPtr, List<int>> _characters = [];
        private readonly bool _patterns;
        private IntPtr _text;
        private int _ids, _looks;

        public PageWriter(IntPtr document, IntPtr scratch, IntPtr page, int index, bool mayUsePatterns)
        {
            _patterns = mayUsePatterns;
            _document = document;
            _scratch = scratch;
            _page = page;
            _index = index;
            _width = Native.FPDF_GetPageWidthF(page);
            _height = Native.FPDF_GetPageHeightF(page);
            _toSvg = PageToSvg(page, _width, _height);
        }

        /// <summary>
        /// From the page's own coordinates (origin bottom left, y upwards, the page as stored) to
        /// the picture's (origin top left, y downwards, the page as shown — turned if it says so).
        /// </summary>
        private static Transform PageToSvg(IntPtr page, double width, double height)
        {
            float left = 0, bottom = 0, right = 0, top = 0;
            bool boxed = Native.FPDFPage_GetCropBox(page, &left, &bottom, &right, &top) != 0 ||
                         Native.FPDFPage_GetMediaBox(page, &left, &bottom, &right, &top) != 0;
            int rotation = Native.FPDFPage_GetRotation(page);
            if (!boxed)
            {
                // no box to be read: the size PDFium reports, the way it reports it
                (left, bottom) = (0, 0);
                (right, top) = rotation is 1 or 3 ? ((float)height, (float)width) : ((float)width, (float)height);
            }
            return rotation switch
            {
                1 => new Transform(0, 1, 1, 0, -bottom, -left),      // a quarter turn clockwise
                2 => new Transform(-1, 0, 0, 1, right, -bottom),     // upside down
                3 => new Transform(0, -1, -1, 0, top, right),        // a quarter turn anticlockwise
                _ => new Transform(1, 0, 0, -1, -left, top)
            };
        }

        public string Write()
        {
            _text = Native.FPDFText_LoadPage(_page);
            try
            {
                MapCharacters();

                // Objects in the order they are painted. What cannot be written as a drawing is drawn
                // as a picture — neighbours of that kind together in one, to keep them few.
                int count = Native.FPDFPage_CountObjects(_page);
                int pending = -1;
                for (int i = 0; i < count; i++)
                {
                    var item = Native.FPDFPage_GetObject(_page, i);
                    if (item == IntPtr.Zero) continue;
                    if (CanDraw(item, depth: 0, top: i))
                    {
                        if (pending >= 0) AddPatch(pending, i - 1);
                        pending = -1;
                        Draw(item, Transform.Identity, depth: 0);
                    }
                    else if (pending < 0)
                    {
                        pending = i;
                    }
                }
                if (pending >= 0) AddPatch(pending, count - 1);

                // form fields as filled in, stamps, highlights: not part of the page's content, but of what it shows
                if (Native.FPDFPage_GetAnnotCount(_page) > 0) AddPatch(-1, -1);
            }
            finally
            {
                if (_text != IntPtr.Zero) Native.FPDFText_ClosePage(_text);
            }

            var svg = new StringBuilder();
            svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" ")
                .Append($"width=\"{N(_width)}pt\" height=\"{N(_height)}pt\" viewBox=\"0 0 {N(_width)} {N(_height)}\">\n");
            if (_defs.Length > 0) svg.Append("<defs>\n").Append(_defs).Append("</defs>\n");
            svg.Append(_body);
            svg.Append("</svg>\n");
            return svg.ToString();
        }

        private string NewId(string prefix) => prefix + (++_ids).ToString(CultureInfo.InvariantCulture);

        // ---------- what can be written as a drawing ----------

        /// <param name="top">The number of the top-level object this one is, or is part of.</param>
        private bool CanDraw(IntPtr item, int depth, int top)
        {
            int type = Native.FPDFPageObj_GetType(item);
            if (type is not (Native.Text or Native.Path or Native.Image or Native.Form)) return false; // a gradient, or something unknown

            // A pattern for a colour: looked for where it could be. An object that stands by itself is
            // drawn and looked at; one inside a form cannot be drawn alone, and is taken at its word
            // only when its colour is none a pattern is reported as.
            if (_patterns && type is Native.Path or Native.Text && ReportedLikeAPattern(item, out var said))
            {
                if (depth > 0 || IsPattern(top, said)) return false;
            }

            // Transparency that is a plain "so many percent see-through" can be said in SVG; a blend
            // mode or a mask cannot, and PDFium does not tell which of them it is.
            if (Native.FPDFPageObj_HasTransparency(item) != 0)
            {
                uint r, g, b, fill = 255, stroke = 255;
                Native.FPDFPageObj_GetFillColor(item, &r, &g, &b, &fill);
                Native.FPDFPageObj_GetStrokeColor(item, &r, &g, &b, &stroke);
                if (type == Native.Form || (fill == 255 && stroke == 255)) return false;
            }

            switch (type)
            {
                case Native.Form:
                {
                    if (depth > 24) return false;
                    int count = Native.FPDFFormObj_CountObjects(item);
                    for (int i = 0; i < count; i++)
                    {
                        var child = Native.FPDFFormObj_GetObject(item, (uint)i);
                        if (child != IntPtr.Zero && !CanDraw(child, depth + 1, top)) return false;
                    }
                    return true;
                }
                case Native.Text:
                    return CanDrawText(item);
                default:
                    return true;
            }
        }

        /// <summary>
        /// Is the object's fill (or, for a shape that is only outlined, its outline) one of the two
        /// colours PDFium reports for a pattern: white for a pattern of tiles, a light grey for a gradient?
        /// </summary>
        private static bool ReportedLikeAPattern(IntPtr item, out (uint R, uint G, uint B) said)
        {
            uint r = 0, g = 0, b = 0, a = 255;
            int fillMode = 1, stroked = 0;
            if (Native.FPDFPageObj_GetType(item) == Native.Path) Native.FPDFPath_GetDrawMode(item, &fillMode, &stroked);
            if (fillMode != 0) Native.FPDFPageObj_GetFillColor(item, &r, &g, &b, &a);
            else Native.FPDFPageObj_GetStrokeColor(item, &r, &g, &b, &a);
            said = (r, g, b);
            return said is (255, 255, 255) or (191, 191, 191);
        }

        /// <summary>
        /// Draws one top-level object by itself, small, and says whether it is anything but the
        /// one colour it was reported in. (A page of very many white shapes is not checked for
        /// ever: after a good number of looks the rest are taken at their word.)
        /// </summary>
        private bool IsPattern(int top, (uint R, uint G, uint B) said)
        {
            if (++_looks > 120) return false;
            var look = Render(top, top, annotations: false, box =>
            {
                double longest = Math.Max(box.Right - box.Left, box.Bottom - box.Top);
                return Math.Clamp(48 / Math.Max(longest, 1), 0.02, 4);
            });
            if (look == null) return false;

            var pixels = look.Value.Pixels;
            for (long i = 0; i < pixels.LongLength; i += 4)
            {
                if (pixels[i + 3] < 250) continue; // an edge, or nothing
                if (Math.Abs(pixels[i] - (int)said.B) > 12 || Math.Abs(pixels[i + 1] - (int)said.G) > 12 || Math.Abs(pixels[i + 2] - (int)said.R) > 12)
                    return true;
            }
            return false;
        }

        private bool CanDrawText(IntPtr item)
        {
            int mode = Native.FPDFTextObj_GetTextRenderMode(item);
            if (mode is 3 or 7) return true;      // invisible: nothing to draw
            if (mode is not (0 or 4)) return false; // outlined letters: their line widths are the font's business
            if (!_characters.TryGetValue(item, out var characters)) return true; // no letters in it

            var font = Native.FPDFTextObj_GetFont(item);
            if (font == IntPtr.Zero) return false;
            double lastX = double.NaN, lastY = double.NaN;
            foreach (int i in characters)
            {
                uint character = Native.FPDFText_GetUnicode(_text, i);
                if (IsBlank(character)) continue;
                if (Glyph(font, character) == null) return false;

                // two letters on the very same spot are one ligature that was spelt out: its own shape is not to be had
                double x, y;
                if (Native.FPDFText_GetCharOrigin(_text, i, &x, &y) == 0) return false;
                if (Math.Abs(x - lastX) < 0.001 && Math.Abs(y - lastY) < 0.001) return false;
                (lastX, lastY) = (x, y);
            }
            return true;
        }

        private static bool IsBlank(uint character) =>
            character <= 0x20 || character is 0xA0 or 0xAD or 0x2028 or 0x2029 or 0xFEFF or 0xFFFE or 0xFFFF ||
            (character >= 0x2000 && character <= 0x200F);

        /// <summary>Which letters of the page belong to which text object.</summary>
        private void MapCharacters()
        {
            if (_text == IntPtr.Zero) return;
            int count = Native.FPDFText_CountChars(_text);
            for (int i = 0; i < count; i++)
            {
                var owner = Native.FPDFText_GetTextObject(_text, i);
                if (owner == IntPtr.Zero) continue; // a space or line end PDFium put in itself
                if (!_characters.TryGetValue(owner, out var list)) _characters[owner] = list = [];
                list.Add(i);
            }
        }

        // ---------- drawing ----------

        /// <param name="parent">From the coordinates this object is described in (those of the form it is part of) to the page's.</param>
        private void Draw(IntPtr item, Transform parent, int depth)
        {
            var clip = Clip(item, parent);
            if (clip != null) _body.Append($"<g clip-path=\"url(#{clip})\">\n");

            var matrix = default(Native.FS_MATRIX);
            Native.FPDFPageObj_GetMatrix(item, &matrix);
            var own = new Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F).Then(parent);

            switch (Native.FPDFPageObj_GetType(item))
            {
                case Native.Path:
                    DrawPath(item, own);
                    break;
                case Native.Image:
                    DrawPicture(item, own);
                    break;
                case Native.Text:
                    DrawText(item);
                    break;
                case Native.Form:
                {
                    int count = Native.FPDFFormObj_CountObjects(item);
                    for (int i = 0; i < count; i++)
                    {
                        var child = Native.FPDFFormObj_GetObject(item, (uint)i);
                        if (child != IntPtr.Zero) Draw(child, own, depth + 1);
                    }
                    break;
                }
            }

            if (clip != null) _body.Append("</g>\n");
        }

        private string Root(Transform transform) => transform.Then(_toSvg).ToString();

        private void DrawPath(IntPtr item, Transform own)
        {
            int fillMode = 0, stroked = 0;
            if (Native.FPDFPath_GetDrawMode(item, &fillMode, &stroked) == 0) return;
            if (fillMode == 0 && stroked == 0) return;

            int count = Native.FPDFPath_CountSegments(item);
            var d = PathData(count, i => Native.FPDFPath_GetPathSegment(item, i));
            if (d.Length == 0) return;

            _body.Append($"<path transform=\"{Root(own)}\" d=\"{d}\"");
            if (fillMode != 0)
            {
                uint r = 0, g = 0, b = 0, a = 255;
                Native.FPDFPageObj_GetFillColor(item, &r, &g, &b, &a);
                _body.Append($" fill=\"{Colour(r, g, b)}\"");
                if (a < 255) _body.Append($" fill-opacity=\"{N(a / 255.0)}\"");
                if (fillMode == 1) _body.Append(" fill-rule=\"evenodd\"");
            }
            else
            {
                _body.Append(" fill=\"none\"");
            }

            if (stroked != 0)
            {
                uint r = 0, g = 0, b = 0, a = 255;
                Native.FPDFPageObj_GetStrokeColor(item, &r, &g, &b, &a);
                float width = 1;
                Native.FPDFPageObj_GetStrokeWidth(item, &width);
                _body.Append($" stroke=\"{Colour(r, g, b)}\"");
                if (a < 255) _body.Append($" stroke-opacity=\"{N(a / 255.0)}\"");
                if (width > 0) _body.Append($" stroke-width=\"{N(width)}\"");
                else _body.Append(" stroke-width=\"1\" vector-effect=\"non-scaling-stroke\""); // "as thin as the device draws"

                string? cap = Native.FPDFPageObj_GetLineCap(item) switch { 1 => "round", 2 => "square", _ => null };
                string? join = Native.FPDFPageObj_GetLineJoin(item) switch { 1 => "round", 2 => "bevel", _ => null };
                if (cap != null) _body.Append($" stroke-linecap=\"{cap}\"");
                if (join != null) _body.Append($" stroke-linejoin=\"{join}\"");
                else _body.Append(" stroke-miterlimit=\"10\""); // what a PDF starts with; SVG starts with 4

                int dashes = Native.FPDFPageObj_GetDashCount(item);
                if (dashes is > 0 and <= 64)
                {
                    var pattern = stackalloc float[dashes];
                    float phase = 0;
                    if (Native.FPDFPageObj_GetDashArray(item, pattern, (nuint)dashes) != 0)
                    {
                        _body.Append(" stroke-dasharray=\"");
                        for (int i = 0; i < dashes; i++) _body.Append(i > 0 ? " " : "").Append(N(pattern[i]));
                        _body.Append('"');
                        if (Native.FPDFPageObj_GetDashPhase(item, &phase) != 0 && phase != 0) _body.Append($" stroke-dashoffset=\"{N(phase)}\"");
                    }
                }
            }
            _body.Append("/>\n");
        }

        private static string Colour(uint r, uint g, uint b) => $"#{r & 255:x2}{g & 255:x2}{b & 255:x2}";

        /// <summary>A path's segments in SVG's letters; a curve is three segments in a row.</summary>
        private static string PathData(int count, Func<int, IntPtr> segment, Transform? transform = null)
        {
            var d = new StringBuilder(count * 12);
            int curve = 0;
            for (int i = 0; i < count; i++)
            {
                var part = segment(i);
                if (part == IntPtr.Zero) continue;
                float x = 0, y = 0;
                if (Native.FPDFPathSegment_GetPoint(part, &x, &y) == 0) continue;
                double px = x, py = y;
                if (transform is { } t) (px, py) = t.Apply(x, y);

                switch (Native.FPDFPathSegment_GetType(part))
                {
                    case Native.MoveTo:
                        d.Append('M').Append(N(px)).Append(' ').Append(N(py));
                        curve = 0;
                        break;
                    case Native.LineTo:
                        d.Append('L').Append(N(px)).Append(' ').Append(N(py));
                        curve = 0;
                        break;
                    case Native.BezierTo:
                        d.Append(curve == 0 ? "C" : " ").Append(N(px)).Append(' ').Append(N(py));
                        curve = (curve + 1) % 3;
                        break;
                }
                if (Native.FPDFPathSegment_GetClose(part) != 0) d.Append('Z');
            }
            return d.ToString();
        }

        /// <summary>
        /// The id of the clip an object is drawn through, or null when it has none. Its outlines
        /// are in the coordinates of whatever the object is part of; they are written in the
        /// page's. Several outlines mean "inside all of them", which SVG says by clipping one clip
        /// by the next. The same clip is written once, however many objects share it.
        /// </summary>
        private string? Clip(IntPtr item, Transform parent)
        {
            var clip = Native.FPDFPageObj_GetClipPath(item);
            if (clip == IntPtr.Zero) return null;
            int count = Native.FPDFClipPath_CountPaths(clip);
            if (count <= 0) return null;

            string? id = null;
            var toRoot = parent.Then(_toSvg);
            for (int p = 0; p < count; p++)
            {
                int path = p;
                int segments = Native.FPDFClipPath_CountPathSegments(clip, path);
                if (segments <= 0) continue;
                var d = PathData(segments, i => Native.FPDFClipPath_GetPathSegment(clip, path, i), toRoot);
                if (d.Length == 0) continue;

                string key = id + "|" + d;
                if (!_clips.TryGetValue(key, out var known))
                {
                    known = NewId("c");
                    _clips[key] = known;
                    _defs.Append($"<clipPath id=\"{known}\"");
                    if (id != null) _defs.Append($" clip-path=\"url(#{id})\"");
                    _defs.Append($"><path d=\"{d}\"/></clipPath>\n");
                }
                id = known;
            }
            return id;
        }

        // ---------- text ----------

        /// <summary>
        /// The id of a letter's outline among the definitions, written there the first time the
        /// letter occurs; null when the font does not give one. The outline is for a letter one
        /// unit high and is made the right size where it is placed.
        /// </summary>
        private string? Glyph(IntPtr font, uint character)
        {
            if (_glyphs.TryGetValue((font, character), out var known)) return known;

            string? id = null;
            var path = Native.FPDFFont_GetGlyphPath(font, character, 1f);
            if (path != IntPtr.Zero)
            {
                int count = Native.FPDFGlyphPath_CountGlyphSegments(path);
                // a letter with nothing to its outline is a font that could not be read, not an empty letter
                if (count > 0)
                {
                    var d = PathData(count, i => Native.FPDFGlyphPath_GetGlyphPathSegment(path, i));
                    if (d.Length > 0)
                    {
                        id = NewId("g");
                        _defs.Append($"<path id=\"{id}\" d=\"{d}\"/>\n");
                    }
                }
            }
            _glyphs[(font, character)] = id;
            return id;
        }

        private void DrawText(IntPtr item)
        {
            int mode = Native.FPDFTextObj_GetTextRenderMode(item);
            if (mode is 3 or 7 || !_characters.TryGetValue(item, out var characters)) return;
            var font = Native.FPDFTextObj_GetFont(item);
            if (font == IntPtr.Zero) return;

            uint r = 0, g = 0, b = 0, a = 255;
            Native.FPDFPageObj_GetFillColor(item, &r, &g, &b, &a);
            _body.Append($"<g fill=\"{Colour(r, g, b)}\"");
            if (a < 255) _body.Append($" fill-opacity=\"{N(a / 255.0)}\"");
            _body.Append(">\n");

            foreach (int i in characters)
            {
                uint character = Native.FPDFText_GetUnicode(_text, i);
                if (IsBlank(character) || Glyph(font, character) is not { } glyph) continue;

                double x, y;
                var matrix = default(Native.FS_MATRIX);
                if (Native.FPDFText_GetCharOrigin(_text, i, &x, &y) == 0 || Native.FPDFText_GetMatrix(_text, i, &matrix) == 0) continue;
                double size = Native.FPDFText_GetFontSize(_text, i);
                if (size <= 0) continue;

                // the outline is one unit high: made the letter's size, turned and slanted as the text is, put on its spot
                var place = new Transform(matrix.A * size, matrix.B * size, matrix.C * size, matrix.D * size, x, y);
                _body.Append($"<use xlink:href=\"#{glyph}\" transform=\"{Root(place)}\"/>\n");
            }
            _body.Append("</g>\n");
        }

        // ---------- pictures ----------

        private void DrawPicture(IntPtr item, Transform own)
        {
            // In a PDF a picture fills the square from (0,0) to (1,1) with its top row at y = 1; SVG's
            // picture has its top row at y = 0.
            var place = new Transform(1, 0, 0, -1, 0, 1).Then(own);
            if (PictureData(item) is not { } data) return;

            if (!_pictures.TryGetValue(data, out var id))
            {
                id = NewId("i");
                _pictures[data] = id;
                _defs.Append($"<image id=\"{id}\" width=\"1\" height=\"1\" preserveAspectRatio=\"none\" xlink:href=\"{data}\"/>\n");
            }
            _body.Append($"<use xlink:href=\"#{id}\" transform=\"{Root(place)}\"/>\n");
        }

        /// <summary>The picture as a data address: at the resolution it has in the PDF, with its transparency.</summary>
        private string? PictureData(IntPtr item)
        {
            uint width = 0, height = 0;
            if (Native.FPDFImageObj_GetImagePixelSize(item, &width, &height) == 0 || width == 0 || height == 0) return null;
            if ((double)width * height > 100_000_000) return null;

            // PDFium draws the picture, mask and all, at the size its matrix gives it: for a moment
            // that is made the picture's own size in pixels.
            var original = default(Native.FS_MATRIX);
            Native.FPDFPageObj_GetMatrix(item, &original);
            var full = new Native.FS_MATRIX { A = width, D = height };
            Native.FPDFPageObj_SetMatrix(item, &full);
            IntPtr bitmap;
            try
            {
                bitmap = Native.FPDFImageObj_GetRenderedBitmap(_document, _page, item);
            }
            finally
            {
                Native.FPDFPageObj_SetMatrix(item, &original);
            }
            if (bitmap == IntPtr.Zero) return null;

            try
            {
                var pixels = ToBgra(bitmap, out int w, out int h, out bool opaque);
                if (pixels == null) return null;

                // a photo nothing shows through is in the PDF as a JPEG file: that very file, not its pixels stored again
                if (opaque && JpegOf(item) is { } jpeg) return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
                return "data:image/png;base64," + Convert.ToBase64String(EncodePng(pixels, w, h));
            }
            finally
            {
                Native.FPDFBitmap_Destroy(bitmap);
            }
        }

        /// <summary>The picture's data as stored, when that is a plain JPEG in red-green-blue or grey.</summary>
        private byte[]? JpegOf(IntPtr item)
        {
            if (Native.FPDFImageObj_GetImageFilterCount(item) != 1) return null;
            var name = stackalloc byte[32];
            nuint length = Native.FPDFImageObj_GetImageFilter(item, 0, name, 32);
            if (length is 0 or > 32 || Encoding.ASCII.GetString(name, (int)length - 1) != "DCTDecode") return null;

            var metadata = default(Native.FPDF_IMAGEOBJ_METADATA);
            if (Native.FPDFImageObj_GetImageMetadata(item, _page, &metadata) == 0) return null;
            if (metadata.Colorspace is not (Native.DeviceGray or Native.DeviceRgb)) return null; // print colours: a browser shows them wrong

            nuint size = Native.FPDFImageObj_GetImageDataRaw(item, null, 0);
            if (size is 0 or > int.MaxValue / 2) return null;
            var data = new byte[size];
            fixed (byte* buffer = data)
                if (Native.FPDFImageObj_GetImageDataRaw(item, buffer, size) != size) return null;
            return data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 ? data : null;
        }

        /// <summary>A PDFium bitmap as 4 bytes a pixel — blue, green, red, how opaque — whatever form it came in.</summary>
        private static byte[]? ToBgra(IntPtr bitmap, out int width, out int height, out bool opaque)
        {
            width = Native.FPDFBitmap_GetWidth(bitmap);
            height = Native.FPDFBitmap_GetHeight(bitmap);
            opaque = true;
            int stride = Native.FPDFBitmap_GetStride(bitmap);
            int format = Native.FPDFBitmap_GetFormat(bitmap);
            var source = (byte*)Native.FPDFBitmap_GetBuffer(bitmap);
            if (width <= 0 || height <= 0 || source == null) return null;

            var pixels = new byte[(long)width * height * 4];
            fixed (byte* target = pixels)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* from = source + (long)y * stride, to = target + (long)y * width * 4;
                    for (int x = 0; x < width; x++, to += 4)
                    {
                        switch (format)
                        {
                            case Native.Gray:
                                to[0] = to[1] = to[2] = from[x];
                                to[3] = 255;
                                break;
                            case Native.Bgr:
                                to[0] = from[x * 3];
                                to[1] = from[x * 3 + 1];
                                to[2] = from[x * 3 + 2];
                                to[3] = 255;
                                break;
                            case Native.Bgrx:
                                to[0] = from[x * 4];
                                to[1] = from[x * 4 + 1];
                                to[2] = from[x * 4 + 2];
                                to[3] = 255;
                                break;
                            default: // blue, green, red, alpha
                                to[0] = from[x * 4];
                                to[1] = from[x * 4 + 1];
                                to[2] = from[x * 4 + 2];
                                to[3] = from[x * 4 + 3];
                                if (to[3] != 255) opaque = false;
                                break;
                        }
                    }
                }
            }
            return pixels;
        }

        private static byte[] EncodePng(byte[] bgra, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            fixed (byte* pixels = bgra)
            {
                using var image = SKImage.FromPixelCopy(info, (IntPtr)pixels, width * 4);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
        }

        // ---------- what has to be a picture ----------

        /// <summary>
        /// Has PDFium draw some of the page's objects by themselves — the top-level objects
        /// <paramref name="first"/> to <paramref name="last"/>, or with first = −1 the annotations
        /// alone — onto nothing, and places the result as a picture where they stand.
        /// </summary>
        private void AddPatch(int first, int last)
        {
            var patch = Render(first, last, annotations: first < 0, box =>
            {
                double scale = PatchDpi / 72.0;
                double pixels = (box.Right - box.Left) * scale * (box.Bottom - box.Top) * scale;
                return pixels > MaxPatchPixels ? scale * Math.Sqrt(MaxPatchPixels / pixels) : scale;
            });
            if (patch is not { } drawn) return;

            // nothing drawn (links and the like have no look of their own): no picture
            bool empty = true;
            for (long i = 3; i < drawn.Pixels.LongLength; i += 4)
            {
                if (drawn.Pixels[i] != 0)
                {
                    empty = false;
                    break;
                }
            }
            if (empty) return;

            var box = drawn.Box;
            _body.Append($"<image x=\"{N(box.Left)}\" y=\"{N(box.Top)}\" width=\"{N(box.Right - box.Left)}\" height=\"{N(box.Bottom - box.Top)}\" preserveAspectRatio=\"none\" ")
                .Append("xlink:href=\"data:image/png;base64,").Append(Convert.ToBase64String(EncodePng(drawn.Pixels, drawn.Width, drawn.Height))).Append("\"/>\n");
        }

        /// <summary>
        /// Draws the objects <paramref name="first"/> to <paramref name="last"/> alone (or the
        /// annotations alone) on a copy of the page from which everything else has been taken, at
        /// the scale <paramref name="scaleFor"/> gives for the box around them. The box is in the
        /// picture's coordinates; the pixels are see-through where nothing was drawn.
        /// </summary>
        private (byte[] Pixels, int Width, int Height, (double Left, double Top, double Right, double Bottom) Box)? Render(
            int first, int last, bool annotations, Func<(double Left, double Top, double Right, double Bottom), double> scaleFor)
        {
            if (_scratch == IntPtr.Zero) return null;
            var page = Native.FPDF_LoadPage(_scratch, _index);
            if (page == IntPtr.Zero) return null;
            try
            {
                double left = 0, top = 0, right = _width, bottom = _height;
                int count = Native.FPDFPage_CountObjects(page);
                if (!annotations)
                {
                    (left, top, right, bottom) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                    for (int i = first; i <= last && i < count; i++)
                    {
                        float l, b, r, t;
                        if (Native.FPDFPageObj_GetBounds(Native.FPDFPage_GetObject(page, i), &l, &b, &r, &t) == 0) continue;
                        foreach (var (x, y) in new[] { _toSvg.Apply(l, b), _toSvg.Apply(r, b), _toSvg.Apply(l, t), _toSvg.Apply(r, t) })
                        {
                            left = Math.Min(left, x);
                            right = Math.Max(right, x);
                            top = Math.Min(top, y);
                            bottom = Math.Max(bottom, y);
                        }
                    }
                    // a little more than the box (a line is half its width wider than its path), and nothing beyond the page
                    left = Math.Max(Math.Floor(left) - 2, 0);
                    top = Math.Max(Math.Floor(top) - 2, 0);
                    right = Math.Min(Math.Ceiling(right) + 2, _width);
                    bottom = Math.Min(Math.Ceiling(bottom) + 2, _height);
                    if (right <= left || bottom <= top) return null;
                }

                // everything else is taken off this copy of the page (it is opened afresh the next time)
                for (int i = count - 1; i >= 0; i--)
                {
                    if (!annotations && i >= first && i <= last) continue;
                    var other = Native.FPDFPage_GetObject(page, i);
                    if (other != IntPtr.Zero && Native.FPDFPage_RemoveObject(page, other) != 0) Native.FPDFPageObj_Destroy(other);
                }

                double scale = scaleFor((left, top, right, bottom));
                int width = (int)Math.Max(1, Math.Round((right - left) * scale)), height = (int)Math.Max(1, Math.Round((bottom - top) * scale));

                var buffer = new byte[(long)width * height * 4]; // all zero: nothing there, and see-through
                fixed (byte* memory = buffer)
                {
                    var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.Bgra, memory, width * 4);
                    if (bitmap == IntPtr.Zero) return null;
                    try
                    {
                        // the whole page at this scale, moved so that the box lies on the bitmap
                        Native.FPDF_RenderPageBitmap(bitmap, page, -(int)Math.Round(left * scale), -(int)Math.Round(top * scale),
                            (int)Math.Round(_width * scale), (int)Math.Round(_height * scale), 0,
                            Native.FPDF_PRINTING | (annotations ? Native.FPDF_ANNOT : 0));
                    }
                    finally
                    {
                        Native.FPDFBitmap_Destroy(bitmap);
                    }
                }
                return (buffer, width, height, (left, top, right, bottom));
            }
            finally
            {
                Native.FPDF_ClosePage(page);
            }
        }
    }

    private static class Native
    {
        private const string Library = "pdfium";

        public const int Text = 1, Path = 2, Image = 3, Shading = 4, Form = 5;
        public const int LineTo = 0, BezierTo = 1, MoveTo = 2;
        public const int Gray = 1, Bgr = 2, Bgrx = 3, Bgra = 4;
        public const int DeviceGray = 1, DeviceRgb = 2;
        public const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800;

        [StructLayout(LayoutKind.Sequential)]
        public struct FS_MATRIX
        {
            public float A, B, C, D, E, F;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FPDF_IMAGEOBJ_METADATA
        {
            public uint Width, Height;
            public float HorizontalDpi, VerticalDpi;
            public uint BitsPerPixel;
            public int Colorspace;
            public int MarkedContentId;
        }

        [DllImport(Library)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Library)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Library)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Library)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Library)] public static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Library)] public static extern int FPDFPage_GetMediaBox(IntPtr page, float* left, float* bottom, float* right, float* top);
        [DllImport(Library)] public static extern int FPDFPage_GetCropBox(IntPtr page, float* left, float* bottom, float* right, float* top);
        [DllImport(Library)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
        [DllImport(Library)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        [DllImport(Library)] public static extern int FPDFPage_CountObjects(IntPtr page);
        [DllImport(Library)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
        [DllImport(Library)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr item);
        [DllImport(Library)] public static extern void FPDFPageObj_Destroy(IntPtr item);
        [DllImport(Library)] public static extern int FPDFPageObj_GetType(IntPtr item);
        [DllImport(Library)] public static extern int FPDFPageObj_HasTransparency(IntPtr item);
        [DllImport(Library)] public static extern int FPDFPageObj_GetMatrix(IntPtr item, FS_MATRIX* matrix);
        [DllImport(Library)] public static extern int FPDFPageObj_SetMatrix(IntPtr item, FS_MATRIX* matrix);
        [DllImport(Library)] public static extern int FPDFPageObj_GetBounds(IntPtr item, float* left, float* bottom, float* right, float* top);
        [DllImport(Library)] public static extern int FPDFPageObj_GetFillColor(IntPtr item, uint* r, uint* g, uint* b, uint* a);
        [DllImport(Library)] public static extern int FPDFPageObj_GetStrokeColor(IntPtr item, uint* r, uint* g, uint* b, uint* a);
        [DllImport(Library)] public static extern int FPDFPageObj_GetStrokeWidth(IntPtr item, float* width);
        [DllImport(Library)] public static extern int FPDFPageObj_GetLineJoin(IntPtr item);
        [DllImport(Library)] public static extern int FPDFPageObj_GetLineCap(IntPtr item);
        [DllImport(Library)] public static extern int FPDFPageObj_GetDashCount(IntPtr item);
        [DllImport(Library)] public static extern int FPDFPageObj_GetDashArray(IntPtr item, float* dashes, nuint count);
        [DllImport(Library)] public static extern int FPDFPageObj_GetDashPhase(IntPtr item, float* phase);
        [DllImport(Library)] public static extern IntPtr FPDFPageObj_GetClipPath(IntPtr item);

        [DllImport(Library)] public static extern int FPDFClipPath_CountPaths(IntPtr clip);
        [DllImport(Library)] public static extern int FPDFClipPath_CountPathSegments(IntPtr clip, int path);
        [DllImport(Library)] public static extern IntPtr FPDFClipPath_GetPathSegment(IntPtr clip, int path, int segment);

        [DllImport(Library)] public static extern int FPDFPath_CountSegments(IntPtr path);
        [DllImport(Library)] public static extern IntPtr FPDFPath_GetPathSegment(IntPtr path, int index);
        [DllImport(Library)] public static extern int FPDFPath_GetDrawMode(IntPtr path, int* fillMode, int* stroke);
        [DllImport(Library)] public static extern int FPDFPathSegment_GetPoint(IntPtr segment, float* x, float* y);
        [DllImport(Library)] public static extern int FPDFPathSegment_GetType(IntPtr segment);
        [DllImport(Library)] public static extern int FPDFPathSegment_GetClose(IntPtr segment);

        [DllImport(Library)] public static extern int FPDFFormObj_CountObjects(IntPtr form);
        [DllImport(Library)] public static extern IntPtr FPDFFormObj_GetObject(IntPtr form, uint index);

        [DllImport(Library)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Library)] public static extern void FPDFText_ClosePage(IntPtr text);
        [DllImport(Library)] public static extern int FPDFText_CountChars(IntPtr text);
        [DllImport(Library)] public static extern uint FPDFText_GetUnicode(IntPtr text, int index);
        [DllImport(Library)] public static extern IntPtr FPDFText_GetTextObject(IntPtr text, int index);
        [DllImport(Library)] public static extern int FPDFText_GetCharOrigin(IntPtr text, int index, double* x, double* y);
        [DllImport(Library)] public static extern int FPDFText_GetMatrix(IntPtr text, int index, FS_MATRIX* matrix);
        [DllImport(Library)] public static extern double FPDFText_GetFontSize(IntPtr text, int index);
        [DllImport(Library)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr text);
        [DllImport(Library)] public static extern int FPDFTextObj_GetTextRenderMode(IntPtr text);
        [DllImport(Library)] public static extern IntPtr FPDFFont_GetGlyphPath(IntPtr font, uint glyph, float fontSize);
        [DllImport(Library)] public static extern int FPDFGlyphPath_CountGlyphSegments(IntPtr path);
        [DllImport(Library)] public static extern IntPtr FPDFGlyphPath_GetGlyphPathSegment(IntPtr path, int index);

        [DllImport(Library)] public static extern int FPDFImageObj_GetImagePixelSize(IntPtr image, uint* width, uint* height);
        [DllImport(Library)] public static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr image);
        [DllImport(Library)] public static extern int FPDFImageObj_GetImageFilterCount(IntPtr image);
        [DllImport(Library)] public static extern nuint FPDFImageObj_GetImageFilter(IntPtr image, int index, byte* buffer, nuint length);
        [DllImport(Library)] public static extern nuint FPDFImageObj_GetImageDataRaw(IntPtr image, byte* buffer, nuint length);
        [DllImport(Library)] public static extern int FPDFImageObj_GetImageMetadata(IntPtr image, IntPtr page, FPDF_IMAGEOBJ_METADATA* metadata);

        [DllImport(Library)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, byte* firstScan, int stride);
        [DllImport(Library)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Library)] public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
        [DllImport(Library)] public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
        [DllImport(Library)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Library)] public static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
        [DllImport(Library)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    }
}
