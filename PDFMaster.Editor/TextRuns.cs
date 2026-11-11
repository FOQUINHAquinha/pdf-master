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

    internal static IReadOnlyList<TextRun> Read(Page page, int pageIndex)
    {
        var frame = FrameOf(page);
        var words = new List<(ViewRect Rect, string Text, double Size, string Font)>();
        foreach (var word in page.GetWords())
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            var b = word.BoundingBox;
            var corners = new[]
            {
                frame.ToView(b.Left, b.Bottom), frame.ToView(b.Right, b.Bottom),
                frame.ToView(b.Left, b.Top), frame.ToView(b.Right, b.Top),
            };
            var rect = EditItem.BoundsOf(corners);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var first = word.Letters.Count > 0 ? word.Letters[0] : null;
            words.Add((rect, word.Text, first?.PointSize ?? rect.Height, first?.FontName ?? string.Empty));
        }

        // Linhas: palavras cujos centros verticais estão próximos.
        var lines = new List<List<(ViewRect Rect, string Text, double Size, string Font)>>();
        foreach (var word in words.OrderBy(w => w.Rect.Center.Y).ThenBy(w => w.Rect.X))
        {
            var line = lines.FirstOrDefault(l =>
            {
                var reference = l[0].Rect;
                return Math.Abs(reference.Center.Y - word.Rect.Center.Y) < Math.Min(reference.Height, word.Rect.Height) * 0.5;
            });
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        var runs = new List<TextRun>();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(w => w.Rect.X).ToList();
            var current = new List<(ViewRect Rect, string Text, double Size, string Font)> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = current[^1];
                var gap = ordered[i].Rect.X - previous.Rect.Right;
                // Um espaço grande separa colunas ou campos: vira outro trecho.
                if (gap > Math.Max(previous.Rect.Height, ordered[i].Rect.Height) * 2.5)
                {
                    runs.Add(ToRun(pageIndex, current));
                    current = [];
                }

                current.Add(ordered[i]);
            }

            runs.Add(ToRun(pageIndex, current));
        }

        return runs.OrderBy(r => r.Rect.Y).ThenBy(r => r.Rect.X).ToList();
    }

    private static TextRun ToRun(int pageIndex, List<(ViewRect Rect, string Text, double Size, string Font)> words)
    {
        var x = words.Min(w => w.Rect.X);
        var y = words.Min(w => w.Rect.Y);
        var rect = new ViewRect(x, y, words.Max(w => w.Rect.Right) - x, words.Max(w => w.Rect.Bottom) - y);
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
        var words = page.GetWords()
            .Select(w =>
            {
                var b = w.BoundingBox;
                return EditItem.BoundsOf([frame.ToView(b.Left, b.Bottom), frame.ToView(b.Right, b.Top), frame.ToView(b.Left, b.Top), frame.ToView(b.Right, b.Bottom)]);
            })
            .Where(r => r.Width > 0 && area.Contains(r.Center))
            .ToList();
        if (words.Count == 0) return [area];

        var lines = new List<List<ViewRect>>();
        foreach (var word in words.OrderBy(w => w.Center.Y))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Center.Y - word.Center.Y) < Math.Min(l[0].Height, word.Height) * 0.5);
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        return lines.Select(l =>
        {
            var x = l.Min(r => r.X);
            var y = l.Min(r => r.Y);
            return new ViewRect(x, y, l.Max(r => r.Right) - x, l.Max(r => r.Bottom) - y);
        }).ToList();
    }
}
