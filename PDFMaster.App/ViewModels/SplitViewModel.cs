using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

public enum SplitMode
{
    EveryPage,
    EveryN,
    Ranges,
    CutPoints,
    Size,
    Bookmarks,
}

public sealed partial class SplitViewModel : ToolViewModelBase
{
    private PdfDocumentInfo? _document;
    private IReadOnlyList<int> _bookmarkPages = [];

    public SplitViewModel(ToolContext context)
        : base(context, "Dividir PDF", "Separe um documento em vários arquivos. O original não é alterado.")
    {
    }

    public override bool IsEmpty => _document is null;
    public bool HasDocument => _document is not null;
    public string DocumentName => _document is null ? string.Empty : Path.GetFileName(_document.Path);

    public string DocumentDetails => _document is null
        ? string.Empty
        : $"{_document.PageCount} página(s) · {FileSizeFormatter.Format(_document.SizeBytes)} · " +
          (_bookmarkPages.Count == 0 ? "sem marcadores" : $"{_bookmarkPages.Count} marcador(es) de primeiro nível");

    public bool HasBookmarks => _bookmarkPages.Count > 0;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    // Um botão de opção por modo.
    [ObservableProperty] private bool _isEveryPage = true;
    [ObservableProperty] private bool _isEveryN;
    [ObservableProperty] private bool _isRanges;
    [ObservableProperty] private bool _isCutPoints;
    [ObservableProperty] private bool _isSize;
    [ObservableProperty] private bool _isBookmarks;

    [ObservableProperty] private string _pagesPerFileText = "2";
    [ObservableProperty] private string _rangesText = string.Empty;
    [ObservableProperty] private string _cutPointsText = string.Empty;
    [ObservableProperty] private string _sizeMbText = "5";
    [ObservableProperty] private string _namePattern = SplitNameFormatter.DefaultPattern;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string? _problem;

    public SplitMode Mode =>
        IsEveryN ? SplitMode.EveryN
        : IsRanges ? SplitMode.Ranges
        : IsCutPoints ? SplitMode.CutPoints
        : IsSize ? SplitMode.Size
        : IsBookmarks ? SplitMode.Bookmarks
        : SplitMode.EveryPage;

    partial void OnIsEveryPageChanged(bool value) => Refresh();
    partial void OnIsEveryNChanged(bool value) => Refresh();
    partial void OnIsRangesChanged(bool value) => Refresh();
    partial void OnIsCutPointsChanged(bool value) => Refresh();
    partial void OnIsSizeChanged(bool value) => Refresh();
    partial void OnIsBookmarksChanged(bool value) => Refresh();
    partial void OnPagesPerFileTextChanged(string value) => Refresh();
    partial void OnRangesTextChanged(string value) => Refresh();
    partial void OnCutPointsTextChanged(string value) => Refresh();
    partial void OnSizeMbTextChanged(string value) => Refresh();
    partial void OnNamePatternChanged(string value) => Refresh();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PickFileCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanRun() => !IsBusy && _document is not null && Problem is null;

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
                Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "Esta ferramenta trabalha apenas com arquivos PDF.");
            return;
        }

        if (pdfs.Count > 1)
        {
            Context.Notifications.Show(NotificationKind.Info, "Um documento por vez",
                $"A divisão trabalha com um arquivo por vez; foi aberto \"{Path.GetFileName(pdfs[0])}\".");
        }

        await WhileBusyAsync("Lendo documento…", async () =>
        {
            var info = await TryReadInfoAsync(pdfs[0]);
            if (info is null) return;

            _document = info;
            _bookmarkPages = info.Bookmarks.Where(b => b.Level == 0).Select(b => b.PageIndex).Distinct().OrderBy(p => p).ToList();
            if (!HasBookmarks && IsBookmarks) IsEveryPage = true;
            Thumbnail = null;
            SuggestOutputFolder(info.Path);
            try
            {
                await Context.Thumbnails.LoadAsync(info.Path, [0], (_, image) => Thumbnail = image, CancellationToken.None);
            }
            catch (PDFMaster.Rendering.PdfRenderException)
            {
                // Sem miniatura; a divisão não depende da renderização.
            }
        });

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(DocumentDetails));
        OnPropertyChanged(nameof(HasBookmarks));
        Refresh();
    }

    private bool TryGetSizeBytes(out long bytes)
    {
        bytes = 0;
        var text = SizeMbText.Trim().Replace('.', ',');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out var megabytes)) return false;
        if (megabytes <= 0 || megabytes > 100_000) return false;
        bytes = (long)(megabytes * 1024 * 1024);
        return bytes >= 1024;
    }

    /// <summary>Calcula os grupos de páginas; null quando o modo é por tamanho (medido ao executar).</summary>
    private IReadOnlyList<IReadOnlyList<int>>? Plan(PdfDocumentInfo document)
    {
        switch (Mode)
        {
            case SplitMode.EveryN:
                if (!int.TryParse(PagesPerFileText.Trim(), out var perFile) || perFile < 1)
                    throw new PageRangeException("Informe quantas páginas cada arquivo deve ter (1 ou mais).");
                return SplitPlanner.EveryN(document.PageCount, perFile);
            case SplitMode.Ranges:
                return SplitPlanner.ByRanges(RangesText, document.PageCount);
            case SplitMode.CutPoints:
                if (string.IsNullOrWhiteSpace(CutPointsText))
                    throw new PageRangeException("Informe as páginas em que cada novo arquivo começa.");
                return SplitPlanner.AtCutPoints(PageRangeParser.Parse(CutPointsText, document.PageCount), document.PageCount);
            case SplitMode.Bookmarks:
                if (_bookmarkPages.Count == 0)
                    throw new PageRangeException("Este documento não tem marcadores.");
                return SplitPlanner.AtCutPoints(_bookmarkPages, document.PageCount);
            case SplitMode.Size:
                if (!TryGetSizeBytes(out _))
                    throw new PageRangeException("Informe o tamanho máximo de cada arquivo em MB (ex.: 5 ou 0,5).");
                return null;
            default:
                return SplitPlanner.EveryPage(document.PageCount);
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Mode));
        if (_document is null)
        {
            Problem = null;
            Preview = "Abra um PDF para ver como ele será dividido.";
            OnStateChanged();
            return;
        }

        try
        {
            var groups = Plan(_document);
            Problem = null;
            if (groups is null)
            {
                Preview = "A quantidade de arquivos é calculada ao executar, medindo o tamanho real de cada parte. " +
                          "Uma página sozinha maior que o limite vira um arquivo próprio.";
            }
            else
            {
                var baseName = Path.GetFileNameWithoutExtension(_document.Path);
                var names = groups.Take(3).Select((g, i) =>
                    SplitNameFormatter.Format(NamePattern, baseName, i + 1, groups.Count, g.Min() + 1, g.Max() + 1));
                Preview = $"Serão criados {groups.Count} arquivo(s): {string.Join(", ", names)}" +
                          (groups.Count > 3 ? "…" : string.Empty);
            }
        }
        catch (PageRangeException ex)
        {
            Problem = ex.Message;
            Preview = string.Empty;
        }

        OnStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        var document = _document;
        if (document is null) return;

        IReadOnlyList<IReadOnlyList<int>>? groups;
        try
        {
            groups = Plan(document);
        }
        catch (PageRangeException ex)
        {
            Problem = ex.Message;
            return;
        }

        var pattern = NamePattern;
        var target = CreateTarget();
        TryGetSizeBytes(out var maxBytes);

        await RunAsync("Dividir PDF", Path.GetFileName(document.Path), "Dividindo o documento…", async (progress, ct) =>
        {
            if (groups is null)
            {
                // Metade do progresso para medir os tamanhos, metade para gravar.
                var measuring = new Progress<double>(v => progress.Report(v * 0.5));
                groups = await Context.Organizer.PlanSplitBySizeAsync(document.Path, maxBytes, measuring, ct);
                var writing = new Progress<double>(v => progress.Report(0.5 + v * 0.5));
                return await Context.Organizer.SplitAsync(document.Path, groups, pattern, target, writing, ct);
            }

            return await Context.Organizer.SplitAsync(document.Path, groups, pattern, target, progress, ct);
        });
    }
}
