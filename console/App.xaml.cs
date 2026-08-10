using System.Windows;

using System.Windows.Threading;
using b1_chat_console.Services;

namespace b1_chat_console;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Used by the release script and NSIS after extraction. Reaching this point already
        // proves that the bundled .NET/WPF runtime can start on the target computer; the
        // verifier then checks every external runtime file before shortcuts are registered.
        if (e.Args.Any(arg => string.Equals(arg, "--verify-install", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var ok = InstallationVerifier.TryVerify(AppContext.BaseDirectory, out var error);
            if (!ok) TraceLog.Write("ERR", "Installation verification: " + error);
            Shutdown(ok ? 0 : 2);
            return;
        }

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TraceLog.Write("ERR", $"Unhandled UI error: {e.Exception.GetType().Name} — {e.Exception.Message}");
        MessageBox.Show(
            "B1 Chat Console encountered an unexpected error.\n\n" +
            "Diagnostic details were written to:\n%LOCALAPPDATA%\\B1ChatConsole\\serial-trace.log",
            "B1 Chat Console", MessageBoxButton.OK, MessageBoxImage.Error);

        // Keep the main console alive when a secondary UI action fails.
        e.Handled = true;
    }
}

