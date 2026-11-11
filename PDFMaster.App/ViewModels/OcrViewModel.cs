using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.OCR;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

public sealed partial class OcrLanguageChoice : ObservableObject
{
    public OcrLanguageChoice(OcrLanguage language, bool selected, Action changed)
    {
        Code = language.Code;
        Label = language.Name == language.Code ? language.Code : $"{language.Name} ({language.Code})";
        IsSelected = selected;
        _changed = changed;
    }

    private readonly Action? _changed;

    public string Code { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed?.Invoke();
}

/// <summary>OCR de um ou vários documentos (PDFs digitalizados e imagens), com acompanhamento por página.</summary>
public sealed partial class OcrViewModel : ToolViewModelBase
{
    public OcrViewModel(ToolContext context)
        : base(context, "OCR — reconhecimento de texto",
            "Torne pesquisáveis os PDFs digitalizados e as imagens, ou extraia o texto para TXT e Word.")
    {
        _dpiText = context.Settings.Current.OcrDpi.ToString(CultureInfo.InvariantCulture);
        LoadLanguages();
        Refresh();
    }

    public ObservableCollection<ConvertFileViewModel> Files { get; } = [];
    public ObservableCollection<OcrLanguageChoice> Languages { get; } = [];

    public override bool IsEmpty => Files.Count == 0;
    protected internal override IReadOnlyList<string> RepeatInputs => Files.Select(f => f.Path).ToList();
    public bool HasFiles => Files.Count > 0;
    public bool HasLanguages => Languages.Count > 0;
    public string ModelsDirectory => Context.Ocr.Runtime.ModelsDirectory;

    [ObservableProperty] private bool _searchablePdf = true;
    [ObservableProperty] private bool _textFile;
    [ObservableProperty] private bool _docx;
    [ObservableProperty] private string _dpiText = "300";
    [ObservableProperty] private string _pagesText = string.Empty;
    [ObservableProperty] private bool _autoRotate = true;
    [ObservableProperty] private bool _deskew = true;
    [ObservableProperty] private bool _enhanceContrast = true;
    [ObservableProperty] private bool _denoise;
    [ObservableProperty] private bool _skipPagesWithText = true;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Mensagem quando os componentes de OCR não puderam ser preparados.</summary>
    [ObservableProperty]
    private string? _runtimeProblem;

    partial void OnSearchablePdfChanged(bool value) => Refresh();
    partial void OnTextFileChanged(bool value) => Refresh();
    partial void OnDocxChanged(bool value) => Refresh();
    partial void OnDpiTextChanged(string value) => Refresh();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PickFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        InstallLanguageCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanClear() => !IsBusy && Files.Count > 0;
    private bool CanRun() => !IsBusy && Files.Count > 0 && Problem is null;

    private void LoadLanguages()
    {
        var selected = Languages.Where(l => l.IsSelected).Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Languages.Clear();
        try
        {
            var installed = Context.Ocr.Runtime.InstalledLanguages();
            foreach (var language in installed)
            {
                // Na primeira carga vêm marcados os idiomas padrão das configurações (ou o primeiro instalado).
                var preferred = Context.Settings.Current.OcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries);
                var anyPreferred = installed.Any(l => preferred.Contains(l.Code, StringComparer.OrdinalIgnoreCase));
                var isSelected = selected.Count > 0
                    ? selected.Contains(language.Code)
                    : anyPreferred ? preferred.Contains(language.Code, StringComparer.OrdinalIgnoreCase) : language == installed[0];
                Languages.Add(new OcrLanguageChoice(language, isSelected, Refresh));
            }

            RuntimeProblem = null;
        }
        catch (PdfEngineException ex)
        {
            RuntimeProblem = ex.Message;
        }

        OnPropertyChanged(nameof(HasLanguages));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFilesAsync()
    {
        var images = string.Join(";", ImagesToPdfConverter.Extensions.Select(e => "*" + e));
        var picked = Context.Dialogs.PickFiles("Adicionar documentos para OCR",
            $"PDFs e imagens|*.pdf;{images}|Documentos PDF|*.pdf|Imagens|{images}", true, StartFolder());
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        FilesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void InstallLanguage()
    {
        var picked = Context.Dialogs.PickFiles("Instalar idioma de OCR",
            "Modelos do Tesseract (*.traineddata)|*.traineddata", true, null);
        if (picked.Count == 0) return;

        var installed = new List<string>();
        foreach (var file in picked)
        {
            try
            {
                installed.Add(Context.Ocr.Runtime.InstallLanguage(file).Name);
            }
            catch (PdfEngineException ex)
            {
                Context.Notifications.Show(NotificationKind.Error, "Idioma não instalado", ex.Message);
            }
        }

        LoadLanguages();
        Refresh();
        if (installed.Count > 0)
        {
            Context.Notifications.Show(NotificationKind.Success, "Idioma de OCR instalado",
                string.Join(", ", installed) + "\nMarque o idioma na lista para usá-lo.");
        }
    }

    [RelayCommand]
    private void OpenModelsFolder()
    {
        if (!Context.Shell.OpenFolder(ModelsDirectory))
            Context.Notifications.Show(NotificationKind.Warning, "Não foi possível abrir a pasta dos modelos", ModelsDirectory);
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return Task.CompletedTask;

        var rejected = 0;
        foreach (var path in paths)
        {
            var isPdf = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(path) || (!isPdf && !OcrService.IsImage(path)))
            {
                rejected++;
                continue;
            }

            var full = Path.GetFullPath(path);
            if (Files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new ConvertFileViewModel(full, Move, Remove));
        }

        if (rejected > 0)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram adicionados",
                $"{rejected} arquivo(s) não são PDF nem imagem (JPG, PNG, BMP, TIFF, GIF).");
        }

        if (Files.Count > 0) SuggestOutputFolder(Files[0].Path);
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

    private bool TryGetDpi(out int dpi) =>
        int.TryParse(DpiText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out dpi) &&
        dpi >= OcrService.MinDpi && dpi <= OcrService.MaxDpi;

    private string? Validate()
    {
        if (RuntimeProblem is not null) return RuntimeProblem;
        if (Languages.Count == 0) return OcrRuntime.NoLanguagesMessage;
        if (!Languages.Any(l => l.IsSelected)) return "Marque pelo menos um idioma.";
        if (!SearchablePdf && !TextFile && !Docx) return "Marque pelo menos um tipo de resultado.";
        if (!TryGetDpi(out _)) return $"Informe a resolução entre {OcrService.MinDpi} e {OcrService.MaxDpi} DPI.";
        return null;
    }

    private void Refresh()
    {
        Problem = Validate();
        Summary = Files.Count == 0
            ? "Adicione os PDFs digitalizados ou as imagens."
            : Files.Count == 1
                ? "1 documento na lista. O original não é alterado: o resultado é gravado como \"nome_ocr\"."
                : $"{Files.Count} documentos na fila. Cada resultado é gravado como \"nome_ocr\"; um documento com problema não interrompe os demais.";
        OnStateChanged();
    }

    private static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        Problem = Validate();
        if (Problem is not null || Files.Count == 0 || !TryGetDpi(out var dpi)) return;

        foreach (var file in Files)
        {
            file.Status = null;
            file.Failed = false;
        }

        // As opções são lidas antes de começar: mudanças na tela durante a execução não interferem.
        var files = Files.ToList();
        var target = CreateTarget();
        var options = new OcrOptions(
            string.Join("+", Languages.Where(l => l.IsSelected).Select(l => l.Code)),
            dpi, AutoRotate, Deskew, EnhanceContrast, Denoise, SkipPagesWithText,
            string.IsNullOrWhiteSpace(PagesText) ? null : PagesText,
            SearchablePdf, TextFile, Docx);
        var inputName = files.Count == 1 ? files[0].Name : $"{files.Count} documentos";
        var failures = 0;
        var pages = 0;
        var rotated = 0;
        var watch = Stopwatch.StartNew();

        await RunAsync(OcrService.OperationName, inputName, "Preparando o OCR…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = OutputPathResolver.CreateReservationSet();
            string? lastError = null;

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                var index = i;

                void Report(OcrProgress p)
                {
                    var fraction = (index + (p.Page - 1) / (double)Math.Max(1, p.PageCount)) / files.Count;
                    progress.Report(fraction);
                    var elapsed = watch.Elapsed;
                    var remaining = fraction > 0.02
                        ? " · restante ~" + Clock(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction))
                        : string.Empty;
                    var errors = failures > 0 ? $" · {failures} com erro" : string.Empty;
                    StatusText = $"Documento {index + 1} de {files.Count}: {file.Name} — página {p.Page} de {p.PageCount} · " +
                                 $"decorrido {Clock(elapsed)}{remaining}{errors}";
                }

                Report(new OcrProgress(1, 1));
                file.Status = "Reconhecendo…";
                try
                {
                    var result = await Context.Ocr.Service.RecognizeAsync(file.Path, options, target, new Progress<OcrProgress>(Report), ct, reserved);
                    produced.AddRange(result.Files);
                    pages += result.PagesRecognized;
                    rotated += result.PagesRotated;

                    var parts = new List<string> { $"{result.PagesRecognized} página(s) reconhecida(s)" };
                    if (result.PagesRecognized > 0) parts.Add($"{result.Words} palavra(s), confiança média de {result.MeanConfidence * 100:0}%");
                    if (result.PagesSkipped > 0) parts.Add($"{result.PagesSkipped} já tinham texto");
                    if (result.PagesRotated > 0) parts.Add($"{result.PagesRotated} com a orientação corrigida");
                    file.Status = string.Join(" · ", parts) + " → " + string.Join(", ", result.Files.Select(f => Path.GetFileName(f.Path)));
                }
                catch (Exception ex) when (ex is PdfEngineException or PageRangeException or PdfRenderException
                                               or IOException or UnauthorizedAccessException)
                {
                    file.Status = ex.Message;
                    file.Failed = true;
                    failures++;
                    lastError = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    file.Status = "Cancelado.";
                    throw;
                }

                progress.Report((i + 1) / (double)files.Count);
            }

            if (produced.Count == 0)
                throw new PdfEngineException(files.Count == 1 && lastError is not null ? lastError : "Nenhum documento pôde ser reconhecido; veja o motivo de cada um na lista.");
            return produced;
        }, () =>
        {
            var text = $"{pages} página(s) reconhecida(s) em {Clock(watch.Elapsed)}.";
            if (rotated > 0) text += $" {rotated} página(s) tiveram a orientação corrigida.";
            if (failures > 0) text += $"\n{failures} documento(s) não puderam ser reconhecidos; veja o motivo na lista.";
            return text;
        });
    }
}
