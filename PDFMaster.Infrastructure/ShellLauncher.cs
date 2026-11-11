using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PDFMaster.Core.Files;

namespace PDFMaster.Infrastructure;

/// <summary>
/// Abre pastas e documentos no Windows. Só abre arquivos cujo tipo está na lista de formatos
/// suportados — um arquivo recebido nunca é executado como programa.
/// </summary>
public sealed class ShellLauncher
{
    private readonly ILogger<ShellLauncher> _logger;

    public ShellLauncher(ILogger<ShellLauncher> logger) => _logger = logger;

    /// <summary>Abre o documento no aplicativo padrão do Windows. Retorna false se não for possível.</summary>
    public bool OpenDocument(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!SupportedFiles.IsSupported(full) || !File.Exists(full)) return false;
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível abrir o documento {Path}", path);
            return false;
        }
    }

    /// <summary>Abre uma página das Configurações do Windows (somente endereços ms-settings:).</summary>
    public bool OpenSettingsPage(string uri)
    {
        if (!uri.StartsWith("ms-settings:", StringComparison.Ordinal)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível abrir {Uri}", uri);
            return false;
        }
    }

    /// <summary>Abre a pasta no Explorador de Arquivos, selecionando o arquivo quando ele existe.</summary>
    public bool OpenFolder(string fileOrFolder)
    {
        try
        {
            var full = Path.GetFullPath(fileOrFolder);
            ProcessStartInfo info;
            if (File.Exists(full))
            {
                info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add("/select," + full);
            }
            else
            {
                var folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (folder is null || !Directory.Exists(folder)) return false;
                info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add(folder);
            }

            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível abrir a pasta de {Path}", fileOrFolder);
            return false;
        }
    }
}
