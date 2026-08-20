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
        public ObservableCollection<HotkeyEntry> Hotkeys { get; } = new();
        private const string AppName = "KeyPulse";
        private readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "config.json");
        private readonly bool _startHidden;
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

                    if (!isModifierOnly)
                    {
                        string keyStr = vk switch
                        {
                            0x0D => "Enter",
                            0x20 => "Space",
                            0x26 => "Up",
                            0x28 => "Down",
                            0x25 => "Left",
                            0x27 => "Right",
                            0x1B => "Escape",
                            0x09 => "Tab",
                            0x08 => "Back",
                            0x2E => "Delete",
                            0x2D => "Insert",
                            0x24 => "Home",
                            0x23 => "End",
                            0x21 => "PageUp",
                            0x22 => "PageDown",
                            0xBB => "OemPlus",
                            0xBC => "OemComma",
                            0xBD => "OemMinus",
                            0xBE => "OemPeriod",
                            _ => ((ConsoleKey)vk).ToString()
                        };
                        parts.Add(keyStr);
                    }

                    var txt = string.Join("+", parts);
                    if (isModifierOnly && parts.Count > 0) txt += "+";

                    keyCombo.Text = txt;
                });
            };

            LoadConfig();
            ApplyShortcutColumnResources();
            HotkeyManager.Start();
            var failures = ApplyHotkeys();
            if (failures > 0)
            {
                Program.LogDebug($"{failures} shortcut(s) were inactive after startup registration.");
            }
            RefreshStatusSummary();
        }

        private AppConfig _currentConfig = new AppConfig();

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
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

        private void LoadConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    if (loaded != null)
                    {
                        _currentConfig = loaded;
                        foreach (var item in loaded.Hotkeys) Hotkeys.Add(item);

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
                        if (loaded.MainWindowWidth > 0 && loaded.MainWindowHeight > 0)
                        {
                            Width = loaded.MainWindowWidth;
                            Height = loaded.MainWindowHeight;
                        }
                        if (Enum.TryParse<Avalonia.Controls.WindowState>(loaded.MainWindowState, out var state))
                        {
                            WindowState = state;
                        }
                    }
                }
                catch { }
            }
        }

        private static double ClampColumnWidth(double value, double fallback, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) value = fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private void ApplyShortcutColumnResources()
        {
            Resources["ShortcutKeyColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutKeyColumnWidth, 220, 160, 360));
            Resources["ShortcutActionColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutActionColumnWidth, 128, 110, 260));
            Resources["ShortcutStatusColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutStatusColumnWidth, 128, 110, 260));
            Resources["ShortcutTargetColumnWidth"] = new GridLength(ClampColumnWidth(_currentConfig.ShortcutTargetColumnWidth, 468, 180, 1600));
        }

        private void WireShortcutColumnSplitters()
        {
            WireShortcutColumnSplitter("ShortcutKeyColumnSplitter", "ShortcutKeyColumnWidth", "ShortcutActionColumnWidth", 160, 360, 110, 260);
            WireShortcutColumnSplitter("ShortcutActionColumnSplitter", "ShortcutActionColumnWidth", "ShortcutStatusColumnWidth", 110, 260, 110, 260);
            WireShortcutColumnSplitter("ShortcutStatusColumnSplitter", "ShortcutStatusColumnWidth", "ShortcutTargetColumnWidth", 110, 260, 180, 1600);
            WireShortcutColumnEdgeSplitter("ShortcutTargetColumnSplitter", "ShortcutTargetColumnWidth", 180, 1600);
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

        private void CaptureShortcutColumnWidths()
        {
            if (this.FindControl<Grid>("ShortcutHeaderGrid") is not Grid headerGrid) return;
            var columns = headerGrid.ColumnDefinitions;
            if (columns.Count < 8) return;

            _currentConfig.ShortcutKeyColumnWidth = ClampColumnWidth(columns[1].ActualWidth, 220, 160, 360);
            _currentConfig.ShortcutActionColumnWidth = ClampColumnWidth(columns[3].ActualWidth, 128, 110, 260);
            _currentConfig.ShortcutStatusColumnWidth = ClampColumnWidth(columns[5].ActualWidth, 128, 110, 260);
            _currentConfig.ShortcutTargetColumnWidth = ClampColumnWidth(columns[7].ActualWidth, 468, 180, 1600);
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

                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonSerializer.Serialize(_currentConfig, AppConfigJsonContext.Default.AppConfig);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
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
                UseGoogleChromeForUrls = source.UseGoogleChromeForUrls
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
                Target = source.Target
            };
        }

        private void RestoreConfigSnapshot(AppConfig config)
        {
            _currentConfig = CloneConfig(config);
            Hotkeys.Clear();
            foreach (var item in _currentConfig.Hotkeys)
            {
                Hotkeys.Add(item);
            }
            ApplyHotkeys();
        }

        private static bool ValidateImportedConfig(AppConfig config, out string error)
        {
            error = string.Empty;
            if (config.Hotkeys == null)
            {
                error = "Backup is missing the shortcut list.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < config.Hotkeys.Count; i++)
            {
                var item = config.Hotkeys[i];
                if (item == null)
                {
                    error = $"Shortcut #{i + 1} is empty.";
                    return false;
                }

                if (!ValidateHotkeyFormat(item.KeyCombination, false, out var hotkeyError, item.AllowRiskyShortcut))
                {
                    error = $"Shortcut #{i + 1}: {hotkeyError}.";
                    return false;
                }

                var normalizedCombo = NormalizeComboKey(item.KeyCombination);
                if (!seen.Add(normalizedCombo))
                {
                    error = $"Shortcut #{i + 1}: duplicate shortcut.";
                    return false;
                }

                if (!ValidateTarget(item.Action, item.Target, out _, out var targetError))
                {
                    error = $"Shortcut #{i + 1}: {targetError}.";
                    return false;
                }
            }

            return true;
        }

        private int ApplyHotkeys()
        {
            var failures = 0;
            var registeredCombos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            HotkeyManager.Clear();
            foreach (var h in Hotkeys)
            {
                if (!h.IsEnabled)
                {
                    h.RegistrationStatus = "Disabled";
                    continue;
                }

                if (!ValidateHotkeyFormat(h.KeyCombination, false, out var hotkeyError, h.AllowRiskyShortcut))
                {
                    h.RegistrationStatus = "Inactive: " + hotkeyError;
                    failures++;
                    continue;
                }

                if (!ValidateTarget(h.Action, h.Target, out var normalizedTarget, out var targetError))
                {
                    h.RegistrationStatus = "Inactive: " + targetError;
                    failures++;
                    continue;
                }

                h.Target = normalizedTarget;
                var normalizedCombo = NormalizeComboKey(h.KeyCombination);
                if (!registeredCombos.Add(normalizedCombo))
                {
                    h.RegistrationStatus = "Inactive: Duplicate";
                    failures++;
                    continue;
                }

                if (HotkeyManager.Register(h.KeyCombination, () => ExecuteAction(h)))
                {
                    h.RegistrationStatus = "Active";
                }
                else
                {
                    h.RegistrationStatus = "Inactive: Conflict";
                    failures++;
                }
            }

            RefreshStatusSummary();
            return failures;
        }

        private void RefreshStatusSummary()
        {
            if (this.FindControl<TextBlock>("ServiceStatusText") is not TextBlock statusText) return;

            var active = Hotkeys.Count(h => h.RegistrationStatus.StartsWith("Active", StringComparison.OrdinalIgnoreCase));
            var inactive = Hotkeys.Count(h => h.IsEnabled && !h.RegistrationStatus.StartsWith("Active", StringComparison.OrdinalIgnoreCase));
            var disabled = Hotkeys.Count(h => !h.IsEnabled);

            if (inactive > 0)
            {
                statusText.Text = $"KeyPulse running. {active} active, {inactive} inactive, {disabled} disabled. Open inactive rows for details.";
                statusText.Foreground = AppBrush("AppWarningBrush");
            }
            else
            {
                statusText.Text = $"KeyPulse running. {active} active, {disabled} disabled. Run as administrator to trigger shortcuts inside elevated apps.";
                statusText.Foreground = AppBrush("AppTextMutedBrush");
            }
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
                targetText.MaxHeight = isTextAction ? 140 : double.PositiveInfinity;
            }

            if (targetLabel != null)
            {
                targetLabel.Text = actionType switch
                {
                    ActionType.OpenFolder => "Folder path",
                    ActionType.LaunchProgram => "Program or script",
                    ActionType.BrowseChrome => "Web URL",
                    ActionType.TypeText => "Text to type",
                    ActionType.InsertText => "Plain text to paste",
                    _ => "Target"
                };
            }

            if (targetHint != null)
            {
                targetHint.Text = actionType switch
                {
                    ActionType.OpenFolder => "Choose a local, removable, or network folder.",
                    ActionType.LaunchProgram => "Choose an app or enter a command with arguments.",
                    ActionType.BrowseChrome => "Enter a website address. Missing http/https is added automatically.",
                    ActionType.TypeText => "For legacy consoles and apps that do not accept paste. KeyPulse simulates keystrokes.",
                    ActionType.InsertText => "Uses plain-text clipboard paste without formatting, then restores the previous text clipboard when possible.",
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

        private void ClearFieldErrors()
        {
            SetFieldError("KeyCombo", null);
            SetFieldError("TargetText", null);
        }

        private static bool ValidateTarget(ActionType actionType, string? rawTarget, out string normalizedTarget, out string error)
        {
            normalizedTarget = rawTarget ?? string.Empty;
            error = string.Empty;

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
                if (!Directory.Exists(folder))
                {
                    error = "Folder not found";
                    return false;
                }

                normalizedTarget = folder;
                return true;
            }

            if (actionType == ActionType.LaunchProgram)
            {
                if (!TryResolveLaunchTarget(trimmedTarget, out var fileName, out var arguments, out error))
                {
                    return false;
                }

                normalizedTarget = FormatLaunchTarget(fileName, arguments);
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
                        if (!ValidateTarget(entry.Action, entry.Target, out var folder, out var folderError))
                        {
                            throw new InvalidOperationException(folderError);
                        }
                        Process.Start("explorer.exe", $"\"{folder}\"");
                        break;
                    case ActionType.LaunchProgram:
                        if (!TryResolveLaunchTarget(entry.Target, out var fileName, out var arguments, out var launchError))
                        {
                            throw new InvalidOperationException(launchError);
                        }
                        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
                        break;
                    case ActionType.BrowseChrome:
                        LaunchUrl(entry.Target);
                        break;
                    case ActionType.TypeText:
                        if (!InputSimulator.TypeText(entry.Target, out var typeError))
                        {
                            throw new InvalidOperationException(typeError);
                        }
                        break;
                    case ActionType.InsertText:
                        if (!InputSimulator.InsertText(entry.Target, out var insertError))
                        {
                            throw new InvalidOperationException(insertError);
                        }
                        break;
                }
                Program.LogDebug($"Shortcut {entry.KeyCombination} completed.");
            }
            catch (Exception ex)
            {
                Program.LogCrash($"Shortcut action failed ({entry.KeyCombination}, {entry.Action}): {ex}");
                ShowActionFailure(ex.Message);
            }
        }

        private void ShowActionFailure(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_actionErrorWindow != null)
                    {
                        _actionErrorRepeatCount++;
                        if (_actionErrorMessageText != null) _actionErrorMessageText.Text = message;
                        if (_actionErrorRepeatText != null) _actionErrorRepeatText.Text = $"Repeated {_actionErrorRepeatCount} times while this warning was open.";
                        _actionErrorWindow.Activate();
                        return;
                    }

                    _actionErrorRepeatCount = 1;
                    var errWin = CreateAppDialog("KeyPulse Action Failed", 500, 230, 420, 190, WindowStartupLocation.CenterScreen, true, true);

                    var sp = CreateDialogPanel(8);
                    sp.Children.Add(new Avalonia.Controls.TextBlock { Text = "Failed to execute shortcut", Foreground = AppBrush("AppDangerBrush"), FontWeight = Avalonia.Media.FontWeight.Bold });
                    _actionErrorMessageText = new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                    _actionErrorRepeatText = new Avalonia.Controls.TextBlock { Text = string.Empty, Classes = { "Muted" }, FontSize = 12 };
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

                    errWin.Content = sp;
                    errWin.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_actionErrorWindow, errWin))
                        {
                            _actionErrorWindow = null;
                            _actionErrorMessageText = null;
                            _actionErrorRepeatText = null;
                            _actionErrorRepeatCount = 0;
                        }
                    };

                    _actionErrorWindow = errWin;
                    errWin.Opened += (s, e) => closeButton.Focus();
                    errWin.Show();
                }
                catch (Exception ex)
                {
                    Program.LogCrash($"Failed to show shortcut action error: {ex}");
                }
            });
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

        private void TargetText_TextChanged(object? sender, TextChangedEventArgs e)
        {
            ValidateEditorTarget();
        }

        private void ValidateEditorTarget()
        {
            if (_loadingEditorFields) return;
            if (_editingEntry == null && !_creatingDuplicate)
            {
                SetFieldError("TargetText", null);
                return;
            }

            var actionType = GetSelectedAction();
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            if (!ValidateTarget(actionType, target, out _, out var targetError))
            {
                SetFieldError("TargetText", targetError);
            }
            else
            {
                SetFieldError("TargetText", null);
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

            _editingEntry.KeyCombination = keyText;
            _editingEntry.Action = (ActionType)actionCombo.SelectedIndex;
            _editingEntry.Target = this.FindControl<TextBox>("TargetText")?.Text ?? string.Empty;

            ApplyHotkeys();
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
            if (!ValidateTarget(actionType, target, out var normalizedTarget, out var targetError))
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
                AllowRiskyShortcut = allowRiskyShortcut
            };

            Hotkeys.Add(entry);
            ApplyHotkeys();

            if (entry.RegistrationStatus != "Active")
            {
                Hotkeys.Remove(entry);
                ApplyHotkeys();
                SetFieldError("KeyCombo", "Shortcut could not be registered");
                Program.PlaySound("error");
                return;
            }

            SaveConfig();
            ClearFieldErrors();
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
            if (this.FindControl<ListBox>("HotkeyList") is ListBox list && !ReferenceEquals(list.SelectedItem, entry))
            {
                list.SelectedItem = entry;
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
                
                if (entry.RegistrationStatus.StartsWith("Inactive:", StringComparison.OrdinalIgnoreCase))
                {
                    SetFieldError("KeyCombo", entry.RegistrationStatus.Substring("Inactive:".Length).Trim());
                }
                else
                {
                    ClearFieldErrors();
                }
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
            ClearFieldErrors();
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

        public void Enabled_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox chk || chk.DataContext is not HotkeyEntry entry) return;

            entry.IsEnabled = chk.IsChecked == true;
            ApplyHotkeys();
            SaveConfig();
            chk.Focus();
        }

        public async void Remove_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("close");
            if (sender is Button btn && btn.DataContext is HotkeyEntry entry)
            {
                if (!await ConfirmRemoveAsync(entry)) return;
                if (ReferenceEquals(_editingEntry, entry) || (_creatingDuplicate && entry.IsEditing)) ResetEditor();
                Hotkeys.Remove(entry);
                SaveConfig();
                ApplyHotkeys();
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
                parts.Add(e.Key.ToString());
            }

            if (sender is TextBox tb)
            {
                var txt = string.Join("+", parts);
                if (isModifierOnly && parts.Count > 0) txt += "+";
                tb.Text = txt;
            }
        }

        public void Settings_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("open");
            var isStartup = false;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                isStartup = key?.GetValue("KeyPulse") != null;
            }

            var w = CreateAppDialog("Settings", 460, 460, 420, 340, WindowStartupLocation.CenterOwner);

            var panel = CreateDialogPanel();

            panel.Children.Add(new TextBlock { Text = "Settings", Classes = { "SectionTitle" } });
            panel.Children.Add(new TextBlock { Text = "Startup", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 8, 0, 0) });
            
            var chk = new CheckBox { Content = "Launch on Boot", IsChecked = isStartup, TabIndex = 0, IsTabStop = true };
            var startupStatus = new TextBlock
            {
                Text = isStartup ? "KeyPulse is registered to start with Windows." : "KeyPulse is not registered to start with Windows.",
                Classes = { "Muted" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            var suppressStartupEvent = false;
            chk.IsCheckedChanged += (s, ev) =>
            {
                if (suppressStartupEvent) return;

                var requestedState = chk.IsChecked == true;
                chk.IsEnabled = false;
                if (Program.SetStartup(requestedState, out var startupError))
                {
                    startupStatus.Text = requestedState
                        ? "Launch on Boot is enabled. KeyPulse will start hidden in the tray."
                        : "Launch on Boot is disabled.";
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

            panel.Children.Add(new TextBlock { Text = "Backup & Restore", Classes = { "Label" }, Margin = new Avalonia.Thickness(0, 14, 0, 0) });
            var backupBtn = new Button { Content = "Backup Configuration", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, TabIndex = 2, IsTabStop = true };
            backupBtn.Classes.Add("Secondary");
            panel.Children.Add(backupBtn);

            var restoreBtn = new Button { Content = "Restore Configuration", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, TabIndex = 3, IsTabStop = true };
            restoreBtn.Classes.Add("Secondary");
            panel.Children.Add(restoreBtn);

            var statusTxt = new TextBlock { Margin = new Avalonia.Thickness(0,10,0,0), Foreground = AppBrush("AppWarningBrush"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "Muted" } };
            panel.Children.Add(statusTxt);

            var closeBtn = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 4, IsTabStop = true };
            closeBtn.Classes.Add("Secondary");
            closeBtn.Click += (s, ev) => w.Close();
            panel.Children.Add(closeBtn);

            backupBtn.Click += async (s, ev) =>
            {
                backupBtn.IsEnabled = false; restoreBtn.IsEnabled = false;
                try
                {
                    statusTxt.Text = "Choose a backup destination.";
                    statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                    var options = new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "Export Backup", DefaultExtension = "json", SuggestedFileName = "KeyPulse_Backup.json" };
                    var file = await w.StorageProvider.SaveFilePickerAsync(options);
                    if (file != null)
                    {
                        statusTxt.Text = "Exporting backup...";
                        statusTxt.Foreground = AppBrush("AppWarningBrush");
                        SaveConfig();
                        File.Copy(ConfigPath, file.Path.LocalPath, true);
                        statusTxt.Text = "Backup exported successfully.";
                        statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                    }
                    else
                    {
                        statusTxt.Text = "Backup canceled.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                    }
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
                    statusTxt.Text = "Choose a backup file.";
                    statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                    var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Import Backup", AllowMultiple = false };
                    var files = await w.StorageProvider.OpenFilePickerAsync(options);
                    if (files != null && files.Count > 0)
                    {
                        try {
                            statusTxt.Text = "Reading backup...";
                            statusTxt.Foreground = AppBrush("AppWarningBrush");
                            var json = File.ReadAllText(files[0].Path.LocalPath);
                            var loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                            if (loaded != null)
                            {
                                if (!ValidateImportedConfig(loaded, out var importError))
                                {
                                    statusTxt.Text = "Restore stopped: " + importError + "\nExisting shortcuts were kept.";
                                    statusTxt.Foreground = AppBrush("AppDangerBrush");
                                    return;
                                }

                                statusTxt.Text = "Validating hotkeys... please wait.";
                                statusTxt.Foreground = AppBrush("AppWarningBrush");

                                var previousConfig = CloneConfig(_currentConfig);

                                try
                                {
                                    _currentConfig = CloneConfig(loaded);
                                    Hotkeys.Clear();

                                    foreach (var item in _currentConfig.Hotkeys)
                                    {
                                        Hotkeys.Add(item);
                                    }

                                    var conflicts = ApplyHotkeys();
                                    if (conflicts > 0)
                                    {
                                        RestoreConfigSnapshot(previousConfig);
                                        statusTxt.Text = $"Restore stopped: {conflicts} shortcut(s) could not be activated.\nExisting shortcuts were kept.";
                                        statusTxt.Foreground = AppBrush("AppWarningBrush");
                                        return;
                                    }

                                    SaveConfig();
                                    statusTxt.Text = "Restore completed successfully.\nAll shortcuts are active.";
                                    statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                                }
                                catch
                                {
                                    RestoreConfigSnapshot(previousConfig);
                                    throw;
                                }
                            }
                        } catch (Exception ex) { statusTxt.Text = "Import failed: " + ex.Message; statusTxt.Foreground = AppBrush("AppDangerBrush"); }
                    }
                    else
                    {
                        statusTxt.Text = "Restore canceled.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                    }
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
            w.Opened += (s, ev) => chk.Focus();
            ShowDialogOrWindow(w);
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
            this.Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            HotkeyManager.Stop();
            base.OnClosed(e);
        }
    }
}









