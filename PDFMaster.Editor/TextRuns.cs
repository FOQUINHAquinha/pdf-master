using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PDFMaster.Editor;

/// <summary>Trecho de texto que já existe no PDF (uma linha, ou parte dela quando há colunas).</summary>
/// <param name="Rect">Posição na página exibida.</param>
/// <param name="FontSize">Tamanho aproximado da fonte, em pontos.</param>
public sealed record TextRun(int PageIndex, ViewRect Rect, string Text, double FontSize, string FontName);

/// <summary>Lê as palavras de uma página (PdfPig) e as agrupa em linhas, no espaço de exibição.</summary>
public static class TextRuns
{
    public static IReadOnlyList<TextRun> Read(string path, int pageIndex)
    {
        using var document = PdfDocument.Open(path);
        if (pageIndex < 0 || pageIndex >= document.NumberOfPages) return [];
        return Read(document.GetPage(pageIndex + 1), pageIndex);
    }

    public static PageFrame FrameOf(Page page)
    {
        var crop = page.CropBox.Bounds;
        return new PageFrame(crop.Left, crop.Bottom, crop.Right, crop.Top, page.Rotation.Value);
    }

    /// <summary>Palavra no espaço do PDF (sem a rotação de exibição), onde as linhas são horizontais.</summary>
    private readonly record struct PdfWord(double Left, double Bottom, double Right, double Top, string Text, double Size, string Font)
    {
        public double Height => Top - Bottom;
        public double CenterY => (Top + Bottom) / 2;
    }

    private static List<PdfWord> Words(Page page) =>
        page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.BoundingBox.Width > 0 && w.BoundingBox.Height > 0)
            .Select(w =>
            {
                var b = w.BoundingBox;
                var first = w.Letters.Count > 0 ? w.Letters[0] : null;
                return new PdfWord(b.Left, b.Bottom, b.Right, b.Top, w.Text, first?.PointSize ?? b.Height, first?.FontName ?? string.Empty);
            })
            .ToList();

    /// <summary>Linhas: palavras cujos centros verticais estão próximos, da esquerda para a direita.</summary>
    private static List<List<PdfWord>> Lines(IEnumerable<PdfWord> words)
    {
        var lines = new List<List<PdfWord>>();
        foreach (var word in words.OrderByDescending(w => w.CenterY).ThenBy(w => w.Left))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].CenterY - word.CenterY) < Math.Min(l[0].Height, word.Height) * 0.5);
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        foreach (var line in lines) line.Sort((a, b) => a.Left.CompareTo(b.Left));
        return lines;
    }

    private static ViewRect ToView(PageFrame frame, double left, double bottom, double right, double top) =>
        EditItem.BoundsOf([frame.ToView(left, bottom), frame.ToView(right, bottom), frame.ToView(left, top), frame.ToView(right, top)]);

    internal static IReadOnlyList<TextRun> Read(Page page, int pageIndex)
    {
        var frame = FrameOf(page);
        var runs = new List<TextRun>();
        foreach (var line in Lines(Words(page)))
        {
            var current = new List<PdfWord> { line[0] };
            for (var i = 1; i < line.Count; i++)
            {
                var previous = current[^1];
                // Um espaço grande separa colunas ou campos: vira outro trecho.
                if (line[i].Left - previous.Right > Math.Max(previous.Height, line[i].Height) * 2.5)
                {
                    runs.Add(ToRun(frame, pageIndex, current));
                    current = [];
                }

                current.Add(line[i]);
            }

            runs.Add(ToRun(frame, pageIndex, current));
        }

        return runs.OrderBy(r => r.Rect.Y).ThenBy(r => r.Rect.X).ToList();
    }

    private static TextRun ToRun(PageFrame frame, int pageIndex, List<PdfWord> words)
    {
        var rect = ToView(frame, words.Min(w => w.Left), words.Min(w => w.Bottom), words.Max(w => w.Right), words.Max(w => w.Top));
        var size = words.GroupBy(w => Math.Round(w.Size, 1)).OrderByDescending(g => g.Count()).First().Key;
        return new TextRun(pageIndex, rect, string.Join(' ', words.Select(w => w.Text)), size, words[0].Font);
    }

    /// <summary>Trecho sob o ponto clicado (ou null).</summary>
    public static TextRun? At(IReadOnlyList<TextRun> runs, ViewPoint point) =>
        runs.Where(r => r.Rect.Inflate(2).Contains(point)).OrderBy(r => r.Rect.Width * r.Rect.Height).FirstOrDefault();

    /// <summary>
    /// Retângulos de texto dentro de uma área arrastada, um por linha (para marca-texto, sublinhado e
    /// tachado). Sem texto na área (página digitalizada), devolve a própria área.
    /// </summary>
    public static IReadOnlyList<ViewRect> Quads(string path, int pageIndex, ViewRect area)
    {
        using var document = PdfDocument.Open(path);
        if (pageIndex < 0 || pageIndex >= document.NumberOfPages) return [area];
        var page = document.GetPage(pageIndex + 1);
        var frame = FrameOf(page);
        var inside = Words(page).Where(w => area.Contains(ToView(frame, w.Left, w.Bottom, w.Right, w.Top).Center)).ToList();
        if (inside.Count == 0) return [area];
        return Lines(inside)
            .Select(l => ToView(frame, l.Min(w => w.Left), l.Min(w => w.Bottom), l.Max(w => w.Right), l.Max(w => w.Top)))
            .ToList();
    }
}
