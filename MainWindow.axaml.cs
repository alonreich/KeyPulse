using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Win32;

namespace KeyPulse
{
    public partial class MainWindow : Window
    {
        /// <summary>Master list. Never filtered or reordered - the UI binds to VisibleHotkeys.</summary>
        public ObservableCollection<HotkeyEntry> Hotkeys { get; } = new();

        /// <summary>ISSUE_17: the searched/sorted projection the list actually shows.</summary>
        public ObservableCollection<HotkeyEntry> VisibleHotkeys { get; } = new();

        private const string AppName = "KeyPulse";
        private readonly string ConfigPath = ConfigStore.ConfigPath;
        private readonly bool _startHidden;

        private string _searchText = string.Empty;
        private string _sortColumn = string.Empty;
        private bool _sortDescending;

        private Avalonia.Threading.DispatcherTimer? _retryTimer;
        private Window? _typingProgressWindow;
        private ProgressBar? _typingProgressBar;
        private TextBlock? _typingProgressText;
        private string? _configLoadError;
        private string? _configQuarantinePath;
        private bool _startupNoticeShown;
        private HotkeyEntry? _editingEntry;
        private bool _loadingEditorFields;
        private bool _creatingDuplicate;
        private string? _duplicateSourceCombo;
        private bool _showingRiskyShortcutWarning;
        private string? _approvedNewRiskyCombo;
        private bool _shortcutColumnResizeActive;
        private Grid? _shortcutColumnResizeGrid;
        private string? _shortcutColumnResizeLeftKey;
        private string? _shortcutColumnResizeRightKey;
        private double _shortcutColumnResizeStartX;
        private double _shortcutColumnResizeLeftStart;
        private double _shortcutColumnResizeRightStart;
        private double _shortcutColumnResizeLeftMin;
        private double _shortcutColumnResizeLeftMax;
        private double _shortcutColumnResizeRightMin;
        private double _shortcutColumnResizeRightMax;
        private Window? _actionErrorWindow;
        private TextBlock? _actionErrorHeadingText;
        private TextBlock? _actionErrorMessageText;
        private TextBlock? _actionErrorRepeatText;
        private int _actionErrorRepeatCount;

        public MainWindow() : this(false)
        {
        }

        public MainWindow(bool startHidden)
        {
            _startHidden = startHidden;
            if (_startHidden) ShowInTaskbar = false;
            this.Opened += MainWindow_Opened;
            
            InitializeComponent();
            DataContext = this;
            WireShortcutColumnSplitters();
            UpdateActionUi();

            var keyCombo = this.FindControl<TextBox>("KeyCombo");
            if (keyCombo != null)
            {
                keyCombo.GotFocus += (s, e) => HotkeyManager.EnableCaptureHook();
                keyCombo.LostFocus += (s, e) => HotkeyManager.DisableCaptureHook();
            }

            // ISSUE_1: the recording hook must never outlive KeyPulse being the active window.
            // The hook itself also refuses to swallow keys unless we are foreground, so this is a
            // second line of defence that also stops the hook from running at all in the background.
            this.Deactivated += (s, e) => HotkeyManager.DisableCaptureHook();
            this.Activated += (s, e) =>
            {
                if (keyCombo != null && keyCombo.IsFocused) HotkeyManager.EnableCaptureHook();
            };

            if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo)
            {
                actionCombo.SelectionChanged += ActionCombo_SelectionChanged;
            }

            if (this.FindControl<TextBox>("TargetText") is TextBox targetText)
            {
                targetText.TextChanged += TargetText_TextChanged;
            }

            HotkeyManager.OnRawKey += (vk, ctrl, alt, shift, win) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (keyCombo == null || !keyCombo.IsFocused) return;

                    var parts = new System.Collections.Generic.List<string>();
                    if (ctrl) parts.Add("Ctrl");
                    if (alt) parts.Add("Alt");
                    if (shift) parts.Add("Shift");
                    if (win) parts.Add("Win");

                    bool isModifierOnly = (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C || vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5);

                    // ISSUE_13: this used to hold its own private list of key names, which disagreed
                    // with the one the parser uses. HotkeyManager owns the only table now.
                    if (!isModifierOnly) parts.Add(HotkeyManager.VirtualKeyToName((uint)vk));

                    var txt = string.Join("+", parts);
                    if (isModifierOnly && parts.Count > 0) txt += "+";

                    keyCombo.Text = txt;
                });
            };

            // ISSUE_10: surface typing runs so a long "Type text" action is visible and cancellable.
            InputSimulator.TypingStarted += OnTypingStarted;
            InputSimulator.TypingProgressChanged += OnTypingProgress;
            InputSimulator.TypingFinished += OnTypingFinished;

            LoadConfig();
            ApplyShortcutColumnResources();
            RefreshVisibleHotkeys();
            HotkeyManager.Start();
            var failures = ApplyHotkeys();
            if (failures > 0)
            {
                Program.LogDebug($"{failures} shortcut(s) were inactive after startup registration.");
            }
            RefreshStatusSummary();
            StartRegistrationRetryTimer();
        }

        private AppConfig _currentConfig = new AppConfig();

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            ReportStartupProblems();

            if (_startHidden)
            {
                App.HiddenWindow = this;
                ShowInTaskbar = false;
                Hide();
                return;
            }

            this.Topmost = true;
            this.Activate();
            this.Topmost = false;
        }

        /// <summary>
        /// ISSUE_4 / ISSUE_7: a damaged settings file and shortcuts that failed to register are the
        /// two failures the user must not discover by accident days later.
        /// </summary>
        private void ReportStartupProblems()
        {
            if (_startupNoticeShown) return;
            _startupNoticeShown = true;

            if (!string.IsNullOrEmpty(_configLoadError))
            {
                var message = _configLoadError ?? string.Empty;
                if (!string.IsNullOrEmpty(_configQuarantinePath))
                {
                    message += "\n\nThe damaged file was kept as:\n" + _configQuarantinePath;
                }
                if (_currentConfig.IsReadOnlySession)
                {
                    message += "\n\nKeyPulse will not save over it until this is resolved, so nothing is lost.";
                }

                ShowActionMessage("KeyPulse settings problem", "Your shortcuts could not be loaded", message, true);
                return;
            }

            // ISSUE_3: shortcuts saved under a different Windows account cannot be decrypted here.
            var unreadable = Hotkeys.Count(h => h.TargetUnreadable);
            if (unreadable > 0)
            {
                ShowActionMessage("KeyPulse settings problem",
                    $"{unreadable} shortcut{(unreadable == 1 ? "" : "s")} could not be read",
                    "Their targets were saved by a different Windows account, or the settings file was "
                    + "edited outside KeyPulse. Nothing was deleted: select each affected row and enter "
                    + "its folder, program, link or text again.", true);
            }

            var broken = Hotkeys.Where(h => h.IsEnabled && !IsStatus(h, "Active") && !IsStatus(h, "Waiting")).ToList();
            if (broken.Count == 0) return;

            var lines = broken.Take(8).Select(h => "  " + h.KeyCombination + " - " + h.RegistrationStatus +
                                                  (string.IsNullOrWhiteSpace(h.StatusHint) ? string.Empty : " (" + h.StatusHint + ")"));
            var detail = string.Join("\n", lines);
            if (broken.Count > 8) detail += $"\n  ...and {broken.Count - 8} more.";

            ShowActionMessage("KeyPulse shortcuts not working",
                $"{broken.Count} shortcut{(broken.Count == 1 ? " is" : "s are")} not working right now",
                detail + "\n\nKeyPulse keeps retrying in the background and will switch them on automatically if the other app releases the keys or the drive comes back.",
                false);
        }

        private void LoadConfig()
        {
            var loaded = ConfigStore.Load(out _configLoadError, out _configQuarantinePath);

            _currentConfig = loaded;
            foreach (var item in loaded.Hotkeys) Hotkeys.Add(item);

            _searchText = string.Empty;
            _sortColumn = loaded.ShortcutSortColumn ?? string.Empty;
            _sortDescending = loaded.ShortcutSortDescending;

            Program.SoundEnabled = loaded.SoundEnabled;
            InputSimulator.CharacterDelayMs = NormalizeTypingDelay(loaded.TypingDelayMs); // ISSUE_9
            App.ApplyTheme(loaded.Theme);

            if (!double.IsNaN(loaded.MainWindowX) && !double.IsNaN(loaded.MainWindowY))
            {
                var pos = new Avalonia.PixelPoint((int)loaded.MainWindowX, (int)loaded.MainWindowY);
                bool isVisible = false;
                foreach (var scr in Screens.All)
                {
                    if (scr.Bounds.Contains(pos)) { isVisible = true; break; }
                }
                if (isVisible) Position = pos;
                else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                var screen = this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (screen != null)
                {
                    double ratioW = screen.Bounds.Width / 1920.0;
                    double ratioH = screen.Bounds.Height / 1080.0;
                    loaded.MainWindowWidth = 1640 * ratioW;
                    loaded.MainWindowHeight = 975 * ratioH;
                }
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            if (loaded.MainWindowWidth > 0 && loaded.MainWindowHeight > 0)
            {
                Width = loaded.MainWindowWidth;
                Height = loaded.MainWindowHeight;
            }
            if (Enum.TryParse<Avalonia.Controls.WindowState>(loaded.MainWindowState, out var state))
            {
                WindowState = state;
            }

            UpdateSortHeaderText();
        }

        /// <summary>ISSUE_9: the three speeds the Settings list offers, in milliseconds.</summary>
        private const int TypingDelayFast = 1;
        private const int TypingDelayNormal = 6;
        private const int TypingDelayCompatible = 18;

        private static int NormalizeTypingDelay(int value)
        {
            if (value <= 2) return TypingDelayFast;
            if (value <= 10) return TypingDelayNormal;
            return TypingDelayCompatible;
        }

        private static double ClampColumnWidth(double value, double fallback, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) value = fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        // ------------------------------------------------------------------
        // ISSUE_23: one set of column bounds, used by the markup, the clamp and the splitters.
        // They used to disagree: the code clamped the Shortcut column to 160-360 while the grid
        // capped it at 250, so the saved width and the rendered width were never the same number.
        // ------------------------------------------------------------------
        private const double StatusColumnMin = 110, StatusColumnMax = 260, StatusColumnDefault = 128;
        private const double KeyColumnMin = 130, KeyColumnMax = 300, KeyColumnDefault = 150;
        private const double ActionColumnMin = 120, ActionColumnMax = 280, ActionColumnDefault = 200;
        private const double TargetColumnMin = 180, TargetColumnMax = 1600, TargetColumnDefault = 468;

        /// <summary>
        /// Fixed width of the Actions column: two 78px buttons, 6px apart, inside a cell with 8px
        /// of padding each side. It is NOT user-resizable and NOT "Auto": the header and the rows
        /// are two separate grids, so "Auto" sized the heading to the word "Actions" and the row to
        /// its buttons, which is why the heading did not line up with anything.
        /// </summary>
        private const double ActionsColumnWidth = 178;

        private void ApplyShortcutColumnResources()
        {
            Resources["ShortcutStatusColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutStatusColumnWidth, StatusColumnDefault, StatusColumnMin, StatusColumnMax));
            Resources["ShortcutKeyColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutKeyColumnWidth, KeyColumnDefault, KeyColumnMin, KeyColumnMax));
            Resources["ShortcutActionColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutActionColumnWidth, ActionColumnDefault, ActionColumnMin, ActionColumnMax));
            Resources["ShortcutTargetColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutTargetColumnWidth, TargetColumnDefault, TargetColumnMin, TargetColumnMax));
            Resources["ShortcutActionsColumnWidth"] = new GridLength(ActionsColumnWidth);
        }

        /// <summary>
        /// ISSUE_23: each splitter now resizes the two columns it physically sits between.
        /// Previously "ShortcutActionColumnSplitter" - the divider between Action and Target -
        /// resized the STATUS column at the far left of the table, and the wiring call for
        /// "ShortcutStatusColumnSplitter" referred to a control that did not exist in the markup,
        /// so it silently did nothing at all.
        /// </summary>
        private void WireShortcutColumnSplitters()
        {
            WireShortcutColumnSplitter("ShortcutStatusColumnSplitter", "ShortcutStatusColumnWidth", "ShortcutKeyColumnWidth", StatusColumnMin, StatusColumnMax, KeyColumnMin, KeyColumnMax);
            WireShortcutColumnSplitter("ShortcutKeyColumnSplitter", "ShortcutKeyColumnWidth", "ShortcutActionColumnWidth", KeyColumnMin, KeyColumnMax, ActionColumnMin, ActionColumnMax);
            WireShortcutColumnSplitter("ShortcutActionColumnSplitter", "ShortcutActionColumnWidth", "ShortcutTargetColumnWidth", ActionColumnMin, ActionColumnMax, TargetColumnMin, TargetColumnMax);
            WireShortcutColumnEdgeSplitter("ShortcutTargetColumnSplitter", "ShortcutTargetColumnWidth", TargetColumnMin, TargetColumnMax);
        }

        private void WireShortcutColumnSplitter(string splitterName, string leftResourceKey, string rightResourceKey, double leftMin, double leftMax, double rightMin, double rightMax)
        {
            if (this.FindControl<Border>(splitterName) is not Border splitter) return;

            splitter.PointerPressed += (s, e) =>
            {
                if (this.FindControl<Grid>("ShortcutHeaderGrid") is not Grid headerGrid) return;

                CaptureShortcutColumnWidths();
                _shortcutColumnResizeActive = true;
                _shortcutColumnResizeGrid = headerGrid;
                _shortcutColumnResizeLeftKey = leftResourceKey;
                _shortcutColumnResizeRightKey = rightResourceKey;
                _shortcutColumnResizeStartX = e.GetPosition(headerGrid).X;
                _shortcutColumnResizeLeftStart = GetShortcutColumnWidth(leftResourceKey);
                _shortcutColumnResizeRightStart = GetShortcutColumnWidth(rightResourceKey);
                _shortcutColumnResizeLeftMin = leftMin;
                _shortcutColumnResizeLeftMax = leftMax;
                _shortcutColumnResizeRightMin = rightMin;
                _shortcutColumnResizeRightMax = rightMax;
                e.Pointer.Capture(splitter);
                e.Handled = true;
            };

            splitter.PointerMoved += (s, e) =>
            {
                if (!_shortcutColumnResizeActive || _shortcutColumnResizeGrid == null) return;
                ResizeShortcutColumns(e.GetPosition(_shortcutColumnResizeGrid).X);
                e.Handled = true;
            };

            splitter.PointerReleased += (s, e) =>
            {
                if (!_shortcutColumnResizeActive) return;
                if (_shortcutColumnResizeGrid != null) ResizeShortcutColumns(e.GetPosition(_shortcutColumnResizeGrid).X);
                EndShortcutColumnResize();
                e.Pointer.Capture(null);
                e.Handled = true;
            };

            splitter.PointerCaptureLost += (s, e) =>
            {
                if (_shortcutColumnResizeActive) EndShortcutColumnResize();
            };
        }

        private void WireShortcutColumnEdgeSplitter(string splitterName, string leftResourceKey, double leftMin, double leftMax)
        {
            if (this.FindControl<Border>(splitterName) is not Border splitter) return;

            splitter.PointerPressed += (s, e) =>
            {
                if (this.FindControl<Grid>("ShortcutHeaderGrid") is not Grid headerGrid) return;

                CaptureShortcutColumnWidths();
                _shortcutColumnResizeActive = true;
                _shortcutColumnResizeGrid = headerGrid;
                _shortcutColumnResizeLeftKey = leftResourceKey;
                _shortcutColumnResizeRightKey = null;
                _shortcutColumnResizeStartX = e.GetPosition(headerGrid).X;
                _shortcutColumnResizeLeftStart = GetShortcutColumnWidth(leftResourceKey);
                _shortcutColumnResizeRightStart = 0;
                _shortcutColumnResizeLeftMin = leftMin;
                _shortcutColumnResizeLeftMax = leftMax;
                _shortcutColumnResizeRightMin = 0;
                _shortcutColumnResizeRightMax = 0;
                e.Pointer.Capture(splitter);
                e.Handled = true;
            };

            splitter.PointerMoved += (s, e) =>
            {
                if (!_shortcutColumnResizeActive || _shortcutColumnResizeGrid == null) return;
                ResizeShortcutColumns(e.GetPosition(_shortcutColumnResizeGrid).X);
                e.Handled = true;
            };

            splitter.PointerReleased += (s, e) =>
            {
                if (!_shortcutColumnResizeActive) return;
                if (_shortcutColumnResizeGrid != null) ResizeShortcutColumns(e.GetPosition(_shortcutColumnResizeGrid).X);
                EndShortcutColumnResize();
                e.Pointer.Capture(null);
                e.Handled = true;
            };

            splitter.PointerCaptureLost += (s, e) =>
            {
                if (_shortcutColumnResizeActive) EndShortcutColumnResize();
            };
        }

        private double GetShortcutColumnWidth(string resourceKey)
        {
            return resourceKey switch
            {
                "ShortcutKeyColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutKeyColumnWidth, 220, 160, 360),
                "ShortcutActionColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutActionColumnWidth, 128, 110, 260),
                "ShortcutStatusColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutStatusColumnWidth, 128, 110, 260),
                "ShortcutTargetColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutTargetColumnWidth, 468, 180, 1600),
                _ => 120
            };
        }

        private void SetShortcutColumnWidth(string resourceKey, double width)
        {
            Resources[resourceKey] = new GridLength(width);
            switch (resourceKey)
            {
                case "ShortcutKeyColumnWidth":
                    _currentConfig.ShortcutKeyColumnWidth = width;
                    break;
                case "ShortcutActionColumnWidth":
                    _currentConfig.ShortcutActionColumnWidth = width;
                    break;
                case "ShortcutStatusColumnWidth":
                    _currentConfig.ShortcutStatusColumnWidth = width;
                    break;
                case "ShortcutTargetColumnWidth":
                    _currentConfig.ShortcutTargetColumnWidth = width;
                    break;
            }
        }

        private void ResizeShortcutColumns(double currentX)
        {
            if (_shortcutColumnResizeLeftKey == null) return;

            var rawDelta = currentX - _shortcutColumnResizeStartX;
            if (_shortcutColumnResizeRightKey == null)
            {
                var edgeDelta = Math.Max(_shortcutColumnResizeLeftMin - _shortcutColumnResizeLeftStart, Math.Min(_shortcutColumnResizeLeftMax - _shortcutColumnResizeLeftStart, rawDelta));
                SetShortcutColumnWidth(_shortcutColumnResizeLeftKey, _shortcutColumnResizeLeftStart + edgeDelta);
                return;
            }

            var minDelta = Math.Max(_shortcutColumnResizeLeftMin - _shortcutColumnResizeLeftStart, _shortcutColumnResizeRightStart - _shortcutColumnResizeRightMax);
            var maxDelta = Math.Min(_shortcutColumnResizeLeftMax - _shortcutColumnResizeLeftStart, _shortcutColumnResizeRightStart - _shortcutColumnResizeRightMin);
            var delta = Math.Max(minDelta, Math.Min(maxDelta, rawDelta));

            SetShortcutColumnWidth(_shortcutColumnResizeLeftKey, _shortcutColumnResizeLeftStart + delta);
            SetShortcutColumnWidth(_shortcutColumnResizeRightKey, _shortcutColumnResizeRightStart - delta);
        }

        private void EndShortcutColumnResize()
        {
            _shortcutColumnResizeActive = false;
            SaveConfig();
            _shortcutColumnResizeGrid = null;
            _shortcutColumnResizeLeftKey = null;
            _shortcutColumnResizeRightKey = null;
        }

        /// <summary>
        /// ISSUE_23: reads each column into ITS OWN setting. The indices used to be off by one
        /// column: the Target column's width was saved as ShortcutStatusColumnWidth and the Actions
        /// column's width as ShortcutTargetColumnWidth, so a Target column the user had widened came
        /// back at its minimum on every restart.
        /// Column order: 0 Status, 1 sep, 2 Shortcut, 3 sep, 4 Action, 5 sep, 6 Target, 7 sep, 8 Actions.
        /// </summary>
        private void CaptureShortcutColumnWidths()
        {
            if (this.FindControl<Grid>("ShortcutHeaderGrid") is not Grid headerGrid) return;
            var columns = headerGrid.ColumnDefinitions;
            if (columns.Count < 9) return;

            _currentConfig.ShortcutStatusColumnWidth = ClampColumnWidth(columns[0].ActualWidth, StatusColumnDefault, StatusColumnMin, StatusColumnMax);
            _currentConfig.ShortcutKeyColumnWidth = ClampColumnWidth(columns[2].ActualWidth, KeyColumnDefault, KeyColumnMin, KeyColumnMax);
            _currentConfig.ShortcutActionColumnWidth = ClampColumnWidth(columns[4].ActualWidth, ActionColumnDefault, ActionColumnMin, ActionColumnMax);
            _currentConfig.ShortcutTargetColumnWidth = ClampColumnWidth(columns[6].ActualWidth, TargetColumnDefault, TargetColumnMin, TargetColumnMax);
        }

        private void SaveConfig()
        {
            try
            {
                if (_shortcutColumnResizeGrid == null)
                {
                    CaptureShortcutColumnWidths();
                }
                _currentConfig.Hotkeys = new System.Collections.Generic.List<HotkeyEntry>(Hotkeys);
                if (WindowState != Avalonia.Controls.WindowState.Minimized)
                {
                    _currentConfig.MainWindowX = Position.X;
                    _currentConfig.MainWindowY = Position.Y;
                    _currentConfig.MainWindowWidth = Bounds.Width;
                    _currentConfig.MainWindowHeight = Bounds.Height;
                    _currentConfig.MainWindowState = WindowState.ToString();
                }

                _currentConfig.ShortcutSortColumn = _sortColumn;
                _currentConfig.ShortcutSortDescending = _sortDescending;

                // ISSUE_4: atomic write, and a hard refusal to save when the existing file could not
                // be read - the old code silently replaced a damaged config with an empty one.
                if (!ConfigStore.Save(_currentConfig, out var saveError) && !string.IsNullOrEmpty(saveError))
                {
                    Program.LogCrash("Could not save configuration: " + saveError);
                }
            }
            catch (Exception ex)
            {
                Program.LogCrash("SaveConfig failed: " + ex);
            }
        }

        private static AppConfig CloneConfig(AppConfig source)
        {
            return new AppConfig
            {
                Hotkeys = source.Hotkeys?.Select(CloneHotkeyEntry).ToList() ?? new List<HotkeyEntry>(),
                MainWindowX = source.MainWindowX,
                MainWindowY = source.MainWindowY,
                MainWindowWidth = source.MainWindowWidth,
                MainWindowHeight = source.MainWindowHeight,
                MainWindowState = source.MainWindowState,
                ShortcutKeyColumnWidth = source.ShortcutKeyColumnWidth,
                ShortcutActionColumnWidth = source.ShortcutActionColumnWidth,
                ShortcutStatusColumnWidth = source.ShortcutStatusColumnWidth,
                ShortcutTargetColumnWidth = source.ShortcutTargetColumnWidth,
                SetupWindowX = source.SetupWindowX,
                SetupWindowY = source.SetupWindowY,
                SetupWindowWidth = source.SetupWindowWidth,
                SetupWindowHeight = source.SetupWindowHeight,
                UseGoogleChromeForUrls = source.UseGoogleChromeForUrls,
                LaunchOnBoot = source.LaunchOnBoot,
                SoundEnabled = source.SoundEnabled,
                Theme = source.Theme,
                TypingDelayMs = source.TypingDelayMs,
                SettingsWindowX = source.SettingsWindowX,
                SettingsWindowY = source.SettingsWindowY,
                SettingsWindowWidth = source.SettingsWindowWidth,
                SettingsWindowHeight = source.SettingsWindowHeight,
                HasSeenTrayHint = source.HasSeenTrayHint,
                ShortcutSortColumn = source.ShortcutSortColumn,
                ShortcutSortDescending = source.ShortcutSortDescending,
                IsReadOnlySession = source.IsReadOnlySession
            };
        }

        private static HotkeyEntry CloneHotkeyEntry(HotkeyEntry source)
        {
            return new HotkeyEntry
            {
                Id = source.Id,
                IsEnabled = source.IsEnabled,
                AllowRiskyShortcut = source.AllowRiskyShortcut,
                KeyCombination = source.KeyCombination,
                Action = source.Action,
                Target = source.Target,
                // Was silently dropped, so a rolled-back restore un-hid every hidden target.
                IsTargetObfuscated = source.IsTargetObfuscated,
                TargetUnreadable = source.TargetUnreadable
            };
        }

        /// <summary>Hands back every live registration. Used before the list itself is replaced.</summary>
        private void ReleaseAllRegistrations()
        {
            foreach (var entry in Hotkeys) ReleaseRegistration(entry);
        }

        private void RestoreConfigSnapshot(AppConfig config)
        {
            ReleaseAllRegistrations();
            _currentConfig = CloneConfig(config);
            Hotkeys.Clear();
            foreach (var item in _currentConfig.Hotkeys)
            {
                Hotkeys.Add(item);
            }
            ApplyHotkeys();
            RefreshVisibleHotkeys();
        }

        private static bool IsStatus(HotkeyEntry entry, string prefix)
        {
            return entry.RegistrationStatus.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// ISSUE_5: reconciles the live Win32 registrations with the current list, one row at a time.
        ///
        /// This method used to open with HotkeyManager.Clear() - it released EVERY hotkey the user
        /// owned and took them all out again. That ran on every add, every edit, every enable/disable
        /// toggle, every remove, and automatically every 30 seconds whenever any single row was
        /// waiting or conflicted. During each of those windows none of the user's shortcuts worked,
        /// and a press landing in the gap was simply lost with no indication why. Rows whose
        /// combination has not changed are now left completely untouched.
        /// </summary>
        private int ApplyHotkeys()
        {
            var failures = 0;
            var claimedCombos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var h in Hotkeys)
            {
                string? blockingStatus = null;
                string blockingHint = string.Empty;

                if (!h.IsEnabled)
                {
                    blockingStatus = "Disabled";
                }
                else if (h.TargetUnreadable)
                {
                    // ISSUE_3: the stored target could not be decrypted on this Windows account.
                    // Say so plainly instead of registering a shortcut that opens cipher text.
                    blockingStatus = "Inactive: Could not be read";
                    blockingHint = "This shortcut was saved by a different Windows account, or the settings file was edited. Select this row and enter the target again.";
                }
                else if (!ValidateHotkeyFormat(h.KeyCombination, false, out var hotkeyError, h.AllowRiskyShortcut))
                {
                    blockingStatus = "Inactive: " + hotkeyError;
                }
                // ISSUE_8: only the *shape* of the target can stop a shortcut from being registered.
                // Whether the folder, drive or program is reachable is decided when it actually fires,
                // so a VPN share or USB stick that is offline at logon no longer kills the shortcut
                // permanently.
                else if (!ValidateTargetShape(h.Action, h.Target, out var targetError))
                {
                    blockingStatus = "Inactive: " + targetError;
                }
                else if (!claimedCombos.Add(NormalizeComboKey(h.KeyCombination)))
                {
                    blockingStatus = "Inactive: Duplicate";
                    blockingHint = "Another KeyPulse shortcut already uses this combination.";
                }

                if (blockingStatus != null)
                {
                    ReleaseRegistration(h);
                    h.RegistrationStatus = blockingStatus;
                    h.StatusHint = blockingHint;
                    h.SuggestedForCombo = string.Empty;
                    if (h.IsEnabled) failures++;
                    continue;
                }

                var normalizedCombo = NormalizeComboKey(h.KeyCombination);

                // Already registered for exactly this combination: leave the Win32 registration
                // alone and only refresh whether its target happens to be reachable right now.
                if (h.RegisteredHotkeyId != 0
                    && string.Equals(h.RegisteredCombo, normalizedCombo, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshLiveStatus(h);
                    continue;
                }

                ReleaseRegistration(h);

                if (HotkeyManager.Register(h.KeyCombination, () => ExecuteAction(h), out var hotkeyId))
                {
                    h.RegisteredHotkeyId = hotkeyId;
                    h.RegisteredCombo = normalizedCombo;
                    h.SuggestedForCombo = string.Empty;
                    RefreshLiveStatus(h);
                }
                else
                {
                    h.RegistrationStatus = "Inactive: Conflict";
                    ApplyConflictHint(h, normalizedCombo);
                    failures++;
                }
            }

            RefreshStatusSummary();
            return failures;
        }

        /// <summary>Hands one row's Win32 hotkey back, leaving every other row registered.</summary>
        private static void ReleaseRegistration(HotkeyEntry entry)
        {
            if (entry.RegisteredHotkeyId == 0) return;

            HotkeyManager.Unregister(entry.RegisteredHotkeyId);
            entry.RegisteredHotkeyId = 0;
            entry.RegisteredCombo = string.Empty;
        }

        /// <summary>Active vs. Waiting for a registered row, without touching the registration.</summary>
        private static void RefreshLiveStatus(HotkeyEntry entry)
        {
            if (IsTargetAvailableNow(entry.Action, entry.Target))
            {
                entry.RegistrationStatus = "Active";
                entry.StatusHint = string.Empty;
            }
            else
            {
                entry.RegistrationStatus = "Waiting";
                entry.StatusHint = "The keys are reserved and working. The folder or program is not reachable right now, so KeyPulse will tell you if you press it before it comes back.";
            }
        }

        /// <summary>
        /// ISSUE_20 / ISSUE_5: never leave the user with a bare "Conflict", but do not recompute the
        /// suggestion on every background retry either. Probing for a free alternative costs up to
        /// sixteen blocking register/unregister round trips per row; doing that for every conflicted
        /// shortcut every thirty seconds, forever, was pure waste.
        /// </summary>
        private static void ApplyConflictHint(HotkeyEntry entry, string normalizedCombo)
        {
            if (string.Equals(entry.SuggestedForCombo, normalizedCombo, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.StatusHint))
            {
                return;
            }

            var suggestion = HotkeyManager.SuggestAlternative(entry.KeyCombination);
            entry.StatusHint = suggestion != null
                ? $"Another app on this PC already owns {entry.KeyCombination}. {suggestion} is free - try that instead."
                : $"Another app on this PC already owns {entry.KeyCombination}. Add another modifier key (Shift or Win) and try again.";
            entry.SuggestedForCombo = normalizedCombo;
        }

        /// <summary>ISSUE_7: retry conflicted shortcuts so they start working on their own.</summary>
        private void StartRegistrationRetryTimer()
        {
            _retryTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _retryTimer.Tick += (s, e) => RetryBrokenShortcuts();
            _retryTimer.Start();
        }

        private void RetryBrokenShortcuts()
        {
            if (InputSimulator.IsTyping) return;
            if (HotkeyManager.IsCaptureMode) return;

            var needsRetry = Hotkeys.Any(h => h.IsEnabled &&
                (IsStatus(h, "Waiting") || IsStatus(h, "Inactive: Conflict")));
            if (!needsRetry) return;

            var activeBefore = Hotkeys.Count(h => IsStatus(h, "Active"));

            // Safe to call now: ISSUE_5 made this incremental, so the shortcuts that already work
            // are not released and re-taken every thirty seconds just to retry the broken ones.
            ApplyHotkeys();

            var activeAfter = Hotkeys.Count(h => IsStatus(h, "Active"));
            if (activeAfter > activeBefore)
            {
                Program.LogDebug($"Background retry activated {activeAfter - activeBefore} shortcut(s).");
            }
        }

        private void RefreshStatusSummary()
        {
            var active = Hotkeys.Count(h => IsStatus(h, "Active"));
            var waiting = Hotkeys.Count(h => h.IsEnabled && IsStatus(h, "Waiting"));
            var broken = Hotkeys.Count(h => h.IsEnabled && !IsStatus(h, "Active") && !IsStatus(h, "Waiting"));
            var disabled = Hotkeys.Count(h => !h.IsEnabled);

            App.UpdateTrayStatus(active, broken);
            UpdateEmptyState();

            if (this.FindControl<TextBlock>("ShortcutCountText") is TextBlock countText)
            {
                countText.Text = Hotkeys.Count == 0
                    ? string.Empty
                    : (VisibleHotkeys.Count == Hotkeys.Count
                        ? $"{Hotkeys.Count} total"
                        : $"showing {VisibleHotkeys.Count} of {Hotkeys.Count}");
            }

            if (this.FindControl<TextBlock>("ServiceStatusText") is not TextBlock statusText) return;

            if (broken > 0)
            {
                statusText.Text = $"KeyPulse running. {active} working, {broken} not working, {disabled} off. Click a red row to see why and how to fix it.";
                statusText.Foreground = AppBrush("AppDangerBrush");
            }
            else if (waiting > 0)
            {
                statusText.Text = $"KeyPulse running. {active} working, {waiting} waiting for a folder or program to come back, {disabled} off.";
                statusText.Foreground = AppBrush("AppWarningBrush");
            }
            else
            {
                // ISSUE_20: the hint only means something when it is actionable. Telling a user who
                // already ran KeyPulse as administrator to run it as administrator is just noise.
                statusText.Text = Program.IsElevated
                    ? $"KeyPulse running as administrator. {active} working, {disabled} off."
                    : $"KeyPulse running. {active} working, {disabled} off. Run as administrator to trigger shortcuts inside elevated apps.";
                statusText.Foreground = AppBrush("AppTextMutedBrush");
            }

            if (this.FindControl<TextBlock>("ServiceStatusText") is TextBlock hintTarget)
            {
                ToolTip.SetTip(hintTarget, Program.IsElevated
                    ? "KeyPulse is already elevated, so it can trigger shortcuts and type into Administrator windows."
                    : "If you wish to trigger hotkeys and type text into Administrator elevated windows, run KeyPulse as Administrator.");
            }
        }

        // ------------------------------------------------------------------
        // ISSUE_17: search, sort and the visible projection of the list.
        // ------------------------------------------------------------------

        public void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            _searchText = (sender as TextBox)?.Text ?? string.Empty;
            RefreshVisibleHotkeys();
        }

        public void ShortcutHeader_Pressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (sender is not Border header || header.Tag is not string column) return;

            if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
            {
                if (_sortDescending)
                {
                    _sortColumn = string.Empty;   // third click returns to the user's own order
                    _sortDescending = false;
                }
                else
                {
                    _sortDescending = true;
                }
            }
            else
            {
                _sortColumn = column;
                _sortDescending = false;
            }

            UpdateSortHeaderText();
            RefreshVisibleHotkeys();
            SaveConfig();
            e.Handled = true;
        }

        private void UpdateSortHeaderText()
        {
            SetSortHeaderText("ShortcutKeyHeaderText", "Shortcut");
            SetSortHeaderText("ShortcutActionHeaderText", "Action");
            SetSortHeaderText("ShortcutStatusHeaderText", "Status");
            SetSortHeaderText("ShortcutTargetHeaderText", "Target");
        }

        private void SetSortHeaderText(string controlName, string column)
        {
            if (this.FindControl<TextBlock>(controlName) is not TextBlock text) return;

            text.Text = string.Equals(_sortColumn, column, StringComparison.Ordinal)
                ? column + (_sortDescending ? "  ▼" : "  ▲")
                : column;
        }

        private static bool MatchesSearch(HotkeyEntry entry, string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;

            // ISSUE_2: a hidden target must not be searchable. Typing the password into the search
            // box and watching which row survives was a complete bypass of the blur.
            return entry.KeyCombination.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.ActionDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!entry.IsTargetObfuscated && entry.Target.Contains(search, StringComparison.OrdinalIgnoreCase))
                || entry.RegistrationStatus.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshVisibleHotkeys()
        {
            var search = _searchText.Trim();
            IEnumerable<HotkeyEntry> query = Hotkeys.Where(h => MatchesSearch(h, search));

            query = _sortColumn switch
            {
                "Shortcut" => _sortDescending
                    ? query.OrderByDescending(h => h.KeyCombination, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(h => h.KeyCombination, StringComparer.OrdinalIgnoreCase),
                "Action" => _sortDescending
                    ? query.OrderByDescending(h => h.ActionDisplay, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(h => h.ActionDisplay, StringComparer.OrdinalIgnoreCase),
                "Status" => _sortDescending
                    ? query.OrderByDescending(h => h.RegistrationStatus, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(h => h.RegistrationStatus, StringComparer.OrdinalIgnoreCase),
                "Target" => _sortDescending
                    ? query.OrderByDescending(h => h.Target, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(h => h.Target, StringComparer.OrdinalIgnoreCase),
                _ => query
            };

            var ordered = query.ToList();

            var previousSelection = this.FindControl<ListBox>("HotkeyList")?.SelectedItem as HotkeyEntry;

            _loadingEditorFields = true;
            try
            {
                VisibleHotkeys.Clear();
                foreach (var entry in ordered) VisibleHotkeys.Add(entry);

                if (previousSelection != null && VisibleHotkeys.Contains(previousSelection)
                    && this.FindControl<ListBox>("HotkeyList") is ListBox list)
                {
                    list.SelectedItem = previousSelection;
                }
            }
            finally
            {
                _loadingEditorFields = false;
            }

            UpdateEmptyState();

            if (this.FindControl<TextBlock>("ShortcutCountText") is TextBlock countText)
            {
                countText.Text = Hotkeys.Count == 0
                    ? string.Empty
                    : (VisibleHotkeys.Count == Hotkeys.Count
                        ? $"{Hotkeys.Count} total"
                        : $"showing {VisibleHotkeys.Count} of {Hotkeys.Count}");
            }
        }

        /// <summary>ISSUE_22: never show an unexplained empty rectangle.</summary>
        private void UpdateEmptyState()
        {
            if (this.FindControl<Border>("EmptyStatePanel") is not Border panel) return;

            var title = this.FindControl<TextBlock>("EmptyStateTitle");
            var body = this.FindControl<TextBlock>("EmptyStateBody");
            var exampleButton = this.FindControl<Button>("EmptyStateExampleButton");

            if (Hotkeys.Count == 0)
            {
                panel.IsVisible = true;
                if (title != null) title.Text = "No shortcuts yet";
                if (body != null) body.Text = "Click the shortcut box above, press the keys you want (for example Ctrl + Alt + D), choose what should happen, then press Add.";
                if (exampleButton != null) exampleButton.IsVisible = true;
                return;
            }

            if (VisibleHotkeys.Count == 0)
            {
                panel.IsVisible = true;
                if (title != null) title.Text = "Nothing matches your search";
                if (body != null) body.Text = $"No shortcut matches “{_searchText.Trim()}”. Clear the search box to see all {Hotkeys.Count} shortcuts.";
                if (exampleButton != null) exampleButton.IsVisible = false;
                return;
            }

            panel.IsVisible = false;
        }

        /// <summary>ISSUE_22: give a brand-new user one working shortcut in a single click.</summary>
        public void AddExample_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");

            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!SafeDirectoryExists(downloads))
            {
                downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string[] candidates = { "Ctrl+Alt+D", "Ctrl+Alt+Shift+D", "Ctrl+Alt+W", "Ctrl+Alt+Shift+W", "Ctrl+Alt+F" };
            var combo = candidates.FirstOrDefault(c =>
                !Hotkeys.Any(h => string.Equals(NormalizeComboKey(h.KeyCombination), NormalizeComboKey(c), StringComparison.OrdinalIgnoreCase))
                && HotkeyManager.Probe(c));

            if (combo == null)
            {
                SetFieldError("KeyCombo", "Could not find a free example shortcut. Pick your own combination above.");
                Program.PlaySound("error");
                return;
            }

            var entry = new HotkeyEntry
            {
                KeyCombination = combo,
                Action = ActionType.OpenFolder,
                Target = downloads,
                IsEnabled = true
            };

            Hotkeys.Add(entry);
            ApplyHotkeys();
            RefreshVisibleHotkeys();
            SaveConfig();
            SelectHotkeyEntry(entry);
        }

        private static Avalonia.Media.IBrush AppBrush(string resourceKey)
        {
            try
            {
                if (Application.Current?.FindResource(resourceKey) is Avalonia.Media.IBrush brush) return brush;
                if (Application.Current?.FindResource("AppTextPrimaryBrush") is Avalonia.Media.IBrush fallback) return fallback;
            }
            catch
            {
            }

            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(0));
        }

        private Window CreateAppDialog(string title, double width, double height, double minWidth, double minHeight, WindowStartupLocation startupLocation, bool canResize = true, bool topmost = false)
        {
            return new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = minWidth,
                MinHeight = minHeight,
                WindowStartupLocation = startupLocation,
                Topmost = topmost,
                CanResize = canResize,
                Icon = this.Icon
            };
        }

        private bool CanUseAsDialogOwner()
        {
            return ShowInTaskbar && IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized;
        }

        private async System.Threading.Tasks.Task ShowDialogOrWindowAsync(Window window)
        {
            if (CanUseAsDialogOwner())
            {
                try
                {
                    await window.ShowDialog(this);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    Program.LogCrash($"Owned dialog failed; falling back to unowned window: {ex}");
                }
            }

            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new System.Threading.Tasks.TaskCompletionSource<object?>();
            void OnClosed(object? sender, EventArgs args) => closed.TrySetResult(null);

            window.Closed += OnClosed;
            try
            {
                window.Show();
                await closed.Task;
            }
            catch (Exception ex)
            {
                Program.LogCrash($"Unowned dialog failed: {ex}");
                closed.TrySetResult(null);
            }
            finally
            {
                window.Closed -= OnClosed;
            }
        }

        private void ShowDialogOrWindow(Window window)
        {
            if (CanUseAsDialogOwner())
            {
                try
                {
                    _ = window.ShowDialog(this);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    Program.LogCrash($"Owned dialog failed; falling back to unowned window: {ex}");
                }
            }

            try
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Show();
            }
            catch (Exception ex)
            {
                Program.LogCrash($"Unowned dialog failed: {ex}");
            }
        }

        private static StackPanel CreateDialogPanel(double spacing = 10)
        {
            return new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = spacing
            };
        }

        public void ActionCombo_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            UpdateActionUi();
            SetFieldError("TargetText", null);
            ValidateEditorTarget();
        }

        private void UpdateActionUi()
        {
            UpdateExpandButtonState(); // ISSUE_17
            var actionType = GetSelectedAction();
            var targetLabel = this.FindControl<TextBlock>("TargetLabel");
            var targetHint = this.FindControl<TextBlock>("TargetHintText");
            var targetText = this.FindControl<TextBox>("TargetText");

            if (targetText != null)
            {
                var isTextAction = actionType == ActionType.TypeText || actionType == ActionType.InsertText;
                targetText.AcceptsReturn = isTextAction;
                targetText.TextWrapping = isTextAction ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
                // ISSUE_17: keep these in step with MainWindow.axaml. Layout measures the height;
                // nothing computes a pixel value from a character count any more.
                targetText.MinHeight = isTextAction ? 92 : 34;
                targetText.MaxHeight = isTextAction ? 180 : 34;
            }

            if (targetLabel != null)
            {
                targetLabel.Text = actionType switch
                {
                    ActionType.OpenFolder => "Folder path",
                    ActionType.LaunchProgram => "Program or script",
                    ActionType.BrowseChrome => "Web URL",
                    ActionType.TypeText => "Text to type out",
                    ActionType.InsertText => "Text to paste",
                    _ => "Target"
                };
            }

            if (targetHint != null)
            {
                targetHint.Text = actionType switch
                {
                    ActionType.OpenFolder => "Any folder on this PC, a USB drive, or a network share. If it is offline when Windows starts, the shortcut still works once it comes back.",
                    ActionType.LaunchProgram => "Pick an app, or type a command with its arguments.",
                    ActionType.BrowseChrome => "Type a website address. The https:// part is added for you.",
                    ActionType.TypeText => "Types your text one key at a time, like a person would. Slower, but works in old console windows and anywhere that ignores paste. Press Esc to stop a long one.",
                    ActionType.InsertText => "Pastes your text instantly with no formatting, then puts your previous clipboard back. Use this unless the target app refuses paste.",
                    _ => string.Empty
                };
            }

            if (this.FindControl<Button>("BrowseBtn") is Button browseButton)
            {
                browseButton.IsVisible = actionType == ActionType.OpenFolder || actionType == ActionType.LaunchProgram;
                browseButton.IsEnabled = browseButton.IsVisible;
            }
        }

        private ActionType GetSelectedAction()
        {
            var selectedIndex = this.FindControl<ComboBox>("ActionCombo")?.SelectedIndex ?? 0;
            if (selectedIndex < 0 || selectedIndex > (int)ActionType.InsertText) return ActionType.OpenFolder;
            return (ActionType)selectedIndex;
        }

        private static string NormalizeComboKey(string combo)
        {
            return string.Join("+", combo.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().ToLowerInvariant()));
        }

        private static bool ValidateHotkeyFormat(string? combo, bool checkAvailability, out string error, bool allowRiskyShortcut = false)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(combo))
            {
                error = "Shortcut required";
                return false;
            }

            if (combo.EndsWith("+", StringComparison.Ordinal))
            {
                error = "Press a non-modifier key";
                return false;
            }

            if (!HotkeyManager.TryParseCombo(combo, out var modifiers, out var vk))
            {
                error = "Invalid shortcut";
                return false;
            }

            if (HotkeyManager.IsTypingKeyWithoutModifier(combo))
            {
                error = "Use Ctrl, Alt, Shift, or Win with normal typing keys";
                return false;
            }

            if (!allowRiskyShortcut && IsRiskyTypingShortcut(modifiers, vk, out var riskyError))
            {
                error = riskyError;
                return false;
            }

            if (checkAvailability && !HotkeyManager.Probe(combo))
            {
                error = "Shortcut already taken by Windows or another app";
                return false;
            }

            return true;
        }

        private static bool IsRiskyTypingShortcut(uint modifiers, uint vk, out string error)
        {
            error = string.Empty;
            if (!HotkeyManager.IsTypingVirtualKey(vk)) return false;
            if ((modifiers & HotkeyManager.ModWin) != 0) return false;

            if (modifiers == HotkeyManager.ModShift)
            {
                error = "Shift plus a typing key conflicts with normal typing";
                return true;
            }

            if (modifiers == HotkeyManager.ModCtrl)
            {
                error = "Ctrl plus a typing key conflicts with common app shortcuts";
                return true;
            }

            if (modifiers == HotkeyManager.ModAlt)
            {
                error = "Alt plus a typing key conflicts with app menus";
                return true;
            }

            return false;
        }

        private static bool IsRiskyTypingShortcut(string? combo, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(combo)) return false;
            if (!HotkeyManager.TryParseCombo(combo, out var modifiers, out var vk)) return false;
            return IsRiskyTypingShortcut(modifiers, vk, out error);
        }

        private bool IsNewRiskyComboApproved(string combo)
        {
            return !string.IsNullOrWhiteSpace(_approvedNewRiskyCombo)
                && string.Equals(_approvedNewRiskyCombo, NormalizeComboKey(combo), StringComparison.OrdinalIgnoreCase);
        }

        private async System.Threading.Tasks.Task<bool> ConfirmRiskyShortcutOverrideAsync(string combo, string reason)
        {
            if (_showingRiskyShortcutWarning) return false;

            _showingRiskyShortcutWarning = true;
            try
            {
                var w = CreateAppDialog("Risky Shortcut", 520, 270, 460, 230, WindowStartupLocation.CenterOwner, false);
                var grid = new Grid
                {
                    Margin = new Avalonia.Thickness(20),
                    RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,Auto,*,Auto")
                };

                var title = new TextBlock
                {
                    Text = $"Use {combo} anyway?",
                    Classes = { "SectionTitle" },
                    Foreground = AppBrush("AppWarningBrush")
                };
                Grid.SetRow(title, 0);
                grid.Children.Add(title);

                var reasonText = new TextBlock
                {
                    Text = reason,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 10, 0, 0)
                };
                Grid.SetRow(reasonText, 1);
                grid.Children.Add(reasonText);

                var details = new TextBlock
                {
                    Text = "This kind of shortcut is commonly used by Windows apps for text input, menus, or commands. A global shortcut can steal that key from the app you are using, so normal app behavior may stop working while KeyPulse is running.",
                    Classes = { "Muted" },
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 8, 0, 12)
                };
                Grid.SetRow(details, 2);
                grid.Children.Add(details);

                var buttons = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8
                };

                var cancel = new Button { Content = "Choose Different", Classes = { "Secondary" }, MinWidth = 130, TabIndex = 0 };
                var proceed = new Button { Content = "Use Anyway", Classes = { "Primary" }, MinWidth = 110, TabIndex = 1 };
                buttons.Children.Add(cancel);
                buttons.Children.Add(proceed);
                Grid.SetRow(buttons, 3);
                grid.Children.Add(buttons);

                var result = false;
                cancel.Click += (s, e) => w.Close();
                proceed.Click += (s, e) =>
                {
                    result = true;
                    w.Close();
                };

                w.Content = grid;
                w.Opened += (s, e) => cancel.Focus();
                await ShowDialogOrWindowAsync(w);
                return result;
            }
            finally
            {
                _showingRiskyShortcutWarning = false;
            }
        }

        private void SetFieldError(string fieldName, string? message)
        {
            var textName = fieldName == "KeyCombo" ? "ShortcutErrorText" : "TargetErrorText";
            if (this.FindControl<TextBlock>(textName) is TextBlock error)
            {
                error.Text = message ?? string.Empty;
                error.IsVisible = !string.IsNullOrWhiteSpace(message);
            }

            if (this.FindControl<TextBox>(fieldName) is TextBox textBox)
            {
                textBox.Classes.Set("invalid", !string.IsNullOrWhiteSpace(message));
            }
        }

        /// <summary>ISSUE_8: "not reachable right now" is amber advice, not a red blocker.</summary>
        private void SetFieldWarning(string fieldName, string? message)
        {
            var textName = fieldName == "KeyCombo" ? "ShortcutWarningText" : "TargetWarningText";
            if (this.FindControl<TextBlock>(textName) is TextBlock warning)
            {
                warning.Text = message ?? string.Empty;
                warning.IsVisible = !string.IsNullOrWhiteSpace(message);
            }
        }

        private void ClearFieldErrors()
        {
            SetFieldError("KeyCombo", null);
            SetFieldError("TargetText", null);
            SetFieldWarning("KeyCombo", null);
            SetFieldWarning("TargetText", null);
        }

        private static bool SafeDirectoryExists(string path)
        {
            try { return Directory.Exists(path); } catch { return false; }
        }

        /// <summary>
        /// ISSUE_8: is this target well formed? Nothing here touches the disk or the network, so a
        /// shortcut is never taken away from the user because a drive happens to be offline.
        /// </summary>
        private static bool ValidateTargetShape(ActionType actionType, string? rawTarget, out string error)
        {
            error = string.Empty;
            var value = rawTarget ?? string.Empty;

            if (actionType == ActionType.TypeText || actionType == ActionType.InsertText)
            {
                if (value.Length == 0)
                {
                    error = "Text required";
                    return false;
                }
                return true;
            }

            var trimmed = value.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                error = "Target required";
                return false;
            }

            if (actionType == ActionType.OpenFolder)
            {
                var folder = trimmed.Trim('"');
                if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    error = "That folder path is not valid";
                    return false;
                }
                return true;
            }

            if (actionType == ActionType.LaunchProgram)
            {
                return TrySplitLaunchCommand(trimmed, out _, out _, out error);
            }

            if (actionType == ActionType.BrowseChrome)
            {
                return TryNormalizeUrlTarget(trimmed, out _, out error);
            }

            error = "Unknown action";
            return false;
        }

        /// <summary>Is the target reachable at this instant? Used for status only, never to unregister.</summary>
        private static bool IsTargetAvailableNow(ActionType actionType, string? rawTarget)
        {
            var value = (rawTarget ?? string.Empty).Trim();
            if (value.Length == 0) return false;

            switch (actionType)
            {
                case ActionType.OpenFolder:
                    return SafeDirectoryExists(value.Trim('"'));
                case ActionType.LaunchProgram:
                    return TryResolveLaunchTarget(value, out _, out _, out _);
                default:
                    return true;
            }
        }

        private static bool ValidateTarget(ActionType actionType, string? rawTarget, out string normalizedTarget, out string error, out string warning)
        {
            normalizedTarget = rawTarget ?? string.Empty;
            error = string.Empty;
            warning = string.Empty;

            if (actionType == ActionType.TypeText || actionType == ActionType.InsertText)
            {
                if (normalizedTarget.Length == 0)
                {
                    error = "Text required";
                    return false;
                }

                return true;
            }

            var trimmedTarget = normalizedTarget.Trim();
            if (string.IsNullOrWhiteSpace(trimmedTarget))
            {
                error = "Target required";
                return false;
            }

            if (actionType == ActionType.OpenFolder)
            {
                var folder = trimmedTarget.Trim('"');
                if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    error = "That folder path is not valid";
                    return false;
                }

                normalizedTarget = folder;
                if (!SafeDirectoryExists(folder))
                {
                    warning = "That folder is not reachable right now. The shortcut is still saved and starts working as soon as the drive or network location is back.";
                }
                return true;
            }

            if (actionType == ActionType.LaunchProgram)
            {
                if (!TrySplitLaunchCommand(trimmedTarget, out var fileName, out var arguments, out error))
                {
                    return false;
                }

                if (TryResolveLaunchTarget(trimmedTarget, out var resolvedFile, out var resolvedArgs, out _))
                {
                    normalizedTarget = FormatLaunchTarget(resolvedFile, resolvedArgs);
                }
                else
                {
                    normalizedTarget = FormatLaunchTarget(fileName, arguments);
                    warning = "That program is not on this PC right now. The shortcut is still saved and starts working as soon as it is available.";
                }

                return true;
            }

            if (actionType == ActionType.BrowseChrome)
            {
                return TryNormalizeUrlTarget(trimmedTarget, out normalizedTarget, out error);
            }

            error = "Unknown action";
            return false;
        }

        private static bool TryNormalizeUrlTarget(string rawTarget, out string normalizedUrl, out string error)
        {
            normalizedUrl = rawTarget.Trim();
            error = string.Empty;

            if (!normalizedUrl.Contains("://", StringComparison.Ordinal))
            {
                normalizedUrl = "https://" + normalizedUrl;
            }

            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Enter a valid web URL";
                return false;
            }

            normalizedUrl = uri.AbsoluteUri;
            return true;
        }

        private static bool TryResolveLaunchTarget(string rawTarget, out string fileName, out string arguments, out string error)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            error = string.Empty;

            try
            {
                if (!TrySplitLaunchCommand(rawTarget, out fileName, out arguments, out error)) return false;

                if (File.Exists(fileName)) return true;

                if (TryFindExecutableOnPath(fileName, out var resolvedPath))
                {
                    fileName = resolvedPath;
                    return true;
                }

                error = "Program not found";
                return false;
            }
            catch (Exception ex)
            {
                error = "Invalid program target: " + ex.Message;
                return false;
            }
        }

        private static bool TrySplitLaunchCommand(string rawTarget, out string fileName, out string arguments, out string error)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            error = string.Empty;

            var target = rawTarget.Trim();
            if (target.Length == 0)
            {
                error = "Program required";
                return false;
            }

            if (target.StartsWith("\"", StringComparison.Ordinal))
            {
                var endQuote = target.IndexOf('"', 1);
                if (endQuote <= 1)
                {
                    error = "Closing quote missing";
                    return false;
                }

                fileName = target.Substring(1, endQuote - 1);
                arguments = target.Substring(endQuote + 1).Trim();
                return true;
            }

            if (File.Exists(target))
            {
                fileName = target;
                return true;
            }

            var lastSpace = target.LastIndexOf(' ');
            while (lastSpace > 0)
            {
                var possiblePath = target.Substring(0, lastSpace);
                if (File.Exists(possiblePath))
                {
                    fileName = possiblePath;
                    arguments = target.Substring(lastSpace + 1).Trim();
                    return true;
                }

                lastSpace = target.LastIndexOf(' ', lastSpace - 1);
            }

            var firstSpace = target.IndexOf(' ');
            if (firstSpace > 0)
            {
                fileName = target.Substring(0, firstSpace);
                arguments = target.Substring(firstSpace + 1).Trim();
            }
            else
            {
                fileName = target;
            }

            return true;
        }

        private static bool TryFindExecutableOnPath(string fileName, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            if (fileName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            {
                return false;
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathExtValue = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD;.VBS;.PS1;.LNK";
            var extensions = Path.HasExtension(fileName)
                ? new[] { string.Empty }
                : pathExtValue.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory.Trim(), fileName + extension);
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string FormatLaunchTarget(string fileName, string arguments)
        {
            var formattedFileName = fileName.Contains(' ') ? $"\"{fileName}\"" : fileName;
            return string.IsNullOrWhiteSpace(arguments) ? formattedFileName : formattedFileName + " " + arguments;
        }

        private void LaunchUrl(string target)
        {
            if (!TryNormalizeUrlTarget(target, out var normalizedUrl, out var error))
            {
                throw new InvalidOperationException(error);
            }

            if (_currentConfig.UseGoogleChromeForUrls && TryFindGoogleChrome(out var chromePath))
            {
                Process.Start(new ProcessStartInfo(chromePath, $"\"{normalizedUrl}\"") { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo(normalizedUrl) { UseShellExecute = true });
        }

        private static bool TryFindGoogleChrome(out string chromePath)
        {
            var candidates = new List<string>();
            AddChromeCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", candidates);
            AddChromeCandidateFromRegistry(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", candidates);
            AddChromeCandidateFromRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", candidates);

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));

            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(candidate))
                {
                    chromePath = candidate;
                    return true;
                }
            }

            chromePath = string.Empty;
            return false;
        }

        private static void AddChromeCandidateFromRegistry(RegistryKey root, string subKey, List<string> candidates)
        {
            try
            {
                using var key = root.OpenSubKey(subKey, false);
                if (key?.GetValue(null) is string path)
                {
                    candidates.Add(path.Trim('"'));
                }
            }
            catch { }
        }

        public async void Browse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                var actionCombo = this.FindControl<ComboBox>("ActionCombo");
                if (actionCombo != null && actionCombo.SelectedIndex != -1)
                {
                    var actionType = (ActionType)actionCombo.SelectedIndex;
                    var targetText = this.FindControl<TextBox>("TargetText");
                    if (targetText == null) return;

                    if (actionType == ActionType.OpenFolder)
                    {
                        var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select Folder", AllowMultiple = false };
                        var folders = await this.StorageProvider.OpenFolderPickerAsync(options);
                        if (folders != null && folders.Count > 0)
                        {
                            targetText.Text = folders[0].Path.LocalPath;
                        }
                    }
                    else if (actionType == ActionType.LaunchProgram)
                    {
                        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { 
                            Title = "Select Executable", 
                            AllowMultiple = false, 
                            FileTypeFilter = new[] { 
                                new Avalonia.Platform.Storage.FilePickerFileType("Executables") { Patterns = new[] { "*.exe", "*.bat", "*.cmd", "*.ps1", "*.vbs", "*.lnk" } }, 
                                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } } 
                            } 
                        };
                        var files = await this.StorageProvider.OpenFilePickerAsync(options);
                        if (files != null && files.Count > 0)
                        {
                            targetText.Text = "\"" + files[0].Path.LocalPath + "\"";
                        }
                    }
                }
            }
            catch { }
        }

        private void ExecuteAction(HotkeyEntry entry)
        {
            try
            {
                Program.LogDebug($"Executing shortcut {entry.KeyCombination} ({entry.Action}).");
                switch (entry.Action)
                {
                    case ActionType.OpenFolder:
                        {
                            // ISSUE_8: availability is checked here, when it matters, with a message
                            // the user can act on - not at registration time where it used to kill
                            // the shortcut for the rest of the session.
                            var folder = (entry.Target ?? string.Empty).Trim().Trim('"');
                            if (!SafeDirectoryExists(folder))
                            {
                                MarkEntryWaiting(entry, "The folder is not available right now.");
                                throw new InvalidOperationException(
                                    $"The folder for {entry.KeyCombination} is not available right now:\n{folder}\n\nIf it is on a USB drive, a network share or a VPN, connect it and press the shortcut again.");
                            }
                            Process.Start("explorer.exe", $"\"{folder}\"");
                            MarkEntryActive(entry);
                            break;
                        }
                    case ActionType.LaunchProgram:
                        if (!TryResolveLaunchTarget(entry.Target, out var fileName, out var arguments, out var launchError))
                        {
                            MarkEntryWaiting(entry, "The program is not available right now.");
                            throw new InvalidOperationException(
                                $"The program for {entry.KeyCombination} could not be found right now ({launchError}):\n{entry.Target}");
                        }
                        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
                        MarkEntryActive(entry);
                        break;
                    case ActionType.BrowseChrome:
                        LaunchUrl(entry.Target);
                        break;
                    case ActionType.TypeText:
                        // ISSUE_10: a second press while a run is in flight is ignored rather than
                        // producing two interleaved streams of characters.
                        if (InputSimulator.IsTyping)
                        {
                            Program.LogDebug($"Ignored {entry.KeyCombination}: typing already in progress.");
                            break;
                        }
                        if (!InputSimulator.TypeText(entry.Target, out var typeError))
                        {
                            throw new InvalidOperationException(typeError);
                        }
                        break;
                    case ActionType.InsertText:
                        if (!InputSimulator.InsertText(entry.Target, out var insertError, out var insertWarning))
                        {
                            throw new InvalidOperationException(insertError);
                        }
                        // ISSUE_3: say so when the previous clipboard could not be handed back,
                        // instead of letting the user discover it at their next Ctrl+V.
                        if (!string.IsNullOrEmpty(insertWarning))
                        {
                            ShowActionMessage("KeyPulse clipboard notice", "Your previous clipboard could not be restored", insertWarning, false);
                        }
                        break;
                }
                Program.LogDebug($"Shortcut {entry.KeyCombination} completed.");
            }
            catch (Exception ex)
            {
                Program.LogCrash($"Shortcut action failed ({entry.KeyCombination}, {entry.Action}): {ex}");
                ShowActionMessage("KeyPulse Action Failed", "That shortcut could not run", ex.Message, true);
            }
        }

        private void MarkEntryWaiting(HotkeyEntry entry, string hint)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!entry.IsEnabled) return;
                entry.RegistrationStatus = "Waiting";
                entry.StatusHint = hint;
                RefreshStatusSummary();
            });
        }

        private void MarkEntryActive(HotkeyEntry entry)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!entry.IsEnabled) return;
                if (!IsStatus(entry, "Waiting")) return;
                entry.RegistrationStatus = "Active";
                entry.StatusHint = string.Empty;
                RefreshStatusSummary();
            });
        }

        // ------------------------------------------------------------------
        // ISSUE_10: visible, cancellable progress for long typing runs.
        // ------------------------------------------------------------------

        private const int TypingProgressThreshold = 120;

        /// <summary>
        /// ISSUE_9: show progress only when the run is actually long enough to need it. Typing is
        /// fast now, so gating on character count alone made the window flash for a fifth of a
        /// second on snippets that used to take ten.
        /// </summary>
        private static bool ShouldShowTypingProgress(int totalCharacters)
        {
            return totalCharacters >= TypingProgressThreshold
                && InputSimulator.EstimateTypingMs(totalCharacters) >= 800;
        }

        private void OnTypingStarted(int totalCharacters)
        {
            if (!ShouldShowTypingProgress(totalCharacters)) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    CloseTypingProgressWindow();

                    var window = CreateAppDialog("KeyPulse is typing", 360, 150, 320, 140, WindowStartupLocation.CenterScreen, false, true);
                    window.ShowActivated = false;   // never steal focus from the app being typed into
                    window.CanResize = false;

                    var panel = CreateDialogPanel(8);
                    _typingProgressText = new TextBlock
                    {
                        Text = $"Typing 0 of {totalCharacters} characters...",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    };
                    _typingProgressBar = new ProgressBar { Minimum = 0, Maximum = totalCharacters, Value = 0 };

                    var hint = new TextBlock
                    {
                        Text = "Press Esc at any time to stop.",
                        Classes = { "Muted" },
                        FontSize = 11
                    };

                    var cancelButton = new Button
                    {
                        Content = "Stop typing",
                        Classes = { "Danger" },
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth = 110
                    };
                    cancelButton.Click += (s, e) => InputSimulator.CancelTyping();

                    panel.Children.Add(_typingProgressText);
                    panel.Children.Add(_typingProgressBar);
                    panel.Children.Add(hint);
                    panel.Children.Add(cancelButton);

                    window.Content = panel;
                    _typingProgressWindow = window;
                    window.Show();
                }
                catch (Exception ex)
                {
                    Program.LogCrash("Failed to show typing progress: " + ex);
                }
            });
        }

        private void OnTypingProgress(int done, int total)
        {
            if (!ShouldShowTypingProgress(total)) return;

            // Do not flood the dispatcher with one message per keystroke on a long run.
            if (done != total && done % 10 != 0) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_typingProgressBar != null) _typingProgressBar.Value = done;
                if (_typingProgressText != null) _typingProgressText.Text = $"Typing {done} of {total} characters...";
            });
        }

        private void OnTypingFinished(bool cancelled)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CloseTypingProgressWindow();
                if (cancelled) Program.PlaySound("close");
            });
        }

        private void CloseTypingProgressWindow()
        {
            try { _typingProgressWindow?.Close(); } catch { }
            _typingProgressWindow = null;
            _typingProgressBar = null;
            _typingProgressText = null;
        }

        private void ShowActionMessage(string windowTitle, string heading, string message, bool isError)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_actionErrorWindow != null)
                    {
                        _actionErrorRepeatCount++;
                        _actionErrorWindow.Title = windowTitle;
                        if (_actionErrorHeadingText != null)
                        {
                            _actionErrorHeadingText.Text = heading;
                            _actionErrorHeadingText.Foreground = AppBrush(isError ? "AppDangerBrush" : "AppWarningBrush");
                        }
                        if (_actionErrorMessageText != null) _actionErrorMessageText.Text = message;
                        if (_actionErrorRepeatText != null) _actionErrorRepeatText.Text = $"Repeated {_actionErrorRepeatCount} times while this notice was open.";
                        // ISSUE_10: deliberately NOT Activate(). Repeating the notice must not rip
                        // the keyboard away from whatever the user is doing.
                        return;
                    }

                    _actionErrorRepeatCount = 1;

                    // ISSUE_10: this window used to open centre-screen, on top, WITH keyboard focus.
                    // A shortcut that failed while the user was mid-sentence in another app swallowed
                    // the next keystrokes into a dialog. It now opens quietly in the corner and never
                    // takes focus - exactly as the typing-progress window already does.
                    var errWin = CreateAppDialog(windowTitle, 460, 250, 400, 200, WindowStartupLocation.Manual, true, true);
                    errWin.ShowActivated = false;
                    PositionNoticeBottomRight(errWin, 460, 250);

                    var sp = CreateDialogPanel(8);
                    _actionErrorHeadingText = new Avalonia.Controls.TextBlock
                    {
                        Text = heading,
                        Foreground = AppBrush(isError ? "AppDangerBrush" : "AppWarningBrush"),
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    };
                    _actionErrorMessageText = new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                    _actionErrorRepeatText = new Avalonia.Controls.TextBlock { Text = string.Empty, Classes = { "Muted" }, FontSize = 12 };
                    sp.Children.Add(_actionErrorHeadingText);
                    sp.Children.Add(_actionErrorMessageText);
                    sp.Children.Add(_actionErrorRepeatText);
                    var closeButton = new Avalonia.Controls.Button
                    {
                        Content = "Close",
                        Classes = { "Secondary" },
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth = 90,
                        Margin = new Avalonia.Thickness(0, 8, 0, 0)
                    };
                    closeButton.Click += (s, e) => errWin.Close();
                    sp.Children.Add(closeButton);

                    errWin.Content = new ScrollViewer
                    {
                        Content = sp,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    };
                    errWin.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_actionErrorWindow, errWin))
                        {
                            _actionErrorWindow = null;
                            _actionErrorHeadingText = null;
                            _actionErrorMessageText = null;
                            _actionErrorRepeatText = null;
                            _actionErrorRepeatCount = 0;
                        }
                    };

                    _actionErrorWindow = errWin;
                    errWin.Show();
                }
                catch (Exception ex)
                {
                    Program.LogCrash($"Failed to show shortcut action message: {ex}");
                }
            });
        }

        /// <summary>Drops a non-activating notice into the corner of the working area.</summary>
        private void PositionNoticeBottomRight(Window window, double width, double height)
        {
            try
            {
                var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (screen == null) return;

                var area = screen.WorkingArea;
                var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                var pixelWidth = (int)(width * scale);
                var pixelHeight = (int)(height * scale);
                var margin = (int)(24 * scale);

                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new Avalonia.PixelPoint(
                    Math.Max(area.X, area.X + area.Width - pixelWidth - margin),
                    Math.Max(area.Y, area.Y + area.Height - pixelHeight - margin));
            }
            catch (Exception ex)
            {
                Program.LogDebug("Could not position the notice window: " + ex.Message);
            }
        }

        public void KeyCombo_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var tb = sender as TextBox;
            if (tb == null) return;
            if (_loadingEditorFields) return;

            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                SetFieldError("KeyCombo", null);
                return;
            }

            if (tb.Text.EndsWith("+"))
            {
                SetFieldError("KeyCombo", null);
                return;
            }

            var allowRiskyShortcut = _editingEntry?.AllowRiskyShortcut == true || IsNewRiskyComboApproved(tb.Text);
            if (!ValidateHotkeyFormat(tb.Text, _editingEntry == null, out var validationError, allowRiskyShortcut))
            {
                SetFieldError("KeyCombo", validationError);
            }
            else
            {
                SetFieldError("KeyCombo", null);
            }
        }

        /// <summary>
        /// ISSUE_17: the box used to set its own pixel Height from a guess that a line holds about
        /// 40 characters - regardless of how wide the window actually was - and hard-capped at 15
        /// lines. Layout does the measuring now: MinHeight, MaxHeight and an automatic scrollbar are
        /// declared in MainWindow.axaml, so the box grows to fit real wrapped text and scrolls after
        /// that, and the "Expand" button opens a proper resizable editor for long snippets.
        /// </summary>
        private void TargetText_TextChanged(object? sender, TextChangedEventArgs e)
        {
            ValidateEditorTarget();
            UpdateExpandButtonState();
        }

        private void UpdateExpandButtonState()
        {
            if (this.FindControl<Button>("ExpandTargetBtn") is not Button expand) return;

            var action = GetSelectedAction();
            expand.IsVisible = action == ActionType.TypeText || action == ActionType.InsertText;
        }

        /// <summary>ISSUE_17: a resizable editor for text too long to work with in a one-line box.</summary>
        public async void ExpandTarget_Click(object? sender, RoutedEventArgs e)
        {
            if (this.FindControl<TextBox>("TargetText") is not TextBox targetText) return;

            var w = CreateAppDialog("Edit text", 720, 520, 420, 300, WindowStartupLocation.CenterOwner);

            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,*,Auto")
            };

            var heading = new TextBlock
            {
                Text = "Text this shortcut sends",
                Classes = { "SectionTitle" },
                Margin = new Avalonia.Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(heading, 0);
            grid.Children.Add(heading);

            var editor = new TextBox
            {
                Text = targetText.Text ?? string.Empty,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            Grid.SetRow(editor, 1);
            grid.Children.Add(editor);

            var counter = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            void UpdateCounter() => counter.Text = $"{(editor.Text ?? string.Empty).Length} characters";
            UpdateCounter();
            editor.TextChanged += (s2, e2) => UpdateCounter();

            var buttons = new Grid
            {
                ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto,Auto"),
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            Grid.SetColumn(counter, 0);
            buttons.Children.Add(counter);

            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
            Grid.SetColumn(cancel, 1);
            var apply = new Button { Content = "Use this text", Classes = { "Primary" }, MinWidth = 120 };
            Grid.SetColumn(apply, 2);
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            var accepted = false;
            cancel.Click += (s2, e2) => w.Close();
            apply.Click += (s2, e2) => { accepted = true; w.Close(); };

            w.Content = grid;
            w.Opened += (s2, e2) => editor.Focus();
            await ShowDialogOrWindowAsync(w);

            if (accepted) targetText.Text = editor.Text ?? string.Empty;
        }

        private void ValidateEditorTarget()
        {
            if (_loadingEditorFields) return;
            if (_editingEntry == null && !_creatingDuplicate)
            {
                SetFieldError("TargetText", null);
                SetFieldWarning("TargetText", null);
                return;
            }

            var actionType = GetSelectedAction();
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            if (!ValidateTarget(actionType, target, out _, out var targetError, out var targetWarning))
            {
                SetFieldError("TargetText", targetError);
                SetFieldWarning("TargetText", null);
            }
            else
            {
                SetFieldError("TargetText", null);
                SetFieldWarning("TargetText", targetWarning);
            }
        }

        private async System.Threading.Tasks.Task CommitSelectedEditorChangeAsync()
        {
            if (_loadingEditorFields || _editingEntry == null) return;

            var keyText = this.FindControl<TextBox>("KeyCombo")?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(keyText) || keyText.EndsWith("+", StringComparison.Ordinal))
            {
                return;
            }

            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            if (actionCombo == null || actionCombo.SelectedIndex < 0 || actionCombo.SelectedIndex > (int)ActionType.InsertText)
            {
                return;
            }

            if (!await EnsureRiskyShortcutApprovedAsync(keyText))
            {
                return;
            }

            if (!ValidateHotkeyFormat(keyText, false, out _, _editingEntry.AllowRiskyShortcut))
            {
                return;
            }

            var newAction = (ActionType)actionCombo.SelectedIndex;
            var rawTarget = this.FindControl<TextBox>("TargetText")?.Text ?? string.Empty;

            if (!ValidateTarget(newAction, rawTarget, out var normalizedTarget, out var targetError, out var targetWarning))
            {
                SetFieldError("TargetText", targetError);
                SetFieldWarning("TargetText", null);
                return;
            }

            SetFieldError("TargetText", null);
            SetFieldWarning("TargetText", targetWarning);

            _editingEntry.KeyCombination = keyText;
            _editingEntry.Action = newAction;
            _editingEntry.Target = normalizedTarget;
            _editingEntry.IsTargetObfuscated = this.FindControl<CheckBox>("ObfuscateCheck")?.IsChecked ?? false;

            ApplyHotkeys();
            RefreshVisibleHotkeys();
            SaveConfig();
            UpdateEditorModeText();
        }

        private async System.Threading.Tasks.Task<bool> EnsureRiskyShortcutApprovedAsync(string combo)
        {
            if (_loadingEditorFields) return true;
            if (!IsRiskyTypingShortcut(combo, out var riskyReason))
            {
                if (_editingEntry != null) _editingEntry.AllowRiskyShortcut = false;
                else _approvedNewRiskyCombo = null;
                return true;
            }

            if (_editingEntry?.AllowRiskyShortcut == true) return true;
            if (_editingEntry == null && IsNewRiskyComboApproved(combo)) return true;

            if (await ConfirmRiskyShortcutOverrideAsync(combo, riskyReason))
            {
                if (_editingEntry != null) _editingEntry.AllowRiskyShortcut = true;
                else _approvedNewRiskyCombo = NormalizeComboKey(combo);
                SetFieldError("KeyCombo", null);
                return true;
            }

            _loadingEditorFields = true;
            try
            {
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo)
                {
                    keyCombo.Text = _editingEntry?.KeyCombination ?? string.Empty;
                    keyCombo.Focus();
                }
            }
            finally
            {
                _loadingEditorFields = false;
            }

            if (_editingEntry == null) _approvedNewRiskyCombo = null;
            SetFieldError("KeyCombo", "Choose a different shortcut");
            return false;
        }

        public async void Add_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            ClearFieldErrors();
            var combo = this.FindControl<TextBox>("KeyCombo")?.Text;
            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            var isEditing = _editingEntry != null;

            if (isEditing)
            {
                await CommitSelectedEditorChangeAsync();
                ResetEditor();
                return;
            }

            if (actionCombo == null || actionCombo.SelectedIndex < 0)
            {
                SetFieldError("TargetText", "Choose an action");
                Program.PlaySound("error");
                return;
            }

            var allowRiskyShortcut = !string.IsNullOrWhiteSpace(combo) && IsNewRiskyComboApproved(combo);
            if (!ValidateHotkeyFormat(combo, !isEditing, out var hotkeyError, allowRiskyShortcut))
            {
                if (!IsRiskyTypingShortcut(combo, out _))
                {
                    SetFieldError("KeyCombo", hotkeyError);
                    Program.PlaySound("error");
                    return;
                }
            }

            if (combo == null || !await EnsureRiskyShortcutApprovedAsync(combo))
            {
                Program.PlaySound("error");
                return;
            }

            allowRiskyShortcut = IsNewRiskyComboApproved(combo);
            if (!ValidateHotkeyFormat(combo, !isEditing, out hotkeyError, allowRiskyShortcut))
            {
                SetFieldError("KeyCombo", hotkeyError);
                Program.PlaySound("error");
                return;
            }

            if (Hotkeys.Any(h => !ReferenceEquals(h, _editingEntry) && string.Equals(NormalizeComboKey(h.KeyCombination), NormalizeComboKey(combo!), StringComparison.OrdinalIgnoreCase)))
            {
                SetFieldError("KeyCombo", "Shortcut already exists in KeyPulse");
                Program.PlaySound("error");
                return;
            }

            var actionType = (ActionType)actionCombo.SelectedIndex;
            if (!ValidateTarget(actionType, target, out var normalizedTarget, out var targetError, out var targetWarning))
            {
                SetFieldError("TargetText", targetError);
                Program.PlaySound("error");
                return;
            }

            var entry = new HotkeyEntry
            {
                KeyCombination = combo!,
                Action = actionType,
                Target = normalizedTarget,
                IsEnabled = true,
                AllowRiskyShortcut = allowRiskyShortcut,
                IsTargetObfuscated = this.FindControl<CheckBox>("ObfuscateCheck")?.IsChecked ?? false
            };

            Hotkeys.Add(entry);
            ApplyHotkeys();

            // "Waiting" means the keys were reserved successfully but the folder or program is offline
            // at this moment; that is a perfectly valid shortcut to keep. ISSUE_8.
            if (!IsStatus(entry, "Active") && !IsStatus(entry, "Waiting"))
            {
                var reason = string.IsNullOrWhiteSpace(entry.StatusHint)
                    ? entry.RegistrationStatus.Replace("Inactive: ", string.Empty)
                    : entry.StatusHint;

                ReleaseRegistration(entry);
                Hotkeys.Remove(entry);
                ApplyHotkeys();
                RefreshVisibleHotkeys();
                SetFieldError("KeyCombo", reason);
                Program.PlaySound("error");
                return;
            }

            RefreshVisibleHotkeys();
            SaveConfig();
            ClearFieldErrors();
            SetFieldWarning("TargetText", targetWarning);
            _creatingDuplicate = false;
            _duplicateSourceCombo = null;
            SelectHotkeyEntry(entry);
        }

        public void HotkeyList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loadingEditorFields) return;

            if (sender is ListBox list && list.SelectedItem is HotkeyEntry entry)
            {
                BeginEditingEntry(entry);
            }
            else
            {
                ResetEditor();
            }
        }

        private void SelectHotkeyEntry(HotkeyEntry entry)
        {
            // A shortcut that the current search filters out cannot be selected, so drop the filter
            // rather than silently doing nothing. ISSUE_17.
            if (!VisibleHotkeys.Contains(entry))
            {
                if (this.FindControl<TextBox>("SearchBox") is TextBox searchBox) searchBox.Text = string.Empty;
                _searchText = string.Empty;
                RefreshVisibleHotkeys();
            }

            if (this.FindControl<ListBox>("HotkeyList") is ListBox list && !ReferenceEquals(list.SelectedItem, entry))
            {
                list.SelectedItem = entry;
                list.ScrollIntoView(entry);
                return;
            }

            BeginEditingEntry(entry);
        }

        private void BeginEditingEntry(HotkeyEntry entry)
        {
            _creatingDuplicate = false;
            _duplicateSourceCombo = null;
            ClearRowEditingExcept(entry);

            _editingEntry = entry;
            entry.IsEditing = true;

            _loadingEditorFields = true;
            try
            {
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = entry.KeyCombination;
                if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = (int)entry.Action;
                if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = entry.Target;
                if (this.FindControl<CheckBox>("ObfuscateCheck") is CheckBox obfuscateCheck) obfuscateCheck.IsChecked = entry.IsTargetObfuscated;
            }
            finally
            {
                _loadingEditorFields = false;
            }

            if (this.FindControl<Button>("AddButton") is Button addButton)
            {
                addButton.Content = "Save";
                addButton.IsVisible = true;
            }
            if (this.FindControl<Button>("CancelEditButton") is Button cancelButton)
            {
                cancelButton.Content = "Cancel";
                cancelButton.IsVisible = true;
            }

            UpdateActionUi();
            UpdateEditorModeText();

            // Must come last: the original code set the message and then immediately wiped it with a
            // trailing ClearFieldErrors(), so a user selecting a broken row was told nothing.
            ShowEntryStatusInEditor(entry);
        }

        private void ShowEntryStatusInEditor(HotkeyEntry entry)
        {
            ClearFieldErrors();

            if (entry.RegistrationStatus.StartsWith("Inactive:", StringComparison.OrdinalIgnoreCase))
            {
                // ISSUE_20: show the actionable hint (including a free combination to try) rather
                // than the bare status word.
                SetFieldError("KeyCombo", string.IsNullOrWhiteSpace(entry.StatusHint)
                    ? entry.RegistrationStatus.Substring("Inactive:".Length).Trim()
                    : entry.StatusHint);
            }
            else if (IsStatus(entry, "Waiting"))
            {
                SetFieldWarning("TargetText", entry.StatusHint);
            }
        }

        private void UpdateEditorModeText()
        {
            if (this.FindControl<TextBlock>("EditorModeText") is not TextBlock editText) return;

            if (_creatingDuplicate)
            {
                editText.Text = string.IsNullOrWhiteSpace(_duplicateSourceCombo)
                    ? "Duplicating shortcut. Choose a new non-conflicting shortcut, then add it."
                    : $"Duplicating {_duplicateSourceCombo}. Choose a new non-conflicting shortcut, then add it.";
                editText.IsVisible = true;
                return;
            }

            if (_editingEntry == null)
            {
                editText.Text = string.Empty;
                editText.IsVisible = false;
                return;
            }

            editText.Text = $"Editing {_editingEntry.KeyCombination} - {_editingEntry.ActionDisplay}. Changes save automatically.";
            editText.IsVisible = true;
        }

        public void CancelEdit_Click(object? sender, RoutedEventArgs e)
        {
            ResetEditor();
        }

        private void ResetEditor()
        {
            ClearRowEditingExcept();
            _editingEntry = null;
            _creatingDuplicate = false;
            _duplicateSourceCombo = null;

            _loadingEditorFields = true;
            try
            {
                if (this.FindControl<ListBox>("HotkeyList") is ListBox list) list.SelectedItem = null;
                if (this.FindControl<Button>("AddButton") is Button addButton)
                {
                    addButton.Content = "Add";
                    addButton.IsVisible = true;
                }
                if (this.FindControl<Button>("CancelEditButton") is Button cancelButton)
                {
                    cancelButton.Content = "Cancel";
                    cancelButton.IsVisible = false;
                }
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = "";
                if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = 0;
                if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = "";
                if (this.FindControl<CheckBox>("ObfuscateCheck") is CheckBox obfuscateCheck) obfuscateCheck.IsChecked = false;
            }
            finally
            {
                _loadingEditorFields = false;
            }

            UpdateActionUi();
            UpdateEditorModeText();
            ClearFieldErrors();
            this.FindControl<TextBox>("KeyCombo")?.Focus();
        }

        private void ClearRowEditingExcept(HotkeyEntry? keep = null)
        {
            foreach (var entry in Hotkeys)
            {
                if (!ReferenceEquals(entry, keep)) entry.IsEditing = false;
            }
        }

        private void HotkeyList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (e.Source is ScrollViewer sv)
            {
                if (this.FindControl<Border>("ScrollFadeTop") is Border top) top.Opacity = sv.Offset.Y > 0 ? 1 : 0;
                if (this.FindControl<Border>("ScrollFadeBottom") is Border bottom) bottom.Opacity = sv.Offset.Y < sv.Extent.Height - sv.Viewport.Height - 1 ? 1 : 0;
                if (this.FindControl<Border>("ScrollFadeLeft") is Border left) left.Opacity = sv.Offset.X > 0 ? 1 : 0;
                if (this.FindControl<Border>("ScrollFadeRight") is Border right) right.Opacity = sv.Offset.X < sv.Extent.Width - sv.Viewport.Width - 1 ? 1 : 0;
            }
        }

        public void Enabled_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HotkeyEntry entry) return;

            entry.IsEnabled = !entry.IsEnabled;
            ApplyHotkeys();
            RefreshVisibleHotkeys();
            SaveConfig();
            btn.Focus();
        }

        public async void Remove_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("close");
            if (sender is Button btn && btn.DataContext is HotkeyEntry entry)
            {
                if (!await ConfirmRemoveAsync(entry)) return;
                if (ReferenceEquals(_editingEntry, entry) || (_creatingDuplicate && entry.IsEditing)) ResetEditor();
                ReleaseRegistration(entry); // a removed row must give its keys back to Windows
                Hotkeys.Remove(entry);
                ApplyHotkeys();
                RefreshVisibleHotkeys();
                SaveConfig();
            }
        }

        public void Duplicate_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            if (sender is not Button btn || btn.DataContext is not HotkeyEntry source) return;

            ClearFieldErrors();
            ClearRowEditingExcept(source);
            source.IsEditing = true;
            _editingEntry = null;
            _creatingDuplicate = true;
            _duplicateSourceCombo = source.KeyCombination;
            _approvedNewRiskyCombo = null;

            _loadingEditorFields = true;
            try
            {
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = string.Empty;
                if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = (int)source.Action;
                if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = source.Target;
                if (this.FindControl<Button>("AddButton") is Button addButton)
                {
                    addButton.Content = "Add Duplicate";
                    addButton.IsVisible = true;
                }
                if (this.FindControl<Button>("CancelEditButton") is Button cancelButton)
                {
                    cancelButton.Content = "Cancel";
                    cancelButton.IsVisible = true;
                }
            }
            finally
            {
                _loadingEditorFields = false;
            }

            UpdateActionUi();
            UpdateEditorModeText();
            if (this.FindControl<TextBox>("KeyCombo") is TextBox shortcutBox) shortcutBox.Focus();
        }

        private async System.Threading.Tasks.Task<bool> ConfirmRemoveAsync(HotkeyEntry entry)
        {
            var w = CreateAppDialog("Remove Shortcut", 420, 190, 380, 170, WindowStartupLocation.CenterOwner, false);

            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,Auto,*,Auto")
            };

            var title = new TextBlock
            {
                Text = "Remove this shortcut?",
                Classes = { "SectionTitle" }
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var details = new TextBlock
            {
                Text = $"{entry.KeyCombination} - {entry.ActionDisplay}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(details, 1);
            grid.Children.Add(details);

            var warning = new TextBlock
            {
                Text = "This removes the shortcut immediately. The target file or text is not deleted.",
                Classes = { "Muted" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 8, 0, 12)
            };
            Grid.SetRow(warning, 2);
            grid.Children.Add(warning);

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };

            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90, TabIndex = 0 };
            var remove = new Button { Content = "Remove", Classes = { "Danger" }, MinWidth = 90, TabIndex = 1 };
            buttons.Children.Add(cancel);
            buttons.Children.Add(remove);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);

            var result = false;
            cancel.Click += (s, e) => w.Close();
            remove.Click += (s, e) =>
            {
                result = true;
                w.Close();
            };

            w.Content = grid;
            w.Opened += (s, e) => cancel.Focus();
            await ShowDialogOrWindowAsync(w);
            return result;
        }

        public void KeyCombo_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            e.Handled = true;

            var currentVk = e.Key switch
            {
                Avalonia.Input.Key.LeftCtrl => 0xA2,
                Avalonia.Input.Key.RightCtrl => 0xA3,
                Avalonia.Input.Key.LeftAlt => 0xA4,
                Avalonia.Input.Key.RightAlt => 0xA5,
                Avalonia.Input.Key.LeftShift => 0xA0,
                Avalonia.Input.Key.RightShift => 0xA1,
                Avalonia.Input.Key.LWin => 0x5B,
                Avalonia.Input.Key.RWin => 0x5C,
                _ => 0
            };
            HotkeyManager.GetModifierSnapshot(currentVk, out var rawCtrl, out var rawAlt, out var rawShift, out var rawWin);
            var mods = e.KeyModifiers;
            var parts = new System.Collections.Generic.List<string>();
            if (rawCtrl || mods.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Ctrl");
            if (rawAlt || mods.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
            if (rawShift || mods.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
            if (rawWin || mods.HasFlag(Avalonia.Input.KeyModifiers.Meta)) parts.Add("Win");
            
            bool isModifierOnly = (e.Key == Avalonia.Input.Key.LeftCtrl || e.Key == Avalonia.Input.Key.RightCtrl ||
                e.Key == Avalonia.Input.Key.LeftAlt || e.Key == Avalonia.Input.Key.RightAlt ||
                e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift ||
                e.Key == Avalonia.Input.Key.LWin || e.Key == Avalonia.Input.Key.RWin);

            if (!isModifierOnly)
            {
                // ISSUE_13: this used to write Avalonia's own Key enum name straight into the box
                // ("Add", "Oem3", "OemBackslash"), which the parser then mis-mapped or rejected.
                // "Add" is the keypad's plus key, but it parsed to 0xBB - the "=" key next to
                // Backspace - so the shortcut the user tested was not the one that got registered.
                // Everything now goes through HotkeyManager's single table.
                var vk = MapAvaloniaKeyToVirtualKey(e.Key);
                if (vk == 0)
                {
                    SetFieldError("KeyCombo", $"KeyPulse cannot use the \"{e.Key}\" key. Try another key.");
                    return;
                }

                parts.Add(HotkeyManager.VirtualKeyToName(vk));
            }

            if (sender is TextBox tb)
            {
                var txt = string.Join("+", parts);
                if (isModifierOnly && parts.Count > 0) txt += "+";
                tb.Text = txt;
            }
        }

        /// <summary>
        /// ISSUE_13: Avalonia's Key enum to a Windows virtual key. This is the only place the two
        /// vocabularies meet; everything downstream speaks virtual keys and HotkeyManager's names.
        /// </summary>
        private static uint MapAvaloniaKeyToVirtualKey(Avalonia.Input.Key key)
        {
            // Letters and the number row map straight onto their ASCII codes.
            if (key >= Avalonia.Input.Key.A && key <= Avalonia.Input.Key.Z)
            {
                return (uint)('A' + (key - Avalonia.Input.Key.A));
            }

            if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9)
            {
                return (uint)('0' + (key - Avalonia.Input.Key.D0));
            }

            if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9)
            {
                return (uint)(0x60 + (key - Avalonia.Input.Key.NumPad0));
            }

            if (key >= Avalonia.Input.Key.F1 && key <= Avalonia.Input.Key.F24)
            {
                return (uint)(0x70 + (key - Avalonia.Input.Key.F1));
            }

            switch (key)
            {
                case Avalonia.Input.Key.Enter: return 0x0D;
                case Avalonia.Input.Key.Space: return 0x20;
                case Avalonia.Input.Key.Escape: return 0x1B;
                case Avalonia.Input.Key.Tab: return 0x09;
                case Avalonia.Input.Key.Back: return 0x08;
                case Avalonia.Input.Key.Delete: return 0x2E;
                case Avalonia.Input.Key.Insert: return 0x2D;
                case Avalonia.Input.Key.Home: return 0x24;
                case Avalonia.Input.Key.End: return 0x23;
                case Avalonia.Input.Key.PageUp: return 0x21;
                case Avalonia.Input.Key.PageDown: return 0x22;
                case Avalonia.Input.Key.Up: return 0x26;
                case Avalonia.Input.Key.Down: return 0x28;
                case Avalonia.Input.Key.Left: return 0x25;
                case Avalonia.Input.Key.Right: return 0x27;
                case Avalonia.Input.Key.CapsLock: return 0x14;
                case Avalonia.Input.Key.NumLock: return 0x90;
                case Avalonia.Input.Key.Scroll: return 0x91;
                case Avalonia.Input.Key.Snapshot: return 0x2C;
                case Avalonia.Input.Key.Pause: return 0x13;
                case Avalonia.Input.Key.Apps: return 0x5D;

                // The keypad. Distinct physical keys from the punctuation row - the whole point.
                case Avalonia.Input.Key.Multiply: return 0x6A;
                case Avalonia.Input.Key.Add: return 0x6B;
                case Avalonia.Input.Key.Separator: return 0x6C;
                case Avalonia.Input.Key.Subtract: return 0x6D;
                case Avalonia.Input.Key.Decimal: return 0x6E;
                case Avalonia.Input.Key.Divide: return 0x6F;

                case Avalonia.Input.Key.OemSemicolon: return 0xBA;
                case Avalonia.Input.Key.OemPlus: return 0xBB;
                case Avalonia.Input.Key.OemComma: return 0xBC;
                case Avalonia.Input.Key.OemMinus: return 0xBD;
                case Avalonia.Input.Key.OemPeriod: return 0xBE;
                case Avalonia.Input.Key.OemQuestion: return 0xBF;
                case Avalonia.Input.Key.OemTilde: return 0xC0;
                case Avalonia.Input.Key.OemOpenBrackets: return 0xDB;
                case Avalonia.Input.Key.OemPipe: return 0xDC;
                case Avalonia.Input.Key.OemCloseBrackets: return 0xDD;
                case Avalonia.Input.Key.OemQuotes: return 0xDE;
                case Avalonia.Input.Key.Oem8: return 0xDF;
                case Avalonia.Input.Key.OemBackslash: return 0xE2;

                case Avalonia.Input.Key.MediaNextTrack: return 0xB0;
                case Avalonia.Input.Key.MediaPreviousTrack: return 0xB1;
                case Avalonia.Input.Key.MediaStop: return 0xB2;
                case Avalonia.Input.Key.MediaPlayPause: return 0xB3;
                case Avalonia.Input.Key.VolumeMute: return 0xAD;
                case Avalonia.Input.Key.VolumeDown: return 0xAE;
                case Avalonia.Input.Key.VolumeUp: return 0xAF;

                case Avalonia.Input.Key.BrowserBack: return 0xA6;
                case Avalonia.Input.Key.BrowserForward: return 0xA7;
                case Avalonia.Input.Key.BrowserRefresh: return 0xA8;
                case Avalonia.Input.Key.BrowserStop: return 0xA9;
                case Avalonia.Input.Key.BrowserSearch: return 0xAA;
                case Avalonia.Input.Key.BrowserFavorites: return 0xAB;
                case Avalonia.Input.Key.BrowserHome: return 0xAC;
            }

            return 0;
        }

        public void Settings_Click(object? sender, RoutedEventArgs e)
        {
            // ISSUE_19: there is no "open.wav" embedded in the assembly, so the old call was silent.
            Program.PlaySound("click");

            double settingsW = _currentConfig.SettingsWindowWidth;
            double settingsH = _currentConfig.SettingsWindowHeight;
            if (double.IsNaN(_currentConfig.SettingsWindowX))
            {
                var screen = this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (screen != null)
                {
                    double ratioW = screen.Bounds.Width / 1920.0;
                    double ratioH = screen.Bounds.Height / 1080.0;
                    settingsW = 580 * ratioW;
                    settingsH = 750 * ratioH;
                }
            }
            var w = CreateAppDialog("Settings", settingsW, settingsH, 440, 400, WindowStartupLocation.CenterOwner);
            if (!double.IsNaN(_currentConfig.SettingsWindowX) && !double.IsNaN(_currentConfig.SettingsWindowY))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Position = new Avalonia.PixelPoint((int)_currentConfig.SettingsWindowX, (int)_currentConfig.SettingsWindowY);
            }
            w.Closing += (senderClosing, args) =>
            {
                _currentConfig.SettingsWindowX = w.Position.X;
                _currentConfig.SettingsWindowY = w.Position.Y;
                _currentConfig.SettingsWindowWidth = w.Bounds.Width;
                _currentConfig.SettingsWindowHeight = w.Bounds.Height;
                SaveConfig();
            };

            var panel = CreateDialogPanel();

            panel.Children.Add(new TextBlock { Text = "Settings", Classes = { "SectionTitle" } });
            panel.Children.Add(new TextBlock { Text = "Startup", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 8, 0, 0) });

            // ISSUE_12: this window used to ask Windows about the scheduled task TWICE before it
            // could even be drawn, and two to four more times per toggle - each one launching
            // schtasks.exe, all of it on the UI thread. Settings hung, unpaintable, every time.
            // The state is fetched in the background and the box fills itself in when the answer
            // arrives; Program caches the answer so this normally costs nothing at all.
            var chk = new CheckBox { Content = "Start KeyPulse when Windows starts", IsEnabled = false, TabIndex = 0, IsTabStop = true };
            var startupStatus = new TextBlock
            {
                Text = "Checking your Windows startup setting...",
                Classes = { "Muted" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            var suppressStartupEvent = false;
            chk.IsCheckedChanged += async (s, ev) =>
            {
                if (suppressStartupEvent) return;

                var requestedState = chk.IsChecked == true;
                chk.IsEnabled = false;
                startupStatus.Text = requestedState ? "Turning on..." : "Turning off...";
                startupStatus.Foreground = AppBrush("AppTextMutedBrush");

                string startupError = string.Empty;
                var ok = await System.Threading.Tasks.Task.Run(() => Program.SetStartup(requestedState, out startupError));

                if (ok)
                {
                    // Keep the in-memory config in step so a later SaveConfig cannot undo it. ISSUE_6.
                    _currentConfig.LaunchOnBoot = requestedState;
                    SaveConfig();

                    startupStatus.Text = requestedState
                        ? "KeyPulse will start hidden in the tray when Windows starts."
                        : "KeyPulse will no longer start with Windows.";
                    startupStatus.Foreground = AppBrush("AppSuccessSoftBrush");
                }
                else
                {
                    suppressStartupEvent = true;
                    chk.IsChecked = !requestedState;
                    suppressStartupEvent = false;
                    startupStatus.Text = "Startup change failed: " + startupError;
                    startupStatus.Foreground = AppBrush("AppDangerBrush");
                }
                chk.IsEnabled = true;
            };
            panel.Children.Add(chk);
            panel.Children.Add(startupStatus);

            panel.Children.Add(new TextBlock { Text = "Browser", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 12, 0, 0) });
            var chromeChk = new CheckBox { Content = "Use Google Chrome to launch URL's", IsChecked = _currentConfig.UseGoogleChromeForUrls, Margin = new Avalonia.Thickness(0, 10, 0, 0), TabIndex = 1, IsTabStop = true };
            chromeChk.IsCheckedChanged += (s, ev) =>
            {
                _currentConfig.UseGoogleChromeForUrls = chromeChk.IsChecked == true;
                SaveConfig();
            };
            panel.Children.Add(chromeChk);

            // ISSUE_21: the app used to be pinned to a black window regardless of the Windows setting.
            panel.Children.Add(new TextBlock { Text = "Appearance", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 12, 0, 0) });
            var themeCombo = new ComboBox
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                TabIndex = 2,
                IsTabStop = true,
                ItemsSource = new List<string> { "Match Windows", "Light", "Dark" }
            };
            themeCombo.SelectedIndex = _currentConfig.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };
            themeCombo.SelectionChanged += (s, ev) =>
            {
                _currentConfig.Theme = themeCombo.SelectedIndex switch
                {
                    1 => "Light",
                    2 => "Dark",
                    _ => "System"
                };
                App.ApplyTheme(_currentConfig.Theme);
                SaveConfig();
            };
            panel.Children.Add(themeCombo);

            // ------------------------------------------------------------------
            // ISSUE_9: typing speed is a setting, not a hard-coded 12 ms per character.
            // ------------------------------------------------------------------
            panel.Children.Add(new TextBlock { Text = "Typing speed", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 12, 0, 0) });
            var speedCombo = new ComboBox
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                TabIndex = 3,
                IsTabStop = true,
                ItemsSource = new List<string>
                {
                    "Fast (recommended)",
                    "Normal",
                    "Compatible - for old consoles and remote sessions"
                }
            };
            speedCombo.SelectedIndex = NormalizeTypingDelay(_currentConfig.TypingDelayMs) switch
            {
                TypingDelayNormal => 1,
                TypingDelayCompatible => 2,
                _ => 0
            };
            var speedHint = new TextBlock
            {
                Text = "Only affects the \"Type text\" action. Drop to Compatible if characters go missing.",
                Classes = { "Muted" },
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            speedCombo.SelectionChanged += (s, ev) =>
            {
                _currentConfig.TypingDelayMs = speedCombo.SelectedIndex switch
                {
                    1 => TypingDelayNormal,
                    2 => TypingDelayCompatible,
                    _ => TypingDelayFast
                };
                InputSimulator.CharacterDelayMs = _currentConfig.TypingDelayMs;
                SaveConfig();
            };
            panel.Children.Add(speedCombo);
            panel.Children.Add(speedHint);

            // ISSUE_19: sounds now actually play, so they need an off switch.
            panel.Children.Add(new TextBlock { Text = "Sound", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 12, 0, 0) });
            var soundChk = new CheckBox { Content = "Play a sound when adding or removing shortcuts", IsChecked = _currentConfig.SoundEnabled, TabIndex = 4, IsTabStop = true };
            soundChk.IsCheckedChanged += (s, ev) =>
            {
                _currentConfig.SoundEnabled = soundChk.IsChecked == true;
                Program.SoundEnabled = _currentConfig.SoundEnabled;
                SaveConfig();
                if (_currentConfig.SoundEnabled) Program.PlaySound("click");
            };
            panel.Children.Add(soundChk);

            panel.Children.Add(new TextBlock { Text = "Backup & Restore", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 14, 0, 0) });
            var backupBtn = new Button { Content = "Backup Configuration", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, TabIndex = 5, IsTabStop = true };
            backupBtn.Classes.Add("Secondary");
            panel.Children.Add(backupBtn);

            var restoreBtn = new Button { Content = "Restore Configuration", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, TabIndex = 6, IsTabStop = true };
            restoreBtn.Classes.Add("Secondary");
            panel.Children.Add(restoreBtn);

            var statusTxt = new TextBlock { Margin = new Avalonia.Thickness(0,10,0,0), Foreground = AppBrush("AppWarningBrush"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "Muted" } };
            panel.Children.Add(statusTxt);

            // ------------------------------------------------------------------
            // ISSUE_15: say which build this is, and offer to find out if it is stale.
            // ------------------------------------------------------------------
            panel.Children.Add(new TextBlock { Text = "About", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 14, 0, 0) });
            panel.Children.Add(new TextBlock
            {
                Text = "KeyPulse version " + Program.AppVersion,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

            var updateStatus = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 2, 0, 0)
            };
            var updateBtn = new Button
            {
                Content = "Check for updates",
                Classes = { "Secondary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                TabIndex = 7,
                IsTabStop = true
            };
            var updateButtonOpensDownloadPage = false;
            updateBtn.Click += async (s, ev) =>
            {
                if (updateButtonOpensDownloadPage)
                {
                    try { Process.Start(new ProcessStartInfo(Program.ReleasesPageUrl) { UseShellExecute = true }); }
                    catch (Exception ex) { updateStatus.Text = "Could not open the download page: " + ex.Message; }
                    return;
                }

                updateBtn.IsEnabled = false;
                updateStatus.Text = "Checking github.com for a newer release...";
                updateStatus.Foreground = AppBrush("AppTextMutedBrush");
                try
                {
                    var result = await UpdateChecker.CheckAsync();
                    if (!result.Succeeded)
                    {
                        updateStatus.Text = "Could not check for updates: " + result.Message;
                        updateStatus.Foreground = AppBrush("AppWarningBrush");
                    }
                    else if (result.UpdateAvailable)
                    {
                        updateStatus.Text = $"Version {result.LatestVersion} is available. You have {Program.AppVersion}.";
                        updateStatus.Foreground = AppBrush("AppWarningBrush");
                        updateBtn.Content = "Open the download page";
                        updateBtn.Classes.Remove("Secondary");
                        updateBtn.Classes.Add("Primary");
                        updateButtonOpensDownloadPage = true;
                    }
                    else
                    {
                        updateStatus.Text = $"You are running the latest release ({Program.AppVersion}).";
                        updateStatus.Foreground = AppBrush("AppSuccessSoftBrush");
                    }
                }
                finally
                {
                    updateBtn.IsEnabled = true;
                }
            };
            panel.Children.Add(updateBtn);
            panel.Children.Add(updateStatus);

            panel.Children.Add(new TextBlock { Text = "Privileges", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 14, 0, 0) });
            bool isAdmin = Program.IsElevated; // ISSUE_20: one cached answer for the whole app
            var adminBtn = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 4, 0, 0), TabIndex = 8, IsTabStop = true };
            var adminStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var adminTitle = new TextBlock { Text = isAdmin ? "Run As User Level" : "Run As Administrator", FontWeight = Avalonia.Media.FontWeight.SemiBold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var adminSub = new TextBlock { Text = isAdmin ? "(Safer, but might not paste into Admin apps)" : "(To Inject Text Into Older CMD Consoles)", FontSize = 10, Classes = { "Muted" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

            adminStack.Children.Add(adminTitle);
            adminStack.Children.Add(adminSub);
            adminBtn.Content = adminStack;

            if (isAdmin) {
                adminBtn.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2e5c46"));
                ToolTip.SetTip(adminBtn, "Click to revert to standard user permissions. This restores your system security posture, but you will lose the ability to inject hotkeys into Admin-level apps.");
            } else {
                adminBtn.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7d1d1d"));
                ToolTip.SetTip(adminBtn, "Some older black windows like CMD block pasting text. Running as Administrator fixes this by elevating the application.");
            }

            adminBtn.Click += async (s, ev) => {
                if (isAdmin) {
                    RelaunchWithPrivilegeChange(w, "--remove-admin-task", statusTxt);
                } else {
                    var warnW = CreateAppDialog("Security Warning", 420, 260, 420, 260, WindowStartupLocation.CenterOwner, false, true);
                    var warnP = CreateDialogPanel();
                    warnP.Children.Add(new TextBlock { Text = "WARNING", Classes = { "ErrorText", "SectionTitle" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    warnP.Children.Add(new TextBlock { Text = "Running a hotkey tool as Administrator grants it FULL control over your PC.\n\nIf malware or an exploit were to hijack this app's input pipeline, it could theoretically wipe your hard drive or bypass your antivirus.\n\nAre you absolutely sure you want to run KeyPulse as an Administrator persistently?", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0,10,0,10) });

                    var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Spacing = 20 };
                    var accepted = false;
                    var okBtn = new Button { Content = "I Accept The Risk", Classes = { "Danger" } };
                    okBtn.Click += (ws, wev) => { accepted = true; warnW.Close(); };
                    var cancelBtn = new Button { Content = "Cancel", Classes = { "Secondary" } };
                    cancelBtn.Click += (ws, wev) => warnW.Close();

                    btnPanel.Children.Add(cancelBtn);
                    btnPanel.Children.Add(okBtn);
                    warnP.Children.Add(btnPanel);
                    warnW.Content = warnP;

                    await ShowDialogOrWindowAsync(warnW);
                    if (accepted) RelaunchWithPrivilegeChange(w, "--setup-admin-task", statusTxt);
                }
            };
            panel.Children.Add(adminBtn);

            var closeBtn = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 9, IsTabStop = true };
            closeBtn.Classes.Add("Secondary");
            closeBtn.Click += (s, ev) => w.Close();
            panel.Children.Add(closeBtn);

            // ------------------------------------------------------------------
            // ISSUE_1 / ISSUE_24: backup and restore.
            //
            // BackupService owns the format, the encryption, the integrity check and the validation
            // so this handler cannot accidentally skip one. What happens here is only the asking:
            // where to put it, whether to protect it, and - on the way back in - whether the user
            // really means to replace what they have.
            // ------------------------------------------------------------------
            backupBtn.Click += async (s, ev) =>
            {
                backupBtn.IsEnabled = false; restoreBtn.IsEnabled = false;
                try
                {
                    statusTxt.Text = "Choose a backup destination.";
                    statusTxt.Foreground = AppBrush("AppTextMutedBrush");

                    var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Export Backup",
                        DefaultExtension = "json",
                        SuggestedFileName = "KeyPulse_Backup_" + DateTime.Now.ToString("yyyy-MM-dd") + ".json",
                        FileTypeChoices = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("KeyPulse backup") { Patterns = new[] { "*.json" } }
                        }
                    };
                    var file = await w.StorageProvider.SaveFilePickerAsync(options);
                    if (file == null)
                    {
                        statusTxt.Text = "Backup canceled.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                        return;
                    }

                    // ISSUE_24: targets can hold passwords - that is what the Blur/Obfuscate box is
                    // for - so exporting them in the clear needs to be a decision, not a default
                    // nobody was ever told about.
                    var choice = await AskBackupPassphraseAsync();
                    if (choice.Cancelled)
                    {
                        statusTxt.Text = "Backup canceled.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                        return;
                    }

                    statusTxt.Text = choice.Passphrase == null
                        ? "Writing backup..."
                        : "Encrypting and writing backup...";
                    statusTxt.Foreground = AppBrush("AppWarningBrush");

                    SaveConfig();
                    var payload = BuildBackupPayload();
                    var destination = file.Path.LocalPath;
                    var passphrase = choice.Passphrase;

                    // Key derivation is deliberately slow. Keep it off the UI thread.
                    string writeError = string.Empty;
                    var ok = await System.Threading.Tasks.Task.Run(
                        () => BackupService.Write(destination, payload, passphrase, out writeError));

                    if (!ok)
                    {
                        statusTxt.Text = "Export failed: " + writeError;
                        statusTxt.Foreground = AppBrush("AppDangerBrush");
                        return;
                    }

                    statusTxt.Text = passphrase == null
                        ? $"Backup exported and verified: {payload.Shortcuts.Count} shortcut(s), plus your window sizes, "
                          + "column widths and preferences. It is NOT password protected, so its targets are readable "
                          + "by anyone who opens the file - keep it somewhere you trust."
                        : $"Backup exported, encrypted and verified: {payload.Shortcuts.Count} shortcut(s), plus your window "
                          + "sizes, column widths and preferences. Without this password the file cannot be restored, "
                          + "and KeyPulse cannot recover it for you.";
                    statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                }
                catch (Exception ex)
                {
                    statusTxt.Text = "Export failed: " + ex.Message;
                    statusTxt.Foreground = AppBrush("AppDangerBrush");
                }
                finally
                {
                    backupBtn.IsEnabled = true; restoreBtn.IsEnabled = true;
                    backupBtn.Focus();
                }
            };

            restoreBtn.Click += async (s, ev) =>
            {
                backupBtn.IsEnabled = false; restoreBtn.IsEnabled = false;
                try
                {
                    statusTxt.Text = "Choose a KeyPulse backup file.";
                    statusTxt.Foreground = AppBrush("AppTextMutedBrush");

                    var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Import Backup",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("KeyPulse backup") { Patterns = new[] { "*.json" } }
                        }
                    };
                    var files = await w.StorageProvider.OpenFilePickerAsync(options);
                    if (files == null || files.Count == 0)
                    {
                        statusTxt.Text = "Restore canceled.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                        return;
                    }

                    var sourcePath = files[0].Path.LocalPath;
                    statusTxt.Text = "Checking the backup...";
                    statusTxt.Foreground = AppBrush("AppWarningBrush");

                    var inspection = await System.Threading.Tasks.Task.Run(() => BackupService.Inspect(sourcePath));
                    if (!inspection.Ok)
                    {
                        statusTxt.Text = "Restore stopped: " + inspection.Error + " Your shortcuts were not touched.";
                        statusTxt.Foreground = AppBrush("AppDangerBrush");
                        return;
                    }

                    // Unlock before asking to confirm, so the confirmation can state real numbers.
                    BackupPayload? payload = inspection.Payload;
                    if (payload == null)
                    {
                        payload = await UnlockBackupAsync(inspection, statusTxt);
                        if (payload == null) return; // UnlockBackupAsync has already explained why
                    }

                    if (!BackupService.ValidatePayload(payload, out var importError, out var importWarnings))
                    {
                        statusTxt.Text = "Restore stopped: " + importError + " Your shortcuts were not touched.";
                        statusTxt.Foreground = AppBrush("AppDangerBrush");
                        return;
                    }

                    var restoreLayout = true;
                    if (!await ConfirmRestoreAsync(inspection, payload, files[0].Name, importWarnings, v => restoreLayout = v))
                    {
                        statusTxt.Text = "Restore canceled. Your shortcuts were not touched.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                        return;
                    }

                    statusTxt.Text = "Restoring... please wait.";
                    statusTxt.Foreground = AppBrush("AppWarningBrush");

                    var rollbackPath = ConfigStore.SaveRollbackCopy("before-restore");
                    var previousConfig = CloneConfig(_currentConfig);

                    try
                    {
                        var problems = ApplyBackupPayload(payload, restoreLayout, out var layoutNote);
                        var imported = Hotkeys.Count;

                        var notes = new List<string>();
                        if (rollbackPath != null) notes.Add("Your previous settings were kept as: " + rollbackPath);
                        if (!string.IsNullOrEmpty(layoutNote)) notes.Add(layoutNote);
                        if (inspection.Kind == BackupKind.LegacyConfigCopy && !string.IsNullOrEmpty(inspection.Error))
                        {
                            notes.Add(inspection.Error);
                        }
                        notes.Add("Whether KeyPulse starts with Windows was left exactly as it is on this PC.");

                        var suffix = "\n" + string.Join("\n", notes);

                        if (problems == 0)
                        {
                            statusTxt.Text = $"Restore complete. All {imported} shortcut(s) are set up and working." + suffix;
                            statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                        }
                        else
                        {
                            var names = Hotkeys
                                .Where(h => h.IsEnabled && !IsStatus(h, "Active") && !IsStatus(h, "Waiting"))
                                .Select(h => h.KeyCombination)
                                .Take(6)
                                .ToList();

                            statusTxt.Text = $"Restored all {imported} shortcut(s). {problems} need attention on this PC: "
                                + string.Join(", ", names)
                                + (problems > names.Count ? ", ..." : string.Empty)
                                + ". Click a red row in the list to see why and what to use instead."
                                + suffix;
                            statusTxt.Foreground = AppBrush("AppWarningBrush");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Put everything back, exactly as it was, and say so.
                        Program.LogCrash("Restore failed, rolling back: " + ex);
                        try
                        {
                            RestoreConfigSnapshot(previousConfig);
                            ApplyShortcutColumnResources();
                            SaveConfig();
                            statusTxt.Text = "Restore failed and your previous shortcuts were put back: " + ex.Message;
                        }
                        catch (Exception rollbackEx)
                        {
                            Program.LogCrash("Rollback after a failed restore also failed: " + rollbackEx);
                            statusTxt.Text = "Restore failed (" + ex.Message + ") and the rollback also failed. "
                                + (rollbackPath == null
                                    ? "Close KeyPulse without saving and check %APPDATA%\\KeyPulse."
                                    : "Your previous settings are in " + rollbackPath + " - close KeyPulse and rename that file to config.json.");
                        }
                        statusTxt.Foreground = AppBrush("AppDangerBrush");
                    }
                }
                catch (Exception ex)
                {
                    statusTxt.Text = "Import failed: " + ex.Message;
                    statusTxt.Foreground = AppBrush("AppDangerBrush");
                }
                finally
                {
                    backupBtn.IsEnabled = true; restoreBtn.IsEnabled = true;
                    restoreBtn.Focus();
                }
            };

            w.Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            w.Opened += (s, ev) => LoadStartupStateAsync(chk, startupStatus, v => suppressStartupEvent = v);
            ShowDialogOrWindow(w);
        }

        /// <summary>ISSUE_12: fetches the startup state off the UI thread and fills the box in.</summary>
        private async void LoadStartupStateAsync(CheckBox chk, TextBlock startupStatus, Action<bool> setSuppress)
        {
            try
            {
                // ISSUE_6: ask the one authority that knows about BOTH the Run key and the elevated
                // logon task. Reading only the Run key made this box lie whenever admin mode was on.
                var (isStartup, adminTaskInstalled) = await System.Threading.Tasks.Task.Run(
                    () => (Program.IsStartupEnabled(), Program.IsAdminTaskInstalled()));

                setSuppress(true);
                chk.IsChecked = isStartup;
                setSuppress(false);

                chk.IsEnabled = true;
                startupStatus.Text = isStartup
                    ? (adminTaskInstalled
                        ? "KeyPulse starts with Windows using its Administrator logon task."
                        : "KeyPulse starts with Windows.")
                    : "KeyPulse does not start with Windows.";
                startupStatus.Foreground = AppBrush("AppTextMutedBrush");
            }
            catch (Exception ex)
            {
                chk.IsEnabled = true;
                startupStatus.Text = "Windows did not answer about the startup setting: " + ex.Message;
                startupStatus.Foreground = AppBrush("AppDangerBrush");
            }
        }

        /// <summary>
        /// ISSUE_7: leaving for an elevated (or de-elevated) restart used to call Environment.Exit(0)
        /// the instant the new process started. That is a hard kill: the window's closing handler
        /// never ran, so SaveConfig never ran, and every shortcut added or edited since the last save
        /// was gone when KeyPulse came back. It also exited even when the user dismissed the UAC
        /// prompt, because the failure was swallowed by an empty catch.
        /// </summary>
        private void RelaunchWithPrivilegeChange(Window settingsWindow, string arguments, TextBlock statusTxt)
        {
            try
            {
                // Save FIRST, while this window is still open so a failure can be reported in it.
                // Nothing below is allowed to lose the user's work.
                SaveConfig();
            }
            catch (Exception ex)
            {
                Program.LogCrash("Could not save before changing privileges: " + ex);
                statusTxt.Text = "Your settings could not be saved, so KeyPulse did not restart: " + ex.Message;
                statusTxt.Foreground = AppBrush("AppDangerBrush");
                return;
            }

            // Its Closing handler stores the window geometry and saves again.
            try { settingsWindow.Close(); } catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Program.ExePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                // Almost always "the user said No to the UAC prompt". Stay running.
                Program.LogDebug("Privilege change was not started: " + ex.Message);
                ShowActionMessage("KeyPulse", "Nothing was changed",
                    "KeyPulse is still running exactly as before. Windows did not allow the change to start ("
                    + ex.Message + ").", false);
                return;
            }

            // The replacement process is on its way; shut this one down cleanly.
            try { HotkeyManager.DisableCaptureHook(); } catch { }
            try { HotkeyManager.Clear(); } catch { }
            try { HotkeyManager.Stop(); } catch { }
            Environment.Exit(0);
        }

        /// <summary>ISSUE_24: everything a restore should be able to put back.</summary>
        private BackupPayload BuildBackupPayload()
        {
            CaptureShortcutColumnWidths();

            return new BackupPayload
            {
                UseGoogleChromeForUrls = _currentConfig.UseGoogleChromeForUrls,
                SoundEnabled = _currentConfig.SoundEnabled,
                TypingDelayMs = _currentConfig.TypingDelayMs,
                Theme = _currentConfig.Theme,
                ShortcutSortColumn = _currentConfig.ShortcutSortColumn,
                ShortcutSortDescending = _currentConfig.ShortcutSortDescending,
                HasSeenTrayHint = _currentConfig.HasSeenTrayHint,
                Window = new BackupWindowLayout
                {
                    MainWindowX = _currentConfig.MainWindowX,
                    MainWindowY = _currentConfig.MainWindowY,
                    MainWindowWidth = _currentConfig.MainWindowWidth,
                    MainWindowHeight = _currentConfig.MainWindowHeight,
                    MainWindowState = _currentConfig.MainWindowState,
                    SettingsWindowX = _currentConfig.SettingsWindowX,
                    SettingsWindowY = _currentConfig.SettingsWindowY,
                    SettingsWindowWidth = _currentConfig.SettingsWindowWidth,
                    SettingsWindowHeight = _currentConfig.SettingsWindowHeight,
                    SetupWindowX = _currentConfig.SetupWindowX,
                    SetupWindowY = _currentConfig.SetupWindowY,
                    SetupWindowWidth = _currentConfig.SetupWindowWidth,
                    SetupWindowHeight = _currentConfig.SetupWindowHeight,
                    ShortcutStatusColumnWidth = _currentConfig.ShortcutStatusColumnWidth,
                    ShortcutKeyColumnWidth = _currentConfig.ShortcutKeyColumnWidth,
                    ShortcutActionColumnWidth = _currentConfig.ShortcutActionColumnWidth,
                    ShortcutTargetColumnWidth = _currentConfig.ShortcutTargetColumnWidth
                },
                Shortcuts = Hotkeys.Select(h => new BackupShortcut
                {
                    Id = h.Id,
                    IsEnabled = h.IsEnabled,
                    AllowRiskyShortcut = h.AllowRiskyShortcut,
                    KeyCombination = h.KeyCombination,
                    Action = h.Action,
                    // The plain value on purpose: on disk it is tied to this Windows account, so a
                    // backup carrying the protected form could never be restored anywhere else.
                    // Protection for the FILE is the passphrase offered when it is saved.
                    Target = h.Target,
                    IsTargetObfuscated = h.IsTargetObfuscated
                }).ToList()
            };
        }

        private readonly struct BackupPassphraseChoice
        {
            public bool Cancelled { get; init; }

            /// <summary>Null means the user chose to save without a password.</summary>
            public string? Passphrase { get; init; }
        }

        /// <summary>ISSUE_24: offers to encrypt the backup, and makes the trade-off explicit.</summary>
        private async System.Threading.Tasks.Task<BackupPassphraseChoice> AskBackupPassphraseAsync()
        {
            var w = CreateAppDialog("Protect this backup", 540, 380, 480, 340, WindowStartupLocation.CenterOwner);

            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto")
            };

            var title = new TextBlock { Text = "Protect this backup with a password?", Classes = { "SectionTitle" } };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var explain = new TextBlock
            {
                Text = "A backup contains the folder, program, link or text behind every shortcut - including anything you hid with the Blur box. With a password the file is encrypted and useless to anyone else. Without one, its contents are readable by anyone who opens it.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Classes = { "Muted" },
                Margin = new Avalonia.Thickness(0, 8, 0, 12)
            };
            Grid.SetRow(explain, 1);
            grid.Children.Add(explain);

            var first = new TextBox { PasswordChar = '●', Watermark = "Password" };
            Grid.SetRow(first, 2);
            grid.Children.Add(first);

            var second = new TextBox { PasswordChar = '●', Watermark = "Type it again", Margin = new Avalonia.Thickness(0, 8, 0, 0) };
            Grid.SetRow(second, 3);
            grid.Children.Add(second);

            var problem = new TextBlock
            {
                Classes = { "ErrorText" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                IsVisible = false,
                Margin = new Avalonia.Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(problem, 4);
            grid.Children.Add(problem);

            var caution = new TextBlock
            {
                Text = "KeyPulse cannot recover a forgotten password. There is no back door - that is the point of it.",
                Classes = { "Muted" },
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(caution, 5);
            grid.Children.Add(caution);

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90 };
            var skip = new Button { Content = "Save without a password", Classes = { "Secondary" }, MinWidth = 180 };
            var protect = new Button { Content = "Encrypt", Classes = { "Primary" }, MinWidth = 100 };
            buttons.Children.Add(cancel);
            buttons.Children.Add(skip);
            buttons.Children.Add(protect);
            Grid.SetRow(buttons, 6);
            grid.Children.Add(buttons);

            var cancelled = true;
            string? chosen = null;

            cancel.Click += (s, e) => w.Close();
            skip.Click += (s, e) => { cancelled = false; chosen = null; w.Close(); };
            protect.Click += (s, e) =>
            {
                var a = first.Text ?? string.Empty;
                var b = second.Text ?? string.Empty;

                if (a.Length < 8)
                {
                    problem.Text = "Use at least 8 characters, or choose \"Save without a password\".";
                    problem.IsVisible = true;
                    first.Focus();
                    return;
                }

                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    problem.Text = "The two passwords do not match.";
                    problem.IsVisible = true;
                    second.Focus();
                    return;
                }

                cancelled = false;
                chosen = a;
                w.Close();
            };

            w.Content = grid;
            w.Opened += (s, e) => first.Focus();
            await ShowDialogOrWindowAsync(w);

            return new BackupPassphraseChoice { Cancelled = cancelled, Passphrase = chosen };
        }

        /// <summary>
        /// ISSUE_24: asks for the password of an encrypted backup and lets the user try again.
        /// Returns null when the user gave up, having already written the reason into statusTxt.
        /// </summary>
        private async System.Threading.Tasks.Task<BackupPayload?> UnlockBackupAsync(BackupInspection inspection, TextBlock statusTxt)
        {
            var attemptProblem = string.Empty;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var passphrase = await AskUnlockPassphraseAsync(attemptProblem);
                if (passphrase == null)
                {
                    statusTxt.Text = "Restore canceled. Your shortcuts were not touched.";
                    statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                    return null;
                }

                statusTxt.Text = "Unlocking the backup...";
                statusTxt.Foreground = AppBrush("AppWarningBrush");

                var opened = await System.Threading.Tasks.Task.Run(() => BackupService.Open(inspection, passphrase));
                if (opened.Ok) return opened.Payload;

                if (!opened.WrongPassphrase)
                {
                    statusTxt.Text = "Restore stopped: " + opened.Error + " Your shortcuts were not touched.";
                    statusTxt.Foreground = AppBrush("AppDangerBrush");
                    return null;
                }

                attemptProblem = opened.Error;
            }

            statusTxt.Text = "Restore stopped: the backup was not unlocked. Your shortcuts were not touched.";
            statusTxt.Foreground = AppBrush("AppDangerBrush");
            return null;
        }

        private async System.Threading.Tasks.Task<string?> AskUnlockPassphraseAsync(string problemText)
        {
            var w = CreateAppDialog("Unlock backup", 480, 280, 440, 260, WindowStartupLocation.CenterOwner);

            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,Auto,Auto,*,Auto")
            };

            var title = new TextBlock { Text = "This backup is password protected", Classes = { "SectionTitle" } };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var box = new TextBox
            {
                PasswordChar = '●',
                Watermark = "Password",
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            var problem = new TextBlock
            {
                Text = problemText,
                Classes = { "ErrorText" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                IsVisible = !string.IsNullOrEmpty(problemText),
                Margin = new Avalonia.Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(problem, 2);
            grid.Children.Add(problem);

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };
            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90 };
            var unlock = new Button { Content = "Unlock", Classes = { "Primary" }, MinWidth = 100, IsDefault = true };
            buttons.Children.Add(cancel);
            buttons.Children.Add(unlock);
            Grid.SetRow(buttons, 4);
            grid.Children.Add(buttons);

            string? result = null;
            cancel.Click += (s, e) => w.Close();
            unlock.Click += (s, e) =>
            {
                result = box.Text ?? string.Empty;
                w.Close();
            };

            w.Content = grid;
            w.Opened += (s, e) => box.Focus();
            await ShowDialogOrWindowAsync(w);
            return result;
        }

        /// <summary>ISSUE_1: say exactly what is about to be replaced, before replacing it.</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmRestoreAsync(
            BackupInspection inspection,
            BackupPayload payload,
            string fileName,
            List<string> warnings,
            Action<bool> setRestoreLayout)
        {
            var w = CreateAppDialog("Restore Shortcuts", 560, 460, 480, 400, WindowStartupLocation.CenterOwner);

            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,Auto,Auto,*,Auto,Auto")
            };

            var title = new TextBlock
            {
                Text = "Replace all your shortcuts?",
                Classes = { "SectionTitle" },
                Foreground = AppBrush("AppWarningBrush")
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var created = string.Empty;
            if (!string.IsNullOrWhiteSpace(inspection.CreatedUtc)
                && DateTime.TryParse(inspection.CreatedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var createdUtc))
            {
                created = ", taken on " + createdUtc.ToLocalTime().ToString("d MMM yyyy HH:mm");
            }

            var origin = inspection.Kind == BackupKind.LegacyConfigCopy
                ? "\nThis is a settings file from an older version of KeyPulse. It will be read and upgraded."
                : (inspection.IsEncrypted ? "\nThe file was password protected and has been unlocked." : string.Empty);

            var details = new TextBlock
            {
                Text = $"\"{fileName}\"{created} holds {payload.Shortcuts.Count} shortcut(s).\n\n"
                     + $"Restoring removes the {Hotkeys.Count} shortcut(s) you have now and puts those in their place."
                     + origin,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(details, 1);
            grid.Children.Add(details);

            var layoutCheck = new CheckBox
            {
                Content = "Also restore window sizes, positions and column widths",
                IsChecked = true,
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            layoutCheck.IsCheckedChanged += (s, e) => setRestoreLayout(layoutCheck.IsChecked == true);
            Grid.SetRow(layoutCheck, 2);
            grid.Children.Add(layoutCheck);

            var warningPanel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
            if (warnings.Count > 0)
            {
                warningPanel.Children.Add(new TextBlock
                {
                    Text = warnings.Count == 1
                        ? "One shortcut in this backup needs attention:"
                        : $"{warnings.Count} shortcuts in this backup need attention:",
                    Foreground = AppBrush("AppWarningBrush"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });

                foreach (var warning in warnings.Take(6))
                {
                    warningPanel.Children.Add(new TextBlock
                    {
                        Text = "  " + warning,
                        Classes = { "Muted" },
                        FontSize = 11,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    });
                }

                if (warnings.Count > 6)
                {
                    warningPanel.Children.Add(new TextBlock
                    {
                        Text = $"  ...and {warnings.Count - 6} more.",
                        Classes = { "Muted" },
                        FontSize = 11
                    });
                }

                warningPanel.Children.Add(new TextBlock
                {
                    Text = "They will still be imported, and flagged in the list.",
                    Classes = { "Muted" },
                    FontSize = 11,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            }

            var warningScroll = new ScrollViewer
            {
                Content = warningPanel,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(warningScroll, 3);
            grid.Children.Add(warningScroll);

            var reassurance = new TextBlock
            {
                Text = "A dated copy of your current settings is written first, so this can be undone by hand if it turns out to be the wrong file. Whether KeyPulse starts with Windows is left alone.",
                Classes = { "Muted" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 8, 0, 12)
            };
            Grid.SetRow(reassurance, 4);
            grid.Children.Add(reassurance);

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };

            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90, TabIndex = 0 };
            var proceed = new Button { Content = "Replace shortcuts", Classes = { "Danger" }, MinWidth = 150, TabIndex = 1 };
            buttons.Children.Add(cancel);
            buttons.Children.Add(proceed);
            Grid.SetRow(buttons, 5);
            grid.Children.Add(buttons);

            var result = false;
            cancel.Click += (s, e) => w.Close();
            proceed.Click += (s, e) => { result = true; w.Close(); };

            w.Content = grid;
            w.Opened += (s, e) => cancel.Focus();
            await ShowDialogOrWindowAsync(w);
            return result;
        }

        /// <summary>
        /// Imports everything, then reports what needs attention. Rolling the whole restore back
        /// because a single key was taken made this feature useless on a new PC, which is its main
        /// use. Returns the number of shortcuts that could not be switched on here.
        /// </summary>
        private int ApplyBackupPayload(BackupPayload payload, bool restoreLayout, out string layoutNote)
        {
            layoutNote = string.Empty;

            ReleaseAllRegistrations();
            Hotkeys.Clear();

            foreach (var item in payload.Shortcuts)
            {
                Hotkeys.Add(new HotkeyEntry
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString() : item.Id,
                    IsEnabled = item.IsEnabled,
                    AllowRiskyShortcut = item.AllowRiskyShortcut,
                    KeyCombination = item.KeyCombination ?? string.Empty,
                    Action = item.Action,
                    Target = item.Target ?? string.Empty,
                    // ISSUE_24: the Blur/Obfuscate choice is per shortcut and travels with it.
                    IsTargetObfuscated = item.IsTargetObfuscated
                });
            }

            _currentConfig.UseGoogleChromeForUrls = payload.UseGoogleChromeForUrls;
            _currentConfig.SoundEnabled = payload.SoundEnabled;
            _currentConfig.TypingDelayMs = NormalizeTypingDelay(payload.TypingDelayMs);
            _currentConfig.Theme = string.IsNullOrWhiteSpace(payload.Theme) ? "System" : payload.Theme;
            _currentConfig.ShortcutSortColumn = payload.ShortcutSortColumn ?? string.Empty;
            _currentConfig.ShortcutSortDescending = payload.ShortcutSortDescending;
            _currentConfig.HasSeenTrayHint = payload.HasSeenTrayHint;

            Program.SoundEnabled = _currentConfig.SoundEnabled;
            InputSimulator.CharacterDelayMs = _currentConfig.TypingDelayMs;
            App.ApplyTheme(_currentConfig.Theme);
            _sortColumn = _currentConfig.ShortcutSortColumn;
            _sortDescending = _currentConfig.ShortcutSortDescending;
            UpdateSortHeaderText();

            if (restoreLayout && payload.Window != null)
            {
                layoutNote = ApplyRestoredWindowLayout(payload.Window);
            }

            var problems = ApplyHotkeys();
            RefreshVisibleHotkeys();
            SaveConfig();
            return problems;
        }

        /// <summary>
        /// ISSUE_24: puts window geometry back, but only where it makes sense on THIS machine.
        ///
        /// A position saved on a three-monitor desk, restored onto a laptop, would otherwise place
        /// the window on a screen that does not exist - it would open somewhere the user cannot see
        /// or reach, and would look exactly like the app failing to start. Sizes are clamped to the
        /// screen and positions are only used when they land on a monitor that is actually attached.
        /// </summary>
        private string ApplyRestoredWindowLayout(BackupWindowLayout layout)
        {
            var skipped = new List<string>();

            // Column widths are screen-independent; clamping alone is enough.
            if (layout.ShortcutStatusColumnWidth > 0) _currentConfig.ShortcutStatusColumnWidth = ClampColumnWidth(layout.ShortcutStatusColumnWidth, StatusColumnDefault, StatusColumnMin, StatusColumnMax);
            if (layout.ShortcutKeyColumnWidth > 0) _currentConfig.ShortcutKeyColumnWidth = ClampColumnWidth(layout.ShortcutKeyColumnWidth, KeyColumnDefault, KeyColumnMin, KeyColumnMax);
            if (layout.ShortcutActionColumnWidth > 0) _currentConfig.ShortcutActionColumnWidth = ClampColumnWidth(layout.ShortcutActionColumnWidth, ActionColumnDefault, ActionColumnMin, ActionColumnMax);
            if (layout.ShortcutTargetColumnWidth > 0) _currentConfig.ShortcutTargetColumnWidth = ClampColumnWidth(layout.ShortcutTargetColumnWidth, TargetColumnDefault, TargetColumnMin, TargetColumnMax);
            ApplyShortcutColumnResources();

            // The Settings and Setup windows read their geometry when they next open, so storing a
            // validated value here is enough.
            if (IsSaneWindowSize(layout.SettingsWindowWidth, layout.SettingsWindowHeight))
            {
                _currentConfig.SettingsWindowWidth = layout.SettingsWindowWidth;
                _currentConfig.SettingsWindowHeight = layout.SettingsWindowHeight;
                var settingsOnScreen = IsPositionOnAConnectedScreen(layout.SettingsWindowX, layout.SettingsWindowY);
                _currentConfig.SettingsWindowX = settingsOnScreen ? layout.SettingsWindowX : double.NaN;
                _currentConfig.SettingsWindowY = settingsOnScreen ? layout.SettingsWindowY : double.NaN;
            }

            if (IsSaneWindowSize(layout.SetupWindowWidth, layout.SetupWindowHeight))
            {
                _currentConfig.SetupWindowWidth = layout.SetupWindowWidth;
                _currentConfig.SetupWindowHeight = layout.SetupWindowHeight;
                var setupOnScreen = IsPositionOnAConnectedScreen(layout.SetupWindowX, layout.SetupWindowY);
                _currentConfig.SetupWindowX = setupOnScreen ? layout.SetupWindowX : double.NaN;
                _currentConfig.SetupWindowY = setupOnScreen ? layout.SetupWindowY : double.NaN;
            }

            // The main window is open right now, so apply it live as well as storing it.
            var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
            if (IsSaneWindowSize(layout.MainWindowWidth, layout.MainWindowHeight))
            {
                var scale = screen == null || screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                var maxWidth = screen == null ? layout.MainWindowWidth : screen.WorkingArea.Width / scale;
                var maxHeight = screen == null ? layout.MainWindowHeight : screen.WorkingArea.Height / scale;

                var width = Math.Clamp(layout.MainWindowWidth, MinWidth, Math.Max(MinWidth, maxWidth));
                var height = Math.Clamp(layout.MainWindowHeight, MinHeight, Math.Max(MinHeight, maxHeight));

                if (width < layout.MainWindowWidth || height < layout.MainWindowHeight)
                {
                    skipped.Add("the window was made smaller to fit this screen");
                }

                _currentConfig.MainWindowWidth = width;
                _currentConfig.MainWindowHeight = height;
                Width = width;
                Height = height;
            }
            else
            {
                skipped.Add("the saved window size was unusable and was ignored");
            }

            if (IsPositionOnAConnectedScreen(layout.MainWindowX, layout.MainWindowY))
            {
                _currentConfig.MainWindowX = layout.MainWindowX;
                _currentConfig.MainWindowY = layout.MainWindowY;
                Position = new Avalonia.PixelPoint((int)layout.MainWindowX, (int)layout.MainWindowY);
            }
            else if (!double.IsNaN(layout.MainWindowX))
            {
                _currentConfig.MainWindowX = double.NaN;
                _currentConfig.MainWindowY = double.NaN;
                skipped.Add("the saved window position is off this PC's screens, so the window was left where it is");
            }

            // Never restore Minimized: the user would see nothing happen at all.
            var state = layout.MainWindowState ?? "Normal";
            if (string.Equals(state, "Maximized", StringComparison.OrdinalIgnoreCase))
            {
                _currentConfig.MainWindowState = "Maximized";
                WindowState = Avalonia.Controls.WindowState.Maximized;
            }
            else
            {
                _currentConfig.MainWindowState = "Normal";
                if (WindowState == Avalonia.Controls.WindowState.Minimized) WindowState = Avalonia.Controls.WindowState.Normal;
            }

            return skipped.Count == 0
                ? "Window sizes, positions and column widths were restored."
                : "Window layout restored, except: " + string.Join("; ", skipped) + ".";
        }

        private static bool IsSaneWindowSize(double width, double height)
        {
            return !double.IsNaN(width) && !double.IsNaN(height)
                && !double.IsInfinity(width) && !double.IsInfinity(height)
                && width >= 200 && height >= 150
                && width <= 20000 && height <= 20000;
        }

        /// <summary>True when this point falls inside a monitor attached to THIS machine.</summary>
        private bool IsPositionOnAConnectedScreen(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return false;
            if (Math.Abs(x) > 100000 || Math.Abs(y) > 100000) return false;

            try
            {
                var point = new Avalonia.PixelPoint((int)x, (int)y);
                foreach (var screen in this.Screens.All)
                {
                    if (screen.Bounds.Contains(point)) return true;
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("Could not check a restored window position: " + ex.Message);
                return false;
            }

            return false;
        }

        protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
        {
            SaveConfig();
            if (((App)Application.Current!).IsExiting
                || e.CloseReason == WindowCloseReason.ApplicationShutdown
                || e.CloseReason == WindowCloseReason.OSShutdown)
            {
                return;
            }

            e.Cancel = true;
            App.HiddenWindow = this;
            ShowInTaskbar = false;
            HotkeyManager.DisableCaptureHook();
            this.Hide();

            // ISSUE_15: the X button hides the window and leaves the shortcuts live. Say so once,
            // so nobody thinks they closed KeyPulse and then wonders why their keys still fire.
            if (!_currentConfig.HasSeenTrayHint)
            {
                _currentConfig.HasSeenTrayHint = true;
                SaveConfig();
                ShowTrayHint();
            }
        }

        private void ShowTrayHint()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var hintWindow = CreateAppDialog("KeyPulse is still running", 460, 250, 420, 220, WindowStartupLocation.CenterScreen, false, true);

                    var panel = CreateDialogPanel(10);
                    panel.Children.Add(new TextBlock
                    {
                        Text = "KeyPulse is still running",
                        Classes = { "SectionTitle" }
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Closing this window only hides it. Your shortcuts keep working in the background.\n\nTo open it again, click the KeyPulse icon in the system tray next to the clock. To stop KeyPulse completely, right-click that icon and choose \"Exit KeyPulse\".",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    });

                    var okButton = new Button
                    {
                        Content = "Got it",
                        Classes = { "Primary" },
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth = 100
                    };
                    okButton.Click += (s, args) => hintWindow.Close();
                    panel.Children.Add(okButton);

                    hintWindow.Content = panel;
                    hintWindow.Opened += (s, args) => okButton.Focus();
                    hintWindow.Show();
                }
                catch (Exception ex)
                {
                    Program.LogCrash("Failed to show tray hint: " + ex);
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _retryTimer?.Stop();
            _retryTimer = null;

            InputSimulator.TypingStarted -= OnTypingStarted;
            InputSimulator.TypingProgressChanged -= OnTypingProgress;
            InputSimulator.TypingFinished -= OnTypingFinished;

            CloseTypingProgressWindow();
            HotkeyManager.DisableCaptureHook();
            HotkeyManager.Clear(); // release every hotkey back to Windows before the loop stops
            HotkeyManager.Stop();
            base.OnClosed(e);
        }
    }
}









