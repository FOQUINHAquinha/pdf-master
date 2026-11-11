using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

/// <summary>Uma página no editor de organização (cópia de uma página de origem ou página em branco).</summary>
public sealed partial class PageItemViewModel : ObservableObject
{
    public PageItemViewModel(string? sourcePath, int sourceIndex, double widthPt, double heightPt)
    {
        SourcePath = sourcePath;
        SourceIndex = sourceIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public string? SourcePath { get; }
    public int SourceIndex { get; }
    public double WidthPt { get; }
    public double HeightPt { get; }
    public bool IsBlank => SourcePath is null;

    public string SourceLabel => IsBlank
        ? "Página em branco"
        : $"{Path.GetFileNameWithoutExtension(SourcePath)} · p. {SourceIndex + 1}";

    /// <summary>Rotação adicional pedida pelo usuário (0, 90, 180 ou 270), ainda não gravada.</summary>
    [ObservableProperty]
    private int _rotation;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Posição no documento de saída, começando em 1.</summary>
    [ObservableProperty]
    private int _position;

    public PageItemViewModel Clone() =>
        new(SourcePath, SourceIndex, WidthPt, HeightPt) { Rotation = Rotation, Thumbnail = Thumbnail };

    public PageInstruction ToInstruction() => IsBlank
        ? PageInstruction.Blank(WidthPt, HeightPt)
        : PageInstruction.From(SourcePath!, SourceIndex, Rotation);
}

/// <summary>
/// Editor visual de páginas: reordenar, girar, duplicar, excluir, inserir páginas em branco ou de
/// outro documento e extrair páginas. Nada é gravado até "Salvar como novo PDF".
/// </summary>
public sealed partial class PageEditorViewModel : ToolViewModelBase
{
    private const int MaxUndo = 50;
    private readonly Stack<List<(PageItemViewModel Item, int Rotation)>> _undo = new();
    private readonly Stack<List<(PageItemViewModel Item, int Rotation)>> _redo = new();
    private CancellationTokenSource _thumbnails = new();
    private string? _mainDocument;

    public PageEditorViewModel(ToolContext context)
        : base(context, "Organizar páginas",
            "Reordene, gire, duplique, exclua, insira e extraia páginas. O arquivo original não é alterado.")
    {
        Pages.CollectionChanged += OnPagesChanged;
    }

    public ObservableCollection<PageItemViewModel> Pages { get; } = [];

    [ObservableProperty]
    private string _fileName = "organizado";

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string _summary = "Abra um PDF para começar.";

    [ObservableProperty]
    private bool _hasChanges;

    public override bool IsEmpty => Pages.Count == 0;
    protected internal override IReadOnlyList<string> RepeatInputs => SourcePath is null ? [] : [SourcePath];
    public bool HasPages => Pages.Count > 0;

    private List<PageItemViewModel> Selected => Pages.Where(p => p.IsSelected).ToList();
    private bool HasSelection => Pages.Any(p => p.IsSelected);

    private bool CanEdit() => !IsBusy;
    private bool CanEditPages() => !IsBusy && Pages.Count > 0;
    private bool CanEditSelection() => !IsBusy && HasSelection;
    private bool CanUndo() => !IsBusy && _undo.Count > 0;
    private bool CanRedo() => !IsBusy && _redo.Count > 0;
    private bool CanSave() => !IsBusy && Pages.Count > 0 && !string.IsNullOrWhiteSpace(FileName);

    partial void OnFileNameChanged(string value) => OnStateChanged();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(SaveCommand);

    protected override void OnStateChanged()
    {
        PickFileCommand.NotifyCanExecuteChanged();
        InsertFromFileCommand.NotifyCanExecuteChanged();
        InsertBlankCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveLeftCommand.NotifyCanExecuteChanged();
        MoveRightCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        SelectRangeCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
    }

    // ---------- Abrir e inserir ----------

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFileAsync()
    {
        var picked = PickPdfs(multiple: false);
        if (picked.Count == 0) return;

        if (Pages.Count > 0 && HasChanges &&
            !Context.Dialogs.Confirm("Abrir outro documento",
                "As alterações ainda não foram salvas e serão descartadas.\n\nAbrir outro documento mesmo assim?"))
        {
            return;
        }

        Reset();
        await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private async Task InsertFromFileAsync()
    {
        var picked = PickPdfs(multiple: true);
        if (picked.Count > 0) await InsertFilesAsync(picked, InsertionIndex(), recordUndo: true);
    }

    /// <summary>Com o editor vazio, abre o documento; caso contrário, insere as páginas no final.</summary>
    public override Task AddFilesAsync(IReadOnlyList<string> paths) =>
        InsertFilesAsync(paths, Pages.Count, recordUndo: Pages.Count > 0);

    private async Task InsertFilesAsync(IReadOnlyList<string> paths, int index, bool recordUndo)
    {
        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count == 0)
        {
            if (paths.Count > 0)
                Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "Esta ferramenta trabalha apenas com arquivos PDF.");
            return;
        }

        var wasEmpty = Pages.Count == 0;
        await WhileBusyAsync("Lendo documentos…", async () =>
        {
            var snapshotTaken = false;
            foreach (var path in pdfs)
            {
                var info = await TryReadInfoAsync(path);
                if (info is null) continue;

                if (recordUndo && !snapshotTaken)
                {
                    PushUndo();
                    snapshotTaken = true;
                }

                var items = new List<PageItemViewModel>(info.PageCount);
                for (var i = 0; i < info.PageCount; i++)
                {
                    var page = info.Pages[i];
                    items.Add(new PageItemViewModel(info.Path, i, page.WidthPt, page.HeightPt));
                }

                index = Math.Clamp(index, 0, Pages.Count);
                foreach (var item in items) Pages.Insert(index++, item);

                if (_mainDocument is null)
                {
                    _mainDocument = info.Path;
                    FileName = Path.GetFileNameWithoutExtension(info.Path) + "_organizado";
                }

                SuggestOutputFolder(info.Path);
                LoadThumbnails(info.Path, items);
            }
        });

        if (!wasEmpty && Pages.Count > 0) HasChanges = true;
        AfterChange();
    }

    private async void LoadThumbnails(string path, List<PageItemViewModel> items)
    {
        var byIndex = items.ToDictionary(i => i.SourceIndex);
        try
        {
            await Context.Thumbnails.LoadAsync(path, byIndex.Keys.OrderBy(k => k).ToList(), (index, image) =>
            {
                // Cópias (duplicatas) da mesma página recebem a mesma miniatura.
                foreach (var page in Pages)
                {
                    if (page.SourceIndex == index && page.Thumbnail is null &&
                        string.Equals(page.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        page.Thumbnail = image;
                    }
                }

                if (byIndex.TryGetValue(index, out var original) && original.Thumbnail is null) original.Thumbnail = image;
            }, _thumbnails.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfRenderException ex)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Miniaturas indisponíveis", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void InsertBlank()
    {
        PushUndo();
        var index = InsertionIndex();
        var reference = Pages[Math.Clamp(index - 1, 0, Pages.Count - 1)];
        Pages.Insert(index, new PageItemViewModel(null, -1, reference.WidthPt, reference.HeightPt));
        HasChanges = true;
        AfterChange();
    }

    /// <summary>Logo depois da última página selecionada, ou no final.</summary>
    private int InsertionIndex()
    {
        var last = -1;
        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].IsSelected) last = i;
        }

        return last < 0 ? Pages.Count : last + 1;
    }

    // ---------- Edição ----------

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void RotateLeft() => Rotate(270);

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void RotateRight() => Rotate(90);

    /// <summary>Gira as páginas selecionadas; sem seleção, gira todas.</summary>
    private void Rotate(int degrees)
    {
        var targets = HasSelection ? Selected : Pages.ToList();
        if (targets.Count == 0) return;
        PushUndo();
        foreach (var page in targets) page.Rotation = (page.Rotation + degrees) % 360;
        HasChanges = true;
        AfterChange();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Duplicate()
    {
        PushUndo();
        for (var i = Pages.Count - 1; i >= 0; i--)
        {
            if (Pages[i].IsSelected) Pages.Insert(i + 1, Pages[i].Clone());
        }

        HasChanges = true;
        AfterChange();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void Delete()
    {
        var selected = Selected;
        if (selected.Count == Pages.Count)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Não é possível excluir todas as páginas",
                "O documento precisa ficar com pelo menos uma página.");
            return;
        }

        PushUndo();
        foreach (var page in selected) Pages.Remove(page);
        HasChanges = true;
        AfterChange();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void MoveLeft() => MoveSelectedBy(-1);

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void MoveRight() => MoveSelectedBy(1);

    private void MoveSelectedBy(int delta)
    {
        var indexes = Enumerable.Range(0, Pages.Count).Where(i => Pages[i].IsSelected).ToList();
        if (indexes.Count == 0) return;
        if (delta < 0 && indexes[0] == 0) return;
        if (delta > 0 && indexes[^1] == Pages.Count - 1) return;

        PushUndo();
        if (delta < 0)
        {
            foreach (var i in indexes) Pages.Move(i, i - 1);
        }
        else
        {
            for (var k = indexes.Count - 1; k >= 0; k--) Pages.Move(indexes[k], indexes[k] + 1);
        }

        HasChanges = true;
        AfterChange();
    }

    /// <summary>Arrastar e soltar: leva as páginas selecionadas para antes da posição indicada.</summary>
    public void MoveSelectedTo(int targetIndex)
    {
        if (IsBusy) return;
        var selected = Selected;
        if (selected.Count == 0) return;

        targetIndex = Math.Clamp(targetIndex, 0, Pages.Count);
        var before = Pages.Take(targetIndex).Count(p => p.IsSelected);
        var destination = targetIndex - before;

        var remaining = Pages.Where(p => !p.IsSelected).ToList();
        remaining.InsertRange(Math.Clamp(destination, 0, remaining.Count), selected);
        if (remaining.SequenceEqual(Pages)) return;

        PushUndo();
        ApplyOrder(remaining);
        HasChanges = true;
        AfterChange();
    }

    // ---------- Seleção ----------

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void SelectAll()
    {
        foreach (var page in Pages) page.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void ClearSelection()
    {
        foreach (var page in Pages) page.IsSelected = false;
    }

    /// <summary>Seleciona pelas posições atuais: "1-5", "1,3,5,7", "2-8,12,15-20".</summary>
    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void SelectRange()
    {
        if (string.IsNullOrWhiteSpace(RangeText))
        {
            Context.Notifications.Show(NotificationKind.Info, "Informe as páginas", "Exemplos: 1-5 · 1,3,5,7 · 2-8,12,15-20");
            return;
        }

        if (!PageRangeParser.TryParse(RangeText, Pages.Count, out var indexes, out var error))
        {
            Context.Notifications.Show(NotificationKind.Warning, "Intervalo inválido", error ?? string.Empty);
            return;
        }

        var wanted = indexes.ToHashSet();
        for (var i = 0; i < Pages.Count; i++) Pages[i].IsSelected = wanted.Contains(i);
    }

    // ---------- Desfazer e refazer ----------

    private List<(PageItemViewModel Item, int Rotation)> Snapshot() => Pages.Select(p => (p, p.Rotation)).ToList();

    private void PushUndo()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        if (_undo.Count > MaxUndo)
        {
            var kept = _undo.Reverse().Skip(_undo.Count - MaxUndo).ToList();
            _undo.Clear();
            foreach (var state in kept) _undo.Push(state);
        }
    }

    private void Restore(List<(PageItemViewModel Item, int Rotation)> state)
    {
        ApplyOrder(state.Select(s => s.Item).ToList());
        foreach (var (item, rotation) in state) item.Rotation = rotation;
        HasChanges = true;
        AfterChange();
    }

    private void ApplyOrder(List<PageItemViewModel> order)
    {
        Pages.Clear();
        foreach (var item in order) Pages.Add(item);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    // ---------- Gravar ----------

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var instructions = Pages.Select(p => p.ToInstruction()).ToList();
        var name = FileName.Trim();
        var target = CreateTarget();
        var saved = await RunAsync("Reorganizar páginas", InputName(), "Gravando o novo documento…", async (progress, ct) =>
            [await Context.Organizer.SaveArrangementAsync(instructions, name, target, "Reorganizar páginas", null, progress, ct)]);
        if (saved) HasChanges = false;
        AfterChange();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private async Task ExtractAsync()
    {
        var instructions = Selected.Select(p => p.ToInstruction()).ToList();
        if (instructions.Count == 0) return;
        var baseName = _mainDocument is null ? "documento" : Path.GetFileNameWithoutExtension(_mainDocument);
        var target = CreateTarget();
        await RunAsync("Extrair páginas", InputName(), "Extraindo as páginas selecionadas…", async (progress, ct) =>
            [await Context.Organizer.SaveArrangementAsync(instructions, baseName + "_extraido", target, "Extrair páginas", null, progress, ct)]);
    }

    private string InputName() => _mainDocument is null ? "documento" : Path.GetFileName(_mainDocument);

    // ---------- Estado ----------

    private void Reset()
    {
        _thumbnails.Cancel();
        _thumbnails.Dispose();
        _thumbnails = new CancellationTokenSource();
        Pages.Clear();
        _undo.Clear();
        _redo.Clear();
        _mainDocument = null;
        HasChanges = false;
        AfterChange();
    }

    private void OnPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (PageItemViewModel item in e.OldItems) item.PropertyChanged -= OnPageChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (PageItemViewModel item in e.NewItems)
            {
                item.PropertyChanged -= OnPageChanged;
                item.PropertyChanged += OnPageChanged;
            }
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItemViewModel.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = Pages.Count(p => p.IsSelected);
        SelectionText = count == 0
            ? "Nenhuma página selecionada — girar se aplica a todas."
            : $"{count} página(s) selecionada(s): {PageRangeParser.Format(Enumerable.Range(0, Pages.Count).Where(i => Pages[i].IsSelected))}";
        OnStateChanged();
    }

    private void AfterChange()
    {
        for (var i = 0; i < Pages.Count; i++) Pages[i].Position = i + 1;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasPages));
        Summary = Pages.Count == 0
            ? "Abra um PDF para começar."
            : $"{Pages.Count} página(s)" + (HasChanges ? " · alterações não salvas" : string.Empty);
        UpdateSelection();
    }
}
