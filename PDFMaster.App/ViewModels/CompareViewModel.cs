using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Comparison;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

/// <summary>Retângulo de destaque sobre a página (em pontos da página exibida).</summary>
public sealed record OverlayBox(double X, double Y, double Width, double Height, string Stroke, string Fill, double Thickness);

/// <summary>Uma diferença na lista lateral.</summary>
public sealed record ChangeItem(DiffChange Change, string Kind, string Text, string Color);

/// <summary>Comparação de dois PDFs, lado a lado, com navegação pelas diferenças e relatório.</summary>
public sealed partial class CompareViewModel : ToolViewModelBase
{
    private const double RenderScale = 1.5;

    public CompareViewModel(ToolContext context)
        : base(context, "Comparar PDFs",
            "Mostra lado a lado o que mudou entre duas versões: texto acrescentado, removido e alterado, páginas inseridas e removidas, imagens e diferenças visuais.")
    {
    }

    [ObservableProperty] private string? _pathA;
    [ObservableProperty] private string? _pathB;
    [ObservableProperty] private bool _compareText = true;
    [ObservableProperty] private bool _compareVisual = true;
    [ObservableProperty] private bool _compareImages = true;
    [ObservableProperty] private ComparisonResult? _result;
    [ObservableProperty] private ChangeItem? _selectedChange;
    [ObservableProperty] private int _pairIndex;
    [ObservableProperty] private ImageSource? _leftImage;
    [ObservableProperty] private ImageSource? _rightImage;
    [ObservableProperty] private double _leftWidth = 595;
    [ObservableProperty] private double _leftHeight = 842;
    [ObservableProperty] private double _rightWidth = 595;
    [ObservableProperty] private double _rightHeight = 842;
    [ObservableProperty] private string? _leftCaption;
    [ObservableProperty] private string? _rightCaption;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _pairText;

    public ObservableCollection<ChangeItem> Changes { get; } = [];
    public ObservableCollection<OverlayBox> LeftBoxes { get; } = [];
    public ObservableCollection<OverlayBox> RightBoxes { get; } = [];

    public string NameA => PathA is null ? "Escolha o primeiro PDF (versão original)" : Path.GetFileName(PathA);
    public string NameB => PathB is null ? "Escolha o segundo PDF (versão nova)" : Path.GetFileName(PathB);
    public bool HasResult => Result is not null;
    public override bool IsEmpty => PathA is null && PathB is null;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(CompareCommand);

    partial void OnPathAChanged(string? value) => InputsChanged();
    partial void OnPathBChanged(string? value) => InputsChanged();

    private void InputsChanged()
    {
        OnPropertyChanged(nameof(NameA));
        OnPropertyChanged(nameof(NameB));
        OnPropertyChanged(nameof(IsEmpty));
        Result = null;
        OnStateChanged();
    }

    partial void OnResultChanged(ComparisonResult? value)
    {
        OnPropertyChanged(nameof(HasResult));
        if (value is null)
        {
            Changes.Clear();
            LeftBoxes.Clear();
            RightBoxes.Clear();
            LeftImage = RightImage = null;
            Summary = PairText = null;
        }

        OnStateChanged();
    }

    partial void OnSelectedChangeChanged(ChangeItem? value)
    {
        if (value is null || Result is null) return;
        var index = Result.Pairs.ToList().FindIndex(p => p.PageA == value.Change.PageA && p.PageB == value.Change.PageB);
        if (index < 0) index = Result.Pairs.ToList().FindIndex(p => p.PageA == value.Change.PageA || p.PageB == value.Change.PageB);
        if (index >= 0) Show(index);
        OnStateChanged();
    }

    protected override void OnStateChanged()
    {
        CompareCommand.NotifyCanExecuteChanged();
        PickACommand.NotifyCanExecuteChanged();
        PickBCommand.NotifyCanExecuteChanged();
        SwapCommand.NotifyCanExecuteChanged();
        SaveReportCommand.NotifyCanExecuteChanged();
        PreviousChangeCommand.NotifyCanExecuteChanged();
        NextChangeCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return Task.CompletedTask;
        var pdfs = OnlyPdfs(paths).Select(Path.GetFullPath).ToList();
        if (pdfs.Count < paths.Count)
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram adicionados", "A comparação aceita apenas PDFs.");
        if (pdfs.Count >= 2)
        {
            PathA = pdfs[0];
            PathB = pdfs[1];
        }
        else if (pdfs.Count == 1)
        {
            if (PathA is null) PathA = pdfs[0];
            else PathB = pdfs[0];
        }

        if (pdfs.Count > 0) SuggestOutputFolder(pdfs[0]);
        return Task.CompletedTask;
    }

    private bool CanEdit() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void PickA()
    {
        var picked = PickPdfs(multiple: false);
        if (picked.Count > 0)
        {
            PathA = Path.GetFullPath(picked[0]);
            SuggestOutputFolder(PathA);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void PickB()
    {
        var picked = PickPdfs(multiple: false);
        if (picked.Count > 0)
        {
            PathB = Path.GetFullPath(picked[0]);
            SuggestOutputFolder(PathB);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Swap() => (PathA, PathB) = (PathB, PathA);

    private bool CanCompare() => !IsBusy && PathA is not null && PathB is not null && (CompareText || CompareVisual || CompareImages);

    partial void OnCompareTextChanged(bool value) => OnStateChanged();
    partial void OnCompareVisualChanged(bool value) => OnStateChanged();
    partial void OnCompareImagesChanged(bool value) => OnStateChanged();

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        if (PathA is not { } a || PathB is not { } b) return;
        var options = new CompareOptions(CompareText, CompareVisual, CompareImages);
        Result = null;
        ComparisonResult? result = null;
        if (!await AnalyzeAsync("Comparando…", async (progress, ct) => result = await DocumentComparer.CompareAsync(a, b, options, progress, ct)) ||
            result is null)
            return;

        Result = result;
        foreach (var c in result.Changes) Changes.Add(Item(c));
        var parts = new List<string>();
        void Part(int count, string label)
        {
            if (count > 0) parts.Add($"{count} {label}");
        }

        Part(result.Count(ChangeKind.Modified), "alteração(ões)");
        Part(result.Count(ChangeKind.Added), "acréscimo(s)");
        Part(result.Count(ChangeKind.Removed), "remoção(ões)");
        Part(result.Count(ChangeKind.PageInserted), "página(s) inserida(s)");
        Part(result.Count(ChangeKind.PageRemoved), "página(s) removida(s)");
        Part(result.Count(ChangeKind.Images), "diferença(s) de imagem");
        Part(result.Count(ChangeKind.Visual), "diferença(s) visual(is)");
        Summary = result.Identical ? "Nenhuma diferença encontrada." : string.Join(", ", parts) + ".";
        if (!result.TextAvailable)
            Summary += " Os documentos não têm texto pesquisável: a comparação foi visual (use o OCR antes para comparar o texto).";
        if (Changes.Count > 0) SelectedChange = Changes[0];
        else Show(0);
    }

    private static ChangeItem Item(DiffChange c)
    {
        var color = ColorOf(c.Kind);
        var text = c.Kind switch
        {
            ChangeKind.Modified => $"\"{Short(c.OldText)}\" → \"{Short(c.NewText)}\"",
            ChangeKind.Added => $"+ \"{Short(c.NewText)}\"",
            ChangeKind.Removed => $"− \"{Short(c.OldText)}\"",
            _ => c.Description,
        };
        var where = c.Kind is ChangeKind.PageInserted or ChangeKind.PageRemoved
            ? string.Empty
            : $"Pág. {(c.PageA is { } x ? x + 1 : 0)} → {(c.PageB is { } y ? y + 1 : 0)} · ";
        return new ChangeItem(c, where + ComparisonReport.KindName(c.Kind), text, color);
    }

    private static string Short(string text) => text.Length <= 70 ? text : text[..67] + "…";

    private static string ColorOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.PageInserted => "#2DA44E",
        ChangeKind.Removed or ChangeKind.PageRemoved => "#CF222E",
        ChangeKind.Modified => "#BF8700",
        _ => "#0969DA",
    };

    private async void Show(int index)
    {
        try
        {
            await ShowAsync(index);
        }
        catch (Exception ex) when (ex is PdfRenderException or IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível mostrar a página", ex.Message);
        }
    }

    private async Task ShowAsync(int index)
    {
        if (Result is not { } result || result.Pairs.Count == 0 || PathA is not { } a || PathB is not { } b) return;
        index = Math.Clamp(index, 0, result.Pairs.Count - 1);
        PairIndex = index;
        var pair = result.Pairs[index];
        PairText = $"Par de páginas {index + 1} de {result.Pairs.Count}";
        LeftBoxes.Clear();
        RightBoxes.Clear();

        if (pair.PageA is { } pa)
        {
            (LeftWidth, LeftHeight) = result.SizesA[pa];
            LeftCaption = $"{Path.GetFileName(a)} — página {pa + 1} de {result.SizesA.Count}";
            LeftImage = ThumbnailService.ToBitmap(await Context.Renderer.RenderPageAsync(a, pa, RenderSize.ByScale(RenderScale)));
        }
        else
        {
            LeftImage = null;
            LeftCaption = "Sem página correspondente: a página ao lado foi inserida no segundo documento.";
        }

        if (pair.PageB is { } pb)
        {
            (RightWidth, RightHeight) = result.SizesB[pb];
            RightCaption = $"{Path.GetFileName(b)} — página {pb + 1} de {result.SizesB.Count}";
            RightImage = ThumbnailService.ToBitmap(await Context.Renderer.RenderPageAsync(b, pb, RenderSize.ByScale(RenderScale)));
        }
        else
        {
            RightImage = null;
            RightCaption = "Sem página correspondente: a página ao lado foi removida no segundo documento.";
        }

        foreach (var item in Changes)
        {
            var c = item.Change;
            var samePair = c.PageA == pair.PageA && c.PageB == pair.PageB;
            if (!samePair) continue;
            var selected = ReferenceEquals(item, SelectedChange);
            var fill = "#33" + item.Color[1..];
            var thickness = selected ? 3 : 1.5;
            foreach (var box in c.BoxesA) LeftBoxes.Add(new OverlayBox(box.X, box.Y, box.Width, box.Height, item.Color, fill, thickness));
            foreach (var box in c.BoxesB) RightBoxes.Add(new OverlayBox(box.X, box.Y, box.Width, box.Height, item.Color, fill, thickness));
        }

        OnStateChanged();
    }

    private bool HasPrevious() => !IsBusy && Changes.Count > 0 && (SelectedChange is null || Changes.IndexOf(SelectedChange) > 0);
    private bool HasNext() => !IsBusy && Changes.Count > 0 && (SelectedChange is null || Changes.IndexOf(SelectedChange) < Changes.Count - 1);

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void PreviousChange() => SelectedChange = Changes[Math.Max(0, (SelectedChange is null ? 1 : Changes.IndexOf(SelectedChange)) - 1)];

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void NextChange() => SelectedChange = Changes[SelectedChange is null ? 0 : Changes.IndexOf(SelectedChange) + 1];

    private bool HasPreviousPage() => !IsBusy && Result is not null && PairIndex > 0;
    private bool HasNextPage() => !IsBusy && Result is not null && PairIndex < Result.Pairs.Count - 1;

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private void PreviousPage() => Show(PairIndex - 1);

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private void NextPage() => Show(PairIndex + 1);

    private bool CanSaveReport() => !IsBusy && Result is not null;

    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    private async Task SaveReportAsync()
    {
        if (Result is not { } result) return;
        var target = CreateTarget();
        await RunAsync(ComparisonReport.OperationName, $"{Path.GetFileName(result.PathA)} × {Path.GetFileName(result.PathB)}", "Gerando relatório…",
            async (_, ct) => [await Context.Security.CompareReport.WriteAsync(result, target, ct)],
            describe: _ => "Relatório da comparação (HTML)");
    }
}
