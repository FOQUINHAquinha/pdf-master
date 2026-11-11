using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;
using PDFMaster.Video;

namespace PDFMaster.App.ViewModels;

/// <summary>Um vídeo da fila, com os dados lidos pelo FFmpeg e o resultado da compressão.</summary>
public sealed partial class VideoFileViewModel : ObservableObject
{
    public VideoFileViewModel(string path, Action<VideoFileViewModel, int> move, Action<VideoFileViewModel> remove)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(Path);
        var info = new FileInfo(Path);
        Bytes = info.Exists ? info.Length : 0;
        _details = $"{FileSizeFormatter.Format(Bytes)} · {System.IO.Path.GetDirectoryName(Path)}";
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, 1));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public string Name { get; }
    public long Bytes { get; }

    /// <summary>Dados do vídeo; null enquanto não foi lido (ou quando não pôde ser lido).</summary>
    public VideoInfo? Info { get; private set; }

    [ObservableProperty]
    private string _details;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _failed;

    public void SetInfo(VideoInfo info)
    {
        Info = info;
        var (width, height) = info.TargetSize();
        Details = $"{info.Summary} · {FileSizeFormatter.Format(Bytes)} → MP4 {width}×{height}";
    }

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>Comprime vídeos de qualquer formato para MP4 em 360p, em fila.</summary>
public sealed partial class VideoViewModel : ToolViewModelBase
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private Task? _engineTask;

    public VideoViewModel(ToolContext context)
        : base(context, "Comprimir vídeo",
            "Converte vídeos de qualquer formato para MP4 em 360p — bem menores, para enviar e guardar.")
    {
        Refresh();
        _engineTask = DetectEngineAsync();
    }

    public ObservableCollection<VideoFileViewModel> Files { get; } = [];

    public override bool IsEmpty => Files.Count == 0;
    protected internal override IReadOnlyList<string> RepeatInputs => Files.Select(f => f.Path).ToList();
    public override bool AcceptsOpenPdfs => false;
    public bool HasFiles => Files.Count > 0;

    /// <summary>Qual FFmpeg está em uso (ou que ele está sendo preparado).</summary>
    [ObservableProperty]
    private string _engineText = "Preparando o FFmpeg…";

    /// <summary>Por que o FFmpeg não pode ser usado; null quando está pronto.</summary>
    [ObservableProperty]
    private string? _engineProblem;

    [ObservableProperty]
    private bool _engineReady;

    [ObservableProperty]
    private bool _usesCustomEngine;

    [ObservableProperty]
    private string _summary = string.Empty;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PickFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        LocateFfmpegCommand.NotifyCanExecuteChanged();
        UseBundledFfmpegCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanClear() => !IsBusy && Files.Count > 0;
    private bool CanRun() => !IsBusy && EngineReady && Files.Count > 0;
    private bool CanUseBundled() => !IsBusy && UsesCustomEngine;

    partial void OnEngineReadyChanged(bool value) => OnStateChanged();
    partial void OnUsesCustomEngineChanged(bool value) => OnStateChanged();

    private async Task DetectEngineAsync()
    {
        EngineReady = false;
        EngineProblem = null;
        EngineText = "Preparando o FFmpeg… (na primeira vez ele é extraído para a pasta de dados, o que leva alguns segundos)";
        UsesCustomEngine = !string.IsNullOrEmpty(Context.Settings.Current.FfmpegPath);
        try
        {
            var info = await Context.Video.Runtime.GetAsync();
            EngineText = "Mecanismo de vídeo: " + info.Description;
            EngineReady = true;
        }
        catch (VideoException ex)
        {
            EngineText = "Mecanismo de vídeo indisponível.";
            EngineProblem = ex.Message;
        }
        catch (Exception ex)
        {
            EngineText = "Mecanismo de vídeo indisponível.";
            EngineProblem = "Não foi possível preparar o FFmpeg: " + ex.Message;
        }

        Refresh();
    }

    private async Task EnsureEngineAsync()
    {
        var task = _engineTask ??= DetectEngineAsync();
        await task;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFilesAsync()
    {
        var videos = string.Join(";", VideoCompressor.Extensions.Select(e => "*" + e));
        var picked = Context.Dialogs.PickFiles("Adicionar vídeos",
            $"Vídeos|{videos}|Todos os arquivos|*.*", true, StartFolder());
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        FilesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task LocateFfmpegAsync()
    {
        var picked = Context.Dialogs.PickFiles("Localizar o FFmpeg", "FFmpeg (ffmpeg.exe)|ffmpeg.exe|Programas (*.exe)|*.exe", false, null);
        if (picked.Count == 0) return;

        await WhileBusyAsync("Verificando o FFmpeg escolhido…", async () =>
        {
            try
            {
                var info = await Context.Video.Runtime.ValidateAsync(picked[0]);
                await Context.Settings.UpdateAsync(s => s.FfmpegPath = info.Path);
                Context.Video.Runtime.CustomPath = info.Path;
                Context.Notifications.Show(NotificationKind.Success, "FFmpeg escolhido", info.Description);
            }
            catch (VideoException ex)
            {
                Context.Notifications.Show(NotificationKind.Error, "Este FFmpeg não pode ser usado", ex.Message);
                return;
            }

            _engineTask = DetectEngineAsync();
            await _engineTask;
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseBundled))]
    private async Task UseBundledFfmpegAsync()
    {
        await Context.Settings.UpdateAsync(s => s.FfmpegPath = null);
        Context.Video.Runtime.CustomPath = null;
        _engineTask = DetectEngineAsync();
        await _engineTask;
    }

    public override async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;

        var added = new List<VideoFileViewModel>();
        var missing = 0;
        foreach (var path in Expand(paths))
        {
            if (!File.Exists(path))
            {
                missing++;
                continue;
            }

            var full = Path.GetFullPath(path);
            if (Files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            var file = new VideoFileViewModel(full, Move, Remove);
            Files.Add(file);
            added.Add(file);
        }

        if (missing > 0)
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram encontrados", $"{missing} arquivo(s) não existem mais.");

        if (Files.Count > 0) SuggestOutputFolder(Files[0].Path);
        FilesChanged();
        if (added.Count == 0) return;

        // Lê cada vídeo para mostrar duração e resolução e recusar o que não for vídeo antes de começar.
        await WhileBusyAsync("Lendo os vídeos…", async () =>
        {
            await EnsureEngineAsync();
            if (!EngineReady) return;
            foreach (var file in added)
            {
                try
                {
                    file.SetInfo(await Context.Video.Compressor.ProbeAsync(file.Path));
                    file.Status = null;
                    file.Failed = false;
                }
                catch (VideoException ex)
                {
                    file.Status = ex.Message;
                    file.Failed = true;
                }
            }
        });
        Refresh();
    }

    /// <summary>Pastas soltas na tela entram com os vídeos que estão diretamente nelas.</summary>
    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                IEnumerable<string> inside;
                try
                {
                    inside = Directory.EnumerateFiles(path).Where(VideoCompressor.HasVideoExtension)
                        .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in inside) yield return file;
            }
            else
            {
                yield return path;
            }
        }
    }

    private void Move(VideoFileViewModel file, int direction)
    {
        if (IsBusy) return;
        var index = Files.IndexOf(file);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count) return;
        Files.Move(index, target);
    }

    private void Remove(VideoFileViewModel file)
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

    private void Refresh()
    {
        var total = Files.Sum(f => f.Bytes);
        var duration = Files.Where(f => f.Info?.Duration is not null).Aggregate(TimeSpan.Zero, (sum, f) => sum + f.Info!.Duration!.Value);
        Summary = Files.Count switch
        {
            0 => "Adicione os vídeos (ou arraste-os para cá). Qualquer formato que o FFmpeg abra é aceito.",
            1 => $"1 vídeo ({FileSizeFormatter.Format(total)}). O original não é alterado: o resultado é gravado como \"nome_360p.mp4\".",
            _ => $"{Files.Count} vídeos na fila ({FileSizeFormatter.Format(total)}" +
                 (duration > TimeSpan.Zero ? ", " + VideoInfo.FormatDuration(duration) + " no total" : string.Empty) +
                 "). Cada resultado é gravado como \"nome_360p.mp4\"; um vídeo com problema não interrompe os demais.",
        };
        OnStateChanged();
    }

    private static string Clock(TimeSpan time) => VideoInfo.FormatDuration(time);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        if (!EngineReady || Files.Count == 0) return;

        foreach (var file in Files.Where(f => !f.Failed || f.Info is not null))
        {
            file.Status = null;
            file.Failed = false;
        }

        var files = Files.ToList();
        var target = CreateTarget();
        var inputName = files.Count == 1 ? files[0].Name : $"{files.Count} vídeos";
        var failures = 0;
        long before = 0;
        long after = 0;
        var watch = Stopwatch.StartNew();

        // O progresso geral pondera cada vídeo pela duração (ou conta 1 quando a duração é desconhecida).
        var weights = files.Select(f => Math.Max(1, f.Info?.Duration?.TotalSeconds ?? 0)).ToArray();
        var totalWeight = weights.Sum();

        await RunAsync(VideoCompressor.OperationName, inputName, "Preparando a conversão…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = OutputPathResolver.CreateReservationSet();
            string? lastError = null;
            double done = 0;

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                var index = i;
                var weight = weights[i];
                var doneBefore = done;

                void Report(VideoProgress p)
                {
                    var overall = (doneBefore + weight * p.Fraction) / totalWeight;
                    progress.Report(overall);
                    var elapsed = watch.Elapsed;
                    var remaining = overall > 0.02
                        ? " · restante ~" + Clock(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - overall) / overall))
                        : string.Empty;
                    var speed = p.Speed is { } s && s > 0 ? $" · {s.ToString("0.0", PtBr)}x" : string.Empty;
                    var errors = failures > 0 ? $" · {failures} com erro" : string.Empty;
                    StatusText = $"Vídeo {index + 1} de {files.Count}: {file.Name} — {p.Fraction * 100:0}%{speed} · " +
                                 $"decorrido {Clock(elapsed)}{remaining}{errors}";
                    file.Status = $"Convertendo… {p.Fraction * 100:0}%";
                }

                Report(new VideoProgress(0, TimeSpan.Zero, file.Info?.Duration, null));
                try
                {
                    var result = await Context.Video.Compressor.CompressAsync(file.Path, target, new Progress<VideoProgress>(Report), ct, reserved);
                    produced.Add(result.File);
                    before += result.SourceBytes;
                    after += result.File.Bytes;
                    file.Status = $"{result.Output.DisplayWidth}×{result.Output.DisplayHeight} · {FileSizeFormatter.Format(result.File.Bytes)} " +
                                  SavingText(result.Saving) + " → " + Path.GetFileName(result.File.Path);
                }
                catch (Exception ex) when (ex is VideoException or PdfEngineException or IOException or UnauthorizedAccessException)
                {
                    file.Status = ex.Message;
                    file.Failed = true;
                    failures++;
                    lastError = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    file.Status = "Cancelado. Nenhum arquivo incompleto foi deixado.";
                    throw;
                }

                done += weight;
                progress.Report(done / totalWeight);
            }

            if (produced.Count == 0)
                throw new VideoException(files.Count == 1 && lastError is not null ? lastError : "Nenhum vídeo pôde ser convertido; veja o motivo de cada um na lista.");
            return produced;
        }, () =>
        {
            var text = $"Tempo: {Clock(watch.Elapsed)}.";
            if (before > 0) text += $" {FileSizeFormatter.Format(before)} → {FileSizeFormatter.Format(after)} {SavingText(1 - after / (double)before)}.";
            if (failures > 0) text += $"\n{failures} vídeo(s) não puderam ser convertidos; veja o motivo na lista.";
            return text;
        }, file => $"MP4 360p, {FileSizeFormatter.Format(file.Bytes)}");
    }

    private static string SavingText(double saving) =>
        saving >= 0
            ? $"({(saving * 100).ToString("0", PtBr)}% menor)"
            : $"(ficou {(-saving * 100).ToString("0", PtBr)}% maior que o original, que já era leve)";
}
