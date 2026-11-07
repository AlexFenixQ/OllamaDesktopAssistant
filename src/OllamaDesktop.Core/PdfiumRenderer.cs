using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace OllamaDesktop.Core;

/// <summary>Small x64 C ABI adapter. No JavaScript, actions, forms or external resource APIs are invoked.</summary>
public sealed class PdfiumRenderer : IDisposable
{
    private static readonly object Gate = new();
    private GCHandle _memory;
    private IntPtr _document;
    static PdfiumRenderer()
    {
        NativeLibrary.SetDllImportResolver(typeof(PdfiumRenderer).Assembly, (name, assembly, search) =>
        {
            if (name != "pdfium") return IntPtr.Zero;
            foreach (var relative in new[] { "pdfium.dll", Path.Combine("runtimes", "win-x64", "native", "pdfium.dll") })
            {
                var full = Path.Combine(AppContext.BaseDirectory, relative);
                if (File.Exists(full)) return NativeLibrary.Load(full);
            }
            throw new DllNotFoundException("Packaged pdfium.dll is missing.");
        });
        Native.FPDF_InitLibrary();
    }
    public PdfiumRenderer(byte[] data)
    {
        _memory = GCHandle.Alloc(data, GCHandleType.Pinned);
        lock (Gate) _document = Native.FPDF_LoadMemDocument64(_memory.AddrOfPinnedObject(), (UIntPtr)(ulong)data.Length, IntPtr.Zero);
        if (_document == IntPtr.Zero) { _memory.Free(); throw new AppException("PDF_OPEN", "PDF повреждён, зашифрован или не поддерживается."); }
    }
    public int PageCount { get { lock (Gate) return Native.FPDF_GetPageCount(_document); } }
    public void Render(int index, string output, int maxSide)
    {
        lock (Gate)
        {
            var page = Native.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero) throw new AppException("PDF_PAGE", "Не удалось открыть страницу PDF.");
            try
            {
                var widthPoints = Native.FPDF_GetPageWidthF(page); var heightPoints = Native.FPDF_GetPageHeightF(page);
                if (!float.IsFinite(widthPoints) || !float.IsFinite(heightPoints) || widthPoints <= 0 || heightPoints <= 0 || widthPoints > 30000 || heightPoints > 30000)
                    throw new AppException("PDF_DIMENSIONS", "Некорректный размер страницы PDF.");
                var factor = Math.Min(maxSide / widthPoints, maxSide / heightPoints);
                var width = Math.Clamp((int)Math.Ceiling(widthPoints * factor), 1, maxSide);
                var height = Math.Clamp((int)Math.Ceiling(heightPoints * factor), 1, maxSide);
                var bitmap = Native.FPDFBitmap_Create(width, height, 1);
                if (bitmap == IntPtr.Zero) throw new AppException("PDF_MEMORY", "Недостаточно памяти для страницы PDF.");
                try
                {
                    Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 1);
                    var stride = Native.FPDFBitmap_GetStride(bitmap); var pixels = new byte[checked(stride * height)];
                    Marshal.Copy(Native.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                    PngWriter.WriteBgra(output, width, height, stride, pixels);
                }
                finally { Native.FPDFBitmap_Destroy(bitmap); }
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }
    public void Dispose()
    {
        lock (Gate) { if (_document != IntPtr.Zero) { Native.FPDF_CloseDocument(_document); _document = IntPtr.Zero; } }
        if (_memory.IsAllocated) _memory.Free();
    }
    private static class Native
    {
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_InitLibrary();
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, UIntPtr size, IntPtr password);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(IntPtr page);
        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(IntPtr document);
    }
}
public static class PngWriter
{
    public static void WriteBgra(string path, int width, int height, int stride, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || (ulong)width * (ulong)height > Limits.ImagePixels || stride < width * 4 || bgra.Length < (long)stride * height) throw new ArgumentException("Invalid bitmap.");
        using var output = File.Create(path); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++) { var source = y * stride + x * 4; var target = 1 + x * 3; row[target] = bgra[source + 2]; row[target + 1] = bgra[source + 1]; row[target + 2] = bgra[source]; }
                zlib.Write(row);
            }
        }
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []);
    }
    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        var name = System.Text.Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var value in name.Concat(data)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u); }
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF); output.Write(number);
    }
}
