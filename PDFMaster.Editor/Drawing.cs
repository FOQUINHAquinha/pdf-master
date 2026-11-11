using PdfSharp.Drawing;
using PDFMaster.PdfEngine;

namespace PDFMaster.Editor;

/// <summary>Desenho com o PDFsharp (texto com fonte incorporada, imagens e formas).</summary>
internal static class Drawing
{
    public static XFont Font(TextStyle style, bool? bold = null)
    {
        WindowsFonts.EnsureInstalled();
        var flags = XFontStyleEx.Regular;
        if (bold ?? style.Bold) flags |= XFontStyleEx.Bold;
        if (style.Italic) flags |= XFontStyleEx.Italic;
        if (style.Underline) flags |= XFontStyleEx.Underline;
        return new XFont(style.Family, Math.Clamp(style.Size, TextStyle.MinSize, TextStyle.MaxSize), flags);
    }

    public static XColor Color(Rgb color, double opacity = 1) =>
        XColor.FromArgb((int)Math.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);

    /// <summary>Altura de uma linha de texto com o espaçamento escolhido.</summary>
    public static double LineHeight(XFont font, TextStyle style) => font.GetHeight() * Math.Clamp(style.LineSpacing, 0.5, 4);

    /// <summary>Quebra o texto em linhas que cabem na largura (palavras longas são cortadas).</summary>
    public static IReadOnlyList<string> Wrap(XGraphics gfx, XFont font, string text, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (gfx.MeasureString(candidate, font).Width <= width || current.Length == 0 && Fits(gfx, font, word, width))
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0) lines.Add(current);
                current = word;
                // Palavra maior que a largura: corta em pedaços.
                while (current.Length > 1 && gfx.MeasureString(current, font).Width > width)
                {
                    var cut = current.Length - 1;
                    while (cut > 1 && gfx.MeasureString(current[..cut], font).Width > width) cut--;
                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    private static bool Fits(XGraphics gfx, XFont font, string word, double width) => gfx.MeasureString(word, font).Width <= width;

    /// <summary>Texto dentro de um retângulo, com quebra de linha e alinhamento.</summary>
    public static void Text(XGraphics gfx, ViewRect rect, string text, TextStyle style, double opacity = 1)
    {
        var font = Font(style);
        var brush = new XSolidBrush(Color(style.Color, opacity));
        var lineHeight = LineHeight(font, style);
        var y = rect.Y;
        foreach (var line in Wrap(gfx, font, text, Math.Max(4, rect.Width)))
        {
            var width = gfx.MeasureString(line, font).Width;
            var x = style.Align switch
            {
                TextAlign.Center => rect.X + (rect.Width - width) / 2,
                TextAlign.Right => rect.Right - width,
                _ => rect.X,
            };
            if (line.Length > 0) gfx.DrawString(line, font, brush, x, y, XStringFormats.TopLeft);
            y += lineHeight;
        }
    }

    /// <summary>Altura necessária para o texto na largura dada.</summary>
    public static double MeasureHeight(string text, TextStyle style, double width)
    {
        using var scratch = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var font = Font(style);
        return Wrap(scratch, font, text, Math.Max(4, width)).Count * LineHeight(font, style);
    }

    public static void Image(XGraphics gfx, ImageItem item)
    {
        var prepared = ImageTools.Prepare(item.Data, item.Crop, item.Opacity);
        using var image = XImage.FromStream(new MemoryStream(prepared));
        var state = gfx.Save();
        if (Math.Abs(item.Rotation % 360) > 0.01)
            gfx.RotateAtTransform(item.Rotation, new XPoint(item.Rect.Center.X, item.Rect.Center.Y));
        gfx.DrawImage(image, item.Rect.X, item.Rect.Y, item.Rect.Width, item.Rect.Height);
        gfx.Restore(state);
    }

    public static XPen Pen(Rgb color, double width, double opacity = 1) => new(Color(color, opacity), Math.Max(0.1, width))
    {
        LineCap = XLineCap.Round,
        LineJoin = XLineJoin.Round,
    };

    /// <summary>Ponta de seta (triângulo) no fim da linha.</summary>
    public static XPoint[] ArrowHead(XPoint from, XPoint to, double strokeWidth)
    {
        var length = Math.Max(8, strokeWidth * 4.5);
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double spread = 0.45;
        return
        [
            to,
            new XPoint(to.X - length * Math.Cos(angle - spread), to.Y - length * Math.Sin(angle - spread)),
            new XPoint(to.X - length * Math.Cos(angle + spread), to.Y - length * Math.Sin(angle + spread)),
        ];
    }
}
