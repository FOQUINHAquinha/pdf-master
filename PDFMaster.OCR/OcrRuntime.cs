using System.Reflection;
using PDFMaster.PdfEngine;

namespace PDFMaster.OCR;

public sealed record OcrLanguage(string Code, string Name);

/// <summary>
/// Prepara o Tesseract para uso: extrai as bibliotecas nativas e os modelos de idioma que vêm
/// dentro do programa para a pasta de dados local e informa os idiomas instalados. Nada é
/// baixado: novos idiomas são instalados a partir de um arquivo .traineddata escolhido pelo usuário.
/// </summary>
public sealed class OcrRuntime
{
    private const string NativePrefix = "ocr.native.";
    private const string ModelPrefix = "ocr.tessdata.";
    private const string RuntimeVersion = "tesseract-5.2.0";
    private static readonly object Gate = new();

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["por"] = "Português",
        ["eng"] = "Inglês",
        ["spa"] = "Espanhol",
        ["fra"] = "Francês",
        ["deu"] = "Alemão",
        ["ita"] = "Italiano",
        ["lat"] = "Latim",
        ["nld"] = "Holandês",
        ["cat"] = "Catalão",
        ["glg"] = "Galego",
    };

    private readonly string _nativeRoot;
    private bool _ready;

    /// <param name="dataDirectory">Pasta de dados do programa (ex.: %LOCALAPPDATA%\PDFMaster).</param>
    public OcrRuntime(string dataDirectory)
    {
        ModelsDirectory = Path.Combine(dataDirectory, "tessdata");
        _nativeRoot = Path.Combine(dataDirectory, "ocr-runtime", RuntimeVersion);
    }

    /// <summary>Pasta dos modelos de idioma (.traineddata).</summary>
    public string ModelsDirectory { get; }

    public const string NoLanguagesMessage =
        "Nenhum idioma de OCR está instalado. Use \"Instalar idioma…\" e escolha um arquivo .traineddata " +
        "(disponíveis no projeto tessdata_fast do Tesseract).";

    /// <summary>Extrai o que vem embutido (se ainda não foi extraído). Pode ser chamado várias vezes.</summary>
    public void EnsureReady()
    {
        lock (Gate)
        {
            if (_ready) return;
            try
            {
                Directory.CreateDirectory(ModelsDirectory);
                var assembly = typeof(OcrRuntime).Assembly;
                var hasNative = false;
                foreach (var resource in assembly.GetManifestResourceNames())
                {
                    if (resource.StartsWith(ModelPrefix, StringComparison.Ordinal))
                    {
                        Extract(assembly, resource, Path.Combine(ModelsDirectory, SafeName(resource[ModelPrefix.Length..])));
                    }
                    else if (resource.StartsWith(NativePrefix, StringComparison.Ordinal))
                    {
                        Extract(assembly, resource, Path.Combine(_nativeRoot, "x64", SafeName(resource[NativePrefix.Length..])));
                        hasNative = true;
                    }
                }

                // Diz ao binding do Tesseract onde estão as bibliotecas nativas extraídas. Sem
                // bibliotecas embutidas, ele procura ao lado do executável (comportamento padrão).
                if (hasNative) SetNativeSearchPath(_nativeRoot);
                _ready = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PdfEngineException("Não foi possível preparar os componentes de OCR na pasta de dados: " + ex.Message, ex);
            }
        }
    }

    public IReadOnlyList<OcrLanguage> InstalledLanguages()
    {
        EnsureReady();
        return Directory.EnumerateFiles(ModelsDirectory, "*.traineddata")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(code => !string.IsNullOrEmpty(code) && !code.Equals("osd", StringComparison.OrdinalIgnoreCase))
            .Select(code => new OcrLanguage(code!, KnownNames.TryGetValue(code!, out var name) ? name : code!))
            .OrderBy(l => l.Code switch { "por" => 0, "eng" => 1, "spa" => 2, _ => 3 })
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Instala um idioma copiando um arquivo .traineddata para a pasta de modelos.</summary>
    public OcrLanguage InstallLanguage(string traineddataPath)
    {
        EnsureReady();
        var name = Path.GetFileName(traineddataPath);
        if (!name.EndsWith(".traineddata", StringComparison.OrdinalIgnoreCase))
            throw new PdfEngineException("Escolha um arquivo de modelo do Tesseract (.traineddata).");

        var code = Path.GetFileNameWithoutExtension(name);
        if (code.Length is < 2 or > 40 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new PdfEngineException("O nome do arquivo deve ser o código do idioma, por exemplo \"fra.traineddata\".");

        try
        {
            var info = new FileInfo(traineddataPath);
            if (!info.Exists || info.Length < 10_000)
                throw new PdfEngineException($"{name} não parece ser um modelo válido (arquivo vazio ou pequeno demais).");

            var destination = Path.Combine(ModelsDirectory, code + ".traineddata");
            if (!string.Equals(Path.GetFullPath(traineddataPath), destination, StringComparison.OrdinalIgnoreCase))
                File.Copy(traineddataPath, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível instalar {name}: {ex.Message}", ex);
        }

        return new OcrLanguage(code, KnownNames.TryGetValue(code, out var known) ? known : code);
    }

    private static string SafeName(string name) => Path.GetFileName(name.Replace('\\', '/'));

    private static void Extract(Assembly assembly, string resource, string destination)
    {
        using var source = assembly.GetManifestResourceStream(resource);
        if (source is null) return;
        var existing = new FileInfo(destination);
        if (existing.Exists && existing.Length == source.Length) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch (IOException) when (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
        {
            // Outra instância do programa extraiu o mesmo arquivo ao mesmo tempo.
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void SetNativeSearchPath(string path)
    {
        var assembly = typeof(Tesseract.TesseractEngine).Assembly;
        // O nome da classe tem um erro de grafia no pacote ("Enviornment"); a busca cobre as duas formas.
        var type = assembly.GetType("Tesseract.TesseractEnviornment") ?? assembly.GetType("Tesseract.TesseractEnvironment");
        type?.GetProperty("CustomSearchPath", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, path);
    }
}
