using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.App.ViewModels;
using PDFMaster.Editor;

namespace PDFMaster.App.Views;

/// <summary>
/// Camada sobre a página renderizada: desenha uma prévia de cada edição (no lugar exato em que será
/// gravada) e converte os gestos do mouse em itens do editor. Coordenadas: pontos da página exibida.
/// </summary>
public sealed class EditorSurface : FrameworkElement
{
    private const double HandleSize = 9;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly Dictionary<(byte[] Data, ImageCrop Crop), BitmapSource?> _images = new();
    private EditorViewModel? _vm;
    private Mode _mode;
    private Point _start;
    private Point _current;
    private readonly List<ViewPoint> _ink = [];

    private enum Mode
    {
        None,
        Move,
        Resize,
        Drag,
        Ink,
        Click,
    }

    public EditorSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = e.NewValue as EditorViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        InvalidateVisual();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.PageItems) or nameof(EditorViewModel.SelectedItem) or nameof(EditorViewModel.Scale)
            or nameof(EditorViewModel.PageRuns) or nameof(EditorViewModel.SelectedTool) or nameof(EditorViewModel.SelectedPage))
        {
            InvalidateVisual();
            Cursor = _vm?.Gesture == EditorGesture.Select ? Cursors.Arrow : Cursors.Cross;
        }
    }

    private double Scale => _vm?.Scale ?? 1;

    private ViewPoint ToView(Point p) => new(p.X / Scale, p.Y / Scale);

    private Rect ToScreen(ViewRect r) => new(r.X * Scale, r.Y * Scale, Math.Max(0, r.Width * Scale), Math.Max(0, r.Height * Scale));

    private Point ToScreen(ViewPoint p) => new(p.X * Scale, p.Y * Scale);

    private Rect HandleRect(ViewRect bounds)
    {
        var r = ToScreen(bounds);
        return new Rect(r.Right - HandleSize / 2, r.Bottom - HandleSize / 2, HandleSize, HandleSize);
    }

    // ---------- Mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_vm is null || !_vm.HasDocument || _vm.IsBusy) return;
        Focus();
        _start = _current = e.GetPosition(this);
        var point = ToView(_start);
        switch (_vm.Gesture)
        {
            case EditorGesture.Select:
                if (_vm.SelectedItem is { } selected && HandleRect(selected.Bounds).Contains(_start) && selected is not (NoteItem or MarkupItem or ReplaceTextItem))
                {
                    _mode = Mode.Resize;
                }
                else
                {
                    var hit = _vm.HitTest(point);
                    _vm.Select(hit);
                    _mode = hit is null ? Mode.None : Mode.Move;
                }

                break;
            case EditorGesture.Click:
                _mode = Mode.Click;
                break;
            case EditorGesture.Ink:
                _mode = Mode.Ink;
                _ink.Clear();
                _ink.Add(point);
                break;
            default:
                _mode = Mode.Drag;
                break;
        }

        if (_mode != Mode.None) CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_vm is null) return;
        var position = e.GetPosition(this);
        if (_mode == Mode.None)
        {
            if (_vm.Gesture == EditorGesture.Select)
            {
                var hover = _vm.SelectedItem is { } s && HandleRect(s.Bounds).Contains(position) ? Cursors.SizeNWSE
                    : _vm.HitTest(ToView(position)) is not null ? Cursors.SizeAll : Cursors.Arrow;
                Cursor = hover;
            }
            else if (_vm.SelectedTool.Tool == EditorTool.EditText)
            {
                InvalidateVisual();
                _current = position;
            }

            return;
        }

        _current = position;
        if (_mode == Mode.Ink)
        {
            var point = ToView(position);
            var last = _ink[^1];
            if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) > 1.2) _ink.Add(point);
        }

        InvalidateVisual();
    }

    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_vm is null || _mode == Mode.None) return;
        var mode = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();
        _current = e.GetPosition(this);
        var start = ToView(_start);
        var end = ToView(_current);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var moved = Math.Abs(_current.X - _start.X) > 3 || Math.Abs(_current.Y - _start.Y) > 3;

        switch (mode)
        {
            case Mode.Move when moved:
                _vm.MoveSelected(dx, dy);
                break;
            case Mode.Resize when _vm.SelectedItem is { } item:
                var b = item.Bounds;
                _vm.ResizeSelected(new ViewRect(b.X, b.Y, Math.Max(6, b.Width + dx), Math.Max(6, b.Height + dy)));
                break;
            case Mode.Click:
                await _vm.ClickAsync(end);
                break;
            case Mode.Drag when !moved:
                await _vm.ClickAsync(start);
                break;
            case Mode.Drag when _vm.Gesture == EditorGesture.Line:
                _vm.CreateFromPoints([start, end]);
                break;
            case Mode.Drag:
                await _vm.CreateInRectAsync(ViewRect.FromCorners(start, end));
                break;
            case Mode.Ink:
                if (_ink.Count == 1) _ink.Add(new ViewPoint(_ink[0].X + 0.5, _ink[0].Y + 0.5));
                _vm.CreateFromPoints(_ink.ToList());
                _ink.Clear();
                break;
        }

        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mode != Mode.None && Mouse.LeftButton == MouseButtonState.Released) return;
        _mode = Mode.None;
        InvalidateVisual();
    }

    // ---------- Desenho ----------

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_vm is null || !_vm.HasDocument) return;

        var accent = (TryFindResource("Brush.Accent") as Brush) ?? Brushes.SeaGreen;

        if (_vm.SelectedTool.Tool == EditorTool.EditText)
        {
            var dashed = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 103, 192)), 1) { DashStyle = DashStyles.Dash };
            var hover = TextRuns.At(_vm.PageRuns, ToView(_current));
            foreach (var run in _vm.PageRuns)
            {
                dc.DrawRectangle(run == hover ? new SolidColorBrush(Color.FromArgb(40, 0, 103, 192)) : null, dashed, ToScreen(run.Rect.Inflate(1)));
            }
        }

        foreach (var item in _vm.PageItems)
        {
            var preview = item;
            if (_mode == Mode.Move && _vm.SelectedItem?.Id == item.Id)
                preview = item.Moved((_current.X - _start.X) / Scale, (_current.Y - _start.Y) / Scale);
            else if (_mode == Mode.Resize && _vm.SelectedItem?.Id == item.Id)
            {
                var b = item.Bounds;
                preview = item.Resized(new ViewRect(b.X, b.Y, Math.Max(6, b.Width + (_current.X - _start.X) / Scale), Math.Max(6, b.Height + (_current.Y - _start.Y) / Scale)));
            }

            Draw(dc, preview);
        }

        if (_vm.SelectedItem is { } selected && selected.PageIndex == (_vm.SelectedPage?.Index ?? -1))
        {
            var bounds = _mode == Mode.Move ? selected.Moved((_current.X - _start.X) / Scale, (_current.Y - _start.Y) / Scale).Bounds : selected.Bounds;
            dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = DashStyles.Dash }, ToScreen(bounds.Inflate(2)));
            if (selected is not (NoteItem or MarkupItem or ReplaceTextItem) && _mode != Mode.Move)
                dc.DrawRectangle(Brushes.White, new Pen(accent, 1.5), HandleRect(selected.Bounds));
        }

        // Prévia do gesto em andamento.
        var previewPen = new Pen(accent, 1.5) { DashStyle = DashStyles.Dash };
        if (_mode == Mode.Drag)
        {
            if (_vm.Gesture == EditorGesture.Line) dc.DrawLine(new Pen(accent, 2), _start, _current);
            else dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 139, 80)), previewPen, new Rect(_start, _current));
        }
        else if (_mode == Mode.Ink && _ink.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToScreen(_ink[0]), false, false);
                g.PolyLineTo(_ink.Skip(1).Select(ToScreen).ToList(), true, true);
            }

            dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    private static Brush Solid(Rgb c, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private FormattedText Text(string text, TextStyle style, double maxWidth, Brush? brush = null)
    {
        var typeface = new Typeface(new FontFamily(style.Family), style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text.Length == 0 ? " " : text, PtBr, FlowDirection.LeftToRight, typeface,
            Math.Max(1, style.Size * Scale), brush ?? Solid(style.Color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(4, maxWidth),
            TextAlignment = style.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            LineHeight = style.Size * Scale * 1.15 * Math.Clamp(style.LineSpacing, 0.5, 4),
        };
        if (style.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
        return formatted;
    }

    private void Draw(DrawingContext dc, EditItem item)
    {
        switch (item)
        {
            case TextItem t:
            {
                var r = ToScreen(t.Rect);
                dc.DrawText(Text(t.Text, t.Style, r.Width), r.TopLeft);
                break;
            }

            case ReplaceTextItem rt:
            {
                var r = ToScreen(rt.Run.Rect.Inflate(1));
                dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 103, 192)), 1) { DashStyle = DashStyles.Dot }, r);
                var formatted = Text(rt.NewText, rt.Style, 4000);
                dc.DrawText(formatted, new Point(r.X + Scale, r.Y + (r.Height - formatted.Height) / 2));
                break;
            }

            case ImageItem i:
            {
                var r = ToScreen(i.Rect);
                var bitmap = Bitmap(i);
                dc.PushTransform(new RotateTransform(i.Rotation, r.X + r.Width / 2, r.Y + r.Height / 2));
                dc.PushOpacity(i.Opacity);
                if (bitmap is not null) dc.DrawImage(bitmap, r);
                else dc.DrawRectangle(Brushes.LightGray, null, r);
                dc.Pop();
                dc.Pop();
                break;
            }

            case ShapeItem s:
                DrawShape(dc, s);
                break;

            case MarkupItem m:
                foreach (var quad in m.Quads)
                {
                    var r = ToScreen(quad);
                    var thickness = Math.Max(1, r.Height * 0.07);
                    switch (m.Markup)
                    {
                        case MarkupKind.Highlight:
                            dc.DrawRectangle(Solid(m.Color, 0.4), null, r);
                            break;
                        case MarkupKind.Underline:
                            dc.DrawLine(new Pen(Solid(m.Color), thickness), new Point(r.Left, r.Bottom - thickness / 2), new Point(r.Right, r.Bottom - thickness / 2));
                            break;
                        default:
                            dc.DrawLine(new Pen(Solid(m.Color), thickness), new Point(r.Left, r.Top + r.Height * 0.55), new Point(r.Right, r.Top + r.Height * 0.55));
                            break;
                    }
                }

                break;

            case NoteItem n:
            {
                var r = ToScreen(n.Bounds);
                var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 1);
                dc.DrawRoundedRectangle(Solid(n.Color), outline, r, n.Note == NoteKind.Comment ? 5 * Scale : 1, n.Note == NoteKind.Comment ? 5 * Scale : 1);
                for (var k = 1; k <= 3; k++)
                    dc.DrawLine(outline, new Point(r.X + r.Width * 0.22, r.Y + r.Height * k / 4.5), new Point(r.Right - r.Width * 0.22, r.Y + r.Height * k / 4.5));
                break;
            }

            case TextBoxItem b:
            {
                var r = ToScreen(b.Rect);
                dc.DrawRectangle(b.Fill is { } f ? Solid(f) : null, new Pen(Solid(b.Border), Math.Max(1, Scale)), r);
                dc.PushClip(new RectangleGeometry(r));
                dc.DrawText(Text(b.Text, b.Style, r.Width - 8 * Scale), new Point(r.X + 4 * Scale, r.Y + 3 * Scale));
                dc.Pop();
                break;
            }

            case StampItem st:
                DrawStamp(dc, st);
                break;

            case FieldItem field:
                DrawField(dc, field);
                break;
        }
    }

    private void DrawShape(DrawingContext dc, ShapeItem s)
    {
        var pen = new Pen(Solid(s.Stroke, s.Opacity), Math.Max(0.5, s.StrokeWidth * Scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var fill = s.Fill is { } f ? Solid(f, s.Opacity) : null;
        var inset = s.StrokeWidth * Scale / 2;
        switch (s.Shape)
        {
            case ShapeKind.Rectangle:
            {
                var r = ToScreen(s.Rect);
                r.Inflate(-inset, -inset);
                dc.DrawRectangle(fill, pen, r.Width > 0 && r.Height > 0 ? r : ToScreen(s.Rect));
                break;
            }

            case ShapeKind.Ellipse:
            {
                var r = ToScreen(s.Rect);
                dc.DrawEllipse(fill, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), Math.Max(0, r.Width / 2 - inset), Math.Max(0, r.Height / 2 - inset));
                break;
            }

            case ShapeKind.Ink:
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(ToScreen(s.Points[0]), false, false);
                    g.PolyLineTo(s.Points.Skip(1).Select(ToScreen).ToList(), true, true);
                }

                dc.DrawGeometry(null, pen, geometry);
                break;
            }

            default:
            {
                var a = ToScreen(s.Points[0]);
                var b = ToScreen(s.Points[^1]);
                dc.DrawLine(pen, a, b);
                if (s.Shape == ShapeKind.Arrow)
                {
                    var length = Math.Max(8, s.StrokeWidth * 4.5) * Scale;
                    var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    var head = new StreamGeometry();
                    using (var g = head.Open())
                    {
                        g.BeginFigure(b, true, true);
                        g.LineTo(new Point(b.X - length * Math.Cos(angle - 0.45), b.Y - length * Math.Sin(angle - 0.45)), true, true);
                        g.LineTo(new Point(b.X - length * Math.Cos(angle + 0.45), b.Y - length * Math.Sin(angle + 0.45)), true, true);
                    }

                    dc.DrawGeometry(pen.Brush, pen, head);
                }

                break;
            }
        }
    }

    private void DrawStamp(DrawingContext dc, StampItem st)
    {
        var r = ToScreen(st.Rect);
        var color = Solid(st.Template.Color, 0.9);
        var border = Math.Clamp(Math.Min(r.Width, r.Height) * 0.06, 1.5, 4 * Scale);
        var inner = r;
        inner.Inflate(-border / 2, -border / 2);
        dc.DrawRoundedRectangle(null, new Pen(color, border), inner, r.Height * 0.25, r.Height * 0.25);
        var subtitle = string.Join(" · ", new[] { st.Template.Subtitle, st.Template.ShowDate ? st.When.ToString("dd/MM/yyyy", PtBr) : null }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        var main = Text(st.Template.Text, new TextStyle("Arial", 10, st.Template.Color, Bold: true), 10000, color);
        var area = r.Height * (subtitle.Length > 0 ? 0.58 : 0.8);
        var factor = Math.Min((r.Width - border * 4) / Math.Max(1, main.Width), area / Math.Max(1, main.Height));
        main = Text(st.Template.Text, new TextStyle("Arial", Math.Max(1, 10 * factor), st.Template.Color, Bold: true), 10000, color);
        var top = subtitle.Length > 0 ? r.Y + r.Height * 0.1 : r.Y + (r.Height - main.Height) / 2;
        dc.DrawText(main, new Point(r.X + (r.Width - main.Width) / 2, top));
        if (subtitle.Length > 0)
        {
            var small = Text(subtitle, new TextStyle("Arial", Math.Clamp(st.Rect.Height * 0.18, 4, 40), st.Template.Color), 10000, color);
            dc.DrawText(small, new Point(r.X + (r.Width - small.Width) / 2, r.Y + r.Height * 0.66));
        }
    }

    private void DrawField(DrawingContext dc, FieldItem field)
    {
        var blue = new SolidColorBrush(Color.FromArgb(40, 0, 103, 192));
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 1);
        var label = new TextStyle("Arial", 7, new Rgb(0x00, 0x50, 0x9A));
        switch (field.Field)
        {
            case FieldKind.CheckBox:
            {
                var r = ToScreen(field.Rect);
                dc.DrawRectangle(Brushes.White, outline, r);
                if (field.Value is { Length: > 0 } v && v != "Off")
                    dc.DrawLine(new Pen(Brushes.Black, Math.Max(1, r.Width * 0.12)), new Point(r.X + r.Width * 0.22, r.Y + r.Height * 0.48), new Point(r.X + r.Width * 0.42, r.Y + r.Height * 0.73));
                break;
            }

            case FieldKind.Radio:
            {
                dc.DrawRectangle(blue, new Pen(blue, 1) { DashStyle = DashStyles.Dash }, ToScreen(field.Rect));
                var options = field.Options.Count > 0 ? field.Options : ["Opção 1", "Opção 2"];
                var buttons = (field with { Options = options }).RadioButtons();
                for (var i = 0; i < buttons.Count; i++)
                {
                    var b = ToScreen(buttons[i]);
                    dc.DrawEllipse(Brushes.White, outline, new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
                    if (options[i] == field.Value) dc.DrawEllipse(Brushes.Black, null, new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 5, b.Height / 5);
                    var text = Text(options[i], new TextStyle("Arial", Math.Clamp(buttons[i].Height * 0.85, 6, 14), Rgb.Black), 4000);
                    dc.DrawText(text, new Point(b.Right + 4 * Scale, b.Y + (b.Height - text.Height) / 2));
                }

                break;
            }

            default:
            {
                var r = ToScreen(field.Rect);
                dc.DrawRectangle(field.Field == FieldKind.Button ? new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)) : Brushes.White, outline, r);
                dc.DrawRectangle(blue, null, r);
                var caption = field.Field switch
                {
                    FieldKind.Button => field.Value is { Length: > 0 } v ? v : field.Action == ButtonAction.Print ? "Imprimir" : "Limpar",
                    FieldKind.Dropdown => (field.Value ?? field.Options.FirstOrDefault() ?? string.Empty) + "  ▾",
                    FieldKind.Date => field.Value ?? "dd/mm/aaaa",
                    _ => field.Value ?? string.Empty,
                };
                dc.PushClip(new RectangleGeometry(r));
                if (caption.Length > 0)
                {
                    var text = Text(caption, new TextStyle("Arial", field.FontSize > 0 ? field.FontSize : Math.Clamp(field.Rect.Height * 0.55, 6, 12), Rgb.Black,
                        Align: field.Field == FieldKind.Button ? TextAlign.Center : TextAlign.Left), r.Width - 4 * Scale);
                    dc.DrawText(text, new Point(r.X + 2 * Scale, r.Y + (field.Field == FieldKind.MultilineText ? 2 * Scale : (r.Height - text.Height) / 2)));
                }

                dc.Pop();
                var name = Text(field.Name, label, 4000);
                dc.DrawText(name, new Point(r.X, r.Y - name.Height));
                break;
            }
        }
    }

    private BitmapSource? Bitmap(ImageItem item)
    {
        var key = (item.Data, item.Crop);
        if (_images.TryGetValue(key, out var cached)) return cached;
        BitmapSource? result = null;
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(item.Data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            if (!item.Crop.IsNone)
            {
                var w = source.PixelWidth;
                var h = source.PixelHeight;
                var x = (int)Math.Round(w * Math.Clamp(item.Crop.Left, 0, 0.95));
                var y = (int)Math.Round(h * Math.Clamp(item.Crop.Top, 0, 0.95));
                var right = (int)Math.Round(w * (1 - Math.Clamp(item.Crop.Right, 0, 0.95)));
                var bottom = (int)Math.Round(h * (1 - Math.Clamp(item.Crop.Bottom, 0, 0.95)));
                source = new CroppedBitmap(source, new Int32Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)));
            }

            source.Freeze();
            result = source;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            result = null;
        }

        if (_images.Count > 64) _images.Clear();
        _images[key] = result;
        return result;
    }
}
