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
    // ISSUE_2: session-local, not Global\. A Global\ mutex created by another Windows user has a
    // DACL this user cannot open, which threw an unhandled UnauthorizedAccessException at startup
    // and also blocked a second user (or an RDP session) from running KeyPulse at all.
    private const string SingleInstanceMutexName = @"Local\KeyPulse_SingleInstance";
    private const string OpenWindowEventName = "KeyPulse_OpenWindow";

    public const string AdminTaskName = "KeyPulse_AdminTask";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static Mutex? _mutex;
    private static bool _mutexOwned;
    private static EventWaitHandle? _openWindowEvent;
    private static Thread? _openWindowListener;
    private static volatile bool _openWindowListenerRunning;

    public static string AppName = "KeyPulse";
    public static string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string ExePath = Path.Combine(InstallDir, "KeyPulse.exe");

    /// <summary>
    /// ISSUE_21: the graphics libraries live inside KeyPulse's own install folder, not in %TEMP%.
    /// %TEMP% is writable by anything else running as this user, and the app told Windows to load
    /// code from it. Living under the install directory also means the uninstaller's recursive
    /// delete removes them, instead of leaving them behind forever.
    /// </summary>
    public static string RuntimeLibDir = Path.Combine(InstallDir, "runtime");

    /// <summary>ISSUE_21 / ISSUE_8: folders earlier builds used. The setup pipeline must delete these.</summary>
    public static string LegacyNativeLibDir = Path.Combine(Path.GetTempPath(), AppName + "_NativeLibs");
    public static string LegacyLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// ISSUE_8: logs live inside %APPDATA%\KeyPulse so the uninstaller's existing purge of that
    /// folder removes them. They used to sit in %LOCALAPPDATA%\KeyPulse, which nothing ever deleted,
    /// leaving a permanent record of every shortcut the user pressed behind after uninstalling.
    /// </summary>
    public static string LogDir = Path.Combine(ConfigStore.ConfigDirectory, "logs");

    /// <summary>ISSUE_19: mirrors AppConfig.SoundEnabled so PlaySound can be muted.</summary>
    public static volatile bool SoundEnabled = true;

    private static object _logLock = new object();
    private static string DebugLogPath = Path.Combine(LogDir, "keypulse_debug.txt");
    private static string CrashLogPath = Path.Combine(LogDir, "keypulse_crash.txt");

    public static void LogDebug(string msg) => RollingLog(DebugLogPath, msg);
    public static void LogCrash(string msg) => RollingLog(CrashLogPath, msg);

    private static void RollingLog(string path, string msg)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    // Roll over instead of throwing the evidence away the moment it gets useful.
                    var previous = path + ".old";
                    try { if (File.Exists(previous)) File.Delete(previous); } catch { }
                    try { File.Move(path, previous); } catch { try { File.Delete(path); } catch { } }
                }
                File.AppendAllText(path, $"[{DateTime.Now:O}] {msg}\n");
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------------
    // ISSUE_15: one honest answer to "which build am I running?"
    //
    // FileVersionInfo reads the version stamped into the exe by the build, so this works under
    // NativeAOT without touching reflection (see the agent directives in project_structure.txt).
    // ---------------------------------------------------------------------

    private static string? _appVersion;

    public static string AppVersion
    {
        get
        {
            if (_appVersion != null) return _appVersion;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
                var version = info.FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    // The build stamps yyyy.MM.dd.HHmm; drop a trailing ".0" placeholder only.
                    _appVersion = version.EndsWith(".0", StringComparison.Ordinal) && version.Count(c => c == '.') == 3
                        ? version.Substring(0, version.Length - 2)
                        : version;
                    return _appVersion;
                }
            }
            catch (Exception ex)
            {
                LogDebug("Could not read the application version: " + ex.Message);
            }

            _appVersion = "unknown";
            return _appVersion;
        }
    }

    public const string RepositorySlug = "alonreich/KeyPulse";
    public const string ReleasesPageUrl = "https://github.com/" + RepositorySlug + "/releases/latest";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + RepositorySlug + "/releases/latest";

    /// <summary>ISSUE_20: answered once, so the UI stops telling an elevated user to elevate.</summary>
    private static bool? _isElevated;

    public static bool IsElevated
    {
        get
        {
            if (_isElevated.HasValue) return _isElevated.Value;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                _isElevated = new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isElevated = false;
            }

            return _isElevated.Value;
        }
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
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash($"Unobserved Task Exception: {e.Exception}");
            e.SetObserved();
        };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (s, e) =>
        {
            LogCrash($"Dispatcher Unhandled Exception: {e.Exception}");
            e.Handled = true;
        };

        ExtractNativeLibs();

        if (args.Contains("--setup-admin-task"))
        {
            RunSetupAdminTask();
            return;
        }

        if (args.Contains("--remove-admin-task"))
        {
            RunRemoveAdminTask();
            return;
        }

        if (isUninstall)
        {
            // The scheduled task is removed (and verified) by SetupWindow's uninstall pipeline so
            // that a failure is reported to the user instead of being swallowed here. ISSUE_13.
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
            bool createdNew;
            try
            {
                _mutex = new Mutex(false, SingleInstanceMutexName);
                try { createdNew = _mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { createdNew = true; }
            }
            catch (Exception ex)
            {
                // Never let the single-instance guard stop KeyPulse from running. ISSUE_2.
                LogCrash($"Single-instance mutex unavailable, continuing without it: {ex}");
                _mutex = null;
                createdNew = true;
            }

            _mutexOwned = createdNew && _mutex != null;

            if (!createdNew)
            {
                // ISSUE_5: reopening from the desktop/Start Menu shortcut is the normal way back in,
                // not an error. Bring the running window forward silently and only complain if that
                // could not be delivered.
                if (!SignalExistingInstance())
                {
                    MessageBox(IntPtr.Zero,
                        "KeyPulse is already running, but its window could not be brought to the front.\n\nOpen it from the KeyPulse icon in the system tray, next to the clock.",
                        "KeyPulse", 0x40);
                }

                _mutex?.Dispose();
                _mutex = null;
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
        finally
        {
            if (_mutex != null)
            {
                if (_mutexOwned)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                }
                _mutex.Dispose();
                _mutex = null;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Elevated-mode plumbing
    // ---------------------------------------------------------------------

    private static bool RunSchtasks(string arguments, out int exitCode, out string output)
    {
        exitCode = -1;
        output = string.Empty;

        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null) return false;

            // ISSUE_19: both pipes must be drained AT THE SAME TIME. Reading one to the end and then
            // the other deadlocks the moment Windows writes enough to fill the pipe we are not
            // reading - and the 15-second timeout below never fires, because the block is on the
            // read, not on the wait. That hung Settings permanently on some machines.
            var standardOutTask = p.StandardOutput.ReadToEndAsync();
            var standardErrorTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(2000); } catch { }
                output = "The Windows task command did not respond within 15 seconds.";
                exitCode = -1;
                return false;
            }

            try
            {
                System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { standardOutTask, standardErrorTask }, 5000);
            }
            catch { }

            output = SafeStreamResult(standardOutTask) + SafeStreamResult(standardErrorTask);
            exitCode = p.ExitCode;
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

    private static string SafeStreamResult(System.Threading.Tasks.Task<string> task)
    {
        try { return task.IsCompletedSuccessfully ? task.Result : string.Empty; }
        catch { return string.Empty; }
    }

    public enum AdminTaskState
    {
        NotPresent,
        Enabled,
        Disabled
    }

    // ISSUE_12: schtasks.exe is a separate process, and the answer was being re-fetched on every
    // single question - twice just to open Settings, twice more per startup toggle, all on the UI
    // thread. The answer is cached and explicitly invalidated whenever KeyPulse changes the task.
    private static readonly object AdminTaskLock = new object();
    private static AdminTaskState? _adminTaskStateCache;

    public static void InvalidateAdminTaskState()
    {
        lock (AdminTaskLock) { _adminTaskStateCache = null; }
    }

    public static AdminTaskState GetAdminTaskState()
    {
        lock (AdminTaskLock)
        {
            if (_adminTaskStateCache.HasValue) return _adminTaskStateCache.Value;
        }

        var state = QueryAdminTaskState();

        lock (AdminTaskLock) { _adminTaskStateCache = state; }
        return state;
    }

    private static AdminTaskState QueryAdminTaskState()
    {
        // /XML is locale-independent; parsing the "Status:" column is not.
        if (!RunSchtasks($"/Query /TN \"{AdminTaskName}\" /XML ONELINE", out _, out var xml))
        {
            return AdminTaskState.NotPresent;
        }

        return xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase)
            ? AdminTaskState.Disabled
            : AdminTaskState.Enabled;
    }

    public static bool IsAdminTaskInstalled() => GetAdminTaskState() != AdminTaskState.NotPresent;

    /// <summary>
    /// ISSUE_13: verified removal. The old uninstaller fired schtasks and ignored the result, so a
    /// task created from an elevated process routinely survived and kept trying to launch a deleted
    /// KeyPulse.exe at every logon, forever.
    /// </summary>
    public static bool TryRemoveAdminTask(out string message)
    {
        message = string.Empty;
        if (GetAdminTaskState() == AdminTaskState.NotPresent) return true;

        RunSchtasks($"/Delete /TN \"{AdminTaskName}\" /F", out var code, out var output);
        InvalidateAdminTaskState();

        if (GetAdminTaskState() == AdminTaskState.NotPresent) return true;

        message = $"The Administrator logon task could not be removed (error {code}). {output.Trim()} " +
                  $"Run this once in an Administrator Command Prompt: schtasks /Delete /TN \"{AdminTaskName}\" /F";
        return false;
    }

    /// <summary>
    /// ISSUE_6: one truthful answer for "does KeyPulse start with Windows?", whether startup is
    /// driven by the Run key or by the elevated logon task.
    /// </summary>
    public static bool IsStartupEnabled()
    {
        var taskState = GetAdminTaskState();
        if (taskState != AdminTaskState.NotPresent) return taskState == AdminTaskState.Enabled;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void RunSetupAdminTask()
    {
        // Read the current startup intent BEFORE touching anything, so elevating cannot lose it.
        var wantsStartup = IsStartupEnabled();

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var taskCmd = $"/Create /F /TN \"{AdminTaskName}\" /TR \"\\\"{ExePath}\\\" --hidden\" /SC ONLOGON /RL HIGHEST /RU \"{user}\"";

        // ISSUE_14: never claim success without checking. A blocked schtasks used to delete the
        // user's startup entry and then report elevated mode as active.
        if (!RunSchtasks(taskCmd, out var exitCode, out var output))
        {
            LogCrash($"schtasks create failed ({exitCode}): {output}");
            MessageBox(IntPtr.Zero,
                "KeyPulse could not switch to Administrator mode.\n\nWindows refused to create the scheduled task (error " + exitCode + ").\nThis is usually blocked by company policy or security software.\n\nNothing was changed. KeyPulse will start normally.",
                "KeyPulse", 0x10);

            try { Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true }); } catch { }
            return;
        }

        InvalidateAdminTaskState();

        if (wantsStartup)
        {
            // The elevated logon task replaces the Run-key launcher; remove it so both do not fire.
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue(AppName, false);
            }
            catch (Exception ex) { LogDebug($"Run key cleanup failed: {ex.Message}"); }

            try { var vbs = Path.Combine(InstallDir, "boot.vbs"); if (File.Exists(vbs)) File.Delete(vbs); } catch { }
        }
        else
        {
            // The user did not have launch-on-boot; elevating must not silently turn it on.
            RunSchtasks($"/Change /TN \"{AdminTaskName}\" /DISABLE", out _, out _);
            InvalidateAdminTaskState();
        }

        ConfigStore.TryUpdate(c => c.LaunchOnBoot = wantsStartup, out _);

        try { Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true }); } catch { }
    }

    private static void RunRemoveAdminTask()
    {
        var wantsStartup = IsStartupEnabled();

        var deleted = RunSchtasks($"/Delete /TN \"{AdminTaskName}\" /F", out var exitCode, out var output);
        InvalidateAdminTaskState();

        if (!deleted)
        {
            LogCrash($"schtasks delete failed ({exitCode}): {output}");
            MessageBox(IntPtr.Zero,
                "KeyPulse could not remove the Administrator logon task (error " + exitCode + ").\n\nRun this once in an Administrator Command Prompt:\n\n    schtasks /Delete /TN \"" + AdminTaskName + "\" /F",
                "KeyPulse", 0x30);
        }
        else if (wantsStartup)
        {
            // ISSUE_6: restore the ordinary startup entry we removed when elevating.
            if (!SetStartup(true, out var startupError))
            {
                LogDebug($"Failed to restore Run-key startup after de-elevating: {startupError}");
            }
        }

        // explorer.exe re-launches KeyPulse at the normal (non-elevated) integrity level.
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{ExePath}\"", UseShellExecute = true }); } catch { }
    }

    // ---------------------------------------------------------------------

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

    private static bool SignalExistingInstance()
    {
        try
        {
            using var openWindowEvent = EventWaitHandle.OpenExisting(OpenWindowEventName);
            openWindowEvent.Set();
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to signal existing instance: {ex.Message}");
            return false;
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

    /// <summary>
    /// ISSUE_21: unpacks the graphics libraries and verifies them BY CONTENT.
    ///
    /// The old version compared file SIZE only, so a library damaged by a bad shutdown, a disk
    /// error or another program was never replaced as long as its length happened to match - the
    /// app then crashed on startup with no explanation until the user found and deleted the folder
    /// by hand. It also unpacked into %TEMP%, which every other process running as this user can
    /// write to, and then called SetDllDirectory on it - telling Windows to load code from a folder
    /// anything could substitute code into. Both are fixed here.
    /// </summary>
    private static void ExtractNativeLibs()
    {
        LogDebug("ExtractNativeLibs started");

        // Best-effort removal of the old %TEMP% location, for anyone upgrading.
        try { if (Directory.Exists(LegacyNativeLibDir)) Directory.Delete(LegacyNativeLibDir, true); } catch { }

        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames();
        var outDir = RuntimeLibDir;

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            // A read-only or missing install directory must not stop the app from starting.
            LogCrash($"Could not create the runtime library folder '{outDir}': {ex.Message}. Falling back to TEMP.");
            outDir = Path.Combine(Path.GetTempPath(), AppName + "_Runtime");
            try { Directory.CreateDirectory(outDir); } catch { }
        }

        SetDllDirectory(outDir);

        foreach (var res in resources)
        {
            if (!res.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(outDir, res.Split('.').SkipLast(1).Last() + ".dll");

            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream == null) continue;

                var expectedHash = ComputeHash(stream);

                if (File.Exists(target) && FileHashMatches(target, expectedHash))
                {
                    LogDebug($"{Path.GetFileName(target)} verified.");
                    continue;
                }

                LogDebug($"Extracting {target}");
                stream.Position = 0;

                var tempTarget = target + ".tmp";
                using (var fs = File.Create(tempTarget))
                {
                    stream.CopyTo(fs);
                }

                try
                {
                    // File.Move with overwrite is atomic enough here and, unlike delete-then-move,
                    // never leaves the folder with no library at all.
                    File.Move(tempTarget, target, true);
                }
                catch (Exception moveEx)
                {
                    // Locked because another KeyPulse instance has it loaded; the copy on disk is
                    // then already the right one or will be replaced on the next launch.
                    LogDebug($"Could not replace {Path.GetFileName(target)}: {moveEx.Message}");
                    try { File.Delete(tempTarget); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to extract {res}: {ex}");
            }
        }

        LogDebug("ExtractNativeLibs finished");
    }

    private static byte[] ComputeHash(Stream stream)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(stream);
    }

    private static bool FileHashMatches(string path, byte[] expectedHash)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var actual = ComputeHash(fs);
            if (actual.Length != expectedHash.Length) return false;
            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expectedHash[i]) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRunningFromInstallPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/').Equals(InstallDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetStartup(bool enable, out string error)
    {
        error = string.Empty;

        // When the elevated logon task owns startup, toggle the task instead of the Run key so the
        // two launchers can never both fire. ISSUE_6.
        var taskState = GetAdminTaskState();
        if (taskState != AdminTaskState.NotPresent)
        {
            var verb = enable ? "/ENABLE" : "/DISABLE";
            var changed = RunSchtasks($"/Change /TN \"{AdminTaskName}\" {verb}", out var code, out var output);
            InvalidateAdminTaskState();

            if (!changed)
            {
                error = $"Windows refused to change the KeyPulse logon task (error {code}). {output.Trim()}";
                return false;
            }

            if (IsStartupEnabled() != enable)
            {
                error = "The KeyPulse logon task did not accept the change.";
                return false;
            }

            ConfigStore.TryUpdate(c => c.LaunchOnBoot = enable, out _);
            return true;
        }

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

                // ISSUE_12: launch once and stand down. The old script polled for a minute and
                // relaunched KeyPulse after the user had deliberately exited it.
                var vbsCode = $@"On Error Resume Next
Set WshShell = CreateObject(""WScript.Shell"")
WshShell.Run """"""{ExePath}"""" --hidden"", 0, False";
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

            ConfigStore.TryUpdate(c => c.LaunchOnBoot = enable, out _);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// ISSUE_19: the old version disposed the player on the same line it started playback, so the
    /// sound was cut off or never heard. PlaySync on a pool thread plays the clip to the end.
    /// </summary>
    public static void PlaySound(string soundName)
    {
        if (!SoundEnabled) return;

        try
        {
            var asm = typeof(Program).Assembly;
            var stream = asm.GetManifestResourceStream($"KeyPulse.Assets.{soundName}.wav");
            if (stream == null)
            {
                LogDebug($"Sound asset not embedded: {soundName}.wav");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (stream)
                    using (var player = new System.Media.SoundPlayer(stream))
                    {
                        player.PlaySync();
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
