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
        private StackPanel _profilesStack = null!;
        private TextBlock _statusText = null!;
        private CheckBox _globalLockCheck = null!;
        private CancellationTokenSource? _liveReadCts;

        public ScreenBrightnessWindow()
        {
            Title = "KeyPulse — Screen Brightness & Shortcut Cycling";
            Width = 880;
            Height = 740;
            MinWidth = 760;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = ScreenBrightnessOverlayWindow.AppBrush("AppBgBrush", "#0B0F19");

            BuildUi();

            Opened += OnWindowOpened;
            Closed += OnWindowClosed;
            ScreenBrightnessService.StateChanged += OnServiceStateChanged;
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
                Padding = new Thickness(12, 7)
            };
            ToolTip.SetTip(identifyBtn, "Flash Screen A, Screen B, Screen C banners on each physical monitor.");
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
                Padding = new Thickness(12, 7)
            };
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
                Padding = new Thickness(12, 7)
            };
            ToolTip.SetTip(testCycleBtn, "Test cycling to the next brightness preset (Lowest -> Highest -> Wraps to Lowest).");
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

            _globalLockCheck = new CheckBox
            {
                Content = "Lock Brightness & Contrast Together Tight (Default across screens — moving one moves the other instantly)",
                IsChecked = cfg.GlobalLockBrightnessContrast,
                FontWeight = FontWeight.SemiBold,
                FontSize = 12.5
            };
            _globalLockCheck.IsCheckedChanged += OnGlobalLockChanged;
            Grid.SetColumn(_globalLockCheck, 0);
            globalBarGrid.Children.Add(_globalLockCheck);

            var trayOverlayBtn = new Button
            {
                Content = "Open Tray Quick Sliders",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5
            };
            trayOverlayBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessOverlayWindow.ShowOverlay();
            };
            Grid.SetColumn(trayOverlayBtn, 1);
            globalBarGrid.Children.Add(trayOverlayBtn);

            globalBar.Child = globalBarGrid;
            contentStack.Children.Add(globalBar);

            var containersGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
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
                Grid.SetColumn(view.RootCard, i);
                containersGrid.Children.Add(view.RootCard);
            }
            contentStack.Children.Add(containersGrid);

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
                FontSize = 12
            };
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
                Padding = new Thickness(14, 7)
            };
            closeBtn.Click += (_, _) => Close();
            Grid.SetColumn(closeBtn, 1);
            footerGrid.Children.Add(closeBtn);

            Grid.SetRow(footerGrid, 2);
            rootGrid.Children.Add(footerGrid);

            Content = rootGrid;
            RenderProfilesList();
        }

        private void OnGlobalLockChanged(object? sender, RoutedEventArgs e)
        {
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
                view.SuppressSliderEvents = false;
            }

            SetStatus(lockAll
                ? "Locked Brightness & Contrast together across all screens."
                : "Unlocked per-screen Brightness & Contrast sliders.", true);
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
            var newProfile = new ScreenBrightnessProfile
            {
                Name = $"Custom {b0}%",
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
                PlaceholderText = "Preset Nickname",
                VerticalAlignment = VerticalAlignment.Center
            };
            nameBox.LostFocus += (_, _) =>
            {
                string trimmed = (nameBox.Text ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && trimmed != profile.Name)
                {
                    profile.Name = trimmed;
                    ScreenBrightnessService.SaveConfig(cfg);
                    ScreenBrightnessService.NotifyStateChanged();
                }
            };
            Grid.SetColumn(nameBox, 0);
            topGrid.Children.Add(nameBox);

            var badgeText = profile.IncludeInCycle
                ? (cycleStepIndex == 1 ? $"CYCLE STEP 1/{totalCycleSteps} (LOWEST • FIRST)" : $"CYCLE STEP {cycleStepIndex}/{totalCycleSteps}")
                : "NOT IN CYCLE";

            var stepBadge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(profile.IncludeInCycle ? "#0369A1" : "#334155")),
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
                Spacing = 14,
                VerticalAlignment = VerticalAlignment.Center
            };

            var cycleChk = new CheckBox
            {
                Content = "In Shortcut Cycle",
                IsChecked = profile.IncludeInCycle,
                FontSize = 11.5
            };
            cycleChk.IsCheckedChanged += (_, _) =>
            {
                profile.IncludeInCycle = cycleChk.IsChecked == true;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.NotifyStateChanged();
                RenderProfilesList();
            };
            togglesPanel.Children.Add(cycleChk);

            var trayChk = new CheckBox
            {
                Content = "Tray Quick Action (Max 3)",
                IsChecked = profile.ShowInTrayQuickActions,
                FontSize = 11.5
            };
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
            togglesPanel.Children.Add(trayChk);

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
            deleteBtn.Click += (_, _) =>
            {
                if (cfg.Profiles.Count <= 1) return;
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

            var targetsGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 12
            };

            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                int slotIdx = i;
                var screenCfg = cfg.Screens.First(s => s.SlotIndex == slotIdx);
                var target = profile.Targets.First(t => t.SlotIndex == slotIdx);
                string slotTitle = ScreenBrightnessService.DefaultSlotName(slotIdx);
                if (!string.IsNullOrWhiteSpace(screenCfg.CustomNickname) &&
                    !string.Equals(screenCfg.CustomNickname, slotTitle, StringComparison.OrdinalIgnoreCase))
                {
                    slotTitle = $"{slotTitle} ({screenCfg.CustomNickname})";
                }

                var slotPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };

                slotPanel.Children.Add(new TextBlock
                {
                    Text = $"{slotTitle}:",
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
                    Width = 110,
                    VerticalAlignment = VerticalAlignment.Center
                };

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

                slotPanel.Children.Add(bSlider);
                slotPanel.Children.Add(bValText);

                Grid.SetColumn(slotPanel, slotIdx);
                targetsGrid.Children.Add(slotPanel);
            }

            rowStack.Children.Add(targetsGrid);
            card.Child = rowStack;
            return card;
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
                    int connected = 0;
                    foreach (var st in liveStates)
                    {
                        if (st.IsConnected) connected++;
                        ScreenContainerBuilder.ApplyLiveStateToContainer(_containers[st.SlotIndex], st, respectUserTouch: true);
                    }
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
                var states = ScreenBrightnessService.GetImmediateCachedStates();
                foreach (var st in states)
                {
                    if (st.SlotIndex >= 0 && st.SlotIndex < _containers.Length && _containers[st.SlotIndex] != null)
                    {
                        ScreenContainerBuilder.SyncFromConfigState(_containers[st.SlotIndex], st);
                    }
                }
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
