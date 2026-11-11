using System.Globalization;

namespace PDFMaster.Editor;

/// <summary>Cor RGB de 8 bits por canal.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Red = new(0xD1, 0x34, 0x38);
    public static readonly Rgb Blue = new(0x00, 0x67, 0xC0);
    public static readonly Rgb Green = new(0x00, 0x8B, 0x50);
    public static readonly Rgb Yellow = new(0xFF, 0xE0, 0x3D);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string? text, out Rgb color)
    {
        color = Black;
        var t = text?.Trim() ?? string.Empty;
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        color = new Rgb((byte)(value >> 16), (byte)(value >> 8 & 0xFF), (byte)(value & 0xFF));
        return true;
    }

    /// <summary>Operador de cor do PDF (preenchimento "rg" ou traço "RG").</summary>
    public string Operator(bool stroke) =>
        $"{Pdf.Number(R / 255.0)} {Pdf.Number(G / 255.0)} {Pdf.Number(B / 255.0)} {(stroke ? "RG" : "rg")}";
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Aparência de um texto inserido.</summary>
/// <param name="LineSpacing">Multiplicador do espaçamento entre linhas (1 = normal).</param>
public sealed record TextStyle(
    string Family = "Arial",
    double Size = 12,
    Rgb Color = default,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    TextAlign Align = TextAlign.Left,
    double LineSpacing = 1.0)
{
    public const double MinSize = 4;
    public const double MaxSize = 200;
}

/// <summary>Recorte de uma imagem em frações do tamanho original (0 = nada cortado).</summary>
public readonly record struct ImageCrop(double Left, double Top, double Right, double Bottom)
{
    public static readonly ImageCrop None = new(0, 0, 0, 0);
    public bool IsNone => Left <= 0 && Top <= 0 && Right <= 0 && Bottom <= 0;
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Ink,
}

public enum MarkupKind
{
    Highlight,
    Underline,
    StrikeOut,
}

public enum NoteKind
{
    /// <summary>Comentário (balão).</summary>
    Comment,

    /// <summary>Nota adesiva.</summary>
    Note,
}

public enum FieldKind
{
    Text,
    MultilineText,
    CheckBox,
    Radio,
    Dropdown,
    Date,
    Button,
}

public enum ButtonAction
{
    /// <summary>Limpa o formulário.</summary>
    Reset,

    /// <summary>Imprime o documento (JavaScript; depende do leitor de PDF).</summary>
    Print,
}

/// <summary>
/// Uma alteração feita no editor. Posições e tamanhos ficam no espaço de exibição da página
/// (pontos, origem no canto superior esquerdo), independentes de zoom e da rotação da página.
/// </summary>
public abstract record EditItem(Guid Id, int PageIndex)
{
    /// <summary>Área ocupada na página exibida (para seleção e desenho na tela).</summary>
    public abstract ViewRect Bounds { get; }

    /// <summary>Descrição curta para o histórico de alterações.</summary>
    public abstract string Kind { get; }

    /// <summary>Cópia deslocada (mover).</summary>
    public abstract EditItem Moved(double dx, double dy);

    /// <summary>Cópia com outro retângulo (redimensionar).</summary>
    public virtual EditItem Resized(ViewRect rect) => Moved(rect.X - Bounds.X, rect.Y - Bounds.Y);

    protected static ViewRect Shift(ViewRect r, double dx, double dy) => r with { X = r.X + dx, Y = r.Y + dy };

    protected static IReadOnlyList<ViewPoint> Shift(IReadOnlyList<ViewPoint> points, double dx, double dy) =>
        points.Select(p => new ViewPoint(p.X + dx, p.Y + dy)).ToList();

    protected static IReadOnlyList<ViewPoint> Scale(IReadOnlyList<ViewPoint> points, ViewRect from, ViewRect to)
    {
        var sx = from.Width > 0 ? to.Width / from.Width : 1;
        var sy = from.Height > 0 ? to.Height / from.Height : 1;
        return points.Select(p => new ViewPoint(to.X + (p.X - from.X) * sx, to.Y + (p.Y - from.Y) * sy)).ToList();
    }

    public static ViewRect BoundsOf(IReadOnlyList<ViewPoint> points)
    {
        if (points.Count == 0) return default;
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        return new ViewRect(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
    }
}

/// <summary>Texto novo gravado no conteúdo da página.</summary>
public sealed record TextItem(Guid Id, int PageIndex, ViewRect Rect, string Text, TextStyle Style) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Rect;
    public override string Kind => "Texto";
    public override EditItem Moved(double dx, double dy) => this with { Rect = Shift(Rect, dx, dy) };
    public override EditItem Resized(ViewRect rect) => this with { Rect = rect };
}

/// <summary>Imagem (ou assinatura visual) gravada no conteúdo da página.</summary>
/// <param name="Data">Bytes do arquivo de imagem original (PNG, JPG, BMP, TIFF ou GIF).</param>
/// <param name="Rotation">Graus no sentido horário, em torno do centro.</param>
/// <param name="Opacity">De 0,05 a 1.</param>
public sealed record ImageItem(
    Guid Id, int PageIndex, ViewRect Rect, byte[] Data, string SourceName,
    double Rotation = 0, double Opacity = 1, ImageCrop Crop = default, bool IsSignature = false) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Rect;
    public override string Kind => IsSignature ? "Assinatura visual" : "Imagem";
    public override EditItem Moved(double dx, double dy) => this with { Rect = Shift(Rect, dx, dy) };
    public override EditItem Resized(ViewRect rect) => this with { Rect = rect };
}

/// <summary>Forma geométrica ou desenho livre (anotação do PDF).</summary>
/// <param name="Points">Linha e seta: início e fim; desenho livre: o traço; retângulo e círculo: não usado.</param>
public sealed record ShapeItem(
    Guid Id, int PageIndex, ShapeKind Shape, ViewRect Rect, IReadOnlyList<ViewPoint> Points,
    Rgb Stroke, double StrokeWidth = 2, Rgb? Fill = null, double Opacity = 1, string? Comment = null) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Shape is ShapeKind.Line or ShapeKind.Arrow or ShapeKind.Ink
        ? BoundsOf(Points).Inflate(StrokeWidth / 2 + 1)
        : Rect;

    public override string Kind => Shape switch
    {
        ShapeKind.Rectangle => "Retângulo",
        ShapeKind.Ellipse => "Círculo",
        ShapeKind.Line => "Linha",
        ShapeKind.Arrow => "Seta",
        _ => "Desenho livre",
    };

    public override EditItem Moved(double dx, double dy) => this with { Rect = Shift(Rect, dx, dy), Points = Shift(Points, dx, dy) };

    public override EditItem Resized(ViewRect rect)
    {
        if (Shape is not (ShapeKind.Line or ShapeKind.Arrow or ShapeKind.Ink)) return this with { Rect = rect };
        // Os pontos ocupam a área sem a margem do traço; assim os novos limites ficam iguais ao retângulo pedido.
        var margin = StrokeWidth / 2 + 1;
        var target = new ViewRect(rect.X + margin, rect.Y + margin, Math.Max(1, rect.Width - 2 * margin), Math.Max(1, rect.Height - 2 * margin));
        return this with { Points = Scale(Points, BoundsOf(Points), target) };
    }
}

/// <summary>Marca-texto, sublinhado ou tachado sobre trechos do texto (anotação do PDF).</summary>
public sealed record MarkupItem(
    Guid Id, int PageIndex, MarkupKind Markup, IReadOnlyList<ViewRect> Quads, Rgb Color, string? Comment = null) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds
    {
        get
        {
            var x = Quads.Min(q => q.X);
            var y = Quads.Min(q => q.Y);
            return new ViewRect(x, y, Quads.Max(q => q.Right) - x, Quads.Max(q => q.Bottom) - y);
        }
    }

    public override string Kind => Markup switch
    {
        MarkupKind.Highlight => "Marca-texto",
        MarkupKind.Underline => "Sublinhado",
        _ => "Tachado",
    };

    public override EditItem Moved(double dx, double dy) => this with { Quads = Quads.Select(q => Shift(q, dx, dy)).ToList() };
}

/// <summary>Comentário ou nota adesiva (ícone na página; o texto aparece ao abrir a nota).</summary>
public sealed record NoteItem(Guid Id, int PageIndex, ViewPoint Position, string Text, NoteKind Note, Rgb Color) : EditItem(Id, PageIndex)
{
    public const double IconSize = 20;
    public override ViewRect Bounds => new(Position.X, Position.Y, IconSize, IconSize);
    public override string Kind => Note == NoteKind.Comment ? "Comentário" : "Nota adesiva";
    public override EditItem Moved(double dx, double dy) => this with { Position = new ViewPoint(Position.X + dx, Position.Y + dy) };
}

/// <summary>Caixa de texto (anotação FreeText, editável em outros leitores de PDF).</summary>
public sealed record TextBoxItem(Guid Id, int PageIndex, ViewRect Rect, string Text, TextStyle Style, Rgb Border, Rgb? Fill) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Rect;
    public override string Kind => "Caixa de texto";
    public override EditItem Moved(double dx, double dy) => this with { Rect = Shift(Rect, dx, dy) };
    public override EditItem Resized(ViewRect rect) => this with { Rect = rect };
}

/// <summary>Carimbo (anotação Stamp) a partir de um modelo da biblioteca.</summary>
public sealed record StampItem(Guid Id, int PageIndex, ViewRect Rect, StampTemplate Template, DateTime When) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Rect;
    public override string Kind => "Carimbo";
    public override EditItem Moved(double dx, double dy) => this with { Rect = Shift(Rect, dx, dy) };
    public override EditItem Resized(ViewRect rect) => this with { Rect = rect };
}

/// <summary>Campo de formulário novo.</summary>
/// <param name="Options">Botões de opção e lista suspensa: as opções.</param>
public sealed record FieldItem(
    Guid Id, int PageIndex, ViewRect Rect, FieldKind Field, string Name,
    IReadOnlyList<string> Options, string? Value = null, ButtonAction Action = ButtonAction.Reset, double FontSize = 0) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Rect;

    public override string Kind => Field switch
    {
        FieldKind.Text => "Campo de texto",
        FieldKind.MultilineText => "Campo de texto (várias linhas)",
        FieldKind.CheckBox => "Caixa de seleção",
        FieldKind.Radio => "Botões de opção",
        FieldKind.Dropdown => "Lista suspensa",
        FieldKind.Date => "Campo de data",
        _ => "Botão",
    };

    public override EditItem Moved(double dx, double dy) => this with { Rect = Shift(Rect, dx, dy) };
    public override EditItem Resized(ViewRect rect) => this with { Rect = rect };

    /// <summary>Retângulos de cada botão de opção (empilhados dentro do retângulo do campo).</summary>
    public IReadOnlyList<ViewRect> RadioButtons()
    {
        var count = Math.Max(1, Options.Count);
        var row = Rect.Height / count;
        var size = Math.Min(14, row * 0.8);
        return Enumerable.Range(0, count)
            .Select(i => new ViewRect(Rect.X, Rect.Y + i * row + (row - size) / 2, size, size))
            .ToList();
    }
}

/// <summary>Como uma edição de texto existente foi aplicada.</summary>
public enum ReplaceMode
{
    /// <summary>O texto foi trocado dentro do próprio PDF, com a mesma fonte.</summary>
    InPlace,

    /// <summary>O texto antigo foi coberto por um retângulo branco e o novo foi escrito por cima.</summary>
    Overlay,
}

/// <summary>Troca de um trecho de texto que já existia no PDF.</summary>
/// <param name="Run">Trecho original (posição e texto lidos do PDF).</param>
/// <param name="Style">Aparência do texto novo quando for preciso sobrepor.</param>
/// <param name="AllowOverlay">Se a troca no próprio PDF não for possível, cobrir e sobrepor.</param>
public sealed record ReplaceTextItem(Guid Id, int PageIndex, TextRun Run, string NewText, TextStyle Style, bool AllowOverlay = true) : EditItem(Id, PageIndex)
{
    public override ViewRect Bounds => Run.Rect;
    public override string Kind => "Texto existente alterado";
    public override EditItem Moved(double dx, double dy) => this;
}
