using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PDFMaster.App.Services;
using PDFMaster.Core;
using PDFMaster.Core.Files;
using PDFMaster.Core.Settings;
using PDFMaster.Domain;
using PDFMaster.Infrastructure;
using PDFMaster.Infrastructure.Files;

namespace PDFMaster.App.ViewModels;

public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(FileEntry entry, Action<FileItemViewModel> open, Action<FileItemViewModel> reveal, Action<FileItemViewModel> remove)
    {
        Entry = entry;
        OpenCommand = new RelayCommand(() => open(this), () => entry.IsUsable);
        RevealCommand = new RelayCommand(() => reveal(this), () => entry.Problem != FileProblem.NotFound);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string Folder => Path.GetDirectoryName(Entry.FullPath) ?? string.Empty;
    public string SizeText => Entry.Problem == FileProblem.NotFound ? "—" : FileSizeFormatter.Format(Entry.SizeBytes);
    public bool IsUsable => Entry.IsUsable;

    public string KindText => Entry.Kind switch
    {
        FileKind.Pdf => "PDF",
        FileKind.Image => "Imagem",
        FileKind.OfficeDocument => "Office",
        FileKind.Text => "Texto",
        FileKind.Html => "HTML",
        _ => "Outro",
    };

    public string Glyph => Entry.Kind switch
    {
        FileKind.Image => "",
        FileKind.Unsupported => "",
        _ => "",
    };

    /// <summary>Aviso exibido ao lado do arquivo (problema, duplicata ou nome repetido).</summary>
    [ObservableProperty]
    private string? _note;

    public string? ProblemText => Entry.Problem switch
    {
        FileProblem.NotFound => "Arquivo não encontrado",
        FileProblem.Unreadable => "Não foi possível ler o arquivo",
        FileProblem.Empty => "Arquivo vazio",
        FileProblem.InvalidPdfHeader => "Não é um PDF válido (cabeçalho ausente)",
        FileProblem.UnsupportedType => "Tipo de arquivo não suportado",
        _ => null,
    };

    public ICommand OpenCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand RemoveCommand { get; }
}

public sealed class RecentFileViewModel
{
    public RecentFileViewModel(RecentFile file, Action<RecentFileViewModel> add, Action<RecentFileViewModel> remove)
    {
        Path = file.Path;
        Name = System.IO.Path.GetFileName(file.Path);
        Folder = System.IO.Path.GetDirectoryName(file.Path) ?? string.Empty;
        WhenText = file.LastOpenedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        AddCommand = new RelayCommand(() => add(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public string WhenText { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
}

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IFileInspector _inspector;
    private readonly FolderScanner _scanner;
    private readonly FileExportService _export;
    private readonly IRecentFilesRepository _recent;
    private readonly IHistoryRepository _history;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ShellLauncher _shell;
    private readonly NotificationCenter _notifications;
    private readonly ILogger<HomeViewModel> _logger;
    private CancellationTokenSource? _cts;

    public HomeViewModel(
        IFileInspector inspector,
        FolderScanner scanner,
        FileExportService export,
        IRecentFilesRepository recent,
        IHistoryRepository history,
        SettingsService settings,
        IDialogService dialogs,
        ShellLauncher shell,
        NotificationCenter notifications,
        ILogger<HomeViewModel> logger)
    {
        _inspector = inspector;
        _scanner = scanner;
        _export = export;
        _recent = recent;
        _history = history;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _notifications = notifications;
        _logger = logger;
    }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public ObservableCollection<HistoryRowViewModel> LastOperations { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand), nameof(AddFolderCommand), nameof(ExportCommand),
        nameof(ClearFilesCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    public bool HasFiles => Files.Count > 0;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public bool HasLastOperations => LastOperations.Count > 0;
    public bool HistoryEnabled => _settings.Current.HistoryEnabled;

    public string FilesSummary
    {
        get
        {
            var usable = Files.Count(f => f.IsUsable);
            var total = Files.Where(f => f.IsUsable).Sum(f => f.Entry.SizeBytes);
            var text = $"{Files.Count} arquivo(s), {FileSizeFormatter.Format(total)}";
            return usable == Files.Count ? text : $"{text} — {Files.Count - usable} com problema";
        }
    }

    private bool CanStart() => !IsBusy;
    private bool CanExport() => !IsBusy && Files.Any(f => f.IsUsable);
    private bool CanClear() => !IsBusy && Files.Count > 0;
    private bool CanCancel() => IsBusy;

    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(HistoryEnabled));

        var recent = await _recent.GetAsync(8);
        RecentFiles.Clear();
        foreach (var file in recent) RecentFiles.Add(new RecentFileViewModel(file, AddRecent, RemoveRecent));
        OnPropertyChanged(nameof(HasRecentFiles));

        var operations = await _history.QueryAsync(new HistoryQuery(Limit: 5));
        LastOperations.Clear();
        foreach (var record in operations) LastOperations.Add(new HistoryRowViewModel(record, _shell, _notifications));
        OnPropertyChanged(nameof(HasLastOperations));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task AddFilesAsync()
    {
        var picked = _dialogs.PickFiles(StartFolder());
        if (picked.Count > 0) await AddPathsAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task AddFolderAsync()
    {
        var folder = _dialogs.PickFolder("Importar pasta", StartFolder());
        if (folder is not null) await AddPathsAsync([folder]);
    }

    /// <summary>Adiciona arquivos e pastas (seleção, importação de pasta ou arrastar e soltar).</summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = "Lendo arquivos…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int added = 0, alreadyListed = 0, withProblem = 0, duplicates = 0;

        try
        {
            var files = await Task.Run(() => Expand(paths, ct), ct);
            IsProgressIndeterminate = false;

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = files[i];
                ProgressValue = files.Count == 0 ? 0 : (double)i / files.Count * 100;
                StatusText = $"Verificando {i + 1} de {files.Count}: {Path.GetFileName(path)}";

                if (Files.Any(f => string.Equals(f.Entry.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    alreadyListed++;
                    continue;
                }

                var entry = await _inspector.InspectAsync(path, computeHash: true, ct);
                var item = new FileItemViewModel(entry, OpenFile, RevealFile, RemoveFile);

                if (!entry.IsUsable)
                {
                    withProblem++;
                    item.Note = item.ProblemText;
                }
                else
                {
                    var twin = Files.FirstOrDefault(f => f.Entry.Sha256 is not null && f.Entry.Sha256 == entry.Sha256);
                    if (twin is not null)
                    {
                        duplicates++;
                        item.Note = $"Conteúdo idêntico a \"{twin.Name}\"";
                    }
                    else if (Files.Any(f => string.Equals(f.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        item.Note = "Mesmo nome de outro arquivo da lista (conteúdo diferente)";
                    }

                    if (_settings.Current.HistoryEnabled) await _recent.TouchAsync(entry.FullPath, DateTime.UtcNow, ct);
                }

                Files.Add(item);
                added++;
            }

            await RememberFolderAsync(paths[0]);

            var details = new List<string>();
            if (duplicates > 0) details.Add($"{duplicates} com conteúdo duplicado");
            if (withProblem > 0) details.Add($"{withProblem} com problema");
            if (alreadyListed > 0) details.Add($"{alreadyListed} já estavam na lista");
            var message = $"{added} arquivo(s) adicionado(s)" + (details.Count > 0 ? ": " + string.Join(", ", details) + "." : ".");

            if (added == 0 && alreadyListed == 0)
                _notifications.Show(NotificationKind.Warning, "Nenhum arquivo suportado encontrado", "A seleção não contém PDF, imagens ou documentos aceitos.");
            else
                _notifications.Show(withProblem > 0 ? NotificationKind.Warning : NotificationKind.Success, "Arquivos adicionados", message);
        }
        catch (OperationCanceledException)
        {
            _notifications.Show(NotificationKind.Info, "Importação cancelada", $"{added} arquivo(s) foram adicionados antes do cancelamento.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao importar arquivos");
            _notifications.Show(NotificationKind.Error, "Falha ao importar arquivos", ex.Message);
        }
        finally
        {
            FinishWork();
            await SafeRefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var sources = Files.Where(f => f.IsUsable).Select(f => f.Entry.FullPath).ToList();
        if (sources.Count == 0) return;

        var settings = _settings.Current;
        var destination = _dialogs.PickFolder("Escolha a pasta de destino", settings.DefaultOutputFolder ?? StartFolder());
        if (destination is null) return;

        if (settings.OverwritePolicy == OverwritePolicy.Overwrite &&
            !_dialogs.Confirm("Substituir arquivos existentes",
                "A configuração atual substitui arquivos de mesmo nome na pasta de destino.\n\nDeseja continuar?"))
        {
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        var options = new OutputOptions(destination, settings.OverwritePolicy, settings.SubfolderMode, FileExportService.OperationName);
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : (double)p.Completed / p.Total * 100;
            if (p.CurrentFile.Length > 0) StatusText = $"Copiando {p.Completed + 1} de {p.Total}: {p.CurrentFile}";
        });

        try
        {
            var token = _cts.Token;
            var results = await Task.Run(() => _export.CopyAsync(sources, options, progress, token));

            if (settings.HistoryEnabled)
            {
                foreach (var r in results)
                {
                    await _history.AddAsync(new HistoryRecord(0, Path.GetFileName(r.SourcePath), FileExportService.OperationName,
                        r.StartedUtc, r.Message, r.Status == OperationStatus.Completed ? r.OutputPath : null, r.Duration, r.Status));
                }
            }

            var done = results.Count(r => r.Status == OperationStatus.Completed);
            var skipped = results.Count(r => r.Status == OperationStatus.Skipped);
            var failed = results.Count(r => r.Status == OperationStatus.Failed);
            var cancelled = results.Count(r => r.Status == OperationStatus.Cancelled);
            var bytes = results.Where(r => r.Status == OperationStatus.Completed).Sum(r => r.Bytes);

            var parts = new List<string> { $"{done} copiado(s) ({FileSizeFormatter.Format(bytes)})" };
            if (skipped > 0) parts.Add($"{skipped} ignorado(s)");
            if (failed > 0) parts.Add($"{failed} com falha");
            if (cancelled > 0) parts.Add($"{cancelled} cancelado(s)");

            var kind = failed > 0 ? NotificationKind.Error
                : cancelled > 0 || skipped > 0 ? NotificationKind.Warning
                : NotificationKind.Success;
            var title = cancelled > 0 ? "Cópia cancelada" : failed > 0 ? "Cópia concluída com falhas" : "Cópia concluída";
            var firstFailure = results.FirstOrDefault(r => r.Status == OperationStatus.Failed);
            var message = string.Join(", ", parts) + "." + (firstFailure is null ? string.Empty : $"\nPrimeira falha: {firstFailure.Message}");
            var firstOutput = results.FirstOrDefault(r => r.Status == OperationStatus.Completed)?.OutputPath;

            _notifications.Show(kind, title, message,
                new NotificationAction("Abrir pasta", () => _shell.OpenFolder(firstOutput ?? destination)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao copiar arquivos");
            _notifications.Show(NotificationKind.Error, "Falha ao copiar arquivos", ex.Message);
        }
        finally
        {
            FinishWork();
            await SafeRefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelando…";
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearFiles()
    {
        Files.Clear();
        FilesChanged();
    }

    [RelayCommand]
    private async Task ClearRecentAsync()
    {
        try
        {
            await _recent.ClearAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar arquivos recentes");
            _notifications.Show(NotificationKind.Error, "Falha ao limpar os arquivos recentes", ex.Message);
        }
    }

    private void OpenFile(FileItemViewModel item)
    {
        if (!_shell.OpenDocument(item.Entry.FullPath))
            _notifications.Show(NotificationKind.Warning, "Não foi possível abrir o arquivo", item.Entry.FullPath);
    }

    private void RevealFile(FileItemViewModel item)
    {
        if (!_shell.OpenFolder(item.Entry.FullPath))
            _notifications.Show(NotificationKind.Warning, "Não foi possível abrir a pasta", item.Folder);
    }

    private void RemoveFile(FileItemViewModel item)
    {
        if (IsBusy) return;
        Files.Remove(item);
        FilesChanged();
    }

    private async void AddRecent(RecentFileViewModel item) => await AddPathsAsync([item.Path]);

    private async void RemoveRecent(RecentFileViewModel item)
    {
        try
        {
            await _recent.RemoveAsync(item.Path);
            RecentFiles.Remove(item);
            OnPropertyChanged(nameof(HasRecentFiles));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remover arquivo recente");
        }
    }

    private IReadOnlyList<string> Expand(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (var file in _scanner.FindSupportedFiles(path, recursive: true, ct))
                {
                    if (seen.Add(file)) result.Add(file);
                }
            }
            else
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full)) result.Add(full);
            }
        }

        return result;
    }

    private string? StartFolder()
    {
        var s = _settings.Current;
        return s.RememberLastFolder && !string.IsNullOrEmpty(s.LastFolder) ? s.LastFolder : s.DefaultOutputFolder;
    }

    private async Task RememberFolderAsync(string firstPath)
    {
        if (!_settings.Current.RememberLastFolder) return;
        var folder = Directory.Exists(firstPath) ? firstPath : Path.GetDirectoryName(firstPath);
        if (string.IsNullOrEmpty(folder) || folder == _settings.Current.LastFolder) return;
        await _settings.UpdateAsync(s => s.LastFolder = folder);
    }

    private void FinishWork()
    {
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        StatusText = string.Empty;
        FilesChanged();
    }

    private void FilesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FilesSummary));
        ExportCommand.NotifyCanExecuteChanged();
        ClearFilesCommand.NotifyCanExecuteChanged();
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao atualizar a página inicial");
        }
    }
}
