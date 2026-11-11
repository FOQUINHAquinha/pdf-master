using System.Globalization;
using PDFMaster.Compression;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.OCR;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PDFMaster.Security;

namespace PDFMaster.Batch;

/// <summary>Todos os serviços de processamento, para a linha de comando, a fila e os testes.</summary>
public sealed class AutomationServices
{
    public AutomationServices(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        var resolver = new OutputPathResolver();
        Planner = new OutputPlanner(resolver);
        Engine = new PdfSharpEngine();
        Renderer = new PdfiumRenderer();
        Organizer = new PdfOrganizer(Engine, resolver);
        Images = new ImagesToPdfConverter(Planner);
        Text = new TextToPdfConverter(Planner);
        Office = new OfficeToPdfConverter(new LibreOffice(), Engine, Planner);
        ToImage = new PdfToImageConverter(Engine, Renderer, Planner);
        Compressor = new PdfCompressor(Planner);
        Ocr = new OcrService(new OcrRuntime(dataDirectory), Engine, Renderer, Images, Planner);
        Editor = new PdfEditor(Planner);
        PdfA = new PdfAConverter(Planner);
        Metadata = new PdfMetadata(Planner);
        Repair = new PdfRepair(Planner);
        Pages = new PageTools(Engine, Organizer, Renderer, Planner);
        Flows = new FlowStore(dataDirectory);
        Runner = new FlowRunner(new FlowServices(Engine, Organizer, Compressor, Ocr, Editor, PdfA, Metadata, Repair, Images, Text, Office, Pages, Planner));
        Queue = new BatchQueue(Runner);
        WatchStore = new WatchStore(dataDirectory);
    }

    public string DataDirectory { get; }
    public OutputPlanner Planner { get; }
    public IPdfEngine Engine { get; }
    public IPdfRenderer Renderer { get; }
    public PdfOrganizer Organizer { get; }
    public ImagesToPdfConverter Images { get; }
    public TextToPdfConverter Text { get; }
    public OfficeToPdfConverter Office { get; }
    public PdfToImageConverter ToImage { get; }
    public PdfCompressor Compressor { get; }
    public OcrService Ocr { get; }
    public PdfEditor Editor { get; }
    public PdfAConverter PdfA { get; }
    public PdfMetadata Metadata { get; }
    public PdfRepair Repair { get; }
    public PageTools Pages { get; }
    public FlowStore Flows { get; }
    public FlowRunner Runner { get; }
    public BatchQueue Queue { get; }
    public WatchStore WatchStore { get; }
}

/// <summary>Comando lido da linha de comando.</summary>
public sealed record CliCommand(
    string Verb,
    IReadOnlyList<string> Inputs,
    string? Output,
    bool Overwrite,
    IReadOnlyDictionary<string, string> Options,
    string? LogFile)
{
    public string? Option(string name) => Options.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Execução sem janela: <c>PDFMaster.exe --merge a.pdf b.pdf --output resultado.pdf</c> e similares.
/// Códigos de saída: 0 sucesso, 1 parâmetros inválidos, 2 falha na operação, 3 sucesso parcial (lote).
/// </summary>
public static class CommandLine
{
    public const int Success = 0;
    public const int InvalidArguments = 1;
    public const int Failed = 2;
    public const int Partial = 3;

    /// <summary>Verbos que rodam sem abrir a janela.</summary>
    public static readonly IReadOnlyList<string> Verbs =
        ["merge", "compress", "ocr", "split", "pdfa", "rotate", "to-image", "flow", "watch", "list-flows", "help", "version",
            "register-menu", "register-openwith", "unregister-all", "clean-temp"];

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "output", "profile", "language", "dpi", "every", "ranges", "level", "degrees", "pages", "format", "log", "duration",
    };

    public const string Help = """
        PDF Master — linha de comando (sem janela)

          PDFMaster.exe --merge a.pdf b.pdf [mais arquivos] --output resultado.pdf
          PDFMaster.exe --compress documento.pdf [--profile sem-perda|leve|equilibrada|forte|maxima] [--output comprimido.pdf|pasta]
          PDFMaster.exe --ocr documento.pdf [--language por|por+eng] [--dpi 300] [--output pdf|pasta]
          PDFMaster.exe --split documento.pdf --output pasta [--every N | --ranges "1-3,4-6"]
          PDFMaster.exe --pdfa documento.pdf [--level 1b|2b|3b] [--output pdf|pasta]
          PDFMaster.exe --rotate documento.pdf --degrees 90|180|270 [--pages "1,3-5"] [--output pdf|pasta]
          PDFMaster.exe --to-image documento.pdf [--format png|jpg] [--dpi 150] [--output pasta]
          PDFMaster.exe --flow "Nome do fluxo" arquivos-ou-pastas [--output pasta]
          PDFMaster.exe --watch [--duration segundos]
          PDFMaster.exe --list-flows | --help | --version

        Manutenção (usada também pelo instalador; só afeta o seu usuário):
          PDFMaster.exe --register-menu       itens do PDF Master no menu de contexto dos PDFs
          PDFMaster.exe --register-openwith   PDF Master em "Abrir com"
          PDFMaster.exe --unregister-all      remove as duas integrações acima
          PDFMaster.exe --clean-temp          apaga os temporários do programa que não estão em uso

        Opções comuns:
          --output   arquivo (.pdf, com um só resultado) ou pasta. Sem --output, o resultado vai para a
                     pasta do primeiro arquivo.
          --overwrite  permite substituir um arquivo de saída que já existe (sem ela, nada é
                       substituído: um nome novo é gerado na pasta ou a operação é recusada).
          --log arquivo  grava também as mensagens neste arquivo.
        Pastas informadas como entrada são lidas (sem subpastas). Os originais nunca são alterados.
        Códigos de saída: 0 sucesso, 1 parâmetros inválidos, 2 falha, 3 sucesso parcial.
        """;

    /// <summary>Indica se os argumentos pedem execução sem janela.</summary>
    public static bool IsHeadless(IReadOnlyList<string> args) =>
        args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && Verbs.Contains(a[2..].ToLowerInvariant())) ||
        args.Any(a => a is "-h" or "/?");

    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        string? verb = null, output = null, log = null;
        var overwrite = false;
        var inputs = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "/?")
            {
                verb ??= "help";
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                inputs.Add(arg);
                continue;
            }

            var name = arg[2..].ToLowerInvariant();
            if (Verbs.Contains(name))
            {
                if (verb is not null && verb != name) throw new ArgumentException($"Use um comando por vez (--{verb} e --{name}).");
                verb = name;
                // --flow recebe o nome do fluxo logo em seguida.
                if (name == "flow")
                {
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Informe o nome do fluxo depois de --flow.");
                    options["flow"] = args[++i];
                }

                continue;
            }

            if (name == "overwrite")
            {
                overwrite = true;
                continue;
            }

            if (!ValueOptions.Contains(name)) throw new ArgumentException($"Opção desconhecida: --{name}. Use --help.");
            if (i + 1 >= args.Count) throw new ArgumentException($"Falta o valor de --{name}.");
            var value = args[++i];
            switch (name)
            {
                case "output": output = value; break;
                case "log": log = value; break;
                default: options[name] = value; break;
            }
        }

        if (verb is null) throw new ArgumentException("Nenhum comando informado. Use --help.");
        return new CliCommand(verb, inputs, output, overwrite, options, log);
    }

    /// <summary>Executa o comando; mensagens vão para <paramref name="console"/>.</summary>
    public static async Task<int> RunAsync(CliCommand command, AutomationServices services, TextWriter console, CancellationToken ct = default)
    {
        var log = command.LogFile is { Length: > 0 } path ? new StreamWriter(path, append: true) : null;
        void Say(string message)
        {
            console.WriteLine(message);
            log?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
        }

        try
        {
            switch (command.Verb)
            {
                case "help":
                    Say(Help);
                    return Success;
                case "version":
                    Say("PDF Master " + typeof(CommandLine).Assembly.GetName().Version?.ToString(3));
                    return Success;
                case "list-flows":
                    foreach (var flow in services.Flows.Load()) Say($"{flow.Name}: {flow.Describe()}");
                    return Success;
                case "watch":
                    return await WatchAsync(command, services, Say, ct).ConfigureAwait(false);
                case "register-menu" or "register-openwith" or "unregister-all":
                {
                    var shell = new ShellIntegration(Environment.ProcessPath ?? throw new IOException("Caminho do programa desconhecido."));
                    if (command.Verb == "register-menu") shell.InstallContextMenu();
                    else if (command.Verb == "register-openwith") shell.RegisterOpenWith();
                    else shell.RemoveAll();
                    Say(command.Verb switch
                    {
                        "register-menu" => "Itens adicionados ao menu de contexto dos PDFs.",
                        "register-openwith" => "PDF Master registrado em \"Abrir com\".",
                        _ => "Integrações com o Windows removidas.",
                    });
                    return Success;
                }

                case "clean-temp":
                {
                    var cleaned = PDFMaster.Infrastructure.TempFiles.Clean(TimeSpan.Zero);
                    Say($"Temporários removidos: {cleaned.Removed} item(ns), {FileSizeFormatter.Format(cleaned.Bytes)}." +
                        (cleaned.InUse > 0 ? $" {cleaned.InUse} em uso foram mantidos." : string.Empty));
                    return Success;
                }
            }

            var inputs = ExpandInputs(command.Inputs, command.Verb == "flow");
            if (inputs.Count == 0) throw new ArgumentException("Informe ao menos um arquivo de entrada.");
            if (command.Verb == "merge" && inputs.Count < 2) throw new ArgumentException("Informe ao menos dois PDFs para mesclar.");
            return await ExecuteAsync(command, inputs, services, Say, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            Say("Erro nos parâmetros: " + ex.Message);
            return InvalidArguments;
        }
        catch (OperationCanceledException)
        {
            Say("Cancelado.");
            return Failed;
        }
        catch (Exception ex) when (ex is PdfEngineException or IOException or UnauthorizedAccessException or PdfRenderException or PageRangeException)
        {
            Say("Falhou: " + ex.Message);
            return Failed;
        }
        finally
        {
            log?.Dispose();
        }
    }

    private static List<string> ExpandInputs(IReadOnlyList<string> inputs, bool anySupported)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            var full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                files.AddRange(Directory.EnumerateFiles(full)
                    .Where(f => anySupported ? FlowRunner.CanImport(f) : Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(full))
            {
                files.Add(full);
            }
            else
            {
                throw new ArgumentException($"Arquivo ou pasta não encontrado: {input}");
            }
        }

        return files;
    }

    private static async Task<int> ExecuteAsync(CliCommand command, List<string> inputs, AutomationServices s, Action<string> say, CancellationToken ct)
    {
        // Resultado em pasta de trabalho; depois cada arquivo vai para o destino pedido.
        var exactFile = command.Output is { } o && Path.GetExtension(o).Length > 1 && !Directory.Exists(o) ? Path.GetFullPath(o) : null;
        var folder = exactFile is not null ? Path.GetDirectoryName(exactFile)!
            : command.Output is { } dir ? Path.GetFullPath(dir)
            : Path.GetDirectoryName(inputs[0])!;
        var work = Path.Combine(Path.GetTempPath(), "PDFMaster", "cli-" + Guid.NewGuid().ToString("N"));
        var workTarget = new OutputTarget(work, OverwritePolicy.AutoRename, SubfolderMode.None);
        var results = new List<string>();
        int failures = 0, succeeded = 0;
        try
        {
            switch (command.Verb)
            {
                case "merge":
                    results.Add((await s.Organizer.MergeAsync(inputs.Select(i => new MergeInput(i)).ToList(),
                        exactFile is not null ? Path.GetFileName(exactFile) : "mesclado.pdf", workTarget, bookmarkPerFile: true, ct: ct).ConfigureAwait(false)).Path);
                    break;
                case "split":
                {
                    var info = await s.Engine.ReadInfoAsync(inputs[0], ct).ConfigureAwait(false);
                    var groups = command.Option("ranges") is { } ranges ? SplitPlanner.ByRanges(ranges, info.PageCount)
                        : SplitPlanner.EveryN(info.PageCount, Number(command.Option("every"), 1, 1, 100_000, "--every"));
                    results.AddRange((await s.Organizer.SplitAsync(inputs[0], groups, null, workTarget, ct: ct).ConfigureAwait(false)).Select(f => f.Path));
                    break;
                }

                default:
                    foreach (var input in inputs)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var produced = await RunOneAsync(command, input, workTarget, s, say, ct).ConfigureAwait(false);
                            results.AddRange(produced);
                            succeeded++;
                        }
                        catch (Exception ex) when (ex is PdfEngineException or IOException or PdfRenderException or PageRangeException)
                        {
                            failures++;
                            say($"{Path.GetFileName(input)}: falhou — {ex.Message}");
                        }
                    }

                    break;
            }

            if (exactFile is not null && results.Count > 1)
                throw new ArgumentException($"A operação gerou {results.Count} arquivos: informe uma pasta em --output.");

            Directory.CreateDirectory(folder);
            foreach (var result in results)
            {
                var destination = exactFile ?? Path.Combine(folder, Path.GetFileName(result));
                if (File.Exists(destination) && !command.Overwrite)
                {
                    if (exactFile is not null)
                        throw new PdfEngineException($"\"{destination}\" já existe. Use --overwrite para substituí-lo.");
                    destination = FreeName(destination);
                }

                if (inputs.Any(i => string.Equals(i, destination, StringComparison.OrdinalIgnoreCase)))
                    throw new PdfEngineException("O resultado não pode substituir um arquivo de entrada.");
                File.Move(result, destination, command.Overwrite);
                say($"Gerado: {destination} ({FileSizeFormatter.Format(new FileInfo(destination).Length)})");
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
                // Temporário.
            }
        }

        if (failures == 0) return Success;
        return succeeded == 0 ? Failed : Partial;
    }

    private static async Task<IReadOnlyList<string>> RunOneAsync(CliCommand command, string input, OutputTarget target, AutomationServices s,
        Action<string> say, CancellationToken ct)
    {
        switch (command.Verb)
        {
            case "compress":
            {
                var profile = Profile(command.Option("profile"));
                var result = await s.Compressor.CompressAsync(input, CompressionSettings.For(profile), target, null, ct).ConfigureAwait(false);
                if (result.Output is null)
                {
                    say($"{Path.GetFileName(input)}: já estava compacto; nenhum arquivo gerado.");
                    return [];
                }

                say($"{Path.GetFileName(input)}: {FileSizeFormatter.Format(result.OriginalBytes)} → {FileSizeFormatter.Format(result.FinalBytes)} ({result.SavedPercent:0}% menor).");
                return [result.Output.Path];
            }

            case "ocr":
            {
                var languages = command.Option("language") ?? "por";
                var options = new OcrOptions(languages, Number(command.Option("dpi"), 300, OcrService.MinDpi, OcrService.MaxDpi, "--dpi"));
                var result = await s.Ocr.RecognizeAsync(input, options, target, null, ct).ConfigureAwait(false);
                say($"{Path.GetFileName(input)}: {result.PagesRecognized} página(s) reconhecida(s), {result.Words} palavra(s).");
                return result.Files.Select(f => f.Path).ToList();
            }

            case "pdfa":
            {
                var level = command.Option("level")?.ToLowerInvariant() switch
                {
                    null or "2b" => PdfALevel.A2b,
                    "1b" => PdfALevel.A1b,
                    "3b" => PdfALevel.A3b,
                    var other => throw new ArgumentException($"Nível PDF/A desconhecido: {other} (use 1b, 2b ou 3b)."),
                };
                try
                {
                    var result = await s.PdfA.ConvertAsync(input, level, target, true, ct).ConfigureAwait(false);
                    say($"{Path.GetFileName(input)}: {result.Validation.Verdict}");
                    return [result.File.Path];
                }
                catch (PdfAConversionException ex)
                {
                    throw new PdfEngineException(ex.Message + " " + string.Join(" ", ex.Problems.Take(4)), ex);
                }
            }

            case "rotate":
            {
                var degrees = Number(command.Option("degrees"), 90, 90, 270, "--degrees");
                if (degrees is not (90 or 180 or 270)) throw new ArgumentException("--degrees aceita 90, 180 ou 270.");
                var info = await s.Engine.ReadInfoAsync(input, ct).ConfigureAwait(false);
                var selected = PageRangeParser.Parse(command.Option("pages"), info.PageCount).ToHashSet();
                var pages = Enumerable.Range(0, info.PageCount).Select(i => PageInstruction.From(input, i, selected.Contains(i) ? degrees : 0)).ToList();
                return [(await s.Organizer.SaveArrangementAsync(pages, Path.GetFileNameWithoutExtension(input) + "_girado.pdf", target, "Girar páginas", ct: ct)
                    .ConfigureAwait(false)).Path];
            }

            case "to-image":
            {
                var format = command.Option("format")?.ToLowerInvariant() switch
                {
                    null or "png" => ImageFormat.Png,
                    "jpg" or "jpeg" => ImageFormat.Jpeg,
                    var other => throw new ArgumentException($"Formato desconhecido: {other} (use png ou jpg)."),
                };
                var options = new PdfToImageOptions(format, Number(command.Option("dpi"), 150, 36, 600, "--dpi"));
                return (await s.ToImage.ConvertAsync(input, options, target, null, ct).ConfigureAwait(false)).Select(f => f.Path).ToList();
            }

            case "flow":
            {
                var name = command.Option("flow")!;
                var flow = s.Flows.Find(name) ?? throw new ArgumentException($"Fluxo não encontrado: \"{name}\". Use --list-flows.");
                var result = await s.Runner.RunAsync(input, flow, target, null, null, ct).ConfigureAwait(false);
                say($"{Path.GetFileName(input)}: {flow.Describe()}" + (result.Notes.Count > 0 ? " — " + string.Join(" ", result.Notes) : string.Empty));
                return [result.File.Path];
            }

            default:
                throw new ArgumentException($"Comando desconhecido: --{command.Verb}.");
        }
    }

    private static async Task<int> WatchAsync(CliCommand command, AutomationServices s, Action<string> say, CancellationToken ct)
    {
        var folders = s.WatchStore.Load().Where(f => f.Enabled).ToList();
        if (folders.Count == 0)
        {
            say("Nenhuma pasta monitorada ativa. Configure em \"Monitorar pasta\" na janela do programa.");
            return InvalidArguments;
        }

        var duration = command.Option("duration") is { } text ? Number(text, 0, 1, 86_400 * 30, "--duration") : 0;
        using var watch = new WatchService(s.WatchStore, s.Flows, s.Queue);
        watch.Activity += say;
        s.Queue.JobFinished += job =>
        {
            if (job.State == JobState.Failed) say($"Falhou: {job.FileName} — {job.Error}");
        };
        watch.Start();
        foreach (var folder in folders)
        {
            var status = watch.StatusOf(folder.Id);
            say(status.Problem is { } problem ? $"{folder.Name}: {problem}" : $"Monitorando \"{folder.InputFolder}\" → \"{folder.OutputFolder}\" ({folder.FlowName}).");
        }

        say(duration > 0 ? $"Monitorando por {duration} s." : "Monitorando. Feche esta janela ou pressione Ctrl+C para parar.");
        try
        {
            await Task.Delay(duration > 0 ? TimeSpan.FromSeconds(duration) : Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        watch.Stop();
        await s.Queue.WhenIdleAsync(CancellationToken.None).ConfigureAwait(false);
        say("Monitoramento encerrado.");
        return Success;
    }

    private static CompressionProfile Profile(string? text) => text?.ToLowerInvariant() switch
    {
        null or "equilibrada" or "balanced" => CompressionProfile.Balanced,
        "sem-perda" or "lossless" => CompressionProfile.Lossless,
        "leve" or "light" => CompressionProfile.Light,
        "forte" or "strong" => CompressionProfile.Strong,
        "maxima" or "máxima" or "maximum" => CompressionProfile.Maximum,
        var other => throw new ArgumentException($"Perfil de compressão desconhecido: {other}."),
    };

    private static int Number(string? text, int fallback, int min, int max, string name)
    {
        if (text is null) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ArgumentException($"{name} deve ser um número entre {min} e {max}.");
        return value;
    }

    private static string FreeName(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
