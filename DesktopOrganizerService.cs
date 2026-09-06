using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace KeyPulse
{
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
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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

        private static readonly string ConfigPath = Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.json");
        private static DesktopLayoutProfile? _lastUndoSnapshot;
        private static System.Threading.Timer? _displayChangeDebounceTimer;
        private static readonly object SyncLock = new();

        public static bool HasUndo => _lastUndoSnapshot != null;
        public static string LastUndoProfileName => _lastUndoSnapshot?.Name ?? string.Empty;

        public static DesktopOrganizerConfig LoadConfig()
        {
            lock (SyncLock)
            {
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var json = File.ReadAllText(ConfigPath);
                        var cfg = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.DesktopOrganizerConfig);
                        if (cfg != null)
                        {
                            cfg.Profiles ??= new List<DesktopLayoutProfile>();
                            return cfg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"DesktopOrganizer LoadConfig failed: {ex.Message}");
                }

                return new DesktopOrganizerConfig();
            }
        }

        public static void SaveConfig(DesktopOrganizerConfig config)
        {
            lock (SyncLock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigStore.ConfigDirectory);
                    var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.DesktopOrganizerConfig);
                    var temp = ConfigPath + ".tmp";
                    File.WriteAllText(temp, json);

                    if (File.Exists(ConfigPath))
                    {
                        try
                        {
                            File.Replace(temp, ConfigPath, null);
                        }
                        catch
                        {
                            File.Copy(temp, ConfigPath, true);
                            try { File.Delete(temp); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(temp, ConfigPath);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogCrash($"DesktopOrganizer SaveConfig failed: {ex.Message}");
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

        public static DesktopLayoutProfile? CaptureCurrentLayout(string profileName, out string error)
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

                IntPtr spacing = SendMessage(hwnd, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero);
                profile.IconSpacingX = (int)(spacing.ToInt64() & 0xFFFF);
                profile.IconSpacingY = (int)((spacing.ToInt64() >> 16) & 0xFFFF);
                if (profile.IconSpacingX <= 0) profile.IconSpacingX = 76;
                if (profile.IconSpacingY <= 0) profile.IconSpacingY = 100;

                int itemCount = (int)SendMessage(hwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

                for (int i = 0; i < itemCount; i++)
                {
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

                    SendMessage(hwnd, LVM_GETITEMTEXTW, (IntPtr)i, pLvItem);

                    IntPtr pLocalText = Marshal.AllocHGlobal(1024);
                    ReadProcessMemory(hProcess, pTextBuffer, pLocalText, 1024, out _);
                    string? text = Marshal.PtrToStringUni(pLocalText);
                    Marshal.FreeHGlobal(pLocalText);

                    SendMessage(hwnd, LVM_GETITEMPOSITION, (IntPtr)i, pPoint);
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

        public static bool RestoreLayout(DesktopLayoutProfile profile, UnsavedIconBehavior behavior, out string report)
        {
            report = string.Empty;
            if (profile == null || profile.Icons == null || profile.Icons.Count == 0)
            {
                report = "The selected profile contains no icon positions.";
                return false;
            }

            // Capture automatic undo snapshot before touching anything
            var preRestoreSnapshot = CaptureCurrentLayout("Pre-Restore Snapshot (" + DateTime.Now.ToString("HH:mm:ss") + ")", out _);
            if (preRestoreSnapshot != null)
            {
                _lastUndoSnapshot = preRestoreSnapshot;
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
                var monitors = GetMonitors();
                if (monitors.Count == 0)
                {
                    report = "No active monitors detected.";
                    return false;
                }

                var primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                int itemCount = (int)SendMessage(hwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

                var occupiedSlots = new HashSet<(int X, int Y)>();
                var consumedSavedIndices = new HashSet<int>();
                var unsavedItems = new List<int>();
                int placedCount = 0;

                int spaceX = profile.IconSpacingX > 0 ? profile.IconSpacingX : 76;
                int spaceY = profile.IconSpacingY > 0 ? profile.IconSpacingY : 100;

                for (int i = 0; i < itemCount; i++)
                {
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

                    SendMessage(hwnd, LVM_GETITEMTEXTW, (IntPtr)i, pLvItem);

                    IntPtr pLocalText = Marshal.AllocHGlobal(1024);
                    ReadProcessMemory(hProcess, pTextBuffer, pLocalText, 1024, out _);
                    string? text = Marshal.PtrToStringUni(pLocalText);
                    Marshal.FreeHGlobal(pLocalText);

                    if (string.IsNullOrEmpty(text)) continue;

                    // Match by name, skipping already consumed duplicate entries
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

                        // Match monitor or fallback to primary monitor if disconnected
                        var targetMon = monitors.FirstOrDefault(m => m.Index == saved.MonitorIndex) ?? primaryMonitor;

                        // Calculate destination coordinates relative to target work area
                        int destX = targetMon.WorkRect.left + saved.RelativeX;
                        int destY = targetMon.WorkRect.top + saved.RelativeY;

                        // Clamp safely inside visible work area (preventing off-screen icons)
                        destX = Math.Clamp(destX, targetMon.WorkRect.left + 4, Math.Max(targetMon.WorkRect.left + 4, targetMon.WorkRect.right - spaceX));
                        destY = Math.Clamp(destY, targetMon.WorkRect.top + 4, Math.Max(targetMon.WorkRect.top + 4, targetMon.WorkRect.bottom - spaceY));

                        // Anti-collision: if coordinate is already taken by another icon, shift down one grid slot
                        int maxX = Math.Max(targetMon.WorkRect.left + 4, targetMon.WorkRect.right - spaceX);
                        while (occupiedSlots.Contains((destX, destY)))
                        {
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

                // Handle unfamiliar (new) icons
                int newArranged = 0;
                if (unsavedItems.Count > 0 && behavior != UnsavedIconBehavior.LeaveInPlace)
                {
                    if (behavior == UnsavedIconBehavior.CenterGrid)
                    {
                        var targetMon = monitors.Count > 1 ? monitors[1] : primaryMonitor;
                        int centerX = targetMon.WorkRect.left + targetMon.WorkRect.Width / 2;
                        int centerY = targetMon.WorkRect.top + targetMon.WorkRect.Height / 2;

                        int rowCount = 2;
                        int cols = (int)Math.Ceiling(unsavedItems.Count / (double)rowCount);

                        int startX = centerX - (cols * spaceX) / 2;
                        int startY = centerY - (rowCount * spaceY) / 2;
                        int maxCenterGridX = Math.Max(targetMon.WorkRect.left + 4, targetMon.WorkRect.right - spaceX);

                        for (int k = 0; k < unsavedItems.Count; k++)
                        {
                            int r = k / cols;
                            int c = k % cols;
                            int destX = startX + c * spaceX;
                            int destY = startY + r * spaceY;

                            destX = Math.Clamp(destX, targetMon.WorkRect.left + 4, maxCenterGridX);
                            destY = Math.Clamp(destY, targetMon.WorkRect.top + 4, Math.Max(targetMon.WorkRect.top + 4, targetMon.WorkRect.bottom - spaceY));

                            while (occupiedSlots.Contains((destX, destY)))
                            {
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

                        for (int k = 0; k < unsavedItems.Count; k++)
                        {
                            while (occupiedSlots.Contains((curX, curY)))
                            {
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

        public static bool UndoLastRestore(out string report)
        {
            if (_lastUndoSnapshot == null)
            {
                report = "No recent restore to undo.";
                return false;
            }

            var snapshot = _lastUndoSnapshot;
            _lastUndoSnapshot = null;
            return RestoreLayout(snapshot, UnsavedIconBehavior.LeaveInPlace, out report);
        }

        private static void SetItemPosition(IntPtr hwnd, IntPtr hProcess, IntPtr pPoint, int index, int x, int y)
        {
            var pt = new POINT { X = x, Y = y };
            IntPtr pLocalPoint = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
            Marshal.StructureToPtr(pt, pLocalPoint, false);
            WriteProcessMemory(hProcess, pPoint, pLocalPoint, Marshal.SizeOf<POINT>(), out _);
            Marshal.FreeHGlobal(pLocalPoint);

            SendMessage(hwnd, LVM_SETITEMPOSITION32, (IntPtr)index, pPoint);
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
    }
}
