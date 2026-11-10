using System.Globalization;
using System.Text.RegularExpressions;

namespace PDFMaster.Core.Pdf;

public sealed class PageRangeException : Exception
{
    public PageRangeException(string message) : base(message)
    {
    }
}

/// <summary>
/// Interpreta intervalos de páginas digitados pelo usuário: "1-5", "1,3,5,7", "2-8,12,15-20",
/// "5-" (da 5 até o fim), "-3" (do início até a 3) e "8-3" (ordem decrescente).
/// As páginas digitadas começam em 1; os índices devolvidos começam em 0.
/// </summary>
public static class PageRangeParser
{
    /// <summary>Todas as páginas dos intervalos, na ordem digitada. Texto vazio = todas as páginas.</summary>
    public static IReadOnlyList<int> Parse(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Range(0, pageCount).ToList();
        return ParseGroups(text, pageCount).SelectMany(g => g).ToList();
    }

    /// <summary>Um grupo de páginas para cada intervalo separado por vírgula.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> ParseGroups(string? text, int pageCount)
    {
        if (pageCount <= 0) throw new PageRangeException("O documento não tem páginas.");
        if (string.IsNullOrWhiteSpace(text)) throw new PageRangeException("Informe as páginas ou os intervalos.");

        var groups = new List<IReadOnlyList<int>>();
        foreach (var rawPart in text.Split([',', ';'], StringSplitOptions.None))
        {
            var part = rawPart.Replace(" ", string.Empty).Replace('–', '-').Replace('—', '-');
            if (part.Length == 0) continue;

            var dash = part.IndexOf('-');
            int first, last;
            if (dash < 0)
            {
                first = last = ParseNumber(part, rawPart);
            }
            else
            {
                if (part.IndexOf('-', dash + 1) >= 0)
                    throw new PageRangeException($"Intervalo inválido: \"{rawPart.Trim()}\".");
                var left = part[..dash];
                var right = part[(dash + 1)..];
                if (left.Length == 0 && right.Length == 0)
                    throw new PageRangeException($"Intervalo inválido: \"{rawPart.Trim()}\".");
                first = left.Length == 0 ? 1 : ParseNumber(left, rawPart);
                last = right.Length == 0 ? pageCount : ParseNumber(right, rawPart);
            }

            Check(first, pageCount);
            Check(last, pageCount);

            var pages = new List<int>(Math.Abs(last - first) + 1);
            if (first <= last)
            {
                for (var p = first; p <= last; p++) pages.Add(p - 1);
            }
            else
            {
                for (var p = first; p >= last; p--) pages.Add(p - 1);
            }

            groups.Add(pages);
        }

        if (groups.Count == 0) throw new PageRangeException("Informe as páginas ou os intervalos.");
        return groups;
    }

    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages, out string? error)
    {
        try
        {
            pages = Parse(text, pageCount);
            error = null;
            return true;
        }
        catch (PageRangeException ex)
        {
            pages = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Escreve índices (base 0) de volta como texto compacto: 0,1,2,4 → "1-3,5".</summary>
    public static string Format(IEnumerable<int> pageIndexes)
    {
        var parts = new List<string>();
        int? start = null, previous = null;
        foreach (var index in pageIndexes)
        {
            if (previous is not null && index == previous + 1)
            {
                previous = index;
                continue;
            }

            if (start is not null) parts.Add(Describe(start.Value, previous!.Value));
            start = previous = index;
        }

        if (start is not null) parts.Add(Describe(start.Value, previous!.Value));
        return string.Join(",", parts);

        static string Describe(int a, int b) => a == b ? (a + 1).ToString(CultureInfo.InvariantCulture) : $"{a + 1}-{b + 1}";
    }

    private static int ParseNumber(string value, string original)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new PageRangeException($"\"{original.Trim()}\" não é uma página ou intervalo válido.");
        return number;
    }

    private static void Check(int page, int pageCount)
    {
        if (page < 1 || page > pageCount)
            throw new PageRangeException($"A página {page} não existe: o documento tem {pageCount} página(s).");
    }
}

/// <summary>Calcula em quais grupos de páginas um documento será dividido.</summary>
public static class SplitPlanner
{
    public static IReadOnlyList<IReadOnlyList<int>> EveryPage(int pageCount) => EveryN(pageCount, 1);

    public static IReadOnlyList<IReadOnlyList<int>> EveryN(int pageCount, int pagesPerFile)
    {
        if (pagesPerFile < 1) throw new PageRangeException("A quantidade de páginas por arquivo deve ser pelo menos 1.");
        var groups = new List<IReadOnlyList<int>>();
        for (var start = 0; start < pageCount; start += pagesPerFile)
        {
            groups.Add(Enumerable.Range(start, Math.Min(pagesPerFile, pageCount - start)).ToList());
        }

        return groups;
    }

    /// <summary>Cada intervalo separado por vírgula vira um arquivo: "1-3,4-10" → 2 arquivos.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> ByRanges(string? text, int pageCount) =>
        PageRangeParser.ParseGroups(text, pageCount);

    /// <summary>
    /// Corta o documento antes de cada página indicada (índices base 0). Com cortes em 3 e 7,
    /// um documento de 10 páginas vira [0-2], [3-6] e [7-9].
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> AtCutPoints(IEnumerable<int> firstPagesOfParts, int pageCount)
    {
        var cuts = firstPagesOfParts.Where(p => p > 0 && p < pageCount).Distinct().OrderBy(p => p).ToList();
        var groups = new List<IReadOnlyList<int>>();
        var start = 0;
        foreach (var cut in cuts)
        {
            groups.Add(Enumerable.Range(start, cut - start).ToList());
            start = cut;
        }

        if (pageCount > start) groups.Add(Enumerable.Range(start, pageCount - start).ToList());
        return groups;
    }
}

public enum InterleaveRemainder
{
    /// <summary>Quando um documento acaba, as páginas restantes dos outros vão para o final.</summary>
    AppendRemaining,
    /// <summary>Para quando o documento mais curto acaba; as páginas restantes ficam de fora.</summary>
    StopAtShortest,
}

/// <param name="PageCount">Total de páginas do documento.</param>
/// <param name="Reverse">Lê o documento de trás para frente (versos digitalizados em ordem inversa).</param>
public sealed record InterleaveSource(int PageCount, bool Reverse = false);

public sealed record InterleavedPage(int DocumentIndex, int PageIndex);

public static class InterleavePlanner
{
    /// <summary>
    /// Alterna páginas de vários documentos: A1, B1, A2, B2… Com <paramref name="pagesPerTurn"/> = 2:
    /// A1, A2, B1, B2, A3, A4…
    /// </summary>
    public static IReadOnlyList<InterleavedPage> Plan(
        IReadOnlyList<InterleaveSource> sources,
        int pagesPerTurn = 1,
        InterleaveRemainder remainder = InterleaveRemainder.AppendRemaining)
    {
        if (sources.Count < 2) throw new PageRangeException("São necessários pelo menos dois documentos para intercalar.");
        if (pagesPerTurn < 1) throw new PageRangeException("A quantidade de páginas por vez deve ser pelo menos 1.");

        var result = new List<InterleavedPage>();
        var taken = new int[sources.Count];
        var shortest = sources.Min(s => s.PageCount);
        var max = sources
            .Select(s => remainder == InterleaveRemainder.StopAtShortest ? shortest : s.PageCount)
            .ToArray();

        var added = true;
        while (added)
        {
            added = false;
            for (var d = 0; d < sources.Count; d++)
            {
                for (var k = 0; k < pagesPerTurn && taken[d] < max[d]; k++)
                {
                    var position = taken[d]++;
                    var pageIndex = sources[d].Reverse ? sources[d].PageCount - 1 - position : position;
                    result.Add(new InterleavedPage(d, pageIndex));
                    added = true;
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Monta os nomes dos arquivos gerados na divisão. Campos aceitos no padrão:
/// {nome} (nome do original), {n} (número do arquivo), {inicio} e {fim} (páginas), {total}.
/// Zeros definem a largura: {n:000} → 001, 002…
/// </summary>
public static partial class SplitNameFormatter
{
    public const string DefaultPattern = "{nome}_{n:000}";

    public static string Format(string? pattern, string baseName, int number, int total, int firstPage, int lastPage)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        if (effective.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) effective = effective[..^4];

        // Sem um campo que mude a cada arquivo, os nomes seriam todos iguais.
        if (total > 1 && !VaryingToken().IsMatch(effective)) effective += "_{n:000}";

        var name = Token().Replace(effective, match =>
        {
            var width = match.Groups[2].Success ? match.Groups[2].Value.Length : 0;
            return match.Groups[1].Value.ToLowerInvariant() switch
            {
                "nome" => baseName,
                "n" => Pad(number, width),
                "inicio" => Pad(firstPage, width),
                "fim" => Pad(lastPage, width),
                "total" => Pad(total, width),
                _ => match.Value,
            };
        });

        return name + ".pdf";
    }

    private static string Pad(int value, int width) =>
        value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    [GeneratedRegex(@"\{(nome|n|inicio|fim|total)(?::(0+))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"\{(n|inicio|fim)(?::0+)?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VaryingToken();
}
