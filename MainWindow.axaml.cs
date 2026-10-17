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
using Avalonia.Layout;
using Avalonia.Media;
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
        private bool _startHidden;

        /// <summary>
        /// ISSUE_32: set the moment a real actor (tray icon click, second-launch signal,
        /// Explorer's "Add to KeyPulse") asks for the shortcuts window. Only a Show() that
        /// happens WITHOUT this flag may still be re-hidden as part of a --hidden startup.
        /// </summary>
        private bool _userAskedToBeShown;

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
        private readonly List<string> _editorCombos = new();
        private bool _loadingEditorFields;
        private bool _creatingDuplicate;
        private string? _duplicateSourceCombo;
        private bool _showingRiskyShortcutWarning;
        private readonly HashSet<string> _approvedNewRiskyCombos = new(StringComparer.OrdinalIgnoreCase);
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
        private Avalonia.Threading.DispatcherTimer? _actionErrorCloseTimer;
        private bool _actionErrorPointerInside;
        private Action<int, bool, bool, bool, bool>? _rawKeyHandler;

        /// <summary>
        /// ISSUE_19: target staged from Explorer's "Add to KeyPulse" context menu. Everything is
        /// filled in; the user only picks the keys and presses Add.
        /// </summary>
        private string? _stagedAddTarget;

        /// <summary>ISSUE_4: serialises background reachability checks so they never pile up.</summary>
        private readonly System.Threading.SemaphoreSlim _availabilityGate = new(1, 1);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeAvailabilityChecks = new();

        public MainWindow() : this(false)
        {
        }

        public MainWindow(bool startHidden)
        {
            _startHidden = startHidden;
            if (_startHidden) ShowInTaskbar = false;
            this.Opened += MainWindow_Opened;
            
            InitializeComponent();
            if (Program.IsDevMode)
            {
                this.Title = "KeyPulse [DEV SANDBOX]";
            }
            DataContext = this;
            WireShortcutColumnSplitters();
            UpdateActionUi();

            var keyCombo = this.FindControl<TextBox>("KeyCombo");
            if (keyCombo != null)
            {
                keyCombo.GotFocus += (s, e) => HotkeyManager.EnableCaptureHook();
                keyCombo.LostFocus += (s, e) => HotkeyManager.DisableCaptureHook();
            }

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

            _rawKeyHandler = (vk, ctrl, alt, shift, win) =>
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

                    if (!isModifierOnly) parts.Add(HotkeyManager.VirtualKeyToName((uint)vk));

                    var txt = string.Join("+", parts);
                    if (isModifierOnly && parts.Count > 0) txt += "+";

                    keyCombo.Text = txt;
                });
            };
            HotkeyManager.OnRawKey += _rawKeyHandler;

            InputSimulator.TypingStarted += OnTypingStarted;
            InputSimulator.TypingProgressChanged += OnTypingProgress;
            InputSimulator.TypingFinished += OnTypingFinished;

            CryptoHelper.ProtectionFailed += OnTargetProtectionFailed;

            var firstRun = !File.Exists(ConfigPath);
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
            RefreshComboChips();
            StartRegistrationRetryTimer();

            if (firstRun && !Program.IsDevMode)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (Program.QueryStartupState() != Program.StartupState.Off) return;
                        if (Program.SetStartup(true, out var startupError))
                        {
                            Program.LogDebug("First run: enabled the default start-with-Windows launcher.");
                        }
                        else
                        {
                            Program.LogDebug("Could not enable default start-with-Windows: " + startupError);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug("Default start-with-Windows check failed: " + ex.Message);
                    }
                });
            }
        }

        private AppConfig _currentConfig = new AppConfig();

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            ReportStartupProblems();

            ConsumeStagedAdd();

            if (_startHidden && !_userAskedToBeShown)
            {
                Program.LogDebug("Main window shown during hidden startup with no user request; hiding again.");
                App.HiddenWindow = this;
                ShowInTaskbar = false;
                Hide();
                return;
            }

            _startHidden = false;
            Program.LogDebug("Main window opened.");

            this.Topmost = true;
            this.Activate();
            this.Topmost = false;

            VerifyAdminModeIsRealAsync();
        }

        /// <summary>
        /// ISSUE_32: the single sanctioned way to bring the shortcuts window forward. Setting
        /// _userAskedToBeShown BEFORE Show() is what disarms the hidden-startup logic inside
        /// MainWindow_Opened, so the first tray click after a --hidden boot opens the window
        /// instead of flashing and hiding it. A maximized window is left maximized.
        /// </summary>
        public void BringToFront()
        {
            _userAskedToBeShown = true;
            _startHidden = false;
            ShowInTaskbar = true;
            Show();
            if (WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                WindowState = Avalonia.Controls.WindowState.Normal;
            }
            this.Topmost = true;
            Activate();
            this.Topmost = false;
            Program.LogDebug("Main window brought to front by a user request.");
        }

        /// <summary>
        /// ISSUE_8: checks that Administrator mode actually took effect, and says so when it did not.
        ///
        /// The logon task is created with `/RL HIGHEST`, which means "the highest level this account
        /// can reach" - on an account without administrator rights that is ordinary rights. Windows
        /// still reports the task as created and enabled, so Settings said "starts with Windows using
        /// its Administrator logon task" while typing into elevated windows kept failing with no
        /// explanation. Runs off the UI thread because the first call may spawn schtasks.
        /// </summary>
        private async void VerifyAdminModeIsRealAsync()
        {
            try
            {
                var misleading = await System.Threading.Tasks.Task.Run(() =>
                {
                    if (Program.IsAdminTaskInstalled())
                    {
                        Program.EnsureAdminTaskBatterySettings();
                    }
                    return Program.IsAdminModeClaimedButNotEffective();
                });
                if (!misleading) return;

                ShowActionMessage("KeyPulse Administrator mode",
                    "Administrator mode is on, but it did not take effect",
                    "Windows started KeyPulse without Administrator rights even though the Administrator "
                    + "logon task is switched on. This normally means this Windows account cannot be "
                    + "elevated, or company policy blocked it.\n\n"
                    + "Everything else works normally. Typing and pasting into windows that are themselves "
                    + "running as Administrator will keep failing until KeyPulse is started with those rights.",
                    false);
            }
            catch (Exception ex)
            {
                Program.LogDebug("Could not verify Administrator mode: " + ex.Message);
            }
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

            var unreadable = Hotkeys.Count(h => h.TargetUnreadable);
            if (unreadable > 0)
            {
                ShowActionMessage("KeyPulse settings problem",
                    $"{unreadable} shortcut{(unreadable == 1 ? "" : "s")} could not be read",
                    "Their targets were saved by a different Windows account, or the settings file was "
                    + "edited outside KeyPulse. Nothing was deleted: the original values are still stored "
                    + "exactly as they were, and will come back on their own if the settings file returns "
                    + "to the account that created them. To use them here instead, select each affected "
                    + "row and enter its folder, program, link or text again.", true);
            }

            var unprotected = Hotkeys.Count(h => h.TargetProtectionFailed);
            if (CryptoHelper.ProtectionUnavailable || unprotected > 0)
            {
                var subject = unprotected > 0
                    ? $"{unprotected} shortcut{(unprotected == 1 ? " is" : "s are")} stored in readable text"
                    : "Windows could not protect new targets";
                ShowActionMessage("KeyPulse security notice",
                    subject,
                    "Windows refused to encrypt this data for your account (this happens with roaming "
                    + "profiles, a damaged crypto store, or some managed machines). KeyPulse has kept "
                    + "working, but anything you type into a target - especially with the Blur box "
                    + "ticked - is stored UNENCRYPTED in the settings file, readable by anyone who can "
                    + "open it. Affected rows are marked amber in the list.", true);
            }

            var broken = Hotkeys.Where(h => h.IsEnabled && !IsStatus(h, "Active") && !IsStatus(h, "Ready") && !IsStatus(h, "Waiting")).ToList();
            if (broken.Count == 0) return;

            var lines = broken.Take(8).Select(h => "  " + h.ActionTitle + " (" + h.KeyCombinationsDisplay + ") - " + h.RegistrationStatus +
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
            InputSimulator.CharacterDelayMs = NormalizeTypingDelay(loaded.TypingDelayMs);
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
                    var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                    var dipWidth = screen.WorkingArea.Width / scale;
                    var dipHeight = screen.WorkingArea.Height / scale;
                    loaded.MainWindowWidth = Math.Clamp(1640 * dipWidth / 1920.0, MinWidth, Math.Max(MinWidth, dipWidth));
                    loaded.MainWindowHeight = Math.Clamp(975 * dipHeight / 1080.0, MinHeight, Math.Max(MinHeight, dipHeight));
                }
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            if (loaded.MainWindowWidth > 0 && loaded.MainWindowHeight > 0)
            {
                var fitScreen = this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (fitScreen != null)
                {
                    var scale = fitScreen.Scaling <= 0 ? 1.0 : fitScreen.Scaling;
                    loaded.MainWindowWidth = Math.Clamp(loaded.MainWindowWidth, MinWidth, Math.Max(MinWidth, fitScreen.WorkingArea.Width / scale));
                    loaded.MainWindowHeight = Math.Clamp(loaded.MainWindowHeight, MinHeight, Math.Max(MinHeight, fitScreen.WorkingArea.Height / scale));
                }
                Width = loaded.MainWindowWidth;
                Height = loaded.MainWindowHeight;
            }
            if (Enum.TryParse<Avalonia.Controls.WindowState>(loaded.MainWindowState, out var state))
            {
                WindowState = state;
            }

            UpdateSortHeaderText();
            try
            {
                if (Screens != null)
                {
                    Screens.Changed += (s, e) =>
                    {
                        DesktopOrganizerService.HandleDisplayChangeDebounced();
                    };
                }
            }
            catch { }
        }

        /// <summary>ISSUE_9: preset speeds in milliseconds, plus support for custom 1..200 ms delays.</summary>
        private const int TypingDelayFast = 1;
        private const int TypingDelayNormal = 6;
        private const int TypingDelayCompatible = 18;

        private static int NormalizeTypingDelay(int value)
        {
            if (value <= 0) return TypingDelayFast;
            return Math.Clamp(value, 1, 200);
        }

        private static double ClampColumnWidth(double value, double fallback, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) value = fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private const double StatusColumnMin = 110, StatusColumnMax = 260, StatusColumnDefault = 128;
        private const double KeyColumnMin = 130, KeyColumnMax = 300, KeyColumnDefault = 150;
        private const double ActionColumnMin = 120, ActionColumnMax = 280, ActionColumnDefault = 200;
        private const double TargetColumnMin = 180, TargetColumnMax = 1600, TargetColumnDefault = 468;

        /// <summary>
        /// Fixed width of the Actions column: 8px cell padding each side, a 48px toggle, a 46px RUN,
        /// a 76px REMOVE, a 100px DUPLICATE and three 6px gaps = 304 (310 with breathing room).
        /// It is NOT user-resizable and NOT "Auto": the header and the rows are two separate grids.
        /// </summary>
        private const double ActionsColumnWidth = 310;

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
                "ShortcutStatusColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutStatusColumnWidth, StatusColumnDefault, StatusColumnMin, StatusColumnMax),
                "ShortcutKeyColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutKeyColumnWidth, KeyColumnDefault, KeyColumnMin, KeyColumnMax),
                "ShortcutActionColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutActionColumnWidth, ActionColumnDefault, ActionColumnMin, ActionColumnMax),
                "ShortcutTargetColumnWidth" => ClampColumnWidth(_currentConfig.ShortcutTargetColumnWidth, TargetColumnDefault, TargetColumnMin, TargetColumnMax),
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

            if (columns[0].ActualWidth <= 0 || columns[2].ActualWidth <= 0 || columns[4].ActualWidth <= 0 || columns[6].ActualWidth <= 0) return;

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
                if (WindowState == Avalonia.Controls.WindowState.Normal)
                {
                    _currentConfig.MainWindowX = Position.X;
                    _currentConfig.MainWindowY = Position.Y;
                    _currentConfig.MainWindowWidth = Bounds.Width;
                    _currentConfig.MainWindowHeight = Bounds.Height;
                }
                if (WindowState != Avalonia.Controls.WindowState.Minimized)
                {
                    _currentConfig.MainWindowState = WindowState.ToString();
                }

                _currentConfig.ShortcutSortColumn = _sortColumn;
                _currentConfig.ShortcutSortDescending = _sortDescending;

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
                DesktopOrganizerWindowX = source.DesktopOrganizerWindowX,
                DesktopOrganizerWindowY = source.DesktopOrganizerWindowY,
                DesktopOrganizerWindowWidth = source.DesktopOrganizerWindowWidth,
                DesktopOrganizerWindowHeight = source.DesktopOrganizerWindowHeight,
                ScreenBrightnessWindowX = source.ScreenBrightnessWindowX,
                ScreenBrightnessWindowY = source.ScreenBrightnessWindowY,
                ScreenBrightnessWindowWidth = source.ScreenBrightnessWindowWidth,
                ScreenBrightnessWindowHeight = source.ScreenBrightnessWindowHeight,
                UseGoogleChromeForUrls = source.UseGoogleChromeForUrls,
                LaunchOnBoot = source.LaunchOnBoot,
                SoundEnabled = source.SoundEnabled,
                Theme = source.Theme,
                TypingDelayMs = source.TypingDelayMs,
                SettingsWindowX = source.SettingsWindowX,
                SettingsWindowY = source.SettingsWindowY,
                SettingsWindowWidth = source.SettingsWindowWidth,
                SettingsWindowHeight = source.SettingsWindowHeight,
                LastActiveSettingsTab = source.LastActiveSettingsTab,
                HasSeenTrayHint = source.HasSeenTrayHint,
                ShortcutSortColumn = source.ShortcutSortColumn,
                ShortcutSortDescending = source.ShortcutSortDescending,
                IsReadOnlySession = source.IsReadOnlySession
            };
        }

        private static HotkeyEntry CloneHotkeyEntry(HotkeyEntry source)
        {
            var clone = new HotkeyEntry
            {
                Id = source.Id,
                IsEnabled = source.IsEnabled,
                AllowRiskyShortcut = source.AllowRiskyShortcut,
                DisplayName = source.DisplayName,
                KeyCombination = source.KeyCombination,
                Action = source.Action,
                Target = source.Target,
                IsTargetObfuscated = source.IsTargetObfuscated,
                AddToQuickActionMenu = source.AddToQuickActionMenu,
                TargetUnreadable = source.TargetUnreadable,
                UnreadableTargetCipher = source.UnreadableTargetCipher,
                TargetProtectionFailed = source.TargetProtectionFailed
            };
            if (source.KeyCombinations != null && source.KeyCombinations.Count > 0)
            {
                clone.KeyCombinations = new List<string>(source.KeyCombinations);
            }
            return clone;
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
                    blockingStatus = "Inactive: Could not be read";
                    blockingHint = "This shortcut was saved by a different Windows account, or the settings file was edited. Select this row and enter the target again.";
                }
                else if (!ValidateTargetShape(h.Action, h.Target, out var targetError))
                {
                    blockingStatus = "Inactive: " + targetError;
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

                if (h.KeyCombinations.Count == 0)
                {
                    ReleaseRegistration(h);
                    h.RegistrationStatus = "Ready (Manual)";
                    h.StatusHint = "Ready to run manually via [RUN] or from tray Quick Actions.";
                    h.SuggestedForCombo = string.Empty;
                    QueueAvailabilityCheck(h);
                    continue;
                }

                var validCombos = new List<(string original, string normalized)>();
                var rowFormatErrors = new List<string>();
                var rowDuplicateErrors = new List<string>();

                foreach (var combo in h.KeyCombinations)
                {
                    if (string.IsNullOrWhiteSpace(combo)) continue;
                    var trimmed = combo.Trim();
                    if (!ValidateHotkeyFormat(trimmed, false, out var hotkeyError, h.AllowRiskyShortcut))
                    {
                        rowFormatErrors.Add($"{trimmed} ({hotkeyError})");
                        continue;
                    }

                    var norm = NormalizeComboKey(trimmed);
                    if (!claimedCombos.Add(norm))
                    {
                        rowDuplicateErrors.Add($"{trimmed} (Duplicate)");
                        continue;
                    }

                    validCombos.Add((trimmed, norm));
                }

                if (validCombos.Count == 0 && h.KeyCombinations.Count > 0)
                {
                    ReleaseRegistration(h);
                    if (rowFormatErrors.Count > 0)
                    {
                        h.RegistrationStatus = "Inactive: " + rowFormatErrors[0];
                    }
                    else if (rowDuplicateErrors.Count > 0)
                    {
                        h.RegistrationStatus = "Inactive: Duplicate";
                        h.StatusHint = "Another KeyPulse shortcut already uses this combination.";
                    }
                    else
                    {
                        h.RegistrationStatus = "Inactive: No valid keys";
                    }
                    h.SuggestedForCombo = string.Empty;
                    if (h.IsEnabled) failures++;
                    continue;
                }

                var currentNormSet = new HashSet<string>(validCombos.Select(v => v.normalized), StringComparer.OrdinalIgnoreCase);
                var toRemove = h.RegisteredHotkeyIds.Keys.Where(k => !currentNormSet.Contains(k)).ToList();
                foreach (var k in toRemove)
                {
                    if (h.RegisteredHotkeyIds.TryGetValue(k, out var regId) && regId != 0)
                    {
                        HotkeyManager.Unregister(regId);
                    }
                    h.RegisteredHotkeyIds.Remove(k);
                    h.ComboStatuses.Remove(k);
                    h.ComboHints.Remove(k);
                }

                var successCount = 0;
                var conflictCount = 0;
                string? firstConflictCombo = null;

                foreach (var (orig, norm) in validCombos)
                {
                    if (h.RegisteredHotkeyIds.TryGetValue(norm, out var existingId) && existingId != 0)
                    {
                        successCount++;
                        h.ComboStatuses[norm] = "Active";
                        continue;
                    }

                    if (HotkeyManager.Register(orig, () => ExecuteAction(h), out var newId))
                    {
                        h.RegisteredHotkeyIds[norm] = newId;
                        h.ComboStatuses[norm] = "Active";
                        h.ComboHints.Remove(norm);
                        successCount++;
                    }
                    else
                    {
                        h.ComboStatuses[norm] = "Conflict";
                        conflictCount++;
                        firstConflictCombo ??= orig;
                    }
                }

                h.RegisteredHotkeyId = h.RegisteredHotkeyIds.Values.FirstOrDefault();
                h.RegisteredCombo = h.RegisteredHotkeyIds.Keys.FirstOrDefault() ?? string.Empty;

                var totalTargetCombos = h.KeyCombinations.Count;
                if (conflictCount == 0 && rowFormatErrors.Count == 0 && rowDuplicateErrors.Count == 0)
                {
                    h.SuggestedForCombo = string.Empty;
                    RefreshLiveStatus(h);
                }
                else if (successCount > 0)
                {
                    h.RegistrationStatus = $"Partial ({successCount}/{totalTargetCombos})";
                    var issues = new List<string>();
                    if (conflictCount > 0) issues.Add($"{conflictCount} conflict(s)");
                    if (rowDuplicateErrors.Count > 0) issues.Add($"{rowDuplicateErrors.Count} duplicate(s)");
                    if (rowFormatErrors.Count > 0) issues.Add($"{rowFormatErrors.Count} format error(s)");
                    h.StatusHint = $"{successCount} combination(s) active. Issues: {string.Join(", ", issues)}.";
                    failures++;
                }
                else
                {
                    if (conflictCount > 0 && firstConflictCombo != null)
                    {
                        h.RegistrationStatus = "Inactive: Conflict";
                        ApplyConflictHint(h, firstConflictCombo);
                    }
                    else if (rowDuplicateErrors.Count > 0)
                    {
                        h.RegistrationStatus = "Inactive: Duplicate";
                        h.StatusHint = "Another KeyPulse shortcut already uses this combination.";
                    }
                    else
                    {
                        h.RegistrationStatus = "Inactive: Failed to register";
                    }
                    failures++;
                }
            }

            RefreshStatusSummary();
            return failures;
        }

        /// <summary>Hands one row's Win32 hotkeys back, leaving every other row registered.</summary>
        private static void ReleaseRegistration(HotkeyEntry entry)
        {
            if (entry.RegisteredHotkeyIds.Count > 0)
            {
                foreach (var id in entry.RegisteredHotkeyIds.Values)
                {
                    if (id != 0) HotkeyManager.Unregister(id);
                }
                entry.RegisteredHotkeyIds.Clear();
            }

            if (entry.RegisteredHotkeyId != 0)
            {
                HotkeyManager.Unregister(entry.RegisteredHotkeyId);
                entry.RegisteredHotkeyId = 0;
            }

            entry.RegisteredCombo = string.Empty;
            entry.ComboStatuses.Clear();
            entry.ComboHints.Clear();
        }

        /// <summary>Active vs. Waiting for a registered row, without touching the registration.</summary>
        private void RefreshLiveStatus(HotkeyEntry entry)
        {
            if (entry.KeyCombinations.Count == 0)
            {
                entry.RegistrationStatus = "Ready (Manual)";
                entry.StatusHint = "Ready to run manually via [RUN] or from tray Quick Actions.";
            }
            else if (entry.TargetReachable == true)
            {
                entry.RegistrationStatus = "Active";
                entry.StatusHint = string.Empty;
            }
            else
            {
                entry.RegistrationStatus = "Waiting";
                entry.StatusHint = entry.TargetReachable == false
                    ? "The keys are reserved and working. The folder or program is not reachable right now, so KeyPulse will tell you if you press it before it comes back."
                    : "Checking whether the folder or program is reachable...";
            }

            QueueAvailabilityCheck(entry);
        }

        /// <summary>
        /// ISSUE_4: the real reachability check, off the UI thread. Results land back on the UI
        /// thread and only ever update status - a row flips from "Waiting" to "Active" on its own
        /// once the drive or share comes back.
        /// </summary>
        private void QueueAvailabilityCheck(HotkeyEntry entry)
        {
            var action = entry.Action;
            if (action != ActionType.OpenFolder && action != ActionType.LaunchProgram)
            {
                entry.TargetReachable = true;
                return;
            }

            var target = entry.Target;
            if (string.IsNullOrEmpty(target)) return;

            var checkKey = entry.Id + ":" + target;
            if (!_activeAvailabilityChecks.TryAdd(checkKey, 0)) return;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await _availabilityGate.WaitAsync();
                try
                {
                    var reachable = IsTargetAvailableNow(action, target);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyAvailabilityResult(entry, reachable));
                }
                finally
                {
                    _availabilityGate.Release();
                    _activeAvailabilityChecks.TryRemove(checkKey, out _);
                }
            });
        }

        private void ApplyAvailabilityResult(HotkeyEntry entry, bool reachable)
        {
            try
            {
                entry.TargetReachable = reachable;

                if (!entry.IsEnabled) return;

                if (entry.KeyCombinations.Count == 0)
                {
                    if (reachable)
                    {
                        if (!IsStatus(entry, "Ready"))
                        {
                            entry.RegistrationStatus = "Ready (Manual)";
                            entry.StatusHint = "Ready to run manually via [RUN] or from tray Quick Actions.";
                            RefreshStatusSummary();
                        }
                    }
                    else if (!IsStatus(entry, "Waiting"))
                    {
                        entry.RegistrationStatus = "Waiting (Manual)";
                        entry.StatusHint = "The folder or program is not reachable right now.";
                        RefreshStatusSummary();
                    }
                    return;
                }

                if (entry.RegisteredHotkeyIds.Count == 0 && entry.RegisteredHotkeyId == 0) return;

                if (reachable)
                {
                    if (!IsStatus(entry, "Active"))
                    {
                        entry.RegistrationStatus = "Active";
                        entry.StatusHint = string.Empty;
                        RefreshStatusSummary();
                    }
                }
                else if (!IsStatus(entry, "Waiting"))
                {
                    entry.RegistrationStatus = "Waiting";
                    entry.StatusHint = "The keys are reserved and working. The folder or program is not reachable right now, so KeyPulse will tell you if you press it before it comes back.";
                    RefreshStatusSummary();
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("Availability result could not be applied: " + ex.Message);
            }
        }

        /// <summary>
        /// ISSUE_20 / ISSUE_5: never leave the user with a bare "Conflict", but do not recompute the
        /// suggestion on every background retry either. Probing for a free alternative costs up to
        /// sixteen blocking register/unregister round trips per row; doing that for every conflicted
        /// shortcut every thirty seconds, forever, was pure waste.
        /// </summary>
        private static void ApplyConflictHint(HotkeyEntry entry, string conflictingCombo)
        {
            var normalizedCombo = NormalizeComboKey(conflictingCombo);
            if (string.Equals(entry.SuggestedForCombo, normalizedCombo, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.StatusHint))
            {
                return;
            }

            var suggestion = HotkeyManager.SuggestAlternative(conflictingCombo);
            entry.StatusHint = suggestion != null
                ? $"Another app on this PC already owns {conflictingCombo}. {suggestion} is free - try that instead."
                : $"Another app on this PC already owns {conflictingCombo}. Add another modifier key (Shift or Win) and try again.";
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

            var activeBefore = Hotkeys.Count(h => IsStatus(h, "Active") || IsStatus(h, "Ready"));

            ApplyHotkeys();

            var activeAfter = Hotkeys.Count(h => IsStatus(h, "Active") || IsStatus(h, "Ready"));
            if (activeAfter > activeBefore)
            {
                Program.LogDebug($"Background retry activated {activeAfter - activeBefore} shortcut(s).");
            }
        }

        private void RefreshStatusSummary()
        {
            var active = Hotkeys.Count(h => IsStatus(h, "Active") || IsStatus(h, "Ready"));
            var waiting = Hotkeys.Count(h => h.IsEnabled && IsStatus(h, "Waiting"));
            var broken = Hotkeys.Count(h => h.IsEnabled && !IsStatus(h, "Active") && !IsStatus(h, "Ready") && !IsStatus(h, "Waiting"));
            var disabled = Hotkeys.Count(h => !h.IsEnabled);

            App.UpdateTrayStatus(active, broken);
            App.UpdateTrayQuickActions(Hotkeys);
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
                statusText.Text = $"KeyPulse running. {active} working, {broken} not working, {disabled} off. The Status column says why each one is off.";
                statusText.Foreground = AppBrush("AppDangerBrush");
            }
            else if (waiting > 0)
            {
                statusText.Text = $"KeyPulse running. {active} working, {waiting} waiting for a folder or program to come back, {disabled} off.";
                statusText.Foreground = AppBrush("AppWarningBrush");
            }
            else
            {
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
                    _sortColumn = string.Empty;
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

            return (!string.IsNullOrEmpty(entry.DisplayName) && entry.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                || entry.KeyCombinations.Any(c => c.Contains(search, StringComparison.OrdinalIgnoreCase))
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
                    ? query.OrderByDescending(h => h.KeyCombinationsDisplay, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(h => h.KeyCombinationsDisplay, StringComparer.OrdinalIgnoreCase),
                "Action" => _sortDescending
                    ? query.OrderByDescending(h => h.ActionTitle, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(h => h.ActionTitle, StringComparer.OrdinalIgnoreCase),
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
                !Hotkeys.Any(h => h.KeyCombinations.Any(k => string.Equals(NormalizeComboKey(k), NormalizeComboKey(c), StringComparison.OrdinalIgnoreCase)))
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
            UpdateExpandButtonState();
            var actionType = GetSelectedAction();
            var targetLabel = this.FindControl<TextBlock>("TargetLabel");
            var targetHint = this.FindControl<TextBlock>("TargetHintText");
            var targetText = this.FindControl<TextBox>("TargetText");

            if (targetText != null)
            {
                var isTextAction = actionType == ActionType.TypeText || actionType == ActionType.InsertText;
                targetText.AcceptsReturn = isTextAction;
                targetText.TextWrapping = isTextAction ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
                targetText.MinHeight = isTextAction ? 92 : 34;
                targetText.MaxHeight = isTextAction ? 180 : 34;
                targetText.IsEnabled = actionType != ActionType.CycleBrightness;
                if (actionType == ActionType.CycleBrightness && string.IsNullOrWhiteSpace(targetText.Text))
                {
                    targetText.Text = "Cycle Screen Brightness (Lowest -> Highest)";
                }
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
                    ActionType.CycleBrightness => "Screen Brightness cycle behavior",
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
                    ActionType.TypeText => "Types your text one key at a time, like a person would. Slower, but works in old console windows and anywhere that ignores paste. Long runs show a Stop button.",
                    ActionType.InsertText => "Pastes your text instantly with no formatting, then puts your previous clipboard back. Use this unless the target app refuses paste.",
                    ActionType.CycleBrightness => "Cycles your Screen Brightness presets starting from the lowest brightness first, climbing up to the highest, then wrapping back to the lowest. Zero OSD popups in fullscreen games.",
                    _ => string.Empty
                };
            }

            if (this.FindControl<CheckBox>("ObfuscateCheck") is CheckBox obfuscateCheck)
            {
                bool canObfuscate = actionType != ActionType.CycleBrightness;
                obfuscateCheck.IsEnabled = canObfuscate;
                if (!canObfuscate)
                {
                    obfuscateCheck.IsChecked = false;
                }
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
            if (selectedIndex < 0 || selectedIndex > (int)ActionType.CycleBrightness) return ActionType.OpenFolder;
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
            var norm = NormalizeComboKey(combo);
            return !string.IsNullOrWhiteSpace(norm) && _approvedNewRiskyCombos.Contains(norm);
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

            if (actionType == ActionType.CycleBrightness)
            {
                return true;
            }

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

            if (actionType == ActionType.CycleBrightness)
            {
                normalizedTarget = "Cycle Screen Brightness (Lowest -> Highest)";
                return true;
            }

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

        internal static bool TryResolveLaunchTarget(string rawTarget, out string fileName, out string arguments, out string error)
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

            var extensions = new[] { ".exe", ".bat", ".cmd", ".ps1", ".lnk", ".vbs", ".com" };
            foreach (var ext in extensions)
            {
                var extIdx = target.IndexOf(ext + " ", StringComparison.OrdinalIgnoreCase);
                if (extIdx > 0)
                {
                    fileName = target.Substring(0, extIdx + ext.Length);
                    arguments = target.Substring(extIdx + ext.Length + 1).Trim();
                    return true;
                }
                if (target.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = target;
                    arguments = string.Empty;
                    return true;
                }
            }

            var switchIndex = target.IndexOf(" -", StringComparison.Ordinal);
            if (switchIndex < 0) switchIndex = target.IndexOf(" /", StringComparison.Ordinal);
            if (switchIndex > 0)
            {
                fileName = target.Substring(0, switchIndex).Trim();
                arguments = target.Substring(switchIndex + 1).Trim();
                return true;
            }

            var firstSpace = target.IndexOf(' ');
            if (firstSpace > 0)
            {
                var candidate = target.Substring(0, firstSpace);
                if (Path.HasExtension(candidate) || candidate.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0)
                {
                    fileName = candidate;
                    arguments = target.Substring(firstSpace + 1).Trim();
                }
                else
                {
                    fileName = target;
                    arguments = string.Empty;
                }
            }
            else
            {
                fileName = target;
                arguments = string.Empty;
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
                using var chrome = Process.Start(new ProcessStartInfo(chromePath, $"\"{normalizedUrl}\"") { UseShellExecute = true });
                return;
            }

            using var browser = Process.Start(new ProcessStartInfo(normalizedUrl) { UseShellExecute = true });
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

        internal void ExecuteAction(HotkeyEntry entry)
        {
            try
            {
                var triggerDesc = !string.IsNullOrWhiteSpace(entry.KeyCombination) ? entry.KeyCombination : entry.ActionTitle;
                Program.LogDebug($"Executing action {triggerDesc} ({entry.Action}).");
                switch (entry.Action)
                {
                    case ActionType.OpenFolder:
                        {
                            var folder = (entry.Target ?? string.Empty).Trim().Trim('"');
                            if (!SafeDirectoryExists(folder))
                            {
                                MarkEntryWaiting(entry, "The folder is not available right now.");
                                throw new InvalidOperationException(
                                    $"The folder for {triggerDesc} is not available right now:\n{folder}\n\nIf it is on a USB drive, a network share or a VPN, connect it and trigger the action again.");
                            }
                            using var opened = Process.Start("explorer.exe", $"\"{folder}\"");
                            MarkEntryActive(entry);
                            break;
                        }
                    case ActionType.LaunchProgram:
                        if (!TryResolveLaunchTarget(entry.Target, out var fileName, out var arguments, out var launchError))
                        {
                            MarkEntryWaiting(entry, "The program is not available right now.");
                            throw new InvalidOperationException(
                                $"The program for {triggerDesc} could not be found right now ({launchError}):\n{entry.Target}");
                        }
                        {
                            using var launched = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
                            MarkEntryActive(entry);
                        }
                        break;
                    case ActionType.BrowseChrome:
                        LaunchUrl(entry.Target);
                        break;
                    case ActionType.TypeText:
                        if (InputSimulator.IsTyping)
                        {
                            Program.LogDebug($"Refused {triggerDesc}: typing already in progress.");
                            Program.PlaySound("error");
                            ShowActionMessage("KeyPulse is busy",
                                "KeyPulse is still typing the previous snippet",
                                $"Your trigger of {triggerDesc} was received, but it was not run because the earlier text has not finished yet. Try again once this notice is gone.", false);
                            break;
                        }
                        if (!InputSimulator.TypeText(entry.Target, out var typeError, out var typeBusy))
                        {
                            if (typeBusy)
                            {
                                Program.PlaySound("error");
                                ShowActionMessage("KeyPulse is busy",
                                    "KeyPulse is still typing the previous snippet",
                                    $"Your trigger of {triggerDesc} was received, but it was not run because the earlier text has not finished yet. Try again once this notice is gone.", false);
                            }
                            else
                            {
                                throw new InvalidOperationException(typeError);
                            }
                        }
                        break;
                    case ActionType.InsertText:
                        if (!InputSimulator.InsertText(entry.Target, out var insertError, out var insertWarning, out var insertBusy))
                        {
                            if (insertBusy)
                            {
                                Program.PlaySound("error");
                                ShowActionMessage("KeyPulse is busy",
                                    "KeyPulse is still typing or pasting the previous snippet",
                                    $"Your trigger of {triggerDesc} was received, but it was not run because the earlier text has not finished yet. Try again once this notice is gone.", false);
                            }
                            else
                            {
                                throw new InvalidOperationException(insertError);
                            }
                        }
                        if (!string.IsNullOrEmpty(insertWarning))
                        {
                            ShowActionMessage("KeyPulse clipboard notice", "Your previous clipboard could not be restored", insertWarning, false);
                        }
                        break;
                    case ActionType.CycleBrightness:
                        ScreenBrightnessService.TriggerShortcutCycleStep();
                        break;
                }
                Program.LogDebug($"Action {triggerDesc} completed.");
            }
            catch (Exception ex)
            {
                var triggerDesc = !string.IsNullOrWhiteSpace(entry.KeyCombination) ? entry.KeyCombination : entry.ActionTitle;
                Program.LogCrash($"Action failed ({triggerDesc}, {entry.Action}): {ex}");
                ShowActionMessage("KeyPulse Action Failed", "That action could not run", ex.Message, true);
            }
        }

        public void Run_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is not HotkeyEntry entry) return;
            Task.Run(async () =>
            {
                try
                {
                    if (entry.Action == ActionType.TypeText || entry.Action == ActionType.InsertText)
                    {
                        await Task.Delay(150);
                    }
                    ExecuteAction(entry);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"Manual execution of {entry.ActionTitle} failed: {ex.Message}");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ShowActionMessage("Run failed", entry.ActionTitle, ex.Message, isError: true);
                    });
                }
            });
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
                    window.ShowActivated = false;
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
                        Text = "Click Stop to cancel. Escape also cancels while this window is the one you are focused on.",
                        Classes = { "Muted" },
                        FontSize = 11,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    };

                    var cancelButton = new Button
                    {
                        Content = "Stop typing",
                        Classes = { "Danger" },
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth = 110
                    };
                    cancelButton.Click += (s, e) => InputSimulator.CancelTyping();

                    window.KeyDown += (s, e) =>
                    {
                        if (e.Key == Avalonia.Input.Key.Escape)
                        {
                            InputSimulator.CancelTyping();
                            e.Handled = true;
                        }
                    };

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

            if (done != total && done % 10 != 0) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_typingProgressBar != null) _typingProgressBar.Value = done;
                if (_typingProgressText != null) _typingProgressText.Text = $"Typing {done} of {total} characters...";
            });
        }

        private void OnTypingFinished(bool cancelled, int sent, int total)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CloseTypingProgressWindow();
                if (!cancelled) return;

                Program.PlaySound("close");
                ShowActionMessage("KeyPulse typing stopped",
                    "Typing was stopped part-way through",
                    $"{sent} of {total} characters were typed. The remaining text was NOT sent, so you may want to finish or delete what landed in the document.", false);
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
                        RestartActionNoticeTimer();
                        return;
                    }

                    _actionErrorRepeatCount = 1;

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

                    errWin.PointerEntered += (s, e) => _actionErrorPointerInside = true;
                    errWin.PointerExited += (s, e) => _actionErrorPointerInside = false;

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
                        StopActionNoticeTimer();
                    };

                    _actionErrorWindow = errWin;
                    errWin.Show();
                    RestartActionNoticeTimer();
                }
                catch (Exception ex)
                {
                    Program.LogCrash($"Failed to show shortcut action message: {ex}");
                }
            });
        }

        private void RestartActionNoticeTimer()
        {
            _actionErrorCloseTimer?.Stop();
            _actionErrorCloseTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _actionErrorCloseTimer.Tick += (s, e) =>
            {
                if (_actionErrorPointerInside)
                {
                    return;
                }
                _actionErrorCloseTimer?.Stop();
                try { _actionErrorWindow?.Close(); } catch { }
            };
            _actionErrorCloseTimer.Start();
        }

        private void StopActionNoticeTimer()
        {
            _actionErrorCloseTimer?.Stop();
            _actionErrorCloseTimer = null;
            _actionErrorPointerInside = false;
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

            if (!ValidateTargetShape(actionType, target, out var targetError))
            {
                SetFieldError("TargetText", targetError);
                SetFieldWarning("TargetText", null);
            }
            else
            {
                SetFieldError("TargetText", null);
                SetFieldWarning("TargetText", null);
            }
        }

        private async System.Threading.Tasks.Task<bool> CommitSelectedEditorChangeAsync()
        {
            if (_loadingEditorFields || _editingEntry == null) return false;

            var keyText = this.FindControl<TextBox>("KeyCombo")?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(keyText))
            {
                if (keyText.EndsWith("+", StringComparison.Ordinal))
                {
                    SetFieldError("KeyCombo", "Enter a complete shortcut");
                    Program.PlaySound("error");
                    return false;
                }

                var normalizedNewCombo = NormalizeComboKey(keyText);
                if (!_editorCombos.Any(c => string.Equals(NormalizeComboKey(c), normalizedNewCombo, StringComparison.OrdinalIgnoreCase)))
                {
                    _editorCombos.Add(keyText);
                }
            }

            foreach (var combo in _editorCombos)
            {
                var norm = NormalizeComboKey(combo);
                var duplicate = Hotkeys.FirstOrDefault(h => !ReferenceEquals(h, _editingEntry) &&
                    h.KeyCombinations.Any(c => string.Equals(NormalizeComboKey(c), norm, StringComparison.OrdinalIgnoreCase)));
                if (duplicate != null)
                {
                    SetFieldError("KeyCombo", $"Shortcut '{combo}' is already in use");
                    Program.PlaySound("error");
                    return false;
                }
            }

            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            if (actionCombo == null || actionCombo.SelectedIndex < 0 || actionCombo.SelectedIndex > (int)ActionType.CycleBrightness)
            {
                SetFieldError("TargetText", "Choose an action");
                Program.PlaySound("error");
                return false;
            }

            var hasAnyRiskyCombo = false;
            foreach (var combo in _editorCombos)
            {
                if (!await EnsureRiskyShortcutApprovedAsync(combo))
                {
                    return false;
                }

                if (IsRiskyTypingShortcut(combo, out _))
                {
                    hasAnyRiskyCombo = true;
                }

                var normCombo = NormalizeComboKey(combo);
                var alreadyOwnedByThisEntry = _editingEntry.RegisteredHotkeyIds.ContainsKey(normCombo)
                    || (_editingEntry.RegisteredHotkeyId != 0
                        && string.Equals(NormalizeComboKey(_editingEntry.RegisteredCombo), normCombo, StringComparison.OrdinalIgnoreCase));

                if (!ValidateHotkeyFormat(combo, !alreadyOwnedByThisEntry, out var formatError, _editingEntry.AllowRiskyShortcut || IsNewRiskyComboApproved(combo)))
                {
                    SetFieldError("KeyCombo", formatError ?? $"Invalid shortcut '{combo}'");
                    Program.PlaySound("error");
                    return false;
                }
            }

            _editingEntry.AllowRiskyShortcut = hasAnyRiskyCombo;

            var newAction = (ActionType)actionCombo.SelectedIndex;
            var rawTarget = this.FindControl<TextBox>("TargetText")?.Text ?? string.Empty;

            var normalizedTarget = string.Empty;
            string targetError = string.Empty, targetWarning = string.Empty;
            var targetValid = await System.Threading.Tasks.Task.Run(() =>
                ValidateTarget(newAction, rawTarget, out normalizedTarget, out targetError, out targetWarning));

            if (!targetValid)
            {
                SetFieldError("TargetText", targetError);
                SetFieldWarning("TargetText", null);
                Program.PlaySound("error");
                return false;
            }

            SetFieldError("TargetText", null);
            SetFieldWarning("TargetText", targetWarning);

            var customName = this.FindControl<TextBox>("ActionNameText")?.Text?.Trim() ?? string.Empty;
            _editingEntry.DisplayName = customName;
            _editingEntry.KeyCombinations = new List<string>(_editorCombos);
            _editingEntry.Action = newAction;
            _editingEntry.Target = normalizedTarget;
            _editingEntry.TargetUnreadable = false;
            _editingEntry.UnreadableTargetCipher = string.Empty;
            _editingEntry.TargetProtectionFailed = false;
            _editingEntry.IsTargetObfuscated = this.FindControl<CheckBox>("ObfuscateCheck")?.IsChecked ?? false;
            _editingEntry.AddToQuickActionMenu = this.FindControl<CheckBox>("QuickActionCheck")?.IsChecked ?? false;

            ApplyHotkeys();
            RefreshVisibleHotkeys();
            SaveConfig();
            UpdateEditorModeText();
            return true;
        }

        private async System.Threading.Tasks.Task<bool> EnsureRiskyShortcutApprovedAsync(string combo)
        {
            if (_loadingEditorFields) return true;
            if (!IsRiskyTypingShortcut(combo, out var riskyReason))
            {
                return true;
            }

            if (_editingEntry?.AllowRiskyShortcut == true || IsNewRiskyComboApproved(combo)) return true;

            if (await ConfirmRiskyShortcutOverrideAsync(combo, riskyReason))
            {
                _approvedNewRiskyCombos.Add(NormalizeComboKey(combo));
                if (_editingEntry != null) _editingEntry.AllowRiskyShortcut = true;
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

            _approvedNewRiskyCombos.Remove(NormalizeComboKey(combo));
            SetFieldError("KeyCombo", "Choose a different shortcut");
            return false;
        }

        private void RefreshComboChips()
        {
            if (this.FindControl<WrapPanel>("ComboChipsPanel") is not WrapPanel panel) return;
            panel.Children.Clear();

            if (_editorCombos.Count == 0)
            {
                panel.IsVisible = false;
                return;
            }

            panel.IsVisible = true;
            foreach (var combo in _editorCombos)
            {
                var currentCombo = combo;
                var chip = new Border
                {
                    Classes = { "TagBadge" },
                    Margin = new Thickness(0, 2, 6, 2),
                    Padding = new Thickness(8, 3, 6, 3),
                    CornerRadius = new CornerRadius(4),
                    Background = AppBrush("AppSurfaceHoverBrush"),
                    BorderBrush = AppBrush("AppBorderSubtleBrush"),
                    BorderThickness = new Thickness(1)
                };

                var sp = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };

                var text = new TextBlock
                {
                    Text = currentCombo,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    FontSize = 12
                };

                var removeBtn = new Button
                {
                    Content = "✕",
                    Padding = new Thickness(2, 0, 2, 0),
                    Margin = new Thickness(2, 0, 0, 0),
                    MinHeight = 16,
                    MinWidth = 16,
                    FontSize = 11,
                    Background = Avalonia.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };
                ToolTip.SetTip(removeBtn, $"Remove {currentCombo}");

                removeBtn.Click += (s, e) =>
                {
                    _editorCombos.Remove(currentCombo);
                    RefreshComboChips();
                    UpdateEditorModeText();
                };

                sp.Children.Add(text);
                sp.Children.Add(removeBtn);
                chip.Child = sp;
                panel.Children.Add(chip);
            }
        }

        public async void AddKey_Click(object? sender, RoutedEventArgs e)
        {
            var keyBox = this.FindControl<TextBox>("KeyCombo");
            var combo = keyBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(combo) || combo.EndsWith("+", StringComparison.Ordinal))
            {
                SetFieldError("KeyCombo", "Press a complete shortcut combination first");
                Program.PlaySound("error");
                return;
            }

            var normalized = NormalizeComboKey(combo);
            if (_editorCombos.Any(c => string.Equals(NormalizeComboKey(c), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                SetFieldError("KeyCombo", "This combination is already added to this action");
                Program.PlaySound("error");
                return;
            }

            if (Hotkeys.Any(h => !ReferenceEquals(h, _editingEntry) &&
                h.KeyCombinations.Any(c => string.Equals(NormalizeComboKey(c), normalized, StringComparison.OrdinalIgnoreCase))))
            {
                SetFieldError("KeyCombo", "Shortcut already exists in KeyPulse");
                Program.PlaySound("error");
                return;
            }

            if (!await EnsureRiskyShortcutApprovedAsync(combo))
            {
                Program.PlaySound("error");
                return;
            }

            var allowRiskyShortcut = (_editingEntry?.AllowRiskyShortcut == true) || IsNewRiskyComboApproved(combo);
            if (!ValidateHotkeyFormat(combo, false, out var hotkeyError, allowRiskyShortcut))
            {
                SetFieldError("KeyCombo", hotkeyError);
                Program.PlaySound("error");
                return;
            }

            _editorCombos.Add(combo);
            RefreshComboChips();
            ClearFieldErrors();
            if (keyBox != null) keyBox.Text = string.Empty;
            UpdateEditorModeText();
            Program.PlaySound("click");
        }

        public async void Add_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            ClearFieldErrors();
            var combo = this.FindControl<TextBox>("KeyCombo")?.Text?.Trim() ?? string.Empty;
            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            var isEditing = _editingEntry != null;

            if (isEditing)
            {
                if (await CommitSelectedEditorChangeAsync())
                {
                    ResetEditor();
                }
                return;
            }

            if (actionCombo == null || actionCombo.SelectedIndex < 0)
            {
                SetFieldError("TargetText", "Choose an action");
                Program.PlaySound("error");
                return;
            }

            if (!string.IsNullOrWhiteSpace(combo))
            {
                if (combo.EndsWith("+", StringComparison.Ordinal))
                {
                    SetFieldError("KeyCombo", "Enter a complete shortcut");
                    Program.PlaySound("error");
                    return;
                }

                var normalizedNewCombo = NormalizeComboKey(combo);
                if (!_editorCombos.Any(c => string.Equals(NormalizeComboKey(c), normalizedNewCombo, StringComparison.OrdinalIgnoreCase)))
                {
                    _editorCombos.Add(combo);
                }
            }

            foreach (var c in _editorCombos)
            {
                var norm = NormalizeComboKey(c);
                if (Hotkeys.Any(h => !ReferenceEquals(h, _editingEntry) &&
                    h.KeyCombinations.Any(existing => string.Equals(NormalizeComboKey(existing), norm, StringComparison.OrdinalIgnoreCase))))
                {
                    SetFieldError("KeyCombo", $"Shortcut '{c}' already exists in KeyPulse");
                    Program.PlaySound("error");
                    return;
                }
            }

            var allowRiskyShortcut = false;
            foreach (var c in _editorCombos)
            {
                var isApproved = IsNewRiskyComboApproved(c);
                if (!ValidateHotkeyFormat(c, !isEditing, out var hotkeyError, isApproved))
                {
                    if (!IsRiskyTypingShortcut(c, out _))
                    {
                        SetFieldError("KeyCombo", hotkeyError);
                        Program.PlaySound("error");
                        return;
                    }
                }

                if (!await EnsureRiskyShortcutApprovedAsync(c))
                {
                    Program.PlaySound("error");
                    return;
                }

                isApproved = IsNewRiskyComboApproved(c);
                if (!ValidateHotkeyFormat(c, !isEditing, out hotkeyError, isApproved))
                {
                    SetFieldError("KeyCombo", hotkeyError);
                    Program.PlaySound("error");
                    return;
                }
                if (isApproved) allowRiskyShortcut = true;
            }

            var actionType = (ActionType)actionCombo.SelectedIndex;

            var normalizedTarget = string.Empty;
            string targetError = string.Empty, targetWarning = string.Empty;
            var targetValid = await System.Threading.Tasks.Task.Run(() =>
                ValidateTarget(actionType, target, out normalizedTarget, out targetError, out targetWarning));

            if (!targetValid)
            {
                SetFieldError("TargetText", targetError);
                Program.PlaySound("error");
                return;
            }

            var customName = this.FindControl<TextBox>("ActionNameText")?.Text?.Trim() ?? string.Empty;
            var entry = new HotkeyEntry
            {
                DisplayName = customName,
                KeyCombinations = new List<string>(_editorCombos),
                Action = actionType,
                Target = normalizedTarget,
                IsEnabled = true,
                AllowRiskyShortcut = allowRiskyShortcut,
                IsTargetObfuscated = this.FindControl<CheckBox>("ObfuscateCheck")?.IsChecked ?? false,
                AddToQuickActionMenu = this.FindControl<CheckBox>("QuickActionCheck")?.IsChecked ?? false
            };

            Hotkeys.Add(entry);
            ApplyHotkeys();

            if (!IsStatus(entry, "Active") && !IsStatus(entry, "Waiting") && !IsStatus(entry, "Ready"))
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
            _stagedAddTarget = null;
            ResetEditor();
            if (this.FindControl<ListBox>("HotkeyList") is ListBox list)
            {
                list.ScrollIntoView(entry);
            }
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

            _editorCombos.Clear();
            if (entry.KeyCombinations != null && entry.KeyCombinations.Count > 0)
            {
                _editorCombos.AddRange(entry.KeyCombinations);
            }
            RefreshComboChips();

            _loadingEditorFields = true;
            try
            {
                if (this.FindControl<TextBox>("ActionNameText") is TextBox actionNameText) actionNameText.Text = entry.DisplayName ?? string.Empty;
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = string.Empty;
                if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = (int)entry.Action;
                if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = entry.Target;
                if (this.FindControl<CheckBox>("ObfuscateCheck") is CheckBox obfuscateCheck) obfuscateCheck.IsChecked = entry.IsTargetObfuscated;
                if (this.FindControl<CheckBox>("QuickActionCheck") is CheckBox quickActionCheck) quickActionCheck.IsChecked = entry.AddToQuickActionMenu;
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

            ShowEntryStatusInEditor(entry);
        }

        private void ShowEntryStatusInEditor(HotkeyEntry entry)
        {
            ClearFieldErrors();

            if (entry.RegistrationStatus.StartsWith("Inactive:", StringComparison.OrdinalIgnoreCase))
            {
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

            if (_stagedAddTarget != null && _editingEntry == null)
            {
                editText.Text = "Adding from Explorer: the action and target are filled in. Press the keys you want in the Shortcut box (or leave empty for manual trigger), then press Add.";
                editText.IsVisible = true;
                return;
            }

            if (_creatingDuplicate)
            {
                editText.Text = string.IsNullOrWhiteSpace(_duplicateSourceCombo)
                    ? "Duplicating action. Choose new keyboard triggers (or leave empty), then add it."
                    : $"Duplicating {_duplicateSourceCombo}. Choose new keyboard triggers (or leave empty), then add it.";
                editText.IsVisible = true;
                return;
            }

            if (_editingEntry == null)
            {
                editText.Text = string.Empty;
                editText.IsVisible = false;
                return;
            }

            editText.Text = $"Editing {_editingEntry.ActionTitle} ({_editingEntry.KeyCombinationsDisplay}) - {_editingEntry.ActionDisplay}. Click Save to apply changes.";
            editText.IsVisible = true;
        }

        public void CancelEdit_Click(object? sender, RoutedEventArgs e)
        {
            _stagedAddTarget = null;
            ResetEditor();
        }

        private void ResetEditor()
        {
            ClearRowEditingExcept();
            _editingEntry = null;
            _creatingDuplicate = false;
            _duplicateSourceCombo = null;
            _approvedNewRiskyCombos.Clear();

            _editorCombos.Clear();
            RefreshComboChips();

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
                if (this.FindControl<TextBox>("ActionNameText") is TextBox actionNameText) actionNameText.Text = "";
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = "";
                if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = 0;
                if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = "";
                if (this.FindControl<CheckBox>("ObfuscateCheck") is CheckBox obfuscateCheck) obfuscateCheck.IsChecked = false;
                if (this.FindControl<CheckBox>("QuickActionCheck") is CheckBox quickActionCheck) quickActionCheck.IsChecked = false;
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
            if (sender is Button btn && btn.DataContext is HotkeyEntry entry)
            {
                if (!await ConfirmRemoveAsync(entry)) return;
                Program.PlaySound("close");
                if (ReferenceEquals(_editingEntry, entry) || (_creatingDuplicate && entry.IsEditing)) ResetEditor();
                ReleaseRegistration(entry);
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
            _duplicateSourceCombo = source.ActionTitle;
            _approvedNewRiskyCombos.Clear();

            _editorCombos.Clear();
            RefreshComboChips();

            _loadingEditorFields = true;
            try
            {
                if (this.FindControl<TextBox>("ActionNameText") is TextBox actionNameText)
                {
                    actionNameText.Text = string.IsNullOrWhiteSpace(source.DisplayName) ? string.Empty : $"{source.DisplayName} (Copy)";
                }
                if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = string.Empty;
                if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = (int)source.Action;
                if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = source.Target;
                if (this.FindControl<CheckBox>("ObfuscateCheck") is CheckBox obfuscateCheck) obfuscateCheck.IsChecked = source.IsTargetObfuscated;
                if (this.FindControl<CheckBox>("QuickActionCheck") is CheckBox quickActionCheck) quickActionCheck.IsChecked = source.AddToQuickActionMenu;
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
                Text = $"{entry.ActionTitle} ({entry.KeyCombinationsDisplay}) - {entry.ActionDisplay}",
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
            if (e.Key == Avalonia.Input.Key.Tab)
            {
                e.Handled = false;
                return;
            }

            if (e.Key == Avalonia.Input.Key.Escape || e.Key == Avalonia.Input.Key.Delete || e.Key == Avalonia.Input.Key.Back)
            {
                if (sender is TextBox clearBox) clearBox.Text = string.Empty;
                SetFieldError("KeyCombo", null);
                e.Handled = true;
                return;
            }

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

        public void ScreenBrightness_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            ScreenBrightnessWindow.OpenWindow(this);
        }

        public void DesktopOrganizer_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            DesktopOrganizerWindow.OpenOrganizer(this);
        }

        private static Border CreateSettingsHelpBadge(string tooltipText)
        {
            var badge = new Border
            {
                Background = AppBrush("AppPanelRaisedBrush"),
                BorderBrush = AppBrush("AppBorderBrush"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(9),
                Width = 18,
                Height = 18,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Help),
                Child = new TextBlock
                {
                    Text = "?",
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = AppBrush("AppTextMutedBrush")
                }
            };
            ToolTip.SetTip(badge, tooltipText);
            return badge;
        }

        private async System.Threading.Tasks.Task<bool> ConfirmSettingsActionAsync(Window owner, string title, string message, string confirmButtonText, bool isDanger = true)
        {
            var dialog = CreateAppDialog(title, 460, 250, 420, 210, WindowStartupLocation.CenterOwner, false, true);
            dialog.SizeToContent = SizeToContent.Height;
            var p = CreateDialogPanel(12);
            p.Children.Add(new TextBlock
            {
                Text = title,
                Classes = { "SectionTitle" },
                Foreground = isDanger ? AppBrush("AppWarningBrush") : AppBrush("AppTextPrimaryBrush")
            });
            p.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 8)
            });

            var btnRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 10
            };
            var confirmed = false;
            var cancelBtn = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90, IsCancel = true };
            var okBtn = new Button { Content = confirmButtonText, Classes = { isDanger ? "Danger" : "Primary" }, MinWidth = 130, IsDefault = true };
            cancelBtn.Click += (_, _) => { confirmed = false; dialog.Close(); };
            okBtn.Click += (_, _) => { confirmed = true; dialog.Close(); };

            dialog.KeyDown += (_, ke) =>
            {
                if (ke.Key == Avalonia.Input.Key.Escape)
                {
                    ke.Handled = true;
                    confirmed = false;
                    dialog.Close();
                }
            };

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            p.Children.Add(btnRow);
            dialog.Content = p;

            if (owner.IsVisible)
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                await ShowDialogOrWindowAsync(dialog);
            }
            return confirmed;
        }

        public void Settings_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");

            double settingsW = _currentConfig.SettingsWindowWidth;
            double settingsH = _currentConfig.SettingsWindowHeight;
            if (double.IsNaN(settingsW) || settingsW < 520) settingsW = 640;
            if (double.IsNaN(settingsH) || settingsH < 480) settingsH = 680;

            if (double.IsNaN(_currentConfig.SettingsWindowX))
            {
                var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (screen != null)
                {
                    var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                    var dipWidth = screen.WorkingArea.Width / scale;
                    var dipHeight = screen.WorkingArea.Height / scale;
                    settingsW = Math.Clamp(640 * dipWidth / 1920.0, 520, Math.Max(520, dipWidth));
                    settingsH = Math.Clamp(680 * dipHeight / 1080.0, 480, Math.Max(480, dipHeight));
                }
            }

            var w = CreateAppDialog("Settings", settingsW, settingsH, 520, 480, WindowStartupLocation.CenterOwner);
            if (!double.IsNaN(_currentConfig.SettingsWindowX) && !double.IsNaN(_currentConfig.SettingsWindowY) &&
                IsPositionOnAConnectedScreen(_currentConfig.SettingsWindowX, _currentConfig.SettingsWindowY))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Position = new Avalonia.PixelPoint((int)_currentConfig.SettingsWindowX, (int)_currentConfig.SettingsWindowY);
            }

            int activeTab = Math.Clamp(_currentConfig.LastActiveSettingsTab, 0, 3);
            bool suppressSyncEvents = false;

            var rootGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
                Margin = new Avalonia.Thickness(20, 16)
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Avalonia.Thickness(0, 0, 0, 12)
            };
            var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock { Text = "Settings & Preferences", Classes = { "SectionTitle" }, FontSize = 20 });
            titleStack.Children.Add(new TextBlock { Text = "Customize startup, appearance, typing speed, displays, and backups.", Classes = { "Muted" }, FontSize = 11.5 });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var searchBox = new TextBox
            {
                Width = 230,
                PlaceholderText = "Search settings...",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TabIndex = 1,
                IsTabStop = true
            };
            ToolTip.SetTip(searchBox, "Type any keyword (e.g. Chrome, theme, delay, brightness, backup, admin) to filter all settings instantly.");
            Grid.SetColumn(searchBox, 1);
            headerGrid.Children.Add(searchBox);
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            var tabBarBorder = new Border
            {
                Classes = { "Panel" },
                Padding = new Avalonia.Thickness(8, 6),
                Margin = new Avalonia.Thickness(0, 0, 0, 12)
            };
            var tabBarGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var tabButtonsPanel = new WrapPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            string[] tabNames = { "General", "Shortcuts & Typing", "Displays & Desktop", "Backup & Reset" };
            var tabButtons = new Button[tabNames.Length];
            for (int i = 0; i < tabNames.Length; i++)
            {
                var btn = new Button
                {
                    Content = tabNames[i],
                    Classes = { i == activeTab ? "Primary" : "Secondary" },
                    Margin = new Avalonia.Thickness(0, 0, 6, 0),
                    Padding = new Avalonia.Thickness(12, 6),
                    FontSize = 12,
                    TabIndex = 2 + i,
                    IsTabStop = true
                };
                tabButtons[i] = btn;
                tabButtonsPanel.Children.Add(btn);
            }
            Grid.SetColumn(tabButtonsPanel, 0);
            tabBarGrid.Children.Add(tabButtonsPanel);

            var restoreTabDefaultsBtn = new Button
            {
                Content = "Restore Tab Defaults",
                Classes = { "Secondary" },
                Padding = new Avalonia.Thickness(10, 5),
                FontSize = 11.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TabIndex = 6,
                IsTabStop = true
            };
            ToolTip.SetTip(restoreTabDefaultsBtn, "Reset only the settings on this current tab back to their original out-of-the-box defaults.");
            Grid.SetColumn(restoreTabDefaultsBtn, 1);
            tabBarGrid.Children.Add(restoreTabDefaultsBtn);
            tabBarBorder.Child = tabBarGrid;
            Grid.SetRow(tabBarBorder, 1);
            rootGrid.Children.Add(tabBarBorder);

            var cardsStack = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(0, 0, 4, 0) };
            var settingCards = new List<(Border Card, int TabCategory, string Keywords)>();

            Border RegisterSettingCard(int tabCategory, string title, string tooltip, string keywords, Control bodyContent, bool isDangerZone = false)
            {
                var card = new Border
                {
                    Classes = { "Panel" },
                    Background = AppBrush("AppPanelRaisedBrush"),
                    BorderBrush = AppBrush(isDangerZone ? "AppDangerBrush" : "AppBorderBrush"),
                    BorderThickness = new Avalonia.Thickness(isDangerZone ? 1.5 : 1),
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Padding = new Avalonia.Thickness(14, 12)
                };

                var stack = new StackPanel { Spacing = 8 };
                var titleRow = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                titleRow.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 13.5,
                    Foreground = isDangerZone ? AppBrush("AppDangerBrush") : AppBrush("AppTextPrimaryBrush"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                titleRow.Children.Add(CreateSettingsHelpBadge(tooltip));
                stack.Children.Add(titleRow);
                stack.Children.Add(bodyContent);
                card.Child = stack;

                settingCards.Add((card, tabCategory, (title + " " + keywords + " " + tooltip).ToLowerInvariant()));
                cardsStack.Children.Add(card);
                return card;
            }


            var chk = new CheckBox
            {
                Content = "Launch KeyPulse at Windows startup",
                IsEnabled = false,
                TabIndex = 10,
                IsTabStop = true
            };
            ToolTip.SetTip(chk, "Automatically start KeyPulse hidden in your system tray whenever you sign in to Windows.");
            var startupStatus = new TextBlock
            {
                Text = "Checking your Windows startup setting...",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            var suppressStartupEvent = false;
            chk.IsCheckedChanged += async (s, ev) =>
            {
                if (suppressStartupEvent || suppressSyncEvents) return;

                var requestedState = chk.IsChecked == true;
                chk.IsEnabled = false;
                startupStatus.Text = requestedState ? "Turning on..." : "Turning off...";
                startupStatus.Foreground = AppBrush("AppTextMutedBrush");

                string startupError = string.Empty;
                var ok = await System.Threading.Tasks.Task.Run(() => Program.SetStartup(requestedState, out startupError));

                if (ok)
                {
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
            var startupBody = new StackPanel { Spacing = 4 };
            startupBody.Children.Add(chk);
            startupBody.Children.Add(startupStatus);
            RegisterSettingCard(0, "Windows Startup",
                "Controls whether KeyPulse launches automatically in the background when you log in to Windows so your hotkeys are always ready.",
                "startup boot launch windows login automatic tray",
                startupBody);

            var themeCombo = new ComboBox
            {
                Width = 220,
                MaxWidth = 240,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                TabIndex = 11,
                IsTabStop = true,
                ItemsSource = new List<string> { "Match Windows (Default)", "Light", "Dark" }
            };
            ToolTip.SetTip(themeCombo, "Choose whether KeyPulse follows your Windows color mode or stays in Light or Dark mode.");
            themeCombo.SelectedIndex = _currentConfig.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };
            themeCombo.SelectionChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                _currentConfig.Theme = themeCombo.SelectedIndex switch
                {
                    1 => "Light",
                    2 => "Dark",
                    _ => "System"
                };
                App.ApplyTheme(_currentConfig.Theme);
                SaveConfig();
            };
            var themeBody = new StackPanel { Spacing = 4 };
            themeBody.Children.Add(themeCombo);
            themeBody.Children.Add(new TextBlock
            {
                Text = "Switches all KeyPulse windows and dialogs between Light and Dark color schemes immediately.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            RegisterSettingCard(0, "Appearance & Theme",
                "Changes the visual color theme across KeyPulse. 'Match Windows' automatically switches between Light and Dark based on your Windows system settings.",
                "appearance theme dark light mode color windows",
                themeBody);

            var soundChk = new CheckBox
            {
                Content = "Play app sound effects",
                IsChecked = _currentConfig.SoundEnabled,
                TabIndex = 12,
                IsTabStop = true
            };
            ToolTip.SetTip(soundChk, "Play subtle audio feedback when clicking buttons, adding/removing shortcuts, cycling screen brightness, or hitting an error.");
            soundChk.IsCheckedChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                _currentConfig.SoundEnabled = soundChk.IsChecked == true;
                Program.SoundEnabled = _currentConfig.SoundEnabled;
                SaveConfig();
                if (_currentConfig.SoundEnabled) Program.PlaySound("click");
            };
            var soundBody = new StackPanel { Spacing = 4 };
            soundBody.Children.Add(soundChk);
            soundBody.Children.Add(new TextBlock
            {
                Text = "Plays subtle audio chimes for button clicks, shortcut changes, brightness presets, layout actions, and error alerts.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            RegisterSettingCard(0, "Sound Effects",
                "Turns all KeyPulse sound effects on or off, including button clicks, brightness step chimes, desktop layout notifications, and error alerts.",
                "sound audio effects mute click chime volume",
                soundBody);

            var updateStatus = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            var updateBtn = new Button
            {
                Content = "Check for Updates",
                Classes = { "Secondary" },
                Width = 220,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TabIndex = 13,
                IsTabStop = true
            };
            ToolTip.SetTip(updateBtn, "Check GitHub for a newer release of KeyPulse.");
            var updateButtonOpensDownloadPage = false;
            string? localUpdateExePath = null;
            updateBtn.Click += async (s, ev) =>
            {
                if (updateButtonOpensDownloadPage)
                {
                    var targetToOpen = !string.IsNullOrWhiteSpace(localUpdateExePath) ? localUpdateExePath : Program.ReleasesPageUrl;
                    try { using var page = Process.Start(new ProcessStartInfo(targetToOpen) { UseShellExecute = true }); }
                    catch (Exception ex) { updateStatus.Text = "Could not open the update target: " + ex.Message; }
                    return;
                }

                updateBtn.IsEnabled = false;
                var isDevUpdate = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KEYPULSE_DEV_UPDATE_SOURCE"));
                updateStatus.Text = isDevUpdate
                    ? "Checking local .\\compiled\\KeyPulse.exe for a newer build..."
                    : "Checking github.com for a newer release...";
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
                        localUpdateExePath = result.LocalInstallerPath;
                        updateStatus.Text = !string.IsNullOrWhiteSpace(localUpdateExePath)
                            ? $"Local dev build {result.LatestVersion} is ready in .\\compiled. You have {Program.AppVersion}."
                            : $"Version {result.LatestVersion} is available. You have {Program.AppVersion}.";
                        updateStatus.Foreground = AppBrush("AppWarningBrush");
                        updateBtn.Content = !string.IsNullOrWhiteSpace(localUpdateExePath)
                            ? "Run local dev installer (.\\compiled\\KeyPulse.exe)"
                            : "Open the download page";
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
            var aboutBody = new StackPanel { Spacing = 6 };
            aboutBody.Children.Add(new TextBlock
            {
                Text = "Installed version: KeyPulse v" + Program.AppVersion,
                FontSize = 12,
                Classes = { "Muted" }
            });
            aboutBody.Children.Add(updateBtn);
            aboutBody.Children.Add(updateStatus);
            RegisterSettingCard(0, "About & Software Updates",
                "Shows your currently installed KeyPulse version and checks online for newer releases.",
                "about version update check release github",
                aboutBody);


            var chromeChk = new CheckBox
            {
                Content = "Open web links in Google Chrome",
                IsChecked = _currentConfig.UseGoogleChromeForUrls,
                TabIndex = 20,
                IsTabStop = true
            };
            ToolTip.SetTip(chromeChk, "When checked, 'Launch website' shortcuts open directly in Google Chrome. Uncheck to use your default Windows web browser.");
            var chromeWarningText = new TextBlock
            {
                Text = "⚠️ Google Chrome was not detected on this PC — web links will automatically fall back to your default Windows browser.",
                Foreground = AppBrush("AppWarningBrush"),
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                IsVisible = _currentConfig.UseGoogleChromeForUrls && !TryFindGoogleChrome(out _)
            };
            void RefreshChromeWarning()
            {
                chromeWarningText.IsVisible = (chromeChk.IsChecked == true) && !TryFindGoogleChrome(out _);
            }
            chromeChk.IsCheckedChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                _currentConfig.UseGoogleChromeForUrls = chromeChk.IsChecked == true;
                RefreshChromeWarning();
                SaveConfig();
            };
            var browserBody = new StackPanel { Spacing = 4 };
            browserBody.Children.Add(chromeChk);
            browserBody.Children.Add(chromeWarningText);
            browserBody.Children.Add(new TextBlock
            {
                Text = "Applies to shortcuts using the 'Launch website' action. Turn off to open links in Edge, Firefox, Brave, or your default browser.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            RegisterSettingCard(1, "Web Browser for URLs",
                "Determines whether website shortcuts explicitly launch Google Chrome or open in your default Windows browser.",
                "browser google chrome url web link website edge firefox",
                browserBody);

            var speedCombo = new ComboBox
            {
                Width = 285,
                MaxWidth = 300,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                TabIndex = 21,
                IsTabStop = true,
                ItemsSource = new List<string>
                {
                    "Fast (1 ms — Recommended)",
                    "Normal (6 ms)",
                    "Compatible (18 ms — Consoles & Remote)",
                    "Custom Delay (1–200 ms)"
                }
            };
            ToolTip.SetTip(speedCombo, "Choose how fast KeyPulse types out characters when using the 'Type text' shortcut action.");

            var speedCustomPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0)
            };
            speedCustomPanel.Children.Add(new TextBlock
            {
                Text = "Keystroke delay:",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var speedMinusBtn = new Button
            {
                Content = "−",
                Classes = { "Secondary" },
                Width = 28,
                Height = 28,
                Padding = new Avalonia.Thickness(0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TabIndex = 22,
                IsTabStop = true
            };
            ToolTip.SetTip(speedMinusBtn, "Decrease typing delay by 1 ms (faster typing).");
            var speedCustomBox = new TextBox
            {
                Width = 54,
                Height = 28,
                MinHeight = 28,
                MaxLength = 3,
                Padding = new Avalonia.Thickness(4, 2),
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Text = NormalizeTypingDelay(_currentConfig.TypingDelayMs).ToString(),
                TabIndex = 23,
                IsTabStop = true
            };
            ToolTip.SetTip(speedCustomBox, "Enter a custom millisecond pause between characters (1 to 200 ms). Higher numbers prevent dropped letters in slow remote desktop or console windows.");
            var speedPlusBtn = new Button
            {
                Content = "+",
                Classes = { "Secondary" },
                Width = 28,
                Height = 28,
                Padding = new Avalonia.Thickness(0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TabIndex = 24,
                IsTabStop = true
            };
            ToolTip.SetTip(speedPlusBtn, "Increase typing delay by 1 ms (more compatible with slow apps).");
            speedCustomPanel.Children.Add(speedMinusBtn);
            speedCustomPanel.Children.Add(speedCustomBox);
            speedCustomPanel.Children.Add(speedPlusBtn);
            speedCustomPanel.Children.Add(new TextBlock
            {
                Text = "ms between characters (1–200 ms)",
                Classes = { "Muted" },
                FontSize = 11.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            void SyncSpeedUiFromValue(int delayMs)
            {
                int clamped = NormalizeTypingDelay(delayMs);
                speedCustomBox.Text = clamped.ToString();
                int idx = clamped switch
                {
                    TypingDelayFast => 0,
                    TypingDelayNormal => 1,
                    TypingDelayCompatible => 2,
                    _ => 3
                };
                if (speedCombo.SelectedIndex != idx)
                {
                    speedCombo.SelectedIndex = idx;
                }
                bool isCustom = speedCombo.SelectedIndex == 3;
                speedCustomPanel.Opacity = isCustom ? 1.0 : 0.65;
                speedCustomBox.IsEnabled = isCustom;
                speedMinusBtn.IsEnabled = isCustom && clamped > 1;
                speedPlusBtn.IsEnabled = isCustom && clamped < 200;
            }

            void ApplyTypingDelayMs(int newDelay, bool forceCustomIndex = false)
            {
                int clamped = NormalizeTypingDelay(newDelay);
                _currentConfig.TypingDelayMs = clamped;
                InputSimulator.CharacterDelayMs = clamped;
                suppressSyncEvents = true;
                try
                {
                    speedCustomBox.Text = clamped.ToString();
                    if (forceCustomIndex) speedCombo.SelectedIndex = 3;
                    bool isCustom = speedCombo.SelectedIndex == 3;
                    speedCustomPanel.Opacity = isCustom ? 1.0 : 0.65;
                    speedCustomBox.IsEnabled = isCustom;
                    speedMinusBtn.IsEnabled = isCustom && clamped > 1;
                    speedPlusBtn.IsEnabled = isCustom && clamped < 200;
                }
                finally
                {
                    suppressSyncEvents = false;
                }
                SaveConfig();
            }

            SyncSpeedUiFromValue(_currentConfig.TypingDelayMs);

            speedCombo.SelectionChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                if (speedCombo.SelectedIndex == 0) ApplyTypingDelayMs(TypingDelayFast);
                else if (speedCombo.SelectedIndex == 1) ApplyTypingDelayMs(TypingDelayNormal);
                else if (speedCombo.SelectedIndex == 2) ApplyTypingDelayMs(TypingDelayCompatible);
                else
                {
                    ApplyTypingDelayMs(_currentConfig.TypingDelayMs, forceCustomIndex: true);
                    speedCustomBox.Focus();
                    speedCustomBox.SelectAll();
                }
            };

            speedMinusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ApplyTypingDelayMs(_currentConfig.TypingDelayMs - 1, forceCustomIndex: true);
            };
            speedPlusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ApplyTypingDelayMs(_currentConfig.TypingDelayMs + 1, forceCustomIndex: true);
            };
            void CommitCustomSpeedBox()
            {
                if (suppressSyncEvents) return;
                if (int.TryParse(speedCustomBox.Text?.Trim(), out int parsed))
                {
                    ApplyTypingDelayMs(parsed, forceCustomIndex: true);
                }
                else
                {
                    speedCustomBox.Text = _currentConfig.TypingDelayMs.ToString();
                }
            }
            speedCustomBox.LostFocus += (_, _) => CommitCustomSpeedBox();
            speedCustomBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Avalonia.Input.Key.Enter)
                {
                    ke.Handled = true;
                    CommitCustomSpeedBox();
                }
            };

            var speedBody = new StackPanel { Spacing = 4 };
            speedBody.Children.Add(speedCombo);
            speedBody.Children.Add(speedCustomPanel);
            speedBody.Children.Add(new TextBlock
            {
                Text = "Only affects the 'Type text' action. Increase the delay if characters are skipped in older consoles, VMs, or remote desktop sessions.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            RegisterSettingCard(1, "Simulated Typing Speed",
                "Controls the pause (in milliseconds) between keystrokes injected by the 'Type text' action. Fast (1 ms) is instant; Compatible (18 ms) or Custom (up to 200 ms) prevents dropped characters in laggy remote sessions.",
                "typing speed delay milliseconds ms fast normal compatible console remote rdp",
                speedBody);


            var sbCfg = ScreenBrightnessService.LoadConfig();
            var sbLockChk = new CheckBox
            {
                Content = "Sync brightness and contrast across all screens",
                IsChecked = sbCfg.GlobalLockBrightnessContrast,
                TabIndex = 30,
                IsTabStop = true
            };
            ToolTip.SetTip(sbLockChk, "When checked, moving a Brightness slider automatically updates Contrast to the same level on all monitors.");
            sbLockChk.IsCheckedChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                var cfg = ScreenBrightnessService.LoadConfig();
                bool locked = sbLockChk.IsChecked == true;
                cfg.GlobalLockBrightnessContrast = locked;
                foreach (var scr in cfg.Screens)
                {
                    scr.LockRatio = locked;
                    if (locked)
                    {
                        scr.LastContrast = scr.LastBrightness;
                        ScreenBrightnessService.RequestScreenLevelChange(scr.SlotIndex, scr.LastBrightness, scr.LastBrightness, updateContrastFromLock: true);
                    }
                }
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.NotifyStateChanged();
            };

            var openBrightnessBtn = new Button
            {
                Content = "Open Screen Brightness & Presets...",
                Classes = { "Secondary" },
                Width = 270,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                TabIndex = 31,
                IsTabStop = true
            };
            ToolTip.SetTip(openBrightnessBtn, "Open the Screen Brightness & Contrast manager to tune per-monitor sliders and shortcut cycle presets.");
            openBrightnessBtn.Click += (s, ev) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessWindow.OpenWindow(w);
            };
            var sbBody = new StackPanel { Spacing = 6 };
            sbBody.Children.Add(sbLockChk);
            sbBody.Children.Add(new TextBlock
            {
                Text = "Adjust hardware monitor brightness & contrast via DDC/CI or laptop backlight and configure in-game shortcut cycle presets.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            sbBody.Children.Add(openBrightnessBtn);
            RegisterSettingCard(2, "Screen Brightness & Contrast",
                "Manage hardware monitor brightness, contrast ratio syncing, and shortcut cycle presets without OSD popups or focus loss.",
                "screen brightness contrast monitor display ddc backlight preset cycle sync lock",
                sbBody);

            var doSummaryText = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            void RefreshDesktopOrganizerSummary()
            {
                var doCfg = DesktopOrganizerService.LoadConfig();
                int manualCount = doCfg.Profiles.Count(p => !p.IsAutoSnapshot);
                int autoCount = doCfg.Profiles.Count(p => p.IsAutoSnapshot);
                string autoRestoreState = doCfg.AutoRestoreOnDisplayChange ? "On" : "Off";
                string autoSnapState = doCfg.AutoPeriodicSnapshotEnabled ? $"Every {Math.Clamp(doCfg.AutoSnapshotIntervalDays, 1, 30)}d" : "Off";
                doSummaryText.Text = $"Saved layouts: {manualCount} manual, {autoCount} auto-backup • Auto-restore: {autoRestoreState} • Auto-backup: {autoSnapState}.";
            }
            RefreshDesktopOrganizerSummary();

            var openOrganizerBtn = new Button
            {
                Content = "Open Desktop Icon Organizer...",
                Classes = { "Secondary" },
                Width = 270,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                TabIndex = 32,
                IsTabStop = true
            };
            ToolTip.SetTip(openOrganizerBtn, "Open Desktop Icon Organizer to save, restore, or automate desktop icon layouts across your monitors.");
            openOrganizerBtn.Click += (s, ev) =>
            {
                Program.PlaySound("click");
                DesktopOrganizerWindow.OpenOrganizer(w);
            };
            var doBody = new StackPanel { Spacing = 6 };
            doBody.Children.Add(doSummaryText);
            doBody.Children.Add(openOrganizerBtn);
            RegisterSettingCard(2, "Desktop Icon Organizer",
                "Saves and restores your desktop icon positions across single or multi-monitor setups, with optional auto-restore when displays connect or change resolution.",
                "desktop icon organizer layout restore snapshot monitor grid",
                doBody);


            var backupBtn = new Button
            {
                Content = "Backup Settings...",
                Classes = { "Primary" },
                MinWidth = 180,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TabIndex = 40,
                IsTabStop = true
            };
            ToolTip.SetTip(backupBtn, "Export all shortcuts, preferences, window layouts, desktop icon profiles, and brightness presets to a single file (with optional AES-GCM password encryption).");

            var restoreBtn = new Button
            {
                Content = "Import Settings...",
                Classes = { "Secondary" },
                MinWidth = 180,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TabIndex = 41,
                IsTabStop = true
            };
            ToolTip.SetTip(restoreBtn, "Restore your shortcuts, preferences, desktop layouts, and brightness presets from a previously saved KeyPulse backup file.");

            var backupBtnRow = new WrapPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin = new Avalonia.Thickness(0, 2, 0, 0)
            };
            backupBtn.Margin = new Avalonia.Thickness(0, 0, 10, 6);
            restoreBtn.Margin = new Avalonia.Thickness(0, 0, 0, 6);
            backupBtnRow.Children.Add(backupBtn);
            backupBtnRow.Children.Add(restoreBtn);

            var backupBody = new StackPanel { Spacing = 6 };
            backupBody.Children.Add(new TextBlock
            {
                Text = "Export a complete snapshot of your shortcuts, preferences, desktop icon layouts, and brightness presets, or import an existing backup file.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            backupBody.Children.Add(backupBtnRow);
            RegisterSettingCard(3, "Backup & Import Settings",
                "One-click export and import for your entire KeyPulse setup. Backups can be password-encrypted with AES-256-GCM so secret shortcut targets remain protected.",
                "backup restore export import configuration file json encrypt password",
                backupBody);

            bool isElevated = Program.IsElevated;
            bool isWiredAdmin = Program.IsAdminTaskInstalled();

            var adminBtn = new Button
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Avalonia.Thickness(12, 8),
                TabIndex = 42,
                IsTabStop = true
            };
            var adminStack = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Vertical,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var adminTitle = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var adminSub = new TextBlock { FontSize = 10.5, Classes = { "Muted" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            adminStack.Children.Add(adminTitle);
            adminStack.Children.Add(adminSub);
            adminBtn.Content = adminStack;

            void RefreshAdminButtonState(bool elevated, bool wiredAdmin)
            {
                if (elevated)
                {
                    adminTitle.Text = "Run As Standard User Level (Requires Restart)";
                    adminSub.Text = wiredAdmin
                        ? "(Administrator active & wired — click to revert to standard user)"
                        : "(Administrator session active — click to revert to standard user)";
                    adminBtn.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2e5c46"));
                    ToolTip.SetTip(adminBtn, "Revert KeyPulse to standard user permissions and restart. Restores normal security isolation, though shortcuts won't inject into elevated Admin windows.");
                }
                else if (wiredAdmin)
                {
                    adminTitle.Text = "Elevate To Match Admin Mode (Requires Restart)";
                    adminSub.Text = "(Admin logon task is installed, but this session is running as standard user)";
                    adminBtn.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8a6d1b"));
                    ToolTip.SetTip(adminBtn, "The Administrator logon task is installed, but this session is not elevated. Click to restart as Administrator or remove the Admin task.");
                }
                else
                {
                    adminTitle.Text = "Run As Administrator (Requires Restart)";
                    adminSub.Text = "(Allows injecting hotkeys and text into elevated CMD/Admin apps)";
                    adminBtn.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7d1d1d"));
                    ToolTip.SetTip(adminBtn, "Restarts KeyPulse with Administrator rights so shortcuts and text typing work inside elevated Command Prompt, PowerShell, and Admin tools.");
                }
            }
            RefreshAdminButtonState(isElevated, isWiredAdmin);

            var resetAllBtn = new Button
            {
                Content = "Restore All Default Settings...",
                Classes = { "Danger" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Avalonia.Thickness(12, 8),
                TabIndex = 43,
                IsTabStop = true
            };
            ToolTip.SetTip(resetAllBtn, "Reset all KeyPulse preferences, typing speed, theme, screen brightness presets, and desktop organizer automation settings back to factory defaults. Your saved shortcuts and manual desktop layouts are kept safe.");

            var dangerBody = new StackPanel { Spacing = 10 };
            dangerBody.Children.Add(new TextBlock
            {
                Text = "Actions below change Windows system privilege tasks (restarting KeyPulse) or reset all application preferences back to factory defaults.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            dangerBody.Children.Add(adminBtn);
            dangerBody.Children.Add(resetAllBtn);
            RegisterSettingCard(3, "Danger Zone — Privileges & Factory Reset",
                "High-impact system actions that restart KeyPulse to change Windows Administrator elevation or reset all preferences back to out-of-the-box defaults.",
                "danger zone privileges administrator admin user elevate uac reset factory defaults",
                dangerBody,
                isDangerZone: true);

            var emptySearchPanel = new Border
            {
                Classes = { "Panel" },
                Padding = new Avalonia.Thickness(24),
                IsVisible = false,
                Child = new StackPanel
                {
                    Spacing = 6,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "No settings match your search",
                            Classes = { "SectionTitle" },
                            FontSize = 15,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = "Try searching for startup, theme, sound, chrome, typing, brightness, desktop, backup, or admin.",
                            Classes = { "Muted" },
                            FontSize = 12,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        }
                    }
                }
            };
            cardsStack.Children.Add(emptySearchPanel);

            var cardsScroll = new ScrollViewer
            {
                Content = cardsStack,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(cardsScroll, 2);
            rootGrid.Children.Add(cardsScroll);

            var statusBorder = new Border
            {
                Classes = { "Panel" },
                Background = AppBrush("AppPanelRaisedBrush"),
                BorderBrush = AppBrush("AppBorderBrush"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(12, 8),
                Margin = new Avalonia.Thickness(0, 10, 0, 0),
                IsVisible = false
            };
            var statusTxt = new TextBlock
            {
                Foreground = AppBrush("AppWarningBrush"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 12,
                Classes = { "Muted" }
            };
            statusTxt.PropertyChanged += (_, pe) =>
            {
                if (pe.Property == TextBlock.TextProperty)
                {
                    statusBorder.IsVisible = !string.IsNullOrWhiteSpace(statusTxt.Text);
                }
            };
            statusBorder.Child = statusTxt;
            Grid.SetRow(statusBorder, 3);
            rootGrid.Children.Add(statusBorder);

            var footerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            var footerHint = new TextBlock
            {
                Text = "All changes are saved automatically • Press Esc to close",
                Classes = { "Muted" },
                FontSize = 11.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(footerHint, 0);
            footerGrid.Children.Add(footerHint);

            var closeBtn = new Button
            {
                Content = "Close",
                Classes = { "Secondary" },
                MinWidth = 96,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                TabIndex = 50,
                IsTabStop = true
            };
            closeBtn.Click += (s, ev) => w.Close();
            Grid.SetColumn(closeBtn, 1);
            footerGrid.Children.Add(closeBtn);
            Grid.SetRow(footerGrid, 4);
            rootGrid.Children.Add(footerGrid);

            void SyncAllSettingsControls()
            {
                suppressSyncEvents = true;
                try
                {
                    chromeChk.IsChecked = _currentConfig.UseGoogleChromeForUrls;
                    RefreshChromeWarning();

                    themeCombo.SelectedIndex = _currentConfig.Theme switch
                    {
                        "Light" => 1,
                        "Dark" => 2,
                        _ => 0
                    };

                    SyncSpeedUiFromValue(_currentConfig.TypingDelayMs);
                    soundChk.IsChecked = _currentConfig.SoundEnabled;

                    var latestSb = ScreenBrightnessService.LoadConfig();
                    sbLockChk.IsChecked = latestSb.GlobalLockBrightnessContrast;

                    RefreshDesktopOrganizerSummary();
                }
                finally
                {
                    suppressSyncEvents = false;
                }
            }

            void RefreshVisibleCards()
            {
                string query = (searchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
                bool isSearching = !string.IsNullOrEmpty(query);

                for (int i = 0; i < tabButtons.Length; i++)
                {
                    tabButtons[i].Classes.Remove("Primary");
                    tabButtons[i].Classes.Remove("Secondary");
                    tabButtons[i].Classes.Add((!isSearching && i == activeTab) ? "Primary" : "Secondary");
                }
                restoreTabDefaultsBtn.IsVisible = !isSearching;

                int shown = 0;
                foreach (var item in settingCards)
                {
                    bool match = isSearching
                        ? item.Keywords.Contains(query)
                        : item.TabCategory == activeTab;
                    item.Card.IsVisible = match;
                    if (match) shown++;
                }
                emptySearchPanel.IsVisible = isSearching && shown == 0;
            }

            for (int i = 0; i < tabButtons.Length; i++)
            {
                int capturedIndex = i;
                tabButtons[i].Click += (_, _) =>
                {
                    Program.PlaySound("click");
                    activeTab = capturedIndex;
                    _currentConfig.LastActiveSettingsTab = activeTab;
                    if (!string.IsNullOrEmpty(searchBox.Text))
                    {
                        searchBox.Text = string.Empty;
                    }
                    RefreshVisibleCards();
                    SaveConfig();
                };
            }

            searchBox.TextChanged += (_, _) => RefreshVisibleCards();
            RefreshVisibleCards();

            restoreTabDefaultsBtn.Click += async (_, _) =>
            {
                Program.PlaySound("click");
                string tabTitle = tabNames[activeTab];
                bool confirmed = await ConfirmSettingsActionAsync(
                    w,
                    $"Restore {tabTitle} Defaults",
                    $"Reset all settings on the '{tabTitle}' tab back to their out-of-the-box defaults?\n\nYour saved shortcuts and manual desktop layouts will not be deleted.",
                    "Restore Defaults");
                if (!confirmed) return;

                if (activeTab == 0)
                {
                    if (Program.SetStartup(true, out var startErr))
                    {
                        _currentConfig.LaunchOnBoot = true;
                        suppressStartupEvent = true;
                        chk.IsChecked = true;
                        suppressStartupEvent = false;
                        startupStatus.Text = "KeyPulse will start hidden in the tray when Windows starts.";
                        startupStatus.Foreground = AppBrush("AppSuccessSoftBrush");
                    }
                    _currentConfig.Theme = "System";
                    _currentConfig.SoundEnabled = true;
                    Program.SoundEnabled = true;
                    App.ApplyTheme(_currentConfig.Theme);
                    SaveConfig();
                }
                else if (activeTab == 1)
                {
                    _currentConfig.UseGoogleChromeForUrls = true;
                    _currentConfig.TypingDelayMs = TypingDelayFast;
                    InputSimulator.CharacterDelayMs = TypingDelayFast;
                    SaveConfig();
                }
                else if (activeTab == 2)
                {
                    ScreenBrightnessService.ResetToDefaults();
                    DesktopOrganizerService.ResetBehavioralDefaults(out _);
                }
                else if (activeTab == 3)
                {
                    resetAllBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }

                SyncAllSettingsControls();
                Program.PlaySound("click");
                statusTxt.Text = $"✓ Restored default settings for the {tabTitle} tab.";
                statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
            };

            resetAllBtn.Click += async (_, _) =>
            {
                Program.PlaySound("click");
                bool confirmed = await ConfirmSettingsActionAsync(
                    w,
                    "Restore All Default Settings",
                    "Reset ALL KeyPulse preferences across every tab back to their original factory defaults?\n\n• Startup: Launch with Windows (On)\n• Theme: Match Windows\n• Sound effects: On\n• Browser: Google Chrome (On)\n• Typing speed: Fast (1 ms)\n• Screen Brightness: Sync On + 3 default presets\n• Desktop Organizer: Auto-restore Off, Auto-backup Off (3 days)\n\nYour saved keyboard shortcuts and manual desktop icon layouts will NOT be deleted.",
                    "Restore All Defaults");
                if (!confirmed) return;

                if (Program.SetStartup(true, out _))
                {
                    _currentConfig.LaunchOnBoot = true;
                    suppressStartupEvent = true;
                    chk.IsChecked = true;
                    suppressStartupEvent = false;
                    startupStatus.Text = "KeyPulse will start hidden in the tray when Windows starts.";
                    startupStatus.Foreground = AppBrush("AppSuccessSoftBrush");
                }

                _currentConfig.Theme = "System";
                _currentConfig.SoundEnabled = true;
                _currentConfig.UseGoogleChromeForUrls = true;
                _currentConfig.TypingDelayMs = TypingDelayFast;
                Program.SoundEnabled = true;
                InputSimulator.CharacterDelayMs = TypingDelayFast;
                App.ApplyTheme(_currentConfig.Theme);
                SaveConfig();

                ScreenBrightnessService.ResetToDefaults();
                DesktopOrganizerService.ResetBehavioralDefaults(out _);

                SyncAllSettingsControls();
                Program.PlaySound("click");
                statusTxt.Text = "✓ All preferences and presets have been restored to factory defaults.";
                statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
            };

            adminBtn.Click += async (s, ev) =>
            {
                var currentElevated = Program.IsElevated;
                var currentWiredAdmin = Program.IsAdminTaskInstalled();

                if (currentElevated)
                {
                    bool confirmRevert = await ConfirmSettingsActionAsync(
                        w,
                        "Revert to Standard User Mode?",
                        "KeyPulse will remove its Administrator logon task and immediately restart as a standard Windows user.\n\nYour shortcuts will be saved before restarting, but KeyPulse will no longer inject keys into elevated Administrator windows.",
                        "Restart as Standard User");
                    if (confirmRevert)
                    {
                        RelaunchWithPrivilegeChange(w, "--remove-admin-task", statusTxt);
                    }
                }
                else if (currentWiredAdmin)
                {
                    var dialog = CreateAppDialog("Administrator Mode Status", 440, 250, 440, 250, WindowStartupLocation.CenterOwner, false, true);
                    var p = CreateDialogPanel();
                    p.Children.Add(new TextBlock { Text = "Administrator Mode Configured", Classes = { "SectionTitle" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    p.Children.Add(new TextBlock
                    {
                        Text = "The Administrator logon task is already configured in Windows, but KeyPulse is currently running without Administrator rights.\n\nRestart KeyPulse with Administrator rights, or revert to standard user mode?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(0, 8, 0, 12)
                    });

                    var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Spacing = 12 };
                    var btnElevate = new Button { Content = "Restart As Admin", Classes = { "Primary" } };
                    var btnRevert = new Button { Content = "Revert To User Level", Classes = { "Danger" } };
                    var btnCancel = new Button { Content = "Cancel", Classes = { "Secondary" } };

                    int choice = 0;
                    btnElevate.Click += (ds, de) => { choice = 1; dialog.Close(); };
                    btnRevert.Click += (ds, de) => { choice = 2; dialog.Close(); };
                    btnCancel.Click += (ds, de) => { choice = 0; dialog.Close(); };

                    btnRow.Children.Add(btnElevate);
                    btnRow.Children.Add(btnRevert);
                    btnRow.Children.Add(btnCancel);
                    p.Children.Add(btnRow);
                    dialog.Content = p;

                    await ShowDialogOrWindowAsync(dialog);
                    if (choice == 1)
                    {
                        RelaunchWithPrivilegeChange(w, "--setup-admin-task", statusTxt);
                    }
                    else if (choice == 2)
                    {
                        RelaunchWithPrivilegeChange(w, "--remove-admin-task", statusTxt);
                    }
                }
                else
                {
                    var warnW = CreateAppDialog("Security Warning", 420, 260, 420, 260, WindowStartupLocation.CenterOwner, false, true);
                    var warnP = CreateDialogPanel();
                    warnP.Children.Add(new TextBlock { Text = "WARNING", Classes = { "ErrorText", "SectionTitle" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    warnP.Children.Add(new TextBlock { Text = "Running a hotkey tool as Administrator grants it FULL control over your PC.\n\nIf malware or an exploit were to hijack this app's input pipeline, it could theoretically wipe your hard drive or bypass your antivirus.\n\nAre you absolutely sure you want to run KeyPulse as an Administrator persistently?", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 10, 0, 10) });

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
                          + "column widths, desktop layouts, brightness presets, and preferences. It is NOT password protected, "
                          + "so its targets are readable by anyone who opens the file — keep it somewhere you trust."
                        : $"Backup exported, encrypted and verified: {payload.Shortcuts.Count} shortcut(s), plus your window "
                          + "sizes, column widths, desktop layouts, brightness presets, and preferences. Without this password "
                          + "the file cannot be restored, and KeyPulse cannot recover it for you.";
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

                    BackupPayload? payload = inspection.Payload;
                    if (payload == null)
                    {
                        payload = await UnlockBackupAsync(inspection, statusTxt);
                        if (payload == null) return;
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
                        SyncAllSettingsControls();

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
                            statusTxt.Text = $"Restore complete. All {imported} shortcut(s) and preferences are set up and working." + suffix;
                            statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                        }
                        else
                        {
                            var names = Hotkeys
                                .Where(h => h.IsEnabled && !IsStatus(h, "Active") && !IsStatus(h, "Ready") && !IsStatus(h, "Waiting"))
                                .Select(h => h.ActionTitle)
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
                        Program.LogCrash("Restore failed, rolling back: " + ex);
                        try
                        {
                            RestoreConfigSnapshot(previousConfig);
                            ApplyShortcutColumnResources();
                            SyncAllSettingsControls();
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

            void OnBrightnessStateChanged()
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var latest = ScreenBrightnessService.LoadConfig();
                    if (sbLockChk.IsChecked != latest.GlobalLockBrightnessContrast)
                    {
                        suppressSyncEvents = true;
                        sbLockChk.IsChecked = latest.GlobalLockBrightnessContrast;
                        suppressSyncEvents = false;
                    }
                });
            }
            void OnOrganizerProfilesChanged()
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(RefreshDesktopOrganizerSummary);
            }

            ScreenBrightnessService.StateChanged += OnBrightnessStateChanged;
            DesktopOrganizerService.ProfilesChanged += OnOrganizerProfilesChanged;

            w.KeyDown += (_, ke) =>
            {
                if (ke.Key == Avalonia.Input.Key.Escape && !ke.Handled)
                {
                    if (!string.IsNullOrEmpty(searchBox.Text))
                    {
                        ke.Handled = true;
                        searchBox.Text = string.Empty;
                        return;
                    }
                    ke.Handled = true;
                    w.Close();
                }
            };

            w.Closing += (senderClosing, args) =>
            {
                ScreenBrightnessService.StateChanged -= OnBrightnessStateChanged;
                DesktopOrganizerService.ProfilesChanged -= OnOrganizerProfilesChanged;
                if (w.WindowState == WindowState.Normal)
                {
                    _currentConfig.SettingsWindowX = w.Position.X;
                    _currentConfig.SettingsWindowY = w.Position.Y;
                    _currentConfig.SettingsWindowWidth = w.Bounds.Width;
                    _currentConfig.SettingsWindowHeight = w.Bounds.Height;
                }
                _currentConfig.LastActiveSettingsTab = activeTab;
                SaveConfig();
            };

            w.Content = rootGrid;
            w.Opened += (s, ev) => LoadStartupStateAsync(chk, startupStatus, v => suppressStartupEvent = v, (elev, wired) => RefreshAdminButtonState(elev, wired));
            ShowDialogOrWindow(w);
        }

        /// <summary>ISSUE_12: fetches the startup state off the UI thread and fills the box in.</summary>
        private async void LoadStartupStateAsync(CheckBox chk, TextBlock startupStatus, Action<bool> setSuppress, Action<bool, bool>? updateAdminBtn = null)
        {
            try
            {
                var (startup, adminTaskInstalled) = await System.Threading.Tasks.Task.Run(() =>
                {
                    Program.InvalidateAdminTaskState();
                    if (Program.IsAdminTaskInstalled())
                    {
                        Program.EnsureAdminTaskBatterySettings();
                    }
                    return (Program.QueryStartupState(), Program.IsAdminTaskInstalled());
                });

                updateAdminBtn?.Invoke(Program.IsElevated, adminTaskInstalled);

                if (startup == Program.StartupState.Unknown)
                {
                    chk.IsEnabled = false;
                    chk.IsThreeState = true;
                    setSuppress(true);
                    chk.IsChecked = null;
                    setSuppress(false);
                    startupStatus.Text = "Windows did not answer when asked whether KeyPulse starts with Windows, so this could not be checked. Nothing was changed. Try closing and reopening Settings.";
                    startupStatus.Foreground = AppBrush("AppWarningBrush");
                    return;
                }

                var isStartup = startup == Program.StartupState.On;

                setSuppress(true);
                chk.IsThreeState = false;
                chk.IsChecked = isStartup;
                setSuppress(false);

                chk.IsEnabled = true;

                var adminTaskIneffective = adminTaskInstalled && isStartup && !Program.IsElevated;

                startupStatus.Text = isStartup
                    ? (adminTaskInstalled
                        ? (adminTaskIneffective
                            ? "KeyPulse starts with Windows using its Administrator logon task, but this session is NOT running as Administrator - the task did not take effect on this account."
                            : "KeyPulse starts with Windows using its Administrator logon task.")
                        : "KeyPulse starts with Windows.")
                    : "KeyPulse does not start with Windows.";
                startupStatus.Foreground = AppBrush(adminTaskIneffective ? "AppWarningBrush" : "AppTextMutedBrush");
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
                SaveConfig();
            }
            catch (Exception ex)
            {
                Program.LogCrash("Could not save before changing privileges: " + ex);
                statusTxt.Text = "Your settings could not be saved, so KeyPulse did not restart: " + ex.Message;
                statusTxt.Foreground = AppBrush("AppDangerBrush");
                return;
            }

            try { settingsWindow.Close(); } catch { }

            try
            {
                var forUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

                using var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = Program.ExePath,
                    Arguments = arguments + " --for-user \"" + forUser + "\"",
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                Program.LogDebug("Privilege change was not started: " + ex.Message);
                ShowActionMessage("KeyPulse", "Nothing was changed",
                    "KeyPulse is still running exactly as before. Windows did not allow the change to start ("
                    + ex.Message + ").", false);
                return;
            }

            try { HotkeyManager.DisableCaptureHook(); } catch { }
            try { HotkeyManager.Clear(); } catch { }
            try { HotkeyManager.Stop(); } catch { }

            try { Program.ReleaseSingleInstanceLock(); } catch { }

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
                LastActiveSettingsTab = _currentConfig.LastActiveSettingsTab,
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
                    DesktopOrganizerWindowX = _currentConfig.DesktopOrganizerWindowX,
                    DesktopOrganizerWindowY = _currentConfig.DesktopOrganizerWindowY,
                    DesktopOrganizerWindowWidth = _currentConfig.DesktopOrganizerWindowWidth,
                    DesktopOrganizerWindowHeight = _currentConfig.DesktopOrganizerWindowHeight,
                    ScreenBrightnessWindowX = _currentConfig.ScreenBrightnessWindowX,
                    ScreenBrightnessWindowY = _currentConfig.ScreenBrightnessWindowY,
                    ScreenBrightnessWindowWidth = _currentConfig.ScreenBrightnessWindowWidth,
                    ScreenBrightnessWindowHeight = _currentConfig.ScreenBrightnessWindowHeight,
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
                    DisplayName = h.DisplayName,
                    KeyCombination = h.KeyCombination,
                    KeyCombinations = new List<string>(h.KeyCombinations),
                    Action = h.Action,
                    Target = h.Target,
                    IsTargetObfuscated = h.IsTargetObfuscated,
                    AddToQuickActionMenu = h.AddToQuickActionMenu
                }).ToList(),
                DesktopOrganizer = DesktopOrganizerService.LoadConfig(),
                ScreenBrightness = ScreenBrightnessService.LoadConfig()
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

            var first = new TextBox { PasswordChar = '●', PlaceholderText = "Password" };
            Grid.SetRow(first, 2);
            grid.Children.Add(first);

            var second = new TextBox { PasswordChar = '●', PlaceholderText = "Type it again", Margin = new Avalonia.Thickness(0, 8, 0, 0) };
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
            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90, IsCancel = true };
            var skip = new Button { Content = "Save without a password", Classes = { "Secondary" }, MinWidth = 180 };
            var protect = new Button { Content = "Encrypt", Classes = { "Primary" }, MinWidth = 100, IsDefault = true };
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
                PlaceholderText = "Password",
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
            var cancel = new Button { Content = "Cancel", Classes = { "Secondary" }, MinWidth = 90, IsCancel = true };
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
                var entry = new HotkeyEntry
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString() : item.Id,
                    IsEnabled = item.IsEnabled,
                    AllowRiskyShortcut = item.AllowRiskyShortcut,
                    DisplayName = item.DisplayName ?? string.Empty,
                    Action = item.Action,
                    Target = item.Target ?? string.Empty,
                    IsTargetObfuscated = item.IsTargetObfuscated,
                    AddToQuickActionMenu = item.AddToQuickActionMenu
                };

                if (item.KeyCombinations != null && item.KeyCombinations.Count > 0)
                {
                    entry.KeyCombinations = new List<string>(item.KeyCombinations);
                }
                else if (!string.IsNullOrWhiteSpace(item.KeyCombination))
                {
                    entry.KeyCombination = item.KeyCombination;
                }

                Hotkeys.Add(entry);
            }

            _currentConfig.UseGoogleChromeForUrls = payload.UseGoogleChromeForUrls;
            _currentConfig.SoundEnabled = payload.SoundEnabled;
            _currentConfig.TypingDelayMs = NormalizeTypingDelay(payload.TypingDelayMs);
            _currentConfig.Theme = string.IsNullOrWhiteSpace(payload.Theme) ? "System" : payload.Theme;
            _currentConfig.ShortcutSortColumn = payload.ShortcutSortColumn ?? string.Empty;
            _currentConfig.ShortcutSortDescending = payload.ShortcutSortDescending;
            _currentConfig.HasSeenTrayHint = payload.HasSeenTrayHint;
            _currentConfig.LastActiveSettingsTab = Math.Clamp(payload.LastActiveSettingsTab, 0, 3);

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

            if (payload.DesktopOrganizer != null)
            {
                DesktopOrganizerService.SaveConfig(payload.DesktopOrganizer, out _, allowEmptyProfiles: true);
                DesktopOrganizerService.RaiseProfilesChanged();
            }

            if (payload.ScreenBrightness != null)
            {
                ScreenBrightnessService.SaveConfig(payload.ScreenBrightness);
                ScreenBrightnessService.NotifyStateChanged();
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

            if (layout.ShortcutStatusColumnWidth > 0) _currentConfig.ShortcutStatusColumnWidth = ClampColumnWidth(layout.ShortcutStatusColumnWidth, StatusColumnDefault, StatusColumnMin, StatusColumnMax);
            if (layout.ShortcutKeyColumnWidth > 0) _currentConfig.ShortcutKeyColumnWidth = ClampColumnWidth(layout.ShortcutKeyColumnWidth, KeyColumnDefault, KeyColumnMin, KeyColumnMax);
            if (layout.ShortcutActionColumnWidth > 0) _currentConfig.ShortcutActionColumnWidth = ClampColumnWidth(layout.ShortcutActionColumnWidth, ActionColumnDefault, ActionColumnMin, ActionColumnMax);
            if (layout.ShortcutTargetColumnWidth > 0) _currentConfig.ShortcutTargetColumnWidth = ClampColumnWidth(layout.ShortcutTargetColumnWidth, TargetColumnDefault, TargetColumnMin, TargetColumnMax);
            ApplyShortcutColumnResources();

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

            if (IsSaneWindowSize(layout.DesktopOrganizerWindowWidth, layout.DesktopOrganizerWindowHeight))
            {
                _currentConfig.DesktopOrganizerWindowWidth = layout.DesktopOrganizerWindowWidth;
                _currentConfig.DesktopOrganizerWindowHeight = layout.DesktopOrganizerWindowHeight;
                var doOnScreen = IsPositionOnAConnectedScreen(layout.DesktopOrganizerWindowX, layout.DesktopOrganizerWindowY);
                _currentConfig.DesktopOrganizerWindowX = doOnScreen ? layout.DesktopOrganizerWindowX : double.NaN;
                _currentConfig.DesktopOrganizerWindowY = doOnScreen ? layout.DesktopOrganizerWindowY : double.NaN;
            }

            if (IsSaneWindowSize(layout.ScreenBrightnessWindowWidth, layout.ScreenBrightnessWindowHeight))
            {
                _currentConfig.ScreenBrightnessWindowWidth = layout.ScreenBrightnessWindowWidth;
                _currentConfig.ScreenBrightnessWindowHeight = layout.ScreenBrightnessWindowHeight;
                var sbOnScreen = IsPositionOnAConnectedScreen(layout.ScreenBrightnessWindowX, layout.ScreenBrightnessWindowY);
                _currentConfig.ScreenBrightnessWindowX = sbOnScreen ? layout.ScreenBrightnessWindowX : double.NaN;
                _currentConfig.ScreenBrightnessWindowY = sbOnScreen ? layout.ScreenBrightnessWindowY : double.NaN;
            }

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

        /// <summary>
        /// ISSUE_19: fills the editor from an Explorer "Add to KeyPulse" request. Everything is
        /// staged - folder actions for folders, program launches for files - and the window waits
        /// for nothing but the shortcut keys and the Add press.
        /// </summary>
        public void StageExternalAdd(string rawPath)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var path = (rawPath ?? string.Empty).Trim().Trim('"');
                    if (path.Length == 0) return;

                    ActionType action;
                    string target;
                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        action = ActionType.BrowseChrome;
                        target = uri.AbsoluteUri;
                    }
                    else if (path.StartsWith(@"\\", StringComparison.Ordinal)
                        || !Path.HasExtension(path))
                    {
                        action = ActionType.OpenFolder;
                        target = path;
                    }
                    else
                    {
                        action = ActionType.LaunchProgram;
                        target = "\"" + path + "\"";
                    }

                    ResetEditor();
                    _stagedAddTarget = path;

                    _loadingEditorFields = true;
                    try
                    {
                        if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = (int)action;
                        if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = target;
                    }
                    finally
                    {
                        _loadingEditorFields = false;
                    }

                    UpdateActionUi();
                    UpdateEditorModeText();

                    BringToFront();
                    this.FindControl<TextBox>("KeyCombo")?.Focus();

                    Program.LogDebug($"Staged an external add for: {path}");
                }
                catch (Exception ex)
                {
                    Program.LogCrash("Could not stage the external add: " + ex);
                }
            });
        }

        /// <summary>ISSUE_19: picks up a target staged by a second launch of KeyPulse.</summary>
        public void ConsumeStagedAdd()
        {
            try
            {
                if (!File.Exists(Program.StagedAddPath)) return;

                var path = File.ReadAllText(Program.StagedAddPath).Trim();
                try { File.Delete(Program.StagedAddPath); } catch { }

                if (path.Length == 0) return;
                StageExternalAdd(path);
            }
            catch (Exception ex)
            {
                Program.LogDebug("Could not consume the staged add: " + ex.Message);
            }
        }

        /// <summary>ISSUE_10: the first time Windows cannot protect a target, say so plainly.</summary>
        private void OnTargetProtectionFailed(string reason)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    ShowActionMessage("KeyPulse security notice",
                        "Windows cannot protect this target",
                        "Windows refused to encrypt it for your account (" + reason + "), so it will be stored in "
                        + "READABLE text in the settings file - including anything you ticked the Blur box for. "
                        + "The list marks such rows amber. This usually happens with roaming profiles, a damaged "
                        + "crypto store, or a machine managed by company policy.", true);
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Protection notice failed: " + ex.Message);
                }
            });
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
            Program.LogDebug("Main window close requested -> hidden back to the tray (ISSUE_32 instrumentation).");
            this.Hide();

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

            CryptoHelper.ProtectionFailed -= OnTargetProtectionFailed;

            StopActionNoticeTimer();

            if (_rawKeyHandler != null)
            {
                HotkeyManager.OnRawKey -= _rawKeyHandler;
                _rawKeyHandler = null;
            }

            CloseTypingProgressWindow();
            HotkeyManager.DisableCaptureHook();
            HotkeyManager.Clear();
            HotkeyManager.Stop();
            base.OnClosed(e);
        }
    }
}


