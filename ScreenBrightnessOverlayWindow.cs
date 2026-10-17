using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    public sealed class ScreenContainerView
    {
        public int SlotIndex { get; init; }
        public Border RootCard { get; init; } = null!;
        public TextBlock TitleText { get; init; } = null!;
        public TextBox InlineRenameBox { get; init; } = null!;
        public TextBlock ModelSubtitle { get; init; } = null!;
        public TextBlock BrightnessValueText { get; init; } = null!;
        public TextBlock ContrastValueText { get; init; } = null!;
        public Slider BrightnessSlider { get; init; } = null!;
        public Slider ContrastSlider { get; init; } = null!;
        public CheckBox LockRatioCheckBox { get; init; } = null!;
        public TextBlock StatusBadgeText { get; init; } = null!;
        public bool UserTouched { get; set; }
        public bool SuppressSliderEvents { get; set; }
    }

    public sealed class ScreenBrightnessOverlayWindow : Window
    {
        private static ScreenBrightnessOverlayWindow? _instance;
        private readonly ScreenContainerView[] _containers = new ScreenContainerView[ScreenBrightnessService.MaxScreens];
        private CancellationTokenSource? _liveReadCts;
        private TextBlock? _headerStatusText;
        private bool _isClosing;

        public ScreenBrightnessOverlayWindow()
        {
            Title = "Screen Brightness Quick Sliders";
            Width = 650;
            Height = 415;
            CanResize = false;
            ShowInTaskbar = false;
            Topmost = true;
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            WindowStartupLocation = WindowStartupLocation.Manual;

            BuildUi();
            PositionNearTrayClamped();

            Opened += OnOverlayOpened;
            Deactivated += OnOverlayDeactivated;
            KeyDown += OnOverlayKeyDown;
            Closed += OnOverlayClosed;
            ScreenBrightnessService.StateChanged += OnServiceStateChanged;
        }

        public static void ShowOverlay()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_instance != null)
                {
                    try
                    {
                        _instance.PositionNearTrayClamped();
                        _instance.Show();
                        _instance.Activate();
                        _instance.Focus();
                        return;
                    }
                    catch
                    {
                        _instance = null;
                    }
                }

                _instance = new ScreenBrightnessOverlayWindow();
                _instance.Show();
                _instance.Activate();
                _instance.Focus();
            });
        }

        private void BuildUi()
        {
            var outerBorder = new Border
            {
                Background = AppBrush("AppPanelBrush", "#0F172A"),
                BorderBrush = AppBrush("AppAccentBrush", "#38BDF8"),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14)
            };

            var rootGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto")
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var titleStack = new StackPanel { Spacing = 2 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Screen Brightness & Contrast",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            _headerStatusText = new TextBlock
            {
                Text = "Reading live monitor levels...",
                FontSize = 11,
                Foreground = AppBrush("AppTextMutedBrush", "#94A3B8")
            };
            titleStack.Children.Add(_headerStatusText);
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            var identifyBtn = new Button
            {
                Content = "Identify Screens",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5
            };
            ToolTip.SetTip(identifyBtn, "Flash Screen A, Screen B, Screen C banners on your displays without closing this overlay.");
            identifyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.ShowIdentifyBanners();
            };
            headerActions.Children.Add(identifyBtn);

            var fullWinBtn = new Button
            {
                Content = "Presets & Setup",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5
            };
            ToolTip.SetTip(fullWinBtn, "Open full Screen Brightness configuration and shortcut cycle steps.");
            fullWinBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessWindow.OpenWindow();
                CloseSafe();
            };
            headerActions.Children.Add(fullWinBtn);

            var closeBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 4),
                FontSize = 12
            };
            closeBtn.Click += (_, _) => CloseSafe();
            headerActions.Children.Add(closeBtn);

            Grid.SetColumn(headerActions, 1);
            headerGrid.Children.Add(headerActions);
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            var containersGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 10
            };

            var initialStates = ScreenBrightnessService.GetImmediateCachedStates();
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                var state = initialStates.First(s => s.SlotIndex == i);
                var containerView = ScreenContainerBuilder.CreateScreenContainer(
                    state,
                    onNickChanged: () => ScreenBrightnessService.NotifyStateChanged());
                _containers[i] = containerView;
                Grid.SetColumn(containerView.RootCard, i);
                containersGrid.Children.Add(containerView.RootCard);
            }

            Grid.SetRow(containersGrid, 1);
            rootGrid.Children.Add(containersGrid);

            var footerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(0, 10, 0, 0)
            };

            var quickLabel = new TextBlock
            {
                Text = "Quick Presets:",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(quickLabel, 0);
            footerGrid.Children.Add(quickLabel);

            var presetsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            PopulateQuickPresetsBar(presetsPanel);
            Grid.SetColumn(presetsPanel, 1);
            footerGrid.Children.Add(presetsPanel);

            var escHint = new TextBlock
            {
                Text = "Wheel / ↑↓ supported • Esc to close",
                FontSize = 10.5,
                Foreground = AppBrush("AppTextMutedBrush", "#64748B"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(escHint, 2);
            footerGrid.Children.Add(escHint);

            Grid.SetRow(footerGrid, 2);
            rootGrid.Children.Add(footerGrid);

            outerBorder.Child = rootGrid;
            Content = outerBorder;
        }

        private void PopulateQuickPresetsBar(StackPanel presetsPanel)
        {
            presetsPanel.Children.Clear();
            var cfg = ScreenBrightnessService.LoadConfig();
            var quickProfiles = ScreenBrightnessService.GetTrayQuickActionProfiles(cfg);

            foreach (var p in quickProfiles)
            {
                bool isCurrent = p.Id == cfg.LastCycleProfileId;
                var btn = new Button
                {
                    Content = p.Name,
                    Classes = { isCurrent ? "Primary" : "Secondary" },
                    Padding = new Thickness(10, 4),
                    FontSize = 11
                };
                ToolTip.SetTip(btn, ScreenBrightnessService.FormatProfileSummary(p, cfg));
                btn.Click += (_, _) =>
                {
                    Program.PlaySound("click");
                    for (int i = 0; i < _containers.Length; i++)
                    {
                        _containers[i].UserTouched = true;
                    }
                    ScreenBrightnessService.ApplyProfileAsync(p);
                };
                presetsPanel.Children.Add(btn);
            }
        }

        private async void OnOverlayOpened(object? sender, EventArgs e)
        {
            _liveReadCts?.Cancel();
            _liveReadCts?.Dispose();
            _liveReadCts = new CancellationTokenSource();
            var token = _liveReadCts.Token;

            try
            {
                var liveStates = await ScreenBrightnessService.ReadAllScreensLiveAsync(token);
                if (token.IsCancellationRequested || _isClosing) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_isClosing) return;
                    int connectedCount = 0;
                    foreach (var state in liveStates)
                    {
                        if (state.IsConnected) connectedCount++;
                        var view = _containers[state.SlotIndex];
                        ScreenContainerBuilder.ApplyLiveStateToContainer(view, state, respectUserTouch: true);
                    }

                    if (_headerStatusText != null)
                    {
                        _headerStatusText.Text = $"{connectedCount} Display{(connectedCount == 1 ? "" : "s")} Active • 250ms Hardware Limiter Ready";
                    }
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
                if (_isClosing) return;
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

        private void PositionNearTrayClamped()
        {
            const int margin = 12;
            double winW = Width;
            double winH = Height;

            try
            {
                GetCursorPos(out POINT pt);
                var screen = Screens.ScreenFromPoint(new PixelPoint(pt.X, pt.Y)) ?? Screens.Primary;
                if (screen != null)
                {
                    double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
                    int physW = (int)Math.Ceiling(winW * scale);
                    int physH = (int)Math.Ceiling(winH * scale);

                    var work = screen.WorkingArea;
                    int targetX = pt.X - physW / 2;
                    int targetY = pt.Y - physH - 16;

                    if (targetY < work.Y + margin)
                    {
                        targetY = pt.Y + 16;
                    }

                    int minX = work.X + margin;
                    int maxX = Math.Max(minX, work.X + work.Width - physW - margin);
                    int minY = work.Y + margin;
                    int maxY = Math.Max(minY, work.Y + work.Height - physH - margin);

                    int clampedX = Math.Clamp(targetX, minX, maxX);
                    int clampedY = Math.Clamp(targetY, minY, maxY);

                    Position = new PixelPoint(clampedX, clampedY);
                    return;
                }
            }
            catch { }

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void OnOverlayDeactivated(object? sender, EventArgs e)
        {
            CloseSafe();
        }

        private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseSafe();
            }
        }

        private void CloseSafe()
        {
            if (_isClosing) return;
            _isClosing = true;
            try { Close(); } catch { }
        }

        private void OnOverlayClosed(object? sender, EventArgs e)
        {
            _isClosing = true;
            ScreenBrightnessService.StateChanged -= OnServiceStateChanged;
            _liveReadCts?.Cancel();
            _liveReadCts?.Dispose();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        internal static IBrush AppBrush(string key, string fallbackHex)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b)
                {
                    return b;
                }
            }
            catch { }
            return new SolidColorBrush(Color.Parse(fallbackHex));
        }
    }

    internal static class ScreenContainerBuilder
    {
        public static ScreenContainerView CreateScreenContainer(ScreenLiveState initialState, Action? onNickChanged = null)
        {
            int slotIndex = initialState.SlotIndex;
            string slotLabel = initialState.DefaultSlotLabel;

            var card = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10)
            };

            var mainGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto")
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 6
            };

            var slotBadge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(initialState.IsConnected ? "#0284C7" : "#475569")),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = slotLabel,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                }
            };
            Grid.SetColumn(slotBadge, 0);
            headerGrid.Children.Add(slotBadge);

            string displayNick = string.IsNullOrWhiteSpace(initialState.Nickname) ? slotLabel : initialState.Nickname;
            var titleText = new TextBlock
            {
                Text = displayNick,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(titleText, "Click to rename this screen inline.");

            var inlineRenameBox = new TextBox
            {
                Text = displayNick,
                FontSize = 12,
                Padding = new Thickness(6, 2),
                MinHeight = 24,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };

            var editIconBtn = new Button
            {
                Content = "✎",
                Classes = { "Secondary" },
                Padding = new Thickness(5, 1),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(editIconBtn, "Rename this screen inline (press Enter to save).");

            void BeginInlineRename()
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                var s = cfg.Screens.First(x => x.SlotIndex == slotIndex);
                inlineRenameBox.Text = string.IsNullOrWhiteSpace(s.CustomNickname) ? slotLabel : s.CustomNickname;
                titleText.IsVisible = false;
                inlineRenameBox.IsVisible = true;
                inlineRenameBox.Focus();
                inlineRenameBox.SelectAll();
            }

            void CommitInlineRename()
            {
                if (!inlineRenameBox.IsVisible) return;
                string newNick = (inlineRenameBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(newNick))
                {
                    newNick = slotLabel;
                }
                inlineRenameBox.IsVisible = false;
                titleText.Text = newNick;
                titleText.IsVisible = true;

                var cfg = ScreenBrightnessService.LoadConfig();
                var s = cfg.Screens.First(x => x.SlotIndex == slotIndex);
                if (s.CustomNickname != newNick)
                {
                    s.CustomNickname = newNick;
                    ScreenBrightnessService.SaveConfig(cfg);
                    onNickChanged?.Invoke();
                }
            }

            titleText.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                BeginInlineRename();
            };

            editIconBtn.Click += (_, _) =>
            {
                if (inlineRenameBox.IsVisible) CommitInlineRename();
                else BeginInlineRename();
            };

            inlineRenameBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    ke.Handled = true;
                    CommitInlineRename();
                }
                else if (ke.Key == Key.Escape)
                {
                    ke.Handled = true;
                    inlineRenameBox.IsVisible = false;
                    titleText.IsVisible = true;
                }
            };
            inlineRenameBox.LostFocus += (_, _) => CommitInlineRename();

            Grid.SetColumn(titleText, 1);
            Grid.SetColumn(inlineRenameBox, 1);
            Grid.SetColumn(editIconBtn, 2);
            headerGrid.Children.Add(titleText);
            headerGrid.Children.Add(inlineRenameBox);
            headerGrid.Children.Add(editIconBtn);

            Grid.SetRow(headerGrid, 0);
            mainGrid.Children.Add(headerGrid);

            var modelSubtitle = new TextBlock
            {
                Text = FormatModelSubtitle(initialState),
                FontSize = 10.5,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 8)
            };
            Grid.SetRow(modelSubtitle, 1);
            mainGrid.Children.Add(modelSubtitle);

            var slidersGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 8,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var brightnessValueText = new TextBlock
            {
                Text = initialState.Brightness.ToString(),
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var brightnessSlider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                Maximum = 100,
                Value = initialState.Brightness,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 145
            };

            var leftColumn = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 4
            };
            Grid.SetRow(brightnessValueText, 0);
            Grid.SetRow(brightnessSlider, 1);
            var bLabel = new TextBlock
            {
                Text = "Brightness",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#E2E8F0"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(bLabel, 2);
            leftColumn.Children.Add(brightnessValueText);
            leftColumn.Children.Add(brightnessSlider);
            leftColumn.Children.Add(bLabel);
            Grid.SetColumn(leftColumn, 0);
            slidersGrid.Children.Add(leftColumn);

            var contrastValueText = new TextBlock
            {
                Text = initialState.Contrast.ToString(),
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#A78BFA")),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var contrastSlider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                Maximum = 100,
                Value = initialState.Contrast,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 145
            };

            var rightColumn = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 4
            };
            Grid.SetRow(contrastValueText, 0);
            Grid.SetRow(contrastSlider, 1);
            var cLabel = new TextBlock
            {
                Text = "Contrast",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#E2E8F0"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(cLabel, 2);
            rightColumn.Children.Add(contrastValueText);
            rightColumn.Children.Add(contrastSlider);
            rightColumn.Children.Add(cLabel);
            Grid.SetColumn(rightColumn, 1);
            slidersGrid.Children.Add(rightColumn);

            Grid.SetRow(slidersGrid, 2);
            mainGrid.Children.Add(slidersGrid);

            var lockRatioCheck = new CheckBox
            {
                Content = "Lock Ratio",
                IsChecked = initialState.LockRatio,
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 2)
            };
            ToolTip.SetTip(lockRatioCheck, "When checked, Brightness and Contrast move together tightly on this screen. Uncheck to adjust them separately.");
            Grid.SetRow(lockRatioCheck, 3);
            mainGrid.Children.Add(lockRatioCheck);

            var statusText = new TextBlock
            {
                Text = initialState.StatusMessage,
                FontSize = 10,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#64748B"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(statusText, 4);
            mainGrid.Children.Add(statusText);

            card.Child = mainGrid;

            var view = new ScreenContainerView
            {
                SlotIndex = slotIndex,
                RootCard = card,
                TitleText = titleText,
                InlineRenameBox = inlineRenameBox,
                ModelSubtitle = modelSubtitle,
                BrightnessValueText = brightnessValueText,
                ContrastValueText = contrastValueText,
                BrightnessSlider = brightnessSlider,
                ContrastSlider = contrastSlider,
                LockRatioCheckBox = lockRatioCheck,
                StatusBadgeText = statusText
            };

            brightnessSlider.PointerPressed += (_, _) => view.UserTouched = true;
            contrastSlider.PointerPressed += (_, _) => view.UserTouched = true;

            brightnessSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || view.SuppressSliderEvents) return;
                view.UserTouched = true;
                int b = (int)Math.Round(brightnessSlider.Value);
                brightnessValueText.Text = b.ToString();

                bool locked = lockRatioCheck.IsChecked == true;
                if (locked)
                {
                    view.SuppressSliderEvents = true;
                    contrastSlider.Value = b;
                    contrastValueText.Text = b.ToString();
                    view.SuppressSliderEvents = false;
                }

                int c = locked ? b : (int)Math.Round(contrastSlider.Value);
                ScreenBrightnessService.RequestScreenLevelChange(slotIndex, b, c, updateContrastFromLock: locked);
            };

            contrastSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || view.SuppressSliderEvents) return;
                view.UserTouched = true;
                int c = (int)Math.Round(contrastSlider.Value);
                contrastValueText.Text = c.ToString();

                bool locked = lockRatioCheck.IsChecked == true;
                if (locked)
                {
                    view.SuppressSliderEvents = true;
                    brightnessSlider.Value = c;
                    brightnessValueText.Text = c.ToString();
                    view.SuppressSliderEvents = false;
                }

                int b = locked ? c : (int)Math.Round(brightnessSlider.Value);
                ScreenBrightnessService.RequestScreenLevelChange(slotIndex, b, c, updateContrastFromLock: false);
            };

            void AttachWheelHandler(Control target, Slider sliderToAdjust)
            {
                target.PointerWheelChanged += (_, we) =>
                {
                    view.UserTouched = true;
                    int delta = we.Delta.Y > 0 ? 2 : (we.Delta.Y < 0 ? -2 : 0);
                    if (delta != 0)
                    {
                        we.Handled = true;
                        sliderToAdjust.Value = Math.Clamp(sliderToAdjust.Value + delta, 0, 100);
                    }
                };
            }

            AttachWheelHandler(leftColumn, brightnessSlider);
            AttachWheelHandler(rightColumn, contrastSlider);

            lockRatioCheck.IsCheckedChanged += (_, _) =>
            {
                if (view.SuppressSliderEvents) return;
                view.UserTouched = true;
                bool isLocked = lockRatioCheck.IsChecked == true;
                var cfg = ScreenBrightnessService.LoadConfig();
                var s = cfg.Screens.First(x => x.SlotIndex == slotIndex);
                s.LockRatio = isLocked;
                ScreenBrightnessService.SaveConfig(cfg);

                if (isLocked)
                {
                    int b = (int)Math.Round(brightnessSlider.Value);
                    view.SuppressSliderEvents = true;
                    contrastSlider.Value = b;
                    contrastValueText.Text = b.ToString();
                    view.SuppressSliderEvents = false;
                    ScreenBrightnessService.RequestScreenLevelChange(slotIndex, b, b, updateContrastFromLock: true);
                }
            };

            return view;
        }

        public static void ApplyLiveStateToContainer(ScreenContainerView view, ScreenLiveState state, bool respectUserTouch)
        {
            view.ModelSubtitle.Text = FormatModelSubtitle(state);
            view.StatusBadgeText.Text = state.StatusMessage;

            if (respectUserTouch && view.UserTouched)
            {
                return;
            }

            view.SuppressSliderEvents = true;
            try
            {
                view.LockRatioCheckBox.IsChecked = state.LockRatio;
                view.BrightnessSlider.Value = state.Brightness;
                view.BrightnessValueText.Text = state.Brightness.ToString();
                int c = state.LockRatio ? state.Brightness : state.Contrast;
                view.ContrastSlider.Value = c;
                view.ContrastValueText.Text = c.ToString();
            }
            finally
            {
                view.SuppressSliderEvents = false;
            }
        }

        public static void SyncFromConfigState(ScreenContainerView view, ScreenLiveState state)
        {
            view.SuppressSliderEvents = true;
            try
            {
                if (!view.InlineRenameBox.IsVisible)
                {
                    view.TitleText.Text = string.IsNullOrWhiteSpace(state.Nickname) ? state.DefaultSlotLabel : state.Nickname;
                }
                view.LockRatioCheckBox.IsChecked = state.LockRatio;
                view.BrightnessSlider.Value = state.Brightness;
                view.BrightnessValueText.Text = state.Brightness.ToString();
                int c = state.LockRatio ? state.Brightness : state.Contrast;
                view.ContrastSlider.Value = c;
                view.ContrastValueText.Text = c.ToString();
            }
            finally
            {
                view.SuppressSliderEvents = false;
            }
        }

        private static string FormatModelSubtitle(ScreenLiveState state)
        {
            if (!state.IsConnected)
            {
                return "Not Connected (Preset Slot)";
            }
            string model = string.IsNullOrWhiteSpace(state.ModelName) ? "Connected Display" : state.ModelName;
            return state.IsPrimary ? $"{model} • Primary" : model;
        }
    }
}
