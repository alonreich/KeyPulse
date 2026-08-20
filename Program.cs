using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace KeyPulse;

class Program
{
    public static string AppName = "KeyPulse";
    public static string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string ExePath = Path.Combine(InstallDir, "KeyPulse.exe");

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--uninstall"))
        {
            var tempUninstaller = Path.Combine(Path.GetTempPath(), "KeyPulse_Uninstaller.exe");
            if (!Environment.ProcessPath!.Equals(tempUninstaller, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Environment.ProcessPath!, tempUninstaller, true);
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempUninstaller,
                    Arguments = "--uninstall",
                    UseShellExecute = true
                });
                return;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        if (args.Contains("--install-worker"))
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        if (!IsRunningFromInstallPath())
        {
            var tempInstaller = Path.Combine(Path.GetTempPath(), "KeyPulse_Installer.exe");
            File.Copy(Environment.ProcessPath!, tempInstaller, true);
            Process.Start(new ProcessStartInfo
            {
                FileName = tempInstaller,
                Arguments = "--install-worker",
                UseShellExecute = true
            });
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool IsRunningFromInstallPath()
    {
        return Environment.ProcessPath!.Equals(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
