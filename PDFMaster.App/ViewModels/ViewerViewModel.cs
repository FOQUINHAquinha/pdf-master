using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

public sealed partial class ViewerPageViewModel : ObservableObject
{
    public ViewerPageViewModel(int index) => Index = index;

    public int Index { get; }
    public int Number => Index + 1;

    [ObservableProperty]
    private ImageSource? _thumbnail;
}

/// <summary>Visualizador: página central, miniaturas, zoom e navegação. Renderiza só a página exibida.</summary>
public sealed partial class ViewerViewModel : ToolViewModelBase
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 6.0;
    private const double MaxPixels = 36_000_000; // limite de memória por página renderizada

    private PdfDocumentInfo? _document;
    private CancellationTokenSource _thumbnails = new();
    private CancellationTokenSource? _render;

    public ViewerViewModel(ToolContext context)
        : base(context, "Visualizador de PDF", "Leia o documento, navegue pelas páginas e ajuste o zoom.")
    {
    }

    /// <summary>Definido pela janela principal: abre o documento atual em outra ferramenta.</summary>
    public Action<string, string>? OpenToolRequested { get; set; }

    public ObservableCollection<ViewerPageViewModel> Pages { get; } = [];

    public override bool IsEmpty => _document is null;
    protected internal override IReadOnlyList<string> RepeatInputs => _document is null ? [] : [_document.Path];
    public bool HasDocument => _document is not null;
    public string DocumentName => _document is null ? string.Empty : Path.GetFileName(_document.Path);

    public string DocumentDetails => _document is null
        ? string.Empty
        : $"{_document.PageCount} página(s) · {FileSizeFormatter.Format(_document.SizeBytes)}";

    public string PageCountText => _document is null ? string.Empty : $"de {_document.PageCount}";
    public string ZoomText => $"{Zoom * 100:0}%";

    /// <summary>Largura útil da área de leitura, informada pela tela (para "ajustar à largura").</summary>
    public double ViewportWidth { get; set; } = 800;

    /// <summary>Escala de DPI do monitor, informada pela tela, para a página ficar nítida.</summary>
    public double DpiScale { get; set; } = 1.0;

    [ObservableProperty]
    private ViewerPageViewModel? _selectedPage;

    [ObservableProperty]
    private string _pageNumberText = string.Empty;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private ImageSource? _pageImage;

    [ObservableProperty]
    private string? _renderError;

    private int CurrentIndex => SelectedPage?.Index ?? 0;

    protected override void OnStateChanged()
    {
        PickFileCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
        GoToPageCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ActualSizeCommand.NotifyCanExecuteChanged();
        FitWidthCommand.NotifyCanExecuteChanged();
        OrganizeCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        SplitCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool HasDoc() => _document is not null;
    private bool CanGoBack() => _document is not null && CurrentIndex > 0;
    private bool CanGoForward() => _document is not null && CurrentIndex < _document.PageCount - 1;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFileAsync()
    {
        var picked = PickPdfs(multiple: false);
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    public override async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count == 0)
        {
            if (paths.Count > 0)
                Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "O visualizador abre apenas arquivos PDF.");
            return;
        }

        PdfDocumentInfo? loaded = null;
        await WhileBusyAsync("Abrindo documento…", async () => loaded = await TryReadInfoAsync(pdfs[0]));
        if (loaded is null) return;

        _thumbnails.Cancel();
        _thumbnails.Dispose();
        _thumbnails = new CancellationTokenSource();

        _document = loaded;
        Pages.Clear();
        for (var i = 0; i < loaded.PageCount; i++) Pages.Add(new ViewerPageViewModel(i));

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(DocumentDetails));
        OnPropertyChanged(nameof(PageCountText));

        SelectedPage = Pages[0];
        FitWidth();
        OnStateChanged();
        LoadThumbnails(loaded.Path);
    }

    private async void LoadThumbnails(string path)
    {
        try
        {
            await Context.Thumbnails.LoadAsync(path, Enumerable.Range(0, Pages.Count).ToList(), (index, image) =>
            {
                if (index < Pages.Count && _document?.Path == path) Pages[index].Thumbnail = image;
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

    partial void OnSelectedPageChanged(ViewerPageViewModel? value)
    {
        if (value is null) return;
        PageNumberText = value.Number.ToString();
        OnStateChanged();
        RenderCurrent();
    }

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        RenderCurrent();
    }

    /// <summary>Renderiza a página atual; um pedido novo cancela o anterior.</summary>
    public async void RenderCurrent()
    {
        var document = _document;
        var page = SelectedPage;
        if (document is null || page is null) return;

        _render?.Cancel();
        var cts = new CancellationTokenSource();
        _render = cts;

        try
        {
            var info = document.Pages[page.Index];
            var scale = Zoom * 96.0 / 72.0 * DpiScale;
            var pixels = info.WidthPt * scale * info.HeightPt * scale;
            if (pixels > MaxPixels) scale *= Math.Sqrt(MaxPixels / pixels);

            var rendered = await Context.Renderer.RenderPageAsync(document.Path, page.Index, RenderSize.ByScale(scale), cts.Token);
            if (cts.IsCancellationRequested) return;
            PageImage = ThumbnailService.ToBitmap(rendered, 96 * DpiScale);
            RenderError = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfRenderException ex)
        {
            PageImage = null;
            RenderError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_render, cts)) _render = null;
            cts.Dispose();
        }
    }

    private void GoTo(int index)
    {
        if (_document is null || Pages.Count == 0) return;
        SelectedPage = Pages[Math.Clamp(index, 0, Pages.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage() => GoTo(CurrentIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextPage() => GoTo(CurrentIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void FirstPage() => GoTo(0);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void LastPage() => GoTo(Pages.Count - 1);

    /// <summary>Vai para a página digitada na caixa de número.</summary>
    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void GoToPage()
    {
        if (int.TryParse(PageNumberText.Trim(), out var number) && number >= 1 && number <= Pages.Count)
        {
            GoTo(number - 1);
        }
        else
        {
            PageNumberText = (CurrentIndex + 1).ToString();
            Context.Notifications.Show(NotificationKind.Warning, "Página inexistente", $"Digite um número entre 1 e {Pages.Count}.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round(Zoom * 1.25, 2));

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round(Zoom / 1.25, 2));

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void ActualSize() => Zoom = 1.0;

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void FitWidth()
    {
        if (_document is null || SelectedPage is null) return;
        var info = _document.Pages[SelectedPage.Index];
        var widthPt = info.Rotation is 90 or 270 ? info.HeightPt : info.WidthPt;
        if (widthPt <= 0) return;

        var available = Math.Max(200, ViewportWidth - 48);
        var fitted = Math.Clamp(Math.Round(available / (widthPt * 96.0 / 72.0), 2), MinZoom, MaxZoom);
        if (Math.Abs(fitted - Zoom) < 0.001) RenderCurrent();
        else Zoom = fitted;
    }

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void Edit()
    {
        if (_document is not null) OpenToolRequested?.Invoke("edit-text", _document.Path);
    }

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void Organize()
    {
        if (_document is not null) OpenToolRequested?.Invoke("reorder", _document.Path);
    }

    [RelayCommand(CanExecute = nameof(HasDoc))]
    private void Split()
    {
        if (_document is not null) OpenToolRequested?.Invoke("split", _document.Path);
    }
}
