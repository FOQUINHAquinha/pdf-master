using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

public enum ConversionKind
{
    PdfToImage,
    ImageToPdf,
    OfficeToPdf,
    TextHtmlToPdf,
    PdfToWord,
    PdfToText,
}

public sealed partial class ConvertFileViewModel : ObservableObject
{
    public ConvertFileViewModel(string path, Action<ConvertFileViewModel, int> move, Action<ConvertFileViewModel> remove)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(Path);
        var info = new FileInfo(Path);
        Details = $"{FileSizeFormatter.Format(info.Exists ? info.Length : 0)} · {System.IO.Path.GetDirectoryName(Path)}";
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, 1));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public string Name { get; }
    public string Details { get; }

    /// <summary>Resultado da última conversão deste arquivo (vazio antes de converter).</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _failed;

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>Tela única das conversões: a lista de arquivos é a mesma, mudam as opções de cada tipo.</summary>
public sealed partial class ConvertViewModel : ToolViewModelBase
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public ConvertViewModel(ToolContext context, ConversionKind kind)
        : base(context, TitleOf(kind), SubtitleOf(kind))
    {
        Kind = kind;
        ImageFormats =
        [
            new(ImageFormat.Png, "PNG — sem perda, ideal para texto e desenhos"),
            new(ImageFormat.Jpeg, "JPG — menor, ideal para fotos"),
            new(ImageFormat.Tiff, "TIFF — sem perda, aceita várias páginas"),
            new(ImageFormat.Bmp, "BMP — sem compressão"),
        ];
        PageSizes =
        [
            new(PdfPageSizeMode.FitImage, "Do tamanho de cada imagem"),
            new(PdfPageSizeMode.A4, "A4"),
            new(PdfPageSizeMode.Letter, "Carta (Letter)"),
        ];
        Orientations =
        [
            new(PdfPageOrientation.Auto, "Automática (conforme a imagem)"),
            new(PdfPageOrientation.Portrait, "Retrato"),
            new(PdfPageOrientation.Landscape, "Paisagem"),
        ];
        TextFormats =
        [
            new(TextExportFormat.Txt, "TXT — texto simples"),
            new(TextExportFormat.Html, "HTML — página da web simples"),
        ];
        _selectedImageFormat = ImageFormats[0];
        _selectedPageSize = PageSizes[0];
        _selectedOrientation = Orientations[0];
        _selectedTextFormat = TextFormats[0];
        Refresh();
    }

    public ConversionKind Kind { get; }
    public ObservableCollection<ConvertFileViewModel> Files { get; } = [];
    public IReadOnlyList<Choice<ImageFormat>> ImageFormats { get; }
    public IReadOnlyList<Choice<PdfPageSizeMode>> PageSizes { get; }
    public IReadOnlyList<Choice<PdfPageOrientation>> Orientations { get; }
    public IReadOnlyList<Choice<TextExportFormat>> TextFormats { get; }

    public override bool IsEmpty => Files.Count == 0;
    public bool HasFiles => Files.Count > 0;

    public bool IsPdfToImage => Kind == ConversionKind.PdfToImage;
    public bool IsImageToPdf => Kind == ConversionKind.ImageToPdf;
    public bool IsOfficeToPdf => Kind == ConversionKind.OfficeToPdf;
    public bool IsTextHtmlToPdf => Kind == ConversionKind.TextHtmlToPdf;
    public bool IsPdfToWord => Kind == ConversionKind.PdfToWord;
    public bool IsPdfToText => Kind == ConversionKind.PdfToText;

    /// <summary>Tipos que podem depender do LibreOffice instalado.</summary>
    public bool ShowsLibreOffice => IsOfficeToPdf || IsTextHtmlToPdf || IsPdfToWord;
    public bool HasLibreOffice => Context.Conversion.LibreOffice.IsAvailable;

    public string LibreOfficeStatus => HasLibreOffice
        ? "LibreOffice encontrado neste computador."
        : Kind switch
        {
            ConversionKind.OfficeToPdf => "LibreOffice não encontrado. " + LibreOffice.MissingMessage,
            ConversionKind.TextHtmlToPdf => "LibreOffice não encontrado: arquivos TXT são convertidos normalmente; arquivos HTML precisam dele.",
            _ => "LibreOffice não encontrado: a conversão em texto corrido funciona normalmente; a opção de manter a aparência precisa dele.",
        };

    public string DropHint => Kind switch
    {
        ConversionKind.ImageToPdf => "Arraste as imagens para cá (JPG, PNG, BMP, TIFF, GIF)",
        ConversionKind.OfficeToPdf => "Arraste os documentos para cá (Word, Excel, PowerPoint, ODF, RTF)",
        ConversionKind.TextHtmlToPdf => "Arraste os arquivos TXT ou HTML para cá",
        _ => "Arraste os PDFs para cá",
    };

    public string Limitations => Kind switch
    {
        ConversionKind.PdfToWord =>
            "Texto corrido: recupera o texto em parágrafos editáveis; imagens, tabelas, colunas, fontes e posição não são reproduzidos. " +
            "Manter a aparência (LibreOffice): fica parecido com o original, mas usa caixas de texto posicionadas e é menos confortável de editar. " +
            "Nenhum dos dois modos é uma reprodução perfeita. PDFs digitalizados precisam de OCR (Fase 4).",
        ConversionKind.PdfToText =>
            "Extrai somente o texto, na ordem de leitura. Imagens, tabelas e formatação não são levados. PDFs digitalizados precisam de OCR (Fase 4).",
        ConversionKind.OfficeToPdf =>
            "A conversão é feita pelo LibreOffice. Documentos com fontes que não estão instaladas ou com recursos exclusivos do Microsoft Office podem sair com diferenças de formatação — confira o resultado.",
        ConversionKind.TextHtmlToPdf =>
            "TXT: página A4, com quebra automática de linhas. HTML: convertido pelo LibreOffice, com suporte limitado a CSS moderno; páginas que carregam conteúdo da internet podem acessá-la durante a conversão.",
        ConversionKind.ImageToPdf =>
            "Arquivos JPG são incorporados como estão, sem perda adicional. As demais imagens são gravadas sem perda, a menos que a opção de JPEG esteja marcada.",
        _ => "Cada página vira uma imagem na resolução escolhida. Resoluções altas geram arquivos grandes.",
    };

    public string RunLabel => "Converter";

    // ----- PDF para imagem -----
    [ObservableProperty] private Choice<ImageFormat> _selectedImageFormat;
    [ObservableProperty] private string _dpiText = "150";
    [ObservableProperty] private string _qualityText = "90";
    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private string _pagesText = string.Empty;
    [ObservableProperty] private bool _singleTiff;

    // ----- Imagem para PDF -----
    [ObservableProperty] private Choice<PdfPageSizeMode> _selectedPageSize;
    [ObservableProperty] private Choice<PdfPageOrientation> _selectedOrientation;
    [ObservableProperty] private string _marginText = "0";
    [ObservableProperty] private bool _onePdfPerImage;
    [ObservableProperty] private bool _useJpeg;
    [ObservableProperty] private string _fileName = "imagens";

    // ----- Texto para PDF -----
    [ObservableProperty] private bool _monospace = true;
    [ObservableProperty] private bool _landscape;

    // ----- PDF para Word / texto -----
    [ObservableProperty] private bool _keepLayout;
    [ObservableProperty] private Choice<TextExportFormat> _selectedTextFormat;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string _summary = string.Empty;

    public bool IsJpegSelected => SelectedImageFormat?.Value == ImageFormat.Jpeg;
    public bool IsTiffSelected => SelectedImageFormat?.Value == ImageFormat.Tiff;
    public bool IsFixedPageSize => SelectedPageSize?.Value != PdfPageSizeMode.FitImage;
    public bool IsSinglePdf => !OnePdfPerImage;

    partial void OnSelectedImageFormatChanged(Choice<ImageFormat> value)
    {
        OnPropertyChanged(nameof(IsJpegSelected));
        OnPropertyChanged(nameof(IsTiffSelected));
        Refresh();
    }

    partial void OnSelectedPageSizeChanged(Choice<PdfPageSizeMode> value) => OnPropertyChanged(nameof(IsFixedPageSize));
    partial void OnOnePdfPerImageChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSinglePdf));
        Refresh();
    }
    partial void OnDpiTextChanged(string value) => Refresh();
    partial void OnQualityTextChanged(string value) => Refresh();
    partial void OnMarginTextChanged(string value) => Refresh();
    partial void OnPagesTextChanged(string value) => Refresh();
    partial void OnKeepLayoutChanged(bool value) => Refresh();
    partial void OnUseJpegChanged(bool value) => Refresh();

    private static string TitleOf(ConversionKind kind) => kind switch
    {
        ConversionKind.PdfToImage => PdfToImageConverter.OperationName,
        ConversionKind.ImageToPdf => ImagesToPdfConverter.OperationName,
        ConversionKind.OfficeToPdf => OfficeToPdfConverter.OperationName,
        ConversionKind.TextHtmlToPdf => TextToPdfConverter.OperationName,
        ConversionKind.PdfToWord => PdfTextExporter.WordOperationName,
        _ => PdfTextExporter.TextOperationName,
    };

    private static string SubtitleOf(ConversionKind kind) => kind switch
    {
        ConversionKind.PdfToImage => "Transforme as páginas em imagens JPG, PNG, TIFF ou BMP.",
        ConversionKind.ImageToPdf => "Crie um PDF a partir de fotos e imagens digitalizadas.",
        ConversionKind.OfficeToPdf => "Converta documentos do Word, Excel e PowerPoint em PDF.",
        ConversionKind.TextHtmlToPdf => "Crie PDFs a partir de arquivos de texto ou HTML.",
        ConversionKind.PdfToWord => "Converta o PDF em um documento DOCX editável.",
        _ => "Extraia o texto do PDF para um arquivo TXT ou HTML.",
    };

    private IReadOnlyList<string> AcceptedExtensions => Kind switch
    {
        ConversionKind.ImageToPdf => ImagesToPdfConverter.Extensions,
        ConversionKind.OfficeToPdf => OfficeToPdfConverter.OfficeExtensions,
        ConversionKind.TextHtmlToPdf => [".txt", ".html", ".htm"],
        _ => [".pdf"],
    };

    private string DialogFilter
    {
        get
        {
            var patterns = string.Join(";", AcceptedExtensions.Select(e => "*" + e));
            return $"Arquivos aceitos|{patterns}";
        }
    }

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PickFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanClear() => !IsBusy && Files.Count > 0;
    private bool CanRun() => !IsBusy && Files.Count > 0 && Problem is null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFilesAsync()
    {
        var picked = Context.Dialogs.PickFiles("Adicionar arquivos", DialogFilter, true, StartFolder());
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        FilesChanged();
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return Task.CompletedTask;

        var accepted = AcceptedExtensions;
        var added = 0;
        var rejected = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !accepted.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                rejected++;
                continue;
            }

            var full = System.IO.Path.GetFullPath(path);
            if (Files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new ConvertFileViewModel(full, Move, Remove));
            added++;
        }

        if (rejected > 0)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram adicionados",
                $"{rejected} arquivo(s) não são de um tipo aceito por esta conversão ({string.Join(", ", accepted)}).");
        }

        if (added > 0) SuggestOutputFolder(Files[0].Path);
        FilesChanged();
        return Task.CompletedTask;
    }

    private void Move(ConvertFileViewModel file, int direction)
    {
        if (IsBusy) return;
        var index = Files.IndexOf(file);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count) return;
        Files.Move(index, target);
    }

    private void Remove(ConvertFileViewModel file)
    {
        if (IsBusy) return;
        Files.Remove(file);
        FilesChanged();
    }

    private void FilesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFiles));
        Refresh();
    }

    private bool TryGetInt(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    /// <summary>Valida as opções; devolve a mensagem do problema ou null.</summary>
    private string? Validate()
    {
        switch (Kind)
        {
            case ConversionKind.PdfToImage:
                if (!TryGetInt(DpiText, PdfToImageConverter.MinDpi, PdfToImageConverter.MaxDpi, out _))
                    return $"Informe a resolução entre {PdfToImageConverter.MinDpi} e {PdfToImageConverter.MaxDpi} DPI.";
                if (IsJpegSelected && !TryGetInt(QualityText, 1, 100, out _)) return "Informe a qualidade JPG entre 1 e 100.";
                break;
            case ConversionKind.ImageToPdf:
                if (!double.TryParse(MarginText.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out var margin) || margin < 0 || margin > 100)
                    return "Informe a margem entre 0 e 100 mm.";
                if (UseJpeg && !TryGetInt(QualityText, 1, 100, out _)) return "Informe a qualidade JPG entre 1 e 100.";
                break;
            case ConversionKind.OfficeToPdf:
                if (!HasLibreOffice) return LibreOffice.MissingMessage;
                break;
            case ConversionKind.TextHtmlToPdf:
                if (!HasLibreOffice && Files.Any(f => !f.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                    return "A lista tem arquivos HTML, que precisam do LibreOffice. " + LibreOffice.MissingMessage;
                break;
            case ConversionKind.PdfToWord:
                if (KeepLayout && !HasLibreOffice) return "A opção de manter a aparência precisa do LibreOffice. " + LibreOffice.MissingMessage;
                break;
        }

        return null;
    }

    private void Refresh()
    {
        Problem = Validate();
        Summary = Files.Count == 0
            ? "Adicione os arquivos que serão convertidos."
            : Kind == ConversionKind.ImageToPdf && !OnePdfPerImage
                ? $"{Files.Count} imagem(ns) em um único PDF, na ordem da lista."
                : $"{Files.Count} arquivo(s) na lista. Cada um gera o seu resultado na pasta de destino.";
        OnStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        Context.Conversion.LibreOffice.Refresh();
        OnPropertyChanged(nameof(HasLibreOffice));
        OnPropertyChanged(nameof(LibreOfficeStatus));
        Problem = Validate();
        if (Problem is not null || Files.Count == 0) return;

        foreach (var file in Files)
        {
            file.Status = null;
            file.Failed = false;
        }

        var files = Files.ToList();
        var target = CreateTarget();
        var failures = new List<string>();
        var inputName = files.Count == 1 ? files[0].Name : $"{files.Count} arquivos";

        // As opções são lidas antes de começar: mudanças na tela durante a execução não interferem.
        TryGetInt(DpiText, 1, 10000, out var dpi);
        TryGetInt(QualityText, 1, 100, out var quality);
        double.TryParse(MarginText.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out var marginMm);
        var imageOptions = new PdfToImageOptions(SelectedImageFormat.Value, dpi, quality, Grayscale,
            string.IsNullOrWhiteSpace(PagesText) ? null : PagesText, SingleTiff);
        var pdfOptions = new ImagesToPdfOptions(SelectedPageSize.Value, SelectedOrientation.Value, marginMm, UseJpeg ? quality : null);
        var textOptions = new TextToPdfOptions(Monospace, Landscape: Landscape);
        var keepLayout = KeepLayout;
        var onePerImage = OnePdfPerImage;
        var textFormat = SelectedTextFormat.Value;
        var outputName = FileName;

        await RunAsync(Title, inputName, "Convertendo…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = OutputPathResolver.CreateReservationSet();

            if (Kind == ConversionKind.ImageToPdf && !onePerImage)
            {
                produced.Add(await Context.Conversion.ImagesToPdf.ConvertAsync(
                    files.Select(f => f.Path).ToList(), outputName, pdfOptions, target, progress, ct, reserved));
                foreach (var file in files) file.Status = "Incluída no PDF";
                return produced;
            }

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                var index = i;
                var part = new Progress<double>(v => progress.Report((index + Math.Clamp(v, 0, 1)) / files.Count));
                StatusText = $"Convertendo {i + 1} de {files.Count}: {file.Name}";
                try
                {
                    var results = await ConvertOneAsync(file.Path, imageOptions, pdfOptions, textOptions, keepLayout,
                        textFormat, target, part, ct, reserved);
                    produced.AddRange(results);
                    file.Status = results.Count == 1
                        ? $"Gerado: {System.IO.Path.GetFileName(results[0].Path)}"
                        : $"{results.Count} arquivos gerados";
                }
                catch (Exception ex) when (ex is PdfEngineException or PageRangeException or PdfRenderException
                                               or IOException or UnauthorizedAccessException)
                {
                    // Um arquivo com problema não interrompe os demais.
                    file.Status = ex.Message;
                    file.Failed = true;
                    failures.Add($"{file.Name}: {ex.Message}");
                }

                progress.Report((i + 1) / (double)files.Count);
            }

            if (produced.Count == 0)
                throw new PdfEngineException(failures.Count == 1 ? failures[0] : "Nenhum arquivo pôde ser convertido.\n" + string.Join("\n", failures.Take(5)));
            return produced;
        }, () => failures.Count == 0 ? null : $"{failures.Count} arquivo(s) não puderam ser convertidos; veja o motivo na lista.");
    }

    private async Task<IReadOnlyList<ProducedFile>> ConvertOneAsync(
        string path,
        PdfToImageOptions imageOptions,
        ImagesToPdfOptions pdfOptions,
        TextToPdfOptions textOptions,
        bool keepLayout,
        TextExportFormat textFormat,
        OutputTarget target,
        IProgress<double> progress,
        CancellationToken ct,
        ISet<string> reserved)
    {
        var tools = Context.Conversion;
        switch (Kind)
        {
            case ConversionKind.PdfToImage:
                return await tools.PdfToImage.ConvertAsync(path, imageOptions, target, progress, ct, reserved);
            case ConversionKind.ImageToPdf:
                return [await tools.ImagesToPdf.ConvertAsync([path], System.IO.Path.GetFileNameWithoutExtension(path),
                    pdfOptions, target, progress, ct, reserved)];
            case ConversionKind.OfficeToPdf:
                return [await tools.Office.ConvertAsync(path, target, OfficeToPdfConverter.OperationName, ct, reserved)];
            case ConversionKind.TextHtmlToPdf:
                if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    return [await tools.Text.ConvertAsync(path, textOptions, target, ct, reserved)];
                return [await tools.Office.ConvertAsync(path, target, TextToPdfConverter.OperationName, ct, reserved)];
            case ConversionKind.PdfToWord:
                if (keepLayout)
                {
                    var info = await Context.Engine.ReadInfoAsync(path, ct);
                    return [await tools.PdfText.ExportWithLayoutAsync(path, info.PageCount, target, ct, reserved)];
                }

                return [await tools.PdfText.ExportAsync(path, TextExportFormat.Docx, target, ct, reserved)];
            default:
                return [await tools.PdfText.ExportAsync(path, textFormat, target, ct, reserved)];
        }
    }
}
