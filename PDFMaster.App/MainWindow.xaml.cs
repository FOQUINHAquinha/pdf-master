using System.Windows;
using System.Windows.Input;

namespace PDFMaster.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Ctrl+F leva o foco para a pesquisa de ferramentas.
        var focusSearch = new RoutedCommand();
        focusSearch.InputGestures.Add(new KeyGesture(Key.F, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(focusSearch, (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
    }

    /// <summary>
    /// Clicar no item do menu que já está selecionado não muda a seleção; quando há uma ferramenta
    /// aberta, esse clique volta para a lista de ferramentas da seção.
    /// </summary>
    private void OnNavClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel) return;
        var selectedBefore = viewModel.SelectedNav;
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(viewModel.SelectedNav, selectedBefore)) viewModel.RefreshCurrentNav();
        });
    }
}
