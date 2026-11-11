using System.Text.Json;
using System.Text.Json.Serialization;
using PDFMaster.Compression;
using PDFMaster.Security;

namespace PDFMaster.Batch;

/// <summary>Posição da numeração de páginas.</summary>
public enum NumberPosition
{
    BottomCenter,
    BottomRight,
    BottomLeft,
    TopCenter,
    TopRight,
    TopLeft,
}

/// <summary>Tamanho da folha para várias páginas por folha e livreto.</summary>
public enum SheetSize
{
    A4,
    Letter,
    A3,
}

/// <summary>Uma etapa de um fluxo. Cada etapa recebe um PDF e entrega outro PDF.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "etapa")]
[JsonDerivedType(typeof(OcrStep), "ocr")]
[JsonDerivedType(typeof(CompressStep), "comprimir")]
[JsonDerivedType(typeof(PageNumbersStep), "numerar")]
[JsonDerivedType(typeof(WatermarkStep), "marca-dagua")]
[JsonDerivedType(typeof(RotateStep), "girar")]
[JsonDerivedType(typeof(DeletePagesStep), "excluir-paginas")]
[JsonDerivedType(typeof(ReverseStep), "inverter")]
[JsonDerivedType(typeof(RemoveBlankPagesStep), "remover-brancas")]
[JsonDerivedType(typeof(CropStep), "recortar")]
[JsonDerivedType(typeof(NUpStep), "paginas-por-folha")]
[JsonDerivedType(typeof(RemoveMetadataStep), "remover-metadados")]
[JsonDerivedType(typeof(RepairStep), "reparar")]
[JsonDerivedType(typeof(PdfAStep), "pdfa")]
public abstract record FlowStep
{
    /// <summary>Nome curto da operação.</summary>
    [JsonIgnore]
    public abstract string Name { get; }

    /// <summary>Resumo das opções, para listas e relatórios.</summary>
    public abstract string Describe();

    /// <summary>Problema nas opções (null = válidas).</summary>
    public virtual string? Validate() => null;

    /// <summary>Etapas disponíveis no editor, com as opções padrão.</summary>
    public static IReadOnlyList<FlowStep> Catalog { get; } =
    [
        new OcrStep(), new CompressStep(), new PageNumbersStep(), new WatermarkStep(), new RotateStep(), new DeletePagesStep(),
        new ReverseStep(), new RemoveBlankPagesStep(), new CropStep(), new NUpStep(), new RemoveMetadataStep(), new RepairStep(), new PdfAStep(),
    ];
}

public sealed record OcrStep(string Languages = "por", int Dpi = 300, bool SkipPagesWithText = true) : FlowStep
{
    public override string Name => "OCR (PDF pesquisável)";
    public override string Describe() => $"idiomas {Languages}, {Dpi} DPI" + (SkipPagesWithText ? ", pula páginas com texto" : string.Empty);
    public override string? Validate() => Dpi is < 100 or > 600 ? "A resolução do OCR deve ficar entre 100 e 600 DPI."
        : string.IsNullOrWhiteSpace(Languages) ? "Informe ao menos um idioma do OCR." : null;
}

public sealed record CompressStep(CompressionProfile Profile = CompressionProfile.Balanced) : FlowStep
{
    public override string Name => "Comprimir";
    public override string Describe() => CompressionSettings.Describe(Profile);
    public override string? Validate() => Profile == CompressionProfile.Custom ? "Escolha um perfil de compressão." : null;
}

public sealed record PageNumbersStep(string Text = "Página {pagina} de {total}", NumberPosition Position = NumberPosition.BottomCenter, double FontSize = 9) : FlowStep
{
    public override string Name => "Numerar páginas";
    public override string Describe() => $"\"{Text}\", {PositionName(Position)}, {FontSize:0.#} pt";
    public override string? Validate() => string.IsNullOrWhiteSpace(Text) ? "Informe o texto da numeração (use {pagina} e {total})."
        : FontSize is < 4 or > 72 ? "O tamanho da fonte deve ficar entre 4 e 72." : null;

    public static string PositionName(NumberPosition p) => p switch
    {
        NumberPosition.BottomRight => "rodapé à direita",
        NumberPosition.BottomLeft => "rodapé à esquerda",
        NumberPosition.TopCenter => "cabeçalho no centro",
        NumberPosition.TopRight => "cabeçalho à direita",
        NumberPosition.TopLeft => "cabeçalho à esquerda",
        _ => "rodapé no centro",
    };
}

public sealed record WatermarkStep(string Text = "CONFIDENCIAL", double OpacityPercent = 30, double Rotation = 45, double FontSize = 60, string Color = "#C02020") : FlowStep
{
    public override string Name => "Marca d'água";
    public override string Describe() => $"\"{Text}\", {OpacityPercent:0}% de opacidade, {Rotation:0}°";
    public override string? Validate() => string.IsNullOrWhiteSpace(Text) ? "Informe o texto da marca d'água."
        : OpacityPercent is < 5 or > 100 ? "A opacidade deve ficar entre 5% e 100%."
        : FontSize is < 4 or > 200 ? "O tamanho da fonte deve ficar entre 4 e 200."
        : !PDFMaster.Editor.Rgb.TryParse(Color, out _) ? "Cor inválida (use #RRGGBB)." : null;
}

public sealed record RotateStep(int Degrees = 90, string Pages = "") : FlowStep
{
    public override string Name => "Girar páginas";
    public override string Describe() => $"{Degrees}°" + (string.IsNullOrWhiteSpace(Pages) ? ", todas as páginas" : $", páginas {Pages}");
    public override string? Validate() => Degrees is 90 or 180 or 270 ? null : "Gire em 90, 180 ou 270 graus.";
}

public sealed record DeletePagesStep(string Pages = "1") : FlowStep
{
    public override string Name => "Excluir páginas";
    public override string Describe() => $"páginas {Pages}";
    public override string? Validate() => string.IsNullOrWhiteSpace(Pages) ? "Informe as páginas a excluir (ex.: 1, 3-5)." : null;
}

public sealed record ReverseStep : FlowStep
{
    public override string Name => "Inverter ordem das páginas";
    public override string Describe() => "da última para a primeira";
}

public sealed record RemoveBlankPagesStep(bool AlsoDuplicates = false) : FlowStep
{
    public override string Name => "Remover páginas em branco";
    public override string Describe() => AlsoDuplicates ? "e páginas repetidas" : "só as páginas em branco";
}

public sealed record CropStep(double TopMm = 10, double BottomMm = 10, double LeftMm = 10, double RightMm = 10) : FlowStep
{
    public override string Name => "Recortar margens";
    public override string Describe() => $"cima {TopMm:0.#} mm, baixo {BottomMm:0.#} mm, esquerda {LeftMm:0.#} mm, direita {RightMm:0.#} mm";
    public override string? Validate() => new[] { TopMm, BottomMm, LeftMm, RightMm }.Any(v => v is < 0 or > 500) ? "Cada margem deve ficar entre 0 e 500 mm."
        : TopMm + BottomMm + LeftMm + RightMm == 0 ? "Informe ao menos uma margem a recortar." : null;
}

public sealed record NUpStep(int PagesPerSheet = 2, bool Booklet = false, SheetSize Sheet = SheetSize.A4) : FlowStep
{
    public override string Name => Booklet ? "Livreto" : "Várias páginas por folha";
    public override string Describe() => Booklet ? $"livreto em folhas {Sheet} (2 páginas por lado, na ordem de dobra)" : $"{PagesPerSheet} páginas por folha {Sheet}";
    public override string? Validate() => Booklet || PagesPerSheet is 2 or 4 ? null : "Escolha 2 ou 4 páginas por folha.";
}

public sealed record RemoveMetadataStep : FlowStep
{
    public override string Name => "Remover metadados";
    public override string Describe() => "título, autor, datas e bloco XMP";
}

public sealed record RepairStep : FlowStep
{
    public override string Name => "Reparar";
    public override string Describe() => "reconstrói a estrutura do arquivo";
}

public sealed record PdfAStep(PdfALevel Level = PdfALevel.A2b) : FlowStep
{
    public override string Name => "Converter para PDF/A";
    public override string Describe() => PdfA.Name(Level) + " (validado com o veraPDF quando instalado)";
}

/// <summary>Sequência de etapas aplicada a cada arquivo.</summary>
/// <param name="Suffix">Acrescentado ao nome do arquivo de saída (ex.: "_processado").</param>
public sealed record BatchFlow(string Name, IReadOnlyList<FlowStep> Steps, string Suffix = "_processado")
{
    public string Describe() => Steps.Count == 0 ? "sem etapas" : string.Join(" → ", Steps.Select(s => s.Name));

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Dê um nome ao fluxo.";
        if (Steps.Count == 0) return "Acrescente ao menos uma etapa.";
        if (Suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "O sufixo do nome tem caracteres inválidos.";
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Validate() is { } problem) return $"Etapa {i + 1} ({Steps[i].Name}): {problem}";
        }

        return null;
    }

    /// <summary>Fluxos de exemplo criados na primeira utilização.</summary>
    public static IReadOnlyList<BatchFlow> Examples { get; } =
    [
        new("Digitalizados: OCR, comprimir, numerar e PDF/A", [new OcrStep(), new CompressStep(), new PageNumbersStep(), new PdfAStep()], "_arquivo"),
        new("Comprimir (equilibrada)", [new CompressStep()], "_comprimido"),
        new("Arquivar em PDF/A-2b", [new PdfAStep()], "_PDFA"),
        new("Limpar: remover páginas em branco e metadados", [new RemoveBlankPagesStep(), new RemoveMetadataStep()], "_limpo"),
    ];
}

/// <summary>Fluxos salvos (arquivo JSON na pasta de dados do programa).</summary>
public sealed class FlowStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public FlowStore(string dataDirectory) => _path = Path.Combine(dataDirectory, "fluxos.json");

    public string FilePath => _path;

    /// <summary>Fluxos salvos; na primeira vez, os exemplos.</summary>
    public IReadOnlyList<BatchFlow> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return BatchFlow.Examples;
            try
            {
                return JsonSerializer.Deserialize<List<BatchFlow>>(File.ReadAllText(_path), Json) ?? [];
            }
            catch (JsonException)
            {
                // Arquivo danificado: guarda uma cópia e volta aos exemplos.
                File.Copy(_path, _path + ".danificado", overwrite: true);
                return BatchFlow.Examples;
            }
        }
    }

    public void Save(IReadOnlyList<BatchFlow> flows)
    {
        if (flows.Select(f => f.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != flows.Count)
            throw new InvalidOperationException("Há fluxos com o mesmo nome.");
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(flows, Json));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public BatchFlow? Find(string name) =>
        Load().FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
