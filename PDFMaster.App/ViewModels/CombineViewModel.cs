using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.Conversion;
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
        Action<CombineFileViewModel, int> move, Action<CombineFileViewModel> remove, bool isImage = false)
    {
        Info = info;
        IsImage = isImage;
        _changed = changed;
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, 1));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public PdfDocumentInfo Info { get; }
    public string Path => Info.Path;
    public string Name => System.IO.Path.GetFileName(Info.Path);
    /// <summary>Foto ou imagem (vira página(s) de PDF na hora de juntar).</summary>
    public bool IsImage { get; }
    public string Details => IsImage
        ? $"Imagem{(Info.PageCount > 1 ? $" com {Info.PageCount} páginas" : string.Empty)} · {FileSizeFormatter.Format(Info.SizeBytes)}"
        : $"{Info.PageCount} página(s) · {FileSizeFormatter.Format(Info.SizeBytes)}";

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
        : base(context, "Mesclar PDF", MergeSubtitle)
    {
        ImagePageSizes =
        [
            new(PdfPageSizeMode.FitImage, "Do tamanho da imagem"),
            new(PdfPageSizeMode.A4, "A4 (imagem ajustada à página)"),
            new(PdfPageSizeMode.Letter, "Carta (imagem ajustada à página)"),
        ];
        _selectedImagePageSize = ImagePageSizes[0];
    }

    private const string MergeSubtitle = "Junte PDFs e fotos (JPG, PNG, TIFF…) em um único documento, na ordem da lista.";

    public IReadOnlyList<Choice<PdfPageSizeMode>> ImagePageSizes { get; }

    [ObservableProperty]
    private Choice<PdfPageSizeMode> _selectedImagePageSize;

    public bool HasImages => Files.Any(f => f.IsImage);

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
            : MergeSubtitle;
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
        var picked = Context.Dialogs.PickFiles("Adicionar PDFs e imagens",
            "PDFs e imagens|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif|PDF|*.pdf|Imagens|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif",
            multiple: true, StartFolder());
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    public override async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        // PDFs e imagens, na ordem recebida; pastas trazem os arquivos de dentro (sem subpastas), por nome.
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase).Where(IsSupported));
            else if (File.Exists(path) && IsSupported(path)) files.Add(path);
        }

        if (files.Count == 0)
        {
            if (paths.Count > 0)
                Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF ou imagem na seleção",
                    "Esta ferramenta junta arquivos PDF e imagens (JPG, PNG, BMP, TIFF e GIF).");
            return;
        }

        await WhileBusyAsync("Lendo documentos…", async () =>
        {
            foreach (var path in files)
            {
                CombineFileViewModel item;
                if (IsImage(path))
                {
                    var pages = await Task.Run(() => ImageFrames(path));
                    if (pages == 0)
                    {
                        Context.Notifications.Show(NotificationKind.Warning, "Imagem não reconhecida",
                            $"{System.IO.Path.GetFileName(path)} não pôde ser lida como imagem.");
                        continue;
                    }

                    var info = new PdfDocumentInfo(path, new FileInfo(path).Length,
                        Enumerable.Repeat(new PdfPageInfo(0, 0, 0), pages).ToList(), [], null);
                    item = new CombineFileViewModel(info, Refresh, Move, Remove, isImage: true);
                    item.Thumbnail = ImageThumbnail(path);
                }
                else
                {
                    var info = await TryReadInfoAsync(path);
                    if (info is null) continue;
                    item = new CombineFileViewModel(info, Refresh, Move, Remove);
                    LoadThumbnail(item);
                }

                item.Validate();
                Files.Add(item);
                SuggestOutputFolder(path);
            }
        });

        Refresh();
    }

    private static bool IsImage(string path) =>
        ImagesToPdfConverter.Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private static bool IsSupported(string path) => IsImage(path) || SupportedFiles.GetKind(path) == FileKind.Pdf;

    /// <summary>Quantidade de quadros (páginas) da imagem; 0 quando o arquivo não é uma imagem válida.</summary>
    private static int ImageFrames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            // GIF animado vira uma página só (o primeiro quadro), como na conversão de imagem para PDF.
            return decoder is GifBitmapDecoder ? 1 : decoder.Frames.Count;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return 0;
        }
    }

    private static ImageSource? ImageThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 112;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
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
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(IsEmpty));

        string? problem = null;
        var summary = string.Empty;

        if (Files.Count == 0)
        {
            summary = "Adicione PDFs ou imagens para começar.";
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
        var imageOptions = new ImagesToPdfOptions(SelectedImagePageSize.Value, PdfPageOrientation.Auto, SelectedImagePageSize.Value == PdfPageSizeMode.FitImage ? 0 : 10);
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PDFMaster", "mesclar-" + Guid.NewGuid().ToString("N"));

        // Cada imagem vira um PDF temporário (com o nome dela, para o marcador) antes de juntar.
        async Task<IReadOnlyList<string>> PdfPathsAsync(IProgress<double> progress, CancellationToken ct)
        {
            var paths = new List<string>();
            var images = files.Count(f => f.IsImage);
            var done = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!file.IsImage)
                {
                    paths.Add(file.Path);
                    continue;
                }

                var folder = System.IO.Path.Combine(work, paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var pdf = await Context.Conversion.ImagesToPdf.ConvertAsync([file.Path], System.IO.Path.GetFileNameWithoutExtension(file.Path), imageOptions,
                    new OutputTarget(folder, OverwritePolicy.AutoRename, SubfolderMode.None), null, ct);
                paths.Add(pdf.Path);
                progress.Report(0.3 * ++done / images);
            }

            return paths;
        }

        try
        {
            if (IsInterleave)
            {
                if (!TryGetPagesPerTurn(out var perTurn)) return;
                var remainder = StopAtShortest ? InterleaveRemainder.StopAtShortest : InterleaveRemainder.AppendRemaining;
                await RunAsync("Intercalar documentos", inputName, "Intercalando páginas…", async (progress, ct) =>
                {
                    var paths = await PdfPathsAsync(progress, ct);
                    var inputs = files.Select((f, i) => new InterleaveInput(paths[i], f.Reverse)).ToList();
                    return [await Context.Organizer.InterleaveAsync(inputs, perTurn, remainder, name, target, progress, ct)];
                });
            }
            else
            {
                var bookmarks = BookmarkPerFile;
                await RunAsync("Mesclar PDF", inputName, HasImages ? "Convertendo imagens e mesclando…" : "Mesclando documentos…", async (progress, ct) =>
                {
                    var paths = await PdfPathsAsync(progress, ct);
                    var inputs = files.Select((f, i) => new MergeInput(paths[i], f.RangeText)).ToList();
                    return [await Context.Organizer.MergeAsync(inputs, name, target, bookmarks, progress, ct)];
                });
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Pasta temporária: o sistema limpa depois.
            }
        }
    }
}
