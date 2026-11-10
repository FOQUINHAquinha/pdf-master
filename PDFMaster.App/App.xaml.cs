using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PDFMaster.App.Services;
using PDFMaster.App.ViewModels;
using PDFMaster.Core;
using PDFMaster.Core.Files;
using PDFMaster.Core.Settings;
using PDFMaster.Core.Tools;
using PDFMaster.Domain;
using PDFMaster.Infrastructure;
using PDFMaster.Infrastructure.Files;
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
            main.Navigate(options.Files.Count > 0 ? "home" : options.Page);
            window.Show();

            // Arquivos e pastas passados na linha de comando ("Abrir com PDF Master").
            if (options.Files.Count > 0)
            {
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
/// Linha de comando da interface: <c>PDFMaster.exe [--theme light|dark] [--page seção] [arquivos e pastas…]</c>.
/// --theme define (e salva) o tema; --page abre direto numa seção; os demais argumentos são
/// arquivos ou pastas adicionados à lista de arquivos abertos.
/// </summary>
public sealed record StartupOptions(AppTheme? Theme, string? Page, IReadOnlyList<string> Files)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        AppTheme? theme = null;
        string? page = null;
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
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 0)
            {
                files.Add(arg);
            }
        }

        return new StartupOptions(theme, page, files);
    }
}
