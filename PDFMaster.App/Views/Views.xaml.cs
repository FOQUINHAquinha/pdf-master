using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDFMaster.App.ViewModels;

namespace PDFMaster.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepts = e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is HomeViewModel { IsBusy: false };
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not HomeViewModel viewModel) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // AddPathsAsync trata e notifica os próprios erros.
        await viewModel.AddPathsAsync(paths);
    }
}

public partial class ToolsPageView : UserControl
{
    public ToolsPageView() => InitializeComponent();
}

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

/// <summary>Arrastar e soltar arquivos sobre uma tela de ferramenta.</summary>
internal static class ToolDrop
{
    public static void Attach(UserControl view)
    {
        view.AllowDrop = true;
        view.DragOver += OnDragOver;
        view.Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var accepts = (sender as FrameworkElement)?.DataContext is ToolViewModelBase { IsBusy: false };
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not ToolViewModelBase viewModel) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // AddFilesAsync trata e notifica os próprios erros.
        await viewModel.AddFilesAsync(paths);
    }
}

public partial class CombineView : UserControl
{
    public CombineView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class OcrView : UserControl
{
    public OcrView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class VideoView : UserControl
{
    public VideoView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class ScanView : UserControl
{
    public ScanView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class ConvertView : UserControl
{
    public ConvertView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class CompressView : UserControl
{
    public CompressView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class SplitView : UserControl
{
    public SplitView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class PageEditorView : UserControl
{
    private const string PageDragFormat = "PDFMaster.Pages";
    private Point _dragStart;
    private bool _dragArmed;

    public PageEditorView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }

    private PageEditorViewModel? ViewModel => DataContext as PageEditorViewModel;

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnPagesMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null;
    }

    private void OnPagesMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;
        if (ViewModel is null || ViewModel.IsBusy || PageList.SelectedItems.Count == 0) return;
        DragDrop.DoDragDrop(PageList, new DataObject(PageDragFormat, true), DragDropEffects.Move);
    }

    private void OnPagesDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(PageDragFormat)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPagesDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(PageDragFormat) || ViewModel is null) return;
        e.Handled = true;

        var target = ViewModel.Pages.Count;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } container)
        {
            var index = PageList.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0)
            {
                // Soltar na metade direita da miniatura insere depois dela.
                var after = e.GetPosition(container).X > container.ActualWidth / 2;
                target = after ? index + 1 : index;
            }
        }

        ViewModel.MoveSelectedTo(target);
    }

    private void OnPagesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is null) return;
        if (ViewModel.DeleteCommand.CanExecute(null)) ViewModel.DeleteCommand.Execute(null);
        e.Handled = true;
    }
}

public partial class ViewerView : UserControl
{
    public ViewerView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
        Loaded += (_, _) => UpdateMetrics(rerender: true);
    }

    private ViewerViewModel? ViewModel => DataContext as ViewerViewModel;

    private void UpdateMetrics(bool rerender)
    {
        if (ViewModel is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var changed = Math.Abs(ViewModel.DpiScale - dpi) > 0.001;
        ViewModel.DpiScale = dpi;
        if (PageScroll.ActualWidth > 0) ViewModel.ViewportWidth = PageScroll.ActualWidth;
        if (rerender && changed) ViewModel.RenderCurrent();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMetrics(rerender: false);

    private void OnPageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var command = e.Delta > 0 ? ViewModel.ZoomInCommand : ViewModel.ZoomOutCommand;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbList.SelectedItem is not null) ThumbList.ScrollIntoView(ThumbList.SelectedItem);
        PageScroll.ScrollToTop();
    }
}
