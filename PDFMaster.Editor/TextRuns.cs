using System.Text;
using PdfSharp.Pdf.IO;
using PDFMaster.Rendering;

namespace PDFMaster.Editor;

/// <summary>Trecho de texto que já existe no PDF (uma linha, ou parte dela quando há colunas).</summary>
/// <param name="Rect">Posição na página exibida.</param>
/// <param name="FontSize">Tamanho aproximado da fonte, em pontos.</param>
public sealed record TextRun(int PageIndex, ViewRect Rect, string Text, double FontSize, string FontName);

/// <summary>
/// Texto de uma página com posição, lido pelo PDFium (o mesmo mecanismo que desenha a página) no
/// espaço do PDF, agrupado em palavras e linhas e convertido para a página exibida.
/// </summary>
public static class TextRuns
{
    /// <summary>Palavra no espaço do PDF (sem a rotação de exibição), onde as linhas do texto são horizontais.</summary>
    private readonly record struct PdfWord(double Left, double Bottom, double Right, double Top, string Text, double Size)
    {
        public double Height => Top - Bottom;
        public double CenterY => (Top + Bottom) / 2;
    }

    public static IReadOnlyList<TextRun> Read(string path, int pageIndex)
    {
        var loaded = Load(path, pageIndex);
        if (loaded is null) return [];
        var (frame, words) = loaded.Value;
        var runs = new List<TextRun>();
        foreach (var line in Lines(words))
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

    /// <summary>
    /// Retângulos de texto dentro de uma área arrastada, um por linha (para marca-texto, sublinhado e
    /// tachado). Sem texto na área (página digitalizada), devolve a própria área.
    /// </summary>
    public static IReadOnlyList<ViewRect> Quads(string path, int pageIndex, ViewRect area)
    {
        var loaded = Load(path, pageIndex);
        if (loaded is null) return [area];
        var (frame, words) = loaded.Value;
        var inside = words.Where(w => area.Contains(ToView(frame, w.Left, w.Bottom, w.Right, w.Top).Center)).ToList();
        if (inside.Count == 0) return [area];
        return Lines(inside)
            .Select(l => ToView(frame, l.Min(w => w.Left), l.Min(w => w.Bottom), l.Max(w => w.Right), l.Max(w => w.Top)).Inflate(1))
            .ToList();
    }

    /// <summary>Trecho sob o ponto clicado (ou null).</summary>
    public static TextRun? At(IReadOnlyList<TextRun> runs, ViewPoint point) =>
        runs.Where(r => r.Rect.Inflate(2).Contains(point)).OrderBy(r => r.Rect.Width * r.Rect.Height).FirstOrDefault();

    private static (PageFrame Frame, List<PdfWord> Words)? Load(string path, int pageIndex)
    {
        PageFrame frame;
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            if (pageIndex < 0 || pageIndex >= document.PageCount) return null;
            frame = PageFrame.Of(document.Pages[pageIndex]);
        }

        return (frame, Words(PdfiumRenderer.ReadChars(path, pageIndex)));
    }

    private static List<PdfWord> Words(IReadOnlyList<PdfChar> chars)
    {
        var words = new List<PdfWord>();
        var text = new StringBuilder();
        double left = 0, right = 0, bottom = 0, top = 0, size = 0;
        PdfChar? previous = null;

        void Flush()
        {
            if (text.Length > 0) words.Add(new PdfWord(left, bottom, right, top, text.ToString(), size));
            text.Clear();
            previous = null;
        }

        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Value) || char.IsControl(c.Value) || !c.HasBox)
            {
                Flush();
                continue;
            }

            if (previous is { } p)
            {
                var height = Math.Max(p.Top - p.Bottom, c.Top - c.Bottom);
                var sameLine = Math.Abs((p.Top + p.Bottom) / 2 - (c.Top + c.Bottom) / 2) < height * 0.6;
                var gap = c.Left - p.Right;
                if (!sameLine || gap > Math.Max(1, c.FontSize) * 0.3 || gap < -height) Flush();
            }

            if (text.Length == 0)
            {
                (left, right, bottom, top, size) = (c.Left, c.Right, c.Bottom, c.Top, c.FontSize);
            }
            else
            {
                left = Math.Min(left, c.Left);
                right = Math.Max(right, c.Right);
                bottom = Math.Min(bottom, c.Bottom);
                top = Math.Max(top, c.Top);
                size = Math.Max(size, c.FontSize);
            }

            text.Append(c.Value);
            previous = c;
        }

        Flush();
        return words;
    }

    /// <summary>Linhas: palavras cujos centros verticais estão próximos, da esquerda para a direita.</summary>
    private static List<List<PdfWord>> Lines(IEnumerable<PdfWord> words)
    {
        var lines = new List<List<PdfWord>>();
        foreach (var word in words.OrderByDescending(w => w.CenterY).ThenBy(w => w.Left))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].CenterY - word.CenterY) < Math.Min(l[0].Height, word.Height) * 0.6);
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        foreach (var line in lines) line.Sort((a, b) => a.Left.CompareTo(b.Left));
        return lines;
    }

    private static ViewRect ToView(PageFrame frame, double left, double bottom, double right, double top) =>
        EditItem.BoundsOf([frame.ToView(left, bottom), frame.ToView(right, bottom), frame.ToView(left, top), frame.ToView(right, top)]);

    private static TextRun ToRun(PageFrame frame, int pageIndex, List<PdfWord> words)
    {
        var rect = ToView(frame, words.Min(w => w.Left), words.Min(w => w.Bottom), words.Max(w => w.Right), words.Max(w => w.Top));
        var size = words.GroupBy(w => Math.Round(w.Size, 1)).OrderByDescending(g => g.Count()).First().Key;
        return new TextRun(pageIndex, rect, string.Join(' ', words.Select(w => w.Text)), size, string.Empty);
    }
}
