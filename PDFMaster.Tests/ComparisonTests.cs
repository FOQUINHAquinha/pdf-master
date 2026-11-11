using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PDFMaster.Comparison;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using Xunit;

namespace PDFMaster.Tests;

public class ComparisonTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Doc(string name, params string[][] pages) => SecuritySamples.Pages(Path.Combine(_temp.Path, name), pages);

    [Fact]
    public async Task A_document_compared_with_itself_has_no_differences()
    {
        var a = Doc("a.pdf", ["Primeira linha", "Segunda linha"], ["Outra pagina"]);
        var result = await DocumentComparer.CompareAsync(a, a);
        Assert.True(result.Identical);
        Assert.Equal(2, result.Pairs.Count);
        Assert.All(result.Pairs, p => Assert.Equal(0, p.VisualDifference));
        Assert.True(result.TextAvailable);
    }

    [Fact]
    public async Task Changed_added_and_removed_words_are_found_where_they_are()
    {
        var a = Doc("a.pdf", ["O comprador paga cem reais", "Clausula segunda permanece", "Linha que sera apagada"]);
        var b = Doc("b.pdf", ["O comprador paga duzentos reais", "Clausula segunda permanece", "Linha nova no final"]);
        var result = await DocumentComparer.CompareAsync(a, b);

        var changed = Assert.Single(result.Changes, c => c.Kind == ChangeKind.Modified && c.OldText == "cem");
        Assert.Equal("duzentos", changed.NewText);
        Assert.Equal(0, changed.PageA);
        Assert.Equal(0, changed.PageB);
        // Primeira linha: y = 760 no PDF → cerca de 82 pt do topo da página exibida.
        var box = Assert.Single(changed.BoxesA);
        Assert.InRange(box.Y, 60, 90);
        Assert.InRange(box.X, 150, 300);
        Assert.Contains(result.Changes, c => c.Kind == ChangeKind.Modified && c.OldText.Contains("sera apagada", StringComparison.Ordinal) &&
                                             c.NewText.Contains("nova no final", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Changes, c => c.OldText.Contains("segunda", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inserted_and_removed_pages_are_aligned()
    {
        var a = Doc("a.pdf", ["Capitulo um sobre o contrato"], ["Capitulo dois sobre o pagamento"], ["Capitulo tres sobre a entrega"]);
        var b = Doc("b.pdf", ["Capitulo um sobre o contrato"], ["Pagina totalmente nova com anexos diversos"], ["Capitulo dois sobre o pagamento"]);
        var result = await DocumentComparer.CompareAsync(a, b);

        var inserted = Assert.Single(result.Changes, c => c.Kind == ChangeKind.PageInserted);
        Assert.Equal(1, inserted.PageB);
        var removed = Assert.Single(result.Changes, c => c.Kind == ChangeKind.PageRemoved);
        Assert.Equal(2, removed.PageA);
        Assert.Contains(result.Pairs, p => p.PageA == 1 && p.PageB == 2);
        Assert.DoesNotContain(result.Changes, c => c.Kind is ChangeKind.Added or ChangeKind.Removed or ChangeKind.Modified);
    }

    [Fact]
    public async Task Drawings_and_images_show_up_as_visual_and_image_differences()
    {
        var a = Doc("a.pdf", ["Texto igual nos dois"]);
        var b = Doc("b.pdf", ["Texto igual nos dois"]);
        var red = ImageSamples.Write(Path.Combine(_temp.Path, "vermelho.png"), 30, 30, (_, _) => (255, 0, 0));
        var blue = ImageSamples.Write(Path.Combine(_temp.Path, "azul.png"), 30, 30, (_, _) => (0, 0, 255));
        foreach (var (path, image) in new[] { (a, red), (b, blue) })
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            using (var graphics = XGraphics.FromPdfPage(document.Pages[0], XGraphicsPdfPageOptions.Append))
            using (var picture = XImage.FromFile(image))
            {
                graphics.DrawImage(picture, 300, 400, 100, 100);
                if (path == b) graphics.DrawRectangle(XBrushes.Black, new XRect(60, 600, 80, 40));
            }

            document.Save(path);
        }

        var result = await DocumentComparer.CompareAsync(a, b);
        Assert.Single(result.Changes, c => c.Kind == ChangeKind.Images);
        var visual = result.Changes.Where(c => c.Kind == ChangeKind.Visual).SelectMany(c => c.BoxesB).ToList();
        Assert.Contains(visual, v => v.X < 320 && v.Right > 380 && v.Y < 420 && v.Bottom > 480);
        Assert.Contains(visual, v => v.X < 80 && v.Right > 120 && v.Y < 620 && v.Bottom > 630);
        Assert.DoesNotContain(result.Changes, c => c.Kind is ChangeKind.Added or ChangeKind.Removed or ChangeKind.Modified);
        Assert.True(result.Pairs.Single().VisualDifference > 0);
    }

    [Fact]
    public async Task The_html_report_lists_every_difference()
    {
        var a = Doc("contrato v1.pdf", ["Valor de cem reais"]);
        var b = Doc("contrato v2.pdf", ["Valor de duzentos reais"]);
        var result = await DocumentComparer.CompareAsync(a, b);
        var report = await new ComparisonReport(new OutputPlanner(new OutputPathResolver()))
            .WriteAsync(result, new OutputTarget(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None));

        Assert.EndsWith("contrato v1_x_contrato v2_comparacao.html", report.Path, StringComparison.Ordinal);
        var html = File.ReadAllText(report.Path);
        Assert.Contains("<span class=\"del\">cem</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ins\">duzentos</span>", html, StringComparison.Ordinal);
        Assert.Contains("Trechos de texto alterados</td><td>1</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_can_be_cancelled()
    {
        var a = Doc("a.pdf", ["Um"], ["Dois"], ["Tres"]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentComparer.CompareAsync(a, a, ct: cts.Token));
    }
}
