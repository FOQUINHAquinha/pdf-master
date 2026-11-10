using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PDFMaster.App.Services;
using PDFMaster.Core.Settings;
using PDFMaster.Core.Tools;
using PDFMaster.Domain;

namespace PDFMaster.App.ViewModels;

public sealed record NavItem(string Key, string Title, string Glyph, ToolCategory? Category = null);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly ToolSearchService _search;
    private readonly SettingsService _settings;
    private readonly HomeViewModel _home;
    private readonly HistoryViewModel _history;
    private readonly SettingsViewModel _settingsPage;
    private readonly ILogger<MainViewModel> _logger;
    private NavItem? _lastNav;
    private bool _suppressSearchChange;

    public MainViewModel(
        ToolCatalog catalog,
        ToolSearchService search,
        SettingsService settings,
        NotificationCenter notifications,
        HomeViewModel home,
        HistoryViewModel history,
        SettingsViewModel settingsPage,
        ILogger<MainViewModel> logger)
    {
        _catalog = catalog;
        _search = search;
        _settings = settings;
        _home = home;
        _history = history;
        _settingsPage = settingsPage;
        _logger = logger;
        Notifications = notifications;

        NavItems =
        [
            new NavItem("home", "Início", ""),
            new NavItem("organize", "Organizar PDF", "", ToolCategory.Organize),
            new NavItem("edit", "Editar PDF", "", ToolCategory.Edit),
            new NavItem("convert", "Converter PDF", "", ToolCategory.Convert),
            new NavItem("compress", "Comprimir PDF", "", ToolCategory.Compress),
            new NavItem("security", "Segurança", "", ToolCategory.Security),
            new NavItem("ocr", "OCR e digitalização", "", ToolCategory.Ocr),
            new NavItem("compare", "Comparar documentos", "", ToolCategory.Compare),
            new NavItem("batch", "Processamento em lote", "", ToolCategory.Batch),
            new NavItem("history", "Histórico", ""),
            new NavItem("settings", "Configurações", ""),
        ];

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        VersionText = "v" + version;

        IsDarkTheme = settings.Current.Theme == AppTheme.Dark;
        settings.Changed += (_, _) => IsDarkTheme = _settings.Current.Theme == AppTheme.Dark;
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public NotificationCenter Notifications { get; }
    public string VersionText { get; }

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isDarkTheme;

    public string ThemeGlyph => IsDarkTheme ? "" : "";
    public string ThemeTooltip => IsDarkTheme ? "Mudar para o tema claro" : "Mudar para o tema escuro";

    /// <summary>Abre a seção pedida (ex.: "history"); sem argumento, abre o Início.</summary>
    public void Navigate(string? key)
    {
        SelectedNav = NavItems.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase))
                      ?? NavItems[0];
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeTooltip));
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;

        if (SearchText.Length > 0)
        {
            _suppressSearchChange = true;
            SearchText = string.Empty;
            _suppressSearchChange = false;
        }

        _lastNav = value;
        CurrentPage = CreatePage(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearchChange) return;

        if (string.IsNullOrWhiteSpace(value))
        {
            var target = _lastNav ?? NavItems[0];
            if (SelectedNav is null) SelectedNav = target;
            else CurrentPage = CreatePage(target);
            return;
        }

        SelectedNav = null;
        var results = _search.Search(value);
        CurrentPage = new ToolsPageViewModel(
            "Resultados da pesquisa",
            results.Count == 0
                ? $"Nenhuma ferramenta encontrada para \"{value.Trim()}\"."
                : $"{results.Count} ferramenta(s) para \"{value.Trim()}\".",
            results,
            showCategory: true);
    }

    private object CreatePage(NavItem item)
    {
        switch (item.Key)
        {
            case "home":
                Run(_home.RefreshAsync());
                return _home;
            case "history":
                Run(_history.RefreshAsync());
                return _history;
            case "settings":
                _settingsPage.Reload();
                return _settingsPage;
            default:
                var category = item.Category ?? ToolCategory.Organize;
                var tools = _catalog.ByCategory(category);
                return new ToolsPageViewModel(
                    item.Title,
                    "Ferramentas desta categoria e a fase em que cada uma será entregue.",
                    tools,
                    showCategory: false);
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void OpenSettings() => Navigate("settings");

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        try
        {
            var next = _settings.Current.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            await _settings.UpdateAsync(s => s.Theme = next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao alternar o tema");
            Notifications.Show(NotificationKind.Error, "Não foi possível salvar o tema", ex.Message);
        }
    }

    private async void Run(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar a página");
            Notifications.Show(NotificationKind.Error, "Falha ao carregar os dados", ex.Message);
        }
    }
}
