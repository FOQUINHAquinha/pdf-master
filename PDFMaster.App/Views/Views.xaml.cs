using System.Windows;
using System.Windows.Controls;
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
