using System.Text;
using PDFMaster.PdfEngine;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PDFMaster.Conversion;

/// <summary>
/// Word, Excel, PowerPoint, ODF, RTF e HTML → PDF, usando o LibreOffice instalado no computador.
/// A fidelidade é a do LibreOffice: documentos com fontes ausentes ou recursos exclusivos do
/// Microsoft Office podem sair com diferenças de formatação.
/// </summary>
public sealed class OfficeToPdfConverter
{
    public const string OperationName = "Office para PDF";

    public static readonly IReadOnlyList<string> OfficeExtensions =
        [".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".ods", ".odp", ".rtf"];

    public static readonly IReadOnlyList<string> HtmlExtensions = [".html", ".htm"];

    private readonly LibreOffice _office;
    private readonly IPdfEngine _engine;
    private readonly OutputPlanner _planner;

    public OfficeToPdfConverter(LibreOffice office, IPdfEngine engine, OutputPlanner planner)
    {
        _office = office;
        _engine = engine;
        _planner = planner;
    }

    public async Task<ProducedFile> ConvertAsync(
        string path, OutputTarget target, string operationName, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var isHtml = HtmlExtensions.Contains(extension);
        if (!isHtml && !OfficeExtensions.Contains(extension))
            throw new PdfEngineException($"{Path.GetFileName(path)}: tipo de arquivo não suportado nesta conversão.");

        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(path) + ".pdf", target, operationName, [path], reserved);
        var produced = await _office.ConvertAsync(path, isHtml ? "pdf:writer_web_pdf_Export" : "pdf", null, ct).ConfigureAwait(false);
        try
        {
            var info = await _engine.ReadInfoAsync(produced, ct).ConfigureAwait(false);
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.Copy(produced, temp, overwrite: true));
            return new ProducedFile(output, bytes, info.PageCount);
        }
        finally
        {
            LibreOffice.DeleteWorkFolder(produced);
        }
    }
}

/// <param name="Monospace">Fonte de largura fixa (Consolas), que preserva o alinhamento de colunas do texto.</param>
public sealed record TextToPdfOptions(bool Monospace = true, double FontSize = 10.5, double MarginMm = 20, bool Landscape = false);

/// <summary>
/// TXT → PDF, feito pelo próprio programa (não depende do LibreOffice). Página A4, quebra
/// automática de linhas, texto pesquisável. Aceita UTF-8, UTF-16 e, na falta deles, Latin-1.
/// </summary>
public sealed class TextToPdfConverter
{
    public const string OperationName = "Texto e HTML para PDF";
    private const double PointsPerMm = 72.0 / 25.4;

    private readonly OutputPlanner _planner;

    public TextToPdfConverter(OutputPlanner planner) => _planner = planner;

    public Task<ProducedFile> ConvertAsync(
        string path, TextToPdfOptions options, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null) =>
        Task.Run(() =>
        {
            if (options.FontSize < 5 || options.FontSize > 72) throw new PdfEngineException("O tamanho da fonte deve ficar entre 5 e 72.");
            var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(path) + ".pdf", target, OperationName, [path], reserved);

            string text;
            try
            {
                text = ReadText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PdfEngineException($"Não foi possível ler {Path.GetFileName(path)}: {ex.Message}", ex);
            }

            EnsureFontResolver();
            using var document = new PdfDocument();
            document.Info.Creator = "PDF Master";
            document.Info.Title = Path.GetFileNameWithoutExtension(path);

            var font = new XFont(options.Monospace ? "Consolas" : "Segoe UI", options.FontSize);
            var (pageWidth, pageHeight) = options.Landscape ? (841.89, 595.276) : (595.276, 841.89);
            var margin = Math.Clamp(options.MarginMm, 0, 80) * PointsPerMm;
            var lineHeight = options.FontSize * 1.35;
            var maxWidth = pageWidth - 2 * margin;

            XGraphics? graphics = null;
            var y = 0.0;
            try
            {
                void NewPage()
                {
                    graphics?.Dispose();
                    var page = document.AddPage();
                    page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(pageWidth, pageHeight));
                    graphics = XGraphics.FromPdfPage(page);
                    y = margin + options.FontSize;
                }

                NewPage();
                foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    ct.ThrowIfCancellationRequested();
                    var segments = rawLine.Split('\f');
                    for (var s = 0; s < segments.Length; s++)
                    {
                        if (s > 0) NewPage(); // quebra de página explícita no texto
                        // A lista é calculada antes de desenhar: mudar de página troca o objeto gráfico.
                        var wrapped = Wrap(graphics!, font, segments[s].Replace("\t", "    "), maxWidth).ToList();
                        foreach (var line in wrapped)
                        {
                            if (y > pageHeight - margin) NewPage();
                            if (line.Length > 0) graphics!.DrawString(line, font, XBrushes.Black, new XPoint(margin, y));
                            y += lineHeight;
                        }
                    }
                }
            }
            finally
            {
                graphics?.Dispose();
            }

            ct.ThrowIfCancellationRequested();
            var pages = document.PageCount;
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => document.Save(temp));
            return new ProducedFile(output, bytes, pages);
        }, ct);

    private static IEnumerable<string> Wrap(XGraphics graphics, XFont font, string line, double maxWidth)
    {
        line = RemoveControlChars(line);
        if (line.Length == 0 || graphics.MeasureString(line, font).Width <= maxWidth)
        {
            yield return line;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in SplitKeepingSpaces(line))
        {
            if (graphics.MeasureString(current + word, font).Width <= maxWidth)
            {
                current.Append(word);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString().TrimEnd();
                current.Clear();
            }

            // Palavra mais larga que a linha: é quebrada por caracteres.
            var piece = word.TrimStart();
            while (piece.Length > 0 && graphics.MeasureString(piece, font).Width > maxWidth)
            {
                var take = piece.Length - 1;
                while (take > 1 && graphics.MeasureString(piece[..take], font).Width > maxWidth) take--;
                yield return piece[..take];
                piece = piece[take..];
            }

            current.Append(piece);
        }

        if (current.Length > 0) yield return current.ToString().TrimEnd();
    }

    /// <summary>Divide em palavras mantendo os espaços à esquerda de cada uma ("a  b" → "a", "  b").</summary>
    private static IEnumerable<string> SplitKeepingSpaces(string line)
    {
        var start = 0;
        var inWord = false;
        for (var i = 0; i < line.Length; i++)
        {
            var isSpace = line[i] == ' ';
            if (isSpace && inWord)
            {
                yield return line[start..i];
                start = i;
                inWord = false;
            }
            else if (!isSpace)
            {
                inWord = true;
            }
        }

        if (start < line.Length) yield return line[start..];
    }

    private static string RemoveControlChars(string text)
    {
        if (!text.Any(char.IsControl)) return text;
        return new string(text.Where(c => !char.IsControl(c)).ToArray());
    }

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>O PDFsharp (versão "Core") precisa que o programa informe onde estão as fontes.</summary>
    private static void EnsureFontResolver() => WindowsFonts.EnsureInstalled();
}
