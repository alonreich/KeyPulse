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
            ExtractNativeLibs();
            SafeStart(args);
            return;
        }

        if (args.Contains("--install-worker"))
        {
            ExtractNativeLibs();
            SafeStart(args);
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

        ExtractNativeLibs();
        SafeStart(args);
    }

    private static void SafeStart(string[] args)
    {
        try {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "keypulse_crash.txt"), ex.ToString());
            if (ex.InnerException != null) File.AppendAllText(Path.Combine(Path.GetTempPath(), "keypulse_crash.txt"), "\nINNER: " + ex.InnerException.ToString());
        }
    }

    private static void ExtractNativeLibs()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), "ExtractNativeLibs started\n");
        var libs = new[] { "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll" };
        var targetDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var lib in libs)
        {
            var target = Path.Combine(targetDir, lib);
            if (!File.Exists(target))
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"KeyPulse.NativeLibs.{lib}");
                if (stream != null)
                {
                    try {
                        using var fs = File.Create(target);
                        stream.CopyTo(fs);
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), $"Extracted {lib}\n");
                    } catch (Exception ex) {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), $"Error extracting {lib}: {ex.Message}\n");
                    }
                }
                else
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), $"Resource KeyPulse.NativeLibs.{lib} not found!\n");
                }
            }
            else
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), $"{lib} already exists\n");
            }
        }
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), "ExtractNativeLibs finished\n");
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
