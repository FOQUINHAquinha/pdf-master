using System.Text;
using Microsoft.Win32;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.Batch;
using PDFMaster.Compression;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PDFMaster.Security;
using PdfMetadata = PDFMaster.Security.PdfMetadata;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Fase 7: fluxos, fila, ferramentas de página, pastas monitoradas, menu de contexto e linha de comando.</summary>
public class BatchTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly AutomationServices _services;

    public BatchTests()
    {
        _services = new AutomationServices(Path.Combine(_temp.Path, "dados"));
    }

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target(string folder = "saida") => new(Path.Combine(_temp.Path, folder), OverwritePolicy.AutoRename, SubfolderMode.None);

    private string Numbered(string name, int pages) =>
        SecuritySamples.Pages(Path.Combine(_temp.Path, name), Enumerable.Range(1, pages).Select(i => new[] { $"Pagina numero {i}" }).ToArray());

    private static string[] PageTexts(string path) =>
        PdfiumRenderer.ReadDisplayText(path).Select(p => new string(p.Chars.Select(c => c.Value).ToArray())).ToArray();

    [Fact]
    public void Flows_survive_a_json_round_trip_with_every_step_type()
    {
        var store = new FlowStore(Path.Combine(_temp.Path, "dados"));
        var flow = new BatchFlow("Tudo", FlowStep.Catalog.ToList(), "_tudo");
        store.Save([flow, BatchFlow.Examples[0]]);

        var loaded = new FlowStore(Path.Combine(_temp.Path, "dados")).Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Tudo", loaded[0].Name);
        Assert.Equal("_tudo", loaded[0].Suffix);
        Assert.Equal(flow.Steps, loaded[0].Steps);
        Assert.Contains("\"etapa\"", File.ReadAllText(store.FilePath));
        Assert.Equal(BatchFlow.Examples[0].Steps, store.Find(BatchFlow.Examples[0].Name)!.Steps);
    }

    [Fact]
    public void Flow_store_starts_with_examples_and_rejects_duplicates_and_invalid_steps()
    {
        var store = new FlowStore(Path.Combine(_temp.Path, "vazio"));
        Assert.Equal(BatchFlow.Examples.Select(f => f.Name), store.Load().Select(f => f.Name));
        Assert.ThrowsAny<Exception>(() => store.Save([new BatchFlow("A", [new ReverseStep()]), new BatchFlow("a", [new ReverseStep()])]));
        Assert.NotNull(new BatchFlow("Ruim", [new RotateStep(45)]).Validate());
        Assert.NotNull(new BatchFlow("Vazio", []).Validate());
        Assert.Null(new BatchFlow("Bom", [new RotateStep(180), new NUpStep(4)]).Validate());
    }

    [Fact]
    public async Task Reverse_puts_pages_in_inverse_order()
    {
        var source = Numbered("tres.pdf", 3);
        var result = await _services.Pages.ReverseAsync(source, Target());
        var texts = PageTexts(result.File!.Path);
        Assert.EndsWith("_invertido.pdf", result.File.Path);
        Assert.Equal(["3", "2", "1"], texts.Select(t => t.Trim()[^1].ToString()).ToArray());
    }

    [Fact]
    public async Task Removes_blank_pages_and_optionally_repeated_pages()
    {
        var source = SecuritySamples.Pages(Path.Combine(_temp.Path, "brancas.pdf"),
            ["Primeira pagina"], [], ["Segunda pagina"], ["Primeira pagina"], []);

        var onlyBlank = await _services.Pages.RemoveBlankPagesAsync(source, alsoDuplicates: false, Target("a"));
        Assert.Equal([2, 5], onlyBlank.RemovedPages);
        Assert.Equal(3, PageTexts(onlyBlank.File!.Path).Length);

        var both = await _services.Pages.RemoveBlankPagesAsync(source, alsoDuplicates: true, Target("b"));
        Assert.Equal([2, 4, 5], both.RemovedPages);
        Assert.Equal(["Primeira pagina", "Segunda pagina"], PageTexts(both.File!.Path).Select(t => t.Trim()).ToArray());

        var clean = SecuritySamples.Pages(Path.Combine(_temp.Path, "limpo.pdf"), ["A"], ["B"]);
        var nothing = await _services.Pages.RemoveBlankPagesAsync(clean, alsoDuplicates: true, Target("c"));
        Assert.Null(nothing.File);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "c")) && Directory.EnumerateFiles(Path.Combine(_temp.Path, "c")).Any());
    }

    [Fact]
    public async Task Crop_sets_the_crop_box_and_refuses_margins_larger_than_the_page()
    {
        var source = Numbered("recorte.pdf", 2);
        var result = await _services.Pages.CropAsync(source, 10, 20, 15, 5, Target());
        var shown = PdfiumRenderer.ReadDisplayText(result.File!.Path);
        const double mm = 72 / 25.4;
        Assert.Equal(2, shown.Count);
        Assert.All(shown, p =>
        {
            Assert.Equal(595 - 20 * mm, p.Width, 0);
            Assert.Equal(842 - 30 * mm, p.Height, 0);
        });
        // Nada é apagado: o texto continua no arquivo.
        Assert.Contains("Pagina numero 1", PageTexts(result.File.Path)[0]);
        await Assert.ThrowsAsync<PdfEngineException>(() => _services.Pages.CropAsync(source, 400, 400, 0, 0, Target("x")));
    }

    [Fact]
    public async Task Booklet_imposes_pages_in_folding_order()
    {
        var source = Numbered("livreto.pdf", 4);
        var result = await _services.Pages.NUpAsync(source, 2, booklet: true, SheetSize.A4, Target());
        var info = await _services.Engine.ReadInfoAsync(result.File!.Path);
        Assert.Equal(2, info.PageCount);
        Assert.All(info.Pages, p => Assert.True(p.WidthPt > p.HeightPt));
        var sides = PdfiumRenderer.ReadDisplayText(result.File.Path);

        // Lado 1: página 4 à esquerda e 1 à direita; lado 2: página 2 à esquerda e 3 à direita.
        static int Left(DisplayPageText side, char digit) =>
            (int)side.Chars.Where(c => c.Value == digit).Select(c => c.X).DefaultIfEmpty(-1).Min();
        Assert.True(Left(sides[0], '4') >= 0 && Left(sides[0], '1') > Left(sides[0], '4'));
        Assert.True(Left(sides[1], '2') >= 0 && Left(sides[1], '3') > Left(sides[1], '2'));
        Assert.Equal(-1, Left(sides[0], '2'));
    }

    [Fact]
    public async Task Two_and_four_up_keep_rotated_pages_upright()
    {
        // Página 300 × 400 com /Rotate 90: o quadrado vermelho aparece no canto superior direito.
        var source = PdfSamples.Create(_temp, "girada.pdf", 5, 300, rotation: _ => 90);
        var (rx, ry, _, _) = await RedCentroid(source, 0, 0, 1);
        Assert.InRange(rx, 0.7, 0.8);
        Assert.InRange(ry, 0.28, 0.39);

        var two = await _services.Pages.NUpAsync(source, 2, booklet: false, SheetSize.A4, Target("dois"));
        Assert.Equal(3, (await _services.Engine.ReadInfoAsync(two.File!.Path)).PageCount);

        // Folha A4 deitada (842 × 595): célula esquerda de 403 × 559 pt; página exibida 400 × 300 centralizada nela.
        const double margin = 18, cellW = (842 - 36) / 2.0, cellH = 595 - 36;
        var scale = Math.Min((cellW - 8) / 400, (cellH - 8) / 300);
        double boxLeft = margin + cellW / 2 - 200 * scale, boxTop = margin + cellH / 2 - 150 * scale;
        var (sx, sy, w, h) = await RedCentroid(two.File.Path, 0, 0, 0.5);
        var message = $"centro na folha: {sx * 842:0} × {sy * 595:0} pt (imagem {w} × {h})";
        Assert.True(Math.Abs(sx * 842 - (boxLeft + rx * 400 * scale)) < 6, message);
        Assert.True(Math.Abs(sy * 595 - (boxTop + ry * 300 * scale)) < 6, message);

        var four = await _services.Pages.NUpAsync(source, 4, booklet: false, SheetSize.A4, Target("quatro"));
        Assert.Equal(2, (await _services.Engine.ReadInfoAsync(four.File!.Path)).PageCount);
    }

    /// <summary>Centro (relativo, 0–1) dos pixels vermelhos da página, considerando só a faixa horizontal [from, to).</summary>
    private async Task<(double X, double Y, int Width, int Height)> RedCentroid(string path, int page, double from, double to)
    {
        double sx = 0, sy = 0;
        long n = 0;
        int w = 0, h = 0;
        await _services.Renderer.RenderPagesAsync(path, [page], RenderSize.Fit(600, 850), (_, p) =>
        {
            w = p.Width;
            h = p.Height;
            for (var y = 0; y < p.Height; y++)
            for (var x = (int)(from * p.Width); x < (int)(to * p.Width); x++)
            {
                var i = 4 * (y * p.Width + x);
                if (p.Bgra[i + 2] > 180 && p.Bgra[i + 1] < 90 && p.Bgra[i] < 90)
                {
                    sx += x + 0.5;
                    sy += y + 0.5;
                    n++;
                }
            }
        });
        Assert.True(n > 0, "Nenhum pixel vermelho encontrado.");
        return (sx / n / w, sy / n / h, w, h);
    }

    [Fact]
    public async Task Flow_runs_all_steps_in_order_and_never_touches_the_original()
    {
        var source = Numbered("contrato.pdf", 4);
        var before = File.ReadAllBytes(source);
        var flow = new BatchFlow("Teste", [new DeletePagesStep("1"), new ReverseStep(), new RotateStep(90, "1"), new PageNumbersStep("Folha {pagina}"),
            new RemoveMetadataStep()], "_fluxo");
        var steps = new List<string>();
        var result = await _services.Runner.RunAsync(source, flow, Target(), new Progress<FlowProgress>(p => steps.Add(p.StepName)));

        Assert.Equal(Path.Combine(_temp.Path, "saida", "contrato_fluxo.pdf"), result.File.Path);
        Assert.Equal(before, File.ReadAllBytes(source));
        var texts = PageTexts(result.File.Path);
        Assert.Equal(3, texts.Length);
        Assert.Contains("Pagina numero 4", texts[0]);
        Assert.Contains("Pagina numero 2", texts[2]);
        Assert.Contains("Folha 1", texts[0]);
        Assert.Contains("Folha 3", texts[2]);
        var info = await _services.Engine.ReadInfoAsync(result.File.Path);
        Assert.Equal([90, 0, 0], info.Pages.Select(p => p.Rotation).ToArray());
        Assert.True(string.IsNullOrEmpty(PdfMetadata.Read(result.File.Path).Title));

        // Segunda execução: nome novo, nunca sobrescreve.
        var again = await _services.Runner.RunAsync(source, flow, Target());
        Assert.NotEqual(result.File.Path, again.File.Path);
        Assert.True(File.Exists(result.File.Path));
    }

    [Fact]
    public async Task Flow_imports_text_files_and_compresses()
    {
        var text = Path.Combine(_temp.Path, "nota.txt");
        File.WriteAllText(text, string.Join("\n", Enumerable.Range(1, 120).Select(i => $"Linha {i} do relatório")));
        var flow = new BatchFlow("Texto", [new CompressStep(CompressionProfile.Lossless), new PageNumbersStep()]);
        var result = await _services.Runner.RunAsync(text, flow, Target());
        Assert.EndsWith("nota_processado.pdf", result.File.Path);
        Assert.True(result.File.PageCount >= 2);
        Assert.Contains("Linha 1 do relat", PageTexts(result.File.Path)[0]);
    }

    private BatchFlow SaveFlow(string name, params FlowStep[] steps)
    {
        var flow = new BatchFlow(name, steps, "_" + name.ToLowerInvariant());
        _services.Flows.Save([.. _services.Flows.Load().Where(f => f.Name != name), flow]);
        return flow;
    }

    [Fact]
    public async Task Queue_processes_reports_failures_and_retries_only_failed_jobs()
    {
        var flow = new BatchFlow("Inverter", [new ReverseStep()], "_inv");
        var good1 = Numbered("um.pdf", 2);
        var good2 = Numbered("dois.pdf", 3);
        var bad = Path.Combine(_temp.Path, "quebrado.pdf");
        File.WriteAllText(bad, "isto não é um PDF");
        var queue = _services.Queue;
        queue.MaxParallel = 2;
        var finished = new List<string>();
        queue.JobFinished += j => { lock (finished) finished.Add(j.FileName); };

        var jobs = new[] { good1, bad, good2 }.Select(f => queue.Enqueue(f, flow, Target())).ToList();
        Assert.All(jobs, j => Assert.Equal(JobState.Waiting, j.State));
        Assert.Equal("Inverter", jobs[0].Operation);
        Assert.Equal(new FileInfo(good1).Length, jobs[0].OriginalBytes);
        queue.Start();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(JobState.Completed, jobs[0].State);
        Assert.Equal(JobState.Failed, jobs[1].State);
        Assert.False(string.IsNullOrWhiteSpace(jobs[1].Error));
        Assert.Equal(JobState.Completed, jobs[2].State);
        Assert.True(File.Exists(jobs[2].OutputPath));
        Assert.True(jobs[2].FinalBytes > 0);
        Assert.Equal(3, finished.Count);

        // Corrige o arquivo e reprocessa só o que falhou.
        File.Copy(good1, bad, overwrite: true);
        Assert.Equal(1, queue.RetryFailed());
        queue.Start();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(JobState.Completed, jobs[1].State);
        Assert.Equal(4, finished.Count);
        Assert.Equal(3, queue.ClearFinished());
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Queue_pause_holds_jobs_and_cancel_stops_them()
    {
        var flow = new BatchFlow("Tres", [new ReverseStep(), new ReverseStep(), new ReverseStep()]);
        var queue = _services.Queue;
        var job = queue.Enqueue(Numbered("pausa.pdf", 2), flow, Target());
        var waiting = queue.Enqueue(Numbered("espera.pdf", 2), flow, Target());
        queue.MaxParallel = 1;
        queue.Start();
        queue.Pause();
        await Task.Delay(800);
        Assert.True(queue.IsPaused);
        Assert.Contains(job.State, new[] { JobState.Waiting, JobState.Paused });
        Assert.Null(job.OutputPath);

        queue.Cancel(waiting);
        queue.Resume();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(JobState.Cancelled, waiting.State);
        Assert.Null(waiting.OutputPath);

        Assert.True(queue.Retry(waiting));
        Assert.Equal(JobState.Waiting, waiting.State);
        queue.Remove(waiting);
        Assert.DoesNotContain(waiting, queue.Jobs);
    }

    [Fact]
    public async Task Watch_folder_processes_new_files_only_and_once()
    {
        SaveFlow("Inverter", new ReverseStep());
        var input = Directory.CreateDirectory(Path.Combine(_temp.Path, "entrada")).FullName;
        var output = Path.Combine(_temp.Path, "processados");
        var old = Numbered(Path.Combine("entrada", "antigo.pdf"), 2);
        var folder = new WatchFolder(Guid.NewGuid(), "Teste", input, "Inverter", output);
        Assert.Null(folder.Validate(_services.Flows));
        Assert.NotNull((folder with { OutputFolder = input }).Validate(_services.Flows));
        Assert.NotNull((folder with { FlowName = "Não existe" }).Validate(_services.Flows));

        using var watch = new WatchService(_services.WatchStore, _services.Flows, _services.Queue, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2));
        var log = new List<string>();
        watch.Activity += m => { lock (log) log.Add(m); };
        watch.Save([folder]);
        watch.Start();
        Assert.True(watch.IsRunning);

        Numbered(Path.Combine("entrada", "novo.pdf"), 3);
        var expected = Path.Combine(output, "novo_inverter.pdf");
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!File.Exists(expected) && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.True(File.Exists(expected), "O arquivo novo não foi processado. " + string.Join(" | ", log));
        await _services.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(3000);

        Assert.False(File.Exists(Path.Combine(output, "antigo_inverter.pdf")));
        Assert.Single(Directory.GetFiles(output));
        Assert.Equal(1, watch.StatusOf(folder.Id).Processed);

        // Pausado, nada novo entra na fila.
        watch.Pause();
        Numbered(Path.Combine("entrada", "durante-pausa.pdf"), 1);
        await Task.Delay(2500);
        Assert.Empty(_services.Queue.Jobs.Where(j => j.FileName == "durante-pausa.pdf"));
        watch.Resume();
        var later = Path.Combine(output, "durante-pausa_inverter.pdf");
        deadline = DateTime.UtcNow.AddSeconds(40);
        while (!File.Exists(later) && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.True(File.Exists(later));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void Context_menu_and_open_with_are_written_and_removed_under_the_user_key()
    {
        var root = @"Software\PDFMasterTests\" + Guid.NewGuid().ToString("N");
        var exe = Path.Combine(_temp.Path, "PDFMaster.exe");
        var shell = new ShellIntegration(exe, root);
        try
        {
            Assert.False(shell.IsContextMenuInstalled);
            shell.InstallContextMenu();
            Assert.True(shell.IsContextMenuInstalled);
            using (var key = Registry.CurrentUser.OpenSubKey(root + @"\Classes\SystemFileAssociations\.pdf\shell\PDFMaster.merge\command"))
            {
                Assert.NotNull(key);
                Assert.Equal($"\"{exe}\" --context merge \"%1\"", key!.GetValue(null));
            }

            Assert.Equal(ShellIntegration.Actions.Count,
                Registry.CurrentUser.OpenSubKey(root + @"\Classes\SystemFileAssociations\.pdf\shell")!.GetSubKeyNames().Count(n => n.StartsWith("PDFMaster.", StringComparison.Ordinal)));

            shell.RegisterOpenWith();
            Assert.True(shell.IsOpenWithRegistered);
            using (var command = Registry.CurrentUser.OpenSubKey(root + @"\Classes\PDFMaster.Document\shell\open\command"))
                Assert.Equal($"\"{exe}\" --tool view \"%1\"", command!.GetValue(null));

            shell.RemoveAll();
            Assert.False(shell.IsContextMenuInstalled);
            Assert.False(shell.IsOpenWithRegistered);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Context_invocations_are_gathered_into_one_list()
    {
        var channel = Guid.NewGuid().ToString("N");
        var first = ContextCollector.CollectAsync("merge", ["a.pdf"], TimeSpan.FromMilliseconds(1500), channel);
        await Task.Delay(200);
        var second = await ContextCollector.CollectAsync("merge", ["b.pdf"], TimeSpan.FromMilliseconds(1500), channel);
        var third = await ContextCollector.CollectAsync("merge", ["c.pdf"], TimeSpan.FromMilliseconds(1500), channel);
        Assert.Null(second);
        Assert.Null(third);
        Assert.Equal(new[] { "a.pdf", "b.pdf", "c.pdf" }, (await first)!);
    }

    // ---------- Linha de comando ----------

    private async Task<(int Code, string Output)> Cli(params string[] args)
    {
        var console = new StringWriter();
        int code;
        try
        {
            code = await CommandLine.RunAsync(CommandLine.Parse(args), _services, console);
        }
        catch (ArgumentException ex)
        {
            return (CommandLine.InvalidArguments, ex.Message);
        }

        return (code, console.ToString());
    }

    [Fact]
    public void Parses_commands_and_rejects_bad_ones()
    {
        var command = CommandLine.Parse(["--compress", "a.pdf", "--profile", "forte", "--output", "b.pdf", "--overwrite"]);
        Assert.Equal("compress", command.Verb);
        Assert.Equal(["a.pdf"], command.Inputs);
        Assert.Equal("b.pdf", command.Output);
        Assert.True(command.Overwrite);
        Assert.Equal("forte", command.Option("profile"));

        Assert.True(CommandLine.IsHeadless(["--merge", "a", "b"]));
        Assert.False(CommandLine.IsHeadless(["--tool", "merge", "a.pdf"]));
        Assert.False(CommandLine.IsHeadless(["a.pdf"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--merge", "--compress", "a.pdf"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--compress", "a.pdf", "--quality", "3"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--flow"]));
        Assert.Equal("Meu fluxo", CommandLine.Parse(["--flow", "Meu fluxo", "x.pdf"]).Option("flow"));
    }

    [Fact]
    public async Task Cli_merges_splits_rotates_and_never_replaces_without_permission()
    {
        var a = Numbered("a.pdf", 2);
        var b = Numbered("b.pdf", 3);
        var merged = Path.Combine(_temp.Path, "cli", "r.pdf");

        var (code, output) = await Cli("--merge", a, b, "--output", merged);
        Assert.Equal(CommandLine.Success, code);
        Assert.Contains("Gerado:", output);
        Assert.Equal(5, (await _services.Engine.ReadInfoAsync(merged)).PageCount);

        var stamp = File.GetLastWriteTimeUtc(merged);
        (code, output) = await Cli("--merge", a, b, "--output", merged);
        Assert.Equal(CommandLine.Failed, code);
        Assert.Contains("--overwrite", output);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(merged));
        (code, _) = await Cli("--merge", b, a, "--output", merged, "--overwrite");
        Assert.Equal(CommandLine.Success, code);
        Assert.Contains("Pagina numero 1", PageTexts(merged)[0]);

        var parts = Path.Combine(_temp.Path, "cli", "partes");
        (code, _) = await Cli("--split", merged, "--output", parts, "--every", "2");
        Assert.Equal(CommandLine.Success, code);
        Assert.Equal(3, Directory.GetFiles(parts, "*.pdf").Length);
        (code, _) = await Cli("--split", merged, "--output", Path.Combine(parts, "um.pdf"));
        Assert.Equal(CommandLine.InvalidArguments, code);

        (code, _) = await Cli("--rotate", a, "--degrees", "180", "--pages", "2", "--output", Path.Combine(_temp.Path, "cli", "g.pdf"));
        Assert.Equal(CommandLine.Success, code);
        Assert.Equal(new[] { 0, 180 }, (await _services.Engine.ReadInfoAsync(Path.Combine(_temp.Path, "cli", "g.pdf"))).Pages.Select(p => p.Rotation).ToArray());
    }

    [Fact]
    public async Task Cli_compresses_runs_flows_and_reports_partial_failures()
    {
        var heavy = PdfSamples.Create(_temp, Path.Combine("lote", "pesado.pdf"), 3, 300, heavy: true);
        File.WriteAllText(Path.Combine(_temp.Path, "lote", "ruim.pdf"), "não é PDF");
        var outFolder = Path.Combine(_temp.Path, "cli-lote");

        var (code, output) = await Cli("--compress", Path.Combine(_temp.Path, "lote"), "--profile", "sem-perda", "--output", outFolder);
        Assert.Equal(CommandLine.Partial, code);
        Assert.Contains("ruim.pdf: falhou", output);

        SaveFlow("Inverter", new ReverseStep());
        (code, output) = await Cli("--flow", "Inverter", heavy, "--output", outFolder);
        Assert.Equal(CommandLine.Success, code);
        Assert.True(File.Exists(Path.Combine(outFolder, "pesado_inverter.pdf")));

        (code, output) = await Cli("--flow", "Não existe", heavy);
        Assert.Equal(CommandLine.InvalidArguments, code);
        (code, output) = await Cli("--list-flows");
        Assert.Contains("Inverter", output);
        (code, output) = await Cli("--compress", Path.Combine(_temp.Path, "nada.pdf"));
        Assert.Equal(CommandLine.InvalidArguments, code);
        (code, output) = await Cli("--help");
        Assert.Contains("--merge", output);
        Assert.Contains("--overwrite", output);
    }
}
