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
                targetText.TextChanged += (s, e) => SetFieldError("TargetText", null);
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

        private void SaveConfig()
        {
            try
            {
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

                if (!ValidateHotkeyFormat(item.KeyCombination, false, out var hotkeyError))
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

                if (!ValidateHotkeyFormat(h.KeyCombination, false, out var hotkeyError))
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
                    ActionType.InsertText => "Text to paste",
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
                    ActionType.TypeText => "Best for short text. KeyPulse simulates typing character by character.",
                    ActionType.InsertText => "Best for longer snippets. KeyPulse uses the clipboard, then restores it when possible.",
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

        private static bool ValidateHotkeyFormat(string? combo, bool checkAvailability, out string error)
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

            if (IsRiskyTypingShortcut(modifiers, vk, out var riskyError))
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
            }
            catch (Exception ex)
            {
                ShowActionFailure(ex.Message);
            }
        }

        private void ShowActionFailure(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
            });
        }

        public void KeyCombo_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var tb = sender as TextBox;
            if (tb == null) return;

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

            if (!ValidateHotkeyFormat(tb.Text, _editingEntry == null, out var validationError))
            {
                SetFieldError("KeyCombo", validationError);
            }
            else
            {
                SetFieldError("KeyCombo", null);
            }
        }

        public void Add_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            ClearFieldErrors();
            var combo = this.FindControl<TextBox>("KeyCombo")?.Text;
            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            var isEditing = _editingEntry != null;

            if (actionCombo == null || actionCombo.SelectedIndex < 0)
            {
                SetFieldError("TargetText", "Choose an action");
                Program.PlaySound("error");
                return;
            }

            if (!ValidateHotkeyFormat(combo, !isEditing, out var hotkeyError))
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

            if (_editingEntry != null)
            {
                var previous = CaptureHotkeyState(_editingEntry);
                _editingEntry.KeyCombination = combo!;
                _editingEntry.Action = actionType;
                _editingEntry.Target = normalizedTarget;

                ApplyHotkeys();
                if (_editingEntry.IsEnabled && _editingEntry.RegistrationStatus != "Active")
                {
                    RestoreHotkeyState(_editingEntry, previous);
                    ApplyHotkeys();
                    SetFieldError("KeyCombo", "Shortcut could not be registered");
                    Program.PlaySound("error");
                    return;
                }

                SaveConfig();
                ClearFieldErrors();
                ResetEditor();
                return;
            }

            var entry = new HotkeyEntry
            {
                KeyCombination = combo!,
                Action = actionType,
                Target = normalizedTarget,
                IsEnabled = true
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

            if (this.FindControl<TextBox>("KeyCombo") is TextBox t) t.Text = "";
            if (this.FindControl<TextBox>("TargetText") is TextBox tg) tg.Text = "";
        }

        private readonly record struct HotkeyState(string KeyCombination, ActionType Action, string Target, bool IsEnabled);

        private static HotkeyState CaptureHotkeyState(HotkeyEntry entry)
        {
            return new HotkeyState(entry.KeyCombination, entry.Action, entry.Target, entry.IsEnabled);
        }

        private static void RestoreHotkeyState(HotkeyEntry entry, HotkeyState state)
        {
            entry.KeyCombination = state.KeyCombination;
            entry.Action = state.Action;
            entry.Target = state.Target;
            entry.IsEnabled = state.IsEnabled;
        }

        public void Edit_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HotkeyEntry entry) return;

            if (_editingEntry != null) _editingEntry.IsEditing = false;
            _editingEntry = entry;
            entry.IsEditing = true;
            if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = entry.KeyCombination;
            if (this.FindControl<ComboBox>("ActionCombo") is ComboBox actionCombo) actionCombo.SelectedIndex = (int)entry.Action;
            if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = entry.Target;

            if (this.FindControl<Button>("AddButton") is Button addButton) addButton.Content = "Save";
            if (this.FindControl<Button>("CancelEditButton") is Button cancelButton) cancelButton.IsVisible = true;
            if (this.FindControl<TextBlock>("EditorModeText") is TextBlock editText)
            {
                editText.Text = $"Editing {entry.KeyCombination} - {entry.ActionDisplay}";
                editText.IsVisible = true;
            }
            ClearFieldErrors();
            this.FindControl<TextBox>("KeyCombo")?.Focus();
        }

        public void CancelEdit_Click(object? sender, RoutedEventArgs e)
        {
            ResetEditor();
        }

        private void ResetEditor()
        {
            if (_editingEntry != null) _editingEntry.IsEditing = false;
            _editingEntry = null;
            if (this.FindControl<Button>("AddButton") is Button addButton) addButton.Content = "Add";
            if (this.FindControl<Button>("CancelEditButton") is Button cancelButton) cancelButton.IsVisible = false;
            if (this.FindControl<TextBlock>("EditorModeText") is TextBlock editText)
            {
                editText.Text = string.Empty;
                editText.IsVisible = false;
            }
            if (this.FindControl<TextBox>("KeyCombo") is TextBox keyCombo) keyCombo.Text = "";
            if (this.FindControl<TextBox>("TargetText") is TextBox targetText) targetText.Text = "";
            ClearFieldErrors();
            this.FindControl<TextBox>("KeyCombo")?.Focus();
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
                if (ReferenceEquals(_editingEntry, entry)) ResetEditor();
                Hotkeys.Remove(entry);
                SaveConfig();
                ApplyHotkeys();
            }
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
            if (this.IsVisible) await w.ShowDialog(this);
            else w.Show();
            return result;
        }

        public void KeyCombo_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            e.Handled = true;

            var mods = e.KeyModifiers;
            var parts = new System.Collections.Generic.List<string>();
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Meta)) parts.Add("Win");
            
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
            if (this.IsVisible) { w.ShowDialog(this); } else { w.Show(); }
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









