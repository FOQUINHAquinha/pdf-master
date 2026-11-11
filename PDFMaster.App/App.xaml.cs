using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PDFMaster.App.Services;
using PDFMaster.App.ViewModels;
using PDFMaster.Compression;
using PDFMaster.Conversion;
using PDFMaster.Video;
using PDFMaster.Editor;
using PDFMaster.Core;
using PDFMaster.Core.Files;
using PDFMaster.Core.Settings;
using PDFMaster.Core.Tools;
using PDFMaster.Domain;
using PDFMaster.Infrastructure;
using PDFMaster.OCR;
using PDFMaster.Infrastructure.Files;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PDFMaster.Scanner;
using PDFMaster.Security;
using PDFMaster.Comparison;
using PDFMaster.Storage;

namespace PDFMaster.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    public App()
    {
        // A interface é toda em português: datas, números e textos dos controles seguem pt-BR.
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var paths = new AppPaths();
            var loggerFactory = LoggingSetup.CreateLoggerFactory(paths);

            var services = new ServiceCollection();
            services.AddSingleton(paths);
            services.AddSingleton(loggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            services.AddSingleton(new SqliteDatabase(paths.DatabasePath));
            services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
            services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
            services.AddSingleton<IRecentFilesRepository, SqliteRecentFilesRepository>();

            services.AddSingleton<SettingsService>();
            services.AddSingleton<ToolCatalog>();
            services.AddSingleton<ToolSearchService>();
            services.AddSingleton(new OutputPathResolver());
            services.AddSingleton<IFileInspector, FileInspector>();
            services.AddSingleton<FolderScanner>();
            services.AddSingleton<FileExportService>();
            services.AddSingleton<ShellLauncher>();

            services.AddSingleton<IPdfEngine, PdfSharpEngine>();
            services.AddSingleton<PdfOrganizer>();
            services.AddSingleton<IPdfRenderer, PdfiumRenderer>();
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton(provider => new ToolContext(
                provider.GetRequiredService<IPdfEngine>(),
                provider.GetRequiredService<PdfOrganizer>(),
                provider.GetRequiredService<IPdfRenderer>(),
                provider.GetRequiredService<ThumbnailService>(),
                provider.GetRequiredService<SettingsService>(),
                provider.GetRequiredService<IHistoryRepository>(),
                provider.GetRequiredService<IRecentFilesRepository>(),
                provider.GetRequiredService<IDialogService>(),
                provider.GetRequiredService<ShellLauncher>(),
                provider.GetRequiredService<NotificationCenter>(),
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<ConversionToolkit>(),
                provider.GetRequiredService<OcrToolkit>(),
                provider.GetRequiredService<VideoToolkit>(),
                provider.GetRequiredService<EditorToolkit>(),
                provider.GetRequiredService<SecurityToolkit>()));

            services.AddSingleton<PdfProtection>();
            services.AddSingleton<PdfMetadata>();
            services.AddSingleton<PdfRepair>();
            services.AddSingleton<PdfAConverter>();
            services.AddSingleton<ComparisonReport>();
            services.AddSingleton(provider => new SecurityToolkit(
                provider.GetRequiredService<PdfProtection>(),
                provider.GetRequiredService<PdfMetadata>(),
                provider.GetRequiredService<PdfRepair>(),
                provider.GetRequiredService<PdfAConverter>(),
                provider.GetRequiredService<ComparisonReport>(),
                System.IO.Path.Combine(paths.DataDirectory, "Certificados confiáveis")));

            services.AddSingleton<PdfEditor>();
            services.AddSingleton(new StampLibrary(paths.DataDirectory));
            services.AddSingleton(new SignatureLibrary(paths.DataDirectory));
            services.AddSingleton<EditorToolkit>();

            services.AddSingleton(new FfmpegRuntime(paths.DataDirectory));
            services.AddSingleton<VideoCompressor>();
            services.AddSingleton<VideoToolkit>();

            services.AddSingleton(new OcrRuntime(paths.DataDirectory));
            services.AddSingleton<OcrService>();
            services.AddSingleton<WiaScanner>();
            services.AddSingleton<OcrToolkit>();

            services.AddSingleton<OutputPlanner>();
            services.AddSingleton<LibreOffice>();
            services.AddSingleton<PdfToImageConverter>();
            services.AddSingleton<ImagesToPdfConverter>();
            services.AddSingleton<OfficeToPdfConverter>();
            services.AddSingleton<TextToPdfConverter>();
            services.AddSingleton<PdfTextExporter>();
            services.AddSingleton<PdfCompressor>();
            services.AddSingleton<ConversionToolkit>();

            services.AddSingleton<NotificationCenter>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<IDialogService, DialogService>();

            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<HistoryViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();

            _services = services.BuildServiceProvider();
            _logger = _services.GetRequiredService<ILogger<App>>();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                _logger?.LogCritical(args.ExceptionObject as Exception, "Exceção fatal não tratada");
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                _logger?.LogError(args.Exception, "Exceção de tarefa não observada");
                args.SetObserved();
            };

            _services.GetRequiredService<SqliteDatabase>().Initialize();
            var settings = _services.GetRequiredService<SettingsService>();
            await settings.LoadAsync();
            var ffmpeg = _services.GetRequiredService<FfmpegRuntime>();
            ffmpeg.CustomPath = settings.Current.FfmpegPath;
            settings.Changed += (_, _) => ffmpeg.CustomPath = settings.Current.FfmpegPath;

            var options = StartupOptions.Parse(e.Args);
            if (options.Theme is { } requestedTheme && requestedTheme != settings.Current.Theme)
            {
                await settings.UpdateAsync(s => s.Theme = requestedTheme);
            }

            var theme = _services.GetRequiredService<ThemeService>();
            theme.Apply(settings.Current.Theme, settings.Current.AccentColor);
            settings.Changed += (_, _) => theme.Apply(settings.Current.Theme, settings.Current.AccentColor);

            var window = _services.GetRequiredService<MainWindow>();
            var main = _services.GetRequiredService<MainViewModel>();
            window.DataContext = main;
            MainWindow = window;
            main.Navigate(options.Files.Count > 0 && options.Tool is null ? "home" : options.Page);
            window.Show();

            if (options.Tool is not null)
            {
                // "--tool merge a.pdf b.pdf": abre a ferramenta já com os arquivos.
                var toolPage = await main.OpenToolAsync(options.Tool, options.Files);
                if (toolPage is not null)
                {
                    if (options.Output is not null) toolPage.OutputFolder = System.IO.Path.GetFullPath(options.Output);
                    if (options.Run && !await toolPage.RunPrimaryAsync())
                    {
                        _services.GetRequiredService<NotificationCenter>().Show(NotificationKind.Warning,
                            "Nada foi executado", "A ferramenta foi aberta, mas faltam dados para executar (--run).");
                    }
                }
            }
            else if (options.Files.Count > 0)
            {
                // Arquivos e pastas passados na linha de comando ("Abrir com PDF Master").
                await _services.GetRequiredService<HomeViewModel>().AddPathsAsync(options.Files);
            }

            _logger.LogInformation("PDF Master iniciado. Dados em {DataDirectory}", paths.DataDirectory);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Falha na inicialização");
            MessageBox.Show(
                "O PDF Master não pôde ser iniciado.\n\n" + ex.Message,
                "PDF Master", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Exceção não tratada na interface");
        e.Handled = true;

        var notifications = _services?.GetService<NotificationCenter>();
        if (notifications is not null)
        {
            notifications.Show(NotificationKind.Error, "Ocorreu um erro inesperado",
                e.Exception.Message + "\nOs detalhes foram gravados no registro de diagnósticos local.");
        }
    }
}

/// <summary>
/// Linha de comando da interface:
/// <c>PDFMaster.exe [--theme light|dark] [--page seção] [--tool ferramenta] [arquivos e pastas…]</c>.
/// --theme define (e salva) o tema; --page abre direto numa seção; --tool abre uma ferramenta
/// (view, merge, split, reorder, interleave…) com os arquivos informados; --output define a pasta
/// de destino da ferramenta e --run executa a ação principal com as opções padrão. Sem --tool, os
/// arquivos e pastas vão para a lista de arquivos abertos.
/// </summary>
public sealed record StartupOptions(AppTheme? Theme, string? Page, string? Tool, string? Output, bool Run, IReadOnlyList<string> Files)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        AppTheme? theme = null;
        string? page = null;
        string? tool = null;
        string? output = null;
        var run = false;
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var hasValue = i + 1 < args.Count;
            if (arg.Equals("--theme", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                if (Enum.TryParse<AppTheme>(args[++i], ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                    theme = parsed;
            }
            else if (arg.Equals("--page", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                page = args[++i];
            }
            else if (arg.Equals("--tool", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                tool = args[++i].ToLowerInvariant();
            }
            else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                output = args[++i];
            }
            else if (arg.Equals("--run", StringComparison.OrdinalIgnoreCase))
            {
                run = true;
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 0)
            {
                files.Add(arg);
            }
        }

        return new StartupOptions(theme, page, tool, output, run, files);
    }
}
