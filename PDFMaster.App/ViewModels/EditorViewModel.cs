using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

public enum EditorTool
{
    Select, EditText, Text, Image, Signature, Stamp,
    Highlight, Underline, StrikeOut, Comment, Note, TextBox,
    Arrow, Line, Rectangle, Ellipse, Ink,
    FieldText, FieldMultiline, FieldCheckBox, FieldRadio, FieldDropdown, FieldDate, FieldButton,
}

/// <summary>Como a área da página responde ao mouse com a ferramenta atual.</summary>
public enum EditorGesture
{
    Select,
    Click,
    Rect,
    Line,
    Ink,
}

public sealed record EditorToolChoice(EditorTool Tool, string Glyph, string Label, string Group, string Hint);

public sealed partial class EditorPageViewModel : ObservableObject
{
    public EditorPageViewModel(int index, double width, double height)
    {
        Index = index;
        Width = width;
        Height = height;
    }

    public int Index { get; }
    public int Number => Index + 1;

    /// <summary>Tamanho exibido, em pontos.</summary>
    public double Width { get; }
    public double Height { get; }

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private int _editCount;
}

public sealed record SignatureChoice(string Path, ImageSource Preview);

/// <summary>
/// Editor visual: página com miniaturas, zoom e navegação; ferramentas de texto, imagem, anotação,
/// assinatura, carimbo e formulário; propriedades do item selecionado; histórico com desfazer/refazer.
/// As alterações só vão para um novo arquivo ao gravar — o original nunca é alterado.
/// </summary>
public sealed partial class EditorViewModel : ToolViewModelBase
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;
    private const double MaxPixels = 30_000_000;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly List<EditItem> _items = [];
    private readonly Stack<(List<EditItem> Items, string Description)> _undo = new();
    private readonly Stack<(List<EditItem> Items, string Description)> _redo = new();
    private readonly Dictionary<int, IReadOnlyList<TextRun>> _runs = [];
    private string? _lastCoalesce;
    private DateTime _lastCoalesceTime;
    private bool _loading;
    private bool _dirty;
    private PdfDocumentInfo? _document;
    private CancellationTokenSource _thumbnails = new();
    private CancellationTokenSource? _render;
    private byte[]? _pendingImage;
    private string _pendingImageName = string.Empty;
    private int _fieldCounter;

    public EditorViewModel(ToolContext context)
        : base(context, "Editor de PDF",
            "Texto, imagens, anotações, assinaturas, carimbos e campos de formulário. As alterações vão para um novo arquivo.")
    {
        Tools =
        [
            new(EditorTool.Select, "", "Selecionar", "Geral", "Clique num item para selecionar; arraste para mover; use o canto inferior direito para redimensionar."),
            new(EditorTool.EditText, "", "Editar texto existente", "Texto", "Clique numa linha de texto do PDF para alterá-la."),
            new(EditorTool.Text, "", "Inserir texto", "Texto", "Clique ou arraste na página para criar uma caixa de texto."),
            new(EditorTool.TextBox, "", "Caixa de texto (anotação)", "Texto", "Arraste para criar uma caixa de texto com borda, editável em outros leitores."),
            new(EditorTool.Image, "", "Imagem", "Imagens", "Escolha a imagem e clique na página para colocá-la."),
            new(EditorTool.Signature, "", "Assinatura visual", "Imagens", "Escolha uma assinatura à direita e clique na página. Não é assinatura digital."),
            new(EditorTool.Stamp, "", "Carimbo", "Imagens", "Escolha um carimbo à direita e clique na página."),
            new(EditorTool.Highlight, "", "Marca-texto", "Anotações", "Arraste sobre o texto para destacá-lo."),
            new(EditorTool.Underline, "", "Sublinhado", "Anotações", "Arraste sobre o texto para sublinhá-lo."),
            new(EditorTool.StrikeOut, "", "Tachado", "Anotações", "Arraste sobre o texto para riscá-lo."),
            new(EditorTool.Comment, "", "Comentário", "Anotações", "Clique para colocar um comentário e escreva o texto à direita."),
            new(EditorTool.Note, "", "Nota adesiva", "Anotações", "Clique para colocar uma nota adesiva e escreva o texto à direita."),
            new(EditorTool.Arrow, "", "Seta", "Formas", "Arraste do início ao fim da seta."),
            new(EditorTool.Line, "", "Linha", "Formas", "Arraste do início ao fim da linha."),
            new(EditorTool.Rectangle, "", "Retângulo", "Formas", "Arraste para desenhar um retângulo."),
            new(EditorTool.Ellipse, "", "Círculo", "Formas", "Arraste para desenhar um círculo ou elipse."),
            new(EditorTool.Ink, "", "Desenho livre", "Formas", "Desenhe com o mouse pressionado."),
            new(EditorTool.FieldText, "", "Campo de texto", "Formulário", "Arraste para criar um campo de texto."),
            new(EditorTool.FieldMultiline, "", "Campo de várias linhas", "Formulário", "Arraste para criar um campo de texto com várias linhas."),
            new(EditorTool.FieldCheckBox, "", "Caixa de seleção", "Formulário", "Clique ou arraste para criar uma caixa de seleção."),
            new(EditorTool.FieldRadio, "", "Botões de opção", "Formulário", "Arraste para criar um grupo de opções (edite as opções à direita)."),
            new(EditorTool.FieldDropdown, "", "Lista suspensa", "Formulário", "Arraste para criar uma lista suspensa (edite as opções à direita)."),
            new(EditorTool.FieldDate, "", "Campo de data", "Formulário", "Arraste para criar um campo de data (dd/mm/aaaa)."),
            new(EditorTool.FieldButton, "", "Botão", "Formulário", "Arraste para criar um botão (limpar ou imprimir)."),
        ];
        _selectedTool = Tools[0];
        Families = WindowsFonts.EditorFamilies;
        LoadSignatures();
        LoadStamps();
    }

    // ---------- Documento e páginas ----------

    public IReadOnlyList<EditorToolChoice> Tools { get; }
    public IReadOnlyList<string> Families { get; }
    public IReadOnlyList<Choice<TextAlign>> Alignments { get; } =
        [new(TextAlign.Left, "Esquerda"), new(TextAlign.Center, "Centro"), new(TextAlign.Right, "Direita")];
    public IReadOnlyList<Choice<ButtonAction>> ButtonActions { get; } =
        [new(ButtonAction.Reset, "Limpar o formulário"), new(ButtonAction.Print, "Imprimir (depende do leitor de PDF)")];
    public IReadOnlyList<string> Swatches { get; } = ["#000000", "#D13438", "#0067C0", "#008B50", "#FFE03D", "#FF8C00", "#8764B8", "#FFFFFF"];

    public ObservableCollection<EditorPageViewModel> Pages { get; } = [];
    public ObservableCollection<string> HistoryEntries { get; } = [];
    public ObservableCollection<SignatureChoice> Signatures { get; } = [];
    public ObservableCollection<StampTemplate> Stamps { get; } = [];

    public override bool IsEmpty => _document is null;
    protected internal override IReadOnlyList<string> RepeatInputs => _document is null ? [] : [_document.Path];
    public bool HasDocument => _document is not null;
    public string DocumentPath => _document?.Path ?? string.Empty;
    public string DocumentName => _document is null ? string.Empty : Path.GetFileName(_document.Path);
    public string PageCountText => _document is null ? string.Empty : $"de {_document.PageCount}";
    public string ZoomText => $"{Zoom * 100:0}%";
    public double ViewportWidth { get; set; } = 800;
    public double DpiScale { get; set; } = 1.0;

    /// <summary>Pontos → pixels independentes de dispositivo.</summary>
    public double Scale => Zoom * 96.0 / 72.0;
    public double PageWidthDip => (SelectedPage?.Width ?? 0) * Scale;
    public double PageHeightDip => (SelectedPage?.Height ?? 0) * Scale;

    public int EditCount => _items.Count;
    public string EditSummary => _items.Count == 0 ? "Nenhuma alteração" : $"{_items.Count} alteração(ões) não gravada(s)";

    [ObservableProperty] private EditorPageViewModel? _selectedPage;
    [ObservableProperty] private string _pageNumberText = string.Empty;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private ImageSource? _pageImage;
    [ObservableProperty] private string? _renderError;
    [ObservableProperty] private EditorToolChoice _selectedTool;
    [ObservableProperty] private EditItem? _selectedItem;
    [ObservableProperty] private string _notice = string.Empty;

    public EditorGesture Gesture => SelectedTool.Tool switch
    {
        EditorTool.Select => EditorGesture.Select,
        EditorTool.EditText or EditorTool.Image or EditorTool.Signature or EditorTool.Stamp or EditorTool.Comment or EditorTool.Note => EditorGesture.Click,
        EditorTool.Arrow or EditorTool.Line => EditorGesture.Line,
        EditorTool.Ink => EditorGesture.Ink,
        _ => EditorGesture.Rect,
    };

    private int CurrentIndex => SelectedPage?.Index ?? 0;

    /// <summary>Itens da página atual, na ordem de criação (o último fica por cima).</summary>
    public IReadOnlyList<EditItem> PageItems => _items.Where(i => i.PageIndex == CurrentIndex).ToList();

    /// <summary>Linhas de texto do PDF na página atual (para "Editar texto existente").</summary>
    public IReadOnlyList<TextRun> PageRuns => _runs.TryGetValue(CurrentIndex, out var runs) ? runs : [];

    public bool IsSignatureTool => SelectedTool.Tool == EditorTool.Signature;
    public bool IsStampTool => SelectedTool.Tool == EditorTool.Stamp || SelectedItem is StampItem;

    protected override void OnStateChanged()
    {
        PickFileCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanSave() => !IsBusy && _document is not null && _items.Count > 0;
    private bool CanUndo() => !IsBusy && _undo.Count > 0;
    private bool CanRedo() => !IsBusy && _redo.Count > 0;
    private bool HasSelection() => !IsBusy && SelectedItem is not null;
    private bool CanGoBack() => _document is not null && CurrentIndex > 0;
    private bool CanGoForward() => _document is not null && CurrentIndex < _document.PageCount - 1;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(SaveCommand);

    /// <summary>Seleciona uma ferramenta pelo id do catálogo (edit-text, annotate, sign-visual, stamps…).</summary>
    public void UseCatalogTool(string toolId)
    {
        var tool = toolId switch
        {
            "edit-text" => EditorTool.EditText,
            "edit-images" => EditorTool.Image,
            "annotate" => EditorTool.Highlight,
            "sign-visual" => EditorTool.Signature,
            "stamps" => EditorTool.Stamp,
            "form-fields" => EditorTool.FieldText,
            _ => EditorTool.Select,
        };
        SelectedTool = Tools.First(t => t.Tool == tool);
    }

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
            // Imagens soltas sobre o editor com um documento aberto: viram itens de imagem.
            var images = paths.Where(p => ImageTools.IsImage(p) && File.Exists(p)).ToList();
            if (_document is not null && images.Count > 0)
            {
                foreach (var image in images) await PlaceImageFileAsync(image, null);
                return;
            }

            if (paths.Count > 0)
                Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "O editor abre um arquivo PDF por vez.");
            return;
        }

        if (_items.Count > 0 && !Context.Dialogs.Confirm("Descartar alterações",
                "As alterações ainda não gravadas deste documento serão descartadas. Deseja abrir outro documento?"))
        {
            return;
        }

        PdfDocumentInfo? loaded = null;
        await WhileBusyAsync("Abrindo documento…", async () => loaded = await TryReadInfoAsync(pdfs[0]));
        if (loaded is null) return;

        _thumbnails.Cancel();
        _thumbnails.Dispose();
        _thumbnails = new CancellationTokenSource();
        _document = loaded;
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
        _runs.Clear();
        HistoryEntries.Clear();
        SelectedItem = null;
        Pages.Clear();
        IReadOnlyList<(double Width, double Height)> sizes;
        try
        {
            var opened = loaded.Path;
            sizes = await Task.Run(() => PageFrame.DisplaySizes(opened));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sizes = loaded.Pages.Select(p => p.Rotation % 180 != 0 ? (p.HeightPt, p.WidthPt) : (p.WidthPt, p.HeightPt)).ToList();
        }

        for (var i = 0; i < loaded.PageCount; i++) Pages.Add(new EditorPageViewModel(i, sizes[i].Width, sizes[i].Height));

        SuggestOutputFolder(loaded.Path);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(DocumentPath));
        OnPropertyChanged(nameof(PageCountText));
        ItemsChanged();
        SelectedPage = Pages[0];
        FitWidth();
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

    partial void OnSelectedPageChanged(EditorPageViewModel? value)
    {
        if (value is null) return;
        PageNumberText = value.Number.ToString(PtBr);
        SelectedItem = null;
        OnPropertyChanged(nameof(PageWidthDip));
        OnPropertyChanged(nameof(PageHeightDip));
        OnPropertyChanged(nameof(PageItems));
        OnStateChanged();
        RenderCurrent();
        _ = LoadRunsAsync(value.Index);
    }

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(PageWidthDip));
        OnPropertyChanged(nameof(PageHeightDip));
        RenderCurrent();
    }

    partial void OnSelectedToolChanged(EditorToolChoice value)
    {
        Notice = value.Hint;
        OnPropertyChanged(nameof(Gesture));
        OnPropertyChanged(nameof(IsSignatureTool));
        OnPropertyChanged(nameof(IsStampTool));
        if (value.Tool != EditorTool.Select) SelectedItem = null;
        if (value.Tool == EditorTool.Image && _pendingImage is null && _document is not null) _ = ChooseImageAsync();
    }

    private async Task LoadRunsAsync(int index)
    {
        var path = _document?.Path;
        if (path is null || _runs.ContainsKey(index)) return;
        try
        {
            var runs = await Task.Run(() => TextRuns.Read(path, index));
            if (_document?.Path != path) return;
            _runs[index] = runs;
            if (index == CurrentIndex) OnPropertyChanged(nameof(PageRuns));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runs[index] = [];
        }
    }

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
            var scale = Scale * DpiScale;
            var pixels = page.Width * scale * page.Height * scale;
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
        if (Pages.Count == 0) return;
        SelectedPage = Pages[Math.Clamp(index, 0, Pages.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage() => GoTo(CurrentIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextPage() => GoTo(CurrentIndex + 1);

    [RelayCommand]
    private void GoToPage()
    {
        if (int.TryParse(PageNumberText.Trim(), out var number) && number >= 1 && number <= Pages.Count) GoTo(number - 1);
        else PageNumberText = (CurrentIndex + 1).ToString(PtBr);
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round(Zoom * 1.25, 2));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round(Zoom / 1.25, 2));

    [RelayCommand]
    private void FitWidth()
    {
        if (SelectedPage is null || SelectedPage.Width <= 0) return;
        var available = Math.Max(200, ViewportWidth - 48);
        var fitted = Math.Clamp(Math.Round(available / (SelectedPage.Width * 96.0 / 72.0), 2), MinZoom, MaxZoom);
        if (Math.Abs(fitted - Zoom) < 0.001) RenderCurrent();
        else Zoom = fitted;
    }

    // ---------- Itens e histórico ----------

    private void ItemsChanged()
    {
        _dirty = _items.Count > 0;
        foreach (var page in Pages) page.EditCount = _items.Count(i => i.PageIndex == page.Index);
        OnPropertyChanged(nameof(PageItems));
        OnPropertyChanged(nameof(EditCount));
        OnPropertyChanged(nameof(EditSummary));
        OnStateChanged();
    }

    /// <summary>Guarda o estado atual para "Desfazer" (alterações seguidas do mesmo tipo no mesmo item viram uma só).</summary>
    private void Remember(string description, string? coalesceKey = null)
    {
        var now = DateTime.UtcNow;
        if (coalesceKey is not null && coalesceKey == _lastCoalesce && now - _lastCoalesceTime < TimeSpan.FromSeconds(3))
        {
            _lastCoalesceTime = now;
            return;
        }

        _lastCoalesce = coalesceKey;
        _lastCoalesceTime = now;
        _undo.Push((_items.ToList(), description));
        _redo.Clear();
        HistoryEntries.Add($"{HistoryEntries.Count + 1}. {description}");
    }

    private void Add(EditItem item, bool select = true)
    {
        Remember($"{item.Kind} — página {item.PageIndex + 1}");
        _items.Add(item);
        ItemsChanged();
        if (select) SelectedItem = item;
    }

    private void Replace(EditItem old, EditItem updated, string description, string? coalesceKey = null)
    {
        var index = _items.FindIndex(i => i.Id == old.Id);
        if (index < 0) return;
        Remember(description, coalesceKey);
        _items[index] = updated;
        ItemsChanged();
        _loading = true;
        try
        {
            SelectedItem = updated;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var (items, description) = _undo.Pop();
        _redo.Push((_items.ToList(), description));
        _items.Clear();
        _items.AddRange(items);
        if (HistoryEntries.Count > 0) HistoryEntries.RemoveAt(HistoryEntries.Count - 1);
        _lastCoalesce = null;
        SelectedItem = null;
        ItemsChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var (items, description) = _redo.Pop();
        _undo.Push((_items.ToList(), description));
        _items.Clear();
        _items.AddRange(items);
        HistoryEntries.Add($"{HistoryEntries.Count + 1}. {description}");
        _lastCoalesce = null;
        SelectedItem = null;
        ItemsChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (SelectedItem is not { } item) return;
        Remember($"{item.Kind} excluído(a) — página {item.PageIndex + 1}");
        _items.RemoveAll(i => i.Id == item.Id);
        SelectedItem = null;
        ItemsChanged();
    }

    /// <summary>Item sob o ponto (o de cima primeiro).</summary>
    public EditItem? HitTest(ViewPoint point) =>
        PageItems.LastOrDefault(i => i.Bounds.Inflate(3).Contains(point));

    public void Select(EditItem? item)
    {
        SelectedItem = item;
        OnStateChanged();
    }

    public void MoveSelected(double dx, double dy)
    {
        if (SelectedItem is not { } item || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)) return;
        Replace(item, item.Moved(dx, dy), $"{item.Kind} movido(a)");
    }

    public void ResizeSelected(ViewRect rect)
    {
        if (SelectedItem is not { } item || rect.Width < 4 || rect.Height < 4) return;
        Replace(item, item.Resized(rect), $"{item.Kind} redimensionado(a)");
    }

    // ---------- Criação pela área da página ----------

    private TextStyle CurrentStyle() => new(
        FontFamily, ParseSize(FontSizeText, 12), ParseColor(ColorHex, Rgb.Black), IsBold, IsItalic, IsUnderline,
        SelectedAlignment?.Value ?? TextAlign.Left, LineSpacing);

    private static double ParseSize(string text, double fallback) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? Math.Clamp(v, TextStyle.MinSize, TextStyle.MaxSize)
            : fallback;

    private static double ParseNumber(string text, double fallback, double min, double max) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? Math.Clamp(v, min, max)
            : fallback;

    private static Rgb ParseColor(string? text, Rgb fallback) => Rgb.TryParse(text, out var c) ? c : fallback;

    private ViewRect Clamp(ViewRect rect)
    {
        var page = SelectedPage!;
        var w = Math.Min(rect.Width, page.Width);
        var h = Math.Min(rect.Height, page.Height);
        return new ViewRect(Math.Clamp(rect.X, 0, page.Width - w), Math.Clamp(rect.Y, 0, page.Height - h), w, h);
    }

    /// <summary>Clique simples na página (ferramentas de clique, ou arrasto curto das ferramentas de área).</summary>
    public async Task ClickAsync(ViewPoint point)
    {
        if (_document is null || SelectedPage is null) return;
        var page = CurrentIndex;
        switch (SelectedTool.Tool)
        {
            case EditorTool.EditText:
                await LoadRunsAsync(page);
                if (TextRuns.At(PageRuns, point) is not { } run)
                {
                    Notice = PageRuns.Count == 0
                        ? "Esta página não tem texto selecionável (pode ser uma imagem digitalizada; use o OCR antes)."
                        : "Clique em cima de uma linha de texto.";
                    return;
                }

                if (_items.OfType<ReplaceTextItem>().FirstOrDefault(r => r.PageIndex == page && r.Run.Rect == run.Rect) is { } existing)
                {
                    SelectedItem = existing;
                    return;
                }

                var style = new TextStyle("Arial", Math.Round(run.FontSize, 1), Rgb.Black);
                Add(new ReplaceTextItem(Guid.NewGuid(), page, run, run.Text, style));
                Notice = "Altere o texto à direita. O programa tenta trocar no próprio PDF; se não der, avisa ao gravar.";
                break;
            case EditorTool.Image:
                if (_pendingImage is null)
                {
                    await ChooseImageAsync();
                    if (_pendingImage is null) return;
                }

                PlaceImage(_pendingImage, _pendingImageName, point, signature: false);
                break;
            case EditorTool.Signature:
                if (SelectedSignature is null)
                {
                    Notice = "Desenhe ou importe uma assinatura à direita antes de clicar na página.";
                    return;
                }

                try
                {
                    PlaceImage(await File.ReadAllBytesAsync(SelectedSignature.Path), Path.GetFileName(SelectedSignature.Path), point, signature: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Context.Notifications.Show(NotificationKind.Error, "Assinatura indisponível", ex.Message);
                }

                break;
            case EditorTool.Stamp:
                if (SelectedStamp is null) return;
                Add(new StampItem(Guid.NewGuid(), page, Clamp(new ViewRect(point.X - 85, point.Y - 27, 170, 54)), SelectedStamp, DateTime.Now));
                break;
            case EditorTool.Comment or EditorTool.Note:
                var kind = SelectedTool.Tool == EditorTool.Comment ? NoteKind.Comment : NoteKind.Note;
                Add(new NoteItem(Guid.NewGuid(), page, new ViewPoint(Math.Clamp(point.X, 0, SelectedPage.Width - 20), Math.Clamp(point.Y, 0, SelectedPage.Height - 20)),
                    string.Empty, kind, ParseColor(ColorHex, Rgb.Yellow) == Rgb.Black ? Rgb.Yellow : ParseColor(ColorHex, Rgb.Yellow)));
                Notice = "Escreva o texto do comentário à direita.";
                break;
            default:
                // Ferramentas de área com um clique: tamanho padrão.
                var size = SelectedTool.Tool switch
                {
                    EditorTool.Text => (220.0, 28.0),
                    EditorTool.TextBox => (200.0, 60.0),
                    EditorTool.FieldCheckBox => (14.0, 14.0),
                    EditorTool.FieldRadio => (140.0, 44.0),
                    EditorTool.FieldMultiline => (220.0, 60.0),
                    EditorTool.FieldButton => (90.0, 24.0),
                    EditorTool.FieldText or EditorTool.FieldDropdown or EditorTool.FieldDate => (160.0, 22.0),
                    _ => (0.0, 0.0),
                };
                if (size.Item1 > 0) await CreateInRectAsync(new ViewRect(point.X, point.Y, size.Item1, size.Item2));
                break;
        }
    }

    /// <summary>Área arrastada na página com uma ferramenta de área.</summary>
    public async Task CreateInRectAsync(ViewRect rect)
    {
        if (_document is null || SelectedPage is null) return;
        var page = CurrentIndex;
        rect = Clamp(rect);
        var tool = SelectedTool.Tool;
        switch (tool)
        {
            case EditorTool.Text:
                var style = CurrentStyle();
                var height = Math.Max(rect.Height, style.Size * 1.4);
                Add(new TextItem(Guid.NewGuid(), page, rect with { Height = height }, "Digite o texto", style));
                SelectedTool = Tools[0];
                break;
            case EditorTool.TextBox:
                Add(new TextBoxItem(Guid.NewGuid(), page, rect, "Digite o texto", CurrentStyle() with { Size = Math.Min(ParseSize(FontSizeText, 12), 14) },
                    ParseColor(ColorHex, Rgb.Black), null));
                SelectedTool = Tools[0];
                break;
            case EditorTool.Highlight or EditorTool.Underline or EditorTool.StrikeOut:
                if (rect.Width < 3 || rect.Height < 3) return;
                var path = _document.Path;
                var quads = await Task.Run(() => TextRuns.Quads(path, page, rect));
                var markup = tool switch
                {
                    EditorTool.Highlight => MarkupKind.Highlight,
                    EditorTool.Underline => MarkupKind.Underline,
                    _ => MarkupKind.StrikeOut,
                };
                var color = tool == EditorTool.Highlight
                    ? (ColorHex is "#000000" or "" ? Rgb.Yellow : ParseColor(ColorHex, Rgb.Yellow))
                    : ParseColor(ColorHex, Rgb.Red);
                Add(new MarkupItem(Guid.NewGuid(), page, markup, quads, color), select: false);
                break;
            case EditorTool.Rectangle or EditorTool.Ellipse:
                if (rect.Width < 3 || rect.Height < 3) return;
                Add(new ShapeItem(Guid.NewGuid(), page, tool == EditorTool.Rectangle ? ShapeKind.Rectangle : ShapeKind.Ellipse, rect, [],
                    ParseColor(ColorHex, Rgb.Red), ParseNumber(StrokeWidthText, 2, 0.25, 30), FillHex.Trim().Length > 0 ? ParseColor(FillHex, Rgb.White) : null, ShapeOpacity / 100), select: false);
                break;
            case EditorTool.FieldText or EditorTool.FieldMultiline or EditorTool.FieldCheckBox or EditorTool.FieldRadio
                or EditorTool.FieldDropdown or EditorTool.FieldDate or EditorTool.FieldButton:
                var kind = tool switch
                {
                    EditorTool.FieldText => FieldKind.Text,
                    EditorTool.FieldMultiline => FieldKind.MultilineText,
                    EditorTool.FieldCheckBox => FieldKind.CheckBox,
                    EditorTool.FieldRadio => FieldKind.Radio,
                    EditorTool.FieldDropdown => FieldKind.Dropdown,
                    EditorTool.FieldDate => FieldKind.Date,
                    _ => FieldKind.Button,
                };
                if (kind == FieldKind.CheckBox) rect = rect with { Width = Math.Max(10, Math.Min(rect.Width, rect.Height)), Height = Math.Max(10, Math.Min(rect.Width, rect.Height)) };
                if (rect.Width < 8 || rect.Height < 8) return;
                IReadOnlyList<string> options = kind is FieldKind.Radio or FieldKind.Dropdown ? ["Opção 1", "Opção 2"] : [];
                var prefix = kind switch
                {
                    FieldKind.CheckBox => "caixa",
                    FieldKind.Radio => "opcoes",
                    FieldKind.Dropdown => "lista",
                    FieldKind.Date => "data",
                    FieldKind.Button => "botao",
                    _ => "campo",
                };
                Add(new FieldItem(Guid.NewGuid(), page, rect, kind, $"{prefix}_{++_fieldCounter}", options));
                SelectedTool = Tools[0];
                break;
        }
    }

    /// <summary>Linha, seta ou desenho livre.</summary>
    public void CreateFromPoints(IReadOnlyList<ViewPoint> points)
    {
        if (_document is null || points.Count < 2) return;
        var bounds = EditItem.BoundsOf(points);
        if (bounds.Width < 2 && bounds.Height < 2) return;
        var shape = SelectedTool.Tool switch
        {
            EditorTool.Arrow => ShapeKind.Arrow,
            EditorTool.Line => ShapeKind.Line,
            _ => ShapeKind.Ink,
        };
        IReadOnlyList<ViewPoint> kept = shape == ShapeKind.Ink ? points : [points[0], points[^1]];
        Add(new ShapeItem(Guid.NewGuid(), CurrentIndex, shape, bounds, kept, ParseColor(ColorHex, Rgb.Red), ParseNumber(StrokeWidthText, 2, 0.25, 30), Opacity: ShapeOpacity / 100), select: false);
    }

    private async Task ChooseImageAsync()
    {
        var images = string.Join(";", ImageTools.Extensions.Select(e => "*" + e));
        var picked = Context.Dialogs.PickFiles("Escolher imagem", $"Imagens|{images}", false, StartFolder());
        if (picked.Count == 0)
        {
            if (SelectedTool.Tool == EditorTool.Image) SelectedTool = Tools[0];
            return;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(picked[0]);
            ImageTools.Size(data);
            _pendingImage = data;
            _pendingImageName = Path.GetFileName(picked[0]);
            Notice = $"Clique na página para colocar \"{_pendingImageName}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Imagem não pôde ser usada", ex.Message);
        }
    }

    private async Task PlaceImageFileAsync(string path, ViewPoint? point)
    {
        try
        {
            var data = await File.ReadAllBytesAsync(path);
            PlaceImage(data, Path.GetFileName(path), point ?? new ViewPoint(SelectedPage!.Width / 2, SelectedPage.Height / 2), signature: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Imagem não pôde ser usada", ex.Message);
        }
    }

    private void PlaceImage(byte[] data, string name, ViewPoint center, bool signature)
    {
        int width, height;
        try
        {
            (width, height) = ImageTools.Size(data);
        }
        catch (InvalidDataException ex)
        {
            Context.Notifications.Show(NotificationKind.Error, "Imagem não pôde ser usada", ex.Message);
            return;
        }

        var maxWidth = signature ? 160.0 : Math.Min(220, SelectedPage!.Width * 0.5);
        var w = Math.Min(maxWidth, width * 0.75);
        var h = w * height / Math.Max(1, width);
        Add(new ImageItem(Guid.NewGuid(), CurrentIndex, Clamp(new ViewRect(center.X - w / 2, center.Y - h / 2, w, h)), data, name, IsSignature: signature));
        if (!signature)
        {
            _pendingImage = null;
            SelectedTool = Tools[0];
        }
    }

    // ---------- Propriedades do item selecionado ----------

    [ObservableProperty] private string _itemText = string.Empty;
    [ObservableProperty] private string _fontFamily = "Arial";
    [ObservableProperty] private string _fontSizeText = "12";
    [ObservableProperty] private bool _isBold;
    [ObservableProperty] private bool _isItalic;
    [ObservableProperty] private bool _isUnderline;
    [ObservableProperty] private Choice<TextAlign>? _selectedAlignment;
    [ObservableProperty] private double _lineSpacing = 1.0;
    [ObservableProperty] private string _colorHex = "#000000";
    [ObservableProperty] private string _fillHex = string.Empty;
    [ObservableProperty] private string _strokeWidthText = "2";
    [ObservableProperty] private double _shapeOpacity = 100;
    [ObservableProperty] private double _imageOpacity = 100;
    [ObservableProperty] private double _imageRotation;
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropRight;
    [ObservableProperty] private double _cropBottom;
    [ObservableProperty] private string _fieldName = string.Empty;
    [ObservableProperty] private string _fieldOptions = string.Empty;
    [ObservableProperty] private string _fieldValue = string.Empty;
    [ObservableProperty] private Choice<ButtonAction>? _fieldAction;
    [ObservableProperty] private bool _allowOverlay = true;

    public bool HasSelectedItem => SelectedItem is not null;
    public bool HasItemText => SelectedItem is TextItem or TextBoxItem or NoteItem or ReplaceTextItem or MarkupItem or ShapeItem;
    public string ItemTextLabel => SelectedItem switch
    {
        ReplaceTextItem => "Texto novo",
        MarkupItem or ShapeItem => "Comentário (opcional)",
        NoteItem => "Texto do comentário",
        _ => "Texto",
    };
    public bool HasTextStyle => SelectedItem is TextItem or TextBoxItem or ReplaceTextItem || SelectedItem is null && SelectedTool.Tool is EditorTool.Text or EditorTool.TextBox;
    public bool HasColor => SelectedItem is not (ImageItem or StampItem or FieldItem);
    public bool IsShapeSelected => SelectedItem is ShapeItem || SelectedItem is null && SelectedTool.Tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Line or EditorTool.Ink;
    public bool IsImageSelected => SelectedItem is ImageItem;
    public bool IsFieldSelected => SelectedItem is FieldItem;
    public bool FieldHasOptions => SelectedItem is FieldItem { Field: FieldKind.Radio or FieldKind.Dropdown };
    public bool FieldIsButton => SelectedItem is FieldItem { Field: FieldKind.Button };
    public bool IsReplaceSelected => SelectedItem is ReplaceTextItem;
    public string OriginalText => SelectedItem is ReplaceTextItem r ? r.Run.Text : string.Empty;
    public string SelectedKind => SelectedItem?.Kind ?? SelectedTool.Label;

    partial void OnSelectedItemChanged(EditItem? value)
    {
        foreach (var name in new[]
                 {
                     nameof(HasSelectedItem), nameof(HasItemText), nameof(ItemTextLabel), nameof(HasTextStyle), nameof(HasColor),
                     nameof(IsShapeSelected), nameof(IsImageSelected), nameof(IsFieldSelected), nameof(FieldHasOptions),
                     nameof(FieldIsButton), nameof(IsReplaceSelected), nameof(OriginalText), nameof(SelectedKind), nameof(IsStampTool),
                 })
        {
            OnPropertyChanged(name);
        }

        OnStateChanged();
        if (value is null || _loading) return;
        _loading = true;
        try
        {
            switch (value)
            {
                case TextItem t:
                    ItemText = t.Text;
                    LoadStyle(t.Style);
                    break;
                case TextBoxItem b:
                    ItemText = b.Text;
                    LoadStyle(b.Style);
                    break;
                case ReplaceTextItem r:
                    ItemText = r.NewText;
                    LoadStyle(r.Style);
                    AllowOverlay = r.AllowOverlay;
                    break;
                case NoteItem n:
                    ItemText = n.Text;
                    ColorHex = n.Color.Hex;
                    break;
                case MarkupItem m:
                    ItemText = m.Comment ?? string.Empty;
                    ColorHex = m.Color.Hex;
                    break;
                case ShapeItem s:
                    ItemText = s.Comment ?? string.Empty;
                    ColorHex = s.Stroke.Hex;
                    FillHex = s.Fill?.Hex ?? string.Empty;
                    StrokeWidthText = s.StrokeWidth.ToString("0.#", CultureInfo.InvariantCulture);
                    ShapeOpacity = Math.Round(s.Opacity * 100);
                    break;
                case ImageItem i:
                    ImageOpacity = Math.Round(i.Opacity * 100);
                    ImageRotation = i.Rotation;
                    CropLeft = i.Crop.Left * 100;
                    CropTop = i.Crop.Top * 100;
                    CropRight = i.Crop.Right * 100;
                    CropBottom = i.Crop.Bottom * 100;
                    break;
                case FieldItem f:
                    FieldName = f.Name;
                    FieldOptions = string.Join(Environment.NewLine, f.Options);
                    FieldValue = f.Value ?? string.Empty;
                    FieldAction = ButtonActions.First(a => a.Value == f.Action);
                    break;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadStyle(TextStyle style)
    {
        FontFamily = style.Family;
        FontSizeText = style.Size.ToString("0.#", CultureInfo.InvariantCulture);
        IsBold = style.Bold;
        IsItalic = style.Italic;
        IsUnderline = style.Underline;
        SelectedAlignment = Alignments.First(a => a.Value == style.Align);
        LineSpacing = style.LineSpacing;
        ColorHex = style.Color.Hex;
    }

    partial void OnItemTextChanged(string value) => ApplyProperty("text");
    partial void OnFontFamilyChanged(string value) => ApplyProperty("style");
    partial void OnFontSizeTextChanged(string value) => ApplyProperty("style");
    partial void OnIsBoldChanged(bool value) => ApplyProperty("style");
    partial void OnIsItalicChanged(bool value) => ApplyProperty("style");
    partial void OnIsUnderlineChanged(bool value) => ApplyProperty("style");
    partial void OnSelectedAlignmentChanged(Choice<TextAlign>? value) => ApplyProperty("style");
    partial void OnLineSpacingChanged(double value) => ApplyProperty("style");
    partial void OnColorHexChanged(string value) => ApplyProperty("color");
    partial void OnFillHexChanged(string value) => ApplyProperty("shape");
    partial void OnStrokeWidthTextChanged(string value) => ApplyProperty("shape");
    partial void OnShapeOpacityChanged(double value) => ApplyProperty("shape");
    partial void OnImageOpacityChanged(double value) => ApplyProperty("image");
    partial void OnImageRotationChanged(double value) => ApplyProperty("image");
    partial void OnCropLeftChanged(double value) => ApplyProperty("image");
    partial void OnCropTopChanged(double value) => ApplyProperty("image");
    partial void OnCropRightChanged(double value) => ApplyProperty("image");
    partial void OnCropBottomChanged(double value) => ApplyProperty("image");
    partial void OnFieldNameChanged(string value) => ApplyProperty("field");
    partial void OnFieldOptionsChanged(string value) => ApplyProperty("field");
    partial void OnFieldValueChanged(string value) => ApplyProperty("field");
    partial void OnFieldActionChanged(Choice<ButtonAction>? value) => ApplyProperty("field");
    partial void OnAllowOverlayChanged(bool value) => ApplyProperty("overlay");

    /// <summary>Aplica as propriedades editadas ao item selecionado (gera um novo item imutável).</summary>
    private void ApplyProperty(string group)
    {
        if (_loading || SelectedItem is not { } item) return;
        var color = ParseColor(ColorHex, Rgb.Black);
        Rgb? typedColor = Rgb.TryParse(ColorHex, out var parsedColor) ? parsedColor : null;
        Rgb? typedFill = Rgb.TryParse(FillHex, out var parsedFill) ? parsedFill : null;
        var noFill = FillHex.Trim().Length == 0;
        var comment = ItemText.Length == 0 ? null : ItemText;
        EditItem? updated = item switch
        {
            TextItem t => t with { Text = ItemText, Style = CurrentStyle() },
            TextBoxItem b => b with { Text = ItemText, Style = CurrentStyle(), Border = color },
            ReplaceTextItem r => r with { NewText = ItemText.Replace('\n', ' ').Replace("\r", string.Empty), Style = CurrentStyle(), AllowOverlay = AllowOverlay },
            NoteItem n => n with { Text = ItemText, Color = typedColor ?? n.Color },
            MarkupItem m => m with { Comment = comment, Color = typedColor ?? m.Color },
            ShapeItem sh => sh with
            {
                Comment = comment,
                Stroke = typedColor ?? sh.Stroke,
                Fill = noFill ? null : typedFill ?? sh.Fill,
                StrokeWidth = ParseNumber(StrokeWidthText, sh.StrokeWidth, 0.25, 30),
                Opacity = Math.Clamp(ShapeOpacity / 100, 0.1, 1),
            },
            ImageItem i => i with
            {
                Opacity = Math.Clamp(ImageOpacity / 100, 0.05, 1),
                Rotation = ImageRotation,
                Crop = new ImageCrop(CropLeft / 100, CropTop / 100, CropRight / 100, CropBottom / 100),
            },
            FieldItem f => f with
            {
                Name = FieldName.Trim().Length == 0 ? f.Name : FieldName.Trim(),
                Options = FieldOptions.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList(),
                Value = FieldValue.Length == 0 ? null : FieldValue,
                Action = FieldAction?.Value ?? f.Action,
            },
            _ => null,
        };

        if (updated is null || updated == item) return;
        Replace(item, updated, $"{item.Kind} alterado(a)", $"{item.Id}:{group}");
    }

    [RelayCommand]
    private void PickColor(string hex)
    {
        if (Rgb.TryParse(hex, out _)) ColorHex = hex;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReplaceImageAsync()
    {
        if (SelectedItem is not ImageItem item) return;
        var images = string.Join(";", ImageTools.Extensions.Select(e => "*" + e));
        var picked = Context.Dialogs.PickFiles("Substituir imagem", $"Imagens|{images}", false, StartFolder());
        if (picked.Count == 0) return;
        try
        {
            var data = await File.ReadAllBytesAsync(picked[0]);
            var (w, h) = ImageTools.Size(data);
            var rect = item.Rect with { Height = item.Rect.Width * h / Math.Max(1, w) };
            Replace(item, item with { Data = data, SourceName = Path.GetFileName(picked[0]), Rect = rect, Crop = ImageCrop.None }, "Imagem substituída");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Imagem não pôde ser usada", ex.Message);
        }
    }

    // ---------- Assinaturas ----------

    [ObservableProperty] private SignatureChoice? _selectedSignature;

    private void LoadSignatures()
    {
        var current = SelectedSignature?.Path;
        Signatures.Clear();
        foreach (var path in Context.Editor.Signatures.List())
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                Signatures.Add(new SignatureChoice(path, image));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                // Arquivo ilegível: fica de fora da lista.
            }
        }

        SelectedSignature = Signatures.FirstOrDefault(s => s.Path == current) ?? Signatures.FirstOrDefault();
    }

    [RelayCommand]
    private void DrawSignature()
    {
        var png = Context.Dialogs.DrawSignature();
        if (png is null) return;
        var path = Context.Editor.Signatures.Add(png);
        LoadSignatures();
        SelectedSignature = Signatures.FirstOrDefault(s => s.Path == path);
        SelectedTool = Tools.First(t => t.Tool == EditorTool.Signature);
        Notice = "Assinatura guardada neste computador. Clique na página para colocá-la.";
    }

    [RelayCommand]
    private async Task ImportSignatureAsync()
    {
        var picked = Context.Dialogs.PickFiles("Importar assinatura", "Imagens (PNG com fundo transparente é o ideal)|*.png;*.jpg;*.jpeg;*.bmp", false, StartFolder());
        if (picked.Count == 0) return;
        try
        {
            var data = await File.ReadAllBytesAsync(picked[0]);
            var png = data.Length > 8 && data[0] == 0x89 ? data : ImageTools.Prepare(data, ImageCrop.None, 0.999);
            var path = Context.Editor.Signatures.Add(png);
            LoadSignatures();
            SelectedSignature = Signatures.FirstOrDefault(s => s.Path == path);
            SelectedTool = Tools.First(t => t.Tool == EditorTool.Signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Assinatura não importada", ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteSignature()
    {
        if (SelectedSignature is null) return;
        if (!Context.Dialogs.Confirm("Excluir assinatura", "Excluir esta assinatura guardada? Os PDFs já gravados não mudam.")) return;
        try
        {
            Context.Editor.Signatures.Delete(SelectedSignature.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Assinatura não excluída", ex.Message);
        }

        LoadSignatures();
    }

    // ---------- Carimbos ----------

    [ObservableProperty] private StampTemplate? _selectedStamp;
    [ObservableProperty] private string _stampText = string.Empty;
    [ObservableProperty] private string _stampColor = "#D13438";
    [ObservableProperty] private bool _stampShowDate;
    [ObservableProperty] private string _stampSubtitle = string.Empty;

    private void LoadStamps()
    {
        var current = SelectedStamp?.Id;
        Stamps.Clear();
        foreach (var stamp in Context.Editor.Stamps.Load()) Stamps.Add(stamp);
        SelectedStamp = Stamps.FirstOrDefault(s => s.Id == current) ?? Stamps.FirstOrDefault();
    }

    partial void OnSelectedStampChanged(StampTemplate? value)
    {
        if (value is null) return;
        StampText = value.Text;
        StampColor = value.Color.Hex;
        StampShowDate = value.ShowDate;
        StampSubtitle = value.Subtitle ?? string.Empty;
    }

    [RelayCommand]
    private void NewStamp()
    {
        SelectedStamp = null;
        StampText = "NOVO CARIMBO";
        StampColor = "#0067C0";
        StampShowDate = false;
        StampSubtitle = string.Empty;
        Notice = "Ajuste o texto e a cor e clique em \"Salvar modelo\".";
    }

    [RelayCommand]
    private void SaveStamp()
    {
        var text = StampText.Trim();
        if (text.Length == 0 || text.Length > StampTemplate.MaxTextLength || !Rgb.TryParse(StampColor, out var color))
        {
            Context.Notifications.Show(NotificationKind.Warning, "Carimbo inválido", $"Digite um texto de até {StampTemplate.MaxTextLength} caracteres e uma cor no formato #RRGGBB.");
            return;
        }

        var stamps = Stamps.ToList();
        var id = SelectedStamp?.Id ?? "carimbo-" + Guid.NewGuid().ToString("N")[..8];
        var template = new StampTemplate(id, text, color, StampShowDate, StampSubtitle.Trim().Length == 0 ? null : StampSubtitle.Trim());
        var index = stamps.FindIndex(s => s.Id == id);
        if (index >= 0) stamps[index] = template;
        else stamps.Add(template);
        SaveStamps(stamps, id);
    }

    [RelayCommand]
    private void DeleteStamp()
    {
        if (SelectedStamp is null) return;
        SaveStamps(Stamps.Where(s => s.Id != SelectedStamp.Id).ToList(), null);
    }

    [RelayCommand]
    private void RestoreStamps()
    {
        var stamps = Stamps.ToList();
        foreach (var example in StampLibrary.Defaults)
        {
            if (stamps.All(s => s.Id != example.Id)) stamps.Add(example);
        }

        SaveStamps(stamps, SelectedStamp?.Id);
    }

    private void SaveStamps(List<StampTemplate> stamps, string? select)
    {
        try
        {
            Context.Editor.Stamps.Save(stamps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Biblioteca de carimbos não gravada", ex.Message);
        }

        LoadStamps();
        if (select is not null) SelectedStamp = Stamps.FirstOrDefault(s => s.Id == select) ?? SelectedStamp;
    }

    // ---------- Gravar ----------

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_document is null || _items.Count == 0) return;
        var items = _items.ToList();
        var source = _document.Path;
        EditResult? result = null;
        await RunAsync(PdfEditor.OperationName, Path.GetFileName(source), "Gravando as alterações num novo arquivo…", async (progress, ct) =>
        {
            progress.Report(0.2);
            result = await Context.Editor.Editor.SaveAsync(source, items, CreateTarget(), ct);
            progress.Report(1);
            return [result.File];
        }, () =>
        {
            if (result is null) return null;
            var text = $"{result.Applied} alteração(ões) aplicada(s).";
            if (result.Notes.Count > 0) text += "\n" + string.Join("\n", result.Notes.Take(4));
            return text;
        });

        if (result is not null)
        {
            _dirty = false;
            Notice = result.Overlaid > 0
                ? "Gravado. Alguns textos existentes foram cobertos em vez de trocados — veja a notificação."
                : "Gravado num novo arquivo. As alterações continuam aqui, se quiser ajustar e gravar de novo.";
        }
    }

    public bool HasUnsavedChanges => _dirty;
}
