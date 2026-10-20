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
        public TextBlock ContrastLabelText { get; init; } = null!;
        public Grid RightContrastColumn { get; init; } = null!;
        public Slider BrightnessSlider { get; init; } = null!;
        public Slider ContrastSlider { get; init; } = null!;
        public CheckBox LockRatioCheckBox { get; init; } = null!;
        public TextBlock StatusBadgeText { get; init; } = null!;
        public bool SupportsContrast { get; set; } = true;
        public bool IsInternalLcd { get; set; }
        public bool UserTouched { get; set; }
        public bool SuppressSliderEvents { get; set; }
        public Action? CommitPendingRename { get; set; }
    }

    public sealed class ScreenBrightnessOverlayWindow : Window
    {
        private static ScreenBrightnessOverlayWindow? _instance;
        private readonly ScreenContainerView[] _containers = new ScreenContainerView[ScreenBrightnessService.MaxScreens];
        private Grid _containersGrid = null!;
        private Button? _identifyBtn;
        private CancellationTokenSource? _liveReadCts;
        private TextBlock? _headerStatusText;
        private Border? _masterBar;
        private Slider? _masterSlider;
        private TextBlock? _masterValueText;
        private int _lastMasterValue = 50;
        private bool _suppressMasterEvents;
        private DateTime _openedUtc = DateTime.MinValue;
        private bool _isClosing;

        public ScreenBrightnessOverlayWindow()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            Title = "Screen Brightness Quick Sliders";
            Width = cfg.OverlayWindowWidth >= 500 ? cfg.OverlayWindowWidth : 700;
            Height = cfg.OverlayWindowHeight >= 400 ? cfg.OverlayWindowHeight : 540;
            MinWidth = 620;
            MinHeight = 480;
            CanResize = true;
            ShowInTaskbar = false;
            Topmost = true;
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            WindowStartupLocation = WindowStartupLocation.Manual;
            Focusable = true;

            BuildUi();

            PointerPressed += OnOverlayPointerPressed;
            Opened += OnOverlayOpened;
            Deactivated += OnOverlayDeactivated;
            KeyDown += OnOverlayKeyDown;
            Closed += OnOverlayClosed;
            ScreenBrightnessService.StateChanged += OnServiceStateChanged;
        }

        private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual v)
            {
                for (int i = 0; i < _containers.Length; i++)
                {
                    var c = _containers[i];
                    if (c?.InlineRenameBox != null && c.InlineRenameBox.IsVisible)
                    {
                        if (!ReferenceEquals(v, c.InlineRenameBox) && !IsVisualInside(v, c.InlineRenameBox))
                        {
                            c.CommitPendingRename?.Invoke();
                            Focus();
                        }
                    }
                }
            }
        }

        internal static bool IsVisualInside(Visual? element, Visual target)
        {
            var cur = element;
            while (cur != null)
            {
                if (ReferenceEquals(cur, target)) return true;
                cur = cur.Parent as Visual;
            }
            return false;
        }

        public static void ShowOverlay()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_instance != null)
                {
                    try
                    {
                        var latestStates = ScreenBrightnessService.GetImmediateCachedStates();
                        _instance.UpdateVisibleScreenContainers(latestStates);
                        _instance._openedUtc = DateTime.UtcNow;
                        _instance.Show();
                        _instance.PositionOrRestoreClamped();
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
                _instance._openedUtc = DateTime.UtcNow;
                _instance.Show();
                _instance.PositionOrRestoreClamped();
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
                RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            headerGrid.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
                    e.Source is not Button && e.Source is not TextBox)
                {
                    BeginMoveDrag(e);
                }
            };
            outerBorder.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
                    (ReferenceEquals(e.Source, outerBorder) || ReferenceEquals(e.Source, rootGrid)))
                {
                    BeginMoveDrag(e);
                }
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

            _identifyBtn = new Button
            {
                Content = "Identify Screens",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5
            };
            ToolTip.SetTip(_identifyBtn, "Flash identification banners on your active displays without closing this overlay.");
            _identifyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.ShowIdentifyBanners();
            };
            headerActions.Children.Add(_identifyBtn);

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
            Avalonia.Automation.AutomationProperties.SetName(closeBtn, "Close");
            ToolTip.SetTip(closeBtn, "Close overlay (Esc)");
            closeBtn.Click += (_, _) => CloseSafe();
            headerActions.Children.Add(closeBtn);

            Grid.SetColumn(headerActions, 1);
            headerGrid.Children.Add(headerActions);
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            _masterBar = new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#1E293B"), 0.0),
                        new GradientStop(Color.Parse("#0F2942"), 1.0)
                    }
                },
                BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#38BDF8"), 0.0),
                        new GradientStop(Color.Parse("#818CF8"), 1.0)
                    }
                },
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 0, 10),
                IsVisible = false
            };

            var masterGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
                ColumnSpacing = 8
            };

            var masterBadge = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0284C7")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "SYNCED MASTER",
                    FontSize = 9.5,
                    FontWeight = FontWeight.ExtraBold,
                    Foreground = Brushes.White
                }
            };
            Grid.SetColumn(masterBadge, 0);
            masterGrid.Children.Add(masterBadge);

            var masterLabel = new TextBlock
            {
                Text = "⚡ ALL DISPLAYS (SYNCED):",
                FontSize = 11.5,
                FontWeight = FontWeight.Bold,
                Foreground = AppBrush("AppAccentBrush", "#38BDF8"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(masterLabel, 1);
            masterGrid.Children.Add(masterLabel);

            var stepDelta = ScreenBrightnessService.LoadConfig().StepDelta;
            if (stepDelta <= 0) stepDelta = 5;

            var masterMinusBtn = new Button
            {
                Content = $"−{stepDelta}%",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(masterMinusBtn, $"Step all displays down by {stepDelta}% (preserves differential, clamps at 0%)");
            masterMinusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                var curDelta = ScreenBrightnessService.LoadConfig().StepDelta;
                if (curDelta <= 0) curDelta = 5;
                ScreenBrightnessService.RequestMasterLevelChange(-curDelta);
            };
            Grid.SetColumn(masterMinusBtn, 2);
            masterGrid.Children.Add(masterMinusBtn);

            _masterSlider = new Slider
            {
                Orientation = Orientation.Horizontal,
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(_masterSlider, "Drag to adjust all displays together while maintaining their relative brightness differential.");
            _masterSlider.PropertyChanged += (_, pe) =>
            {
                if (pe.Property != RangeBase.ValueProperty || _suppressMasterEvents) return;
                int currentVal = (int)Math.Round(_masterSlider.Value);
                int delta = currentVal - _lastMasterValue;
                if (delta != 0)
                {
                    _lastMasterValue = currentVal;
                    if (_masterValueText != null) _masterValueText.Text = currentVal.ToString();
                    ScreenBrightnessService.RequestMasterLevelChange(delta);
                }
            };
            Grid.SetColumn(_masterSlider, 3);
            masterGrid.Children.Add(_masterSlider);

            var masterPlusBtn = new Button
            {
                Content = $"+{stepDelta}%",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(masterPlusBtn, $"Step all displays up by {stepDelta}% (preserves differential, clamps at 100%)");
            masterPlusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                var curDelta = ScreenBrightnessService.LoadConfig().StepDelta;
                if (curDelta <= 0) curDelta = 5;
                ScreenBrightnessService.RequestMasterLevelChange(curDelta);
            };
            Grid.SetColumn(masterPlusBtn, 4);
            masterGrid.Children.Add(masterPlusBtn);

            _masterValueText = new TextBlock
            {
                Text = "50",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 24,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(_masterValueText, 5);
            masterGrid.Children.Add(_masterValueText);

            _masterBar.Child = masterGrid;
            Grid.SetRow(_masterBar, 1);
            rootGrid.Children.Add(_masterBar);

            _containersGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*"),
                ColumnSpacing = 10
            };

            var initialStates = ScreenBrightnessService.GetImmediateCachedStates();
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                var state = initialStates.First(s => s.SlotIndex == i);
                var containerView = ScreenContainerBuilder.CreateScreenContainer(
                    state,
                    onNickChanged: () => ScreenBrightnessService.NotifyStateChanged(),
                    onBrightnessChanged: (slot, val) => UpdateMasterBaselineFromScreens());
                _containers[i] = containerView;
            }

            UpdateVisibleScreenContainers(initialStates);

            Grid.SetRow(_containersGrid, 2);
            rootGrid.Children.Add(_containersGrid);

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

            Grid.SetRow(footerGrid, 3);
            rootGrid.Children.Add(footerGrid);

            outerBorder.Child = rootGrid;
            Content = outerBorder;
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

            if (_identifyBtn != null)
            {
                _identifyBtn.Content = activeCount == 1 ? "Identify Screen" : "Identify Screens";
            }

            if (_masterBar != null)
            {
                _masterBar.IsVisible = activeCount > 1;
                if (activeCount > 1 && _masterSlider != null)
                {
                    var baseScreen = activeStates.FirstOrDefault(s => s.SlotIndex == 0) ?? activeStates[0];
                    _suppressMasterEvents = true;
                    _masterSlider.Value = baseScreen.Brightness;
                    _lastMasterValue = baseScreen.Brightness;
                    if (_masterValueText != null) _masterValueText.Text = baseScreen.Brightness.ToString();
                    _suppressMasterEvents = false;
                }
            }

            var ovCfg = ScreenBrightnessService.LoadConfig();
            if (double.IsNaN(ovCfg.OverlayWindowX) && (ovCfg.OverlayWindowWidth <= 0 || ovCfg.OverlayWindowWidth == 700))
            {
                double targetWidth = activeCount switch
                {
                    1 => 440,
                    2 => 580,
                    _ => 700
                };

                if (Math.Abs(Width - targetWidth) > 1)
                {
                    Width = targetWidth;
                }
            }
        }

        private void UpdateMasterBaselineFromScreens()
        {
            if (_masterSlider == null) return;
            var states = ScreenBrightnessService.GetImmediateCachedStates();
            var activeStates = states.Where(s => s.IsConnected).OrderBy(s => s.SlotIndex).ToList();
            if (activeStates.Count <= 1) return;

            var baseScreen = activeStates.FirstOrDefault(s => s.SlotIndex == 0) ?? activeStates[0];
            _suppressMasterEvents = true;
            _masterSlider.Value = baseScreen.Brightness;
            _lastMasterValue = baseScreen.Brightness;
            if (_masterValueText != null) _masterValueText.Text = baseScreen.Brightness.ToString();
            _suppressMasterEvents = false;
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
            _openedUtc = DateTime.UtcNow;
            PositionOrRestoreClamped();
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
                    UpdateVisibleScreenContainers(liveStates);
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
                UpdateVisibleScreenContainers(states);
                foreach (var st in states)
                {
                    if (st.SlotIndex >= 0 && st.SlotIndex < _containers.Length && _containers[st.SlotIndex] != null)
                    {
                        ScreenContainerBuilder.SyncFromConfigState(_containers[st.SlotIndex], st);
                    }
                }
            });
        }

        private void PositionOrRestoreClamped()
        {
            try
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                if (!double.IsNaN(cfg.OverlayWindowX) && !double.IsNaN(cfg.OverlayWindowY))
                {
                    int x = (int)Math.Round(cfg.OverlayWindowX);
                    int y = (int)Math.Round(cfg.OverlayWindowY);
                    var screens = Screens;
                    if (screens != null)
                    {
                        var screen = screens.ScreenFromPoint(new PixelPoint(x, y));
                        if (screen != null)
                        {
                            Position = new PixelPoint(x, y);
                            return;
                        }
                    }
                }
            }
            catch { }

            PositionNearTrayClamped();
        }

        private void SaveWindowGeometry()
        {
            try
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                cfg.OverlayWindowX = Position.X;
                cfg.OverlayWindowY = Position.Y;
                cfg.OverlayWindowWidth = (int)Math.Round(Width);
                cfg.OverlayWindowHeight = (int)Math.Round(Height);
                ScreenBrightnessService.SaveConfig(cfg);
            }
            catch { }
        }

        private void PositionNearTrayClamped()
        {
            const int margin = 12;
            double winW = Width;
            double winH = Height;

            try
            {
                GetCursorPos(out POINT pt);
                var screens = Screens;
                if (screens == null)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    return;
                }

                var screen = screens.ScreenFromPoint(new PixelPoint(pt.X, pt.Y)) ?? screens.Primary;
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
            if ((DateTime.UtcNow - _openedUtc).TotalMilliseconds < 600) return;
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
            SaveWindowGeometry();
            ScreenBrightnessService.FlushConfig();
            try { Close(); } catch { }
        }

        private void OnOverlayClosed(object? sender, EventArgs e)
        {
            _isClosing = true;
            ScreenBrightnessService.FlushConfig();
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
        public static ScreenContainerView CreateScreenContainer(ScreenLiveState initialState, Action? onNickChanged = null, Action<int, int>? onBrightnessChanged = null)
        {
            int slotIndex = initialState.SlotIndex;
            string slotLabel = initialState.DefaultSlotLabel;

            var card = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
                Focusable = true,
                ClipToBounds = true
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
                Background = initialState.IsConnected
                    ? ScreenBrightnessOverlayWindow.AppBrush("AppAccentBrush", "#0284C7")
                    : ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#475569"),
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
            ToolTip.SetTip(titleText, "Click to give this display a friendly nickname (up to 32 characters).");

            var inlineRenameBox = new TextBox
            {
                Text = displayNick,
                MaxLength = 32,
                FontSize = 12,
                Padding = new Thickness(6, 3),
                MinHeight = 26,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(inlineRenameBox, "Type a nickname up to 32 characters, then click ✓ (or press Enter) to save, or ✕ (Esc) to cancel.");

            var editIconBtn = new Button
            {
                Content = "✎",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(editIconBtn, "Rename this display (up to 32 characters).");

            var saveRenameBtn = new Button
            {
                Content = "✓",
                Classes = { "Primary" },
                Padding = new Thickness(7, 2),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(saveRenameBtn, "Save display nickname (Enter)");

            var cancelRenameBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(cancelRenameBtn, "Cancel renaming (Esc)");

            var renameActionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            renameActionsPanel.Children.Add(saveRenameBtn);
            renameActionsPanel.Children.Add(cancelRenameBtn);

            var statusText = new TextBlock
            {
                Text = initialState.StatusMessage,
                FontSize = 10,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#64748B"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2
            };

            bool isCancellingRename = false;

            void BeginInlineRename()
            {
                isCancellingRename = false;
                var cfg = ScreenBrightnessService.LoadConfig();
                var s = cfg.Screens.First(x => x.SlotIndex == slotIndex);
                inlineRenameBox.Text = string.IsNullOrWhiteSpace(s.CustomNickname) ? slotLabel : s.CustomNickname;
                titleText.IsVisible = false;
                editIconBtn.IsVisible = false;
                inlineRenameBox.IsVisible = true;
                renameActionsPanel.IsVisible = true;
                inlineRenameBox.Focus();
                inlineRenameBox.SelectAll();
            }

            void CancelInlineRename()
            {
                if (!inlineRenameBox.IsVisible) return;
                isCancellingRename = true;
                inlineRenameBox.IsVisible = false;
                renameActionsPanel.IsVisible = false;
                titleText.IsVisible = true;
                editIconBtn.IsVisible = true;
                isCancellingRename = false;
            }

            void CommitInlineRename()
            {
                if (!inlineRenameBox.IsVisible || isCancellingRename) return;
                string newNick = (inlineRenameBox.Text ?? string.Empty).Trim();
                if (newNick.Length > 32) newNick = newNick.Substring(0, 32).Trim();
                if (string.IsNullOrWhiteSpace(newNick))
                {
                    newNick = slotLabel;
                }

                var cfg = ScreenBrightnessService.LoadConfig();
                if (cfg.Screens.Any(x => x.SlotIndex != slotIndex && string.Equals(x.CustomNickname, newNick, StringComparison.OrdinalIgnoreCase)))
                {
                    statusText.Text = $"⚠️ Nickname \"{newNick}\" is already used by another screen.";
                    inlineRenameBox.IsVisible = false;
                    renameActionsPanel.IsVisible = false;
                    titleText.IsVisible = true;
                    editIconBtn.IsVisible = true;
                    return;
                }

                inlineRenameBox.IsVisible = false;
                renameActionsPanel.IsVisible = false;
                titleText.Text = newNick;
                titleText.IsVisible = true;
                editIconBtn.IsVisible = true;

                var s = cfg.Screens.First(x => x.SlotIndex == slotIndex);
                if (s.CustomNickname != newNick)
                {
                    s.CustomNickname = newNick;
                    ScreenBrightnessService.SaveConfig(cfg);
                    statusText.Text = $"✓ Saved display name \"{newNick}\"";
                    onNickChanged?.Invoke();
                }
                else
                {
                    statusText.Text = $"✓ Display name \"{newNick}\"";
                }
            }

            titleText.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                BeginInlineRename();
            };

            editIconBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                BeginInlineRename();
            };

            saveRenameBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                CommitInlineRename();
                card.Focus();
            };

            cancelRenameBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                CancelInlineRename();
                card.Focus();
            };

            inlineRenameBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    ke.Handled = true;
                    Program.PlaySound("click");
                    CommitInlineRename();
                    card.Focus();
                }
                else if (ke.Key == Key.Escape)
                {
                    ke.Handled = true;
                    CancelInlineRename();
                    card.Focus();
                }
            };
            inlineRenameBox.LostFocus += (_, _) => CommitInlineRename();

            card.PointerPressed += (_, pe) =>
            {
                if (inlineRenameBox.IsVisible && pe.Source is Visual v)
                {
                    if (!ReferenceEquals(v, inlineRenameBox) &&
                        !ScreenBrightnessOverlayWindow.IsVisualInside(v, inlineRenameBox) &&
                        !ScreenBrightnessOverlayWindow.IsVisualInside(v, renameActionsPanel))
                    {
                        CommitInlineRename();
                        card.Focus();
                    }
                }
            };

            Grid.SetColumn(titleText, 1);
            Grid.SetColumn(inlineRenameBox, 1);
            Grid.SetColumn(editIconBtn, 2);
            Grid.SetColumn(renameActionsPanel, 2);
            headerGrid.Children.Add(titleText);
            headerGrid.Children.Add(inlineRenameBox);
            headerGrid.Children.Add(editIconBtn);
            headerGrid.Children.Add(renameActionsPanel);

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
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppAccentBrush", "#38BDF8"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
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
                MinHeight = 110,
                Margin = new Thickness(0, 4, 0, 4)
            };
            ToolTip.SetTip(brightnessSlider, "Adjust hardware backlight brightness (0% to 100%). Use mouse wheel or drag.");

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
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
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
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#A78BFA"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
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
                MinHeight = 110,
                Margin = new Thickness(0, 4, 0, 4)
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
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(cLabel, 2);
            rightColumn.Children.Add(contrastValueText);
            rightColumn.Children.Add(contrastSlider);
            rightColumn.Children.Add(cLabel);
            Grid.SetColumn(rightColumn, 1);
            slidersGrid.Children.Add(rightColumn);

            Grid.SetRow(slidersGrid, 2);
            mainGrid.Children.Add(slidersGrid);

            var lockRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 2)
            };

            var lockRatioCheck = new CheckBox
            {
                Content = "Sync ratio",
                IsChecked = initialState.LockRatio,
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(lockRatioCheck, "When checked, Contrast automatically matches Brightness on this monitor. Uncheck to tune Contrast independently.");

            var lockHelpBadge = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelBrush", "#111827"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Width = 24,
                Height = 24,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Help),
                Child = new TextBlock
                {
                    Text = "?",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
                }
            };
            ToolTip.SetTip(lockHelpBadge, "Sync Ratio keeps Brightness and Contrast locked at the same level so moving Brightness adjusts both together. Uncheck to unlock independent Contrast control.");
            Avalonia.Automation.AutomationProperties.SetName(lockHelpBadge, "Help: Sync ratio keeps Brightness and Contrast locked at the same level");

            lockRow.Children.Add(lockRatioCheck);
            lockRow.Children.Add(lockHelpBadge);

            Grid.SetRow(lockRow, 3);
            mainGrid.Children.Add(lockRow);

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
                ContrastLabelText = cLabel,
                RightContrastColumn = rightColumn,
                BrightnessSlider = brightnessSlider,
                ContrastSlider = contrastSlider,
                LockRatioCheckBox = lockRatioCheck,
                StatusBadgeText = statusText,
                SupportsContrast = !initialState.IsConnected || initialState.SupportsContrast,
                IsInternalLcd = initialState.IsInternalLcd,
                CommitPendingRename = CommitInlineRename
            };

            UpdateContrastInteractivity(view);

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
                onBrightnessChanged?.Invoke(slotIndex, b);
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
                    if (!sliderToAdjust.IsEnabled) return;
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
                cfg.GlobalLockBrightnessContrast = cfg.Screens.All(x => x.LockRatio);
                ScreenBrightnessService.SaveConfig(cfg);

                UpdateContrastInteractivity(view);

                if (isLocked)
                {
                    int b = (int)Math.Round(brightnessSlider.Value);
                    view.SuppressSliderEvents = true;
                    contrastSlider.Value = b;
                    contrastValueText.Text = b.ToString();
                    view.SuppressSliderEvents = false;
                    ScreenBrightnessService.RequestScreenLevelChange(slotIndex, b, b, updateContrastFromLock: true);
                }

                ScreenBrightnessService.NotifyStateChanged();
            };

            return view;
        }

        public static void UpdateContrastInteractivity(ScreenContainerView view)
        {
            bool locked = view.LockRatioCheckBox.IsChecked == true;
            bool supportsContrast = view.SupportsContrast && !view.IsInternalLcd;

            if (!supportsContrast)
            {
                view.ContrastSlider.IsEnabled = false;
                view.RightContrastColumn.Opacity = 0.42;
                view.ContrastLabelText.Text = "Contrast (N/A)";
                view.LockRatioCheckBox.IsEnabled = false;
                ToolTip.SetTip(view.RightContrastColumn, "Built-in laptop displays control backlight brightness only and do not support hardware DDC/CI contrast.");
            }
            else if (locked)
            {
                view.ContrastSlider.IsEnabled = false;
                view.RightContrastColumn.Opacity = 0.62;
                view.ContrastLabelText.Text = "Contrast (Synced)";
                view.LockRatioCheckBox.IsEnabled = true;
                ToolTip.SetTip(view.RightContrastColumn, "Contrast is synced with Brightness. Uncheck 'Sync ratio' below to adjust Contrast independently.");
            }
            else
            {
                view.ContrastSlider.IsEnabled = true;
                view.RightContrastColumn.Opacity = 1.0;
                view.ContrastLabelText.Text = "Contrast";
                view.LockRatioCheckBox.IsEnabled = true;
                ToolTip.SetTip(view.RightContrastColumn, "Adjust hardware DDC/CI contrast independently (0% to 100%).");
            }
        }

        public static void ApplyLiveStateToContainer(ScreenContainerView view, ScreenLiveState state, bool respectUserTouch)
        {
            view.ModelSubtitle.Text = FormatModelSubtitle(state);
            view.StatusBadgeText.Text = state.StatusMessage;
            view.SupportsContrast = !state.IsConnected || state.SupportsContrast;
            view.IsInternalLcd = state.IsInternalLcd;

            if (respectUserTouch && view.UserTouched)
            {
                UpdateContrastInteractivity(view);
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
                UpdateContrastInteractivity(view);
            }
            finally
            {
                view.SuppressSliderEvents = false;
            }
        }

        public static void SyncFromConfigState(ScreenContainerView view, ScreenLiveState state)
        {
            if (view.UserTouched) return;
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
                UpdateContrastInteractivity(view);
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
