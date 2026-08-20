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
            LogCrash(ex);
        }
    }

    public static void LogDebug(string message)
    {
        RollingLog(Path.Combine(Path.GetTempPath(), "keypulse_debug.txt"), message);
    }

    public static void LogCrash(Exception ex)
    {
        RollingLog(Path.Combine(Path.GetTempPath(), "keypulse_crash.txt"), ex.ToString());
        if (ex.InnerException != null)
        {
            RollingLog(Path.Combine(Path.GetTempPath(), "keypulse_crash.txt"), "INNER: " + ex.InnerException.ToString());
        }
    }

    private static void RollingLog(string path, string message)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 5 * 1024 * 1024)
            {
                File.Delete(path);
            }
            File.AppendAllText(path, $"[{DateTime.Now:O}] {message}\n");
        }
        catch { }
    }

    private static void ExtractNativeLibs()
    {
        LogDebug("ExtractNativeLibs started");
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
                        LogDebug($"Extracted {lib}");
                    } catch (Exception ex) {
                        LogDebug($"Error extracting {lib}: {ex.Message}");
                    }
                }
                else
                {
                    LogDebug($"Resource KeyPulse.NativeLibs.{lib} not found!");
                }
            }
            else
            {
                LogDebug($"{lib} already exists");
            }
        }
        LogDebug("ExtractNativeLibs finished");
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
