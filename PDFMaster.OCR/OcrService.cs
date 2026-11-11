using System.Text;
using PDFMaster.Conversion;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFMaster.OCR;

/// <param name="Languages">Códigos separados por "+", ex.: "por+eng".</param>
/// <param name="Dpi">Resolução em que cada página é renderizada para o reconhecimento.</param>
/// <param name="AutoRotate">Detecta páginas deitadas ou de cabeça para baixo e corrige a orientação.</param>
/// <param name="Deskew">Detecta e compensa pequenas inclinações (até ±7°) antes de reconhecer.</param>
/// <param name="SkipPagesWithText">Não reconhece páginas que já têm texto pesquisável.</param>
/// <param name="Pages">Seleção de páginas ("1-3,7"); vazio = todas.</param>
public sealed record OcrOptions(
    string Languages = "por",
    int Dpi = 300,
    bool AutoRotate = true,
    bool Deskew = true,
    bool EnhanceContrast = true,
    bool Denoise = false,
    bool SkipPagesWithText = true,
    string? Pages = null,
    bool SearchablePdf = true,
    bool TextFile = false,
    bool Docx = false);

/// <param name="Page">Posição da página em andamento (começando em 1) entre as selecionadas.</param>
public sealed record OcrProgress(int Page, int PageCount);

public sealed record OcrResult(
    IReadOnlyList<ProducedFile> Files,
    int PagesRecognized,
    int PagesSkipped,
    int PagesRotated,
    int Words,
    double MeanConfidence);

/// <summary>
/// OCR de documentos: PDF digitalizado ou imagem → PDF pesquisável (a página original é mantida
/// e recebe uma camada de texto invisível), TXT ou DOCX.
///
/// O ajuste de contraste, a remoção de ruído e a correção de inclinação valem para a imagem
/// usada no reconhecimento; a aparência da página no PDF de saída não muda. A correção de
/// orientação, essa sim, gira a página no resultado.
/// </summary>
public sealed class OcrService
{
    public const string OperationName = "OCR";
    public const int MinDpi = 100;
    public const int MaxDpi = 600;
    private const int MinLettersToSkip = 16;

    private readonly OcrRuntime _runtime;
    private readonly IPdfEngine _engine;
    private readonly IPdfRenderer _renderer;
    private readonly ImagesToPdfConverter _images;
    private readonly OutputPlanner _planner;

    public OcrService(OcrRuntime runtime, IPdfEngine engine, IPdfRenderer renderer, ImagesToPdfConverter images, OutputPlanner planner)
    {
        _runtime = runtime;
        _engine = engine;
        _renderer = renderer;
        _images = images;
        _planner = planner;
    }

    public static bool IsImage(string path) =>
        ImagesToPdfConverter.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<OcrResult> RecognizeAsync(
        string path,
        OcrOptions options,
        OutputTarget target,
        IProgress<OcrProgress>? progress = null,
        CancellationToken ct = default,
        ISet<string>? reserved = null)
    {
        if (options.Dpi < MinDpi || options.Dpi > MaxDpi)
            throw new PdfEngineException($"A resolução do OCR deve ficar entre {MinDpi} e {MaxDpi} DPI.");
        if (!options.SearchablePdf && !options.TextFile && !options.Docx)
            throw new PdfEngineException("Escolha pelo menos um tipo de resultado: PDF pesquisável, TXT ou DOCX.");

        var original = Path.GetFullPath(path);
        if (!File.Exists(original)) throw new PdfEngineException($"Arquivo não encontrado: {original}");
        if (!IsImage(original)) return await RecognizePdfAsync(original, original, options, target, progress, ct, reserved).ConfigureAwait(false);

        // Imagem: vira primeiro um PDF temporário de uma página por quadro, com a imagem intacta.
        var work = Path.Combine(Path.GetTempPath(), "PDFMaster", "ocr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var temp = await _images.ConvertAsync([original], "imagem", new ImagesToPdfOptions(),
                new OutputTarget(work, OverwritePolicy.AutoRename, SubfolderMode.None), null, ct).ConfigureAwait(false);
            return await RecognizePdfAsync(temp.Path, original, options with { SkipPagesWithText = false }, target, progress, ct, reserved)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temporário que não pôde ser apagado agora; fica na pasta temporária do Windows.
            }
        }
    }

    private sealed record PageLayer(int PageIndex, IReadOnlyList<OcrWord> Words, Affine ToRender, int RenderWidth, int RenderHeight, int Orientation);

    /// <param name="pdfPath">PDF que será lido (o original ou o temporário criado a partir de uma imagem).</param>
    /// <param name="namedAfter">Arquivo que dá nome aos resultados e que nunca pode ser sobrescrito.</param>
    private async Task<OcrResult> RecognizePdfAsync(
        string pdfPath,
        string namedAfter,
        OcrOptions options,
        OutputTarget target,
        IProgress<OcrProgress>? progress,
        CancellationToken ct,
        ISet<string>? reserved)
    {
        var info = await _engine.ReadInfoAsync(pdfPath, ct).ConfigureAwait(false);
        var selected = PageRangeParser.Parse(options.Pages, info.PageCount).Distinct().OrderBy(p => p).ToList();
        if (selected.Count == 0) throw new PdfEngineException("Nenhuma página foi selecionada.");

        // Texto que o documento já tem, para pular páginas que não precisam de OCR.
        IReadOnlyList<string> existing = [];
        if (options.SkipPagesWithText)
        {
            try
            {
                existing = await Task.Run(() => PdfTextExporter.ReadPages(pdfPath, ct), ct).ConfigureAwait(false);
            }
            catch (PdfEngineException)
            {
                // Sem conseguir ler o texto existente, todas as páginas passam pelo OCR.
            }
        }

        var pageTexts = new Dictionary<int, string>();
        var toRecognize = new List<int>();
        foreach (var index in selected)
        {
            var text = index < existing.Count ? existing[index] : string.Empty;
            if (options.SkipPagesWithText && text.Count(char.IsLetterOrDigit) >= MinLettersToSkip) pageTexts[index] = text;
            else toRecognize.Add(index);
        }

        var skipped = selected.Count - toRecognize.Count;
        if (toRecognize.Count == 0 && !options.TextFile && !options.Docx)
        {
            throw new PdfEngineException(
                $"{Path.GetFileName(namedAfter)} já tem texto pesquisável em todas as páginas selecionadas; não há o que reconhecer. " +
                "Desmarque \"pular páginas que já têm texto\" para forçar o OCR.");
        }

        var layers = new List<PageLayer>();
        var confidenceSum = 0.0;
        if (toRecognize.Count > 0)
        {
            using var session = await Task.Run(() => new OcrSession(_runtime, options.Languages), ct).ConfigureAwait(false);
            var done = 0;
            progress?.Report(new OcrProgress(skipped + 1, selected.Count));
            await _renderer.RenderPagesAsync(pdfPath, toRecognize, RenderSize.ByScale(options.Dpi / 72.0), (index, rendered) =>
            {
                ct.ThrowIfCancellationRequested();
                var layer = RecognizePage(session, index, rendered, options, out var text);
                layers.Add(layer);
                pageTexts[index] = text.Text;
                confidenceSum += text.MeanConfidence;
                done++;
                progress?.Report(new OcrProgress(Math.Min(selected.Count, skipped + done + 1), selected.Count));
            }, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        var words = layers.Sum(l => l.Words.Count);
        var rotated = layers.Count(l => l.Orientation != 0);
        var baseName = Path.GetFileNameWithoutExtension(namedAfter) + "_ocr";
        var sources = new[] { namedAfter };
        var files = new List<ProducedFile>();

        if (options.SearchablePdf && layers.Count > 0)
        {
            var (output, overwrite) = _planner.Resolve(baseName + ".pdf", target, OperationName, sources, reserved);
            var bytes = await Task.Run(() => WriteSearchablePdf(pdfPath, namedAfter, layers, output, overwrite, ct), ct).ConfigureAwait(false);
            files.Add(new ProducedFile(output, bytes, info.PageCount));
        }

        var orderedTexts = selected.Select(index => pageTexts.GetValueOrDefault(index, string.Empty)).ToList();
        if (options.TextFile)
        {
            var (output, overwrite) = _planner.Resolve(baseName + ".txt", target, OperationName, sources, reserved);
            var content = string.Join("\r\n\r\n", orderedTexts.Select(t => t.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim())) + "\r\n";
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.WriteAllText(temp, content, new UTF8Encoding(true)));
            files.Add(new ProducedFile(output, bytes, selected.Count));
        }

        if (options.Docx)
        {
            var (output, overwrite) = _planner.Resolve(baseName + ".docx", target, OperationName, sources, reserved);
            var bytes = OutputPlanner.WriteAtomically(output, overwrite,
                temp => DocxWriter.Write(temp, orderedTexts.Select(PdfTextExporter.Paragraphs).ToList()));
            files.Add(new ProducedFile(output, bytes, selected.Count));
        }

        return new OcrResult(files, layers.Count, skipped, rotated, words, layers.Count == 0 ? 0 : confidenceSum / layers.Count);
    }

    private static PageLayer RecognizePage(OcrSession session, int index, RenderedPage rendered, OcrOptions options, out OcrPageText text)
    {
        var page = GrayImage.FromBgra(rendered.Width, rendered.Height, rendered.Bgra);
        if (options.EnhanceContrast) page = page.StretchContrast();
        if (options.Denoise) page = page.Median3();

        (OcrPageText Text, Affine ToRender) Attempt(int orientation)
        {
            var image = page.Rotate90(orientation);
            var toRender = OcrGeometry.Orientation(orientation, page.Width, page.Height);
            if (options.Deskew)
            {
                var angle = image.EstimateSkew();
                if (angle != 0)
                {
                    toRender = toRender.After(OcrGeometry.Deskew(angle, image.Width, image.Height));
                    image = image.Rotate(angle);
                }
            }

            return (session.Recognize(image, options.Dpi), toRender);
        }

        var best = Attempt(0);
        var bestOrientation = 0;

        // A página é considerada bem orientada quando a maior parte do que foi lido é confiável.
        // Caso contrário, as outras três orientações são testadas e fica a que lê melhor.
        var upright = best.Text.GoodWords >= 5 && best.Text.GoodWords * 2 >= best.Text.Words.Count;
        if (options.AutoRotate && !upright)
        {
            var baseScore = best.Text.Score;
            foreach (var orientation in new[] { 90, 180, 270 })
            {
                var attempt = Attempt(orientation);
                if (attempt.Text.Score >= 2 && attempt.Text.Score > baseScore * 1.5 && attempt.Text.Score > best.Text.Score)
                {
                    best = attempt;
                    bestOrientation = orientation;
                }
            }
        }

        text = best.Text;
        return new PageLayer(index, best.Text.Words, best.ToRender, rendered.Width, rendered.Height, bestOrientation);
    }

    private static long WriteSearchablePdf(string pdfPath, string namedAfter, List<PageLayer> layers, string output, bool overwrite, CancellationToken ct)
    {
        PdfDocument document;
        try
        {
            document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PdfEngineException(
                $"{Path.GetFileName(namedAfter)} não pôde ser aberto para gravar a camada de texto. O arquivo pode estar protegido por senha ou danificado.", ex);
        }

        using (document)
        {
            try
            {
                var font = PdfTextLayer.CreateFont(document);
                foreach (var layer in layers)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = document.Pages[layer.PageIndex];
                    var box = page.Elements.ContainsKey("/CropBox") ? page.Elements.GetRectangle("/CropBox") : page.MediaBox;
                    var x0 = Math.Min(box.X1, box.X2);
                    var y0 = Math.Min(box.Y1, box.Y2);
                    var width = Math.Abs(box.X2 - box.X1);
                    var height = Math.Abs(box.Y2 - box.Y1);
                    if (width <= 0 || height <= 0) continue;

                    var rotation = ((page.Rotate % 360) + 360) % 360;
                    if (layer.Words.Count > 0)
                    {
                        var toPage = OcrGeometry.RenderToPage(rotation, x0, y0, width, height, layer.RenderWidth, layer.RenderHeight)
                            .After(layer.ToRender);
                        PdfTextLayer.AddToPage(document, page, PdfTextLayer.BuildContent(layer.Words, toPage), font);
                    }

                    // A camada está no sistema de coordenadas da página, que não depende de /Rotate;
                    // por isso basta ajustar a rotação para a página aparecer em pé.
                    if (layer.Orientation != 0) page.Rotate = (rotation + layer.Orientation) % 360;
                }

                ct.ThrowIfCancellationRequested();
                return OutputPlanner.WriteAtomically(output, overwrite, temp => document.Save(temp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PdfEngineException("Não foi possível gravar o PDF pesquisável: " + ex.Message, ex);
            }
            catch (Exception ex) when (ex is not PdfEngineException and not OperationCanceledException and not OutOfMemoryException)
            {
                throw new PdfEngineException(
                    $"{Path.GetFileName(namedAfter)} usa uma estrutura que o programa não conseguiu regravar com a camada de texto. Detalhe: {ex.Message}", ex);
            }
        }
    }
}
