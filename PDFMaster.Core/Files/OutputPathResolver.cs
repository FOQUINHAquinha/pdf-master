using PDFMaster.Domain;

namespace PDFMaster.Core.Files;

public sealed record OutputOptions(
    string DestinationFolder,
    OverwritePolicy Policy,
    SubfolderMode Subfolders,
    string OperationName);

/// <param name="FullPath">Caminho final do arquivo de saída.</param>
/// <param name="Skip">O arquivo já existe e a política manda não gravar.</param>
/// <param name="Overwrites">O arquivo já existe e será substituído.</param>
public sealed record OutputDecision(string FullPath, bool Skip, bool Overwrites);

/// <summary>
/// Decide onde cada resultado é gravado, garantindo que nada seja sobrescrito por acidente.
/// </summary>
public sealed class OutputPathResolver
{
    private readonly Func<string, bool> _fileExists;

    public OutputPathResolver(Func<string, bool>? fileExists = null)
    {
        _fileExists = fileExists ?? File.Exists;
    }

    public string ResolveFolder(OutputOptions options, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(options.DestinationFolder))
            throw new ArgumentException("A pasta de destino não foi informada.", nameof(options));

        var root = Path.GetFullPath(options.DestinationFolder);
        var folder = options.Subfolders switch
        {
            SubfolderMode.ByDate => Path.Combine(root, nowLocal.ToString("yyyy-MM-dd")),
            SubfolderMode.ByOperation => Path.Combine(root, PathSafety.SanitizeFileName(options.OperationName)),
            _ => root,
        };

        if (options.Subfolders != SubfolderMode.None) PathSafety.EnsureWithin(root, folder);
        return folder;
    }

    /// <param name="reserved">
    /// Caminhos já atribuídos no mesmo lote. Dois arquivos de entrada com o mesmo nome nunca
    /// disputam o mesmo arquivo de saída, qualquer que seja a política.
    /// </param>
    public OutputDecision Resolve(
        OutputOptions options,
        string desiredFileName,
        DateTime nowLocal,
        ISet<string>? reserved = null)
    {
        var folder = ResolveFolder(options, nowLocal);
        var fileName = PathSafety.SanitizeFileName(Path.GetFileName(desiredFileName));
        var candidate = Path.Combine(folder, fileName);
        PathSafety.EnsureWithin(folder, candidate);

        var takenInBatch = reserved is not null && reserved.Contains(candidate);
        var existsOnDisk = _fileExists(candidate);

        if (!takenInBatch && existsOnDisk)
        {
            switch (options.Policy)
            {
                case OverwritePolicy.Skip:
                    return new OutputDecision(candidate, Skip: true, Overwrites: false);
                case OverwritePolicy.Overwrite:
                    reserved?.Add(candidate);
                    return new OutputDecision(candidate, Skip: false, Overwrites: true);
            }
        }

        if (takenInBatch || existsOnDisk)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var i = 2; ; i++)
            {
                candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
                if ((reserved is null || !reserved.Contains(candidate)) && !_fileExists(candidate)) break;
            }
        }

        reserved?.Add(candidate);
        return new OutputDecision(candidate, Skip: false, Overwrites: false);
    }

    public static ISet<string> CreateReservationSet() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
