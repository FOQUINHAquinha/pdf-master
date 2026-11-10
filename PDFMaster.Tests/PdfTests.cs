using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using Xunit;

namespace PDFMaster.Tests;

public class PageRangeParserTests
{
    [Theory]
    [InlineData("1-5", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("1,3,5,7", new[] { 0, 2, 4, 6 })]
    [InlineData("2-8,12,15-20", new[] { 1, 2, 3, 4, 5, 6, 7, 11, 14, 15, 16, 17, 18, 19 })]
    [InlineData(" 3 ; 1 - 2 ", new[] { 2, 0, 1 })]
    [InlineData("18-", new[] { 17, 18, 19 })]
    [InlineData("-3", new[] { 0, 1, 2 })]
    [InlineData("5-3", new[] { 4, 3, 2 })]
    [InlineData("2,2", new[] { 1, 1 })]
    [InlineData("4–6", new[] { 3, 4, 5 })]
    public void Parses_valid_ranges(string text, int[] expected)
    {
        Assert.Equal(expected, PageRangeParser.Parse(text, 20).ToArray());
    }

    [Fact]
    public void Empty_text_means_all_pages()
    {
        Assert.Equal(new[] { 0, 1, 2 }, PageRangeParser.Parse("  ", 3).ToArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("1-25")]
    [InlineData("abc")]
    [InlineData("1--3")]
    [InlineData("-")]
    [InlineData("1.5")]
    [InlineData("+2")]
    public void Rejects_invalid_ranges_with_a_message(string text)
    {
        var ex = Assert.Throws<PageRangeException>(() => PageRangeParser.Parse(text, 20));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.False(PageRangeParser.TryParse(text, 20, out _, out var error));
        Assert.Equal(ex.Message, error);
    }

    [Fact]
    public void Groups_keep_one_list_per_range()
    {
        var groups = PageRangeParser.ParseGroups("1-3, 4-10,12", 12);
        Assert.Equal(new[] { 3, 7, 1 }, groups.Select(g => g.Count).ToArray());
        Assert.Equal(11, groups[2][0]);
    }

    [Fact]
    public void Format_is_the_inverse_of_parse()
    {
        Assert.Equal("1-3,5,7-8", PageRangeParser.Format([0, 1, 2, 4, 6, 7]));
        Assert.Equal(string.Empty, PageRangeParser.Format([]));
        Assert.Equal(new[] { 0, 1, 2, 4, 6, 7 }, PageRangeParser.Parse(PageRangeParser.Format([0, 1, 2, 4, 6, 7]), 10).ToArray());
    }
}

public class PlannerTests
{
    private static string Describe(IEnumerable<IReadOnlyList<int>> groups) =>
        string.Join(" | ", groups.Select(g => string.Join(",", g)));

    [Fact]
    public void Split_every_n_pages()
    {
        Assert.Equal("0 | 1 | 2", Describe(SplitPlanner.EveryPage(3)));
        Assert.Equal("0,1,2 | 3,4,5 | 6", Describe(SplitPlanner.EveryN(7, 3)));
        Assert.Throws<PageRangeException>(() => SplitPlanner.EveryN(7, 0));
    }

    [Fact]
    public void Split_at_cut_points_ignores_invalid_and_repeated_cuts()
    {
        Assert.Equal("0,1,2 | 3,4,5,6 | 7,8,9", Describe(SplitPlanner.AtCutPoints([7, 3, 3, 0, 10, 99], 10)));
        Assert.Equal("0,1,2", Describe(SplitPlanner.AtCutPoints([], 3)));
    }

    [Fact]
    public void Split_by_ranges_makes_one_file_per_range()
    {
        Assert.Equal("0,1 | 4 | 2,3", Describe(SplitPlanner.ByRanges("1-2,5,3-4", 5)));
    }

    private static string Interleave(IReadOnlyList<InterleaveSource> sources, int perTurn = 1,
        InterleaveRemainder remainder = InterleaveRemainder.AppendRemaining) =>
        string.Join(" ", InterleavePlanner.Plan(sources, perTurn, remainder)
            .Select(p => (char)('A' + p.DocumentIndex) + (p.PageIndex + 1).ToString()));

    [Fact]
    public void Interleave_alternates_pages_as_in_the_specification()
    {
        Assert.Equal("A1 B1 A2 B2 A3 B3", Interleave([new(3), new(3)]));
    }

    [Fact]
    public void Interleave_uneven_documents()
    {
        Assert.Equal("A1 B1 A2 B2 A3 A4", Interleave([new(4), new(2)]));
        Assert.Equal("A1 B1 A2 B2", Interleave([new(4), new(2)], remainder: InterleaveRemainder.StopAtShortest));
    }

    [Fact]
    public void Interleave_in_blocks_reversed_and_with_three_documents()
    {
        Assert.Equal("A1 A2 B1 B2 A3 A4 B3", Interleave([new(4), new(3)], perTurn: 2));
        Assert.Equal("A1 B3 A2 B2 A3 B1", Interleave([new(3), new(3, Reverse: true)]));
        Assert.Equal("A1 B1 C1 A2 C2", Interleave([new(2), new(1), new(2)]));
    }

    [Fact]
    public void Interleave_needs_two_documents()
    {
        Assert.Throws<PageRangeException>(() => InterleavePlanner.Plan([new(3)]));
    }

    [Theory]
    [InlineData(null, 1, 3, "Documento_001.pdf")]
    [InlineData("{nome}_{n:000}", 12, 30, "Documento_012.pdf")]
    [InlineData("{nome} - parte {n} de {total}", 2, 3, "Documento - parte 2 de 3.pdf")]
    [InlineData("pag_{inicio:00}-{fim:00}.pdf", 1, 3, "pag_04-09.pdf")]
    [InlineData("fixo", 2, 3, "fixo_002.pdf")]
    [InlineData("fixo", 1, 1, "fixo.pdf")]
    public void Formats_split_file_names(string? pattern, int number, int total, string expected)
    {
        Assert.Equal(expected, SplitNameFormatter.Format(pattern, "Documento", number, total, 4, 9));
    }
}

/// <summary>Gera PDFs de teste. A largura de cada página identifica de onde ela veio.</summary>
public static class PdfSamples
{
    public static string Create(TempFolder temp, string name, int pages, int firstWidth, int height = 400,
        Func<int, int>? rotation = null, bool bookmarks = false, bool heavy = false)
    {
        var path = Path.Combine(temp.Path, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var random = new Random(12345);

        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(firstWidth + i, height));
            if (rotation is not null) page.Rotate = rotation(i);

            using (var graphics = XGraphics.FromPdfPage(page))
            {
                graphics.DrawRectangle(XBrushes.Red, new XRect(40, 40, 120, 120));
                if (heavy)
                {
                    var pen = new XPen(XColors.Black, 0.3);
                    for (var k = 0; k < 2500; k++)
                    {
                        graphics.DrawLine(pen, random.NextDouble() * 300, random.NextDouble() * 400,
                            random.NextDouble() * 300, random.NextDouble() * 400);
                    }
                }
            }

            if (bookmarks && i % 3 == 0) document.Outlines.Add($"Capítulo {i / 3 + 1}", page, true);
        }

        document.Save(path);
        return path;
    }
}

public class PdfEngineTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfSharpEngine _engine = new();

    public void Dispose() => _temp.Dispose();

    private string Out(string name) => Path.Combine(_temp.Path, "saida", name);

    private async Task<int[]> Widths(string path) =>
        (await _engine.ReadInfoAsync(path)).Pages.Select(p => (int)Math.Round(p.WidthPt)).ToArray();

    [Fact]
    public async Task Reads_page_count_sizes_rotation_and_size()
    {
        var path = PdfSamples.Create(_temp, "a.pdf", 4, 300, rotation: i => i == 2 ? 90 : 0);
        var info = await _engine.ReadInfoAsync(path);

        Assert.Equal(4, info.PageCount);
        Assert.Equal(new[] { 300, 301, 302, 303 }, info.Pages.Select(p => (int)Math.Round(p.WidthPt)).ToArray());
        Assert.All(info.Pages, p => Assert.Equal(400, p.HeightPt, 3));
        Assert.Equal(new[] { 0, 0, 90, 0 }, info.Pages.Select(p => p.Rotation).ToArray());
        Assert.Equal(new FileInfo(path).Length, info.SizeBytes);
        Assert.Empty(info.Bookmarks);
    }

    [Fact]
    public async Task Reads_bookmarks_with_their_pages()
    {
        var path = PdfSamples.Create(_temp, "livro.pdf", 7, 300, bookmarks: true);
        var info = await _engine.ReadInfoAsync(path);

        Assert.Equal(new[] { "Capítulo 1", "Capítulo 2", "Capítulo 3" }, info.Bookmarks.Select(b => b.Title).ToArray());
        Assert.Equal(new[] { 0, 3, 6 }, info.Bookmarks.Select(b => b.PageIndex).ToArray());
    }

    [Fact]
    public async Task Composes_pages_from_several_documents_in_the_requested_order()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 3, 300);
        var b = PdfSamples.Create(_temp, "b.pdf", 2, 500);
        var output = Out("novo.pdf");

        var bytes = await _engine.ComposeAsync(
            [PageInstruction.From(b, 1), PageInstruction.From(a, 0), PageInstruction.From(a, 2), PageInstruction.From(a, 0)],
            output);

        Assert.Equal(new[] { 501, 300, 302, 300 }, await Widths(output));
        Assert.Equal(new FileInfo(output).Length, bytes);
        Assert.Equal(new[] { 300, 301, 302 }, await Widths(a)); // original intacto
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.pdfmaster-tmp"));
    }

    [Fact]
    public async Task Rotation_is_added_to_the_existing_rotation_and_size_is_kept()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 3, 300, rotation: i => i == 1 ? 90 : 0);
        var output = Out("girado.pdf");

        await _engine.ComposeAsync(
            [PageInstruction.From(a, 0, 90), PageInstruction.From(a, 1, 90), PageInstruction.From(a, 1, 270), PageInstruction.From(a, 2, -90)],
            output);

        var info = await _engine.ReadInfoAsync(output);
        Assert.Equal(new[] { 90, 180, 0, 270 }, info.Pages.Select(p => p.Rotation).ToArray());
        Assert.Equal(new[] { 300, 301, 301, 302 }, info.Pages.Select(p => (int)Math.Round(p.WidthPt)).ToArray());
        Assert.All(info.Pages, p => Assert.Equal(400, p.HeightPt, 3));
    }

    [Fact]
    public async Task Blank_pages_have_the_requested_size()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 1, 300);
        var output = Out("branco.pdf");

        await _engine.ComposeAsync([PageInstruction.Blank(), PageInstruction.From(a, 0), PageInstruction.Blank(200, 100)], output);

        var info = await _engine.ReadInfoAsync(output);
        Assert.Equal(new[] { 595, 300, 200 }, info.Pages.Select(p => (int)Math.Round(p.WidthPt)).ToArray());
        Assert.Equal(new[] { 842, 400, 100 }, info.Pages.Select(p => (int)Math.Round(p.HeightPt)).ToArray());
    }

    [Fact]
    public async Task Writes_bookmarks_that_can_be_read_back()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 4, 300);
        var output = Out("marcado.pdf");

        await _engine.ComposeAsync(
            Enumerable.Range(0, 4).Select(i => PageInstruction.From(a, i)).ToList(),
            output,
            new ComposeOptions([new OutputBookmark("Início", 0), new OutputBookmark("Anexo", 2)], Title: "Teste"));

        var info = await _engine.ReadInfoAsync(output);
        Assert.Equal(new[] { "Início", "Anexo" }, info.Bookmarks.Select(b => b.Title).ToArray());
        Assert.Equal(new[] { 0, 2 }, info.Bookmarks.Select(b => b.PageIndex).ToArray());
        Assert.Equal("Teste", info.Title);
    }

    [Fact]
    public async Task Never_writes_over_a_source_file()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 2, 300);
        var before = File.ReadAllBytes(a);

        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _engine.ComposeAsync([PageInstruction.From(a, 1)], a, new ComposeOptions(Overwrite: true)));

        Assert.Equal(before, File.ReadAllBytes(a));
    }

    [Fact]
    public async Task Existing_output_is_kept_unless_overwrite_is_requested()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 2, 300);
        var output = Out("existe.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "antigo");

        await Assert.ThrowsAsync<PdfEngineException>(() => _engine.ComposeAsync([PageInstruction.From(a, 0)], output));
        Assert.Equal("antigo", File.ReadAllText(output));

        await _engine.ComposeAsync([PageInstruction.From(a, 0)], output, new ComposeOptions(Overwrite: true));
        Assert.Equal(new[] { 300 }, await Widths(output));
    }

    [Fact]
    public async Task Reports_clear_errors_for_bad_input()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 2, 300);
        var fake = _temp.File("falso.pdf", "isto nao e um PDF de verdade, apenas texto");

        await Assert.ThrowsAsync<PdfEngineException>(() => _engine.ReadInfoAsync(fake));
        await Assert.ThrowsAsync<PdfEngineException>(() => _engine.ReadInfoAsync(Path.Combine(_temp.Path, "nao-existe.pdf")));
        await Assert.ThrowsAsync<PdfEngineException>(() => _engine.ComposeAsync([PageInstruction.From(a, 5)], Out("x.pdf")));
        await Assert.ThrowsAsync<PdfEngineException>(() => _engine.ComposeAsync([], Out("y.pdf")));
        Assert.False(File.Exists(Out("x.pdf")));
        Assert.False(File.Exists(Out("y.pdf")));
    }

    [Fact]
    public async Task Cancellation_leaves_no_output_and_no_temporary_file()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 30, 300);
        var output = Out("cancelado.pdf");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value > 0.3) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ComposeAsync(
            Enumerable.Range(0, 30).Select(i => PageInstruction.From(a, i)).ToList(), output, null, progress, cts.Token));

        Assert.False(File.Exists(output));
        var folder = Path.GetDirectoryName(output)!;
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0);
    }

    [Fact]
    public async Task Measure_matches_the_size_actually_written()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 5, 300, heavy: true);
        var pages = Enumerable.Range(1, 3).Select(i => PageInstruction.From(a, i)).ToList();

        var measured = await _engine.MeasureAsync(pages);
        var written = await _engine.ComposeAsync(pages, Out("medido.pdf"));

        Assert.InRange(measured, written - 512, written + 512);
    }

    private sealed class InlineProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}

public class PdfOrganizerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfSharpEngine _engine = new();
    private readonly PdfOrganizer _organizer;

    public PdfOrganizerTests()
    {
        _organizer = new PdfOrganizer(_engine, new OutputPathResolver());
    }

    public void Dispose() => _temp.Dispose();

    private string OutFolder => Path.Combine(_temp.Path, "saida");

    private OutputTarget Target(OverwritePolicy policy = OverwritePolicy.AutoRename, string? folder = null) =>
        new(folder ?? OutFolder, policy, SubfolderMode.None);

    private async Task<int[]> Widths(string path) =>
        (await _engine.ReadInfoAsync(path)).Pages.Select(p => (int)Math.Round(p.WidthPt)).ToArray();

    [Fact]
    public async Task Merge_joins_files_with_page_ranges_and_one_bookmark_per_file()
    {
        var a = PdfSamples.Create(_temp, "Contrato.pdf", 5, 300);
        var b = PdfSamples.Create(_temp, "Anexo.pdf", 3, 500);

        var result = await _organizer.MergeAsync(
            [new MergeInput(a, "1-2,5"), new MergeInput(b), new MergeInput(a, "3")],
            "resultado", Target(), bookmarkPerFile: true);

        Assert.Equal(Path.Combine(OutFolder, "resultado.pdf"), result.Path);
        Assert.Equal(7, result.PageCount);
        Assert.Equal(new[] { 300, 301, 304, 500, 501, 502, 302 }, await Widths(result.Path));

        var info = await _engine.ReadInfoAsync(result.Path);
        Assert.Equal(new[] { "Contrato", "Anexo", "Contrato" }, info.Bookmarks.Select(x => x.Title).ToArray());
        Assert.Equal(new[] { 0, 3, 6 }, info.Bookmarks.Select(x => x.PageIndex).ToArray());
        Assert.Equal(new FileInfo(result.Path).Length, result.Bytes);
    }

    [Fact]
    public async Task Merge_keeps_page_rotation_and_size_of_each_source()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 2, 300, height: 400);
        var b = PdfSamples.Create(_temp, "b.pdf", 2, 700, height: 250, rotation: _ => 90);

        var result = await _organizer.MergeAsync([new MergeInput(a), new MergeInput(b)], "m.pdf", Target(), false);

        var info = await _engine.ReadInfoAsync(result.Path);
        Assert.Equal(new[] { 0, 0, 90, 90 }, info.Pages.Select(p => p.Rotation).ToArray());
        Assert.Equal(new[] { 400, 400, 250, 250 }, info.Pages.Select(p => (int)Math.Round(p.HeightPt)).ToArray());
        Assert.Empty(info.Bookmarks);
    }

    [Fact]
    public async Task Merge_reports_which_file_has_an_invalid_range()
    {
        var a = PdfSamples.Create(_temp, "Contrato.pdf", 2, 300);
        var ex = await Assert.ThrowsAsync<PdfEngineException>(() =>
            _organizer.MergeAsync([new MergeInput(a, "1-9")], "x", Target(), false));
        Assert.Contains("Contrato.pdf", ex.Message);
        Assert.False(Directory.Exists(OutFolder) && Directory.GetFiles(OutFolder).Length > 0);
    }

    [Fact]
    public async Task Interleave_alternates_documents()
    {
        var a = PdfSamples.Create(_temp, "frentes.pdf", 3, 300);
        var b = PdfSamples.Create(_temp, "versos.pdf", 3, 500);

        var normal = await _organizer.InterleaveAsync([new InterleaveInput(a), new InterleaveInput(b)], 1,
            InterleaveRemainder.AppendRemaining, "i1", Target());
        Assert.Equal(new[] { 300, 500, 301, 501, 302, 502 }, await Widths(normal.Path));

        var reversed = await _organizer.InterleaveAsync([new InterleaveInput(a), new InterleaveInput(b, Reverse: true)], 1,
            InterleaveRemainder.AppendRemaining, "i2", Target());
        Assert.Equal(new[] { 300, 502, 301, 501, 302, 500 }, await Widths(reversed.Path));

        await Assert.ThrowsAsync<PdfEngineException>(() => _organizer.InterleaveAsync([new InterleaveInput(a)], 1,
            InterleaveRemainder.AppendRemaining, "i3", Target()));
    }

    [Fact]
    public async Task Split_creates_numbered_files_and_keeps_the_original()
    {
        var a = PdfSamples.Create(_temp, "Documento.pdf", 7, 300);
        var before = File.ReadAllBytes(a);
        var reports = new List<double>();

        var files = await _organizer.SplitAsync(a, SplitPlanner.EveryN(7, 3), null, Target(),
            new ListProgress(reports));

        Assert.Equal(new[] { "Documento_001.pdf", "Documento_002.pdf", "Documento_003.pdf" },
            files.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.Equal(new[] { 3, 3, 1 }, files.Select(f => f.PageCount).ToArray());
        Assert.Equal(new[] { 300, 301, 302 }, await Widths(files[0].Path));
        Assert.Equal(new[] { 306 }, await Widths(files[2].Path));
        Assert.Equal(before, File.ReadAllBytes(a));
        Assert.Equal(1.0, reports[^1], 3);
    }

    [Fact]
    public async Task Split_by_bookmarks_and_custom_pattern()
    {
        var a = PdfSamples.Create(_temp, "livro.pdf", 7, 300, bookmarks: true);
        var info = await _engine.ReadInfoAsync(a);

        var groups = SplitPlanner.AtCutPoints(info.Bookmarks.Where(b => b.Level == 0).Select(b => b.PageIndex), info.PageCount);
        var files = await _organizer.SplitAsync(a, groups, "{nome} p{inicio}-{fim}", Target());

        Assert.Equal(new[] { "livro p1-3.pdf", "livro p4-6.pdf", "livro p7-7.pdf" },
            files.Select(f => Path.GetFileName(f.Path)).ToArray());
    }

    [Fact]
    public async Task Split_into_the_source_folder_never_replaces_existing_files()
    {
        var a = PdfSamples.Create(_temp, "Documento.pdf", 2, 300);
        var folder = Path.GetDirectoryName(a)!;
        var existing = _temp.File("Documento_001.pdf", "ja existia");

        var files = await _organizer.SplitAsync(a, SplitPlanner.EveryPage(2), null, Target(folder: folder));

        Assert.Equal("ja existia", File.ReadAllText(existing));
        Assert.Equal(new[] { "Documento_001 (2).pdf", "Documento_002.pdf" },
            files.Select(f => Path.GetFileName(f.Path)).ToArray());

        var ex = await Assert.ThrowsAsync<PdfEngineException>(() =>
            _organizer.SplitAsync(a, SplitPlanner.EveryPage(2), null, Target(OverwritePolicy.Skip, folder)));
        Assert.Contains("Documento_001.pdf", ex.Message);

        var replaced = await _organizer.SplitAsync(a, SplitPlanner.EveryPage(2), null, Target(OverwritePolicy.Overwrite, folder));
        Assert.Equal("Documento_001.pdf", Path.GetFileName(replaced[0].Path));
        Assert.Equal(new[] { 300 }, await Widths(existing));
    }

    [Fact]
    public async Task Split_by_size_respects_the_limit_and_keeps_every_page()
    {
        var a = PdfSamples.Create(_temp, "pesado.pdf", 9, 300, heavy: true);
        var total = new FileInfo(a).Length;
        var limit = total / 3;

        var groups = await _organizer.PlanSplitBySizeAsync(a, limit);
        var files = await _organizer.SplitAsync(a, groups, null, Target());

        Assert.True(files.Count >= 3, $"esperava 3 ou mais arquivos, vieram {files.Count}");
        Assert.Equal(Enumerable.Range(0, 9).ToArray(), groups.SelectMany(g => g).ToArray());
        Assert.All(files, f => Assert.True(f.Bytes <= limit + 512 || f.PageCount == 1,
            $"{Path.GetFileName(f.Path)} tem {f.Bytes} bytes; limite {limit}"));
        Assert.Contains(files, f => f.PageCount > 1);

        await Assert.ThrowsAsync<PdfEngineException>(() => _organizer.PlanSplitBySizeAsync(a, 10));
    }

    [Fact]
    public async Task Saving_an_arrangement_never_overwrites_the_source_even_with_overwrite_policy()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 3, 300);
        var folder = Path.GetDirectoryName(a)!;

        var result = await _organizer.SaveArrangementAsync(
            [PageInstruction.From(a, 2, 90), PageInstruction.Blank(), PageInstruction.From(a, 0)],
            "a.pdf", Target(OverwritePolicy.Overwrite, folder), "Reorganizar páginas");

        Assert.Equal("a (2).pdf", Path.GetFileName(result.Path));
        Assert.Equal(new[] { 300, 301, 302 }, await Widths(a));
        Assert.Equal(new[] { 302, 595, 300 }, await Widths(result.Path));
    }

    [Fact]
    public async Task Missing_destination_folder_is_reported()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 1, 300);
        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _organizer.SaveArrangementAsync([PageInstruction.From(a, 0)], "x", new OutputTarget("", OverwritePolicy.AutoRename, SubfolderMode.None), "Teste"));
    }

    private sealed class ListProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}

public class PdfRendererTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfiumRenderer _renderer = new();

    public void Dispose() => _temp.Dispose();

    private static (byte B, byte G, byte R, byte A) Pixel(RenderedPage page, int x, int y)
    {
        var i = (y * page.Width + x) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2], page.Bgra[i + 3]);
    }

    [Fact]
    public async Task Renders_a_page_with_its_content_on_a_white_background()
    {
        var path = PdfSamples.Create(_temp, "a.pdf", 1, 300);
        var page = await _renderer.RenderPageAsync(path, 0, RenderSize.ByScale(1));

        Assert.InRange(page.Width, 299, 301);
        Assert.InRange(page.Height, 399, 401);
        Assert.Equal(page.Width * page.Height * 4, page.Bgra.Length);

        var corner = Pixel(page, 5, 5);
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), corner);

        var inside = Pixel(page, 100, 100); // dentro do retângulo vermelho (40..160)
        Assert.True(inside.R > 200 && inside.G < 60 && inside.B < 60, $"pixel = {inside}");
    }

    [Fact]
    public async Task Scale_and_fit_change_the_output_size()
    {
        var path = PdfSamples.Create(_temp, "a.pdf", 1, 300);

        var doubled = await _renderer.RenderPageAsync(path, 0, RenderSize.ByScale(2));
        Assert.InRange(doubled.Width, 598, 602);
        Assert.InRange(doubled.Height, 798, 802);

        var thumbnail = await _renderer.RenderPageAsync(path, 0, RenderSize.Fit(150, 200));
        Assert.InRange(thumbnail.Width, 100, 150);
        Assert.InRange(thumbnail.Height, 150, 200);
    }

    [Fact]
    public async Task Rotated_pages_are_rendered_rotated()
    {
        var path = PdfSamples.Create(_temp, "r.pdf", 1, 300, rotation: _ => 90);
        var page = await _renderer.RenderPageAsync(path, 0, RenderSize.ByScale(1));

        Assert.InRange(page.Width, 399, 401);
        Assert.InRange(page.Height, 299, 301);
    }

    [Fact]
    public async Task Renders_several_pages_in_order_and_skips_missing_ones()
    {
        var path = PdfSamples.Create(_temp, "a.pdf", 4, 300);
        var seen = new List<(int Index, int Width)>();

        await _renderer.RenderPagesAsync(path, [3, 0, 9, 2], RenderSize.ByScale(1), (index, page) => seen.Add((index, page.Width)));

        Assert.Equal(new[] { 3, 0, 2 }, seen.Select(s => s.Index).ToArray());
        Assert.InRange(seen[0].Width, 302, 304);
        Assert.InRange(seen[1].Width, 299, 301);
    }

    [Fact]
    public async Task Output_of_the_engine_is_readable_by_the_renderer()
    {
        var a = PdfSamples.Create(_temp, "a.pdf", 2, 300);
        var output = Path.Combine(_temp.Path, "girado.pdf");
        await new PdfSharpEngine().ComposeAsync([PageInstruction.From(a, 0, 90), PageInstruction.Blank(200, 100)], output);

        var first = await _renderer.RenderPageAsync(output, 0, RenderSize.ByScale(1));
        var second = await _renderer.RenderPageAsync(output, 1, RenderSize.ByScale(1));

        Assert.InRange(first.Width, 399, 401);   // 300×400 girada 90° → 400×300
        Assert.InRange(first.Height, 299, 301);
        Assert.InRange(second.Width, 199, 201);
        Assert.InRange(second.Height, 99, 101);
    }

    [Fact]
    public async Task Invalid_file_and_cancellation_are_reported()
    {
        var fake = _temp.File("falso.pdf", "nada de PDF aqui");
        await Assert.ThrowsAsync<PdfRenderException>(() => _renderer.RenderPageAsync(fake, 0, RenderSize.ByScale(1)));
        await Assert.ThrowsAsync<PdfRenderException>(() =>
            _renderer.RenderPageAsync(Path.Combine(_temp.Path, "nao-existe.pdf"), 0, RenderSize.ByScale(1)));

        var path = PdfSamples.Create(_temp, "a.pdf", 1, 300);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _renderer.RenderPageAsync(path, 0, RenderSize.ByScale(1), cts.Token));
    }
}

/// <summary>
/// Não é um teste de comportamento: gera os PDFs usados pelo teste de inicialização do CI
/// (capturas de tela das ferramentas). Só grava quando PDFMASTER_SMOKE_DIR está definida.
/// </summary>
public class SmokeSamples
{
    [Fact]
    public void Writes_sample_documents_for_the_startup_test()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMASTER_SMOKE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;

        Directory.CreateDirectory(folder);
        Write(Path.Combine(folder, "Relatorio.pdf"), 7, XBrushes.SeaGreen, landscapeEvery: 3);
        Write(Path.Combine(folder, "Anexos.pdf"), 4, XBrushes.SteelBlue, landscapeEvery: 0);
    }

    private static void Write(string path, int pages, XBrush brush, int landscapeEvery)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var landscape = landscapeEvery > 0 && i % landscapeEvery == landscapeEvery - 1;
            double width = landscape ? 842 : 595, height = landscape ? 595 : 842;
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));

            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(brush, new XRect(0, 0, width, 90));
            // Uma barra por número de página, para as páginas ficarem distinguíveis sem texto.
            for (var k = 0; k <= i; k++) graphics.DrawRectangle(brush, new XRect(60 + k * 60, 150, 40, 40));
            var pen = new XPen(XColors.Gray, 1);
            for (var line = 0; line < 22; line++)
            {
                graphics.DrawLine(pen, 60, 240 + line * 22, width - 60 - (line % 4) * 50, 240 + line * 22);
            }

            if (i % 3 == 0) document.Outlines.Add($"Seção {i / 3 + 1}", page, true);
        }

        document.Save(path);
    }
}
