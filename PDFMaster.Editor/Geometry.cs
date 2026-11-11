using System.Globalization;
using PdfSharp.Pdf;

namespace PDFMaster.Editor;

/// <summary>Ponto no espaço de exibição da página: em pontos, origem no canto superior esquerdo, y para baixo.</summary>
public readonly record struct ViewPoint(double X, double Y);

/// <summary>Retângulo no espaço de exibição da página (pontos, origem no canto superior esquerdo).</summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public ViewPoint Center => new(X + Width / 2, Y + Height / 2);

    /// <summary>Retângulo normalizado a partir de dois cantos quaisquer (arrasto do mouse).</summary>
    public static ViewRect FromCorners(ViewPoint a, ViewPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public bool Contains(ViewPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public ViewRect Inflate(double amount) => new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);
}

/// <summary>Transformação afim no formato do PDF: x' = a·x + c·y + e; y' = b·x + d·y + f.</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Primeiro aplica <paramref name="inner"/>, depois esta.</summary>
    public Affine After(Affine inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);

    /// <summary>Só a parte linear (sem deslocamento).</summary>
    public Affine Linear => this with { E = 0, F = 0 };

    public Affine Inverse()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Transformação sem inversa.");
        var a = D / det;
        var b = -B / det;
        var c = -C / det;
        var d = A / det;
        return new Affine(a, b, c, d, -(a * E + c * F), -(b * E + d * F));
    }

    /// <summary>Operador "cm" do PDF.</summary>
    public string ToPdfOperator() =>
        string.Join(' ', new[] { A, B, C, D, E, F }.Select(Pdf.Number)) + " cm";
}

/// <summary>
/// Relação entre o que a pessoa vê (a página exibida, já girada e recortada pela CropBox) e o
/// espaço de usuário do PDF. Todas as edições são guardadas no espaço de exibição e convertidas
/// na hora de gravar, para que funcionem também em páginas giradas.
/// </summary>
public sealed class PageFrame
{
    public PageFrame(double x0, double y0, double x1, double y1, int rotation)
    {
        X0 = Math.Min(x0, x1);
        Y0 = Math.Min(y0, y1);
        X1 = Math.Max(x0, x1);
        Y1 = Math.Max(y0, y1);
        Rotation = ((rotation % 360) + 360) % 360 / 90 * 90;
        var w = X1 - X0;
        var h = Y1 - Y0;
        var turned = Rotation is 90 or 270;
        Width = turned ? h : w;
        Height = turned ? w : h;
        ViewToPdf = Rotation switch
        {
            90 => new Affine(0, 1, 1, 0, X0, Y0),
            180 => new Affine(-1, 0, 0, 1, X1, Y0),
            270 => new Affine(0, -1, -1, 0, X1, Y1),
            _ => new Affine(1, 0, 0, -1, X0, Y1),
        };
        PdfToView = ViewToPdf.Inverse();
    }

    public double X0 { get; }
    public double Y0 { get; }
    public double X1 { get; }
    public double Y1 { get; }

    /// <summary>Rotação de exibição (/Rotate) normalizada: 0, 90, 180 ou 270.</summary>
    public int Rotation { get; }

    /// <summary>Largura exibida, em pontos.</summary>
    public double Width { get; }

    /// <summary>Altura exibida, em pontos.</summary>
    public double Height { get; }

    public Affine ViewToPdf { get; }
    public Affine PdfToView { get; }

    /// <summary>Tamanho exibido (pontos) de cada página do arquivo.</summary>
    public static IReadOnlyList<(double Width, double Height)> DisplaySizes(string path)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var sizes = new List<(double, double)>();
        for (var i = 0; i < document.PageCount; i++)
        {
            var frame = Of(document.Pages[i]);
            sizes.Add((frame.Width, frame.Height));
        }

        return sizes;
    }

    public static PageFrame Of(PdfPage page)
    {
        var box = Box(page, "/CropBox") ?? Box(page, "/MediaBox") ?? new[] { 0.0, 0, 612, 792 };
        // A área exibida é a CropBox limitada pela MediaBox.
        if (Box(page, "/MediaBox") is { } media)
        {
            box = [Math.Max(Math.Min(box[0], box[2]), Math.Min(media[0], media[2])),
                   Math.Max(Math.Min(box[1], box[3]), Math.Min(media[1], media[3])),
                   Math.Min(Math.Max(box[0], box[2]), Math.Max(media[0], media[2])),
                   Math.Min(Math.Max(box[1], box[3]), Math.Max(media[1], media[3]))];
        }

        return new PageFrame(box[0], box[1], box[2], box[3], page.Elements.GetInteger("/Rotate"));
    }

    private static double[]? Box(PdfPage page, string key)
    {
        var array = page.Elements.GetArray(key);
        if (array is null || array.Elements.Count != 4) return null;
        var values = new double[4];
        for (var i = 0; i < 4; i++) values[i] = array.Elements.GetReal(i);
        return values[2] - values[0] == 0 || values[3] - values[1] == 0 ? null : values;
    }

    /// <summary>
    /// Mapeia o espaço de um formulário (XObject) desenhado pelo PDFsharp com o tamanho de
    /// <paramref name="rect"/> (origem embaixo, y para cima) para o espaço de usuário do PDF,
    /// de modo que ele apareça exatamente sobre <paramref name="rect"/> na página exibida.
    /// </summary>
    public Affine FormToPdf(ViewRect rect) =>
        ViewToPdf.After(new Affine(1, 0, 0, -1, rect.X, rect.Y + rect.Height));

    /// <summary>Retângulo do PDF (x1 y1 x2 y2) que envolve o retângulo exibido.</summary>
    public double[] PdfRect(ViewRect rect)
    {
        var corners = new[]
        {
            ViewToPdf.Apply(rect.X, rect.Y), ViewToPdf.Apply(rect.Right, rect.Y),
            ViewToPdf.Apply(rect.X, rect.Bottom), ViewToPdf.Apply(rect.Right, rect.Bottom),
        };
        return [corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y)];
    }

    public (double X, double Y) ToPdf(ViewPoint p) => ViewToPdf.Apply(p.X, p.Y);

    public ViewPoint ToView(double x, double y)
    {
        var (vx, vy) = PdfToView.Apply(x, y);
        return new ViewPoint(vx, vy);
    }
}

/// <summary>Formatação de números e textos no formato do PDF.</summary>
public static class Pdf
{
    public static string Number(double value)
    {
        if (Math.Abs(value) < 1e-9) return "0";
        var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Codifica texto em WinAnsi (fontes padrão do PDF); caracteres fora dela viram "?".</summary>
    public static byte[] WinAnsi(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++) bytes[i] = WinAnsiByte(text[i]) ?? (byte)'?';
        return bytes;
    }

    public static byte? WinAnsiByte(char c)
    {
        if (c is >= ' ' and <= '~') return (byte)c;
        if (c is >= ' ' and <= 'ÿ') return (byte)c;
        var index = Array.IndexOf(WinAnsiHigh, c);
        return index >= 0 ? (byte)(0x80 + index) : null;
    }

    /// <summary>Caractere Unicode do código WinAnsi (null para códigos sem caractere).</summary>
    public static char? FromWinAnsi(byte code)
    {
        if (code is >= 0x20 and <= 0x7E) return (char)code;
        if (code >= 0xA0) return (char)code;
        if (code is >= 0x80 and <= 0x9F)
        {
            var c = WinAnsiHigh[code - 0x80];
            return c == '\0' ? null : c;
        }

        return null;
    }

    // Códigos 0x80–0x9F da WinAnsiEncoding (Windows-1252).
    private static readonly char[] WinAnsiHigh =
    [
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    ];

    /// <summary>String literal do PDF com os bytes dados: (…) com escapes.</summary>
    public static string Literal(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length + 2);
        sb.Append('(');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    sb.Append('\\').Append((char)b);
                    break;
                case < 0x20 or > 0x7E:
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    sb.Append((char)b);
                    break;
            }
        }

        return sb.Append(')').ToString();
    }

    /// <summary>Data no formato do PDF: D:AAAAMMDDHHmmSS-03'00'.</summary>
    public static string Date(DateTimeOffset when)
    {
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return $"D:{when:yyyyMMddHHmmss}{sign}{abs.Hours:00}'{abs.Minutes:00}'";
    }
}
