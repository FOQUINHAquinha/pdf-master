using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;

namespace PDFMaster.Batch;

/// <summary>Uma pasta monitorada: cada arquivo novo que chega nela passa pelo fluxo escolhido.</summary>
/// <param name="ProcessExisting">Ao ativar, também processa os arquivos que já estavam na pasta.</param>
public sealed record WatchFolder(
    Guid Id, string Name, string InputFolder, string FlowName, string OutputFolder,
    bool IncludeSubfolders = false, bool ProcessExisting = false, bool Enabled = true)
{
    /// <summary>Problema na configuração (null = válida).</summary>
    public string? Validate(FlowStore flows)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Dê um nome à pasta monitorada.";
        if (string.IsNullOrWhiteSpace(InputFolder) || !Directory.Exists(InputFolder)) return "A pasta de entrada não existe.";
        if (string.IsNullOrWhiteSpace(OutputFolder)) return "Escolha a pasta de destino.";
        if (flows.Find(FlowName) is null) return $"O fluxo \"{FlowName}\" não existe mais.";
        var input = Normalize(InputFolder);
        var output = Normalize(OutputFolder);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            return "A pasta de destino precisa ser diferente da pasta de entrada (senão os resultados seriam processados de novo).";
        if (IncludeSubfolders && output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "Com subpastas incluídas, o destino não pode ficar dentro da pasta de entrada.";
        return null;
    }

    internal static string Normalize(string folder) => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>Situação de uma pasta monitorada.</summary>
public sealed record WatchStatus(bool Active, int Queued, int Processed, int Failed, DateTime? LastActivity, string? Problem);

/// <summary>Configurações e arquivos já processados (JSON na pasta de dados).</summary>
public sealed class WatchStore
{
    private readonly string _configPath;
    private readonly string _seenPath;
    private readonly object _gate = new();
    private Dictionary<Guid, HashSet<string>>? _seen;

    public WatchStore(string dataDirectory)
    {
        _configPath = Path.Combine(dataDirectory, "pastas-monitoradas.json");
        _seenPath = Path.Combine(dataDirectory, "pastas-monitoradas-vistos.json");
    }

    public IReadOnlyList<WatchFolder> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_configPath)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<WatchFolder>>(File.ReadAllText(_configPath), FlowStore.Json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<WatchFolder> folders)
    {
        lock (_gate)
        {
            Write(_configPath, JsonSerializer.Serialize(folders, FlowStore.Json));
            // Esquece o que foi visto por pastas que não existem mais.
            var seen = Seen();
            foreach (var id in seen.Keys.Except(folders.Select(f => f.Id)).ToList()) seen.Remove(id);
            Write(_seenPath, JsonSerializer.Serialize(seen, FlowStore.Json));
        }
    }

    internal bool HasBaseline(Guid id)
    {
        lock (_gate) return Seen().ContainsKey(id);
    }

    internal bool WasSeen(Guid id, string key)
    {
        lock (_gate) return Seen().TryGetValue(id, out var set) && set.Contains(key);
    }

    internal void MarkSeen(Guid id, IEnumerable<string> keys)
    {
        lock (_gate)
        {
            var seen = Seen();
            if (!seen.TryGetValue(id, out var set)) seen[id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys) set.Add(key);
            Write(_seenPath, JsonSerializer.Serialize(seen, FlowStore.Json));
        }
    }

    private Dictionary<Guid, HashSet<string>> Seen()
    {
        if (_seen is not null) return _seen;
        try
        {
            _seen = File.Exists(_seenPath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, HashSet<string>>>(File.ReadAllText(_seenPath), FlowStore.Json) ?? []
                : [];
        }
        catch (JsonException)
        {
            _seen = [];
        }

        foreach (var key in _seen.Keys.ToList()) _seen[key] = new HashSet<string>(_seen[key], StringComparer.OrdinalIgnoreCase);
        return _seen;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Monitoramento de pastas: observa as pastas configuradas (eventos do Windows e uma varredura
/// periódica, que pega o que os eventos perderem), espera cada arquivo terminar de ser copiado e o
/// coloca na fila com o fluxo da pasta. Cada arquivo é processado uma vez (mudou de tamanho ou de
/// data = arquivo novo). O original nunca é alterado nem movido.
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly WatchStore _store;
    private readonly FlowStore _flows;
    private readonly BatchQueue _queue;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _scanInterval;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Watcher> _watchers = [];
    private readonly ConcurrentDictionary<Guid, (Guid Folder, string Key)> _jobs = new();
    private Timer? _timer;
    private bool _paused;
    private DateTime _lastScan = DateTime.MinValue;

    /// <param name="settle">Tempo sem mudança de tamanho/data para considerar o arquivo pronto.</param>
    public WatchService(WatchStore store, FlowStore flows, BatchQueue queue, TimeSpan? settle = null, TimeSpan? scanInterval = null)
    {
        _store = store;
        _flows = flows;
        _queue = queue;
        _settle = settle ?? TimeSpan.FromSeconds(2);
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(30);
        _queue.JobFinished += OnJobFinished;
    }

    /// <summary>Algo mudou (atividade, estado). Chamado fora da thread da interface.</summary>
    public event Action? Changed;

    /// <summary>Mensagens de atividade ("arquivo X entrou na fila"), para a tela e a linha de comando.</summary>
    public event Action<string>? Activity;

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _timer is not null;
        }
    }

    public IReadOnlyList<WatchFolder> Folders => _store.Load();

    public WatchStatus StatusOf(Guid id)
    {
        lock (_gate)
        {
            return _watchers.TryGetValue(id, out var w)
                ? new WatchStatus(w.Active && !_paused, w.Queued, w.Processed, w.Failed, w.LastActivity, w.Problem)
                : new WatchStatus(false, 0, 0, 0, null, null);
        }
    }

    /// <summary>Ativa as pastas habilitadas (e reinicia as que já estavam ativas).</summary>
    public void Start()
    {
        lock (_gate)
        {
            StopWatchers();
            foreach (var folder in _store.Load().Where(f => f.Enabled))
            {
                var watcher = new Watcher(folder);
                _watchers[folder.Id] = watcher;
                if (folder.Validate(_flows) is { } problem)
                {
                    watcher.Problem = problem;
                    continue;
                }

                try
                {
                    var input = WatchFolder.Normalize(folder.InputFolder);
                    watcher.FileWatcher = new FileSystemWatcher(input)
                    {
                        IncludeSubdirectories = folder.IncludeSubfolders,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var w = watcher;
                    watcher.FileWatcher.Created += (_, e) => Touch(w, e.FullPath);
                    watcher.FileWatcher.Changed += (_, e) => Touch(w, e.FullPath);
                    watcher.FileWatcher.Renamed += (_, e) => Touch(w, e.FullPath);
                    watcher.FileWatcher.Error += (_, _) => _lastScan = DateTime.MinValue;
                    watcher.FileWatcher.EnableRaisingEvents = true;
                    watcher.Active = true;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    watcher.Problem = "Não foi possível monitorar a pasta: " + ex.Message;
                    continue;
                }

                // Sem "processar existentes": o que já está na pasta na primeira ativação fica de fora.
                if (!folder.ProcessExisting && !_store.HasBaseline(folder.Id))
                    _store.MarkSeen(folder.Id, Candidates(folder).Select(Key).Where(k => k is not null).Cast<string>());
                else if (!_store.HasBaseline(folder.Id))
                    _store.MarkSeen(folder.Id, []);
            }

            _lastScan = DateTime.MinValue;
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(700));
        }

        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopWatchers();
            _timer?.Dispose();
            _timer = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Continua observando, mas não coloca nada na fila até retomar.</summary>
    public void Pause()
    {
        lock (_gate) _paused = true;
        Activity?.Invoke("Monitoramento pausado.");
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _lastScan = DateTime.MinValue;
        }

        Activity?.Invoke("Monitoramento retomado.");
        Changed?.Invoke();
    }

    /// <summary>Grava as configurações e reinicia o monitoramento se estiver ativo.</summary>
    public void Save(IReadOnlyList<WatchFolder> folders)
    {
        _store.Save(folders);
        if (IsRunning) Start();
        else Changed?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _queue.JobFinished -= OnJobFinished;
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers.Values) w.FileWatcher?.Dispose();
        _watchers.Clear();
    }

    private void Touch(Watcher watcher, string path)
    {
        lock (_gate)
        {
            if (Eligible(watcher.Folder, path)) watcher.Pending[path] = (-1, DateTime.MinValue, DateTime.UtcNow);
        }
    }

    private void Tick()
    {
        var queued = new List<(Watcher Watcher, string Path, string Key, BatchFlow Flow)>();
        lock (_gate)
        {
            if (_timer is null) return;
            var rescan = DateTime.UtcNow - _lastScan >= _scanInterval;
            if (rescan) _lastScan = DateTime.UtcNow;
            foreach (var watcher in _watchers.Values.Where(w => w.Active))
            {
                if (rescan)
                {
                    foreach (var file in Candidates(watcher.Folder))
                    {
                        if (!watcher.Pending.ContainsKey(file)) watcher.Pending[file] = (-1, DateTime.MinValue, DateTime.UtcNow);
                    }
                }

                if (_paused) continue;
                foreach (var (path, previous) in watcher.Pending.ToList())
                {
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(path);
                        if (!info.Exists)
                        {
                            watcher.Pending.Remove(path);
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    // Pronto quando tamanho e data não mudam durante o tempo de espera e ninguém está gravando.
                    if (info.Length != previous.Length || info.LastWriteTimeUtc != previous.Written)
                    {
                        watcher.Pending[path] = (info.Length, info.LastWriteTimeUtc, DateTime.UtcNow);
                        continue;
                    }

                    if (DateTime.UtcNow - previous.Since < _settle || info.Length == 0 || !CanRead(path)) continue;
                    watcher.Pending.Remove(path);
                    var key = Key(path);
                    if (key is null || _store.WasSeen(watcher.Folder.Id, key) || watcher.InQueue.Contains(key)) continue;
                    if (_flows.Find(watcher.Folder.FlowName) is not { } flow)
                    {
                        watcher.Problem = $"O fluxo \"{watcher.Folder.FlowName}\" não existe mais.";
                        continue;
                    }

                    watcher.InQueue.Add(key);
                    queued.Add((watcher, path, key, flow));
                }
            }
        }

        foreach (var (watcher, path, key, flow) in queued)
        {
            var target = new OutputTarget(watcher.Folder.OutputFolder, OverwritePolicy.AutoRename, SubfolderMode.None);
            var job = _queue.Enqueue(path, flow, target, "Pasta monitorada: " + watcher.Folder.Name);
            _jobs[job.Id] = (watcher.Folder.Id, key);
            lock (_gate)
            {
                watcher.Queued++;
                watcher.LastActivity = DateTime.Now;
            }

            Activity?.Invoke($"{watcher.Folder.Name}: \"{Path.GetFileName(path)}\" entrou na fila ({flow.Name}).");
        }

        if (queued.Count > 0)
        {
            _queue.Start();
            Changed?.Invoke();
        }
    }

    private void OnJobFinished(BatchJob job)
    {
        if (!_jobs.TryRemove(job.Id, out var origin)) return;
        _store.MarkSeen(origin.Folder, [origin.Key]);
        lock (_gate)
        {
            if (_watchers.TryGetValue(origin.Folder, out var watcher))
            {
                watcher.InQueue.Remove(origin.Key);
                watcher.Queued = Math.Max(0, watcher.Queued - 1);
                if (job.State == JobState.Completed) watcher.Processed++;
                else if (job.State == JobState.Failed) watcher.Failed++;
                watcher.LastActivity = DateTime.Now;
            }
        }

        Activity?.Invoke(job.State switch
        {
            JobState.Completed => $"\"{job.FileName}\" processado → {Path.GetFileName(job.OutputPath)}.",
            JobState.Failed => $"\"{job.FileName}\" falhou: {job.Error}",
            _ => $"\"{job.FileName}\" cancelado.",
        });
        Changed?.Invoke();
    }

    private static IEnumerable<string> Candidates(WatchFolder folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(WatchFolder.Normalize(folder.InputFolder), "*",
                folder.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (Eligible(folder, file)) yield return file;
        }
    }

    private static bool Eligible(WatchFolder folder, string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith(".pdfmaster-tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            return false;
        var output = WatchFolder.Normalize(folder.OutputFolder);
        if (Path.GetFullPath(path).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return FlowRunner.CanImport(path);
    }

    /// <summary>Identifica a versão do arquivo: caminho, tamanho e data de gravação.</summary>
    private static string? Key(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Watcher(WatchFolder folder)
    {
        public WatchFolder Folder { get; } = folder;
        public FileSystemWatcher? FileWatcher { get; set; }
        public bool Active { get; set; }
        public string? Problem { get; set; }
        public int Queued { get; set; }
        public int Processed { get; set; }
        public int Failed { get; set; }
        public DateTime? LastActivity { get; set; }
        public Dictionary<string, (long Length, DateTime Written, DateTime Since)> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> InQueue { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
