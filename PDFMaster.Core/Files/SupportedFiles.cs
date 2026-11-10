using PDFMaster.Domain;

namespace PDFMaster.Core.Files;

/// <summary>Tipos de arquivo que o programa aceita como entrada.</summary>
public static class SupportedFiles
{
    private static readonly Dictionary<string, FileKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = FileKind.Pdf,
        [".png"] = FileKind.Image,
        [".jpg"] = FileKind.Image,
        [".jpeg"] = FileKind.Image,
        [".bmp"] = FileKind.Image,
        [".tif"] = FileKind.Image,
        [".tiff"] = FileKind.Image,
        [".docx"] = FileKind.OfficeDocument,
        [".doc"] = FileKind.OfficeDocument,
        [".xlsx"] = FileKind.OfficeDocument,
        [".xls"] = FileKind.OfficeDocument,
        [".pptx"] = FileKind.OfficeDocument,
        [".ppt"] = FileKind.OfficeDocument,
        [".txt"] = FileKind.Text,
        [".html"] = FileKind.Html,
        [".htm"] = FileKind.Html,
    };

    public static IReadOnlyCollection<string> Extensions => Kinds.Keys;

    public static FileKind GetKind(string path)
    {
        var ext = Path.GetExtension(path);
        return Kinds.TryGetValue(ext, out var kind) ? kind : FileKind.Unsupported;
    }

    public static bool IsSupported(string path) => GetKind(path) != FileKind.Unsupported;

    /// <summary>Filtro para a caixa de diálogo de abertura de arquivos.</summary>
    public static string DialogFilter
    {
        get
        {
            var all = string.Join(";", Kinds.Keys.Select(e => "*" + e));
            return $"Arquivos suportados|{all}|Documentos PDF|*.pdf|Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|" +
                   "Documentos Office|*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt|Texto e HTML|*.txt;*.html;*.htm";
        }
    }
}

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var culture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return unit == 0
            ? $"{bytes} B"
            : string.Format(culture, "{0:0.##} {1}", value, Units[unit]);
    }
}
