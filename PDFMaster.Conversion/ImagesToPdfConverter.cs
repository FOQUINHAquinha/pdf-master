using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.PdfEngine;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFMaster.Conversion;

public enum PdfPageSizeMode
{
    /// <summary>A página fica do tamanho da imagem (pixels ÷ DPI da imagem).</summary>
    FitImage,
    A4,
    Letter,
}

public enum PdfPageOrientation
{
    /// <summary>Retrato ou paisagem conforme o formato de cada imagem.</summary>
    Auto,
    Portrait,
    Landscape,
}

/// <param name="MarginMm">Margem em milímetros em volta da imagem.</param>
/// <param name="JpegQuality">
/// Quando informado, imagens que não são JPEG e não têm transparência são gravadas em JPEG com
/// esta qualidade (arquivo menor, com perda). Null mantém todas sem perda.
/// </param>
public sealed record ImagesToPdfOptions(
    PdfPageSizeMode PageSize = PdfPageSizeMode.FitImage,
    PdfPageOrientation Orientation = PdfPageOrientation.Auto,
    double MarginMm = 0,
    int? JpegQuality = null);

/// <summary>
/// JPG, PNG, BMP, TIFF (inclusive de várias páginas) e GIF → PDF. Arquivos JPEG são incorporados
/// como estão, sem recompressão. A orientação EXIF das fotos é respeitada.
/// </summary>
public sealed class ImagesToPdfConverter
{
    public const string OperationName = "Imagem para PDF";
    private const double PointsPerMm = 72.0 / 25.4;

    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"];

    private readonly OutputPlanner _planner;

    public ImagesToPdfConverter(OutputPlanner planner) => _planner = planner;

    public Task<ProducedFile> ConvertAsync(
        IReadOnlyList<string> images,
        string fileName,
        ImagesToPdfOptions options,
        OutputTarget target,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        ISet<string>? reserved = null) => Task.Run(() =>
    {
        if (images.Count == 0) throw new PdfEngineException("Adicione pelo menos uma imagem.");
        if (options.MarginMm < 0 || options.MarginMm > 100)
            throw new PdfEngineException("A margem deve ficar entre 0 e 100 mm.");

        var name = string.IsNullOrWhiteSpace(fileName) ? "imagens" : fileName.Trim();
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
        var (output, overwrite) = _planner.Resolve(name, target, OperationName, images, reserved);

        var streams = new List<Stream>();
        try
        {
            using var document = new PdfDocument();
            document.Info.Creator = "PDF Master";
            var pageCount = 0;

            for (var i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var picture in Load(images[i], options.JpegQuality))
                {
                    ct.ThrowIfCancellationRequested();
                    streams.Add(picture.Data);
                    AddPage(document, picture, options);
                    pageCount++;
                }

                progress?.Report((i + 1) / (double)images.Count * 0.9);
            }

            if (pageCount == 0) throw new PdfEngineException("Nenhuma imagem pôde ser lida.");

            ct.ThrowIfCancellationRequested();
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => document.Save(temp));
            progress?.Report(1);
            return new ProducedFile(output, bytes, pageCount);
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }, ct);

    private static void AddPage(PdfDocument document, Picture picture, ImagesToPdfOptions options)
    {
        var margin = options.MarginMm * PointsPerMm;
        var quarterTurn = picture.Rotation is 90 or 270;
        // Tamanho natural da imagem já orientada, em pontos.
        var naturalWidth = (quarterTurn ? picture.PixelHeight / picture.DpiY : picture.PixelWidth / picture.DpiX) * 72.0;
        var naturalHeight = (quarterTurn ? picture.PixelWidth / picture.DpiX : picture.PixelHeight / picture.DpiY) * 72.0;

        double pageWidth, pageHeight;
        if (options.PageSize == PdfPageSizeMode.FitImage)
        {
            pageWidth = naturalWidth + 2 * margin;
            pageHeight = naturalHeight + 2 * margin;
        }
        else
        {
            var (shortSide, longSide) = options.PageSize == PdfPageSizeMode.A4 ? (595.276, 841.89) : (612.0, 792.0);
            var landscape = options.Orientation switch
            {
                PdfPageOrientation.Portrait => false,
                PdfPageOrientation.Landscape => true,
                _ => naturalWidth > naturalHeight,
            };
            (pageWidth, pageHeight) = landscape ? (longSide, shortSide) : (shortSide, longSide);
        }

        // O PDF limita o lado da página a 14400 pontos.
        pageWidth = Math.Clamp(pageWidth, 3, 14400);
        pageHeight = Math.Clamp(pageHeight, 3, 14400);

        var availableWidth = Math.Max(1, pageWidth - 2 * margin);
        var availableHeight = Math.Max(1, pageHeight - 2 * margin);
        var scale = Math.Min(availableWidth / naturalWidth, availableHeight / naturalHeight);
        var drawWidth = naturalWidth * scale;
        var drawHeight = naturalHeight * scale;

        var page = document.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(pageWidth, pageHeight));

        using var graphics = XGraphics.FromPdfPage(page);
        using var image = XImage.FromStream(picture.Data);
        if (picture.Rotation == 0)
        {
            graphics.DrawImage(image, (pageWidth - drawWidth) / 2, (pageHeight - drawHeight) / 2, drawWidth, drawHeight);
        }
        else
        {
            // Gira em torno do centro da página; antes da rotação, a imagem tem os lados trocados.
            var (unrotatedWidth, unrotatedHeight) = quarterTurn ? (drawHeight, drawWidth) : (drawWidth, drawHeight);
            graphics.TranslateTransform(pageWidth / 2, pageHeight / 2);
            graphics.RotateTransform(picture.Rotation);
            graphics.DrawImage(image, -unrotatedWidth / 2, -unrotatedHeight / 2, unrotatedWidth, unrotatedHeight);
        }
    }

    /// <summary>Lê um arquivo de imagem; TIFF e GIF podem ter vários quadros (um por página).</summary>
    private static IEnumerable<Picture> Load(string path, int? jpegQuality)
    {
        var name = Path.GetFileName(path);
        byte[] bytes;
        BitmapDecoder decoder;
        try
        {
            bytes = File.ReadAllBytes(path);
            decoder = BitmapDecoder.Create(new MemoryStream(bytes),
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            throw new PdfEngineException($"{name} não pôde ser lido como imagem: {ex.Message}", ex);
        }

        if (decoder.Frames.Count == 0) throw new PdfEngineException($"{name} não contém imagem.");

        var isJpeg = decoder is JpegBitmapDecoder;
        for (var f = 0; f < decoder.Frames.Count; f++)
        {
            var frame = decoder.Frames[f];
            var dpiX = frame.DpiX is > 1 and < 10000 ? frame.DpiX : 96;
            var dpiY = frame.DpiY is > 1 and < 10000 ? frame.DpiY : 96;

            if (isJpeg)
            {
                var (rotation, mirrored) = ReadExifOrientation(frame);
                if (!mirrored)
                {
                    // JPEG incorporado como está: sem recompressão e sem perda adicional.
                    yield return new Picture(new MemoryStream(bytes), frame.PixelWidth, frame.PixelHeight, dpiX, dpiY, rotation);
                    continue;
                }
            }

            yield return Encode(frame, dpiX, dpiY, isJpeg ? 92 : jpegQuality, isJpeg ? ReadExifOrientation(frame) : (0, false));
        }
    }

    private static Picture Encode(BitmapSource frame, double dpiX, double dpiY, int? jpegQuality, (int Rotation, bool Mirrored) exif)
    {
        BitmapSource source = frame;
        if (exif.Mirrored)
        {
            source = new TransformedBitmap(source, new ScaleTransform(-1, 1));
        }

        var hasAlpha = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 ||
                       source.Format == PixelFormats.Rgba64 || source.Format == PixelFormats.Prgba64 ||
                       source.Format == PixelFormats.Rgba128Float || source.Format == PixelFormats.Prgba128Float ||
                       (source.Format == PixelFormats.Indexed8 && source.Palette is { } palette && palette.Colors.Any(c => c.A < 255));
        var isGray = source.Format == PixelFormats.Gray8 || source.Format == PixelFormats.Gray16 ||
                     source.Format == PixelFormats.BlackWhite || source.Format == PixelFormats.Gray2 ||
                     source.Format == PixelFormats.Gray4;

        BitmapEncoder encoder;
        if (jpegQuality is { } quality && !hasAlpha)
        {
            source = new FormatConvertedBitmap(source, isGray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        }
        else
        {
            var target = hasAlpha ? PixelFormats.Bgra32 : isGray ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            source = new FormatConvertedBitmap(source, target, null, 0);
            encoder = new PngBitmapEncoder();
        }

        var data = new MemoryStream();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(data);
        data.Position = 0;
        return new Picture(data, source.PixelWidth, source.PixelHeight, dpiX, dpiY, exif.Rotation);
    }

    /// <summary>Orientação EXIF → rotação em graus (sentido horário) e se a imagem é espelhada.</summary>
    private static (int Rotation, bool Mirrored) ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
            {
                return orientation switch
                {
                    2 => (0, true),
                    3 => (180, false),
                    4 => (180, true),
                    5 => (90, true),
                    6 => (90, false),
                    7 => (270, true),
                    8 => (270, false),
                    _ => (0, false),
                };
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException
                                       or IOException or System.Runtime.InteropServices.COMException)
        {
            // Sem metadados legíveis: a imagem é usada como está.
        }

        return (0, false);
    }

    private sealed record Picture(Stream Data, int PixelWidth, int PixelHeight, double DpiX, double DpiY, int Rotation);
}
