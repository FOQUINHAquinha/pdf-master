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
}
