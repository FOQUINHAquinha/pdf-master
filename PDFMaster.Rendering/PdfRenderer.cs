using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

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

/// <summary>Abstração da renderização de páginas (hoje PDFium, via Docnet.Core).</summary>
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
    // O PDFium não é seguro para uso simultâneo; todas as chamadas passam por esta trava.
    private static readonly object Gate = new();

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

        // O arquivo é lido para a memória para não ficar bloqueado enquanto a renderização acontece.
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfRenderException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }

        var dimensions = size.IsFit ? new PageDimensions(size.ShortSide, size.LongSide) : new PageDimensions(size.Scale);

        IDocReader reader;
        lock (Gate)
        {
            try
            {
                reader = DocLib.Instance.GetDocReader(bytes, dimensions);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new PdfRenderException(
                    $"\"{Path.GetFileName(path)}\" não pôde ser renderizado. O arquivo pode estar protegido por senha ou danificado.", ex);
            }
        }

        try
        {
            foreach (var index in pageIndexes)
            {
                ct.ThrowIfCancellationRequested();
                RenderedPage rendered;
                lock (Gate)
                {
                    if (index < 0 || index >= reader.GetPageCount()) continue;
                    try
                    {
                        using var page = reader.GetPageReader(index);
                        var width = page.GetPageWidth();
                        var height = page.GetPageHeight();
                        var pixels = page.GetImage();
                        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) continue;
                        FlattenOnWhite(pixels);
                        rendered = new RenderedPage(width, height, pixels);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        throw new PdfRenderException($"A página {index + 1} não pôde ser renderizada.", ex);
                    }
                }

                onPage(index, rendered);
            }
        }
        finally
        {
            lock (Gate)
            {
                reader.Dispose();
            }
        }
    }, ct);

    /// <summary>O PDFium devolve fundo transparente; aqui a página é assentada sobre branco.</summary>
    private static void FlattenOnWhite(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            if (alpha == 255) continue;
            if (alpha == 0)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
            }
            else
            {
                var inverse = 255 - alpha;
                bgra[i] = (byte)((bgra[i] * alpha + 255 * inverse) / 255);
                bgra[i + 1] = (byte)((bgra[i + 1] * alpha + 255 * inverse) / 255);
                bgra[i + 2] = (byte)((bgra[i + 2] * alpha + 255 * inverse) / 255);
            }

            bgra[i + 3] = 255;
        }
    }
}
