using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KeyPulse
{
    public enum DesktopOrganizerLoadStatus
    {
        Success,
        NotFound,
        Inaccessible,
        Corrupt,
        Recovered
    }

    public sealed class DesktopOrganizerLoadResult
    {
        public DesktopOrganizerLoadStatus Status { get; init; }
        public DesktopOrganizerConfig Config { get; init; } = new();
        public string ErrorMessage { get; init; } = string.Empty;
        public string? RecoveryPath { get; init; }
        public bool IsReadOnly => Status == DesktopOrganizerLoadStatus.Inaccessible || Status == DesktopOrganizerLoadStatus.Corrupt;
    }

    public static class DesktopOrganizerService
    {
        private const uint LVM_FIRST = 0x1000;
        private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
        private const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;
        private const uint LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
        private const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;
        private const uint LVM_GETITEMSPACING = LVM_FIRST + 51;

        private const int GWL_STYLE = -16;
        private const int LVS_AUTOARRANGE = 0x0100;

        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const uint MONITORINFOF_PRIMARY = 0x00000001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;

            public int Width => right - left;
            public int Height => bottom - top;
            public bool Contains(int x, int y) => x >= left && x <= right && y >= top && y <= bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        public class MonitorInfoDetail
        {
            public int Index { get; set; }
            public RECT MonitorRect { get; set; }
            public RECT WorkRect { get; set; }
            public bool IsPrimary { get; set; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private static bool SafeSendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint timeoutMs, out IntPtr result)
        {
            result = IntPtr.Zero;
            var ret = SendMessageTimeout(hWnd, Msg, wParam, lParam, SMTO_ABORTIFHUNG, timeoutMs, out result);
            return ret != IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        public static readonly string ConfigPath = Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.json");
        public static readonly string LkgConfigPath = Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.lkg.json");

        private static DesktopLayoutProfile? _lastUndoSnapshot;
        private static System.Threading.Timer? _displayChangeDebounceTimer;
        private static readonly object SyncLock = new();

        public static bool HasUndo => _lastUndoSnapshot != null;
        public static string LastUndoProfileName => _lastUndoSnapshot?.Name ?? string.Empty;

        public static DesktopOrganizerLoadResult LoadConfigDetailed()
        {
            lock (SyncLock)
            {
                if (!File.Exists(ConfigPath))
                {
                    if (File.Exists(LkgConfigPath))
                    {
                        if (TryReadValidConfig(LkgConfigPath, out var lkgConfig, out _))
                        {
                            Program.LogDebug("DesktopOrganizer: primary file missing, recovered from last-known-good.");
                            return new DesktopOrganizerLoadResult
                            {
                                Status = DesktopOrganizerLoadStatus.Recovered,
                                Config = lkgConfig!,
                                RecoveryPath = LkgConfigPath
                            };
                        }
                    }

                    return new DesktopOrganizerLoadResult
                    {
                        Status = DesktopOrganizerLoadStatus.NotFound,
                        Config = new DesktopOrganizerConfig()
                    };
                }

                string json;
                try
                {
                    using var fs = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    json = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    Program.LogCrash($"DesktopOrganizer: failed to read config ({ex.Message}). Activating protective mode.");
                    return new DesktopOrganizerLoadResult
                    {
                        Status = DesktopOrganizerLoadStatus.Inaccessible,
                        ErrorMessage = ex.Message,
                        Config = new DesktopOrganizerConfig()
                    };
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    var quarantine = QuarantineDamagedFile();
                    if (File.Exists(LkgConfigPath) && TryReadValidConfig(LkgConfigPath, out var lkgConfig, out _))
                    {
                        return new DesktopOrganizerLoadResult
                        {
                            Status = DesktopOrganizerLoadStatus.Recovered,
                            Config = lkgConfig!,
                            RecoveryPath = LkgConfigPath,
                            ErrorMessage = $"Primary layout file was empty. Quarantined to {quarantine} and recovered from last-known-good copy."
                        };
                    }

                    return new DesktopOrganizerLoadResult
                    {
                        Status = DesktopOrganizerLoadStatus.Corrupt,
                        ErrorMessage = "Primary layout file was empty and no recovery copy was available.",
                        Config = new DesktopOrganizerConfig()
                    };
                }

                try
                {
                    var cfg = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.DesktopOrganizerConfig);
                    if (cfg == null || cfg.Profiles == null)
                    {
                        throw new InvalidDataException("Deserialized layout configuration was empty or malformed.");
                    }

                    if (cfg.Profiles.Count == 0
                        && File.Exists(LkgConfigPath)
                        && TryReadValidConfig(LkgConfigPath, out var lkgWithProfiles, out _)
                        && lkgWithProfiles != null
                        && lkgWithProfiles.Profiles.Count > 0)
                    {
                        Program.LogDebug("DesktopOrganizer: primary file had 0 profiles while last-known-good had saved layouts; restoring from last-known-good.");
                        cfg.Profiles = lkgWithProfiles.Profiles;
                        if (string.IsNullOrEmpty(cfg.ActiveProfileId))
                        {
                            cfg.ActiveProfileId = lkgWithProfiles.ActiveProfileId;
                        }
                    }

                    return new DesktopOrganizerLoadResult
                    {
                        Status = DesktopOrganizerLoadStatus.Success,
                        Config = cfg
                    };
                }
                catch (Exception ex)
                {
                    var quarantine = QuarantineDamagedFile();
                    Program.LogCrash($"DesktopOrganizer: corrupt layout JSON ({ex.Message}). Quarantined to {quarantine}.");

                    if (File.Exists(LkgConfigPath) && TryReadValidConfig(LkgConfigPath, out var lkgConfig, out _))
                    {
                        return new DesktopOrganizerLoadResult
                        {
                            Status = DesktopOrganizerLoadStatus.Recovered,
                            Config = lkgConfig!,
                            RecoveryPath = LkgConfigPath,
                            ErrorMessage = $"Layout configuration could not be read. Recovered from verified previous copy."
                        };
                    }

                    return new DesktopOrganizerLoadResult
                    {
                        Status = DesktopOrganizerLoadStatus.Corrupt,
                        ErrorMessage = $"Layout configuration could not be read ({ex.Message}). Existing file protected from overwrite.",
                        Config = new DesktopOrganizerConfig()
                    };
                }
            }
        }

        public static DesktopOrganizerConfig LoadConfig()
        {
            var res = LoadConfigDetailed();
            return res.Config;
        }

        public static bool SaveConfig(DesktopOrganizerConfig config, out string error, bool allowEmptyProfiles = false)
        {
            error = string.Empty;
            if (config == null)
            {
                error = "Configuration cannot be null.";
                return false;
            }

            lock (SyncLock)
            {
                try
                {
                    config.Profiles ??= new List<DesktopLayoutProfile>();

                    if (config.Profiles.Count == 0 && !allowEmptyProfiles)
                    {
                        if ((TryReadValidConfig(ConfigPath, out var existingOnDisk, out _) && existingOnDisk != null && existingOnDisk.Profiles.Count > 0)
                            || (TryReadValidConfig(LkgConfigPath, out existingOnDisk, out _) && existingOnDisk != null && existingOnDisk.Profiles.Count > 0))
                        {
                            Program.LogDebug("DesktopOrganizer: prevented accidental overwrite of non-empty saved profiles with an empty list.");
                            config.Profiles = existingOnDisk.Profiles;
                            if (string.IsNullOrEmpty(config.ActiveProfileId))
                            {
                                config.ActiveProfileId = existingOnDisk.ActiveProfileId;
                            }
                        }
                    }

                    var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.DesktopOrganizerConfig);

                    if (!AtomicFileWriter.WriteAtomic(ConfigPath, json, out error))
                    {
                        return false;
                    }

                    if (!AtomicFileWriter.TryVerifyReadableJson(ConfigPath, AppConfigJsonContext.Default.DesktopOrganizerConfig, out var verified, out var verifyErr))
                    {
                        error = $"Verification of written layout file failed: {verifyErr}";
                        Program.LogCrash(error);
                        return false;
                    }

                    if (verified?.Profiles == null || verified.Profiles.Count != config.Profiles.Count)
                    {
                        error = "Verification failed: on-disk profile count mismatch.";
                        Program.LogCrash(error);
                        return false;
                    }

                    if (config.Profiles.Count > 0 || allowEmptyProfiles)
                    {
                        AtomicFileWriter.WriteAtomic(LkgConfigPath, json, out _);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Program.LogCrash($"DesktopOrganizer SaveConfig exception: {ex.Message}");
                    return false;
                }
            }
        }

        public static void SaveConfig(DesktopOrganizerConfig config)
        {
            SaveConfig(config, out _);
        }

        private static bool TryReadValidConfig(string path, out DesktopOrganizerConfig? config, out string error)
        {
            config = null;
            error = string.Empty;
            try
            {
                if (!File.Exists(path))
                {
                    error = "File does not exist.";
                    return false;
                }

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "File is empty.";
                    return false;
                }

                config = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.DesktopOrganizerConfig);
                if (config == null || config.Profiles == null)
                {
                    error = "Parsed config or profiles were null.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string? QuarantineDamagedFile()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var target = Path.Combine(ConfigStore.ConfigDirectory,
                    $"desktop_organizer.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(ConfigPath, target, true);
                return target;
            }
            catch
            {
                return null;
            }
        }

        public static string? SaveWipeSafetyCopy()
        {
            lock (SyncLock)
            {
                try
                {
                    var sourcePath = File.Exists(ConfigPath) ? ConfigPath : (File.Exists(LkgConfigPath) ? LkgConfigPath : null);
                    if (sourcePath == null) return null;

                    var parent = Directory.GetParent(ConfigStore.ConfigDirectory)?.FullName;
                    if (string.IsNullOrEmpty(parent)) return null;

                    var target = Path.Combine(parent,
                        $"KeyPulse.desktop_organizer.before-wipe-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(sourcePath, target, true);

                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(sourcePath).Length)
                    {
                        Program.LogCrash("DesktopOrganizer pre-wipe safety copy verification failed.");
                        return null;
                    }

                    try
                    {
                        var existing = Directory.GetFiles(parent, "KeyPulse.desktop_organizer.before-wipe-*.json");
                        if (existing.Length > 3)
                        {
                            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                            for (var i = 0; i < existing.Length - 3; i++)
                            {
                                try { File.Delete(existing[i]); } catch { }
                            }
                        }
                    }
                    catch { }

                    return target;
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"DesktopOrganizer SaveWipeSafetyCopy failed: {ex.Message}");
                    return null;
                }
            }
        }

        public static List<MonitorInfoDetail> GetMonitors()
        {
            var list = new List<MonitorInfoDetail>();
            int idx = 1;

            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf<MONITORINFO>();
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    list.Add(new MonitorInfoDetail
                    {
                        Index = idx++,
                        MonitorRect = mi.rcMonitor,
                        WorkRect = mi.rcWork,
                        IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return list;
        }

        public static IntPtr GetDesktopListView()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr hProgman = FindWindow("Progman", null);
                IntPtr hShellViewWin = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);

                if (hShellViewWin == IntPtr.Zero)
                {
                    IntPtr hWorkerW = IntPtr.Zero;
                    do
                    {
                        hWorkerW = FindWindowEx(IntPtr.Zero, hWorkerW, "WorkerW", null);
                        if (hWorkerW != IntPtr.Zero)
                        {
                            hShellViewWin = FindWindowEx(hWorkerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                            if (hShellViewWin != IntPtr.Zero) break;
                        }
                    } while (hWorkerW != IntPtr.Zero);
                }

                if (hShellViewWin != IntPtr.Zero)
                {
                    IntPtr hListView = FindWindowEx(hShellViewWin, IntPtr.Zero, "SysListView32", null);
                    if (hListView != IntPtr.Zero) return hListView;
                }

                Thread.Sleep(50);
            }

            return IntPtr.Zero;
        }

        public static bool IsAutoArrangeActive(out bool isDesktopVisible)
        {
            isDesktopVisible = true;
            IntPtr hwnd = GetDesktopListView();
            if (hwnd == IntPtr.Zero) return false;

            isDesktopVisible = IsWindowVisible(hwnd);
            int style = GetWindowLong(hwnd, GWL_STYLE);
            return (style & LVS_AUTOARRANGE) != 0;
        }

        public static DesktopLayoutProfile? CaptureCurrentLayout(string profileName, out string error, CancellationToken cancellationToken = default)
        {
            error = string.Empty;
            IntPtr hwnd = GetDesktopListView();
            if (hwnd == IntPtr.Zero)
            {
                error = "Could not locate the Windows Desktop ListView.";
                return null;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            IntPtr hProcess = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                error = "Could not open Windows Explorer process memory.";
                return null;
            }

            int lvItemSize = Marshal.SizeOf<LVITEM>();
            int pointSize = Marshal.SizeOf<POINT>();
            int bufferSize = lvItemSize + 1024 + pointSize;

            IntPtr allocatedMemory = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)bufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (allocatedMemory == IntPtr.Zero)
            {
                CloseHandle(hProcess);
                error = "Could not allocate memory in Windows Explorer.";
                return null;
            }

            IntPtr pLvItem = allocatedMemory;
            IntPtr pTextBuffer = allocatedMemory + lvItemSize;
            IntPtr pPoint = allocatedMemory + lvItemSize + 1024;

            try
            {
                var monitors = GetMonitors();
                var profile = new DesktopLayoutProfile
                {
                    Name = string.IsNullOrWhiteSpace(profileName) ? $"Layout ({DateTime.Now:MMM d, HH:mm})" : profileName.Trim(),
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    DisplayCount = monitors.Count
                };

                foreach (var m in monitors)
                {
                    profile.Monitors.Add(new DesktopMonitorSnapshot
                    {
                        Index = m.Index,
                        Left = m.MonitorRect.left,
                        Top = m.MonitorRect.top,
                        Width = m.MonitorRect.Width,
                        Height = m.MonitorRect.Height,
                        IsPrimary = m.IsPrimary
                    });
                }

                if (SafeSendMessageTimeout(hwnd, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero, 1000, out var spacing))
                {
                    profile.IconSpacingX = (int)(spacing.ToInt64() & 0xFFFF);
                    profile.IconSpacingY = (int)((spacing.ToInt64() >> 16) & 0xFFFF);
                }
                if (profile.IconSpacingX <= 0) profile.IconSpacingX = 76;
                if (profile.IconSpacingY <= 0) profile.IconSpacingY = 100;

                if (!SafeSendMessageTimeout(hwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, 1000, out var itemCountPtr))
                {
                    error = "Windows Explorer did not answer desktop icon queries in time.";
                    return null;
                }
                int itemCount = (int)itemCountPtr;

                for (int i = 0; i < itemCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        error = "Operation cancelled.";
                        return null;
                    }

                    var lvi = new LVITEM
                    {
                        cchTextMax = 512,
                        pszText = pTextBuffer,
                        iSubItem = 0
                    };

                    IntPtr pLocalLvi = Marshal.AllocHGlobal(lvItemSize);
                    Marshal.StructureToPtr(lvi, pLocalLvi, false);
                    WriteProcessMemory(hProcess, pLvItem, pLocalLvi, lvItemSize, out _);
                    Marshal.FreeHGlobal(pLocalLvi);

                    SafeSendMessageTimeout(hwnd, LVM_GETITEMTEXTW, (IntPtr)i, pLvItem, 1000, out _);

                    IntPtr pLocalText = Marshal.AllocHGlobal(1024);
                    ReadProcessMemory(hProcess, pTextBuffer, pLocalText, 1024, out _);
                    string? text = Marshal.PtrToStringUni(pLocalText);
                    Marshal.FreeHGlobal(pLocalText);

                    SafeSendMessageTimeout(hwnd, LVM_GETITEMPOSITION, (IntPtr)i, pPoint, 1000, out _);
                    IntPtr pLocalPoint = Marshal.AllocHGlobal(pointSize);
                    ReadProcessMemory(hProcess, pPoint, pLocalPoint, pointSize, out _);
                    var pt = Marshal.PtrToStructure<POINT>(pLocalPoint);
                    Marshal.FreeHGlobal(pLocalPoint);

                    if (!string.IsNullOrEmpty(text))
                    {
                        int monitorIdx = 1;
                        var matchedMonitor = monitors.FirstOrDefault(m => m.MonitorRect.Contains(pt.X, pt.Y)) ?? monitors.FirstOrDefault();
                        if (matchedMonitor != null) monitorIdx = matchedMonitor.Index;

                        var relX = matchedMonitor != null ? pt.X - matchedMonitor.WorkRect.left : pt.X;
                        var relY = matchedMonitor != null ? pt.Y - matchedMonitor.WorkRect.top : pt.Y;

                        profile.Icons.Add(new DesktopIconSnapshot
                        {
                            Name = text,
                            MonitorIndex = monitorIdx,
                            RelativeX = relX,
                            RelativeY = relY,
                            AbsoluteX = pt.X,
                            AbsoluteY = pt.Y
                        });
                    }
                }

                if (profile.Icons.Count == 0)
                {
                    error = "No desktop icons were found to capture.";
                    return null;
                }

                return profile;
            }
            catch (Exception ex)
            {
                error = "Failed to capture icons: " + ex.Message;
                return null;
            }
            finally
            {
                VirtualFreeEx(hProcess, allocatedMemory, 0, MEM_RELEASE);
                CloseHandle(hProcess);
            }
        }

        public static bool RestoreLayout(DesktopLayoutProfile profile, UnsavedIconBehavior behavior, out string report, CancellationToken cancellationToken = default, bool isUndoOperation = false)
        {
            report = string.Empty;
            if (profile == null || profile.Icons == null || profile.Icons.Count == 0)
            {
                report = "The selected profile contains no icon positions.";
                return false;
            }

            IntPtr hwnd = GetDesktopListView();
            if (hwnd == IntPtr.Zero)
            {
                report = "Could not locate the Windows Desktop ListView.";
                return false;
            }

            if (IsAutoArrangeActive(out var desktopVisible))
            {
                report = "Windows 'Auto arrange icons' is currently enabled. Right-click the desktop, select View, uncheck 'Auto arrange icons', and try again.";
                return false;
            }
            if (!desktopVisible)
            {
                report = "Windows desktop icons are currently hidden. Right-click the desktop, select View, and check 'Show desktop icons'.";
                return false;
            }

            var monitors = GetMonitors();
            if (monitors.Count == 0)
            {
                report = "No active monitors detected.";
                return false;
            }

            DesktopLayoutProfile? preRestoreSnapshot = null;
            if (!isUndoOperation)
            {
                preRestoreSnapshot = CaptureCurrentLayout("Pre-Restore Snapshot (" + DateTime.Now.ToString("HH:mm:ss") + ")", out _, cancellationToken);
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            IntPtr hProcess = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                report = "Could not open Windows Explorer process memory.";
                return false;
            }

            int lvItemSize = Marshal.SizeOf<LVITEM>();
            int pointSize = Marshal.SizeOf<POINT>();
            int bufferSize = lvItemSize + 1024 + pointSize;

            IntPtr allocatedMemory = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)bufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (allocatedMemory == IntPtr.Zero)
            {
                CloseHandle(hProcess);
                report = "Could not allocate memory in Windows Explorer.";
                return false;
            }

            IntPtr pLvItem = allocatedMemory;
            IntPtr pTextBuffer = allocatedMemory + lvItemSize;
            IntPtr pPoint = allocatedMemory + lvItemSize + 1024;

            try
            {
                var primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                if (!SafeSendMessageTimeout(hwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, 1000, out var itemCountPtr))
                {
                    report = "Windows Explorer did not answer desktop icon queries in time.";
                    return false;
                }
                int itemCount = (int)itemCountPtr;

                if (!isUndoOperation && preRestoreSnapshot != null)
                {
                    _lastUndoSnapshot = preRestoreSnapshot;
                }

                var occupiedSlots = new HashSet<(int X, int Y)>();
                var consumedSavedIndices = new HashSet<int>();
                var unsavedItems = new List<int>();
                int placedCount = 0;

                int spaceX = profile.IconSpacingX > 0 ? profile.IconSpacingX : 76;
                int spaceY = profile.IconSpacingY > 0 ? profile.IconSpacingY : 100;

                for (int i = 0; i < itemCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        report = "Restore cancelled.";
                        return false;
                    }

                    var lvi = new LVITEM
                    {
                        cchTextMax = 512,
                        pszText = pTextBuffer,
                        iSubItem = 0
                    };

                    IntPtr pLocalLvi = Marshal.AllocHGlobal(lvItemSize);
                    Marshal.StructureToPtr(lvi, pLocalLvi, false);
                    WriteProcessMemory(hProcess, pLvItem, pLocalLvi, lvItemSize, out _);
                    Marshal.FreeHGlobal(pLocalLvi);

                    SafeSendMessageTimeout(hwnd, LVM_GETITEMTEXTW, (IntPtr)i, pLvItem, 1000, out _);

                    IntPtr pLocalText = Marshal.AllocHGlobal(1024);
                    ReadProcessMemory(hProcess, pTextBuffer, pLocalText, 1024, out _);
                    string? text = Marshal.PtrToStringUni(pLocalText);
                    Marshal.FreeHGlobal(pLocalText);

                    if (string.IsNullOrEmpty(text)) continue;

                    int matchIdx = -1;
                    for (int s = 0; s < profile.Icons.Count; s++)
                    {
                        if (!consumedSavedIndices.Contains(s) && string.Equals(profile.Icons[s].Name, text, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIdx = s;
                            break;
                        }
                    }

                    if (matchIdx >= 0)
                    {
                        consumedSavedIndices.Add(matchIdx);
                        var saved = profile.Icons[matchIdx];

                        var targetMon = monitors.FirstOrDefault(m => m.Index == saved.MonitorIndex) ?? primaryMonitor;

                        int destX = targetMon.WorkRect.left + saved.RelativeX;
                        int destY = targetMon.WorkRect.top + saved.RelativeY;

                        destX = Math.Clamp(destX, targetMon.WorkRect.left + 4, Math.Max(targetMon.WorkRect.left + 4, targetMon.WorkRect.right - spaceX));
                        destY = Math.Clamp(destY, targetMon.WorkRect.top + 4, Math.Max(targetMon.WorkRect.top + 4, targetMon.WorkRect.bottom - spaceY));

                        int maxX = Math.Max(targetMon.WorkRect.left + 4, targetMon.WorkRect.right - spaceX);
                        int maxSlots = Math.Max(1, (targetMon.WorkRect.Width / Math.Max(1, spaceX)) * (targetMon.WorkRect.Height / Math.Max(1, spaceY)));
                        int slotSearchAttempts = 0;

                        while (occupiedSlots.Contains((destX, destY)) && slotSearchAttempts < maxSlots)
                        {
                            slotSearchAttempts++;
                            destY += spaceY;
                            if (destY > targetMon.WorkRect.bottom - spaceY)
                            {
                                destY = targetMon.WorkRect.top + 4;
                                destX += spaceX;
                                if (destX > maxX)
                                {
                                    destX = targetMon.WorkRect.left + 4;
                                }
                            }
                        }

                        occupiedSlots.Add((destX, destY));
                        SetItemPosition(hwnd, hProcess, pPoint, i, destX, destY);
                        placedCount++;
                    }
                    else
                    {
                        unsavedItems.Add(i);
                    }
                }

                int newArranged = 0;
                if (unsavedItems.Count > 0 && behavior != UnsavedIconBehavior.LeaveInPlace)
                {
                    if (behavior == UnsavedIconBehavior.CenterGrid || behavior == UnsavedIconBehavior.SecondaryMonitorCenterGrid)
                    {
                        var targetMon = (behavior == UnsavedIconBehavior.SecondaryMonitorCenterGrid && monitors.Count > 1)
                            ? monitors[1]
                            : primaryMonitor;
                        int centerX = targetMon.WorkRect.left + targetMon.WorkRect.Width / 2;
                        int centerY = targetMon.WorkRect.top + targetMon.WorkRect.Height / 2;

                        int rowCount = 2;
                        int cols = (int)Math.Ceiling(unsavedItems.Count / (double)rowCount);

                        int startX = centerX - (cols * spaceX) / 2;
                        int startY = centerY - (rowCount * spaceY) / 2;
                        int maxCenterGridX = Math.Max(targetMon.WorkRect.left + 4, targetMon.WorkRect.right - spaceX);
                        int maxCenterSlots = Math.Max(1, (targetMon.WorkRect.Width / Math.Max(1, spaceX)) * (targetMon.WorkRect.Height / Math.Max(1, spaceY)));

                        for (int k = 0; k < unsavedItems.Count; k++)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            int r = k / cols;
                            int c = k % cols;
                            int destX = startX + c * spaceX;
                            int destY = startY + r * spaceY;

                            destX = Math.Clamp(destX, targetMon.WorkRect.left + 4, maxCenterGridX);
                            destY = Math.Clamp(destY, targetMon.WorkRect.top + 4, Math.Max(targetMon.WorkRect.top + 4, targetMon.WorkRect.bottom - spaceY));

                            int centerAttempts = 0;
                            while (occupiedSlots.Contains((destX, destY)) && centerAttempts < maxCenterSlots)
                            {
                                centerAttempts++;
                                destY += spaceY;
                                if (destY > targetMon.WorkRect.bottom - spaceY)
                                {
                                    destY = targetMon.WorkRect.top + 4;
                                    destX += spaceX;
                                    if (destX > maxCenterGridX)
                                    {
                                        destX = targetMon.WorkRect.left + 4;
                                    }
                                }
                            }

                            occupiedSlots.Add((destX, destY));
                            SetItemPosition(hwnd, hProcess, pPoint, unsavedItems[k], destX, destY);
                            newArranged++;
                        }
                    }
                    else if (behavior == UnsavedIconBehavior.SideColumn)
                    {
                        int minCurX = primaryMonitor.WorkRect.left + 4;
                        int resetCurX = Math.Max(minCurX, primaryMonitor.WorkRect.right - (spaceX * 2));
                        int curX = resetCurX;
                        int curY = primaryMonitor.WorkRect.top + 10;
                        int maxSideSlots = Math.Max(1, (primaryMonitor.WorkRect.Width / Math.Max(1, spaceX)) * (primaryMonitor.WorkRect.Height / Math.Max(1, spaceY)));

                        for (int k = 0; k < unsavedItems.Count; k++)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            int sideAttempts = 0;
                            while (occupiedSlots.Contains((curX, curY)) && sideAttempts < maxSideSlots)
                            {
                                sideAttempts++;
                                curY += spaceY;
                                if (curY > primaryMonitor.WorkRect.bottom - spaceY)
                                {
                                    curY = primaryMonitor.WorkRect.top + 10;
                                    curX -= spaceX;
                                    if (curX < minCurX)
                                    {
                                        curX = resetCurX;
                                    }
                                }
                            }

                            occupiedSlots.Add((curX, curY));
                            SetItemPosition(hwnd, hProcess, pPoint, unsavedItems[k], curX, curY);
                            newArranged++;
                            curY += spaceY;
                        }
                    }
                }

                InvalidateRect(hwnd, IntPtr.Zero, true);
                UpdateWindow(hwnd);

                report = newArranged > 0
                    ? $"Restored \"{profile.Name}\": {placedCount} icons restored, {newArranged} new icons organized."
                    : $"Restored \"{profile.Name}\": {placedCount} icons restored.";

                return true;
            }
            catch (Exception ex)
            {
                report = "Restore failed: " + ex.Message;
                return false;
            }
            finally
            {
                VirtualFreeEx(hProcess, allocatedMemory, 0, MEM_RELEASE);
                CloseHandle(hProcess);
            }
        }

        public static bool UndoLastRestore(out string report, CancellationToken cancellationToken = default)
        {
            if (_lastUndoSnapshot == null)
            {
                report = "No recent restore to undo.";
                return false;
            }

            var snapshot = _lastUndoSnapshot;
            if (RestoreLayout(snapshot, UnsavedIconBehavior.LeaveInPlace, out report, cancellationToken, isUndoOperation: true))
            {
                _lastUndoSnapshot = null;
                return true;
            }

            return false;
        }

        private static void SetItemPosition(IntPtr hwnd, IntPtr hProcess, IntPtr pPoint, int index, int x, int y)
        {
            var pt = new POINT { X = x, Y = y };
            IntPtr pLocalPoint = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
            Marshal.StructureToPtr(pt, pLocalPoint, false);
            WriteProcessMemory(hProcess, pPoint, pLocalPoint, Marshal.SizeOf<POINT>(), out _);
            Marshal.FreeHGlobal(pLocalPoint);

            SafeSendMessageTimeout(hwnd, LVM_SETITEMPOSITION32, (IntPtr)index, pPoint, 1000, out _);
        }

        public static void HandleDisplayChangeDebounced()
        {
            lock (SyncLock)
            {
                var cfg = LoadConfig();
                if (!cfg.AutoRestoreOnDisplayChange || string.IsNullOrWhiteSpace(cfg.ActiveProfileId)) return;

                var profile = cfg.Profiles.FirstOrDefault(p => p.Id == cfg.ActiveProfileId);
                if (profile == null) return;

                _displayChangeDebounceTimer?.Dispose();
                _displayChangeDebounceTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        var monitors = GetMonitors();
                        if (monitors.Count == profile.DisplayCount)
                        {
                            Program.LogDebug($"Display change settled with {monitors.Count} monitors. Auto-restoring layout '{profile.Name}'...");
                            RestoreLayout(profile, cfg.NewIconBehavior, out var r);
                            Program.LogDebug("Auto-restore: " + r);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug($"Auto-restore on display change failed: {ex.Message}");
                    }
                }, null, 3000, Timeout.Infinite);
            }
        }

        public const int MaxAutoSnapshots = 5;
        private static System.Threading.Timer? _periodicSnapshotTimer;
        public static event Action? ProfilesChanged;

        public static void RaiseProfilesChanged()
        {
            try { ProfilesChanged?.Invoke(); } catch { }
        }

        public static void EnsureAutoSnapshotScheduler()
        {
            lock (SyncLock)
            {
                if (_periodicSnapshotTimer != null) return;
                _periodicSnapshotTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        TryTakePeriodicAutoSnapshot(forceImmediateIfNone: false, out var _);
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug($"Periodic auto-snapshot check failed: {ex.Message}");
                    }
                }, null, TimeSpan.FromMinutes(15), TimeSpan.FromHours(6));
            }
        }

        public static bool TryTakePeriodicAutoSnapshot(bool forceImmediateIfNone, out string statusMessage)
        {
            statusMessage = string.Empty;
            var loadRes = LoadConfigDetailed();
            if (loadRes.IsReadOnly)
            {
                statusMessage = "Config is read-only.";
                return false;
            }

            var cfg = loadRes.Config;
            if (!cfg.AutoPeriodicSnapshotEnabled)
            {
                statusMessage = "Periodic auto-snapshots are disabled.";
                return false;
            }

            var existingAuto = cfg.Profiles.Where(p => p.IsAutoSnapshot).ToList();
            var intervalDays = Math.Max(1, cfg.AutoSnapshotIntervalDays);

            if (!forceImmediateIfNone || existingAuto.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(cfg.LastAutoSnapshotUtc)
                    && DateTime.TryParse(cfg.LastAutoSnapshotUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastUtc))
                {
                    if ((DateTime.UtcNow - lastUtc.ToUniversalTime()).TotalDays < intervalDays)
                    {
                        statusMessage = "Next periodic snapshot is not due yet.";
                        return false;
                    }
                }
            }

            if (IsAutoArrangeActive(out var desktopVisible) || !desktopVisible)
            {
                statusMessage = "Skipped auto-snapshot because Auto-Arrange is active or desktop icons are hidden.";
                return false;
            }

            var monitors = GetMonitors();
            if (monitors.Count == 0)
            {
                statusMessage = "Skipped auto-snapshot because no monitors were detected.";
                return false;
            }

            var activeProfile = cfg.Profiles.FirstOrDefault(p => p.Id == cfg.ActiveProfileId)
                                ?? cfg.Profiles.FirstOrDefault(p => !p.IsAutoSnapshot);
            if (activeProfile != null && activeProfile.DisplayCount > 0 && monitors.Count < activeProfile.DisplayCount)
            {
                statusMessage = $"Skipped auto-snapshot because only {monitors.Count} of {activeProfile.DisplayCount} expected screen(s) are connected.";
                return false;
            }

            var captured = CaptureCurrentLayout($"Auto-Snapshot ({DateTime.Now:MMM d, HH:mm})", out var capErr);
            if (captured == null || captured.Icons.Count == 0)
            {
                statusMessage = "Could not capture layout for auto-snapshot: " + capErr;
                return false;
            }

            captured.IsAutoSnapshot = true;

            var latestAuto = existingAuto.LastOrDefault();
            if (latestAuto != null && IsLayoutIdentical(captured, latestAuto))
            {
                cfg.LastAutoSnapshotUtc = DateTime.UtcNow.ToString("O");
                SaveConfig(cfg, out _);
                statusMessage = "Icons have not moved since the last auto-snapshot; skipped duplicate.";
                return false;
            }

            cfg.Profiles.Add(captured);
            cfg.LastAutoSnapshotUtc = DateTime.UtcNow.ToString("O");

            PruneOldestAutoSnapshots(cfg);

            if (SaveConfig(cfg, out var saveErr))
            {
                statusMessage = $"Saved automatic layout snapshot ({cfg.Profiles.Count(p => p.IsAutoSnapshot)}/{MaxAutoSnapshots}).";
                RaiseProfilesChanged();
                return true;
            }

            statusMessage = "Could not save auto-snapshot: " + saveErr;
            return false;
        }

        public static void PruneOldestAutoSnapshots(DesktopOrganizerConfig cfg)
        {
            if (cfg?.Profiles == null) return;
            var autoList = cfg.Profiles.Where(p => p.IsAutoSnapshot).ToList();
            while (autoList.Count > MaxAutoSnapshots)
            {
                var oldest = autoList[0];
                cfg.Profiles.Remove(oldest);
                autoList.RemoveAt(0);
            }
        }

        private static bool IsLayoutIdentical(DesktopLayoutProfile a, DesktopLayoutProfile b)
        {
            if (a.DisplayCount != b.DisplayCount) return false;
            if (a.Icons.Count != b.Icons.Count) return false;

            var bMap = new Dictionary<string, DesktopIconSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var icon in b.Icons)
            {
                if (!string.IsNullOrEmpty(icon.Name) && !bMap.ContainsKey(icon.Name))
                {
                    bMap[icon.Name] = icon;
                }
            }

            foreach (var iconA in a.Icons)
            {
                if (!bMap.TryGetValue(iconA.Name, out var iconB)) return false;
                if (iconA.MonitorIndex != iconB.MonitorIndex) return false;
                if (Math.Abs(iconA.RelativeX - iconB.RelativeX) > 4 || Math.Abs(iconA.RelativeY - iconB.RelativeY) > 4)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool ResetBehavioralDefaults(out string errorMessage)
        {
            errorMessage = string.Empty;
            var loadRes = LoadConfigDetailed();
            if (loadRes.IsReadOnly)
            {
                errorMessage = "Desktop Organizer configuration is currently in read-only recovery mode.";
                return false;
            }

            var cfg = loadRes.Config;
            cfg.AutoRestoreOnDisplayChange = false;
            cfg.AutoPeriodicSnapshotEnabled = false;
            cfg.AutoSnapshotIntervalDays = 3;
            cfg.NewIconBehavior = UnsavedIconBehavior.CenterGrid;

            if (!SaveConfig(cfg, out errorMessage))
            {
                return false;
            }

            RaiseProfilesChanged();
            return true;
        }
    }
}
