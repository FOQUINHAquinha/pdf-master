using System.Text;

namespace PDFMaster.Core.Files;

/// <summary>Validações de nomes e caminhos para impedir gravação fora da pasta escolhida.</summary>
public static class PathSafety
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Transforma qualquer texto em um nome de arquivo válido no Windows, sem componentes de
    /// diretório. Entradas como "..\..\x.pdf" viram um nome simples.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "arquivo";

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < 32 || Array.IndexOf(InvalidChars, ch) >= 0) sb.Append('_');
            else sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        // Um nome só com pontos/sublinhados (ex.: "..") não é um nome útil.
        if (cleaned.Length == 0 || cleaned.All(c => c is '.' or '_' or ' ')) return "arquivo";

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;

        const int maxLength = 200;
        if (cleaned.Length > maxLength)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length > 20) ext = string.Empty;
            cleaned = cleaned[..(maxLength - ext.Length)] + ext;
        }

        return cleaned;
    }

    /// <summary>Verdadeiro quando <paramref name="candidate"/> está dentro de <paramref name="root"/>.</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureWithin(string root, string candidate)
    {
        if (!IsWithin(root, candidate))
            throw new InvalidOperationException("O caminho de saída está fora da pasta de destino escolhida.");
    }
}
