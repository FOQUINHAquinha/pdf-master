using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFMaster.Compression;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Imagens e PDFs de apoio para os testes de conversão e compressão.</summary>
public static class ImageSamples
{
    /// <summary>Grava uma imagem; o formato vem da extensão (.png, .jpg, .bmp, .tif).</summary>
    public static string Write(string path, int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel,
        double dpi = 96, int jpegQuality = 95, ushort? exifOrientation = null, int frames = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stride = width * 3;
        var data = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = y * stride + x * 3;
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
            }
        }

        var source = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgr24, null, data, stride);
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new PngBitmapEncoder(),
        };

        for (var f = 0; f < frames; f++)
        {
            if (exifOrientation is { } orientation)
            {
                var metadata = new BitmapMetadata("jpg");
                metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
                encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
            }
            else
            {
                encoder.Frames.Add(BitmapFrame.Create(source));
            }
        }

        using var stream = new FileStream(path, FileMode.Create);
        encoder.Save(stream);
        return path;
    }

    /// <summary>Degradê suave com um pouco de ruído: parecido com uma foto ou página digitalizada.</summary>
    public static (byte R, byte G, byte B) Photo(int x, int y)
    {
        var noise = (x * 7919 + y * 104729) % 9;
        return ((byte)Math.Clamp(x / 8 % 256 + noise, 0, 255), (byte)Math.Clamp(y / 6 % 256 + noise, 0, 255),
            (byte)Math.Clamp((x + y) / 12 % 256 + noise, 0, 255));
    }

    /// <summary>PDF A4 de uma página com a imagem ocupando a página inteira.</summary>
    public static string PdfWithImage(string pdfPath, string imagePath, string? title = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
        using var document = new PdfDocument();
        if (title is not null) document.Info.Title = title;
        var page = document.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(595, 842));
        using (var graphics = XGraphics.FromPdfPage(page))
        using (var image = XImage.FromFile(imagePath))
        {
            graphics.DrawImage(image, 0, 0, 595, 842);
        }

        document.Save(pdfPath);
        return pdfPath;
    }

    public static (int Width, int Height, double DpiX, PixelFormat Format, int Frames, byte[] Bgra) Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.Format, decoder.Frames.Count, pixels);
    }
}

public class PdfToImageTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfToImageConverter _converter =
        new(new PdfSharpEngine(), new PdfiumRenderer(), new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private string OutFolder => Path.Combine(_temp.Path, "saida");
    private OutputTarget Target => new(OutFolder, OverwritePolicy.AutoRename, SubfolderMode.None);

    [Fact]
    public async Task One_png_per_page_with_the_page_content()
    {
        var pdf = PdfSamples.Create(_temp, "doc.pdf", 3, 300);
        var files = await _converter.ConvertAsync(pdf, new PdfToImageOptions(Dpi: 72), Target);

        Assert.Equal(new[] { "doc_p001.png", "doc_p002.png", "doc_p003.png" }, files.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.All(files, f => Assert.True(File.Exists(f.Path) && f.Bytes == new FileInfo(f.Path).Length));

        var image = ImageSamples.Read(files[0].Path);
        Assert.InRange(image.Width, 299, 301);
        Assert.InRange(image.Height, 399, 401);
        var red = (100 * image.Width + 100) * 4;       // dentro do quadrado vermelho da amostra
        Assert.True(image.Bgra[red + 2] > 200 && image.Bgra[red + 1] < 60 && image.Bgra[red] < 60);
        var white = (300 * image.Width + 250) * 4;     // fundo
        Assert.True(image.Bgra[white] > 240 && image.Bgra[white + 1] > 240 && image.Bgra[white + 2] > 240);
        Assert.Equal(302, ImageSamples.Read(files[2].Path).Width);
    }

    [Fact]
    public async Task Resolution_scales_the_image_and_is_recorded_in_the_file()
    {
        var pdf = PdfSamples.Create(_temp, "doc.pdf", 1, 300);
        var files = await _converter.ConvertAsync(pdf, new PdfToImageOptions(Dpi: 144), Target);

        var image = ImageSamples.Read(files[0].Path);
        Assert.InRange(image.Width, 599, 601);
        Assert.InRange(image.DpiX, 143, 145);
    }

    [Fact]
    public async Task Jpeg_grayscale_bmp_and_page_selection()
    {
        var pdf = PdfSamples.Create(_temp, "doc.pdf", 4, 300);

        var gray = await _converter.ConvertAsync(pdf,
            new PdfToImageOptions(ImageFormat.Jpeg, 72, 80, Grayscale: true, Pages: "2"), Target);
        Assert.Equal("doc_p002.jpg", Path.GetFileName(Assert.Single(gray).Path));
        Assert.Equal(PixelFormats.Gray8, ImageSamples.Read(gray[0].Path).Format);

        var bmp = await _converter.ConvertAsync(pdf, new PdfToImageOptions(ImageFormat.Bmp, 72, Pages: "4,1"), Target);
        Assert.Equal(new[] { "doc_p004.bmp", "doc_p001.bmp" }, bmp.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.Equal(303, ImageSamples.Read(bmp[0].Path).Width);
    }

    [Fact]
    public async Task Tiff_can_hold_all_pages_in_one_file()
    {
        var pdf = PdfSamples.Create(_temp, "doc.pdf", 3, 300);

        var single = await _converter.ConvertAsync(pdf, new PdfToImageOptions(ImageFormat.Tiff, 72, SingleTiff: true), Target);
        Assert.Equal("doc.tif", Path.GetFileName(Assert.Single(single).Path));
        Assert.Equal(3, single[0].PageCount);
        Assert.Equal(3, ImageSamples.Read(single[0].Path).Frames);

        var separate = await _converter.ConvertAsync(pdf, new PdfToImageOptions(ImageFormat.Tiff, 72), Target);
        Assert.Equal(3, separate.Count);
    }

    [Fact]
    public async Task Existing_images_are_never_overwritten_and_bad_options_are_rejected()
    {
        var pdf = PdfSamples.Create(_temp, "doc.pdf", 1, 300);
        var first = await _converter.ConvertAsync(pdf, new PdfToImageOptions(Dpi: 72), Target);
        var second = await _converter.ConvertAsync(pdf, new PdfToImageOptions(Dpi: 72), Target);

        Assert.Equal("doc_p001.png", Path.GetFileName(first[0].Path));
        Assert.Equal("doc_p001 (2).png", Path.GetFileName(second[0].Path));

        await Assert.ThrowsAsync<PdfEngineException>(() => _converter.ConvertAsync(pdf, new PdfToImageOptions(Dpi: 5000), Target));
        await Assert.ThrowsAsync<PageRangeException>(() => _converter.ConvertAsync(pdf, new PdfToImageOptions(Pages: "9"), Target));
    }

    [Fact]
    public async Task Cancellation_leaves_no_files()
    {
        var pdf = PdfSamples.Create(_temp, "doc.pdf", 5, 300);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _converter.ConvertAsync(pdf, new PdfToImageOptions(), Target, null, cts.Token));
        Assert.False(Directory.Exists(OutFolder) && Directory.GetFiles(OutFolder).Length > 0);
    }
}

public class ImagesToPdfTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfSharpEngine _engine = new();
    private readonly PdfiumRenderer _renderer = new();
    private readonly ImagesToPdfConverter _converter = new(new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);
    private string Image(string name) => Path.Combine(_temp.Path, "imagens", name);

    private async Task<(int Width, int Height)[]> PageSizes(string pdf) =>
        (await _engine.ReadInfoAsync(pdf)).Pages.Select(p => ((int)Math.Round(p.WidthPt), (int)Math.Round(p.HeightPt))).ToArray();

    [Fact]
    public async Task Page_takes_the_size_of_the_image_and_shows_it()
    {
        var blue = ImageSamples.Write(Image("azul.png"), 200, 100, (x, y) => (20, 40, 220));
        var result = await _converter.ConvertAsync([blue], "saida", new ImagesToPdfOptions(), Target);

        Assert.Equal("saida.pdf", Path.GetFileName(result.Path));
        Assert.Equal(1, result.PageCount);
        Assert.Equal(new[] { (150, 75) }, await PageSizes(result.Path)); // 200×100 px a 96 DPI

        var rendered = await _renderer.RenderPageAsync(result.Path, 0, RenderSize.ByScale(1));
        var center = (rendered.Height / 2 * rendered.Width + rendered.Width / 2) * 4;
        Assert.True(rendered.Bgra[center] > 180 && rendered.Bgra[center + 2] < 80, "o centro da página deveria ser azul");
    }

    [Fact]
    public async Task Fixed_page_sizes_orientation_and_margins()
    {
        var wide = ImageSamples.Write(Image("larga.png"), 400, 100, (x, y) => (200, 30, 30));
        var tall = ImageSamples.Write(Image("alta.png"), 100, 400, (x, y) => (30, 200, 30));

        var auto = await _converter.ConvertAsync([wide, tall], "a4", new ImagesToPdfOptions(PdfPageSizeMode.A4), Target);
        Assert.Equal(new[] { (842, 595), (595, 842) }, await PageSizes(auto.Path));

        var portrait = await _converter.ConvertAsync([wide], "retrato",
            new ImagesToPdfOptions(PdfPageSizeMode.Letter, PdfPageOrientation.Portrait), Target);
        Assert.Equal(new[] { (612, 792) }, await PageSizes(portrait.Path));

        var margin = await _converter.ConvertAsync([wide], "margem", new ImagesToPdfOptions(MarginMm: 10), Target);
        Assert.Equal(new[] { (357, 132) }, await PageSizes(margin.Path)); // 300×75 pt + 2 × 28,35 pt

        // Com margem, a borda da página fica branca e o centro tem a imagem.
        var rendered = await _renderer.RenderPageAsync(margin.Path, 0, RenderSize.ByScale(1));
        Assert.True(rendered.Bgra[(5 * rendered.Width + 5) * 4 + 1] > 240);
        var center = (rendered.Height / 2 * rendered.Width + rendered.Width / 2) * 4;
        Assert.True(rendered.Bgra[center + 2] > 150 && rendered.Bgra[center + 1] < 90);
    }

    [Fact]
    public async Task Jpeg_files_are_embedded_without_recompression()
    {
        var jpeg = ImageSamples.Write(Image("foto.jpg"), 640, 480, ImageSamples.Photo, jpegQuality: 85);
        var result = await _converter.ConvertAsync([jpeg], "foto", new ImagesToPdfOptions(), Target);

        var pdfBytes = File.ReadAllBytes(result.Path);
        var jpegBytes = File.ReadAllBytes(jpeg);
        Assert.True(IndexOf(pdfBytes, jpegBytes.AsSpan(0, 64).ToArray()) >= 0, "o JPEG original deveria estar dentro do PDF");
        Assert.True(IndexOf(pdfBytes, jpegBytes.AsSpan(jpegBytes.Length - 64, 64).ToArray()) >= 0);
        Assert.Equal(new[] { (480, 360) }, await PageSizes(result.Path));
    }

    [Fact]
    public async Task Photo_orientation_from_exif_is_respected()
    {
        // Foto "deitada" no arquivo, marcada para ser exibida girada 90° (retrato).
        var photo = ImageSamples.Write(Image("celular.jpg"), 200, 100,
            (x, y) => x < 100 ? ((byte)220, (byte)20, (byte)20) : ((byte)20, (byte)20, (byte)220), exifOrientation: 6);
        var result = await _converter.ConvertAsync([photo], "celular", new ImagesToPdfOptions(), Target);

        Assert.Equal(new[] { (75, 150) }, await PageSizes(result.Path));
        var rendered = await _renderer.RenderPageAsync(result.Path, 0, RenderSize.ByScale(2));
        // Girando 90° no sentido horário, a metade esquerda (vermelha) vai para o topo.
        var top = (rendered.Height / 4 * rendered.Width + rendered.Width / 2) * 4;
        var bottom = (rendered.Height * 3 / 4 * rendered.Width + rendered.Width / 2) * 4;
        Assert.True(rendered.Bgra[top + 2] > 150 && rendered.Bgra[top] < 100, "o topo deveria ser vermelho");
        Assert.True(rendered.Bgra[bottom] > 150 && rendered.Bgra[bottom + 2] < 100, "a base deveria ser azul");
    }

    [Fact]
    public async Task Multi_page_tiff_bmp_and_lossy_option()
    {
        var tiff = ImageSamples.Write(Image("digitalizado.tif"), 300, 200, ImageSamples.Photo, frames: 3);
        var bmp = ImageSamples.Write(Image("desenho.bmp"), 120, 120, (x, y) => (10, 10, 10));

        var result = await _converter.ConvertAsync([tiff, bmp], "varias", new ImagesToPdfOptions(), Target);
        Assert.Equal(4, result.PageCount);
        Assert.Equal(4, (await PageSizes(result.Path)).Length);

        var big = ImageSamples.Write(Image("grande.png"), 1200, 900, ImageSamples.Photo);
        var lossless = await _converter.ConvertAsync([big], "sem-perda", new ImagesToPdfOptions(), Target);
        var lossy = await _converter.ConvertAsync([big], "com-perda", new ImagesToPdfOptions(JpegQuality: 60), Target);
        Assert.True(lossy.Bytes < lossless.Bytes / 2, $"JPEG {lossy.Bytes} deveria ser bem menor que sem perda {lossless.Bytes}");
    }

    [Fact]
    public async Task Invalid_input_is_reported_and_nothing_is_written()
    {
        var fake = _temp.File(Path.Combine("imagens", "falsa.png"), "isto nao e uma imagem");
        await Assert.ThrowsAsync<PdfEngineException>(() => _converter.ConvertAsync([fake], "x", new ImagesToPdfOptions(), Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _converter.ConvertAsync([], "x", new ImagesToPdfOptions(), Target));
        Assert.False(Directory.Exists(Target.Folder) && Directory.GetFiles(Target.Folder).Length > 0);
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}

public class TextConversionTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly OutputPlanner _planner = new(new OutputPathResolver());
    private readonly TextToPdfConverter _toPdf;
    private readonly PdfTextExporter _exporter;

    public TextConversionTests()
    {
        _toPdf = new TextToPdfConverter(_planner);
        _exporter = new PdfTextExporter(_planner, new LibreOffice());
    }

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private string WriteText(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private static string AllText(string pdf) => string.Join("\n", PdfTextExporter.ReadPages(pdf));

    [Fact]
    public async Task Text_file_becomes_a_searchable_multi_page_pdf()
    {
        var lines = Enumerable.Range(1, 150).Select(i => $"Linha {i}: ação, coração e órgão público.");
        var txt = WriteText("notas.txt", string.Join("\r\n", lines));

        var result = await _toPdf.ConvertAsync(txt, new TextToPdfOptions(), Target);

        Assert.Equal("notas.pdf", Path.GetFileName(result.Path));
        Assert.True(result.PageCount >= 2, $"150 linhas deveriam ocupar mais de uma página, mas foram {result.PageCount}");
        var text = AllText(result.Path);
        Assert.Contains("ação", text);
        Assert.Contains("órgão", text);
        Assert.Contains("Linha 1:", text);
        Assert.Contains("Linha 150:", text);
    }

    [Fact]
    public async Task Long_lines_wrap_instead_of_being_cut_and_form_feed_starts_a_page()
    {
        var longLine = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"palavra{i}"));
        var txt = WriteText("longo.txt", longLine + "\fsegunda pagina aqui");

        var result = await _toPdf.ConvertAsync(txt, new TextToPdfOptions(Monospace: false), Target);

        Assert.Equal(2, result.PageCount);
        var pages = PdfTextExporter.ReadPages(result.Path);
        Assert.Contains("palavra1 ", pages[0]);
        Assert.Contains("palavra120", pages[0]);
        Assert.Contains("segunda", pages[1]);
    }

    [Fact]
    public void Reads_utf8_utf16_and_latin1_text()
    {
        Assert.Equal("ação", TextToPdfConverter.ReadText(WriteText("a.txt", "ação", new UTF8Encoding(true))));
        Assert.Equal("ação", TextToPdfConverter.ReadText(WriteText("b.txt", "ação", new UTF8Encoding(false))));
        Assert.Equal("ação", TextToPdfConverter.ReadText(WriteText("c.txt", "ação", Encoding.Unicode)));
        Assert.Equal("ação", TextToPdfConverter.ReadText(WriteText("d.txt", "ação", Encoding.Latin1)));
    }

    [Fact]
    public async Task Pdf_text_is_exported_to_txt_html_and_docx()
    {
        var txt = WriteText("carta.txt", "Prezado cliente,\r\n\r\nSegue o contrato 4521 <revisado> & assinado.\r\n\r\nAtenciosamente.");
        var pdf = (await _toPdf.ConvertAsync(txt, new TextToPdfOptions(Monospace: false), Target)).Path;

        var asText = await _exporter.ExportAsync(pdf, TextExportFormat.Txt, Target);
        Assert.Equal("carta.txt", Path.GetFileName(asText.Path));
        Assert.Contains("contrato 4521", File.ReadAllText(asText.Path));

        var asHtml = await _exporter.ExportAsync(pdf, TextExportFormat.Html, Target);
        var html = File.ReadAllText(asHtml.Path);
        Assert.Contains("<p>", html);
        Assert.Contains("&lt;revisado&gt; &amp; assinado", html);

        var asDocx = await _exporter.ExportAsync(pdf, TextExportFormat.Docx, Target);
        Assert.Equal("carta.docx", Path.GetFileName(asDocx.Path));
        using var zip = ZipFile.OpenRead(asDocx.Path);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("contrato 4521 &lt;revisado&gt; &amp; assinado", xml);
        System.Xml.Linq.XDocument.Parse(xml); // XML bem formado
    }

    [Fact]
    public async Task Pdf_without_text_is_reported_instead_of_producing_an_empty_file()
    {
        var scanned = PdfSamples.Create(_temp, "digitalizado.pdf", 2, 300);
        var error = await Assert.ThrowsAsync<PdfEngineException>(() => _exporter.ExportAsync(scanned, TextExportFormat.Docx, Target));
        Assert.Contains("OCR", error.Message);
        Assert.False(Directory.Exists(Target.Folder) && Directory.GetFiles(Target.Folder).Length > 0);

        var fake = _temp.File("falso.pdf", "isto nao e um pdf");
        await Assert.ThrowsAsync<PdfEngineException>(() => _exporter.ExportAsync(fake, TextExportFormat.Txt, Target));
    }

    [Fact]
    public void Paragraphs_join_lines_and_hyphenated_words()
    {
        var paragraphs = PdfTextExporter.Paragraphs("Primeira linha do pará-\ngrafo continua aqui.\n\n  Segundo parágrafo.  \n\n\n");
        Assert.Equal(new[] { "Primeira linha do parágrafo continua aqui.", "Segundo parágrafo." }, paragraphs.ToArray());
        // Hífen no fim da linha seguido de minúscula é tratado como divisão silábica.
        Assert.Equal(new[] { "subitem A" }, PdfTextExporter.Paragraphs("sub-\nitem A").ToArray());
        // Seguido de maiúscula ou número, o hífen é mantido.
        Assert.Equal(new[] { "Anexo- B" }, PdfTextExporter.Paragraphs("Anexo-\nB").ToArray());
    }

    [Fact]
    public void Docx_writer_escapes_text_and_breaks_pages()
    {
        var path = Path.Combine(_temp.Path, "teste.docx");
        DocxWriter.Write(path, [["a < b & c", "controle\u0001removido"], [], ["última"]]);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        System.Xml.Linq.XDocument.Parse(xml);
        Assert.Contains("a &lt; b &amp; c", xml);
        Assert.Contains("controleremovido", xml);
        Assert.Equal(2, xml.Split("w:type=\"page\"").Length - 1); // três páginas → duas quebras
    }
}

/// <summary>
/// Conversões que dependem do LibreOffice. No CI ele é instalado antes dos testes e a ausência
/// é uma falha; em outros computadores, sem LibreOffice, estes testes não verificam nada.
/// </summary>
public class LibreOfficeTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly LibreOffice _office = new();
    private readonly PdfSharpEngine _engine = new();
    private readonly OutputPlanner _planner = new(new OutputPathResolver());

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private bool Available()
    {
        if (_office.IsAvailable) return true;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("PDFMASTER_REQUIRE_LIBREOFFICE"), "1", StringComparison.Ordinal),
            "O LibreOffice deveria estar instalado neste ambiente de teste.");
        return false;
    }

    [Fact]
    public async Task Word_html_and_excel_documents_become_pdf()
    {
        if (!Available()) return;
        var converter = new OfficeToPdfConverter(_office, _engine, _planner);

        var docx = Path.Combine(_temp.Path, "Contrato.docx");
        DocxWriter.Write(docx, [["Contrato de locação número 12345", "Cláusula primeira."], ["Segunda página do contrato."]]);
        var fromWord = await converter.ConvertAsync(docx, Target, OfficeToPdfConverter.OperationName);
        Assert.Equal("Contrato.pdf", Path.GetFileName(fromWord.Path));
        Assert.Equal(2, fromWord.PageCount);
        Assert.Contains("12345", string.Join("\n", PdfTextExporter.ReadPages(fromWord.Path)));

        var html = _temp.File("pagina.html", "<html><body><h1>Titulo HTML 777</h1><p>Paragrafo.</p></body></html>");
        var fromHtml = await converter.ConvertAsync(html, Target, TextToPdfConverter.OperationName);
        Assert.Contains("777", string.Join("\n", PdfTextExporter.ReadPages(fromHtml.Path)));

        // Planilha de verdade: o próprio LibreOffice gera o XLSX a partir de um CSV.
        var csv = _temp.File("dados.csv", "Produto,Valor\nCaneta,4321\nLapis,15\n");
        var generated = await _office.ConvertAsync(csv, "xlsx", null, CancellationToken.None);
        var xlsx = Path.Combine(_temp.Path, "Planilha.xlsx");
        File.Copy(generated, xlsx);
        LibreOffice.DeleteWorkFolder(generated);
        var fromExcel = await converter.ConvertAsync(xlsx, Target, OfficeToPdfConverter.OperationName);
        Assert.Contains("4321", string.Join("\n", PdfTextExporter.ReadPages(fromExcel.Path)));

        // Os originais continuam onde estavam e nada sobra na pasta de trabalho.
        Assert.True(File.Exists(docx) && File.Exists(html) && File.Exists(xlsx));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(generated)!)));

        await Assert.ThrowsAsync<PdfEngineException>(() => converter.ConvertAsync(_temp.File("x.zip", "PK"), Target, "x"));
    }

    [Fact]
    public async Task Pdf_becomes_docx_keeping_the_layout()
    {
        if (!Available()) return;
        var txt = Path.Combine(_temp.Path, "origem.txt");
        File.WriteAllText(txt, "Documento para o Word, protocolo 98765.");
        var pdf = (await new TextToPdfConverter(_planner).ConvertAsync(txt, new TextToPdfOptions(Monospace: false), Target)).Path;

        var docx = await new PdfTextExporter(_planner, _office).ExportWithLayoutAsync(pdf, 1, Target);

        Assert.Equal("origem.docx", Path.GetFileName(docx.Path));
        using var zip = ZipFile.OpenRead(docx.Path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        // O importador pode dividir o texto em vários trechos; as marcações são retiradas antes de conferir.
        var text = System.Text.RegularExpressions.Regex.Replace(reader.ReadToEnd(), "<[^>]+>", string.Empty);
        Assert.Contains("98765", text);
    }
}

public class PdfCompressorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfSharpEngine _engine = new();
    private readonly PdfiumRenderer _renderer = new();
    private readonly PdfCompressor _compressor = new(new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private string OutFolder => Path.Combine(_temp.Path, "saida");
    private OutputTarget Target => new(OutFolder, OverwritePolicy.AutoRename, SubfolderMode.None);

    private string ScannedPdf(string name, string imageExtension = ".png", string? title = null)
    {
        var image = ImageSamples.Write(Path.Combine(_temp.Path, "img", name + imageExtension), 1600, 2200, ImageSamples.Photo, jpegQuality: 98);
        return ImageSamples.PdfWithImage(Path.Combine(_temp.Path, name + ".pdf"), image, title);
    }

    /// <summary>Diferença média por canal entre a primeira página de dois PDFs (0 = idênticas).</summary>
    private async Task<double> Difference(string a, string b)
    {
        var first = await _renderer.RenderPageAsync(a, 0, RenderSize.Fit(300, 420));
        var second = await _renderer.RenderPageAsync(b, 0, RenderSize.Fit(300, 420));
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        long total = 0;
        for (var i = 0; i < first.Bgra.Length; i++) total += Math.Abs(first.Bgra[i] - second.Bgra[i]);
        return total / (double)first.Bgra.Length;
    }

    [Fact]
    public async Task Balanced_profile_shrinks_a_scanned_document_and_keeps_it_readable()
    {
        var pdf = ScannedPdf("digitalizado");
        var before = File.ReadAllBytes(pdf);

        var result = await _compressor.CompressAsync(pdf, CompressionSettings.For(CompressionProfile.Balanced), Target);

        Assert.True(result.Reduced);
        Assert.Equal("digitalizado_comprimido.pdf", Path.GetFileName(result.Output!.Path));
        Assert.Equal(before.Length, result.OriginalBytes);
        Assert.Equal(new FileInfo(result.Output.Path).Length, result.FinalBytes);
        Assert.True(result.FinalBytes < result.OriginalBytes / 2, $"{result.OriginalBytes} → {result.FinalBytes}");
        Assert.Equal(1, result.ImagesFound);
        Assert.Equal(1, result.ImagesRecompressed);
        Assert.InRange(result.SavedPercent, 50, 100);

        Assert.Equal(before, File.ReadAllBytes(pdf)); // o original não muda
        Assert.Equal(1, (await _engine.ReadInfoAsync(result.Output.Path)).PageCount);
        Assert.True(await Difference(pdf, result.Output.Path) < 8, "a página comprimida deveria continuar parecida com a original");
        Assert.Empty(Directory.GetFiles(OutFolder, "*.pdfmaster-tmp"));
    }

    [Fact]
    public async Task Stronger_profiles_produce_smaller_files_from_jpeg_images()
    {
        var pdf = ScannedPdf("foto", ".jpg");

        var light = await _compressor.CompressAsync(pdf, CompressionSettings.For(CompressionProfile.Light), Target);
        var maximum = await _compressor.CompressAsync(pdf, CompressionSettings.For(CompressionProfile.Maximum), Target);

        Assert.True(light.Reduced && maximum.Reduced);
        Assert.True(maximum.FinalBytes < light.FinalBytes, $"máxima {maximum.FinalBytes} deveria ser menor que leve {light.FinalBytes}");
        Assert.Equal("foto_comprimido (2).pdf", Path.GetFileName(maximum.Output!.Path)); // não sobrescreve o primeiro resultado
        Assert.True(await Difference(pdf, maximum.Output.Path) < 20);
    }

    [Fact]
    public async Task Lossless_profile_never_touches_images()
    {
        var pdf = ScannedPdf("intacto");
        var result = await _compressor.CompressAsync(pdf, CompressionSettings.For(CompressionProfile.Lossless), Target);

        Assert.Equal(0, result.ImagesRecompressed);
        if (result.Reduced)
        {
            Assert.True(result.FinalBytes < result.OriginalBytes);
            Assert.True(await Difference(pdf, result.Output!.Path) < 0.01);
        }
        else
        {
            Assert.Null(result.Output);
        }
    }

    [Fact]
    public async Task When_nothing_is_gained_no_file_is_written()
    {
        // Um documento que já passou pela compressão não tem mais o que ganhar com um perfil mais leve.
        var pdf = ScannedPdf("ja-otimizado");
        var first = await _compressor.CompressAsync(pdf, CompressionSettings.For(CompressionProfile.Maximum), Target);
        Assert.True(first.Reduced);

        var second = await _compressor.CompressAsync(first.Output!.Path, CompressionSettings.For(CompressionProfile.Light), Target);

        Assert.Equal(0, second.ImagesRecompressed); // regravar com qualidade maior só aumentaria a imagem
        var expectedName = Path.Combine(OutFolder, "ja-otimizado_comprimido_comprimido.pdf");
        if (second.Reduced)
        {
            Assert.True(second.FinalBytes < second.OriginalBytes);
            Assert.True(File.Exists(expectedName));
        }
        else
        {
            Assert.Null(second.Output);
            Assert.True(second.FinalBytes >= second.OriginalBytes);
            Assert.False(File.Exists(expectedName), "sem redução, nenhum arquivo deveria ser gravado");
        }

        Assert.Empty(Directory.GetFiles(OutFolder, "*.pdfmaster-tmp"));
    }

    [Fact]
    public async Task Grayscale_and_metadata_options()
    {
        var pdf = ScannedPdf("colorido", ".png", title: "Título confidencial");
        Assert.Equal("Título confidencial", (await _engine.ReadInfoAsync(pdf)).Title);

        var result = await _compressor.CompressAsync(pdf,
            new CompressionSettings(70, 150, ConvertLosslessImages: true, Grayscale: true, RemoveMetadata: true), Target);

        Assert.True(result.Reduced);
        Assert.Null((await _engine.ReadInfoAsync(result.Output!.Path)).Title);

        var rendered = await _renderer.RenderPageAsync(result.Output.Path, 0, RenderSize.Fit(200, 280));
        for (var i = 0; i < rendered.Bgra.Length; i += 4 * 37)
        {
            Assert.InRange(Math.Abs(rendered.Bgra[i] - rendered.Bgra[i + 2]), 0, 3); // azul ≈ vermelho: sem cor
        }
    }

    [Fact]
    public async Task Invalid_files_and_cancellation_leave_nothing_behind()
    {
        var fake = _temp.File("falso.pdf", "isto nao e um pdf");
        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _compressor.CompressAsync(fake, CompressionSettings.For(CompressionProfile.Balanced), Target));
        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _compressor.CompressAsync(Path.Combine(_temp.Path, "nao-existe.pdf"), CompressionSettings.For(CompressionProfile.Balanced), Target));

        var pdf = ScannedPdf("cancelado");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _compressor.CompressAsync(pdf, CompressionSettings.For(CompressionProfile.Balanced), Target, null, cts.Token));
        Assert.False(Directory.Exists(OutFolder) && Directory.GetFiles(OutFolder).Length > 0);
    }
}

/// <summary>
/// Não é um teste de comportamento: gera os arquivos usados pela execução das ferramentas da
/// Fase 3 no CI. Só grava quando PDFMASTER_SMOKE_DIR está definida.
/// </summary>
public class SmokeSamplesPhase3
{
    [Fact]
    public async Task Writes_sample_files_for_the_conversion_tools()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMASTER_SMOKE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;

        Directory.CreateDirectory(folder);
        ImageSamples.Write(Path.Combine(folder, "Foto.png"), 800, 600, ImageSamples.Photo);
        ImageSamples.Write(Path.Combine(folder, "Recibo.jpg"), 600, 800, (x, y) => y % 40 < 3 ? ((byte)60, (byte)60, (byte)60) : ((byte)250, (byte)250, (byte)245));

        var lines = Enumerable.Range(1, 60).Select(i => $"Linha {i} das notas de reunião: decisão, ação e prazo.");
        File.WriteAllText(Path.Combine(folder, "Notas.txt"), string.Join("\r\n", lines), new UTF8Encoding(false));

        DocxWriter.Write(Path.Combine(folder, "Carta.docx"),
            [["Carta de apresentação", "Este documento foi criado para testar a conversão de Word para PDF."], ["Segunda página da carta."]]);

        var planner = new OutputPlanner(new OutputPathResolver());
        var target = new OutputTarget(Path.Combine(folder, "tmp"), OverwritePolicy.Overwrite, SubfolderMode.None);
        var textPdf = await new TextToPdfConverter(planner).ConvertAsync(Path.Combine(folder, "Notas.txt"), new TextToPdfOptions(Monospace: false), target);
        File.Copy(textPdf.Path, Path.Combine(folder, "Texto.pdf"), overwrite: true);

        var scan = ImageSamples.Write(Path.Combine(folder, "tmp", "pagina.png"), 1600, 2200, ImageSamples.Photo);
        ImageSamples.PdfWithImage(Path.Combine(folder, "Digitalizado.pdf"), scan);
        Directory.Delete(Path.Combine(folder, "tmp"), recursive: true);
    }
}
