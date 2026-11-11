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
    private readonly ToolContext _toolContext;
    private readonly Dictionary<string, ToolViewModelBase> _tools = new(StringComparer.Ordinal);
    private NavItem? _lastNav;
    private bool _suppressSearchChange;
    private bool _suppressNavChange;

    public MainViewModel(
        ToolCatalog catalog,
        ToolSearchService search,
        SettingsService settings,
        NotificationCenter notifications,
        HomeViewModel home,
        HistoryViewModel history,
        SettingsViewModel settingsPage,
        ToolContext toolContext,
        ILogger<MainViewModel> logger)
    {
        _toolContext = toolContext;
        _catalog = catalog;
        _search = search;
        _settings = settings;
        _home = home;
        _history = history;
        _settingsPage = settingsPage;
        _logger = logger;
        Notifications = notifications;
        _home.OpenToolRequested = (id, files) => OpenTool(id, files);

        NavItems =
        [
            new NavItem("home", "Início", ""),
            new NavItem("organize", "Organizar PDF", "", ToolCategory.Organize),
            new NavItem("edit", "Editar PDF", "", ToolCategory.Edit),
            new NavItem("convert", "Converter PDF", "", ToolCategory.Convert),
            new NavItem("compress", "Comprimir PDF", "", ToolCategory.Compress),
            new NavItem("video", "Comprimir vídeo", "\uE714", ToolCategory.Video),
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

    /// <summary>Crédito do autor, mostrado discretamente no rodapé do menu lateral.</summary>
    public string AuthorText => AppInfo.Credit;

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
        if (value is null || _suppressNavChange) return;

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
            showCategory: true,
            OpenTool);
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
            case "video":
                // A aba de vídeo tem uma só ferramenta: abre direto a tela de compressão.
                return GetToolViewModel("video-compress");
            default:
                var category = item.Category ?? ToolCategory.Organize;
                var tools = _catalog.ByCategory(category);
                return new ToolsPageViewModel(
                    item.Title,
                    "Clique em uma ferramenta disponível para abri-la. As demais mostram a fase em que serão entregues.",
                    tools,
                    showCategory: false,
                    OpenTool);
        }
    }

    // ---------- Ferramentas ----------

    private void OpenTool(ToolDefinition tool) => OpenTool(tool.Id, null);

    /// <summary>
    /// Abre uma ferramenta implementada. Sem arquivos informados, a ferramenta recebe os PDFs
    /// que estão na lista de arquivos abertos da página inicial (se ela ainda estiver vazia).
    /// </summary>
    public async void OpenTool(string toolId, IReadOnlyList<string>? files) => await OpenToolAsync(toolId, files);

    /// <summary>Como <see cref="OpenTool(string, IReadOnlyList{string})"/>, devolvendo a tela aberta (ou null).</summary>
    public async Task<ToolViewModelBase?> OpenToolAsync(string toolId, IReadOnlyList<string>? files)
    {
        try
        {
            var tool = _catalog.Find(toolId);
            if (tool is null || !tool.IsAvailable)
            {
                Notifications.Show(NotificationKind.Warning, "Ferramenta indisponível",
                    tool is null ? $"\"{toolId}\" não existe." : $"{tool.Name} está prevista para a Fase {tool.PlannedPhase}.");
                return null;
            }

            var viewModel = GetToolViewModel(toolId);

            if (SearchText.Length > 0)
            {
                _suppressSearchChange = true;
                SearchText = string.Empty;
                _suppressSearchChange = false;
            }

            var nav = NavItems.FirstOrDefault(n => n.Category == tool.Category);
            if (nav is not null)
            {
                _suppressNavChange = true;
                SelectedNav = nav;
                _suppressNavChange = false;
                _lastNav = nav;
            }

            CurrentPage = viewModel;

            IReadOnlyList<string> input = files is { Count: > 0 } ? files
                : viewModel.IsEmpty && viewModel.AcceptsOpenPdfs ? _home.OpenPdfPaths : [];
            if (input.Count > 0) await viewModel.AddFilesAsync(input);
            return viewModel;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao abrir a ferramenta {Tool}", toolId);
            Notifications.Show(NotificationKind.Error, "Não foi possível abrir a ferramenta", ex.Message);
            return null;
        }
    }

    /// <summary>Uma instância por tela, mantida enquanto o programa está aberto (o trabalho não se perde ao navegar).</summary>
    private ToolViewModelBase GetToolViewModel(string toolId)
    {
        var key = toolId switch
        {
            "merge" or "interleave" => "combine",
            "split" => "split",
            "view" => "viewer",
            "compress" => "compress",
            "ocr" or "ocr-batch" => "ocr",
            "scan" => "scan",
            "video-compress" => "video",
            "edit-text" or "edit-images" or "annotate" or "sign-visual" or "stamps" or "form-fields" => "editor",
            "watermark" => "watermark",
            "header-footer" => "header-footer",
            "forms" => "forms",
            "protect" or "unlock" or "security-info" or "metadata" or "verify-signature" or "repair" or "pdfa" or "compare" => toolId,
            "pdf-to-image" or "image-to-pdf" or "office-to-pdf" or "text-html-to-pdf" or "pdf-to-word" or "pdf-to-text" => toolId,
            _ => "pages",
        };

        if (!_tools.TryGetValue(key, out var viewModel))
        {
            viewModel = key switch
            {
                "combine" => new CombineViewModel(_toolContext),
                "split" => new SplitViewModel(_toolContext),
                "compress" => new CompressViewModel(_toolContext),
                "ocr" => new OcrViewModel(_toolContext),
                "scan" => new ScanViewModel(_toolContext),
                "video" => new VideoViewModel(_toolContext),
                "editor" => new EditorViewModel(_toolContext),
                "watermark" => new WatermarkViewModel(_toolContext),
                "header-footer" => new HeaderFooterViewModel(_toolContext),
                "forms" => new FormFillViewModel(_toolContext) { OpenToolRequested = (id, path) => OpenTool(id, [path]) },
                "protect" => new ProtectViewModel(_toolContext),
                "unlock" => new UnlockViewModel(_toolContext),
                "security-info" => new SecurityInfoViewModel(_toolContext),
                "metadata" => new MetadataViewModel(_toolContext),
                "verify-signature" => new SignatureViewModel(_toolContext),
                "repair" => new RepairViewModel(_toolContext),
                "pdfa" => new PdfAViewModel(_toolContext),
                "compare" => new CompareViewModel(_toolContext),
                "pdf-to-image" => new ConvertViewModel(_toolContext, ConversionKind.PdfToImage),
                "image-to-pdf" => new ConvertViewModel(_toolContext, ConversionKind.ImageToPdf),
                "office-to-pdf" => new ConvertViewModel(_toolContext, ConversionKind.OfficeToPdf),
                "text-html-to-pdf" => new ConvertViewModel(_toolContext, ConversionKind.TextHtmlToPdf),
                "pdf-to-word" => new ConvertViewModel(_toolContext, ConversionKind.PdfToWord),
                "pdf-to-text" => new ConvertViewModel(_toolContext, ConversionKind.PdfToText),
                "viewer" => new ViewerViewModel(_toolContext) { OpenToolRequested = (id, path) => OpenTool(id, [path]) },
                _ => new PageEditorViewModel(_toolContext),
            };
            viewModel.CloseRequested = CloseTool;
            _tools[key] = viewModel;
        }

        if (viewModel is CombineViewModel combine) combine.SetMode(interleave: toolId == "interleave");
        if (viewModel is EditorViewModel editor) editor.UseCatalogTool(toolId);
        return viewModel;
    }

    private void CloseTool()
    {
        var nav = SelectedNav ?? _lastNav ?? NavItems[0];
        // A aba de vídeo é a própria ferramenta: "voltar" leva para o início.
        if (nav.Key == "video") Navigate("home");
        else CurrentPage = CreatePage(nav);
    }

    /// <summary>Clique em um item do menu que já estava selecionado: volta da ferramenta para a lista.</summary>
    public void RefreshCurrentNav()
    {
        if (SelectedNav is not null && SelectedNav.Key != "video" && CurrentPage is ToolViewModelBase) CurrentPage = CreatePage(SelectedNav);
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
