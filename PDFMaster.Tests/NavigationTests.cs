using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Fase 8: marcadores, links e anexos.</summary>
public class NavigationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfSharpEngine _engine = new();
    private readonly DocumentNavigation _navigation;

    public NavigationTests() => _navigation = new DocumentNavigation(_engine, new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private static readonly NavigationChanges Nothing = new(null, [], false, [], [], false);

    private string WithText(string name, params string[] pages)
    {
        var path = Path.Combine(_temp.Path, name);
        using var document = new PdfDocument();
        foreach (var text in pages)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString(text, new XFont("Arial", 12), XBrushes.Black, 60, 100);
        }

        document.Save(path);
        return path;
    }

    [Fact]
    public async Task Bookmarks_are_replaced_with_levels_and_validated()
    {
        var source = PdfSamples.Create(_temp, "livro.pdf", 6, 300, bookmarks: true);
        var before = await _navigation.ReadAsync(source);
        Assert.Equal(2, before.Bookmarks.Count);

        IReadOnlyList<PdfBookmark> wanted = [new("Capítulo 1", 0, 0), new("Seção 1.1", 1, 1), new("Seção 1.1.1", 2, 2), new("Capítulo 2", 4, 0)];
        var result = await _navigation.SaveAsync(source, Nothing with { Bookmarks = wanted }, Target);
        Assert.EndsWith("livro_navegacao.pdf", result.File.Path);
        Assert.Equal(wanted, (await _engine.ReadInfoAsync(result.File.Path)).Bookmarks);

        Assert.NotNull(DocumentNavigation.Validate([new("A", 0, 1)], 6));
        Assert.NotNull(DocumentNavigation.Validate([new("A", 0, 0), new("B", 1, 2)], 6));
        Assert.NotNull(DocumentNavigation.Validate([new("A", 9, 0)], 6));
        Assert.NotNull(DocumentNavigation.Validate([new(" ", 0, 0)], 6));
        await Assert.ThrowsAsync<PdfEngineException>(() => _navigation.SaveAsync(source, Nothing with { Bookmarks = [new("A", 0, 1)] }, Target));
    }

    [Fact]
    public async Task Urls_written_in_the_text_become_links_once()
    {
        var source = WithText("site.pdf", "Consulte https://www.cnj.jus.br/pesquisa ou www.exemplo.com.br.", "Sem endereço aqui.");
        var result = await _navigation.SaveAsync(source, Nothing with { LinkUrlsInText = true }, Target);
        Assert.Equal(2, result.LinksCreated);
        var links = (await _navigation.ReadAsync(result.File.Path)).Links;
        Assert.Equal(new[] { "https://www.cnj.jus.br/pesquisa", "https://www.exemplo.com.br" }, links.Select(l => l.Url));
        Assert.All(links, l => Assert.Equal(0, l.PageIndex));

        // Rodar de novo não duplica; remover tira só o link pedido.
        var again = await _navigation.SaveAsync(result.File.Path, Nothing with { LinkUrlsInText = true }, Target);
        Assert.Equal(0, again.LinksCreated);
        var removed = await _navigation.SaveAsync(result.File.Path, Nothing with { RemoveLinks = [(links[0].PageIndex, links[0].AnnotationIndex)] }, Target);
        Assert.Equal(1, removed.LinksRemoved);
        Assert.Equal("https://www.exemplo.com.br", Assert.Single((await _navigation.ReadAsync(removed.File.Path)).Links).Url);
    }

    [Fact]
    public async Task Attachments_are_added_read_and_removed()
    {
        var source = WithText("contrato.pdf", "Contrato");
        var xml = Path.Combine(_temp.Path, "dados.xml");
        File.WriteAllText(xml, "<nota>ok</nota>");
        var photo = Path.Combine(_temp.Path, "matrícula.txt");
        File.WriteAllText(photo, "Matrícula 12.345", Encoding.UTF8);

        var added = await _navigation.SaveAsync(source, Nothing with { AddAttachments = [xml, photo, xml] }, Target);
        var info = await _navigation.ReadAsync(added.File.Path);
        Assert.Equal(new[] { "dados (2).xml", "dados.xml", "matrícula.txt" }, info.Attachments.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.Equal("<nota>ok</nota>", Encoding.UTF8.GetString(DocumentNavigation.ReadAttachment(added.File.Path, "dados.xml")));
        Assert.Equal("Matrícula 12.345", Encoding.UTF8.GetString(DocumentNavigation.ReadAttachment(added.File.Path, "matrícula.txt")));

        var removed = await _navigation.SaveAsync(added.File.Path, Nothing with { RemoveAttachments = ["dados.xml", "dados (2).xml"] }, Target);
        Assert.Equal(2, removed.AttachmentsRemoved);
        Assert.Equal("matrícula.txt", Assert.Single((await _navigation.ReadAsync(removed.File.Path)).Attachments).Name);
        await Assert.ThrowsAsync<PdfEngineException>(() => _navigation.SaveAsync(source, Nothing with { AddAttachments = [Path.Combine(_temp.Path, "nao.txt")] }, Target));
    }

    [Fact]
    public async Task Table_of_contents_page_links_to_the_bookmarked_pages()
    {
        var source = WithText("manual.pdf", "Introdução", "Instalação", "Uso", "Perguntas");
        IReadOnlyList<PdfBookmark> marks = [new("Introdução", 0, 0), new("Instalação", 1, 0), new("Requisitos", 1, 1), new("Uso", 2, 0)];
        var result = await _navigation.SaveAsync(source, Nothing with { Bookmarks = marks, TableOfContents = true }, Target);
        Assert.True(result.TableOfContents);
        Assert.Equal(5, result.File.PageCount);

        var info = await _engine.ReadInfoAsync(result.File.Path);
        Assert.Equal(new PdfBookmark("Sumário", 0, 0), info.Bookmarks[0]);
        Assert.Equal(new PdfBookmark("Uso", 3, 0), info.Bookmarks[^1]);
        var links = (await _navigation.ReadAsync(result.File.Path)).Links;
        Assert.Equal(new int?[] { 1, 2, 2, 3 }, links.Where(l => l.PageIndex == 0).Select(l => l.TargetPage));
        var text = string.Concat(PDFMaster.Rendering.PdfiumRenderer.ReadDisplayText(result.File.Path)[0].Chars.Select(c => c.Value));
        Assert.Contains("Sumário", text);
        Assert.Contains("Requisitos", text);

        await Assert.ThrowsAsync<PdfEngineException>(() => _navigation.SaveAsync(source, Nothing with { TableOfContents = true }, Target));
    }
}
