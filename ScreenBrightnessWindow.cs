using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace KeyPulse
{
    public sealed class ScreenBrightnessWindow : Window
    {
        private static ScreenBrightnessWindow? _instance;
        private readonly ScreenContainerView[] _containers = new ScreenContainerView[ScreenBrightnessService.MaxScreens];
        private Grid _containersGrid = null!;
        private StackPanel _profilesStack = null!;
        private TextBlock _statusText = null!;
        private CheckBox _globalLockCheck = null!;
        private bool _suppressGlobalLockEvent;
        private CancellationTokenSource? _liveReadCts;

        public ScreenBrightnessWindow()
        {
            Title = "KeyPulse — Screen Brightness & Shortcut Cycling";
            MinWidth = 760;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = ScreenBrightnessOverlayWindow.AppBrush("AppBgBrush", "#0B0F19");
            Focusable = true;

            ApplySavedWindowGeometry();
            BuildUi();

            PointerPressed += OnWindowPointerPressed;
            KeyDown += OnWindowKeyDown;
            Opened += OnWindowOpened;
            Closing += (_, _) => SaveWindowGeometry();
            Closed += OnWindowClosed;
            ScreenBrightnessService.StateChanged += OnServiceStateChanged;
        }

        private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual v)
            {
                for (int i = 0; i < _containers.Length; i++)
                {
                    var c = _containers[i];
                    if (c?.InlineRenameBox != null && c.InlineRenameBox.IsVisible)
                    {
                        if (!ReferenceEquals(v, c.InlineRenameBox) && !ScreenBrightnessOverlayWindow.IsVisualInside(v, c.InlineRenameBox))
                        {
                            c.CommitPendingRename?.Invoke();
                            Focus();
                        }
                    }
                }
            }
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        }

        private void ApplySavedWindowGeometry()
        {
            try
            {
                var appCfg = ConfigStore.Load(out _, out _);
                Width = (!double.IsNaN(appCfg.ScreenBrightnessWindowWidth) && appCfg.ScreenBrightnessWindowWidth >= MinWidth)
                    ? appCfg.ScreenBrightnessWindowWidth : 880;
                Height = (!double.IsNaN(appCfg.ScreenBrightnessWindowHeight) && appCfg.ScreenBrightnessWindowHeight >= MinHeight)
                    ? appCfg.ScreenBrightnessWindowHeight : 740;

                if (!double.IsNaN(appCfg.ScreenBrightnessWindowX) && !double.IsNaN(appCfg.ScreenBrightnessWindowY))
                {
                    var x = (int)appCfg.ScreenBrightnessWindowX;
                    var y = (int)appCfg.ScreenBrightnessWindowY;
                    var monitors = DesktopOrganizerService.GetMonitors();
                    bool onScreen = monitors.Any(m =>
                        x + 100 >= m.WorkRect.left && x <= m.WorkRect.right - 100 &&
                        y + 60 >= m.WorkRect.top && y <= m.WorkRect.bottom - 60);
                    if (onScreen)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Position = new PixelPoint(x, y);
                    }
                }
            }
            catch
            {
                Width = 880;
                Height = 740;
            }
        }

        private void SaveWindowGeometry()
        {
            try
            {
                if (WindowState != WindowState.Normal) return;
                var x = Position.X;
                var y = Position.Y;
                var w = Bounds.Width > 0 ? Bounds.Width : Width;
                var h = Bounds.Height > 0 ? Bounds.Height : Height;
                ConfigStore.TryUpdate(cfg =>
                {
                    cfg.ScreenBrightnessWindowX = x;
                    cfg.ScreenBrightnessWindowY = y;
                    cfg.ScreenBrightnessWindowWidth = w;
                    cfg.ScreenBrightnessWindowHeight = h;
                }, out _);
            }
            catch { }
        }

        private static Border CreateHelpBadge(string tooltipText)
        {
            var badge = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Width = 17,
                Height = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Help),
                Child = new TextBlock
                {
                    Text = "?",
                    FontSize = 10.5,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
                }
            };
            ToolTip.SetTip(badge, tooltipText);
            return badge;
        }

        public static void OpenWindow(Window? owner = null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_instance != null)
                {
                    if (_instance.WindowState == WindowState.Minimized)
                    {
                        _instance.WindowState = WindowState.Normal;
                    }
                    _instance.Activate();
                    _instance.Focus();
                    return;
                }

                _instance = new ScreenBrightnessWindow();
                if (owner != null && owner.IsVisible)
                {
                    _instance.Show(owner);
                }
                else
                {
                    _instance.Show();
                }
            });
        }

        private void BuildUi()
        {
            var cfg = ScreenBrightnessService.LoadConfig();

            var rootGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Margin = new Thickness(20, 16)
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 14)
            };

            var titleStack = new StackPanel { Spacing = 4 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Screen Brightness & Contrast Control",
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Hardware DDC/CI & Laptop Backlight • Anti-Cheat Safe (Zero In-Game Focus Steal) • 250ms Speed Limiter",
                FontSize = 12,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
            });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var topButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            var identifyBtn = new Button
            {
                Content = "Identify Screens (A / B / C)",
                Classes = { "Secondary" },
                Padding = new Thickness(12, 7),
                TabIndex = 1
            };
            ToolTip.SetTip(identifyBtn, "Flash Screen A, Screen B, Screen C banners on each physical monitor so you know which card controls which display.");
            identifyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.ShowIdentifyBanners();
            };
            topButtons.Children.Add(identifyBtn);

            var refreshBtn = new Button
            {
                Content = "Read Live Hardware",
                Classes = { "Secondary" },
                Padding = new Thickness(12, 7),
                TabIndex = 2
            };
            ToolTip.SetTip(refreshBtn, "Query all connected monitors right now to read their current physical brightness and contrast levels.");
            refreshBtn.Click += async (_, _) =>
            {
                Program.PlaySound("click");
                for (int i = 0; i < _containers.Length; i++) _containers[i].UserTouched = false;
                await RefreshHardwareAsync();
            };
            topButtons.Children.Add(refreshBtn);

            var testCycleBtn = new Button
            {
                Content = "Cycle Next Step",
                Classes = { "Primary" },
                Padding = new Thickness(12, 7),
                TabIndex = 3
            };
            ToolTip.SetTip(testCycleBtn, "Test cycling to the next brightness preset (starts at lowest brightness, climbs up, then wraps back to lowest).");
            testCycleBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.TriggerShortcutCycleStep();
                SetStatus("Cycled to next brightness step (Lowest-First order).", true);
            };
            topButtons.Children.Add(testCycleBtn);

            Grid.SetColumn(topButtons, 1);
            headerGrid.Children.Add(topButtons);
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var contentStack = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var globalBar = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelBrush", "#111827"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#1F2937"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10)
            };

            var globalBarGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            var globalLockRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            _globalLockCheck = new CheckBox
            {
                Content = "Sync brightness and contrast across all screens",
                IsChecked = cfg.GlobalLockBrightnessContrast,
                FontWeight = FontWeight.SemiBold,
                FontSize = 12.5,
                TabIndex = 4
            };
            ToolTip.SetTip(_globalLockCheck, "When enabled, moving a Brightness slider automatically sets Contrast to the same percentage on all displays. Uncheck to adjust Contrast independently.");
            _globalLockCheck.IsCheckedChanged += OnGlobalLockChanged;
            globalLockRow.Children.Add(_globalLockCheck);
            globalLockRow.Children.Add(CreateHelpBadge("Keeps Brightness and Contrast locked at a 1:1 ratio across every connected screen. Turn this off if you want separate Brightness and Contrast sliders for your displays or presets."));

            Grid.SetColumn(globalLockRow, 0);
            globalBarGrid.Children.Add(globalLockRow);

            var globalBarButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            var trayOverlayBtn = new Button
            {
                Content = "Open Tray Quick Sliders",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5,
                TabIndex = 5
            };
            ToolTip.SetTip(trayOverlayBtn, "Open the compact system tray popup sliders near your clock.");
            trayOverlayBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessOverlayWindow.ShowOverlay();
            };
            globalBarButtons.Children.Add(trayOverlayBtn);

            var restoreDefaultsBtn = new Button
            {
                Content = "Restore Defaults",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5,
                TabIndex = 6
            };
            ToolTip.SetTip(restoreDefaultsBtn, "Reset all screen nicknames, sync settings, and brightness presets back to their original factory defaults.");
            restoreDefaultsBtn.Click += OnRestoreDefaultsClick;
            globalBarButtons.Children.Add(restoreDefaultsBtn);

            Grid.SetColumn(globalBarButtons, 1);
            globalBarGrid.Children.Add(globalBarButtons);

            globalBar.Child = globalBarGrid;
            contentStack.Children.Add(globalBar);

            _containersGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*"),
                ColumnSpacing = 12,
                MinHeight = 290
            };

            var initialStates = ScreenBrightnessService.GetImmediateCachedStates();
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                var st = initialStates.First(s => s.SlotIndex == i);
                var view = ScreenContainerBuilder.CreateScreenContainer(
                    st,
                    onNickChanged: () =>
                    {
                        RenderProfilesList();
                        ScreenBrightnessService.NotifyStateChanged();
                    });
                _containers[i] = view;
            }

            UpdateVisibleScreenContainers(initialStates);
            contentStack.Children.Add(_containersGrid);

            var presetsPanelBorder = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelBrush", "#111827"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#1F2937"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14)
            };

            var presetsStack = new StackPanel { Spacing = 10 };

            var presetsHeader = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            var presetsTitleStack = new StackPanel { Spacing = 2 };
            presetsTitleStack.Children.Add(new TextBlock
            {
                Text = "Shortcut Cycle Steps & Tray Quick Actions",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            presetsTitleStack.Children.Add(new TextBlock
            {
                Text = "Pressing your Cycle Brightness shortcut key starts at the lowest brightness preset first, climbs up on each press, and wraps back to the lowest. Pin up to 3 presets to the System Tray submenu.",
                FontSize = 11.5,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(presetsTitleStack, 0);
            presetsHeader.Children.Add(presetsTitleStack);

            var addPresetButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            var addFromCurrentBtn = new Button
            {
                Content = "+ Save Current Sliders as Preset",
                Classes = { "Primary" },
                Padding = new Thickness(12, 6),
                FontSize = 12,
                TabIndex = 7
            };
            ToolTip.SetTip(addFromCurrentBtn, "Create a new brightness preset using the current Screen A, B, and C slider percentages.");
            addFromCurrentBtn.Click += OnSaveCurrentSlidersAsPreset;
            addPresetButtons.Children.Add(addFromCurrentBtn);

            Grid.SetColumn(addPresetButtons, 1);
            presetsHeader.Children.Add(addPresetButtons);
            presetsStack.Children.Add(presetsHeader);

            _profilesStack = new StackPanel { Spacing = 8 };
            presetsStack.Children.Add(_profilesStack);

            presetsPanelBorder.Child = presetsStack;
            contentStack.Children.Add(presetsPanelBorder);

            scroll.Content = contentStack;
            Grid.SetRow(scroll, 1);
            rootGrid.Children.Add(scroll);

            var footerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 12, 0, 0)
            };

            _statusText = new TextBlock
            {
                Text = "Ready. Assign a key combo to 'Cycle Screen Brightness' in the main KeyPulse window to switch presets in-game.",
                FontSize = 12,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_statusText, 0);
            footerGrid.Children.Add(_statusText);

            var closeBtn = new Button
            {
                Content = "Close",
                Classes = { "Secondary" },
                MinWidth = 90,
                Padding = new Thickness(14, 7),
                TabIndex = 20
            };
            closeBtn.Click += (_, _) => Close();
            Grid.SetColumn(closeBtn, 1);
            footerGrid.Children.Add(closeBtn);

            Grid.SetRow(footerGrid, 2);
            rootGrid.Children.Add(footerGrid);

            Content = rootGrid;
            RenderProfilesList();
        }

        private async Task<bool> ConfirmActionAsync(string title, string message, string confirmButtonText)
        {
            var tcs = new TaskCompletionSource<bool>();
            var dlg = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = Icon,
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppBgBrush", "#0B0F19")
            };

            var root = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            root.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.Bold,
                FontSize = 16,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#E2E8F0")
            });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Classes = { "Secondary" },
                MinWidth = 90
            };
            cancelBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(false);
                dlg.Close();
            };

            var confirmBtn = new Button
            {
                Content = confirmButtonText,
                Classes = { "Danger" },
                MinWidth = 120
            };
            confirmBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(true);
                dlg.Close();
            };

            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape)
                {
                    ke.Handled = true;
                    tcs.TrySetResult(false);
                    dlg.Close();
                }
                else if (ke.Key == Key.Enter)
                {
                    ke.Handled = true;
                    tcs.TrySetResult(true);
                    dlg.Close();
                }
            };
            dlg.Closed += (_, _) => tcs.TrySetResult(false);

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(confirmBtn);
            root.Children.Add(btnRow);
            dlg.Content = root;

            if (IsVisible)
            {
                await dlg.ShowDialog(this);
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dlg.Show();
            }

            return await tcs.Task;
        }

        private async void OnRestoreDefaultsClick(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            bool confirmed = await ConfirmActionAsync(
                "Restore Default Brightness Settings",
                "Reset all screen nicknames, brightness/contrast sync toggles, and shortcut presets back to their original factory defaults?\n\n• Sync Brightness & Contrast: On\n• Default levels: 65% across Screen A, B, C\n• Default presets: Night / Low (15%), Balanced (55%), Daylight / Competitive (90%)",
                "Restore Defaults");
            if (!confirmed) return;

            var cfg = ScreenBrightnessService.ResetToDefaults();
            _suppressGlobalLockEvent = true;
            _globalLockCheck.IsChecked = cfg.GlobalLockBrightnessContrast;
            _suppressGlobalLockEvent = false;

            var states = ScreenBrightnessService.GetImmediateCachedStates();
            foreach (var st in states)
            {
                if (st.SlotIndex >= 0 && st.SlotIndex < _containers.Length && _containers[st.SlotIndex] != null)
                {
                    ScreenContainerBuilder.SyncFromConfigState(_containers[st.SlotIndex], st);
                }
            }

            RenderProfilesList();
            Program.PlaySound("click");
            SetStatus("✓ Restored default Screen Brightness settings and presets.", true);
        }

        private void OnGlobalLockChanged(object? sender, RoutedEventArgs e)
        {
            if (_suppressGlobalLockEvent) return;
            bool lockAll = _globalLockCheck.IsChecked == true;
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.GlobalLockBrightnessContrast = lockAll;

            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                var s = cfg.Screens.First(x => x.SlotIndex == i);
                s.LockRatio = lockAll;
                if (lockAll)
                {
                    s.LastContrast = s.LastBrightness;
                }
            }
            ScreenBrightnessService.SaveConfig(cfg);

            for (int i = 0; i < _containers.Length; i++)
            {
                var view = _containers[i];
                view.SuppressSliderEvents = true;
                view.LockRatioCheckBox.IsChecked = lockAll;
                if (lockAll)
                {
                    int b = (int)Math.Round(view.BrightnessSlider.Value);
                    view.ContrastSlider.Value = b;
                    view.ContrastValueText.Text = b.ToString();
                    ScreenBrightnessService.RequestScreenLevelChange(i, b, b, updateContrastFromLock: true);
                }
                ScreenContainerBuilder.UpdateContrastInteractivity(view);
                view.SuppressSliderEvents = false;
            }

            RenderProfilesList();
            ScreenBrightnessService.NotifyStateChanged();

            SetStatus(lockAll
                ? "Synced Brightness & Contrast together across all screens."
                : "Unlocked independent Brightness & Contrast sliders.", true);
        }

        private void OnSaveCurrentSlidersAsPreset(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            var cfg = ScreenBrightnessService.LoadConfig();
            int pinnedCount = cfg.Profiles.Count(p => p.ShowInTrayQuickActions);

            var targets = new List<ScreenCycleEntry>();
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                int b = (int)Math.Round(_containers[i].BrightnessSlider.Value);
                int c = (int)Math.Round(_containers[i].ContrastSlider.Value);
                targets.Add(new ScreenCycleEntry
                {
                    SlotIndex = i,
                    Brightness = b,
                    Contrast = c
                });
            }

            int b0 = targets[0].Brightness;
            string baseName = $"Custom {b0}%";
            string candidate = baseName;
            int suffix = 2;
            while (cfg.Profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = $"{baseName} ({suffix++})";
            }

            var newProfile = new ScreenBrightnessProfile
            {
                Name = candidate,
                IncludeInCycle = true,
                ShowInTrayQuickActions = pinnedCount < ScreenBrightnessService.MaxTrayQuickActions,
                Targets = targets
            };

            cfg.Profiles.Add(newProfile);
            ScreenBrightnessService.SaveConfig(cfg);
            ScreenBrightnessService.NotifyStateChanged();
            RenderProfilesList();
            SetStatus($"Added preset \"{newProfile.Name}\".", true);
        }

        private void RenderProfilesList()
        {
            if (_profilesStack == null) return;
            _profilesStack.Children.Clear();

            var cfg = ScreenBrightnessService.LoadConfig();
            var orderedCycle = ScreenBrightnessService.GetOrderedCycleProfiles(cfg);

            var sorted = cfg.Profiles
                .OrderBy(p => p.Targets.FirstOrDefault(t => t.SlotIndex == 0)?.Brightness ?? 50)
                .ThenBy(p => p.Targets.Average(t => t.Brightness))
                .ToList();

            foreach (var profile in sorted)
            {
                int cycleRank = profile.IncludeInCycle ? orderedCycle.FindIndex(x => x.Id == profile.Id) + 1 : 0;
                _profilesStack.Children.Add(CreateProfileRow(profile, cfg, cycleRank, orderedCycle.Count));
            }
        }

        private Border CreateProfileRow(ScreenBrightnessProfile profile, ScreenBrightnessConfig cfg, int cycleStepIndex, int totalCycleSteps)
        {
            bool isActive = profile.Id == cfg.LastCycleProfileId;

            var card = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush(isActive ? "AppSelectedBrush" : "AppPanelRaisedBrush", isActive ? "#1E3A8A" : "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush(isActive ? "AppAccentBrush" : "AppBorderBrush", isActive ? "#38BDF8" : "#334155"),
                BorderThickness = new Thickness(isActive ? 1.5 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10)
            };

            var rowStack = new StackPanel { Spacing = 8 };

            var topGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("200,Auto,*,Auto"),
                ColumnSpacing = 10
            };

            var nameBox = new TextBox
            {
                Text = profile.Name,
                MaxLength = 32,
                PlaceholderText = "Preset name (max 32 chars)",
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(nameBox, "Enter a unique preset name up to 32 characters.");

            void CommitPresetName()
            {
                string trimmed = (nameBox.Text ?? string.Empty).Trim();
                if (trimmed.Length > 32) trimmed = trimmed.Substring(0, 32).Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    nameBox.Text = profile.Name;
                    SetStatus("⚠️ Preset name cannot be empty.", false);
                    return;
                }
                if (cfg.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                {
                    nameBox.Text = profile.Name;
                    SetStatus($"⚠️ Another preset is already named \"{trimmed}\".", false);
                    return;
                }
                if (trimmed != profile.Name)
                {
                    profile.Name = trimmed;
                    nameBox.Text = trimmed;
                    ScreenBrightnessService.SaveConfig(cfg);
                    ScreenBrightnessService.NotifyStateChanged();
                    SetStatus($"Renamed preset to \"{trimmed}\".", true);
                }
            }

            nameBox.LostFocus += (_, _) => CommitPresetName();
            nameBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    ke.Handled = true;
                    CommitPresetName();
                }
            };
            Grid.SetColumn(nameBox, 0);
            topGrid.Children.Add(nameBox);

            var badgeText = profile.IncludeInCycle
                ? (cycleStepIndex == 1 ? $"CYCLE STEP 1/{totalCycleSteps} (LOWEST • FIRST)" : $"CYCLE STEP {cycleStepIndex}/{totalCycleSteps}")
                : "NOT IN CYCLE";

            var stepBadge = new Border
            {
                Background = profile.IncludeInCycle
                    ? ScreenBrightnessOverlayWindow.AppBrush("AppAccentBrush", "#0369A1")
                    : ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                }
            };
            Grid.SetColumn(stepBadge, 1);
            topGrid.Children.Add(stepBadge);

            var togglesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var cycleGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var cycleChk = new CheckBox
            {
                Content = "Include in shortcut cycle",
                IsChecked = profile.IncludeInCycle,
                FontSize = 11.5
            };
            ToolTip.SetTip(cycleChk, "Include this preset when pressing your 'Cycle Screen Brightness' shortcut key.");
            cycleChk.IsCheckedChanged += (_, _) =>
            {
                profile.IncludeInCycle = cycleChk.IsChecked == true;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.NotifyStateChanged();
                RenderProfilesList();
            };
            cycleGroup.Children.Add(cycleChk);
            cycleGroup.Children.Add(CreateHelpBadge("When checked, pressing your Cycle Brightness shortcut key steps through this preset in lowest-to-highest order."));
            togglesPanel.Children.Add(cycleGroup);

            var trayGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var trayChk = new CheckBox
            {
                Content = "Pin to tray menu (Max 3)",
                IsChecked = profile.ShowInTrayQuickActions,
                FontSize = 11.5
            };
            ToolTip.SetTip(trayChk, "Show this preset in the KeyPulse System Tray menu and Quick Sliders popup (up to 3 presets).");
            trayChk.IsCheckedChanged += (_, _) =>
            {
                if (trayChk.IsChecked == true)
                {
                    int currentPinned = cfg.Profiles.Count(p => p.ShowInTrayQuickActions && p.Id != profile.Id);
                    if (currentPinned >= ScreenBrightnessService.MaxTrayQuickActions)
                    {
                        trayChk.IsChecked = false;
                        SetStatus("Maximum 3 presets can be pinned to the System Tray Quick Actions submenu.", false);
                        return;
                    }
                    profile.ShowInTrayQuickActions = true;
                }
                else
                {
                    profile.ShowInTrayQuickActions = false;
                }
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.NotifyStateChanged();
            };
            trayGroup.Children.Add(trayChk);
            trayGroup.Children.Add(CreateHelpBadge("Pins this preset as a one-click button inside the System Tray icon menu and Quick Sliders popup (maximum 3 pinned presets)."));
            togglesPanel.Children.Add(trayGroup);

            Grid.SetColumn(togglesPanel, 2);
            topGrid.Children.Add(togglesPanel);

            var actionBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            var applyBtn = new Button
            {
                Content = "Apply",
                Classes = { "Primary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5
            };
            ToolTip.SetTip(applyBtn, "Apply this preset's brightness and contrast levels to your displays right now.");
            applyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                for (int i = 0; i < _containers.Length; i++) _containers[i].UserTouched = true;
                ScreenBrightnessService.ApplyProfileAsync(profile);
                RenderProfilesList();
                SetStatus($"Applied preset \"{profile.Name}\".", true);
            };
            actionBtns.Children.Add(applyBtn);

            var captureBtn = new Button
            {
                Content = "Use Current Sliders",
                Classes = { "Secondary" },
                Padding = new Thickness(9, 5),
                FontSize = 11.5
            };
            ToolTip.SetTip(captureBtn, "Copy the current Screen A / B / C slider numbers into this preset.");
            captureBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
                {
                    var t = profile.Targets.First(x => x.SlotIndex == i);
                    t.Brightness = (int)Math.Round(_containers[i].BrightnessSlider.Value);
                    t.Contrast = (int)Math.Round(_containers[i].ContrastSlider.Value);
                }
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.NotifyStateChanged();
                RenderProfilesList();
                SetStatus($"Updated \"{profile.Name}\" with current slider levels.", true);
            };
            actionBtns.Children.Add(captureBtn);

            var deleteBtn = new Button
            {
                Content = "Delete",
                Classes = { "Danger" },
                Padding = new Thickness(9, 5),
                FontSize = 11.5,
                IsEnabled = cfg.Profiles.Count > 1
            };
            ToolTip.SetTip(deleteBtn, "Delete this brightness preset.");
            deleteBtn.Click += async (_, _) =>
            {
                if (cfg.Profiles.Count <= 1) return;
                Program.PlaySound("click");
                bool confirmed = await ConfirmActionAsync(
                    "Delete Brightness Preset",
                    $"Are you sure you want to delete the brightness preset \"{profile.Name}\"?\n\nThis cannot be undone.",
                    "Delete Preset");
                if (!confirmed) return;

                Program.PlaySound("close");
                cfg.Profiles.Remove(profile);
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.NotifyStateChanged();
                RenderProfilesList();
                SetStatus($"Removed preset \"{profile.Name}\".", true);
            };
            actionBtns.Children.Add(deleteBtn);

            Grid.SetColumn(actionBtns, 3);
            topGrid.Children.Add(actionBtns);
            rowStack.Children.Add(topGrid);

            var connectedSlots = ScreenBrightnessService.GetConnectedSlotIndices(cfg);
            if (connectedSlots.Count == 0) connectedSlots.Add(0);

            var targetsGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", connectedSlots.Count))),
                ColumnSpacing = 12
            };

            for (int colIdx = 0; colIdx < connectedSlots.Count; colIdx++)
            {
                int slotIdx = connectedSlots[colIdx];
                var screenCfg = cfg.Screens.First(s => s.SlotIndex == slotIdx);
                var target = profile.Targets.First(t => t.SlotIndex == slotIdx);
                string slotTitle = ScreenBrightnessService.DefaultSlotName(slotIdx);
                if (!string.IsNullOrWhiteSpace(screenCfg.CustomNickname) &&
                    !string.Equals(screenCfg.CustomNickname, slotTitle, StringComparison.OrdinalIgnoreCase))
                {
                    slotTitle = $"{slotTitle} ({screenCfg.CustomNickname})";
                }

                var slotStack = new StackPanel
                {
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var bRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };

                bRow.Children.Add(new TextBlock
                {
                    Text = screenCfg.LockRatio ? $"{slotTitle}:" : $"{slotTitle} (Bri):",
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var bSlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = target.Brightness,
                    Width = 105,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(bSlider, screenCfg.LockRatio
                    ? $"Target Brightness & Contrast for {slotTitle} when this preset is applied."
                    : $"Target Brightness for {slotTitle} when this preset is applied.");

                var bValText = new TextBlock
                {
                    Text = $"{target.Brightness}%",
                    FontSize = 11.5,
                    FontWeight = FontWeight.Bold,
                    MinWidth = 36,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                bSlider.PropertyChanged += (_, pe) =>
                {
                    if (pe.Property != RangeBase.ValueProperty) return;
                    int val = (int)Math.Round(bSlider.Value);
                    target.Brightness = val;
                    if (screenCfg.LockRatio)
                    {
                        target.Contrast = val;
                    }
                    bValText.Text = $"{val}%";
                    ScreenBrightnessService.SaveConfig(cfg);
                };

                bRow.Children.Add(bSlider);
                bRow.Children.Add(bValText);
                slotStack.Children.Add(bRow);

                if (!screenCfg.LockRatio)
                {
                    var cRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    cRow.Children.Add(new TextBlock
                    {
                        Text = $"{slotTitle} (Con):",
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var cSlider = new Slider
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = target.Contrast,
                        Width = 105,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ToolTip.SetTip(cSlider, $"Target Contrast for {slotTitle} when this preset is applied (independent because Sync ratio is off).");

                    var cValText = new TextBlock
                    {
                        Text = $"{target.Contrast}%",
                        FontSize = 11.5,
                        FontWeight = FontWeight.Bold,
                        MinWidth = 36,
                        Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    cSlider.PropertyChanged += (_, pe) =>
                    {
                        if (pe.Property != RangeBase.ValueProperty) return;
                        int val = (int)Math.Round(cSlider.Value);
                        target.Contrast = val;
                        cValText.Text = $"{val}%";
                        ScreenBrightnessService.SaveConfig(cfg);
                    };

                    cRow.Children.Add(cSlider);
                    cRow.Children.Add(cValText);
                    slotStack.Children.Add(cRow);
                }

                Grid.SetColumn(slotStack, colIdx);
                targetsGrid.Children.Add(slotStack);
            }

            rowStack.Children.Add(targetsGrid);
            card.Child = rowStack;
            return card;
        }

        private void UpdateVisibleScreenContainers(IReadOnlyList<ScreenLiveState> states)
        {
            if (_containersGrid == null) return;

            var activeStates = states.Where(s => s.IsConnected).OrderBy(s => s.SlotIndex).ToList();
            if (activeStates.Count == 0 && states.Count > 0)
            {
                activeStates.Add(states[0]);
            }

            int activeCount = Math.Max(1, activeStates.Count);
            _containersGrid.Children.Clear();
            _containersGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", activeCount)));

            for (int col = 0; col < activeStates.Count; col++)
            {
                int slotIdx = activeStates[col].SlotIndex;
                var view = _containers[slotIdx];
                view.RootCard.IsVisible = true;
                Grid.SetColumn(view.RootCard, col);
                _containersGrid.Children.Add(view.RootCard);
            }
        }

        private async void OnWindowOpened(object? sender, EventArgs e)
        {
            await RefreshHardwareAsync();
        }

        private async Task RefreshHardwareAsync()
        {
            _liveReadCts?.Cancel();
            _liveReadCts?.Dispose();
            _liveReadCts = new CancellationTokenSource();
            var token = _liveReadCts.Token;

            SetStatus("Reading live monitor brightness & contrast...", true);
            try
            {
                var liveStates = await ScreenBrightnessService.ReadAllScreensLiveAsync(token);
                if (token.IsCancellationRequested) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateVisibleScreenContainers(liveStates);
                    int connected = 0;
                    foreach (var st in liveStates)
                    {
                        if (st.IsConnected) connected++;
                        ScreenContainerBuilder.ApplyLiveStateToContainer(_containers[st.SlotIndex], st, respectUserTouch: true);
                    }
                    RenderProfilesList();
                    SetStatus($"Live hardware read complete ({connected} display{(connected == 1 ? "" : "s")} connected).", true);
                });
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void OnServiceStateChanged()
        {
            Dispatcher.UIThread.Post(() =>
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                if (_globalLockCheck != null && _globalLockCheck.IsChecked != cfg.GlobalLockBrightnessContrast)
                {
                    _suppressGlobalLockEvent = true;
                    _globalLockCheck.IsChecked = cfg.GlobalLockBrightnessContrast;
                    _suppressGlobalLockEvent = false;
                }

                var states = ScreenBrightnessService.GetImmediateCachedStates();
                UpdateVisibleScreenContainers(states);
                foreach (var st in states)
                {
                    if (st.SlotIndex >= 0 && st.SlotIndex < _containers.Length && _containers[st.SlotIndex] != null)
                    {
                        ScreenContainerBuilder.SyncFromConfigState(_containers[st.SlotIndex], st);
                    }
                }

                RenderProfilesList();
            });
        }

        private void SetStatus(string msg, bool ok)
        {
            if (_statusText != null)
            {
                _statusText.Text = msg;
                _statusText.Foreground = ScreenBrightnessOverlayWindow.AppBrush(
                    ok ? "AppTextMutedBrush" : "AppWarningBrush",
                    ok ? "#94A3B8" : "#F59E0B");
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            ScreenBrightnessService.StateChanged -= OnServiceStateChanged;
            _liveReadCts?.Cancel();
            _liveReadCts?.Dispose();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }
    }
}
