using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using Xunit;
using Sheet = DocumentFormat.OpenXml.Spreadsheet;

namespace PDFMaster.Tests;

/// <summary>Fase 8: PDF para Excel e PowerPoint, conferidos com o validador oficial do Open XML.</summary>
public class OfficeExportTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfToOfficeConverter _converter = new(new PdfiumRenderer(), new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private string Table()
    {
        var path = Path.Combine(_temp.Path, "tabela.pdf");
        using var document = new PdfDocument();
        for (var p = 0; p < 2; p++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var g = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 11);
            string[][] rows =
            [
                ["Item", "Quantidade", "Valor (R$)"],
                ["Caneta azul", "10", "1.234,56"],
                ["Papel A4", "3", "45,90"],
                ["Grampeador", "1", "-12,5"],
            ];
            for (var r = 0; r < rows.Length; r++)
            {
                g.DrawString(rows[r][0], font, XBrushes.Black, 60, 100 + r * 20);
                g.DrawString(rows[r][1], font, XBrushes.Black, 250, 100 + r * 20);
                g.DrawString(rows[r][2], font, XBrushes.Black, 400, 100 + r * 20);
            }

            g.DrawString($"Página {p + 1}", font, XBrushes.Black, 60, 400);
        }

        document.Save(path);
        return path;
    }

    private static void Valid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator().Validate(package).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10).Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }

    [Fact]
    public async Task Excel_has_one_sheet_per_page_with_columns_and_numbers()
    {
        var file = await _converter.ConvertAsync(Table(), OfficeFormat.Excel, Target);
        Assert.EndsWith("tabela.xlsx", file.Path);
        using var workbook = SpreadsheetDocument.Open(file.Path, false);
        Valid(workbook);
        var sheets = workbook.WorkbookPart!.Workbook.Sheets!.Elements<Sheet.Sheet>().ToList();
        Assert.Equal(new[] { "Página 1", "Página 2" }, sheets.Select(s => s.Name!.Value));

        var part = (WorksheetPart)workbook.WorkbookPart.GetPartById(sheets[0].Id!);
        string? Cell(string reference)
        {
            var cell = part.Worksheet.Descendants<Sheet.Cell>().FirstOrDefault(c => c.CellReference == reference);
            return cell?.InlineString?.Text?.Text ?? cell?.CellValue?.Text;
        }

        Assert.Equal("Item", Cell("A1"));
        Assert.Equal("Quantidade", Cell("B1"));
        Assert.Equal("Valor (R$)", Cell("C1"));
        Assert.Equal("Caneta azul", Cell("A2"));
        Assert.Equal("10", Cell("B2"));
        Assert.Equal("1234.56", Cell("C2"));
        Assert.Equal("-12.5", Cell("C4"));
        Assert.Equal("Página 1", Cell("A5"));
    }

    [Fact]
    public async Task PowerPoint_has_one_slide_per_page_with_the_page_image()
    {
        var file = await _converter.ConvertAsync(Table(), OfficeFormat.PowerPoint, Target, dpi: 96);
        Assert.EndsWith("tabela.pptx", file.Path);
        using var presentation = PresentationDocument.Open(file.Path, false);
        Valid(presentation);
        var slides = presentation.PresentationPart!.SlideParts.ToList();
        Assert.Equal(2, slides.Count);
        Assert.All(slides, s => Assert.Single(s.ImageParts));
        var size = presentation.PresentationPart.Presentation.SlideSize!;
        Assert.Equal(595 * 12700, size.Cx!.Value);
        Assert.Equal(842 * 12700, size.Cy!.Value);
    }

    [Fact]
    public async Task Pages_without_text_cannot_go_to_excel()
    {
        var path = Path.Combine(_temp.Path, "vazio.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.Save(path);
        }

        await Assert.ThrowsAsync<PdfEngineException>(() => _converter.ConvertAsync(path, OfficeFormat.Excel, Target));
        Assert.True(PdfToOfficeConverter.TryNumber("1.234.567,89", out var big) && Math.Abs(big - 1234567.89) < 1e-6);
        Assert.False(PdfToOfficeConverter.TryNumber("12/05/2026", out _));
        Assert.False(PdfToOfficeConverter.TryNumber("CPF 123", out _));
        Assert.Equal("AA", XlsxWriter.Column(26));
    }
}
