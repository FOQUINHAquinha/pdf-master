using System.Globalization;
using System.Text;
using PDFMaster.Domain;

namespace PDFMaster.Core.Tools;

public static class TextNormalizer
{
    /// <summary>Minúsculas, sem acentos e sem pontuação: "Divisão de PDF!" → "divisao de pdf".</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }
}

/// <summary>Pesquisa de ferramentas por nome, descrição e sinônimos, ignorando acentos.</summary>
public sealed class ToolSearchService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "o", "as", "os", "de", "do", "da", "dos", "das", "em", "no", "na", "um", "uma",
        "para", "por", "com", "e", "ou", "meu", "minha", "meus",
    };

    private readonly List<IndexedTool> _index;

    public ToolSearchService(ToolCatalog catalog)
    {
        _index = catalog.All.Select(t => new IndexedTool(
            t,
            TextNormalizer.Normalize(t.Name),
            TextNormalizer.Normalize(t.Description),
            t.Synonyms.Select(TextNormalizer.Normalize).ToArray())).ToList();
    }

    public IReadOnlyList<ToolDefinition> Search(string? query)
    {
        var q = TextNormalizer.Normalize(query);
        if (q.Length == 0) return _index.Select(i => i.Tool).ToList();

        var allTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = allTokens.Where(t => !StopWords.Contains(t)).ToArray();
        if (tokens.Length == 0) tokens = allTokens;

        var results = new List<(ToolDefinition Tool, int Score)>();
        foreach (var item in _index)
        {
            var score = 0;
            if (item.Name == q) score += 1000;
            else if (item.Name.Contains(q, StringComparison.Ordinal)) score += 500;

            if (item.Synonyms.Any(s => s == q)) score += 450;
            else if (item.Synonyms.Any(s => s.Contains(q, StringComparison.Ordinal))) score += 300;

            if (item.Description.Contains(q, StringComparison.Ordinal)) score += 100;

            var tokenScore = 0;
            var allMatched = true;
            foreach (var token in tokens)
            {
                if (HasWordStartingWith(item.Name, token)) tokenScore += 40;
                else if (item.Synonyms.Any(s => HasWordStartingWith(s, token))) tokenScore += 30;
                else if (HasWordStartingWith(item.Description, token)) tokenScore += 10;
                else allMatched = false;
            }

            if (allMatched) score += tokenScore + 50;
            if (score > 0) results.Add((item.Tool, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Tool.Name, StringComparer.CurrentCulture)
            .Select(r => r.Tool)
            .ToList();
    }

    private static bool HasWordStartingWith(string text, string token)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf(' ', start);
            if (end < 0) end = text.Length;
            if (end - start >= token.Length &&
                string.CompareOrdinal(text, start, token, 0, token.Length) == 0)
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    private sealed record IndexedTool(ToolDefinition Tool, string Name, string Description, string[] Synonyms);
}
