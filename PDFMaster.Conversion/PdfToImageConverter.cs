using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Conversion;

public enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
    Tiff,
}

/// <summary>Codificação de imagens pelos codecs do Windows (WIC), sem bibliotecas de terceiros.</summary>
public static class ImageCodec
{
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tif",
        _ => ".png",
    };

    public static BitmapSource FromRendered(RenderedPage page, double dpi, bool grayscale)
    {
        BitmapSource image = BitmapSource.Create(page.Width, page.Height, dpi, dpi, PixelFormats.Bgr32, null,
            page.Bgra, page.Width * 4);
        image = new FormatConvertedBitmap(image, grayscale ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
        image.Freeze();
        return image;
    }

    public static BitmapEncoder CreateEncoder(ImageFormat format, int jpegQuality) => format switch
    {
        ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
        ImageFormat.Bmp => new BmpBitmapEncoder(),
        ImageFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        _ => new PngBitmapEncoder(),
    };

    public static void Save(BitmapSource image, ImageFormat format, int jpegQuality, string path)
    {
        var encoder = CreateEncoder(format, jpegQuality);
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}

/// <param name="Dpi">Resolução da imagem gerada (72 = tamanho natural da página).</param>
/// <param name="Pages">Seleção de páginas ("1-3,7"); vazio = todas.</param>
/// <param name="SingleTiff">Com TIFF, grava todas as páginas em um único arquivo de várias páginas.</param>
public sealed record PdfToImageOptions(
    ImageFormat Format = ImageFormat.Png,
    int Dpi = 150,
    int JpegQuality = 90,
    bool Grayscale = false,
    string? Pages = null,
    bool SingleTiff = false);

/// <summary>PDF → JPG, PNG, BMP ou TIFF. Uma imagem por página (ou um TIFF de várias páginas).</summary>
public sealed class PdfToImageConverter
{
    public const string OperationName = "PDF para imagem";
    public const int MinDpi = 36;
    public const int MaxDpi = 600;

    private readonly IPdfEngine _engine;
    private readonly IPdfRenderer _renderer;
    private readonly OutputPlanner _planner;

    public PdfToImageConverter(IPdfEngine engine, IPdfRenderer renderer, OutputPlanner planner)
    {
        _engine = engine;
        _renderer = renderer;
        _planner = planner;
    }

    public async Task<IReadOnlyList<ProducedFile>> ConvertAsync(
        string path,
        PdfToImageOptions options,
        OutputTarget target,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        ISet<string>? reserved = null)
    {
        if (options.Dpi < MinDpi || options.Dpi > MaxDpi)
            throw new PdfEngineException($"A resolução deve ficar entre {MinDpi} e {MaxDpi} DPI.");

        var info = await _engine.ReadInfoAsync(path, ct).ConfigureAwait(false);
        var pages = PageRangeParser.Parse(options.Pages, info.PageCount);
        if (pages.Count == 0) throw new PdfEngineException("Nenhuma página foi selecionada.");

        reserved ??= OutputPathResolver.CreateReservationSet();
        var sources = new[] { info.Path };
        var baseName = Path.GetFileNameWithoutExtension(info.Path);
        var extension = ImageCodec.Extension(options.Format);
        var scale = RenderSize.ByScale(options.Dpi / 72.0);
        var produced = new List<ProducedFile>();
        var done = 0;

        if (options.Format == ImageFormat.Tiff && options.SingleTiff)
        {
            var (output, overwrite) = _planner.Resolve(baseName + extension, target, OperationName, sources, reserved);
            // As imagens são congeladas (podem trocar de thread); o codificador é criado e usado
            // em uma única thread, como o WPF exige.
            var frames = new List<BitmapSource>(pages.Count);
            await _renderer.RenderPagesAsync(info.Path, pages, scale, (_, rendered) =>
            {
                frames.Add(ImageCodec.FromRendered(rendered, options.Dpi, options.Grayscale));
                progress?.Report(++done / (double)pages.Count * 0.9);
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            var bytes = await Task.Run(() => OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                var encoder = ImageCodec.CreateEncoder(ImageFormat.Tiff, options.JpegQuality);
                foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
                using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(stream);
            }), ct).ConfigureAwait(false);
            progress?.Report(1);
            return [new ProducedFile(output, bytes, pages.Count)];
        }

        var digits = Math.Max(3, info.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        await _renderer.RenderPagesAsync(info.Path, pages, scale, (index, rendered) =>
        {
            var number = (index + 1).ToString(new string('0', digits), CultureInfo.InvariantCulture);
            var (output, overwrite) = _planner.Resolve($"{baseName}_p{number}{extension}", target, OperationName, sources, reserved);
            var image = ImageCodec.FromRendered(rendered, options.Dpi, options.Grayscale);
            var bytes = OutputPlanner.WriteAtomically(output, overwrite,
                temp => ImageCodec.Save(image, options.Format, options.JpegQuality, temp));
            produced.Add(new ProducedFile(output, bytes, 1));
            progress?.Report(++done / (double)pages.Count);
        }, ct).ConfigureAwait(false);

        return produced;
    }
}
