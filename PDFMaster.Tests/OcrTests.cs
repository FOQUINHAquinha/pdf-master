using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf.IO;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.OCR;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PDFMaster.Scanner;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Documentos "digitalizados" de verdade: páginas que são só imagem, sem camada de texto.</summary>
public static class OcrSamples
{
    public static readonly string[] Lines =
    [
        "Contrato de locação número 12345",
        "Assinado em Brasília no dia 10 de março.",
        "O locatário pagará o aluguel mensalmente.",
    ];

    /// <summary>Imagem PNG de uma página A4 com o texto de <see cref="Lines"/> (fonte de 16 pt).</summary>
    public static async Task<string> PageImage(string folder, string name, int dpi = 200, Func<GrayImage, GrayImage>? transform = null)
    {
        Directory.CreateDirectory(folder);
        var work = Path.Combine(folder, name + "-tmp");
        Directory.CreateDirectory(work);
        var txt = Path.Combine(work, "texto.txt");
        File.WriteAllText(txt, string.Join("\r\n", Lines), new UTF8Encoding(false));

        var planner = new OutputPlanner(new OutputPathResolver());
        var textPdf = await new TextToPdfConverter(planner).ConvertAsync(txt, new TextToPdfOptions(Monospace: false, FontSize: 16),
            new OutputTarget(work, OverwritePolicy.Overwrite, SubfolderMode.None));
        var rendered = await new PdfiumRenderer().RenderPageAsync(textPdf.Path, 0, RenderSize.ByScale(dpi / 72.0));

        var gray = GrayImage.FromBgra(rendered.Width, rendered.Height, rendered.Bgra);
        if (transform is not null) gray = transform(gray);
        var source = BitmapSource.Create(gray.Width, gray.Height, dpi, dpi, PixelFormats.Gray8, null, gray.Pixels, gray.Width);
        var png = Path.Combine(folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = new FileStream(png, FileMode.Create))
        {
            encoder.Save(stream);
        }

        Directory.Delete(work, recursive: true);
        return png;
    }

    /// <summary>PDF A4 de uma página cuja única coisa é a imagem do texto.</summary>
    public static async Task<string> ScannedPdf(string folder, string name, int dpi = 200, int rotate = 0)
    {
        var png = await PageImage(folder, name + "-img", dpi);
        var pdf = ImageSamples.PdfWithImage(Path.Combine(folder, name + ".pdf"), png);
        File.Delete(png);
        if (rotate == 0) return pdf;

        var rotated = Path.Combine(folder, name + "-girado.pdf");
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Rotate = rotate;
            document.Save(rotated);
        }

        File.Delete(pdf);
        return rotated;
    }
}

public class OcrImageTests
{
    private static GrayImage Bars(int width = 800, int height = 600)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (var y = 60; y < height - 60; y++)
        {
            if (y % 40 >= 6) continue;
            for (var x = 100; x < width - 100; x++) pixels[y * width + x] = 0;
        }

        return new GrayImage(width, height, pixels);
    }

    private static GrayImage Dot(int width, int height, int x, int y)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        pixels[y * width + x] = 0;
        return new GrayImage(width, height, pixels);
    }

    private static (int X, int Y) Darkest(GrayImage image)
    {
        var index = Array.IndexOf(image.Pixels, image.Pixels.Min());
        return (index % image.Width, index / image.Width);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Quarter_turns_match_the_coordinate_mapping(int degrees)
    {
        var source = Dot(40, 30, 7, 4);
        var rotated = source.Rotate90(degrees);

        Assert.Equal(degrees == 180 ? (40, 30) : (30, 40), (rotated.Width, rotated.Height));
        var (u, v) = Darkest(rotated);
        var (x, y) = OcrGeometry.Orientation(degrees, source.Width, source.Height).Apply(u, v);
        Assert.InRange(x, 7 - 1.01, 7 + 1.01);
        Assert.InRange(y, 4 - 1.01, 4 + 1.01);

        // Sentido horário: o canto superior esquerdo vai para o superior direito em 90°.
        if (degrees == 90) Assert.Equal((30 - 1 - 4, 7), (u, v));
    }

    [Fact]
    public void Free_rotation_matches_the_deskew_mapping()
    {
        var source = new GrayImage(200, 160, Enumerable.Repeat((byte)255, 200 * 160).ToArray());
        for (var dy = 0; dy < 3; dy++)
        {
            for (var dx = 0; dx < 3; dx++) source.Pixels[(40 + dy) * 200 + 150 + dx] = 0;
        }

        var rotated = source.Rotate(5);
        var (p, q) = Darkest(rotated);
        var (x, y) = OcrGeometry.Deskew(5, 200, 160).Apply(p, q);
        Assert.InRange(x, 150 - 2, 153 + 2);
        Assert.InRange(y, 40 - 2, 43 + 2);
        Assert.Same(source, source.Rotate(0));
    }

    [Fact]
    public void Skew_is_estimated_and_corrected()
    {
        var straight = Bars();
        Assert.Equal(0, straight.EstimateSkew());

        var skewed = straight.Rotate(3);
        var estimate = skewed.EstimateSkew();
        Assert.InRange(estimate, -3.6, -2.4); // o ângulo que desfaz a rotação aplicada

        Assert.InRange(skewed.Rotate(estimate).EstimateSkew(), -0.5, 0.5);
        Assert.Equal(0, Dot(400, 400, 10, 10).EstimateSkew()); // página praticamente em branco
    }

    [Fact]
    public void Contrast_and_noise_filters()
    {
        var faded = new GrayImage(100, 100, Enumerable.Range(0, 10000).Select(i => (byte)(i % 100 < 50 ? 110 : 170)).ToArray());
        var stretched = faded.StretchContrast();
        Assert.True(stretched.Pixels.Min() < 10 && stretched.Pixels.Max() > 245);

        var noisy = Dot(50, 50, 25, 25);
        Assert.Equal(255, noisy.Median3().Pixels[25 * 50 + 25]);
    }

    [Fact]
    public void Bmp_encoding_is_readable_and_keeps_pixels_and_resolution()
    {
        var image = new GrayImage(5, 3, [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140]);
        var decoder = new BmpBitmapDecoder(new MemoryStream(image.ToBmp(300)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        var pixels = new byte[15];
        gray.CopyPixels(pixels, 5, 0);

        Assert.Equal((5, 3), (frame.PixelWidth, frame.PixelHeight));
        Assert.Equal(image.Pixels, pixels);
        Assert.InRange(frame.DpiX, 299, 301);
    }

    [Fact]
    public void Page_mapping_follows_the_pdf_rotation_rules()
    {
        // Página de 600×800 pt renderizada a 2 px por ponto; canto superior esquerdo da imagem exibida.
        Assert.Equal((10.0, 820.0), OcrGeometry.RenderToPage(0, 10, 20, 600, 800, 1200, 1600).Apply(0, 0));
        Assert.Equal((610.0, 20.0), OcrGeometry.RenderToPage(0, 10, 20, 600, 800, 1200, 1600).Apply(1200, 1600));
        // /Rotate 90: a origem do PDF aparece no canto superior esquerdo.
        Assert.Equal((10.0, 20.0), OcrGeometry.RenderToPage(90, 10, 20, 600, 800, 1600, 1200).Apply(0, 0));
        Assert.Equal((610.0, 20.0), OcrGeometry.RenderToPage(90, 10, 20, 600, 800, 1600, 1200).Apply(0, 1200));
        Assert.Equal((610.0, 20.0), OcrGeometry.RenderToPage(180, 10, 20, 600, 800, 1200, 1600).Apply(0, 0));
        // /Rotate 270: a origem do PDF aparece no canto inferior direito.
        Assert.Equal((10.0, 20.0), OcrGeometry.RenderToPage(270, 10, 20, 600, 800, 1600, 1200).Apply(1600, 1200));

        var composed = new Affine(2, 0, 0, 2, 5, 7).After(new Affine(1, 0, 0, 1, 10, 20));
        Assert.Equal((25.0, 47.0), composed.Apply(0, 0));
    }

    [Fact]
    public void Text_layer_is_invisible_text_in_winansi()
    {
        Assert.Equal(new byte[] { 0x61, 0xE7, 0xE3, 0x6F, 0x20, 0x80, 0x3F }, PdfTextLayer.Encode("ação €中"));

        var word = new OcrWord("Olá", 100, 200, 160, 220, 198, 222, 95, true, true);
        var content = Encoding.ASCII.GetString(PdfTextLayer.BuildContent([word], OcrGeometry.RenderToPage(0, 0, 0, 600, 800, 1200, 1600)));

        Assert.StartsWith("q BT 3 Tr /PMOcr 1 Tf", content);
        Assert.Contains("<4F6CE120> Tj", content);
        Assert.EndsWith("ET Q\n", content);
        // Origem: x = 100 px ÷ 2 = 50 pt; linha de base um pouco acima de 222 px → cerca de 691,6 pt.
        Assert.Contains(" 50 691.64 Tm", content);
    }
}

/// <summary>
/// Reconhecimento de verdade com o Tesseract. No CI os modelos de idioma são baixados antes da
/// compilação e a ausência deles é uma falha; em outros computadores, sem os modelos, estes
/// testes não verificam nada.
/// </summary>
public class OcrRecognitionTests : IDisposable
{
    private static readonly OcrRuntime Runtime = new(Path.Combine(Path.GetTempPath(), "pdfm-tests-ocr-runtime"));

    private readonly TempFolder _temp = new();
    private readonly PdfSharpEngine _engine = new();
    private readonly PdfiumRenderer _renderer = new();
    private readonly OcrService _service;

    public OcrRecognitionTests()
    {
        var planner = new OutputPlanner(new OutputPathResolver());
        _service = new OcrService(Runtime, _engine, _renderer, new ImagesToPdfConverter(planner), planner);
    }

    public void Dispose() => _temp.Dispose();

    private string OutFolder => Path.Combine(_temp.Path, "saida");
    private OutputTarget Target => new(OutFolder, OverwritePolicy.AutoRename, SubfolderMode.None);
    private static OcrOptions Options => new("por", Dpi: 200);

    private static bool Available()
    {
        if (Runtime.InstalledLanguages().Any(l => l.Code == "por")) return true;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("PDFMASTER_REQUIRE_OCR"), "1", StringComparison.Ordinal),
            "Os modelos de idioma do OCR deveriam estar incorporados neste ambiente de teste.");
        return false;
    }

    private static string AllText(string pdf) => string.Join("\n", PdfTextExporter.ReadPages(pdf));

    /// <summary>A palavra "Contrato" da camada de texto deve ficar sobre a palavra da imagem.</summary>
    private static void AssertFirstWordPosition(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var word = document.GetPage(1).GetWords().First(w => w.Text.StartsWith("Contrato", StringComparison.Ordinal));
        // No original: margem esquerda de 56,7 pt e linha de base a 72,7 pt do topo (842 − 72,7 = 769,3).
        Assert.InRange(word.BoundingBox.Left, 48, 66);
        Assert.InRange(word.BoundingBox.Right, 100, 140);
        Assert.InRange(word.BoundingBox.Bottom, 758, 778);
        Assert.InRange(word.BoundingBox.Top, 770, 794);
    }

    [Fact]
    public async Task Scanned_pdf_becomes_searchable_and_looks_the_same()
    {
        if (!Available()) return;
        var pdf = await OcrSamples.ScannedPdf(_temp.Path, "contrato");
        var before = File.ReadAllBytes(pdf);
        Assert.DoesNotContain("12345", AllText(pdf));

        var progress = new List<OcrProgress>();
        var result = await _service.RecognizeAsync(pdf, Options, Target, new SyncOcrProgress(progress.Add));

        var output = Assert.Single(result.Files);
        Assert.Equal("contrato_ocr.pdf", Path.GetFileName(output.Path));
        Assert.Equal((1, 0, 0), (result.PagesRecognized, result.PagesSkipped, result.PagesRotated));
        Assert.True(result.Words >= 15, $"palavras: {result.Words}");
        Assert.True(result.MeanConfidence > 0.7, $"confiança: {result.MeanConfidence}");
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Equal(1, p.PageCount));

        var text = AllText(output.Path);
        Assert.Contains("Contrato", text);
        Assert.Contains("12345", text);
        Assert.Contains("locação", text);
        Assert.Contains("Brasília", text);
        AssertFirstWordPosition(output.Path);

        // O original não muda e a página continua com a mesma aparência (o texto é invisível).
        Assert.Equal(before, File.ReadAllBytes(pdf));
        var a = await _renderer.RenderPageAsync(pdf, 0, RenderSize.Fit(400, 566));
        var b = await _renderer.RenderPageAsync(output.Path, 0, RenderSize.Fit(400, 566));
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long difference = 0;
        for (var i = 0; i < a.Bgra.Length; i++) difference += Math.Abs(a.Bgra[i] - b.Bgra[i]);
        Assert.True(difference / (double)a.Bgra.Length < 0.5, "a camada de texto não deveria aparecer na página");
        Assert.Empty(Directory.GetFiles(OutFolder, "*.pdfmaster-tmp"));

        // Uma segunda passada não tem o que fazer: o documento já é pesquisável.
        var again = await Assert.ThrowsAsync<PdfEngineException>(() => _service.RecognizeAsync(output.Path, Options, Target));
        Assert.Contains("já tem texto", again.Message);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Sideways_and_upside_down_pages_are_turned_upright(int rotation)
    {
        if (!Available()) return;
        var pdf = await OcrSamples.ScannedPdf(_temp.Path, "deitado", rotate: rotation);

        var result = await _service.RecognizeAsync(pdf, Options, Target);

        Assert.Equal(1, result.PagesRotated);
        var output = result.Files[0].Path;
        var info = await _engine.ReadInfoAsync(output);
        Assert.Equal(0, info.Pages[0].Rotation);
        Assert.Contains("12345", AllText(output));
        // A camada fica no sistema de coordenadas da página: mesma posição do documento em pé.
        AssertFirstWordPosition(output);

        // Sem a correção de orientação, a página fica como estava.
        var kept = await _service.RecognizeAsync(pdf, Options with { AutoRotate = false }, Target);
        Assert.Equal(0, kept.PagesRotated);
        Assert.Equal(rotation, (await _engine.ReadInfoAsync(kept.Files[0].Path)).Pages[0].Rotation);
    }

    [Fact]
    public async Task Image_is_recognized_into_pdf_text_and_word_files()
    {
        if (!Available()) return;
        var png = await OcrSamples.PageImage(_temp.Path, "foto");

        var result = await _service.RecognizeAsync(png, Options with { TextFile = true, Docx = true }, Target);

        Assert.Equal(new[] { "foto_ocr.pdf", "foto_ocr.txt", "foto_ocr.docx" }, result.Files.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.Contains("12345", AllText(result.Files[0].Path));

        var txt = File.ReadAllText(result.Files[1].Path);
        Assert.Contains("Contrato de locação", txt);
        Assert.Contains("aluguel mensalmente", txt);

        using var zip = ZipFile.OpenRead(result.Files[2].Path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("12345", reader.ReadToEnd());
        Assert.True(File.Exists(png));
    }

    [Fact]
    public async Task Skewed_scan_is_straightened_before_recognition()
    {
        if (!Available()) return;
        var png = await OcrSamples.PageImage(_temp.Path, "torto", transform: image => image.Rotate(4));

        var result = await _service.RecognizeAsync(png, Options with { SearchablePdf = false, TextFile = true }, Target);

        var txt = File.ReadAllText(Assert.Single(result.Files).Path);
        Assert.Contains("Contrato", txt);
        Assert.Contains("12345", txt);
        Assert.Contains("mensalmente", txt);
    }

    [Fact]
    public async Task Pages_that_already_have_text_are_not_recognized_again()
    {
        if (!Available()) return;
        var txt = Path.Combine(_temp.Path, "digital.txt");
        File.WriteAllText(txt, "Documento digital com texto de verdade, protocolo 778899.");
        var planner = new OutputPlanner(new OutputPathResolver());
        var pdf = (await new TextToPdfConverter(planner).ConvertAsync(txt, new TextToPdfOptions(Monospace: false),
            new OutputTarget(Path.Combine(_temp.Path, "origem"), OverwritePolicy.AutoRename, SubfolderMode.None))).Path;

        var error = await Assert.ThrowsAsync<PdfEngineException>(() => _service.RecognizeAsync(pdf, Options, Target));
        Assert.Contains("já tem texto", error.Message);
        Assert.False(Directory.Exists(OutFolder) && Directory.GetFiles(OutFolder).Length > 0);

        var text = await _service.RecognizeAsync(pdf, Options with { SearchablePdf = false, TextFile = true }, Target);
        Assert.Equal((0, 1), (text.PagesRecognized, text.PagesSkipped));
        Assert.Contains("778899", File.ReadAllText(Assert.Single(text.Files).Path));

        // Forçando, a página passa pelo OCR mesmo já tendo texto.
        var forced = await _service.RecognizeAsync(pdf, Options with { SkipPagesWithText = false }, Target);
        Assert.Equal(1, forced.PagesRecognized);
    }

    [Fact]
    public async Task Invalid_requests_and_cancellation_leave_nothing_behind()
    {
        if (!Available()) return;
        var pdf = await OcrSamples.ScannedPdf(_temp.Path, "erros");

        await Assert.ThrowsAsync<PdfEngineException>(() => _service.RecognizeAsync(pdf, Options with { Languages = "xyz" }, Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _service.RecognizeAsync(pdf, Options with { Dpi = 5000 }, Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _service.RecognizeAsync(pdf, Options with { SearchablePdf = false }, Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _service.RecognizeAsync(_temp.File("falso.pdf", "nao e pdf"), Options, Target));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RecognizeAsync(pdf, Options, Target, null, cts.Token));
        Assert.False(Directory.Exists(OutFolder) && Directory.GetFiles(OutFolder).Length > 0);
    }

    [Fact]
    public void Languages_are_listed_and_installed_from_a_file()
    {
        if (!Available()) return;
        var codes = Runtime.InstalledLanguages().Select(l => l.Code).ToList();
        Assert.Equal(new[] { "por", "eng", "spa" }, codes.Take(3).ToArray());
        Assert.Equal("Português", Runtime.InstalledLanguages()[0].Name);

        Assert.Throws<PdfEngineException>(() => Runtime.InstallLanguage(_temp.File("idioma.txt", "x")));
        Assert.Throws<PdfEngineException>(() => Runtime.InstallLanguage(_temp.File("vazio.traineddata", "x")));
        Assert.Throws<PdfEngineException>(() => Runtime.InstallLanguage(_temp.File("nome invalido.traineddata", new byte[20000])));

        var copy = Path.Combine(_temp.Path, "tst.traineddata");
        File.Copy(Path.Combine(Runtime.ModelsDirectory, "eng.traineddata"), copy);
        try
        {
            Assert.Equal("tst", Runtime.InstallLanguage(copy).Code);
            Assert.Contains(Runtime.InstalledLanguages(), l => l.Code == "tst");
        }
        finally
        {
            File.Delete(Path.Combine(Runtime.ModelsDirectory, "tst.traineddata"));
        }
    }

    private sealed class SyncOcrProgress(Action<OcrProgress> handler) : IProgress<OcrProgress>
    {
        public void Report(OcrProgress value) => handler(value);
    }
}

public class ScannerTests
{
    [Fact]
    public async Task Listing_scanners_never_crashes_even_without_any_device()
    {
        var scanner = new WiaScanner();
        try
        {
            var devices = await scanner.ListDevicesAsync();
            Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id)));
        }
        catch (ScannerException ex)
        {
            // Computador sem o serviço WIA: o erro é informado em português, sem derrubar o programa.
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
    }

    [Fact]
    public async Task Scanning_with_an_unknown_device_reports_an_error()
    {
        using var temp = new TempFolder();
        var scanner = new WiaScanner();
        var error = await Assert.ThrowsAsync<ScannerException>(() =>
            scanner.ScanAsync("dispositivo-inexistente", new ScanSettings(), Path.Combine(temp.Path, "paginas")));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));

        await Assert.ThrowsAsync<ScannerException>(() =>
            scanner.ScanAsync("x", new ScanSettings(Dpi: 5), Path.Combine(temp.Path, "paginas")));
    }
}

/// <summary>Gera o documento digitalizado usado pela execução do OCR no CI (só com PDFMASTER_SMOKE_DIR).</summary>
public class SmokeSamplesPhase4
{
    [Fact]
    public async Task Writes_a_scanned_document_for_the_ocr_tool()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMASTER_SMOKE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        await OcrSamples.ScannedPdf(folder, "Escaneado");
    }
}
