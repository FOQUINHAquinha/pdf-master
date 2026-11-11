using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.Core.Pdf;
using PDFMaster.PdfEngine;

namespace PDFMaster.Editor;

/// <summary>Resultado de uma gravação do editor.</summary>
/// <param name="InPlace">Textos existentes trocados no próprio PDF.</param>
/// <param name="Overlaid">Textos existentes cobertos e reescritos por cima.</param>
/// <param name="Notes">Avisos para a pessoa (o que mudou o layout, o que não foi aplicado).</param>
public sealed record EditResult(ProducedFile File, int Applied, int InPlace, int Overlaid, IReadOnlyList<string> Notes);

/// <summary>
/// Grava as edições num novo PDF (o original nunca é alterado): texto, imagens, assinaturas,
/// anotações, carimbos, campos de formulário, marca d'água, cabeçalho e rodapé.
/// </summary>
public sealed class PdfEditor
{
    public const string OperationName = "Editar PDF";
    public const string WatermarkOperation = "Marca d'água";
    public const string HeaderFooterOperation = "Cabeçalho e rodapé";
    public const string FormOperation = "Preencher formulário";

    private readonly OutputPlanner _planner;

    public PdfEditor(OutputPlanner planner) => _planner = planner;

    public Task<EditResult> SaveAsync(string source, IReadOnlyList<EditItem> items, OutputTarget target,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        if (items.Count == 0) throw new PdfEngineException("Nenhuma alteração para gravar.");
        return Run(source, "_editado", OperationName, target, reserved, ct, document =>
        {
            var notes = new List<string>();
            var (inPlace, overlaid) = Apply(document, items, notes, ct);
            return (items.Count, inPlace, overlaid, notes);
        });
    }

    public Task<EditResult> WatermarkAsync(string source, WatermarkOptions options, OutputTarget target,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        if (options.Image is null && string.IsNullOrWhiteSpace(options.Text))
            throw new PdfEngineException("Digite o texto da marca d'água ou escolha uma imagem.");
        return Run(source, "_marca-dagua", WatermarkOperation, target, reserved, ct, document =>
        {
            var pages = Pages(options.Pages, document.PageCount);
            DocumentStamps.ApplyWatermark(document, pages, options);
            return (pages.Count, 0, 0, new List<string>());
        });
    }

    public Task<EditResult> HeaderFooterAsync(string source, HeaderFooterOptions options, OutputTarget target,
        CancellationToken ct = default, ISet<string>? reserved = null, DateTime? now = null)
    {
        if (options.IsEmpty) throw new PdfEngineException("Preencha pelo menos um texto de cabeçalho ou rodapé.");
        return Run(source, "_cabecalho-rodape", HeaderFooterOperation, target, reserved, ct, document =>
        {
            var pages = Pages(options.Pages, document.PageCount);
            DocumentStamps.ApplyHeaderFooter(document, pages, options, Path.GetFileNameWithoutExtension(source), now ?? DateTime.Now);
            return (pages.Count, 0, 0, new List<string>());
        });
    }

    public Task<EditResult> FillFormAsync(string source, IReadOnlyDictionary<string, string?> values, OutputTarget target,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        return Run(source, "_preenchido", FormOperation, target, reserved, ct, document =>
        {
            if (FormFields.Read(document).Count == 0) throw new PdfEngineException("Este PDF não tem campos de formulário.");
            var hadXfa = FormFields.HasXfa(document);
            var skipped = FormFields.Fill(document, values);
            var notes = new List<string>();
            if (skipped.Count > 0) notes.Add($"{skipped.Count} campo(s) somente leitura ou não preenchíveis foram mantidos: {string.Join(", ", skipped.Take(5))}.");
            if (hadXfa) notes.Add("O formulário tinha também uma versão XFA, que foi removida para que os valores apareçam em todos os leitores.");
            return (values.Count - skipped.Count, 0, 0, notes);
        });
    }

    private static IReadOnlyList<int> Pages(string? range, int count)
    {
        try
        {
            return string.IsNullOrWhiteSpace(range) ? Enumerable.Range(0, count).ToList() : PageRangeParser.Parse(range, count);
        }
        catch (PageRangeException ex)
        {
            throw new PdfEngineException(ex.Message, ex);
        }
    }

    private Task<EditResult> Run(string source, string suffix, string operation, OutputTarget target, ISet<string>? reserved,
        CancellationToken ct, Func<PdfDocument, (int Applied, int InPlace, int Overlaid, List<string> Notes)> edit)
    {
        var full = Path.GetFullPath(source);
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + suffix + ".pdf", target, operation, [full], reserved);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var document = Open(full);
            var (applied, inPlace, overlaid, notes) = edit(document);
            var pageCount = document.PageCount;
            ct.ThrowIfCancellationRequested();
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                ct.ThrowIfCancellationRequested();
                document.Save(temp);
            });
            return new EditResult(new ProducedFile(output, bytes, pageCount), applied, inPlace, overlaid, notes);
        }, ct);
    }

    private static PdfDocument Open(string path)
    {
        if (!File.Exists(path)) throw new PdfEngineException($"Arquivo não encontrado: \"{path}\".");
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException(
                $"Não foi possível abrir \"{Path.GetFileName(path)}\" para edição. O arquivo pode estar protegido por senha ou danificado.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(path)}\" não pôde ser interpretado como PDF; o arquivo pode estar danificado.", ex);
        }
    }

    /// <summary>Aplica as edições ao documento aberto. Público para testes.</summary>
    public static (int InPlace, int Overlaid) Apply(PdfDocument document, IReadOnlyList<EditItem> items, List<string> notes, CancellationToken ct = default)
    {
        var inPlace = 0;
        var overlaid = 0;
        foreach (var group in items.GroupBy(i => i.PageIndex).OrderBy(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (group.Key < 0 || group.Key >= document.PageCount)
                throw new PdfEngineException($"Página {group.Key + 1} não existe no documento.");
            var page = document.Pages[group.Key];
            var frame = PageFrame.Of(page);
            var pageItems = group.ToList();

            // 1) Texto existente: primeiro tenta trocar no próprio PDF.
            var overlays = new List<ReplaceTextItem>();
            foreach (var replace in pageItems.OfType<ReplaceTextItem>())
            {
                if (ExistingText.TryReplace(page, replace.Run.Text, replace.NewText, out var reason))
                {
                    inPlace++;
                    if (Math.Abs(replace.NewText.Length - replace.Run.Text.Length) > 0)
                        notes.Add($"Página {group.Key + 1}: \"{Short(replace.NewText)}\" foi trocado no próprio PDF, com a mesma fonte; como o tamanho mudou, o espaço até o trecho seguinte da linha pode ficar diferente.");
                }
                else if (replace.AllowOverlay)
                {
                    overlays.Add(replace);
                    overlaid++;
                    notes.Add($"Página {group.Key + 1}: \"{Short(replace.Run.Text)}\" não pôde ser trocado no próprio PDF ({reason}) e foi coberto por um retângulo branco com o texto novo por cima. O texto antigo continua no arquivo, por baixo: não use isso para esconder informações.");
                }
                else
                {
                    notes.Add($"Página {group.Key + 1}: \"{Short(replace.Run.Text)}\" não foi alterado: {reason}");
                }
            }

            // 2) Conteúdo novo da página (texto, imagens, sobreposições, rótulos de botões de opção).
            var drawsContent = pageItems.Any(i => i is TextItem or ImageItem || i is FieldItem { Field: FieldKind.Radio }) || overlays.Count > 0;
            if (drawsContent)
            {
                var form = PdfObjects.Draw(document, frame.Width, frame.Height, gfx =>
                {
                    foreach (var overlay in overlays)
                    {
                        var cover = overlay.Run.Rect.Inflate(1);
                        gfx.DrawRectangle(XBrushes.White, cover.X, cover.Y, cover.Width, cover.Height);
                        var style = overlay.Style;
                        var top = overlay.Run.Rect.Y + (overlay.Run.Rect.Height - Drawing.Font(style).GetHeight()) / 2;
                        Drawing.Text(gfx, new ViewRect(overlay.Run.Rect.X, top, Math.Max(overlay.Run.Rect.Width, 4) * 4, overlay.Run.Rect.Height * 2),
                            overlay.NewText.Replace('\n', ' '), style);
                    }

                    foreach (var item in pageItems)
                    {
                        switch (item)
                        {
                            case TextItem text:
                                Drawing.Text(gfx, text.Rect, text.Text, text.Style);
                                break;
                            case ImageItem image:
                                Drawing.Image(gfx, image);
                                break;
                            case FieldItem { Field: FieldKind.Radio } radio:
                                var buttons = radio.RadioButtons();
                                var options = radio.Options.Count > 0 ? radio.Options : ["Opção 1", "Opção 2"];
                                for (var i = 0; i < buttons.Count && i < options.Count; i++)
                                {
                                    var b = buttons[i];
                                    var style = new TextStyle("Arial", Math.Clamp(b.Height * 0.85, 6, 14), Rgb.Black);
                                    var font = Drawing.Font(style);
                                    gfx.DrawString(options[i], font, XBrushes.Black, b.Right + 4, b.Center.Y - font.GetHeight() / 2, XStringFormats.TopLeft);
                                }

                                break;
                        }
                    }
                });

                PdfObjects.Place(document, page, [(form, frame.FormToPdf(new ViewRect(0, 0, frame.Width, frame.Height)), false)]);
            }

            // 3) Anotações e campos, na ordem em que foram criados.
            foreach (var item in pageItems)
            {
                switch (item)
                {
                    case MarkupItem or ShapeItem or NoteItem or TextBoxItem or StampItem:
                        Annotations.Add(document, page, frame, item);
                        break;
                    case FieldItem field:
                        FormFields.Create(document, page, frame, field);
                        break;
                }
            }
        }

        return (inPlace, overlaid);
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
