using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Win32;

namespace KeyPulse
{
    public partial class MainWindow
    {
        private async System.Threading.Tasks.Task<bool> ConfirmSettingsActionAsync(Window owner, string title, string message, string confirmButtonText, bool isDanger = true)
        {
            var dialog = CreateAppDialog(title, 460, 250, 420, 210, WindowStartupLocation.CenterOwner, false, false);
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

            if (_activeSettingsWindow != null)
            {
                _activeSettingsWindow.Activate();
                _activeSettingsWindow.Focus();
                return;
            }
            this.Hide();

            double settingsW = _currentConfig.SettingsWindowWidth;
            double settingsH = _currentConfig.SettingsWindowHeight;
            if (double.IsNaN(settingsW) || settingsW < 580) settingsW = 720;
            if (double.IsNaN(settingsH) || settingsH < 500) settingsH = 700;

            if (double.IsNaN(_currentConfig.SettingsWindowX))
            {
                var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (screen != null)
                {
                    var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                    var dipWidth = screen.WorkingArea.Width / scale;
                    var dipHeight = screen.WorkingArea.Height / scale;
                    settingsW = Math.Clamp(720 * dipWidth / 1920.0, 580, Math.Max(580, dipWidth));
                    settingsH = Math.Clamp(700 * dipHeight / 1080.0, 500, Math.Max(500, dipHeight));
                }
            }

            var w = CreateAppDialog("Settings", settingsW, settingsH, 580, 500, WindowStartupLocation.CenterOwner);
            _activeSettingsWindow = w;
            w.Closed += (_, _) => { _activeSettingsWindow = null; };

            if (Enum.TryParse<WindowState>(_currentConfig.SettingsWindowState, out var ws) && ws == WindowState.Maximized)
            {
                w.WindowState = WindowState.Maximized;
            }

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
            titleStack.Children.Add(new TextBlock { Text = "Customize startup, privileges, appearance, typing speed, and backups.", Classes = { "Muted" }, FontSize = 11.5 });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var searchPanel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Width = 260,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var searchBox = new TextBox
            {
                PlaceholderText = "Search settings...",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TabIndex = 1,
                IsTabStop = true
            };
            ToolTip.SetTip(searchBox, "Type any keyword (e.g. startup, admin, theme, sound, typing, brightness, backup) to filter all settings instantly.");
            var clearSearchBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                IsVisible = false,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 2),
                FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(clearSearchBtn, "Clear search filter");
            clearSearchBtn.Click += (_, _) =>
            {
                searchBox.Text = string.Empty;
                searchBox.Focus();
            };
            searchBox.TextChanged += (_, _) =>
            {
                clearSearchBtn.IsVisible = !string.IsNullOrEmpty(searchBox.Text);
            };
            searchBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Avalonia.Input.Key.Escape && !string.IsNullOrEmpty(searchBox.Text))
                {
                    searchBox.Text = string.Empty;
                    ke.Handled = true;
                }
            };
            Grid.SetColumn(searchBox, 0);
            Grid.SetColumn(clearSearchBtn, 1);
            searchPanel.Children.Add(searchBox);
            searchPanel.Children.Add(clearSearchBtn);
            Grid.SetColumn(searchPanel, 1);
            headerGrid.Children.Add(searchPanel);
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            string[] tabNames = { "General", "Appearance", "Displays & Icons", "Backup & Updates" };
            string[] tabDescriptions = {
                "Configure Windows startup behavior, privileges, sound effects, preferred browser, and typing speed.",
                "Customize visual theme (System, Light, Dark) and interface appearance across KeyPulse.",
                "Manage multi-monitor screen brightness synchronization and automated desktop icon layouts.",
                "Check for updates, manage update channels, export/import backups, and factory reset."
            };

            var tabStrip = new WrapPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = new Avalonia.Thickness(4, 0, 4, 0)
            };

            var tabEars = new Border[tabNames.Length];
            var tabTexts = new TextBlock[tabNames.Length];
            for (int i = 0; i < tabNames.Length; i++)
            {
                var text = new TextBlock
                {
                    Text = tabNames[i],
                    FontSize = 12.5,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                tabTexts[i] = text;

                var ear = new Border
                {
                    CornerRadius = new Avalonia.CornerRadius(6, 6, 0, 0),
                    Padding = new Avalonia.Thickness(16, 9),
                    Margin = new Avalonia.Thickness(0, 0, 6, 0),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Focusable = true,
                    TabIndex = 2 + i,
                    IsTabStop = true,
                    Background = AppBrush("AppPanelBrush"),
                    BorderBrush = AppBrush("AppBorderBrush"),
                    BorderThickness = new Avalonia.Thickness(1, 1, 1, 1),
                    Child = text
                };
                Avalonia.Automation.AutomationProperties.SetName(ear, $"{tabNames[i]} Settings Tab");
                tabEars[i] = ear;
                tabStrip.Children.Add(ear);
            }
            Grid.SetRow(tabStrip, 1);
            rootGrid.Children.Add(tabStrip);

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
                Content = "Start KeyPulse automatically with Windows",
                IsEnabled = false,
                TabIndex = 10,
                IsTabStop = true
            };
            ToolTip.SetTip(chk, "Automatically launch KeyPulse hidden in your system tray whenever you sign in to Windows.");
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

            bool isElevated = Program.IsElevated;
            bool isWiredAdmin = Program.IsAdminTaskInstalled();

            var adminBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var adminBadgeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            };
            adminBadge.Child = adminBadgeText;

            var adminStatusDesc = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            var adminBtn = new Button
            {
                Classes = { "Secondary" },
                Padding = new Thickness(14, 7),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                TabIndex = 11,
                IsTabStop = true
            };

            void RefreshAdminButtonState(bool elevated, bool wiredAdmin)
            {
                bool isLight = string.Equals(_currentConfig.Theme, "Light", StringComparison.OrdinalIgnoreCase) ||
                               (string.Equals(_currentConfig.Theme, "System", StringComparison.OrdinalIgnoreCase) && Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light);

                if (elevated)
                {
                    adminBadge.Background = new SolidColorBrush(Color.Parse(isLight ? "#DCFCE7" : "#143522"));
                    adminBadge.BorderBrush = new SolidColorBrush(Color.Parse(isLight ? "#86EFAC" : "#166534"));
                    adminBadge.BorderThickness = new Thickness(1);
                    adminBadgeText.Text = "Administrator (Elevated Session)";
                    adminBadgeText.Foreground = new SolidColorBrush(Color.Parse(isLight ? "#166534" : "#4ADE80"));
                    adminStatusDesc.Text = "Full permissions active. Shortcuts, typing, and hotkeys work inside elevated Command Prompt, PowerShell, Task Manager, and Admin windows.";
                    adminBtn.Content = "Revert to Standard User";
                    adminBtn.Classes.Remove("Primary");
                    adminBtn.Classes.Remove("Danger");
                    if (!adminBtn.Classes.Contains("Secondary")) adminBtn.Classes.Add("Secondary");
                    ToolTip.SetTip(adminBtn, "Revert KeyPulse to standard user permissions and restart.");
                }
                else if (wiredAdmin)
                {
                    adminBadge.Background = new SolidColorBrush(Color.Parse(isLight ? "#FEF3C7" : "#382806"));
                    adminBadge.BorderBrush = new SolidColorBrush(Color.Parse(isLight ? "#FCD34D" : "#854D0E"));
                    adminBadge.BorderThickness = new Thickness(1);
                    adminBadgeText.Text = "Admin Task Configured (Session Standard)";
                    adminBadgeText.Foreground = new SolidColorBrush(Color.Parse(isLight ? "#92400E" : "#FBBF24"));
                    adminStatusDesc.Text = "The Administrator logon task is configured in Windows, but this current session is running without Administrator rights.";
                    adminBtn.Content = "Restart as Administrator";
                    adminBtn.Classes.Remove("Secondary");
                    if (!adminBtn.Classes.Contains("Primary")) adminBtn.Classes.Add("Primary");
                    ToolTip.SetTip(adminBtn, "Click to restart KeyPulse with Administrator rights matching the logon task.");
                }
                else
                {
                    adminBadge.Background = new SolidColorBrush(Color.Parse(isLight ? "#E2E8F0" : "#1E293B"));
                    adminBadge.BorderBrush = new SolidColorBrush(Color.Parse(isLight ? "#CBD5E1" : "#334155"));
                    adminBadge.BorderThickness = new Thickness(1);
                    adminBadgeText.Text = "Standard User (Normal Isolation)";
                    adminBadgeText.Foreground = new SolidColorBrush(Color.Parse(isLight ? "#334155" : "#94A3B8"));
                    adminStatusDesc.Text = "Running as standard user. Shortcuts cannot inject keystrokes into windows running as Administrator.";
                    adminBtn.Content = "Restart as Administrator";
                    adminBtn.Classes.Remove("Secondary");
                    if (!adminBtn.Classes.Contains("Primary")) adminBtn.Classes.Add("Primary");
                    ToolTip.SetTip(adminBtn, "Restart KeyPulse with Administrator rights to allow hotkeys and typing inside elevated Admin tools.");
                }
            }
            RefreshAdminButtonState(isElevated, isWiredAdmin);

            var sysIntegrationBody = new StackPanel { Spacing = 10 };
            sysIntegrationBody.Children.Add(chk);
            sysIntegrationBody.Children.Add(startupStatus);
            sysIntegrationBody.Children.Add(new Border
            {
                Height = 1,
                Background = AppBrush("AppBorderBrush"),
                Margin = new Thickness(0, 4)
            });

            var privHeaderStack = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            privHeaderStack.Children.Add(new TextBlock
            {
                Text = "Windows Administrator Privileges",
                FontWeight = FontWeight.SemiBold,
                FontSize = 12.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            privHeaderStack.Children.Add(adminBadge);
            sysIntegrationBody.Children.Add(privHeaderStack);
            sysIntegrationBody.Children.Add(adminStatusDesc);
            sysIntegrationBody.Children.Add(adminBtn);

            RegisterSettingCard(0, "Windows Integration & Privileges",
                "Controls whether KeyPulse starts automatically with Windows and whether it runs with Administrator rights to inject keys into elevated consoles and admin apps.",
                "startup boot launch windows login automatic tray admin administrator privilege elevate uac runas rights",
                sysIntegrationBody);

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


            var speedPresetPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };
            int[] speedPresetValues = { TypingDelayFast, TypingDelayNormal, TypingDelayCompatible };
            string[] speedPresetNames = { "Fast (1 ms — Recommended)", "Normal (6 ms)", "Compatible (18 ms)" };
            var speedPresetButtons = new Button[speedPresetValues.Length];

            var speedCustomPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0)
            };
            speedCustomPanel.Children.Add(new TextBlock
            {
                Text = "Custom delay:",
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
                TabIndex = 24,
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
                TabIndex = 25,
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
                TabIndex = 26,
                IsTabStop = true
            };
            ToolTip.SetTip(speedPlusBtn, "Increase typing delay by 1 ms (more compatible with slow apps).");
            speedCustomPanel.Children.Add(speedMinusBtn);
            speedCustomPanel.Children.Add(speedCustomBox);
            speedCustomPanel.Children.Add(speedPlusBtn);
            speedCustomPanel.Children.Add(new TextBlock
            {
                Text = "ms pause between characters (1–200 ms)",
                Classes = { "Muted" },
                FontSize = 11.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            void SyncSpeedUiFromValue(int delayMs)
            {
                int clamped = NormalizeTypingDelay(delayMs);
                speedCustomBox.Text = clamped.ToString();
                for (int i = 0; i < speedPresetValues.Length; i++)
                {
                    bool isMatch = speedPresetValues[i] == clamped;
                    speedPresetButtons[i].Classes.Remove("Primary");
                    speedPresetButtons[i].Classes.Remove("Secondary");
                    speedPresetButtons[i].Classes.Add(isMatch ? "Primary" : "Secondary");
                }
                speedMinusBtn.IsEnabled = clamped > 1;
                speedPlusBtn.IsEnabled = clamped < 200;
            }

            void ApplyTypingDelayMs(int newDelay)
            {
                int clamped = NormalizeTypingDelay(newDelay);
                _currentConfig.TypingDelayMs = clamped;
                InputSimulator.CharacterDelayMs = clamped;
                suppressSyncEvents = true;
                try
                {
                    SyncSpeedUiFromValue(clamped);
                }
                finally
                {
                    suppressSyncEvents = false;
                }
                SaveConfig();
            }

            for (int i = 0; i < speedPresetValues.Length; i++)
            {
                int presetVal = speedPresetValues[i];
                var spBtn = new Button
                {
                    Content = speedPresetNames[i],
                    Classes = { _currentConfig.TypingDelayMs == presetVal ? "Primary" : "Secondary" },
                    Padding = new Thickness(10, 5),
                    FontSize = 11.5,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    TabIndex = 21 + i,
                    IsTabStop = true
                };
                spBtn.Click += (_, _) =>
                {
                    Program.PlaySound("click");
                    ApplyTypingDelayMs(presetVal);
                };
                speedPresetButtons[i] = spBtn;
                speedPresetPanel.Children.Add(spBtn);
            }
            SyncSpeedUiFromValue(_currentConfig.TypingDelayMs);

            speedMinusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ApplyTypingDelayMs(_currentConfig.TypingDelayMs - 1);
            };
            speedPlusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ApplyTypingDelayMs(_currentConfig.TypingDelayMs + 1);
            };
            var speedErrorText = new TextBlock
            {
                Classes = { "ErrorText" },
                FontSize = 11,
                IsVisible = false,
                Margin = new Thickness(0, 2, 0, 0)
            };

            void CommitCustomSpeedBox()
            {
                if (suppressSyncEvents) return;
                if (int.TryParse(speedCustomBox.Text?.Trim(), out int parsed) && parsed >= 1 && parsed <= 200)
                {
                    speedErrorText.IsVisible = false;
                    ApplyTypingDelayMs(parsed);
                }
                else
                {
                    speedErrorText.Text = "Please enter a valid delay between 1 and 200 ms.";
                    speedErrorText.IsVisible = true;
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

            var speedBody = new StackPanel { Spacing = 6 };
            speedBody.Children.Add(speedPresetPanel);
            speedBody.Children.Add(speedCustomPanel);
            speedBody.Children.Add(speedErrorText);
            speedBody.Children.Add(new TextBlock
            {
                Text = "Only affects the 'Type text' action. Increase the delay if characters are skipped in older consoles, VMs, or remote desktop sessions.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            RegisterSettingCard(0, "Simulated Typing Speed",
                "Controls the pause (in milliseconds) between keystrokes injected by the 'Type text' action. Fast (1 ms) is instant; Compatible (18 ms) or Custom prevents dropped characters in laggy remote sessions.",
                "typing speed delay milliseconds ms fast normal compatible console remote rdp",
                speedBody);

            var browserPanel = new StackPanel { Spacing = 6 };
            var browserRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            browserRow.Children.Add(new TextBlock
            {
                Text = "Preferred browser:",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            var browserCombo = new ComboBox
            {
                ItemsSource = new[]
                {
                    "System Default (Recommended)",
                    "Google Chrome",
                    "Microsoft Edge",
                    "Mozilla Firefox",
                    "Brave Browser"
                },
                Width = 240,
                TabIndex = 27,
                IsTabStop = true
            };
            ToolTip.SetTip(browserCombo, "Select which browser opens when launching web link shortcuts. 'System Default' uses whatever browser is configured in Windows.");

            string[] browserKeys = { "System", "Chrome", "Edge", "Firefox", "Brave" };
            int GetBrowserKeyIndex(string? key)
            {
                for (int i = 0; i < browserKeys.Length; i++)
                {
                    if (string.Equals(browserKeys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
                }
                return 0;
            }

            browserRow.Children.Add(browserCombo);
            browserPanel.Children.Add(browserRow);

            var browserStatusText = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            browserPanel.Children.Add(browserStatusText);

            void RefreshBrowserStateUi()
            {
                string pref = _currentConfig.PreferredBrowser ?? (_currentConfig.UseGoogleChromeForUrls ? "Chrome" : "System");
                browserCombo.SelectedIndex = GetBrowserKeyIndex(pref);

                if (string.Equals(pref, "System", StringComparison.OrdinalIgnoreCase))
                {
                    browserStatusText.Text = "✓ Web links will launch in your default Windows browser (Edge, Chrome, Firefox, Brave, etc.).";
                    browserStatusText.Foreground = AppBrush("AppTextMutedBrush");
                }
                else if (TryFindBrowser(pref, out _))
                {
                    browserStatusText.Text = $"✓ {pref} detected on this PC. Web link shortcuts will launch explicitly in {pref}.";
                    browserStatusText.Foreground = AppBrush("AppSuccessSoftBrush");
                }
                else
                {
                    browserStatusText.Text = $"⚠️ {pref} was not detected on this PC — web links will automatically fall back to your default Windows browser.";
                    browserStatusText.Foreground = AppBrush("AppWarningBrush");
                }
            }

            browserCombo.SelectionChanged += (_, _) =>
            {
                if (suppressSyncEvents) return;
                int idx = Math.Clamp(browserCombo.SelectedIndex, 0, browserKeys.Length - 1);
                _currentConfig.PreferredBrowser = browserKeys[idx];
                _currentConfig.UseGoogleChromeForUrls = (browserKeys[idx] == "Chrome");
                RefreshBrowserStateUi();
                SaveConfig();
            };
            RefreshBrowserStateUi();

            RegisterSettingCard(0, "Web Browser for URLs",
                "Determines whether website shortcuts explicitly launch your selected browser or open in your default Windows browser.",
                "browser google chrome url web link website edge firefox brave default",
                browserPanel);

            var themeSegmentedPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };
            string[] themeOptions = { "Match Windows (Default)", "Light", "Dark" };
            string[] themeValues = { "System", "Light", "Dark" };
            var themeSegmentButtons = new Button[themeOptions.Length];
            for (int i = 0; i < themeOptions.Length; i++)
            {
                int capturedIdx = i;
                var tBtn = new Button
                {
                    Content = themeOptions[i],
                    Classes = { string.Equals(_currentConfig.Theme, themeValues[i], StringComparison.OrdinalIgnoreCase) ? "Primary" : "Secondary" },
                    Padding = new Thickness(14, 6),
                    FontSize = 12,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    TabIndex = 30 + i,
                    IsTabStop = true
                };
                tBtn.Click += (_, _) =>
                {
                    Program.PlaySound("click");
                    _currentConfig.Theme = themeValues[capturedIdx];
                    App.ApplyTheme(_currentConfig.Theme);
                    SaveConfig();
                    for (int j = 0; j < themeSegmentButtons.Length; j++)
                    {
                        themeSegmentButtons[j].Classes.Remove("Primary");
                        themeSegmentButtons[j].Classes.Remove("Secondary");
                        themeSegmentButtons[j].Classes.Add(j == capturedIdx ? "Primary" : "Secondary");
                    }
                };
                themeSegmentButtons[i] = tBtn;
                themeSegmentedPanel.Children.Add(tBtn);
            }
            var themeBody = new StackPanel { Spacing = 6 };
            themeBody.Children.Add(themeSegmentedPanel);
            themeBody.Children.Add(new TextBlock
            {
                Text = "Switches all KeyPulse windows and dialogs between Light and Dark color schemes immediately.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            RegisterSettingCard(1, "Appearance & Theme",
                "Changes the visual color theme across KeyPulse. 'Match Windows' automatically switches between Light and Dark based on your Windows system settings.",
                "appearance theme dark light mode color windows",
                themeBody);

            var sbCfg = ScreenBrightnessService.LoadConfig();

            var sbEnableChk = new CheckBox
            {
                Content = "Enable Screen Brightness & Hardware Display Control",
                IsChecked = sbCfg.IsFeatureActive,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                TabIndex = 32,
                IsTabStop = true
            };
            ToolTip.SetTip(sbEnableChk, "When unchecked, KeyPulse completely disables all Screen Brightness menus, shortcuts, background monitor polling, and DDC/CI communications.");

            var sbStatusText = new TextBlock
            {
                Text = sbCfg.IsFeatureActive
                    ? "Hardware display control is active."
                    : "Hardware display control is completely turned off. No hardware queries or DDC/CI communications will occur.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            var sbControlsPanel = new StackPanel
            {
                Spacing = 6,
                IsVisible = sbCfg.IsFeatureActive
            };

            sbEnableChk.IsCheckedChanged += async (s, ev) =>
            {
                if (suppressSyncEvents) return;
                var cfg = ScreenBrightnessService.LoadConfig();
                bool wantEnabled = sbEnableChk.IsChecked == true;

                if (wantEnabled)
                {
                    if (!cfg.DisclaimerAccepted)
                    {
                        bool accepted = await ScreenBrightnessService.ShowDisclaimerDialogAsync(w);
                        if (!accepted)
                        {
                            suppressSyncEvents = true;
                            sbEnableChk.IsChecked = false;
                            suppressSyncEvents = false;
                            return;
                        }
                    }
                    else
                    {
                        cfg.Enabled = true;
                        ScreenBrightnessService.SaveConfig(cfg);
                        ScreenBrightnessService.RegisterBrightnessHotkeys();
                        ScreenBrightnessService.NotifyStateChanged();
                    }
                }
                else
                {
                    cfg.Enabled = false;
                    ScreenBrightnessService.SaveConfig(cfg);
                    ScreenBrightnessService.UnregisterAllBrightnessHotkeys();
                    ScreenBrightnessService.InvalidateMonitorCache();
                    ScreenBrightnessService.NotifyStateChanged();
                }

                bool active = ScreenBrightnessService.IsFeatureActive;
                sbControlsPanel.IsVisible = active;
                sbStatusText.Text = active
                    ? "Hardware display control is active."
                    : "Hardware display control is completely turned off. No hardware queries or DDC/CI communications will occur.";
            };
            var sbLockChk = new CheckBox
            {
                Content = "Lock brightness and contrast together across all screens",
                IsChecked = sbCfg.GlobalLockBrightnessContrast,
                TabIndex = 33,
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
                Content = "Open Screen Brightness Manager...",
                Classes = { "Secondary" },
                Width = 270,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                TabIndex = 34,
                IsTabStop = true
            };
            ToolTip.SetTip(openBrightnessBtn, "Open the Screen Brightness & Contrast manager to tune per-monitor sliders and shortcut cycle presets.");
            openBrightnessBtn.Click += (s, ev) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessWindow.OpenWindow(w);
            };
            sbControlsPanel.Children.Add(sbLockChk);
            sbControlsPanel.Children.Add(new TextBlock
            {
                Text = "Adjust hardware monitor brightness & contrast via DDC/CI or laptop backlight and configure in-game shortcut cycle presets.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            sbControlsPanel.Children.Add(openBrightnessBtn);

            var sbBody = new StackPanel { Spacing = 8 };
            sbBody.Children.Add(sbEnableChk);
            sbBody.Children.Add(sbStatusText);
            sbBody.Children.Add(sbControlsPanel);
            RegisterSettingCard(2, "Screen Brightness & Contrast",
                "Manage hardware monitor brightness, contrast ratio syncing, and shortcut cycle presets without OSD popups or focus loss.",
                "screen brightness contrast monitor display ddc backlight preset cycle sync lock",
                sbBody);

            var doCfg = DesktopOrganizerService.LoadConfig();
            var doAutoRestoreCheck = new CheckBox
            {
                Content = "Automatically restore icon positions on display or resolution change",
                IsChecked = doCfg.AutoRestoreOnDisplayChange,
                TabIndex = 35,
                IsTabStop = true
            };
            ToolTip.SetTip(doAutoRestoreCheck, "When checked, KeyPulse automatically detects display connection, disconnection, or resolution changes and restores your saved desktop icon positions.");
            doAutoRestoreCheck.IsCheckedChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                var cfg = DesktopOrganizerService.LoadConfig();
                cfg.AutoRestoreOnDisplayChange = doAutoRestoreCheck.IsChecked == true;
                DesktopOrganizerService.SaveConfig(cfg, out _);
                DesktopOrganizerService.RaiseProfilesChanged();
            };

            var doAutoBackupCheck = new CheckBox
            {
                Content = "Automatically create periodic backup snapshots of desktop icons",
                IsChecked = doCfg.AutoPeriodicSnapshotEnabled,
                TabIndex = 36,
                IsTabStop = true
            };
            ToolTip.SetTip(doAutoBackupCheck, "When checked, KeyPulse automatically creates periodic background snapshots of your desktop icon positions so you can always roll back after messy reorganizations.");
            doAutoBackupCheck.IsCheckedChanged += (s, ev) =>
            {
                if (suppressSyncEvents) return;
                var cfg = DesktopOrganizerService.LoadConfig();
                cfg.AutoPeriodicSnapshotEnabled = doAutoBackupCheck.IsChecked == true;
                DesktopOrganizerService.SaveConfig(cfg, out _);
                DesktopOrganizerService.RaiseProfilesChanged();
            };

            var doSummaryText = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            void RefreshDesktopOrganizerSummary()
            {
                var curCfg = DesktopOrganizerService.LoadConfig();
                int manualCount = curCfg.Profiles.Count(p => !p.IsAutoSnapshot);
                int autoCount = curCfg.Profiles.Count(p => p.IsAutoSnapshot);
                string autoRestoreState = curCfg.AutoRestoreOnDisplayChange ? "On" : "Off";
                string autoSnapState = curCfg.AutoPeriodicSnapshotEnabled ? $"Every {Math.Clamp(curCfg.AutoSnapshotIntervalDays, 1, 30)}d" : "Off";
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
                TabIndex = 37,
                IsTabStop = true
            };
            ToolTip.SetTip(openOrganizerBtn, "Open Desktop Icon Organizer to save, restore, or automate desktop icon layouts across your monitors.");
            openOrganizerBtn.Click += (s, ev) =>
            {
                Program.PlaySound("click");
                DesktopOrganizerWindow.OpenOrganizer(w);
            };
            var doBody = new StackPanel { Spacing = 6 };
            doBody.Children.Add(doAutoRestoreCheck);
            doBody.Children.Add(doAutoBackupCheck);
            doBody.Children.Add(doSummaryText);
            doBody.Children.Add(openOrganizerBtn);
            RegisterSettingCard(2, "Desktop Icon Organizer",
                "Saves and restores your desktop icon positions across single or multi-monitor setups, with optional auto-restore when displays connect or change resolution.",
                "desktop icon organizer layout restore snapshot monitor grid",
                doBody);


            var autoUpdateCheck = new CheckBox
            {
                Content = "Automatically check for updates on startup",
                IsChecked = _currentConfig.AutoCheckForUpdates,
                TabIndex = 38,
                IsTabStop = true
            };
            ToolTip.SetTip(autoUpdateCheck, "When checked, KeyPulse quietly checks GitHub releases for updates in the background and notifies you if a new version is available.");

            var channelPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            channelPanel.Children.Add(new TextBlock
            {
                Text = "Update channel:",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var channelCombo = new ComboBox
            {
                ItemsSource = new[] { "Stable (Recommended)", "Preview / Beta" },
                SelectedIndex = string.Equals(_currentConfig.UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Width = 200,
                TabIndex = 39,
                IsTabStop = true
            };
            ToolTip.SetTip(channelCombo, "Select which release channel to receive updates from. Stable is thoroughly tested; Preview includes early experimental features.");
            channelCombo.SelectionChanged += (_, _) =>
            {
                if (suppressSyncEvents) return;
                _currentConfig.UpdateChannel = channelCombo.SelectedIndex == 1 ? "Beta" : "Stable";
                SaveConfig();
            };
            channelPanel.Children.Add(channelCombo);

            void RefreshUpdateChannelInteractivity()
            {
                bool enabled = autoUpdateCheck.IsChecked == true;
                channelCombo.IsEnabled = enabled;
                channelPanel.Opacity = enabled ? 1.0 : 0.55;
            }

            autoUpdateCheck.IsCheckedChanged += (_, _) =>
            {
                if (suppressSyncEvents) return;
                _currentConfig.AutoCheckForUpdates = autoUpdateCheck.IsChecked == true;
                RefreshUpdateChannelInteractivity();
                SaveConfig();
            };
            RefreshUpdateChannelInteractivity();

            var checkUpdatesBtn = new Button
            {
                Content = "Check for Updates Now",
                Classes = { "Secondary" },
                Padding = new Thickness(14, 6),
                FontSize = 12,
                TabIndex = 40,
                IsTabStop = true
            };
            ToolTip.SetTip(checkUpdatesBtn, "Immediately check GitHub for a newer version of KeyPulse.");

            var updateStatusTxt = new TextBlock
            {
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            checkUpdatesBtn.Click += async (_, _) =>
            {
                Program.PlaySound("click");
                checkUpdatesBtn.IsEnabled = false;
                updateStatusTxt.Text = "Checking for updates...";
                updateStatusTxt.Foreground = AppBrush("AppWarningBrush");
                try
                {
                    var result = await UpdateChecker.CheckAsync(_currentConfig.UpdateChannel ?? "Stable");
                    if (!result.Succeeded)
                    {
                        updateStatusTxt.Text = $"Update check failed: {result.Message}";
                        updateStatusTxt.Foreground = AppBrush("AppDangerBrush");
                    }
                    else if (result.UpdateAvailable)
                    {
                        updateStatusTxt.Text = $"v{result.LatestVersion} is available!";
                        updateStatusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                        ShowUpdateNotification(result);
                    }
                    else
                    {
                        updateStatusTxt.Text = $"KeyPulse is up to date (v{Program.AppVersion}).";
                        updateStatusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                    }
                }
                catch (Exception ex)
                {
                    updateStatusTxt.Text = $"Update check error: {ex.Message}";
                    updateStatusTxt.Foreground = AppBrush("AppDangerBrush");
                }
                finally
                {
                    checkUpdatesBtn.IsEnabled = true;
                }
            };

            var updateBtnRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 4, 0, 0)
            };
            updateBtnRow.Children.Add(checkUpdatesBtn);
            updateBtnRow.Children.Add(updateStatusTxt);

            var updatesBody = new StackPanel { Spacing = 6 };
            updatesBody.Children.Add(new TextBlock
            {
                Text = $"KeyPulse v{Program.AppVersion} • Fast, keyboard-first automation & screen utility for Windows.",
                Classes = { "Muted" },
                FontSize = 11.5
            });
            updatesBody.Children.Add(autoUpdateCheck);
            updatesBody.Children.Add(channelPanel);
            updatesBody.Children.Add(updateBtnRow);
            RegisterSettingCard(3, "About & Software Updates",
                "Application version information, background update check toggles, update channel selection (Stable vs Beta), and manual update checks.",
                "about version update updates release check github channel stable beta preview upgrade",
                updatesBody);

            var backupBtn = new Button
            {
                Content = "Backup Settings...",
                Classes = { "Primary" },
                MinWidth = 180,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TabIndex = 41,
                IsTabStop = true
            };
            ToolTip.SetTip(backupBtn, "Export all shortcuts, preferences, window layouts, desktop icon profiles, and brightness presets to a single file (with optional AES-GCM password encryption).");

            var restoreBtn = new Button
            {
                Content = "Import Settings...",
                Classes = { "Secondary" },
                MinWidth = 180,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TabIndex = 42,
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

            var revealExportBtn = new Button
            {
                Content = "Show in File Explorer",
                Classes = { "Secondary" },
                Margin = new Avalonia.Thickness(10, 0, 0, 6),
                IsVisible = false,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                TabIndex = 43,
                IsTabStop = true
            };
            string lastExportedFile = string.Empty;
            revealExportBtn.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(lastExportedFile) && System.IO.File.Exists(lastExportedFile))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastExportedFile}\"") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug("Failed to open explorer for backup: " + ex.Message);
                    }
                }
            };
            backupBtnRow.Children.Add(revealExportBtn);

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

            var resetAllBtn = new Button
            {
                Content = "Restore All Default Settings...",
                Classes = { "Danger" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Avalonia.Thickness(12, 8),
                TabIndex = 44,
                IsTabStop = true
            };
            ToolTip.SetTip(resetAllBtn, "Reset all KeyPulse preferences, typing speed, theme, update channel, screen brightness presets, and desktop organizer automation settings back to factory defaults. Your saved shortcuts and manual desktop layouts are kept safe.");

            var dangerBody = new StackPanel { Spacing = 10 };
            dangerBody.Children.Add(new TextBlock
            {
                Text = "Reset all application preferences, presets, and behavior back to their original factory defaults. Your saved shortcuts and custom desktop layouts will NOT be deleted.",
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            dangerBody.Children.Add(resetAllBtn);
            RegisterSettingCard(3, "Danger Zone — Factory Reset",
                "Destructive preference reset action that restores all out-of-the-box defaults for startup, theme, sounds, typing, brightness, and updates without deleting saved shortcuts.",
                "danger zone factory reset restore defaults wipe all settings preferences",
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

            var pageHeaderGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Avalonia.Thickness(2, 0, 2, 10)
            };
            var pageHeaderStack = new StackPanel { Spacing = 2 };
            var tabPageTitle = new TextBlock
            {
                Text = tabNames[activeTab],
                Classes = { "SectionTitle" },
                FontSize = 14
            };
            var tabPageDesc = new TextBlock
            {
                Text = tabDescriptions[activeTab],
                Classes = { "Muted" },
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            pageHeaderStack.Children.Add(tabPageTitle);
            pageHeaderStack.Children.Add(tabPageDesc);
            Grid.SetColumn(pageHeaderStack, 0);
            pageHeaderGrid.Children.Add(pageHeaderStack);
            Grid.SetColumn(restoreTabDefaultsBtn, 1);
            pageHeaderGrid.Children.Add(restoreTabDefaultsBtn);

            var pageInnerGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*")
            };
            Grid.SetRow(pageHeaderGrid, 0);
            pageInnerGrid.Children.Add(pageHeaderGrid);

            var cardsScroll = new ScrollViewer
            {
                Content = cardsStack,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(cardsScroll, 1);
            pageInnerGrid.Children.Add(cardsScroll);

            var pageContainer = new Border
            {
                Background = AppBrush("AppPanelRaisedBrush"),
                BorderBrush = AppBrush("AppBorderBrush"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(0, 8, 8, 8),
                Padding = new Avalonia.Thickness(14, 12),
                Margin = new Avalonia.Thickness(0, 0, 0, 0),
                Child = pageInnerGrid
            };
            Grid.SetRow(pageContainer, 2);
            rootGrid.Children.Add(pageContainer);

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
                    chk.IsChecked = _currentConfig.LaunchOnBoot;
                    soundChk.IsChecked = _currentConfig.SoundEnabled;
                    RefreshAdminButtonState(Program.IsElevated, Program.IsAdminTaskInstalled());

                    RefreshBrowserStateUi();
                    SyncSpeedUiFromValue(_currentConfig.TypingDelayMs);

                    for (int j = 0; j < themeSegmentButtons.Length; j++)
                    {
                        bool active = string.Equals(_currentConfig.Theme, themeValues[j], StringComparison.OrdinalIgnoreCase);
                        themeSegmentButtons[j].Classes.Remove("Primary");
                        themeSegmentButtons[j].Classes.Remove("Secondary");
                        themeSegmentButtons[j].Classes.Add(active ? "Primary" : "Secondary");
                    }

                    var latestSb = ScreenBrightnessService.LoadConfig();
                    sbLockChk.IsChecked = latestSb.GlobalLockBrightnessContrast;

                    var latestDo = DesktopOrganizerService.LoadConfig();
                    doAutoRestoreCheck.IsChecked = latestDo.AutoRestoreOnDisplayChange;
                    doAutoBackupCheck.IsChecked = latestDo.AutoPeriodicSnapshotEnabled;
                    RefreshDesktopOrganizerSummary();

                    autoUpdateCheck.IsChecked = _currentConfig.AutoCheckForUpdates;
                    channelCombo.SelectedIndex = string.Equals(_currentConfig.UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    RefreshUpdateChannelInteractivity();
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

                for (int i = 0; i < tabEars.Length; i++)
                {
                    bool isActive = (!isSearching && i == activeTab);
                    if (isActive)
                    {
                        tabEars[i].Background = AppBrush("AppPanelRaisedBrush");
                        tabEars[i].BorderBrush = AppBrush("AppAccentBrush");
                        tabEars[i].BorderThickness = new Avalonia.Thickness(1, 3, 1, 0);
                        tabEars[i].Margin = new Avalonia.Thickness(0, 0, 4, -1);
                        tabEars[i].ZIndex = 10;
                        tabTexts[i].Foreground = AppBrush("AppAccentBrush");
                        tabTexts[i].FontWeight = Avalonia.Media.FontWeight.Bold;
                        tabTexts[i].Opacity = 1.0;
                    }
                    else
                    {
                        tabEars[i].Background = AppBrush("AppPanelBrush");
                        tabEars[i].BorderBrush = AppBrush("AppBorderBrush");
                        tabEars[i].BorderThickness = new Avalonia.Thickness(1, 1, 1, 1);
                        tabEars[i].Margin = new Avalonia.Thickness(0, 0, 4, 0);
                        tabEars[i].ZIndex = 1;
                        tabTexts[i].Foreground = AppBrush("AppTextPrimaryBrush");
                        tabTexts[i].FontWeight = Avalonia.Media.FontWeight.Normal;
                        tabTexts[i].Opacity = 0.8;
                    }
                }

                restoreTabDefaultsBtn.IsVisible = !isSearching && activeTab != 3;
                if (isSearching)
                {
                    tabPageTitle.Text = $"Search Results for \"{query}\"";
                    tabPageDesc.Text = "Showing all settings across all categories matching your search query.";
                }
                else
                {
                    tabPageTitle.Text = tabNames[activeTab];
                    tabPageDesc.Text = tabDescriptions[activeTab];
                }

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

            for (int i = 0; i < tabEars.Length; i++)
            {
                int capturedIndex = i;
                tabEars[i].PointerEntered += (_, _) =>
                {
                    if (activeTab != capturedIndex || !string.IsNullOrEmpty(searchBox.Text))
                    {
                        tabEars[capturedIndex].Background = AppBrush("AppHoverBrush");
                        tabEars[capturedIndex].BorderBrush = AppBrush("AppBorderStrongBrush");
                        tabTexts[capturedIndex].Foreground = AppBrush("AppTextPrimaryBrush");
                        tabTexts[capturedIndex].Opacity = 1.0;
                    }
                };
                tabEars[i].PointerExited += (_, _) =>
                {
                    if (activeTab != capturedIndex || !string.IsNullOrEmpty(searchBox.Text))
                    {
                        tabEars[capturedIndex].Background = AppBrush("AppPanelBrush");
                        tabEars[capturedIndex].BorderBrush = AppBrush("AppBorderBrush");
                        tabTexts[capturedIndex].Foreground = AppBrush("AppTextPrimaryBrush");
                        tabTexts[capturedIndex].Opacity = 0.8;
                    }
                };
                tabEars[i].PointerPressed += (_, _) =>
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
                tabEars[i].KeyDown += (_, ke) =>
                {
                    if (ke.Key == Avalonia.Input.Key.Enter || ke.Key == Avalonia.Input.Key.Space)
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
                        ke.Handled = true;
                    }
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
                    _currentConfig.SoundEnabled = true;
                    Program.SoundEnabled = true;
                    _currentConfig.PreferredBrowser = "System";
                    _currentConfig.UseGoogleChromeForUrls = false;
                    _currentConfig.TypingDelayMs = TypingDelayFast;
                    InputSimulator.CharacterDelayMs = TypingDelayFast;
                    SaveConfig();
                }
                else if (activeTab == 1)
                {
                    _currentConfig.Theme = "System";
                    App.ApplyTheme(_currentConfig.Theme);
                    SaveConfig();
                }
                else if (activeTab == 2)
                {
                    ScreenBrightnessService.ResetToDefaults();
                    DesktopOrganizerService.ResetBehavioralDefaults(out _);
                    SaveConfig();
                }
                else if (activeTab == 3)
                {
                    _currentConfig.AutoCheckForUpdates = true;
                    _currentConfig.UpdateChannel = "Stable";
                    _currentConfig.SkippedUpdateVersion = string.Empty;
                    SaveConfig();
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
                    "Reset ALL KeyPulse preferences across every tab back to their original factory defaults?\n\n• Startup: Launch with Windows (On)\n• Theme: Match Windows\n• Sound effects: On\n• Browser: System Default\n• Typing speed: Fast (1 ms)\n• Updates: Automatic checks on (Stable channel)\n• Screen Brightness: Sync On + 3 default presets\n• Desktop Organizer: Auto-restore Off, Auto-backup Off (3 days)\n\nYour saved keyboard shortcuts and manual desktop icon layouts will NOT be deleted.",
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
                _currentConfig.PreferredBrowser = "System";
                _currentConfig.UseGoogleChromeForUrls = false;
                _currentConfig.TypingDelayMs = TypingDelayFast;
                _currentConfig.AutoCheckForUpdates = true;
                _currentConfig.UpdateChannel = "Stable";
                _currentConfig.SkippedUpdateVersion = string.Empty;
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

                    var choice = await AskBackupPassphraseAsync(w);
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

                    lastExportedFile = destination;
                    revealExportBtn.IsVisible = true;
                    statusTxt.Text = passphrase == null
                        ? $"Backup exported to: {destination}\nVerified {payload.Shortcuts.Count} shortcut(s), plus your window sizes, "
                          + "column widths, desktop layouts, brightness presets, and preferences. It is NOT password protected, "
                          + "so its targets are readable by anyone who opens the file — keep it somewhere you trust."
                        : $"Backup exported to: {destination}\nVerified, encrypted and saved {payload.Shortcuts.Count} shortcut(s), plus your window "
                          + "sizes, column widths, desktop layouts, brightness presets, and preferences. Without this password "
                          + "the file cannot be restored, and KeyPulse cannot recover it for you.";
                    statusTxt.Foreground = AppBrush("AppSuccessSoftBrush");
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Export failed: " + ex);
                    statusTxt.Text = "Export failed. Please check folder permissions and disk space, then try again.";
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
                        payload = await UnlockBackupAsync(inspection, statusTxt, w);
                        if (payload == null) return;
                    }

                    if (!BackupService.ValidatePayload(payload, out var importError, out var importWarnings))
                    {
                        statusTxt.Text = "Restore stopped: " + importError + " Your shortcuts were not touched.";
                        statusTxt.Foreground = AppBrush("AppDangerBrush");
                        return;
                    }

                    var restoreLayout = true;
                    if (!await ConfirmRestoreAsync(inspection, payload, files[0].Name, importWarnings, v => restoreLayout = v, w))
                    {
                        statusTxt.Text = "Restore canceled. Your shortcuts were not touched.";
                        statusTxt.Foreground = AppBrush("AppTextMutedBrush");
                        return;
                    }

                    statusTxt.Text = "Restoring... please wait.";
                    statusTxt.Foreground = AppBrush("AppWarningBrush");

                    var rollbackPaths = ConfigStore.SaveAllRollbackCopies("before-restore");
                    var rollbackPath = rollbackPaths.FirstOrDefault();
                    var previousConfig = CloneConfig(_currentConfig);
                    var previousOrganizer = DesktopOrganizerService.LoadConfig();
                    var previousBrightness = ScreenBrightnessService.LoadConfig();
                    bool needsBrightnessConsent = false;

                    try
                    {
                        var problems = ApplyBackupPayload(payload, restoreLayout, out var layoutNote, out needsBrightnessConsent);
                        SyncAllSettingsControls();

                        var imported = Hotkeys.Count;

                        var notes = new List<string>();
                        if (rollbackPaths.Count > 0)
                        {
                            notes.Add("Your previous settings were kept as: " + string.Join(", ", rollbackPaths));
                        }
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
                            if (previousOrganizer != null)
                            {
                                DesktopOrganizerService.SaveConfig(previousOrganizer, out _, allowEmptyProfiles: true);
                                DesktopOrganizerService.RaiseProfilesChanged();
                            }
                            if (previousBrightness != null)
                            {
                                ScreenBrightnessService.SaveConfig(previousBrightness);
                                ScreenBrightnessService.NotifyStateChanged();
                            }
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
                        needsBrightnessConsent = false;
                    }

                    if (needsBrightnessConsent && await ScreenBrightnessService.ShowDisclaimerDialogAsync(w))
                    {
                        SyncAllSettingsControls();
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Import failed: " + ex);
                    statusTxt.Text = "Import failed. Please check that the selected file is a valid KeyPulse backup.";
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
                _activeSettingsWindow = null;
                ScreenBrightnessService.StateChanged -= OnBrightnessStateChanged;
                DesktopOrganizerService.ProfilesChanged -= OnOrganizerProfilesChanged;
                _currentConfig.SettingsWindowState = w.WindowState.ToString();
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

            try { ScreenBrightnessService.InvalidateMonitorCache(); } catch { }
            try { Program.ReleaseSingleInstanceLock(); } catch { }

            Environment.Exit(0);
        }

        /// <summary>ISSUE_24: everything a restore should be able to put back.</summary>
    }
}
