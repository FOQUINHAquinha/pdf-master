using System.Diagnostics;
using Microsoft.Win32;
using PDFMaster.PdfEngine;

namespace PDFMaster.Conversion;

/// <summary>
/// Integração opcional com o LibreOffice (MPL-2.0), usado em modo "headless" para converter
/// documentos do Office, ODF, RTF e HTML em PDF. O LibreOffice não é distribuído com o PDF Master
/// nem baixado por ele: precisa estar instalado no computador.
/// </summary>
public sealed class LibreOffice
{
    public const string MissingMessage =
        "Esta conversão precisa do LibreOffice, que não foi encontrado neste computador. " +
        "Ele é gratuito e pode ser instalado a partir de libreoffice.org; o PDF Master não baixa nem instala nada por conta própria.";

    // Um perfil do LibreOffice só pode ser usado por um processo de cada vez.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private string? _executable;
    private bool _searched;

    public bool IsAvailable => Executable is not null;

    /// <summary>Caminho do soffice encontrado, ou null.</summary>
    public string? Executable
    {
        get
        {
            if (!_searched)
            {
                _executable = Find();
                _searched = true;
            }

            return _executable;
        }
    }

    /// <summary>Procura de novo (por exemplo, depois que o usuário instalou o LibreOffice).</summary>
    public void Refresh() => _searched = false;

    private static string? Find()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("PDFMASTER_SOFFICE"),
        };

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
                if (key?.GetValue(null) is string folder) candidates.Add(Path.Combine(folder, "soffice.com"));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Sem acesso ao registro: segue para os caminhos padrão.
            }
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(folder)) candidates.Add(Path.Combine(folder, "LibreOffice", "program", "soffice.com"));
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (File.Exists(candidate)) return candidate;
            // Aceita também o .exe quando só ele existe (ou quando a variável aponta para ele).
            var exe = Path.ChangeExtension(candidate, ".exe");
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    /// <summary>
    /// Converte <paramref name="inputPath"/> e devolve o caminho do arquivo gerado, dentro de uma
    /// pasta temporária que o chamador deve apagar com <see cref="DeleteWorkFolder"/>.
    /// O arquivo original é copiado antes; o LibreOffice nunca abre o original.
    /// </summary>
    /// <param name="convertTo">Formato de saída do soffice, ex.: "pdf", "docx", "pdf:writer_web_pdf_Export".</param>
    /// <param name="inputFilter">Filtro de importação opcional, ex.: "writer_pdf_import".</param>
    public async Task<string> ConvertAsync(string inputPath, string convertTo, string? inputFilter, CancellationToken ct)
    {
        var executable = Executable ?? throw new PdfEngineException(MissingMessage);
        if (!File.Exists(inputPath)) throw new PdfEngineException($"Arquivo não encontrado: {inputPath}");

        var work = Path.Combine(Path.GetTempPath(), "PDFMaster", "lo-" + Guid.NewGuid().ToString("N"));
        var inputFolder = Path.Combine(work, "in");
        var outputFolder = Path.Combine(work, "out");
        var profile = Path.Combine(Path.GetTempPath(), "PDFMaster", "lo-perfil");

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(inputFolder);
            Directory.CreateDirectory(outputFolder);
            Directory.CreateDirectory(profile);

            // Nome simples: evita problemas com caracteres especiais na linha de comando.
            var copy = Path.Combine(inputFolder, "entrada" + Path.GetExtension(inputPath).ToLowerInvariant());
            File.Copy(inputPath, copy);

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = work,
            };
            start.ArgumentList.Add("--headless");
            start.ArgumentList.Add("--norestore");
            start.ArgumentList.Add("--nolockcheck");
            start.ArgumentList.Add("--nodefault");
            start.ArgumentList.Add("--nofirststartwizard");
            start.ArgumentList.Add("-env:UserInstallation=" + new Uri(profile).AbsoluteUri);
            if (!string.IsNullOrEmpty(inputFilter)) start.ArgumentList.Add("--infilter=" + inputFilter);
            start.ArgumentList.Add("--convert-to");
            start.ArgumentList.Add(convertTo);
            start.ArgumentList.Add("--outdir");
            start.ArgumentList.Add(outputFolder);
            start.ArgumentList.Add(copy);

            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // O processo já terminou.
                }

                ct.ThrowIfCancellationRequested();
                throw new PdfEngineException("O LibreOffice demorou mais de 5 minutos e a conversão foi interrompida.");
            }

            var produced = Directory.Exists(outputFolder) ? Directory.GetFiles(outputFolder).FirstOrDefault() : null;
            if (produced is null || new FileInfo(produced).Length == 0)
            {
                var detail = (await stderr.ConfigureAwait(false)).Trim();
                if (detail.Length == 0) detail = (await stdout.ConfigureAwait(false)).Trim();
                if (detail.Length > 400) detail = detail[..400];
                throw new PdfEngineException(
                    $"O LibreOffice não conseguiu converter {Path.GetFileName(inputPath)}." +
                    (detail.Length > 0 ? " Detalhe: " + detail : string.Empty));
            }

            return produced;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            DeleteWorkFolder(work);
            throw new PdfEngineException("Não foi possível executar o LibreOffice: " + ex.Message, ex);
        }
        catch
        {
            DeleteWorkFolder(work);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Apaga a pasta temporária de uma conversão (aceita o caminho do arquivo gerado).</summary>
    public static void DeleteWorkFolder(string producedOrFolder)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "PDFMaster");
            var folder = Path.GetFullPath(producedOrFolder);
            // Sobe até a pasta "lo-…" criada para esta conversão.
            while (folder.Length > root.Length && !Path.GetFileName(folder).StartsWith("lo-", StringComparison.Ordinal))
            {
                folder = Path.GetDirectoryName(folder) ?? string.Empty;
            }

            if (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(folder).StartsWith("lo-", StringComparison.Ordinal) &&
                Path.GetFileName(folder) != "lo-perfil" && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporário que não pôde ser apagado agora; fica na pasta temporária do Windows.
        }
    }
}
