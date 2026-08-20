using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using System.Diagnostics;

namespace KeyPulse;

class Program
{
    private static Mutex? _mutex;
    public static string AppName = "KeyPulse";
    public static string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    public static string ExePath = Path.Combine(InstallDir, "KeyPulse.exe");

    private static object _logLock = new object();
    private static string DebugLogPath = Path.Combine(InstallDir, "keypulse_debug.txt");
    private static string CrashLogPath = Path.Combine(InstallDir, "keypulse_crash.txt");

    public static void LogDebug(string msg) => RollingLog(DebugLogPath, msg);
    public static void LogCrash(string msg) => RollingLog(CrashLogPath, msg);

    private static void RollingLog(string path, string msg)
    {
        try
        {
            lock (_logLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                {
                    File.Delete(path);
                }
                File.AppendAllText(path, $"[{DateTime.Now:O}] {msg}\n");
            }
        }
        catch { }
    }

    [STAThread]
    public static void Main(string[] args)
    {
                _mutex = new Mutex(false, "KeyPulse_SingleInstance");
        bool createdNew = false;
        try { createdNew = _mutex.WaitOne(3000); } catch (AbandonedMutexException) { createdNew = true; }
        if (!createdNew && !args.Contains("--uninstall") && !args.Contains("--install-worker")) return;

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash($"Unhandled Exception: {e.ExceptionObject}");
        };

        ExtractNativeLibs();

        if (args.Contains("--uninstall"))
        {
            var tempUninstaller = Path.Combine(Path.GetTempPath(), "KeyPulse_Uninstaller.exe");
            if (!Environment.ProcessPath!.Equals(tempUninstaller, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Environment.ProcessPath!, tempUninstaller, true);
                try { var ps = new Process { StartInfo = new ProcessStartInfo { FileName = tempUninstaller, Arguments = "--uninstall", UseShellExecute = true, Verb = "runas" } }; ps.Start(); } catch { }
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
            try
            {
                var ps = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = "--install-worker",
                        UseShellExecute = true,
                        Verb = "runas"
                    }
                };
                ps.Start();
            }
            catch (Exception ex)
            {
                LogCrash($"Elevation failed: {ex.Message}");
            }
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash($"Fatal avalonia crash: {ex}");
        }
    }

    private static void ExtractNativeLibs()
    {
        LogDebug("ExtractNativeLibs started");
        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames();
        var outDir = Path.GetDirectoryName(Environment.ProcessPath!)!;

        foreach (var res in resources)
        {
            if (res.EndsWith(".dll"))
            {
                var target = Path.Combine(outDir, res.Split('.').SkipLast(1).Last() + ".dll");
                using var stream = asm.GetManifestResourceStream(res);
                if (stream == null) continue;

                if (!File.Exists(target) || new FileInfo(target).Length != stream.Length)
                {
                    LogDebug($"Extracting {target}");
                    try 
                    { 
                        using var fs = File.Create(target);
                        stream.CopyTo(fs);
                    } 
                    catch(Exception ex) 
                    { 
                        LogDebug($"Failed to extract: {ex}"); 
                    }
                }
                else
                {
                    LogDebug($"{Path.GetFileName(target)} already exists");
                }
            }
        }
        LogDebug("ExtractNativeLibs finished");
    }

    private static bool IsRunningFromInstallPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/').Equals(InstallDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartup(bool enable)
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var vbsPath = Path.Combine(InstallDir, "boot.vbs");
                var vbsCode = $@"Set WshShell = CreateObject(""WScript.Shell"")
WshShell.Run """"{ExePath}"""", 0, False
WScript.Sleep 10000
WshShell.Run """"{ExePath}"""", 0, False
WScript.Sleep 10000
WshShell.Run """"{ExePath}"""", 0, False
WScript.Sleep 10000
WshShell.Run """"{ExePath}"""", 0, False";
                File.WriteAllText(vbsPath, vbsCode);
                key.SetValue(AppName, $"wscript.exe \"{vbsPath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
                var vbsPath = Path.Combine(InstallDir, "boot.vbs");
                if (File.Exists(vbsPath)) File.Delete(vbsPath);
            }
        }
        catch { }
    }

    public static void PlaySound(string soundName)
    {
        try
        {
            var asm = typeof(Program).Assembly;
            using var stream = asm.GetManifestResourceStream($"KeyPulse.Assets.{soundName}.wav");
            if (stream != null)
            {
                using var player = new System.Media.SoundPlayer(stream);
                player.Play();
            }
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}













