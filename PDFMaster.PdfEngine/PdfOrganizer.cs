using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;

namespace PDFMaster.PdfEngine;

/// <summary>Onde e como gravar os resultados de uma operação.</summary>
public sealed record OutputTarget(string Folder, OverwritePolicy Policy, SubfolderMode Subfolders);

public sealed record ProducedFile(string Path, long Bytes, int PageCount);

/// <param name="RangeText">Páginas a usar ("1-3,7"); vazio = todas.</param>
public sealed record MergeInput(string Path, string? RangeText = null);

public sealed record InterleaveInput(string Path, bool Reverse = false);

/// <summary>
/// Operações de organização de PDF montadas sobre o <see cref="IPdfEngine"/>: mesclar, intercalar,
/// dividir e salvar um novo arranjo de páginas. Nenhuma delas altera os arquivos de origem.
/// </summary>
public sealed class PdfOrganizer
{
    private readonly IPdfEngine _engine;
    private readonly OutputPathResolver _resolver;

    public PdfOrganizer(IPdfEngine engine, OutputPathResolver resolver)
    {
        _engine = engine;
        _resolver = resolver;
    }

    public async Task<ProducedFile> MergeAsync(
        IReadOnlyList<MergeInput> inputs,
        string fileName,
        OutputTarget target,
        bool bookmarkPerFile,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (inputs.Count == 0) throw new PdfEngineException("Adicione pelo menos um arquivo para mesclar.");

        var pages = new List<PageInstruction>();
        var bookmarks = new List<OutputBookmark>();
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var info = await _engine.ReadInfoAsync(input.Path, ct).ConfigureAwait(false);
            IReadOnlyList<int> selected;
            try
            {
                selected = PageRangeParser.Parse(input.RangeText, info.PageCount);
            }
            catch (PageRangeException ex)
            {
                throw new PdfEngineException($"{System.IO.Path.GetFileName(input.Path)}: {ex.Message}", ex);
            }

            if (selected.Count == 0) continue;
            bookmarks.Add(new OutputBookmark(System.IO.Path.GetFileNameWithoutExtension(input.Path), pages.Count));
            pages.AddRange(selected.Select(p => PageInstruction.From(input.Path, p)));
        }

        return await SaveArrangementAsync(pages, fileName, target, "Mesclar PDF",
            bookmarkPerFile ? bookmarks : null, progress, ct).ConfigureAwait(false);
    }

    public async Task<ProducedFile> InterleaveAsync(
        IReadOnlyList<InterleaveInput> inputs,
        int pagesPerTurn,
        InterleaveRemainder remainder,
        string fileName,
        OutputTarget target,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var sources = new List<InterleaveSource>();
        foreach (var input in inputs)
        {
            var info = await _engine.ReadInfoAsync(input.Path, ct).ConfigureAwait(false);
            sources.Add(new InterleaveSource(info.PageCount, input.Reverse));
        }

        List<PageInstruction> pages;
        try
        {
            pages = InterleavePlanner.Plan(sources, pagesPerTurn, remainder)
                .Select(p => PageInstruction.From(inputs[p.DocumentIndex].Path, p.PageIndex))
                .ToList();
        }
        catch (PageRangeException ex)
        {
            throw new PdfEngineException(ex.Message, ex);
        }

        return await SaveArrangementAsync(pages, fileName, target, "Intercalar documentos", null, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Grava um arquivo para cada grupo de páginas.</summary>
    public async Task<IReadOnlyList<ProducedFile>> SplitAsync(
        string path,
        IReadOnlyList<IReadOnlyList<int>> groups,
        string? namePattern,
        OutputTarget target,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (groups.Count == 0) throw new PdfEngineException("A divisão não gerou nenhum arquivo.");

        var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
        var reserved = OutputPathResolver.CreateReservationSet();
        var results = new List<ProducedFile>(groups.Count);

        for (var i = 0; i < groups.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[i];
            if (group.Count == 0) continue;

            var name = SplitNameFormatter.Format(namePattern, baseName, i + 1, groups.Count, group.Min() + 1, group.Max() + 1);
            var pages = group.Select(p => PageInstruction.From(path, p)).ToList();
            var (output, overwrite) = ResolveOutput(name, target, "Dividir PDF", [path], reserved);
            var bytes = await _engine.ComposeAsync(pages, output, new ComposeOptions(Overwrite: overwrite), null, ct)
                .ConfigureAwait(false);
            results.Add(new ProducedFile(output, bytes, pages.Count));
            progress?.Report((i + 1) / (double)groups.Count);
        }

        return results;
    }

    /// <summary>
    /// Agrupa as páginas de modo que cada arquivo fique com, no máximo, o tamanho pedido.
    /// O tamanho é medido de verdade (não estimado); uma página sozinha maior que o limite
    /// vira um arquivo próprio.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<int>>> PlanSplitBySizeAsync(
        string path,
        long maxBytes,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (maxBytes < 1024) throw new PdfEngineException("O tamanho por arquivo deve ser de pelo menos 1 KB.");

        var info = await _engine.ReadInfoAsync(path, ct).ConfigureAwait(false);
        var groups = new List<IReadOnlyList<int>>();
        var start = 0;

        while (start < info.PageCount)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = info.PageCount - start;

            // Busca binária pela maior quantidade de páginas que cabe no limite.
            int low = 1, high = remaining, best = 1;
            while (low <= high)
            {
                ct.ThrowIfCancellationRequested();
                var middle = low + (high - low) / 2;
                var candidate = Enumerable.Range(start, middle).Select(p => PageInstruction.From(path, p)).ToList();
                var size = await _engine.MeasureAsync(candidate, ct).ConfigureAwait(false);
                if (size <= maxBytes)
                {
                    best = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            groups.Add(Enumerable.Range(start, best).ToList());
            start += best;
            progress?.Report(start / (double)info.PageCount);
        }

        return groups;
    }

    /// <summary>Grava um novo PDF com exatamente as páginas indicadas, na ordem indicada.</summary>
    public async Task<ProducedFile> SaveArrangementAsync(
        IReadOnlyList<PageInstruction> pages,
        string fileName,
        OutputTarget target,
        string operationName,
        IReadOnlyList<OutputBookmark>? bookmarks = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (pages.Count == 0) throw new PdfEngineException("Nenhuma página foi selecionada.");

        var name = string.IsNullOrWhiteSpace(fileName) ? "documento.pdf" : fileName.Trim();
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";

        var sources = pages.Where(p => !p.IsBlank).Select(p => p.SourcePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (output, overwrite) = ResolveOutput(name, target, operationName, sources, null);
        var bytes = await _engine.ComposeAsync(pages, output, new ComposeOptions(bookmarks, Overwrite: overwrite), progress, ct)
            .ConfigureAwait(false);
        return new ProducedFile(output, bytes, pages.Count);
    }

    private (string Path, bool Overwrite) ResolveOutput(
        string fileName,
        OutputTarget target,
        string operationName,
        IReadOnlyList<string> sources,
        ISet<string>? reserved)
    {
        if (string.IsNullOrWhiteSpace(target.Folder))
            throw new PdfEngineException("Escolha a pasta de destino.");

        OutputDecision decision;
        try
        {
            var options = new OutputOptions(target.Folder, target.Policy, target.Subfolders, operationName);
            decision = _resolver.Resolve(options, fileName, DateTime.Now, reserved);

            // Um arquivo de origem nunca é o destino, nem com a política "substituir".
            var collides = sources.Any(s => string.Equals(System.IO.Path.GetFullPath(s), decision.FullPath, StringComparison.OrdinalIgnoreCase));
            if (collides)
            {
                var safe = options with { Policy = OverwritePolicy.AutoRename };
                var taken = reserved ?? OutputPathResolver.CreateReservationSet();
                taken.Add(decision.FullPath);
                decision = _resolver.Resolve(safe, fileName, DateTime.Now, taken);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            throw new PdfEngineException("Pasta ou nome de saída inválido: " + ex.Message, ex);
        }

        if (decision.Skip)
        {
            throw new PdfEngineException(
                $"Já existe \"{System.IO.Path.GetFileName(decision.FullPath)}\" na pasta de destino e a configuração atual mantém o arquivo existente.");
        }

        return (decision.FullPath, decision.Overwrites);
    }
}
