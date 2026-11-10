using System.Windows;
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
    private AppTheme? _themeOverride;

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
            _themeOverride = options.Theme;

            var theme = _services.GetRequiredService<ThemeService>();
            theme.Apply(_themeOverride ?? settings.Current.Theme, settings.Current.AccentColor);
            settings.Changed += (_, _) =>
            {
                _themeOverride = null;
                theme.Apply(settings.Current.Theme, settings.Current.AccentColor);
            };

            var window = _services.GetRequiredService<MainWindow>();
            var main = _services.GetRequiredService<MainViewModel>();
            window.DataContext = main;
            MainWindow = window;
            main.Navigate(options.Page);
            window.Show();

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

/// <summary>Opções de linha de comando da interface: --theme light|dark e --page &lt;seção&gt;.</summary>
public sealed record StartupOptions(AppTheme? Theme, string? Page)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        AppTheme? theme = null;
        string? page = null;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals("--theme", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<AppTheme>(args[i + 1], ignoreCase: true, out var parsed))
            {
                theme = parsed;
            }
            else if (args[i].Equals("--page", StringComparison.OrdinalIgnoreCase))
            {
                page = args[i + 1];
            }
        }

        return new StartupOptions(theme, page);
    }
}
