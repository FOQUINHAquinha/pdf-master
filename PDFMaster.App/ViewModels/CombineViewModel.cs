using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

public sealed partial class CombineFileViewModel : ObservableObject
{
    private readonly Action _changed;

    public CombineFileViewModel(PdfDocumentInfo info, Action changed,
        Action<CombineFileViewModel, int> move, Action<CombineFileViewModel> remove)
    {
        Info = info;
        _changed = changed;
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, 1));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public PdfDocumentInfo Info { get; }
    public string Path => Info.Path;
    public string Name => System.IO.Path.GetFileName(Info.Path);
    public string Details => $"{Info.PageCount} página(s) · {FileSizeFormatter.Format(Info.SizeBytes)}";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>Páginas a usar na mesclagem; vazio = todas.</summary>
    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    private string? _rangeError;

    /// <summary>Na intercalação, lê o documento de trás para frente.</summary>
    [ObservableProperty]
    private bool _reverse;

    public bool IsValid => RangeError is null;

    /// <summary>Quantidade de páginas selecionadas pelo intervalo (ou todas).</summary>
    public int SelectedPageCount { get; private set; }

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }

    public void Validate()
    {
        if (PageRangeParser.TryParse(RangeText, Info.PageCount, out var pages, out var error))
        {
            SelectedPageCount = pages.Count;
            RangeError = null;
        }
        else
        {
            SelectedPageCount = 0;
            RangeError = error;
        }

        OnPropertyChanged(nameof(IsValid));
    }

    partial void OnRangeTextChanged(string value)
    {
        Validate();
        _changed();
    }

    partial void OnReverseChanged(bool value) => _changed();
}

/// <summary>Mesclar PDF e Intercalar documentos (a mesma lista de arquivos, duas formas de combinar).</summary>
public sealed partial class CombineViewModel : ToolViewModelBase
{
    public CombineViewModel(ToolContext context)
        : base(context, "Mesclar PDF", "Junte vários arquivos PDF em um único documento, na ordem da lista.")
    {
    }

    public ObservableCollection<CombineFileViewModel> Files { get; } = [];

    [ObservableProperty]
    private bool _isInterleave;

    [ObservableProperty]
    private bool _isMerge = true;

    [ObservableProperty]
    private bool _bookmarkPerFile = true;

    [ObservableProperty]
    private string _pagesPerTurnText = "1";

    [ObservableProperty]
    private bool _stopAtShortest;

    [ObservableProperty]
    private string _fileName = "mesclado";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _problem;

    public override bool IsEmpty => Files.Count == 0;
    protected internal override IReadOnlyList<string> RepeatInputs => Files.Select(f => f.Path).ToList();
    public bool HasFiles => Files.Count > 0;
    public string RunLabel => IsInterleave ? "Intercalar" : "Mesclar";

    /// <summary>Escolhe o modo ao abrir a ferramenta pelo menu.</summary>
    public void SetMode(bool interleave)
    {
        IsInterleave = interleave;
        IsMerge = !interleave;
    }

    partial void OnIsInterleaveChanged(bool value)
    {
        if (value == IsMerge) IsMerge = !value;
        Title = value ? "Intercalar documentos" : "Mesclar PDF";
        Subtitle = value
            ? "Alterne as páginas de dois ou mais PDFs: A1, B1, A2, B2…"
            : "Junte vários arquivos PDF em um único documento, na ordem da lista.";
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(RunLabel));
        if (FileName is "mesclado" or "intercalado") FileName = value ? "intercalado" : "mesclado";
        Refresh();
    }

    partial void OnIsMergeChanged(bool value)
    {
        if (value == IsInterleave) IsInterleave = !value;
    }

    partial void OnPagesPerTurnTextChanged(string value) => Refresh();
    partial void OnStopAtShortestChanged(bool value) => Refresh();
    partial void OnFileNameChanged(string value) => Refresh();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PickFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanClear() => !IsBusy && Files.Count > 0;
    private bool CanRun() => !IsBusy && Problem is null && Files.Count >= (IsInterleave ? 2 : 1);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFilesAsync()
    {
        var picked = PickPdfs(multiple: true);
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    public override async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count == 0)
        {
            if (paths.Count > 0)
                Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "Esta ferramenta trabalha apenas com arquivos PDF.");
            return;
        }

        await WhileBusyAsync("Lendo documentos…", async () =>
        {
            foreach (var path in pdfs)
            {
                var info = await TryReadInfoAsync(path);
                if (info is null) continue;

                var item = new CombineFileViewModel(info, Refresh, Move, Remove);
                item.Validate();
                Files.Add(item);
                SuggestOutputFolder(info.Path);
                LoadThumbnail(item);
            }
        });

        Refresh();
    }

    private async void LoadThumbnail(CombineFileViewModel item)
    {
        try
        {
            await Context.Thumbnails.LoadAsync(item.Path, [0], (_, image) => item.Thumbnail = image, CancellationToken.None);
        }
        catch (Exception)
        {
            // Sem miniatura; o arquivo continua utilizável e erros de leitura aparecem ao executar.
        }
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        Refresh();
    }

    private void Move(CombineFileViewModel item, int delta)
    {
        if (IsBusy) return;
        var index = Files.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Files.Count) return;
        Files.Move(index, target);
        Refresh();
    }

    private void Remove(CombineFileViewModel item)
    {
        if (IsBusy) return;
        Files.Remove(item);
        Refresh();
    }

    private bool TryGetPagesPerTurn(out int value) =>
        int.TryParse(PagesPerTurnText.Trim(), out value) && value is >= 1 and <= 1000;

    /// <summary>Recalcula o resumo e os problemas que impedem a execução.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(IsEmpty));

        string? problem = null;
        var summary = string.Empty;

        if (Files.Count == 0)
        {
            summary = "Adicione arquivos PDF para começar.";
        }
        else if (string.IsNullOrWhiteSpace(FileName))
        {
            problem = "Informe o nome do arquivo final.";
        }
        else if (IsInterleave)
        {
            if (Files.Count < 2)
            {
                problem = "A intercalação precisa de pelo menos dois documentos.";
            }
            else if (!TryGetPagesPerTurn(out var perTurn))
            {
                problem = "Informe quantas páginas pegar de cada documento por vez (1 ou mais).";
            }
            else
            {
                var total = InterleavePlanner.Plan(
                    Files.Select(f => new InterleaveSource(f.Info.PageCount, f.Reverse)).ToList(),
                    perTurn,
                    StopAtShortest ? InterleaveRemainder.StopAtShortest : InterleaveRemainder.AppendRemaining).Count;
                summary = $"{Files.Count} documentos intercalados → {total} página(s) no resultado.";
            }
        }
        else
        {
            var invalid = Files.FirstOrDefault(f => !f.IsValid);
            if (invalid is not null)
            {
                problem = $"{invalid.Name}: {invalid.RangeError}";
            }
            else
            {
                var total = Files.Sum(f => f.SelectedPageCount);
                summary = $"{Files.Count} arquivo(s) → {total} página(s) no resultado.";
            }
        }

        Problem = problem;
        Summary = summary;
        OnStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        var files = Files.ToList();
        var inputName = files.Count == 1 ? files[0].Name : $"{files.Count} arquivos ({files[0].Name}…)";
        var name = FileName.Trim();
        var target = CreateTarget();

        if (IsInterleave)
        {
            if (!TryGetPagesPerTurn(out var perTurn)) return;
            var inputs = files.Select(f => new InterleaveInput(f.Path, f.Reverse)).ToList();
            var remainder = StopAtShortest ? InterleaveRemainder.StopAtShortest : InterleaveRemainder.AppendRemaining;
            await RunAsync("Intercalar documentos", inputName, "Intercalando páginas…", async (progress, ct) =>
                [await Context.Organizer.InterleaveAsync(inputs, perTurn, remainder, name, target, progress, ct)]);
        }
        else
        {
            var inputs = files.Select(f => new MergeInput(f.Path, f.RangeText)).ToList();
            var bookmarks = BookmarkPerFile;
            await RunAsync("Mesclar PDF", inputName, "Mesclando documentos…", async (progress, ct) =>
                [await Context.Organizer.MergeAsync(inputs, name, target, bookmarks, progress, ct)]);
        }
    }
}
