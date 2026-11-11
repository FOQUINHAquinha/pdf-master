using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Compression;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.App.ViewModels;

public sealed partial class CompressFileViewModel : ObservableObject
{
    public CompressFileViewModel(string path, long bytes, Action<CompressFileViewModel> remove)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        OriginalBytes = bytes;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public string Name { get; }
    public long OriginalBytes { get; }
    public string OriginalText => FileSizeFormatter.Format(OriginalBytes);

    /// <summary>Resultado da última compressão (vazio antes de comprimir).</summary>
    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private bool _failed;

    public ICommand RemoveCommand { get; }
}

public sealed partial class CompressViewModel : ToolViewModelBase
{
    public CompressViewModel(ToolContext context)
        : base(context, PdfCompressor.OperationName,
            "Reduza o tamanho do PDF. O original nunca é substituído: o resultado é um novo arquivo.")
    {
        Profiles = Enum.GetValues<CompressionProfile>()
            .Select(p => new Choice<CompressionProfile>(p, CompressionSettings.Describe(p)))
            .ToList();
        var preferred = Enum.TryParse<CompressionProfile>(context.Settings.Current.DefaultCompressionProfile, out var saved) && saved != CompressionProfile.Custom
            ? saved
            : CompressionProfile.Balanced;
        _selectedProfile = Profiles.First(p => p.Value == preferred);
        Refresh();
    }

    public ObservableCollection<CompressFileViewModel> Files { get; } = [];
    public IReadOnlyList<Choice<CompressionProfile>> Profiles { get; }

    public override bool IsEmpty => Files.Count == 0;
    public bool HasFiles => Files.Count > 0;

    [ObservableProperty] private Choice<CompressionProfile> _selectedProfile;
    [ObservableProperty] private string _qualityText = "75";
    [ObservableProperty] private string _dpiText = "150";
    [ObservableProperty] private bool _convertLossless = true;
    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private bool _removeMetadata;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string _summary = string.Empty;

    // ----- Comparação antes/depois do último resultado -----
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private string _comparisonTitle = string.Empty;
    [ObservableProperty] private string _originalSizeText = string.Empty;
    [ObservableProperty] private string _finalSizeText = string.Empty;
    [ObservableProperty] private string _savedText = string.Empty;
    [ObservableProperty] private string _qualityUsedText = string.Empty;
    [ObservableProperty] private ImageSource? _beforeImage;
    [ObservableProperty] private ImageSource? _afterImage;

    public bool IsCustom => SelectedProfile?.Value == CompressionProfile.Custom;

    partial void OnSelectedProfileChanged(Choice<CompressionProfile> value)
    {
        OnPropertyChanged(nameof(IsCustom));
        Refresh();
    }

    partial void OnQualityTextChanged(string value) => Refresh();
    partial void OnDpiTextChanged(string value) => Refresh();

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
        var picked = PickPdfs(multiple: true);
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        HasComparison = false;
        FilesChanged();
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return Task.CompletedTask;

        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count == 0 && paths.Count > 0)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "Esta ferramenta trabalha apenas com arquivos PDF.");
        }

        foreach (var path in pdfs)
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new CompressFileViewModel(full, new FileInfo(full).Length, Remove));
        }

        if (Files.Count > 0) SuggestOutputFolder(Files[0].Path);
        FilesChanged();
        return Task.CompletedTask;
    }

    private void Remove(CompressFileViewModel file)
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

    private bool TryGetSettings(out CompressionSettings settings, out string? problem)
    {
        problem = null;
        if (!IsCustom)
        {
            settings = CompressionSettings.For(SelectedProfile.Value) with { RemoveMetadata = RemoveMetadata };
            return true;
        }

        settings = CompressionSettings.For(CompressionProfile.Balanced);
        if (!int.TryParse(QualityText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var quality) || quality is < 1 or > 100)
        {
            problem = "Informe a qualidade JPEG entre 1 e 100.";
            return false;
        }

        if (!int.TryParse(DpiText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dpi) || dpi is < 36 or > 1200)
        {
            problem = "Informe a resolução máxima entre 36 e 1200 DPI.";
            return false;
        }

        settings = new CompressionSettings(quality, dpi, ConvertLossless, Grayscale, RemoveMetadata);
        return true;
    }

    private void Refresh()
    {
        TryGetSettings(out _, out var problem);
        Problem = problem;
        var total = Files.Sum(f => f.OriginalBytes);
        Summary = Files.Count == 0
            ? "Adicione os PDFs que serão comprimidos."
            : $"{Files.Count} arquivo(s), {FileSizeFormatter.Format(total)} no total. Cada resultado é gravado como \"nome_comprimido.pdf\".";
        OnStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        if (!TryGetSettings(out var settings, out var problem))
        {
            Problem = problem;
            return;
        }

        foreach (var file in Files)
        {
            file.ResultText = null;
            file.Failed = false;
        }

        var files = Files.ToList();
        var target = CreateTarget();
        var profileLabel = SelectedProfile.Label;
        var inputName = files.Count == 1 ? files[0].Name : $"{files.Count} arquivos";
        var results = new List<(CompressFileViewModel File, CompressionResult Result)>();
        var notes = new List<string>();
        HasComparison = false;

        var ok = await RunAsync(PdfCompressor.OperationName, inputName, "Comprimindo…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = OutputPathResolver.CreateReservationSet();
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                var index = i;
                var part = new Progress<double>(v => progress.Report((index + Math.Clamp(v, 0, 1)) / files.Count));
                StatusText = $"Comprimindo {i + 1} de {files.Count}: {file.Name}";
                try
                {
                    var result = await Context.Conversion.Compressor.CompressAsync(file.Path, settings, target, part, ct, reserved);
                    results.Add((file, result));
                    if (result.Output is { } output)
                    {
                        produced.Add(output);
                        file.ResultText = $"{FileSizeFormatter.Format(result.OriginalBytes)} → {FileSizeFormatter.Format(result.FinalBytes)} " +
                                          $"(redução de {result.SavedPercent:0}%) · {result.ImagesRecompressed} de {result.ImagesFound} imagem(ns) regravada(s)";
                        if (result.HadSignatures)
                            notes.Add($"{file.Name} tem assinatura digital: a cópia comprimida não mantém a validade da assinatura.");
                    }
                    else
                    {
                        file.ResultText = "Sem redução com este perfil — nenhum arquivo foi gravado.";
                    }
                }
                catch (Exception ex) when (ex is PdfEngineException or IOException or UnauthorizedAccessException)
                {
                    file.ResultText = ex.Message;
                    file.Failed = true;
                }
            }

            if (produced.Count == 0)
            {
                var failed = files.FirstOrDefault(f => f.Failed);
                throw new PdfEngineException(failed is not null && files.Count == 1
                    ? failed.ResultText!
                    : "Nenhum arquivo ficou menor com este perfil, então nada foi gravado. " +
                      "Documentos sem imagens ou já otimizados têm pouco a ganhar; experimente um perfil mais forte.");
            }

            return produced;
        }, () =>
        {
            var reduced = results.Where(r => r.Result.Reduced).ToList();
            var original = reduced.Sum(r => r.Result.OriginalBytes);
            var final = reduced.Sum(r => r.Result.FinalBytes);
            var percent = original == 0 ? 0 : (original - final) * 100.0 / original;
            var text = $"Original: {FileSizeFormatter.Format(original)} · Final: {FileSizeFormatter.Format(final)} · Redução: {percent:0}%";
            return notes.Count == 0 ? text : text + "\n" + string.Join("\n", notes);
        });

        if (!ok) return;
        var best = results.FirstOrDefault(r => r.Result.Reduced);
        if (best.File is not null) await ShowComparisonAsync(best.File, best.Result, profileLabel);
    }

    /// <summary>Mostra tamanhos e a primeira página antes e depois, lado a lado.</summary>
    private async Task ShowComparisonAsync(CompressFileViewModel file, CompressionResult result, string profileLabel)
    {
        ComparisonTitle = file.Name;
        OriginalSizeText = FileSizeFormatter.Format(result.OriginalBytes);
        FinalSizeText = FileSizeFormatter.Format(result.FinalBytes);
        SavedText = $"{FileSizeFormatter.Format(result.SavedBytes)} ({result.SavedPercent:0}%)";
        QualityUsedText = profileLabel;
        BeforeImage = null;
        AfterImage = null;
        HasComparison = true;

        try
        {
            var size = RenderSize.Fit(520, 740);
            var before = await Context.Renderer.RenderPageAsync(file.Path, 0, size);
            var after = await Context.Renderer.RenderPageAsync(result.Output!.Path, 0, size);
            BeforeImage = ThumbnailService.ToBitmap(before);
            AfterImage = ThumbnailService.ToBitmap(after);
        }
        catch (Exception ex) when (ex is PdfRenderException or IOException or UnauthorizedAccessException)
        {
            // Sem pré-visualização: os tamanhos continuam sendo exibidos.
        }
    }
}
