using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HamBridgeWpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Catch any unhandled WPF dispatcher exceptions so they show a
            // friendly message and log to %AppData%\HamBridge\error.log
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogError(e.Exception);
            MessageBox.Show(
                $"Unexpected error:\n\n{e.Exception.Message}\n\nSee error.log for details.",
                "HamBridge – Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogError(ex);
        }

        private static void LogError(Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HamBridge");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "error.log"),
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n");
            }
            catch { /* best-effort */ }
        }
    }
}
