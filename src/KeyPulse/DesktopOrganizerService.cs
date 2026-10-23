using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        private const int GWL_STYLE = -16;
        private const int LVS_AUTOARRANGE = 0x0100;

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
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateWindow(IntPtr hWnd);


        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int pdwCookie);
            void RegisterPending(int lThreadId, [In] ref object pvarloc, [In] ref object pvarlocRoot, int swClass, out int pdwCookie);
            void Revoke(int dwCookie);
            void OnNavigated(int dwCookie, [In] ref object pvarloc);
            [PreserveSig]
            int FindWindowSW([In] ref object pvarloc, [In] ref object pvarlocRoot, int swClass, out int phwnd, int swflags, [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut);
            void OnCreated(int dwCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
            void ProcessAttachDetach(bool fAttach);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService([In] ref Guid guidService, [In] ref Guid riid);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void Dummy1();
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp(int fEnterMode);
            void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            void SetMenuSB(IntPtr hmenuShared, IntPtr holeMenu, IntPtr hwndActiveObject);
            void RemoveMenusSB(IntPtr hmenuShared);
            void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
            void EnableModelessSB(int fEnable);
            void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            void BrowseObject(IntPtr pidl, uint wFlags);
            void GetViewStateStream(uint grfMode, IntPtr ppStm);
            void GetControlWindow(uint id, out IntPtr phwnd);
            void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pResult);
            void QueryActiveShellView(out IntPtr ppshv);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp(int fEnterMode);
            void TranslateAccelerator(IntPtr pmsg);
            void EnableModeless(int fEnable);
            void UIActivate(uint uState);
            void Refresh();
            void CreateViewWindow(IntPtr psavView, IntPtr psfvPns, IntPtr psfsStMode, IntPtr prcView, out IntPtr phwndView);
            void DestroyViewWindow();
            void GetCurrentInfo(IntPtr lpfs);
            void AddPropertySheetPages(uint dwReserved, IntPtr lpfn, IntPtr lparam);
            void SaveViewState();
            void SelectItem(IntPtr pidlItem, uint uFlags);
            void GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            void GetCurrentViewMode(out uint pViewMode);
            void SetCurrentViewMode(uint ViewMode);
            void GetFolder(ref Guid riid, out IntPtr ppv);
            void Item(int iItemIndex, out IntPtr ppidl);
            void ItemCount(uint uFlags, out int pcItems);
            void Items(uint uFlags, ref Guid riid, out IntPtr ppv);
            void GetSelectionMarkedItem(out int piItem);
            void GetFocusedItem(out int piItem);
            void GetItemPosition(IntPtr pidl, out POINT ppt);
            void GetSpacing([In, Out] ref POINT ppt);
            void GetDefaultSpacing(out POINT ppt);
            [PreserveSig] int GetAutoArrange();
            void SelectItem(int iItem, uint dwFlags);
            [PreserveSig] int SelectAndPositionItems(uint cidl, [In] IntPtr[] apidl, [In] POINT[] apt, uint dwFlags);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

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

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        private static readonly BlockingCollection<Action> _staQueue = new();
        private static Thread? _staThread;
        private static readonly object _staInitLock = new();

        internal static Thread? StaWorkerThreadForTesting => _staThread;

        private static void EnsureStaWorkerStarted()
        {
            if (_staThread != null && _staThread.IsAlive) return;
            lock (_staInitLock)
            {
                if (_staThread != null && _staThread.IsAlive) return;
                var thread = new Thread(StaWorkerLoop)
                {
                    IsBackground = true,
                    Name = "DesktopOrganizer_STA_Worker"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _staThread = thread;
            }
        }

        private static void StaWorkerLoop()
        {
            bool coInit = false;
            try
            {
                int hr = CoInitializeEx(IntPtr.Zero, 2 /* COINIT_APARTMENTTHREADED */);
                coInit = (hr == 0 || hr == 1);

                foreach (var workItem in _staQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        workItem();
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug($"DesktopOrganizer STA task unhandled exception: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogCrash($"DesktopOrganizer STA worker thread crashed: {ex}");
            }
            finally
            {
                if (coInit)
                {
                    try { CoUninitialize(); } catch { }
                }
            }
        }

        internal static Task<T> RunInStaAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<T>(cancellationToken);
            }

            if (Thread.CurrentThread == _staThread)
            {
                try
                {
                    return Task.FromResult(action());
                }
                catch (OperationCanceledException)
                {
                    return Task.FromCanceled<T>(cancellationToken);
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            EnsureStaWorkerStarted();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            CancellationTokenRegistration ctr = default;
            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(() =>
                {
                    tcs.TrySetCanceled(cancellationToken);
                });
            }

            void WorkItem()
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    bool coInit = false;
                    try
                    {
                        int hr = CoInitializeEx(IntPtr.Zero, 2 /* COINIT_APARTMENTTHREADED */);
                        coInit = (hr == 0 || hr == 1);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled(cancellationToken);
                            return;
                        }

                        T result = action();
                        tcs.TrySetResult(result);
                    }
                    finally
                    {
                        if (coInit)
                        {
                            try { CoUninitialize(); } catch { }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    ctr.Dispose();
                }
            }

            try
            {
                _staQueue.Add(WorkItem);
            }
            catch (Exception ex)
            {
                ctr.Dispose();
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        internal static T RunInSta<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            if (Thread.CurrentThread == _staThread)
            {
                return action();
            }

            return RunInStaAsync(action, cancellationToken).GetAwaiter().GetResult();
        }

        private static bool TryGetDesktopFolderView(out IFolderView? folderView, out IShellView? shellView, out IntPtr hwndDesktop)
        {
            folderView = null;
            shellView = null;
            hwndDesktop = IntPtr.Zero;

            IShellWindows? shellWindows = null;
            object? disp = null;
            object? browserObj = null;
            IntPtr pShellView = IntPtr.Zero;
            IntPtr pFolderView = IntPtr.Zero;

            try
            {
                var clsid = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
                shellWindows = ComActivation.CreateInstance<IShellWindows>(clsid);
                object loc = 0;
                object empty = Type.Missing;
                int hr = shellWindows.FindWindowSW(ref loc, ref empty, 8, out int hwnd, 1, out disp);
                if (hr != 0 || disp == null) return false;
                hwndDesktop = (IntPtr)hwnd;

                if (disp is not IServiceProvider sp) return false;
                var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iid = typeof(IShellBrowser).GUID;
                browserObj = sp.QueryService(ref sid, ref iid);
                if (browserObj is not IShellBrowser browser) return false;

                browser.QueryActiveShellView(out pShellView);
                if (pShellView == IntPtr.Zero) return false;

                Guid IID_IFolderView = new Guid("cde725b0-ccc9-4519-917e-325d72fab4ce");
                hr = Marshal.QueryInterface(pShellView, in IID_IFolderView, out pFolderView);
                shellView = (IShellView)Marshal.GetUniqueObjectForIUnknown(pShellView);

                if (hr == 0 && pFolderView != IntPtr.Zero)
                {
                    folderView = (IFolderView)Marshal.GetUniqueObjectForIUnknown(pFolderView);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"DesktopOrganizer: COM folder view failed: {ex.Message}");
            }
            finally
            {
                if (pFolderView != IntPtr.Zero) Marshal.Release(pFolderView);
                if (pShellView != IntPtr.Zero) Marshal.Release(pShellView);
                if (browserObj != null) { try { Marshal.ReleaseComObject(browserObj); } catch { } }
                if (disp != null) { try { Marshal.ReleaseComObject(disp); } catch { } }
                if (shellWindows != null) { try { Marshal.ReleaseComObject(shellWindows); } catch { } }
            }
            return false;
        }

        public static bool IsAutoArrangeActive(out bool isDesktopVisible)
        {
            bool visible = true;
            bool result = RunInSta(() =>
            {
                IFolderView? fv = null;
                IShellView? sv = null;
                try
                {
                    if (TryGetDesktopFolderView(out fv, out sv, out var hwnd))
                    {
                        if (hwnd != IntPtr.Zero) visible = IsWindowVisible(hwnd);
                        if (fv != null)
                        {
                            return fv.GetAutoArrange() == 0;
                        }
                    }
                }
                finally
                {
                    if (fv != null) { try { Marshal.ReleaseComObject(fv); } catch { } }
                    if (sv != null) { try { Marshal.ReleaseComObject(sv); } catch { } }
                }

                IntPtr listviewHwnd = GetDesktopListView();
                if (listviewHwnd == IntPtr.Zero) return false;

                visible = IsWindowVisible(listviewHwnd);
                int style = GetWindowLong(listviewHwnd, GWL_STYLE);
                return (style & LVS_AUTOARRANGE) != 0;
            });

            isDesktopVisible = visible;
            return result;
        }

        public static async Task<(bool IsAutoArrange, bool IsDesktopVisible)> IsAutoArrangeActiveDetailedAsync(CancellationToken cancellationToken = default)
        {
            bool visible = true;
            bool isAutoArrange = await RunInStaAsync(() =>
            {
                IFolderView? fv = null;
                IShellView? sv = null;
                try
                {
                    if (TryGetDesktopFolderView(out fv, out sv, out var hwnd))
                    {
                        if (hwnd != IntPtr.Zero) visible = IsWindowVisible(hwnd);
                        if (fv != null)
                        {
                            return fv.GetAutoArrange() == 0;
                        }
                    }
                }
                finally
                {
                    if (fv != null) { try { Marshal.ReleaseComObject(fv); } catch { } }
                    if (sv != null) { try { Marshal.ReleaseComObject(sv); } catch { } }
                }

                IntPtr listviewHwnd = GetDesktopListView();
                if (listviewHwnd == IntPtr.Zero) return false;

                visible = IsWindowVisible(listviewHwnd);
                int style = GetWindowLong(listviewHwnd, GWL_STYLE);
                return (style & LVS_AUTOARRANGE) != 0;
            }, cancellationToken).ConfigureAwait(false);

            return (isAutoArrange, visible);
        }

        public static async Task<bool> IsAutoArrangeActiveAsync(CancellationToken cancellationToken = default)
        {
            var (isAutoArrange, _) = await IsAutoArrangeActiveDetailedAsync(cancellationToken).ConfigureAwait(false);
            return isAutoArrange;
        }

        public static DesktopLayoutProfile? CaptureCurrentLayout(string profileName, out string error, CancellationToken cancellationToken = default)
        {
            string capturedError = string.Empty;
            try
            {
                var result = RunInSta(() => CaptureCurrentLayoutCore(profileName, out capturedError, cancellationToken), cancellationToken);
                error = capturedError;
                return result;
            }
            catch (OperationCanceledException)
            {
                error = "Operation cancelled.";
                return null;
            }
        }

        public static async Task<(DesktopLayoutProfile? Profile, string Error)> CaptureCurrentLayoutDetailedAsync(string profileName, CancellationToken cancellationToken = default)
        {
            string capturedError = string.Empty;
            try
            {
                var result = await RunInStaAsync(() => CaptureCurrentLayoutCore(profileName, out capturedError, cancellationToken), cancellationToken).ConfigureAwait(false);
                return (result, capturedError);
            }
            catch (OperationCanceledException)
            {
                return (null, "Operation cancelled.");
            }
        }

        public static async Task<DesktopLayoutProfile?> CaptureCurrentLayoutAsync(string profileName, CancellationToken cancellationToken = default)
        {
            var (profile, _) = await CaptureCurrentLayoutDetailedAsync(profileName, cancellationToken).ConfigureAwait(false);
            return profile;
        }

        private static DesktopLayoutProfile? CaptureCurrentLayoutCore(string profileName, out string error, CancellationToken cancellationToken = default)
        {
            error = string.Empty;
            if (cancellationToken.IsCancellationRequested)
            {
                error = "Operation cancelled.";
                return null;
            }

            IFolderView? folderView = null;
            IShellView? shellView = null;

            try
            {
                if (!TryGetDesktopFolderView(out folderView, out shellView, out _) || folderView == null)
                {
                    error = "Could not connect to Windows Desktop Shell COM interface.";
                    return null;
                }

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

                var spacingPt = new POINT();
                try { folderView.GetSpacing(ref spacingPt); } catch { }
                if (spacingPt.X <= 0 || spacingPt.Y <= 0)
                {
                    try { folderView.GetDefaultSpacing(out spacingPt); } catch { }
                }
                profile.IconSpacingX = spacingPt.X > 0 ? spacingPt.X : 76;
                profile.IconSpacingY = spacingPt.Y > 0 ? spacingPt.Y : 100;

                folderView.ItemCount(0, out int itemCount);

                for (int i = 0; i < itemCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        error = "Operation cancelled.";
                        return null;
                    }

                    folderView.Item(i, out IntPtr pidl);
                    if (pidl == IntPtr.Zero) continue;

                    string? text = null;
                    try
                    {
                        if (SHGetNameFromIDList(pidl, 0, out string itemName) == 0)
                        {
                            text = itemName;
                        }
                    }
                    catch { }

                    folderView.GetItemPosition(pidl, out POINT pt);
                    CoTaskMemFree(pidl);

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
                if (folderView != null) { try { Marshal.ReleaseComObject(folderView); } catch { } }
                if (shellView != null) { try { Marshal.ReleaseComObject(shellView); } catch { } }
            }
        }

        public static bool RestoreLayout(DesktopLayoutProfile profile, UnsavedIconBehavior behavior, out string report, CancellationToken cancellationToken = default, bool isUndoOperation = false)
        {
            string capturedReport = string.Empty;
            try
            {
                bool result = RunInSta(() => RestoreLayoutCore(profile, behavior, out capturedReport, cancellationToken, isUndoOperation), cancellationToken);
                report = capturedReport;
                return result;
            }
            catch (OperationCanceledException)
            {
                report = "Restore cancelled.";
                return false;
            }
        }

        public static async Task<(bool Success, string Report)> RestoreLayoutDetailedAsync(DesktopLayoutProfile profile, UnsavedIconBehavior behavior, CancellationToken cancellationToken = default, bool isUndoOperation = false)
        {
            string capturedReport = string.Empty;
            try
            {
                bool result = await RunInStaAsync(() => RestoreLayoutCore(profile, behavior, out capturedReport, cancellationToken, isUndoOperation), cancellationToken).ConfigureAwait(false);
                return (result, capturedReport);
            }
            catch (OperationCanceledException)
            {
                return (false, "Restore cancelled.");
            }
        }

        public static async Task<bool> RestoreLayoutAsync(DesktopLayoutProfile profile, UnsavedIconBehavior behavior, CancellationToken cancellationToken = default, bool isUndoOperation = false)
        {
            var (success, _) = await RestoreLayoutDetailedAsync(profile, behavior, cancellationToken, isUndoOperation).ConfigureAwait(false);
            return success;
        }

        private static bool RestoreLayoutCore(DesktopLayoutProfile profile, UnsavedIconBehavior behavior, out string report, CancellationToken cancellationToken = default, bool isUndoOperation = false)
        {
            report = string.Empty;
            if (cancellationToken.IsCancellationRequested)
            {
                report = "Restore cancelled.";
                return false;
            }

            if (profile == null || profile.Icons == null || profile.Icons.Count == 0)
            {
                report = "The selected profile contains no icon positions.";
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

            IFolderView? folderView = null;
            IShellView? shellView = null;
            IntPtr hwndDesktop = IntPtr.Zero;

            try
            {
                if (!TryGetDesktopFolderView(out folderView, out shellView, out hwndDesktop) || folderView == null)
                {
                    report = "Could not connect to Windows Desktop Shell COM interface.";
                    return false;
                }

                var primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                folderView.ItemCount(0, out int itemCount);

                if (!isUndoOperation && preRestoreSnapshot != null)
                {
                    _lastUndoSnapshot = preRestoreSnapshot;
                }

                var occupiedSlots = new HashSet<(int X, int Y)>();
                var consumedSavedIndices = new HashSet<int>();
                var unsavedItems = new List<(int Index, IntPtr Pidl)>();
                int placedCount = 0;
                int newArranged = 0;
                try
                {

                int spaceX = profile.IconSpacingX > 0 ? profile.IconSpacingX : 76;
                int spaceY = profile.IconSpacingY > 0 ? profile.IconSpacingY : 100;

                for (int i = 0; i < itemCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        report = "Restore cancelled.";
                        return false;
                    }

                    folderView.Item(i, out IntPtr pidl);
                    if (pidl == IntPtr.Zero) continue;

                    string? text = null;
                    try
                    {
                        if (SHGetNameFromIDList(pidl, 0, out string itemName) == 0)
                        {
                            text = itemName;
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(text))
                    {
                        CoTaskMemFree(pidl);
                        continue;
                    }

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
                        var pt = new POINT { X = destX, Y = destY };
                        folderView.SelectAndPositionItems(1, new IntPtr[] { pidl }, new POINT[] { pt }, 0);
                        CoTaskMemFree(pidl);
                        placedCount++;
                    }
                    else
                    {
                        unsavedItems.Add((i, pidl));
                    }
                }

                newArranged = 0;
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
                            if (cancellationToken.IsCancellationRequested)
                            {
                                report = "Restore cancelled.";
                                return false;
                            }

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
                            var pt = new POINT { X = destX, Y = destY };
                            folderView.SelectAndPositionItems(1, new IntPtr[] { unsavedItems[k].Pidl }, new POINT[] { pt }, 0);
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
                            if (cancellationToken.IsCancellationRequested)
                            {
                                report = "Restore cancelled.";
                                return false;
                            }

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
                            var pt = new POINT { X = curX, Y = curY };
                            folderView.SelectAndPositionItems(1, new IntPtr[] { unsavedItems[k].Pidl }, new POINT[] { pt }, 0);
                            newArranged++;
                            curY += spaceY;
                        }
                    }
                }

                }
                finally
                {
                    foreach (var item in unsavedItems)
                {
                    if (item.Pidl != IntPtr.Zero)
                    {
                        try { CoTaskMemFree(item.Pidl); } catch { }
                    }
                }

                }

                try { shellView?.Refresh(); } catch { }
                if (hwndDesktop != IntPtr.Zero)
                {
                    InvalidateRect(hwndDesktop, IntPtr.Zero, true);
                    UpdateWindow(hwndDesktop);
                }

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
                if (folderView != null) { try { Marshal.ReleaseComObject(folderView); } catch { } }
                if (shellView != null) { try { Marshal.ReleaseComObject(shellView); } catch { } }
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

        public static async Task<(bool Success, string Report)> UndoLastRestoreDetailedAsync(CancellationToken cancellationToken = default)
        {
            if (_lastUndoSnapshot == null)
            {
                return (false, "No recent restore to undo.");
            }

            var snapshot = _lastUndoSnapshot;
            var (success, report) = await RestoreLayoutDetailedAsync(snapshot, UnsavedIconBehavior.LeaveInPlace, cancellationToken, isUndoOperation: true).ConfigureAwait(false);
            if (success)
            {
                _lastUndoSnapshot = null;
                return (true, report);
            }

            return (false, report);
        }

        public static async Task<bool> UndoLastRestoreAsync(CancellationToken cancellationToken = default)
        {
            var (success, _) = await UndoLastRestoreDetailedAsync(cancellationToken).ConfigureAwait(false);
            return success;
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
