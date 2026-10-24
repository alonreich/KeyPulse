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

    public static partial class DesktopOrganizerService
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


        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        public static string ConfigPath => Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.json");
        public static string LkgConfigPath => Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.lkg.json");

        private static DesktopLayoutProfile? _lastUndoSnapshot;
        private static System.Threading.Timer? _displayChangeDebounceTimer;
        private static readonly object SyncLock = new();

        public static bool HasUndo => Volatile.Read(ref _lastUndoSnapshot) != null;

        /// <summary>Pause after an icon size change before positions are written (desktop re-flow).</summary>
        internal static int IconSizeSettleMs = 600;

        internal static DesktopLayoutProfile? PeekUndoSnapshot() => Volatile.Read(ref _lastUndoSnapshot);

        internal static void PublishUndoSnapshot(DesktopLayoutProfile snapshot) => Interlocked.Exchange(ref _lastUndoSnapshot, snapshot);

        internal static DesktopLayoutProfile? TakeUndoSnapshot() => Interlocked.Exchange(ref _lastUndoSnapshot, null);

        internal static bool TryReinstateUndoSnapshot(DesktopLayoutProfile snapshot) =>
            Interlocked.CompareExchange(ref _lastUndoSnapshot, snapshot, null) == null;

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

        // ---- Desktop icon size (D1-D4) ------------------------------------------------------------
        // Ctrl + mouse wheel on the desktop silently changes the icon size, and with it the icon grid.
        // Positions saved at one size land scrambled at another, so each layout records the size it
        // was saved at and Restore puts that size back before moving any icon.
        private static readonly Guid IID_IFolderView2 = new("1af3a467-214f-4298-908e-06b03e0b39f9");

        // vtable slots: IUnknown (3) + IFolderView (14) = 17, then IFolderView2 in declaration order:
        // ... InvokeVerbOnSelection (17+17), SetViewModeAndIconSize (17+18), GetViewModeAndIconSize (17+19).
        private const int FolderView2SetViewModeAndIconSizeSlot = 35;
        private const int FolderView2GetViewModeAndIconSizeSlot = 36;
        private const uint FVM_ICON = 1;

        /// <summary>Windows' "Medium icons" desktop size - the normal size, and the size Reset uses.</summary>
        public const int NormalDesktopIconSize = 48;
        public const int SmallDesktopIconSize = 32;
        public const int LargeDesktopIconSize = 96;

        public readonly record struct DesktopIconSizeInfo(int Size, uint ViewMode)
        {
            public bool IsNormal => Size == NormalDesktopIconSize;
        }

        /// <summary>Plain-English name for an icon size, e.g. "Medium (normal)", "Large", "72 px".</summary>
        public static string DescribeIconSize(int size) => size switch
        {
            NormalDesktopIconSize => "Medium (normal)",
            SmallDesktopIconSize => "Small",
            LargeDesktopIconSize => "Large",
            256 => "Extra large",
            _ => size + " px"
        };

        /// <summary>One sentence saying how far the icons are from normal, or null when they are normal.</summary>
        public static string? DescribeIconSizeDifference(int size)
        {
            if (size <= 0 || size == NormalDesktopIconSize) return null;
            var direction = size > NormalDesktopIconSize ? "larger" : "smaller";
            var ratio = size > NormalDesktopIconSize ? (double)size / NormalDesktopIconSize : (double)NormalDesktopIconSize / size;
            var amount = ratio < 1.25 ? "slightly " : ratio < 2 ? string.Empty : "much ";
            var label = DescribeIconSize(size);
            var shown = label.EndsWith(" px", StringComparison.Ordinal) ? label : $"{label} ({size} px)";
            return $"Your desktop icons are {shown}, {amount}{direction} than normal (Medium, {NormalDesktopIconSize} px).";
        }

        public static DesktopIconSizeInfo? GetDesktopIconSize()
        {
            try
            {
                return RunInSta(GetDesktopIconSizeCore);
            }
            catch (Exception ex)
            {
                Program.LogDebug("DesktopOrganizer: reading the icon size failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>One read-only startup line proving the desktop shell is reachable (or why not).</summary>
        public static async Task LogDesktopShellHealthAsync()
        {
            try
            {
                await Task.Delay(3000).ConfigureAwait(false);
                var size = await GetDesktopIconSizeAsync().ConfigureAwait(false);
                Program.LogDebug(size.HasValue
                    ? $"DesktopOrganizer: desktop shell reachable; icon size {size.Value.Size} px (normal {NormalDesktopIconSize})."
                    : "DesktopOrganizer: desktop shell NOT reachable at startup; Save/Restore layout will report the reason.");
            }
            catch (Exception ex)
            {
                Program.LogDebug("DesktopOrganizer: startup shell check failed: " + ex.Message);
            }
        }

        public static Task<DesktopIconSizeInfo?> GetDesktopIconSizeAsync(CancellationToken cancellationToken = default)
            => RunInStaAsync(GetDesktopIconSizeCore, cancellationToken);

        public static Task<bool> SetDesktopIconSizeAsync(int size, CancellationToken cancellationToken = default)
            => RunInStaAsync(() => SetDesktopIconSizeCore(size), cancellationToken);

        private static DesktopIconSizeInfo? GetDesktopIconSizeCore()
        {
            using var view = DesktopShellView.TryOpen(true, out var error);
            if (view == null || view.FolderView2 == IntPtr.Zero)
            {
                if (view == null) Program.LogDebug("DesktopOrganizer: desktop shell view unavailable: " + error);
                return null;
            }
            return ReadIconSize(view.FolderView2);
        }

        private static bool SetDesktopIconSizeCore(int size)
        {
            using var view = DesktopShellView.TryOpen(true, out var error);
            if (view == null || view.FolderView2 == IntPtr.Zero)
            {
                if (view == null) Program.LogDebug("DesktopOrganizer: desktop shell view unavailable: " + error);
                return false;
            }
            var current = ReadIconSize(view.FolderView2);
            var ok = WriteIconSize(view.FolderView2, current?.ViewMode ?? FVM_ICON, size);
            if (ok) RedrawDesktop(view);
            return ok;
        }

        private static void RedrawDesktop(DesktopShellView view)
        {
            try { view.Refresh(); } catch { }
            if (view.DesktopHwnd != IntPtr.Zero)
            {
                InvalidateRect(view.DesktopHwnd, IntPtr.Zero, true);
                UpdateWindow(view.DesktopHwnd);
            }
        }

        private static unsafe DesktopIconSizeInfo? ReadIconSize(IntPtr folderView2)
        {
            var vtbl = *(IntPtr**)folderView2;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int*, int>)vtbl[FolderView2GetViewModeAndIconSizeSlot];
            uint mode = 0;
            int size = 0;
            int hr = get(folderView2, &mode, &size);
            return hr == 0 && size > 0 ? new DesktopIconSizeInfo(size, mode) : null;
        }

        private static unsafe bool WriteIconSize(IntPtr folderView2, uint viewMode, int size)
        {
            if (size <= 0) return false;
            var vtbl = *(IntPtr**)folderView2;
            var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)vtbl[FolderView2SetViewModeAndIconSizeSlot];
            int hr = set(folderView2, viewMode == 0 ? FVM_ICON : viewMode, size);
            if (hr != 0) Program.LogDebug($"DesktopOrganizer: SetViewModeAndIconSize({size}) failed (hr=0x{hr:X8}).");
            return hr == 0;
        }

        /// <summary>
        /// Describes how the current displays differ from the ones a layout was saved on, or returns
        /// null when they match (same count, sizes and primary).
        /// </summary>
        internal static string? DescribeDisplayDifference(DesktopLayoutProfile profile, IReadOnlyList<(int Index, int Width, int Height, bool IsPrimary)> current)
        {
            if (profile.Monitors == null || profile.Monitors.Count == 0) return null;
            if (profile.Monitors.Count != current.Count)
            {
                return $"This layout was saved with {profile.Monitors.Count} display(s); {current.Count} are connected now, so icons from missing displays were placed on the main screen.";
            }
            foreach (var saved in profile.Monitors)
            {
                var now = current.FirstOrDefault(m => m.Index == saved.Index);
                if (now.Index != saved.Index || now.Width != saved.Width || now.Height != saved.Height || now.IsPrimary != saved.IsPrimary)
                {
                    return "Your display arrangement or resolution changed since this layout was saved, so some icons may sit at slightly different spots.";
                }
            }
            return null;
        }

        public static bool IsAutoArrangeActive(out bool isDesktopVisible)
        {
            bool visible = true;
            bool result = RunInSta(() =>
            {
                using (var view = DesktopShellView.TryOpen(false, out var error))
                {
                    if (view != null)
                    {
                        if (view.DesktopHwnd != IntPtr.Zero) visible = IsWindowVisible(view.DesktopHwnd);
                        return view.IsAutoArrangeOn();
                    }
                    Program.LogDebug("DesktopOrganizer: desktop shell view unavailable: " + error);
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

            DesktopShellView? view = null;

            try
            {
                view = DesktopShellView.TryOpen(true, out var openError);
                if (view == null)
                {
                    Program.LogDebug("DesktopOrganizer: desktop shell view unavailable: " + openError);
                    error = "Could not connect to the Windows desktop (" + openError + ").";
                    return null;
                }
                var folderView2 = view.FolderView2;

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
                try { spacingPt = view.GetSpacing(); } catch { }
                profile.IconSpacingX = spacingPt.X > 0 ? spacingPt.X : 76;
                profile.IconSpacingY = spacingPt.Y > 0 ? spacingPt.Y : 100;

                if (folderView2 != IntPtr.Zero)
                {
                    try
                    {
                        var iconSize = ReadIconSize(folderView2);
                        if (iconSize.HasValue)
                        {
                            profile.DesktopIconSize = iconSize.Value.Size;
                            profile.DesktopViewMode = iconSize.Value.ViewMode;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug("DesktopOrganizer: could not read the icon size during capture: " + ex.Message);
                    }
                }

                int itemCount = view.ItemCount();

                for (int i = 0; i < itemCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        error = "Operation cancelled.";
                        return null;
                    }

                    IntPtr pidl = view.Item(i);
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

                    POINT pt;
                    try { pt = view.GetItemPosition(pidl); }
                    finally { CoTaskMemFree(pidl); }

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
                view?.Dispose();
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

            DesktopShellView? view = null;
            string iconSizeNote = string.Empty;

            try
            {
                view = DesktopShellView.TryOpen(true, out var openError);
                if (view == null)
                {
                    Program.LogDebug("DesktopOrganizer: desktop shell view unavailable: " + openError);
                    report = "Could not connect to the Windows desktop (" + openError + ").";
                    return false;
                }
                var folderView2 = view.FolderView2;

                if (profile.DesktopIconSize > 0 && folderView2 != IntPtr.Zero)
                {
                    var currentSize = ReadIconSize(folderView2);
                    if (currentSize.HasValue && currentSize.Value.Size != profile.DesktopIconSize)
                    {
                        if (WriteIconSize(folderView2, profile.DesktopViewMode != 0 ? profile.DesktopViewMode : currentSize.Value.ViewMode, profile.DesktopIconSize))
                        {
                            iconSizeNote = $" Icon size set back to {DescribeIconSize(profile.DesktopIconSize)}.";
                            // The desktop re-flows its grid asynchronously after a size change; let it settle
                            // before positions are written, or Explorer re-snaps them to the new grid.
                            Thread.Sleep(IconSizeSettleMs);
                        }
                        else
                        {
                            iconSizeNote = $" The icon size could not be set back to {DescribeIconSize(profile.DesktopIconSize)}; positions may look off.";
                        }
                    }
                }

                var primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                int itemCount = view.ItemCount();

                if (!isUndoOperation && preRestoreSnapshot != null)
                {
                    PublishUndoSnapshot(preRestoreSnapshot);
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

                    IntPtr pidl = view.Item(i);
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
                        try { view.PositionItem(pidl, pt); } catch (Exception ex) { Program.LogDebug("DesktopOrganizer: positioning an icon failed: " + ex.Message); }
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
                            try { view.PositionItem(unsavedItems[k].Pidl, pt); } catch (Exception ex) { Program.LogDebug("DesktopOrganizer: positioning an icon failed: " + ex.Message); }
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
                            try { view.PositionItem(unsavedItems[k].Pidl, pt); } catch (Exception ex) { Program.LogDebug("DesktopOrganizer: positioning an icon failed: " + ex.Message); }
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

                RedrawDesktop(view);

                report = newArranged > 0
                    ? $"Restored \"{profile.Name}\": {placedCount} icons restored, {newArranged} new icons organized."
                    : $"Restored \"{profile.Name}\": {placedCount} icons restored.";
                report += iconSizeNote;
                var displayNote = DescribeDisplayDifference(profile, monitors.Select(m => (m.Index, m.MonitorRect.Width, m.MonitorRect.Height, m.IsPrimary)).ToList());
                if (displayNote != null) report += " " + displayNote;

                return true;
            }
            catch (Exception ex)
            {
                report = "Restore failed: " + ex.Message;
                return false;
            }
            finally
            {
                view?.Dispose();
            }
        }

        public static bool UndoLastRestore(out string report, CancellationToken cancellationToken = default)
        {
            var snapshot = TakeUndoSnapshot();
            if (snapshot == null)
            {
                report = "No recent restore to undo.";
                return false;
            }

            bool success = RestoreLayout(snapshot, UnsavedIconBehavior.LeaveInPlace, out report, cancellationToken, isUndoOperation: true);
            if (!success)
            {
                TryReinstateUndoSnapshot(snapshot);
            }

            return success;
        }

        public static async Task<(bool Success, string Report)> UndoLastRestoreDetailedAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = TakeUndoSnapshot();
            if (snapshot == null)
            {
                return (false, "No recent restore to undo.");
            }

            var (success, report) = await RestoreLayoutDetailedAsync(snapshot, UnsavedIconBehavior.LeaveInPlace, cancellationToken, isUndoOperation: true).ConfigureAwait(false);
            if (success)
            {
                return (true, report);
            }

            TryReinstateUndoSnapshot(snapshot);
            return (false, report);
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
