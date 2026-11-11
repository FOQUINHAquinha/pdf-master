using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFMaster.Domain;

namespace PDFMaster.PdfEngine;

/// <summary>Implementação de <see cref="IPdfEngine"/> com a biblioteca PDFsharp (licença MIT).</summary>
public sealed class PdfSharpEngine : IPdfEngine
{
    public Task<PdfDocumentInfo> ReadInfoAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var document = Open(path);

        var pages = new List<PdfPageInfo>(document.PageCount);
        for (var i = 0; i < document.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = document.Pages[i];
            var box = page.MediaBox;
            pages.Add(new PdfPageInfo(Math.Abs(box.Width), Math.Abs(box.Height), Normalize(page.Rotate)));
        }

        var bookmarks = new List<PdfBookmark>();
        try
        {
            var pageNumbers = new Dictionary<int, int>();
            for (var i = 0; i < document.PageCount; i++)
            {
                var reference = document.Pages[i].Reference;
                if (reference is not null) pageNumbers[reference.ObjectNumber] = i;
            }

            CollectBookmarks(document, document.Outlines, 0, pageNumbers, bookmarks);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or NullReferenceException or ArgumentException)
        {
            // Marcadores malformados não impedem o uso do documento.
            bookmarks.Clear();
        }

        var title = document.Info.Title;
        return new PdfDocumentInfo(
            Path.GetFullPath(path),
            new FileInfo(path).Length,
            pages,
            bookmarks,
            string.IsNullOrWhiteSpace(title) ? null : title);
    }, ct);

    public Task<long> ComposeAsync(
        IReadOnlyList<PageInstruction> pages,
        string outputPath,
        ComposeOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) => Task.Run(() =>
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var overwrite = options?.Overwrite ?? false;

        foreach (var instruction in pages)
        {
            if (!instruction.IsBlank &&
                string.Equals(Path.GetFullPath(instruction.SourcePath!), fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdfEngineException("O arquivo de saída não pode ser um dos arquivos de origem. Os originais nunca são alterados.");
            }
        }

        if (!overwrite && File.Exists(fullOutput))
            throw new PdfEngineException($"Já existe um arquivo em \"{fullOutput}\".");

        var folder = Path.GetDirectoryName(fullOutput)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".pdfmaster-tmp");

        try
        {
            using (var sources = new SourceCache())
            using (var output = Build(pages, options, sources, progress, ct))
            {
                ct.ThrowIfCancellationRequested();
                output.Save(temp);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, fullOutput, overwrite);
            progress?.Report(1);
            return new FileInfo(fullOutput).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException("Não foi possível gravar o arquivo de saída: " + ex.Message, ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // O temporário fica para trás apenas se o sistema o mantiver bloqueado.
            }
        }
    }, ct);

    public Task<long> MeasureAsync(IReadOnlyList<PageInstruction> pages, CancellationToken ct = default) => Task.Run(() =>
    {
        using var sources = new SourceCache();
        using var output = Build(pages, null, sources, null, ct);
        using var stream = new MemoryStream();
        output.Save(stream, closeStream: false);
        return stream.Length;
    }, ct);

    private static PdfDocument Build(
        IReadOnlyList<PageInstruction> pages,
        ComposeOptions? options,
        SourceCache sources,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (pages.Count == 0) throw new PdfEngineException("Nenhuma página foi selecionada para o documento de saída.");

        var output = new PdfDocument();
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var instruction = pages[i];

                if (instruction.IsBlank)
                {
                    var blank = output.AddPage();
                    blank.MediaBox = new PdfRectangle(
                        new XPoint(0, 0),
                        new XPoint(Math.Max(1, instruction.BlankWidthPt), Math.Max(1, instruction.BlankHeightPt)));
                }
                else
                {
                    var source = sources.Get(instruction.SourcePath!);
                    if (instruction.PageIndex < 0 || instruction.PageIndex >= source.PageCount)
                    {
                        throw new PdfEngineException(
                            $"A página {instruction.PageIndex + 1} não existe em \"{Path.GetFileName(instruction.SourcePath)}\".");
                    }

                    var original = source.Pages[instruction.PageIndex];
                    var originalRotation = Normalize(original.Rotate);
                    var imported = output.AddPage(original);
                    var rotation = Normalize(originalRotation + instruction.Rotation);
                    if (Normalize(imported.Rotate) != rotation) imported.Rotate = rotation;
                }

                // A montagem fica com 90% do progresso; a gravação, com o restante.
                progress?.Report((i + 1) / (double)pages.Count * 0.9);
            }

            if (options?.Bookmarks is { Count: > 0 } bookmarks)
            {
                foreach (var bookmark in bookmarks)
                {
                    if (bookmark.PageIndex >= 0 && bookmark.PageIndex < output.PageCount)
                        output.Outlines.Add(bookmark.Title, output.Pages[bookmark.PageIndex], true);
                }
            }

            if (!string.IsNullOrWhiteSpace(options?.Title)) output.Info.Title = options!.Title!;
            output.Info.Creator = "PDF Master";
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static PdfDocument Open(string path)
    {
        if (!File.Exists(path))
            throw new PdfEngineException($"Arquivo não encontrado: \"{path}\".");

        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException(
                $"Não foi possível ler \"{Path.GetFileName(path)}\". O arquivo pode estar protegido por senha ou danificado. " +
                "Use \"Remover proteção\" (com a senha) ou \"Reparar PDF\" e abra a cópia gerada.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new PdfEngineException(
                $"\"{Path.GetFileName(path)}\" não pôde ser interpretado como PDF; o arquivo pode estar danificado.", ex);
        }
    }

    private static void CollectBookmarks(
        PdfDocument document,
        PdfOutlineCollection outlines,
        int level,
        Dictionary<int, int> pageNumbers,
        List<PdfBookmark> result)
    {
        if (level > 32) return;
        foreach (var outline in outlines)
        {
            var pageIndex = ResolvePage(document, outline, pageNumbers);
            if (pageIndex is not null && !string.IsNullOrWhiteSpace(outline.Title))
                result.Add(new PdfBookmark(outline.Title.Trim(), pageIndex.Value, level));
            if (outline.HasChildren) CollectBookmarks(document, outline.Outlines, level + 1, pageNumbers, result);
        }
    }

    /// <summary>
    /// Descobre a página de destino de um marcador. Destinos nomeados não são resolvidos
    /// (o marcador é ignorado).
    /// </summary>
    private static int? ResolvePage(PdfDocument document, PdfOutline outline, Dictionary<int, int> pageNumbers)
    {
        if (outline.DestinationPage is { } destinationPage)
        {
            for (var i = 0; i < document.PageCount; i++)
            {
                if (ReferenceEquals(document.Pages[i], destinationPage)) return i;
            }
        }

        var destination = Dereference(outline.Elements["/Dest"]);
        if (destination is null && Dereference(outline.Elements["/A"]) is PdfDictionary action &&
            action.Elements.GetName("/S") == "/GoTo")
        {
            destination = Dereference(action.Elements["/D"]);
        }

        if (destination is PdfArray { Elements.Count: > 0 } array && array.Elements[0] is PdfReference pageReference &&
            pageNumbers.TryGetValue(pageReference.ObjectNumber, out var index))
        {
            return index;
        }

        return null;
    }

    private static PdfItem? Dereference(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>Mantém cada arquivo de origem aberto uma única vez durante a montagem.</summary>
    private sealed class SourceCache : IDisposable
    {
        private readonly Dictionary<string, PdfDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

        public PdfDocument Get(string path)
        {
            var full = Path.GetFullPath(path);
            if (!_documents.TryGetValue(full, out var document))
            {
                document = Open(full);
                _documents[full] = document;
            }

            return document;
        }

        public void Dispose()
        {
            foreach (var document in _documents.Values) document.Dispose();
            _documents.Clear();
        }
    }
}
