using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace KeyPulse
{
    public sealed class ScreenLiveState
    {
        public int SlotIndex { get; set; }
        public string HardwareId { get; set; } = string.Empty;
        public string DefaultSlotLabel => SlotIndex switch { 0 => "Screen A", 1 => "Screen B", _ => "Screen C" };
        public string Nickname { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public int Brightness { get; set; } = 50;
        public int Contrast { get; set; } = 50;
        public bool LockRatio { get; set; } = false;
        public bool IsLinkedToNext { get; set; }
        public bool IsConnected { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsInternalLcd { get; set; }
        public bool SupportsBrightness { get; set; }
        public bool SupportsContrast { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public PixelRect Bounds { get; set; }
    }

    public static class ScreenBrightnessService
    {
        public const int MaxScreens = 3;
        public const int MaxTrayQuickActions = 3;
        private const int VesaInterCommandDelayMs = DdcWriteSafety.CommandSpacingMs;
        private static readonly SemaphoreSlim BusLock = new(1, 1);
        private static void LogDdc(string message) => Program.LogDebug(message);
        private static string FormatWin32Error(int err) => new System.ComponentModel.Win32Exception(err).Message;

        public static void InvalidateConfigCache()
        {
            lock (ConfigLock)
            {
                _cachedConfig = null;
            }
        }

        private static string AppDataFolder => ConfigStore.ConfigDirectory;

        public static string ConfigFilePath => Path.Combine(AppDataFolder, "screen_brightness.json");
        public static string BackupConfigFilePath => Path.Combine(AppDataFolder, "screen_brightness.lkg.json");

        private static readonly object ConfigLock = new();
        private static ScreenBrightnessConfig? _cachedConfig;

        internal static bool? ForceActiveForTesting { get; set; }

        public static bool IsFeatureActive
        {
            get
            {
                if (ForceActiveForTesting.HasValue) return ForceActiveForTesting.Value;
                if (ConfigStore.IsTestExecutionEnvironment()) return true;
                lock (ConfigLock)
                {
                    return GetLiveConfigLocked().IsFeatureActive;
                }
            }
        }

        private static readonly object CycleLock = new();
        private static readonly SemaphoreSlim HardwareWriteGate = new(1, 1);
        private const int BusLockTimeoutMs = 5000;
        private static CancellationTokenSource? _cycleCts;
        private static bool _hasStartedCycleInSession;
        private static DateTime _lastCycleRequestUtc = DateTime.MinValue;

        private sealed class ScreenWriteThrottle
        {
            public readonly object Sync = new();
            public int PendingBrightness = -1;
            public int PendingContrast = -1;
            public int LastWrittenBrightness = -1;
            public int LastWrittenContrast = -1;
            public DateTime LastWriteUtc = DateTime.MinValue;
            public DateTime LastRequestUtc = DateTime.MinValue;
            public DateTime FirstPendingUtc = DateTime.MinValue;
            public bool FlushScheduled;
            public int ConsecutiveFailures;
        }

        private static readonly ConcurrentDictionary<int, ScreenWriteThrottle> Throttles = new();

        /// <summary>
        /// Test seam: replaces the physical write inside <see cref="WriteSingleScreenHardwareWithRetryAsync"/>
        /// (slot, brightness, contrast, writeBrightness, writeContrast) -> success.
        /// </summary>
        internal static Func<int, int, int, bool, bool, bool>? HardwareWriteOverrideForTesting { get; set; }

        /// <summary>
        /// Test seam: replaces only the native DDC/CI commands inside <see cref="TryWriteDdcMonitorAsync"/>
        /// (hMonitor, brightness, contrast, writeBrightness, writeContrast) -> success. BusLock arbitration and the
        /// trailing VESA spacing still run for real around it.
        /// </summary>
        internal static Func<IntPtr, int, int, bool, bool, bool>? DdcBusWriteOverrideForTesting { get; set; }

        internal static Task<bool> TryWriteDdcMonitorForTestingAsync(IntPtr hMonitor, int brightness, int contrast, bool writeBrightness, bool writeContrast) =>
            TryWriteDdcMonitorAsync(hMonitor, brightness, contrast, writeBrightness, writeContrast);

        internal static Task<bool> WriteSingleScreenHardwareWithRetryForTestingAsync(int slotIndex, int brightness, int contrast, bool writeBrightness, bool writeContrast) =>
            WriteSingleScreenHardwareWithRetryAsync(slotIndex, brightness, contrast, writeBrightness, writeContrast);

        internal static SemaphoreSlim BusLockForTesting => BusLock;

        internal static int VesaInterCommandDelayMsForTesting => VesaInterCommandDelayMs;

        internal static void ResetWriteSchedulerForTesting() => Throttles.Clear();

        internal static bool HasPendingHardwareWritesForTesting => Throttles.Values.Any(t => { lock (t.Sync) return t.FlushScheduled; });

        private static void DdcPause(int ms)
        {
            if (PhysicalMonitorsProviderForTesting != null) return;
            Thread.Sleep(ms);
        }

        /// <summary>Non-blocking <see cref="DdcPause"/> for the asynchronous hardware write pipeline.</summary>
        private static Task DdcPauseAsync(int ms)
        {
            if (PhysicalMonitorsProviderForTesting != null) return Task.CompletedTask;
            return Task.Delay(ms);
        }

        public static event Action? StateChanged;

        public static void NotifyStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch { }
        }

        #region Config Persistence

        /// <summary>
        /// Baseline copy of each handed-out snapshot's per-screen runtime fields, used by <see cref="SaveConfig"/>
        /// to tell "the caller changed this" apart from "the caller merely still holds an old value".
        /// </summary>
        private static readonly ConditionalWeakTable<ScreenBrightnessConfig, ScreenHardwareSetting[]> SnapshotBaselines = new();

        /// <summary>
        /// Returns the live cached configuration, loading it from disk (primary, then last-known-good backup) if needed.
        /// The returned instance is PRIVATE to this service: callers must hold <see cref="ConfigLock"/> and must never leak it.
        /// </summary>
        private static ScreenBrightnessConfig GetLiveConfigLocked()
        {
            if (_cachedConfig != null)
            {
                return _cachedConfig;
            }

            var loaded = TryReadConfigFile(ConfigFilePath) ?? TryReadConfigFile(BackupConfigFilePath);
            if (loaded == null)
            {
                loaded = CreateDefaultConfig();
                SaveConfigInternal(loaded);
            }
            else
            {
                NormalizeConfig(loaded);
            }

            _cachedConfig = loaded;
            return _cachedConfig;
        }

        /// <summary>
        /// Returns an independent deep-copied snapshot of the configuration. Mutating it (including its
        /// Screens / Profiles / DirectHotkeys collections) never affects the shared cache or other threads;
        /// call <see cref="SaveConfig"/> to publish changes (copy-on-write).
        /// </summary>
        public static ScreenBrightnessConfig LoadConfig()
        {
            lock (ConfigLock)
            {
                return CreateSnapshot(GetLiveConfigLocked());
            }
        }

        private static ScreenBrightnessConfig CreateSnapshot(ScreenBrightnessConfig live)
        {
            var snapshot = CloneConfig(live);
            SnapshotBaselines.AddOrUpdate(snapshot, CloneScreens(live.Screens));
            return snapshot;
        }

        public static bool SaveConfig(ScreenBrightnessConfig config)
        {
            lock (ConfigLock)
            {
                var incoming = CloneConfig(config);
                MergeUnchangedRuntimeFields(config, incoming);
                NormalizeConfig(incoming);
                _cachedConfig = incoming;
                SnapshotBaselines.AddOrUpdate(config, CloneScreens(incoming.Screens));
                bool ok = SaveConfigInternal(incoming);
                return ok;
            }
        }

        /// <summary>
        /// A snapshot can be stale by the time it is saved (DDC polling and hotkeys keep updating the live levels).
        /// Per-screen runtime fields the caller did NOT touch since taking the snapshot keep their current live value,
        /// so a stale whole-config save can never roll back hardware state; fields the caller changed win.
        /// </summary>
        private static void MergeUnchangedRuntimeFields(ScreenBrightnessConfig source, ScreenBrightnessConfig incoming)
        {
            var live = _cachedConfig;
            if (live == null || !SnapshotBaselines.TryGetValue(source, out var baseline)) return;

            foreach (var inc in incoming.Screens)
            {
                var b = baseline.FirstOrDefault(x => x.SlotIndex == inc.SlotIndex);
                var l = live.Screens.FirstOrDefault(x => x.SlotIndex == inc.SlotIndex);
                if (b == null || l == null) continue;

                if (inc.LastBrightness == b.LastBrightness) inc.LastBrightness = l.LastBrightness;
                if (inc.LastContrast == b.LastContrast) inc.LastContrast = l.LastContrast;
                if (inc.VirtualBrightness == b.VirtualBrightness) inc.VirtualBrightness = l.VirtualBrightness;
                if (inc.VirtualContrast == b.VirtualContrast) inc.VirtualContrast = l.VirtualContrast;
                if (string.Equals(inc.HardwareId, b.HardwareId, StringComparison.Ordinal)) inc.HardwareId = l.HardwareId;
                if (string.Equals(inc.ModelName, b.ModelName, StringComparison.Ordinal)) inc.ModelName = l.ModelName;
            }
        }

        private static ScreenHardwareSetting CloneScreen(ScreenHardwareSetting s) => new ScreenHardwareSetting
        {
            SlotIndex = s.SlotIndex,
            HardwareId = s.HardwareId,
            ModelName = s.ModelName,
            CustomNickname = s.CustomNickname,
            LockRatio = s.LockRatio,
            LastBrightness = s.LastBrightness,
            LastContrast = s.LastContrast,
            VirtualBrightness = s.VirtualBrightness,
            VirtualContrast = s.VirtualContrast,
            IsLinkedToNext = s.IsLinkedToNext
        };

        private static ScreenHardwareSetting[] CloneScreens(List<ScreenHardwareSetting>? screens)
        {
            if (screens == null) return Array.Empty<ScreenHardwareSetting>();
            var copy = screens.ToArray();
            var result = new List<ScreenHardwareSetting>(copy.Length);
            foreach (var s in copy)
            {
                if (s != null) result.Add(CloneScreen(s));
            }
            return result.ToArray();
        }

        private static ScreenCycleEntry CloneEntry(ScreenCycleEntry t) => new ScreenCycleEntry
        {
            SlotIndex = t.SlotIndex,
            HardwareId = t.HardwareId,
            Brightness = t.Brightness,
            Contrast = t.Contrast,
            Enabled = t.Enabled
        };

        private static ScreenBrightnessProfile CloneProfile(ScreenBrightnessProfile p)
        {
            var targets = new List<ScreenCycleEntry>();
            var src = p.Targets?.ToArray();
            if (src != null)
            {
                foreach (var t in src)
                {
                    if (t != null) targets.Add(CloneEntry(t));
                }
            }
            return new ScreenBrightnessProfile
            {
                Id = p.Id,
                Name = p.Name,
                ShowInTrayQuickActions = p.ShowInTrayQuickActions,
                IncludeInCycle = p.IncludeInCycle,
                Hotkey = p.Hotkey,
                Targets = targets
            };
        }

        private static ScreenDirectHotkey CloneDirectHotkey(ScreenDirectHotkey d) => new ScreenDirectHotkey
        {
            Id = d.Id,
            Name = d.Name,
            Hotkey = d.Hotkey,
            TargetSlotIndex = d.TargetSlotIndex,
            Brightness = d.Brightness,
            Contrast = d.Contrast,
            LockRatio = d.LockRatio
        };

        /// <summary>
        /// Deep, allocation-light clone (no reflection or serializer round-trip, so NativeAOT-safe and cheap enough
        /// for high-frequency hotkey stepping). Every collection and element is a brand-new instance.
        /// </summary>
        internal static ScreenBrightnessConfig CloneConfig(ScreenBrightnessConfig src)
        {
            var screens = new List<ScreenHardwareSetting>(MaxScreens);
            var srcScreens = src.Screens?.ToArray();
            if (srcScreens != null)
            {
                foreach (var s in srcScreens)
                {
                    if (s != null) screens.Add(CloneScreen(s));
                }
            }

            var profiles = new List<ScreenBrightnessProfile>();
            var srcProfiles = src.Profiles?.ToArray();
            if (srcProfiles != null)
            {
                foreach (var p in srcProfiles)
                {
                    if (p != null) profiles.Add(CloneProfile(p));
                }
            }

            var directs = new List<ScreenDirectHotkey>();
            var srcDirects = src.DirectHotkeys?.ToArray();
            if (srcDirects != null)
            {
                foreach (var d in srcDirects)
                {
                    if (d != null) directs.Add(CloneDirectHotkey(d));
                }
            }

            return new ScreenBrightnessConfig
            {
                SchemaVersion = src.SchemaVersion,
                Enabled = src.Enabled,
                DisclaimerAccepted = src.DisclaimerAccepted,
                HasSeenWelcomeCard = src.HasSeenWelcomeCard,
                GlobalLockBrightnessContrast = src.GlobalLockBrightnessContrast,
                LastCycleProfileId = src.LastCycleProfileId,
                CycleUpHotkey = src.CycleUpHotkey,
                CycleDownHotkey = src.CycleDownHotkey,
                StepUpHotkey = src.StepUpHotkey,
                StepDownHotkey = src.StepDownHotkey,
                StepDelta = src.StepDelta,
                LockMonitorDifferential = src.LockMonitorDifferential,
                OverlayWindowX = src.OverlayWindowX,
                OverlayWindowY = src.OverlayWindowY,
                OverlayWindowWidth = src.OverlayWindowWidth,
                OverlayWindowHeight = src.OverlayWindowHeight,
                Screens = screens,
                Profiles = profiles,
                DirectHotkeys = directs
            };
        }

        public static void FlushConfig()
        {
            lock (ConfigLock)
            {
                if (_cachedConfig != null)
                {
                    SaveConfigInternal(_cachedConfig);
                }
            }
        }

        public static ScreenBrightnessConfig ResetToDefaults()
        {
            ScreenBrightnessConfig defaults;
            lock (ConfigLock)
            {
                var live = CreateDefaultConfig();
                _cachedConfig = live;
                SaveConfigInternal(live);
                defaults = CreateSnapshot(live);
            }
            NotifyStateChanged();
            return defaults;
        }

        private static ScreenBrightnessConfig? TryReadConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.ScreenBrightnessConfig);
            }
            catch
            {
                return null;
            }
        }

        private static bool SaveConfigInternal(ScreenBrightnessConfig config)
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }

                var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.ScreenBrightnessConfig);
                if (string.IsNullOrWhiteSpace(json)) return false;

                var dir = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir, "screen_brightness*.tmp"))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                    catch { }
                }

                bool saved = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (AtomicFileWriter.WriteAtomic(ConfigFilePath, json, out _))
                    {
                        saved = true;
                        break;
                    }
                    Thread.Sleep(30 * (attempt + 1));
                }

                if (!saved)
                {
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        try
                        {
                            foreach (var f in Directory.GetFiles(dir, "screen_brightness*.tmp"))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                        catch { }
                    }
                    return false;
                }

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (AtomicFileWriter.WriteAtomic(BackupConfigFilePath, json, out _))
                    {
                        break;
                    }
                    Thread.Sleep(20);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogDdc($"[CONFIG] Error saving config: {ex.Message}");
                return false;
            }
        }

        internal static ScreenBrightnessConfig CreateDefaultConfig()
        {
            var cfg = new ScreenBrightnessConfig
            {
                Enabled = false,
                DisclaimerAccepted = false,
                GlobalLockBrightnessContrast = false,
                StepUpHotkey = "Ctrl+Alt+=, Ctrl+Alt+NumPadAdd",
                StepDownHotkey = "Ctrl+Alt+-, Ctrl+Alt+NumPadSubtract",
                StepDelta = 5,
                LockMonitorDifferential = true,
                DirectHotkeys = new List<ScreenDirectHotkey>(),
                Screens = new List<ScreenHardwareSetting>
                {
                    new ScreenHardwareSetting { SlotIndex = 0, CustomNickname = "Screen A", LockRatio = false, LastBrightness = 50, LastContrast = 50, VirtualBrightness = 50, VirtualContrast = 50 },
                    new ScreenHardwareSetting { SlotIndex = 1, CustomNickname = "Screen B", LockRatio = false, LastBrightness = 50, LastContrast = 50, VirtualBrightness = 50, VirtualContrast = 50 },
                    new ScreenHardwareSetting { SlotIndex = 2, CustomNickname = "Screen C", LockRatio = false, LastBrightness = 50, LastContrast = 50, VirtualBrightness = 50, VirtualContrast = 50 }
                },
                Profiles = new List<ScreenBrightnessProfile>
                {
                    new ScreenBrightnessProfile
                    {
                        Name = "Night / Low",
                        ShowInTrayQuickActions = true,
                        IncludeInCycle = true,
                        Targets = new List<ScreenCycleEntry>
                        {
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 25, Contrast = 25 },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 25, Contrast = 25 },
                            new ScreenCycleEntry { SlotIndex = 2, Brightness = 25, Contrast = 25 }
                        }
                    },
                    new ScreenBrightnessProfile
                    {
                        Name = "Balanced",
                        ShowInTrayQuickActions = true,
                        IncludeInCycle = true,
                        Targets = new List<ScreenCycleEntry>
                        {
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 50, Contrast = 50 },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 50, Contrast = 50 },
                            new ScreenCycleEntry { SlotIndex = 2, Brightness = 50, Contrast = 50 }
                        }
                    },
                    new ScreenBrightnessProfile
                    {
                        Name = "Daylight / Competitive",
                        ShowInTrayQuickActions = true,
                        IncludeInCycle = true,
                        Targets = new List<ScreenCycleEntry>
                        {
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 75, Contrast = 75 },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 75, Contrast = 75 },
                            new ScreenCycleEntry { SlotIndex = 2, Brightness = 75, Contrast = 75 }
                        }
                    }
                }
            };
            return cfg;
        }

        private static void NormalizeConfig(ScreenBrightnessConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.StepUpHotkey) || string.Equals(cfg.StepUpHotkey, "Ctrl+Alt+NumPadAdd", StringComparison.OrdinalIgnoreCase))
            {
                cfg.StepUpHotkey = "Ctrl+Alt+=, Ctrl+Alt+NumPadAdd";
            }
            if (string.IsNullOrWhiteSpace(cfg.StepDownHotkey) || string.Equals(cfg.StepDownHotkey, "Ctrl+Alt+NumPadSubtract", StringComparison.OrdinalIgnoreCase))
            {
                cfg.StepDownHotkey = "Ctrl+Alt+-, Ctrl+Alt+NumPadSubtract";
            }
            cfg.DirectHotkeys ??= new List<ScreenDirectHotkey>();
            foreach (var dh in cfg.DirectHotkeys.ToArray())
            {
                if (string.IsNullOrWhiteSpace(dh.Id)) dh.Id = Guid.NewGuid().ToString("N");
                dh.Brightness = Math.Clamp(dh.Brightness, 0, 100);
                dh.Contrast = Math.Clamp(dh.Contrast, 0, 100);
            }

            var screens = new List<ScreenHardwareSetting>(cfg.Screens ?? new List<ScreenHardwareSetting>());
            var profiles = new List<ScreenBrightnessProfile>(cfg.Profiles ?? new List<ScreenBrightnessProfile>());

            for (int i = 0; i < MaxScreens; i++)
            {
                var existing = screens.FirstOrDefault(s => s.SlotIndex == i);
                if (existing == null)
                {
                    screens.Add(new ScreenHardwareSetting
                    {
                        SlotIndex = i,
                        CustomNickname = DefaultSlotName(i),
                        LockRatio = cfg.GlobalLockBrightnessContrast,
                        LastBrightness = 50,
                        LastContrast = 50
                    });
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(existing.CustomNickname))
                    {
                        existing.CustomNickname = DefaultSlotName(i);
                    }
                    existing.LastBrightness = Math.Clamp(existing.LastBrightness, 0, 100);
                    existing.LastContrast = Math.Clamp(existing.LastContrast, 0, 100);
                    if (existing.VirtualBrightness == 0 && existing.LastBrightness > 0) existing.VirtualBrightness = existing.LastBrightness;
                    if (existing.VirtualContrast == 0 && existing.LastContrast > 0) existing.VirtualContrast = existing.LastContrast;
                    if (existing.LockRatio)
                    {
                        existing.LastContrast = existing.LastBrightness;
                    }
                }
            }

            cfg.Screens = screens.OrderBy(s => s.SlotIndex).Take(MaxScreens).ToList();

            foreach (var p in profiles)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Brightness Preset";
                var targets = new List<ScreenCycleEntry>(p.Targets ?? new List<ScreenCycleEntry>());
                for (int i = 0; i < MaxScreens; i++)
                {
                    var t = targets.FirstOrDefault(x => x.SlotIndex == i);
                    if (t == null)
                    {
                        targets.Add(new ScreenCycleEntry { SlotIndex = i, Brightness = 50, Contrast = 50 });
                    }
                    else
                    {
                        t.Brightness = Math.Clamp(t.Brightness, 0, 100);
                        t.Contrast = Math.Clamp(t.Contrast, 0, 100);
                    }
                }
                p.Targets = targets.OrderBy(x => x.SlotIndex).Take(MaxScreens).ToList();
            }
            cfg.Profiles = profiles;
        }

        public static string DefaultSlotName(int slotIndex) => slotIndex switch
        {
            0 => "Screen A",
            1 => "Screen B",
            _ => "Screen C"
        };

        #endregion

        #region Monitor Enumeration & Stable Hardware Binding

        private sealed class ConnectedMonitorDescriptor
        {
            public IntPtr HMonitor { get; set; }
            public string DeviceName { get; set; } = string.Empty;
            public string HardwareId { get; set; } = string.Empty;
            public string ModelName { get; set; } = string.Empty;
            public bool IsPrimary { get; set; }
            /// <summary>Windows reports this output as a built-in (laptop / tablet) panel, the only kind \\.\LCD controls.</summary>
            public bool IsInternalPanel { get; set; }
            public PixelRect Bounds { get; set; }
            public bool? SupportsBrightness { get; set; }
            public bool? SupportsContrast { get; set; }
        }

        private static List<ConnectedMonitorDescriptor> EnumerateConnectedMonitors()
        {
            var raw = new List<ConnectedMonitorDescriptor>();
            var internalPanels = GetInternalPanelDeviceNames();

            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    string devName = mi.szDevice?.TrimEnd('\0') ?? string.Empty;
                    string hwId = string.Empty;
                    string model = string.Empty;

                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                    if (EnumDisplayDevices(devName, 0, ref dd, 0))
                    {
                        string ddId = (dd.DeviceID ?? string.Empty).Trim();
                        string ddKey = (dd.DeviceKey ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(ddId) && !string.IsNullOrWhiteSpace(ddKey))
                        {
                            hwId = $"{ddId}|{ddKey}";
                        }
                        else if (!string.IsNullOrWhiteSpace(ddId))
                        {
                            hwId = ddId;
                        }
                        else
                        {
                            hwId = ddKey;
                        }
                        model = (dd.DeviceString ?? string.Empty).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(hwId))
                    {
                        hwId = devName;
                    }

                    raw.Add(new ConnectedMonitorDescriptor
                    {
                        HMonitor = hMonitor,
                        DeviceName = devName,
                        HardwareId = hwId,
                        ModelName = string.IsNullOrWhiteSpace(model) ? "Display" : model,
                        IsPrimary = (mi.dwFlags & 1) != 0,
                        IsInternalPanel = internalPanels.Contains(devName),
                        Bounds = new PixelRect(
                            mi.rcMonitor.Left,
                            mi.rcMonitor.Top,
                            Math.Max(1, mi.rcMonitor.Right - mi.rcMonitor.Left),
                            Math.Max(1, mi.rcMonitor.Bottom - mi.rcMonitor.Top))
                    });
                }
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

            return raw
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.Bounds.X)
                .ThenBy(m => m.Bounds.Y)
                .Take(MaxScreens)
                .ToList();
        }

        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

        /// <summary>Test seam: replaces the QueryDisplayConfig lookup of built-in panel GDI names (\\.\DISPLAYn).</summary>
        internal static Func<HashSet<string>>? InternalPanelDeviceNamesOverrideForTesting { get; set; }

        /// <summary>True for the connector types Windows uses for a built-in panel (eDP, embedded UDI, internal).</summary>
        internal static bool IsInternalOutputTechnology(uint outputTechnology) =>
            outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL ||
            outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED ||
            outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

        /// <summary>
        /// GDI device names of the active outputs that are built-in panels. \\.\LCD only ever drives the built-in
        /// backlight, so the LCD path is used for these monitors alone; any failure yields an empty set (no LCD writes).
        /// </summary>
        private static HashSet<string> GetInternalPanelDeviceNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var testOverride = InternalPanelDeviceNamesOverrideForTesting;
            if (testOverride != null) return testOverride();

            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
                {
                    return names;
                }

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                {
                    return names;
                }

                for (int i = 0; i < pathCount; i++)
                {
                    if (!IsInternalOutputTechnology(paths[i].targetInfo.outputTechnology)) continue;

                    var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id
                    };
                    if (DisplayConfigGetDeviceInfo(ref source) == 0 && !string.IsNullOrWhiteSpace(source.viewGdiDeviceName))
                    {
                        names.Add(source.viewGdiDeviceName.TrimEnd('\0'));
                    }
                }
            }
            catch (Exception ex)
            {
                LogDdc($"[LCD] Built-in panel detection failed; laptop backlight control disabled for this scan: {ex.Message}");
                names.Clear();
            }
            return names;
        }

        private static bool HardwareIdsMatch(string savedId, string liveId)
        {
            if (string.IsNullOrWhiteSpace(savedId) || string.IsNullOrWhiteSpace(liveId)) return false;
            if (string.Equals(savedId, liveId, StringComparison.OrdinalIgnoreCase)) return true;
            if (liveId.StartsWith(savedId + "|", StringComparison.OrdinalIgnoreCase)) return true;
            if (savedId.StartsWith(liveId + "|", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Maps connected physical monitors to stable SlotIndex (0 = Screen A, 1 = Screen B, 2 = Screen C)
        /// using saved HardwareId + ModelName so Windows never swaps Screen A and Screen B after reboot.
        /// </summary>
        private static Dictionary<int, ConnectedMonitorDescriptor> BindMonitorsToSlots(ScreenBrightnessConfig cfg)
        {
            if (!IsFeatureActive) return new Dictionary<int, ConnectedMonitorDescriptor>();
            var connected = EnumerateConnectedMonitors();
            var result = new Dictionary<int, ConnectedMonitorDescriptor>();
            var unassignedMonitors = new List<ConnectedMonitorDescriptor>(connected);

            lock (ConfigLock)
            {
                cfg = GetLiveConfigLocked();
                bool changedBinding = false;

                foreach (var slot in cfg.Screens.OrderBy(s => s.SlotIndex))
                {
                    if (!string.IsNullOrWhiteSpace(slot.HardwareId))
                    {
                        var match = unassignedMonitors.FirstOrDefault(m => HardwareIdsMatch(slot.HardwareId, m.HardwareId));
                        if (match != null)
                        {
                            result[slot.SlotIndex] = match;
                            unassignedMonitors.Remove(match);
                            if (!string.Equals(slot.HardwareId, match.HardwareId, StringComparison.Ordinal))
                            {
                                slot.HardwareId = match.HardwareId;
                                changedBinding = true;
                            }
                            if (!string.IsNullOrWhiteSpace(match.ModelName) && slot.ModelName != match.ModelName)
                            {
                                slot.ModelName = match.ModelName;
                                changedBinding = true;
                            }
                        }
                    }
                }

                for (int slotIdx = 0; slotIdx < MaxScreens && unassignedMonitors.Count > 0; slotIdx++)
                {
                    if (result.ContainsKey(slotIdx)) continue;
                    var nextMon = unassignedMonitors[0];
                    unassignedMonitors.RemoveAt(0);
                    result[slotIdx] = nextMon;

                    var slotSetting = cfg.Screens.First(s => s.SlotIndex == slotIdx);
                    if (string.IsNullOrWhiteSpace(slotSetting.HardwareId) || slotSetting.HardwareId != nextMon.HardwareId)
                    {
                        slotSetting.HardwareId = nextMon.HardwareId;
                        slotSetting.ModelName = nextMon.ModelName;
                        changedBinding = true;
                    }
                }

                if (changedBinding)
                {
                    SaveConfigInternal(cfg);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the sorted list of SlotIndex values (0..2) that are physically connected right now.
        /// </summary>
        private static List<int>? _cachedConnectedSlots;
        private static DateTime _lastConnectedSlotsCheck = DateTime.MinValue;

        public static void InvalidateMonitorCache()
        {
            lock (ConfigLock)
            {
                _cachedConnectedSlots = null;
                _lastConnectedSlotsCheck = DateTime.MinValue;
            }
        }

        public static void ResetThrottles()
        {
            foreach (var kvp in Throttles)
            {
                lock (kvp.Value.Sync)
                {
                    kvp.Value.LastWrittenBrightness = -1;
                    kvp.Value.LastWrittenContrast = -1;
                }
            }
        }

        #region DDC/CI Monitor Sessions & Handle Lifecycle Management

        internal sealed class DdcMonitorSession : IDisposable
        {
            public IntPtr HMonitor { get; }
            public IntPtr HPhysicalMonitor { get; }
            public string Description { get; } = string.Empty;
            public uint MinBrightness { get; set; } = 0;
            public uint MaxBrightness { get; set; } = 100;
            public uint MinContrast { get; set; } = 0;
            public uint MaxContrast { get; set; } = 100;
            public bool SupportsBrightness { get; set; }
            public bool SupportsContrast { get; set; }
            private int _isDisposed;
            public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

            private int _isInvalidated;
            public bool IsInvalidated
            {
                get => Volatile.Read(ref _isInvalidated) != 0;
                set => Interlocked.Exchange(ref _isInvalidated, value ? 1 : 0);
            }

            private readonly PHYSICAL_MONITOR[] _physicalMonitors;
            private readonly uint _count;

            public DdcMonitorSession(IntPtr hMonitor, IntPtr hPhys, string desc, PHYSICAL_MONITOR[] pms, uint count)
            {
                HMonitor = hMonitor;
                HPhysicalMonitor = hPhys;
                Description = desc;
                _physicalMonitors = pms;
                _count = count;
            }

            public void Invalidate()
            {
                Interlocked.Exchange(ref _isInvalidated, 1);
            }

            public void Dispose()
            {
                Invalidate();
                if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
                try
                {
                    if (_count > 0 && _physicalMonitors != null && _physicalMonitors.Length > 0)
                    {
                        if (DestroyPhysicalMonitorsHookForTesting != null)
                        {
                            DestroyPhysicalMonitorsHookForTesting(_count, _physicalMonitors);
                        }
                        else
                        {
                            DestroyPhysicalMonitors(_count, _physicalMonitors);
                        }
                        LogDdc($"[DDC-SESSION] Closed physical monitor handle hPhys={HPhysicalMonitor} for hMon={HMonitor}.");
                    }
                }
                catch (Exception ex)
                {
                    LogDdc($"[DDC-SESSION] Error closing physical monitors: {ex.Message}");
                }
            }
        }

        private static readonly object SessionLock = new();
        private static readonly ConcurrentDictionary<IntPtr, DdcMonitorSession> ActiveSessions = new();

        internal static int ActiveSessionCountForTesting => ActiveSessions.Count;
        internal static bool HasActiveSessionForTesting(IntPtr hMonitor) => ActiveSessions.ContainsKey(hMonitor);
        internal static IEnumerable<IntPtr> ActiveSessionKeysForTesting => ActiveSessions.Keys;
        internal static DdcMonitorSession? GetOrCreateSessionForTesting(IntPtr hMonitor) => GetOrCreateSession(hMonitor);
        internal static Func<IntPtr, (bool success, uint count, PHYSICAL_MONITOR[]? pms)>? PhysicalMonitorsProviderForTesting { get; set; }
        internal static Action<uint, PHYSICAL_MONITOR[]>? DestroyPhysicalMonitorsHookForTesting { get; set; }

        public static void InvalidateAllSessions()
        {
            try
            {
                bool acquired = BusLock.Wait(5000);
                if (!acquired)
                {
                    LogDdc("[DDC-SESSION] Warning: BusLock acquisition timed out (5000ms) during InvalidateAllSessions. Proceeding with handle invalidation.");
                }

                try
                {
                    lock (SessionLock)
                    {
                        int count = ActiveSessions.Count;
                        foreach (var kvp in ActiveSessions)
                        {
                            try
                            {
                                kvp.Value.Invalidate();
                                kvp.Value.Dispose();
                            }
                            catch (Exception ex)
                            {
                                LogDdc($"[DDC-SESSION] Error disposing session for hMon={kvp.Key}: {ex.Message}");
                            }
                        }
                        ActiveSessions.Clear();
                        LogDdc($"[DDC-SESSION] All cached physical monitor sessions invalidated ({count} session(s)).");
                    }
                }
                finally
                {
                    if (acquired)
                    {
                        BusLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                LogDdc($"[DDC-SESSION] Error during InvalidateAllSessions: {ex.Message}");
            }
        }

        private static DdcMonitorSession? GetOrCreateSession(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero) return null;

            if (ActiveSessions.TryGetValue(hMonitor, out var cached) &&
                !cached.IsDisposed && !cached.IsInvalidated)
            {
                return cached;
            }

            lock (SessionLock)
            {
                if (ActiveSessions.TryGetValue(hMonitor, out var existing))
                {
                    if (!existing.IsDisposed && !existing.IsInvalidated)
                    {
                        return existing;
                    }
                    ActiveSessions.TryRemove(hMonitor, out _);
                    existing.Dispose();
                }

                uint count;
                PHYSICAL_MONITOR[] pms;

                if (PhysicalMonitorsProviderForTesting != null)
                {
                    var result = PhysicalMonitorsProviderForTesting(hMonitor);
                    if (!result.success || result.count == 0 || result.pms == null)
                    {
                        return null;
                    }
                    count = result.count;
                    pms = result.pms;
                }
                else
                {
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        LogDdc($"[DDC-SESSION] GetNumberOfPhysicalMonitorsFromHMONITOR failed for hMon={hMonitor}: {FormatWin32Error(err)}");
                        return null;
                    }

                    pms = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, pms))
                    {
                        int err = Marshal.GetLastWin32Error();
                        LogDdc($"[DDC-SESSION] GetPhysicalMonitorsFromHMONITOR failed for hMon={hMonitor}, count={count}: {FormatWin32Error(err)}");
                        return null;
                    }
                }

                // 0 is a valid physical monitor handle: dxva2 numbers handles from 0, so it is what a process gets
                // when no other app (e.g. Twinkle Tray) holds one. Success of GetPhysicalMonitorsFromHMONITOR is the only check.
                IntPtr hPhys = pms[0].hPhysicalMonitor;

                string desc = pms[0].szPhysicalMonitorDescription?.Trim() ?? string.Empty;
                var session = new DdcMonitorSession(hMonitor, hPhys, desc, pms, count);

                try
                {
                    var rb = ReadDdcFeature(hPhys, DdcFeature.Brightness);
                    if (rb.Ok)
                    {
                        session.SupportsBrightness = true;
                        session.MinBrightness = rb.Min;
                        session.MaxBrightness = rb.Max;
                    }

                    if (PhysicalMonitorsProviderForTesting == null)
                    {
                        Thread.Sleep(VesaInterCommandDelayMs);
                    }

                    var rc = ReadDdcFeature(hPhys, DdcFeature.Contrast);
                    if (rc.Ok)
                    {
                        session.SupportsContrast = true;
                        session.MinContrast = rc.Min;
                        session.MaxContrast = rc.Max;
                    }
                }
                catch (Exception ex)
                {
                    LogDdc($"[DDC-SESSION] Error probing VESA capabilities for hMon={hMonitor}, hPhys={hPhys}: {ex.Message}");
                    session.Dispose();
                    return null;
                }

                if (ActiveSessions.TryGetValue(hMonitor, out var priorSession))
                {
                    ActiveSessions.TryRemove(hMonitor, out _);
                    priorSession.Dispose();
                }

                ActiveSessions[hMonitor] = session;
                LogDdc($"[DDC-SESSION] Created persistent session for hMon={hMonitor}, hPhys={hPhys}, desc='{desc}', supportsB={session.SupportsBrightness} [{session.MinBrightness}..{session.MaxBrightness}], supportsC={session.SupportsContrast} [{session.MinContrast}..{session.MaxContrast}].");
                return session;
            }
        }

        #endregion

        public static void WakeDdcBus(IntPtr hMonitor)
        {
            if (!IsFeatureActive || hMonitor == IntPtr.Zero) return;
            try
            {
                bool acquired = BusLock.Wait(5000);
                if (!acquired)
                {
                    LogDdc($"[DDC-WAKE] Timed out waiting for BusLock to wake hMon={hMonitor}.");
                    return;
                }

                try
                {
                    GetOrCreateSession(hMonitor);
                }
                finally
                {
                    BusLock.Release();
                }
            }
            catch (Exception ex)
            {
                LogDdc($"[DDC-WAKE] Error waking DDC bus for hMon={hMonitor}: {ex.Message}");
            }
        }

        public static void OnDisplayChangedOrWoken()
        {
            if (!IsFeatureActive) return;
                    InvalidateMonitorCache();
            ResetThrottles();
            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(400);
                    var cfg = LoadConfig();
                    var bound = BindMonitorsToSlots(cfg);
                    foreach (var kvp in bound)
                    {
                        WakeDdcBus(kvp.Value.HMonitor);
                    }
                    NotifyStateChanged();
                }
                catch { }
            });
        }

        public static List<int> GetConnectedSlotIndices(ScreenBrightnessConfig? cfg = null)
        {
            if (!IsFeatureActive) return new List<int>();
            lock (ConfigLock)
            {
                cfg ??= GetLiveConfigLocked();
                if (_cachedConnectedSlots != null && (DateTime.UtcNow - _lastConnectedSlotsCheck).TotalMilliseconds < 300)
                {
                    return new List<int>(_cachedConnectedSlots);
                }
                var bound = BindMonitorsToSlots(cfg);
                var list = bound.Keys.OrderBy(k => k).ToList();
                _cachedConnectedSlots = list;
                _lastConnectedSlotsCheck = DateTime.UtcNow;
                return new List<int>(list);
            }
        }

        /// <summary>
        /// Returns immediate cached snapshot for instant (0ms) UI creation before async hardware read completes.
        /// </summary>
        public static List<ScreenLiveState> GetImmediateCachedStates()
        {
            var cfg = LoadConfig();
            var bound = BindMonitorsToSlots(cfg);
            var list = new List<ScreenLiveState>(MaxScreens);

            for (int i = 0; i < MaxScreens; i++)
            {
                var s = GetSlotSettingOrDefault(cfg, i);
                bool isConn = bound.TryGetValue(i, out var mon);
                int b = Math.Clamp(s.LastBrightness, 0, 100);
                int c = s.LockRatio ? b : Math.Clamp(s.LastContrast, 0, 100);

                list.Add(new ScreenLiveState
                {
                    SlotIndex = i,
                    HardwareId = s.HardwareId,
                    Nickname = string.IsNullOrWhiteSpace(s.CustomNickname) ? DefaultSlotName(i) : s.CustomNickname,
                    ModelName = isConn && !string.IsNullOrWhiteSpace(mon!.ModelName) ? mon.ModelName : s.ModelName,
                    Brightness = b,
                    Contrast = c,
                    LockRatio = s.LockRatio,
                    IsLinkedToNext = s.IsLinkedToNext,
                    IsConnected = isConn,
                    IsPrimary = isConn && mon!.IsPrimary,
                    SupportsBrightness = isConn,
                    SupportsContrast = isConn,
                    StatusMessage = isConn ? "Reading live hardware level..." : "Display not connected",
                    Bounds = isConn ? mon!.Bounds : default
                });
            }

            return list;
        }

        /// <summary>
        /// Reads live hardware Brightness & Contrast across all connected displays in parallel on background threads.
        /// Strictly releases all PHYSICAL_MONITOR handles in a finally block.
        /// </summary>
        public static async Task<List<ScreenLiveState>> ReadAllScreensLiveAsync(CancellationToken ct = default)
        {
            if (!IsFeatureActive) return new List<ScreenLiveState>();
            return await ReadAllScreensLiveAsyncCore(ct).ConfigureAwait(false);
        }

        public readonly record struct LiveReadOutcome(List<ScreenLiveState> States, Exception? Error)
        {
            public bool IsFallback => Error != null;
        }

        /// <summary>
        /// Test seam: replaces the live hardware read inside <see cref="ReadAllScreensLiveOrCachedAsync"/>.
        /// </summary>
        internal static Func<CancellationToken, Task<List<ScreenLiveState>>>? LiveReadOverrideForTesting;

        /// <summary>
        /// Exception boundary for the async void window-open handlers. Cancellation still propagates as
        /// <see cref="OperationCanceledException"/>; any other non-fatal failure (DDC/Win32 enumeration,
        /// unmapped slot, torn config) is logged and answered with the cached states so the window stays usable.
        /// </summary>
        public static async Task<LiveReadOutcome> ReadAllScreensLiveOrCachedAsync(CancellationToken ct = default)
        {
            try
            {
                var reader = LiveReadOverrideForTesting;
                var states = reader != null
                    ? await reader(ct).ConfigureAwait(false)
                    : await ReadAllScreensLiveAsync(ct).ConfigureAwait(false);
                return new LiveReadOutcome(states ?? new List<ScreenLiveState>(), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!IsFatalException(ex))
            {
                LogDdc($"[ScreenBrightness] Live hardware read failed, falling back to cached states: {ex.GetType().Name}: {ex.Message}");
                return new LiveReadOutcome(GetCachedStatesSafe(), ex);
            }
        }

        /// <summary>
        /// <see cref="GetImmediateCachedStates"/> that never throws; returns an empty list as the last resort.
        /// </summary>
        public static List<ScreenLiveState> GetCachedStatesSafe()
        {
            try
            {
                return GetImmediateCachedStates();
            }
            catch (Exception ex) when (!IsFatalException(ex))
            {
                LogDdc($"[ScreenBrightness] Cached state fallback failed: {ex.GetType().Name}: {ex.Message}");
                return new List<ScreenLiveState>();
            }
        }

        internal static bool IsFatalException(Exception ex) =>
            ex is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException;

        /// <summary>
        /// Lock-free slot lookup over a config snapshot. A missing slot yields a default setting instead of
        /// throwing <see cref="InvalidOperationException"/> the way <c>First(...)</c> did.
        /// </summary>
        internal static ScreenHardwareSetting GetSlotSettingOrDefault(ScreenBrightnessConfig cfg, int slotIndex)
        {
            var screens = cfg.Screens;
            if (screens != null)
            {
                for (int i = 0; i < screens.Count; i++)
                {
                    var s = screens[i];
                    if (s != null && s.SlotIndex == slotIndex) return s;
                }
            }
            return new ScreenHardwareSetting { SlotIndex = slotIndex, CustomNickname = DefaultSlotName(slotIndex) };
        }

        private static async Task<List<ScreenLiveState>> ReadAllScreensLiveAsyncCore(CancellationToken ct)
        {
            var cfg = LoadConfig();
            var bound = BindMonitorsToSlots(cfg);

            var results = new ScreenLiveState[MaxScreens];
            for (int i = 0; i < MaxScreens; i++)
            {
                if (ct.IsCancellationRequested) break;
                int slotIndex = i;
                var slotCfg = GetSlotSettingOrDefault(cfg, slotIndex);
                bound.TryGetValue(slotIndex, out var mon);

                results[slotIndex] = await Task.Run(() => ReadSingleScreenHardware(slotIndex, slotCfg, mon, ct), ct).ConfigureAwait(false);
                if (i < MaxScreens - 1 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(35, ct).ConfigureAwait(false);
                }
            }

            for (int i = 0; i < MaxScreens; i++)
            {
                if (results[i] == null)
                {
                    var slotCfg = GetSlotSettingOrDefault(cfg, i);
                    bound.TryGetValue(i, out var mon);
                    results[i] = new ScreenLiveState
                    {
                        SlotIndex = i,
                        HardwareId = slotCfg.HardwareId,
                        Nickname = string.IsNullOrWhiteSpace(slotCfg.CustomNickname) ? DefaultSlotName(i) : slotCfg.CustomNickname,
                        ModelName = mon != null && !string.IsNullOrWhiteSpace(mon.ModelName) ? mon.ModelName : slotCfg.ModelName,
                        Brightness = Math.Clamp(slotCfg.LastBrightness, 0, 100),
                        Contrast = slotCfg.LockRatio ? Math.Clamp(slotCfg.LastBrightness, 0, 100) : Math.Clamp(slotCfg.LastContrast, 0, 100),
                        LockRatio = slotCfg.LockRatio,
                        IsLinkedToNext = slotCfg.IsLinkedToNext,
                        IsConnected = mon != null,
                        IsPrimary = mon?.IsPrimary ?? false,
                        Bounds = mon?.Bounds ?? default
                    };
                }
            }

            lock (ConfigLock)
            {
                var liveCfg = GetLiveConfigLocked();
                bool updated = false;
                foreach (var res in results)
                {
                    if (!res.IsConnected || !res.SupportsBrightness) continue;
                    var slotCfg = liveCfg.Screens.FirstOrDefault(x => x.SlotIndex == res.SlotIndex);
                    if (slotCfg != null)
                    {
                        if (slotCfg.LastBrightness != res.Brightness || slotCfg.LastContrast != res.Contrast)
                        {
                            slotCfg.LastBrightness = res.Brightness;
                            slotCfg.LastContrast = res.Contrast;
                            updated = true;
                        }
                        if (!string.IsNullOrWhiteSpace(res.ModelName) && slotCfg.ModelName != res.ModelName)
                        {
                            slotCfg.ModelName = res.ModelName;
                            updated = true;
                        }
                    }

                    var throttle = Throttles.GetOrAdd(res.SlotIndex, _ => new ScreenWriteThrottle());
                    lock (throttle.Sync)
                    {
                        throttle.LastWrittenBrightness = res.Brightness;
                        throttle.LastWrittenContrast = res.Contrast;
                    }
                }

                if (updated)
                {
                    SaveConfigInternal(liveCfg);
                }
            }

            return results.OrderBy(r => r.SlotIndex).ToList();
        }

        private static ScreenLiveState ReadSingleScreenHardware(
            int slotIndex,
            ScreenHardwareSetting slotCfg,
            ConnectedMonitorDescriptor? mon,
            CancellationToken ct)
        {
            var state = new ScreenLiveState
            {
                SlotIndex = slotIndex,
                HardwareId = slotCfg.HardwareId,
                Nickname = string.IsNullOrWhiteSpace(slotCfg.CustomNickname) ? DefaultSlotName(slotIndex) : slotCfg.CustomNickname,
                ModelName = mon != null && !string.IsNullOrWhiteSpace(mon.ModelName) ? mon.ModelName : slotCfg.ModelName,
                Brightness = Math.Clamp(slotCfg.LastBrightness, 0, 100),
                Contrast = slotCfg.LockRatio ? Math.Clamp(slotCfg.LastBrightness, 0, 100) : Math.Clamp(slotCfg.LastContrast, 0, 100),
                LockRatio = slotCfg.LockRatio,
                IsLinkedToNext = slotCfg.IsLinkedToNext,
                IsConnected = mon != null,
                IsPrimary = mon?.IsPrimary ?? false,
                Bounds = mon?.Bounds ?? default
            };

            if (mon == null || ct.IsCancellationRequested)
            {
                state.StatusMessage = "Display not connected";
                return state;
            }

            if (TryReadDdcMonitor(mon.HMonitor, out int ddcBrightness, out int ddcContrast, out bool hasBrightness, out bool hasContrast, out string ddcDesc))
            {
                if (!string.IsNullOrWhiteSpace(ddcDesc) && (string.IsNullOrWhiteSpace(state.ModelName) || state.ModelName == "Generic PnP Monitor" || state.ModelName == "Display"))
                {
                    state.ModelName = ddcDesc;
                }
                state.SupportsBrightness = hasBrightness;
                state.SupportsContrast = hasContrast;
                mon.SupportsBrightness = hasBrightness;
                mon.SupportsContrast = hasContrast;
                if (hasBrightness) state.Brightness = ddcBrightness;
                if (hasContrast)
                {
                    state.Contrast = state.LockRatio ? state.Brightness : ddcContrast;
                }
                else
                {
                    state.Contrast = state.Brightness;
                }
                state.StatusMessage = hasContrast ? "DDC/CI Hardware Ready" : "DDC/CI Brightness Ready";
                return state;
            }

            if (mon.IsInternalPanel && TryReadInternalLcdBrightness(out int lcdPercent))
            {
                state.IsInternalLcd = true;
                state.SupportsBrightness = true;
                state.SupportsContrast = false;
                state.Brightness = lcdPercent;
                state.Contrast = lcdPercent;
                state.StatusMessage = "Internal Display Backlight Ready";
                return state;
            }

            state.SupportsBrightness = false;
            state.SupportsContrast = false;
            state.StatusMessage = "Hardware control unavailable (Enable DDC/CI in monitor buttons)";
            return state;
        }

        #endregion

        #region Throttled Hardware Writes (250ms Speed Limiter + Trailing-Edge Flush + Delta-Only)

        /// <summary>
        /// Queues a hardware update for a specific screen slot, enforcing the 250ms speed limiter,
        /// trailing-edge final flush, lock-ratio rule, and delta-only writes.
        /// </summary>
        public static void RequestScreenLevelChange(int slotIndex, int brightness, int contrast, bool updateContrastFromLock = true)
        {
            if (!IsFeatureActive) return;
            RequestScreenLevelChangeInternal(slotIndex, brightness, contrast, updateContrastFromLock, preserveVirtual: false);
        }

        private static void RequestScreenLevelChangeInternal(int slotIndex, int brightness, int contrast, bool updateContrastFromLock, bool preserveVirtual)
        {
            if (slotIndex < 0 || slotIndex >= MaxScreens) return;

            brightness = Math.Clamp(brightness, 0, 100);
            contrast = Math.Clamp(contrast, 0, 100);

            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                var slotCfg = cfg.Screens.First(s => s.SlotIndex == slotIndex);
                if (slotCfg.LockRatio && updateContrastFromLock)
                {
                    contrast = brightness;
                }
                slotCfg.LastBrightness = brightness;
                slotCfg.LastContrast = contrast;
                if (!preserveVirtual)
                {
                    slotCfg.VirtualBrightness = brightness;
                    slotCfg.VirtualContrast = contrast;
    
                }

            }

            Program.LogDebug($"[ScreenBrightness] RequestScreenLevelChange slot={slotIndex} b={brightness} c={contrast} updateContrastFromLock={updateContrastFromLock}");
            QueueThrottledHardwareWrite(slotIndex, brightness, contrast);
        }

        private static void QueueThrottledHardwareWrite(int slotIndex, int brightness, int contrast)
        {
            var throttle = Throttles.GetOrAdd(slotIndex, _ => new ScreenWriteThrottle());
            DdcScheduleDecision decision;

            lock (throttle.Sync)
            {
                var now = DateTime.UtcNow;
                throttle.PendingBrightness = brightness;
                throttle.PendingContrast = contrast;
                throttle.ConsecutiveFailures = 0;
                throttle.LastRequestUtc = now;

                decision = DdcWriteSafety.DecideSchedule(now, throttle.LastWriteUtc, throttle.FlushScheduled);
                if (decision == DdcScheduleDecision.AlreadyScheduled) return;
                throttle.FlushScheduled = true;
                throttle.FirstPendingUtc = now;
                if (decision == DdcScheduleDecision.WriteNow) throttle.LastWriteUtc = now;
            }

            Program.LogDebug($"[ScreenBrightness] QueueThrottledHardwareWrite slot={slotIndex} b={brightness} c={contrast} decision={decision}");
            if (decision == DdcScheduleDecision.WriteNow)
            {
                _ = Task.Run(() => ExecutePendingWriteForSlotAsync(slotIndex, throttle));
            }
            else
            {
                _ = Task.Run(() => RunSettledFlushAsync(slotIndex, throttle));
            }
        }

        /// <summary>
        /// Waits until input has been still for <see cref="DdcWriteSafety.SettleMs"/> (or the burst hit
        /// <see cref="DdcWriteSafety.MaxLatencyMs"/>) and then writes only the newest value, so a held hotkey or
        /// slider drag costs a handful of monitor NVRAM writes instead of several per second.
        /// </summary>
        private static async Task RunSettledFlushAsync(int slotIndex, ScreenWriteThrottle throttle)
        {
            while (true)
            {
                int wait;
                lock (throttle.Sync)
                {
                    wait = DdcWriteSafety.ComputeFlushDelayMs(DateTime.UtcNow, throttle.LastRequestUtc, throttle.FirstPendingUtc);
                }
                if (wait <= 0) break;
                await Task.Delay(wait).ConfigureAwait(false);
            }
            await ExecutePendingWriteForSlotAsync(slotIndex, throttle).ConfigureAwait(false);
        }

        private static async Task ExecutePendingWriteForSlotAsync(int slotIndex, ScreenWriteThrottle throttle)
        {
            int targetB;
            int targetC;
            int prevB;
            int prevC;

            lock (throttle.Sync)
            {
                throttle.FlushScheduled = false;
                throttle.LastWriteUtc = DateTime.UtcNow;
                targetB = throttle.PendingBrightness;
                targetC = throttle.PendingContrast;
                prevB = throttle.LastWrittenBrightness;
                prevC = throttle.LastWrittenContrast;

                if (targetB == prevB && targetC == prevC)
                {
                    Program.LogDebug($"[ScreenBrightness] ExecutePendingWriteForSlotAsync slot={slotIndex} targets unchanged ({targetB}/{targetC})");
                    _ = Task.Run(() =>
                    {
                        var cfg = LoadConfig();
                        lock (ConfigLock)
                        {
                            SaveConfigInternal(cfg);
                        }
                    });
                    return;
                }
            }

            bool changeB = targetB != prevB && targetB >= 0;
            bool changeC = targetC != prevC && targetC >= 0;

            Program.LogDebug($"[ScreenBrightness] ExecutePendingWriteForSlotAsync slot={slotIndex} writing targetB={targetB} targetC={targetC} changeB={changeB} changeC={changeC}");
            bool written = await WriteSingleScreenHardwareWithRetryAsync(slotIndex, targetB, targetC, changeB, changeC).ConfigureAwait(false);
            Program.LogDebug($"[ScreenBrightness] ExecutePendingWriteForSlotAsync slot={slotIndex} write result={written}");

            if (written)
            {
                lock (throttle.Sync)
                {
                    throttle.LastWrittenBrightness = targetB;
                    throttle.LastWrittenContrast = targetC;
                    throttle.ConsecutiveFailures = 0;
                }
            }
            else
            {
                lock (throttle.Sync)
                {
                    throttle.ConsecutiveFailures++;
                }
            }

            bool needsFollowUp = false;
            lock (throttle.Sync)
            {
                if (written &&
                    (throttle.PendingBrightness != throttle.LastWrittenBrightness ||
                     throttle.PendingContrast != throttle.LastWrittenContrast) &&
                    !throttle.FlushScheduled)
                {
                    throttle.FlushScheduled = true;
                    throttle.FirstPendingUtc = DateTime.UtcNow;
                    needsFollowUp = true;
                }
            }

            if (needsFollowUp)
            {
                _ = Task.Run(() => RunSettledFlushAsync(slotIndex, throttle));
            }
            else
            {
                _ = Task.Run(() =>
                {
                    FlushConfig();
                });
            }
        }

        private static async Task<bool> WriteSingleScreenHardwareWithRetryAsync(int slotIndex, int brightness, int contrast, bool writeBrightness, bool writeContrast)
        {
            await HardwareWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var testWriter = HardwareWriteOverrideForTesting;
                if (testWriter != null)
                {
                    return testWriter(slotIndex, brightness, contrast, writeBrightness, writeContrast);
                }
                if (ConfigStore.IsTestExecutionEnvironment())
                {
                    return true;
                }

                try
                {
                    var cfg = LoadConfig();
                    var bound = BindMonitorsToSlots(cfg);
                    if (!bound.TryGetValue(slotIndex, out var mon))
                    {
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetryAsync slot={slotIndex} not found in bound monitors (bound count={bound.Count})");
                        return false;
                    }

                    Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetryAsync slot={slotIndex} hMon={mon.HMonitor} isPrimary={mon.IsPrimary} model={mon.ModelName}");

                    bool canWriteB = writeBrightness && (mon.SupportsBrightness ?? true);
                    bool canWriteC = writeContrast && (mon.SupportsContrast ?? true);
                    if (!canWriteB && !canWriteC)
                    {
                        Program.LogDebug($"[ScreenBrightness] slot={slotIndex} neither requested feature is supported (supportsB={mon.SupportsBrightness}, supportsC={mon.SupportsContrast}), skipping.");
                        return true;
                    }

                    if (await TryWriteDdcMonitorAsync(mon.HMonitor, brightness, contrast, canWriteB, canWriteC).ConfigureAwait(false))
                    {
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetryAsync slot={slotIndex} TryWriteDdcMonitorAsync succeeded");
                        await Task.Delay(30).ConfigureAwait(false);
                        return true;
                    }

                    if (mon.IsInternalPanel && writeBrightness && TryWriteInternalLcdBrightness(brightness))
                    {
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetryAsync slot={slotIndex} TryWriteInternalLcdBrightness succeeded");
                        await Task.Delay(30).ConfigureAwait(false);
                        return true;
                    }

                    InvalidateMonitorCache();
                    var refreshed = BindMonitorsToSlots(cfg);
                    if (refreshed.TryGetValue(slotIndex, out var freshMon) && freshMon.HMonitor != mon.HMonitor)
                    {
                        await DdcPauseAsync(DdcWriteSafety.CommandSpacingMs).ConfigureAwait(false);
                        bool freshOk = await TryWriteDdcMonitorAsync(freshMon.HMonitor, brightness, contrast, writeBrightness, writeContrast).ConfigureAwait(false);
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetryAsync slot={slotIndex} retry freshMon hMon={freshMon.HMonitor} result={freshOk}");
                        return freshOk;
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetryAsync slot={slotIndex} exception: {ex}");
                }
                return false;
            }
            finally
            {
                HardwareWriteGate.Release();
            }
        }

        #endregion

        #region Presets & Rapid Shortcut Key Cycling (Lowest-First + Latest-Step Wins)

        private static int GetSafeTargetBrightness(ScreenBrightnessProfile profile, int slotIndex)
        {
            var entry = profile.Targets?.FirstOrDefault(t => t.SlotIndex == slotIndex);
            return Math.Clamp(entry?.Brightness ?? 50, 0, 100);
        }

        private static double GetActiveAverageBrightness(ScreenBrightnessProfile profile, IReadOnlyList<int> activeSlots)
        {
            if (profile.Targets == null || profile.Targets.Count == 0) return 50.0;
            if (activeSlots.Count > 0)
            {
                double sum = 0;
                int count = 0;
                foreach (int slot in activeSlots)
                {
                    var t = profile.Targets.FirstOrDefault(x => x.SlotIndex == slot);
                    if (t != null)
                    {
                        sum += Math.Clamp(t.Brightness, 0, 100);
                        count++;
                    }
                }
                if (count > 0) return sum / count;
            }
            return profile.Targets.Average(t => Math.Clamp(t.Brightness, 0, 100));
        }

        private static bool ProfilesDifferOnConnectedScreens(
            ScreenBrightnessConfig cfg,
            ScreenBrightnessProfile a,
            ScreenBrightnessProfile b,
            IReadOnlyList<int> connectedSlots)
        {
            if (ReferenceEquals(a, b) || a.Id == b.Id) return false;
            var slotsToCheck = connectedSlots.Count > 0 ? connectedSlots : new[] { 0 };
            foreach (int slot in slotsToCheck)
            {
                var screenCfg = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                bool locked = screenCfg?.LockRatio ?? true;
                var ta = a.Targets?.FirstOrDefault(t => t.SlotIndex == slot);
                var tb = b.Targets?.FirstOrDefault(t => t.SlotIndex == slot);
                int ba = Math.Clamp(ta?.Brightness ?? 50, 0, 100);
                int bb = Math.Clamp(tb?.Brightness ?? 50, 0, 100);
                if (ba != bb) return true;

                int ca = locked ? ba : Math.Clamp(ta?.Contrast ?? ba, 0, 100);
                int cb = locked ? bb : Math.Clamp(tb?.Contrast ?? bb, 0, 100);
                if (ca != cb) return true;
            }
            return false;
        }

        public static List<ScreenBrightnessProfile> GetOrderedCycleProfiles(ScreenBrightnessConfig cfg)
        {
            NormalizeConfig(cfg);
            var activeSlots = GetConnectedSlotIndices(cfg);
            int primarySlot = activeSlots.Count > 0 ? activeSlots[0] : 0;

            var candidates = cfg.Profiles.Where(p => p.IncludeInCycle).ToList();
            if (candidates.Count == 0 && cfg.Profiles.Count > 0)
            {
                candidates = cfg.Profiles.ToList();
            }

            return candidates
                .OrderBy(p => GetSafeTargetBrightness(p, primarySlot))
                .ThenBy(p => GetActiveAverageBrightness(p, activeSlots))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<ScreenBrightnessProfile> GetTrayQuickActionProfiles(ScreenBrightnessConfig cfg)
        {
            NormalizeConfig(cfg);
            var activeSlots = GetConnectedSlotIndices(cfg);
            int primarySlot = activeSlots.Count > 0 ? activeSlots[0] : 0;

            return cfg.Profiles
                .Where(p => p.ShowInTrayQuickActions)
                .OrderBy(p => GetSafeTargetBrightness(p, primarySlot))
                .ThenBy(p => GetActiveAverageBrightness(p, activeSlots))
                .Take(MaxTrayQuickActions)
                .ToList();
        }

        public static string FormatProfileSummary(ScreenBrightnessProfile profile, ScreenBrightnessConfig cfg)
        {
            var connectedSlots = GetConnectedSlotIndices(cfg);
            if (connectedSlots.Count == 0)
            {
                connectedSlots.Add(0);
            }

            var parts = new List<string>(connectedSlots.Count);
            foreach (int i in connectedSlots)
            {
                var target = profile.Targets?.FirstOrDefault(t => t.SlotIndex == i);
                if (target != null)
                {
                    if (connectedSlots.Count == 1)
                    {
                        parts.Add($"{target.Brightness}%");
                    }
                    else
                    {
                        string letter = i switch { 0 => "A", 1 => "B", _ => "C" };
                        parts.Add($"{letter}:{target.Brightness}%");
                    }
                }
            }
            return parts.Count > 0
                ? $"{profile.Name} ({string.Join(" | ", parts)})"
                : profile.Name;
        }

        /// <summary>
        /// Advances the brightness cycle step immediately in memory (Lowest first on initial press, then climbing up, then wrapping to lowest, or vice versa).
        /// Accounts for 1, 2, or 3 active displays by skipping redundant multi-display steps that do not change any currently connected monitor.
        /// Cancels any in-flight hardware command when mashed rapidly in a game and sends only the final target step.
        /// Zero UI/OSD popups so fullscreen games (Fortnite, Valorant, etc.) never lose focus.
        /// </summary>
        public static void TriggerShortcutCycleStep(bool reverse = false)
        {
            if (!IsFeatureActive) return;
            ScreenBrightnessProfile? targetProfile = null;
            CancellationToken token;
            int cycleApplyDelayMs = 0;

            lock (CycleLock)
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                var ordered = GetOrderedCycleProfiles(cfg);
                if (ordered.Count > 10) ordered = ordered.Take(10).ToList();
                if (ordered.Count == 0) return;

                var connectedSlots = GetConnectedSlotIndices(cfg);

                int nextIndex = 0;
                if (_hasStartedCycleInSession && !string.IsNullOrWhiteSpace(cfg.LastCycleProfileId))
                {
                    int currentIndex = ordered.FindIndex(p => p.Id == cfg.LastCycleProfileId);
                    if (currentIndex >= 0 && ordered.Count > 1)
                    {
                        var currentProfile = ordered[currentIndex];
                        int candidateIndex = reverse
                            ? (currentIndex - 1 + ordered.Count) % ordered.Count
                            : (currentIndex + 1) % ordered.Count;
                        nextIndex = candidateIndex;

                        for (int step = 0; step < ordered.Count - 1; step++)
                        {
                            int probeIdx = reverse
                                ? (currentIndex - 1 - step + ordered.Count * 2) % ordered.Count
                                : (currentIndex + 1 + step) % ordered.Count;
                            if (ProfilesDifferOnConnectedScreens(cfg, currentProfile, ordered[probeIdx], connectedSlots))
                            {
                                nextIndex = probeIdx;
                                break;
                            }
                        }
                    }
                }

                _hasStartedCycleInSession = true;
                targetProfile = CloneProfile(ordered[nextIndex]);
                cfg.LastCycleProfileId = targetProfile.Id;

                ApplyProfileToMemory(cfg, targetProfile);
                SaveConfigInternal(cfg);

                var nowUtc = DateTime.UtcNow;
                cycleApplyDelayMs = (nowUtc - _lastCycleRequestUtc).TotalMilliseconds >= DdcWriteSafety.SettleMs ? 0 : DdcWriteSafety.SettleMs;
                _lastCycleRequestUtc = nowUtc;

                _cycleCts?.Cancel();
                _cycleCts?.Dispose();
                _cycleCts = new CancellationTokenSource();
                token = _cycleCts.Token;
            }

            NotifyStateChanged();

            if (targetProfile != null)
            {
                var capturedProfile = targetProfile;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(cycleApplyDelayMs, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;
                        await ApplyProfileToHardwareDeltaOnlyAsync(capturedProfile, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch
                    {
                    }
                });
            }
        }

        public static void ApplyProfileAsync(ScreenBrightnessProfile profile)
        {
            if (!IsFeatureActive || profile == null) return;
            CancellationToken token;

            lock (CycleLock)
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                cfg.LastCycleProfileId = profile.Id;
                _hasStartedCycleInSession = true;
                ApplyProfileToMemory(cfg, profile);
                SaveConfigInternal(cfg);

                _cycleCts?.Cancel();
                _cycleCts?.Dispose();
                _cycleCts = new CancellationTokenSource();
                token = _cycleCts.Token;
            }

            NotifyStateChanged();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        await ApplyProfileToHardwareDeltaOnlyAsync(profile, token).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            });
        }

        #region Hotkey Registration

        private static readonly List<int> _registeredBrightnessHotkeyIds = new();

        /// <summary>
        /// Combos another app held when they were registered (e.g. Twinkle Tray owning Ctrl+Alt+=). They are retried
        /// every <see cref="HotkeyRetryIntervalMs"/> so the shortcut starts working as soon as the other app releases it.
        /// Guarded by <see cref="CycleLock"/>.
        /// </summary>
        private static readonly List<(string Combo, Action Action, string Desc)> _failedBrightnessHotkeys = new();
        private const int HotkeyRetryIntervalMs = 10000;
        private static System.Threading.Timer? _hotkeyRetryTimer;

        /// <summary>Test seam: replaces <see cref="HotkeyManager.Register(string, Action, out int)"/>; returns the id, or 0 on failure.</summary>
        internal static Func<string, Action, int>? BrightnessHotkeyRegisterOverrideForTesting { get; set; }

        /// <summary>Test seam: replaces <see cref="HotkeyManager.Unregister"/> for brightness hotkeys.</summary>
        internal static Action<int>? BrightnessHotkeyUnregisterOverrideForTesting { get; set; }

        internal static int FailedBrightnessHotkeyCountForTesting
        {
            get { lock (CycleLock) return _failedBrightnessHotkeys.Count; }
        }

        internal static bool IsBrightnessHotkeyRetryScheduledForTesting
        {
            get { lock (CycleLock) return _hotkeyRetryTimer != null; }
        }

        internal static void RetryFailedBrightnessHotkeysForTesting() => RetryFailedBrightnessHotkeys();

        private static bool TryRegisterBrightnessHotkey(string combo, Action action, out int id)
        {
            var testRegister = BrightnessHotkeyRegisterOverrideForTesting;
            if (testRegister != null)
            {
                id = testRegister(combo, action);
                return id != 0;
            }
            return HotkeyManager.Register(combo, action, out id);
        }

        public static void UnregisterAllBrightnessHotkeys()
        {
            lock (CycleLock)
            {
                var testUnregister = BrightnessHotkeyUnregisterOverrideForTesting;
                foreach (var id in _registeredBrightnessHotkeyIds)
                {
                    try
                    {
                        if (testUnregister != null) testUnregister(id);
                        else HotkeyManager.Unregister(id);
                    }
                    catch { }
                }
                _registeredBrightnessHotkeyIds.Clear();
                _failedBrightnessHotkeys.Clear();
                StopHotkeyRetryTimerLocked();
            }
        }

        private static void ScheduleHotkeyRetryLocked()
        {
            if (_failedBrightnessHotkeys.Count == 0)
            {
                StopHotkeyRetryTimerLocked();
                return;
            }
            if (_hotkeyRetryTimer == null)
            {
                _hotkeyRetryTimer = new System.Threading.Timer(_ => RetryFailedBrightnessHotkeys(), null, HotkeyRetryIntervalMs, Timeout.Infinite);
            }
            else
            {
                _hotkeyRetryTimer.Change(HotkeyRetryIntervalMs, Timeout.Infinite);
            }
        }

        private static void StopHotkeyRetryTimerLocked()
        {
            _hotkeyRetryTimer?.Dispose();
            _hotkeyRetryTimer = null;
        }

        private static void RetryFailedBrightnessHotkeys()
        {
            bool anyRecovered = false;
            try
            {
                lock (CycleLock)
                {
                    if (_failedBrightnessHotkeys.Count == 0)
                    {
                        StopHotkeyRetryTimerLocked();
                        return;
                    }
                    if (!IsFeatureActive)
                    {
                        _failedBrightnessHotkeys.Clear();
                        StopHotkeyRetryTimerLocked();
                        return;
                    }

                    for (int i = _failedBrightnessHotkeys.Count - 1; i >= 0; i--)
                    {
                        var (combo, action, desc) = _failedBrightnessHotkeys[i];
                        if (TryRegisterBrightnessHotkey(combo, action, out int id))
                        {
                            _registeredBrightnessHotkeyIds.Add(id);
                            _failedBrightnessHotkeys.RemoveAt(i);
                            anyRecovered = true;
                            Program.LogDebug($"[ScreenBrightness] Registered {desc} hotkey on retry (another app released it): {combo} (id={id})");
                        }
                    }

                    ScheduleHotkeyRetryLocked();
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[ScreenBrightness] Brightness hotkey retry failed: {ex.Message}");
            }

            if (anyRecovered) NotifyStateChanged();
        }

        /// <summary>
        /// Plain-language warning for brightness shortcuts another app currently owns, or null when all registered.
        /// Shown in both brightness windows; it clears itself once <see cref="RetryFailedBrightnessHotkeys"/> wins the key.
        /// </summary>
        public static string? DescribeTakenBrightnessHotkeys()
        {
            List<string> parts;
            lock (CycleLock)
            {
                if (_failedBrightnessHotkeys.Count == 0) return null;
                parts = _failedBrightnessHotkeys.Select(f => $"{f.Combo} ({FriendlyHotkeyPurpose(f.Desc)})").Distinct().ToList();
            }
            return $"⚠️ Taken by another app: {string.Join(", ", parts)}. KeyPulse grabs it as soon as it is free.";
        }

        private static string FriendlyHotkeyPurpose(string desc) => desc switch
        {
            "StepUp" => "brighter",
            "StepDown" => "darker",
            "CycleUp" => "next preset",
            "CycleDown" => "previous preset",
            _ when desc.StartsWith("Direct-", StringComparison.Ordinal) => "screen key",
            _ when desc.StartsWith("Profile ", StringComparison.Ordinal) => "preset " + desc.Substring("Profile ".Length).Trim('\''),
            _ => desc
        };

        private static string FailedHotkeysSignatureLocked() =>
            string.Join("|", _failedBrightnessHotkeys.Select(f => f.Combo + "/" + f.Desc));

        public static void RegisterBrightnessHotkeys()
        {
            bool failuresChanged;
            lock (CycleLock)
            {
                string failedBefore = FailedHotkeysSignatureLocked();
                RegisterBrightnessHotkeysLocked();
                failuresChanged = failedBefore != FailedHotkeysSignatureLocked();
            }
            if (failuresChanged) NotifyStateChanged();
        }

        private static void RegisterBrightnessHotkeysLocked()
        {
            {
                UnregisterAllBrightnessHotkeys();
                if (!IsFeatureActive) return;
                var cfg = LoadConfig();

                void RegisterKeyList(string hotkeys, Action action, string desc)
                {
                    if (string.IsNullOrWhiteSpace(hotkeys)) return;
                    var parts = hotkeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var combo in parts)
                    {
                        if (TryRegisterBrightnessHotkey(combo, action, out int id))
                        {
                            _registeredBrightnessHotkeyIds.Add(id);
                            Program.LogDebug($"[ScreenBrightness] Registered {desc} hotkey: {combo} (id={id})");
                        }
                        else if (HotkeyManager.TryParseCombo(combo, out _, out _))
                        {
                            _failedBrightnessHotkeys.Add((combo, action, desc));
                            Program.LogDebug($"[ScreenBrightness] Failed to register {desc} hotkey: {combo} (win32Err={HotkeyManager.LastRegisterError}); will retry every {HotkeyRetryIntervalMs / 1000}s in case another app holds it.");
                        }
                        else
                        {
                            Program.LogDebug($"[ScreenBrightness] Failed to register {desc} hotkey: {combo} (unparseable combo, not retried)");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(cfg.CycleUpHotkey))
                {
                    RegisterKeyList(cfg.CycleUpHotkey, () => TriggerShortcutCycleStep(reverse: false), "CycleUp");
                }

                if (!string.IsNullOrWhiteSpace(cfg.CycleDownHotkey))
                {
                    RegisterKeyList(cfg.CycleDownHotkey, () => TriggerShortcutCycleStep(reverse: true), "CycleDown");
                }

                if (!string.IsNullOrWhiteSpace(cfg.StepUpHotkey))
                {
                    int delta = cfg.StepDelta > 0 ? cfg.StepDelta : 5;
                    RegisterKeyList(cfg.StepUpHotkey, () => TriggerBrightnessStep(delta), "StepUp");
                }

                if (!string.IsNullOrWhiteSpace(cfg.StepDownHotkey))
                {
                    int delta = cfg.StepDelta > 0 ? cfg.StepDelta : 5;
                    RegisterKeyList(cfg.StepDownHotkey, () => TriggerBrightnessStep(-delta), "StepDown");
                }

                foreach (var profile in cfg.Profiles)
                {
                    if (!string.IsNullOrWhiteSpace(profile.Hotkey))
                    {
                        var capturedId = profile.Id;
                        RegisterKeyList(profile.Hotkey, () =>
                        {
                            var current = LoadConfig().Profiles.FirstOrDefault(x => x.Id == capturedId);
                            if (current != null) ApplyProfileAsync(current);
                        }, $"Profile '{profile.Name}'");
                    }
                }

                if (cfg.DirectHotkeys != null)
                {
                    foreach (var dh in cfg.DirectHotkeys)
                    {
                        if (!string.IsNullOrWhiteSpace(dh.Hotkey))
                        {
                            var capturedDhId = dh.Id;
                            RegisterKeyList(dh.Hotkey, () =>
                            {
                                var current = LoadConfig().DirectHotkeys.FirstOrDefault(x => x.Id == capturedDhId);
                                if (current != null) TriggerDirectHotkey(current);
                            }, $"Direct-{dh.TargetSlotIndex}");
                        }
                    }
                }

                ScheduleHotkeyRetryLocked();
            }
        }

        public static void TriggerBrightnessStep(int delta)
        {
            var cfg = LoadConfig();
            var connectedSlots = GetConnectedSlotIndices(cfg);
            if (connectedSlots.Count == 0) return;

            if (cfg.LockMonitorDifferential || connectedSlots.Count == 1)
            {
                RequestMasterLevelChange(delta);
            }
            else
            {
                int targetSlot = GetFocusedScreenSlot(cfg);
                var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == targetSlot)
                             ?? cfg.Screens.FirstOrDefault(s => s.SlotIndex == connectedSlots[0]);
                if (screen != null)
                {
                    int newB = Math.Clamp(screen.LastBrightness + delta, 0, 100);
                    int newC = screen.LockRatio ? newB : Math.Clamp(screen.LastContrast + delta, 0, 100);
                    RequestScreenLevelChange(screen.SlotIndex, newB, newC, updateContrastFromLock: screen.LockRatio);
                }
            }

            SaveConfig(cfg);
            NotifyStateChanged();
        }

        private static int GetFocusedScreenSlot(ScreenBrightnessConfig cfg)
        {
            try
            {
                if (GetCursorPos(out POINT pt))
                {
                    var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                    if (hMon != IntPtr.Zero)
                    {
                        var bound = BindMonitorsToSlots(cfg);
                        foreach (var kvp in bound)
                        {
                            if (kvp.Value.HMonitor == hMon)
                            {
                                return kvp.Key;
                            }
                        }
                    }
                }
            }
            catch { }

            var fallback = GetConnectedSlotIndices(cfg);
            return fallback.Count > 0 ? fallback[0] : 0;
        }

        public static void TriggerDirectHotkey(ScreenDirectHotkey dh)
        {
            var cfg = LoadConfig();
            int b = Math.Clamp(dh.Brightness, 0, 100);
            int c = dh.LockRatio ? b : Math.Clamp(dh.Contrast, 0, 100);

            if (dh.TargetSlotIndex >= 0 && dh.TargetSlotIndex < MaxScreens)
            {
                var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == dh.TargetSlotIndex);
                if (screen != null)
                {
                    RequestScreenLevelChange(dh.TargetSlotIndex, b, c, updateContrastFromLock: dh.LockRatio);
                }
            }
            else
            {
                var connected = GetConnectedSlotIndices(cfg);
                foreach (var slot in connected)
                {
                    var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                    if (screen != null)
                    {
                        int targetC = screen.LockRatio ? b : c;
                        RequestScreenLevelChange(slot, b, targetC, updateContrastFromLock: screen.LockRatio);
                    }
                }
            }

            SaveConfig(cfg);
            NotifyStateChanged();
        }

        public static void ReanchorAllScreens()
        {
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                foreach (var s in cfg.Screens)
                {
                    s.VirtualBrightness = s.LastBrightness;
                    s.VirtualContrast = s.LastContrast;
                }
                SaveConfigInternal(cfg);
            }
        }

        public static void RequestMasterLevelChange(int deltaBrightness)
        {
            var connectedSlots = GetConnectedSlotIndices();
            if (connectedSlots.Count == 0 || deltaBrightness == 0) return;

            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                foreach (var slot in connectedSlots)
                {
                    var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                    if (screen != null)
                    {
                        if (screen.VirtualBrightness == 0 && screen.LastBrightness > 0)
                        {
                            screen.VirtualBrightness = screen.LastBrightness;
                        }
                        if (screen.VirtualContrast == 0 && screen.LastContrast > 0)
                        {
                            screen.VirtualContrast = screen.LastContrast;
                        }

                        int newVirtualB = Math.Clamp(screen.VirtualBrightness + deltaBrightness, -100, 200);
                        screen.VirtualBrightness = newVirtualB;

                        int newPhysicalB = Math.Clamp(newVirtualB, 0, 100);
                        screen.LastBrightness = newPhysicalB;

                        int newPhysicalC;
                        if (screen.LockRatio)
                        {
                            screen.VirtualContrast = newVirtualB;
                            newPhysicalC = newPhysicalB;
                            screen.LastContrast = newPhysicalC;
                        }
                        else
                        {
                            int newVirtualC = Math.Clamp(screen.VirtualContrast + deltaBrightness, -100, 200);
                            screen.VirtualContrast = newVirtualC;
                            newPhysicalC = Math.Clamp(newVirtualC, 0, 100);
                            screen.LastContrast = newPhysicalC;
                        }

                        RequestScreenLevelChangeInternal(slot, newPhysicalB, newPhysicalC, updateContrastFromLock: screen.LockRatio, preserveVirtual: true);
                    }
                }
                SaveConfigInternal(cfg);
            }

            NotifyStateChanged();
        }

        public static void RequestIndividualOrLinkedChange(int slotIndex, int brightness, int contrast, bool isBrightnessChanged, bool isContrastChanged)
        {
            if (slotIndex < 0 || slotIndex >= MaxScreens) return;

            brightness = Math.Clamp(brightness, 0, 100);
            contrast = Math.Clamp(contrast, 0, 100);

            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                var sourceSlotCfg = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slotIndex);
                if (sourceSlotCfg == null) return;

                int deltaB = brightness - sourceSlotCfg.LastBrightness;
                int deltaC = contrast - sourceSlotCfg.LastContrast;

                if (sourceSlotCfg.LockRatio && isBrightnessChanged)
                {
                    contrast = brightness;
                }
                else if (sourceSlotCfg.LockRatio && isContrastChanged)
                {
                    brightness = contrast;
                }

                sourceSlotCfg.LastBrightness = brightness;
                sourceSlotCfg.LastContrast = contrast;
                sourceSlotCfg.VirtualBrightness = brightness;
                sourceSlotCfg.VirtualContrast = contrast;

                QueueThrottledHardwareWrite(slotIndex, brightness, contrast);

                var syncGroup = GetLinkedSlotGroup(cfg, slotIndex);
                if (syncGroup.Count > 1)
                {
                    foreach (var targetSlot in syncGroup)
                    {
                        if (targetSlot == slotIndex) continue;
                        var targetSlotCfg = cfg.Screens.FirstOrDefault(s => s.SlotIndex == targetSlot);
                        if (targetSlotCfg == null) continue;

                        int targetB = targetSlotCfg.LastBrightness;
                        int targetC = targetSlotCfg.LastContrast;

                        if (isBrightnessChanged)
                        {
                            targetB = brightness;
                        }

                        if (targetSlotCfg.LockRatio)
                        {
                            targetC = targetB;
                        }
                        else if (isContrastChanged)
                        {
                            targetC = contrast;
                        }

                        targetSlotCfg.LastBrightness = targetB;
                        targetSlotCfg.LastContrast = targetC;
                        targetSlotCfg.VirtualBrightness = targetB;
                        targetSlotCfg.VirtualContrast = targetC;

                        QueueThrottledHardwareWrite(targetSlot, targetB, targetC);
                    }
                }
            }

            NotifyStateChanged();
        }

        public static List<int> GetLinkedSlotGroup(ScreenBrightnessConfig cfg, int slotIndex)
        {
            var connected = GetConnectedSlotIndices(cfg);
            if (!connected.Contains(slotIndex))
            {
                connected = cfg.Screens.Select(s => s.SlotIndex).OrderBy(k => k).ToList();
            }
            if (!connected.Contains(slotIndex)) return new List<int> { slotIndex };

            int idx = connected.IndexOf(slotIndex);
            int left = idx;
            while (left > 0)
            {
                var prevSlot = connected[left - 1];
                var prevScreen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == prevSlot);
                if (prevScreen != null && prevScreen.IsLinkedToNext)
                {
                    left--;
                }
                else
                {
                    break;
                }
            }

            int right = idx;
            while (right < connected.Count - 1)
            {
                var curSlot = connected[right];
                var curScreen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == curSlot);
                if (curScreen != null && curScreen.IsLinkedToNext)
                {
                    right++;
                }
                else
                {
                    break;
                }
            }

            var group = new List<int>();
            for (int k = left; k <= right; k++)
            {
                group.Add(connected[k]);
            }
            return group;
        }

        public static void SetScreenLinkedToNext(int slotIndex, bool isLinked)
        {
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                var s = cfg.Screens.FirstOrDefault(x => x.SlotIndex == slotIndex);
                if (s != null)
                {
                    s.IsLinkedToNext = isLinked;
                    if (isLinked)
                    {
                        var group = GetLinkedSlotGroup(cfg, slotIndex);
                        if (group.Count > 1)
                        {
                            var screensInGroup = cfg.Screens.Where(sc => group.Contains(sc.SlotIndex)).ToList();
                            int avgB = (int)Math.Round(screensInGroup.Average(sc => sc.LastBrightness));
                            int avgC = (int)Math.Round(screensInGroup.Average(sc => sc.LastContrast));

                            foreach (var sc in screensInGroup)
                            {
                                sc.LastBrightness = avgB;
                                sc.LastContrast = sc.LockRatio ? avgB : avgC;
                                sc.VirtualBrightness = sc.LastBrightness;
                                sc.VirtualContrast = sc.LastContrast;
                                QueueThrottledHardwareWrite(sc.SlotIndex, sc.LastBrightness, sc.LastContrast);
                            }
                        }
                    }
                    SaveConfigInternal(cfg);
                }
            }
            ReanchorAllScreens();
            NotifyStateChanged();
        }

        public static void SetScreenLevelsBatch(IReadOnlyList<(int SlotIndex, int Brightness, int Contrast, int VirtualBrightness, int VirtualContrast)> levels)
        {
            if (levels == null || levels.Count == 0) return;
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                foreach (var (slotIndex, b, c, vb, vc) in levels)
                {
                    var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slotIndex);
                    if (screen != null)
                    {
                        screen.LastBrightness = Math.Clamp(b, 0, 100);
                        screen.LastContrast = Math.Clamp(c, 0, 100);
                        screen.VirtualBrightness = Math.Clamp(vb, -100, 200);
                        screen.VirtualContrast = Math.Clamp(vc, -100, 200);
                        QueueThrottledHardwareWrite(slotIndex, screen.LastBrightness, screen.LastContrast);
                    }
                }
            }
            NotifyStateChanged();
        }

        public static void SetScreenLevelsBatch(IReadOnlyList<(int SlotIndex, int Brightness, int Contrast)> levels)
        {
            if (levels == null || levels.Count == 0) return;
            SetScreenLevelsBatch(levels.Select(x => (x.SlotIndex, x.Brightness, x.Contrast, x.Brightness, x.Contrast)).ToList());
        }

        #endregion

        private static void ApplyProfileToMemory(ScreenBrightnessConfig cfg, ScreenBrightnessProfile profile)
        {
            for (int i = 0; i < MaxScreens; i++)
            {
                var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == i);
                var target = profile.Targets?.FirstOrDefault(t => t.SlotIndex == i);
                if (screen != null && target != null && target.Enabled)
                {
                    int b = Math.Clamp(target.Brightness, 0, 100);
                    int c = screen.LockRatio ? b : Math.Clamp(target.Contrast, 0, 100);
                    screen.LastBrightness = b;
                    screen.LastContrast = c;
                    screen.VirtualBrightness = b;
                    screen.VirtualContrast = c;
                }
            }
        }

        private static async Task ApplyProfileToHardwareDeltaOnlyAsync(ScreenBrightnessProfile profile, CancellationToken token)
        {
            var cfg = LoadConfig();
            var bound = BindMonitorsToSlots(cfg);
            if (bound.Count == 0) return;

            var connectedSlotIndices = bound.Keys.ToArray();
            foreach (var slotIndex in connectedSlotIndices.OrderBy(k => k))
            {
                if (token.IsCancellationRequested) return;
                var screenCfg = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slotIndex);
                var target = profile.Targets?.FirstOrDefault(t => t.SlotIndex == slotIndex);
                if (screenCfg == null || target == null || !target.Enabled) continue;

                int targetB = Math.Clamp(target.Brightness, 0, 100);
                int targetC = screenCfg.LockRatio ? targetB : Math.Clamp(target.Contrast, 0, 100);

                var throttle = Throttles.GetOrAdd(slotIndex, _ => new ScreenWriteThrottle());
                lock (throttle.Sync)
                {
                    throttle.PendingBrightness = targetB;
                    throttle.PendingContrast = targetC;
                    throttle.LastWriteUtc = DateTime.UtcNow;
                }

                if (await WriteSingleScreenHardwareWithRetryAsync(slotIndex, targetB, targetC, writeBrightness: true, writeContrast: true).ConfigureAwait(false))
                {
                    lock (throttle.Sync)
                    {
                        throttle.LastWrittenBrightness = targetB;
                        throttle.LastWrittenContrast = targetC;
                    }
                }

                if (token.IsCancellationRequested) return;
                await Task.Delay(30).ConfigureAwait(false);
            }
        }

        #endregion

        #region Non-Activating Click-Through "Identify Screens" Banners

        public static void ShowIdentifyBanners()
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var cfg = LoadConfig();
                var bound = BindMonitorsToSlots(cfg);
                var banners = new List<Window>();

                foreach (var kvp in bound.OrderBy(k => k.Key))
                {
                    int slotIndex = kvp.Key;
                    var mon = kvp.Value;
                    var slotCfg = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slotIndex);
                    string slotLabel = DefaultSlotName(slotIndex);
                    string customName = slotCfg?.CustomNickname ?? slotLabel;
                    string displayTitle = !string.IsNullOrWhiteSpace(customName) && !string.Equals(customName, slotLabel, StringComparison.OrdinalIgnoreCase)
                        ? customName
                        : slotLabel;

                    var banner = new Window
                    {
                        Width = 360,
                        Height = 130,
                        WindowDecorations = WindowDecorations.None,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Topmost = true,
                        CanResize = false,
                        Background = Brushes.Transparent,
                        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                        WindowStartupLocation = WindowStartupLocation.Manual
                    };

                    int centerX = mon.Bounds.X + (mon.Bounds.Width - 360) / 2;
                    int centerY = mon.Bounds.Y + 80;
                    banner.Position = new PixelPoint(centerX, centerY);

                    banner.Content = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#EB0F172A")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
                        BorderThickness = new Thickness(2.5),
                        CornerRadius = new CornerRadius(14),
                        Padding = new Thickness(20, 16),
                        Child = new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Spacing = 6,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = displayTitle,
                                    FontSize = 24,
                                    FontWeight = FontWeight.Bold,
                                    Foreground = Brushes.White,
                                    HorizontalAlignment = HorizontalAlignment.Center
                                },
                                new TextBlock
                                {
                                    Text = string.IsNullOrWhiteSpace(mon.ModelName) ? mon.DeviceName : mon.ModelName,
                                    FontSize = 13,
                                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                                    HorizontalAlignment = HorizontalAlignment.Center
                                }
                            }
                        }
                    };

                    banner.Opened += (_, _) =>
                    {
                        var handle = banner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                        if (handle != IntPtr.Zero)
                        {
                            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
                            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
                        }
                    };

                    banners.Add(banner);
                    banner.Show();
                }

                await Task.Delay(2200);
                foreach (var b in banners)
                {
                    try { b.Close(); } catch { }
                }
            });
        }

        #endregion

        #region Win32 DDC/CI (dxva2.dll) & Internal Backlight (\\.\LCD) Interop

        private static bool TryReadDdcMonitor(
            IntPtr hMonitor,
            out int normBrightness,
            out int normContrast,
            out bool supportsBrightness,
            out bool supportsContrast,
            out string description)
        {
            normBrightness = 65;
            normContrast = 65;
            supportsBrightness = false;
            supportsContrast = false;
            description = string.Empty;

            if (hMonitor == IntPtr.Zero) return false;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            {
                return false;
            }

            var physicalMonitors = new PHYSICAL_MONITOR[count];
            bool gotHandles = false;
            try
            {
                gotHandles = GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors);
                if (!gotHandles) return false;

                for (int i = 0; i < count; i++)
                {
                    IntPtr hPhys = physicalMonitors[i].hPhysicalMonitor;

                    if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(physicalMonitors[i].szPhysicalMonitorDescription))
                    {
                        description = physicalMonitors[i].szPhysicalMonitorDescription.Trim();
                    }

                    var rb = ReadDdcFeature(hPhys, DdcFeature.Brightness);
                    if (rb.Ok)
                    {
                        supportsBrightness = true;
                        normBrightness = DdcWriteSafety.ToPercent(DdcFeature.Brightness, rb.Current, rb.Min, rb.Max);
                    }

                    DdcPause(DdcWriteSafety.CommandSpacingMs);
                    var rc = ReadDdcFeature(hPhys, DdcFeature.Contrast);
                    if (rc.Ok)
                    {
                        supportsContrast = true;
                        normContrast = DdcWriteSafety.ToPercent(DdcFeature.Contrast, rc.Current, rc.Min, rc.Max);
                    }

                    if (supportsBrightness)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (gotHandles)
                {
                    try { DestroyPhysicalMonitors(count, physicalMonitors); } catch { }
                }
            }
        }

        /// <summary>
        /// Writes one monitor while holding exclusive I2C bus access. Bus arbitration is fully asynchronous: a slot
        /// waiting for <see cref="BusLock"/> or for the VESA inter-command spacing yields its ThreadPool thread.
        /// The trailing spacing runs before the lock is released so the next bus user always gets a quiet bus.
        /// </summary>
        private static async Task<bool> TryWriteDdcMonitorAsync(IntPtr hMonitor, int brightnessPercent, int contrastPercent, bool writeBrightness, bool writeContrast)
        {
            if (hMonitor == IntPtr.Zero || (!writeBrightness && !writeContrast)) return false;

            if (!await BusLock.WaitAsync(BusLockTimeoutMs).ConfigureAwait(false))
            {
                LogDdc($"[DDC] Timed out ({BusLockTimeoutMs}ms) waiting for BusLock to write hMon={hMonitor}; write skipped.");
                return false;
            }

            try
            {
                var busWriter = DdcBusWriteOverrideForTesting;
                if (busWriter != null)
                {
                    return busWriter(hMonitor, brightnessPercent, contrastPercent, writeBrightness, writeContrast);
                }
                return await WriteDdcMonitorOnBusAsync(hMonitor, brightnessPercent, contrastPercent, writeBrightness, writeContrast).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DDC] TryWriteDdcMonitorAsync exception on hMon={hMonitor}: {ex}");
                return false;
            }
            finally
            {
                try
                {
                    await DdcPauseAsync(VesaInterCommandDelayMs).ConfigureAwait(false);
                }
                finally
                {
                    BusLock.Release();
                }
            }
        }

        /// <summary>Native DDC/CI command sequence; the caller must hold <see cref="BusLock"/>.</summary>
        private static async Task<bool> WriteDdcMonitorOnBusAsync(IntPtr hMonitor, int brightnessPercent, int contrastPercent, bool writeBrightness, bool writeContrast)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            {
                Program.LogDebug($"[DDC] GetNumberOfPhysicalMonitorsFromHMONITOR failed or 0 for hMon={hMonitor}");
                return false;
            }

            var physicalMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
            {
                Program.LogDebug($"[DDC] GetPhysicalMonitorsFromHMONITOR failed for hMon={hMonitor}, count={count}");
                return false;
            }

            try
            {
                bool anySucceeded = false;

                for (int i = 0; i < count; i++)
                {
                    // 0 is a valid handle (see GetOrCreateSession); never skip it.
                    IntPtr hPhysical = physicalMonitors[i].hPhysicalMonitor;

                    bool wroteB = false;
                    bool wroteC = false;

                    if (writeBrightness)
                    {
                        var outcome = await DdcWriteSafety.WriteFeatureAsync(
                            DdcFeature.Brightness,
                            brightnessPercent,
                            () => ReadDdcFeatureAsync(hPhysical, DdcFeature.Brightness),
                            v => SetMonitorBrightness(hPhysical, v),
                            v => SetVCPFeature(hPhysical, 0x10, v),
                            DdcPauseAsync).ConfigureAwait(false);
                        wroteB = DdcWriteSafety.IsSuccess(outcome);
                        Program.LogDebug($"[DDC] Brightness write hPhysical={hPhysical} target={brightnessPercent}% -> {outcome} (win32Err={(wroteB ? 0 : Marshal.GetLastWin32Error())})");
                    }

                    if (writeContrast)
                    {
                        if (writeBrightness)
                        {
                            await DdcPauseAsync(DdcWriteSafety.CommandSpacingMs).ConfigureAwait(false);
                        }

                        var outcome = await DdcWriteSafety.WriteFeatureAsync(
                            DdcFeature.Contrast,
                            contrastPercent,
                            () => ReadDdcFeatureAsync(hPhysical, DdcFeature.Contrast),
                            v => SetMonitorContrast(hPhysical, v),
                            v => SetVCPFeature(hPhysical, 0x12, v),
                            DdcPauseAsync).ConfigureAwait(false);
                        wroteC = DdcWriteSafety.IsSuccess(outcome);
                        Program.LogDebug($"[DDC] Contrast write hPhysical={hPhysical} target={contrastPercent}% -> {outcome} (win32Err={(wroteC ? 0 : Marshal.GetLastWin32Error())})");
                    }

                    if (writeBrightness && wroteB)
                    {
                        anySucceeded = true;
                    }
                    else if (!writeBrightness && writeContrast && wroteC)
                    {
                        anySucceeded = true;
                    }
                }

                Program.LogDebug($"[DDC] TryWriteDdcMonitorAsync hMon={hMonitor} count={count} writeB={writeBrightness}({brightnessPercent}%) writeC={writeContrast}({contrastPercent}%) -> anySucceeded={anySucceeded}");
                return anySucceeded;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DDC] TryWriteDdcMonitorAsync exception on hMon={hMonitor}: {ex}");
                return false;
            }
            finally
            {
                DestroyPhysicalMonitors(count, physicalMonitors);
            }
        }

        /// <summary>
        /// Reads the current raw value and real hardware range of a feature (high-level MCCS call, then raw VCP after a
        /// spaced pause). A failed read yields <c>Ok = false</c>; callers must then never guess a range and write.
        /// </summary>
        private static DdcFeatureReading ReadDdcFeature(IntPtr hPhysical, DdcFeature feature)
        {
            try
            {
                return DdcWriteSafety.ReadFeature(
                    () =>
                    {
                        uint mn, cur, mx;
                        bool ok = feature == DdcFeature.Brightness
                            ? GetMonitorBrightness(hPhysical, out mn, out cur, out mx)
                            : GetMonitorContrast(hPhysical, out mn, out cur, out mx);
                        return new DdcFeatureReading(ok, cur, mn, mx);
                    },
                    () =>
                    {
                        bool ok = GetVCPFeatureAndVCPFeatureReply(hPhysical, feature == DdcFeature.Brightness ? (byte)0x10 : (byte)0x12, out _, out uint cur, out uint mx);
                        return new DdcFeatureReading(ok, cur, 0, mx);
                    },
                    DdcPause);
            }
            catch
            {
                return default;
            }
        }

        /// <summary><see cref="ReadDdcFeature"/> with a non-blocking fallback pause, for the asynchronous write pipeline.</summary>
        private static async Task<DdcFeatureReading> ReadDdcFeatureAsync(IntPtr hPhysical, DdcFeature feature)
        {
            try
            {
                return await DdcWriteSafety.ReadFeatureAsync(
                    () =>
                    {
                        uint mn, cur, mx;
                        bool ok = feature == DdcFeature.Brightness
                            ? GetMonitorBrightness(hPhysical, out mn, out cur, out mx)
                            : GetMonitorContrast(hPhysical, out mn, out cur, out mx);
                        return new DdcFeatureReading(ok, cur, mn, mx);
                    },
                    () =>
                    {
                        bool ok = GetVCPFeatureAndVCPFeatureReply(hPhysical, feature == DdcFeature.Brightness ? (byte)0x10 : (byte)0x12, out _, out uint cur, out uint mx);
                        return new DdcFeatureReading(ok, cur, 0, mx);
                    },
                    DdcPauseAsync).ConfigureAwait(false);
            }
            catch
            {
                return default;
            }
        }

        private static int NormalizeTo100(uint current, uint min, uint max)
        {
            if (max <= min) return (int)Math.Clamp(current, 0, 100);
            uint clamped = Math.Clamp(current, min, max);
            double ratio = (double)(clamped - min) / (max - min);
            return (int)Math.Clamp(Math.Round(ratio * 100.0), 0, 100);
        }

        private static uint DenormalizeFrom100(int percent, uint min, uint max)
        {
            int p = Math.Clamp(percent, 0, 100);
            if (max <= min) return (uint)p;
            double raw = min + (p / 100.0) * (max - min);
            return (uint)Math.Clamp((int)Math.Round(raw), (int)min, (int)max);
        }

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_VIDEO_QUERY_SUPPORTED_BRIGHTNESS = 0x00230494;
        private const uint IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS = 0x00230498;
        private const uint IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS = 0x0023049C;

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAY_BRIGHTNESS
        {
            public byte ucDisplayPolicy;
            public byte ucACBrightness;
            public byte ucDCBrightness;
        }

        private static bool TryReadInternalLcdBrightness(out int percent)
        {
            percent = 65;
            IntPtr hLcd = CreateFileW(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hLcd == IntPtr.Zero || hLcd == new IntPtr(-1)) return false;

            try
            {
                var db = new DISPLAY_BRIGHTNESS();
                if (!DeviceIoControl(hLcd, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS, IntPtr.Zero, 0, ref db, Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), out _, IntPtr.Zero))
                {
                    return false;
                }

                byte rawLevel = db.ucACBrightness != 0 ? db.ucACBrightness : db.ucDCBrightness;
                byte minLevel = 0;
                byte maxLevel = 100;

                byte[] supported = new byte[256];
                if (DeviceIoControlBytes(hLcd, IOCTL_VIDEO_QUERY_SUPPORTED_BRIGHTNESS, IntPtr.Zero, 0, supported, supported.Length, out uint bytesReturned, IntPtr.Zero) && bytesReturned > 1)
                {
                    minLevel = supported[0];
                    maxLevel = supported[bytesReturned - 1];
                    for (int i = 0; i < bytesReturned; i++)
                    {
                        if (supported[i] < minLevel) minLevel = supported[i];
                        if (supported[i] > maxLevel) maxLevel = supported[i];
                    }
                }

                percent = NormalizeTo100(rawLevel, minLevel, maxLevel);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(hLcd);
            }
        }

        private static bool TryWriteInternalLcdBrightness(int percent)
        {
            IntPtr hLcd = CreateFileW(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hLcd == IntPtr.Zero || hLcd == new IntPtr(-1)) return false;

            try
            {
                byte targetByte = (byte)Math.Clamp(percent, 0, 100);
                byte[] supported = new byte[256];
                if (DeviceIoControlBytes(hLcd, IOCTL_VIDEO_QUERY_SUPPORTED_BRIGHTNESS, IntPtr.Zero, 0, supported, supported.Length, out uint bytesReturned, IntPtr.Zero) && bytesReturned > 0)
                {
                    byte minLevel = supported[0];
                    byte maxLevel = supported[bytesReturned - 1];
                    for (int i = 0; i < bytesReturned; i++)
                    {
                        if (supported[i] < minLevel) minLevel = supported[i];
                        if (supported[i] > maxLevel) maxLevel = supported[i];
                    }
                    uint denorm = DenormalizeFrom100(percent, minLevel, maxLevel);
                    byte closest = supported[0];
                    int bestDiff = Math.Abs(closest - (int)denorm);
                    for (int i = 1; i < bytesReturned; i++)
                    {
                        int d = Math.Abs(supported[i] - (int)denorm);
                        if (d < bestDiff)
                        {
                            bestDiff = d;
                            closest = supported[i];
                        }
                    }
                    targetByte = closest;
                }

                var current = new DISPLAY_BRIGHTNESS();
                if (DeviceIoControl(hLcd, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS, IntPtr.Zero, 0, ref current, Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), out _, IntPtr.Zero) &&
                    current.ucACBrightness == targetByte && current.ucDCBrightness == targetByte)
                {
                    return true;
                }

                var db = new DISPLAY_BRIGHTNESS
                {
                    ucDisplayPolicy = 0x03,
                    ucACBrightness = targetByte,
                    ucDCBrightness = targetByte
                };

                return DeviceIoControl(hLcd, IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS, ref db, Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(hLcd);
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public uint refreshRateNumerator;
            public uint refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        /// <summary>Only sized correctly (64 bytes); the mode contents are never read.</summary>
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorContrast(IntPtr hMonitor, out uint pdwMinimumContrast, out uint pdwCurrentContrast, out uint pdwMaximumContrast);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorContrast(IntPtr hMonitor, uint dwNewContrast);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr hMonitor,
            byte bVCPCode,
            out uint pvct,
            out uint pdwCurrentValue,
            out uint pdwMaximumValue);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            ref DISPLAY_BRIGHTNESS lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref DISPLAY_BRIGHTNESS lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlBytes(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            [Out] byte[] lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion

        #region Hardware Safety & Liability Disclaimer

        public static async Task<bool> ShowDisclaimerDialogAsync(Window? owner = null)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(() => ShowDisclaimerDialogAsync(owner));
            }

            var tcs = new TaskCompletionSource<bool>();
            var dialog = new Window
            {
                Title = "Hardware Control & Safety Disclaimer",
                Width = 540,
                Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false,
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppSurfaceBrush", "#0F172A")
            };

            var mainPanel = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16
            };

            var header = new TextBlock
            {
                Text = "Screen Brightness & DDC/CI Hardware Disclaimer",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            };

            var warningCard = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderSoftBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16)
            };

            var scroll = new ScrollViewer
            {
                MaxHeight = 220,
                Content = new TextBlock
                {
                    Text = "The Screen Brightness feature communicates directly with your physical monitor's internal scaler microcontroller over the GPU I2C bus via VESA DDC/CI (Monitor Control Command Set) and laptop display drivers.\n\n" +
                           "Please understand and acknowledge the following:\n" +
                           "1. Display microcontrollers, scalers, and firmware vary widely by manufacturer and panel type. Some monitors may occasionally be sensitive to hardware DDC/CI commands.\n" +
                           "2. KEYPULSE AND ITS DEVELOPERS PROVIDE THIS SOFTWARE STRICTLY \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.\n" +
                           "3. THE AUTHORS AND CONTRIBUTORS ACCEPT NO LIABILITY FOR ANY DIRECT, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL HARDWARE DAMAGE, SCALER LOCKUPS, WEAR, OR LOSS OF USE.\n" +
                           "4. Many monitors save every brightness/contrast change to a small internal memory (NVRAM/EEPROM) that supports a limited number of writes. Very frequent changes over a long time may wear it. To minimize this, KeyPulse only sends brightness (VCP 0x10) and contrast (VCP 0x12), reads the monitor first and skips values it already has, writes held-key / slider changes only once they settle, never auto-retries a monitor that refuses a command, and never writes to a monitor whose range it could not read.\n" +
                           "5. You can completely disable this feature at any time in Settings. When disabled, KeyPulse completely halts all display queries, DDC/CI communications, and background monitor processes.\n\n" +
                           "Do you understand these risks and accept these terms to activate hardware display control?",
                    FontSize = 12,
                    LineHeight = 18,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#CBD5E1"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            warningCard.Child = scroll;

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 12
            };

            var cancelBtn = new Button
            {
                Content = "Cancel (Keep Disabled)",
                Classes = { "Secondary" },
                MinWidth = 160,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            var acceptBtn = new Button
            {
                Content = "Accept Risk & Enable",
                Classes = { "Primary" },
                MinWidth = 160,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            cancelBtn.Click += (_, _) =>
            {
                var cfg = LoadConfig();
                cfg.Enabled = false;
                SaveConfig(cfg);
                UnregisterAllBrightnessHotkeys();
                InvalidateMonitorCache();
                NotifyStateChanged();
                tcs.TrySetResult(false);
                dialog.Close();
            };

            acceptBtn.Click += (_, _) =>
            {
                var cfg = LoadConfig();
                cfg.DisclaimerAccepted = true;
                cfg.Enabled = true;
                SaveConfig(cfg);
                RegisterBrightnessHotkeys();
                NotifyStateChanged();
                tcs.TrySetResult(true);
                dialog.Close();
            };

            dialog.KeyDown += (_, ke) =>
            {
                if (ke.Key == Avalonia.Input.Key.Escape)
                {
                    ke.Handled = true;
                    dialog.Close();
                }
            };

            dialog.Closed += (_, _) =>
            {
                tcs.TrySetResult(false);
            };

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(acceptBtn);

            mainPanel.Children.Add(header);
            mainPanel.Children.Add(warningCard);
            mainPanel.Children.Add(btnRow);
            dialog.Content = mainPanel;

            if (owner != null && owner.IsVisible)
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                dialog.Show();
            }

            return await tcs.Task;
        }

        #endregion
    }
}
