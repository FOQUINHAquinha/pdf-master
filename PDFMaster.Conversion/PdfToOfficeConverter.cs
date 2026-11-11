using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Conversion;

public enum OfficeFormat
{
    /// <summary>Planilha: uma aba por página, com o texto organizado em linhas e colunas.</summary>
    Excel,

    /// <summary>Apresentação: um slide por página, com a imagem da página.</summary>
    PowerPoint,
}

/// <summary>
/// PDF → XLSX e PDF → PPTX, gravados pelo próprio programa (os formatos Office Open XML são
/// pacotes ZIP com XML). No Excel, as palavras de cada linha são separadas em colunas pelos
/// espaços entre elas e alinhadas pelas posições na página; números no formato brasileiro viram
/// números. No PowerPoint, cada página vira um slide com a imagem da página (o texto não fica
/// editável). Documentos digitalizados não têm texto: para o Excel, use o OCR antes.
/// </summary>
public sealed class PdfToOfficeConverter
{
    public const string OperationName = "PDF para Excel e PowerPoint";
    private const long EmuPerPoint = 12700;

    private readonly IPdfRenderer _renderer;
    private readonly OutputPlanner _planner;

    public PdfToOfficeConverter(IPdfRenderer renderer, OutputPlanner planner)
    {
        _renderer = renderer;
        _planner = planner;
    }

    public async Task<ProducedFile> ConvertAsync(string path, OfficeFormat format, OutputTarget target, int dpi = 150, IProgress<double>? progress = null,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new PdfEngineException($"Arquivo não encontrado: {full}");
        if (dpi is < 72 or > 300) throw new PdfEngineException("A resolução dos slides deve ficar entre 72 e 300 DPI.");
        var extension = format == OfficeFormat.Excel ? ".xlsx" : ".pptx";
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + extension, target, OperationName, [full], reserved);

        IReadOnlyList<DisplayPageText> pages;
        try
        {
            pages = await Task.Run(() => PdfiumRenderer.ReadDisplayText(full, ct), ct).ConfigureAwait(false);
        }
        catch (PdfRenderException ex)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(full)}\" não pôde ser lido: {ex.Message}", ex);
        }

        if (pages.Count == 0) throw new PdfEngineException("O documento não tem páginas.");
        progress?.Report(0.1);

        long bytes;
        if (format == OfficeFormat.Excel)
        {
            var sheets = pages.Select(ExtractTable).ToList();
            if (sheets.All(s => s.Count == 0))
                throw new PdfEngineException("O documento não tem texto para levar ao Excel (é digitalizado? Use o OCR antes).");
            bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => XlsxWriter.Write(temp, sheets));
        }
        else
        {
            var images = new byte[pages.Count][];
            var sizes = new (int Width, int Height)[pages.Count];
            var done = 0;
            await _renderer.RenderPagesAsync(full, Enumerable.Range(0, pages.Count).ToList(), RenderSize.ByScale(dpi / 72.0), (index, page) =>
            {
                images[index] = Png(page);
                sizes[index] = (page.Width, page.Height);
                progress?.Report(0.1 + 0.8 * Interlocked.Increment(ref done) / pages.Count);
            }, ct).ConfigureAwait(false);
            var first = pages[0];
            bytes = OutputPlanner.WriteAtomically(output, overwrite,
                temp => PptxWriter.Write(temp, (long)(first.Width * EmuPerPoint), (long)(first.Height * EmuPerPoint), images, sizes));
        }

        progress?.Report(1);
        return new ProducedFile(output, bytes, pages.Count);
    }

    private static byte[] Png(RenderedPage page)
    {
        var source = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32, null, page.Bgra, page.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    // ---------------- Texto → tabela ----------------

    private sealed record Cell(double X, double Right, string Text);

    private sealed record Word(double X, double Y, double Right, double Bottom, string Text)
    {
        public double Center => (Y + Bottom) / 2;
        public double Height => Bottom - Y;
    }

    /// <summary>Linhas de células de uma página, já distribuídas em colunas (null = célula vazia).</summary>
    public static IReadOnlyList<IReadOnlyList<string?>> ExtractTable(DisplayPageText page)
    {
        // Palavras: caracteres seguidos na ordem do conteúdo, separados pelos espaços do próprio PDF
        // ou por saltos de posição (outra linha ou volta para a esquerda).
        var words = new List<Word>();
        var current = new List<DisplayChar>();
        void Flush()
        {
            if (current.Count == 0) return;
            words.Add(new Word(current.Min(c => c.X), current.Min(c => c.Y), current.Max(c => c.X + c.Width), current.Max(c => c.Y + c.Height),
                new string(current.Select(c => c.Value).ToArray())));
            current.Clear();
        }

        foreach (var c in page.Chars)
        {
            if (c.Value == ' ' || c.Height <= 0)
            {
                Flush();
                continue;
            }

            if (current.Count > 0)
            {
                var previous = current[^1];
                var sameLine = Math.Abs((previous.Y + previous.Height / 2) - (c.Y + c.Height / 2)) <= Math.Max(previous.Height, c.Height) * 0.5;
                var forward = c.X >= previous.X - 0.5 && c.X - (previous.X + previous.Width) < Math.Max(previous.Height, c.Height) * 0.8;
                if (!sameLine || !forward) Flush();
            }

            current.Add(c);
        }

        Flush();
        if (words.Count == 0) return [];

        // Linhas: palavras cujo centro vertical está próximo.
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderBy(w => w.Center))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line.Average(x => x.Center) - w.Center) <= Math.Max(2, line.Average(x => x.Height) * 0.5)) line.Add(w);
            else lines.Add([w]);
        }

        // Células: dentro da linha, um espaço bem maior que o espaço entre palavras separa colunas.
        var rows = new List<List<Cell>>();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(w => w.X).ToList();
            var gap = ordered.Average(w => w.Height) * 1.2;
            var cells = new List<Cell>();
            var text = new StringBuilder(ordered[0].Text);
            double start = ordered[0].X, right = ordered[0].Right;
            for (var i = 1; i < ordered.Count; i++)
            {
                var w = ordered[i];
                if (w.X - right > gap)
                {
                    cells.Add(new Cell(start, right, text.ToString()));
                    text.Clear().Append(w.Text);
                    start = w.X;
                }
                else
                {
                    text.Append(' ').Append(w.Text);
                }

                right = Math.Max(right, w.Right);
            }

            cells.Add(new Cell(start, right, text.ToString()));
            rows.Add(cells);
        }

        // Colunas: posições de início das células agrupadas (tolerância de 12 pt).
        var anchors = new List<double>();
        foreach (var x in rows.SelectMany(r => r).Select(c => c.X).Order())
        {
            if (anchors.Count == 0 || x - anchors[^1] > 12) anchors.Add(x);
        }

        var table = new List<IReadOnlyList<string?>>();
        foreach (var cells in rows)
        {
            var row = new string?[anchors.Count];
            var lastColumn = -1;
            foreach (var cell in cells)
            {
                var column = anchors.FindLastIndex(a => a <= cell.X + 12);
                column = Math.Max(column, lastColumn + 1);
                if (column >= row.Length) column = row.Length - 1;
                row[column] = row[column] is null ? cell.Text : row[column] + " " + cell.Text;
                lastColumn = column;
            }

            var length = Array.FindLastIndex(row, v => v is not null) + 1;
            table.Add(row.Take(length).ToArray());
        }

        return table;
    }

    /// <summary>Número no formato brasileiro ("1.234,56", "-12,5", "45") ou internacional simples ("12.5").</summary>
    public static bool TryNumber(string text, out double value)
    {
        value = 0;
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 30) return false;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^-?\d{1,3}(\.\d{3})*(,\d+)?$|^-?\d+(,\d+)?$"))
            return double.TryParse(t, NumberStyles.Number, pt, out value);
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^-?\d+\.\d+$"))
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return false;
    }

    internal static string Xml(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) clean.Append(c);
        }

        return SecurityElement.Escape(clean.ToString()) ?? string.Empty;
    }

    internal static void Entry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static void Entry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }
}

/// <summary>XLSX mínimo e válido: uma aba por página, textos como "inlineStr" e números como valores.</summary>
public static class XlsxWriter
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static void Write(string path, IReadOnlyList<IReadOnlyList<IReadOnlyList<string?>>> sheets)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var types = new StringBuilder(Header)
            .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
            .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
            .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>")
            .Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= sheets.Count; i++)
            types.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        types.Append("</Types>");
        PdfToOfficeConverter.Entry(zip, "[Content_Types].xml", types.ToString());
        PdfToOfficeConverter.Entry(zip, "_rels/.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        var workbook = new StringBuilder(Header)
            .Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var rels = new StringBuilder(Header).Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= sheets.Count; i++)
        {
            workbook.Append($"<sheet name=\"Página {i}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
            rels.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        }

        workbook.Append("</sheets></workbook>");
        rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>")
            .Append("</Relationships>");
        PdfToOfficeConverter.Entry(zip, "xl/workbook.xml", workbook.ToString());
        PdfToOfficeConverter.Entry(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
        PdfToOfficeConverter.Entry(zip, "xl/styles.xml", Header +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>");

        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = new StringBuilder(Header)
                .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            var rows = sheets[i];
            for (var r = 0; r < rows.Count; r++)
            {
                sheet.Append($"<row r=\"{r + 1}\">");
                for (var c = 0; c < rows[r].Count; c++)
                {
                    if (rows[r][c] is not { Length: > 0 } text) continue;
                    var reference = Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                    if (PdfToOfficeConverter.TryNumber(text, out var number))
                        sheet.Append($"<c r=\"{reference}\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    else
                        sheet.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{PdfToOfficeConverter.Xml(text)}</t></is></c>");
                }

                sheet.Append("</row>");
            }

            sheet.Append("</sheetData></worksheet>");
            PdfToOfficeConverter.Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", sheet.ToString());
        }
    }

    public static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}

/// <summary>PPTX mínimo e válido: um slide por página, com a imagem da página ocupando o slide.</summary>
public static class PptxWriter
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Write(string path, long slideWidth, long slideHeight, IReadOnlyList<byte[]> images, IReadOnlyList<(int Width, int Height)> sizes)
    {
        // Limites do PowerPoint para o tamanho do slide (1 a 56 polegadas).
        slideWidth = Math.Clamp(slideWidth, 914400, 51206400);
        slideHeight = Math.Clamp(slideHeight, 914400, 51206400);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var types = new StringBuilder(Header)
            .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
            .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
            .Append("<Default Extension=\"png\" ContentType=\"image/png\"/>")
            .Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>")
            .Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>")
            .Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>")
            .Append("<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        for (var i = 1; i <= images.Count; i++)
            types.Append($"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        types.Append("</Types>");
        PdfToOfficeConverter.Entry(zip, "[Content_Types].xml", types.ToString());
        PdfToOfficeConverter.Entry(zip, "_rels/.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");

        var presentation = new StringBuilder(Header)
            .Append($"<p:presentation xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" saveSubsetFonts=\"1\">")
            .Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>");
        var presentationRels = new StringBuilder(Header)
            .Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">")
            .Append($"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>")
            .Append($"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>");
        for (var i = 1; i <= images.Count; i++)
        {
            presentation.Append($"<p:sldId id=\"{255 + i}\" r:id=\"rId{i + 2}\"/>");
            presentationRels.Append($"<Relationship Id=\"rId{i + 2}\" Type=\"{Rel}/slide\" Target=\"slides/slide{i}.xml\"/>");
        }

        presentation.Append("</p:sldIdLst>")
            .Append($"<p:sldSz cx=\"{slideWidth}\" cy=\"{slideHeight}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>")
            .Append("<p:defaultTextStyle><a:lvl1pPr><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:defaultTextStyle></p:presentation>");
        presentationRels.Append("</Relationships>");
        PdfToOfficeConverter.Entry(zip, "ppt/presentation.xml", presentation.ToString());
        PdfToOfficeConverter.Entry(zip, "ppt/_rels/presentation.xml.rels", presentationRels.ToString());

        const string emptyTree = "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                                 "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
                                 "</p:spTree></p:cSld>";
        PdfToOfficeConverter.Entry(zip, "ppt/slideMasters/slideMaster1.xml", Header +
            $"<p:sldMaster xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\">" + emptyTree +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
            "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle>" +
            "<p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr></p:bodyStyle>" +
            "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>");
        PdfToOfficeConverter.Entry(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
        PdfToOfficeConverter.Entry(zip, "ppt/slideLayouts/slideLayout1.xml", Header +
            $"<p:sldLayout xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" type=\"blank\" preserve=\"1\">" +
            emptyTree.Replace("<p:cSld>", "<p:cSld name=\"Em branco\">", StringComparison.Ordinal) +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        PdfToOfficeConverter.Entry(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Header +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        PdfToOfficeConverter.Entry(zip, "ppt/theme/theme1.xml", Theme);

        for (var i = 0; i < images.Count; i++)
        {
            // A imagem ocupa o slide mantendo a proporção da página.
            var (w, h) = sizes[i];
            var scale = Math.Min(slideWidth / (double)w, slideHeight / (double)h);
            long cx = (long)(w * scale), cy = (long)(h * scale);
            long x = (slideWidth - cx) / 2, y = (slideHeight - cy) / 2;
            PdfToOfficeConverter.Entry(zip, $"ppt/media/image{i + 1}.png", images[i]);
            PdfToOfficeConverter.Entry(zip, $"ppt/slides/slide{i + 1}.xml", Header +
                $"<p:sld xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"><p:cSld><p:spTree>" +
                "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
                $"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Página {i + 1}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>" +
                "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            PdfToOfficeConverter.Entry(zip, $"ppt/slides/_rels/slide{i + 1}.xml.rels", Header +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{Rel}/image\" Target=\"../media/image{i + 1}.png\"/></Relationships>");
        }
    }

    private static string Color(string name, string hex) => $"<a:{name}><a:srgbClr val=\"{hex}\"/></a:{name}>";

    private static readonly string Theme = Header +
        $"<a:theme xmlns:a=\"{NsA}\" name=\"PDF Master\"><a:themeElements>" +
        "<a:clrScheme name=\"PDF Master\">" +
        "<a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>" +
        Color("dk2", "1F2937") + Color("lt2", "F3F4F6") + Color("accent1", "15803D") + Color("accent2", "2563EB") + Color("accent3", "D97706") +
        Color("accent4", "7C3AED") + Color("accent5", "DC2626") + Color("accent6", "0891B2") + Color("hlink", "2563EB") + Color("folHlink", "7C3AED") +
        "</a:clrScheme>" +
        "<a:fontScheme name=\"PDF Master\"><a:majorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"PDF Master\">" +
        "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>" +
        "<a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements></a:theme>";
}
