using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PDFMaster.Core.Files;
using PDFMaster.Core.Settings;
using PDFMaster.Domain;
using PDFMaster.Infrastructure.Files;
using PDFMaster.Storage;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Pasta temporária exclusiva de cada teste, apagada ao final.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string content) => File(name, Encoding.ASCII.GetBytes(content));

    public string File(string name, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public class StorageTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SqliteDatabase _db;

    public StorageTests()
    {
        _db = new SqliteDatabase(Path.Combine(_temp.Path, "data", "test.db"));
        _db.Initialize();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Initialize_is_idempotent_and_creates_the_file()
    {
        _db.Initialize();
        _db.Initialize();
        Assert.True(File.Exists(_db.DatabasePath));
    }

    [Fact]
    public async Task Settings_are_persisted_between_service_instances()
    {
        var first = new SettingsService(new SqliteSettingsStore(_db));
        await first.LoadAsync();
        Assert.Equal(AppTheme.Light, first.Current.Theme);

        var raised = 0;
        first.Changed += (_, _) => raised++;
        await first.UpdateAsync(s =>
        {
            s.Theme = AppTheme.Dark;
            s.AccentColor = "#5B5FC7";
            s.HistoryEnabled = false;
        });
        Assert.Equal(1, raised);

        var second = new SettingsService(new SqliteSettingsStore(_db));
        await second.LoadAsync();
        Assert.Equal(AppTheme.Dark, second.Current.Theme);
        Assert.Equal("#5B5FC7", second.Current.AccentColor);
        Assert.False(second.Current.HistoryEnabled);

        await second.UpdateAsync(s => s.AccentColor = null);
        var third = new SettingsService(new SqliteSettingsStore(_db));
        await third.LoadAsync();
        Assert.Null(third.Current.AccentColor);
    }

    [Fact]
    public async Task History_add_query_filter_delete_and_clear()
    {
        var repo = new SqliteHistoryRepository(_db);
        var day1 = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 1, 11, 12, 0, 0, DateTimeKind.Utc);

        var id1 = await repo.AddAsync(new HistoryRecord(0, "contrato.pdf", "Copiar arquivos", day1, "Copiado.",
            @"C:\saida\contrato.pdf", TimeSpan.FromMilliseconds(1500), OperationStatus.Completed));
        var id2 = await repo.AddAsync(new HistoryRecord(0, "100%_final.pdf", "Mesclar PDF", day2, "Falha de leitura.",
            null, TimeSpan.FromSeconds(2), OperationStatus.Failed));
        Assert.NotEqual(id1, id2);

        var all = await repo.QueryAsync(new HistoryQuery());
        Assert.Equal(2, all.Count);
        Assert.Equal("100%_final.pdf", all[0].FileName); // mais recente primeiro
        Assert.Null(all[0].OutputPath);
        Assert.Equal(OperationStatus.Failed, all[0].Status);
        Assert.Equal(day1, all[1].StartedUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), all[1].Duration);

        Assert.Single(await repo.QueryAsync(new HistoryQuery(Text: "contrato")));
        Assert.Single(await repo.QueryAsync(new HistoryQuery(Text: "100%_")));   // % e _ são literais
        Assert.Empty(await repo.QueryAsync(new HistoryQuery(Text: "1_0")));
        Assert.Single(await repo.QueryAsync(new HistoryQuery(Operation: "Mesclar PDF")));
        Assert.Single(await repo.QueryAsync(new HistoryQuery(FromUtc: day2.AddHours(-1))));
        Assert.Single(await repo.QueryAsync(new HistoryQuery(ToUtc: day2.AddHours(-1))));
        Assert.Empty(await repo.QueryAsync(new HistoryQuery(FromUtc: day2.AddDays(1))));
        Assert.Single(await repo.QueryAsync(new HistoryQuery(Limit: 1)));

        Assert.Equal(new[] { "Copiar arquivos", "Mesclar PDF" }, (await repo.GetOperationNamesAsync()).ToArray());

        await repo.DeleteAsync(id1);
        Assert.Single(await repo.QueryAsync(new HistoryQuery()));

        await repo.ClearAsync();
        Assert.Empty(await repo.QueryAsync(new HistoryQuery()));
    }

    [Fact]
    public async Task History_text_filter_is_not_sql_injectable()
    {
        var repo = new SqliteHistoryRepository(_db);
        await repo.AddAsync(new HistoryRecord(0, "a.pdf", "Copiar arquivos", DateTime.UtcNow, "ok", null,
            TimeSpan.Zero, OperationStatus.Completed));

        Assert.Empty(await repo.QueryAsync(new HistoryQuery(Text: "'; DROP TABLE history; --")));
        Assert.Single(await repo.QueryAsync(new HistoryQuery()));
    }

    [Fact]
    public async Task Recent_files_are_ordered_deduplicated_and_removable()
    {
        var repo = new SqliteRecentFilesRepository(_db);
        var t = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

        await repo.TouchAsync(@"C:\docs\a.pdf", t);
        await repo.TouchAsync(@"C:\docs\b.pdf", t.AddMinutes(1));
        await repo.TouchAsync(@"C:\DOCS\A.PDF", t.AddMinutes(2)); // mesmo arquivo, outra caixa

        var list = await repo.GetAsync(10);
        Assert.Equal(2, list.Count);
        Assert.Equal(t.AddMinutes(2), list[0].LastOpenedUtc);
        Assert.EndsWith("b.pdf", list[1].Path);

        await repo.RemoveAsync(@"C:\docs\b.pdf");
        Assert.Single(await repo.GetAsync(10));

        await repo.ClearAsync();
        Assert.Empty(await repo.GetAsync(10));
    }

    [Fact]
    public async Task Recent_files_keep_only_the_newest_fifty()
    {
        var repo = new SqliteRecentFilesRepository(_db);
        var t = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 60; i++) await repo.TouchAsync($@"C:\docs\{i:000}.pdf", t.AddSeconds(i));

        var list = await repo.GetAsync(50);
        Assert.Equal(50, list.Count);
        Assert.EndsWith("059.pdf", list[0].Path);
        Assert.EndsWith("010.pdf", list[^1].Path);
    }
}

public class FileInspectorTests : IDisposable
{
    private const string MinimalPdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n";
    private readonly TempFolder _temp = new();
    private readonly FileInspector _inspector = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Valid_pdf_is_usable_and_hashed()
    {
        var path = _temp.File("ok.pdf", MinimalPdf);
        var entry = await _inspector.InspectAsync(path, computeHash: true);

        Assert.True(entry.IsUsable);
        Assert.Equal(FileKind.Pdf, entry.Kind);
        Assert.Equal("ok.pdf", entry.Name);
        Assert.Equal(MinimalPdf.Length, entry.SizeBytes);
        Assert.Equal(64, entry.Sha256!.Length);
    }

    [Fact]
    public async Task Pdf_header_may_be_preceded_by_junk_bytes()
    {
        var path = _temp.File("lixo.pdf", "\u00EF\u00BB\u00BFlixo inicial\n" + MinimalPdf);
        Assert.True((await _inspector.InspectAsync(path, computeHash: false)).IsUsable);
    }

    [Fact]
    public async Task File_renamed_to_pdf_is_rejected()
    {
        var path = _temp.File("falso.pdf", "MZ isto na verdade nao e um PDF");
        var entry = await _inspector.InspectAsync(path, computeHash: true);

        Assert.Equal(FileProblem.InvalidPdfHeader, entry.Problem);
        Assert.Null(entry.Sha256);
    }

    [Fact]
    public async Task Reports_empty_missing_and_unsupported_files()
    {
        Assert.Equal(FileProblem.Empty, (await _inspector.InspectAsync(_temp.File("vazio.pdf", ""), true)).Problem);
        Assert.Equal(FileProblem.NotFound, (await _inspector.InspectAsync(Path.Combine(_temp.Path, "nao-existe.pdf"), true)).Problem);
        Assert.Equal(FileProblem.UnsupportedType, (await _inspector.InspectAsync(_temp.File("programa.exe", "MZ"), true)).Problem);
    }

    [Fact]
    public async Task Identical_content_gives_identical_hash()
    {
        var a = await _inspector.InspectAsync(_temp.File("a.pdf", MinimalPdf), true);
        var b = await _inspector.InspectAsync(_temp.File("copia.pdf", MinimalPdf), true);
        var c = await _inspector.InspectAsync(_temp.File("outro.pdf", MinimalPdf + "%extra"), true);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Sha256, c.Sha256);
    }

    [Fact]
    public async Task Locked_file_is_reported_as_unreadable()
    {
        var path = _temp.File("travado.pdf", MinimalPdf);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(FileProblem.Unreadable, (await _inspector.InspectAsync(path, true)).Problem);
        }
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var path = _temp.File("grande.pdf", Encoding.ASCII.GetBytes(MinimalPdf).Concat(new byte[4 * 1024 * 1024]).ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _inspector.InspectAsync(path, true, cts.Token));
    }

    [Fact]
    public void Folder_scan_finds_supported_files_recursively()
    {
        _temp.File("a.pdf", MinimalPdf);
        _temp.File(Path.Combine("sub", "b.PNG"), "x");
        _temp.File(Path.Combine("sub", "nivel2", "c.docx"), "x");
        _temp.File("ignorado.exe", "x");
        _temp.File("ignorado.zip", "x");

        var scanner = new FolderScanner();
        Assert.Equal(3, scanner.FindSupportedFiles(_temp.Path, recursive: true).Count);
        Assert.Single(scanner.FindSupportedFiles(_temp.Path, recursive: false));
    }
}

public class FileExportTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FileExportService _service =
        new(NullLogger<FileExportService>.Instance, new OutputPathResolver());

    public void Dispose() => _temp.Dispose();

    private string Destination => Path.Combine(_temp.Path, "destino");

    private OutputOptions Options(OverwritePolicy policy, SubfolderMode mode = SubfolderMode.None) =>
        new(Destination, policy, mode, FileExportService.OperationName);

    [Fact]
    public async Task Copies_files_and_leaves_originals_untouched()
    {
        var a = _temp.File(Path.Combine("origem", "a.pdf"), "conteudo A");
        var b = _temp.File(Path.Combine("origem", "b.pdf"), "conteudo B!");
        var reports = new List<ExportProgress>();

        var results = await _service.CopyAsync([a, b], Options(OverwritePolicy.AutoRename),
            new SyncProgress(reports.Add), CancellationToken.None);

        Assert.All(results, r => Assert.Equal(OperationStatus.Completed, r.Status));
        Assert.Equal("conteudo A", File.ReadAllText(Path.Combine(Destination, "a.pdf")));
        Assert.Equal("conteudo B!", File.ReadAllText(Path.Combine(Destination, "b.pdf")));
        Assert.Equal("conteudo A", File.ReadAllText(a));
        Assert.Equal(10, results[0].Bytes);
        Assert.Equal(2, reports[^1].Completed);
        Assert.Empty(Directory.GetFiles(Destination, "*.pdfmaster-tmp"));
    }

    [Fact]
    public async Task AutoRename_never_overwrites_existing_output()
    {
        var source = _temp.File(Path.Combine("origem", "a.pdf"), "novo");
        _temp.File(Path.Combine("destino", "a.pdf"), "antigo");

        var results = await _service.CopyAsync([source], Options(OverwritePolicy.AutoRename), null, CancellationToken.None);

        Assert.Equal(Path.Combine(Destination, "a (2).pdf"), results[0].OutputPath);
        Assert.Equal("antigo", File.ReadAllText(Path.Combine(Destination, "a.pdf")));
        Assert.Equal("novo", File.ReadAllText(Path.Combine(Destination, "a (2).pdf")));
    }

    [Fact]
    public async Task Skip_policy_keeps_existing_and_reports_skipped()
    {
        var source = _temp.File(Path.Combine("origem", "a.pdf"), "novo");
        _temp.File(Path.Combine("destino", "a.pdf"), "antigo");

        var results = await _service.CopyAsync([source], Options(OverwritePolicy.Skip), null, CancellationToken.None);

        Assert.Equal(OperationStatus.Skipped, results[0].Status);
        Assert.Equal("antigo", File.ReadAllText(Path.Combine(Destination, "a.pdf")));
    }

    [Fact]
    public async Task Overwrite_policy_replaces_existing()
    {
        var source = _temp.File(Path.Combine("origem", "a.pdf"), "novo");
        _temp.File(Path.Combine("destino", "a.pdf"), "antigo");

        var results = await _service.CopyAsync([source], Options(OverwritePolicy.Overwrite), null, CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, results[0].Status);
        Assert.Equal("novo", File.ReadAllText(Path.Combine(Destination, "a.pdf")));
    }

    [Fact]
    public async Task Same_name_from_two_folders_produces_two_outputs()
    {
        var first = _temp.File(Path.Combine("x", "doc.pdf"), "um");
        var second = _temp.File(Path.Combine("y", "doc.pdf"), "dois");

        var results = await _service.CopyAsync([first, second], Options(OverwritePolicy.Overwrite), null, CancellationToken.None);

        Assert.Equal("um", File.ReadAllText(results[0].OutputPath!));
        Assert.Equal("dois", File.ReadAllText(results[1].OutputPath!));
    }

    [Fact]
    public async Task Copying_onto_itself_is_refused_even_with_overwrite()
    {
        var source = _temp.File(Path.Combine("destino", "a.pdf"), "original");

        var results = await _service.CopyAsync([source], Options(OverwritePolicy.Overwrite), null, CancellationToken.None);

        Assert.Equal(OperationStatus.Skipped, results[0].Status);
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task Missing_source_fails_without_stopping_the_batch()
    {
        var ok = _temp.File(Path.Combine("origem", "ok.pdf"), "ok");
        var missing = Path.Combine(_temp.Path, "origem", "sumiu.pdf");

        var results = await _service.CopyAsync([missing, ok], Options(OverwritePolicy.AutoRename), null, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, results[0].Status);
        Assert.Equal(OperationStatus.Completed, results[1].Status);
        Assert.Empty(Directory.GetFiles(Destination, "*.pdfmaster-tmp"));
    }

    [Fact]
    public async Task Cancellation_marks_remaining_items_and_leaves_no_partial_files()
    {
        var files = Enumerable.Range(0, 5)
            .Select(i => _temp.File(Path.Combine("origem", $"f{i}.pdf"), new byte[256 * 1024]))
            .ToList();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Completed == 2) cts.Cancel();
        });

        var results = await _service.CopyAsync(files, Options(OverwritePolicy.AutoRename), progress, cts.Token);

        Assert.Equal(5, results.Count);
        Assert.Equal(2, results.Count(r => r.Status == OperationStatus.Completed));
        Assert.Equal(3, results.Count(r => r.Status == OperationStatus.Cancelled));
        Assert.Equal(2, Directory.GetFiles(Destination).Length);
    }

    [Fact]
    public async Task Subfolder_by_operation_is_created()
    {
        var source = _temp.File(Path.Combine("origem", "a.pdf"), "x");

        var results = await _service.CopyAsync([source], Options(OverwritePolicy.AutoRename, SubfolderMode.ByOperation), null, CancellationToken.None);

        Assert.Equal(Path.Combine(Destination, FileExportService.OperationName, "a.pdf"), results[0].OutputPath);
        Assert.True(File.Exists(results[0].OutputPath));
    }

    /// <summary>IProgress síncrono, para os testes não dependerem de agendamento de threads.</summary>
    private sealed class SyncProgress(Action<ExportProgress> handler) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => handler(value);
    }
}
