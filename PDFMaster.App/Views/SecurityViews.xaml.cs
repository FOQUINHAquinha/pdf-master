using System.Windows;
using System.Windows.Controls;
using PDFMaster.App.ViewModels;

namespace PDFMaster.App.Views;

public partial class ProtectView : UserControl
{
    public ProtectView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }

    // As senhas vão direto da caixa para a memória da tela; nunca passam por arquivo.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { Tag: string field } box && DataContext is ProtectViewModel viewModel) viewModel.SetPassword(field, box.Password);
    }
}

public partial class UnlockView : UserControl
{
    public UnlockView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && DataContext is UnlockViewModel viewModel) viewModel.SetPassword(box.Password);
    }
}

public partial class SecurityInfoView : UserControl
{
    public SecurityInfoView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class MetadataView : UserControl
{
    public MetadataView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class SignatureView : UserControl
{
    public SignatureView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class RepairView : UserControl
{
    public RepairView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && DataContext is RepairViewModel viewModel) viewModel.SetPassword(box.Password);
    }
}

public partial class PdfAView : UserControl
{
    public PdfAView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class CompareView : UserControl
{
    public CompareView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class BatchView : UserControl
{
    public BatchView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class WatchFolderView : UserControl
{
    public WatchFolderView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class PageToolView : UserControl
{
    public PageToolView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class ExtractContentView : UserControl
{
    public ExtractContentView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}

public partial class GrayscaleView : UserControl
{
    public GrayscaleView()
    {
        InitializeComponent();
        ToolDrop.Attach(this);
    }
}
