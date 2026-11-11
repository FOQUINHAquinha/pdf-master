using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Batch;
using PDFMaster.Compression;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Security;

namespace PDFMaster.App.ViewModels;

/// <summary>Executa no fio da interface (a fila e o monitoramento avisam de outros fios).</summary>
internal static class Ui
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

// =====================================================================================
// Fila de tarefas
// =====================================================================================

/// <summary>Uma linha da fila: nome, operação, estado, progresso, tamanhos, destino, tempo e erro.</summary>
public sealed partial class BatchJobRow : ObservableObject
{
    private readonly BatchViewModel _owner;

    public BatchJobRow(BatchJob job, BatchViewModel owner)
    {
        Job = job;
        _owner = owner;
        job.PropertyChanged += OnJobChanged;
    }

    public BatchJob Job { get; }
    public string Name => Job.FileName;
    public string SourcePath => Job.SourcePath;
    public string Operation => Job.Operation;
    public string Origin => Job.Origin;

    public string StateText => Job.State switch
    {
        JobState.Waiting => "Aguardando",
        JobState.Processing => "Processando",
        JobState.Completed => "Concluído",
        JobState.Failed => "Falhou",
        JobState.Cancelled => "Cancelado",
        _ => "Pausado",
    };

    public double ProgressPercent => Job.Progress * 100;
    public string StepText => Job.Step;
    public string OriginalSize => FileSizeFormatter.Format(Job.OriginalBytes);
    public string FinalSize => Job.FinalBytes is { } bytes ? FileSizeFormatter.Format(bytes) : "—";
    public string Destination => Job.OutputPath ?? Job.Destination;
    public string ElapsedText => Job.Elapsed == TimeSpan.Zero ? "—" : Job.Elapsed.TotalSeconds < 60
        ? Job.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) + " s"
        : $"{(int)Job.Elapsed.TotalMinutes} min {Job.Elapsed.Seconds:00} s";
    public string? Error => Job.Error;
    public string? NotesText => Job.Notes.Count > 0 ? string.Join(" ", Job.Notes) : null;
    public bool IsFailed => Job.State == JobState.Failed;
    public bool IsCompleted => Job.State == JobState.Completed;
    public bool CanCancel => !Job.IsFinished;
    public bool CanRetry => Job.IsFinished;

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e) => Ui.Post(() =>
    {
        OnPropertyChanged(string.Empty);
        OpenCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        if (e.PropertyName == nameof(BatchJob.State)) _owner.JobStateChanged();
    });

    public void Detach() => Job.PropertyChanged -= OnJobChanged;

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private void Open() => _owner.OpenResult(this);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => _owner.Retry(this);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _owner.CancelJob(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveJob(this);
}

/// <summary>
/// Processamento em lote: os arquivos entram na fila com o fluxo escolhido (uma ou várias etapas) e
/// são processados em paralelo, com pausa, cancelamento, repetição e reprocessamento das falhas.
/// </summary>
public sealed partial class BatchViewModel : ToolViewModelBase
{
    private readonly BatchQueue _queue;

    public BatchViewModel(ToolContext context)
        : base(context, "Fila de tarefas",
            "Aplique um fluxo (OCR, comprimir, numerar, PDF/A…) a muitos documentos. Cada arquivo gera uma cópia nova; os originais não são alterados.")
    {
        _queue = context.Automation.Queue;
        Editor = new FlowEditorViewModel(context.Automation.Flows, context.Notifications, ReloadFlows);
        ReloadFlows();
        _queue.JobsChanged += () => Ui.Post(Sync);
        _queue.StateChanged += () => Ui.Post(JobStateChanged);
        Sync();
    }

    public ObservableCollection<BatchFlow> Flows { get; } = [];
    public ObservableCollection<BatchJobRow> Jobs { get; } = [];
    public FlowEditorViewModel Editor { get; }

    public override bool IsEmpty => Jobs.Count == 0;
    public override bool AcceptsOpenPdfs => false;
    protected internal override IReadOnlyList<string> RepeatInputs => Jobs.Select(j => j.SourcePath).ToList();
    public bool HasJobs => Jobs.Count > 0;

    [ObservableProperty] private BatchFlow? _selectedFlow;
    [ObservableProperty] private bool _isEditingFlows;
    [ObservableProperty] private string _summary = "Nenhuma tarefa na fila.";

    public string ParallelText => $"Até {_queue.MaxParallel} tarefa(s) ao mesmo tempo (altere em Configurações › Padrões e desempenho).";
    public bool IsRunning => _queue.IsRunning && !_queue.IsPaused;
    public bool IsPaused => _queue.IsPaused;
    public string? SelectedFlowDescription => SelectedFlow is null ? null : $"{SelectedFlow.Describe()} · sufixo \"{SelectedFlow.Suffix}\"";

    partial void OnSelectedFlowChanged(BatchFlow? value) => OnPropertyChanged(nameof(SelectedFlowDescription));

    private void ReloadFlows()
    {
        var selected = SelectedFlow?.Name;
        Flows.Clear();
        foreach (var flow in Context.Automation.Flows.Load()) Flows.Add(flow);
        SelectedFlow = Flows.FirstOrDefault(f => f.Name == selected) ?? Flows.FirstOrDefault();
    }

    private void Sync()
    {
        var current = _queue.Jobs;
        foreach (var row in Jobs.Where(r => !current.Contains(r.Job)).ToList())
        {
            row.Detach();
            Jobs.Remove(row);
        }

        foreach (var job in current.Where(j => Jobs.All(r => r.Job != j))) Jobs.Add(new BatchJobRow(job, this));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasJobs));
        JobStateChanged();
    }

    internal void JobStateChanged()
    {
        var jobs = _queue.Jobs;
        int Count(JobState s) => jobs.Count(j => j.State == s);
        var parts = new List<string>();
        void Add(int n, string text)
        {
            if (n > 0) parts.Add($"{n} {text}");
        }

        Add(Count(JobState.Waiting), "aguardando");
        Add(Count(JobState.Processing), "processando");
        Add(Count(JobState.Paused), "pausada(s)");
        Add(Count(JobState.Completed), "concluída(s)");
        Add(Count(JobState.Failed), "com falha");
        Add(Count(JobState.Cancelled), "cancelada(s)");
        Summary = jobs.Count == 0 ? "Nenhuma tarefa na fila." : $"{jobs.Count} tarefa(s): {string.Join(", ", parts)}." + (_queue.IsPaused ? " Fila pausada." : string.Empty);
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(ParallelText));
        OnStateChanged();
    }

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    public override Task<bool> RunPrimaryAsync()
    {
        if (!CanStart()) return Task.FromResult(false);
        Start();
        return Task.FromResult(true);
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (SelectedFlow is null)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Escolha um fluxo", "Crie ou escolha um fluxo antes de adicionar arquivos.");
            return Task.CompletedTask;
        }

        var files = new List<string>();
        var skipped = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Somente os arquivos da própria pasta (sem subpastas), na ordem do nome.
                var inFolder = Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase).ToList();
                files.AddRange(inFolder.Where(FlowRunner.CanImport));
                skipped += inFolder.Count(f => !FlowRunner.CanImport(f));
            }
            else if (File.Exists(path) && FlowRunner.CanImport(path))
            {
                files.Add(Path.GetFullPath(path));
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0)
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos ficaram de fora",
                $"{skipped} arquivo(s) não são PDF, imagem, texto, HTML ou documento do Office.");
        if (files.Count == 0) return Task.CompletedTask;

        SuggestOutputFolder(files[0]);
        var target = CreateTarget();
        foreach (var file in files) _queue.Enqueue(file, SelectedFlow, target);
        Context.Notifications.Show(NotificationKind.Info, "Arquivos na fila",
            $"{files.Count} arquivo(s) com o fluxo \"{SelectedFlow.Name}\". Clique em Iniciar para processar.");
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddFilesPickAsync()
    {
        var picked = Context.Dialogs.PickFiles("Arquivos para a fila",
            "Documentos suportados|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.txt;*.html;*.htm;*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.odt;*.ods;*.odp;*.rtf|PDF|*.pdf",
            multiple: true, StartFolder());
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = Context.Dialogs.PickFolder("Pasta com os documentos", StartFolder());
        if (folder is not null) await AddFilesAsync([folder]);
    }

    private bool CanStart() => _queue.Jobs.Any(j => j.State == JobState.Waiting) && (!_queue.IsRunning || _queue.IsPaused);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (!HasOutputFolder)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Escolha a pasta de destino", "Indique onde os resultados devem ser gravados.");
            return;
        }

        _queue.Start();
        JobStateChanged();
    }

    private bool CanPause() => _queue.IsRunning && !_queue.IsPaused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _queue.Pause();
        JobStateChanged();
    }

    private bool CanResume() => _queue.IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _queue.Resume();
        JobStateChanged();
    }

    private bool CanCancelAll() => _queue.Jobs.Any(j => !j.IsFinished);

    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        if (!Context.Dialogs.Confirm("Cancelar a fila", "As tarefas em andamento e as que aguardam serão canceladas. Arquivos já concluídos permanecem.\n\nDeseja continuar?"))
            return;
        _queue.CancelAll();
    }

    private bool CanRetryFailed() => _queue.Jobs.Any(j => j.State == JobState.Failed);

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private void RetryFailed()
    {
        var count = _queue.RetryFailed();
        if (count > 0) _queue.Start();
        JobStateChanged();
    }

    private bool CanClearFinished() => _queue.Jobs.Any(j => j.IsFinished);

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished() => _queue.ClearFinished();

    [RelayCommand]
    private void ToggleFlowEditor()
    {
        IsEditingFlows = !IsEditingFlows;
        if (IsEditingFlows) Editor.Load(SelectedFlow?.Name);
    }

    internal void OpenResult(BatchJobRow row)
    {
        if (row.Job.OutputPath is not { } path || !Context.Shell.OpenDocument(path))
            Context.Notifications.Show(NotificationKind.Warning, "Arquivo não encontrado", row.Job.OutputPath ?? row.Name);
    }

    internal void Retry(BatchJobRow row)
    {
        if (_queue.Retry(row.Job)) _queue.Start();
        JobStateChanged();
    }

    internal void CancelJob(BatchJobRow row) => _queue.Cancel(row.Job);

    internal void RemoveJob(BatchJobRow row) => _queue.Remove(row.Job);
}

// =====================================================================================
// Editor de fluxos
// =====================================================================================

/// <summary>Um campo de opção de uma etapa (texto, número, sim/não ou lista).</summary>
public sealed partial class StepField : ObservableObject
{
    public StepField(ParameterInfo parameter, object? value, Action changed)
    {
        Parameter = parameter;
        Label = StepLabels.Label(parameter.Name!);
        var type = parameter.ParameterType;
        if (type == typeof(bool))
        {
            IsBool = true;
            _flag = (bool)value!;
        }
        else if (type.IsEnum)
        {
            IsChoice = true;
            Choices = Enum.GetValues(type).Cast<object>()
                .Where(v => !(v is CompressionProfile p && p == CompressionProfile.Custom))
                .Select(v => new Choice<object>(v, StepLabels.EnumName(v))).ToList();
            _selectedChoice = Choices.FirstOrDefault(c => c.Value.Equals(value)) ?? Choices[0];
        }
        else
        {
            IsText = true;
            _text = value switch
            {
                double d => d.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")),
                int i => i.ToString(CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? string.Empty,
            };
        }

        _changed = changed;
    }

    private readonly Action _changed;

    public ParameterInfo Parameter { get; }
    public string Label { get; }
    public bool IsBool { get; }
    public bool IsChoice { get; }
    public bool IsText { get; }
    public IReadOnlyList<Choice<object>> Choices { get; } = [];

    [ObservableProperty] private bool _flag;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private Choice<object>? _selectedChoice;

    partial void OnFlagChanged(bool value) => _changed();
    partial void OnTextChanged(string value) => _changed();
    partial void OnSelectedChoiceChanged(Choice<object>? value) => _changed();

    /// <summary>Valor do campo; null com <paramref name="problem"/> quando o texto não é um número válido.</summary>
    public object? Value(out string? problem)
    {
        problem = null;
        var type = Parameter.ParameterType;
        if (IsBool) return Flag;
        if (IsChoice) return SelectedChoice?.Value;
        if (type == typeof(string)) return Text;
        var normalized = Text.Trim().Replace(',', '.');
        if (type == typeof(int))
        {
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        }
        else if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            return d;
        }

        problem = $"{Label}: informe um número.";
        return null;
    }
}

/// <summary>Uma etapa sendo editada: as opções viram campos, montados a partir do registro da etapa.</summary>
public sealed partial class StepDraft : ObservableObject
{
    private readonly Type _type;
    private readonly ConstructorInfo _constructor;

    public StepDraft(FlowStep step)
    {
        _type = step.GetType();
        _constructor = _type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        Fields = _constructor.GetParameters()
            .Select(p => new StepField(p, _type.GetProperty(p.Name!)!.GetValue(step), Refresh))
            .ToList();
        Name = step.Name;
        Refresh();
    }

    public IReadOnlyList<StepField> Fields { get; }
    public bool HasFields => Fields.Count > 0;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string? _problem;

    /// <summary>Monta a etapa com os valores atuais; null quando algum campo é inválido.</summary>
    public FlowStep? Build(out string? problem)
    {
        var values = new object?[Fields.Count];
        for (var i = 0; i < Fields.Count; i++)
        {
            values[i] = Fields[i].Value(out problem);
            if (problem is not null) return null;
        }

        var step = (FlowStep)_constructor.Invoke(values);
        problem = step.Validate();
        return step;
    }

    private void Refresh()
    {
        var step = Build(out var problem);
        Problem = problem;
        if (step is not null)
        {
            Name = step.Name;
            Description = step.Describe();
        }
    }
}

/// <summary>Um fluxo sendo editado.</summary>
public sealed partial class FlowDraft : ObservableObject
{
    public FlowDraft(BatchFlow flow)
    {
        _name = flow.Name;
        _suffix = flow.Suffix;
        foreach (var step in flow.Steps) Steps.Add(new StepDraft(step));
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _suffix;
    public ObservableCollection<StepDraft> Steps { get; } = [];

    public BatchFlow? Build(out string? problem)
    {
        var steps = new List<FlowStep>();
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i].Build(out problem);
            if (step is null || problem is not null)
            {
                problem = $"Fluxo \"{Name}\", etapa {i + 1} ({Steps[i].Name}): {problem}";
                return null;
            }

            steps.Add(step);
        }

        var flow = new BatchFlow(Name.Trim(), steps, Suffix.Trim());
        problem = flow.Validate() is { } p ? $"Fluxo \"{Name}\": {p}" : null;
        return flow;
    }
}

/// <summary>Editor visual de fluxos: lista de fluxos, etapas em ordem e as opções de cada etapa.</summary>
public sealed partial class FlowEditorViewModel : ObservableObject
{
    private readonly FlowStore _store;
    private readonly NotificationCenter _notifications;
    private readonly Action _saved;

    public FlowEditorViewModel(FlowStore store, NotificationCenter notifications, Action saved)
    {
        _store = store;
        _notifications = notifications;
        _saved = saved;
        StepTypes = FlowStep.Catalog.Select(s => new Choice<FlowStep>(s, s is NUpStep ? "Várias páginas por folha / livreto" : s.Name)).ToList();
        _selectedStepType = StepTypes[0];
    }

    public ObservableCollection<FlowDraft> Flows { get; } = [];
    public IReadOnlyList<Choice<FlowStep>> StepTypes { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteFlowCommand), nameof(DuplicateFlowCommand), nameof(AddStepCommand))]
    private FlowDraft? _selectedFlow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStepCommand), nameof(MoveStepUpCommand), nameof(MoveStepDownCommand))]
    private StepDraft? _selectedStep;

    [ObservableProperty] private Choice<FlowStep> _selectedStepType;
    [ObservableProperty] private string? _status;

    public void Load(string? select)
    {
        Flows.Clear();
        foreach (var flow in _store.Load()) Flows.Add(new FlowDraft(flow));
        SelectedFlow = Flows.FirstOrDefault(f => f.Name == select) ?? Flows.FirstOrDefault();
        Status = null;
    }

    partial void OnSelectedFlowChanged(FlowDraft? value) => SelectedStep = value?.Steps.FirstOrDefault();

    [RelayCommand]
    private void NewFlow()
    {
        var name = "Novo fluxo";
        for (var i = 2; Flows.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"Novo fluxo {i}";
        var draft = new FlowDraft(new BatchFlow(name, [new CompressStep()]));
        Flows.Add(draft);
        SelectedFlow = draft;
    }

    private bool HasFlow() => SelectedFlow is not null;

    [RelayCommand(CanExecute = nameof(HasFlow))]
    private void DuplicateFlow()
    {
        if (SelectedFlow?.Build(out var problem) is not { } flow || problem is not null)
        {
            Status = "Corrija o fluxo antes de duplicá-lo.";
            return;
        }

        var copy = new FlowDraft(flow with { Name = flow.Name + " (cópia)" });
        Flows.Add(copy);
        SelectedFlow = copy;
    }

    [RelayCommand(CanExecute = nameof(HasFlow))]
    private void DeleteFlow()
    {
        if (SelectedFlow is null) return;
        var index = Flows.IndexOf(SelectedFlow);
        Flows.Remove(SelectedFlow);
        SelectedFlow = Flows.Count == 0 ? null : Flows[Math.Min(index, Flows.Count - 1)];
        Status = "Fluxo removido da lista. Clique em Salvar fluxos para confirmar.";
    }

    [RelayCommand(CanExecute = nameof(HasFlow))]
    private void AddStep()
    {
        if (SelectedFlow is null) return;
        var step = new StepDraft(SelectedStepType.Value);
        var index = SelectedStep is null ? SelectedFlow.Steps.Count : SelectedFlow.Steps.IndexOf(SelectedStep) + 1;
        SelectedFlow.Steps.Insert(index, step);
        SelectedStep = step;
    }

    private bool HasStep() => SelectedStep is not null;

    [RelayCommand(CanExecute = nameof(HasStep))]
    private void RemoveStep()
    {
        if (SelectedFlow is null || SelectedStep is null) return;
        var index = SelectedFlow.Steps.IndexOf(SelectedStep);
        SelectedFlow.Steps.Remove(SelectedStep);
        SelectedStep = SelectedFlow.Steps.Count == 0 ? null : SelectedFlow.Steps[Math.Min(index, SelectedFlow.Steps.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasStep))]
    private void MoveStepUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasStep))]
    private void MoveStepDown() => Move(1);

    private void Move(int direction)
    {
        if (SelectedFlow is null || SelectedStep is null) return;
        var index = SelectedFlow.Steps.IndexOf(SelectedStep);
        var target = index + direction;
        if (target < 0 || target >= SelectedFlow.Steps.Count) return;
        var step = SelectedStep;
        SelectedFlow.Steps.Move(index, target);
        SelectedStep = step;
    }

    [RelayCommand]
    private void Save()
    {
        var flows = new List<BatchFlow>();
        foreach (var draft in Flows)
        {
            var flow = draft.Build(out var problem);
            if (flow is null || problem is not null)
            {
                Status = problem;
                SelectedFlow = draft;
                return;
            }

            flows.Add(flow);
        }

        try
        {
            _store.Save(flows);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            return;
        }

        Status = $"{flows.Count} fluxo(s) salvos.";
        _notifications.Show(NotificationKind.Success, "Fluxos salvos", $"{flows.Count} fluxo(s) disponíveis na fila, na linha de comando e nas pastas monitoradas.");
        _saved();
    }

    [RelayCommand]
    private void Discard() => Load(SelectedFlow?.Name);
}

/// <summary>Rótulos em português das opções das etapas.</summary>
internal static class StepLabels
{
    public static string Label(string parameter) => parameter switch
    {
        "Languages" => "Idiomas (códigos do Tesseract, ex.: por+eng)",
        "Dpi" => "Resolução (DPI, 100 a 600)",
        "SkipPagesWithText" => "Pular páginas que já têm texto pesquisável",
        "Profile" => "Perfil de compressão",
        "Text" => "Texto (campos: {pagina}, {total}, {arquivo}, {data})",
        "Position" => "Posição",
        "FontSize" => "Tamanho da fonte (pt)",
        "OpacityPercent" => "Opacidade (%)",
        "Rotation" => "Inclinação (graus)",
        "Color" => "Cor (#RRGGBB)",
        "Degrees" => "Graus (90, 180 ou 270)",
        "Pages" => "Páginas (ex.: 1, 3-5; vazio = todas)",
        "AlsoDuplicates" => "Remover também páginas repetidas",
        "TopMm" => "Margem de cima (mm)",
        "BottomMm" => "Margem de baixo (mm)",
        "LeftMm" => "Margem da esquerda (mm)",
        "RightMm" => "Margem da direita (mm)",
        "PagesPerSheet" => "Páginas por folha (2 ou 4)",
        "Booklet" => "Montar livreto (ignora páginas por folha)",
        "Sheet" => "Tamanho da folha",
        "Level" => "Nível PDF/A",
        _ => parameter,
    };

    public static string EnumName(object value) => value switch
    {
        CompressionProfile p => CompressionSettings.Describe(p),
        NumberPosition p => PageNumbersStep.PositionName(p),
        SheetSize.A4 => "A4",
        SheetSize.Letter => "Carta",
        SheetSize.A3 => "A3",
        PdfALevel l => PdfA.Name(l),
        _ => value.ToString() ?? string.Empty,
    };
}

// =====================================================================================
// Pastas monitoradas
// =====================================================================================

/// <summary>Uma pasta monitorada na lista, com contadores.</summary>
public sealed class WatchFolderRow
{
    public WatchFolderRow(WatchFolder folder, WatchStatus status)
    {
        Folder = folder;
        StateText = !folder.Enabled ? "Desativada"
            : status.Problem is { } problem ? "Com problema: " + problem
            : status.Active ? "Monitorando" : "Parada";
        Counters = $"{status.Queued} na fila · {status.Processed} processado(s) · {status.Failed} com falha" +
                   (status.LastActivity is { } last ? $" · último arquivo às {last.ToLocalTime():HH:mm:ss}" : string.Empty);
        HasProblem = folder.Enabled && status.Problem is not null;
    }

    public WatchFolder Folder { get; }
    public string Name => Folder.Name;
    public string Route => $"{Folder.InputFolder}  →  {Folder.OutputFolder}";
    public string FlowText => "Fluxo: " + Folder.FlowName + (Folder.IncludeSubfolders ? " · inclui subpastas" : string.Empty);
    public string StateText { get; }
    public string Counters { get; }
    public bool HasProblem { get; }
}

/// <summary>
/// Monitoramento de pastas: cada PDF (ou outro documento suportado) que chega à pasta de entrada
/// passa pelo fluxo escolhido e o resultado vai para a pasta de saída. Opcional, configurável e pausável.
/// </summary>
public sealed partial class WatchFolderViewModel : ToolViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly WatchService _watch;
    private Guid? _editingId;

    public WatchFolderViewModel(ToolContext context)
        : base(context, "Monitorar pasta",
            "Processa automaticamente cada documento que chega a uma pasta, com o fluxo escolhido. Funciona com o programa aberto (ou com PDFMaster.exe --watch).")
    {
        _watch = context.Automation.Watch;
        _watch.Changed += () => Ui.Post(Refresh);
        _watch.Activity += message => Ui.Post(() =>
        {
            Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (Log.Count > MaxLogLines) Log.RemoveAt(Log.Count - 1);
        });
        Refresh();
    }

    public ObservableCollection<WatchFolderRow> Folders { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<string> FlowNames { get; } = [];

    public override bool IsEmpty => Folders.Count == 0;
    public override bool AcceptsOpenPdfs => false;
    public bool HasFolders => Folders.Count > 0;
    public bool IsPaused => _watch.IsPaused;
    public bool IsRunning => _watch.IsRunning;
    public string ServiceText => !_watch.IsRunning ? "Monitoramento parado." : _watch.IsPaused ? "Monitoramento pausado: arquivos novos aguardam a retomada." : "Monitoramento ativo.";

    [ObservableProperty] private WatchFolderRow? _selectedFolder;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editInput = string.Empty;
    [ObservableProperty] private string _editOutput = string.Empty;
    [ObservableProperty] private string? _editFlow;
    [ObservableProperty] private bool _editSubfolders;
    [ObservableProperty] private bool _editProcessExisting;
    [ObservableProperty] private bool _editEnabled = true;
    [ObservableProperty] private string? _editProblem;

    private void Refresh()
    {
        var selected = SelectedFolder?.Folder.Id;
        Folders.Clear();
        foreach (var folder in _watch.Folders) Folders.Add(new WatchFolderRow(folder, _watch.StatusOf(folder.Id)));
        SelectedFolder = Folders.FirstOrDefault(f => f.Folder.Id == selected);
        FlowNames.Clear();
        foreach (var flow in Context.Automation.Flows.Load()) FlowNames.Add(flow.Name);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ServiceText));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        // Arrastar uma pasta para a tela começa o cadastro com ela como entrada.
        if (paths.FirstOrDefault(Directory.Exists) is { } folder)
        {
            New();
            EditInput = folder;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void New()
    {
        _editingId = null;
        EditName = "Pasta monitorada " + (Folders.Count + 1);
        EditInput = string.Empty;
        EditOutput = string.Empty;
        EditFlow = FlowNames.FirstOrDefault();
        EditSubfolders = false;
        EditProcessExisting = false;
        EditEnabled = true;
        EditProblem = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void Edit(WatchFolderRow? row)
    {
        row ??= SelectedFolder;
        if (row is null) return;
        var f = row.Folder;
        _editingId = f.Id;
        EditName = f.Name;
        EditInput = f.InputFolder;
        EditOutput = f.OutputFolder;
        EditFlow = FlowNames.Contains(f.FlowName) ? f.FlowName : FlowNames.FirstOrDefault();
        EditSubfolders = f.IncludeSubfolders;
        EditProcessExisting = f.ProcessExisting;
        EditEnabled = f.Enabled;
        EditProblem = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void BrowseInput()
    {
        var folder = Context.Dialogs.PickFolder("Pasta a monitorar (entrada)", EditInput.Length > 0 ? EditInput : StartFolder());
        if (folder is not null) EditInput = folder;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var folder = Context.Dialogs.PickFolder("Pasta para os resultados (saída)", EditOutput.Length > 0 ? EditOutput : StartFolder());
        if (folder is not null) EditOutput = folder;
    }

    [RelayCommand]
    private void SaveFolder()
    {
        if (EditFlow is null)
        {
            EditProblem = "Crie um fluxo na Fila de tarefas antes de monitorar uma pasta.";
            return;
        }

        if (EditInput.Length == 0 || EditOutput.Length == 0)
        {
            EditProblem = "Escolha a pasta de entrada e a de saída.";
            return;
        }

        var folder = new WatchFolder(_editingId ?? Guid.NewGuid(), EditName.Trim(), Path.GetFullPath(EditInput), EditFlow, Path.GetFullPath(EditOutput),
            EditSubfolders, EditProcessExisting, EditEnabled);
        if (folder.Validate(Context.Automation.Flows) is { } problem)
        {
            EditProblem = problem;
            return;
        }

        var all = _watch.Folders.Where(f => f.Id != folder.Id).Append(folder).ToList();
        if (!Persist(all)) return;
        IsEditing = false;
        if (folder.Enabled && !_watch.IsRunning) _watch.Start();
        Refresh();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditProblem = null;
    }

    [RelayCommand]
    private void Toggle(WatchFolderRow? row)
    {
        if (row is null) return;
        var all = _watch.Folders.Select(f => f.Id == row.Folder.Id ? f with { Enabled = !f.Enabled } : f).ToList();
        if (!Persist(all)) return;
        if (all.Any(f => f.Enabled) && !_watch.IsRunning) _watch.Start();
        Refresh();
    }

    [RelayCommand]
    private void Delete(WatchFolderRow? row)
    {
        if (row is null) return;
        if (!Context.Dialogs.Confirm("Excluir pasta monitorada",
                $"\"{row.Name}\" deixa de ser monitorada. Nenhum arquivo é apagado.\n\nDeseja continuar?"))
            return;
        Persist(_watch.Folders.Where(f => f.Id != row.Folder.Id).ToList());
        Refresh();
    }

    private bool Persist(IReadOnlyList<WatchFolder> folders)
    {
        try
        {
            _watch.Save(folders);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível salvar", ex.Message);
            return false;
        }
    }

    private bool CanPause() => _watch.IsRunning && !_watch.IsPaused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _watch.Pause();
        Refresh();
    }

    private bool CanResume() => _watch.IsPaused || (!_watch.IsRunning && _watch.Folders.Any(f => f.Enabled));

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        if (_watch.IsRunning) _watch.Resume();
        else _watch.Start();
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder(WatchFolderRow? row)
    {
        if (row is not null && !Context.Shell.OpenFolder(row.Folder.OutputFolder))
            Context.Notifications.Show(NotificationKind.Warning, "Pasta não encontrada", row.Folder.OutputFolder);
    }
}

// =====================================================================================
// Ferramentas de página da Fase 7
// =====================================================================================

public enum PageToolKind
{
    Reverse,
    BlankPages,
    Crop,
    NUp,
}

/// <summary>Inverter ordem, remover páginas em branco/repetidas, recortar margens e várias páginas por folha / livreto.</summary>
public sealed partial class PageToolViewModel : FileListViewModel
{
    public PageToolViewModel(ToolContext context, PageToolKind kind)
        : base(context, TitleOf(kind), SubtitleOf(kind), single: false)
    {
        Kind = kind;
        Sheets = [new(SheetSize.A4, "A4"), new(SheetSize.Letter, "Carta"), new(SheetSize.A3, "A3")];
        _selectedSheet = Sheets[0];
    }

    public PageToolKind Kind { get; }
    public bool IsBlank => Kind == PageToolKind.BlankPages;
    public bool IsCrop => Kind == PageToolKind.Crop;
    public bool IsNUp => Kind == PageToolKind.NUp;
    public IReadOnlyList<Choice<SheetSize>> Sheets { get; }

    public override string EmptyHint => "Arraste para cá os PDFs";

    [ObservableProperty] private bool _alsoDuplicates;
    [ObservableProperty] private string _topMm = "10";
    [ObservableProperty] private string _bottomMm = "10";
    [ObservableProperty] private string _leftMm = "10";
    [ObservableProperty] private string _rightMm = "10";
    [ObservableProperty] private bool _twoPerSheet = true;
    [ObservableProperty] private bool _fourPerSheet;
    [ObservableProperty] private bool _booklet;
    [ObservableProperty] private Choice<SheetSize> _selectedSheet;
    [ObservableProperty] private string? _result;

    public string ActionText => Kind switch
    {
        PageToolKind.Reverse => "Inverter ordem",
        PageToolKind.BlankPages => "Remover páginas",
        PageToolKind.Crop => "Recortar margens",
        _ => "Montar folhas",
    };

    private static string TitleOf(PageToolKind kind) => kind switch
    {
        PageToolKind.Reverse => PageTools.ReverseOperation,
        PageToolKind.BlankPages => "Páginas em branco e repetidas",
        PageToolKind.Crop => PageTools.CropOperation,
        _ => "Livreto e várias páginas por folha",
    };

    private static string SubtitleOf(PageToolKind kind) => kind switch
    {
        PageToolKind.Reverse => "Grava uma cópia com as páginas da última para a primeira (útil para digitalizações feitas na ordem inversa).",
        PageToolKind.BlankPages => "Encontra páginas sem texto e sem conteúdo visível (e, se quiser, páginas idênticas a uma anterior) e grava uma cópia sem elas.",
        PageToolKind.Crop => "Reduz a área visível de todas as páginas pelas margens informadas. O conteúdo fora da área não é apagado do arquivo.",
        _ => "Coloca 2 ou 4 páginas em cada folha, ou monta um livreto para imprimir frente e verso e dobrar ao meio.",
    };

    protected override void StateChanged() => ApplyCommand.NotifyCanExecuteChanged();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(ApplyCommand);

    private bool CanApply() => !IsBusy && HasFiles && HasOutputFolder;

    private static bool TryMm(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 500;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        double top = 0, bottom = 0, left = 0, right = 0;
        if (IsCrop && !(TryMm(TopMm, out top) && TryMm(BottomMm, out bottom) && TryMm(LeftMm, out left) && TryMm(RightMm, out right)))
        {
            Context.Notifications.Show(NotificationKind.Warning, "Margens inválidas", "Informe cada margem em milímetros, entre 0 e 500.");
            return;
        }

        var files = Files.Select(f => f.Path).ToList();
        var target = CreateTarget();
        var summaries = new List<string>();
        var pages = Context.Automation.Pages;
        var perSheet = FourPerSheet ? 4 : 2;
        Result = null;
        await RunAsync(Title, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} arquivos", "Processando…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                StatusText = $"{Path.GetFileName(file)} ({i + 1} de {files.Count})…";
                var result = Kind switch
                {
                    PageToolKind.Reverse => await pages.ReverseAsync(file, target, ct),
                    PageToolKind.BlankPages => await pages.RemoveBlankPagesAsync(file, AlsoDuplicates, target, ct),
                    PageToolKind.Crop => await pages.CropAsync(file, top, bottom, left, right, target, ct),
                    _ => await pages.NUpAsync(file, perSheet, Booklet, SelectedSheet.Value, target, ct),
                };
                summaries.Add($"{Path.GetFileName(file)}: {result.Summary}");
                if (result.File is not null) produced.Add(result.File);
                progress.Report((i + 1.0) / files.Count);
            }

            if (produced.Count == 0) throw new PdfEngineException(string.Join("\n", summaries));
            return produced;
        });
        if (summaries.Count > 0) Result = string.Join("\n", summaries);
    }
}
