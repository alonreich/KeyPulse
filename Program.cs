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
    private const string SingleInstanceMutexName = "KeyPulse_SingleInstance";
    private const string OpenWindowEventName = "KeyPulse_OpenWindow";

    private static Mutex? _mutex;
    private static EventWaitHandle? _openWindowEvent;
    private static Thread? _openWindowListener;
    private static volatile bool _openWindowListenerRunning;

    public static string AppName = "KeyPulse";
    public static string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string ExePath = Path.Combine(InstallDir, "KeyPulse.exe");

    private static object _logLock = new object();
    private static string DebugLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "keypulse_debug.txt");
    private static string CrashLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "keypulse_crash.txt");

    public static void LogDebug(string msg) => RollingLog(DebugLogPath, msg);
    public static void LogCrash(string msg) => RollingLog(CrashLogPath, msg);

    private static void RollingLog(string path, string msg)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        var isUninstall = args.Contains("--uninstall");
        var isInstallWorker = args.Contains("--install-worker");
        var isSetupMode = isUninstall || isInstallWorker;

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash($"Unhandled Exception: {e.ExceptionObject}");
        };

        ExtractNativeLibs();

        if (isUninstall)
        {
            var tempUninstaller = Path.Combine(Path.GetTempPath(), "KeyPulse_Uninstaller.exe");
            if (!Environment.ProcessPath!.Equals(tempUninstaller, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Environment.ProcessPath!, tempUninstaller, true);
                try { var ps = new Process { StartInfo = new ProcessStartInfo { FileName = tempUninstaller, Arguments = "--uninstall", UseShellExecute = true } }; ps.Start(); } catch { }
                return;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        if (isInstallWorker)
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
                        UseShellExecute = true
                    }
                };
                ps.Start();
            }
            catch (Exception ex)
            {
                LogCrash($"Install relaunch failed: {ex.Message}");
            }
            return;
        }

        if (!isSetupMode)
        {
            _mutex = new Mutex(false, SingleInstanceMutexName);
            bool createdNew = false;
            try { createdNew = _mutex.WaitOne(3000); } catch (AbandonedMutexException) { createdNew = true; }
            if (!createdNew)
            {
                SignalExistingInstance();
                return;
            }

            EnsureOpenWindowEvent();
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

    private static void EnsureOpenWindowEvent()
    {
        if (_openWindowEvent != null) return;

        try
        {
            _openWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenWindowEventName);
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to create open-window event: {ex.Message}");
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var openWindowEvent = EventWaitHandle.OpenExisting(OpenWindowEventName);
            openWindowEvent.Set();
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to signal existing instance: {ex.Message}");
        }
    }

    public static void StartOpenWindowRequestListener(Action openWindow)
    {
        EnsureOpenWindowEvent();
        if (_openWindowEvent == null || _openWindowListener != null) return;

        _openWindowListenerRunning = true;
        _openWindowListener = new Thread(() =>
        {
            while (_openWindowListenerRunning)
            {
                try
                {
                    _openWindowEvent.WaitOne();
                    if (_openWindowListenerRunning) openWindow();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogDebug($"Open-window listener failed: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        });
        _openWindowListener.IsBackground = true;
        _openWindowListener.Start();
    }

    public static void StopOpenWindowRequestListener()
    {
        _openWindowListenerRunning = false;
        try { _openWindowEvent?.Set(); } catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static void ExtractNativeLibs()
    {
        LogDebug("ExtractNativeLibs started");
        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames();
        var outDir = Path.Combine(Path.GetTempPath(), AppName + "_NativeLibs");

        try { Directory.CreateDirectory(outDir); } catch { }
        SetDllDirectory(outDir);

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
                        var tempTarget = target + ".tmp";
                        using (var fs = File.Create(tempTarget))
                        {
                            stream.CopyTo(fs);
                        }
                        if (File.Exists(target))
                        {
                            try { File.Delete(target); } catch { /* Ignore if locked, we'll try again next boot */ }
                        }
                        if (!File.Exists(target))
                        {
                            File.Move(tempTarget, target);
                        }
                        else
                        {
                            File.Delete(tempTarget);
                        }
                    } 
                    catch(Exception ex) 
                    { 
                        LogDebug($"Failed to extract: {ex}"); 
                    }
                }
                else
                {
                    LogDebug($"{Path.GetFileName(target)} already exists and matches length.");
                }
            }
        }
        LogDebug("ExtractNativeLibs finished");
    }

    private static bool IsRunningFromInstallPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/').Equals(InstallDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetStartup(bool enable, out string error)
    {
        error = string.Empty;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null)
            {
                error = "Could not open the Windows startup registry key.";
                return false;
            }

            var vbsPath = Path.Combine(InstallDir, "boot.vbs");
            if (enable)
            {
                Directory.CreateDirectory(InstallDir);
                var vbsCode = $@"Set WshShell = CreateObject(""WScript.Shell"")
Set objWMIService = GetObject(""winmgmts:\\.\root\cimv2"")
For i = 1 To 4
    Set colItems = objWMIService.ExecQuery(""Select * From Win32_Process Where Name = 'KeyPulse.exe'"")
    If colItems.Count = 0 Then
        WshShell.Run """"{ExePath}"" --hidden"", 0, False
    Else
        Exit For
    End If
    WScript.Sleep 10000
Next";
                File.WriteAllText(vbsPath, vbsCode);
                key.SetValue(AppName, $"wscript.exe \"{vbsPath}\"");

                if (!File.Exists(vbsPath) || key.GetValue(AppName)?.ToString()?.Contains(vbsPath, StringComparison.OrdinalIgnoreCase) != true)
                {
                    error = "Windows startup entry was not saved.";
                    return false;
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
                if (File.Exists(vbsPath)) File.Delete(vbsPath);

                if (key.GetValue(AppName) != null)
                {
                    error = "Windows startup entry could not be removed.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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













