using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PDFMaster.App.Services;
using PDFMaster.Compression;
using PDFMaster.Conversion;
using PDFMaster.Core;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Core.Settings;
using PDFMaster.Domain;
using PDFMaster.Infrastructure;
using PDFMaster.OCR;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PDFMaster.Scanner;
using PDFMaster.Video;
using PDFMaster.Editor;

namespace PDFMaster.App.ViewModels;

/// <summary>Serviços compartilhados por todas as telas de ferramenta.</summary>
public sealed record ToolContext(
    IPdfEngine Engine,
    PdfOrganizer Organizer,
    IPdfRenderer Renderer,
    ThumbnailService Thumbnails,
    SettingsService Settings,
    IHistoryRepository History,
    IRecentFilesRepository Recent,
    IDialogService Dialogs,
    ShellLauncher Shell,
    NotificationCenter Notifications,
    ILoggerFactory LoggerFactory,
    ConversionToolkit Conversion,
    OcrToolkit Ocr,
    VideoToolkit Video,
    EditorToolkit Editor,
    SecurityToolkit Security);

/// <summary>Segurança e ferramentas avançadas (Fase 6).</summary>
/// <param name="TrustedRootsFolder">Pasta onde a pessoa coloca certificados raiz adicionais (ex.: ICP-Brasil).</param>
public sealed record SecurityToolkit(
    PDFMaster.Security.PdfProtection Protection,
    PDFMaster.Security.PdfMetadata Metadata,
    PDFMaster.Security.PdfRepair Repair,
    PDFMaster.Security.PdfAConverter PdfA,
    PDFMaster.Comparison.ComparisonReport CompareReport,
    string TrustedRootsFolder);

/// <summary>Editor de PDF e as bibliotecas locais de carimbos e assinaturas (Fase 5).</summary>
public sealed record EditorToolkit(PdfEditor Editor, StampLibrary Stamps, SignatureLibrary Signatures);

/// <summary>Compressão de vídeo (FFmpeg).</summary>
public sealed record VideoToolkit(FfmpegRuntime Runtime, VideoCompressor Compressor);

/// <summary>Serviços de OCR e digitalização (Fase 4).</summary>
public sealed record OcrToolkit(OcrRuntime Runtime, OcrService Service, WiaScanner Scanner);

/// <summary>Serviços de conversão e compressão (Fase 3).</summary>
public sealed record ConversionToolkit(
    PdfToImageConverter PdfToImage,
    ImagesToPdfConverter ImagesToPdf,
    OfficeToPdfConverter Office,
    TextToPdfConverter Text,
    PdfTextExporter PdfText,
    PdfCompressor Compressor,
    LibreOffice LibreOffice);

/// <summary>
/// Base das telas de ferramenta: estado de ocupado, progresso, cancelamento, pasta de destino,
/// registro no histórico e notificação do resultado.
/// </summary>
public abstract partial class ToolViewModelBase : ObservableObject
{
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    protected ToolViewModelBase(ToolContext context, string title, string subtitle)
    {
        Context = context;
        Title = title;
        Subtitle = subtitle;
        _logger = context.LoggerFactory.CreateLogger(GetType());
        _outputFolder = context.Settings.Current.DefaultOutputFolder ?? string.Empty;
    }

    protected ToolContext Context { get; }
    public string Title { get; protected set; }
    public string Subtitle { get; protected set; }

    /// <summary>Definido pela janela principal: volta para a lista de ferramentas.</summary>
    public Action? CloseRequested { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _outputFolder;

    public bool HasOutputFolder => OutputFolder.Length > 0;

    partial void OnOutputFolderChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutputFolder));
        OnStateChanged();
    }

    partial void OnIsBusyChanged(bool value) => OnStateChanged();

    /// <summary>Chamado quando algo que afeta os botões muda (ocupado, pasta, arquivos).</summary>
    protected virtual void OnStateChanged()
    {
    }

    /// <summary>Recebe arquivos por seleção, arrastar e soltar ou linha de comando.</summary>
    public abstract Task AddFilesAsync(IReadOnlyList<string> paths);

    /// <summary>Verdadeiro quando a tela ainda não tem nenhum documento carregado.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>Se a tela recebe os PDFs abertos na página inicial ao ser aberta sem arquivos.</summary>
    public virtual bool AcceptsOpenPdfs => true;

    /// <summary>
    /// Executa a ação principal da tela (o botão de destaque), se ela estiver habilitada.
    /// Usado pela linha de comando (--run). Devolve false quando não havia o que executar.
    /// </summary>
    public virtual Task<bool> RunPrimaryAsync() => Task.FromResult(false);

    protected static async Task<bool> ExecuteIfPossibleAsync(IAsyncRelayCommand command)
    {
        if (!command.CanExecute(null)) return false;
        await command.ExecuteAsync(null);
        return true;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var folder = Context.Dialogs.PickFolder("Pasta de destino", HasOutputFolder ? OutputFolder : null);
        if (folder is not null) OutputFolder = folder;
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelando…";
    }

    /// <summary>Se a pasta de destino ainda está vazia, usa a pasta do arquivo aberto.</summary>
    protected void SuggestOutputFolder(string filePath)
    {
        if (HasOutputFolder) return;
        OutputFolder = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
    }

    protected OutputTarget CreateTarget()
    {
        var settings = Context.Settings.Current;
        return new OutputTarget(OutputFolder, settings.OverwritePolicy, settings.SubfolderMode);
    }

    protected static IReadOnlyList<string> OnlyPdfs(IReadOnlyList<string> paths) =>
        paths.Where(p => SupportedFiles.GetKind(p) == FileKind.Pdf && File.Exists(p)).ToList();

    protected string? StartFolder()
    {
        var s = Context.Settings.Current;
        return s.RememberLastFolder && !string.IsNullOrEmpty(s.LastFolder) ? s.LastFolder : s.DefaultOutputFolder;
    }

    protected IReadOnlyList<string> PickPdfs(bool multiple)
    {
        var s = Context.Settings.Current;
        var start = s.RememberLastFolder && !string.IsNullOrEmpty(s.LastFolder) ? s.LastFolder : s.DefaultOutputFolder;
        return Context.Dialogs.PickPdfs(multiple, start);
    }

    /// <summary>Lê os dados do PDF; em caso de erro, avisa o usuário e devolve null.</summary>
    protected async Task<PdfDocumentInfo?> TryReadInfoAsync(string path)
    {
        try
        {
            var info = await Context.Engine.ReadInfoAsync(path);
            if (info.PageCount == 0)
            {
                Context.Notifications.Show(NotificationKind.Warning, "Documento sem páginas", Path.GetFileName(path));
                return null;
            }

            if (Context.Settings.Current.HistoryEnabled)
                await Context.Recent.TouchAsync(info.Path, DateTime.UtcNow);
            return info;
        }
        catch (PdfEngineException ex)
        {
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível abrir o PDF", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ler {Path}", path);
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível abrir o PDF", ex.Message);
            return null;
        }
    }

    /// <summary>Executa em segundo plano algo que não gera arquivos (ex.: carregar documentos).</summary>
    protected async Task WhileBusyAsync(string status, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = status;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha em {Tool}", Title);
            Context.Notifications.Show(NotificationKind.Error, "Ocorreu um erro", ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            StatusText = string.Empty;
        }
    }

    /// <summary>
    /// Executa em segundo plano uma análise que não gera arquivos, com progresso e cancelamento.
    /// Devolve false quando foi cancelada ou falhou (o erro já foi mostrado).
    /// </summary>
    protected async Task<bool> AnalyzeAsync(string busyText, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (IsBusy) return false;
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        StatusText = busyText;
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(value => ProgressValue = Math.Clamp(value, 0, 1) * 100);
        try
        {
            await work(progress, _cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Context.Notifications.Show(NotificationKind.Info, $"{Title}: cancelado", "A análise foi interrompida.");
            return false;
        }
        catch (Exception ex) when (ex is PdfEngineException or PdfRenderException or IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Error, $"{Title}: falhou", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha inesperada em {Tool}", Title);
            Context.Notifications.Show(NotificationKind.Error, $"{Title}: falhou", ex.Message + "\nOs detalhes foram gravados no registro de diagnósticos local.");
            return false;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            ProgressValue = 0;
            StatusText = string.Empty;
        }
    }

    /// <summary>
    /// Executa uma operação que gera arquivos: mostra progresso, permite cancelar, registra no
    /// histórico e notifica o resultado com ações para abrir o arquivo e a pasta.
    /// </summary>
    protected async Task<bool> RunAsync(
        string operation,
        string inputName,
        string busyText,
        Func<IProgress<double>, CancellationToken, Task<IReadOnlyList<ProducedFile>>> work,
        Func<string?>? extraMessage = null,
        Func<ProducedFile, string>? describe = null)
    {
        if (IsBusy) return false;

        if (!HasOutputFolder)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Escolha a pasta de destino", "Indique onde o resultado deve ser gravado.");
            return false;
        }

        var settings = Context.Settings.Current;
        if (settings.OverwritePolicy == OverwritePolicy.Overwrite &&
            !Context.Dialogs.Confirm("Substituir arquivos existentes",
                "A configuração atual substitui arquivos de mesmo nome na pasta de destino. Os arquivos de origem nunca são substituídos.\n\nDeseja continuar?"))
        {
            return false;
        }

        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        StatusText = busyText;
        _cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        var progress = new Progress<double>(value => ProgressValue = Math.Clamp(value, 0, 1) * 100);
        string Describe(ProducedFile file) =>
            describe?.Invoke(file) ?? $"{file.PageCount} página(s), {FileSizeFormatter.Format(file.Bytes)}";

        try
        {
            var files = await work(progress, _cts.Token);
            watch.Stop();

            if (settings.HistoryEnabled)
            {
                foreach (var file in files)
                {
                    await Context.History.AddAsync(new HistoryRecord(0, inputName, operation, started,
                        $"{Path.GetFileName(file.Path)} — {Describe(file)}",
                        file.Path, watch.Elapsed, OperationStatus.Completed));
                }
            }

            var first = files[0];
            var message = files.Count == 1
                ? $"{Path.GetFileName(first.Path)}\n{Describe(first)}"
                : $"{files.Count} arquivos gerados ({FileSizeFormatter.Format(files.Sum(f => f.Bytes))}) em\n{Path.GetDirectoryName(first.Path)}";

            if (extraMessage?.Invoke() is { Length: > 0 } extra) message += "\n" + extra;

            Context.Notifications.Show(NotificationKind.Success, $"{operation}: concluído", message,
                new NotificationAction("Abrir arquivo", () => Context.Shell.OpenDocument(first.Path)),
                new NotificationAction("Abrir pasta", () => Context.Shell.OpenFolder(first.Path)));
            return true;
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(inputName, operation, started, watch.Elapsed, "Cancelado pelo usuário.", OperationStatus.Cancelled);
            Context.Notifications.Show(NotificationKind.Info, $"{operation}: cancelado", "Nenhum arquivo incompleto foi deixado na pasta de destino.");
            return false;
        }
        catch (Exception ex) when (ex is PdfEngineException or PageRangeException or PdfRenderException or VideoException
                                       or IOException or UnauthorizedAccessException)
        {
            await RecordFailureAsync(inputName, operation, started, watch.Elapsed, ex.Message, OperationStatus.Failed);
            Context.Notifications.Show(NotificationKind.Error, $"{operation}: falhou", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha inesperada em {Operation}", operation);
            await RecordFailureAsync(inputName, operation, started, watch.Elapsed, ex.Message, OperationStatus.Failed);
            Context.Notifications.Show(NotificationKind.Error, $"{operation}: falhou",
                ex.Message + "\nOs detalhes foram gravados no registro de diagnósticos local.");
            return false;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            ProgressValue = 0;
            StatusText = string.Empty;
        }
    }

    private async Task RecordFailureAsync(string inputName, string operation, DateTime started, TimeSpan duration,
        string message, OperationStatus status)
    {
        if (!Context.Settings.Current.HistoryEnabled) return;
        try
        {
            await Context.History.AddAsync(new HistoryRecord(0, inputName, operation, started, message, null, duration, status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao registrar o histórico");
        }
    }
}
