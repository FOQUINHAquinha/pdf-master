using PdfSharp.Fonts;

namespace PDFMaster.PdfEngine;

/// <summary>
/// Fontes do Windows para o PDFsharp (versão "Core", que precisa saber onde estão os arquivos).
/// Um único resolvedor para o programa inteiro: o PDFsharp só aceita defini-lo uma vez.
/// </summary>
public static class WindowsFonts
{
    private static readonly object Gate = new();

    /// <summary>Famílias oferecidas no editor (todas presentes em uma instalação padrão do Windows 10/11).</summary>
    public static readonly IReadOnlyList<string> EditorFamilies =
    [
        "Arial", "Calibri", "Cambria", "Consolas", "Courier New", "Georgia", "Segoe UI", "Tahoma", "Times New Roman", "Verdana",
    ];

    // Arquivos: normal, negrito, itálico, negrito itálico.
    private static readonly Dictionary<string, string[]> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"],
        ["Calibri"] = ["calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"],
        ["Cambria"] = ["cambria.ttc", "cambriab.ttf", "cambriai.ttf", "cambriaz.ttf"],
        ["Consolas"] = ["consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"],
        ["Courier New"] = ["cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"],
        ["Georgia"] = ["georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf"],
        ["Segoe UI"] = ["segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"],
        ["Tahoma"] = ["tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf"],
        ["Times New Roman"] = ["times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"],
        ["Verdana"] = ["verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf"],
    };

    /// <summary>Instala o resolvedor, se ninguém instalou antes. Pode ser chamado várias vezes.</summary>
    public static void EnsureInstalled()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new Resolver();
        }
    }

    /// <summary>Arquivo de fonte usado para a família e o estilo pedidos (ou o substituto).</summary>
    public static string FileFor(string familyName, bool bold, bool italic)
    {
        var family = familyName.Trim();
        // "Courier" sozinho (sem "New") é a fonte de largura fixa do texto para PDF: Consolas.
        if (family.Equals("Courier", StringComparison.OrdinalIgnoreCase)) family = "Consolas";
        if (!Files.TryGetValue(family, out var files)) files = Files["Segoe UI"];
        var index = (bold ? 1 : 0) + (italic ? 2 : 0);
        foreach (var candidate in new[] { files[index], files[0], "segoeui.ttf", "arial.ttf" })
        {
            if (File.Exists(FontPath(candidate))) return candidate;
        }

        return files[index];
    }

    private static string FontPath(string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.GetFileName(file));

    private sealed class Resolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var file = FileFor(familyName, isBold, isItalic);
            if (!File.Exists(FontPath(file))) return null;

            // Quando não existe o arquivo do estilo, o PDFsharp simula negrito/itálico.
            var simulate = (isBold || isItalic) && file.Equals(FileFor(familyName, false, false), StringComparison.OrdinalIgnoreCase);
            return simulate ? new FontResolverInfo(file, isBold, isItalic) : new FontResolverInfo(file);
        }

        public byte[]? GetFont(string faceName)
        {
            var path = FontPath(faceName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
