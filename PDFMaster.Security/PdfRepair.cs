using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Security;

public enum DamageLevel
{
    /// <summary>Nenhum problema estrutural encontrado.</summary>
    None,

    /// <summary>Estrutura com defeitos, mas o documento é legível (recuperável).</summary>
    Recoverable,

    /// <summary>Nem todas as páginas puderam ser lidas.</summary>
    PartiallyRecoverable,

    /// <summary>Nada pôde ser recuperado.</summary>
    Unrecoverable,

    /// <summary>Protegido por senha: precisa da senha para analisar.</summary>
    PasswordRequired,
}

/// <summary>Diagnóstico estrutural de um arquivo PDF.</summary>
/// <param name="Problems">Problemas encontrados, em linguagem simples.</param>
/// <param name="ObjectsInFile">Objetos encontrados por varredura dos bytes (null quando há fluxos de objetos compactados).</param>
/// <param name="MissingObjects">Objetos referenciados que não existem no arquivo (null quando não dá para saber).</param>
/// <param name="BrokenPages">Páginas que não carregam (a partir de 0).</param>
public sealed record StructureReport(
    DamageLevel Level, long FileSize, bool HasHeader, bool HasEndMarker, bool XrefValid, int XrefEntries, int XrefWrongOffsets,
    bool StrictReaderOpens, int PageCount, IReadOnlyList<int> BrokenPages, int? ObjectsInFile, int? MissingObjects,
    IReadOnlyList<string> Problems)
{
    public bool Damaged => Level is DamageLevel.Recoverable or DamageLevel.PartiallyRecoverable or DamageLevel.Unrecoverable;
}

/// <summary>Resultado do reparo.</summary>
/// <param name="RecoveredPages">Páginas do arquivo recuperado.</param>
/// <param name="LostPages">Páginas do original que não puderam ser recuperadas (números a partir de 1).</param>
public sealed record RepairResult(ProducedFile File, StructureReport Before, int RecoveredPages, IReadOnlyList<int> LostPages,
    int ObjectsWritten, IReadOnlyList<string> Notes);

/// <summary>
/// Diagnóstico e reparo: confere cabeçalho, marcador de fim, tabela de referências cruzadas (cada
/// deslocamento precisa apontar para o objeto certo) e se cada página carrega; o reparo usa a
/// reconstrução do PDFium (varredura dos objetos) e grava uma cópia nova, sem tocar no original.
/// </summary>
public sealed partial class PdfRepair
{
    public const string OperationName = "Reparar PDF";

    [GeneratedRegex(@"(?<!\d)(\d{1,7})\s+(\d{1,5})\s+obj\b")]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"(?<![\d.])(\d{1,7})\s+(\d{1,5})\s+R\b")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"\G\s?(\d{1,7})\s+(\d{1,5})\s+obj\b")]
    private static partial Regex ObjectHerePattern();

    [GeneratedRegex(@"(?:^|[\r\n])xref\s*[\r\n]")]
    private static partial Regex ClassicTablePattern();

    [GeneratedRegex(@"/Type\s*/XRef\b")]
    private static partial Regex XrefStreamPattern();

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXrefPattern();

    private readonly OutputPlanner _planner;

    public PdfRepair(OutputPlanner planner) => _planner = planner;

    public static StructureReport Diagnose(string path, string? password = null, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível ler \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }

        var problems = new List<string>();
        var text = Encoding.Latin1.GetString(bytes);
        var headerAt = text.IndexOf("%PDF-", 0, Math.Min(text.Length, 1024), StringComparison.Ordinal);
        var hasHeader = headerAt >= 0;
        if (!hasHeader) problems.Add("O cabeçalho \"%PDF-\" não foi encontrado no início do arquivo.");
        else if (headerAt > 0) problems.Add($"Há {headerAt} bytes estranhos antes do cabeçalho do PDF.");
        var hasEnd = text.LastIndexOf("%%EOF", StringComparison.Ordinal) is var eof && eof >= 0 && text.Length - eof < 1100;
        if (!hasEnd) problems.Add("O marcador de fim (%%EOF) está ausente: o arquivo parece cortado (download ou cópia incompleta).");

        ct.ThrowIfCancellationRequested();
        var (xrefValid, entries, wrong, xrefProblem) = CheckXref(text, Math.Max(0, headerAt));
        if (xrefProblem is not null) problems.Add(xrefProblem);

        // Objetos presentes e referenciados (só faz sentido sem fluxos de objetos compactados).
        int? inFile = null, missing = null;
        if (!text.Contains("/ObjStm", StringComparison.Ordinal))
        {
            var present = new HashSet<int>();
            var skeleton = Skeleton(text);
            foreach (Match m in ObjectPattern().Matches(skeleton)) present.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            var referenced = new Dictionary<int, int>();
            foreach (Match m in ReferencePattern().Matches(skeleton)) referenced.TryAdd(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Index);
            var absent = referenced.Where(r => r.Key != 0 && !present.Contains(r.Key)).OrderBy(r => r.Key).ToList();
            inFile = present.Count;
            missing = absent.Count;
            if (missing > 0)
            {
                var first = absent[0];
                var context = skeleton[Math.Max(0, first.Value - 30)..Math.Min(skeleton.Length, first.Value + 12)].ReplaceLineEndings(" ");
                problems.Add($"{missing} objeto(s) referenciado(s) não existe(m) no arquivo (conteúdo perdido): " +
                             string.Join(", ", absent.Take(8).Select(a => a.Key)) + $" (ex.: \"…{context}…\").");
            }
        }

        ct.ThrowIfCancellationRequested();
        var strict = true;
        try
        {
            using var document = password is null
                ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
            _ = document.PageCount;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            strict = false;
            if (ex is not PdfReaderException || !ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Um leitor rigoroso não consegue abrir o arquivo ({ex.GetType().Name}): {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();
        PdfiumInspection pdfium;
        try
        {
            pdfium = PdfiumRenderer.Inspect(path, password, checkPages: true);
        }
        catch (PdfRenderException ex)
        {
            throw new PdfEngineException(ex.Message, ex);
        }

        DamageLevel level;
        if (!pdfium.Opened && pdfium.PasswordRequired)
        {
            level = DamageLevel.PasswordRequired;
            problems.Insert(0, password is null ? "O documento é protegido por senha. Informe a senha para analisar." : "Senha incorreta.");
        }
        else if (!pdfium.Opened || pdfium.PageCount == 0)
        {
            level = DamageLevel.Unrecoverable;
            problems.Add("Nenhuma página pôde ser lida.");
        }
        else if (pdfium.BrokenPages.Count > 0)
        {
            level = DamageLevel.PartiallyRecoverable;
            problems.Add($"{pdfium.BrokenPages.Count} de {pdfium.PageCount} página(s) não carregam: {string.Join(", ", pdfium.BrokenPages.Select(p => p + 1))}.");
        }
        else
        {
            level = problems.Count > 0 ? DamageLevel.Recoverable : DamageLevel.None;
        }

        return new StructureReport(level, bytes.LongLength, hasHeader, hasEnd, xrefValid, entries, wrong, strict,
            pdfium.PageCount, pdfium.BrokenPages, inFile, missing, problems);
    }

    /// <summary>
    /// Grava uma cópia reconstruída pelo PDFium. Páginas que não carregam são retiradas da cópia (e relatadas).
    /// </summary>
    public Task<RepairResult> RepairAsync(string source, string? password, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(source);
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + "_reparado.pdf", target, OperationName, [full], reserved);
        return Task.Run(() =>
        {
            var before = Diagnose(full, password, ct);
            if (before.Level == DamageLevel.PasswordRequired) throw new PdfEngineException(before.Problems[0]);
            if (before.Level == DamageLevel.Unrecoverable)
                throw new PdfEngineException("Não foi possível recuperar nenhuma página deste arquivo. " + string.Join(" ", before.Problems));

            var notes = new List<string>();
            var recovered = 0;
            var objects = 0;
            IReadOnlyList<int> lost = [];
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                try
                {
                    var (count, broken) = PdfiumRenderer.SaveCopy(full, password, temp, removeBrokenPages: true);
                    lost = broken.Select(b => b + 1).ToList();
                    recovered = count - broken.Count;
                }
                catch (PdfRenderException ex)
                {
                    throw new PdfEngineException("A reconstrução falhou: " + ex.Message, ex);
                }

                ct.ThrowIfCancellationRequested();
                FillMissingObjects(temp, full, password, notes);
                var check = PdfiumRenderer.Inspect(temp, password, checkPages: true);
                if (!check.Opened || check.PageCount != recovered || check.BrokenPages.Count > 0)
                    throw new PdfEngineException("A cópia recuperada ainda apresenta problemas e não foi salva.");
                objects = CountObjects(File.ReadAllBytes(temp));
                try
                {
                    using var strict = password is null
                        ? PdfReader.Open(temp, PdfDocumentOpenMode.Import)
                        : PdfReader.Open(temp, password, PdfDocumentOpenMode.Import);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    notes.Add("Algumas ferramentas mais rigorosas ainda podem ter dificuldade com a cópia: " + ex.Message);
                }
            });

            if (before.MissingObjects is > 0)
                notes.Add($"{before.MissingObjects} objeto(s) referenciado(s) não existiam no arquivo; o que dependia deles (imagens, fontes ou textos) pode aparecer incompleto.");
            if (lost.Count > 0) notes.Add($"Páginas perdidas: {string.Join(", ", lost)}.");
            if (password is not null) notes.Add("A cópia mantém a mesma proteção por senha do original.");
            if (!before.Damaged) notes.Add("Nenhum dano foi encontrado; a cópia foi regravada com uma estrutura nova.");
            return new RepairResult(new ProducedFile(output, bytes, recovered), before, recovered, lost, objects, notes);
        }, ct);
    }

    /// <summary>
    /// A reconstrução pode deixar referências a objetos que não foram gravados (por exemplo, o
    /// dicionário de informações citado no trailer). Eles são recriados a partir do arquivo original,
    /// quando o texto deles ainda está lá, ou como objetos vazios — senão leitores rigorosos recusam a cópia.
    /// </summary>
    private static void FillMissingObjects(string path, string original, string? password, List<string> notes)
    {
        var bytes = File.ReadAllBytes(path);
        var skeleton = Skeleton(Encoding.Latin1.GetString(bytes));
        if (skeleton.Contains("/ObjStm", StringComparison.Ordinal)) return;
        var present = new HashSet<int>();
        foreach (Match m in ObjectPattern().Matches(skeleton)) present.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        var absent = new SortedSet<int>();
        foreach (Match m in ReferencePattern().Matches(skeleton))
        {
            var number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (number != 0 && !present.Contains(number)) absent.Add(number);
        }

        if (absent.Count == 0) return;
        if (password is not null)
        {
            notes.Add($"{absent.Count} objeto(s) citado(s) no documento não puderam ser recuperados.");
            return;
        }

        DocumentInfoWriter.TrailerInfo trailer;
        try
        {
            trailer = DocumentInfoWriter.ReadTrailer(bytes);
        }
        catch (PdfEngineException)
        {
            return;
        }

        var source = File.ReadAllBytes(original);
        var objects = new List<(int Number, int Generation, byte[] Body)>();
        var recovered = 0;
        foreach (var number in absent)
        {
            var text = DocumentInfoWriter.ObjectText(source, number, 0);
            string body;
            if (text is not null && text.StartsWith("<<", StringComparison.Ordinal) && !text.Contains("stream", StringComparison.Ordinal))
            {
                body = text;
                recovered++;
            }
            else
            {
                body = number == trailer.InfoNumber ? "<< >>" : "null";
            }

            objects.Add((number, 0, Encoding.Latin1.GetBytes(body)));
        }

        var patched = path + ".objetos";
        try
        {
            File.WriteAllBytes(patched, DocumentInfoWriter.Append(bytes, trailer, objects, trailer.InfoNumber > 0 ? trailer.InfoNumber : null, trailer.InfoGeneration));
            PdfiumRenderer.SaveCopy(patched, null, path);
        }
        catch (PdfRenderException)
        {
            File.WriteAllBytes(path, bytes);
            return;
        }
        finally
        {
            DocumentInfoWriter.TryDelete(patched);
        }

        notes.Add(recovered == absent.Count
            ? $"{absent.Count} objeto(s) que a reconstrução tinha deixado de fora foram recuperados do arquivo original."
            : $"{recovered} de {absent.Count} objeto(s) ausente(s) recuperados do original; os demais foram substituídos por objetos vazios.");
    }

    /// <summary>Texto do arquivo sem o conteúdo dos fluxos (evita achar "objetos" dentro de dados binários).</summary>
    private static string Skeleton(string text)
    {
        var builder = new StringBuilder(text.Length / 4);
        var position = 0;
        while (position < text.Length)
        {
            var stream = text.IndexOf("stream", position, StringComparison.Ordinal);
            if (stream < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            var bodyStart = stream + 6;
            // "endstream" também contém "stream": só conta o início de um fluxo.
            if (stream >= 3 && string.CompareOrdinal(text, stream - 3, "end", 0, 3) == 0 ||
                bodyStart < text.Length && text[bodyStart] is not ('\r' or '\n'))
            {
                builder.Append(text, position, bodyStart - position);
                position = bodyStart;
                continue;
            }

            builder.Append(text, position, bodyStart - position);
            var end = text.IndexOf("endstream", bodyStart, StringComparison.Ordinal);
            if (end < 0) break;
            builder.Append('\n');
            position = end;
        }

        return builder.ToString();
    }

    private static int CountObjects(byte[] bytes)
    {
        var present = new HashSet<int>();
        foreach (Match m in ObjectPattern().Matches(Skeleton(Encoding.Latin1.GetString(bytes))))
            present.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return present.Count;
    }

    /// <summary>
    /// Confere a tabela de referências cruzadas: cada entrada em uso deve apontar para "N G obj".
    /// Aceita a tabela clássica e a compactada (fluxo XRef). Quando "startxref" aponta para o lugar
    /// errado, procura a última tabela no arquivo e confere as entradas dela mesmo assim.
    /// </summary>
    private static (bool Valid, int Entries, int Wrong, string? Problem) CheckXref(string text, int headerAt)
    {
        string? problem = null;
        var position = -1;
        var starts = StartXrefPattern().Matches(text);
        if (starts.Count == 0)
        {
            problem = "A posição da tabela de referências (startxref) não foi encontrada.";
        }
        else if (!long.TryParse(starts[^1].Groups[1].Value, CultureInfo.InvariantCulture, out var start) || start <= 0 || start >= text.Length)
        {
            problem = "A posição da tabela de referências aponta para fora do arquivo.";
        }
        else if (IsTableAt(text, (int)start) || IsTableAt(text, (int)start + headerAt))
        {
            position = IsTableAt(text, (int)start) ? (int)start : (int)start + headerAt;
        }
        else
        {
            problem = "A posição da tabela de referências não aponta para uma tabela (deslocamentos errados).";
        }

        if (position < 0)
        {
            // Procura a última tabela (clássica ou compactada) para conferir as entradas.
            var classic = ClassicTablePattern().Matches(text);
            var streams = XrefStreamPattern().Matches(text);
            var lastClassic = classic.Count > 0 ? classic[^1].Index + (text[classic[^1].Index] is '\r' or '\n' ? 1 : 0) : -1;
            var lastStream = -1;
            if (streams.Count > 0)
            {
                var objectStart = text.LastIndexOf(" obj", streams[^1].Index, StringComparison.Ordinal);
                if (objectStart > 0) lastStream = ObjectStart(text, objectStart);
            }

            position = Math.Max(lastClassic, lastStream);
            if (position < 0) return (false, 0, 0, problem ?? "Nenhuma tabela de referências foi encontrada.");
        }

        var (entries, wrong, malformed) = string.CompareOrdinal(text, position, "xref", 0, 4) == 0
            ? ClassicEntries(text, position, headerAt)
            : StreamEntries(text, position, headerAt);
        if (malformed is not null) return (false, entries, wrong, problem is null ? malformed : problem + " " + malformed);
        if (wrong > 0)
        {
            var detail = $"{wrong} de {entries} entrada(s) da tabela de referências apontam para o lugar errado.";
            return (false, entries, wrong, problem is null ? detail : problem + " " + detail);
        }

        return (problem is null, entries, 0, problem);
    }

    private static bool IsTableAt(string text, int at) =>
        at >= 0 && at < text.Length - 4 && (string.CompareOrdinal(text, at, "xref", 0, 4) == 0 || ObjectHerePattern().Match(text, at).Success && IsXrefStream(text, at));

    private static bool IsXrefStream(string text, int at)
    {
        var limit = Math.Min(text.Length - at, 2048);
        var head = text.Substring(at, limit);
        var stream = head.IndexOf("stream", StringComparison.Ordinal);
        return XrefStreamPattern().IsMatch(stream > 0 ? head[..stream] : head);
    }

    /// <summary>Início do "N G obj" que termina em <paramref name="objKeyword"/>.</summary>
    private static int ObjectStart(string text, int objKeyword)
    {
        var i = objKeyword;
        var numbers = 0;
        while (i > 0 && numbers < 2)
        {
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            while (i > 0 && char.IsAsciiDigit(text[i - 1])) i--;
            numbers++;
        }

        return i;
    }

    private static (int Entries, int Wrong, string? Malformed) ClassicEntries(string text, int position, int headerAt)
    {
        var entries = 0;
        var wrong = 0;
        var reader = new StringReader(text[(position + 4)..Math.Min(text.Length, position + 4 + 20 * 2_000_000)]);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("trailer", StringComparison.Ordinal)) break;
            var header = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 2 || !int.TryParse(header[0], CultureInfo.InvariantCulture, out var first) ||
                !int.TryParse(header[1], CultureInfo.InvariantCulture, out var count))
                return (entries, wrong, "A tabela de referências está malformada.");
            for (var i = 0; i < count; i++)
            {
                var entry = reader.ReadLine();
                if (entry is null) return (entries, wrong, "A tabela de referências termina antes do esperado.");
                var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return (entries, wrong, "A tabela de referências está malformada.");
                if (parts[2] != "n") continue;
                entries++;
                if (!long.TryParse(parts[0], CultureInfo.InvariantCulture, out var offset) ||
                    !Matches(text, offset, first + i) && !Matches(text, offset + headerAt, first + i)) wrong++;
            }
        }

        return (entries, wrong, null);
    }

    /// <summary>Entradas de uma tabela compactada (fluxo /Type /XRef com /W, /Index e FlateDecode com preditor PNG).</summary>
    private static (int Entries, int Wrong, string? Malformed) StreamEntries(string text, int position, int headerAt)
    {
        var streamAt = text.IndexOf("stream", position, StringComparison.Ordinal);
        if (streamAt < 0) return (0, 0, "A tabela de referências compactada está incompleta.");
        var dictionary = text[position..streamAt];
        var dataStart = streamAt + 6;
        if (dataStart < text.Length && text[dataStart] == '\r') dataStart++;
        if (dataStart < text.Length && text[dataStart] == '\n') dataStart++;
        var dataEnd = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
        if (dataEnd < 0) return (0, 0, "A tabela de referências compactada está incompleta.");
        var lengthMatch = Regex.Match(dictionary, @"/Length\s+(\d+)\b(?!\s+\d+\s+R)");
        if (lengthMatch.Success && int.TryParse(lengthMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var declared) && dataStart + declared <= text.Length)
            dataEnd = dataStart + declared;

        var widths = Regex.Match(dictionary, @"/W\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s*\]");
        if (!widths.Success) return (0, 0, "A tabela de referências compactada está malformada.");
        int w0 = int.Parse(widths.Groups[1].Value, CultureInfo.InvariantCulture), w1 = int.Parse(widths.Groups[2].Value, CultureInfo.InvariantCulture),
            w2 = int.Parse(widths.Groups[3].Value, CultureInfo.InvariantCulture);
        var rowSize = w0 + w1 + w2;
        if (rowSize is <= 0 or > 32) return (0, 0, "A tabela de referências compactada está malformada.");

        var sizeMatch = Regex.Match(dictionary, @"/Size\s+(\d+)");
        var ranges = new List<(int First, int Count)>();
        var index = Regex.Match(dictionary, @"/Index\s*\[([\d\s]*)\]");
        if (index.Success)
        {
            var numbers = index.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToArray();
            for (var i = 0; i + 1 < numbers.Length; i += 2) ranges.Add((numbers[i], numbers[i + 1]));
        }
        else if (sizeMatch.Success)
        {
            ranges.Add((0, int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture)));
        }

        byte[] data;
        try
        {
            var raw = Encoding.Latin1.GetBytes(text[dataStart..dataEnd]);
            data = dictionary.Contains("/FlateDecode", StringComparison.Ordinal) ? Inflate(raw) : raw;
        }
        catch (InvalidDataException)
        {
            return (0, 0, "A tabela de referências compactada não pôde ser descompactada.");
        }

        var predictor = Regex.Match(dictionary, @"/Predictor\s+(\d+)");
        if (predictor.Success && int.Parse(predictor.Groups[1].Value, CultureInfo.InvariantCulture) >= 10)
        {
            var columns = Regex.Match(dictionary, @"/Columns\s+(\d+)") is { Success: true } c ? int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture) : rowSize;
            data = PngUp(data, columns);
        }

        var entries = 0;
        var wrong = 0;
        var row = 0;
        foreach (var (first, count) in ranges)
        {
            for (var i = 0; i < count; i++, row++)
            {
                var at = row * rowSize;
                if (at + rowSize > data.Length) return (entries, wrong, "A tabela de referências compactada termina antes do esperado.");
                long Field(int offset, int width, long fallback)
                {
                    if (width == 0) return fallback;
                    long value = 0;
                    for (var k = 0; k < width; k++) value = value << 8 | data[at + offset + k];
                    return value;
                }

                var type = Field(0, w0, 1);
                if (type != 1) continue;
                entries++;
                var offsetValue = Field(w0, w1, 0);
                if (!Matches(text, offsetValue, first + i) && !Matches(text, offsetValue + headerAt, first + i)) wrong++;
            }
        }

        return (entries, wrong, null);
    }

    private static byte[] Inflate(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Desfaz o preditor PNG (filtros por linha: nenhum, sub, up, média, Paeth).</summary>
    private static byte[] PngUp(byte[] data, int columns)
    {
        var stride = columns + 1;
        var rows = data.Length / stride;
        var result = new byte[rows * columns];
        var previous = new byte[columns];
        for (var r = 0; r < rows; r++)
        {
            var filter = data[r * stride];
            for (var c = 0; c < columns; c++)
            {
                int x = data[r * stride + 1 + c], left = c > 0 ? result[r * columns + c - 1] : 0, up = previous[c], upLeft = c > 0 ? previous[c - 1] : 0;
                var value = filter switch
                {
                    1 => x + left,
                    2 => x + up,
                    3 => x + (left + up) / 2,
                    4 => x + Paeth(left, up, upLeft),
                    _ => x,
                };
                result[r * columns + c] = (byte)value;
            }

            Array.Copy(result, r * columns, previous, 0, columns);
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static bool Matches(string text, long at, int number)
    {
        if (at < 0 || at >= text.Length) return false;
        var m = ObjectHerePattern().Match(text, (int)at);
        return m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == number;
    }
}
