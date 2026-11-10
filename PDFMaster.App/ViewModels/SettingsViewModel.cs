using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PDFMaster.App.Services;
using PDFMaster.Core;
using PDFMaster.Core.Settings;
using PDFMaster.Domain;
using PDFMaster.Infrastructure;

namespace PDFMaster.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public sealed class AccentSwatch
{
    public AccentSwatch(string hex, Action<string> select)
    {
        Hex = hex;
        SelectCommand = new RelayCommand(() => select(hex));
    }

    public string Hex { get; }
    public ICommand SelectCommand { get; }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IHistoryRepository _history;
    private readonly IRecentFilesRepository _recent;
    private readonly IDialogService _dialogs;
    private readonly ShellLauncher _shell;
    private readonly NotificationCenter _notifications;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public SettingsViewModel(
        SettingsService settings,
        IHistoryRepository history,
        IRecentFilesRepository recent,
        IDialogService dialogs,
        ShellLauncher shell,
        NotificationCenter notifications,
        AppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _history = history;
        _recent = recent;
        _dialogs = dialogs;
        _shell = shell;
        _notifications = notifications;
        _logger = logger;

        DataDirectory = paths.DataDirectory;
        LogsDirectory = paths.LogsDirectory;
        IsPortable = paths.IsPortable;

        Swatches = ThemeService.AccentPresets.Select(hex => new AccentSwatch(hex, SetAccent)).ToList();
        OverwritePolicies =
        [
            new(OverwritePolicy.AutoRename, "Criar um nome novo — \"arquivo (2).pdf\" (recomendado)"),
            new(OverwritePolicy.Skip, "Manter o arquivo existente e pular o novo"),
            new(OverwritePolicy.Overwrite, "Substituir o arquivo existente (pede confirmação)"),
        ];
        SubfolderModes =
        [
            new(SubfolderMode.None, "Gravar direto na pasta de destino"),
            new(SubfolderMode.ByDate, "Criar subpasta por data (2026-01-31)"),
            new(SubfolderMode.ByOperation, "Criar subpasta por operação"),
        ];

        _selectedOverwritePolicy = OverwritePolicies[0];
        _selectedSubfolderMode = SubfolderModes[0];
        _settings.Changed += (_, _) => Reload();
        Reload();
    }

    public IReadOnlyList<AccentSwatch> Swatches { get; }
    public IReadOnlyList<Choice<OverwritePolicy>> OverwritePolicies { get; }
    public IReadOnlyList<Choice<SubfolderMode>> SubfolderModes { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public bool IsPortable { get; }

    [ObservableProperty]
    private bool _isLightTheme;

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private string _accentText = string.Empty;

    [ObservableProperty]
    private string _accentStatus = string.Empty;

    [ObservableProperty]
    private string _defaultFolder = string.Empty;

    [ObservableProperty]
    private bool _rememberLastFolder;

    [ObservableProperty]
    private Choice<OverwritePolicy> _selectedOverwritePolicy;

    [ObservableProperty]
    private Choice<SubfolderMode> _selectedSubfolderMode;

    [ObservableProperty]
    private bool _historyEnabled;

    public bool HasDefaultFolder => DefaultFolder.Length > 0;

    /// <summary>Copia os valores salvos para a tela, sem regravar.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            IsDarkTheme = s.Theme == AppTheme.Dark;
            IsLightTheme = s.Theme == AppTheme.Light;
            AccentText = s.AccentColor ?? string.Empty;
            AccentStatus = s.AccentColor is null ? "Usando a cor padrão do tema." : $"Cor personalizada: {s.AccentColor}";
            DefaultFolder = s.DefaultOutputFolder ?? string.Empty;
            RememberLastFolder = s.RememberLastFolder;
            SelectedOverwritePolicy = OverwritePolicies.First(c => c.Value == s.OverwritePolicy);
            SelectedSubfolderMode = SubfolderModes.First(c => c.Value == s.SubfolderMode);
            HistoryEnabled = s.HistoryEnabled;
            OnPropertyChanged(nameof(HasDefaultFolder));
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (value) Save(s => s.Theme = AppTheme.Dark);
    }

    partial void OnIsLightThemeChanged(bool value)
    {
        if (value) Save(s => s.Theme = AppTheme.Light);
    }

    partial void OnRememberLastFolderChanged(bool value) => Save(s => s.RememberLastFolder = value);

    partial void OnSelectedOverwritePolicyChanged(Choice<OverwritePolicy> value)
    {
        if (value is not null) Save(s => s.OverwritePolicy = value.Value);
    }

    partial void OnSelectedSubfolderModeChanged(Choice<SubfolderMode> value)
    {
        if (value is not null) Save(s => s.SubfolderMode = value.Value);
    }

    partial void OnHistoryEnabledChanged(bool value) => Save(s => s.HistoryEnabled = value);

    private void SetAccent(string hex) => Save(s => s.AccentColor = hex);

    [RelayCommand]
    private void ApplyAccent()
    {
        var text = AccentText.Trim();
        if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
        if (!SettingsService.IsValidAccent(text))
        {
            AccentStatus = "Cor inválida. Use o formato #RRGGBB, por exemplo #008B50.";
            return;
        }

        Save(s => s.AccentColor = text.ToUpperInvariant());
    }

    [RelayCommand]
    private void ResetAccent() => Save(s => s.AccentColor = null);

    [RelayCommand]
    private void BrowseDefaultFolder()
    {
        var folder = _dialogs.PickFolder("Pasta padrão para os resultados", HasDefaultFolder ? DefaultFolder : null);
        if (folder is not null) Save(s => s.DefaultOutputFolder = folder);
    }

    [RelayCommand]
    private void ClearDefaultFolder() => Save(s => s.DefaultOutputFolder = null);

    [RelayCommand]
    private void OpenLogs()
    {
        if (!_shell.OpenFolder(LogsDirectory))
            _notifications.Show(NotificationKind.Warning, "Não foi possível abrir a pasta de diagnósticos", LogsDirectory);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!_dialogs.Confirm("Limpar histórico",
                "Todos os registros do histórico serão apagados. Os arquivos gerados não são excluídos.\n\nDeseja continuar?"))
        {
            return;
        }

        await RunAsync(() => _history.ClearAsync(), "Histórico limpo", "Todos os registros foram apagados.");
    }

    [RelayCommand]
    private async Task ClearRecentAsync() =>
        await RunAsync(() => _recent.ClearAsync(), "Arquivos recentes limpos", "A lista de arquivos recentes foi apagada.");

    private async Task RunAsync(Func<Task> action, string title, string message)
    {
        try
        {
            await action();
            _notifications.Show(NotificationKind.Success, title, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha em ação das configurações");
            _notifications.Show(NotificationKind.Error, "A ação falhou", ex.Message);
        }
    }

    private async void Save(Action<AppSettings> change)
    {
        if (_loading) return;
        try
        {
            await _settings.UpdateAsync(change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar configurações");
            _notifications.Show(NotificationKind.Error, "Não foi possível salvar as configurações", ex.Message);
        }
    }
}
