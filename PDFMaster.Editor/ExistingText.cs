using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace PDFMaster.Editor;

/// <summary>
/// Troca de texto dentro do próprio conteúdo da página, com a mesma fonte, quando a estrutura do PDF
/// permite: fontes simples (WinAnsi ou ASCII), o trecho num único comando de texto e, se a fonte for
/// incorporada como subconjunto, todos os caracteres novos já presentes nela. Caso contrário, informa
/// o motivo para que o editor ofereça a sobreposição.
/// </summary>
internal static class ExistingText
{
    private sealed class Segment
    {
        public required COperator Operator { get; init; }
        public required List<CString> Strings { get; init; }
        public required FontInfo Font { get; init; }
        public string? Text { get; set; }
    }

    private sealed class FontInfo
    {
        public required string Key { get; init; }
        public bool Composite { get; init; }
        public bool WinAnsi { get; init; }
        public bool Subset { get; init; }
        public bool Embedded { get; init; }
        public int FirstChar { get; init; }
        public double[] Widths { get; init; } = [];
        public HashSet<char> Used { get; } = [];
    }

    public static bool TryReplace(PdfPage page, string oldText, string newText, out string reason)
    {
        reason = string.Empty;
        if (oldText.Length == 0)
        {
            reason = "Não há texto original para trocar.";
            return false;
        }

        CSequence content;
        try
        {
            content = ContentReader.ReadContent(page);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "O conteúdo da página não pôde ser lido para edição.";
            return false;
        }

        // Imagens embutidas no conteúdo (BI/ID/EI) não sobrevivem com segurança a uma regravação.
        if (content.OfType<COperator>().Any(o => o.Name is "BI" or "ID" or "EI"))
        {
            reason = "A página tem imagens embutidas no próprio conteúdo; regravá-lo poderia danificá-las.";
            return false;
        }

        var fontDictionaries = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/Font");
        var fonts = new Dictionary<string, FontInfo>(StringComparer.Ordinal);
        var segments = new List<Segment>();
        Collect(content, fontDictionaries, fonts, segments, null);

        foreach (var segment in segments) segment.Text = Decode(segment);

        // 1) O trecho inteiro; 2) só a parte que mudou (por palavras).
        var attempts = new List<(string Old, string New)> { (oldText, newText) };
        if (Difference(oldText, newText) is { } diff && diff.Old != oldText) attempts.Add(diff);

        foreach (var (oldPart, newPart) in attempts)
        {
            var occurrences = segments.Sum(s => s.Text is null ? 0 : Count(s.Text, oldPart));
            if (occurrences == 0) continue;
            if (occurrences > 1)
            {
                reason = $"O trecho \"{Short(oldPart)}\" aparece mais de uma vez na página; a troca no próprio PDF seria ambígua.";
                return false;
            }

            var segment = segments.First(s => s.Text is not null && s.Text.Contains(oldPart, StringComparison.Ordinal));
            var font = segment.Font;
            var missing = newPart.Where(c => !CanShow(font, c)).Distinct().ToList();
            if (missing.Count > 0)
            {
                reason = font.Subset
                    ? $"A fonte incorporada no PDF não tem os caracteres {string.Join(", ", missing.Select(c => $"\"{c}\""))}."
                    : $"A fonte do PDF não consegue exibir {string.Join(", ", missing.Select(c => $"\"{c}\""))}.";
                return false;
            }

            var replaced = segment.Text!.Replace(oldPart, newPart, StringComparison.Ordinal);
            var bytes = replaced.Select(c => font.WinAnsi ? Pdf.WinAnsiByte(c)!.Value : (byte)c).ToArray();
            var value = new string(bytes.Select(b => (char)b).ToArray());
            if (segment.Operator.Name == "TJ" && segment.Operator.Operands.Count > 0 && segment.Operator.Operands[0] is CArray array)
            {
                // Os ajustes de espaçamento do TJ deixam de valer para o trecho trocado.
                var single = segment.Strings[0];
                single.Value = value;
                array.Clear();
                array.Add(single);
            }
            else
            {
                segment.Strings[0].Value = value;
            }

            var contents = page.Contents;
            contents.Elements.Clear();
            contents.AppendContent().CreateStream(content.ToContent());
            return true;
        }

        reason = segments.Any(s => s.Font.Composite) && segments.All(s => s.Text is null || !s.Text.Contains(oldText[..Math.Min(3, oldText.Length)], StringComparison.Ordinal))
            ? "O texto usa uma fonte composta (Unicode/CID), que não permite troca segura no próprio PDF."
            : "O trecho não foi localizado num único comando de texto da página (pode estar dividido em pedaços, dentro de um objeto ou numa fonte não suportada).";
        return false;
    }

    private static void Collect(CSequence sequence, PdfDictionary? fontDictionaries, Dictionary<string, FontInfo> fonts, List<Segment> segments, FontInfo? current)
    {
        foreach (var item in sequence)
        {
            if (item is not COperator op) continue;
            switch (op.Name)
            {
                case "Tf" when op.Operands.Count >= 1 && op.Operands[0] is CName name:
                    current = Font(fontDictionaries, fonts, name.Name);
                    break;
                case "Tj" or "'" when current is not null && op.Operands.Count >= 1 && op.Operands[^1] is CString s:
                    segments.Add(new Segment { Operator = op, Strings = [s], Font = current });
                    break;
                case "\"" when current is not null && op.Operands.Count >= 3 && op.Operands[2] is CString s3:
                    segments.Add(new Segment { Operator = op, Strings = [s3], Font = current });
                    break;
                case "TJ" when current is not null && op.Operands.Count >= 1 && op.Operands[0] is CArray array:
                    var strings = array.OfType<CString>().ToList();
                    if (strings.Count > 0) segments.Add(new Segment { Operator = op, Strings = strings, Font = current });
                    break;
            }
        }
    }

    private static FontInfo Font(PdfDictionary? fontDictionaries, Dictionary<string, FontInfo> fonts, string key)
    {
        if (fonts.TryGetValue(key, out var known)) return known;
        var dict = fontDictionaries?.Elements.GetDictionary(key);
        FontInfo info;
        if (dict is null)
        {
            info = new FontInfo { Key = key, Composite = true };
        }
        else
        {
            var subtype = dict.Elements.GetName("/Subtype");
            var encodingItem = dict.Elements["/Encoding"];
            if (encodingItem is PdfReference r) encodingItem = r.Value;
            var baseFont = dict.Elements.GetName("/BaseFont");
            var descriptor = dict.Elements.GetDictionary("/FontDescriptor");
            var embedded = descriptor is not null &&
                           (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));
            var widthsArray = dict.Elements.GetArray("/Widths");
            var widths = new double[widthsArray?.Elements.Count ?? 0];
            for (var i = 0; i < widths.Length; i++) widths[i] = widthsArray!.Elements.GetReal(i);
            info = new FontInfo
            {
                Key = key,
                // Fontes compostas e codificações com /Differences não são decodificadas com segurança.
                Composite = subtype is "/Type0" or "/Type3" || encodingItem is PdfDictionary,
                WinAnsi = encodingItem is PdfName { Value: "/WinAnsiEncoding" },
                Subset = baseFont.Length > 8 && baseFont[7] == '+',
                Embedded = embedded,
                FirstChar = dict.Elements.GetInteger("/FirstChar"),
                Widths = widths,
            };
        }

        fonts[key] = info;
        return info;
    }

    private static string? Decode(Segment segment)
    {
        if (segment.Font.Composite) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var s in segment.Strings)
        {
            if (s.CStringType is CStringType.UnicodeString or CStringType.UnicodeHexString) return null;
            foreach (var ch in s.Value)
            {
                if (ch > 0xFF) return null;
                char? decoded = segment.Font.WinAnsi ? Pdf.FromWinAnsi((byte)ch) : ch is >= ' ' and <= '~' ? ch : null;
                if (decoded is null) return null;
                sb.Append(decoded.Value);
                segment.Font.Used.Add(decoded.Value);
            }
        }

        return sb.ToString();
    }

    private static bool CanShow(FontInfo font, char c)
    {
        byte code;
        if (font.WinAnsi)
        {
            if (Pdf.WinAnsiByte(c) is not { } b) return false;
            code = b;
        }
        else
        {
            if (c is < ' ' or > '~') return false;
            code = (byte)c;
        }

        // Subconjunto incorporado: só os caracteres que já existem nele.
        if (font.Subset && !font.Used.Contains(c)) return false;
        if (font.Widths.Length > 0)
        {
            var index = code - font.FirstChar;
            if (index < 0 || index >= font.Widths.Length) return false;
            if (font.Embedded && font.Widths[index] <= 0 && c != ' ') return false;
        }

        return true;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>Parte que mudou entre o texto antigo e o novo, ampliada até os limites das palavras.</summary>
    internal static (string Old, string New)? Difference(string oldText, string newText)
    {
        if (oldText == newText) return null;
        var prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix &&
               oldText[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;

        // Amplia até espaços para que a busca encontre palavras inteiras.
        while (prefix > 0 && oldText[prefix - 1] != ' ') prefix--;
        while (suffix > 0 && oldText[^suffix] != ' ') suffix--;
        var oldPart = oldText[prefix..^suffix];
        var newPart = newText[prefix..^suffix];
        return oldPart.Length == 0 ? null : (oldPart, newPart);
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
