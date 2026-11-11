using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFMaster.Editor;

/// <summary>
/// Anotações do PDF (marca-texto, formas, notas, caixas de texto, carimbos). Cada uma recebe uma
/// aparência própria (/AP), para ficar igual em qualquer leitor de PDF, e continua sendo uma
/// anotação: outros programas conseguem selecionar, mover ou excluir.
/// </summary>
internal static class Annotations
{
    public static void Add(PdfDocument document, PdfPage page, PageFrame frame, EditItem item)
    {
        switch (item)
        {
            case MarkupItem markup:
                Markup(document, page, frame, markup);
                break;
            case ShapeItem shape:
                Shape(document, page, frame, shape);
                break;
            case NoteItem note:
                Note(document, page, frame, note);
                break;
            case TextBoxItem box:
                TextBox(document, page, frame, box);
                break;
            case StampItem stamp:
                Stamp(document, page, frame, stamp);
                break;
        }
    }

    /// <summary>Cria a anotação com a aparência desenhada sobre <paramref name="rect"/> (coordenadas locais).</summary>
    private static PdfDictionary Create(PdfDocument document, PdfPage page, PageFrame frame, string subtype, ViewRect rect, Action<XGraphics> draw)
    {
        var annotation = PdfObjects.Annotation(document, page, subtype, frame.PdfRect(rect));
        var appearance = PdfObjects.Draw(document, rect.Width, rect.Height, draw);
        // Em páginas giradas, a aparência gira junto para ficar "em pé" na página exibida.
        appearance.Elements["/Matrix"] = PdfObjects.Matrix(document, frame.FormToPdf(rect).Linear);
        PdfObjects.SetAppearance(annotation, appearance);
        return annotation;
    }

    private static XPoint Local(ViewRect rect, ViewPoint p) => new(p.X - rect.X, p.Y - rect.Y);

    private static XRect Local(ViewRect rect, ViewRect r) => new(r.X - rect.X, r.Y - rect.Y, r.Width, r.Height);

    private static void Markup(PdfDocument document, PdfPage page, PageFrame frame, MarkupItem item)
    {
        var bounds = item.Bounds;
        var subtype = item.Markup switch
        {
            MarkupKind.Highlight => "/Highlight",
            MarkupKind.Underline => "/Underline",
            _ => "/StrikeOut",
        };

        var annotation = Create(document, page, frame, subtype, bounds, gfx =>
        {
            foreach (var quad in item.Quads)
            {
                var r = Local(bounds, quad);
                switch (item.Markup)
                {
                    case MarkupKind.Highlight:
                        gfx.DrawRectangle(new XSolidBrush(Drawing.Color(item.Color, 0.4)), r);
                        break;
                    case MarkupKind.Underline:
                        var thickness = Math.Max(0.8, r.Height * 0.07);
                        gfx.DrawLine(Drawing.Pen(item.Color, thickness), r.Left, r.Bottom - thickness / 2, r.Right, r.Bottom - thickness / 2);
                        break;
                    default:
                        gfx.DrawLine(Drawing.Pen(item.Color, Math.Max(0.8, r.Height * 0.07)), r.Left, r.Top + r.Height * 0.55, r.Right, r.Top + r.Height * 0.55);
                        break;
                }
            }
        });

        var quads = new List<double>();
        foreach (var q in item.Quads)
        {
            // Ordem usada pelos leitores de PDF: superior esquerdo, superior direito, inferior esquerdo, inferior direito.
            foreach (var corner in new[] { new ViewPoint(q.X, q.Y), new ViewPoint(q.Right, q.Y), new ViewPoint(q.X, q.Bottom), new ViewPoint(q.Right, q.Bottom) })
            {
                var (x, y) = frame.ToPdf(corner);
                quads.Add(x);
                quads.Add(y);
            }
        }

        annotation.Elements["/QuadPoints"] = PdfObjects.Numbers(document, quads.ToArray());
        annotation.Elements["/C"] = PdfObjects.Color(document, item.Color);
        if (!string.IsNullOrWhiteSpace(item.Comment)) annotation.Elements["/Contents"] = PdfObjects.Text(item.Comment);
    }

    private static void Shape(PdfDocument document, PdfPage page, PageFrame frame, ShapeItem item)
    {
        var bounds = item.Bounds;
        var pen = Drawing.Pen(item.Stroke, item.StrokeWidth, item.Opacity);
        var subtype = item.Shape switch
        {
            ShapeKind.Rectangle => "/Square",
            ShapeKind.Ellipse => "/Circle",
            ShapeKind.Ink => "/Ink",
            _ => "/Line",
        };

        var annotation = Create(document, page, frame, subtype, bounds, gfx =>
        {
            var half = item.StrokeWidth / 2;
            var inner = new XRect(half, half, Math.Max(1, bounds.Width - item.StrokeWidth), Math.Max(1, bounds.Height - item.StrokeWidth));
            XBrush? fill = item.Fill is { } f ? new XSolidBrush(Drawing.Color(f, item.Opacity)) : null;
            switch (item.Shape)
            {
                case ShapeKind.Rectangle:
                    if (fill is null) gfx.DrawRectangle(pen, inner);
                    else gfx.DrawRectangle(pen, fill, inner);
                    break;
                case ShapeKind.Ellipse:
                    if (fill is null) gfx.DrawEllipse(pen, inner);
                    else gfx.DrawEllipse(pen, fill, inner);
                    break;
                case ShapeKind.Ink:
                    var points = item.Points.Select(p => Local(bounds, p)).ToArray();
                    if (points.Length == 1) gfx.DrawEllipse(new XSolidBrush(pen.Color), points[0].X - half, points[0].Y - half, item.StrokeWidth, item.StrokeWidth);
                    else if (points.Length > 1) gfx.DrawLines(pen, points);
                    break;
                default:
                    var a = Local(bounds, item.Points[0]);
                    var b = Local(bounds, item.Points[^1]);
                    gfx.DrawLine(pen, a, b);
                    if (item.Shape == ShapeKind.Arrow)
                        gfx.DrawPolygon(pen, new XSolidBrush(pen.Color), Drawing.ArrowHead(a, b, item.StrokeWidth), XFillMode.Winding);
                    break;
            }
        });

        annotation.Elements["/C"] = PdfObjects.Color(document, item.Stroke);
        var border = new PdfDictionary(document);
        border.Elements["/W"] = new PdfReal(item.StrokeWidth);
        annotation.Elements["/BS"] = border;
        if (item.Opacity < 0.999) annotation.Elements["/CA"] = new PdfReal(Math.Round(item.Opacity, 3));
        if (item.Fill is { } interior && item.Shape is ShapeKind.Rectangle or ShapeKind.Ellipse)
            annotation.Elements["/IC"] = PdfObjects.Color(document, interior);
        if (!string.IsNullOrWhiteSpace(item.Comment)) annotation.Elements["/Contents"] = PdfObjects.Text(item.Comment);

        if (item.Shape is ShapeKind.Line or ShapeKind.Arrow)
        {
            var (x1, y1) = frame.ToPdf(item.Points[0]);
            var (x2, y2) = frame.ToPdf(item.Points[^1]);
            annotation.Elements["/L"] = PdfObjects.Numbers(document, x1, y1, x2, y2);
            if (item.Shape == ShapeKind.Arrow)
                annotation.Elements["/LE"] = new PdfArray(document, new PdfName("/None"), new PdfName("/ClosedArrow"));
        }
        else if (item.Shape == ShapeKind.Ink)
        {
            var path = new List<double>();
            foreach (var point in item.Points)
            {
                var (x, y) = frame.ToPdf(point);
                path.Add(x);
                path.Add(y);
            }

            annotation.Elements["/InkList"] = new PdfArray(document, PdfObjects.Numbers(document, path.ToArray()));
        }
    }

    private static void Note(PdfDocument document, PdfPage page, PageFrame frame, NoteItem item)
    {
        var rect = item.Bounds;
        var annotation = Create(document, page, frame, "/Text", rect, gfx =>
        {
            var size = NoteItem.IconSize;
            var body = new XRect(1, 1, size - 2, size - 2);
            var outline = Drawing.Pen(new Rgb(0x40, 0x40, 0x40), 0.8);
            if (item.Note == NoteKind.Comment)
            {
                // Balão de fala.
                gfx.DrawRoundedRectangle(outline, new XSolidBrush(Drawing.Color(item.Color)), new XRect(1, 1, size - 2, size - 6), new XSize(5, 5));
                gfx.DrawPolygon(outline, new XSolidBrush(Drawing.Color(item.Color)), [new XPoint(5, size - 6), new XPoint(5, size - 1), new XPoint(10, size - 6)], XFillMode.Winding);
            }
            else
            {
                gfx.DrawRectangle(outline, new XSolidBrush(Drawing.Color(item.Color)), body);
            }

            var line = Drawing.Pen(new Rgb(0x40, 0x40, 0x40), 1);
            for (var y = 6.0; y <= (item.Note == NoteKind.Comment ? 11 : 15); y += 4.5) gfx.DrawLine(line, 5, y, size - 5, y);
        });

        annotation.Elements["/Contents"] = PdfObjects.Text(item.Text);
        annotation.Elements["/Name"] = new PdfName(item.Note == NoteKind.Comment ? "/Comment" : "/Note");
        annotation.Elements["/Open"] = new PdfBoolean(false);
        annotation.Elements["/C"] = PdfObjects.Color(document, item.Color);
        // Ícone: não muda de tamanho com o zoom nem gira (como nos leitores de PDF).
        annotation.Elements["/F"] = new PdfInteger(4 | 8 | 16);
    }

    private static void TextBox(PdfDocument document, PdfPage page, PageFrame frame, TextBoxItem item)
    {
        var rect = item.Rect;
        var annotation = Create(document, page, frame, "/FreeText", rect, gfx =>
        {
            var box = new XRect(0.5, 0.5, rect.Width - 1, rect.Height - 1);
            if (item.Fill is { } fill) gfx.DrawRectangle(Drawing.Pen(item.Border, 1), new XSolidBrush(Drawing.Color(fill)), box);
            else gfx.DrawRectangle(Drawing.Pen(item.Border, 1), box);
            Drawing.Text(gfx, new ViewRect(4, 3, rect.Width - 8, rect.Height - 6), item.Text, item.Style);
        });

        annotation.Elements["/Contents"] = PdfObjects.Text(item.Text);
        var c = item.Style.Color;
        annotation.Elements["/DA"] = new PdfString(string.Create(CultureInfo.InvariantCulture,
            $"/Helv {Pdf.Number(item.Style.Size)} Tf {c.Operator(stroke: false)}"));
        annotation.Elements["/Q"] = new PdfInteger(item.Style.Align switch { TextAlign.Center => 1, TextAlign.Right => 2, _ => 0 });
        annotation.Elements["/C"] = item.Fill is { } bg ? PdfObjects.Color(document, bg) : new PdfArray(document);
    }

    private static void Stamp(PdfDocument document, PdfPage page, PageFrame frame, StampItem item)
    {
        var rect = item.Rect;
        var template = item.Template;
        var date = template.ShowDate ? item.When.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) : null;
        var subtitle = string.Join(" · ", new[] { template.Subtitle, date }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var annotation = Create(document, page, frame, "/Stamp", rect, gfx => DrawStamp(gfx, rect.Width, rect.Height, template, subtitle));
        annotation.Elements["/Name"] = new PdfName("/PDFMasterCarimbo");
        annotation.Elements["/Contents"] = PdfObjects.Text(subtitle.Length > 0 ? $"{template.Text} — {subtitle}" : template.Text);
        annotation.Elements["/C"] = PdfObjects.Color(document, template.Color);
    }

    /// <summary>Carimbo: moldura arredondada, texto em negrito ajustado à largura e linha opcional (data).</summary>
    public static void DrawStamp(XGraphics gfx, double width, double height, StampTemplate template, string subtitle)
    {
        var border = Math.Clamp(Math.Min(width, height) * 0.06, 1.5, 4);
        var color = Drawing.Color(template.Color, 0.9);
        gfx.DrawRoundedRectangle(new XPen(color, border), new XRect(border / 2, border / 2, width - border, height - border),
            new XSize(height * 0.25, height * 0.25));

        var hasSubtitle = subtitle.Length > 0;
        var textArea = height * (hasSubtitle ? 0.58 : 0.8);
        var brush = new XSolidBrush(color);
        var style = new TextStyle("Arial", 10, template.Color, Bold: true);
        var font = Drawing.Font(style);
        var measured = gfx.MeasureString(template.Text, font);
        var scale = Math.Min((width - border * 4) / Math.Max(1, measured.Width), textArea / Math.Max(1, measured.Height));
        font = Drawing.Font(style with { Size = Math.Clamp(10 * scale, TextStyle.MinSize, TextStyle.MaxSize) });
        var top = hasSubtitle ? height * 0.1 : (height - gfx.MeasureString(template.Text, font).Height) / 2;
        gfx.DrawString(template.Text, font, brush, new XRect(0, top, width, textArea), XStringFormats.TopCenter);

        if (hasSubtitle)
        {
            var small = Drawing.Font(style with { Bold = false, Size = Math.Clamp(height * 0.18, TextStyle.MinSize, 40) });
            gfx.DrawString(subtitle, small, brush, new XRect(0, height * 0.66, width, height * 0.3), XStringFormats.TopCenter);
        }
    }
}
