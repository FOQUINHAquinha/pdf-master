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
            var referenced = new HashSet<int>();
            foreach (Match m in ReferencePattern().Matches(skeleton)) referenced.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            referenced.ExceptWith(present);
            referenced.Remove(0);
            inFile = present.Count;
            missing = referenced.Count;
            if (missing > 0) problems.Add($"{missing} objeto(s) referenciado(s) não existe(m) no arquivo (conteúdo perdido).");
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
                problems.Add("Um leitor rigoroso não consegue abrir o arquivo: " + ex.Message);
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
    /// Confere a tabela de referências cruzadas clássica apontada por "startxref": cada entrada em uso
    /// deve apontar para "N G obj". Tabelas compactadas (fluxos XRef) são conferidas só no endereço inicial.
    /// </summary>
    private static (bool Valid, int Entries, int Wrong, string? Problem) CheckXref(string text, int headerAt)
    {
        var starts = StartXrefPattern().Matches(text);
        if (starts.Count == 0) return (false, 0, 0, "A posição da tabela de referências (startxref) não foi encontrada.");
        if (!long.TryParse(starts[^1].Groups[1].Value, CultureInfo.InvariantCulture, out var start) || start <= 0 || start >= text.Length)
            return (false, 0, 0, "A posição da tabela de referências aponta para fora do arquivo.");

        var position = (int)start;
        if (string.CompareOrdinal(text, position, "xref", 0, 4) != 0)
        {
            if (ObjectHerePattern().Match(text, position).Success)
                return (true, 0, 0, null);
            return (false, 0, 0, "A posição da tabela de referências não aponta para uma tabela (deslocamentos errados).");
        }

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
                return (false, entries, wrong, "A tabela de referências está malformada.");
            for (var i = 0; i < count; i++)
            {
                var entry = reader.ReadLine();
                if (entry is null) return (false, entries, wrong, "A tabela de referências termina antes do esperado.");
                var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return (false, entries, wrong, "A tabela de referências está malformada.");
                if (parts[2] != "n") continue;
                entries++;
                if (!long.TryParse(parts[0], CultureInfo.InvariantCulture, out var offset)) { wrong++; continue; }
                // Bytes antes do cabeçalho deslocam tudo; os dois jeitos são aceitos.
                if (!Matches(text, offset, first + i) && !Matches(text, offset + headerAt, first + i)) wrong++;
            }
        }

        if (wrong > 0)
            return (false, entries, wrong, $"{wrong} de {entries} entrada(s) da tabela de referências apontam para o lugar errado.");
        return (true, entries, 0, null);
    }

    private static bool Matches(string text, long at, int number)
    {
        if (at < 0 || at >= text.Length) return false;
        var m = ObjectHerePattern().Match(text, (int)at);
        return m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == number;
    }
}
