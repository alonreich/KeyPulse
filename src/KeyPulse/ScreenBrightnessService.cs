using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        public int Brightness { get; set; } = 65;
        public int Contrast { get; set; } = 65;
        public bool LockRatio { get; set; } = true;
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
        private const int ThrottleIntervalMs = 250;

        private static string AppDataFolder => ConfigStore.ConfigDirectory;

        public static string ConfigFilePath => Path.Combine(AppDataFolder, "screen_brightness.json");
        public static string BackupConfigFilePath => Path.Combine(AppDataFolder, "screen_brightness.lkg.json");

        private static readonly object ConfigLock = new();
        private static ScreenBrightnessConfig? _cachedConfig;

        private static readonly object CycleLock = new();
        private static readonly object HardwareWriteLock = new();
        private static CancellationTokenSource? _cycleCts;
        private static bool _hasStartedCycleInSession;

        private sealed class ScreenWriteThrottle
        {
            public readonly object Sync = new();
            public int PendingBrightness = -1;
            public int PendingContrast = -1;
            public int LastWrittenBrightness = -1;
            public int LastWrittenContrast = -1;
            public DateTime LastWriteUtc = DateTime.MinValue;
            public bool FlushScheduled;
        }

        private static readonly ConcurrentDictionary<int, ScreenWriteThrottle> Throttles = new();

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

        public static ScreenBrightnessConfig LoadConfig()
        {
            lock (ConfigLock)
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
        }

        public static bool SaveConfig(ScreenBrightnessConfig config)
        {
            lock (ConfigLock)
            {
                NormalizeConfig(config);
                _cachedConfig = config;
                bool ok = SaveConfigInternal(config);
                return ok;
            }
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
                defaults = CreateDefaultConfig();
                _cachedConfig = defaults;
                SaveConfigInternal(defaults);
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

                var tempPath = ConfigFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(ConfigFilePath))
                {
                    try
                    {
                        File.Copy(ConfigFilePath, BackupConfigFilePath, overwrite: true);
                    }
                    catch { }
                }

                File.Move(tempPath, ConfigFilePath, overwrite: true);
                try
                {
                    File.Copy(ConfigFilePath, BackupConfigFilePath, overwrite: true);
                }
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ScreenBrightnessConfig CreateDefaultConfig()
        {
            var cfg = new ScreenBrightnessConfig
            {
                StepUpHotkey = "Ctrl+Alt+=, Ctrl+Alt+NumPadAdd",
                StepDownHotkey = "Ctrl+Alt+-, Ctrl+Alt+NumPadSubtract",
                StepDelta = 5,
                LockMonitorDifferential = true,
                DirectHotkeys = new List<ScreenDirectHotkey>(),
                Screens = new List<ScreenHardwareSetting>
                {
                    new ScreenHardwareSetting { SlotIndex = 0, CustomNickname = "Screen A", LockRatio = true, LastBrightness = 65, LastContrast = 65 },
                    new ScreenHardwareSetting { SlotIndex = 1, CustomNickname = "Screen B", LockRatio = true, LastBrightness = 65, LastContrast = 65 },
                    new ScreenHardwareSetting { SlotIndex = 2, CustomNickname = "Screen C", LockRatio = true, LastBrightness = 65, LastContrast = 65 }
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
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 15, Contrast = 15 },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 15, Contrast = 15 },
                            new ScreenCycleEntry { SlotIndex = 2, Brightness = 15, Contrast = 15 }
                        }
                    },
                    new ScreenBrightnessProfile
                    {
                        Name = "Balanced",
                        ShowInTrayQuickActions = true,
                        IncludeInCycle = true,
                        Targets = new List<ScreenCycleEntry>
                        {
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 55, Contrast = 55 },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 55, Contrast = 55 },
                            new ScreenCycleEntry { SlotIndex = 2, Brightness = 55, Contrast = 55 }
                        }
                    },
                    new ScreenBrightnessProfile
                    {
                        Name = "Daylight / Competitive",
                        ShowInTrayQuickActions = true,
                        IncludeInCycle = true,
                        Targets = new List<ScreenCycleEntry>
                        {
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 90, Contrast = 90 },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 90, Contrast = 90 },
                            new ScreenCycleEntry { SlotIndex = 2, Brightness = 90, Contrast = 90 }
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
            foreach (var dh in cfg.DirectHotkeys)
            {
                if (string.IsNullOrWhiteSpace(dh.Id)) dh.Id = Guid.NewGuid().ToString("N");
                dh.Brightness = Math.Clamp(dh.Brightness, 0, 100);
                dh.Contrast = Math.Clamp(dh.Contrast, 0, 100);
            }

            cfg.Screens ??= new List<ScreenHardwareSetting>();
            cfg.Profiles ??= new List<ScreenBrightnessProfile>();

            for (int i = 0; i < MaxScreens; i++)
            {
                var existing = cfg.Screens.FirstOrDefault(s => s.SlotIndex == i);
                if (existing == null)
                {
                    cfg.Screens.Add(new ScreenHardwareSetting
                    {
                        SlotIndex = i,
                        CustomNickname = DefaultSlotName(i),
                        LockRatio = cfg.GlobalLockBrightnessContrast,
                        LastBrightness = 65,
                        LastContrast = 65
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
                    if (existing.LockRatio)
                    {
                        existing.LastContrast = existing.LastBrightness;
                    }
                }
            }

            cfg.Screens = cfg.Screens.OrderBy(s => s.SlotIndex).Take(MaxScreens).ToList();

            foreach (var p in cfg.Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Brightness Preset";
                p.Targets ??= new List<ScreenCycleEntry>();
                for (int i = 0; i < MaxScreens; i++)
                {
                    var t = p.Targets.FirstOrDefault(x => x.SlotIndex == i);
                    if (t == null)
                    {
                        p.Targets.Add(new ScreenCycleEntry { SlotIndex = i, Brightness = 50, Contrast = 50 });
                    }
                    else
                    {
                        t.Brightness = Math.Clamp(t.Brightness, 0, 100);
                        t.Contrast = Math.Clamp(t.Contrast, 0, 100);
                    }
                }
                p.Targets = p.Targets.OrderBy(x => x.SlotIndex).Take(MaxScreens).ToList();
            }
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
            public PixelRect Bounds { get; set; }
        }

        private static List<ConnectedMonitorDescriptor> EnumerateConnectedMonitors()
        {
            var raw = new List<ConnectedMonitorDescriptor>();

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
            var connected = EnumerateConnectedMonitors();
            var result = new Dictionary<int, ConnectedMonitorDescriptor>();
            var unassignedMonitors = new List<ConnectedMonitorDescriptor>(connected);

            lock (ConfigLock)
            {
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
        public static List<int> GetConnectedSlotIndices(ScreenBrightnessConfig? cfg = null)
        {
            cfg ??= LoadConfig();
            var bound = BindMonitorsToSlots(cfg);
            return bound.Keys.OrderBy(k => k).ToList();
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
                var s = cfg.Screens.First(x => x.SlotIndex == i);
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
            var cfg = LoadConfig();
            var bound = BindMonitorsToSlots(cfg);

            var results = new ScreenLiveState[MaxScreens];
            for (int i = 0; i < MaxScreens; i++)
            {
                if (ct.IsCancellationRequested) break;
                int slotIndex = i;
                var slotCfg = cfg.Screens.First(x => x.SlotIndex == slotIndex);
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
                    var slotCfg = cfg.Screens.First(x => x.SlotIndex == i);
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
                        IsConnected = mon != null,
                        IsPrimary = mon?.IsPrimary ?? false,
                        Bounds = mon?.Bounds ?? default
                    };
                }
            }

            lock (ConfigLock)
            {
                bool updated = false;
                foreach (var res in results)
                {
                    if (!res.IsConnected || !res.SupportsBrightness) continue;
                    var slotCfg = cfg.Screens.FirstOrDefault(x => x.SlotIndex == res.SlotIndex);
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
                    SaveConfigInternal(cfg);
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

            if ((mon.IsPrimary || slotIndex == 0) && TryReadInternalLcdBrightness(out int lcdPercent))
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
            if (slotIndex < 0 || slotIndex >= MaxScreens) return;

            brightness = Math.Clamp(brightness, 0, 100);
            contrast = Math.Clamp(contrast, 0, 100);

            var cfg = LoadConfig();
            lock (ConfigLock)
            {
                var slotCfg = cfg.Screens.First(s => s.SlotIndex == slotIndex);
                if (slotCfg.LockRatio && updateContrastFromLock)
                {
                    contrast = brightness;
                }
                slotCfg.LastBrightness = brightness;
                slotCfg.LastContrast = contrast;
            }

            Program.LogDebug($"[ScreenBrightness] RequestScreenLevelChange slot={slotIndex} b={brightness} c={contrast} updateContrastFromLock={updateContrastFromLock}");
            QueueThrottledHardwareWrite(slotIndex, brightness, contrast);
        }

        private static void QueueThrottledHardwareWrite(int slotIndex, int brightness, int contrast)
        {
            var throttle = Throttles.GetOrAdd(slotIndex, _ => new ScreenWriteThrottle());
            bool fireImmediately = false;
            int delayMs = ThrottleIntervalMs;

            lock (throttle.Sync)
            {
                throttle.PendingBrightness = brightness;
                throttle.PendingContrast = contrast;

                var elapsedMs = (DateTime.UtcNow - throttle.LastWriteUtc).TotalMilliseconds;
                if (elapsedMs >= ThrottleIntervalMs && !throttle.FlushScheduled)
                {
                    throttle.LastWriteUtc = DateTime.UtcNow;
                    fireImmediately = true;
                }
                else if (!throttle.FlushScheduled)
                {
                    throttle.FlushScheduled = true;
                    delayMs = Math.Max(25, ThrottleIntervalMs - (int)Math.Max(0, elapsedMs));
                }
                else
                {
                    return;
                }
            }

            Program.LogDebug($"[ScreenBrightness] QueueThrottledHardwareWrite slot={slotIndex} b={brightness} c={contrast} immediate={fireImmediately} delay={delayMs}ms");
            if (fireImmediately)
            {
                _ = Task.Run(() => ExecutePendingWriteForSlot(slotIndex, throttle));
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    ExecutePendingWriteForSlot(slotIndex, throttle);
                });
            }
        }

        private static void ExecutePendingWriteForSlot(int slotIndex, ScreenWriteThrottle throttle)
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
                    Program.LogDebug($"[ScreenBrightness] ExecutePendingWriteForSlot slot={slotIndex} targets unchanged ({targetB}/{targetC})");
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

            Program.LogDebug($"[ScreenBrightness] ExecutePendingWriteForSlot slot={slotIndex} writing targetB={targetB} targetC={targetC} changeB={changeB} changeC={changeC}");
            bool written = WriteSingleScreenHardwareWithRetry(slotIndex, targetB, targetC, changeB, changeC);
            Program.LogDebug($"[ScreenBrightness] ExecutePendingWriteForSlot slot={slotIndex} write result={written}");

            lock (throttle.Sync)
            {
                throttle.LastWrittenBrightness = targetB;
                throttle.LastWrittenContrast = targetC;
            }

            bool needsFollowUp = false;
            lock (throttle.Sync)
            {
                if (throttle.PendingBrightness != throttle.LastWrittenBrightness ||
                    throttle.PendingContrast != throttle.LastWrittenContrast)
                {
                    if (!throttle.FlushScheduled)
                    {
                        throttle.FlushScheduled = true;
                        needsFollowUp = true;
                    }
                }
            }

            if (needsFollowUp)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(ThrottleIntervalMs).ConfigureAwait(false);
                    ExecutePendingWriteForSlot(slotIndex, throttle);
                });
            }
            else
            {
                _ = Task.Run(() =>
                {
                    var cfg = LoadConfig();
                    lock (ConfigLock)
                    {
                        SaveConfigInternal(cfg);
                    }
                });
            }
        }

        private static bool WriteSingleScreenHardwareWithRetry(int slotIndex, int brightness, int contrast, bool writeBrightness, bool writeContrast)
        {
            lock (HardwareWriteLock)
            {
                try
                {
                    var cfg = LoadConfig();
                    var bound = BindMonitorsToSlots(cfg);
                    if (!bound.TryGetValue(slotIndex, out var mon))
                    {
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetry slot={slotIndex} not found in bound monitors (bound count={bound.Count})");
                        return false;
                    }

                    Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetry slot={slotIndex} hMon={mon.HMonitor} isPrimary={mon.IsPrimary} model={mon.ModelName}");

                    if (TryWriteDdcMonitor(mon.HMonitor, brightness, contrast, writeBrightness, writeContrast))
                    {
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetry slot={slotIndex} TryWriteDdcMonitor succeeded");
                        Thread.Sleep(30);
                        return true;
                    }

                    if ((mon.IsPrimary || slotIndex == 0) && writeBrightness && TryWriteInternalLcdBrightness(brightness))
                    {
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetry slot={slotIndex} TryWriteInternalLcdBrightness succeeded");
                        Thread.Sleep(30);
                        return true;
                    }

                    Thread.Sleep(50);
                    var refreshed = BindMonitorsToSlots(cfg);
                    if (refreshed.TryGetValue(slotIndex, out var freshMon))
                    {
                        bool freshOk = TryWriteDdcMonitor(freshMon.HMonitor, brightness, contrast, writeBrightness, writeContrast);
                        Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetry slot={slotIndex} retry freshMon hMon={freshMon.HMonitor} result={freshOk}");
                        Thread.Sleep(30);
                        return freshOk;
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[ScreenBrightness] WriteSingleScreenHardwareWithRetry slot={slotIndex} exception: {ex}");
                }
                return false;
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
            ScreenBrightnessProfile? targetProfile = null;
            CancellationToken token;

            lock (CycleLock)
            {
                var cfg = LoadConfig();
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
                targetProfile = ordered[nextIndex];
                cfg.LastCycleProfileId = targetProfile.Id;

                ApplyProfileToMemory(cfg, targetProfile);
                SaveConfigInternal(cfg);

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
                        await Task.Delay(65, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;
                        ApplyProfileToHardwareDeltaOnly(capturedProfile, token);
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
            if (profile == null) return;
            CancellationToken token;

            lock (CycleLock)
            {
                var cfg = LoadConfig();
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

            _ = Task.Run(() =>
            {
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        ApplyProfileToHardwareDeltaOnly(profile, token);
                    }
                }
                catch
                {
                }
            });
        }

        #region Hotkey Registration

        private static readonly List<int> _registeredBrightnessHotkeyIds = new();

        public static void UnregisterAllBrightnessHotkeys()
        {
            lock (CycleLock)
            {
                foreach (var id in _registeredBrightnessHotkeyIds)
                {
                    try { HotkeyManager.Unregister(id); } catch { }
                }
                _registeredBrightnessHotkeyIds.Clear();
            }
        }

        public static void RegisterBrightnessHotkeys()
        {
            lock (CycleLock)
            {
                UnregisterAllBrightnessHotkeys();
                var cfg = LoadConfig();

                void RegisterKeyList(string hotkeys, Action action, string desc)
                {
                    if (string.IsNullOrWhiteSpace(hotkeys)) return;
                    var parts = hotkeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var combo in parts)
                    {
                        if (HotkeyManager.Register(combo, action, out int id))
                        {
                            _registeredBrightnessHotkeyIds.Add(id);
                            Program.LogDebug($"[ScreenBrightness] Registered {desc} hotkey: {combo} (id={id})");
                        }
                        else
                        {
                            Program.LogDebug($"[ScreenBrightness] Failed to register {desc} hotkey: {combo}");
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
                        var captured = profile;
                        RegisterKeyList(profile.Hotkey, () => ApplyProfileAsync(captured), $"Profile '{profile.Name}'");
                    }
                }

                if (cfg.DirectHotkeys != null)
                {
                    foreach (var dh in cfg.DirectHotkeys)
                    {
                        if (!string.IsNullOrWhiteSpace(dh.Hotkey))
                        {
                            var capturedDh = dh;
                            RegisterKeyList(dh.Hotkey, () => TriggerDirectHotkey(capturedDh), $"Direct-{capturedDh.TargetSlotIndex}");
                        }
                    }
                }
            }
        }

        public static void TriggerBrightnessStep(int delta)
        {
            var cfg = LoadConfig();
            var connectedSlots = GetConnectedSlotIndices(cfg);
            if (connectedSlots.Count == 0) return;

            if (cfg.LockMonitorDifferential || connectedSlots.Count == 1)
            {
                foreach (var slot in connectedSlots)
                {
                    var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                    if (screen != null)
                    {
                        int newB = Math.Clamp(screen.LastBrightness + delta, 0, 100);
                        int newC = screen.LockRatio ? newB : Math.Clamp(screen.LastContrast + delta, 0, 100);
                        RequestScreenLevelChange(slot, newB, newC, updateContrastFromLock: screen.LockRatio);
                    }
                }
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

        public static void RequestMasterLevelChange(int deltaBrightness)
        {
            var cfg = LoadConfig();
            var connectedSlots = GetConnectedSlotIndices(cfg);
            if (connectedSlots.Count == 0 || deltaBrightness == 0) return;

            foreach (var slot in connectedSlots)
            {
                var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                if (screen != null)
                {
                    int newB = Math.Clamp(screen.LastBrightness + deltaBrightness, 0, 100);
                    int newC = screen.LockRatio ? newB : Math.Clamp(screen.LastContrast + deltaBrightness, 0, 100);
                    RequestScreenLevelChange(slot, newB, newC, updateContrastFromLock: screen.LockRatio);
                }
            }

            SaveConfig(cfg);
            NotifyStateChanged();
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
                }
            }
        }

        private static void ApplyProfileToHardwareDeltaOnly(ScreenBrightnessProfile profile, CancellationToken token)
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

                if (WriteSingleScreenHardwareWithRetry(slotIndex, targetB, targetC, writeBrightness: true, writeContrast: true))
                {
                    lock (throttle.Sync)
                    {
                        throttle.LastWrittenBrightness = targetB;
                        throttle.LastWrittenContrast = targetC;
                    }
                }

                if (token.IsCancellationRequested) return;
                Thread.Sleep(30);
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
                    string displayTitle = string.Equals(customName, slotLabel, StringComparison.OrdinalIgnoreCase)
                        ? slotLabel
                        : $"{slotLabel} • {customName}";

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
                    if (hPhys == IntPtr.Zero) continue;

                    if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(physicalMonitors[i].szPhysicalMonitorDescription))
                    {
                        description = physicalMonitors[i].szPhysicalMonitorDescription.Trim();
                    }

                    if (GetMonitorBrightness(hPhys, out uint minB, out uint curB, out uint maxB) && maxB > minB)
                    {
                        supportsBrightness = true;
                        normBrightness = NormalizeTo100(curB, minB, maxB);
                    }
                    else if (GetVCPFeatureAndVCPFeatureReply(hPhys, 0x10, out _, out uint curVcpB, out uint maxVcpB) && maxVcpB > 0)
                    {
                        supportsBrightness = true;
                        normBrightness = NormalizeTo100(curVcpB, 0, maxVcpB);
                    }

                    if (GetMonitorContrast(hPhys, out uint minC, out uint curC, out uint maxC) && maxC > minC)
                    {
                        supportsContrast = true;
                        normContrast = NormalizeTo100(curC, minC, maxC);
                    }
                    else if (GetVCPFeatureAndVCPFeatureReply(hPhys, 0x12, out _, out uint curVcpC, out uint maxVcpC) && maxVcpC > 0)
                    {
                        supportsContrast = true;
                        normContrast = NormalizeTo100(curVcpC, 0, maxVcpC);
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

        private static bool TryWriteDdcMonitor(IntPtr hMonitor, int brightnessPercent, int contrastPercent, bool writeBrightness, bool writeContrast)
        {
            if (hMonitor == IntPtr.Zero || (!writeBrightness && !writeContrast)) return false;

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
                    IntPtr hPhysical = physicalMonitors[i].hPhysicalMonitor;
                    if (hPhysical == IntPtr.Zero) continue;

                    bool wroteB = false;
                    bool wroteC = false;

                    if (writeBrightness)
                    {
                        uint targetRawB = (uint)Math.Clamp(brightnessPercent, 0, 100);

                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt > 0)
                            {
                                Thread.Sleep(35);
                            }

                            if (SetMonitorBrightness(hPhysical, targetRawB) || SetVCPFeature(hPhysical, 0x10, targetRawB))
                            {
                                wroteB = true;
                                break;
                            }
                        }

                        if (!wroteB)
                        {
                            int err = Marshal.GetLastWin32Error();
                            Program.LogDebug($"[DDC] SetMonitorBrightness & SetVCPFeature(0x10) failed after retries on hPhysical={hPhysical}, targetRaw={targetRawB}, win32Err={err}");
                        }
                    }

                    if (writeContrast)
                    {
                        if (wroteB)
                        {
                            Thread.Sleep(40);
                        }

                        uint targetRawC = (uint)Math.Clamp(contrastPercent, 0, 100);

                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt > 0)
                            {
                                Thread.Sleep(35);
                            }

                            if (SetMonitorContrast(hPhysical, targetRawC) || SetVCPFeature(hPhysical, 0x12, targetRawC))
                            {
                                wroteC = true;
                                break;
                            }
                        }

                        if (!wroteC)
                        {
                            int err = Marshal.GetLastWin32Error();
                            Program.LogDebug($"[DDC] SetMonitorContrast & SetVCPFeature(0x12) failed after retries on hPhysical={hPhysical}, targetRaw={targetRawC}, win32Err={err}");
                        }
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

                Program.LogDebug($"[DDC] TryWriteDdcMonitor hMon={hMonitor} count={count} writeB={writeBrightness}({brightnessPercent}%) writeC={writeContrast}({contrastPercent}%) -> anySucceeded={anySucceeded}");
                return anySucceeded;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[DDC] TryWriteDdcMonitor exception on hMon={hMonitor}: {ex}");
                return false;
            }
            finally
            {
                DestroyPhysicalMonitors(count, physicalMonitors);
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
        private struct PHYSICAL_MONITOR
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
    }
}
