using System.Runtime.InteropServices;

namespace PDFMaster.Rendering;

/// <summary>Página renderizada: pixels BGRA de 32 bits, opacos, sobre fundo branco.</summary>
public sealed record RenderedPage(int Width, int Height, byte[] Bgra);

/// <summary>Como dimensionar a renderização.</summary>
public readonly record struct RenderSize
{
    private RenderSize(double scale, int shortSide, int longSide)
    {
        Scale = scale;
        ShortSide = shortSide;
        LongSide = longSide;
    }

    public double Scale { get; }
    public int ShortSide { get; }
    public int LongSide { get; }
    public bool IsFit => ShortSide > 0;

    /// <summary>Fator sobre o tamanho natural da página: 1.0 = 72 pontos por polegada.</summary>
    public static RenderSize ByScale(double scale) => new(Math.Clamp(scale, 0.05, 12), 0, 0);

    /// <summary>Ajusta a página dentro de uma caixa (lado menor × lado maior), em pixels.</summary>
    public static RenderSize Fit(int shortSide, int longSide) =>
        new(0, Math.Max(16, Math.Min(shortSide, longSide)), Math.Max(16, Math.Max(shortSide, longSide)));
}

public sealed class PdfRenderException : Exception
{
    public PdfRenderException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Abstração da renderização de páginas (hoje PDFium, chamado diretamente).</summary>
public interface IPdfRenderer
{
    Task<RenderedPage> RenderPageAsync(string path, int pageIndex, RenderSize size, CancellationToken ct = default);

    /// <summary>
    /// Renderiza várias páginas abrindo o documento uma única vez. <paramref name="onPage"/> é
    /// chamado fora da thread da interface, na ordem pedida.
    /// </summary>
    Task RenderPagesAsync(
        string path,
        IReadOnlyList<int> pageIndexes,
        RenderSize size,
        Action<int, RenderedPage> onPage,
        CancellationToken ct = default);
}

public sealed class PdfiumRenderer : IPdfRenderer
{
    private const int MaxSide = 16000;
    private const long MaxPixels = 120_000_000;

    // O PDFium não é seguro para uso simultâneo; todas as chamadas passam por esta trava.
    private static readonly object Gate = new();
    private static bool _initialized;

    public async Task<RenderedPage> RenderPageAsync(string path, int pageIndex, RenderSize size, CancellationToken ct = default)
    {
        RenderedPage? result = null;
        await RenderPagesAsync(path, [pageIndex], size, (_, page) => result = page, ct).ConfigureAwait(false);
        return result ?? throw new PdfRenderException($"A página {pageIndex + 1} não pôde ser renderizada.");
    }

    public Task RenderPagesAsync(
        string path,
        IReadOnlyList<int> pageIndexes,
        RenderSize size,
        Action<int, RenderedPage> onPage,
        CancellationToken ct = default) => Task.Run(() =>
    {
        if (pageIndexes.Count == 0) return;
        ct.ThrowIfCancellationRequested();

        // O arquivo é lido sob demanda pelo PDFium (não é carregado inteiro na memória) e é aberto
        // com compartilhamento total, para não ficar bloqueado enquanto a renderização acontece.
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PdfRenderException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }

        using (stream)
        {
            if (stream.Length == 0 || stream.Length > uint.MaxValue)
                throw new PdfRenderException($"\"{Path.GetFileName(path)}\" não pôde ser renderizado: tamanho de arquivo não suportado.");

            var reader = new BlockReader(stream);
            Native.GetBlockCallback callback = reader.Read;
            IntPtr document;
            int pageCount;
            lock (Gate)
            {
                if (!_initialized)
                {
                    Native.FPDF_InitLibrary();
                    _initialized = true;
                }

                var access = new Native.FileAccess
                {
                    FileLength = (uint)stream.Length,
                    GetBlock = Marshal.GetFunctionPointerForDelegate(callback),
                    Param = IntPtr.Zero,
                };
                document = Native.FPDF_LoadCustomDocument(ref access, null);
                if (document == IntPtr.Zero)
                {
                    throw new PdfRenderException(
                        $"\"{Path.GetFileName(path)}\" não pôde ser renderizado. O arquivo pode estar protegido por senha ou danificado.");
                }

                pageCount = Native.FPDF_GetPageCount(document);
            }

            try
            {
                foreach (var index in pageIndexes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (index < 0 || index >= pageCount) continue;

                    RenderedPage? rendered;
                    lock (Gate)
                    {
                        rendered = RenderLocked(document, index, size);
                    }

                    if (rendered is not null) onPage(index, rendered);
                }
            }
            finally
            {
                lock (Gate)
                {
                    Native.FPDF_CloseDocument(document);
                }

                GC.KeepAlive(callback);
            }
        }
    }, ct);

    private static RenderedPage? RenderLocked(IntPtr document, int index, RenderSize size)
    {
        var page = Native.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero) throw new PdfRenderException($"A página {index + 1} não pôde ser renderizada.");

        try
        {
            double pageWidth = Native.FPDF_GetPageWidthF(page);
            double pageHeight = Native.FPDF_GetPageHeightF(page);
            if (pageWidth <= 0 || pageHeight <= 0) return null;

            var scale = size.IsFit
                ? Math.Min(size.ShortSide / Math.Min(pageWidth, pageHeight), size.LongSide / Math.Max(pageWidth, pageHeight))
                : size.Scale;

            // Limites de segurança: lado e área máximos da imagem.
            scale = Math.Min(scale, MaxSide / Math.Max(pageWidth, pageHeight));
            scale = Math.Min(scale, Math.Sqrt(MaxPixels / (pageWidth * pageHeight)));

            var width = Math.Max(1, (int)Math.Round(pageWidth * scale));
            var height = Math.Max(1, (int)Math.Round(pageHeight * scale));
            var stride = width * 4;
            var pixels = new byte[(long)stride * height];

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.FormatBgra, handle.AddrOfPinnedObject(), stride);
                if (bitmap == IntPtr.Zero) throw new PdfRenderException($"A página {index + 1} não pôde ser renderizada.");
                try
                {
                    Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Native.RenderAnnotations);
                }
                finally
                {
                    Native.FPDFBitmap_Destroy(bitmap);
                }
            }
            finally
            {
                handle.Free();
            }

            // A página foi desenhada sobre branco opaco; o canal alfa é fixado em 255.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new RenderedPage(width, height, pixels);
        }
        finally
        {
            Native.FPDF_ClosePage(page);
        }
    }

    /// <summary>Entrega ao PDFium os trechos do arquivo que ele pede (sempre dentro da trava).</summary>
    private sealed class BlockReader(FileStream stream)
    {
        private byte[] _buffer = new byte[64 * 1024];

        public int Read(IntPtr param, uint position, IntPtr buffer, uint size)
        {
            try
            {
                var count = checked((int)size);
                if (_buffer.Length < count) _buffer = new byte[count];
                stream.Position = position;
                var total = 0;
                while (total < count)
                {
                    var read = stream.Read(_buffer, total, count - total);
                    if (read == 0) return 0;
                    total += read;
                }

                Marshal.Copy(_buffer, 0, buffer, count);
                return 1;
            }
            catch (Exception)
            {
                // Exceções não podem atravessar o código nativo; 0 informa falha de leitura ao PDFium.
                return 0;
            }
        }
    }

    private static class Native
    {
        private const string Library = "pdfium";

        public const int FormatBgra = 4;
        public const int RenderAnnotations = 0x01;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetBlockCallback(IntPtr param, uint position, IntPtr buffer, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct FileAccess
        {
            public uint FileLength;
            public IntPtr GetBlock;
            public IntPtr Param;
        }

        [DllImport(Library)]
        public static extern void FPDF_InitLibrary();

        [DllImport(Library, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern IntPtr FPDF_LoadCustomDocument(ref FileAccess access, [MarshalAs(UnmanagedType.LPStr)] string? password);

        [DllImport(Library)]
        public static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Library)]
        public static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(Library)]
        public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

        [DllImport(Library)]
        public static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(Library)]
        public static extern float FPDF_GetPageWidthF(IntPtr page);

        [DllImport(Library)]
        public static extern float FPDF_GetPageHeightF(IntPtr page);

        [DllImport(Library)]
        public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

        [DllImport(Library)]
        public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport(Library)]
        public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        [DllImport(Library)]
        public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }
}
