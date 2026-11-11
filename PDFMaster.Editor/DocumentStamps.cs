using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFMaster.Editor;

public enum MarkPosition
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>Marca d'água de texto ou de imagem.</summary>
/// <param name="ImageWidthPercent">Largura da imagem em % da largura da página.</param>
/// <param name="Pages">Intervalo de páginas ("1-3,5"); vazio = todas.</param>
/// <param name="Behind">Por baixo do conteúdo (some sob áreas opacas, como páginas digitalizadas).</param>
public sealed record WatermarkOptions(
    string? Text,
    byte[]? Image = null,
    TextStyle? Style = null,
    double Opacity = 0.3,
    double Rotation = 45,
    MarkPosition Position = MarkPosition.Center,
    double ImageWidthPercent = 50,
    string? Pages = null,
    bool Behind = false);

/// <summary>Textos de cabeçalho e rodapé. Campos: {arquivo}, {data}, {hora}, {pagina}, {total}.</summary>
/// <param name="EdgeMarginMm">Distância da borda superior/inferior até o texto.</param>
/// <param name="SideMarginMm">Distância das bordas laterais.</param>
/// <param name="FirstNumber">Número exibido na primeira página processada.</param>
public sealed record HeaderFooterOptions(
    string? HeaderLeft = null, string? HeaderCenter = null, string? HeaderRight = null,
    string? FooterLeft = null, string? FooterCenter = null, string? FooterRight = "Página {pagina} de {total}",
    TextStyle? Style = null,
    double EdgeMarginMm = 10,
    double SideMarginMm = 15,
    string? Pages = null,
    int FirstNumber = 1)
{
    public bool IsEmpty => new[] { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }.All(string.IsNullOrWhiteSpace);
}

/// <summary>Marca d'água, cabeçalho e rodapé: aplicados nas páginas escolhidas, no conteúdo da página.</summary>
public static class DocumentStamps
{
    private const double PointsPerMm = 72.0 / 25.4;

    public static readonly TextStyle DefaultWatermarkStyle = new("Arial", 60, new Rgb(0xC0, 0x20, 0x20), Bold: true);
    public static readonly TextStyle DefaultHeaderStyle = new("Arial", 9, new Rgb(0x30, 0x30, 0x30));

    /// <summary>Troca os campos {arquivo}, {data}, {hora}, {pagina} e {total}.</summary>
    public static string Expand(string template, string fileName, int number, int total, DateTime now)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        return template
            .Replace("{arquivo}", fileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{data}", now.ToString("dd/MM/yyyy", pt), StringComparison.OrdinalIgnoreCase)
            .Replace("{hora}", now.ToString("HH:mm", pt), StringComparison.OrdinalIgnoreCase)
            .Replace("{pagina}", number.ToString(pt), StringComparison.OrdinalIgnoreCase)
            .Replace("{página}", number.ToString(pt), StringComparison.OrdinalIgnoreCase)
            .Replace("{total}", total.ToString(pt), StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyWatermark(PdfDocument document, IReadOnlyList<int> pages, WatermarkOptions options)
    {
        var style = options.Style ?? DefaultWatermarkStyle;
        foreach (var index in pages)
        {
            var page = document.Pages[index];
            var frame = PageFrame.Of(page);
            var form = PdfObjects.Draw(document, frame.Width, frame.Height, gfx =>
            {
                double width;
                double height;
                XImage? image = null;
                XFont? font = null;
                if (options.Image is { } data)
                {
                    image = XImage.FromStream(new MemoryStream(ImageTools.Prepare(data, ImageCrop.None, options.Opacity)));
                    width = frame.Width * Math.Clamp(options.ImageWidthPercent, 5, 100) / 100;
                    height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);
                }
                else
                {
                    font = Drawing.Font(style);
                    var size = gfx.MeasureString(options.Text ?? string.Empty, font);
                    width = size.Width;
                    height = size.Height;
                }

                var center = Anchor(frame, options.Position, width, height, options.Rotation);
                var state = gfx.Save();
                gfx.RotateAtTransform(-options.Rotation, center);
                var x = center.X - width / 2;
                var y = center.Y - height / 2;
                if (image is not null)
                {
                    gfx.DrawImage(image, x, y, width, height);
                    image.Dispose();
                }
                else
                {
                    gfx.DrawString(options.Text ?? string.Empty, font!, new XSolidBrush(Drawing.Color(style.Color, options.Opacity)), x, y, XStringFormats.TopLeft);
                }

                gfx.Restore(state);
            });

            PdfObjects.Place(document, page, [(form, frame.FormToPdf(new ViewRect(0, 0, frame.Width, frame.Height)), options.Behind)]);
        }
    }

    /// <summary>Centro da marca, respeitando uma margem e o espaço ocupado depois de girar.</summary>
    private static XPoint Anchor(PageFrame frame, MarkPosition position, double width, double height, double rotation)
    {
        var radians = rotation * Math.PI / 180;
        var halfW = (Math.Abs(width * Math.Cos(radians)) + Math.Abs(height * Math.Sin(radians))) / 2;
        var halfH = (Math.Abs(width * Math.Sin(radians)) + Math.Abs(height * Math.Cos(radians))) / 2;
        const double margin = 36;
        var left = Math.Min(frame.Width / 2, margin + halfW);
        var right = Math.Max(frame.Width / 2, frame.Width - margin - halfW);
        var top = Math.Min(frame.Height / 2, margin + halfH);
        var bottom = Math.Max(frame.Height / 2, frame.Height - margin - halfH);
        var x = position switch
        {
            MarkPosition.TopLeft or MarkPosition.Left or MarkPosition.BottomLeft => left,
            MarkPosition.TopRight or MarkPosition.Right or MarkPosition.BottomRight => right,
            _ => frame.Width / 2,
        };
        var y = position switch
        {
            MarkPosition.TopLeft or MarkPosition.Top or MarkPosition.TopRight => top,
            MarkPosition.BottomLeft or MarkPosition.Bottom or MarkPosition.BottomRight => bottom,
            _ => frame.Height / 2,
        };
        return new XPoint(x, y);
    }

    internal static void ApplyHeaderFooter(PdfDocument document, IReadOnlyList<int> pages, HeaderFooterOptions options, string fileName, DateTime now)
    {
        var style = options.Style ?? DefaultHeaderStyle;
        var total = pages.Count;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = document.Pages[pages[i]];
            var frame = PageFrame.Of(page);
            var number = options.FirstNumber + i;
            var lastNumber = options.FirstNumber + total - 1;
            var form = PdfObjects.Draw(document, frame.Width, frame.Height, gfx =>
            {
                var font = Drawing.Font(style);
                var brush = new XSolidBrush(Drawing.Color(style.Color));
                var side = options.SideMarginMm * PointsPerMm;
                var edge = options.EdgeMarginMm * PointsPerMm;
                var lineHeight = gfx.MeasureString("Ág", font).Height;
                var width = frame.Width - 2 * side;

                void Row(string? left, string? center, string? right, double y)
                {
                    foreach (var (template, format) in new[] { (left, XStringFormats.TopLeft), (center, XStringFormats.TopCenter), (right, XStringFormats.TopRight) })
                    {
                        if (string.IsNullOrWhiteSpace(template)) continue;
                        var text = Expand(template, fileName, number, lastNumber, now);
                        gfx.DrawString(text, font, brush, new XRect(side, y, width, lineHeight), format);
                    }
                }

                Row(options.HeaderLeft, options.HeaderCenter, options.HeaderRight, edge);
                Row(options.FooterLeft, options.FooterCenter, options.FooterRight, frame.Height - edge - lineHeight);
            });

            PdfObjects.Place(document, page, [(form, frame.FormToPdf(new ViewRect(0, 0, frame.Width, frame.Height)), false)]);
        }
    }
}
