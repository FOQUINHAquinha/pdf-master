using System.Diagnostics;
using System.Xml.Linq;

namespace PDFMaster.Security;

/// <summary>
/// Validador externo opcional: o veraPDF (software livre, instalado pela pessoa; requer Java). Quando
/// presente, confirma ou recusa a conformidade PDF/A. O programa nunca baixa nem instala o veraPDF.
/// </summary>
public static class VeraPdf
{
    public const string Name = "veraPDF";

    /// <summary>Caminho do veraPDF (verapdf.bat), ou null se não estiver instalado.</summary>
    public static string? Find()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("PDFMASTER_VERAPDF") is { Length: > 0 } configured)
            candidates.Add(Directory.Exists(configured) ? Path.Combine(configured, "verapdf.bat") : configured);
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
                 })
        {
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "veraPDF", "verapdf.bat"));
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(folder.Trim('"'), "verapdf.bat"));
            }
            catch (ArgumentException)
            {
                // Entrada inválida no PATH.
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Valida com o veraPDF. Devolve null se ele não estiver instalado.</summary>
    public static ExternalValidation? Validate(string pdfPath, PdfALevel level, CancellationToken ct = default)
    {
        var tool = Find();
        if (tool is null) return null;

        // Cópia com extensão .pdf numa pasta temporária (o arquivo de trabalho pode ter outro nome).
        var copy = Path.Combine(Path.GetTempPath(), $"pdfmaster-verapdf-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(pdfPath, copy);
            var info = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "--flavour", PdfA.Flavour(level), "--format", "xml", copy }) info.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = info };
            try
            {
                if (!process.Start()) return new ExternalValidation(Name, null, "O veraPDF não pôde ser iniciado.", []);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new ExternalValidation(Name, null, "O veraPDF não pôde ser iniciado: " + ex.Message, []);
            }

            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using (ct.Register(() =>
                   {
                       try
                       {
                           process.Kill(entireProcessTree: true);
                       }
                       catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                       {
                           // Já terminou.
                       }
                   }))
            {
                if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
                {
                    process.Kill(entireProcessTree: true);
                    return new ExternalValidation(Name, null, "O veraPDF demorou demais e foi interrompido.", []);
                }
            }

            ct.ThrowIfCancellationRequested();
            return Parse(output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
        }
        catch (IOException ex)
        {
            return new ExternalValidation(Name, null, "O arquivo não pôde ser preparado para o veraPDF: " + ex.Message, []);
        }
        finally
        {
            DocumentInfoWriter.TryDelete(copy);
        }
    }

    internal static ExternalValidation Parse(string xml, string errors)
    {
        var start = xml.IndexOf('<');
        if (start < 0)
            return new ExternalValidation(Name, null, "O veraPDF não devolveu um relatório. " + PdfA.Describe(errors.Trim(), 300), []);
        XDocument report;
        try
        {
            report = XDocument.Parse(xml[start..]);
        }
        catch (System.Xml.XmlException)
        {
            return new ExternalValidation(Name, null, "O relatório do veraPDF não pôde ser lido.", []);
        }

        var validation = report.Descendants().FirstOrDefault(e => e.Name.LocalName == "validationReport");
        if (validation?.Attribute("isCompliant")?.Value is not { } compliantText)
        {
            var exception = report.Descendants().FirstOrDefault(e => e.Name.LocalName is "exceptionMessage" or "exception")?.Value;
            return new ExternalValidation(Name, null, "O veraPDF não conseguiu validar o arquivo. " + PdfA.Describe(exception, 300), []);
        }

        var compliant = compliantText.Equals("true", StringComparison.OrdinalIgnoreCase);
        var failed = report.Descendants()
            .Where(e => e.Name.LocalName == "rule" && e.Attribute("status")?.Value == "failed")
            .Select(e =>
            {
                var clause = e.Attribute("clause")?.Value;
                var test = e.Attribute("testNumber")?.Value;
                var description = e.Elements().FirstOrDefault(c => c.Name.LocalName == "description")?.Value.Trim();
                return $"{clause}-{test}: {PdfA.Describe(description, 200)}";
            })
            .ToList();
        var profile = validation.Attribute("profileName")?.Value ?? string.Empty;
        var summary = compliant
            ? $"O veraPDF confirmou a conformidade ({profile})."
            : $"O veraPDF encontrou {failed.Count} regra(s) não atendida(s) ({profile}).";
        return new ExternalValidation(Name, compliant, summary, failed);
    }
}
