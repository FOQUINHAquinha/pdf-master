using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PDFMaster.Security;

/// <summary>Níveis de PDF/A oferecidos (conformidade "b": aparência visual preservada).</summary>
public enum PdfALevel
{
    A1b = 1,
    A2b = 2,
    A3b = 3,
}

public enum CheckStatus
{
    Passed,
    Failed,

    /// <summary>Não foi possível verificar (por exemplo, conteúdo de página ilegível).</summary>
    NotVerified,
}

/// <summary>Um grupo de requisitos verificado, com exemplos do que falhou.</summary>
public sealed record PdfACheck(string Id, string Description, CheckStatus Status, IReadOnlyList<string> Problems);

/// <summary>Resultado do validador externo (veraPDF), quando instalado.</summary>
/// <param name="Compliant">true/false segundo o veraPDF; null quando a validação não pôde ser feita.</param>
public sealed record ExternalValidation(string Validator, bool? Compliant, string Summary, IReadOnlyList<string> FailedRules);

/// <summary>Relatório de validação de um arquivo.</summary>
/// <param name="DeclaredPart">Parte PDF/A que o arquivo declara (0 = não declara).</param>
public sealed record PdfAValidation(string Path, PdfALevel Level, int DeclaredPart, string DeclaredConformance,
    IReadOnlyList<PdfACheck> Checks, ExternalValidation? External)
{
    public bool InternalPassed => Checks.All(c => c.Status == CheckStatus.Passed);

    public int Failures => Checks.Count(c => c.Status == CheckStatus.Failed);

    /// <summary>Conclusão honesta: só fala em conformidade quando um validador completo confirmou.</summary>
    public string Verdict
    {
        get
        {
            var name = PdfA.Name(Level);
            if (External?.Compliant == false) return $"NÃO conforme com {name}, segundo o {External.Validator}.";
            if (!InternalPassed)
                return Failures > 0 ? $"NÃO conforme com {name}: {Failures} grupo(s) de requisitos falharam na verificação interna." : $"Verificação interna incompleta para {name}.";
            if (External?.Compliant == true) return $"Conforme com {name}, segundo o {External.Validator} e a verificação interna.";
            return $"Aprovado na verificação interna de {name}. A conformidade completa só é confirmada por um validador como o veraPDF.";
        }
    }
}

/// <summary>Resultado da conversão.</summary>
/// <param name="Changes">O que foi alterado no documento para atender ao nível.</param>
public sealed record PdfAConversion(PDFMaster.PdfEngine.ProducedFile File, PdfAValidation Validation, IReadOnlyList<string> Changes);

/// <summary>A conversão parou antes de gerar o arquivo; <see cref="Problems"/> explica o porquê.</summary>
public sealed class PdfAConversionException : PDFMaster.PdfEngine.PdfEngineException
{
    public PdfAConversionException(string message, IReadOnlyList<string> problems, PdfAValidation? validation = null)
        : base(message) => (Problems, Validation) = (problems, validation);

    public IReadOnlyList<string> Problems { get; }

    public PdfAValidation? Validation { get; }
}

public static class PdfA
{
    public static string Name(PdfALevel level) => level switch
    {
        PdfALevel.A1b => "PDF/A-1b",
        PdfALevel.A2b => "PDF/A-2b",
        _ => "PDF/A-3b",
    };

    public static string Flavour(PdfALevel level) => level switch
    {
        PdfALevel.A1b => "1b",
        PdfALevel.A2b => "2b",
        _ => "3b",
    };

    internal static readonly HashSet<string> AnnotationTypes14 =
    [
        "/Text", "/Link", "/FreeText", "/Line", "/Square", "/Circle", "/Polygon", "/PolyLine", "/Highlight", "/Underline",
        "/Squiggly", "/StrikeOut", "/Stamp", "/Ink", "/Popup", "/FileAttachment", "/Sound", "/Movie", "/Widget", "/PrinterMark", "/TrapNet",
    ];

    internal static readonly HashSet<string> AnnotationTypes17 =
    [
        .. AnnotationTypes14, "/Caret", "/Screen", "/Watermark", "/3D", "/Redact",
    ];

    internal static bool ForbiddenAnnotation(string subtype, PdfALevel level) => level switch
    {
        PdfALevel.A1b => !AnnotationTypes14.Contains(subtype) || subtype is "/FileAttachment" or "/Sound" or "/Movie",
        PdfALevel.A2b => !AnnotationTypes17.Contains(subtype) || subtype is "/3D" or "/Sound" or "/Screen" or "/Movie" or "/FileAttachment",
        _ => !AnnotationTypes17.Contains(subtype) || subtype is "/3D" or "/Sound" or "/Screen" or "/Movie",
    };

    internal static bool ForbiddenAction(string type, PdfALevel level) => level == PdfALevel.A1b
        ? type is "/Launch" or "/Sound" or "/Movie" or "/ResetForm" or "/ImportData" or "/JavaScript" or "/Hide" or "/SetState" or "/NOP"
        : type is "/Launch" or "/Sound" or "/Movie" or "/ResetForm" or "/ImportData" or "/Hide" or "/SetOCGState" or "/Rendition" or "/Trans" or "/GoTo3DView" or "/JavaScript";

    internal static readonly HashSet<string> AllowedNamedActions = ["/NextPage", "/PrevPage", "/FirstPage", "/LastPage"];

    internal static readonly HashSet<string> StandardBlendModes =
    [
        "/Normal", "/Compatible", "/Multiply", "/Screen", "/Overlay", "/Darken", "/Lighten", "/ColorDodge", "/ColorBurn",
        "/HardLight", "/SoftLight", "/Difference", "/Exclusion", "/Hue", "/Saturation", "/Color", "/Luminosity",
    ];

    internal static readonly HashSet<string> FontSubtypes = ["/Type0", "/Type1", "/MMType1", "/Type3", "/TrueType"];

    /// <summary>Chaves pelas quais uma ação é alcançada.</summary>
    internal static bool IsActionKey(string key, string parentKey) => key is "/A" or "/OpenAction" or "/Next" || parentKey == "/AA";

    internal static bool IsAnnotation(PdfDictionary d, string key) =>
        d.Elements.ContainsKey("/Rect") && d.Elements.ContainsKey("/Subtype") && (key == "/Annots" || d.Elements.GetName("/Type") == "/Annot" || key == "/Popup");

    internal static bool IsFont(PdfDictionary d) =>
        d.Elements.GetName("/Type") == "/Font" && FontSubtypes.Contains(d.Elements.GetName("/Subtype"));

    internal static bool IsImage(PdfDictionary d) => d.Stream is not null && d.Elements.GetName("/Subtype") == "/Image";

    internal static bool IsForm(PdfDictionary d) => d.Stream is not null && d.Elements.GetName("/Subtype") == "/Form";

    internal static string Describe(string? text, int max = 60)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    internal static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    /// <summary>Filtros de um fluxo (nome único ou lista).</summary>
    internal static IEnumerable<string> Filters(PdfDictionary d)
    {
        switch (Resolve(d.Elements["/Filter"]))
        {
            case PdfName name:
                yield return name.Value;
                break;
            case PdfArray array:
                for (var i = 0; i < array.Elements.Count; i++)
                {
                    if (Resolve(array.Elements[i]) is PdfName n) yield return n.Value;
                }

                break;
        }
    }
}

/// <summary>Dicionário alcançado a partir do catálogo, com a chave pela qual foi alcançado.</summary>
internal readonly record struct GraphNode(PdfDictionary Dictionary, string Key, string ParentKey);

/// <summary>Percorre todos os dicionários alcançáveis a partir do catálogo, uma vez cada.</summary>
internal static class PdfGraph
{
    public static List<GraphNode> Dictionaries(PdfDocument document)
    {
        var result = new List<GraphNode>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfItem Item, string Key, string ParentKey)>();
        stack.Push((document.Internals.Catalog, "/Root", string.Empty));
        while (stack.Count > 0)
        {
            var (item, key, parentKey) = stack.Pop();
            if (item is PdfReference reference)
            {
                if (reference.Value is not { } value) continue;
                item = value;
            }

            switch (item)
            {
                case PdfDictionary dictionary:
                    if (!visited.Add(dictionary)) continue;
                    result.Add(new GraphNode(dictionary, key, parentKey));
                    foreach (var child in dictionary.Elements.Keys.ToList())
                    {
                        if (dictionary.Elements[child] is { } value) stack.Push((value, child, key));
                    }

                    break;
                case PdfArray array:
                    if (!visited.Add(array)) continue;
                    for (var i = array.Elements.Count - 1; i >= 0; i--)
                    {
                        if (array.Elements[i] is { } value) stack.Push((value, key, parentKey));
                    }

                    break;
            }
        }

        return result;
    }

    /// <summary>Todas as listas (arrays) alcançáveis, para limites de tamanho.</summary>
    public static IEnumerable<PdfArray> Arrays(IEnumerable<GraphNode> nodes)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            foreach (var key in node.Dictionary.Elements.Keys)
            {
                if (PdfA.Resolve(node.Dictionary.Elements[key]) is PdfArray array && seen.Add(array)) yield return array;
            }
        }
    }
}
