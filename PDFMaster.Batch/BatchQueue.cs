using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;

namespace PDFMaster.Batch;

public enum JobState
{
    Waiting,
    Processing,
    Completed,
    Failed,
    Cancelled,
    Paused,
}

/// <summary>Uma tarefa da fila: um arquivo passando por um fluxo.</summary>
public sealed class BatchJob : INotifyPropertyChanged
{
    private JobState _state;
    private double _progress;
    private string _step = string.Empty;
    private long? _finalBytes;
    private string? _outputPath;
    private TimeSpan _elapsed;
    private string? _error;
    private IReadOnlyList<string> _notes = [];
    private OutputTarget _target;

    internal BatchJob(string source, BatchFlow flow, OutputTarget target, string origin)
    {
        SourcePath = Path.GetFullPath(source);
        FileName = Path.GetFileName(SourcePath);
        Flow = flow;
        _target = target;
        Origin = origin;
        OriginalBytes = File.Exists(SourcePath) ? new FileInfo(SourcePath).Length : 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();
    public string SourcePath { get; }
    public string FileName { get; }
    public BatchFlow Flow { get; }
    public OutputTarget Target
    {
        get => _target;
        internal set
        {
            Set(ref _target, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Destination)));
        }
    }

    /// <summary>De onde veio a tarefa ("Fila", "Pasta monitorada: …", "Linha de comando").</summary>
    public string Origin { get; }

    public string Operation => Flow.Name;
    public string Destination => Target.Folder;
    public long OriginalBytes { get; }

    public JobState State { get => _state; internal set => Set(ref _state, value); }
    public double Progress { get => _progress; internal set => Set(ref _progress, value); }
    public string Step { get => _step; internal set => Set(ref _step, value); }
    public long? FinalBytes { get => _finalBytes; internal set => Set(ref _finalBytes, value); }
    public string? OutputPath { get => _outputPath; internal set => Set(ref _outputPath, value); }
    public TimeSpan Elapsed { get => _elapsed; internal set => Set(ref _elapsed, value); }
    public string? Error { get => _error; internal set => Set(ref _error, value); }
    public IReadOnlyList<string> Notes { get => _notes; internal set => Set(ref _notes, value); }

    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    internal CancellationTokenSource? Cancellation { get; set; }

    internal void Reset()
    {
        State = JobState.Waiting;
        Progress = 0;
        Step = string.Empty;
        FinalBytes = null;
        OutputPath = null;
        Elapsed = TimeSpan.Zero;
        Error = null;
        Notes = [];
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(State)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFinished)));
    }
}

/// <summary>
/// Fila de tarefas com processamento em paralelo limitado (<see cref="MaxParallel"/>), pausa entre
/// etapas, cancelamento por tarefa ou geral, repetição e reprocessamento das que falharam.
/// </summary>
public sealed class BatchQueue
{
    public const int MaxAllowedParallel = 8;

    private readonly FlowRunner _runner;
    private readonly List<BatchJob> _jobs = [];
    private readonly object _gate = new();
    private readonly object _reserveGate = new();
    private readonly ISet<string> _reserved = OutputPathResolver.CreateReservationSet();
    private readonly SemaphoreSlim _wake = new(0);
    private TaskCompletionSource _resume = NewSource(completed: true);
    private TaskCompletionSource _idle = NewSource(completed: true);
    private int _maxParallel = DefaultParallel;
    private int _active;
    private bool _running;
    private bool _paused;

    public BatchQueue(FlowRunner runner) => _runner = runner;

    /// <summary>Metade dos núcleos, entre 1 e 4 (OCR e PDF/A usam bastante memória).</summary>
    public static int DefaultParallel => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Tarefa concluída, com falha ou cancelada (chamado fora da thread da interface).</summary>
    public event Action<BatchJob>? JobFinished;

    /// <summary>A fila começou, pausou, retomou ou ficou ociosa.</summary>
    public event Action? StateChanged;

    /// <summary>Tarefas entraram ou saíram da lista.</summary>
    public event Action? JobsChanged;

    public int MaxParallel
    {
        get => _maxParallel;
        set
        {
            _maxParallel = Math.Clamp(value, 1, MaxAllowedParallel);
            _wake.Release();
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    public IReadOnlyList<BatchJob> Jobs
    {
        get
        {
            lock (_gate) return _jobs.ToList();
        }
    }

    public BatchJob Enqueue(string path, BatchFlow flow, OutputTarget target, string origin = "Fila")
    {
        var job = new BatchJob(path, flow, target, origin);
        lock (_gate)
        {
            _jobs.Add(job);
            if (_running) _idle = Busy(_idle);
        }

        _wake.Release();
        JobsChanged?.Invoke();
        return job;
    }

    /// <summary>Começa (ou continua) a processar as tarefas que aguardam.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_paused)
            {
                _paused = false;
                _resume.TrySetResult();
            }

            if (!_running)
            {
                _running = true;
                _idle = Busy(_idle);
                _ = Task.Run(LoopAsync);
            }
        }

        _wake.Release();
        StateChanged?.Invoke();
    }

    /// <summary>Não inicia tarefas novas; as em andamento param ao fim da etapa atual.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused || !_running) return;
            _paused = true;
            _resume = NewSource(completed: false);
        }

        StateChanged?.Invoke();
    }

    public void Resume() => Start();

    public void Cancel(BatchJob job)
    {
        lock (_gate)
        {
            if (job.State == JobState.Waiting) job.State = JobState.Cancelled;
            job.Cancellation?.Cancel();
        }

        _wake.Release();
    }

    /// <summary>Cancela as tarefas em andamento e as que aguardam.</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var job in _jobs)
            {
                if (job.State == JobState.Waiting) job.State = JobState.Cancelled;
                job.Cancellation?.Cancel();
            }

            if (_paused)
            {
                _paused = false;
                _resume.TrySetResult();
            }
        }

        _wake.Release();
        StateChanged?.Invoke();
    }

    /// <summary>Muda o destino de uma tarefa que ainda aguarda; devolve false se ela já começou.</summary>
    public bool Retarget(BatchJob job, OutputTarget target)
    {
        lock (_gate)
        {
            if (job.State != JobState.Waiting) return false;
            job.Target = target;
            return true;
        }
    }

    /// <summary>Volta a tarefa para a fila (concluída, com falha ou cancelada).</summary>
    public bool Retry(BatchJob job)
    {
        lock (_gate)
        {
            if (!job.IsFinished || !_jobs.Contains(job)) return false;
            job.Reset();
            if (_running) _idle = Busy(_idle);
        }

        _wake.Release();
        return true;
    }

    /// <summary>Volta para a fila só as tarefas que falharam.</summary>
    public int RetryFailed()
    {
        List<BatchJob> failed;
        lock (_gate) failed = _jobs.Where(j => j.State == JobState.Failed).ToList();
        return failed.Count(Retry);
    }

    public void Remove(BatchJob job)
    {
        lock (_gate)
        {
            job.Cancellation?.Cancel();
            _jobs.Remove(job);
        }

        _wake.Release();
        JobsChanged?.Invoke();
    }

    /// <summary>Tira da lista as tarefas concluídas (com ou sem sucesso).</summary>
    public int ClearFinished()
    {
        int removed;
        lock (_gate) removed = _jobs.RemoveAll(j => j.IsFinished);
        if (removed > 0) JobsChanged?.Invoke();
        return removed;
    }

    /// <summary>Completa quando não houver tarefa aguardando nem em andamento.</summary>
    public Task WhenIdleAsync(CancellationToken ct = default)
    {
        lock (_gate) return _idle.Task.WaitAsync(ct);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                while (!_paused && _active < _maxParallel && _jobs.FirstOrDefault(j => j.State == JobState.Waiting) is { } next)
                {
                    var job = next;
                    job.State = JobState.Processing;
                    job.Cancellation = new CancellationTokenSource();
                    _active++;
                    _ = Task.Run(() => RunAsync(job));
                }

                if (_active == 0 && !_jobs.Any(j => j.State == JobState.Waiting))
                {
                    _running = false;
                    _paused = false;
                    _resume.TrySetResult();
                    _idle.TrySetResult();
                    break;
                }
            }

            await _wake.WaitAsync().ConfigureAwait(false);
        }

        StateChanged?.Invoke();
    }

    private async Task RunAsync(BatchJob job)
    {
        var watch = Stopwatch.StartNew();
        var ct = job.Cancellation!.Token;
        try
        {
            var progress = new SyncProgress<FlowProgress>(p =>
            {
                job.Progress = p.Fraction;
                job.Step = p.Step == 0 ? p.StepName : $"Etapa {p.Step} de {p.StepCount}: {p.StepName}";
                job.Elapsed = watch.Elapsed;
            });
            var result = await _runner.RunAsync(job.SourcePath, job.Flow, job.Target, progress, PauseAsync, ct, _reserved, _reserveGate)
                .ConfigureAwait(false);
            job.OutputPath = result.File.Path;
            job.FinalBytes = result.File.Bytes;
            job.Notes = result.Notes;
            job.Progress = 1;
            job.Step = "Concluído";
            job.State = JobState.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Step = "Cancelado";
            job.State = JobState.Cancelled;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            job.Error = ex is PdfEngineException or IOException or UnauthorizedAccessException or PDFMaster.Rendering.PdfRenderException
                ? ex.Message
                : $"Erro inesperado: {ex.Message}";
            job.Step = "Falhou";
            job.State = JobState.Failed;
        }
        finally
        {
            job.Elapsed = watch.Elapsed;
            lock (_gate)
            {
                job.Cancellation?.Dispose();
                job.Cancellation = null;
                _active--;
            }

            _wake.Release();
        }

        JobFinished?.Invoke(job);

        async Task PauseAsync(CancellationToken token)
        {
            Task wait;
            lock (_gate) wait = _resume.Task;
            if (wait.IsCompleted) return;
            job.State = JobState.Paused;
            await wait.WaitAsync(token).ConfigureAwait(false);
            job.State = JobState.Processing;
        }
    }

    private static TaskCompletionSource NewSource(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }

    private static TaskCompletionSource Busy(TaskCompletionSource current) => current.Task.IsCompleted ? NewSource(completed: false) : current;

    /// <summary>IProgress sem contexto de sincronização (as propriedades da tarefa avisam a interface).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
