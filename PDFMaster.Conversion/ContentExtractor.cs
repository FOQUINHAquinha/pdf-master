using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Conversion;

/// <summary>O que extrair do PDF.</summary>
public sealed record ExtractOptions(bool Images = true, bool Text = true, bool Attachments = true, bool Fonts = false, bool Metadata = true);

/// <summary>Resultado: a pasta criada e quantos itens de cada tipo foram gravados.</summary>
public sealed record ExtractResult(string Folder, int Images, int TextPages, int Attachments, int Fonts, bool Metadata, IReadOnlyList<string> Notes)
{
    public int Files => Images + (TextPages > 0 ? 1 : 0) + Attachments + Fonts + (Metadata ? 1 : 0);
}

/// <summary>
/// Extrai o conteúdo de um PDF para uma pasta nova: imagens incorporadas (JPEG como estão; as
/// demais em PNG), o texto de cada página, os anexos, as fontes incorporadas e os metadados.
/// O PDF original não é alterado.
/// </summary>
public sealed class ContentExtractor
{
    public const string OperationName = "Extrair conteúdo";

    public Task<ExtractResult> ExtractAsync(string path, ExtractOptions options, string outputFolder, IProgress<double>? progress = null,
        CancellationToken ct = default) => Task.Run(() =>
    {
        if (!options.Images && !options.Text && !options.Attachments && !options.Fonts && !options.Metadata)
            throw new PdfEngineException("Escolha pelo menos um tipo de conteúdo para extrair.");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new PdfEngineException($"Arquivo não encontrado: {path}");

        PdfDocument document;
        try
        {
            document = PdfReader.Open(full, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfEngineException("O documento está protegido por senha. Use \"Remover proteção\" antes de extrair o conteúdo.", ex);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or IOException or NotSupportedException)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(full)}\" não pôde ser lido como PDF: {ex.Message}", ex);
        }

        using (document)
        {
            var folder = FreeFolder(Path.Combine(Path.GetFullPath(outputFolder), Path.GetFileNameWithoutExtension(full) + "_conteudo"));
            Directory.CreateDirectory(folder);
            var notes = new List<string>();
            int images = 0, textPages = 0, attachments = 0, fonts = 0;
            var metadata = false;

            if (options.Images)
            {
                ct.ThrowIfCancellationRequested();
                images = ExtractImages(full, Path.Combine(folder, "imagens"), notes, ct);
            }

            progress?.Report(0.4);
            if (options.Text)
            {
                ct.ThrowIfCancellationRequested();
                var pages = PdfTextExporter.ReadPages(full, ct);
                textPages = pages.Count(p => p.Trim().Length > 0);
                if (textPages > 0)
                {
                    var builder = new StringBuilder();
                    for (var i = 0; i < pages.Count; i++)
                    {
                        builder.Append("===== Página ").Append(i + 1).AppendLine(" =====");
                        builder.AppendLine(pages[i].TrimEnd()).AppendLine();
                    }

                    File.WriteAllText(Path.Combine(folder, "texto.txt"), builder.ToString(), new UTF8Encoding(true));
                }
                else
                {
                    notes.Add("Nenhum texto encontrado (documento digitalizado? Use o OCR antes).");
                }
            }

            progress?.Report(0.6);
            if (options.Attachments) attachments = ExtractAttachments(document, Path.Combine(folder, "anexos"), notes);
            progress?.Report(0.75);
            if (options.Fonts) fonts = ExtractFonts(document, Path.Combine(folder, "fontes"), notes);
            progress?.Report(0.9);
            if (options.Metadata) metadata = WriteMetadata(document, folder);

            progress?.Report(1);
            return new ExtractResult(folder, images, textPages, attachments, fonts, metadata, notes);
        }
    }, ct);

    private static int ExtractImages(string pdf, string folder, List<string> notes, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var counter = new Dictionary<int, int>();
        var written = 0;
        var skipped = 0;
        PdfiumRenderer.ExtractImages(pdf, null, image =>
        {
            var key = Convert.ToHexString(SHA256.HashData(image.Jpeg ?? image.Pixels!));
            if (!seen.Add(key)) return; // a mesma imagem usada em várias páginas é gravada uma vez
            counter[image.PageIndex] = counter.GetValueOrDefault(image.PageIndex) + 1;
            var name = $"pagina-{image.PageIndex + 1:000}_{counter[image.PageIndex]:00}";
            Directory.CreateDirectory(folder);
            if (image.Jpeg is { } jpeg)
            {
                File.WriteAllBytes(Path.Combine(folder, name + ".jpg"), jpeg);
                written++;
                return;
            }

            var source = ToBitmap(image);
            if (source is null)
            {
                skipped++;
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(stream);
            written++;
        }, ct);
        if (written == 0) notes.Add("Nenhuma imagem incorporada encontrada.");
        if (skipped > 0) notes.Add($"{skipped} imagem(ns) em formato não suportado foram ignoradas.");
        return written;
    }

    private static BitmapSource? ToBitmap(PdfiumRenderer.EmbeddedImage image)
    {
        PixelFormat format;
        byte[] pixels = image.Pixels!;
        switch (image.Format)
        {
            case 1:
                format = PixelFormats.Gray8;
                break;
            case 2:
                format = PixelFormats.Bgr24;
                break;
            case 3:
                format = PixelFormats.Bgr32;
                break;
            case 4:
                format = PixelFormats.Bgra32;
                break;
            default:
                return null;
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, format, null, pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static int ExtractAttachments(PdfDocument document, string folder, List<string> notes)
    {
        var files = new List<(string Name, byte[] Data)>();
        var names = document.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles");
        if (names is not null) CollectNameTree(names, files, 0);

        // Anexos presos a comentários (ícone de clipe na página).
        foreach (var page in document.Pages)
        {
            var annotations = page.Elements.GetArray("/Annots");
            if (annotations is null) continue;
            foreach (var item in annotations.Elements)
            {
                var annotation = (item as PdfReference)?.Value as PdfDictionary ?? item as PdfDictionary;
                if (annotation is null || annotation.Elements.GetName("/Subtype") != "/FileAttachment") continue;
                if (annotation.Elements.GetDictionary("/FS") is { } spec && FileFromSpec(spec) is { } file) files.Add(file);
            }
        }

        var written = 0;
        foreach (var (name, data) in files)
        {
            Directory.CreateDirectory(folder);
            var target = FreeFile(Path.Combine(folder, SafeName(name)));
            File.WriteAllBytes(target, data);
            written++;
        }

        if (written == 0) notes.Add("O documento não tem anexos.");
        else notes.Add("Anexos podem conter programas ou macros: abra somente os que você reconhece.");
        return written;
    }

    private static void CollectNameTree(PdfDictionary node, List<(string, byte[])> files, int depth)
    {
        if (depth > 20) return;
        if (node.Elements.GetArray("/Names") is { } pairs)
        {
            for (var i = 0; i + 1 < pairs.Elements.Count; i += 2)
            {
                var spec = (pairs.Elements[i + 1] as PdfReference)?.Value as PdfDictionary ?? pairs.Elements[i + 1] as PdfDictionary;
                var fallback = (pairs.Elements[i] as PdfString)?.Value;
                if (spec is not null && FileFromSpec(spec, fallback) is { } file) files.Add(file);
            }
        }

        if (node.Elements.GetArray("/Kids") is { } kids)
        {
            foreach (var kid in kids.Elements)
            {
                if (((kid as PdfReference)?.Value ?? kid) is PdfDictionary child) CollectNameTree(child, files, depth + 1);
            }
        }
    }

    private static (string, byte[])? FileFromSpec(PdfDictionary spec, string? fallback = null)
    {
        var name = spec.Elements.GetString("/UF");
        if (string.IsNullOrWhiteSpace(name)) name = spec.Elements.GetString("/F");
        if (string.IsNullOrWhiteSpace(name)) name = fallback ?? "anexo";
        var embedded = spec.Elements.GetDictionary("/EF");
        var stream = embedded?.Elements.GetDictionary("/UF")?.Stream ?? embedded?.Elements.GetDictionary("/F")?.Stream;
        if (stream is null) return null;
        var data = stream.UnfilteredValue;
        return (name, data);
    }

    private static int ExtractFonts(PdfDocument document, string folder, List<string> notes)
    {
        var seen = new HashSet<PdfObjectID>();
        var written = 0;
        foreach (var obj in document.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary descriptor || descriptor.Elements.GetName("/Type") != "/FontDescriptor") continue;
            foreach (var (key, extension) in new[] { ("/FontFile2", ".ttf"), ("/FontFile", ".pfa"), ("/FontFile3", ".cff") })
            {
                if (descriptor.Elements.GetReference(key) is not { } reference || !seen.Add(reference.ObjectID)) continue;
                if (reference.Value is not PdfDictionary { Stream: { } stream } fontFile) continue;
                var ext = key == "/FontFile3" && fontFile.Elements.GetName("/Subtype") == "/OpenType" ? ".otf" : extension;
                var name = descriptor.Elements.GetName("/FontName").TrimStart('/');
                // Prefixo de subconjunto (ABCDEF+Nome): a fonte contém só os caracteres usados no documento.
                var data = stream.UnfilteredValue;
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(FreeFile(Path.Combine(folder, SafeName(name.Length > 0 ? name : "fonte") + ext)), data);
                written++;
            }
        }

        notes.Add(written == 0
            ? "Nenhuma fonte incorporada encontrada."
            : "As fontes extraídas continuam sujeitas às licenças de seus autores; fontes com prefixo \"ABCDEF+\" são subconjuntos (só os caracteres usados).");
        return written;
    }

    private static bool WriteMetadata(PdfDocument document, string folder)
    {
        var info = document.Info;
        var builder = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) builder.Append(label).Append(": ").AppendLine(value);
        }

        Line("Título", info.Title);
        Line("Autor", info.Author);
        Line("Assunto", info.Subject);
        Line("Palavras-chave", info.Keywords);
        Line("Aplicativo de criação", info.Creator);
        Line("Produtor do PDF", info.Producer);
        if (info.Elements.ContainsKey("/CreationDate")) Line("Criado em", info.CreationDate.ToString("dd/MM/yyyy HH:mm:ss"));
        if (info.Elements.ContainsKey("/ModDate")) Line("Modificado em", info.ModificationDate.ToString("dd/MM/yyyy HH:mm:ss"));
        builder.Append("Páginas: ").AppendLine(document.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("Versão do PDF: ").AppendLine((document.Version / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(folder, "metadados.txt"), builder.ToString(), new UTF8Encoding(true));

        if (document.Internals.Catalog.Elements.GetDictionary("/Metadata") is { Stream: { } xmp })
        {
            var data = xmp.UnfilteredValue;
            File.WriteAllBytes(Path.Combine(folder, "metadados-xmp.xml"), data);
        }

        return true;
    }

    /// <summary>Nome de arquivo seguro: sem pastas, sem caracteres inválidos e sem nomes reservados do Windows.</summary>
    public static string SafeName(string name)
    {
        var file = name.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        file = new string(file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (file.Length == 0 || file is "." or "..") file = "anexo";
        var stem = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
            file = "_" + file;
        return file.Length > 150 ? file[..150] : file;
    }

    private static string FreeFile(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string FreeFolder(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (var i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }
}
