using System.IO;
using System.Windows;
using Microsoft.Win32;
using PDFMaster.Core.Files;

namespace PDFMaster.App.Services;

public interface IDialogService
{
    IReadOnlyList<string> PickFiles(string? initialFolder);
    IReadOnlyList<string> PickPdfs(bool multiple, string? initialFolder);
    IReadOnlyList<string> PickFiles(string title, string filter, bool multiple, string? initialFolder);
    string? PickFolder(string title, string? initialFolder);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);

    /// <summary>Janela para desenhar a assinatura com o mouse; devolve um PNG com fundo transparente (ou null).</summary>
    byte[]? DrawSignature();
}

public sealed class DialogService : IDialogService
{
    public IReadOnlyList<string> PickFiles(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir arquivos",
            Filter = SupportedFiles.DialogFilter,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (IsExistingFolder(initialFolder)) dialog.InitialDirectory = initialFolder!;

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }

    public IReadOnlyList<string> PickPdfs(bool multiple, string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = multiple ? "Abrir arquivos PDF" : "Abrir PDF",
            Filter = "Documentos PDF|*.pdf",
            Multiselect = multiple,
            CheckFileExists = true,
        };
        if (IsExistingFolder(initialFolder)) dialog.InitialDirectory = initialFolder!;

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }

    public IReadOnlyList<string> PickFiles(string title, string filter, bool multiple, string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = multiple,
            CheckFileExists = true,
        };
        if (IsExistingFolder(initialFolder)) dialog.InitialDirectory = initialFolder!;

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (IsExistingFolder(initialFolder)) dialog.InitialDirectory = initialFolder!;

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public byte[]? DrawSignature()
    {
        var ink = new System.Windows.Controls.InkCanvas
        {
            Width = 560,
            Height = 220,
            Background = System.Windows.Media.Brushes.White,
            Cursor = System.Windows.Input.Cursors.Pen,
        };
        ink.DefaultDrawingAttributes.Color = System.Windows.Media.Color.FromRgb(0x10, 0x20, 0x60);
        ink.DefaultDrawingAttributes.Width = 2.6;
        ink.DefaultDrawingAttributes.Height = 2.6;
        ink.DefaultDrawingAttributes.FitToCurve = true;

        var window = new Window
        {
            Title = "Desenhar assinatura",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ShowInTaskbar = false,
        };

        var clear = new System.Windows.Controls.Button { Content = "Limpar", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new System.Windows.Controls.Button { Content = "Usar esta assinatura", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        clear.Click += (_, _) => ink.Strokes.Clear();
        ok.Click += (_, _) => window.DialogResult = ink.Strokes.Count > 0;

        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Assine com o mouse (ou caneta) na área branca. A assinatura fica guardada só neste computador.",
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.Border
        {
            BorderBrush = System.Windows.Media.Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = ink,
        });
        panel.Children.Add(buttons);
        window.Content = panel;

        if (window.ShowDialog() != true || ink.Strokes.Count == 0) return null;

        // PNG com fundo transparente, em 3× a resolução da tela, recortado ao redor do traço.
        var bounds = ink.Strokes.GetBounds();
        bounds.Inflate(4, 4);
        const double scale = 3;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new System.Windows.Media.TranslateTransform(-bounds.X, -bounds.Y));
            ink.Strokes.Draw(dc);
            dc.Pop();
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)),
            96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool IsExistingFolder(string? path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
}
