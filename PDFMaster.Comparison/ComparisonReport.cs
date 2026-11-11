using System.Globalization;
using System.Net;
using System.Text;
using PDFMaster.PdfEngine;

namespace PDFMaster.Comparison;

/// <summary>Relatório da comparação em HTML (abre em qualquer navegador, sem dependências externas).</summary>
public sealed class ComparisonReport
{
    public const string OperationName = "Comparar PDFs";

    private readonly OutputPlanner _planner;

    public ComparisonReport(OutputPlanner planner) => _planner = planner;

    public Task<ProducedFile> WriteAsync(ComparisonResult result, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var name = $"{Path.GetFileNameWithoutExtension(result.PathA)}_x_{Path.GetFileNameWithoutExtension(result.PathB)}_comparacao.html";
        var (output, overwrite) = _planner.Resolve(name, target, OperationName, [result.PathA, result.PathB], reserved);
        return Task.Run(() =>
        {
            var html = Html(result, DateTimeOffset.Now);
            ct.ThrowIfCancellationRequested();
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.WriteAllText(temp, html, new UTF8Encoding(false)));
            return new ProducedFile(output, bytes, 0);
        }, ct);
    }

    public static string Html(ComparisonResult r, DateTimeOffset when)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="pt-BR"><head><meta charset="utf-8"><title>Comparação de PDFs</title>
            <style>
            body{font-family:"Segoe UI",Arial,sans-serif;margin:32px;color:#1f2328;max-width:1100px}
            h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid #d0d7de;padding-bottom:4px}
            .muted{color:#59636e;font-size:13px}table{border-collapse:collapse;width:100%;font-size:14px}
            td,th{border:1px solid #d0d7de;padding:6px 8px;text-align:left;vertical-align:top}th{background:#f6f8fa}
            .del{background:#ffebe9;color:#82071e;text-decoration:line-through}.ins{background:#dafbe1;color:#116329}
            .tag{display:inline-block;border-radius:10px;padding:1px 8px;font-size:12px;font-weight:600}
            .t-Added{background:#dafbe1;color:#116329}.t-Removed{background:#ffebe9;color:#82071e}.t-Modified{background:#fff8c5;color:#7d4e00}
            .t-PageInserted{background:#dafbe1;color:#116329}.t-PageRemoved{background:#ffebe9;color:#82071e}.t-Visual,.t-Images{background:#ddf4ff;color:#0550ae}
            .summary td:first-child{width:45%}
            </style></head><body>
            """);
        sb.Append("<h1>Comparação de PDFs</h1>");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"muted\">Gerado pelo PDF Master em {E(when.ToString("dd/MM/yyyy HH:mm", culture))}</div>");
        sb.Append("<h2>Documentos</h2><table class=\"summary\">");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><th>Primeiro (original)</th><td>{E(r.PathA)}<br><span class=\"muted\">{r.SizesA.Count} página(s)</span></td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><th>Segundo (novo)</th><td>{E(r.PathB)}<br><span class=\"muted\">{r.SizesB.Count} página(s)</span></td></tr>");
        sb.Append("</table>");

        sb.Append("<h2>Resumo</h2><table class=\"summary\">");
        void Row(string label, int count) => sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(label)}</td><td>{count}</td></tr>");
        Row("Trechos de texto acrescentados", r.Count(ChangeKind.Added));
        Row("Trechos de texto removidos", r.Count(ChangeKind.Removed));
        Row("Trechos de texto alterados", r.Count(ChangeKind.Modified));
        Row("Páginas inseridas", r.Count(ChangeKind.PageInserted));
        Row("Páginas removidas", r.Count(ChangeKind.PageRemoved));
        Row("Diferenças de imagens", r.Count(ChangeKind.Images));
        Row("Diferenças visuais", r.Count(ChangeKind.Visual));
        sb.Append("</table>");
        if (r.Identical) sb.Append("<p><strong>Nenhuma diferença encontrada.</strong></p>");
        if (!r.TextAvailable)
            sb.Append("<p class=\"muted\">Os documentos não têm texto pesquisável (provavelmente digitalizados): a comparação foi feita pela aparência das páginas. Use o OCR antes para comparar o texto.</p>");

        sb.Append("<h2>Páginas</h2><table><tr><th>Primeiro</th><th>Segundo</th><th>Texto em comum</th><th>Pixels diferentes</th></tr>");
        foreach (var pair in r.Pairs)
        {
            var a = pair.PageA is { } pa ? (pa + 1).ToString(culture) : "—";
            var b = pair.PageB is { } pb ? (pb + 1).ToString(culture) : "—";
            var similarity = pair.PageA is not null && pair.PageB is not null ? pair.Similarity.ToString("P0", culture) : "—";
            var visual = pair.VisualDifference is { } v ? v.ToString("P1", culture) : "—";
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{a}</td><td>{b}</td><td>{similarity}</td><td>{visual}{(pair.SizeChanged ? " (tamanho mudou)" : string.Empty)}</td></tr>");
        }

        sb.Append("</table>");

        if (!r.Identical)
        {
            sb.Append("<h2>Diferenças</h2><table><tr><th>#</th><th>Tipo</th><th>Páginas</th><th>Detalhe</th></tr>");
            foreach (var c in r.Changes)
            {
                var pages = $"{(c.PageA is { } x ? (x + 1).ToString(culture) : "—")} → {(c.PageB is { } y ? (y + 1).ToString(culture) : "—")}";
                string detail = c.Kind switch
                {
                    ChangeKind.Added => $"<span class=\"ins\">{E(c.NewText)}</span>",
                    ChangeKind.Removed => $"<span class=\"del\">{E(c.OldText)}</span>",
                    ChangeKind.Modified => $"<span class=\"del\">{E(c.OldText)}</span> → <span class=\"ins\">{E(c.NewText)}</span>",
                    ChangeKind.PageInserted => E(c.Description) + (c.NewText.Length > 0 ? $"<br><span class=\"muted\">{E(c.NewText)}</span>" : string.Empty),
                    ChangeKind.PageRemoved => E(c.Description) + (c.OldText.Length > 0 ? $"<br><span class=\"muted\">{E(c.OldText)}</span>" : string.Empty),
                    _ => E(c.Description),
                };
                sb.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{c.Index + 1}</td><td><span class=\"tag t-{c.Kind}\">{E(KindName(c.Kind))}</span></td><td>{pages}</td><td>{detail}</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("<p class=\"muted\">O texto é comparado palavra por palavra na ordem de leitura; mudanças só de formatação aparecem como diferenças visuais. As imagens são comparadas pelo conteúdo do arquivo de cada imagem.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Acrescentado",
        ChangeKind.Removed => "Removido",
        ChangeKind.Modified => "Alterado",
        ChangeKind.PageInserted => "Página inserida",
        ChangeKind.PageRemoved => "Página removida",
        ChangeKind.Images => "Imagens",
        _ => "Visual",
    };
}
