using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Comparison;

/// <summary>Retângulo na página exibida, em pontos (origem no canto superior esquerdo).</summary>
public readonly record struct DiffBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Intersects(DiffBox other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public static DiffBox Union(IEnumerable<DiffBox> boxes)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var b in boxes)
        {
            x0 = Math.Min(x0, b.X);
            y0 = Math.Min(y0, b.Y);
            x1 = Math.Max(x1, b.Right);
            y1 = Math.Max(y1, b.Bottom);
        }

        return x0 > x1 ? default : new DiffBox(x0, y0, x1 - x0, y1 - y0);
    }
}

public enum ChangeKind
{
    /// <summary>Texto que só existe no segundo documento.</summary>
    Added,

    /// <summary>Texto que só existe no primeiro documento.</summary>
    Removed,

    /// <summary>Texto trocado (removido de um lado e acrescentado no mesmo lugar do outro).</summary>
    Modified,

    PageInserted,
    PageRemoved,

    /// <summary>Diferença de pixels não explicada pelo texto (imagem, desenho, cor, formatação).</summary>
    Visual,

    /// <summary>Imagens da página diferentes (comparadas pelo conteúdo do arquivo de imagem).</summary>
    Images,
}

/// <summary>Uma diferença, com as áreas a destacar em cada documento.</summary>
/// <param name="PageA">Página no primeiro documento (a partir de 0), se houver.</param>
/// <param name="PageB">Página no segundo documento (a partir de 0), se houver.</param>
public sealed record DiffChange(int Index, ChangeKind Kind, int? PageA, int? PageB, string OldText, string NewText,
    IReadOnlyList<DiffBox> BoxesA, IReadOnlyList<DiffBox> BoxesB, string Description);

/// <summary>Par de páginas alinhadas (uma das duas ausente quando a página foi inserida ou removida).</summary>
/// <param name="Similarity">Semelhança do texto (0 a 1).</param>
/// <param name="VisualDifference">Fração de pixels diferentes (0 a 1); null quando não comparada.</param>
public sealed record PagePair(int? PageA, int? PageB, double Similarity, double? VisualDifference, bool SizeChanged);

public sealed record CompareOptions(bool CompareText = true, bool CompareVisual = true, bool CompareImages = true);

public sealed record ComparisonResult(
    string PathA, string PathB, IReadOnlyList<(double Width, double Height)> SizesA, IReadOnlyList<(double Width, double Height)> SizesB,
    IReadOnlyList<PagePair> Pairs, IReadOnlyList<DiffChange> Changes, bool TextAvailable)
{
    public int Count(ChangeKind kind) => Changes.Count(c => c.Kind == kind);

    public bool Identical => Changes.Count == 0;
}

/// <summary>
/// Comparação de dois PDFs: alinha as páginas pelo texto (detectando páginas inseridas e removidas),
/// compara as palavras de cada par (adições, remoções e trocas, com a posição de cada uma), as imagens
/// incorporadas e, opcionalmente, os pixels das páginas renderizadas.
/// </summary>
public static class DocumentComparer
{
    private const double RenderScale = 1.25;
    private const int Cell = 6;

    private sealed record Word(string Text, string Key, DiffBox Box);

    private sealed class PageInfo
    {
        public required List<Word> Words { get; init; }
        public required Dictionary<string, int> Counts { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public byte[]? Thumbnail { get; set; }
        public int ThumbnailWidth { get; set; }
    }

    public static Task<ComparisonResult> CompareAsync(string pathA, string pathB, CompareOptions? options = null, IProgress<double>? progress = null,
        CancellationToken ct = default) => Task.Run(async () =>
    {
        options ??= new CompareOptions();
        var fullA = Path.GetFullPath(pathA);
        var fullB = Path.GetFullPath(pathB);
        IReadOnlyList<DisplayPageText> textA, textB;
        try
        {
            textA = PdfiumRenderer.ReadDisplayText(fullA, ct);
            textB = PdfiumRenderer.ReadDisplayText(fullB, ct);
        }
        catch (PdfRenderException ex)
        {
            throw new PdfEngineException(ex.Message, ex);
        }

        progress?.Report(0.1);
        var pagesA = textA.Select(Page).ToList();
        var pagesB = textB.Select(Page).ToList();
        var textAvailable = pagesA.Any(p => p.Words.Count > 0) || pagesB.Any(p => p.Words.Count > 0);

        // Miniaturas para alinhar páginas sem texto (digitalizadas).
        await Thumbnails(fullA, pagesA, ct).ConfigureAwait(false);
        await Thumbnails(fullB, pagesB, ct).ConfigureAwait(false);
        progress?.Report(0.2);

        var pairs = Align(pagesA, pagesB, ct);
        var imagesA = options.CompareImages ? ImageFingerprints(fullA) : null;
        var imagesB = options.CompareImages ? ImageFingerprints(fullB) : null;

        var changes = new List<DiffChange>();
        var results = new List<PagePair>();
        void Add(ChangeKind kind, int? a, int? b, string oldText, string newText, IReadOnlyList<DiffBox> boxesA, IReadOnlyList<DiffBox> boxesB, string description) =>
            changes.Add(new DiffChange(changes.Count, kind, a, b, oldText, newText, boxesA, boxesB, description));

        for (var p = 0; p < pairs.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            var (a, b, similarity) = pairs[p];
            if (a is null)
            {
                var page = pagesB[b!.Value];
                Add(ChangeKind.PageInserted, null, b, string.Empty, Excerpt(page.Words), [], [new DiffBox(0, 0, page.Width, page.Height)],
                    $"Página {b + 1} inserida no segundo documento.");
                results.Add(new PagePair(null, b, 0, null, false));
                continue;
            }

            if (b is null)
            {
                var page = pagesA[a.Value];
                Add(ChangeKind.PageRemoved, a, null, Excerpt(page.Words), string.Empty, [new DiffBox(0, 0, page.Width, page.Height)], [],
                    $"Página {a + 1} do primeiro documento removida.");
                results.Add(new PagePair(a, null, 0, null, false));
                continue;
            }

            var pa = pagesA[a.Value];
            var pb = pagesB[b.Value];
            var textBoxesA = new List<DiffBox>();
            var textBoxesB = new List<DiffBox>();
            var moved = new List<DiffBox>();
            if (options.CompareText) CompareWords(pa, pb, a.Value, b.Value, Add, textBoxesA, textBoxesB, moved, ct);

            if (imagesA is not null && imagesB is not null)
            {
                var listA = a.Value < imagesA.Count ? imagesA[a.Value] : [];
                var listB = b.Value < imagesB.Count ? imagesB[b.Value] : [];
                var removed = Except(listA, listB);
                var added = Except(listB, listA);
                if (removed > 0 || added > 0)
                {
                    var text = (removed, added) switch
                    {
                        ( > 0, > 0) when removed == added => $"{removed} imagem(ns) trocada(s)",
                        ( > 0, > 0) => $"{removed} imagem(ns) removida(s) e {added} acrescentada(s)",
                        ( > 0, _) => $"{removed} imagem(ns) removida(s)",
                        _ => $"{added} imagem(ns) acrescentada(s)",
                    };
                    Add(ChangeKind.Images, a, b, string.Empty, string.Empty, [], [], $"Página {a + 1} → {b + 1}: {text}.");
                }
            }

            double? visual = null;
            var sizeChanged = Math.Abs(pa.Width - pb.Width) > 0.5 || Math.Abs(pa.Height - pb.Height) > 0.5;
            if (sizeChanged)
                Add(ChangeKind.Visual, a, b, string.Empty, string.Empty, [], [],
                    $"Tamanho da página mudou: {pa.Width:0} × {pa.Height:0} pt → {pb.Width:0} × {pb.Height:0} pt.");
            if (options.CompareVisual)
            {
                var (fraction, regions) = await VisualDiff(fullA, a.Value, fullB, b.Value, ct).ConfigureAwait(false);
                visual = fraction;
                var explained = textBoxesA.Concat(textBoxesB).Select(t => t with { X = 0, Width = Math.Max(pa.Width, pb.Width) }).Concat(moved).ToList();
                foreach (var region in regions)
                {
                    var area = region.Width * region.Height;
                    var covered = explained.Where(e => e.Intersects(region)).Sum(e => Overlap(e, region));
                    if (area <= 0 || covered / area >= 0.6) continue;
                    Add(ChangeKind.Visual, a, b, string.Empty, string.Empty, [region], [region],
                        $"Página {a + 1} → {b + 1}: diferença visual (imagem, desenho, cor ou formatação).");
                }
            }

            results.Add(new PagePair(a, b, similarity, visual, sizeChanged));
            progress?.Report(0.2 + 0.8 * (p + 1) / pairs.Count);
        }

        return new ComparisonResult(fullA, fullB, pagesA.Select(x => (x.Width, x.Height)).ToList(), pagesB.Select(x => (x.Width, x.Height)).ToList(),
            results, changes, textAvailable);
    }, ct);

    private static PageInfo Page(DisplayPageText text)
    {
        var words = new List<Word>();
        var builder = new StringBuilder();
        var boxes = new List<DiffBox>();
        void Flush()
        {
            if (builder.Length == 0) return;
            var value = builder.ToString();
            words.Add(new Word(value, value.Normalize(NormalizationForm.FormKC), DiffBox.Union(boxes)));
            builder.Clear();
            boxes.Clear();
        }

        foreach (var c in text.Chars)
        {
            if (c.Value == ' ' || c.Width <= 0 && c.Height <= 0)
            {
                Flush();
                continue;
            }

            // Um salto de linha sem espaço também separa palavras.
            if (boxes.Count > 0 && (c.Y > boxes[^1].Bottom || c.Y + c.Height < boxes[^1].Y || c.X + c.Width < boxes[^1].X - 1)) Flush();
            builder.Append(c.Value);
            boxes.Add(new DiffBox(c.X, c.Y, c.Width, c.Height));
        }

        Flush();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words) counts[w.Key] = counts.GetValueOrDefault(w.Key) + 1;
        return new PageInfo { Words = words, Counts = counts, Width = text.Width, Height = text.Height };
    }

    private static async Task Thumbnails(string path, List<PageInfo> pages, CancellationToken ct)
    {
        var indexes = Enumerable.Range(0, pages.Count).Where(i => pages[i].Words.Count < 3 && pages[i].Width > 0).ToList();
        if (indexes.Count == 0) return;
        var renderer = new PdfiumRenderer();
        await renderer.RenderPagesAsync(path, indexes, RenderSize.Fit(48, 64), (index, page) =>
        {
            var gray = new byte[page.Width * page.Height];
            for (var i = 0; i < gray.Length; i++) gray[i] = (byte)((page.Bgra[4 * i] + page.Bgra[4 * i + 1] + page.Bgra[4 * i + 2]) / 3);
            pages[index].Thumbnail = gray;
            pages[index].ThumbnailWidth = page.Width;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Semelhança entre duas páginas: palavras em comum ou, sem texto, aparência da miniatura.</summary>
    private static double Similarity(PageInfo a, PageInfo b)
    {
        var total = a.Words.Count + b.Words.Count;
        if (a.Words.Count >= 3 && b.Words.Count >= 3)
        {
            var (small, large) = a.Counts.Count <= b.Counts.Count ? (a.Counts, b.Counts) : (b.Counts, a.Counts);
            var common = small.Sum(pair => Math.Min(pair.Value, large.GetValueOrDefault(pair.Key)));
            return 2.0 * common / total;
        }

        if (a.Thumbnail is null || b.Thumbnail is null || a.Thumbnail.Length != b.Thumbnail.Length || a.ThumbnailWidth != b.ThumbnailWidth) return 0;
        long difference = 0;
        for (var i = 0; i < a.Thumbnail.Length; i++) difference += Math.Abs(a.Thumbnail[i] - b.Thumbnail[i]);
        return 1 - difference / (255.0 * a.Thumbnail.Length);
    }

    private static bool Matchable(PageInfo a, PageInfo b, double similarity) =>
        a.Words.Count >= 3 && b.Words.Count >= 3 ? similarity >= 0.35 : similarity >= 0.9;

    /// <summary>
    /// Alinhamento das páginas por programação dinâmica (maior soma de semelhanças, mantendo a ordem),
    /// numa faixa ao redor da diagonal. Páginas sem par são inseridas ou removidas.
    /// </summary>
    private static List<(int? A, int? B, double Similarity)> Align(List<PageInfo> a, List<PageInfo> b, CancellationToken ct)
    {
        int n = a.Count, m = b.Count;
        var result = new List<(int?, int?, double)>();
        if ((long)n * m > 4_000_000)
        {
            for (var i = 0; i < Math.Max(n, m); i++) result.Add((i < n ? i : null, i < m ? i : null, i < n && i < m ? Similarity(a[i], b[i]) : 0));
            return result;
        }

        const int band = 60;
        var score = new double[n + 1, m + 1];
        var sims = new Dictionary<(int, int), double>();
        for (var i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var center = (int)Math.Round((double)i * m / Math.Max(1, n));
            for (var j = 1; j <= m; j++)
            {
                var best = Math.Max(score[i - 1, j], score[i, j - 1]);
                if (Math.Abs(j - center) <= band)
                {
                    var s = Similarity(a[i - 1], b[j - 1]);
                    if (Matchable(a[i - 1], b[j - 1], s))
                    {
                        sims[(i - 1, j - 1)] = s;
                        best = Math.Max(best, score[i - 1, j - 1] + s);
                    }
                }

                score[i, j] = best;
            }
        }

        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && sims.TryGetValue((x - 1, y - 1), out var s) && Math.Abs(score[x, y] - (score[x - 1, y - 1] + s)) < 1e-9)
            {
                result.Add((x - 1, y - 1, s));
                x--;
                y--;
            }
            else if (x > 0 && (y == 0 || Math.Abs(score[x, y] - score[x - 1, y]) < 1e-9))
            {
                result.Add((x - 1, null, 0));
                x--;
            }
            else
            {
                result.Add((null, y - 1, 0));
                y--;
            }
        }

        result.Reverse();
        return result;
    }

    private static void CompareWords(PageInfo pa, PageInfo pb, int a, int b,
        Action<ChangeKind, int?, int?, string, string, IReadOnlyList<DiffBox>, IReadOnlyList<DiffBox>, string> add,
        List<DiffBox> boxesA, List<DiffBox> boxesB, List<DiffBox> moved, CancellationToken ct)
    {
        var keysA = pa.Words.Select(w => w.Key).ToList();
        var keysB = pb.Words.Select(w => w.Key).ToList();
        var edits = TextDiff.Diff(keysA, keysB, 4000, ct);
        if (edits is null)
        {
            // Diferentes demais para um alinhamento palavra a palavra: a página toda é a troca.
            var all = new List<Edit>();
            all.AddRange(Enumerable.Range(0, keysA.Count).Select(i => new Edit(EditKind.Delete, i, 0)));
            all.AddRange(Enumerable.Range(0, keysB.Count).Select(j => new Edit(EditKind.Insert, keysA.Count, j)));
            edits = all;
        }

        var removed = new List<Word>();
        var inserted = new List<Word>();
        void Flush()
        {
            if (removed.Count == 0 && inserted.Count == 0) return;
            var kind = removed.Count > 0 && inserted.Count > 0 ? ChangeKind.Modified : removed.Count > 0 ? ChangeKind.Removed : ChangeKind.Added;
            var oldText = string.Join(" ", removed.Select(w => w.Text));
            var newText = string.Join(" ", inserted.Select(w => w.Text));
            var ra = LineBoxes(removed);
            var rb = LineBoxes(inserted);
            boxesA.AddRange(ra);
            boxesB.AddRange(rb);
            var where = $"Página {a + 1} → {b + 1}";
            var description = kind switch
            {
                ChangeKind.Modified => $"{where}: \"{Short(oldText)}\" → \"{Short(newText)}\"",
                ChangeKind.Removed => $"{where}: removido \"{Short(oldText)}\"",
                _ => $"{where}: acrescentado \"{Short(newText)}\"",
            };
            add(kind, a, b, oldText, newText, ra, rb, description);
            removed.Clear();
            inserted.Clear();
        }

        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case EditKind.Delete:
                    removed.Add(pa.Words[edit.A]);
                    break;
                case EditKind.Insert:
                    inserted.Add(pb.Words[edit.B]);
                    break;
                default:
                    Flush();
                    var wa = pa.Words[edit.A].Box;
                    var wb = pb.Words[edit.B].Box;
                    if (Math.Abs(wa.X - wb.X) > 1 || Math.Abs(wa.Y - wb.Y) > 1)
                    {
                        moved.Add(wa);
                        moved.Add(wb);
                    }

                    break;
            }
        }

        Flush();
    }

    /// <summary>Uma caixa por linha (palavras vizinhas da mesma linha juntas).</summary>
    private static List<DiffBox> LineBoxes(List<Word> words)
    {
        var lines = new List<List<DiffBox>>();
        foreach (var w in words)
        {
            var line = lines.LastOrDefault();
            var last = line?[^1];
            if (line is null || last is null || Math.Abs(w.Box.Y + w.Box.Height / 2 - (last.Value.Y + last.Value.Height / 2)) > Math.Max(w.Box.Height, last.Value.Height) / 2 ||
                w.Box.X < last.Value.X)
            {
                lines.Add([w.Box]);
            }
            else
            {
                line.Add(w.Box);
            }
        }

        return lines.Select(l => DiffBox.Union(l)).Select(b => new DiffBox(b.X - 1, b.Y - 1, b.Width + 2, b.Height + 2)).ToList();
    }

    private static string Short(string text) => text.Length <= 80 ? text : text[..77] + "…";

    private static string Excerpt(List<Word> words) => Short(string.Join(" ", words.Take(30).Select(w => w.Text)));

    private static double Overlap(DiffBox a, DiffBox b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>Pixels diferentes entre as duas páginas e as regiões (em pontos) onde estão.</summary>
    private static async Task<(double Fraction, List<DiffBox> Regions)> VisualDiff(string pathA, int a, string pathB, int b, CancellationToken ct)
    {
        var renderer = new PdfiumRenderer();
        RenderedPage first, second;
        try
        {
            first = await renderer.RenderPageAsync(pathA, a, RenderSize.ByScale(RenderScale), ct).ConfigureAwait(false);
            second = await renderer.RenderPageAsync(pathB, b, RenderSize.ByScale(RenderScale), ct).ConfigureAwait(false);
        }
        catch (PdfRenderException)
        {
            return (0, []);
        }

        var width = Math.Min(first.Width, second.Width);
        var height = Math.Min(first.Height, second.Height);
        var columns = (width + Cell - 1) / Cell;
        var rows = (height + Cell - 1) / Cell;
        var cells = new int[columns * rows];
        long changed = 0;
        for (var y = 0; y < height; y++)
        {
            var rowA = y * first.Width * 4;
            var rowB = y * second.Width * 4;
            for (var x = 0; x < width; x++)
            {
                int i = rowA + 4 * x, j = rowB + 4 * x;
                var d = Math.Max(Math.Abs(first.Bgra[i] - second.Bgra[j]), Math.Max(Math.Abs(first.Bgra[i + 1] - second.Bgra[j + 1]), Math.Abs(first.Bgra[i + 2] - second.Bgra[j + 2])));
                if (d <= 48) continue;
                changed++;
                cells[y / Cell * columns + x / Cell]++;
            }
        }

        var total = (double)Math.Max(first.Width * first.Height, second.Width * second.Height);
        // Área fora da parte comum (página de tamanho diferente) também conta como diferença.
        changed += (long)(total - width * (double)height);
        var regions = Regions(cells, columns, rows)
            .Select(r => new DiffBox(r.X * Cell / RenderScale, r.Y * Cell / RenderScale, r.Width * Cell / RenderScale, r.Height * Cell / RenderScale))
            .ToList();
        return (changed / total, regions);
    }

    /// <summary>Grupos de células alteradas (vizinhas a até 3 células) como retângulos, em células.</summary>
    private static List<DiffBox> Regions(int[] cells, int columns, int rows)
    {
        const int minimumPixels = 2;
        const int reach = 3;
        var seen = new bool[cells.Length];
        var regions = new List<DiffBox>();
        var queue = new Queue<int>();
        for (var start = 0; start < cells.Length; start++)
        {
            if (seen[start] || cells[start] < minimumPixels) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                int cx = cell % columns, cy = cell / columns;
                x0 = Math.Min(x0, cx);
                y0 = Math.Min(y0, cy);
                x1 = Math.Max(x1, cx);
                y1 = Math.Max(y1, cy);
                for (var dy = -reach; dy <= reach; dy++)
                {
                    for (var dx = -reach; dx <= reach; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) continue;
                        var next = ny * columns + nx;
                        if (seen[next] || cells[next] < minimumPixels) continue;
                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            regions.Add(new DiffBox(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
            if (regions.Count >= 200) break;
        }

        return regions;
    }

    /// <summary>Resumo (SHA-256) das imagens de cada página, incluindo as de formulários (XObjects) usados nela.</summary>
    private static List<List<string>>? ImageFingerprints(string path)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var result = new List<List<string>>();
            for (var p = 0; p < document.PageCount; p++)
            {
                var list = new List<string>();
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                Collect(document.Pages[p].Elements.GetDictionary("/Resources"), list, visited, 0);
                result.Add(list);
            }

            return result;
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static void Collect(PdfDictionary? resources, List<string> list, HashSet<object> visited, int depth)
    {
        if (resources is null || depth > 8 || !visited.Add(resources)) return;
        var objects = resources.Elements.GetDictionary("/XObject");
        if (objects is null) return;
        foreach (var key in objects.Elements.Keys)
        {
            if (objects.Elements.GetDictionary(key) is not { Stream: not null } x || !visited.Add(x)) continue;
            switch (x.Elements.GetName("/Subtype"))
            {
                case "/Image":
                    list.Add(Convert.ToHexString(SHA256.HashData(x.Stream.Value)));
                    break;
                case "/Form":
                    Collect(x.Elements.GetDictionary("/Resources"), list, visited, depth + 1);
                    break;
            }
        }
    }

    private static int Except(List<string> a, List<string> b)
    {
        var counts = new Dictionary<string, int>();
        foreach (var x in b) counts[x] = counts.GetValueOrDefault(x) + 1;
        var missing = 0;
        foreach (var x in a)
        {
            if (counts.GetValueOrDefault(x) > 0) counts[x]--;
            else missing++;
        }

        return missing;
    }
}
