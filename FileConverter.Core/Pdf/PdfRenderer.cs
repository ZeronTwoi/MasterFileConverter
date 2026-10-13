using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace FileConverter.Core.Pdf;

/// <summary>
/// One rendered page: 8-bit BGRA pixels, top row first, no padding between rows. The buffer is
/// borrowed — a page at 300 dpi is 35 MB, and a fresh array for every page would keep the garbage
/// collector busy — so it may be longer than the picture, and goes back when the page is disposed.
/// </summary>
public sealed class PdfPageImage(int width, int height, byte[] bgra) : IDisposable
{
    private byte[]? _bgra = bgra;

    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>The pixels: the first <c>Width × Height × 4</c> bytes.</summary>
    public byte[] Bgra => _bgra ?? throw new ObjectDisposedException(nameof(PdfPageImage));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _bgra, null) is { } buffer) ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// Renders PDF pages with PDFium (the engine inside Chrome). The native library comes from the
/// platform project, so each download carries only its own system's build.
///
/// PDFium is not thread-safe: every call goes through one process-wide lock. Rendering is
/// therefore serial, and callers do the expensive part — encoding the pixels — in parallel.
/// </summary>
public sealed unsafe class PdfRenderer : IDisposable
{
    /// <summary>Held around every call into PDFium, by whoever makes one.</summary>
    internal static readonly object Engine = new();
    private static bool _initialized;

    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long _length;
    private byte* _data;
    private IntPtr _document;

    public int PageCount { get; }

    /// <summary>PDFium's handle of the open document, for the calls this class does not wrap (under <see cref="Engine"/>).</summary>
    internal IntPtr Handle => _document;

    /// <summary>
    /// Opens the same file a second time, as a document of its own: one whose pages can be taken
    /// apart without the first one noticing. The caller closes it, and before this renderer is disposed.
    /// </summary>
    internal IntPtr OpenAgain()
    {
        lock (Engine) return Native.FPDF_LoadMemDocument64(_data, (nuint)_length, null);
    }

    internal static void Close(IntPtr document)
    {
        if (document == IntPtr.Zero) return;
        lock (Engine) Native.FPDF_CloseDocument(document);
    }

    /// <summary>Opens a PDF. Throws <see cref="FriendlyException"/> when it can't be read.</summary>
    public PdfRenderer(string path)
    {
        long length = _length = new FileInfo(path).Length;
        if (length == 0) throw new FriendlyException("The PDF file is empty.");

        // The file is mapped rather than read: PDFium needs the bytes for as long as the document
        // is open, and a mapping costs no memory for the parts it never touches. It also avoids
        // handing PDFium a file name, which it would read in the system's legacy encoding.
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _data);
        _data += _view.PointerOffset;

        lock (Engine)
        {
            EnsureInitialized();
            _document = Native.FPDF_LoadMemDocument64(_data, (nuint)length, null);
            if (_document == IntPtr.Zero)
            {
                uint error = Native.FPDF_GetLastError();
                Dispose();
                throw new FriendlyException(error switch
                {
                    4 => "This PDF is password-protected. Remove the password first, then convert it.",
                    3 => "This file is not a PDF, or it is damaged.",
                    _ => "Could not open the PDF for rendering."
                });
            }
            PageCount = Native.FPDF_GetPageCount(_document);
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        try
        {
            Native.FPDF_InitLibrary();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new FriendlyException("The PDF engine (PDFium) is missing from this installation.");
        }
        _initialized = true;
    }

    /// <summary>Page size in points (1/72 inch).</summary>
    public (double Width, double Height) GetPageSize(int index)
    {
        lock (Engine)
        {
            var size = default(Native.FS_SIZEF);
            if (Native.FPDF_GetPageSizeByIndexF(_document, index, &size) == 0)
                throw new FriendlyException($"Page {index + 1} of the PDF could not be read.");
            return (size.Width, size.Height);
        }
    }

    /// <summary>Renders a page onto a white background at the given pixel size.</summary>
    public PdfPageImage Render(int index, int width, int height)
    {
        long size = (long)width * height * 4;
        if (size > Array.MaxLength)
            throw new FriendlyException($"Page {index + 1} is too large to render at this resolution.");
        var pixels = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            Draw(index, width, height, pixels);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
        return new PdfPageImage(width, height, pixels);
    }

    private void Draw(int index, int width, int height, byte[] pixels)
    {
        lock (Engine)
        {
            var page = Native.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero)
                throw new FriendlyException($"Page {index + 1} of the PDF could not be read.");
            try
            {
                fixed (byte* buffer = pixels)
                {
                    var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.FPDFBitmap_BGRA, buffer, width * 4);
                    if (bitmap == IntPtr.Zero)
                        throw new FriendlyException($"Page {index + 1} is too large to render at this resolution.");
                    try
                    {
                        Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0,
                            Native.FPDF_ANNOT | Native.FPDF_PRINTING);
                    }
                    finally
                    {
                        Native.FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
            finally
            {
                Native.FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        lock (Engine)
        {
            if (_document != IntPtr.Zero)
            {
                Native.FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }
        }
        if (_data != null)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _data = null;
        }
        _view?.Dispose();
        _map?.Dispose();
    }

    private static class Native
    {
        private const string Library = "pdfium";

        public const int FPDFBitmap_BGRA = 4;
        public const int FPDF_ANNOT = 0x01;     // draw annotations and filled-in form fields
        public const int FPDF_PRINTING = 0x800; // render as for print: full-quality images, no screen-only hints

        [StructLayout(LayoutKind.Sequential)]
        public struct FS_SIZEF
        {
            public float Width;
            public float Height;
        }

        [DllImport(Library)] public static extern void FPDF_InitLibrary();
        [DllImport(Library)] public static extern uint FPDF_GetLastError();
        [DllImport(Library)] public static extern IntPtr FPDF_LoadMemDocument64(byte* data, nuint size, byte* password);
        [DllImport(Library)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Library)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Library)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int index, FS_SIZEF* size);
        [DllImport(Library)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Library)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Library)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, byte* firstScan, int stride);
        [DllImport(Library)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Library)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Library)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY,
            int sizeX, int sizeY, int rotate, int flags);
    }
}
