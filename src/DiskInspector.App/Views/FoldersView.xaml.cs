using System.Windows.Controls;
using System.Windows.Input;
using DiskInspector.App.ViewModels;

namespace DiskInspector.App.Views;

public partial class FoldersView : UserControl
{
    public FoldersView()
    {
        InitializeComponent();
        // Scroll the selection into view when the tab is shown (it may have changed while hidden).
        IsVisibleChanged += (_, _) => ScrollSelectionIntoView();
    }

    FolderTreeViewModel? Vm => DataContext as FolderTreeViewModel;

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is FolderRow row)
            Vm?.Toggle(row);
    }

    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not FolderRow row || Vm is null)
            return;
        if (e.Key == Key.Right && row.HasChildren && !row.IsExpanded || e.Key == Key.Left && row.IsExpanded || e.Key == Key.Enter)
        {
            Vm.Toggle(row);
            e.Handled = true;
        }
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScrollSelectionIntoView();

    void ScrollSelectionIntoView()
    {
        // ScrollIntoView forces a layout pass; on a hidden list that costs seconds and achieves nothing.
        if (!List.IsVisible)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (List.IsVisible && List.SelectedItem is { } item)
                List.ScrollIntoView(item);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
