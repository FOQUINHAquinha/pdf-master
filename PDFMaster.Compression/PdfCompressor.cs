using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.PdfEngine;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFMaster.Compression;

public enum CompressionProfile
{
    Lossless,
    Light,
    Balanced,
    Strong,
    Maximum,
    Custom,
}

/// <param name="JpegQuality">Qualidade JPEG (1–100) usada ao regravar imagens; 0 = não regravar imagens.</param>
/// <param name="MaxDpi">Resolução máxima das imagens em relação ao tamanho da página; 0 = não reduzir.</param>
/// <param name="ConvertLosslessImages">Regrava em JPEG também as imagens guardadas sem perda (com perda de qualidade).</param>
/// <param name="Grayscale">Converte as imagens coloridas regravadas para escala de cinza.</param>
/// <param name="RemoveMetadata">Remove título, autor, assunto, palavras-chave e o bloco XMP.</param>
public sealed record CompressionSettings(
    int JpegQuality,
    int MaxDpi,
    bool ConvertLosslessImages,
    bool Grayscale = false,
    bool RemoveMetadata = false)
{
    public static CompressionSettings For(CompressionProfile profile) => profile switch
    {
        CompressionProfile.Lossless => new CompressionSettings(0, 0, false),
        CompressionProfile.Light => new CompressionSettings(85, 220, false),
        CompressionProfile.Strong => new CompressionSettings(60, 110, true),
        CompressionProfile.Maximum => new CompressionSettings(40, 72, true),
        _ => new CompressionSettings(75, 150, true),
    };

    public static string Describe(CompressionProfile profile) => profile switch
    {
        CompressionProfile.Lossless => "Sem perda — só reorganiza e compacta a estrutura; as imagens não são alteradas",
        CompressionProfile.Light => "Leve — imagens em JPEG 85, até 220 DPI",
        CompressionProfile.Balanced => "Equilibrada — imagens em JPEG 75, até 150 DPI",
        CompressionProfile.Strong => "Forte — imagens em JPEG 60, até 110 DPI",
        CompressionProfile.Maximum => "Máxima — imagens em JPEG 40, até 72 DPI",
        _ => "Personalizada",
    };
}

/// <param name="Output">Arquivo gerado; null quando não houve redução (nada é gravado nesse caso).</param>
public sealed record CompressionResult(
    ProducedFile? Output,
    long OriginalBytes,
    long FinalBytes,
    int ImagesFound,
    int ImagesRecompressed,
    bool HadSignatures)
{
    public bool Reduced => Output is not null;
    public long SavedBytes => Math.Max(0, OriginalBytes - FinalBytes);
    public double SavedPercent => OriginalBytes == 0 ? 0 : SavedBytes * 100.0 / OriginalBytes;
}

/// <summary>
/// Redução de tamanho de PDF feita pelo próprio programa (sem Ghostscript): regrava imagens JPEG
/// com menor qualidade, reduz imagens com resolução acima do necessário, compacta streams sem
/// compressão, descarta objetos não utilizados e, opcionalmente, remove metadados.
///
/// Limites: só são regravadas imagens RGB ou em tons de cinza de 8 bits; imagens CMYK, indexadas,
/// de 1 bit, JBIG2/JPEG 2000 e com máscara por cor ficam como estão. O original nunca é alterado.
/// </summary>
public sealed class PdfCompressor
{
    public const string OperationName = "Comprimir PDF";
    private const int MinPixelsToRecompress = 64 * 64;

    private readonly OutputPlanner _planner;

    public PdfCompressor(OutputPlanner planner) => _planner = planner;

    public Task<CompressionResult> CompressAsync(
        string path,
        CompressionSettings settings,
        OutputTarget target,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        ISet<string>? reserved = null) => Task.Run(() =>
    {
        if (settings.JpegQuality is < 0 or > 100) throw new PdfEngineException("A qualidade JPEG deve ficar entre 1 e 100.");
        if (settings.MaxDpi is < 0 or > 1200) throw new PdfEngineException("A resolução máxima deve ficar entre 36 e 1200 DPI.");

        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new PdfEngineException($"Arquivo não encontrado: {full}");
        var originalBytes = new FileInfo(full).Length;
        var name = Path.GetFileNameWithoutExtension(full) + "_comprimido.pdf";
        var (output, overwrite) = _planner.Resolve(name, target, OperationName, [full], reserved);

        PdfDocument document;
        try
        {
            document = PdfReader.Open(full, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                ? "está protegido por senha (remova a proteção antes, em \"Remover proteção\")"
                : "não é um PDF válido ou está danificado";
            throw new PdfEngineException($"{Path.GetFileName(full)} não pôde ser aberto: {reason}.", ex);
        }

        using (document)
        {
            var hadSignatures = false;
            var imagesFound = 0;
            var recompressed = 0;
            long finalBytes = 0;
            try
            {
                hadSignatures = HasSignatures(document);
                var images = CollectImages(document);
                imagesFound = images.Count;
                var index = 0;
                if (settings.JpegQuality > 0)
                {
                    foreach (var (image, usage) in images)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (TryRecompress(image, usage, settings)) recompressed++;
                        progress?.Report(++index / (double)Math.Max(1, images.Count) * 0.8);
                    }
                }

                ct.ThrowIfCancellationRequested();
                CompressPlainStreams(document, ct);
                if (settings.RemoveMetadata) RemoveMetadata(document);

                document.Options.NoCompression = false;
                document.Options.CompressContentStreams = true;
                document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
                progress?.Report(0.9);

                var pageCount = document.PageCount;
                var written = OutputPlanner.WriteAtomically(output, overwrite, temp =>
                {
                    document.Save(temp);
                    finalBytes = new FileInfo(temp).Length;
                    ct.ThrowIfCancellationRequested();
                    // Sem redução, o arquivo não é entregue (seria só uma cópia maior). A exceção faz
                    // o temporário ser apagado antes de chegar ao destino.
                    if (finalBytes >= originalBytes) throw new NoReductionException();
                });

                progress?.Report(1);
                return new CompressionResult(new ProducedFile(output, written, pageCount),
                    originalBytes, finalBytes, imagesFound, recompressed, hadSignatures);
            }
            catch (NoReductionException)
            {
                return new CompressionResult(null, originalBytes, finalBytes, imagesFound, recompressed, hadSignatures);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PdfEngineException("Não foi possível gravar o arquivo comprimido: " + ex.Message, ex);
            }
            catch (Exception ex) when (ex is not PdfEngineException and not OperationCanceledException and not OutOfMemoryException)
            {
                throw new PdfEngineException(
                    $"{Path.GetFileName(full)} não pôde ser comprimido: usa uma estrutura que o programa não conseguiu regravar. Detalhe: {ex.Message}", ex);
            }
        }
    }, ct);

    private sealed class NoReductionException : Exception;

    private sealed class ImageUsage
    {
        /// <summary>Maior página (em pontos) em que a imagem aparece.</summary>
        public double ShortSidePt { get; set; }
        public double LongSidePt { get; set; }
    }

    private static bool HasSignatures(PdfDocument document)
    {
        try
        {
            var form = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            return form is not null && form.Elements.GetInteger("/SigFlags") != 0;
        }
        catch (Exception ex) when (ex is InvalidCastException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>Imagens usadas pelas páginas (inclusive dentro de formulários XObject) e onde aparecem.</summary>
    private static Dictionary<PdfDictionary, ImageUsage> CollectImages(PdfDocument document)
    {
        var images = new Dictionary<PdfDictionary, ImageUsage>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            var box = page.MediaBox;
            var width = Math.Abs(box.Width);
            var height = Math.Abs(box.Height);
            if (width <= 0 || height <= 0) continue;
            Visit(page.Elements.GetDictionary("/Resources"), Math.Min(width, height), Math.Max(width, height), images,
                new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);
        }

        return images;
    }

    private static void Visit(PdfDictionary? resources, double shortSide, double longSide,
        Dictionary<PdfDictionary, ImageUsage> images, HashSet<PdfDictionary> seen, int depth)
    {
        if (resources is null || depth > 6) return;
        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null) return;

        foreach (var key in xobjects.Elements.Keys.ToList())
        {
            if (Resolve(xobjects.Elements[key]) is not PdfDictionary item || item.Stream is null) continue;
            var subtype = item.Elements.GetName("/Subtype");
            if (subtype == "/Image")
            {
                if (!images.TryGetValue(item, out var usage)) images[item] = usage = new ImageUsage();
                usage.ShortSidePt = Math.Max(usage.ShortSidePt, shortSide);
                usage.LongSidePt = Math.Max(usage.LongSidePt, longSide);
            }
            else if (subtype == "/Form" && seen.Add(item))
            {
                Visit(item.Elements.GetDictionary("/Resources"), shortSide, longSide, images, seen, depth + 1);
            }
        }
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static string? SingleFilter(PdfDictionary image)
    {
        var filter = Resolve(image.Elements["/Filter"]);
        if (filter is PdfName name) return name.Value;
        if (filter is PdfArray { Elements.Count: 1 } array && Resolve(array.Elements[0]) is PdfName only) return only.Value;
        return null;
    }

    /// <summary>1 = tons de cinza, 3 = RGB, 0 = espaço de cor que não é regravado.</summary>
    private static int ColorComponents(PdfDictionary image)
    {
        var space = Resolve(image.Elements["/ColorSpace"]);
        if (space is PdfName name)
        {
            return name.Value switch
            {
                "/DeviceRGB" => 3,
                "/DeviceGray" => 1,
                _ => 0,
            };
        }

        if (space is PdfArray { Elements.Count: 2 } array &&
            Resolve(array.Elements[0]) is PdfName { Value: "/ICCBased" } &&
            Resolve(array.Elements[1]) is PdfDictionary profile)
        {
            var n = profile.Elements.GetInteger("/N");
            return n is 1 or 3 ? n : 0;
        }

        return 0;
    }

    private static bool TryRecompress(PdfDictionary image, ImageUsage usage, CompressionSettings settings)
    {
        try
        {
            var stream = image.Stream;
            if (stream is null || stream.Value is null || stream.Value.Length < 2048) return false;
            if (image.Elements.GetBoolean("/ImageMask")) return false;
            if (image.Elements["/Decode"] is not null) return false;
            if (Resolve(image.Elements["/Mask"]) is PdfArray) return false; // transparência por cor: JPEG a destruiria

            var width = image.Elements.GetInteger("/Width");
            var height = image.Elements.GetInteger("/Height");
            if (width <= 0 || height <= 0 || (long)width * height < MinPixelsToRecompress) return false;
            if (image.Elements.GetInteger("/BitsPerComponent") != 8) return false;

            var components = ColorComponents(image);
            if (components == 0) return false;

            BitmapSource source;
            var filter = SingleFilter(image);
            if (filter == "/DCTDecode")
            {
                var decoder = new JpegBitmapDecoder(new MemoryStream(stream.Value),
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
                var decodedGray = source.Format == PixelFormats.Gray8;
                var decodedColor = source.Format == PixelFormats.Bgr24 || source.Format == PixelFormats.Bgr32 ||
                                   source.Format == PixelFormats.Rgb24;
                // CMYK e demais variações ficam como estão.
                if ((components == 1 && !decodedGray) || (components == 3 && !decodedColor)) return false;
            }
            else if (filter == "/FlateDecode" && settings.ConvertLosslessImages)
            {
                var raw = stream.UnfilteredValue;
                var rowBytes = width * components;
                if (raw is null || raw.Length < (long)rowBytes * height) return false;
                source = BitmapSource.Create(width, height, 96, 96,
                    components == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, raw, rowBytes);
            }
            else
            {
                return false;
            }

            // Reduz a imagem quando ela tem mais pixels do que a página precisa na resolução pedida.
            if (settings.MaxDpi > 0 && usage.LongSidePt > 0)
            {
                var maxLong = usage.LongSidePt / 72.0 * settings.MaxDpi;
                var maxShort = usage.ShortSidePt / 72.0 * settings.MaxDpi;
                var scale = Math.Min(maxLong / Math.Max(source.PixelWidth, source.PixelHeight),
                    maxShort / Math.Min(source.PixelWidth, source.PixelHeight));
                if (scale < 0.9)
                {
                    source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                }
            }

            var toGray = components == 1 || settings.Grayscale;
            source = new FormatConvertedBitmap(source, toGray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(settings.JpegQuality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            var jpeg = buffer.ToArray();

            // Só troca se compensar: regravar um JPEG já pequeno poderia aumentá-lo.
            if (jpeg.Length >= stream.Value.Length * 0.95) return false;

            stream.Value = jpeg;
            image.Elements.SetName("/Filter", "/DCTDecode");
            image.Elements.Remove("/DecodeParms");
            image.Elements.SetInteger("/Width", source.PixelWidth);
            image.Elements.SetInteger("/Height", source.PixelHeight);
            image.Elements.SetInteger("/BitsPerComponent", 8);
            image.Elements.SetInteger("/Length", jpeg.Length);
            if (toGray && components == 3) image.Elements.SetName("/ColorSpace", "/DeviceGray");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                       or InvalidOperationException or InvalidCastException or OverflowException
                                       or IOException or System.Runtime.InteropServices.COMException)
        {
            // Imagem que os codecs não entenderam: fica exatamente como estava.
            return false;
        }
    }

    /// <summary>Compacta (Flate) os streams gravados sem nenhuma compressão.</summary>
    private static void CompressPlainStreams(PdfDocument document, CancellationToken ct)
    {
        foreach (var item in document.Internals.GetAllObjects())
        {
            ct.ThrowIfCancellationRequested();
            if (item is not PdfDictionary dictionary || dictionary.Stream?.Value is not { Length: > 256 } raw) continue;
            if (dictionary.Elements["/Filter"] is not null) continue;
            var type = dictionary.Elements.GetName("/Type");
            if (type is "/XRef" or "/ObjStm" or "/Metadata") continue;

            using var buffer = new MemoryStream();
            using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            if (buffer.Length >= raw.Length * 0.9) continue;
            dictionary.Stream!.Value = buffer.ToArray();
            dictionary.Elements.SetName("/Filter", "/FlateDecode");
            dictionary.Elements.SetInteger("/Length", (int)buffer.Length);
        }
    }

    private static void RemoveMetadata(PdfDocument document)
    {
        foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator" })
        {
            document.Info.Elements.Remove(key);
        }

        document.Internals.Catalog.Elements.Remove("/Metadata");
    }
}
