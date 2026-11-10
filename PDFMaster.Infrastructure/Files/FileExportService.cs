using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PDFMaster.Core.Files;
using PDFMaster.Domain;

namespace PDFMaster.Infrastructure.Files;

public sealed record ExportItemResult(
    string SourcePath,
    string? OutputPath,
    OperationStatus Status,
    string Message,
    long Bytes,
    DateTime StartedUtc,
    TimeSpan Duration);

public sealed record ExportProgress(int Completed, int Total, string CurrentFile);

/// <summary>
/// Copia arquivos para uma pasta de destino aplicando a política de nomes e de sobrescrita.
/// Os originais nunca são alterados.
/// </summary>
public sealed class FileExportService
{
    public const string OperationName = "Copiar arquivos";

    private readonly ILogger<FileExportService> _logger;
    private readonly OutputPathResolver _resolver;

    public FileExportService(ILogger<FileExportService> logger, OutputPathResolver resolver)
    {
        _logger = logger;
        _resolver = resolver;
    }

    public async Task<IReadOnlyList<ExportItemResult>> CopyAsync(
        IReadOnlyList<string> sources,
        OutputOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<ExportItemResult>(sources.Count);
        var reserved = OutputPathResolver.CreateReservationSet();
        var now = DateTime.Now;

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var started = DateTime.UtcNow;
            var watch = Stopwatch.StartNew();

            if (ct.IsCancellationRequested)
            {
                results.Add(new ExportItemResult(source, null, OperationStatus.Cancelled,
                    "Cancelado antes de iniciar.", 0, started, TimeSpan.Zero));
                continue;
            }

            progress?.Report(new ExportProgress(i, sources.Count, Path.GetFileName(source)));
            string? tempPath = null;
            try
            {
                var sourceFull = Path.GetFullPath(source);
                var decision = _resolver.Resolve(options, Path.GetFileName(sourceFull), now, reserved);

                if (string.Equals(decision.FullPath, sourceFull, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new ExportItemResult(source, null, OperationStatus.Skipped,
                        "Origem e destino são o mesmo arquivo; nada foi copiado.", 0, started, watch.Elapsed));
                    continue;
                }

                if (decision.Skip)
                {
                    results.Add(new ExportItemResult(source, decision.FullPath, OperationStatus.Skipped,
                        "Já existe um arquivo com este nome no destino; mantido o existente.", 0, started, watch.Elapsed));
                    continue;
                }

                var folder = Path.GetDirectoryName(decision.FullPath)!;
                Directory.CreateDirectory(folder);
                tempPath = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".pdfmaster-tmp");

                long bytes;
                await using (var input = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 81920, FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
                    bytes = output.Length;
                }

                File.Move(tempPath, decision.FullPath, overwrite: decision.Overwrites);
                tempPath = null;

                results.Add(new ExportItemResult(source, decision.FullPath, OperationStatus.Completed,
                    decision.Overwrites ? "Copiado, substituindo o arquivo existente." : "Copiado.",
                    bytes, started, watch.Elapsed));
            }
            catch (OperationCanceledException)
            {
                results.Add(new ExportItemResult(source, null, OperationStatus.Cancelled,
                    "Cancelado durante a cópia.", 0, started, watch.Elapsed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Falha ao copiar {Source}", source);
                results.Add(new ExportItemResult(source, null, OperationStatus.Failed, ex.Message, 0, started, watch.Elapsed));
            }
            finally
            {
                if (tempPath is not null) TryDelete(tempPath);
            }
        }

        progress?.Report(new ExportProgress(sources.Count, sources.Count, string.Empty));
        return results;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Não foi possível remover o temporário {Path}", path);
        }
    }
}
