using System.Windows;
using DiskInspector.App.Services;
using DiskInspector.Core.Analysis;
using Microsoft.Win32;

namespace DiskInspector.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        GroupsFile.Text = WorkFiles.UserFilePath;
        CategoriesFile.Text = Categorizer.UserOverridePath;
    }

    AppSettings Settings => (AppSettings)DataContext;

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder for snapshots" };
        if (Directory.Exists(Settings.SnapshotFolder))
            dialog.InitialDirectory = Settings.SnapshotFolder;
        if (dialog.ShowDialog(this) == true)
        {
            Settings.SnapshotFolder = dialog.FolderName;
            FolderBox.Text = dialog.FolderName;
        }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.SnapshotFolder);
            Shell.Open(Settings.SnapshotFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"That folder can't be opened:\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string full = Path.GetFullPath(Settings.SnapshotFolder.Trim().Trim('"'));
            Directory.CreateDirectory(full);
            Settings.SnapshotFolder = full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"The snapshot folder can't be used:\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
