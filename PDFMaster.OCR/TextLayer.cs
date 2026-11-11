using System.Globalization;
using System.Text;
using PdfSharp.Pdf;

namespace PDFMaster.OCR;

/// <summary>Transformação afim, na convenção do PDF: x' = A·x + C·y + E; y' = B·x + D·y + F.</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Identity => new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Composição: aplica <paramref name="inner"/> primeiro e depois esta.</summary>
    public Affine After(Affine inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);
}

/// <summary>
/// Conversões de coordenadas entre a imagem entregue ao OCR e a página do PDF. O OCR enxerga a
/// página renderizada, possivelmente girada (orientação) e alinhada (inclinação); a camada de
/// texto precisa ser escrita no sistema de coordenadas original da página.
/// </summary>
public static class OcrGeometry
{
    /// <summary>
    /// Imagem alinhada → imagem antes do alinhamento, para <see cref="GrayImage.Rotate"/> com o
    /// mesmo ângulo (a imagem mantém o tamanho).
    /// </summary>
    public static Affine Deskew(double degrees, int width, int height)
    {
        var a = degrees * Math.PI / 180;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var cx = width / 2.0;
        var cy = height / 2.0;
        return new Affine(cos, sin, -sin, cos, cx - cos * cx + sin * cy, cy - sin * cx - cos * cy);
    }

    /// <summary>
    /// Imagem girada com <see cref="GrayImage.Rotate90"/> → imagem renderizada original.
    /// </summary>
    public static Affine Orientation(int clockwiseDegrees, int renderWidth, int renderHeight) =>
        (((clockwiseDegrees % 360) + 360) % 360) switch
        {
            90 => new Affine(0, -1, 1, 0, 0, renderHeight),
            180 => new Affine(-1, 0, 0, -1, renderWidth, renderHeight),
            270 => new Affine(0, 1, -1, 0, renderWidth, 0),
            _ => Affine.Identity,
        };

    /// <summary>
    /// Pixels da página renderizada (como exibida, origem no canto superior esquerdo) → espaço do
    /// usuário do PDF (sem a rotação /Rotate, origem embaixo à esquerda).
    /// </summary>
    /// <param name="pageRotation">Valor de /Rotate da página: 0, 90, 180 ou 270.</param>
    /// <param name="x0">Canto inferior esquerdo da caixa da página.</param>
    /// <param name="boxWidth">Largura da caixa da página, sem rotação, em pontos.</param>
    public static Affine RenderToPage(int pageRotation, double x0, double y0, double boxWidth, double boxHeight,
        int renderWidth, int renderHeight)
    {
        var rotation = ((pageRotation % 360) + 360) % 360;
        var sideways = rotation is 90 or 270;
        var sx = (sideways ? boxHeight : boxWidth) / renderWidth;
        var sy = (sideways ? boxWidth : boxHeight) / renderHeight;
        return rotation switch
        {
            90 => new Affine(0, sx, sy, 0, x0, y0),
            180 => new Affine(-sx, 0, 0, sy, x0 + boxWidth, y0),
            270 => new Affine(0, -sx, -sy, 0, x0 + boxWidth, y0 + boxHeight),
            _ => new Affine(sx, 0, 0, -sy, x0, y0 + boxHeight),
        };
    }
}

/// <summary>
/// Gera a camada de texto invisível de uma página: cada palavra reconhecida é escrita na posição
/// em que aparece na imagem, com o modo de renderização 3 (não pinta nada), para que o texto
/// possa ser pesquisado, selecionado e copiado.
///
/// A fonte usada é a Courier padrão do PDF (não precisa ser incorporada), com codificação
/// WinAnsi: cobre português, inglês, espanhol e as demais línguas da Europa Ocidental.
/// Caracteres fora dessa codificação entram como "?" na camada do PDF.
/// </summary>
public static class PdfTextLayer
{
    public const string FontResourceName = "/PMOcr";
    private const double CourierAdvance = 0.6;

    /// <param name="words">Palavras em coordenadas da imagem do OCR.</param>
    /// <param name="toPage">Imagem do OCR → espaço do usuário do PDF.</param>
    public static byte[] BuildContent(IReadOnlyList<OcrWord> words, Affine toPage)
    {
        var sb = new StringBuilder(words.Count * 64 + 64);
        sb.Append("q BT 3 Tr ").Append(FontResourceName).Append(" 1 Tf\n");
        foreach (var word in words)
        {
            var encoded = Encode(word.Text);
            var widthPx = word.X2 - word.X1;
            var lineHeight = word.LineBottom - word.LineTop;
            if (encoded.Length == 0 || widthPx <= 0 || lineHeight <= 0) continue;

            // A linha de base fica um pouco acima da base da caixa (espaço das letras descendentes).
            var baseline = word.LineBottom - 0.22 * lineHeight;
            var (ox, oy) = toPage.Apply(word.X1, baseline);
            var stretch = widthPx / (encoded.Length * CourierAdvance);
            var size = lineHeight * 0.9;

            // Eixo do texto = direção x da imagem; eixo "para cima" = direção −y da imagem.
            Number(sb, toPage.A * stretch);
            Number(sb, toPage.B * stretch);
            Number(sb, -toPage.C * size);
            Number(sb, -toPage.D * size);
            Number(sb, ox);
            Number(sb, oy);
            sb.Append("Tm <");
            foreach (var value in encoded) sb.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append("20> Tj\n"); // espaço ao final: separa as palavras ao copiar o texto
        }

        sb.Append("ET Q\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Acrescenta a camada à página, isolando o conteúdo original entre q … Q.</summary>
    public static void AddToPage(PdfDocument document, PdfPage page, byte[] layer, PdfDictionary font)
    {
        var contents = page.Contents;
        contents.PrependContent().CreateStream("q\n"u8.ToArray());
        var tail = new byte[3 + layer.Length];
        "\nQ\n"u8.CopyTo(tail);
        layer.CopyTo(tail, 3);
        contents.AppendContent().CreateStream(tail);

        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            resources = new PdfDictionary(document);
            page.Elements["/Resources"] = resources;
        }

        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            fonts = new PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
        }

        fonts.Elements[FontResourceName] = font.Reference!;
    }

    /// <summary>Cria o objeto de fonte compartilhado por todas as páginas do documento.</summary>
    public static PdfDictionary CreateFont(PdfDocument document)
    {
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Courier");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        document.Internals.AddObject(font);
        return font;
    }

    /// <summary>Texto → bytes WinAnsi (Windows-1252).</summary>
    public static byte[] Encode(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var ch in text)
        {
            if (ch < 0x20) continue;
            if (ch < 0x7F || (ch >= 0xA0 && ch <= 0xFF))
            {
                bytes.Add((byte)ch);
                continue;
            }

            bytes.Add((byte)(ch switch
            {
                '€' => 0x80, '‚' => 0x82, 'ƒ' => 0x83, '„' => 0x84, '…' => 0x85, '†' => 0x86, '‡' => 0x87,
                'ˆ' => 0x88, '‰' => 0x89, 'Š' => 0x8A, '‹' => 0x8B, 'Œ' => 0x8C, 'Ž' => 0x8E,
                '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97,
                '˜' => 0x98, '™' => 0x99, 'š' => 0x9A, '›' => 0x9B, 'œ' => 0x9C, 'ž' => 0x9E, 'Ÿ' => 0x9F,
                _ => 0x3F,
            }));
        }

        return bytes.ToArray();
    }

    private static void Number(StringBuilder sb, double value) =>
        sb.Append(value.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ');
}
