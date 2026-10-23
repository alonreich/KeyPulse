using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace KeyPulse
{
    public partial class MainWindow
    {
        private BackupPayload BuildBackupPayload()
        {
            CaptureShortcutColumnWidths();

            return new BackupPayload
            {
                UseGoogleChromeForUrls = _currentConfig.UseGoogleChromeForUrls,
                PreferredBrowser = _currentConfig.PreferredBrowser ?? "System",
                SoundEnabled = _currentConfig.SoundEnabled,
                TypingDelayMs = _currentConfig.TypingDelayMs,
                Theme = _currentConfig.Theme,
                AutoCheckForUpdates = _currentConfig.AutoCheckForUpdates,
                UpdateChannel = _currentConfig.UpdateChannel ?? "Stable",
                SkippedUpdateVersion = _currentConfig.SkippedUpdateVersion ?? string.Empty,
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
                    SettingsWindowState = _currentConfig.SettingsWindowState,
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
        private async System.Threading.Tasks.Task<BackupPassphraseChoice> AskBackupPassphraseAsync(Window? owner = null)
        {
            var w = CreateAppDialog("Protect this backup", 540, 390, 480, 350, WindowStartupLocation.CenterOwner);

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
                Text = "A backup contains the folder, program, link or text behind every shortcut - including anything marked with 'Hide secret text'. With a password the file is encrypted and useless to anyone else. Without one, its contents are readable by anyone who opens it.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Classes = { "Muted" },
                Margin = new Avalonia.Thickness(0, 8, 0, 12)
            };
            Grid.SetRow(explain, 1);
            grid.Children.Add(explain);

            var firstGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var first = new TextBox { PasswordChar = '●', PlaceholderText = "Password" };
            Grid.SetColumn(first, 0);
            var firstToggle = new Button
            {
                Content = "👁",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(firstToggle, "Show / Hide password");
            firstToggle.Click += (_, _) =>
            {
                bool hidden = first.PasswordChar == '●';
                first.PasswordChar = hidden ? '\0' : '●';
                firstToggle.Content = hidden ? "🙈" : "👁";
            };
            Grid.SetColumn(firstToggle, 1);
            firstGrid.Children.Add(first);
            firstGrid.Children.Add(firstToggle);
            Grid.SetRow(firstGrid, 2);
            grid.Children.Add(firstGrid);

            var secondGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 8, 0, 0) };
            var second = new TextBox { PasswordChar = '●', PlaceholderText = "Type it again" };
            Grid.SetColumn(second, 0);
            var secondToggle = new Button
            {
                Content = "👁",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(secondToggle, "Show / Hide password");
            secondToggle.Click += (_, _) =>
            {
                bool hidden = second.PasswordChar == '●';
                second.PasswordChar = hidden ? '\0' : '●';
                secondToggle.Content = hidden ? "🙈" : "👁";
            };
            Grid.SetColumn(secondToggle, 1);
            secondGrid.Children.Add(second);
            secondGrid.Children.Add(secondToggle);
            Grid.SetRow(secondGrid, 3);
            grid.Children.Add(secondGrid);

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
            await ShowDialogOrWindowAsync(w, owner);

            return new BackupPassphraseChoice { Cancelled = cancelled, Passphrase = chosen };
        }

        /// <summary>
        /// ISSUE_24: asks for the password of an encrypted backup and lets the user try again.
        /// Returns null when the user gave up, having already written the reason into statusTxt.
        /// </summary>
        private async System.Threading.Tasks.Task<BackupPayload?> UnlockBackupAsync(BackupInspection inspection, TextBlock statusTxt, Window? owner = null)
        {
            var attemptProblem = string.Empty;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var passphrase = await AskUnlockPassphraseAsync(attemptProblem, owner);
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

        private async System.Threading.Tasks.Task<string?> AskUnlockPassphraseAsync(string problemText, Window? owner = null)
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

            var boxGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Avalonia.Thickness(0, 12, 0, 0)
            };
            var box = new TextBox
            {
                PasswordChar = '●',
                PlaceholderText = "Password"
            };
            Grid.SetColumn(box, 0);
            var boxToggle = new Button
            {
                Content = "👁",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(boxToggle, "Show / Hide password");
            boxToggle.Click += (_, _) =>
            {
                bool hidden = box.PasswordChar == '●';
                box.PasswordChar = hidden ? '\0' : '●';
                boxToggle.Content = hidden ? "🙈" : "👁";
            };
            Grid.SetColumn(boxToggle, 1);
            boxGrid.Children.Add(box);
            boxGrid.Children.Add(boxToggle);
            Grid.SetRow(boxGrid, 1);
            grid.Children.Add(boxGrid);

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
            await ShowDialogOrWindowAsync(w, owner);
            return result;
        }

        /// <summary>ISSUE_1: say exactly what is about to be replaced, before replacing it.</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmRestoreAsync(
            BackupInspection inspection,
            BackupPayload payload,
            string fileName,
            List<string> warnings,
            Action<bool> setRestoreLayout,
            Window? owner = null)
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
            await ShowDialogOrWindowAsync(w, owner);
            return result;
        }

        /// <summary>
        /// Imports everything, then reports what needs attention. Rolling the whole restore back
        /// because a single key was taken made this feature useless on a new PC, which is its main
        /// use. Returns the number of shortcuts that could not be switched on here.
        /// </summary>
        private int ApplyBackupPayload(BackupPayload payload, bool restoreLayout, out string layoutNote, out bool needsBrightnessConsent)
        {
            layoutNote = string.Empty;
            needsBrightnessConsent = false;

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
            _currentConfig.PreferredBrowser = string.IsNullOrWhiteSpace(payload.PreferredBrowser) ? (payload.UseGoogleChromeForUrls ? "Chrome" : "System") : payload.PreferredBrowser;
            _currentConfig.SoundEnabled = payload.SoundEnabled;
            _currentConfig.TypingDelayMs = NormalizeTypingDelay(payload.TypingDelayMs);
            _currentConfig.Theme = string.IsNullOrWhiteSpace(payload.Theme) ? "System" : payload.Theme;
            _currentConfig.AutoCheckForUpdates = payload.AutoCheckForUpdates;
            _currentConfig.UpdateChannel = string.IsNullOrWhiteSpace(payload.UpdateChannel) ? "Stable" : payload.UpdateChannel;
            _currentConfig.SkippedUpdateVersion = payload.SkippedUpdateVersion ?? string.Empty;
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
                needsBrightnessConsent = BackupService.PrepareScreenBrightnessForRestore(
                    payload.ScreenBrightness,
                    ScreenBrightnessService.LoadConfig().DisclaimerAccepted);
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
            if (!string.IsNullOrWhiteSpace(layout.SettingsWindowState))
            {
                _currentConfig.SettingsWindowState = layout.SettingsWindowState;
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
    }
}
