using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Batch;

/// <summary>Resultado de uma ferramenta de páginas.</summary>
/// <param name="File">Arquivo gerado; null quando não havia nada a mudar (nada é gravado).</param>
/// <param name="Summary">O que foi feito, em uma frase.</param>
public sealed record PageToolResult(ProducedFile? File, string Summary, IReadOnlyList<int> RemovedPages);

/// <summary>
/// Ferramentas de páginas usadas pelos fluxos e por telas próprias: inverter ordem, remover páginas
/// em branco ou repetidas, recortar margens e montar várias páginas por folha ou livreto.
/// </summary>
public sealed class PageTools
{
    public const string ReverseOperation = "Inverter ordem das páginas";
    public const string BlankOperation = "Remover páginas em branco";
    public const string CropOperation = "Recortar margens";
    public const string NUpOperation = "Várias páginas por folha";
    private const double PointsPerMm = 72.0 / 25.4;

    private readonly IPdfEngine _engine;
    private readonly PdfOrganizer _organizer;
    private readonly IPdfRenderer _renderer;
    private readonly OutputPlanner _planner;

    public PageTools(IPdfEngine engine, PdfOrganizer organizer, IPdfRenderer renderer, OutputPlanner planner)
    {
        _engine = engine;
        _organizer = organizer;
        _renderer = renderer;
        _planner = planner;
    }

    public async Task<PageToolResult> ReverseAsync(string source, OutputTarget target, CancellationToken ct = default)
    {
        var info = await _engine.ReadInfoAsync(source, ct).ConfigureAwait(false);
        var pages = Enumerable.Range(0, info.PageCount).Reverse().Select(i => PageInstruction.From(info.Path, i)).ToList();
        var file = await _organizer.SaveArrangementAsync(pages, Path.GetFileNameWithoutExtension(source) + "_invertido.pdf", target, ReverseOperation, ct: ct)
            .ConfigureAwait(false);
        return new PageToolResult(file, $"{info.PageCount} página(s) na ordem inversa.", []);
    }

    /// <summary>
    /// Remove páginas sem nenhum conteúdo visível e sem texto (e, se pedido, páginas idênticas a uma
    /// anterior: mesmo tamanho, mesmo texto e mesma imagem renderizada).
    /// </summary>
    public async Task<PageToolResult> RemoveBlankPagesAsync(string source, bool alsoDuplicates, OutputTarget target, CancellationToken ct = default)
    {
        var info = await _engine.ReadInfoAsync(source, ct).ConfigureAwait(false);
        IReadOnlyList<DisplayPageText> text;
        try
        {
            text = await Task.Run(() => PdfiumRenderer.ReadDisplayText(info.Path, ct), ct).ConfigureAwait(false);
        }
        catch (PdfRenderException ex)
        {
            throw new PdfEngineException(ex.Message, ex);
        }

        var blank = new List<int>();
        var duplicates = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await _renderer.RenderPagesAsync(info.Path, Enumerable.Range(0, info.PageCount).ToList(), RenderSize.Fit(120, 170), (index, page) =>
        {
            var hasText = index < text.Count && text[index].Chars.Any(c => !char.IsWhiteSpace(c.Value));
            long dark = 0;
            var gray = new byte[page.Width * page.Height];
            for (var i = 0; i < gray.Length; i++)
            {
                var value = (page.Bgra[4 * i] + page.Bgra[4 * i + 1] + page.Bgra[4 * i + 2]) / 3;
                gray[i] = (byte)(value >> 4);
                if (value < 235) dark++;
            }

            if (!hasText && dark <= gray.Length / 1000)
            {
                lock (blank) blank.Add(index);
                return;
            }

            if (!alsoDuplicates) return;
            var words = index < text.Count ? new string(text[index].Chars.Select(c => c.Value).ToArray()) : string.Empty;
            var key = $"{page.Width}x{page.Height}|{Convert.ToHexString(SHA256.HashData(gray))}|{words}";
            lock (seen)
            {
                if (!seen.Add(key)) duplicates.Add(index);
            }
        }, ct).ConfigureAwait(false);

        var removed = blank.Concat(duplicates).Distinct().Order().ToList();
        if (removed.Count == 0)
            return new PageToolResult(null, alsoDuplicates ? "Nenhuma página em branco ou repetida encontrada." : "Nenhuma página em branco encontrada.", []);
        if (removed.Count == info.PageCount) throw new PdfEngineException("Todas as páginas estão em branco: nada foi gravado.");

        var keep = Enumerable.Range(0, info.PageCount).Except(removed).Select(i => PageInstruction.From(info.Path, i)).ToList();
        var file = await _organizer.SaveArrangementAsync(keep, Path.GetFileNameWithoutExtension(source) + "_sem-brancas.pdf", target, BlankOperation, ct: ct)
            .ConfigureAwait(false);
        var parts = new List<string>();
        if (blank.Count > 0) parts.Add($"{blank.Count} em branco");
        if (duplicates.Count > 0) parts.Add($"{duplicates.Count} repetida(s)");
        return new PageToolResult(file, $"Removidas {string.Join(" e ", parts)}: página(s) {string.Join(", ", removed.Select(r => r + 1))}.",
            removed.Select(r => r + 1).ToList());
    }

    /// <summary>Recorta as margens da página exibida (a área visível passa a ser menor; nada é apagado do arquivo).</summary>
    public Task<PageToolResult> CropAsync(string source, double topMm, double bottomMm, double leftMm, double rightMm, OutputTarget target,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(source);
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + "_recortado.pdf", target, CropOperation, [full], reserved);
        return Task.Run(() =>
        {
            using var document = Open(full, PdfDocumentOpenMode.Modify);
            double t = topMm * PointsPerMm, b = bottomMm * PointsPerMm, l = leftMm * PointsPerMm, r = rightMm * PointsPerMm;
            for (var i = 0; i < document.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.Pages[i];
                var frame = PageFrame.Of(page);
                if (l + r > frame.Width - 36 || t + b > frame.Height - 36)
                    throw new PdfEngineException($"As margens são maiores que a página {i + 1} ({frame.Width / PointsPerMm:0} × {frame.Height / PointsPerMm:0} mm).");
                var box = frame.PdfRect(new ViewRect(l, t, frame.Width - l - r, frame.Height - t - b));
                page.Elements["/CropBox"] = new PdfArray(document, box.Select(v => (PdfItem)new PdfReal(Math.Round(v, 3))).ToArray());
            }

            var pages = document.PageCount;
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, document.Save);
            return new PageToolResult(new ProducedFile(output, bytes, pages), $"Margens recortadas em {pages} página(s).", []);
        }, ct);
    }

    /// <summary>
    /// Monta 2 ou 4 páginas por folha, ou um livreto (folhas dobradas ao meio, 2 páginas por lado na
    /// ordem de dobra, completando com páginas em branco até um múltiplo de 4).
    /// </summary>
    public Task<PageToolResult> NUpAsync(string source, int perSheet, bool booklet, SheetSize sheet, OutputTarget target,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        if (!booklet && perSheet is not (2 or 4)) throw new PdfEngineException("Escolha 2 ou 4 páginas por folha.");
        var full = Path.GetFullPath(source);
        var suffix = booklet ? "_livreto" : $"_{perSheet}-por-folha";
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + suffix + ".pdf", target, NUpOperation, [full], reserved);
        return Task.Run(() =>
        {
            int count;
            List<(double Width, double Height, int Rotation)> frames;
            using (var probe = Open(full, PdfDocumentOpenMode.Import))
            {
                count = probe.PageCount;
                frames = Enumerable.Range(0, count).Select(i =>
                {
                    var p = probe.Pages[i];
                    var rotation = ((p.Elements.GetInteger("/Rotate") % 360) + 360) % 360;
                    var media = p.MediaBox;
                    return (media.Width, media.Height, rotation);
                }).ToList();
            }

            // Ordem das páginas em cada lado de folha (-1 = em branco).
            var slots = new List<int[]>();
            if (booklet)
            {
                var total = (count + 3) / 4 * 4;
                int P(int i) => i < count ? i : -1;
                for (var s = 0; s < total / 4; s++)
                {
                    slots.Add([P(total - 1 - 2 * s), P(2 * s)]);
                    slots.Add([P(2 * s + 1), P(total - 2 - 2 * s)]);
                }
            }
            else
            {
                for (var i = 0; i < count; i += perSheet) slots.Add(Enumerable.Range(i, perSheet).Select(p => p < count ? p : -1).ToArray());
            }

            var (paperW, paperH) = sheet switch
            {
                SheetSize.Letter => (612.0, 792.0),
                SheetSize.A3 => (842.0, 1191.0),
                _ => (595.0, 842.0),
            };
            var twoUp = booklet || perSheet == 2;
            double sheetW = twoUp ? paperH : paperW, sheetH = twoUp ? paperW : paperH;
            int columns = 2, rows = twoUp ? 1 : 2;
            const double margin = 18;

            // O PDFsharp já "endireita" as páginas com /Rotate ao importá-las como formulário, mas recorta
            // o resultado pela caixa original. Por isso o desenho usa uma cópia temporária sem /Rotate e
            // a rotação é aplicada aqui, do mesmo jeito que um leitor de PDF exibe a página.
            var unrotated = full;
            if (frames.Any(f => f.Rotation != 0))
            {
                unrotated = Path.Combine(Path.GetTempPath(), "PDFMaster", "nup-" + Guid.NewGuid().ToString("N") + ".pdf");
                Directory.CreateDirectory(Path.GetDirectoryName(unrotated)!);
                using var copy = Open(full, PdfDocumentOpenMode.Modify);
                foreach (var page in copy.Pages) page.Elements.Remove("/Rotate");
                copy.Save(unrotated);
            }

            try
            {
                return Compose(unrotated);
            }
            finally
            {
                if (!ReferenceEquals(unrotated, full)) TryDelete(unrotated);
            }

            PageToolResult Compose(string drawingSource)
            {
                using var form = XPdfForm.FromFile(drawingSource);
                using var outputDocument = new PdfDocument();
                foreach (var side in slots)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = outputDocument.AddPage();
                    page.Width = XUnit.FromPoint(sheetW);
                    page.Height = XUnit.FromPoint(sheetH);
                    using var gfx = XGraphics.FromPdfPage(page);
                    double cellW = (sheetW - 2 * margin) / columns, cellH = (sheetH - 2 * margin) / rows;
                    for (var k = 0; k < side.Length; k++)
                    {
                        if (side[k] < 0) continue;
                        var (w, h, rotation) = frames[side[k]];
                        var turned = rotation is 90 or 270;
                        double shownW = turned ? h : w, shownH = turned ? w : h;
                        var scale = Math.Min((cellW - 8) / shownW, (cellH - 8) / shownH);
                        double cx = margin + (k % columns + 0.5) * cellW, cy = margin + (k / columns + 0.5) * cellH;
                        form.PageIndex = side[k];
                        var state = gfx.Save();
                        gfx.TranslateTransform(cx, cy);
                        if (rotation != 0) gfx.RotateTransform(rotation);
                        gfx.DrawImage(form, -w * scale / 2, -h * scale / 2, w * scale, h * scale);
                        gfx.Restore(state);
                    }
                }

                var sheets = outputDocument.PageCount;
                var bytes = OutputPlanner.WriteAtomically(output, overwrite, outputDocument.Save);
                var summary = booklet
                    ? $"Livreto com {sheets / 2} folha(s) ({sheets} lados) para imprimir frente e verso e dobrar ao meio."
                    : $"{count} página(s) em {sheets} folha(s), {perSheet} por folha.";
                return new PageToolResult(new ProducedFile(output, bytes, sheets), summary, []);
            }
        }, ct);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Arquivo temporário: o sistema limpa depois.
        }
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(path)}\" não pôde ser aberto: pode estar protegido por senha ou danificado.", ex);
        }
    }
}
