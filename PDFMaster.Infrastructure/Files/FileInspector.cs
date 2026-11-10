using System.Security;
using System.Security.Cryptography;
using PDFMaster.Core;
using PDFMaster.Core.Files;
using PDFMaster.Domain;

namespace PDFMaster.Infrastructure.Files;

public sealed class FileInspector : IFileInspector
{
    private const int HeaderWindow = 1024;

    public async Task<FileEntry> InspectAsync(string path, bool computeHash, CancellationToken ct = default)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return new FileEntry(path, path, 0, DateTime.MinValue, FileKind.Unsupported, FileProblem.Unreadable, null);
        }

        var name = Path.GetFileName(full);
        var kind = SupportedFiles.GetKind(full);

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
                return new FileEntry(full, name, 0, DateTime.MinValue, kind, FileProblem.NotFound, null);

            var modified = info.LastWriteTimeUtc;
            if (kind == FileKind.Unsupported)
                return new FileEntry(full, name, info.Length, modified, kind, FileProblem.UnsupportedType, null);
            if (info.Length == 0)
                return new FileEntry(full, name, 0, modified, kind, FileProblem.Empty, null);

            await using var stream = new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (kind == FileKind.Pdf)
            {
                var buffer = new byte[HeaderWindow];
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
                    .ConfigureAwait(false);
                if (!ContainsPdfHeader(buffer.AsSpan(0, read)))
                    return new FileEntry(full, name, info.Length, modified, kind, FileProblem.InvalidPdfHeader, null);
                stream.Position = 0;
            }

            string? hash = null;
            if (computeHash)
            {
                var bytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                hash = Convert.ToHexString(bytes);
            }

            return new FileEntry(full, name, info.Length, modified, kind, FileProblem.None, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new FileEntry(full, name, 0, DateTime.MinValue, kind, FileProblem.Unreadable, null);
        }
    }

    /// <summary>
    /// A especificação PDF aceita o marcador "%PDF-" em qualquer posição dos primeiros 1024 bytes.
    /// </summary>
    public static bool ContainsPdfHeader(ReadOnlySpan<byte> start) => start.IndexOf("%PDF-"u8) >= 0;
}

public sealed class FolderScanner
{
    /// <summary>Lista os arquivos suportados de uma pasta, ignorando o que não pode ser lido.</summary>
    public IReadOnlyList<string> FindSupportedFiles(string folder, bool recursive, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (SupportedFiles.IsSupported(file)) result.Add(file);
        }

        result.Sort(StringComparer.CurrentCultureIgnoreCase);
        return result;
    }
}
