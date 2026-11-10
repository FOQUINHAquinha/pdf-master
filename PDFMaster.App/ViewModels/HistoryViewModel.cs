using System.Collections.ObjectModel;
using System.IO;
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

public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(HistoryRecord record, ShellLauncher shell, NotificationCenter notifications,
        Action<HistoryRowViewModel>? delete = null)
    {
        Record = record;
        var hasOutput = !string.IsNullOrEmpty(record.OutputPath);

        OpenFileCommand = new RelayCommand(() =>
        {
            if (!shell.OpenDocument(record.OutputPath!))
                notifications.Show(NotificationKind.Warning, "Arquivo não encontrado",
                    "O arquivo gerado não existe mais neste local:\n" + record.OutputPath);
        }, () => hasOutput);

        OpenFolderCommand = new RelayCommand(() =>
        {
            if (!shell.OpenFolder(record.OutputPath!))
                notifications.Show(NotificationKind.Warning, "Pasta não encontrada",
                    "A pasta de destino não existe mais:\n" + Path.GetDirectoryName(record.OutputPath));
        }, () => hasOutput);

        DeleteCommand = new RelayCommand(() => delete?.Invoke(this), () => delete is not null);
    }

    public HistoryRecord Record { get; }
    public string FileName => Record.FileName;
    public string Operation => Record.Operation;
    public string Result => Record.Result;
    public string OutputPath => Record.OutputPath ?? string.Empty;
    public string WhenText => Record.StartedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    public string DurationText => Record.Duration.TotalSeconds < 1
        ? $"{Record.Duration.TotalMilliseconds:0} ms"
        : Record.Duration.TotalMinutes < 1
            ? $"{Record.Duration.TotalSeconds:0.0} s"
            : $"{(int)Record.Duration.TotalMinutes} min {Record.Duration.Seconds} s";

    public string StatusText => Record.Status switch
    {
        OperationStatus.Completed => "Concluído",
        OperationStatus.Failed => "Falhou",
        OperationStatus.Cancelled => "Cancelado",
        OperationStatus.Skipped => "Ignorado",
        _ => Record.Status.ToString(),
    };

    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DeleteCommand { get; }
}

public sealed partial class HistoryViewModel : ObservableObject
{
    public const string AllOperations = "Todas as operações";

    private readonly IHistoryRepository _history;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ShellLauncher _shell;
    private readonly NotificationCenter _notifications;
    private readonly ILogger<HistoryViewModel> _logger;
    private bool _loading;

    public HistoryViewModel(
        IHistoryRepository history,
        SettingsService settings,
        IDialogService dialogs,
        ShellLauncher shell,
        NotificationCenter notifications,
        ILogger<HistoryViewModel> logger)
    {
        _history = history;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _notifications = notifications;
        _logger = logger;
    }

    public ObservableCollection<HistoryRowViewModel> Records { get; } = [];
    public ObservableCollection<string> Operations { get; } = [AllOperations];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedOperation = AllOperations;

    [ObservableProperty]
    private DateTime? _fromDate;

    [ObservableProperty]
    private DateTime? _toDate;

    public bool HasRecords => Records.Count > 0;
    public bool HistoryEnabled => _settings.Current.HistoryEnabled;

    public string EmptyText => !HistoryEnabled
        ? "O histórico está desativado nas Configurações. Nenhuma operação está sendo registrada."
        : HasFilters
            ? "Nenhuma operação corresponde aos filtros."
            : "Nenhuma operação registrada ainda.";

    private bool HasFilters => SearchText.Length > 0 || SelectedOperation != AllOperations || FromDate is not null || ToDate is not null;

    partial void OnSearchTextChanged(string value) => Requery();
    partial void OnSelectedOperationChanged(string value) => Requery();
    partial void OnFromDateChanged(DateTime? value) => Requery();
    partial void OnToDateChanged(DateTime? value) => Requery();

    public async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var names = await _history.GetOperationNamesAsync();
            var selected = SelectedOperation;
            Operations.Clear();
            Operations.Add(AllOperations);
            foreach (var name in names) Operations.Add(name);
            SelectedOperation = Operations.Contains(selected) ? selected : AllOperations;
        }
        finally
        {
            _loading = false;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Datas escolhidas na interface são locais; o banco guarda UTC.
        DateTime? from = FromDate?.Date.ToUniversalTime();
        DateTime? to = ToDate?.Date.AddDays(1).ToUniversalTime();
        var query = new HistoryQuery(
            SearchText.Length > 0 ? SearchText : null,
            SelectedOperation == AllOperations ? null : SelectedOperation,
            from,
            to);

        var records = await _history.QueryAsync(query);
        Records.Clear();
        foreach (var record in records) Records.Add(new HistoryRowViewModel(record, _shell, _notifications, Delete));
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(HistoryEnabled));
        OnPropertyChanged(nameof(EmptyText));
        ClearAllCommand.NotifyCanExecuteChanged();
    }

    private async void Requery()
    {
        if (_loading) return;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao consultar o histórico");
            _notifications.Show(NotificationKind.Error, "Falha ao consultar o histórico", ex.Message);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _loading = true;
        SearchText = string.Empty;
        SelectedOperation = AllOperations;
        FromDate = null;
        ToDate = null;
        _loading = false;
        Requery();
    }

    private async void Delete(HistoryRowViewModel row)
    {
        try
        {
            await _history.DeleteAsync(row.Record.Id);
            Records.Remove(row);
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(EmptyText));
            ClearAllCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao excluir registro do histórico");
            _notifications.Show(NotificationKind.Error, "Falha ao excluir o registro", ex.Message);
        }
    }

    private bool CanClearAll() => Records.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private async Task ClearAllAsync()
    {
        if (!_dialogs.Confirm("Limpar histórico",
                "Todos os registros do histórico serão apagados. Os arquivos gerados não são excluídos.\n\nDeseja continuar?"))
        {
            return;
        }

        try
        {
            await _history.ClearAsync();
            await RefreshAsync();
            _notifications.Show(NotificationKind.Success, "Histórico limpo", "Todos os registros foram apagados.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar o histórico");
            _notifications.Show(NotificationKind.Error, "Falha ao limpar o histórico", ex.Message);
        }
    }
}
