namespace PDFMaster.Infrastructure;

/// <summary>
/// Arquivos temporários do programa: tudo fica em %TEMP%\PDFMaster (cada operação usa uma subpasta
/// própria e a apaga ao terminar). Sobras de operações interrompidas são removidas aqui.
/// Nada fora dessa pasta é tocado.
/// </summary>
public static class TempFiles
{
    public static string Root => Path.Combine(Path.GetTempPath(), "PDFMaster");

    /// <summary>Resultado da limpeza.</summary>
    public sealed record CleanResult(int Removed, long Bytes, int InUse);

    /// <summary>
    /// Apaga itens de <see cref="Root"/> sem alteração há mais de <paramref name="olderThan"/>.
    /// Itens em uso por outra operação (arquivos abertos) são mantidos.
    /// </summary>
    public static CleanResult Clean(TimeSpan olderThan, string? root = null)
    {
        root ??= Root;
        if (!Directory.Exists(root)) return new CleanResult(0, 0, 0);
        var limit = DateTime.UtcNow - olderThan;
        int removed = 0, inUse = 0;
        long bytes = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            try
            {
                if (LastChangeUtc(entry) > limit) continue;
                var size = Size(entry);
                if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
                else entry.Delete();
                removed++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                inUse++;
            }
        }

        return new CleanResult(removed, bytes, inUse);
    }

    private static DateTime LastChangeUtc(FileSystemInfo entry)
    {
        var latest = entry.LastWriteTimeUtc;
        if (entry is DirectoryInfo directory)
        {
            foreach (var item in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                if (item.LastWriteTimeUtc > latest) latest = item.LastWriteTimeUtc;
        }

        return latest;
    }

    private static long Size(FileSystemInfo entry) => entry switch
    {
        FileInfo file => file.Length,
        DirectoryInfo directory => directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length),
        _ => 0,
    };
}
