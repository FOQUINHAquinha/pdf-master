using System.Text.Json;
using System.Text.Json.Serialization;

namespace PDFMaster.Editor;

/// <summary>Modelo de carimbo.</summary>
/// <param name="ShowDate">Acrescenta a data abaixo do texto (ex.: "RECEBIDO" / "10/11/2026").</param>
public sealed record StampTemplate(string Id, string Text, Rgb Color, bool ShowDate = false, string? Subtitle = null)
{
    public const int MaxTextLength = 60;
}

/// <summary>
/// Biblioteca de carimbos guardada localmente (carimbos.json na pasta de dados). Na primeira vez
/// vem com os exemplos CONFIDENCIAL, CÓPIA, ORIGINAL, DOCUMENTO ANALISADO, RECEBIDO e RASCUNHO.
/// </summary>
public sealed class StampLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new RgbConverter() } };
    private readonly string _file;
    private readonly object _gate = new();

    public StampLibrary(string dataDirectory) => _file = Path.Combine(dataDirectory, "carimbos.json");

    public static IReadOnlyList<StampTemplate> Defaults { get; } =
    [
        new("confidencial", "CONFIDENCIAL", Rgb.Red),
        new("copia", "CÓPIA", Rgb.Blue),
        new("original", "ORIGINAL", Rgb.Green),
        new("analisado", "DOCUMENTO ANALISADO", Rgb.Blue, ShowDate: true),
        new("recebido", "RECEBIDO", Rgb.Green, ShowDate: true),
        new("rascunho", "RASCUNHO", new Rgb(0x80, 0x80, 0x80)),
    ];

    public IReadOnlyList<StampTemplate> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_file)) return Defaults;
                var stored = JsonSerializer.Deserialize<List<StampTemplate>>(File.ReadAllText(_file), Json);
                return stored is null ? Defaults : stored.Where(IsValid).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return Defaults;
            }
        }
    }

    public void Save(IReadOnlyList<StampTemplate> stamps)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stamps.Where(IsValid).ToList(), Json));
            File.Move(temp, _file, overwrite: true);
        }
    }

    public static bool IsValid(StampTemplate stamp) =>
        !string.IsNullOrWhiteSpace(stamp.Id) && !string.IsNullOrWhiteSpace(stamp.Text) && stamp.Text.Length <= StampTemplate.MaxTextLength;

    private sealed class RgbConverter : JsonConverter<Rgb>
    {
        public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Rgb.TryParse(reader.GetString(), out var color) ? color : Rgb.Black;

        public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options) => writer.WriteStringValue(value.Hex);
    }
}

/// <summary>Assinaturas visuais guardadas localmente como PNG (pasta "assinaturas" na pasta de dados).</summary>
public sealed class SignatureLibrary
{
    public SignatureLibrary(string dataDirectory) => Folder = Path.Combine(dataDirectory, "assinaturas");

    public string Folder { get; }

    public IReadOnlyList<string> List()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Guarda uma assinatura (PNG) e devolve o caminho do modelo.</summary>
    public string Add(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P') throw new ArgumentException("A assinatura precisa estar em PNG.");
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"assinatura-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    public void Delete(string path)
    {
        var full = Path.GetFullPath(path);
        // Só apaga arquivos da própria biblioteca.
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(full)) File.Delete(full);
    }
}
