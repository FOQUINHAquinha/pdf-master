using PDFMaster.Compression;
using PDFMaster.Conversion;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.OCR;
using PDFMaster.PdfEngine;
using PDFMaster.Security;

namespace PDFMaster.Batch;

/// <summary>Serviços usados pelas etapas dos fluxos.</summary>
public sealed record FlowServices(
    IPdfEngine Engine,
    PdfOrganizer Organizer,
    PdfCompressor Compressor,
    OcrService Ocr,
    PdfEditor Editor,
    PdfAConverter PdfA,
    PdfMetadata Metadata,
    PdfRepair Repair,
    ImagesToPdfConverter Images,
    TextToPdfConverter Text,
    OfficeToPdfConverter Office,
    PageTools Pages,
    OutputPlanner Planner);

/// <param name="Step">Etapa em andamento (começando em 1); 0 = importação.</param>
/// <param name="Fraction">Andamento do arquivo inteiro (0 a 1).</param>
public sealed record FlowProgress(int Step, int StepCount, string StepName, double Fraction);

/// <param name="Notes">Observações das etapas (ex.: "sem redução de tamanho").</param>
public sealed record FlowRunResult(ProducedFile File, IReadOnlyList<string> Notes);

/// <summary>
/// Executa um fluxo num arquivo: importa (imagem, texto ou Office viram PDF), aplica cada etapa
/// numa pasta temporária e grava o resultado final no destino. O original nunca é alterado e,
/// se algo falhar ou for cancelado, nada fica no destino.
/// </summary>
public sealed class FlowRunner
{
    private static readonly string[] TextExtensions = [".txt"];
    private static readonly object DefaultGate = new();
    private readonly FlowServices _s;

    public FlowRunner(FlowServices services) => _s = services;

    /// <summary>Tipos de arquivo que podem entrar num fluxo.</summary>
    public static bool CanImport(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".pdf" || ImagesToPdfConverter.Extensions.Contains(extension) || TextExtensions.Contains(extension) ||
               OfficeToPdfConverter.OfficeExtensions.Contains(extension) || OfficeToPdfConverter.HtmlExtensions.Contains(extension);
    }

    /// <param name="betweenSteps">Chamado antes de cada etapa (usado para pausar a fila).</param>
    /// <param name="reserveGate">Trava compartilhada entre tarefas paralelas que gravam na mesma pasta.</param>
    public async Task<FlowRunResult> RunAsync(string source, BatchFlow flow, OutputTarget target, IProgress<FlowProgress>? progress = null,
        Func<CancellationToken, Task>? betweenSteps = null, CancellationToken ct = default, ISet<string>? reserved = null, object? reserveGate = null)
    {
        if (flow.Validate() is { } problem) throw new PdfEngineException(problem);
        var original = Path.GetFullPath(source);
        if (!File.Exists(original)) throw new PdfEngineException($"Arquivo não encontrado: {original}");
        if (!CanImport(original)) throw new PdfEngineException($"{Path.GetFileName(original)}: tipo de arquivo não suportado nos fluxos.");

        var name = Path.GetFileNameWithoutExtension(original);
        var work = Path.Combine(Path.GetTempPath(), "PDFMaster", "fluxo-" + Guid.NewGuid().ToString("N"));
        var notes = new List<string>();
        var count = flow.Steps.Count;
        try
        {
            Directory.CreateDirectory(work);
            var current = await ImportAsync(original, name, work, ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                if (betweenSteps is not null) await betweenSteps(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var step = flow.Steps[i];
                var index = i;
                progress?.Report(new FlowProgress(i + 1, count, step.Name, (double)i / count));
                var stepProgress = new Progress<double>(f => progress?.Report(new FlowProgress(index + 1, count, step.Name, (index + Math.Clamp(f, 0, 1)) / count)));
                var folder = Path.Combine(work, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var stepTarget = new OutputTarget(folder, OverwritePolicy.AutoRename, SubfolderMode.None);
                var produced = await RunStepAsync(step, current, stepTarget, stepProgress, notes, ct).ConfigureAwait(false);
                if (produced is null) continue;

                // Nome curto e igual ao original em cada etapa (o campo {arquivo} continua certo).
                var renamed = Path.Combine(folder, name + ".pdf");
                if (!string.Equals(produced, renamed, StringComparison.OrdinalIgnoreCase)) File.Move(produced, renamed, overwrite: true);
                current = renamed;
            }

            ct.ThrowIfCancellationRequested();
            string output;
            bool overwrite;
            lock (reserveGate ?? DefaultGate)
            {
                (output, overwrite) = _s.Planner.Resolve(name + flow.Suffix + ".pdf", target, "Fluxo: " + flow.Name, [original], reserved);
            }

            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.Copy(current, temp, overwrite: true));
            var pages = (await _s.Engine.ReadInfoAsync(output, ct).ConfigureAwait(false)).PageCount;
            progress?.Report(new FlowProgress(count, count, "Concluído", 1));
            return new FlowRunResult(new ProducedFile(output, bytes, pages), notes);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Pasta temporária: o sistema limpa depois.
            }
        }
    }

    private async Task<string> ImportAsync(string original, string name, string work, CancellationToken ct)
    {
        var extension = Path.GetExtension(original).ToLowerInvariant();
        var folder = Path.Combine(work, "0");
        var target = new OutputTarget(folder, OverwritePolicy.AutoRename, SubfolderMode.None);
        if (extension == ".pdf")
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, name + ".pdf");
            File.Copy(original, copy);
            return copy;
        }

        if (ImagesToPdfConverter.Extensions.Contains(extension))
            return (await _s.Images.ConvertAsync([original], name + ".pdf", new ImagesToPdfOptions(), target, null, ct).ConfigureAwait(false)).Path;
        if (TextExtensions.Contains(extension))
            return (await _s.Text.ConvertAsync(original, new TextToPdfOptions(), target, ct).ConfigureAwait(false)).Path;
        return (await _s.Office.ConvertAsync(original, target, OfficeToPdfConverter.OperationName, ct).ConfigureAwait(false)).Path;
    }

    /// <summary>Executa uma etapa; devolve o novo arquivo ou null quando a etapa não mudou nada.</summary>
    private async Task<string?> RunStepAsync(FlowStep step, string current, OutputTarget target, IProgress<double> progress, List<string> notes,
        CancellationToken ct)
    {
        switch (step)
        {
            case OcrStep ocr:
            {
                var options = new OcrOptions(ocr.Languages, ocr.Dpi, SkipPagesWithText: ocr.SkipPagesWithText);
                var result = await _s.Ocr.RecognizeAsync(current, options, target,
                    new Progress<OcrProgress>(p => progress.Report(p.PageCount == 0 ? 0 : (double)p.Page / p.PageCount)), ct).ConfigureAwait(false);
                if (result.PagesRecognized == 0) notes.Add("OCR: nenhuma página precisou de reconhecimento.");
                return result.Files.FirstOrDefault()?.Path;
            }

            case CompressStep compress:
            {
                var result = await _s.Compressor.CompressAsync(current, CompressionSettings.For(compress.Profile), target, progress, ct).ConfigureAwait(false);
                if (result.Output is null) notes.Add("Compressão: o arquivo já estava compacto (sem redução).");
                return result.Output?.Path;
            }

            case PageNumbersStep numbers:
            {
                var options = numbers.Position switch
                {
                    NumberPosition.BottomRight => new HeaderFooterOptions(FooterRight: numbers.Text),
                    NumberPosition.BottomLeft => new HeaderFooterOptions(FooterLeft: numbers.Text, FooterRight: null),
                    NumberPosition.TopCenter => new HeaderFooterOptions(HeaderCenter: numbers.Text, FooterRight: null),
                    NumberPosition.TopRight => new HeaderFooterOptions(HeaderRight: numbers.Text, FooterRight: null),
                    NumberPosition.TopLeft => new HeaderFooterOptions(HeaderLeft: numbers.Text, FooterRight: null),
                    _ => new HeaderFooterOptions(FooterCenter: numbers.Text, FooterRight: null),
                };
                options = options with { Style = DocumentStamps.DefaultHeaderStyle with { Size = numbers.FontSize } };
                return (await _s.Editor.HeaderFooterAsync(current, options, target, ct).ConfigureAwait(false)).File.Path;
            }

            case WatermarkStep mark:
            {
                Rgb.TryParse(mark.Color, out var color);
                var style = DocumentStamps.DefaultWatermarkStyle with { Size = mark.FontSize, Color = color };
                var options = new WatermarkOptions(mark.Text, Style: style, Opacity: mark.OpacityPercent / 100, Rotation: mark.Rotation);
                return (await _s.Editor.WatermarkAsync(current, options, target, ct).ConfigureAwait(false)).File.Path;
            }

            case RotateStep rotate:
            {
                var info = await _s.Engine.ReadInfoAsync(current, ct).ConfigureAwait(false);
                var selected = Pages(rotate.Pages, info.PageCount).ToHashSet();
                var pages = Enumerable.Range(0, info.PageCount)
                    .Select(i => PageInstruction.From(current, i, selected.Contains(i) ? rotate.Degrees : 0)).ToList();
                return (await _s.Organizer.SaveArrangementAsync(pages, Path.GetFileName(current), target, step.Name, ct: ct).ConfigureAwait(false)).Path;
            }

            case DeletePagesStep delete:
            {
                var info = await _s.Engine.ReadInfoAsync(current, ct).ConfigureAwait(false);
                var removed = Pages(delete.Pages, info.PageCount).ToHashSet();
                var keep = Enumerable.Range(0, info.PageCount).Where(i => !removed.Contains(i)).Select(i => PageInstruction.From(current, i)).ToList();
                if (keep.Count == 0) throw new PdfEngineException("A exclusão removeria todas as páginas.");
                return (await _s.Organizer.SaveArrangementAsync(keep, Path.GetFileName(current), target, step.Name, ct: ct).ConfigureAwait(false)).Path;
            }

            case ReverseStep:
                return (await _s.Pages.ReverseAsync(current, target, ct).ConfigureAwait(false)).File?.Path;

            case RemoveBlankPagesStep blank:
            {
                var result = await _s.Pages.RemoveBlankPagesAsync(current, blank.AlsoDuplicates, target, ct).ConfigureAwait(false);
                notes.Add(result.Summary);
                return result.File?.Path;
            }

            case CropStep crop:
                return (await _s.Pages.CropAsync(current, crop.TopMm, crop.BottomMm, crop.LeftMm, crop.RightMm, target, ct).ConfigureAwait(false)).File?.Path;

            case NUpStep nup:
                return (await _s.Pages.NUpAsync(current, nup.PagesPerSheet, nup.Booklet, nup.Sheet, target, ct).ConfigureAwait(false)).File?.Path;

            case RemoveMetadataStep:
                return (await _s.Metadata.RemoveAllAsync(current, target, ct).ConfigureAwait(false)).Path;

            case RepairStep:
                return (await _s.Repair.RepairAsync(current, null, target, ct).ConfigureAwait(false)).File.Path;

            case PdfAStep pdfa:
                try
                {
                    var result = await _s.PdfA.ConvertAsync(current, pdfa.Level, target, externalValidation: true, ct).ConfigureAwait(false);
                    notes.Add(result.Validation.Verdict);
                    return result.File.Path;
                }
                catch (PdfAConversionException ex)
                {
                    throw new PdfEngineException(ex.Message + " " + string.Join(" ", ex.Problems.Take(4)), ex);
                }

            default:
                throw new PdfEngineException($"Etapa desconhecida: {step.GetType().Name}.");
        }
    }

    private static IReadOnlyList<int> Pages(string text, int count)
    {
        try
        {
            return PageRangeParser.Parse(text, count);
        }
        catch (PageRangeException ex)
        {
            throw new PdfEngineException(ex.Message, ex);
        }
    }
}
