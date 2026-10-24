using System.Windows;
using System.Windows.Threading;
using DiskInspector.App.Services;
using DiskInspector.App.ViewModels;

namespace DiskInspector.App;

public partial class App : Application
{
    int _errorDialogs;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception (process ending)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        Log.Info($"Started {Environment.ProcessPath} {string.Join(' ', e.Args)} on {Environment.OSVersion}");

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        if (e.Args.Contains("--no-shots"))
        {
            window.ShowActivated = false;
            window.WindowState = WindowState.Minimized;
        }
        MainWindow = window;
        window.Show();
        window.Dispatcher.BeginInvoke(() =>
        {
            if (!SelfTest.TryStart(e.Args, vm, window))
                vm.ApplyStartupArguments(e.Args);
        }, DispatcherPriority.ApplicationIdle);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        // Avoid a cascade of dialogs if something keeps failing while rendering.
        if (Interlocked.Increment(ref _errorDialogs) > 3)
            return;
        MessageBox.Show(
            $"Something went wrong, but DiskInspector kept running.\n\n{e.Exception.Message}\n\nDetails were written to:\n{Log.CurrentFile}",
            "DiskInspector", MessageBoxButton.OK, MessageBoxImage.Warning);
        Interlocked.Decrement(ref _errorDialogs);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Exit");
        base.OnExit(e);
    }
}
