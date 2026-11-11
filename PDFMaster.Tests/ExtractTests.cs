using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.Conversion;
using PDFMaster.PdfEngine;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Fase 8: extrair conteúdo.</summary>
public class ExtractTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ContentExtractor _extractor = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>PDF com uma foto JPEG, uma imagem PNG (gravada sem perda), texto, um anexo e título.</summary>
    private string Sample()
    {
        var jpg = ImageSamples.Write(Path.Combine(_temp.Path, "foto.jpg"), 64, 48, (x, y) => ((byte)(x * 4), (byte)(y * 5), 120));
        var png = ImageSamples.Write(Path.Combine(_temp.Path, "grafico.png"), 30, 20, (x, y) => (x < 15 ? (byte)255 : (byte)0, 0, 0));
        var pdf = Path.Combine(_temp.Path, "relatorio.pdf");
        using (var document = new PdfDocument())
        {
            document.Info.Title = "Relatório anual";
            document.Info.Author = "Setor de Notas";
            for (var i = 0; i < 2; i++)
            {
                var page = document.AddPage();
                using var g = XGraphics.FromPdfPage(page);
                g.DrawString($"Página de teste {i + 1}", new XFont("Arial", 14), XBrushes.Black, 72, 72);
                using (var photo = XImage.FromFile(jpg)) g.DrawImage(photo, 72, 100, 128, 96);
                if (i == 0)
                {
                    using var chart = XImage.FromFile(png);
                    g.DrawImage(chart, 72, 300, 60, 40);
                }
            }

            document.Save(pdf);
        }

        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            var data = new PdfDictionary(document);
            data.CreateStream(Encoding.UTF8.GetBytes("<nota>dados</nota>"));
            document.Internals.AddObject(data);
            var ef = new PdfDictionary(document);
            ef.Elements.SetReference("/F", data);
            var spec = new PdfDictionary(document);
            spec.Elements.SetName("/Type", "/Filespec");
            spec.Elements.SetString("/F", @"..\..\Windows\dados.xml");
            spec.Elements["/EF"] = ef;
            document.Internals.AddObject(spec);
            var pairs = new PdfArray(document);
            pairs.Elements.Add(new PdfString("dados.xml"));
            pairs.Elements.Add(spec.Reference!);
            var tree = new PdfDictionary(document);
            tree.Elements["/Names"] = pairs;
            var names = new PdfDictionary(document);
            names.Elements["/EmbeddedFiles"] = tree;
            document.Internals.Catalog.Elements["/Names"] = names;
            document.Save(pdf);
        }

        return pdf;
    }

    [Fact]
    public async Task Extracts_images_text_attachments_fonts_and_metadata()
    {
        var pdf = Sample();
        var before = File.ReadAllBytes(pdf);
        var output = Path.Combine(_temp.Path, "saida");
        var result = await _extractor.ExtractAsync(pdf, new ExtractOptions(Fonts: true), output);

        Assert.Equal(Path.Combine(output, "relatorio_conteudo"), result.Folder);
        Assert.Equal(before, File.ReadAllBytes(pdf));

        // A foto (repetida nas duas páginas) é gravada uma vez, como JPEG original; o PNG vira PNG.
        Assert.Equal(2, result.Images);
        var jpg = Assert.Single(Directory.GetFiles(Path.Combine(result.Folder, "imagens"), "*.jpg"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_temp.Path, "foto.jpg")).Take(2), File.ReadAllBytes(jpg).Take(2));
        var png = Assert.Single(Directory.GetFiles(Path.Combine(result.Folder, "imagens"), "*.png"));
        var image = ImageSamples.Read(png);
        Assert.Equal((30, 20), (image.Width, image.Height));
        Assert.True(image.Bgra[2] > 200 && image.Bgra[1] < 50); // vermelho à esquerda
        Assert.True(image.Bgra[4 * 25 + 2] < 50); // preto à direita

        var text = File.ReadAllText(Path.Combine(result.Folder, "texto.txt"));
        Assert.Contains("Página de teste 1", text);
        Assert.Contains("===== Página 2 =====", text);
        Assert.Equal(2, result.TextPages);

        // Nome do anexo sem pastas: nada é gravado fora da pasta de saída.
        Assert.Equal(1, result.Attachments);
        Assert.Equal("<nota>dados</nota>", File.ReadAllText(Path.Combine(result.Folder, "anexos", "dados.xml")));
        Assert.Single(Directory.GetFiles(_temp.Path, "dados.xml", SearchOption.AllDirectories));

        Assert.True(result.Fonts >= 1);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(result.Folder, "fontes"), "*.ttf"));
        var meta = File.ReadAllText(Path.Combine(result.Folder, "metadados.txt"));
        Assert.Contains("Título: Relatório anual", meta);
        Assert.Contains("Autor: Setor de Notas", meta);

        // Segunda extração: pasta nova, nada é sobrescrito.
        var again = await _extractor.ExtractAsync(pdf, new ExtractOptions(), output);
        Assert.Equal(result.Folder + " (2)", again.Folder);
    }

    [Fact]
    public async Task Rejects_empty_choice_protected_and_invalid_files()
    {
        var pdf = Sample();
        await Assert.ThrowsAsync<PdfEngineException>(() => _extractor.ExtractAsync(pdf, new ExtractOptions(false, false, false, false, false), _temp.Path));
        var bad = Path.Combine(_temp.Path, "ruim.pdf");
        File.WriteAllText(bad, "não é pdf");
        await Assert.ThrowsAsync<PdfEngineException>(() => _extractor.ExtractAsync(bad, new ExtractOptions(), _temp.Path));
        Assert.Equal("_CON.txt", ContentExtractor.SafeName("CON.txt"));
        Assert.Equal("a_b.txt", ContentExtractor.SafeName("a:b.txt"));
        Assert.Equal("passwd", ContentExtractor.SafeName("/etc/passwd"));
    }
}
