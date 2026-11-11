using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>PDFs com texto em Helvetica (fonte padrão, WinAnsi), escritos operador a operador.</summary>
public static class EditorSamples
{
    public static string TextPdf(string path, int rotate = 0, int pages = 1, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (lines.Length == 0) lines = ["Contrato numero 12345", "Valor total: R$ 1.500,00"];
        using var document = new PdfDocument();
        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        document.Internals.AddObject(font);

        for (var p = 0; p < pages; p++)
        {
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(595, 842));
            var resources = new PdfDictionary(document);
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font.Reference!;
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;

            var content = new StringBuilder();
            var y = 760;
            foreach (var line in lines)
            {
                content.Append($"BT /F1 18 Tf 72 {y} Td {Pdf.Literal(Pdf.WinAnsi(line))} Tj ET\n");
                y -= 40;
            }

            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
            if (rotate != 0) page.Rotate = rotate;
        }

        document.Save(path);
        return path;
    }

    /// <summary>PDF com texto em comando TJ (com ajuste de espaçamento no meio da palavra).</summary>
    public static string TjPdf(string path)
    {
        TextPdf(path, lines: ["Linha de cima"]);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        document.Pages[0].Contents.AppendContent().CreateStream("BT /F1 18 Tf 72 600 Td [(Contr) -20 (ato 777)] TJ ET\n"u8.ToArray());
        document.Save(path);
        return path;
    }

    public static byte[] SolidPng(string folder, string name, Func<int, int, (byte R, byte G, byte B)> pixel, int size = 40) =>
        File.ReadAllBytes(ImageSamples.Write(Path.Combine(folder, name), size, size, pixel));

    public static (byte R, byte G, byte B) Pixel(RenderedPage page, double x, double y)
    {
        var px = Math.Clamp((int)x, 0, page.Width - 1);
        var py = Math.Clamp((int)y, 0, page.Height - 1);
        var i = (py * page.Width + px) * 4;
        return (page.Bgra[i + 2], page.Bgra[i + 1], page.Bgra[i]);
    }

    /// <summary>Quantos pixels da área satisfazem a condição.</summary>
    public static int Count(RenderedPage page, ViewRect area, Func<(byte R, byte G, byte B), bool> test)
    {
        var count = 0;
        for (var y = (int)Math.Max(0, area.Y); y < Math.Min(page.Height, area.Bottom); y++)
        {
            for (var x = (int)Math.Max(0, area.X); x < Math.Min(page.Width, area.Right); x++)
            {
                if (test(Pixel(page, x, y))) count++;
            }
        }

        return count;
    }

    public static bool Red((byte R, byte G, byte B) p) => p.R > 170 && p.G < 110 && p.B < 110;
    public static bool Blue((byte R, byte G, byte B) p) => p.B > 170 && p.R < 90 && p.G < 120;
    public static bool Green((byte R, byte G, byte B) p) => p.G > 110 && p.R < 90 && p.B < 140;
    public static bool White((byte R, byte G, byte B) p) => p.R > 235 && p.G > 235 && p.B > 235;
    public static bool Dark((byte R, byte G, byte B) p) => p.R < 110 && p.G < 110 && p.B < 110;
}

public class PageFrameTests
{
    [Theory]
    [InlineData(0, 0, 842, 595, 842)]
    [InlineData(90, 0, 0, 842, 595)]
    [InlineData(180, 595, 0, 595, 842)]
    [InlineData(270, 595, 842, 842, 595)]
    public void Top_left_of_the_displayed_page_maps_to_the_right_pdf_corner(int rotation, double x, double y, double width, double height)
    {
        var frame = new PageFrame(0, 0, 595, 842, rotation);
        Assert.Equal((width, height), (frame.Width, frame.Height));
        Assert.Equal((x, y), frame.ToPdf(new ViewPoint(0, 0)));

        var back = frame.ToView(123, 456);
        var (px, py) = frame.ToPdf(back);
        Assert.Equal(123, px, 6);
        Assert.Equal(456, py, 6);
    }

    [Fact]
    public void Crop_box_offset_and_rect_conversion()
    {
        var frame = new PageFrame(50, 100, 350, 500, 0);
        Assert.Equal((300.0, 400.0), (frame.Width, frame.Height));
        Assert.Equal((60.0, 490.0), frame.ToPdf(new ViewPoint(10, 10)));
        Assert.Equal(new[] { 60.0, 470, 80, 490 }, frame.PdfRect(new ViewRect(10, 10, 20, 20)));
        Assert.Equal(270, new PageFrame(0, 0, 10, 10, -90).Rotation);
    }

    [Fact]
    public void Pdf_number_and_string_formatting()
    {
        Assert.Equal("0", Pdf.Number(-0.00001));
        Assert.Equal("12.5", Pdf.Number(12.5));
        Assert.Equal(@"(a\(b\)c\\ \351)", Pdf.Literal(Pdf.WinAnsi("a(b)c\\ é")));
        Assert.Equal((byte)0x80, Pdf.WinAnsiByte('€'));
        Assert.Null(Pdf.WinAnsiByte('中'));
        Assert.Equal('€', Pdf.FromWinAnsi(0x80));
    }
}

public class PdfEditorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfiumRenderer _renderer = new();
    private readonly PdfEditor _editor = new(new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private string Out => Path.Combine(_temp.Path, "saida");
    private OutputTarget Target => new(Out, OverwritePolicy.AutoRename, SubfolderMode.None);
    private string Sample(int rotate = 0, int pages = 1, params string[] lines) =>
        EditorSamples.TextPdf(Path.Combine(_temp.Path, $"doc-{Guid.NewGuid():N}.pdf"), rotate, pages, lines);

    private Task<RenderedPage> Render(string path, int page = 0) => _renderer.RenderPageAsync(path, page, RenderSize.ByScale(1));

    private static string Text(string path, int page = 0) => PdfTextExporter.ReadPages(path)[page];

    private static PdfDictionary[] Annots(string path, int page = 0)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annots = document.Pages[page].Elements.GetArray("/Annots");
        return annots is null ? [] : Enumerable.Range(0, annots.Elements.Count).Select(i => annots.Elements.GetDictionary(i)!).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Text_and_images_land_where_they_were_placed_even_on_rotated_pages(int rotation)
    {
        var source = Sample(rotation);
        var before = File.ReadAllBytes(source);
        var blue = EditorSamples.SolidPng(_temp.Path, "azul.png", (_, _) => (0, 0, 255));
        var items = new EditItem[]
        {
            new TextItem(Guid.NewGuid(), 0, new ViewRect(100, 100, 300, 40), "Texto novo 2026", new TextStyle("Arial", 20, Rgb.Red, Bold: true)),
            new ImageItem(Guid.NewGuid(), 0, new ViewRect(400, 300, 60, 60), blue, "azul.png"),
        };

        var result = await _editor.SaveAsync(source, items, Target);

        Assert.Equal(Path.Combine(Out, Path.GetFileNameWithoutExtension(source) + "_editado.pdf"), result.File.Path);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Contains("Texto novo 2026", Text(result.File.Path));
        Assert.Contains("12345", Text(result.File.Path));

        var page = await Render(result.File.Path);
        var frame = new PageFrame(0, 0, 595, 842, rotation);
        Assert.Equal(((int)frame.Width, (int)frame.Height), (page.Width, page.Height));
        Assert.True(EditorSamples.Blue(EditorSamples.Pixel(page, 430, 330)), "imagem fora do lugar");
        Assert.True(EditorSamples.White(EditorSamples.Pixel(page, 430, 250)));
        Assert.True(EditorSamples.Count(page, new ViewRect(95, 95, 310, 50), EditorSamples.Red) > 80, "texto fora do lugar");
        Assert.Equal(0, EditorSamples.Count(page, new ViewRect(95, 200, 310, 50), EditorSamples.Red));
    }

    [Fact]
    public async Task Images_can_be_cropped_rotated_and_made_transparent()
    {
        var source = Sample();
        // Metade de cima vermelha, de baixo verde.
        var halves = EditorSamples.SolidPng(_temp.Path, "metades.png", (_, y) => y < 20 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)160, (byte)0));
        var blue = EditorSamples.SolidPng(_temp.Path, "azul.png", (_, _) => (0, 0, 255));
        var items = new EditItem[]
        {
            // Girada 90° no sentido horário: o vermelho vai para a direita.
            new ImageItem(Guid.NewGuid(), 0, new ViewRect(100, 300, 100, 100), halves, "metades.png", Rotation: 90),
            // Recorte da metade de baixo: só verde.
            new ImageItem(Guid.NewGuid(), 0, new ViewRect(250, 300, 100, 100), halves, "metades.png", Crop: new ImageCrop(0, 0.5, 0, 0)),
            new ImageItem(Guid.NewGuid(), 0, new ViewRect(400, 300, 100, 100), blue, "azul.png", Opacity: 0.5),
        };

        var page = await Render((await _editor.SaveAsync(source, items, Target)).File.Path);

        Assert.True(EditorSamples.Red(EditorSamples.Pixel(page, 180, 350)));
        Assert.False(EditorSamples.Red(EditorSamples.Pixel(page, 120, 350)));
        Assert.Equal(0, EditorSamples.Count(page, new ViewRect(255, 305, 90, 90), EditorSamples.Red));
        var (r, g, b) = EditorSamples.Pixel(page, 450, 350);
        Assert.InRange(r, 100, 160);
        Assert.InRange(b, 230, 255);
        Assert.InRange(g, 100, 160);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task Annotations_have_their_own_appearance_and_stay_editable(int rotation)
    {
        var source = Sample(rotation);
        var run = TextRuns.Read(source, 0).Single(r => r.Text.Contains("12345"));
        var quads = TextRuns.Quads(source, 0, run.Rect.Inflate(4));
        var items = new EditItem[]
        {
            new MarkupItem(Guid.NewGuid(), 0, MarkupKind.Highlight, quads, Rgb.Yellow, "Conferir"),
            new ShapeItem(Guid.NewGuid(), 0, ShapeKind.Rectangle, new ViewRect(100, 400, 120, 80), [], Rgb.Red, 4),
            new ShapeItem(Guid.NewGuid(), 0, ShapeKind.Ellipse, new ViewRect(260, 400, 120, 80), [], Rgb.Blue, 2, Fill: Rgb.Yellow),
            new ShapeItem(Guid.NewGuid(), 0, ShapeKind.Arrow, default, [new ViewPoint(100, 520), new ViewPoint(300, 540)], Rgb.Red, 3),
            new ShapeItem(Guid.NewGuid(), 0, ShapeKind.Line, default, [new ViewPoint(100, 560), new ViewPoint(300, 560)], Rgb.Black, 1),
            new ShapeItem(Guid.NewGuid(), 0, ShapeKind.Ink, default, [new ViewPoint(320, 520), new ViewPoint(340, 540), new ViewPoint(360, 520)], Rgb.Blue, 2),
            new NoteItem(Guid.NewGuid(), 0, new ViewPoint(420, 120), "Verificar a data", NoteKind.Note, Rgb.Yellow),
            new NoteItem(Guid.NewGuid(), 0, new ViewPoint(450, 120), "Ok", NoteKind.Comment, Rgb.Yellow),
            new TextBoxItem(Guid.NewGuid(), 0, new ViewRect(100, 200, 200, 40), "Observação importante", new TextStyle(Size: 12), Rgb.Black, null),
            new StampItem(Guid.NewGuid(), 0, new ViewRect(330, 200, 160, 50), StampLibrary.Defaults[4], new DateTime(2026, 11, 10)),
        };

        var output = (await _editor.SaveAsync(source, items, Target)).File.Path;
        var annots = Annots(output);

        Assert.Equal(new[] { "/Highlight", "/Square", "/Circle", "/Line", "/Line", "/Ink", "/Text", "/Text", "/FreeText", "/Stamp" },
            annots.Select(a => a.Elements.GetName("/Subtype")).ToArray());
        Assert.All(annots, a => Assert.NotNull(a.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")));
        Assert.Equal(8, annots[0].Elements.GetArray("/QuadPoints")!.Elements.Count);
        Assert.Equal("Conferir", annots[0].Elements.GetString("/Contents"));
        Assert.Equal("/ClosedArrow", annots[3].Elements.GetArray("/LE")!.Elements[1].ToString());
        Assert.Equal("Verificar a data", annots[6].Elements.GetString("/Contents"));
        Assert.Equal("Observação importante", annots[8].Elements.GetString("/Contents"));
        Assert.Contains("RECEBIDO", annots[9].Elements.GetString("/Contents"));
        Assert.Contains("10/11/2026", annots[9].Elements.GetString("/Contents"));

        var page = await Render(output);
        // Marca-texto amarelo sobre a linha; retângulo vermelho com o meio em branco.
        Assert.True(EditorSamples.Count(page, run.Rect, p => p.R > 230 && p.G > 200 && p.B < 205) > run.Rect.Width * run.Rect.Height * 0.3);
        Assert.True(EditorSamples.Red(EditorSamples.Pixel(page, 102, 440)), "borda do retângulo");
        Assert.True(EditorSamples.White(EditorSamples.Pixel(page, 160, 440)), "meio do retângulo");
        Assert.True(EditorSamples.Count(page, new ViewRect(330, 200, 160, 50), EditorSamples.Green) > 100, "carimbo");
    }

    [Fact]
    public async Task Existing_text_is_replaced_inside_the_pdf_when_the_font_allows()
    {
        var source = Sample();
        var run = TextRuns.Read(source, 0).Single(r => r.Text == "Contrato numero 12345");

        var result = await _editor.SaveAsync(source, [new ReplaceTextItem(Guid.NewGuid(), 0, run, "Contrato numero 54321", new TextStyle())], Target);

        Assert.Equal((1, 0), (result.InPlace, result.Overlaid));
        var text = Text(result.File.Path);
        Assert.Contains("54321", text);
        Assert.DoesNotContain("12345", text);
        Assert.Contains("R$ 1.500,00", text);
        // Mesmo lugar: o trecho novo é lido na mesma posição.
        var after = TextRuns.Read(result.File.Path, 0).Single(r => r.Text.Contains("54321"));
        Assert.InRange(after.Rect.X - run.Rect.X, -1, 1);
        Assert.InRange(after.Rect.Y - run.Rect.Y, -1, 1);
    }

    [Fact]
    public async Task Text_split_by_spacing_adjustments_is_also_replaced()
    {
        var source = EditorSamples.TjPdf(Path.Combine(_temp.Path, "tj.pdf"));
        var run = TextRuns.Read(source, 0).Single(r => r.Text.Contains("777"));

        var result = await _editor.SaveAsync(source, [new ReplaceTextItem(Guid.NewGuid(), 0, run, run.Text.Replace("777", "778"), new TextStyle())], Target);

        Assert.Equal(1, result.InPlace);
        Assert.Contains("778", Text(result.File.Path));
        Assert.DoesNotContain("777", Text(result.File.Path));
    }

    [Fact]
    public async Task Ambiguous_text_is_covered_and_the_person_is_warned()
    {
        var source = Sample(0, 1, "Recibo ABC", "Recibo ABC");
        var run = TextRuns.Read(source, 0)[0];

        var overlay = await _editor.SaveAsync(source, [new ReplaceTextItem(Guid.NewGuid(), 0, run, "Recibo XYZ", new TextStyle("Arial", 18))], Target);
        Assert.Equal((0, 1), (overlay.InPlace, overlay.Overlaid));
        Assert.Contains("Recibo XYZ", Text(overlay.File.Path));
        Assert.Contains(overlay.Notes, n => n.Contains("mais de uma vez") && n.Contains("esconder"));
        var page = await Render(overlay.File.Path);
        Assert.True(EditorSamples.Count(page, run.Rect, EditorSamples.Dark) > 20);

        var refused = await _editor.SaveAsync(source, [new ReplaceTextItem(Guid.NewGuid(), 0, run, "Recibo XYZ", new TextStyle(), AllowOverlay: false)], Target);
        Assert.Equal((0, 0), (refused.InPlace, refused.Overlaid));
        Assert.DoesNotContain("XYZ", Text(refused.File.Path));
        Assert.Contains(refused.Notes, n => n.Contains("não foi alterado"));
    }

    [Fact]
    public async Task Text_written_by_pdfsharp_is_replaced_in_place_or_covered()
    {
        var txt = _temp.File("linha.txt", "Linha de teste 2468");
        var planner = new OutputPlanner(new OutputPathResolver());
        var source = (await new TextToPdfConverter(planner).ConvertAsync(txt, new TextToPdfOptions(Monospace: false),
            new OutputTarget(Path.Combine(_temp.Path, "origem"), OverwritePolicy.AutoRename, SubfolderMode.None))).Path;
        var run = TextRuns.Read(source, 0).Single(r => r.Text.Contains("2468"));

        var result = await _editor.SaveAsync(source, [new ReplaceTextItem(Guid.NewGuid(), 0, run, run.Text.Replace("2468", "8642"), new TextStyle())], Target);

        Assert.Equal(1, result.InPlace + result.Overlaid);
        Assert.Contains("8642", Text(result.File.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task Text_positions_read_from_the_pdf_match_the_rendered_page(int rotation)
    {
        var source = Sample(rotation);
        var run = TextRuns.Read(source, 0).Single(r => r.Text.Contains("12345"));
        var page = await Render(source);
        Assert.True(EditorSamples.Count(page, run.Rect, EditorSamples.Dark) > 40, $"trecho em {run.Rect}");
        Assert.InRange(run.FontSize, 16, 20);
    }

    [Fact]
    public async Task Watermark_goes_on_the_chosen_pages_over_or_behind_the_content()
    {
        var source = Sample(0, 3);
        var marked = await _editor.WatermarkAsync(source, new WatermarkOptions("CONFIDENCIAL", Pages: "1,3"), Target);
        var pages = PdfTextExporter.ReadPages(marked.File.Path);
        Assert.Contains("CONFIDENCIAL", pages[0]);
        Assert.DoesNotContain("CONFIDENCIAL", pages[1]);
        Assert.Contains("CONFIDENCIAL", pages[2]);
        Assert.Equal(2, marked.Applied);

        // Página toda coberta por uma imagem cinza (como uma digitalização).
        var gray = ImageSamples.Write(Path.Combine(_temp.Path, "cinza.png"), 100, 140, (_, _) => (128, 128, 128));
        var scan = ImageSamples.PdfWithImage(Path.Combine(_temp.Path, "scan.pdf"), gray);
        var style = new TextStyle("Arial", 90, Rgb.Red, Bold: true);
        var center = new ViewRect(147, 371, 300, 100);
        var over = await Render((await _editor.WatermarkAsync(scan, new WatermarkOptions("XX", Style: style, Opacity: 1, Rotation: 0), Target)).File.Path);
        var behind = await Render((await _editor.WatermarkAsync(scan, new WatermarkOptions("XX", Style: style, Opacity: 1, Rotation: 0, Behind: true), Target)).File.Path);
        Assert.True(EditorSamples.Count(over, center, EditorSamples.Red) > 300);
        Assert.Equal(0, EditorSamples.Count(behind, center, EditorSamples.Red));

        var image = await _editor.WatermarkAsync(source, new WatermarkOptions(null, File.ReadAllBytes(ImageSamples.Write(Path.Combine(_temp.Path, "logo.png"), 50, 50, (_, _) => (0, 0, 255))), Opacity: 1, Rotation: 0), Target);
        Assert.True(EditorSamples.Blue(EditorSamples.Pixel(await Render(image.File.Path), 297, 421)));

        await Assert.ThrowsAsync<PdfEngineException>(() => _editor.WatermarkAsync(source, new WatermarkOptions(" "), Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _editor.WatermarkAsync(source, new WatermarkOptions("X", Pages: "9"), Target));
    }

    [Fact]
    public async Task Header_and_footer_fields_are_filled_per_page()
    {
        var source = Sample(0, 3);
        var options = new HeaderFooterOptions(HeaderLeft: "{arquivo}", HeaderRight: "{data} {hora}", FooterRight: "Página {pagina} de {total}");

        var result = await _editor.HeaderFooterAsync(source, options, Target, now: new DateTime(2026, 11, 10, 14, 30, 0));

        var pages = PdfTextExporter.ReadPages(result.File.Path);
        Assert.Contains("Página 1 de 3", pages[0]);
        Assert.Contains("Página 3 de 3", pages[2]);
        Assert.Contains(Path.GetFileNameWithoutExtension(source), pages[1]);
        Assert.Contains("10/11/2026 14:30", pages[0]);

        var numbered = await _editor.HeaderFooterAsync(Sample(90, 2), options with { FirstNumber = 5, HeaderLeft = null, HeaderRight = null }, Target);
        var rotated = PdfTextExporter.ReadPages(numbered.File.Path);
        Assert.Contains("Página 6 de 6", rotated[1]);
        var page = await Render(numbered.File.Path);
        // Rodapé embaixo, à direita, na página exibida (842 × 595).
        Assert.True(EditorSamples.Count(page, new ViewRect(600, 540, 200, 55), EditorSamples.Dark) > 30);

        Assert.Equal("a — 3/9", DocumentStamps.Expand("{arquivo} — {pagina}/{total}", "a", 3, 9, DateTime.Now));
        await Assert.ThrowsAsync<PdfEngineException>(() => _editor.HeaderFooterAsync(source, new HeaderFooterOptions(FooterRight: null), Target));
    }

    [Fact]
    public async Task Form_fields_are_created_read_and_filled()
    {
        var source = Sample();
        var items = new EditItem[]
        {
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 200, 200, 22), FieldKind.Text, "nome", [], "João"),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 240, 200, 60), FieldKind.MultilineText, "obs", []),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 320, 14, 14), FieldKind.CheckBox, "aceito", [], "Sim"),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 350, 120, 40), FieldKind.Radio, "plano", ["Básico", "Completo"], "Completo"),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 400, 120, 22), FieldKind.Dropdown, "estado", ["SP", "RJ", "MG"], "RJ"),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 440, 120, 22), FieldKind.Date, "data", []),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 480, 80, 24), FieldKind.Button, "limpar", []),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(300, 200, 120, 22), FieldKind.Text, "nome", []),
        };

        var created = await _editor.SaveAsync(source, items, Target);
        var fields = FormFields.Read(created.File.Path);

        Assert.Equal(new[] { "nome", "obs", "aceito", "plano", "estado", "data", "limpar", "nome_2" }, fields.Select(f => f.Name).ToArray());
        Assert.Equal(new[] { FormFieldType.Text, FormFieldType.Text, FormFieldType.CheckBox, FormFieldType.Radio, FormFieldType.Choice, FormFieldType.Text, FormFieldType.Button, FormFieldType.Text },
            fields.Select(f => f.Type).ToArray());
        Assert.Equal("João", fields[0].Value);
        Assert.True(fields[1].Multiline);
        Assert.Equal("Sim", fields[2].Value);
        Assert.Equal(new[] { "Básico", "Completo" }, fields[3].Options);
        Assert.Equal("Completo", fields[3].Value);
        Assert.Equal(new[] { "SP", "RJ", "MG" }, fields[4].Options);
        Assert.Equal("RJ", fields[4].Value);
        Assert.All(fields, f => Assert.Equal(0, f.PageIndex));
        // Rótulos das opções ficam escritos na página.
        Assert.Contains("Completo", Text(created.File.Path));

        var values = new Dictionary<string, string?>
        {
            ["nome"] = "Maria José",
            ["obs"] = "linha 1\nlinha 2",
            ["aceito"] = "Off",
            ["plano"] = "Básico",
            ["estado"] = "MG",
            ["limpar"] = "x",
        };
        var filled = await _editor.FillFormAsync(created.File.Path, values, Target);
        var after = FormFields.Read(filled.File.Path);
        Assert.Equal("Maria José", after[0].Value);
        Assert.Equal("linha 1\nlinha 2", after[1].Value);
        Assert.Equal("Off", after[2].Value);
        Assert.Equal("Básico", after[3].Value);
        Assert.Equal("MG", after[4].Value);
        Assert.Contains(filled.Notes, n => n.Contains("limpar"));

        // O valor aparece na página (aparência refeita).
        var page = await Render(filled.File.Path);
        Assert.True(EditorSamples.Count(page, new ViewRect(72, 200, 200, 22), EditorSamples.Dark) > 30);

        await Assert.ThrowsAsync<PdfEngineException>(() => _editor.FillFormAsync(source, values, Target));
    }

    [Fact]
    public async Task Nothing_to_save_or_wrong_page_is_refused_and_the_original_never_changes()
    {
        var source = Sample();
        var before = File.ReadAllBytes(source);
        await Assert.ThrowsAsync<PdfEngineException>(() => _editor.SaveAsync(source, [], Target));
        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _editor.SaveAsync(source, [new TextItem(Guid.NewGuid(), 3, new ViewRect(0, 0, 10, 10), "x", new TextStyle())], Target));
        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _editor.SaveAsync(_temp.File("falso.pdf", "não é pdf"), [new TextItem(Guid.NewGuid(), 0, new ViewRect(0, 0, 10, 10), "x", new TextStyle())], Target));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(Directory.Exists(Out) && Directory.GetFiles(Out).Length > 0);
    }
}

public class EditorLibraryTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Stamp_library_starts_with_the_examples_and_keeps_changes()
    {
        var library = new StampLibrary(_temp.Path);
        Assert.Equal(new[] { "CONFIDENCIAL", "CÓPIA", "ORIGINAL", "DOCUMENTO ANALISADO", "RECEBIDO", "RASCUNHO" }, library.Load().Select(s => s.Text).ToArray());

        var custom = library.Load().Append(new StampTemplate("pago", "PAGO", Rgb.Green, ShowDate: true, Subtitle: "Financeiro")).ToList();
        custom.RemoveAll(s => s.Id == "rascunho");
        custom.Add(new StampTemplate("ruim", "", Rgb.Red));
        library.Save(custom);

        var reloaded = new StampLibrary(_temp.Path).Load();
        Assert.Contains(reloaded, s => s is { Text: "PAGO", ShowDate: true, Subtitle: "Financeiro" } && s.Color == Rgb.Green);
        Assert.DoesNotContain(reloaded, s => s.Id is "rascunho" or "ruim");

        File.WriteAllText(Path.Combine(_temp.Path, "carimbos.json"), "{ quebrado");
        Assert.Equal(6, new StampLibrary(_temp.Path).Load().Count);
    }

    [Fact]
    public void Signatures_are_kept_as_png_and_only_library_files_are_deleted()
    {
        var library = new SignatureLibrary(_temp.Path);
        Assert.Empty(library.List());
        var png = File.ReadAllBytes(ImageSamples.Write(Path.Combine(_temp.Path, "ass.png"), 30, 10, (_, _) => (0, 0, 0)));
        var saved = library.Add(png);
        Assert.Single(library.List());
        Assert.Throws<ArgumentException>(() => library.Add([1, 2, 3]));

        var outside = _temp.File("outro.png", "x");
        library.Delete(outside);
        Assert.True(File.Exists(outside));
        library.Delete(saved);
        Assert.Empty(library.List());
    }

    [Fact]
    public void Edit_items_move_and_resize()
    {
        var ink = new ShapeItem(Guid.NewGuid(), 0, ShapeKind.Ink, default, [new ViewPoint(10, 10), new ViewPoint(30, 50)], Rgb.Black, 2);
        var moved = (ShapeItem)ink.Moved(5, 5);
        Assert.Equal(new ViewPoint(15, 15), moved.Points[0]);
        var resized = (ShapeItem)ink.Resized(new ViewRect(0, 0, 100, 100));
        Assert.Equal(new ViewRect(0, 0, 100, 100), resized.Bounds);

        var text = new TextItem(Guid.NewGuid(), 0, new ViewRect(1, 2, 3, 4), "a", new TextStyle());
        Assert.Equal(new ViewRect(11, 22, 3, 4), text.Moved(10, 20).Bounds);
        var radio = new FieldItem(Guid.NewGuid(), 0, new ViewRect(0, 0, 100, 60), FieldKind.Radio, "r", ["a", "b", "c"]);
        Assert.Equal(3, radio.RadioButtons().Count);
        Assert.Equal(("12345", "54321"), ExistingText.Difference("Contrato 12345", "Contrato 54321"));
        Assert.Equal(("numero", "num"), ExistingText.Difference("Contrato numero 12345", "Contrato num 12345"));
        Assert.True(Rgb.TryParse("#008B50", out var green) && green == Rgb.Green);
        Assert.False(Rgb.TryParse("verde", out _));
    }
}

/// <summary>Gera o formulário usado pela tela de preenchimento no CI (só com PDFMASTER_SMOKE_DIR).</summary>
public class SmokeSamplesPhase5
{
    [Fact]
    public async Task Writes_a_form_for_the_form_tool()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMASTER_SMOKE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var work = Path.Combine(folder, "fase5");
        var source = EditorSamples.TextPdf(Path.Combine(work, "Formulario.pdf"), 0, 1, "Cadastro de cliente", "Nome:", "Estado:");
        var editor = new PdfEditor(new OutputPlanner(new OutputPathResolver()));
        var result = await editor.SaveAsync(source,
        [
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(140, 105, 260, 22), FieldKind.Text, "nome", [], "Maria"),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(140, 145, 120, 22), FieldKind.Dropdown, "estado", ["SP", "RJ", "MG"], "SP"),
            new FieldItem(Guid.NewGuid(), 0, new ViewRect(72, 200, 14, 14), FieldKind.CheckBox, "aceito", []),
            new StampItem(Guid.NewGuid(), 0, new ViewRect(380, 40, 160, 50), StampLibrary.Defaults[0], DateTime.Now),
        ], new OutputTarget(work, OverwritePolicy.Overwrite, SubfolderMode.None));
        File.Copy(result.File.Path, Path.Combine(folder, "Formulario.pdf"), overwrite: true);
    }
}
