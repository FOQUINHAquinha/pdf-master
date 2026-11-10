using Microsoft.Extensions.Logging;
using Serilog;

namespace PDFMaster.Infrastructure;

/// <summary>
/// Locais onde o programa grava seus próprios dados. Ordem de escolha:
/// 1. variável de ambiente PDFMASTER_DATA_DIR;
/// 2. modo portátil: arquivo "portable.flag" ao lado do executável → pasta "Data" ao lado dele;
/// 3. %LOCALAPPDATA%\PDFMaster.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? overrideDirectory = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable("PDFMASTER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            DataDirectory = Path.GetFullPath(overrideDirectory);
        }
        else if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            DataDirectory = Path.GetFullPath(fromEnv);
        }
        else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag")))
        {
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
            IsPortable = true;
        }
        else
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDFMaster");
        }

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string DataDirectory { get; }
    public bool IsPortable { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "pdfmaster.db");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");
}

public static class LoggingSetup
{
    /// <summary>Log estruturado em arquivo local, com rotação diária. Nada é enviado para fora.</summary>
    public static ILoggerFactory CreateLoggerFactory(AppPaths paths)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "pdfmaster-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}")
            .CreateLogger();

        return LoggerFactory.Create(builder => builder.AddSerilog(serilog, dispose: true));
    }
}
