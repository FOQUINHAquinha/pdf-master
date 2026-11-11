using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Editor;

/// <summary>Link existente numa página: endereço da web ou página de destino dentro do documento.</summary>
public sealed record PdfLinkInfo(int PageIndex, int AnnotationIndex, string? Url, int? TargetPage)
{
    public string Description => Url is not null ? Url : TargetPage is { } page ? $"Ir para a página {page + 1}" : "Ação não reconhecida";
}

/// <summary>Arquivo anexado ao PDF.</summary>
public sealed record PdfAttachmentInfo(string Name, long Bytes, string? Description);

public sealed record NavigationInfo(int PageCount, IReadOnlyList<PdfBookmark> Bookmarks, IReadOnlyList<PdfLinkInfo> Links, IReadOnlyList<PdfAttachmentInfo> Attachments);

/// <summary>Alterações a gravar numa cópia do documento.</summary>
/// <param name="Bookmarks">Marcadores na ordem e no nível desejados (substituem os atuais); null mantém os atuais.</param>
/// <param name="RemoveLinks">Links a remover (página e posição na lista de comentários da página).</param>
/// <param name="LinkUrlsInText">Cria links clicáveis para os endereços (http://, https://, www.) escritos no texto.</param>
/// <param name="AddAttachments">Arquivos a anexar.</param>
/// <param name="RemoveAttachments">Nomes dos anexos a remover.</param>
/// <param name="TableOfContents">Insere no início uma página de sumário montada a partir dos marcadores, com links.</param>
public sealed record NavigationChanges(
    IReadOnlyList<PdfBookmark>? Bookmarks,
    IReadOnlyList<(int PageIndex, int AnnotationIndex)> RemoveLinks,
    bool LinkUrlsInText,
    IReadOnlyList<string> AddAttachments,
    IReadOnlyList<string> RemoveAttachments,
    bool TableOfContents);

public sealed record NavigationResult(ProducedFile File, int Bookmarks, int LinksCreated, int LinksRemoved, int AttachmentsAdded, int AttachmentsRemoved, bool TableOfContents);

/// <summary>
/// Marcadores, links e anexos: lê o que o documento tem e grava uma cópia com as alterações.
/// O original nunca é alterado.
/// </summary>
public sealed partial class DocumentNavigation
{
    public const string OperationName = "Marcadores, links e anexos";

    private readonly IPdfEngine _engine;
    private readonly OutputPlanner _planner;

    public DocumentNavigation(IPdfEngine engine, OutputPlanner planner)
    {
        _engine = engine;
        _planner = planner;
    }

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""'()\[\]{}]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    public async Task<NavigationInfo> ReadAsync(string path, CancellationToken ct = default)
    {
        var info = await _engine.ReadInfoAsync(path, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var document = Open(path, PdfDocumentOpenMode.Import);
            var links = new List<PdfLinkInfo>();
            var pageNumbers = PageNumbers(document);
            for (var p = 0; p < document.PageCount; p++)
            {
                var annotations = document.Pages[p].Elements.GetArray("/Annots");
                if (annotations is null) continue;
                for (var i = 0; i < annotations.Elements.Count; i++)
                {
                    if (Resolve(annotations.Elements[i]) is not PdfDictionary annotation || annotation.Elements.GetName("/Subtype") != "/Link") continue;
                    links.Add(Describe(annotation, p, i, pageNumbers));
                }
            }

            var attachments = EmbeddedFiles(document).Select(f => new PdfAttachmentInfo(f.Name, f.Data.Length, f.Description)).ToList();
            return new NavigationInfo(info.PageCount, info.Bookmarks, links, attachments);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Conteúdo de um anexo, pelo nome.</summary>
    public static byte[] ReadAttachment(string path, string name)
    {
        using var document = Open(path, PdfDocumentOpenMode.Import);
        return EmbeddedFiles(document).FirstOrDefault(f => f.Name == name)?.Data ?? throw new PdfEngineException($"Anexo não encontrado: {name}");
    }

    public Task<NavigationResult> SaveAsync(string path, NavigationChanges changes, OutputTarget target, CancellationToken ct = default,
        ISet<string>? reserved = null) => Task.Run(() =>
    {
        var full = Path.GetFullPath(path);
        foreach (var file in changes.AddAttachments)
            if (!File.Exists(file)) throw new PdfEngineException($"Arquivo para anexar não encontrado: {file}");
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + "_navegacao.pdf", target, OperationName, [full], reserved);

        // Posições do texto (para os links automáticos) vêm do PDFium, no espaço da página exibida.
        var text = changes.LinkUrlsInText ? PdfiumRenderer.ReadDisplayText(full, ct) : null;

        using var document = Open(full, PdfDocumentOpenMode.Modify);
        int linksRemoved = 0, linksCreated = 0, attachmentsRemoved = 0;

        // Links removidos (do fim para o começo, para não deslocar os índices).
        foreach (var group in changes.RemoveLinks.GroupBy(l => l.PageIndex))
        {
            if (group.Key < 0 || group.Key >= document.PageCount) continue;
            var annotations = document.Pages[group.Key].Elements.GetArray("/Annots");
            if (annotations is null) continue;
            foreach (var index in group.Select(l => l.AnnotationIndex).Distinct().OrderDescending())
            {
                if (index < annotations.Elements.Count && Resolve(annotations.Elements[index]) is PdfDictionary { } a && a.Elements.GetName("/Subtype") == "/Link")
                {
                    annotations.Elements.RemoveAt(index);
                    linksRemoved++;
                }
            }
        }

        if (text is not null)
        {
            for (var p = 0; p < document.PageCount && p < text.Count; p++)
            {
                ct.ThrowIfCancellationRequested();
                linksCreated += LinkUrls(document.Pages[p], text[p]);
            }
        }

        // Anexos.
        var files = EmbeddedFiles(document).Where(f => !changes.RemoveAttachments.Contains(f.Name)).Select(f => (f.Name, f.Spec)).ToList();
        attachmentsRemoved = EmbeddedFiles(document).Count - files.Count;
        foreach (var file in changes.AddAttachments)
        {
            var name = UniqueName(Path.GetFileName(file), files.Select(f => f.Name));
            files.Add((name, FileSpec(document, name, File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file))));
        }

        if (attachmentsRemoved > 0 || changes.AddAttachments.Count > 0) WriteEmbeddedFiles(document, files);

        // Sumário (antes dos marcadores: as páginas mudam de posição).
        var bookmarks = changes.Bookmarks ?? CurrentBookmarks(document);
        var offset = 0;
        if (changes.TableOfContents)
        {
            if (bookmarks.Count == 0) throw new PdfEngineException("Crie marcadores antes de gerar o sumário: ele é montado a partir deles.");
            offset = InsertTableOfContents(document, bookmarks);
        }

        if (changes.Bookmarks is not null || offset > 0)
        {
            var shifted = bookmarks.Select(b => b with { PageIndex = b.PageIndex + offset }).ToList();
            if (offset > 0) shifted.Insert(0, new PdfBookmark("Sumário", 0, 0));
            WriteBookmarks(document, shifted);
            bookmarks = shifted;
        }

        if (bookmarks.Count > 0) document.PageMode = PdfPageMode.UseOutlines;
        var pageCount = document.PageCount;
        var bytes = OutputPlanner.WriteAtomically(output, overwrite, document.Save);
        return new NavigationResult(new ProducedFile(output, bytes, pageCount), bookmarks.Count, linksCreated, linksRemoved, changes.AddAttachments.Count,
            attachmentsRemoved, offset > 0);
    }, ct);

    // ---------------- Marcadores ----------------

    /// <summary>Confere a estrutura: o primeiro marcador no nível 0 e cada um no máximo um nível abaixo do anterior.</summary>
    public static string? Validate(IReadOnlyList<PdfBookmark> bookmarks, int pageCount)
    {
        for (var i = 0; i < bookmarks.Count; i++)
        {
            var b = bookmarks[i];
            if (string.IsNullOrWhiteSpace(b.Title)) return $"O marcador {i + 1} está sem título.";
            if (b.PageIndex < 0 || b.PageIndex >= pageCount) return $"\"{b.Title}\": a página deve ficar entre 1 e {pageCount}.";
            var max = i == 0 ? 0 : bookmarks[i - 1].Level + 1;
            if (b.Level < 0 || b.Level > max) return $"\"{b.Title}\": nível inválido (um marcador só pode ficar um nível abaixo do anterior).";
        }

        return null;
    }

    private static void WriteBookmarks(PdfDocument document, IReadOnlyList<PdfBookmark> bookmarks)
    {
        if (Validate(bookmarks, document.PageCount) is { } problem) throw new PdfEngineException(problem);
        document.Outlines.Clear();
        var parents = new List<PdfOutlineCollection> { document.Outlines };
        foreach (var bookmark in bookmarks)
        {
            parents.RemoveRange(bookmark.Level + 1, parents.Count - bookmark.Level - 1);
            var outline = parents[bookmark.Level].Add(bookmark.Title.Trim(), document.Pages[bookmark.PageIndex], true);
            parents.Add(outline.Outlines);
        }
    }

    private static List<PdfBookmark> CurrentBookmarks(PdfDocument document)
    {
        var result = new List<PdfBookmark>();
        var numbers = PageNumbers(document);
        void Walk(PdfOutlineCollection outlines, int level)
        {
            if (level > 32) return;
            foreach (var outline in outlines)
            {
                if (outline.DestinationPage?.Reference is { } reference && numbers.TryGetValue(reference.ObjectNumber, out var page) &&
                    !string.IsNullOrWhiteSpace(outline.Title))
                    result.Add(new PdfBookmark(outline.Title.Trim(), page, level));
                if (outline.HasChildren) Walk(outline.Outlines, level + 1);
            }
        }

        Walk(document.Outlines, 0);
        return result;
    }

    /// <summary>Insere páginas de sumário no início; devolve quantas páginas foram inseridas.</summary>
    private static int InsertTableOfContents(PdfDocument document, IReadOnlyList<PdfBookmark> bookmarks)
    {
        const double margin = 56, lineHeight = 18;
        var size = (Width: document.Pages[0].Width.Point, Height: document.Pages[0].Height.Point);
        var perPage = (int)((size.Height - 2 * margin - 40) / lineHeight);
        var pagesNeeded = (bookmarks.Count + perPage - 1) / perPage;
        var title = new XFont("Arial", 18, XFontStyleEx.Bold);
        var font = new XFont("Arial", 11);
        var tocPages = new List<PdfPage>();
        for (var t = 0; t < pagesNeeded; t++)
        {
            var page = document.InsertPage(t);
            page.Width = XUnit.FromPoint(size.Width);
            page.Height = XUnit.FromPoint(size.Height);
            tocPages.Add(page);
        }

        for (var t = 0; t < pagesNeeded; t++)
        {
            var page = tocPages[t];
            using var g = XGraphics.FromPdfPage(page);
            var y = margin;
            if (t == 0)
            {
                g.DrawString("Sumário", title, XBrushes.Black, margin, y + 18);
                y += 40;
            }

            foreach (var bookmark in bookmarks.Skip(t * perPage).Take(perPage))
            {
                var indent = Math.Min(bookmark.Level, 6) * 16;
                var number = (bookmark.PageIndex + pagesNeeded + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var numberWidth = g.MeasureString(number, font).Width;
                var right = size.Width - margin;
                var label = Fit(g, bookmark.Title.Trim(), font, right - margin - indent - numberWidth - 24);
                g.DrawString(label, font, XBrushes.Black, margin + indent, y + 12);
                var labelEnd = margin + indent + g.MeasureString(label, font).Width + 6;
                var dots = right - numberWidth - 6;
                if (dots > labelEnd) g.DrawLine(new XPen(XColors.Gray, 0.6) { DashStyle = XDashStyle.Dot }, labelEnd, y + 11, dots, y + 11);
                g.DrawString(number, font, XBrushes.Black, right - numberWidth, y + 12);

                // Link da linha inteira para a página do marcador (coordenadas do PDF: origem embaixo).
                var rect = new PdfRectangle(new XPoint(margin + indent, size.Height - y - lineHeight + 2), new XPoint(right, size.Height - y + 2));
                page.AddDocumentLink(rect, bookmark.PageIndex + pagesNeeded + 1);
                y += lineHeight;
            }
        }

        return pagesNeeded;
    }

    private static string Fit(XGraphics g, string text, XFont font, double width)
    {
        if (g.MeasureString(text, font).Width <= width) return text;
        while (text.Length > 1 && g.MeasureString(text + "…", font).Width > width) text = text[..^1];
        return text + "…";
    }

    // ---------------- Links ----------------

    private static PdfLinkInfo Describe(PdfDictionary annotation, int page, int index, Dictionary<int, int> pageNumbers)
    {
        string? url = null;
        int? target = null;
        if (Resolve(annotation.Elements["/A"]) is PdfDictionary action)
        {
            if (action.Elements.GetName("/S") == "/URI") url = action.Elements.GetString("/URI");
            else if (action.Elements.GetName("/S") == "/GoTo") target = DestinationPage(action.Elements["/D"], pageNumbers);
        }
        else if (annotation.Elements["/Dest"] is { } destination)
        {
            target = DestinationPage(destination, pageNumbers);
        }

        return new PdfLinkInfo(page, index, url, target);
    }

    private static int? DestinationPage(PdfItem? destination, Dictionary<int, int> pageNumbers) =>
        Resolve(destination) is PdfArray { Elements.Count: > 0 } array && array.Elements[0] is PdfReference reference &&
        pageNumbers.TryGetValue(reference.ObjectNumber, out var page)
            ? page
            : null;

    /// <summary>Cria links sobre os endereços escritos no texto da página que ainda não têm link.</summary>
    private static int LinkUrls(PdfPage page, DisplayPageText text)
    {
        if (text.Chars.Count == 0) return 0;
        var content = new string(text.Chars.Select(c => c.Value).ToArray());
        var frame = PageFrame.Of(page);
        var existing = ExistingLinkRects(page);
        var created = 0;
        foreach (Match match in UrlPattern().Matches(content))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            var boxes = text.Chars.Skip(match.Index).Take(url.Length).Where(c => c.Width > 0).ToList();
            if (boxes.Count == 0) continue;
            var view = new ViewRect(boxes.Min(b => b.X), boxes.Min(b => b.Y), boxes.Max(b => b.X + b.Width) - boxes.Min(b => b.X),
                boxes.Max(b => b.Y + b.Height) - boxes.Min(b => b.Y));
            var r = frame.PdfRect(view);
            if (existing.Any(e => Overlaps(e, r))) continue;
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            page.AddWebLink(new PdfRectangle(new XPoint(r[0], r[1]), new XPoint(r[2], r[3])), href);
            existing.Add(r);
            created++;
        }

        return created;
    }

    private static List<double[]> ExistingLinkRects(PdfPage page)
    {
        var result = new List<double[]>();
        if (page.Elements.GetArray("/Annots") is not { } annotations) return result;
        foreach (var item in annotations.Elements)
        {
            if (Resolve(item) is PdfDictionary a && a.Elements.GetName("/Subtype") == "/Link" && a.Elements.GetArray("/Rect") is { Elements.Count: 4 } rect)
            {
                var v = rect.Elements.Select(e => Resolve(e) switch
                {
                    PdfReal real => real.Value,
                    PdfInteger integer => integer.Value,
                    _ => 0,
                }).ToArray();
                result.Add([Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3])]);
            }
        }

        return result;
    }

    private static bool Overlaps(double[] a, double[] b) => a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];

    // ---------------- Anexos ----------------

    private sealed record Embedded(string Name, byte[] Data, string? Description, PdfItem Spec);

    private static List<Embedded> EmbeddedFiles(PdfDocument document)
    {
        var result = new List<Embedded>();
        var tree = document.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles");
        if (tree is not null) Collect(tree, result, 0);
        return result;
    }

    private static void Collect(PdfDictionary node, List<Embedded> result, int depth)
    {
        if (depth > 20) return;
        if (node.Elements.GetArray("/Names") is { } pairs)
        {
            for (var i = 0; i + 1 < pairs.Elements.Count; i += 2)
            {
                var key = (Resolve(pairs.Elements[i]) as PdfString)?.Value ?? "anexo";
                var specItem = pairs.Elements[i + 1];
                if (Resolve(specItem) is not PdfDictionary spec) continue;
                var name = spec.Elements.GetString("/UF");
                if (string.IsNullOrWhiteSpace(name)) name = spec.Elements.GetString("/F");
                if (string.IsNullOrWhiteSpace(name)) name = key;
                var ef = spec.Elements.GetDictionary("/EF");
                var stream = ef?.Elements.GetDictionary("/UF")?.Stream ?? ef?.Elements.GetDictionary("/F")?.Stream;
                var description = spec.Elements.GetString("/Desc");
                result.Add(new Embedded(name, stream?.UnfilteredValue ?? [], string.IsNullOrWhiteSpace(description) ? null : description, specItem));
            }
        }

        if (node.Elements.GetArray("/Kids") is { } kids)
        {
            foreach (var kid in kids.Elements)
                if (Resolve(kid) is PdfDictionary child) Collect(child, result, depth + 1);
        }
    }

    private static PdfItem FileSpec(PdfDocument document, string name, byte[] data, DateTime modifiedUtc)
    {
        var stream = new PdfDictionary(document);
        stream.Elements.SetName("/Type", "/EmbeddedFile");
        var parameters = new PdfDictionary(document);
        parameters.Elements.SetInteger("/Size", data.Length);
        parameters.Elements.SetDateTime("/ModDate", modifiedUtc.ToLocalTime());
        stream.Elements["/Params"] = parameters;
        stream.CreateStream(data);
        document.Internals.AddObject(stream);
        var ef = new PdfDictionary(document);
        ef.Elements.SetReference("/F", stream);
        ef.Elements.SetReference("/UF", stream);
        var spec = new PdfDictionary(document);
        spec.Elements.SetName("/Type", "/Filespec");
        spec.Elements.SetString("/F", name);
        spec.Elements["/UF"] = new PdfString(name, PdfStringEncoding.Unicode);
        spec.Elements["/EF"] = ef;
        document.Internals.AddObject(spec);
        return spec.Reference!;
    }

    private static void WriteEmbeddedFiles(PdfDocument document, List<(string Name, PdfItem Spec)> files)
    {
        var catalog = document.Internals.Catalog;
        var names = catalog.Elements.GetDictionary("/Names");
        if (files.Count == 0)
        {
            names?.Elements.Remove("/EmbeddedFiles");
            return;
        }

        if (names is null)
        {
            names = new PdfDictionary(document);
            catalog.Elements["/Names"] = names;
        }

        // A árvore de nomes exige as chaves em ordem.
        var pairs = new PdfArray(document);
        foreach (var (name, spec) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            pairs.Elements.Add(new PdfString(name, PdfStringEncoding.Unicode));
            pairs.Elements.Add(spec);
        }

        var tree = new PdfDictionary(document);
        tree.Elements["/Names"] = pairs;
        names.Elements["/EmbeddedFiles"] = tree;
    }

    private static string UniqueName(string name, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ---------------- Comum ----------------

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static Dictionary<int, int> PageNumbers(PdfDocument document)
    {
        var numbers = new Dictionary<int, int>();
        for (var i = 0; i < document.PageCount; i++)
            if (document.Pages[i].Reference is { } reference) numbers[reference.ObjectNumber] = i;
        return numbers;
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        if (!File.Exists(path)) throw new PdfEngineException($"Arquivo não encontrado: \"{path}\".");
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            var reason = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "está protegido por senha (remova a proteção antes, em \"Remover proteção\")"
                : "não pôde ser lido como PDF";
            throw new PdfEngineException($"\"{Path.GetFileName(path)}\" {reason}.", ex);
        }
    }
}
