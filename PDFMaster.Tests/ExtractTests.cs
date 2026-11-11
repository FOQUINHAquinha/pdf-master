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

/// <summary>Fase 8: converter para tons de cinza.</summary>
public class GrayscaleTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PDFMaster.Compression.GrayscaleConverter _converter =
        new(new OutputPlanner(new PDFMaster.Core.Files.OutputPathResolver()), new PDFMaster.Rendering.PdfiumRenderer());

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), PDFMaster.Domain.OverwritePolicy.AutoRename, PDFMaster.Domain.SubfolderMode.None);

    /// <summary>Texto vermelho, retângulo azul (RGB), retângulo CMYK, imagem PNG verde e foto JPEG colorida.</summary>
    private string Colorful()
    {
        var jpg = ImageSamples.Write(Path.Combine(_temp.Path, "foto.jpg"), 80, 60, (x, y) => (220, (byte)(x * 3), 30));
        var png = ImageSamples.Write(Path.Combine(_temp.Path, "verde.png"), 40, 40, (_, _) => (0, 200, 0));
        var pdf = Path.Combine(_temp.Path, "colorido.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using (var g = XGraphics.FromPdfPage(page))
            {
                g.DrawString("Texto em vermelho", new XFont("Arial", 20), new XSolidBrush(XColor.FromArgb(255, 220, 20, 20)), 60, 80);
                g.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 30, 60, 230)), 60, 120, 200, 80);
                using (var image = XImage.FromFile(png)) g.DrawImage(image, 300, 120, 100, 100);
                using (var photo = XImage.FromFile(jpg)) g.DrawImage(photo, 60, 260, 240, 180);
            }

            document.Save(pdf);
        }

        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            // CMYK (magenta puro) num stream separado, com espaço de cor por nome.
            document.Pages[0].Contents.AppendContent().CreateStream("q 0 1 0 0 k 60 500 200 60 re f Q\nq /DeviceRGB cs 1 0.5 0 sc 300 500 100 60 re f Q\n"u8.ToArray());
            document.Save(pdf);
        }

        return pdf;
    }

    private static (byte R, byte G, byte B) Pixel(PDFMaster.Rendering.RenderedPage page, double xPt, double yPt)
    {
        var x = (int)(xPt / 595 * page.Width);
        var y = (int)(yPt / 842 * page.Height);
        var i = 4 * (y * page.Width + x);
        return (page.Bgra[i + 2], page.Bgra[i + 1], page.Bgra[i]);
    }

    [Fact]
    public async Task Converts_text_drawings_and_images_and_keeps_text_selectable()
    {
        var pdf = Colorful();
        var before = File.ReadAllBytes(pdf);
        var result = await _converter.ConvertAsync(pdf, Target);

        Assert.EndsWith("colorido_cinza.pdf", result.File.Path);
        Assert.Equal(before, File.ReadAllBytes(pdf));
        Assert.Empty(result.PagesStillInColor);
        Assert.True(result.ColorOperators >= 3, $"operadores: {result.ColorOperators}");
        Assert.Equal(2, result.Images);
        Assert.Contains("Texto em vermelho", string.Concat(PDFMaster.Rendering.PdfiumRenderer.ReadDisplayText(result.File.Path)[0].Chars.Select(c => c.Value)));

        var page = await new PDFMaster.Rendering.PdfiumRenderer().RenderPageAsync(result.File.Path, 0, PDFMaster.Rendering.RenderSize.Fit(595, 842));
        // Azul (30, 60, 230) → luminância ≈ 70; verde (0, 200, 0) → ≈ 117; magenta CMYK → (255, 0, 255) ≈ 105.
        var blue = Pixel(page, 160, 160);
        Assert.True(blue.R == blue.G && blue.G == blue.B && Math.Abs(blue.R - 70) <= 3, $"azul virou {blue}");
        var green = Pixel(page, 350, 170);
        Assert.True(Math.Abs(green.R - green.G) <= 2 && Math.Abs(green.G - green.B) <= 2 && Math.Abs(green.R - 117) <= 4, $"verde virou {green}");
        var magenta = Pixel(page, 160, 842 - 530);
        Assert.True(magenta.R == magenta.G && Math.Abs(magenta.R - 105) <= 3, $"magenta virou {magenta}");
        var orange = Pixel(page, 350, 842 - 530);
        Assert.True(orange.R == orange.G && orange.G == orange.B, $"laranja virou {orange}");
    }

    [Fact]
    public async Task Reports_pages_that_still_have_color_and_refuses_invalid_files()
    {
        var pdf = Path.Combine(_temp.Path, "especial.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.Save(pdf);
        }

        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            // Cor de separação ("tinta especial") com alternativa RGB: não é convertida e deve ser apontada.
            var page = document.Pages[0];
            var function = new PdfDictionary(document);
            function.Elements.SetInteger("/FunctionType", 2);
            var domain = new PdfArray(document);
            domain.Elements.Add(new PdfInteger(0));
            domain.Elements.Add(new PdfInteger(1));
            function.Elements["/Domain"] = domain;
            var c1 = new PdfArray(document);
            c1.Elements.Add(new PdfReal(1));
            c1.Elements.Add(new PdfReal(0));
            c1.Elements.Add(new PdfReal(0));
            function.Elements["/C1"] = c1;
            function.Elements.SetReal("/N", 1);
            var separation = new PdfArray(document);
            separation.Elements.Add(new PdfName("/Separation"));
            separation.Elements.Add(new PdfName("/Vermelho"));
            separation.Elements.Add(new PdfName("/DeviceRGB"));
            separation.Elements.Add(function);
            var spaces = new PdfDictionary(document);
            spaces.Elements["/CS1"] = separation;
            var resources = page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(document);
            resources.Elements["/ColorSpace"] = spaces;
            page.Elements["/Resources"] = resources;
            page.Contents.AppendContent().CreateStream("/CS1 cs 1 sc 100 100 300 300 re f\n"u8.ToArray());
            document.Save(pdf);
        }

        var result = await _converter.ConvertAsync(pdf, Target);
        Assert.Equal(new[] { 1 }, result.PagesStillInColor);
        Assert.Contains(result.Notes, n => n.Contains("separação", StringComparison.Ordinal));

        var bad = Path.Combine(_temp.Path, "ruim.pdf");
        File.WriteAllText(bad, "não é PDF");
        await Assert.ThrowsAsync<PdfEngineException>(() => _converter.ConvertAsync(bad, Target));
    }
}
