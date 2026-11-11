using System.Runtime.InteropServices;

namespace PDFMaster.Rendering;

/// <summary>Página renderizada: pixels BGRA de 32 bits, opacos, sobre fundo branco.</summary>
public sealed record RenderedPage(int Width, int Height, byte[] Bgra);

/// <param name="SecurityRevision">-1 = sem criptografia; 2/3 = RC4; 4 = RC4 ou AES-128; 5/6 = AES-256.</param>
/// <param name="Permissions">Bits /P do PDF (todos ligados quando aberto com a senha de proprietário).</param>
/// <param name="FileVersion">Versão do PDF ×10 (ex.: 17 = 1.7).</param>
/// <param name="Error">Código do PDFium quando não abriu (2 arquivo, 3 formato, 4 senha, 5 segurança).</param>
public sealed record PdfiumInspection(bool Opened, bool PasswordRequired, int SecurityRevision, uint Permissions, int PageCount,
    int FileVersion, int Error, IReadOnlyList<int> BrokenPages)
{
    public bool Encrypted => SecurityRevision >= 0 || PasswordRequired;
}

/// <summary>Caractere com a caixa na página exibida (pontos, origem no canto superior esquerdo, y para baixo).</summary>
public readonly record struct DisplayChar(char Value, double X, double Y, double Width, double Height);

/// <summary>Texto de uma página exibida: tamanho exibido (com rotação e CropBox) e caracteres posicionados.</summary>
public sealed record DisplayPageText(double Width, double Height, IReadOnlyList<DisplayChar> Chars);

/// <summary>Caractere de uma página com a caixa no espaço de usuário do PDF (pontos, y para cima).</summary>
public readonly record struct PdfChar(char Value, double Left, double Right, double Bottom, double Top, double FontSize)
{
    public bool HasBox => Right > Left && Top > Bottom;
}

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
            var formInfo = IntPtr.Zero;
            var form = IntPtr.Zero;
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

                // Campos de formulário (widgets) só são desenhados pelo ambiente de formulários do PDFium.
                formInfo = Native.CreateFormFillInfo();
                form = Native.FPDFDOC_InitFormFillEnvironment(document, formInfo);
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
                        rendered = RenderLocked(document, form, index, size);
                    }

                    if (rendered is not null) onPage(index, rendered);
                }
            }
            finally
            {
                lock (Gate)
                {
                    if (form != IntPtr.Zero) Native.FPDFDOC_ExitFormFillEnvironment(form);
                    Native.FPDF_CloseDocument(document);
                }

                if (formInfo != IntPtr.Zero) Marshal.FreeHGlobal(formInfo);
                GC.KeepAlive(callback);
            }
        }
    }, ct);

    /// <summary>
    /// Caracteres de uma página com a caixa de cada um no espaço de usuário do PDF (sem a rotação de
    /// exibição). Espaços e quebras de linha inferidos pelo PDFium vêm como caracteres de espaço.
    /// </summary>
    public static IReadOnlyList<PdfChar> ReadChars(string path, int pageIndex)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PdfRenderException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }

        using (stream)
        {
            if (stream.Length == 0 || stream.Length > uint.MaxValue) return [];
            var reader = new BlockReader(stream);
            Native.GetBlockCallback callback = reader.Read;
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
                var document = Native.FPDF_LoadCustomDocument(ref access, null);
                if (document == IntPtr.Zero)
                    throw new PdfRenderException($"\"{Path.GetFileName(path)}\" não pôde ser lido. O arquivo pode estar protegido por senha ou danificado.");
                try
                {
                    if (pageIndex < 0 || pageIndex >= Native.FPDF_GetPageCount(document)) return [];
                    var page = Native.FPDF_LoadPage(document, pageIndex);
                    if (page == IntPtr.Zero) return [];
                    try
                    {
                        var text = Native.FPDFText_LoadPage(page);
                        if (text == IntPtr.Zero) return [];
                        try
                        {
                            var count = Native.FPDFText_CountChars(text);
                            var chars = new List<PdfChar>(Math.Max(0, count));
                            for (var i = 0; i < count; i++)
                            {
                                var code = Native.FPDFText_GetUnicode(text, i);
                                var c = code is > 0 and < 0x10000 ? (char)code : ' ';
                                if (Native.FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) == 0)
                                    left = right = bottom = top = 0;
                                chars.Add(new PdfChar(c, left, right, bottom, top, Native.FPDFText_GetFontSize(text, i)));
                            }

                            return chars;
                        }
                        finally
                        {
                            Native.FPDFText_ClosePage(text);
                        }
                    }
                    finally
                    {
                        Native.FPDF_ClosePage(page);
                    }
                }
                finally
                {
                    Native.FPDF_CloseDocument(document);
                    GC.KeepAlive(callback);
                }
            }
        }
    }

    /// <summary>
    /// Abre o documento com o PDFium (que reconstrói a tabela de referências de arquivos danificados)
    /// e executa <paramref name="action"/> dentro da trava. Devolve null e o código de erro do PDFium
    /// quando o arquivo não abre (4 = senha necessária ou incorreta).
    /// </summary>
    private static (T? Result, uint Error) WithDocument<T>(string path, string? password, Func<IntPtr, T> action)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PdfRenderException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }

        using (stream)
        {
            if (stream.Length == 0 || stream.Length > uint.MaxValue) return (default, 3);
            var reader = new BlockReader(stream);
            Native.GetBlockCallback callback = reader.Read;
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
                var document = Native.FPDF_LoadCustomDocument(ref access, password);
                if (document == IntPtr.Zero) return (default, Native.FPDF_GetLastError());
                try
                {
                    return (action(document), 0);
                }
                finally
                {
                    Native.FPDF_CloseDocument(document);
                    GC.KeepAlive(callback);
                }
            }
        }
    }

    /// <summary>Páginas que o PDFium não consegue carregar (índices a partir de 0).</summary>
    private static List<int> BrokenPages(IntPtr document, int count)
    {
        var broken = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var page = Native.FPDF_LoadPage(document, i);
            if (page == IntPtr.Zero)
            {
                broken.Add(i);
                continue;
            }

            if (Native.FPDF_GetPageWidthF(page) <= 0 || Native.FPDF_GetPageHeightF(page) <= 0) broken.Add(i);
            Native.FPDF_ClosePage(page);
        }

        return broken;
    }

    /// <summary>Informações de proteção e integridade lidas pelo PDFium.</summary>
    public static PdfiumInspection Inspect(string path, string? password = null, bool checkPages = false)
    {
        var (result, error) = WithDocument(path, password, document =>
        {
            var count = Native.FPDF_GetPageCount(document);
            var version = Native.FPDF_GetFileVersion(document, out var v) != 0 ? v : 0;
            return new PdfiumInspection(true, false, Native.FPDF_GetSecurityHandlerRevision(document), Native.FPDF_GetDocPermissions(document),
                count, version, 0, checkPages ? BrokenPages(document, count) : []);
        });
        return result ?? new PdfiumInspection(false, error == 4, -1, 0, 0, 0, (int)error, []);
    }

    /// <summary>
    /// Regrava o documento como o PDFium o entendeu (tabela de referências nova, sem atualizações
    /// incrementais). Usado no reparo de arquivos danificados. Devolve as páginas que não carregaram.
    /// </summary>
    /// <param name="removeBrokenPages">Retira da cópia as páginas que não carregam.</param>
    public static (int PageCount, IReadOnlyList<int> BrokenPages) SaveCopy(string path, string? password, string output, int fileVersion = 0,
        bool removeBrokenPages = false)
    {
        var (result, error) = WithDocument(path, password, document =>
        {
            var count = Native.FPDF_GetPageCount(document);
            var broken = BrokenPages(document, count);
            if (removeBrokenPages)
            {
                for (var i = broken.Count - 1; i >= 0; i--) Native.FPDFPage_Delete(document, broken[i]);
            }

            using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            Native.WriteBlockCallback write = (_, data, size) =>
            {
                try
                {
                    var buffer = new byte[size];
                    Marshal.Copy(data, buffer, 0, (int)size);
                    target.Write(buffer, 0, buffer.Length);
                    return 1;
                }
                catch (Exception)
                {
                    return 0;
                }
            };
            var writer = new Native.FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) };
            var ok = (fileVersion > 0
                ? Native.FPDF_SaveWithVersion(document, ref writer, Native.NoIncremental, fileVersion)
                : Native.FPDF_SaveAsCopy(document, ref writer, Native.NoIncremental)) != 0;
            GC.KeepAlive(write);
            if (!ok) throw new PdfRenderException("O PDFium não conseguiu regravar o documento.");
            return (count, (IReadOnlyList<int>)broken);
        });
        if (error != 0) throw new PdfRenderException(error == 4 ? "O documento está protegido por senha." : "O PDFium também não conseguiu abrir o arquivo.");
        return result;
    }

    /// <summary>
    /// Texto de todas as páginas com a posição de cada caractere na página exibida, calculada pelo
    /// próprio PDFium (mesma rotação e recorte da renderização). Páginas que não carregam vêm vazias.
    /// </summary>
    public static IReadOnlyList<DisplayPageText> ReadDisplayText(string path, CancellationToken ct = default)
    {
        // Escala do "dispositivo" virtual: 10 unidades por ponto (precisão de 0,1 pt nas conversões inteiras).
        const int k = 10;
        var (result, error) = WithDocument(path, null, document =>
        {
            var pages = new List<DisplayPageText>();
            var count = Native.FPDF_GetPageCount(document);
            for (var p = 0; p < count; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = Native.FPDF_LoadPage(document, p);
                if (page == IntPtr.Zero)
                {
                    pages.Add(new DisplayPageText(0, 0, []));
                    continue;
                }

                try
                {
                    double width = Native.FPDF_GetPageWidthF(page), height = Native.FPDF_GetPageHeightF(page);
                    int sx = (int)Math.Round(width * k), sy = (int)Math.Round(height * k);
                    var chars = new List<DisplayChar>();
                    var text = Native.FPDFText_LoadPage(page);
                    if (text != IntPtr.Zero)
                    {
                        try
                        {
                            var n = Native.FPDFText_CountChars(text);
                            for (var i = 0; i < n; i++)
                            {
                                var code = Native.FPDFText_GetUnicode(text, i);
                                var c = code is > 0 and < 0x10000 ? (char)code : ' ';
                                if (char.IsWhiteSpace(c) || char.IsControl(c) ||
                                    Native.FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) == 0)
                                {
                                    chars.Add(new DisplayChar(' ', 0, 0, 0, 0));
                                    continue;
                                }

                                Native.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, left, top, out var x1, out var y1);
                                Native.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, right, bottom, out var x2, out var y2);
                                chars.Add(new DisplayChar(c, Math.Min(x1, x2) / (double)k, Math.Min(y1, y2) / (double)k,
                                    Math.Abs(x2 - x1) / (double)k, Math.Abs(y2 - y1) / (double)k));
                            }
                        }
                        finally
                        {
                            Native.FPDFText_ClosePage(text);
                        }
                    }

                    pages.Add(new DisplayPageText(width, height, chars));
                }
                finally
                {
                    Native.FPDF_ClosePage(page);
                }
            }

            return pages;
        });
        if (result is null)
            throw new PdfRenderException(error == 4
                ? $"\"{Path.GetFileName(path)}\" está protegido por senha."
                : $"\"{Path.GetFileName(path)}\" não pôde ser lido. O arquivo pode estar danificado.");
        return result;
    }

    private static RenderedPage? RenderLocked(IntPtr document, IntPtr form, int index, RenderSize size)
    {
        var page = Native.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero) throw new PdfRenderException($"A página {index + 1} não pôde ser renderizada.");
        if (form != IntPtr.Zero) Native.FORM_OnAfterLoadPage(page, form);

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
                    if (form != IntPtr.Zero) Native.FPDF_FFLDraw(form, bitmap, page, 0, 0, width, height, 0, Native.RenderAnnotations);
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
            if (form != IntPtr.Zero) Native.FORM_OnBeforeClosePage(page, form);
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
        public static extern IntPtr FPDF_LoadCustomDocument(ref FileAccess access, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

        [DllImport(Library)]
        public static extern uint FPDF_GetLastError();

        [DllImport(Library)]
        public static extern uint FPDF_GetDocPermissions(IntPtr document);

        [DllImport(Library)]
        public static extern int FPDF_GetSecurityHandlerRevision(IntPtr document);

        [DllImport(Library)]
        public static extern int FPDF_GetFileVersion(IntPtr document, out int version);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WriteBlockCallback(IntPtr self, IntPtr data, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct FileWrite
        {
            public int Version;
            public IntPtr WriteBlock;
        }

        public const uint NoIncremental = 2;

        [DllImport(Library)]
        public static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite writer, uint flags);

        [DllImport(Library)]
        public static extern void FPDFPage_Delete(IntPtr document, int pageIndex);

        [DllImport(Library)]
        public static extern int FPDF_SaveWithVersion(IntPtr document, ref FileWrite writer, uint flags, int fileVersion);

        [DllImport(Library)]
        public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
            double pageX, double pageY, out int deviceX, out int deviceY);

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

        [DllImport(Library)]
        public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

        [DllImport(Library)]
        public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);

        [DllImport(Library)]
        public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);

        [DllImport(Library)]
        public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);

        [DllImport(Library)]
        public static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        /// <summary>
        /// FPDF_FORMFILLINFO versão 1 sem nenhuma função de retorno: o suficiente para desenhar os
        /// campos (o PDFium só chama as funções que estiverem preenchidas). Memória zerada e maior que
        /// a estrutura; liberada depois de FPDFDOC_ExitFormFillEnvironment.
        /// </summary>
        public static IntPtr CreateFormFillInfo()
        {
            const int size = 1024;
            var memory = Marshal.AllocHGlobal(size);
            for (var offset = 0; offset < size; offset += 8) Marshal.WriteInt64(memory, offset, 0);
            Marshal.WriteInt32(memory, 0, 1);
            return memory;
        }

        [DllImport(Library)]
        public static extern IntPtr FPDFText_LoadPage(IntPtr page);

        [DllImport(Library)]
        public static extern void FPDFText_ClosePage(IntPtr textPage);

        [DllImport(Library)]
        public static extern int FPDFText_CountChars(IntPtr textPage);

        [DllImport(Library)]
        public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);

        [DllImport(Library)]
        public static extern double FPDFText_GetFontSize(IntPtr textPage, int index);

        [DllImport(Library)]
        public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
    }
}
