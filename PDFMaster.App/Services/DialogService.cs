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

    private static bool IsExistingFolder(string? path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
}
