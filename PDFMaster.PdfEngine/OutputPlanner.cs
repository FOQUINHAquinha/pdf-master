using PDFMaster.Core.Files;
using PDFMaster.Domain;

namespace PDFMaster.PdfEngine;

/// <summary>
/// Decide o caminho de cada arquivo de saída de uma operação: aplica a política de sobrescrita,
/// as subpastas e garante que um arquivo de origem nunca seja usado como destino.
/// </summary>
public sealed class OutputPlanner
{
    private readonly OutputPathResolver _resolver;

    public OutputPlanner(OutputPathResolver resolver) => _resolver = resolver;

    /// <param name="sources">Arquivos de entrada da operação; nunca são escolhidos como saída.</param>
    /// <param name="reserved">Nomes já atribuídos no mesmo lote.</param>
    public (string Path, bool Overwrite) Resolve(
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
            var collides = sources.Any(s => string.Equals(Path.GetFullPath(s), decision.FullPath, StringComparison.OrdinalIgnoreCase));
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
                $"Já existe \"{Path.GetFileName(decision.FullPath)}\" na pasta de destino e a configuração atual mantém o arquivo existente.");
        }

        return (decision.FullPath, decision.Overwrites);
    }

    /// <summary>
    /// Grava um arquivo de forma que ele só apareça no destino quando estiver completo: escreve em
    /// um temporário na mesma pasta e renomeia. Em caso de erro ou cancelamento, nada fica para trás.
    /// </summary>
    public static long WriteAtomically(string outputPath, bool overwrite, Action<string> writeTo)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(folder);
        string? temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".pdfmaster-tmp");
        try
        {
            writeTo(temp);
            var bytes = new FileInfo(temp).Length;
            File.Move(temp, outputPath, overwrite);
            temp = null;
            return bytes;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // O temporário fica para trás; o erro original é o que importa.
                }
            }
        }
    }
}
