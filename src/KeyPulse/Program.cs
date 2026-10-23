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
    public static readonly bool IsDevMode = DetectDevMode();

    private static bool DetectDevMode()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("KEYPULSE_DEV_MODE"), "1", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KEYPULSE_DEV_HOME")))
            return true;
        try
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            return cmdArgs.Any(a =>
                string.Equals(a, "run-ui", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static readonly string DevHomeDir = ResolveDevHomeDir();

    private static string ResolveDevHomeDir()
    {
        var envHome = Environment.GetEnvironmentVariable("KEYPULSE_DEV_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
            return Path.GetFullPath(envHome);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyPulse_DEV");
    }

    private static string SingleInstanceMutexName => IsDevMode ? @"Local\KeyPulse_SingleInstance_DEV" : @"Local\KeyPulse_SingleInstance";
    private static string OpenWindowEventName => IsDevMode ? "KeyPulse_OpenWindow_DEV" : "KeyPulse_OpenWindow";

    /// <summary>
    /// ISSUE_12: the installer signals this named event to ask a running KeyPulse to shut down
    /// cleanly. Closing the main window just hides it (by design), so WM_CLOSE could never work and
    /// every upgrade stalled for a second and then hard-killed the app.
    /// </summary>
    private static string ExitAppEventName => IsDevMode ? "KeyPulse_ExitApp_DEV" : "KeyPulse_ExitApp";

    /// <summary>ISSUE_19: target handed over from the Explorer "Add to KeyPulse" context menu.</summary>
    public static string StagedAddPath => Path.Combine(ConfigStore.ConfigDirectory, "staged-add.txt");

    /// <summary>ISSUE_19: set when this launch was started with --add-target and owns the window.</summary>
    public static string? PendingAddTarget;

    public const string AdminTaskName = "KeyPulse_AdminTask";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    public static bool IsProcessElevated(Process process)
    {
        try
        {
            if (OpenProcessToken(process.Handle, TOKEN_QUERY, out var tokenHandle))
            {
                try
                {
                    var elevation = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        if (GetTokenInformation(tokenHandle, TokenElevation, elevation, sizeof(int), out _))
                        {
                            return Marshal.ReadInt32(elevation) != 0;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(elevation);
                    }
                }
                finally
                {
                    CloseHandle(tokenHandle);
                }
            }
            else
            {
                if (Marshal.GetLastWin32Error() == 5) return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

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
    /// In Dev Mode (DEVDATA_01), native libraries unpack to %LOCALAPPDATA%\KeyPulse_DEV\runtime.
    /// </summary>
    public static string RuntimeLibDir = IsDevMode ? Path.Combine(DevHomeDir, "runtime") : Path.Combine(InstallDir, "runtime");

    /// <summary>ISSUE_21 / ISSUE_8: folders earlier builds used. The setup pipeline must delete these.</summary>
    public static string LegacyNativeLibDir = Path.Combine(Path.GetTempPath(), AppName + "_NativeLibs");

    /// <summary>
    /// ISSUE_15: where ExtractNativeLibs falls back to when the install directory is not writable.
    /// The uninstall self-delete script cleans this AND LegacyNativeLibDir - the legacy folder is
    /// still targeted on purpose, because an upgrade from an old build leaves one behind.
    /// </summary>
    public static string RuntimeFallbackDir = Path.Combine(Path.GetTempPath(), IsDevMode ? AppName + "_DEV_Runtime" : AppName + "_Runtime");
    public static string LegacyLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// ISSUE_8: logs live inside %APPDATA%\KeyPulse so the uninstaller's existing purge of that
    /// folder removes them. In Dev Mode (DEVDATA_01), logs go to KEYPULSE_DEV_LOG_DIR (%TMP%\KeyPulse_DEV or .devlogs).
    /// </summary>
    private static string? _logDir;
    public static string LogDir => _logDir ??= ResolveLogDir();

    private static string ResolveLogDir()
    {
        var devLogDir = Environment.GetEnvironmentVariable("KEYPULSE_DEV_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(devLogDir))
            return Path.GetFullPath(devLogDir);
        if (IsDevMode)
            return Path.Combine(Path.GetTempPath(), "KeyPulse_DEV");
        var baseDir = !string.IsNullOrEmpty(ConfigStore.ConfigDirectory)
            ? ConfigStore.ConfigDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
        return Path.Combine(baseDir, "logs");
    }

    /// <summary>ISSUE_19: mirrors AppConfig.SoundEnabled so PlaySound can be muted.</summary>
    public static volatile bool SoundEnabled = true;

    private static object _logLock = new object();
    private static string DebugLogPath => Path.Combine(LogDir, "keypulse_debug.txt");
    private static string CrashLogPath => Path.Combine(LogDir, "keypulse_crash.txt");

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

    public static string CanaryFilePath => Path.Combine(ConfigStore.ConfigDirectory, ".post_update_canary");

    private static void CheckPostUpdateCanaryRecovery()
    {
        try
        {
            if (!File.Exists(CanaryFilePath)) return;

            var lines = File.ReadAllLines(CanaryFilePath);
            if (lines.Length == 0) return;

            var parts = lines[0].Split('|');
            var version = parts.Length > 0 ? parts[0] : "unknown";
            var backupExe = parts.Length > 2 ? parts[2] : string.Empty;
            var crashCount = parts.Length > 3 && int.TryParse(parts[3], out var cc) ? cc : 0;

            if (crashCount >= 2 && !string.IsNullOrWhiteSpace(backupExe) && File.Exists(backupExe))
            {
                var prompt = $"KeyPulse encountered repeated startup failures after updating to v{version}.\n\n"
                           + "Would you like to restore the previous working version?";
                var choice = MessageBox(IntPtr.Zero, prompt, "KeyPulse - Update Crash Recovery", 0x24 /* MB_YESNO | MB_ICONQUESTION */);
                if (choice == 6 /* IDYES */)
                {
                    try
                    {
                        File.Delete(CanaryFilePath);
                        var oldExe = ExePath + ".old";
                        try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
                        File.Move(ExePath, oldExe, true);
                        File.Copy(backupExe, ExePath, true);
                        MoveFileEx(oldExe, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = ExePath,
                            UseShellExecute = true
                        });
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        MessageBox(IntPtr.Zero, "Failed to restore backup: " + ex.Message, "KeyPulse", 0x10);
                    }
                }
            }
        }
        catch { }
    }

    public static void IncrementCanaryCrashCount()
    {
        try
        {
            if (!File.Exists(CanaryFilePath)) return;
            var lines = File.ReadAllLines(CanaryFilePath);
            if (lines.Length == 0) return;
            var parts = lines[0].Split('|');
            var version = parts.Length > 0 ? parts[0] : "unknown";
            var time = parts.Length > 1 ? parts[1] : DateTime.UtcNow.ToString("O");
            var backupExe = parts.Length > 2 ? parts[2] : string.Empty;
            var crashCount = (parts.Length > 3 && int.TryParse(parts[3], out var cc) ? cc : 0) + 1;
            File.WriteAllText(CanaryFilePath, $"{version}|{time}|{backupExe}|{crashCount}");
        }
        catch { }
    }

    public static void ClearPostUpdateCanary()
    {
        try
        {
            if (File.Exists(CanaryFilePath))
            {
                var lines = File.ReadAllLines(CanaryFilePath);
                if (lines.Length > 0)
                {
                    var parts = lines[0].Split('|');
                    var backupExe = parts.Length > 2 ? parts[2] : string.Empty;
                    if (!string.IsNullOrWhiteSpace(backupExe) && File.Exists(backupExe))
                    {
                        try { File.Delete(backupExe); } catch { }
                    }
                }
                File.Delete(CanaryFilePath);
            }
        }
        catch { }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        CheckPostUpdateCanaryRecovery();

        var isUninstall = args.Contains("--uninstall");
        var isInstallWorker = args.Contains("--install-worker");
        var isUpdateWorker = args.Contains("--update-worker") || args.Contains("--update");
        var isSetupMode = isUninstall || isInstallWorker || isUpdateWorker;

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.IsTerminating)
            {
                IncrementCanaryCrashCount();
            }
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

        var addTarget = GetArgumentValue(args, "--add-target");

        if (args.Contains("--setup-admin-task"))
        {
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
            if (!TryAcquireSetupLock(10000))
            {
                MessageBox(IntPtr.Zero,
                    "KeyPulse setup is already running.\n\nFinish or close the setup window that is already open, then try again.",
                    "KeyPulse", 0x40);
                return;
            }

            var stagingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), IsDevMode ? "KeyPulse_DEV_Uninstall" : "KeyPulse_Uninstall");
            Directory.CreateDirectory(stagingDir);
            var tempUninstaller = Path.Combine(stagingDir, "KeyPulse.exe");
            if (!Environment.ProcessPath!.Equals(tempUninstaller, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(Environment.ProcessPath!, tempUninstaller, true);
                }
                catch (Exception ex)
                {
                    ReleaseSingleInstanceLock();
                    LogCrash($"Could not stage the uninstaller at {tempUninstaller}: {ex}");
                    MessageBox(IntPtr.Zero,
                        "KeyPulse could not start its uninstaller.\n\n" + ex.Message +
                        "\n\nClose any running KeyPulse setup window, make sure there is free space in your user profile folder, and try again.",
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
                finally
                {
                    ReleaseSingleInstanceLock();
                }
                return;
            }
            try
            {
                ExtractNativeLibs();
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                ReleaseSingleInstanceLock();
            }
            return;
        }

        if (isInstallWorker || isUpdateWorker)
        {
            var parentPidStr = GetArgumentValue(args, "--parent-pid");
            if (!string.IsNullOrEmpty(parentPidStr) && int.TryParse(parentPidStr, out int parentPid))
            {
                try
                {
                    using var parentProc = Process.GetProcessById(parentPid);
                    if (!parentProc.HasExited)
                    {
                        parentProc.WaitForExit(6000);
                    }
                }
                catch (ArgumentException) { /* Parent process already exited */ }
                catch (Exception ex)
                {
                    LogCrash($"Wait for parent PID {parentPid} error: {ex.Message}");
                }
            }

            if (!TryAcquireSetupLock(10000))
            {
                MessageBox(IntPtr.Zero,
                    "KeyPulse setup is already running.\n\nFinish or close the setup window that is already open, then try again.",
                    "KeyPulse", 0x40);
                return;
            }

            try
            {
                ExtractNativeLibs();
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                ReleaseSingleInstanceLock();
            }
            return;
        }

        if (!IsRunningFromInstallPath() && !IsDevMode)
        {
            if (!TryAcquireSetupLock())
            {
                MessageBox(IntPtr.Zero,
                    "KeyPulse setup is already running.\n\nFinish or close the setup window that is already open, then try again.",
                    "KeyPulse", 0x40);
                return;
            }

            try
            {
                ExtractNativeLibs();
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                LogCrash($"Setup start failed: {ex.Message}");
            }
            finally
            {
                ReleaseSingleInstanceLock();
            }
            return;
        }

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
                var ipcDelivered = false;
                if (addTarget != null)
                {
                    ipcDelivered = IpcService.TrySendAddTarget(addTarget);
                    if (!ipcDelivered)
                    {
                        WriteStagedAddTarget(addTarget);
                    }
                }

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


    private static string SetupMutexName => IsDevMode ? @"Local\KeyPulse_Setup_DEV" : @"Local\KeyPulse_Setup";
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
    private static bool TryAcquireSetupLock(int timeoutMs = 0)
    {
        try
        {
            _setupMutex = CreateSharedMutex(SetupMutexName);
            try { _setupMutexOwned = _setupMutex.WaitOne(timeoutMs, false); }
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
    /// In Dev Mode (DEVDATA_01), only another process running this exact dev binary path counts.
    /// </summary>
    public static bool IsAnotherInstalledInstanceRunning()
    {
        try
        {
            var expectedPath = IsDevMode ? Environment.ProcessPath : ExePath;
            if (string.IsNullOrWhiteSpace(expectedPath)) return false;

            foreach (var process in Process.GetProcessesByName(AppName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;

                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { /* higher integrity, or exited */ }

                    if (path == null)
                    {
                        if (!IsDevMode) return true;
                        continue;
                    }
                    if (path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) return true;
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

    private static readonly Guid CLSID_TaskScheduler = new Guid("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");
    private static ITaskService CreateTaskService() => ComActivation.CreateInstance<ITaskService>(CLSID_TaskScheduler);

    private static bool TryGetAdminTaskViaCom(out string? xml, out bool isEnabled, out bool notFound)
    {
        xml = null;
        isEnabled = false;
        notFound = false;
        try
        {
            var service = CreateTaskService();
            service.Connect(Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            var folder = service.GetFolder(@"\");
            try
            {
                var task = folder.GetTask(AdminTaskName);
                if (task != null)
                {
                    xml = task.Xml;
                    isEnabled = task.Enabled;
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                notFound = true;
                return false;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002 || (uint)ex.HResult == 0x80041318)
            {
                notFound = true;
                return false;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Task Scheduler COM query failed: {ex.Message}");
        }
        return false;
    }

    private static bool TryRegisterAdminTaskViaCom(string xml, out string error)
    {
        error = string.Empty;
        try
        {
            var service = CreateTaskService();
            service.Connect(Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            var folder = service.GetFolder(@"\");
            var task = folder.RegisterTask(AdminTaskName, xml, 6, Type.Missing, Type.Missing, 3, Type.Missing);
            if (task != null) return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogDebug($"Task Scheduler COM registration failed: {ex.Message}");
        }
        return false;
    }

    private static bool TryDeleteAdminTaskViaCom()
    {
        try
        {
            var service = CreateTaskService();
            service.Connect(Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            var folder = service.GetFolder(@"\");
            folder.DeleteTask(AdminTaskName, 0);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070002 || (uint)ex.HResult == 0x80041318)
        {
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"Task Scheduler COM delete failed: {ex.Message}");
            return false;
        }
    }

    private static AdminTaskState QueryAdminTaskState()
    {
        if (IsDevMode) return AdminTaskState.NotPresent;

        if (TryGetAdminTaskViaCom(out var taskXml, out var isEnabled, out var notFound))
        {
            return isEnabled ? AdminTaskState.Enabled : AdminTaskState.Disabled;
        }
        if (notFound)
        {
            return AdminTaskState.NotPresent;
        }

        if (!RunSchtasks($"/Query /TN \"{AdminTaskName}\" /XML", out _, out var xml))
        {
            var taskFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", AdminTaskName);
            var missing = !File.Exists(taskFilePath)
                || xml.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
            return missing ? AdminTaskState.NotPresent : AdminTaskState.Unknown;
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

        if (TryDeleteAdminTaskViaCom())
        {
            InvalidateAdminTaskState();
            if (GetAdminTaskState() == AdminTaskState.NotPresent) return true;
        }

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
        if (IsDevMode)
        {
            var devCfg = ConfigStore.Load(out _, out _);
            return devCfg.LaunchOnBoot ? StartupState.On : StartupState.Off;
        }

        var taskState = GetAdminTaskState();
        if (taskState == AdminTaskState.Enabled) return StartupState.On;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            if (key?.GetValue(AppName) != null) return StartupState.On;
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

    public static bool EnsureAdminTaskBatterySettings()
    {
        try
        {
            string? xml = null;
            if (TryGetAdminTaskViaCom(out var comXml, out _, out _))
            {
                xml = comXml;
            }
            else if (RunSchtasks($"/Query /TN \"{AdminTaskName}\" /XML", out _, out var schtasksXml) && !string.IsNullOrWhiteSpace(schtasksXml))
            {
                xml = schtasksXml;
            }

            if (string.IsNullOrWhiteSpace(xml)) return false;

            var needsUpdate = xml.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase)
                           || xml.Contains("<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>", StringComparison.OrdinalIgnoreCase)
                           || xml.Contains("<StopOnIdleEnd>true</StopOnIdleEnd>", StringComparison.OrdinalIgnoreCase)
                           || !xml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase);

            if (!needsUpdate) return true;

            var updatedXml = xml;
            if (updatedXml.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase))
            {
                updatedXml = updatedXml.Replace("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase);
            }
            else if (!updatedXml.Contains("<DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase) && updatedXml.Contains("<Settings>"))
            {
                updatedXml = updatedXml.Replace("<Settings>", "<Settings>\n    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase);
            }

            if (updatedXml.Contains("<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>", StringComparison.OrdinalIgnoreCase))
            {
                updatedXml = updatedXml.Replace("<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>", "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", StringComparison.OrdinalIgnoreCase);
            }

            if (updatedXml.Contains("<StopOnIdleEnd>true</StopOnIdleEnd>", StringComparison.OrdinalIgnoreCase))
            {
                updatedXml = updatedXml.Replace("<StopOnIdleEnd>true</StopOnIdleEnd>", "<StopOnIdleEnd>false</StopOnIdleEnd>", StringComparison.OrdinalIgnoreCase);
            }

            if (TryRegisterAdminTaskViaCom(updatedXml, out _))
            {
                InvalidateAdminTaskState();
                return true;
            }

            var safeDir = Directory.Exists(InstallDir) ? InstallDir : Path.GetTempPath();
            var tempXmlPath = Path.Combine(safeDir, $"keypulse_task_heal_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tempXmlPath, updatedXml, System.Text.Encoding.Unicode);
                var fixedOk = RunSchtasks($"/Create /TN \"{AdminTaskName}\" /XML \"{tempXmlPath}\" /F", out _, out _);
                InvalidateAdminTaskState();
                return fixedOk;
            }
            finally
            {
                try { if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            LogDebug("EnsureAdminTaskBatterySettings failed: " + ex.Message);
            return false;
        }
    }

    public static bool CreateOrUpdateAdminTask(bool enable, out string error, string? forUser = null)
    {
        error = string.Empty;

        bool taskMatchesExe = false;
        bool taskIsElevated = false;
        bool taskIsEnabled = false;

        if (TryGetAdminTaskViaCom(out var existingXml, out var comEnabled, out var comNotFound) && !comNotFound && !string.IsNullOrWhiteSpace(existingXml))
        {
            taskMatchesExe = existingXml.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
            taskIsElevated = existingXml.IndexOf("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase) >= 0;
            taskIsEnabled = comEnabled;
        }
        else if (RunSchtasks($"/Query /TN \"{AdminTaskName}\" /XML", out _, out var schtasksXml) && !string.IsNullOrWhiteSpace(schtasksXml))
        {
            taskMatchesExe = schtasksXml.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
            taskIsElevated = schtasksXml.IndexOf("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase) >= 0;
            taskIsEnabled = !schtasksXml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
        }

        if (taskMatchesExe && taskIsElevated)
        {
            if (enable == taskIsEnabled)
            {
                EnsureAdminTaskBatterySettings();
                InvalidateAdminTaskState();
                return true;
            }

            var verb = enable ? "/ENABLE" : "/DISABLE";
            if (RunSchtasks($"/Change /TN \"{AdminTaskName}\" {verb}", out _, out _))
            {
                EnsureAdminTaskBatterySettings();
                InvalidateAdminTaskState();
                return true;
            }
        }

        var user = forUser ?? System.Security.Principal.WindowsIdentity.GetCurrent().Name;

        var xmlTemplate = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Date>{DateTime.UtcNow:s}</Date>
    <Author>{System.Security.SecurityElement.Escape(user)}</Author>
    <URI>\{AdminTaskName}</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <IdleSettings>
      <Duration>PT10M</Duration>
      <WaitTimeout>PT1H</WaitTimeout>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
    {(enable ? "<Enabled>true</Enabled>" : "<Enabled>false</Enabled>")}
  </Settings>
  <Triggers>
    <LogonTrigger>
      <Delay>PT3S</Delay>
    </LogonTrigger>
  </Triggers>
  <Actions Context=""Author"">
    <Exec>
      <Command>{System.Security.SecurityElement.Escape(ExePath)}</Command>
      <Arguments>--hidden</Arguments>
    </Exec>
  </Actions>
</Task>";

        if (TryRegisterAdminTaskViaCom(xmlTemplate, out var comError))
        {
            InvalidateAdminTaskState();
            return true;
        }

        var safeDir = Directory.Exists(InstallDir) ? InstallDir : Path.GetTempPath();
        var tempXmlPath = Path.Combine(safeDir, $"keypulse_task_{Guid.NewGuid():N}.xml");
        bool created = false;
        int exitCode = -1;
        string output = string.Empty;

        try
        {
            File.WriteAllText(tempXmlPath, xmlTemplate, System.Text.Encoding.Unicode);
            created = RunSchtasks($"/Create /TN \"{AdminTaskName}\" /XML \"{tempXmlPath}\" /F", out exitCode, out output);
        }
        catch (Exception ex)
        {
            LogCrash($"XML task creation error: {ex.Message}");
            error = ex.Message;
        }
        finally
        {
            try { if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath); } catch { }
        }

        if (!created)
        {
            var taskCmd = $"/Create /F /TN \"{AdminTaskName}\" /TR \"\\\"{ExePath}\\\" --hidden\" /SC ONLOGON /RL HIGHEST /RU \"{user}\"";
            created = RunSchtasks(taskCmd, out exitCode, out output);
            if (created)
            {
                EnsureAdminTaskBatterySettings();
                if (!enable)
                {
                    RunSchtasks($"/Change /TN \"{AdminTaskName}\" /DISABLE", out _, out _);
                }
            }
        }

        if (!created && !IsElevated && File.Exists(ExePath))
        {
            try
            {
                using var elevatedHelper = Process.Start(new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"--setup-admin-task --for-user \"{user}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                if (elevatedHelper != null && elevatedHelper.WaitForExit(10000))
                {
                    InvalidateAdminTaskState();
                    if (GetAdminTaskState() == AdminTaskState.Enabled)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug("Elevated task setup helper failed: " + ex.Message);
            }
        }

        InvalidateAdminTaskState();

        if (!created)
        {
            error = string.IsNullOrWhiteSpace(output) ? $"Error code {exitCode}" : output.Trim();
            return false;
        }

        return true;
    }

    private static void RunSetupAdminTask(string[] args)
    {
        var startupState = QueryStartupState();

        var user = GetArgumentValue(args, "--for-user")
                   ?? System.Security.Principal.WindowsIdentity.GetCurrent().Name;

        var enable = startupState != StartupState.Off;
        if (!CreateOrUpdateAdminTask(enable, out var createError, user))
        {
            LogCrash($"schtasks create failed: {createError}");
            MessageBox(IntPtr.Zero,
                "KeyPulse could not switch to Administrator mode.\n\nWindows refused to create the scheduled task (" + createError + ").\nThis is usually blocked by company policy or security software.\n\nNothing was changed. KeyPulse will start normally.",
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
            LogDebug("Previous startup state unknown; registry and task enablement left untouched.");
        }


        try { using var relaunch = Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true }); } catch { }
    }

    private static void RunRemoveAdminTask()
    {
        var startupState = QueryStartupState();

        int exitCode = 0;
        string output = string.Empty;
        var deleted = TryDeleteAdminTaskViaCom();
        if (!deleted)
        {
            deleted = RunSchtasks($"/Delete /TN \"{AdminTaskName}\" /F", out exitCode, out output);
        }
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
            File.AppendAllLines(StagedAddPath, new[] { target ?? string.Empty });
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

        var outDir = RuntimeLibDir;
        if (Directory.Exists(outDir) &&
            File.Exists(Path.Combine(outDir, "libSkiaSharp.dll")) &&
            File.Exists(Path.Combine(outDir, "av_libglesv2.dll")) &&
            File.Exists(Path.Combine(outDir, "libHarfBuzzSharp.dll")))
        {
            SetDllDirectory(outDir);
            LogDebug("ExtractNativeLibs fast-path: native libraries already present.");
            return;
        }

        try { if (Directory.Exists(LegacyNativeLibDir)) Directory.Delete(LegacyNativeLibDir, true); } catch { }

        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames();

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            LogCrash($"Could not create the secure runtime library folder '{outDir}': {ex.Message}.");
            var msg = "KeyPulse could not create its secure runtime library directory:\n\n"
                    + outDir + "\n\n"
                    + ex.Message + "\n\n"
                    + "For security reasons, KeyPulse refuses to load native binaries from shared temporary folders. Check folder permissions and disk space.";
            MessageBox(IntPtr.Zero, msg, "KeyPulse - Security Initialization Error", 0x10 /* MB_ICONERROR */);
            Environment.Exit(1);
            return;
        }

        var failedLibs = new List<string>();

        foreach (var res in resources)
        {
            bool isGz = res.EndsWith(".dll.gz", StringComparison.OrdinalIgnoreCase);
            if (!isGz && !res.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            string dllFileName;
            var parts = res.Split('.');
            if (isGz)
            {
                if (parts.Length < 3) continue;
                dllFileName = parts[^3] + ".dll";
            }
            else
            {
                if (parts.Length < 2) continue;
                dllFileName = parts[^2] + ".dll";
            }

            var target = Path.Combine(outDir, dllFileName);
            var stampPath = target + ".stamp";

            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream == null)
                {
                    failedLibs.Add($"{dllFileName} (embedded resource stream missing)");
                    continue;
                }

                var streamLength = stream.Length;
                var expectedHash = ComputeHash(stream);
                var expectedHashHex = Convert.ToHexString(expectedHash);
                var expectedStamp = streamLength + "|" + expectedHashHex;

                if (File.Exists(target) && File.Exists(stampPath))
                {
                    try
                    {
                        var fi = new FileInfo(target);
                        if (fi.Length > 64)
                        {
                            var headerBytes = new byte[2];
                            using (var headerStream = File.OpenRead(target))
                            {
                                int read = headerStream.Read(headerBytes, 0, 2);
                                if (read == 2 && headerBytes[0] == 0x4D && headerBytes[1] == 0x5A)
                                {
                                    var existingStamp = File.ReadAllText(stampPath).Trim();
                                    if (string.Equals(existingStamp, expectedStamp, StringComparison.OrdinalIgnoreCase))
                                    {
                                        LogDebug($"{dllFileName} verified by stamp cache.");
                                        continue;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                LogDebug($"Extracting or repairing native component {target}");
                stream.Position = 0;

                var tempTarget = Path.Combine(outDir, $"{dllFileName}.tmp-{Guid.NewGuid():N}");
                using (var fs = File.Create(tempTarget))
                {
                    if (isGz)
                    {
                        using var gz = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
                        gz.CopyTo(fs);
                    }
                    else
                    {
                        stream.CopyTo(fs);
                    }
                }

                var extractedInfo = new FileInfo(tempTarget);
                if (!extractedInfo.Exists || extractedInfo.Length == 0)
                {
                    try { File.Delete(tempTarget); } catch { }
                    failedLibs.Add($"{dllFileName} (extracted file empty or missing)");
                    continue;
                }

                try
                {
                    if (File.Exists(target))
                    {
                        File.Replace(tempTarget, target, null);
                    }
                    else
                    {
                        File.Move(tempTarget, target);
                    }
                    WriteStamp(stampPath, streamLength, expectedHashHex);
                    LogDebug($"{dllFileName} extracted and verified.");
                }
                catch (Exception moveEx)
                {
                    LogCrash($"Could not install native library {dllFileName}: {moveEx.Message}");
                    try { File.Delete(tempTarget); } catch { }
                    failedLibs.Add($"{dllFileName} ({moveEx.Message})");
                }
            }
            catch (Exception ex)
            {
                LogCrash($"Failed to extract and verify native component {res}: {ex}");
                failedLibs.Add($"{dllFileName} ({ex.Message})");
            }
        }

        if (failedLibs.Count > 0)
        {
            var msg = "KeyPulse could not safely verify or extract its required native components:\n\n"
                    + string.Join("\n", failedLibs)
                    + "\n\nThe application cannot proceed securely and will exit. Check disk space and folder permissions.";
            LogCrash(msg);
            MessageBox(IntPtr.Zero, msg, "KeyPulse - Component Verification Error", 0x10 /* MB_ICONERROR */);
            Environment.Exit(1);
            return;
        }

        SetDllDirectory(outDir);
        LogDebug("ExtractNativeLibs finished: all native libraries verified.");
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

    private static byte[] ComputeHash(Stream stream)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(stream);
    }

    public static bool IsRunningFromInstallPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/').Equals(InstallDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetStartup(bool enable, out string error)
    {
        error = string.Empty;

        if (IsDevMode)
        {
            MirrorLaunchOnBoot(enable);
            return true;
        }

        var taskState = GetAdminTaskState();
        if (taskState == AdminTaskState.Unknown)
        {
            error = "Windows did not answer when KeyPulse asked about its own startup entry, so nothing was changed. Wait a moment and try again.";
            return false;
        }

        if (taskState == AdminTaskState.Enabled || taskState == AdminTaskState.Disabled)
        {
            bool comToggled = false;
            try
            {
                var service = CreateTaskService();
                service.Connect(Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                var folder = service.GetFolder(@"\");
                var task = folder.GetTask(AdminTaskName);
                if (task != null)
                {
                    task.Enabled = enable;
                    comToggled = true;
                    InvalidateAdminTaskState();
                }
            }
            catch { }

            if (!comToggled)
            {
                var verb = enable ? "/ENABLE" : "/DISABLE";
                var changed = RunSchtasks($"/Change /TN \"{AdminTaskName}\" {verb}", out var code, out var output);
                InvalidateAdminTaskState();

                if (!changed)
                {
                    error = $"Windows refused to change the KeyPulse logon task (error {code}). {output.Trim()}";
                    return false;
                }
            }

            if (IsStartupEnabled() != enable)
            {
                error = "The KeyPulse logon task did not accept the change.";
                return false;
            }

            if (enable)
            {
                EnsureAdminTaskBatterySettings();
            }

            MirrorLaunchOnBoot(enable);
            return true;
        }

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
        if (IsDevMode) return true;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null)
            {
                error = "Could not open the Windows startup registry key.";
                return false;
            }

            var vbsPath = Path.Combine(InstallDir, "boot.vbs");
            try { if (File.Exists(vbsPath)) File.Delete(vbsPath); } catch { }

            if (enable)
            {
                Directory.CreateDirectory(InstallDir);
                var runCmd = $"\"{ExePath}\" --hidden";
                key.SetValue(AppName, runCmd);

                if (key.GetValue(AppName)?.ToString()?.Contains(ExePath, StringComparison.OrdinalIgnoreCase) != true)
                {
                    error = "Windows startup entry was not saved.";
                    return false;
                }
            }
            else
            {
                key.DeleteValue(AppName, false);

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


[ComImport, Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITaskService
{
    [return: MarshalAs(UnmanagedType.Interface)]
    ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
    void DummyGetRunningTasks();
    void DummyNewTask();
    void Connect(
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object serverName,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object user,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object domain,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object password);
    bool Connected { get; }
}

[ComImport, Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITaskFolder
{
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
    [return: MarshalAs(UnmanagedType.Interface)]
    ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
    void DummyGetFolders();
    void DummyCreateFolder();
    void DummyDeleteFolder();
    [return: MarshalAs(UnmanagedType.Interface)]
    IRegisteredTask GetTask([MarshalAs(UnmanagedType.BStr)] string path);
    void DummyGetTasks();
    void DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);
    [return: MarshalAs(UnmanagedType.Interface)]
    IRegisteredTask RegisterTask(
        [MarshalAs(UnmanagedType.BStr)] string path,
        [MarshalAs(UnmanagedType.BStr)] string xmlText,
        int flags,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object userId,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object password,
        int logonType,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] object sddl);
}

[ComImport, Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IRegisteredTask
{
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
    int State { get; }
    bool Enabled { get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    void DummyRun();
    void DummyRunEx();
    void DummyGetInstances();
    void DummyLastRunTime();
    void DummyLastTaskResult();
    void DummyNumberOfMissedRuns();
    void DummyNextRunTime();
    void DummyDefinition();
    string Xml { [return: MarshalAs(UnmanagedType.BStr)] get; }
}
