using PDFMaster.Core.Files;
using PDFMaster.Core.Settings;
using PDFMaster.Core.Tools;
using PDFMaster.Domain;
using Xunit;

namespace PDFMaster.Tests;

public class ToolSearchTests
{
    private readonly ToolSearchService _search = new(new ToolCatalog());

    [Theory]
    [InlineData("juntar documentos", "merge")]
    [InlineData("JUNTAR", "merge")]
    [InlineData("mesclar", "merge")]
    [InlineData("separar pdf", "split")]
    [InlineData("DIVIDIR", "split")]
    [InlineData("reduzir tamanho", "compress")]
    [InlineData("colocar senha", "protect")]
    [InlineData("tirar senha", "unlock")]
    [InlineData("pdf escaneado", "ocr")]
    [InlineData("word para pdf", "office-to-pdf")]
    [InlineData("numerar páginas", "header-footer")]
    [InlineData("marca dagua", "watermark")]
    [InlineData("scanner", "scan")]
    public void Finds_tool_by_name_synonym_or_description(string query, string expectedId)
    {
        var results = _search.Search(query);
        Assert.NotEmpty(results);
        Assert.Equal(expectedId, results[0].Id);
    }

    [Fact]
    public void Search_ignores_accents_and_case()
    {
        Assert.Equal(_search.Search("seguranca digital").Select(t => t.Id), _search.Search("SEGURANÇA DIGITAL").Select(t => t.Id));
        Assert.Contains(_search.Search("paginas"), t => t.Id == "extract-pages");
    }

    [Fact]
    public void Empty_query_returns_whole_catalog()
    {
        Assert.Equal(new ToolCatalog().All.Count, _search.Search("   ").Count);
    }

    [Fact]
    public void Unknown_term_returns_nothing()
    {
        Assert.Empty(_search.Search("xyzzy-inexistente"));
    }

    [Fact]
    public void Catalog_ids_are_unique_and_only_implemented_tools_are_available()
    {
        var all = new ToolCatalog().All;
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.All(all, t => Assert.InRange(t.PlannedPhase, 2, 7));

        // Somente as ferramentas da Fase 2 estão implementadas.
        var available = all.Where(t => t.IsAvailable).Select(t => t.Id).OrderBy(id => id).ToArray();
        Assert.Equal(
            new[] { "delete-pages", "extract-pages", "insert-pages", "interleave", "merge", "reorder", "rotate", "split", "view" },
            available);
        Assert.All(all.Where(t => t.IsAvailable), t => Assert.Equal(2, t.PlannedPhase));
        Assert.All(ToolCatalog.Implemented, id => Assert.NotNull(new ToolCatalog().Find(id)));
    }

    [Theory]
    [InlineData("abrir pdf", "view")]
    [InlineData("visualizar", "view")]
    [InlineData("intercalar", "interleave")]
    [InlineData("girar", "rotate")]
    [InlineData("excluir páginas", "delete-pages")]
    public void Finds_phase_two_tools(string query, string expectedId)
    {
        Assert.Equal(expectedId, _search.Search(query)[0].Id);
    }

    [Fact]
    public void Every_menu_category_has_tools()
    {
        var catalog = new ToolCatalog();
        foreach (var category in Enum.GetValues<ToolCategory>())
        {
            Assert.NotEmpty(catalog.ByCategory(category));
        }
    }
}

public class PathSafetyTests
{
    [Theory]
    [InlineData("relatório.pdf", "relatório.pdf")]
    [InlineData("a<b>c:d.pdf", "a_b_c_d.pdf")]
    [InlineData("..\\..\\windows\\x.pdf", ".._.._windows_x.pdf")]
    [InlineData("nome. ", "nome")]
    [InlineData("", "arquivo")]
    [InlineData("..", "arquivo")]
    [InlineData("CON.pdf", "_CON.pdf")]
    [InlineData("nul", "_nul")]
    public void Sanitizes_file_names(string input, string expected)
    {
        Assert.Equal(expected, PathSafety.SanitizeFileName(input));
    }

    [Fact]
    public void Sanitized_name_is_limited_in_length_and_keeps_extension()
    {
        var name = PathSafety.SanitizeFileName(new string('a', 400) + ".pdf");
        Assert.True(name.Length <= 200);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void Detects_paths_outside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdfm-root");
        Assert.True(PathSafety.IsWithin(root, Path.Combine(root, "sub", "a.pdf")));
        Assert.False(PathSafety.IsWithin(root, Path.Combine(root, "..", "fora.pdf")));
        Assert.False(PathSafety.IsWithin(root, root + "-vizinho" + Path.DirectorySeparatorChar + "a.pdf"));
        Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureWithin(root, Path.Combine(root, "..", "fora.pdf")));
    }
}

public class OutputPathResolverTests
{
    private static readonly DateTime Now = new(2026, 3, 7, 10, 0, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pdfm-out");

    private OutputOptions Options(OverwritePolicy policy, SubfolderMode mode = SubfolderMode.None) =>
        new(_root, policy, mode, "Copiar arquivos");

    [Fact]
    public void Uses_requested_name_when_free()
    {
        var resolver = new OutputPathResolver(_ => false);
        var decision = resolver.Resolve(Options(OverwritePolicy.AutoRename), "a.pdf", Now);
        Assert.Equal(Path.Combine(_root, "a.pdf"), decision.FullPath);
        Assert.False(decision.Skip);
        Assert.False(decision.Overwrites);
    }

    [Fact]
    public void AutoRename_picks_next_free_number()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_root, "a.pdf"),
            Path.Combine(_root, "a (2).pdf"),
        };
        var resolver = new OutputPathResolver(existing.Contains);
        var decision = resolver.Resolve(Options(OverwritePolicy.AutoRename), "a.pdf", Now);
        Assert.Equal(Path.Combine(_root, "a (3).pdf"), decision.FullPath);
        Assert.False(decision.Overwrites);
    }

    [Fact]
    public void Skip_policy_keeps_existing_file()
    {
        var resolver = new OutputPathResolver(_ => true);
        var decision = resolver.Resolve(Options(OverwritePolicy.Skip), "a.pdf", Now);
        Assert.True(decision.Skip);
        Assert.False(decision.Overwrites);
    }

    [Fact]
    public void Overwrite_policy_is_the_only_one_that_replaces()
    {
        var existing = Path.Combine(_root, "a.pdf");
        var resolver = new OutputPathResolver(p => string.Equals(p, existing, StringComparison.OrdinalIgnoreCase));
        var decision = resolver.Resolve(Options(OverwritePolicy.Overwrite), "a.pdf", Now);
        Assert.True(decision.Overwrites);
        Assert.Equal(existing, decision.FullPath);
    }

    [Theory]
    [InlineData(OverwritePolicy.AutoRename)]
    [InlineData(OverwritePolicy.Skip)]
    [InlineData(OverwritePolicy.Overwrite)]
    public void Two_inputs_with_same_name_never_share_an_output(OverwritePolicy policy)
    {
        var resolver = new OutputPathResolver(_ => false);
        var reserved = OutputPathResolver.CreateReservationSet();
        var first = resolver.Resolve(Options(policy), "a.pdf", Now, reserved);
        var second = resolver.Resolve(Options(policy), "A.PDF", Now, reserved);
        Assert.False(second.Skip);
        Assert.False(second.Overwrites);
        Assert.NotEqual(first.FullPath, second.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Subfolders_by_date_and_operation()
    {
        var resolver = new OutputPathResolver(_ => false);
        Assert.Equal(Path.Combine(_root, "2026-03-07", "a.pdf"),
            resolver.Resolve(Options(OverwritePolicy.AutoRename, SubfolderMode.ByDate), "a.pdf", Now).FullPath);
        Assert.Equal(Path.Combine(_root, "Copiar arquivos", "a.pdf"),
            resolver.Resolve(Options(OverwritePolicy.AutoRename, SubfolderMode.ByOperation), "a.pdf", Now).FullPath);
    }

    [Fact]
    public void Output_never_leaves_destination_folder()
    {
        var resolver = new OutputPathResolver(_ => false);
        var decision = resolver.Resolve(Options(OverwritePolicy.AutoRename), Path.Combine("..", "..", "fora.pdf"), Now);
        Assert.True(PathSafety.IsWithin(_root, decision.FullPath));
    }

    [Fact]
    public void Missing_destination_is_rejected()
    {
        var resolver = new OutputPathResolver(_ => false);
        Assert.Throws<ArgumentException>(() =>
            resolver.Resolve(new OutputOptions(" ", OverwritePolicy.AutoRename, SubfolderMode.None, "x"), "a.pdf", Now));
    }
}

public class SettingsAndFormattingTests
{
    [Fact]
    public void Settings_round_trip_through_dictionary()
    {
        var original = new AppSettings
        {
            Theme = AppTheme.Dark,
            AccentColor = "#0067C0",
            DefaultOutputFolder = @"C:\Saida",
            RememberLastFolder = false,
            LastFolder = @"C:\Ultima",
            OverwritePolicy = OverwritePolicy.Skip,
            SubfolderMode = SubfolderMode.ByDate,
            HistoryEnabled = false,
        };

        var restored = SettingsService.FromDictionary(SettingsService.ToDictionary(original));

        Assert.Equal(original.Theme, restored.Theme);
        Assert.Equal(original.AccentColor, restored.AccentColor);
        Assert.Equal(original.DefaultOutputFolder, restored.DefaultOutputFolder);
        Assert.Equal(original.RememberLastFolder, restored.RememberLastFolder);
        Assert.Equal(original.LastFolder, restored.LastFolder);
        Assert.Equal(original.OverwritePolicy, restored.OverwritePolicy);
        Assert.Equal(original.SubfolderMode, restored.SubfolderMode);
        Assert.Equal(original.HistoryEnabled, restored.HistoryEnabled);
    }

    [Fact]
    public void Invalid_stored_values_fall_back_to_defaults()
    {
        var restored = SettingsService.FromDictionary(new Dictionary<string, string>
        {
            ["theme"] = "Roxo",
            ["accent"] = "verde",
            ["overwritePolicy"] = "???",
            ["historyEnabled"] = "talvez",
        });

        Assert.Equal(AppTheme.Light, restored.Theme);
        Assert.Null(restored.AccentColor);
        Assert.Equal(OverwritePolicy.AutoRename, restored.OverwritePolicy);
        Assert.True(restored.HistoryEnabled);
    }

    [Theory]
    [InlineData("#008B50", true)]
    [InlineData("#abcdef", true)]
    [InlineData("008B50", false)]
    [InlineData("#08B50", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void Validates_accent_color(string? value, bool expected)
    {
        Assert.Equal(expected, SettingsService.IsValidAccent(value));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(26214400, "25 MB")]
    public void Formats_file_sizes_in_portuguese(long bytes, string expected)
    {
        Assert.Equal(expected, FileSizeFormatter.Format(bytes));
    }

    [Theory]
    [InlineData("a.PDF", FileKind.Pdf)]
    [InlineData("foto.jpeg", FileKind.Image)]
    [InlineData("planilha.xlsx", FileKind.OfficeDocument)]
    [InlineData("nota.txt", FileKind.Text)]
    [InlineData("pagina.htm", FileKind.Html)]
    [InlineData("virus.exe", FileKind.Unsupported)]
    [InlineData("semextensao", FileKind.Unsupported)]
    public void Classifies_files_by_extension(string name, FileKind expected)
    {
        Assert.Equal(expected, SupportedFiles.GetKind(name));
    }
}
