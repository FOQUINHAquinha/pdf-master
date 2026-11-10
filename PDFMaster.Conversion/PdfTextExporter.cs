using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using PDFMaster.PdfEngine;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PDFMaster.Conversion;

public enum TextExportFormat
{
    Txt,
    Html,
    Docx,
}

/// <summary>
/// PDF → TXT, HTML ou DOCX em "texto corrido": recupera o texto do documento em parágrafos, na
/// ordem de leitura. É uma conversão de conteúdo, não de aparência — imagens, tabelas, colunas,
/// fontes e posição dos elementos não são reproduzidos. PDFs digitalizados (sem camada de texto)
/// não têm o que extrair; para eles é preciso OCR.
/// </summary>
public sealed class PdfTextExporter
{
    public const string WordOperationName = "PDF para Word";
    public const string TextOperationName = "PDF para texto e HTML";

    private readonly OutputPlanner _planner;
    private readonly LibreOffice _office;

    public PdfTextExporter(OutputPlanner planner, LibreOffice office)
    {
        _planner = planner;
        _office = office;
    }

    /// <summary>Texto de cada página, na ordem de leitura. Parágrafos são separados por linha em branco.</summary>
    public static IReadOnlyList<string> ReadPages(string path, CancellationToken ct = default)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var pages = new List<string>(document.NumberOfPages);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                pages.Add(ContentOrderTextExtractor.GetText(page, true) ?? string.Empty);
            }

            return pages;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PdfEngineException(
                $"Não foi possível ler o texto de {Path.GetFileName(path)}. O arquivo pode estar protegido por senha ou danificado.", ex);
        }
    }

    public Task<ProducedFile> ExportAsync(
        string path, TextExportFormat format, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null) =>
        Task.Run(() =>
        {
            var extension = format switch
            {
                TextExportFormat.Html => ".html",
                TextExportFormat.Docx => ".docx",
                _ => ".txt",
            };
            var operation = format == TextExportFormat.Docx ? WordOperationName : TextOperationName;
            var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(path) + extension, target, operation, [path], reserved);

            var pages = ReadPages(path, ct);
            if (!pages.Any(p => p.Any(c => !char.IsWhiteSpace(c))))
            {
                throw new PdfEngineException(
                    $"{Path.GetFileName(path)} não tem texto para extrair — provavelmente é um documento digitalizado. " +
                    "O reconhecimento de texto (OCR) chega na Fase 4.");
            }

            var title = Path.GetFileNameWithoutExtension(path);
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                switch (format)
                {
                    case TextExportFormat.Html:
                        File.WriteAllText(temp, BuildHtml(title, pages), new UTF8Encoding(true));
                        break;
                    case TextExportFormat.Docx:
                        DocxWriter.Write(temp, pages.Select(Paragraphs).ToList());
                        break;
                    default:
                        File.WriteAllText(temp, BuildText(pages), new UTF8Encoding(true));
                        break;
                }
            });

            return new ProducedFile(output, bytes, pages.Count);
        }, ct);

    /// <summary>
    /// PDF → DOCX tentando manter a aparência, pelo importador de PDF do LibreOffice. O resultado
    /// usa caixas de texto posicionadas: parece com o original, mas é menos confortável de editar.
    /// </summary>
    public async Task<ProducedFile> ExportWithLayoutAsync(
        string path, int pageCount, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(path) + ".docx", target, WordOperationName, [path], reserved);
        var produced = await _office.ConvertAsync(path, "docx", "writer_pdf_import", ct).ConfigureAwait(false);
        try
        {
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.Copy(produced, temp, overwrite: true));
            return new ProducedFile(output, bytes, pageCount);
        }
        finally
        {
            LibreOffice.DeleteWorkFolder(produced);
        }
    }

    /// <summary>Divide o texto de uma página em parágrafos; as linhas de um parágrafo são unidas.</summary>
    public static IReadOnlyList<string> Paragraphs(string pageText)
    {
        var result = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0) result.Add(text);
            current.Clear();
        }

        foreach (var raw in pageText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                // Palavra dividida por hífen no fim da linha: junta sem espaço.
                if (current[^1] == '-' && current.Length > 1 && char.IsLetter(current[^2]) && char.IsLower(line[0]))
                    current.Length--;
                else
                    current.Append(' ');
            }

            current.Append(line);
        }

        Flush();
        return result;
    }

    private static string BuildText(IReadOnlyList<string> pages)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0) sb.Append("\r\n\r\n");
            sb.Append(pages[i].Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Trim());
        }

        sb.Append("\r\n");
        return sb.ToString();
    }

    private static string BuildHtml(string title, IReadOnlyList<string> pages)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"pt-BR\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(WebUtility.HtmlEncode(title))
            .Append("</title>\n<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:50em;margin:2em auto;line-height:1.5;padding:0 1em}")
            .Append("section{border-bottom:1px solid #ccc;padding-bottom:1em;margin-bottom:1em}</style>\n</head>\n<body>\n");
        for (var i = 0; i < pages.Count; i++)
        {
            sb.Append("<section id=\"pagina-").Append(i + 1).Append("\">\n");
            foreach (var paragraph in Paragraphs(pages[i]))
            {
                sb.Append("<p>").Append(WebUtility.HtmlEncode(paragraph)).Append("</p>\n");
            }

            sb.Append("</section>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }
}

/// <summary>
/// Grava um DOCX mínimo e válido (parágrafos de texto, com quebra de página entre as páginas do
/// original), sem depender de bibliotecas: um DOCX é um arquivo ZIP com alguns XML.
/// </summary>
public static class DocxWriter
{
    /// <param name="pages">Para cada página do documento, a lista de parágrafos.</param>
    public static void Write(string path, IReadOnlyList<IReadOnlyList<string>> pages)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>");

        AddEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>");

        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");

        var first = true;
        foreach (var page in pages)
        {
            IReadOnlyList<string> paragraphs = page.Count > 0 ? page : new[] { string.Empty };
            for (var i = 0; i < paragraphs.Count; i++)
            {
                body.Append("<w:p><w:pPr><w:spacing w:after=\"160\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr><w:r>")
                    .Append("<w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr>");
                if (i == 0 && !first) body.Append("<w:br w:type=\"page\"/>");
                body.Append("<w:t xml:space=\"preserve\">").Append(Escape(paragraphs[i])).Append("</w:t></w:r></w:p>");
            }

            first = false;
        }

        body.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>")
            .Append("<w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/>")
            .Append("</w:sectPr></w:body></w:document>");
        AddEntry(zip, "word/document.xml", body.ToString());
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Escapa para XML e remove caracteres que o XML 1.0 não aceita.</summary>
    private static string Escape(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF)) clean.Append(ch);
        }

        return SecurityElement.Escape(clean.ToString()) ?? string.Empty;
    }
}
