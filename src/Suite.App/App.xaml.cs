using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using Suite.Core;

namespace Suite.App;

/// <summary>
/// Interaction logic for App.xaml. Registers global exception hooks so the app can never vanish
/// silently again — every fatal or dispatcher-level exception is written to the suite log first.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        LogSafe("app: started " + Environment.ProcessPath);
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogSafe("FATAL dispatcher exception: " + e.Exception);
        try
        {
            MessageBox.Show("The app hit an unexpected error. Details were written to the log at\n" + SuitePaths.Log
                + "\n\n" + e.Exception.Message, "Center Ice Modding Suite",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        e.Handled = true;
    }

    void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogSafe("FATAL unobserved task exception: " + e.Exception);
        e.SetObserved();
    }

    void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogSafe("FATAL unhandled exception (terminating=" + e.IsTerminating + "): " + e.ExceptionObject);
    }

    static void LogSafe(string message)
    {
        try { SuiteLog.Write(message); } catch { }
    }
}
