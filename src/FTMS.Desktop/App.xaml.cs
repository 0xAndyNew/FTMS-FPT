using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using FTMS.Infrastructure;

namespace FTMS.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
        DailyLogCleaner.Clean(root);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Đảm bảo ứng dụng chạy với quyền Administrator
        if (!IsAdministrator())
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    Shutdown();
                    return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Người dùng bấm Hủy/Không cấp quyền UAC
                    Shutdown();
                    return;
                }
            }
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "error.log"),
            $"{DateTimeOffset.Now:O} {TelegramErrorSanitizer.Sanitize(e.Exception.ToString())}\n\n");
        System.Windows.MessageBox.Show($"FTMS Companion g\u1eb7p l\u1ed7i: {TelegramErrorSanitizer.Sanitize(e.Exception.Message)}",
            "FTMS Companion", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

