using System.Text;
using PDFMaster.PdfEngine;
using Tesseract;

namespace PDFMaster.OCR;

/// <summary>Uma palavra reconhecida. Coordenadas em pixels da imagem entregue ao OCR.</summary>
public sealed record OcrWord(
    string Text,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double LineTop,
    double LineBottom,
    float Confidence,
    bool StartsLine,
    bool StartsParagraph);

/// <param name="Text">Texto da página: linhas separadas por quebra, parágrafos por linha em branco.</param>
public sealed record OcrPageText(IReadOnlyList<OcrWord> Words, string Text)
{
    /// <summary>Palavras com pelo menos duas letras ou dígitos e confiança de 60% ou mais.</summary>
    public int GoodWords => Words.Count(IsGood);

    /// <summary>Soma das confianças das palavras boas; usada para comparar orientações.</summary>
    public double Score => Words.Where(IsGood).Sum(w => w.Confidence / 100.0);

    public double MeanConfidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence) / 100.0;

    private static bool IsGood(OcrWord word) =>
        word.Confidence >= 60 && word.Text.Count(char.IsLetterOrDigit) >= 2;
}

/// <summary>
/// Um mecanismo do Tesseract carregado com os idiomas escolhidos. Não é seguro para uso
/// simultâneo: reconheça uma página de cada vez e descarte ao terminar.
/// </summary>
public sealed class OcrSession : IDisposable
{
    private readonly TesseractEngine _engine;

    /// <param name="languages">Códigos separados por "+", ex.: "por+eng".</param>
    public OcrSession(OcrRuntime runtime, string languages)
    {
        runtime.EnsureReady();
        var installed = runtime.InstalledLanguages().Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (installed.Count == 0) throw new PdfEngineException(OcrRuntime.NoLanguagesMessage);

        var requested = languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0) throw new PdfEngineException("Escolha pelo menos um idioma para o OCR.");
        var missing = requested.Where(code => !installed.Contains(code)).ToList();
        if (missing.Count > 0)
            throw new PdfEngineException($"Idioma de OCR não instalado: {string.Join(", ", missing)}.");

        try
        {
            _engine = new TesseractEngine(runtime.ModelsDirectory, string.Join("+", requested), EngineMode.Default);
        }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or BadImageFormatException
                                       or IOException or TypeInitializationException or System.Reflection.TargetInvocationException)
        {
            throw new PdfEngineException(
                "O mecanismo de OCR (Tesseract) não pôde ser iniciado. Detalhe: " + (ex.InnerException?.Message ?? ex.Message), ex);
        }
    }

    public OcrPageText Recognize(GrayImage image, int dpi)
    {
        try
        {
            _engine.SetVariable("user_defined_dpi", Math.Clamp(dpi, 70, 2400).ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var pix = Pix.LoadFromMemory(image.ToBmp(dpi));
            using var page = _engine.Process(pix, PageSegMode.Auto);
            using var iterator = page.GetIterator();

            var words = new List<OcrWord>();
            var text = new StringBuilder();
            iterator.Begin();
            do
            {
                if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;
                var word = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrEmpty(word)) continue;

                var startsParagraph = iterator.IsAtBeginningOf(PageIteratorLevel.Para);
                var startsLine = startsParagraph || iterator.IsAtBeginningOf(PageIteratorLevel.TextLine);
                double lineTop = box.Y1, lineBottom = box.Y2;
                if (iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var line) && line.Height > 0)
                {
                    // Linhas detectadas com altura absurda (ex.: borda da página) não servem de referência.
                    if (line.Height <= box.Height * 4)
                    {
                        lineTop = line.Y1;
                        lineBottom = line.Y2;
                    }
                }

                if (text.Length > 0) text.Append(startsParagraph ? "\n\n" : startsLine ? "\n" : " ");
                text.Append(word);
                words.Add(new OcrWord(word, box.X1, box.Y1, box.X2, box.Y2, lineTop, lineBottom,
                    iterator.GetConfidence(PageIteratorLevel.Word), startsLine, startsParagraph));
            }
            while (iterator.Next(PageIteratorLevel.Word));

            return new OcrPageText(words, text.ToString());
        }
        catch (TesseractException ex)
        {
            throw new PdfEngineException("O OCR falhou nesta página. Detalhe: " + ex.Message, ex);
        }
    }

    public void Dispose() => _engine.Dispose();
}
