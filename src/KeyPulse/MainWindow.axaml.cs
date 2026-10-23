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
using Avalonia.VisualTree;
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
        public bool IsHiddenOnLaunch => _startHidden || !IsVisible;

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
        private Window? _activeSettingsWindow;
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
            UpdateScreenBrightnessBottomBtn();
            ScreenBrightnessService.StateChanged += () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateScreenBrightnessBottomBtn);
            };
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
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateScrollIndicators, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// ISSUE_32: the single sanctioned way to bring the shortcuts window forward. Setting
        /// _userAskedToBeShown BEFORE Show() is what disarms the hidden-startup logic inside
        /// MainWindow_Opened, so the first tray click after a --hidden boot opens the window
        /// instead of flashing and hiding it. A maximized window is left maximized.
        /// </summary>
        public UpdateCheckResult? PendingUpdateResult { get; set; }

        public void BringToFront()
        {
            ResetSearchFilter();
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
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateScrollIndicators, Avalonia.Threading.DispatcherPriority.Loaded);

            if (PendingUpdateResult.HasValue)
            {
                var update = PendingUpdateResult.Value;
                PendingUpdateResult = null;
                ShowUpdateNotification(update);
            }
        }

        private void RelaunchAsAdministrator(Window? dialogToClose = null)
        {
            try
            {
                SaveConfig();
            }
            catch (Exception ex)
            {
                Program.LogCrash("Could not save before elevating: " + ex);
            }

            try { dialogToClose?.Close(); } catch { }

            try
            {
                using var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = Program.ExePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                Program.LogDebug("Elevation was not started: " + ex.Message);
                ShowActionMessage("KeyPulse", "Elevation Canceled",
                    "KeyPulse is still running normally. Windows did not allow elevation to start (" + ex.Message + ").", false);
                return;
            }

            try { ScreenBrightnessService.InvalidateMonitorCache(); } catch { }
            try { HotkeyManager.DisableCaptureHook(); } catch { }
            try { HotkeyManager.Clear(); } catch { }
            try { HotkeyManager.Stop(); } catch { }

            try { Program.ReleaseSingleInstanceLock(); } catch { }

            Environment.Exit(0);
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

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var adminNoticeWin = CreateAppDialog("KeyPulse Administrator mode", 480, 260, 440, 230, WindowStartupLocation.Manual, true, true);
                        adminNoticeWin.ShowActivated = false;
                        PositionNoticeBottomRight(adminNoticeWin, 480, 260);

                        var sp = CreateDialogPanel(8);
                        var headingText = new Avalonia.Controls.TextBlock
                        {
                            Text = "Administrator mode is on, but it did not take effect",
                            Foreground = AppBrush("AppWarningBrush"),
                            FontWeight = Avalonia.Media.FontWeight.Bold,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        };
                        var msgText = new Avalonia.Controls.TextBlock
                        {
                            Text = "Windows started KeyPulse without Administrator rights even though the Administrator logon task is switched on. This normally means KeyPulse was opened manually via a desktop shortcut or file rather than through Windows startup.\n\nEverything else works normally. Typing and pasting into windows that are themselves running as Administrator will keep failing until KeyPulse is restarted with those rights.",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        };

                        var btnRow = new Avalonia.Controls.StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Margin = new Avalonia.Thickness(0, 8, 0, 0)
                        };

                        var restartAdminBtn = new Avalonia.Controls.Button
                        {
                            Content = "Restart As Administrator",
                            Classes = { "Primary" },
                            MinWidth = 180,
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                        };
                        ToolTip.SetTip(restartAdminBtn, "Close KeyPulse and relaunch it with elevated Administrator privileges via Windows UAC prompt.");
                        restartAdminBtn.Click += (_, _) =>
                        {
                            Program.PlaySound("click");
                            RelaunchAsAdministrator(adminNoticeWin);
                        };

                        var closeBtn = new Avalonia.Controls.Button
                        {
                            Content = "Close",
                            Classes = { "Secondary" },
                            MinWidth = 90,
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                        };
                        closeBtn.Click += (_, _) => adminNoticeWin.Close();

                        btnRow.Children.Add(restartAdminBtn);
                        btnRow.Children.Add(closeBtn);

                        sp.Children.Add(headingText);
                        sp.Children.Add(msgText);
                        sp.Children.Add(btnRow);

                        adminNoticeWin.Content = new ScrollViewer
                        {
                            Content = sp,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                        };

                        adminNoticeWin.Show();
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug("Could not show admin notice window: " + ex.Message);
                    }
                });
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

            var unprotected = Hotkeys.Count(h => h.IsTargetObfuscated && (h.TargetProtectionFailed || CryptoHelper.ProtectionUnavailable));
            if (CryptoHelper.ProtectionUnavailable && unprotected > 0)
            {
                var subject = $"{unprotected} hidden shortcut{(unprotected == 1 ? " is" : "s are")} stored in readable text";
                ShowActionMessage("KeyPulse security notice",
                    subject,
                    "Windows was unable to encrypt this data using DPAPI account encryption (this can occur with "
                    + "temporary profiles, network home drives, or specific Windows security policies). KeyPulse continues to "
                    + "function, but targets marked with 'Hide secret text' "
                    + "are stored in readable text in your settings file. Affected rows are marked with a '⚠️ Unencrypted' badge.", true);
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
            SaveConfigDebounced();
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

        private System.Threading.CancellationTokenSource? _saveDebounceCts;

        private void SaveConfigDebounced(int delayMs = 400)
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts = new System.Threading.CancellationTokenSource();
            var token = _saveDebounceCts.Token;
            System.Threading.Tasks.Task.Delay(delayMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    SaveConfig();
                });
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private void SaveConfig()
        {
            try
            {
                _saveDebounceCts?.Cancel();
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
            HotkeyCoordinator.ReleaseAll(Hotkeys);
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

                var (successCount, conflictCount, firstConflictCombo) = HotkeyCoordinator.Reconcile(h, validCombos, () => ExecuteAction(h));

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
            try { ScreenBrightnessService.RegisterBrightnessHotkeys(); } catch { }
            return failures;
        }

        /// <summary>Hands one row's Win32 hotkeys back, leaving every other row registered.</summary>
        private static void ReleaseRegistration(HotkeyEntry entry) => HotkeyCoordinator.Release(entry);

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
            if (this.FindControl<Button>("ClearSearchBtn") is Button clearBtn)
            {
                clearBtn.IsVisible = !string.IsNullOrEmpty(_searchText);
            }
            RefreshVisibleHotkeys();
        }

        public void ClearSearch_Click(object? sender, RoutedEventArgs e)
        {
            if (this.FindControl<TextBox>("SearchBox") is TextBox box)
            {
                box.Text = string.Empty;
                box.Focus();
            }
        }

        public void SearchBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                if (sender is TextBox box && !string.IsNullOrEmpty(box.Text))
                {
                    box.Text = string.Empty;
                    e.Handled = true;
                }
            }
        }

        public void ShortcutHeader_Pressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (sender is not Border header || header.Tag is not string column) return;
            ToggleSort(column);
            e.Handled = true;
        }

        public void ShortcutHeader_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Space)
            {
                if (sender is Border header && header.Tag is string column)
                {
                    ToggleSort(column);
                    e.Handled = true;
                }
            }
        }

        private void ToggleSort(string column)
        {
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
            SaveConfigDebounced();
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

            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateScrollIndicators, Avalonia.Threading.DispatcherPriority.Loaded);
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
                var app = Application.Current;
                if (app != null)
                {
                    if (app.TryGetResource(resourceKey, app.ActualThemeVariant, out var themed) && themed is Avalonia.Media.IBrush themedBrush)
                        return themedBrush;
                    if (app.TryGetResource(resourceKey, null, out var generic) && generic is Avalonia.Media.IBrush genericBrush)
                        return genericBrush;
                }
            }
            catch
            {
            }

            bool isDark = Application.Current?.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light;
            return resourceKey switch
            {
                "AppSurfaceBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#0f172a" : "#f8fafc")),
                "AppPanelBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#182235" : "#ffffff")),
                "AppPanelRaisedBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#1e293b" : "#f1f5f9")),
                "AppBorderBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#334155" : "#cbd5e1")),
                "AppBorderStrongBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#475569" : "#94a3b8")),
                "AppTextPrimaryBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#f8fafc" : "#0f172a")),
                "AppTextMutedBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#b6c2d0" : "#475569")),
                "AppInputBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#111827" : "#ffffff")),
                "AppHoverBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#27364d" : "#e2e8f0")),
                "AppSelectedBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#1d4ed833" : "#bfdbfe")),
                "AppAccentBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#60a5fa")),
                "AppAccentStrongBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2563eb")),
                "AppAccentPressedBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1d4ed8")),
                "AppSuccessBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22c55e")),
                "AppSuccessSoftBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#86efac")),
                "AppWarningBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f59e0b")),
                "AppDangerBrush" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444")),
                _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(isDark ? "#f8fafc" : "#0f172a"))
            };
        }

        private Window CreateAppDialog(string title, double width, double height, double minWidth, double minHeight, WindowStartupLocation startupLocation, bool canResize = true, bool topmost = false)
        {
            var win = new Window
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
            win.KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape)
                {
                    e.Handled = true;
                    win.Close();
                }
            };
            return win;
        }

        private bool CanUseAsDialogOwner()
        {
            return ShowInTaskbar && IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized;
        }

        private async System.Threading.Tasks.Task ShowDialogOrWindowAsync(Window window, Window? owner = null)
        {
            var targetOwner = owner ?? (CanUseAsDialogOwner() ? this : null);
            if (targetOwner != null && targetOwner.IsVisible)
            {
                try
                {
                    await window.ShowDialog(targetOwner);
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
                targetText.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                targetText.MinHeight = 34;
                targetText.MaxHeight = 76;
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
            normalizedUrl = rawTarget.Trim().Trim('"');
            error = string.Empty;

            if (normalizedUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var fileUri))
                {
                    normalizedUrl = fileUri.AbsoluteUri;
                    return true;
                }
            }

            if (File.Exists(normalizedUrl) ||
                normalizedUrl.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                normalizedUrl.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fileUri = new Uri(Path.GetFullPath(normalizedUrl));
                    normalizedUrl = fileUri.AbsoluteUri;
                    return true;
                }
                catch { }
            }

            if (!normalizedUrl.Contains("://", StringComparison.Ordinal))
            {
                normalizedUrl = "https://" + normalizedUrl;
            }

            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            {
                error = "Enter a valid web URL or file path";
                return false;
            }

            normalizedUrl = uri.AbsoluteUri;
            return true;
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
                            Title = "Select Program or File", 
                            AllowMultiple = false, 
                            FileTypeFilter = new[] { 
                                new Avalonia.Platform.Storage.FilePickerFileType("Programs & Files") { Patterns = new[] { "*.exe", "*.bat", "*.cmd", "*.ps1", "*.vbs", "*.lnk", "*.html", "*.htm" } }, 
                                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } } 
                            } 
                        };
                        var files = await this.StorageProvider.OpenFilePickerAsync(options);
                        if (files != null && files.Count > 0)
                        {
                            targetText.Text = "\"" + files[0].Path.LocalPath + "\"";
                        }
                    }
                    else if (actionType == ActionType.BrowseChrome)
                    {
                        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { 
                            Title = "Select HTML Web Page or File", 
                            AllowMultiple = false, 
                            FileTypeFilter = new[] { 
                                new Avalonia.Platform.Storage.FilePickerFileType("Web Pages") { Patterns = new[] { "*.html", "*.htm", "*.shtml", "*.xhtml", "*.svg" } }, 
                                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } } 
                            } 
                        };
                        var files = await this.StorageProvider.OpenFilePickerAsync(options);
                        if (files != null && files.Count > 0)
                        {
                            targetText.Text = new Uri(files[0].Path.LocalPath).AbsoluteUri;
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

        public void ShowUpdateNotification(UpdateCheckResult result)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var updateWin = CreateAppDialog("KeyPulse - Update Available", 560, 440, 500, 360, WindowStartupLocation.CenterScreen, true, true);
                    var sp = CreateDialogPanel(10);

                    var heading = new Avalonia.Controls.TextBlock
                    {
                        Text = $"KeyPulse v{result.LatestVersion} is Available",
                        Classes = { "SectionTitle" },
                        Foreground = AppBrush("AppAccentBrush")
                    };
                    var sub = new Avalonia.Controls.TextBlock
                    {
                        Text = $"You have v{Program.AppVersion}. Review what's new and update automatically:",
                        Classes = { "Muted" },
                        FontSize = 12
                    };

                    sp.Children.Add(heading);
                    sp.Children.Add(sub);

                    var notesHeader = new Avalonia.Controls.TextBlock
                    {
                        Text = "What's New in This Release:",
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        Margin = new Avalonia.Thickness(0, 4, 0, 2)
                    };
                    sp.Children.Add(notesHeader);

                    var notesText = new Avalonia.Controls.TextBox
                    {
                        Text = result.ReleaseNotes ?? "No release notes provided.",
                        IsReadOnly = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Height = 160,
                        AcceptsReturn = true
                    };
                    sp.Children.Add(notesText);

                    var progressBar = new Avalonia.Controls.ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = 0,
                        IsVisible = false,
                        Margin = new Avalonia.Thickness(0, 4, 0, 0)
                    };
                    var progressStatus = new Avalonia.Controls.TextBlock
                    {
                        Text = string.Empty,
                        FontSize = 11.5,
                        Classes = { "Muted" },
                        IsVisible = false
                    };
                    sp.Children.Add(progressBar);
                    sp.Children.Add(progressStatus);

                    var btnPanel = new Avalonia.Controls.StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Avalonia.Thickness(0, 8, 0, 0)
                    };

                    var cancelBtn = new Avalonia.Controls.Button
                    {
                        Content = "Cancel Download",
                        Classes = { "Secondary" },
                        MinWidth = 120,
                        IsVisible = false,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                    };

                    var updateBtn = new Avalonia.Controls.Button
                    {
                        Content = "Update Now",
                        Classes = { "Primary" },
                        MinWidth = 110,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                    };

                    var remindBtn = new Avalonia.Controls.Button
                    {
                        Content = "Remind Me Later",
                        Classes = { "Secondary" },
                        MinWidth = 120,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                    };

                    var skipBtn = new Avalonia.Controls.Button
                    {
                        Content = "Skip This Version",
                        Classes = { "Secondary" },
                        MinWidth = 120,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                    };

                    remindBtn.Click += (s, e) => updateWin.Close();
                    skipBtn.Click += (s, e) =>
                    {
                        _currentConfig.SkippedUpdateVersion = result.LatestVersion;
                        SaveConfig();
                        updateWin.Close();
                    };

                    System.Threading.CancellationTokenSource? downloadCts = null;
                    cancelBtn.Click += (s, e) =>
                    {
                        downloadCts?.Cancel();
                        cancelBtn.IsEnabled = false;
                        cancelBtn.Content = "Canceling...";
                    };

                    bool readyToRestart = !string.IsNullOrWhiteSpace(result.LocalInstallerPath);
                    string targetInstallerExe = result.LocalInstallerPath ?? string.Empty;

                    void ExecuteRestart()
                    {
                        try
                        {
                            var parentArg = $"--parent-pid {Environment.ProcessId}";
                            bool shouldElevate = Program.IsElevated
                                || Program.IsAdminTaskInstalled()
                                || Program.GetAdminTaskState() == Program.AdminTaskState.Enabled;
                            var adminArg = shouldElevate ? " --was-elevated" : "";
                            var psi = new ProcessStartInfo(targetInstallerExe)
                            {
                                UseShellExecute = true,
                                Arguments = $"--update-worker {parentArg}{adminArg}",
                                Verb = shouldElevate ? "runas" : ""
                            };
                            Process.Start(psi);
                            updateWin.Close();
                            ((App)Avalonia.Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                        }
                        catch (Exception ex)
                        {
                            Program.LogDebug("Could not launch updater: " + ex);
                            updateBtn.IsEnabled = true;
                            progressStatus.Foreground = AppBrush("AppDangerBrush");
                            progressStatus.Text = "Could not launch updater. Please try running the downloaded file manually or check file permissions.";
                        }
                    }

                    if (readyToRestart)
                    {
                        updateBtn.Content = "Restart & Install Now";
                        remindBtn.Content = "Install Later";
                        remindBtn.IsVisible = true;
                        remindBtn.IsEnabled = true;
                        skipBtn.IsVisible = false;
                        cancelBtn.IsVisible = false;
                        progressBar.IsVisible = false;
                        progressStatus.IsVisible = true;
                        progressStatus.Foreground = AppBrush("AppSuccessSoftBrush");
                        progressStatus.Text = "Update is ready to install.";
                    }

                    updateBtn.Click += async (s, e) =>
                    {
                        if (readyToRestart)
                        {
                            ExecuteRestart();
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
                        {
                            try { Process.Start(new ProcessStartInfo(Program.ReleasesPageUrl) { UseShellExecute = true }); } catch { }
                            updateWin.Close();
                            return;
                        }

                        updateBtn.IsEnabled = false;
                        updateBtn.IsVisible = false;
                        remindBtn.IsEnabled = false;
                        skipBtn.IsEnabled = false;
                        cancelBtn.IsVisible = true;
                        cancelBtn.IsEnabled = true;
                        cancelBtn.Content = "Cancel Download";
                        progressBar.IsVisible = true;
                        progressBar.IsIndeterminate = result.AssetSizeBytes <= 0;
                        progressStatus.IsVisible = true;
                        progressStatus.Foreground = AppBrush("AppTextMutedBrush");
                        progressStatus.Text = "Downloading update from GitHub...";

                        downloadCts = new System.Threading.CancellationTokenSource();

                        var progress = new Progress<double>(p =>
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Value = p * 100.0;
                            progressStatus.Text = $"Downloading update: {(int)(p * 100)}%";
                        });

                        var (succeeded, msg, stagedExe) = await UpdateChecker.DownloadUpdateAsync(
                            result.DownloadUrl,
                            result.LatestVersion,
                            result.ExpectedSha256,
                            progress,
                            downloadCts.Token);

                        if (!succeeded || string.IsNullOrWhiteSpace(stagedExe))
                        {
                            progressBar.IsVisible = false;
                            cancelBtn.IsVisible = false;
                            updateBtn.IsVisible = true;
                            updateBtn.IsEnabled = true;
                            remindBtn.IsEnabled = true;
                            skipBtn.IsEnabled = true;
                            progressStatus.Foreground = AppBrush("AppDangerBrush");
                            progressStatus.Text = "Download failed: " + msg;
                            updateBtn.Content = "Retry Update";
                            return;
                        }

                        targetInstallerExe = stagedExe;
                        readyToRestart = true;
                        progressBar.IsVisible = false;
                        cancelBtn.IsVisible = false;
                        skipBtn.IsVisible = false;

                        remindBtn.Content = "Install Later";
                        remindBtn.IsEnabled = true;
                        remindBtn.IsVisible = true;

                        updateBtn.Content = "Restart & Install Now";
                        updateBtn.Classes.Clear();
                        updateBtn.Classes.Add("Primary");
                        updateBtn.IsEnabled = true;
                        updateBtn.IsVisible = true;

                        progressStatus.Foreground = AppBrush("AppSuccessSoftBrush");
                        progressStatus.Text = "Update downloaded and verified. Restart to apply update.";
                    };

                    btnPanel.Children.Add(skipBtn);
                    btnPanel.Children.Add(remindBtn);
                    btnPanel.Children.Add(cancelBtn);
                    btnPanel.Children.Add(updateBtn);
                    sp.Children.Add(btnPanel);

                    updateWin.Content = sp;
                    updateWin.Show();
                }
                catch (Exception ex)
                {
                    Program.LogCrash("Failed to display update dialog: " + ex.Message);
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

        private System.Threading.CancellationTokenSource? _hotkeyProbeCts;

        public void KeyCombo_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var tb = sender as TextBox;
            if (tb == null) return;
            if (_loadingEditorFields) return;

            _hotkeyProbeCts?.Cancel();

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
            if (!ValidateHotkeyFormat(tb.Text, false, out var validationError, allowRiskyShortcut))
            {
                SetFieldError("KeyCombo", validationError);
                return;
            }

            SetFieldError("KeyCombo", null);

            if (_editingEntry == null)
            {
                _hotkeyProbeCts = new System.Threading.CancellationTokenSource();
                var token = _hotkeyProbeCts.Token;
                var text = tb.Text;
                System.Threading.Tasks.Task.Delay(250, token).ContinueWith(t =>
                {
                    if (t.IsCanceled || token.IsCancellationRequested) return;
                    bool available = HotkeyManager.Probe(text);
                    if (token.IsCancellationRequested) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (!available && tb.Text == text)
                        {
                            SetFieldError("KeyCombo", "Shortcut already taken by Windows or another app");
                        }
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
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
            if (actionCombo == null || actionCombo.SelectedIndex < 0 || actionCombo.SelectedIndex > (int)ActionType.InsertText)
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
                    Background = AppBrush("AppPanelRaisedBrush"),
                    BorderBrush = AppBrush("AppBorderBrush"),
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
                    Foreground = AppBrush("AppTextPrimaryBrush"),
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
                Avalonia.Automation.AutomationProperties.SetName(removeBtn, $"Remove {currentCombo} shortcut");
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
                var existingEntry = Hotkeys.FirstOrDefault(h => !ReferenceEquals(h, _editingEntry) &&
                    h.KeyCombinations.Any(existing => string.Equals(NormalizeComboKey(existing), norm, StringComparison.OrdinalIgnoreCase)));
                if (existingEntry != null)
                {
                    SetFieldError("KeyCombo", $"Shortcut '{c}' already exists. Select it in the table below to edit or change its action.");
                    if (this.FindControl<ListBox>("HotkeyList") is ListBox existingList)
                    {
                        existingList.ScrollIntoView(existingEntry);
                    }
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

            if (_editingEntry != null)
            {
                var currentTarget = this.FindControl<TextBox>("TargetText")?.Text;
                var currentName = this.FindControl<TextBox>("ActionNameText")?.Text?.Trim() ?? string.Empty;
                var actionCombo = this.FindControl<ComboBox>("ActionCombo");
                var currentAction = actionCombo != null && actionCombo.SelectedIndex >= 0 ? (ActionType)actionCombo.SelectedIndex : _editingEntry.Action;
                if (!string.Equals(_editingEntry.Target, currentTarget, StringComparison.Ordinal)
                    || !string.Equals(_editingEntry.DisplayName, currentName, StringComparison.Ordinal)
                    || _editingEntry.Action != currentAction)
                {
                    _ = CommitSelectedEditorChangeAsync();
                }
            }

            if (sender is ListBox list && list.SelectedItem is HotkeyEntry entry)
            {
                BeginEditingEntry(entry);
            }
            else
            {
                if (_editingEntry != null)
                {
                    ResetEditor();
                }
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

        private ScrollViewer? _hotkeyScrollViewer;

        private ScrollViewer? GetHotkeyListScrollViewer()
        {
            if (_hotkeyScrollViewer != null) return _hotkeyScrollViewer;
            if (this.FindControl<ListBox>("HotkeyList") is ListBox lb)
            {
                _hotkeyScrollViewer = lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            }
            return _hotkeyScrollViewer;
        }

        private void UpdateScrollIndicators()
        {
            var sv = GetHotkeyListScrollViewer();
            if (sv == null) return;

            double maxScrollY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            bool canScrollDown = maxScrollY > 2 && sv.Offset.Y < maxScrollY - 2;
            bool canScrollUp = sv.Offset.Y > 2;

            if (this.FindControl<Border>("ScrollFadeTop") is Border topFade)
                topFade.Opacity = canScrollUp ? 1 : 0;
            if (this.FindControl<Border>("ScrollFadeBottom") is Border bottomFade)
                bottomFade.Opacity = canScrollDown ? 1 : 0;

            if (this.FindControl<Button>("ScrollMoreDownPill") is Button downPill)
            {
                downPill.IsVisible = canScrollDown;
                if (canScrollDown && this.FindControl<TextBlock>("ScrollMoreDownText") is TextBlock downText)
                {
                    double itemHeight = 44.0;
                    double remainingPx = maxScrollY - sv.Offset.Y;
                    int hiddenCount = Math.Max(1, (int)Math.Ceiling(remainingPx / itemHeight));
                    downText.Text = hiddenCount == 1 ? "1 more shortcut below" : $"{hiddenCount} more shortcuts below";
                }
            }

            if (this.FindControl<Button>("ScrollMoreUpPill") is Button upPill)
            {
                upPill.IsVisible = canScrollUp;
            }
        }

        private void HotkeyList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (e.Source is ScrollViewer sv)
            {
                _hotkeyScrollViewer = sv;
                if (this.FindControl<Border>("ScrollFadeLeft") is Border left) left.Opacity = sv.Offset.X > 0 ? 1 : 0;
                if (this.FindControl<Border>("ScrollFadeRight") is Border right) right.Opacity = sv.Offset.X < sv.Extent.Width - sv.Viewport.Width - 1 ? 1 : 0;
                if (this.FindControl<Grid>("ShortcutHeaderGrid") is Grid headerGrid)
                {
                    headerGrid.Margin = new Avalonia.Thickness(-sv.Offset.X, 0, 0, 0);
                }
            }
            UpdateScrollIndicators();
        }

        public void ScrollMoreDownPill_Click(object? sender, RoutedEventArgs e)
        {
            var sv = GetHotkeyListScrollViewer();
            if (sv == null) return;

            Program.PlaySound("click");
            double targetY = Math.Min(sv.Extent.Height - sv.Viewport.Height, sv.Offset.Y + Math.Max(44, sv.Viewport.Height * 0.75));
            sv.Offset = new Avalonia.Vector(sv.Offset.X, targetY);
            UpdateScrollIndicators();
        }

        public void ScrollMoreUpPill_Click(object? sender, RoutedEventArgs e)
        {
            var sv = GetHotkeyListScrollViewer();
            if (sv == null) return;

            Program.PlaySound("click");
            sv.Offset = new Avalonia.Vector(sv.Offset.X, 0);
            UpdateScrollIndicators();
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
        public static uint MapAvaloniaKeyToVirtualKey(Avalonia.Input.Key key)
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

        private void UpdateScreenBrightnessBottomBtn()
        {
            var btn = this.FindControl<Button>("ScreenBrightnessBottomBtn");
            if (btn != null)
            {
                btn.IsVisible = ScreenBrightnessService.IsFeatureActive;
            }
        }

        public async void ScreenBrightness_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            this.Hide();
            if (!ScreenBrightnessService.IsFeatureActive)
            {
                bool accepted = await ScreenBrightnessService.ShowDisclaimerDialogAsync(this);
                if (!accepted) return;
            }
            ScreenBrightnessWindow.OpenWindow(this);
        }

        public void DesktopOrganizer_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            this.Hide();
            DesktopOrganizerWindow.OpenOrganizer();
        }

        private static Border CreateSettingsHelpBadge(string tooltipText)
        {
            var badge = new Border
            {
                Background = AppBrush("AppPanelRaisedBrush"),
                BorderBrush = AppBrush("AppBorderBrush"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(12),
                Width = 24,
                Height = 24,
                Focusable = true,
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
            Avalonia.Automation.AutomationProperties.SetName(badge, "Help: " + tooltipText);
            return badge;
        }

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

                string[] lines;
                lock (typeof(Program))
                {
                    lines = File.ReadAllLines(Program.StagedAddPath);
                    try { File.Delete(Program.StagedAddPath); } catch { }
                }

                foreach (var line in lines)
                {
                    var path = line.Trim();
                    if (path.Length > 0)
                    {
                        StageExternalAdd(path);
                    }
                }
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
                    var cleanReason = string.IsNullOrWhiteSpace(reason) ? "system protection unavailable" : reason.Trim();
                    ShowActionMessage("KeyPulse security notice",
                        "Windows cannot protect this target",
                        "Windows was unable to encrypt this item using DPAPI account encryption (" + cleanReason + "). "
                        + "It will be stored in readable text in your settings file - including items marked with 'Hide secret text'. "
                        + "The shortcut list marks these rows with a '⚠️ Unencrypted' badge. "
                        + "This can happen with temporary profiles, network home drives, or specific Windows security policies.", true);
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Protection notice failed: " + ex.Message);
                }
            });
        }

        private void ResetSearchFilter()
        {
            if (this.FindControl<TextBox>("SearchBox") is TextBox box && !string.IsNullOrEmpty(box.Text))
            {
                box.Text = string.Empty;
            }
            if (this.FindControl<Button>("ClearSearchBtn") is Button clearBtn)
            {
                clearBtn.IsVisible = false;
            }
            _searchText = string.Empty;
            RefreshVisibleHotkeys();
        }

        protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
        {
            ResetSearchFilter();
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
            try { ScreenBrightnessService.UnregisterAllBrightnessHotkeys(); } catch { }
            try { ScreenBrightnessService.InvalidateMonitorCache(); } catch { }
            HotkeyManager.Clear();
            HotkeyManager.Stop();
            base.OnClosed(e);
        }
    }
}


