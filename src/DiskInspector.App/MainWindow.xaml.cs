using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DiskInspector.App.ViewModels;
using DiskInspector.App.Views;

namespace DiskInspector.App;

public partial class MainWindow : Window
{
    readonly Dictionary<int, FrameworkElement> _views = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
                old.PropertyChanged -= Vm_PropertyChanged;
            if (e.NewValue is MainViewModel vm)
            {
                vm.PropertyChanged += Vm_PropertyChanged;
                ShowView(vm.SelectedViewIndex);
            }
        };
    }

    void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedViewIndex))
            ShowView(Vm.SelectedViewIndex);
    }

    FrameworkElement CreateView(string key) => key switch
    {
        "sunburst" => new SunburstView(),
        "folders" => new FoldersView { DataContext = Vm.Folders },
        "types" => new TypesView { DataContext = Vm.Types },
        "categories" => new CategoriesView { DataContext = Vm.Categories },
        "work" => new WorkFilesView { DataContext = Vm.WorkFiles },
        "locations" => new LocationsView { DataContext = Vm.Locations },
        "largest" => new LargestFilesView { DataContext = Vm.Largest },
        "age" => new AgeView { DataContext = Vm.Ages },
        _ => new TreemapView(),
    };

    /// <summary>Puts the selected view in the window, creating it the first time. Views are kept for later so they keep their state.</summary>
    void ShowView(int index)
    {
        if (!_views.TryGetValue(index, out var view))
        {
            view = CreateView(index >= 0 && index < Vm.Views.Count ? Vm.Views[index].Key : "treemap");
            _views[index] = view;
        }
        ViewsHost.Content = view;
    }

    MainViewModel Vm => (MainViewModel)DataContext;

    void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is { } menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = ExportButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    void DismissNotice_Click(object sender, RoutedEventArgs e) => Vm.Notice = null;

    void ShowIssues_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView { ItemsSource = Vm.Issues, Margin = new Thickness(12) };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Folder", Width = 560, DisplayMemberBinding = new System.Windows.Data.Binding("Path") });
        view.Columns.Add(new GridViewColumn { Header = "Problem", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("Message") });
        list.View = view;
        var text = new TextBlock
        {
            Margin = new Thickness(12, 12, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Windows did not let the normal scan look inside these folders, so their contents are not counted. " +
                   "Most are protected system folders. The fast scan (administrator) can read all of them.",
        };
        DockPanel.SetDock(text, Dock.Top);
        var panel = new DockPanel();
        panel.Children.Add(text);
        panel.Children.Add(list);
        new Window
        {
            Title = $"Folders that could not be read ({Vm.Issues.Count:N0})",
            Content = panel,
            Owner = this,
            Width = 900,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        }.Show();
    }
}
