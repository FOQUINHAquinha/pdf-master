using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Security;

/// <summary>Valores finais do dicionário de informações (null = chave ausente).</summary>
internal sealed record InfoValues(
    string? Title, string? Author, string? Subject, string? Keywords, string? Creator, string? Producer,
    DateTimeOffset? Created, DateTimeOffset? Modified)
{
    public static InfoValues From(DocumentProperties p) => new(
        Blank(p.Title), Blank(p.Author), Blank(p.Subject), Blank(p.Keywords), Blank(p.Creator), Blank(p.Producer),
        TruncateLocal(p.Created), TruncateLocal(p.Modified));

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>As datas do PDF têm precisão de segundos.</summary>
    public static DateTimeOffset? TruncateLocal(DateTime? value) =>
        value is { } v ? Truncate(new DateTimeOffset(v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Local) : v)) : null;

    public static DateTimeOffset Truncate(DateTimeOffset v) => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, v.Offset);
}

/// <summary>
/// Grava o dicionário de informações exatamente como pedido. O PDFsharp acrescenta o próprio nome em
/// "Produtor" e "Criador" ao salvar; por isso o arquivo salvo por ele recebe uma atualização incremental
/// com o dicionário final, e o PDFium regrava tudo numa única revisão (sem a versão antiga do dicionário).
/// </summary>
internal static partial class DocumentInfoWriter
{
    [GeneratedRegex(@"startxref\s+(?<xref>\d+)")]
    private static partial Regex StartXrefPattern();

    [GeneratedRegex(@"/(?<key>Size|Root|Info)\s+(?<a>\d+)(?:\s+(?<b>\d+)\s+R)?")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"/ID\s*\[[^\]]*\]")]
    private static partial Regex IdPattern();

    /// <summary>
    /// <paramref name="savedByPdfSharp"/> → <paramref name="output"/>: troca o dicionário de
    /// informações (e o bloco XMP, quando pedido) e regrava o arquivo com o PDFium (versão do cabeçalho
    /// opcional, ex.: 14 = 1.4). O PDFsharp grava a tabela de referências compactada e gera o próprio
    /// XMP; por isso o PDFium regrava primeiro numa tabela clássica, onde a atualização incremental é
    /// acrescentada, e depois numa única revisão final.
    /// </summary>
    /// <param name="xmp">Novo bloco XMP; null mantém o que houver.</param>
    /// <param name="removeXmp">Retira o bloco XMP do catálogo.</param>
    public static void Apply(string savedByPdfSharp, InfoValues values, string output, int fileVersion = 0, byte[]? xmp = null, bool removeXmp = false)
    {
        var classic = savedByPdfSharp + ".classic";
        var patched = savedByPdfSharp + ".info";
        try
        {
            PdfiumRenderer.SaveCopy(savedByPdfSharp, null, classic);
            var bytes = File.ReadAllBytes(classic);
            var trailer = ReadTrailer(bytes);
            var next = trailer.Size;
            var objects = new List<(int Number, int Generation, byte[] Body)>();
            var info = trailer.InfoNumber > 0 ? (trailer.InfoNumber, trailer.InfoGeneration) : (next++, 0);
            objects.Add((info.Item1, info.Item2, Encoding.Latin1.GetBytes(Dictionary(values))));

            if (xmp is not null || removeXmp)
            {
                var catalog = ObjectText(bytes, trailer.RootNumber, trailer.RootGeneration)
                              ?? throw new PdfEngineException("O catálogo do documento não foi encontrado.");
                var current = MetadataReference().Match(catalog);
                if (removeXmp)
                {
                    if (current.Success) objects.Add((trailer.RootNumber, trailer.RootGeneration, Encoding.Latin1.GetBytes(catalog.Remove(current.Index, current.Length))));
                }
                else if (current.Success)
                {
                    objects.Add((int.Parse(current.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(current.Groups[2].Value, CultureInfo.InvariantCulture), MetadataStream(xmp!)));
                }
                else
                {
                    var number = next++;
                    objects.Add((number, 0, MetadataStream(xmp!)));
                    var open = catalog.IndexOf("<<", StringComparison.Ordinal);
                    if (open < 0) throw new PdfEngineException("O catálogo do documento está malformado.");
                    objects.Add((trailer.RootNumber, trailer.RootGeneration,
                        Encoding.Latin1.GetBytes(catalog.Insert(open + 2, string.Create(CultureInfo.InvariantCulture, $" /Metadata {number} 0 R ")))));
                }
            }

            File.WriteAllBytes(patched, Append(bytes, trailer, objects, info.Item1, info.Item2));
            PdfiumRenderer.SaveCopy(patched, null, output, fileVersion);
        }
        catch (PdfRenderException ex)
        {
            throw new PdfEngineException("O arquivo final não pôde ser gravado: " + ex.Message, ex);
        }
        finally
        {
            TryDelete(classic);
            TryDelete(patched);
        }
    }

    [GeneratedRegex(@"/Metadata\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex MetadataReference();

    private static byte[] MetadataStream(byte[] xmp) =>
        [.. Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n")),
         .. xmp, .. "\nendstream"u8.ToArray()];

    /// <summary>Dados do último trailer de um arquivo com tabela de referências clássica.</summary>
    internal sealed record TrailerInfo(int Size, int RootNumber, int RootGeneration, int InfoNumber, int InfoGeneration, string? Id, string Prev);

    internal static TrailerInfo ReadTrailer(byte[] bytes)
    {
        var tailLength = Math.Min(bytes.Length, 8192);
        var tail = Encoding.Latin1.GetString(bytes, bytes.Length - tailLength, tailLength);
        var trailerAt = tail.LastIndexOf("trailer", StringComparison.Ordinal);
        var starts = StartXrefPattern().Matches(tail);
        if (trailerAt < 0 || starts.Count == 0 || starts[^1].Index < trailerAt)
            throw new PdfEngineException("Estrutura final do arquivo inesperada (sem trailer).");
        var body = tail[trailerAt..starts[^1].Index];

        int size = 0, rootNumber = 0, rootGeneration = 0, infoNumber = 0, infoGeneration = 0;
        foreach (Match entry in EntryPattern().Matches(body))
        {
            var a = int.Parse(entry.Groups["a"].Value, CultureInfo.InvariantCulture);
            var b = entry.Groups["b"].Success ? int.Parse(entry.Groups["b"].Value, CultureInfo.InvariantCulture) : 0;
            switch (entry.Groups["key"].Value)
            {
                case "Size": size = a; break;
                case "Root" when entry.Groups["b"].Success: (rootNumber, rootGeneration) = (a, b); break;
                case "Info" when entry.Groups["b"].Success: (infoNumber, infoGeneration) = (a, b); break;
            }
        }

        if (size == 0 || rootNumber == 0) throw new PdfEngineException("Não foi possível localizar a raiz do documento.");
        var id = IdPattern().Match(body);
        return new TrailerInfo(size, rootNumber, rootGeneration, infoNumber, infoGeneration, id.Success ? id.Value : null, starts[^1].Groups["xref"].Value);
    }

    /// <summary>Texto de um objeto sem fluxo (entre "obj" e "endobj"), localizado pela tabela clássica do arquivo.</summary>
    internal static string? ObjectText(byte[] bytes, int number, int generation)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var header = new Regex($@"(?<![\d]){number}\s+{generation}\s+obj\b");
        var matches = header.Matches(text);
        if (matches.Count == 0) return null;
        var start = matches[^1].Index + matches[^1].Length;
        var end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end].Trim();
    }

    /// <summary>Acrescenta uma revisão (atualização incremental) com os objetos dados.</summary>
    internal static byte[] Append(byte[] bytes, TrailerInfo trailer, IReadOnlyList<(int Number, int Generation, byte[] Body)> objects, int infoNumber, int infoGeneration)
    {
        using var update = new MemoryStream();
        void Write(string s) => update.Write(Encoding.Latin1.GetBytes(s));
        Write("\n");
        var offsets = new List<(int Number, int Generation, long Offset)>();
        foreach (var (number, generation, body) in objects)
        {
            offsets.Add((number, generation, bytes.LongLength + update.Position));
            Write(string.Create(CultureInfo.InvariantCulture, $"{number} {generation} obj\n"));
            update.Write(body);
            Write("\nendobj\n");
        }

        var xref = bytes.LongLength + update.Position;
        Write("xref\n0 1\n0000000000 65535 f\r\n");
        foreach (var (number, generation, offset) in offsets.OrderBy(o => o.Number))
            Write(string.Create(CultureInfo.InvariantCulture, $"{number} 1\n{offset:D10} {generation:D5} n\r\n"));
        var size = Math.Max(trailer.Size, objects.Max(o => o.Number) + 1);
        Write(string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {size} /Root {trailer.RootNumber} {trailer.RootGeneration} R /Info {infoNumber} {infoGeneration} R /Prev {trailer.Prev}{(trailer.Id is { } id ? " " + id : string.Empty)} >>\n"));
        Write(string.Create(CultureInfo.InvariantCulture, $"startxref\n{xref}\n%%EOF\n"));
        return [.. bytes, .. update.ToArray()];
    }

    /// <summary>Lê o dicionário de informações de um arquivo pronto (para conferência).</summary>
    public static InfoValues Read(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var info = document.Info.Elements;
        string? S(string key) => info.ContainsKey(key) ? info.GetString(key) : null;
        DateTimeOffset? D(string key) => info.ContainsKey(key) && ParseDate(info.GetString(key)) is { } d ? d : null;
        return new InfoValues(S("/Title"), S("/Author"), S("/Subject"), S("/Keywords"), S("/Creator"), S("/Producer"), D("/CreationDate"), D("/ModDate"));
    }

    private static string Dictionary(InfoValues v)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (value is not null) parts.Add($"/{key} {PdfText(value)}");
        }

        Add("Title", v.Title);
        Add("Author", v.Author);
        Add("Subject", v.Subject);
        Add("Keywords", v.Keywords);
        Add("Creator", v.Creator);
        Add("Producer", v.Producer);
        if (v.Created is { } created) parts.Add($"/CreationDate ({PdfDate(created)})");
        if (v.Modified is { } modified) parts.Add($"/ModDate ({PdfDate(modified)})");
        return "<< " + string.Join(" ", parts) + " >>";
    }

    /// <summary>Texto ASCII como literal; o resto em UTF-16BE (hexadecimal, com BOM).</summary>
    internal static string PdfText(string value)
    {
        if (value.All(c => c is >= ' ' and <= '~'))
            return "(" + value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
    }

    internal static string PdfDate(DateTimeOffset value)
    {
        var offset = value.Offset;
        var zone = offset == TimeSpan.Zero ? "Z" : $"{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
        return "D:" + value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }

    internal static string XmpDate(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) +
        (value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture));

    /// <summary>Data no formato do PDF ("D:AAAAMMDDHHmmSS+HH'mm'"), com as partes opcionais.</summary>
    internal static DateTimeOffset? ParseDate(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        var m = Regex.Match(s, @"^(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?\s*(?:(Z)|([+\-])(\d{2})'?(\d{2})?'?)?");
        if (!m.Success) return null;
        int G(int i, int fallback) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : fallback;
        var offset = m.Groups[8].Success
            ? new TimeSpan(G(9, 0), G(10, 0), 0) * (m.Groups[8].Value == "-" ? -1 : 1)
            : TimeSpan.Zero;
        try
        {
            return new DateTimeOffset(G(1, 1), G(2, 1), G(3, 1), G(4, 0), G(5, 0), G(6, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporário: o sistema limpa depois.
        }
    }
}

/// <summary>Bloco de metadados XMP coerente com o dicionário de informações.</summary>
internal static class Xmp
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace XmpMM = "http://ns.adobe.com/xap/1.0/mm/";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    /// <param name="pdfAPart">1, 2 ou 3 para declarar PDF/A (nível B); 0 para não declarar.</param>
    public static byte[] Build(InfoValues v, int pdfAPart, string documentId)
    {
        var description = new XElement(Rdf + "Description",
            new XAttribute(Rdf + "about", string.Empty),
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "xmp", XmpNs),
            new XAttribute(XNamespace.Xmlns + "pdf", PdfNs),
            new XAttribute(XNamespace.Xmlns + "xmpMM", XmpMM));
        if (pdfAPart > 0) description.Add(new XAttribute(XNamespace.Xmlns + "pdfaid", PdfAId));

        XElement Alt(XName name, string value) => new(name, new XElement(Rdf + "Alt",
            new XElement(Rdf + "li", new XAttribute(XmlNs + "lang", "x-default"), value)));

        if (v.Title is { } title) description.Add(Alt(Dc + "title", title));
        if (v.Author is { } author) description.Add(new XElement(Dc + "creator", new XElement(Rdf + "Seq", new XElement(Rdf + "li", author))));
        if (v.Subject is { } subject) description.Add(Alt(Dc + "description", subject));
        description.Add(new XElement(Dc + "format", "application/pdf"));
        if (v.Keywords is { } keywords) description.Add(new XElement(PdfNs + "Keywords", keywords));
        if (v.Producer is { } producer) description.Add(new XElement(PdfNs + "Producer", producer));
        if (v.Creator is { } creator) description.Add(new XElement(XmpNs + "CreatorTool", creator));
        if (v.Created is { } created) description.Add(new XElement(XmpNs + "CreateDate", Info(created)));
        if (v.Modified is { } modified)
        {
            description.Add(new XElement(XmpNs + "ModifyDate", Info(modified)));
            description.Add(new XElement(XmpNs + "MetadataDate", Info(modified)));
        }

        description.Add(new XElement(XmpMM + "DocumentID", "uuid:" + documentId));
        if (pdfAPart > 0)
        {
            description.Add(new XElement(PdfAId + "part", pdfAPart.ToString(CultureInfo.InvariantCulture)));
            description.Add(new XElement(PdfAId + "conformance", "B"));
        }

        var meta = new XElement(X + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", X),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf), description));
        var body = meta.ToString(SaveOptions.None);
        var packet = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" + body + "\n" +
                     new string(' ', 2000) + "\n<?xpacket end=\"w\"?>";
        return new UTF8Encoding(false).GetBytes(packet);

        static string Info(DateTimeOffset d) => DocumentInfoWriter.XmpDate(d);
    }

    /// <summary>Lê parte e conformidade PDF/A declaradas (0 e "" quando ausentes).</summary>
    public static (int Part, string Conformance) ReadPdfAId(byte[] xmp)
    {
        var doc = Parse(xmp);
        if (doc is null) return (0, string.Empty);
        string? Value(string name) =>
            doc.Descendants(PdfAId + name).FirstOrDefault()?.Value ??
            doc.Descendants(Rdf + "Description").Select(d => d.Attribute(PdfAId + name)?.Value).FirstOrDefault(a => a is not null);
        return (int.TryParse(Value("part"), CultureInfo.InvariantCulture, out var part) ? part : 0, Value("conformance") ?? string.Empty);
    }

    /// <summary>Valores do XMP equivalentes ao dicionário de informações (para conferir a coerência).</summary>
    public static InfoValues ReadInfo(byte[] xmp)
    {
        var doc = Parse(xmp);
        if (doc is null) return new InfoValues(null, null, null, null, null, null, null, null);
        string? First(XName name) =>
            doc.Descendants(name).Select(e => e.Descendants(Rdf + "li").FirstOrDefault()?.Value ?? e.Value).FirstOrDefault() ??
            doc.Descendants(Rdf + "Description").Select(d => d.Attribute(name)?.Value).FirstOrDefault(a => a is not null);
        DateTimeOffset? Date(XName name) => DateTimeOffset.TryParse(First(name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return new InfoValues(First(Dc + "title"), First(Dc + "creator"), First(Dc + "description"), First(PdfNs + "Keywords"),
            First(XmpNs + "CreatorTool"), First(PdfNs + "Producer"), Date(XmpNs + "CreateDate"), Date(XmpNs + "ModifyDate"));
    }

    private static XDocument? Parse(byte[] xmp)
    {
        try
        {
            var text = Encoding.UTF8.GetString(xmp).TrimStart('﻿');
            var start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
            var end = text.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal);
            var close = "</x:xmpmeta>";
            if (end < 0)
            {
                end = text.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal);
                close = "</rdf:RDF>";
            }

            if (start < 0 || end < 0) return null;
            return XDocument.Parse(text[start..(end + close.Length)]);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
