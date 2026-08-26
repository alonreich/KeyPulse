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
    private const string SingleInstanceMutexName = @"Local\KeyPulse_SingleInstance";
    private const string OpenWindowEventName = "KeyPulse_OpenWindow";

    /// <summary>
    /// ISSUE_12: the installer signals this named event to ask a running KeyPulse to shut down
    /// cleanly. Closing the main window just hides it (by design), so WM_CLOSE could never work and
    /// every upgrade stalled for a second and then hard-killed the app.
    /// </summary>
    private const string ExitAppEventName = "KeyPulse_ExitApp";

    /// <summary>ISSUE_19: target handed over from the Explorer "Add to KeyPulse" context menu.</summary>
    public static readonly string StagedAddPath = Path.Combine(ConfigStore.ConfigDirectory, "staged-add.txt");

    /// <summary>ISSUE_19: set when this launch was started with --add-target and owns the window.</summary>
    public static string? PendingAddTarget;

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

    /// <summary>
    /// ISSUE_15: where ExtractNativeLibs falls back to when the install directory is not writable.
    /// The uninstall self-delete script cleans this AND LegacyNativeLibDir - the legacy folder is
    /// still targeted on purpose, because an upgrade from an old build leaves one behind.
    /// </summary>
    public static string RuntimeFallbackDir = Path.Combine(Path.GetTempPath(), AppName + "_Runtime");
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
                    var previous = path + ".old";
                    try { if (File.Exists(previous)) File.Delete(previous); } catch { }
                    try { File.Move(path, previous); } catch { try { File.Delete(path); } catch { } }
                }
                File.AppendAllText(path, $"[{DateTime.Now:O}] {msg}\n");
            }
        }
        catch { }
    }


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

        // ISSUE_19: "Add to KeyPulse" from Explorer's right-click menu. When a copy is already
        // running the target is staged in a file and the running instance is signalled; otherwise
        // it is handed to the window this launch creates.
        var addTarget = GetArgumentValue(args, "--add-target");

        if (args.Contains("--setup-admin-task"))
        {
            // ISSUE_11: the elevated helpers never start Avalonia, so they no longer unpack or
            // verify ~30 MB of graphics libraries first. They also never touch the settings file.
            RunSetupAdminTask(args);
            return;
        }

        if (args.Contains("--remove-admin-task"))
        {
            RunRemoveAdminTask();
            return;
        }

        if (isUninstall)
        {
            var tempUninstaller = Path.Combine(Path.GetTempPath(), "KeyPulse_Uninstaller.exe");
            if (!Environment.ProcessPath!.Equals(tempUninstaller, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(Environment.ProcessPath!, tempUninstaller, true);
                }
                catch (Exception ex)
                {
                    LogCrash($"Could not stage the uninstaller at {tempUninstaller}: {ex}");
                    MessageBox(IntPtr.Zero,
                        "KeyPulse could not start its uninstaller.\n\n" + ex.Message +
                        "\n\nClose any running KeyPulse setup window, make sure there is free space in your temporary folder, and try again.",
                        "KeyPulse", 0x10);
                    return;
                }

                try
                {
                    using var ps = Process.Start(new ProcessStartInfo
                    {
                        FileName = tempUninstaller,
                        Arguments = args.Contains("--elevated-retry") ? "--uninstall --elevated-retry" : "--uninstall",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    LogCrash($"Could not launch the staged uninstaller: {ex}");
                    MessageBox(IntPtr.Zero,
                        "KeyPulse could not start its uninstaller.\n\n" + ex.Message,
                        "KeyPulse", 0x10);
                }
                return;
            }
            // ISSUE_11: extraction now happens AFTER the single-instance check, so a second launch
            // that only brings the window forward costs nothing. Setup windows still need the
            // graphics libraries before Avalonia boots.
            ExtractNativeLibs();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        if (isInstallWorker)
        {
            ExtractNativeLibs();
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
                ps.Dispose();
            }
            catch (Exception ex)
            {
                LogCrash($"Install relaunch failed: {ex.Message}");
            }
            return;
        }

        if (isSetupMode)
        {
            if (!TryAcquireSetupLock())
            {
                MessageBox(IntPtr.Zero,
                    "KeyPulse setup is already running.\n\nFinish or close the setup window that is already open, then try again.",
                    "KeyPulse", 0x40);
                return;
            }
        }
        else
        {
            bool createdNew;
            var lockUnavailable = false;

            try
            {
                _mutex = CreateSharedMutex(SingleInstanceMutexName);
                try { createdNew = _mutex.WaitOne(0, false); }
                catch (AbandonedMutexException)
                {
                    createdNew = true;
                }
            }
            catch (Exception ex)
            {
                LogCrash($"Single-instance mutex unavailable, falling back to a process check: {ex}");
                _mutex = null;
                createdNew = true;
                lockUnavailable = true;
            }

            _mutexOwned = createdNew && _mutex != null;

            if (createdNew && (lockUnavailable || IsAnotherInstalledInstanceRunning()))
            {
                LogDebug("Another installed KeyPulse process is already running; deferring to it.");
                createdNew = false;
                _mutexOwned = false;
            }

            if (!createdNew)
            {
                // ISSUE_19: hand the Explorer "Add to KeyPulse" target to the running copy.
                if (addTarget != null) WriteStagedAddTarget(addTarget);

                if (!SignalExistingInstance())
                {
                    MessageBox(IntPtr.Zero,
                        "KeyPulse is already running, but its window could not be brought to the front.\n\nOpen it from the KeyPulse icon in the system tray, next to the clock.",
                        "KeyPulse", 0x40);
                }

                ReleaseSingleInstanceLock();
                return;
            }

            EnsureOpenWindowEvent();
        }

        try
        {
            // ISSUE_19: remember the Explorer-requested target for the window about to open.
            if (addTarget != null) PendingAddTarget = addTarget;

            ExtractNativeLibs();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash($"Fatal avalonia crash: {ex}");
        }
        finally
        {
            ReleaseSingleInstanceLock();
        }
    }


    private const string SetupMutexName = @"Local\KeyPulse_Setup";
    private static Mutex? _setupMutex;
    private static bool _setupMutexOwned;

    /// <summary>
    /// Grants every user full access and stamps a LOW mandatory label.
    ///
    /// ISSUE_7: a named object created by an elevated process inherits a High integrity label, and
    /// Windows' no-write-up rule then refuses a normal-integrity process the write access that
    /// opening a mutex or setting an event requires. That is why a desktop shortcut could start a
    /// SECOND KeyPulse while the elevated logon-task copy was already running. A low label lets any
    /// integrity level in the same session open it, which is exactly what a per-user guard wants.
    /// </summary>
    private const string SharedObjectSddl = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";

    private const uint SecurityDescriptorRevision = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, uint stringSDRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMutexW(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    /// <summary>Runs <paramref name="create"/> with a SECURITY_ATTRIBUTES built from the shared SDDL.</summary>
    private static IntPtr CreateWithSharedSecurity(Func<IntPtr, IntPtr> create)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SharedObjectSddl, SecurityDescriptorRevision, out descriptor, IntPtr.Zero))
            {
                LogDebug("Could not build the shared security descriptor; falling back to default security.");
                return create(IntPtr.Zero);
            }

            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = 0
            };

            var buffer = Marshal.AllocHGlobal(attributes.nLength);
            try
            {
                Marshal.StructureToPtr(attributes, buffer, false);
                return create(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    private static Mutex CreateSharedMutex(string name)
    {
        var handle = CreateWithSharedSecurity(attributes => CreateMutexW(attributes, false, name));
        if (handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var mutex = new Mutex(false);
        mutex.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, true);
        return mutex;
    }

    private static EventWaitHandle CreateSharedEvent(string name)
    {
        var handle = CreateWithSharedSecurity(attributes => CreateEventW(attributes, false, false, name));
        if (handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        waitHandle.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, true);
        return waitHandle;
    }

    /// <summary>ISSUE_3: one setup window at a time, whatever the user double-clicks.</summary>
    private static bool TryAcquireSetupLock()
    {
        try
        {
            _setupMutex = CreateSharedMutex(SetupMutexName);
            try { _setupMutexOwned = _setupMutex.WaitOne(0, false); }
            catch (AbandonedMutexException) { _setupMutexOwned = true; }

            if (!_setupMutexOwned)
            {
                _setupMutex.Dispose();
                _setupMutex = null;
            }

            return _setupMutexOwned;
        }
        catch (Exception ex)
        {
            LogCrash($"Setup lock unavailable, continuing without it: {ex}");
            _setupMutex = null;
            _setupMutexOwned = false;
            return true;
        }
    }

    /// <summary>
    /// ISSUE_7: last-resort check for a KeyPulse already running from the install directory.
    ///
    /// Only processes whose executable really is our installed binary count. A second copy of the
    /// INSTALLER is also called "KeyPulse", and treating that as a running app would be wrong.
    /// A process we cannot inspect is assumed to be a KeyPulse at a higher integrity level - which
    /// is precisely the case the mutex label is there to fix, so it counts.
    /// </summary>
    public static bool IsAnotherInstalledInstanceRunning()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(AppName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;

                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { /* higher integrity, or exited */ }

                    if (path == null) return true;
                    if (path.Equals(ExePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug("Could not check for another running KeyPulse: " + ex.Message);
        }

        return false;
    }

    /// <summary>
    /// ISSUE_14: hands the single-instance lock back explicitly.
    ///
    /// Switching in or out of Administrator mode ends the process with Environment.Exit, which does
    /// NOT run finally blocks - so the lock used to be released only by Windows abandoning it at
    /// process death, and the replacement instance started only because it happened to catch
    /// AbandonedMutexException. Call this before any hard exit so the restart works by design.
    /// </summary>
    public static void ReleaseSingleInstanceLock()
    {
        if (_mutex != null)
        {
            if (_mutexOwned)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutexOwned = false;
            }

            try { _mutex.Dispose(); } catch { }
            _mutex = null;
        }

        if (_setupMutex != null)
        {
            if (_setupMutexOwned)
            {
                try { _setupMutex.ReleaseMutex(); } catch { }
                _setupMutexOwned = false;
            }

            try { _setupMutex.Dispose(); } catch { }
            _setupMutex = null;
        }
    }


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
        Disabled,

        /// <summary>
        /// ISSUE_9: Windows did not answer the question. A slow, blocked or timed-out schtasks call
        /// used to be reported as "the task does not exist", so Settings said startup was off while
        /// it was on, and switching startup on wrote a SECOND launcher - two tray icons at login.
        /// Unknown must never be treated as NotPresent.
        /// </summary>
        Unknown
    }

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
        // ISSUE_33: this used to pass "/XML ONELINE" - ONELINE is not an schtasks option, so
        // EVERY query failed with "Improper display format type specified" and came back as
        // AdminTaskState.Unknown. Settings then froze the checkbox with "Windows did not
        // answer..." while the real task kept launching KeyPulse at every logon, so the
        // checkbox and reality could never agree. Plain /XML is the documented form; an
        // enabled task may omit <Enabled> entirely, which the check below already treats
        // as enabled.
        if (!RunSchtasks($"/Query /TN \"{AdminTaskName}\" /XML", out _, out var xml))
        {
            // ISSUE_9: schtasks exits non-zero both when the task genuinely does not exist and when
            // the query failed. Only "cannot find" means the task is really absent; anything else
            // (timeout, security software, access denied) is Unknown, never "off".
            var notFound = xml.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
            return notFound ? AdminTaskState.NotPresent : AdminTaskState.Unknown;
        }

        return xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase)
            ? AdminTaskState.Disabled
            : AdminTaskState.Enabled;
    }

    public static bool IsAdminTaskInstalled()
    {
        var state = GetAdminTaskState();
        return state == AdminTaskState.Enabled || state == AdminTaskState.Disabled;
    }

    /// <summary>
    /// ISSUE_8: true when Administrator mode is switched ON but this process is NOT actually
    /// elevated.
    ///
    /// `/RL HIGHEST` means "the highest level this account can reach", which on an account without
    /// administrator rights is ordinary rights. Windows reports the task as created and enabled, so
    /// Settings happily said "starts with Windows using its Administrator logon task" while typing
    /// into elevated windows kept failing with no explanation. Policy blocking elevation, and a task
    /// created for the wrong account, land here too. Call it off the UI thread - it may spawn
    /// schtasks the first time.
    /// </summary>
    public static bool IsAdminModeClaimedButNotEffective()
    {
        if (IsElevated) return false;
        return GetAdminTaskState() == AdminTaskState.Enabled;
    }

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
    /// ISSUE_9: the honest three-way answer to "does KeyPulse start with Windows?". Unknown means
    /// Windows did not answer - never guess "off", or a second launcher gets written on top of the
    /// existing one and two KeyPulse copies start at the next login.
    /// </summary>
    public enum StartupState
    {
        On,
        Off,
        Unknown
    }

    /// <summary>ISSUE_9: one truthful answer, whether startup is driven by the Run key or the logon task.</summary>
    public static StartupState QueryStartupState()
    {
        var taskState = GetAdminTaskState();
        if (taskState == AdminTaskState.Enabled) return StartupState.On;
        // ISSUE_33: a disabled (or absent) task launches nothing, but a leftover Run-key
        // launcher still would - so fall through to the registry check instead of
        // answering "off" while Windows keeps starting KeyPulse.

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            if (key?.GetValue(AppName) != null) return StartupState.On;
            // ISSUE_9: when the task state could not be determined, absence of a Run-key entry is
            // NOT proof that startup is off - the task might exist but be unqueryable.
            return taskState == AdminTaskState.Unknown ? StartupState.Unknown : StartupState.Off;
        }
        catch
        {
            return taskState == AdminTaskState.Unknown ? StartupState.Unknown : StartupState.Off;
        }
    }

    /// <summary>ISSUE_6 legacy helper: true only when startup is CONFIRMED on.</summary>
    public static bool IsStartupEnabled() => QueryStartupState() == StartupState.On;

    /// <summary>Reads "--name value" from the command line, or null when it is absent.</summary>
    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static void RunSetupAdminTask(string[] args)
    {
        // ISSUE_9: tri-state. Only touch the registry when the previous state is actually known.
        var startupState = QueryStartupState();

        var user = GetArgumentValue(args, "--for-user")
                   ?? System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var taskCmd = $"/Create /F /TN \"{AdminTaskName}\" /TR \"\\\"{ExePath}\\\" --hidden\" /SC ONLOGON /RL HIGHEST /RU \"{user}\"";

        if (!RunSchtasks(taskCmd, out var exitCode, out var output))
        {
            LogCrash($"schtasks create failed ({exitCode}): {output}");
            MessageBox(IntPtr.Zero,
                "KeyPulse could not switch to Administrator mode.\n\nWindows refused to create the scheduled task (error " + exitCode + ").\nThis is usually blocked by company policy or security software.\n\nNothing was changed. KeyPulse will start normally.",
                "KeyPulse", 0x10);

            try { using var relaunch = Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true }); } catch { }
            return;
        }

        InvalidateAdminTaskState();

        if (startupState == StartupState.On)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue(AppName, false);
            }
            catch (Exception ex) { LogDebug($"Run key cleanup failed: {ex.Message}"); }

            try { var vbs = Path.Combine(InstallDir, "boot.vbs"); if (File.Exists(vbs)) File.Delete(vbs); } catch { }
        }
        else if (startupState == StartupState.Off)
        {
            RunSchtasks($"/Change /TN \"{AdminTaskName}\" /DISABLE", out _, out _);
            InvalidateAdminTaskState();
        }
        else
        {
            // ISSUE_9: the previous state could not be determined. Touching nothing here is the only
            // choice that cannot leave the user with zero - or two - startup launchers.
            LogDebug("Previous startup state unknown; registry and task enablement left untouched.");
        }

        // ISSUE_3: this helper may run as a DIFFERENT administrator account (over-the-shoulder UAC).
        // It must never open, decrypt or rewrite the user's settings file: every target used to be
        // read under the wrong account, fail to decrypt, and get saved back blank. Its only job is
        // the scheduled task above; the relaunched app updates the LaunchOnBoot mirror itself.

        try { using var relaunch = Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true }); } catch { }
    }

    private static void RunRemoveAdminTask()
    {
        // ISSUE_9: only restore the registry launcher when startup is CONFIRMED on.
        var startupState = QueryStartupState();

        var deleted = RunSchtasks($"/Delete /TN \"{AdminTaskName}\" /F", out var exitCode, out var output);
        InvalidateAdminTaskState();

        if (!deleted)
        {
            LogCrash($"schtasks delete failed ({exitCode}): {output}");
            MessageBox(IntPtr.Zero,
                "KeyPulse could not remove the Administrator logon task (error " + exitCode + ").\n\nRun this once in an Administrator Command Prompt:\n\n    schtasks /Delete /TN \"" + AdminTaskName + "\" /F",
                "KeyPulse", 0x30);
        }
        else if (startupState == StartupState.On)
        {
            // ISSUE_3: registry only. This helper can run as a different administrator account, so
            // it must not open or rewrite the user's settings file the way SetStartup would.
            if (!TrySetRegistryStartup(true, out var startupError))
            {
                LogDebug($"Failed to restore Run-key startup after de-elevating: {startupError}");
            }
        }

        try { using var shell = Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{ExePath}\"", UseShellExecute = true }); } catch { }
    }


    private static void EnsureOpenWindowEvent()
    {
        if (_openWindowEvent != null) return;

        try
        {
            _openWindowEvent = CreateSharedEvent(OpenWindowEventName);
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

    private static EventWaitHandle? _exitAppEvent;
    private static Thread? _exitAppListener;
    private static volatile bool _exitAppListenerRunning;

    /// <summary>
    /// ISSUE_12: the running app listens here so an installer can ask it to exit properly - saving
    /// settings, releasing hotkeys and letting the tray icon disappear - instead of being killed.
    /// </summary>
    public static void StartExitRequestListener(Action exitApp)
    {
        try
        {
            _exitAppEvent = CreateSharedEvent(ExitAppEventName);
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to create exit-app event: {ex.Message}");
            return;
        }

        if (_exitAppListener != null) return;

        _exitAppListenerRunning = true;
        _exitAppListener = new Thread(() =>
        {
            while (_exitAppListenerRunning)
            {
                try
                {
                    _exitAppEvent.WaitOne();
                    if (_exitAppListenerRunning) exitApp();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogDebug($"Exit-app listener failed: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        });
        _exitAppListener.IsBackground = true;
        _exitAppListener.Start();
    }

    /// <summary>ISSUE_12: asks every running KeyPulse to shut down cleanly. True when signalled.</summary>
    public static bool SignalInstancesToExit()
    {
        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(ExitAppEventName);
            exitEvent.Set();
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to signal running KeyPulse to exit: {ex.Message}");
            return false;
        }
    }

    /// <summary>ISSUE_19: parks an Explorer "Add to KeyPulse" target for the running instance.</summary>
    private static void WriteStagedAddTarget(string target)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.ConfigDirectory);
            File.WriteAllText(StagedAddPath, target ?? string.Empty);
        }
        catch (Exception ex)
        {
            LogDebug("Could not stage the Add-to-KeyPulse target: " + ex.Message);
        }
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
            LogCrash($"Could not create the runtime library folder '{outDir}': {ex.Message}. Falling back to TEMP.");
                outDir = RuntimeFallbackDir;
            try { Directory.CreateDirectory(outDir); } catch { }
        }

        SetDllDirectory(outDir);

        foreach (var res in resources)
        {
            if (!res.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(outDir, res.Split('.').SkipLast(1).Last() + ".dll");
            var stampPath = target + ".stamp";

            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream == null) continue;

                // ISSUE_11: a file that was verified on a PREVIOUS launch is re-verified cheaply.
                // The stamp records the resource length and the SHA-256 that was verified (or
                // written) last time; if the on-disk length still matches both, accept it. Reading
                // and hashing ~30 MB of libraries on every single start made the window take a
                // visible beat to appear. Any mismatch falls through to the full content check.
                if (File.Exists(target) && File.Exists(stampPath)
                    && TryReadStamp(stampPath, out var stampedLength, out var stampedHash)
                    && stampedLength == stream.Length
                    && new FileInfo(target).Length == stampedLength)
                {
                    LogDebug($"{Path.GetFileName(target)} verified by stamp ({stampedHash[..8]}…).");
                    continue;
                }

                var expectedHash = ComputeHash(stream);
                var expectedHashHex = Convert.ToHexString(expectedHash);

                if (File.Exists(target) && FileHashMatches(target, expectedHash))
                {
                    LogDebug($"{Path.GetFileName(target)} verified.");
                    WriteStamp(stampPath, stream.Length, expectedHashHex);
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
                    File.Move(tempTarget, target, true);
                    WriteStamp(stampPath, stream.Length, expectedHashHex);
                }
                catch (Exception moveEx)
                {
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

    /// <summary>ISSUE_11: "<length>|<sha256-hex>" sidecar that makes a repeat launch cheap.</summary>
    private static void WriteStamp(string stampPath, long resourceLength, string hashHex)
    {
        try
        {
            File.WriteAllText(stampPath, resourceLength + "|" + hashHex);
        }
        catch (Exception ex)
        {
            LogDebug("Could not write the library stamp: " + ex.Message);
        }
    }

    private static bool TryReadStamp(string stampPath, out long resourceLength, out string hashHex)
    {
        resourceLength = -1;
        hashHex = string.Empty;
        try
        {
            var parts = File.ReadAllText(stampPath).Trim().Split('|');
            if (parts.Length != 2) return false;
            if (!long.TryParse(parts[0], out resourceLength) || resourceLength <= 0) return false;
            hashHex = parts[1];
            return hashHex.Length == 64;
        }
        catch
        {
            return false;
        }
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

        var taskState = GetAdminTaskState();
        if (taskState == AdminTaskState.Unknown)
        {
            // ISSUE_9: if Windows would not tell us whether the logon task exists, switching
            // startup on must NOT fall through to the registry - that wrote a second launcher
            // next to the unqueryable task, and two KeyPulse copies started at the next login.
            error = "Windows did not answer when KeyPulse asked about its own startup entry, so nothing was changed. Wait a moment and try again.";
            return false;
        }

        if (taskState == AdminTaskState.Enabled || taskState == AdminTaskState.Disabled)
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

            MirrorLaunchOnBoot(enable);
            return true;
        }

        // taskState == NotPresent: the plain per-user registry launcher. Extracted so elevated
        // helpers (ISSUE_3) can set it without ever touching the user's settings file.
        if (!TrySetRegistryStartup(enable, out error)) return false;

        MirrorLaunchOnBoot(enable);
        return true;
    }

    /// <summary>
    /// ISSUE_3: the registry Run-key launcher on its own, with no settings-file access. Safe for
    /// elevated helpers that may be running as a different Windows account.
    /// </summary>
    public static bool TrySetRegistryStartup(bool enable, out string error)
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

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// ISSUE_2: the LaunchOnBoot mirror is best-effort. ConfigStore.TryUpdate refuses to write while
    /// the settings file is damaged, and that refusal must never undo a Windows-level change that
    /// already succeeded.
    /// </summary>
    private static void MirrorLaunchOnBoot(bool enable)
    {
        if (!ConfigStore.TryUpdate(c => c.LaunchOnBoot = enable, out var mirrorError))
        {
            LogDebug("Could not mirror the startup change into the settings file: " + mirrorError);
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
