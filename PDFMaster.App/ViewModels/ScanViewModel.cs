using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.OCR;
using PDFMaster.PdfEngine;
using PDFMaster.Scanner;

namespace PDFMaster.App.ViewModels;

public sealed class ScanPageViewModel
{
    public ScanPageViewModel(string path, bool temporary, Action<ScanPageViewModel, int> move, Action<ScanPageViewModel> remove)
    {
        Path = path;
        IsTemporary = temporary;
        Name = System.IO.Path.GetFileName(path);
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, 1));
        RemoveCommand = new RelayCommand(() => remove(this));

        try
        {
            // Miniatura carregada inteira na memória, para não manter o arquivo aberto.
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 160;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
            Details = FileSizeFormatter.Format(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            Details = "Sem pré-visualização";
        }
    }

    public string Path { get; }

    /// <summary>Página criada pela digitalização (apagada ao sair da lista); imagens do usuário nunca são apagadas.</summary>
    public bool IsTemporary { get; }
    public string Name { get; }
    public string Details { get; } = string.Empty;
    public ImageSource? Thumbnail { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>Digitalização pelo WIA: as páginas são reunidas e gravadas em um PDF, com OCR opcional.</summary>
public sealed partial class ScanViewModel : ToolViewModelBase
{
    public const string OperationName = "Digitalizar";
    private readonly string _sessionFolder;

    public ScanViewModel(ToolContext context)
        : base(context, "Digitalizar", "Digitalize documentos direto para PDF, com reconhecimento de texto opcional.")
    {
        var root = Path.Combine(Path.GetTempPath(), "PDFMaster");
        CleanOldSessions(root);
        _sessionFolder = Path.Combine(root, "scan-" + Guid.NewGuid().ToString("N"));

        Sources =
        [
            new(ScanSource.Flatbed, "Vidro do scanner (uma página por vez)"),
            new(ScanSource.Feeder, "Alimentador automático (todas as folhas)"),
            new(ScanSource.FeederDuplex, "Alimentador automático, frente e verso"),
        ];
        ColorModes =
        [
            new(ScanColor.Color, "Colorido"),
            new(ScanColor.Grayscale, "Escala de cinza"),
            new(ScanColor.BlackAndWhite, "Preto e branco"),
        ];
        _selectedSource = Sources[0];
        _selectedColor = ColorModes[0];
        Refresh();
    }

    public ObservableCollection<ScannerDevice> Devices { get; } = [];
    public ObservableCollection<ScanPageViewModel> Pages { get; } = [];
    public IReadOnlyList<Choice<ScanSource>> Sources { get; }
    public IReadOnlyList<Choice<ScanColor>> ColorModes { get; }

    public override bool IsEmpty => Pages.Count == 0;
    public bool HasPages => Pages.Count > 0;
    public bool HasDevices => Devices.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private ScannerDevice? _selectedDevice;

    [ObservableProperty] private Choice<ScanSource> _selectedSource;
    [ObservableProperty] private Choice<ScanColor> _selectedColor;
    [ObservableProperty] private string _dpiText = "300";
    [ObservableProperty] private string _fileName = "digitalizacao";
    [ObservableProperty] private bool _runOcr;
    [ObservableProperty] private bool _useJpeg = true;

    /// <summary>Situação da lista de scanners (ex.: "Nenhum scanner encontrado").</summary>
    [ObservableProperty]
    private string _deviceStatus = "Clique em \"Procurar scanners\" para listar os aparelhos instalados.";

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string _summary = string.Empty;

    partial void OnDpiTextChanged(string value) => Refresh();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(SaveCommand);

    protected override void OnStateChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        FindDevicesCommand.NotifyCanExecuteChanged();
        AddImagesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanScan() => !IsBusy && SelectedDevice is not null && TryGetDpi(out _);
    private bool CanClear() => !IsBusy && Pages.Count > 0;
    private bool CanSave() => !IsBusy && Pages.Count > 0;

    private bool TryGetDpi(out int dpi) =>
        int.TryParse(DpiText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out dpi) && dpi is >= 50 and <= 1200;

    private void Refresh()
    {
        Problem = TryGetDpi(out _) ? null : "Informe a resolução entre 50 e 1200 DPI.";
        Summary = Pages.Count == 0
            ? "Nenhuma página ainda. Digitalize ou adicione imagens."
            : $"{Pages.Count} página(s) prontas para gravar, na ordem da lista.";
        OnStateChanged();
    }

    private void PagesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasPages));
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task FindDevicesAsync()
    {
        await WhileBusyAsync("Procurando scanners…", async () =>
        {
            try
            {
                var previous = SelectedDevice?.Id;
                var found = await Context.Ocr.Scanner.ListDevicesAsync();
                Devices.Clear();
                foreach (var device in found) Devices.Add(device);
                SelectedDevice = Devices.FirstOrDefault(d => d.Id == previous) ?? Devices.FirstOrDefault();
                DeviceStatus = Devices.Count == 0
                    ? "Nenhum scanner com driver WIA foi encontrado. Verifique se o aparelho está ligado, conectado e instalado no Windows."
                    : $"{Devices.Count} scanner(s) encontrado(s).";
            }
            catch (ScannerException ex)
            {
                Devices.Clear();
                SelectedDevice = null;
                DeviceStatus = ex.Message;
            }

            OnPropertyChanged(nameof(HasDevices));
        });
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (SelectedDevice is not { } device || !TryGetDpi(out var dpi)) return;
        var settings = new ScanSettings(SelectedSource.Value, SelectedColor.Value, dpi);

        await WhileBusyAsync("Digitalizando… acompanhe no aparelho.", async () =>
        {
            try
            {
                var progress = new Progress<int>(count => StatusText = $"Digitalizando… {count} página(s) recebida(s).");
                var files = await Context.Ocr.Scanner.ScanAsync(device.Id, settings, _sessionFolder, progress);
                foreach (var file in files) Pages.Add(new ScanPageViewModel(file, temporary: true, Move, Remove));
                PagesChanged();
                Context.Notifications.Show(NotificationKind.Success, "Digitalização concluída",
                    $"{files.Count} página(s) adicionada(s). Digitalize mais ou grave o PDF.");
            }
            catch (ScannerException ex)
            {
                Context.Notifications.Show(NotificationKind.Error, "A digitalização falhou", ex.Message);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task AddImagesAsync()
    {
        var patterns = string.Join(";", ImagesToPdfConverter.Extensions.Select(e => "*" + e));
        var picked = Context.Dialogs.PickFiles("Adicionar imagens já digitalizadas", $"Imagens|{patterns}", true, StartFolder());
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        foreach (var page in Pages) DeleteIfTemporary(page);
        Pages.Clear();
        PagesChanged();
    }

    /// <summary>Imagens arrastadas ou escolhidas entram como páginas, junto com as digitalizadas.</summary>
    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return Task.CompletedTask;
        var rejected = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path) && OcrService.IsImage(path))
                Pages.Add(new ScanPageViewModel(Path.GetFullPath(path), temporary: false, Move, Remove));
            else
                rejected++;
        }

        if (rejected > 0)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram adicionados",
                $"{rejected} arquivo(s) não são imagens (JPG, PNG, BMP, TIFF, GIF).");
        }

        if (Pages.Count > 0 && !HasOutputFolder)
        {
            var first = Pages.FirstOrDefault(p => !p.IsTemporary);
            if (first is not null) SuggestOutputFolder(first.Path);
        }

        PagesChanged();
        return Task.CompletedTask;
    }

    private void Move(ScanPageViewModel page, int direction)
    {
        if (IsBusy) return;
        var index = Pages.IndexOf(page);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Pages.Count) return;
        Pages.Move(index, target);
    }

    private void Remove(ScanPageViewModel page)
    {
        if (IsBusy) return;
        Pages.Remove(page);
        DeleteIfTemporary(page);
        PagesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Pages.Count == 0) return;
        var images = Pages.Select(p => p.Path).ToList();
        var target = CreateTarget();
        var name = string.IsNullOrWhiteSpace(FileName) ? "digitalizacao" : FileName.Trim();
        var options = new ImagesToPdfOptions(JpegQuality: UseJpeg ? 85 : null);
        var withOcr = RunOcr;
        OcrResult? ocr = null;

        var ok = await RunAsync(OperationName, $"{images.Count} página(s)", "Gravando o PDF…", async (progress, ct) =>
        {
            if (!withOcr)
                return [await Context.Conversion.ImagesToPdf.ConvertAsync(images, name, options, target, progress, ct)];

            // Com OCR: o PDF das imagens é montado em uma pasta temporária e o resultado final
            // (com a camada de texto) é o que vai para a pasta de destino.
            var work = Path.Combine(_sessionFolder, "pdf-" + Guid.NewGuid().ToString("N"));
            try
            {
                var plain = await Context.Conversion.ImagesToPdf.ConvertAsync(images, name, options,
                    new OutputTarget(work, OverwritePolicy.AutoRename, SubfolderMode.None),
                    new Progress<double>(v => progress.Report(v * 0.2)), ct);
                StatusText = "Reconhecendo o texto…";
                var language = Context.Ocr.Runtime.InstalledLanguages().Select(l => l.Code).FirstOrDefault()
                               ?? throw new PdfEngineException(OcrRuntime.NoLanguagesMessage);
                ocr = await Context.Ocr.Service.RecognizeAsync(plain.Path, new OcrOptions(language, SkipPagesWithText: false), target,
                    new Progress<OcrProgress>(p =>
                    {
                        progress.Report(0.2 + 0.8 * (p.Page - 1) / Math.Max(1, p.PageCount));
                        StatusText = $"Reconhecendo o texto… página {p.Page} de {p.PageCount}";
                    }), ct);
                return ocr.Files;
            }
            finally
            {
                TryDeleteFolder(work);
            }
        }, () => ocr is null ? null : $"OCR: {ocr.Words} palavra(s) reconhecida(s), confiança média de {ocr.MeanConfidence * 100:0}%.");

        if (ok)
        {
            Context.Notifications.Show(NotificationKind.Info, "Páginas mantidas na lista",
                "Use \"Limpar\" para começar um novo documento.");
        }
    }

    private static void DeleteIfTemporary(ScanPageViewModel page)
    {
        if (!page.IsTemporary) return;
        try
        {
            File.Delete(page.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fica na pasta temporária; é removida na limpeza de sessões antigas.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Remove páginas digitalizadas de sessões com mais de dois dias.</summary>
    private static void CleanOldSessions(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.EnumerateDirectories(root, "scan-*"))
            {
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-2)) TryDeleteFolder(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
