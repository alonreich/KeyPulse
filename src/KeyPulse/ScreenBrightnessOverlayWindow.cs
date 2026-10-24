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
        public Grid SlidersGrid { get; init; } = null!;
        public TextBlock BrightnessLabelText { get; init; } = null!;
        public Slider BrightnessSlider { get; init; } = null!;
        public Slider ContrastSlider { get; init; } = null!;
        public CheckBox LockRatioCheckBox { get; init; } = null!;
        public Border SlotBadge { get; init; } = null!;
        public TextBlock StatusBadgeText { get; init; } = null!;
        public bool SupportsContrast { get; set; } = true;
        public bool ShowLockRatioToggle { get; init; } = true;
        public bool IsInternalLcd { get; set; }
        public bool UserTouched { get; set; }
        public bool IsDraggingBrightness { get; set; }
        public bool IsDraggingContrast { get; set; }
        public bool SuppressSliderEvents { get; set; }
        public Action? CommitPendingRename { get; set; }
    }

    public sealed class ScreenBrightnessOverlayWindow : Window
    {
        private static ScreenBrightnessOverlayWindow? _instance;
        private readonly ScreenContainerView[] _containers = new ScreenContainerView[ScreenBrightnessService.MaxScreens];
        private bool _isUserDraggingMaster;
        private List<(int SlotIndex, int Brightness, int Contrast, bool LockRatio, int VirtualBrightness, int VirtualContrast)> _masterDragBaselines = new();
        private double _masterDragStartValue;
        private int _brickWallUp;
        private int _brickWallDown;
        private bool _hasHitBrickWallUp;
        private bool _hasHitBrickWallDown;
        private bool _breakthroughUp;
        private bool _breakthroughDown;
        private int _lastPushedMasterValue = -1;
        private string _lastRenderedScreenConfig = string.Empty;
        private Grid _containersGrid = null!;
        private Button? _gearBtn;
        private CancellationTokenSource? _liveReadCts;
        private TextBlock? _headerStatusText;
        private bool _hardwareReadFailed;
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
            Title = "Brightness slider";
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
            if (!ScreenBrightnessService.IsFeatureActive) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!ScreenBrightnessService.IsFeatureActive) return;

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
                Text = "Brightness slider",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            // Shown only when something is wrong (hardware read failed); a healthy window shows just the title.
            _headerStatusText = new TextBlock
            {
                FontSize = 11,
                Foreground = AppBrush("AppWarningBrush", "#F59E0B"),
                IsVisible = false
            };
            titleStack.Children.Add(_headerStatusText);

            // F7: visible while Focus Mode has the other screens dimmed.
            _focusModeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppBrush("AppAccentBrush", "#38BDF8"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var focusOffBtn = new Button
            {
                Content = "Turn off",
                Classes = { "Secondary" },
                FontSize = 11,
                Padding = new Thickness(8, 1),
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(focusOffBtn, "Brings the other screens back to their brightness from before. The main screen stays as it is.");
            focusOffBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                if (ScreenBrightnessService.IsFocusModeActive) ScreenBrightnessService.ToggleFocusMode();
            };
            _focusModeChip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Margin = new Thickness(0, 2, 0, 0) };
            _focusModeChip.Children.Add(_focusModeText);
            _focusModeChip.Children.Add(focusOffBtn);
            titleStack.Children.Add(_focusModeChip);
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Everything that is not a slider lives behind one gear button.
            var separateContrastItem = new MenuItem
            {
                Header = "Separate contrast sliders",
                ToggleType = MenuItemToggleType.CheckBox
            };
            separateContrastItem.Click += (_, _) => SetSeparateContrast(separateContrastItem.IsChecked);

            var identifyItem = new MenuItem { Header = "Identify screens" };
            identifyItem.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.ShowIdentifyBanners();
            };

            var setupItem = new MenuItem { Header = "Brightness settings…" };
            setupItem.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessWindow.OpenWindow();
                CloseSafe();
            };

            var gearMenu = new MenuFlyout();
            gearMenu.Items.Add(separateContrastItem);
            gearMenu.Items.Add(identifyItem);
            gearMenu.Items.Add(new Separator());
            gearMenu.Items.Add(setupItem);
            gearMenu.Opening += (_, _) =>
                separateContrastItem.IsChecked = !ScreenBrightnessService.LoadConfig().Screens.All(x => x.LockRatio);

            _gearBtn = new Button
            {
                Content = "⚙",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 4),
                FontSize = 12,
                Flyout = gearMenu
            };
            Avalonia.Automation.AutomationProperties.SetName(_gearBtn, "More options");
            ToolTip.SetTip(_gearBtn, "More: separate contrast, identify screens, presets & setup.");
            headerActions.Children.Add(_gearBtn);

            var closeBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 4),
                FontSize = 12
            };
            Avalonia.Automation.AutomationProperties.SetName(closeBtn, "Close");
            ToolTip.SetTip(closeBtn, "Close (Esc). Tip: mouse wheel or ↑/↓ moves a slider.");
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

            var masterLabel = new TextBlock
            {
                Text = "All screens",
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
                Content = "−",
                Classes = { "Secondary" },
                Width = 32,
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(masterMinusBtn, $"Step all displays down by {stepDelta}% (maintains differential gap)");
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
            _masterSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
            {
                _isUserDraggingMaster = true;
                // Focus Mode: linked screens snap to the main screen before the drag baselines are read.
                ScreenBrightnessService.PrepareForMainScreenAdjustment();
                var states = ScreenBrightnessService.GetImmediateCachedStates();
                var activeScreens = states.Where(s => s.IsConnected).OrderBy(s => s.SlotIndex).ToList();
                if (activeScreens.Count == 0 && states.Count > 0)
                {
                    activeScreens.Add(states[0]);
                }
                var cfg = ScreenBrightnessService.LoadConfig();
                _masterDragBaselines = activeScreens.Select(s =>
                {
                    var sc = cfg.Screens.FirstOrDefault(x => x.SlotIndex == s.SlotIndex);
                    int vb = sc?.VirtualBrightness ?? s.Brightness;
                    int vc = sc?.VirtualContrast ?? s.Contrast;
                    if (vb == 0 && s.Brightness > 0) vb = s.Brightness;
                    if (vc == 0 && s.Contrast > 0) vc = s.Contrast;
                    return (s.SlotIndex, s.Brightness, s.Contrast, s.LockRatio, vb, vc);
                }).ToList();
                _masterDragStartValue = _masterSlider.Value;
                _lastPushedMasterValue = (int)Math.Round(_masterDragStartValue);

                int maxUp = _masterDragBaselines.Count > 0 ? _masterDragBaselines.Min(s => Math.Max(0, 100 - s.Brightness)) : 0;
                int maxDown = _masterDragBaselines.Count > 0 ? _masterDragBaselines.Min(s => Math.Max(0, s.Brightness)) : 0;

                _brickWallUp = (int)Math.Round(_masterDragStartValue + maxUp);
                _brickWallDown = (int)Math.Round(_masterDragStartValue - maxDown);

                _hasHitBrickWallUp = (maxUp == 0);
                _breakthroughUp = (maxUp == 0);
                _hasHitBrickWallDown = (maxDown == 0);
                _breakthroughDown = (maxDown == 0);
            }, RoutingStrategies.Tunnel);

            _masterSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
            {
                _isUserDraggingMaster = false;
                ScreenBrightnessService.FlushConfig();
                UpdateMasterBaselineFromScreens();
            }, RoutingStrategies.Tunnel);

            _masterSlider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
            {
            }, RoutingStrategies.Tunnel);

            _masterSlider.PropertyChanged += (_, pe) =>
            {
                if (pe.Property != RangeBase.ValueProperty || _suppressMasterEvents || !_isUserDraggingMaster) return;
                int targetVal = (int)Math.Round(_masterSlider.Value);
                if (targetVal == _lastPushedMasterValue) return;

                if (targetVal >= _brickWallUp && _brickWallUp < 100)
                {
                    if (!_breakthroughUp)
                    {
                        if (!_hasHitBrickWallUp)
                        {
                            _hasHitBrickWallUp = true;
                            targetVal = _brickWallUp;
                            _suppressMasterEvents = true;
                            _masterSlider.Value = _brickWallUp;
                            _suppressMasterEvents = false;
                        }
                        else
                        {
                            _breakthroughUp = true;
                        }
                    }
                }
                else if (targetVal <= _brickWallDown && _brickWallDown > 0)
                {
                    if (!_breakthroughDown)
                    {
                        if (!_hasHitBrickWallDown)
                        {
                            _hasHitBrickWallDown = true;
                            targetVal = _brickWallDown;
                            _suppressMasterEvents = true;
                            _masterSlider.Value = _brickWallDown;
                            _suppressMasterEvents = false;
                        }
                        else
                        {
                            _breakthroughDown = true;
                        }
                    }
                }

                if (targetVal < _brickWallUp)
                {
                    _hasHitBrickWallUp = false;
                    _breakthroughUp = false;
                }
                if (targetVal > _brickWallDown)
                {
                    _hasHitBrickWallDown = false;
                    _breakthroughDown = false;
                }

                _lastPushedMasterValue = targetVal;
                _lastMasterValue = targetVal;
                if (_masterValueText != null) _masterValueText.Text = targetVal.ToString();

                var batch = new List<(int SlotIndex, int Brightness, int Contrast, int VirtualBrightness, int VirtualContrast)>(_masterDragBaselines.Count);
                int delta = (int)Math.Round(targetVal - _masterDragStartValue);

                if (targetVal >= _brickWallUp && _breakthroughUp)
                {
                    double t = (_brickWallUp == 100) ? 1.0 : (targetVal - _brickWallUp) / (100.0 - _brickWallUp);
                    t = Math.Clamp(t, 0.0, 1.0);
                    foreach (var s in _masterDragBaselines)
                    {
                        int wallB = s.Brightness + (int)Math.Round(_brickWallUp - _masterDragStartValue);
                        int b = (int)Math.Round(wallB + t * (100 - wallB));
                        b = Math.Clamp(b, 0, 100);
                        int c = s.LockRatio ? b : Math.Clamp(s.Contrast + (b - s.Brightness), 0, 100);
                        int vb = Math.Clamp(s.VirtualBrightness + delta, -100, 200);
                        int vc = s.LockRatio ? vb : Math.Clamp(s.VirtualContrast + delta, -100, 200);
                        batch.Add((s.SlotIndex, b, c, vb, vc));
                    }
                }
                else if (targetVal <= _brickWallDown && _breakthroughDown)
                {
                    double t = (_brickWallDown == 0) ? 0.0 : targetVal / (double)_brickWallDown;
                    t = Math.Clamp(t, 0.0, 1.0);
                    foreach (var s in _masterDragBaselines)
                    {
                        int wallB = s.Brightness + (int)Math.Round(_brickWallDown - _masterDragStartValue);
                        int b = (int)Math.Round(wallB * t);
                        b = Math.Clamp(b, 0, 100);
                        int c = s.LockRatio ? b : Math.Clamp(s.Contrast + (b - s.Brightness), 0, 100);
                        int vb = Math.Clamp(s.VirtualBrightness + delta, -100, 200);
                        int vc = s.LockRatio ? vb : Math.Clamp(s.VirtualContrast + delta, -100, 200);
                        batch.Add((s.SlotIndex, b, c, vb, vc));
                    }
                }
                else
                {
                    foreach (var s in _masterDragBaselines)
                    {
                        int vb = Math.Clamp(s.VirtualBrightness + delta, -100, 200);
                        int vc = s.LockRatio ? vb : Math.Clamp(s.VirtualContrast + delta, -100, 200);
                        int b = Math.Clamp(vb, 0, 100);
                        int c = s.LockRatio ? b : Math.Clamp(vc, 0, 100);
                        batch.Add((s.SlotIndex, b, c, vb, vc));
                    }
                }

                ScreenBrightnessService.SetScreenLevelsBatch(batch);
            };
            Grid.SetColumn(_masterSlider, 3);
            masterGrid.Children.Add(_masterSlider);

            var masterPlusBtn = new Button
            {
                Content = "+",
                Classes = { "Secondary" },
                Width = 32,
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(masterPlusBtn, $"Step all displays up by {stepDelta}% (maintains differential gap)");
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
                    onBrightnessChanged: (slot, val) => { ScreenBrightnessService.ReanchorAllScreens(); UpdateMasterBaselineFromScreens(); },
                    showLockRatioToggle: false);
                _containers[i] = containerView;
            }

            UpdateVisibleScreenContainers(initialStates);

            Grid.SetRow(_containersGrid, 2);
            rootGrid.Children.Add(_containersGrid);

            var welcome = CreateWelcomeCard();
            if (welcome != null)
            {
                Grid.SetRow(welcome, 3);
                rootGrid.Children.Add(welcome);
            }

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
            string configKey = string.Join(",", activeStates.Select(s => $"{s.SlotIndex}:{s.IsConnected}:{s.IsLinkedToNext}"));

            if (configKey != _lastRenderedScreenConfig)
            {
                _lastRenderedScreenConfig = configKey;
                _containersGrid.Children.Clear();

                if (activeCount == 1)
                {
                    _containersGrid.ColumnDefinitions = new ColumnDefinitions("*");
                    int slotIdx = activeStates[0].SlotIndex;
                    var view = _containers[slotIdx];
                    view.RootCard.IsVisible = true;
                    Grid.SetColumn(view.RootCard, 0);
                    _containersGrid.Children.Add(view.RootCard);
                }
                else
                {
                    var colDefs = new List<string>();
                    for (int i = 0; i < activeCount; i++)
                    {
                        colDefs.Add("*");
                        if (i < activeCount - 1)
                        {
                            colDefs.Add("Auto");
                        }
                    }
                    _containersGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", colDefs));

                    int gridCol = 0;
                    for (int i = 0; i < activeCount; i++)
                    {
                        var curState = activeStates[i];
                        int slotIdx = curState.SlotIndex;
                        var view = _containers[slotIdx];
                        view.RootCard.IsVisible = true;
                        Grid.SetColumn(view.RootCard, gridCol++);
                        _containersGrid.Children.Add(view.RootCard);

                        if (i < activeCount - 1)
                        {
                            var nextState = activeStates[i + 1];
                            var linkConnector = CreateInterDisplayLinkConnector(curState, nextState);
                            Grid.SetColumn(linkConnector, gridCol++);
                            _containersGrid.Children.Add(linkConnector);
                        }
                    }
                }
            }

            if (_masterBar != null)
            {
                _masterBar.IsVisible = activeCount > 1;
                if (activeCount > 1 && _masterSlider != null && !_isUserDraggingMaster)
                {
                    int avg = (int)Math.Round(activeStates.Average(s => s.Brightness));
                    _suppressMasterEvents = true;
                    _masterSlider.Value = avg;
                    _lastMasterValue = avg;
                    if (_masterValueText != null) _masterValueText.Text = avg.ToString();
                    _suppressMasterEvents = false;
                }
            }

            var ovCfg = ScreenBrightnessService.LoadConfig();
            if (double.IsNaN(ovCfg.OverlayWindowX) && (ovCfg.OverlayWindowWidth <= 0 || ovCfg.OverlayWindowWidth == 700))
            {
                double targetWidth = activeCount switch
                {
                    1 => 440,
                    2 => 620,
                    _ => 840
                };

                if (Math.Abs(Width - targetWidth) > 1)
                {
                    Width = targetWidth;
                }
            }
        }

        private Control CreateInterDisplayLinkConnector(ScreenLiveState leftState, ScreenLiveState rightState)
        {
            int leftSlot = leftState.SlotIndex;
            bool isLinked = leftState.IsLinkedToNext;

            var container = new Border
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0),
                ZIndex = 10
            };

            var syncBtn = new Button
            {
                Padding = new Thickness(7, 5),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(isLinked ? 1.5 : 1),
                Background = isLinked
                    ? new SolidColorBrush(Color.Parse("#0369A1"))
                    : AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = isLinked
                    ? new SolidColorBrush(Color.Parse("#38BDF8"))
                    : AppBrush("AppBorderBrush", "#475569"),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var arrowText = new TextBlock
            {
                Text = "⇄",
                FontSize = 14,
                FontWeight = FontWeight.ExtraBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isLinked ? Brushes.White : AppBrush("AppTextMutedBrush", "#94A3B8")
            };

            var syncLabel = new TextBlock
            {
                Text = "SYNC",
                FontSize = 8.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isLinked ? Brushes.White : AppBrush("AppTextMutedBrush", "#94A3B8")
            };

            stack.Children.Add(arrowText);
            stack.Children.Add(syncLabel);
            syncBtn.Content = stack;

            string tip = isLinked
                ? $"Displays are synced ({leftState.Nickname} ⇄ {rightState.Nickname})! Adjusting either screen moves both together. Click to unlink."
                : $"Click to sync {leftState.Nickname} and {rightState.Nickname} together.";
            ToolTip.SetTip(syncBtn, tip);

            syncBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                bool newLinked = !leftState.IsLinkedToNext;
                ScreenBrightnessService.SetScreenLinkedToNext(leftSlot, newLinked);
            };

            container.Child = syncBtn;
            return container;
        }

        private void UpdateMasterBaselineFromScreens()
        {
            if (_masterSlider == null || _isUserDraggingMaster) return;
            var states = ScreenBrightnessService.GetImmediateCachedStates();
            var activeStates = states.Where(s => s.IsConnected).OrderBy(s => s.SlotIndex).ToList();
            if (activeStates.Count <= 1) return;

            int avg = (int)Math.Round(activeStates.Average(s => s.Brightness));
            _suppressMasterEvents = true;
            _masterSlider.Value = avg;
            _lastMasterValue = avg;
            if (_masterValueText != null) _masterValueText.Text = avg.ToString();
            _suppressMasterEvents = false;
        }

        /// <summary>Wording for the welcome card, built from the user's actual step / preset shortcuts.</summary>
        internal static string BuildWelcomeText(ScreenBrightnessConfig cfg)
        {
            static string First(string combos) =>
                combos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

            var lines = new List<string>();
            string up = First(cfg.StepUpHotkey ?? string.Empty), down = First(cfg.StepDownHotkey ?? string.Empty);
            if (up.Length > 0 && down.Length > 0) lines.Add($"{up} / {down} = brighter / darker (all screens).");
            else if (up.Length > 0) lines.Add($"{up} = brighter (all screens).");
            else if (down.Length > 0) lines.Add($"{down} = darker (all screens).");
            lines.Add("Tray icon > presets, one click.");
            lines.Add("⚙ > Brightness settings: shortcuts, presets and Focus Mode.");
            return string.Join("\n", lines);
        }

        /// <summary>One-time tips card; closing it sets <see cref="ScreenBrightnessConfig.HasSeenWelcomeCard"/> so it never returns.</summary>
        private Border? CreateWelcomeCard()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            if (cfg.HasSeenWelcomeCard) return null;

            var card = new Border
            {
                Background = AppBrush("AppPanelBrush", "#111827"),
                BorderBrush = AppBrush("AppAccentBrush", "#38BDF8"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 10, 0, 0)
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            grid.Children.Add(new TextBlock
            {
                Text = BuildWelcomeText(cfg),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppBrush("AppTextPrimaryBrush", "#E2E8F0"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var gotIt = new Button
            {
                Content = "Got it",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            gotIt.Click += (_, _) =>
            {
                card.IsVisible = false;
                var latest = ScreenBrightnessService.LoadConfig();
                latest.HasSeenWelcomeCard = true;
                ScreenBrightnessService.SaveConfig(latest);
            };
            Grid.SetColumn(gotIt, 1);
            grid.Children.Add(gotIt);
            card.Child = grid;
            return card;
        }

        /// <summary>Header line appears only for a problem: failed hardware read first, else shortcuts taken by another app.</summary>
        private void UpdateHeaderStatus()
        {
            if (_headerStatusText == null) return;
            string? msg = _hardwareReadFailed
                ? "⚠️ Hardware read failed, using cached settings."
                : ScreenBrightnessService.DescribeTakenBrightnessHotkeys();
            _headerStatusText.Text = msg ?? string.Empty;
            _headerStatusText.TextWrapping = TextWrapping.Wrap;
            _headerStatusText.IsVisible = msg != null;
        }

        /// <summary>Gear-menu toggle: off = one slider per screen (contrast follows brightness), on = separate contrast sliders.</summary>
        private void SetSeparateContrast(bool separate)
        {
            bool lockAll = !separate;
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.GlobalLockBrightnessContrast = lockAll;
            foreach (var s in cfg.Screens)
            {
                s.LockRatio = lockAll;
                if (lockAll) s.LastContrast = s.LastBrightness;
            }
            ScreenBrightnessService.SaveConfig(cfg);

            if (lockAll)
            {
                for (int i = 0; i < _containers.Length; i++)
                {
                    var view = _containers[i];
                    if (view == null) continue;
                    int b = (int)Math.Round(view.BrightnessSlider.Value);
                    ScreenBrightnessService.RequestScreenLevelChange(i, b, b, updateContrastFromLock: true);
                }
            }
            ScreenBrightnessService.NotifyStateChanged();
        }

        private async void OnOverlayOpened(object? sender, EventArgs e)
        {
            _openedUtc = DateTime.UtcNow;
            UpdateFocusModeChip();
            CancellationToken token;
            try
            {
                PositionOrRestoreClamped();
                _liveReadCts?.Cancel();
                _liveReadCts?.Dispose();
                _liveReadCts = new CancellationTokenSource();
                token = _liveReadCts.Token;
            }
            catch (Exception ex) when (!ScreenBrightnessService.IsFatalException(ex))
            {
                Program.LogDebug($"[ScreenBrightnessOverlay] OnOverlayOpened setup failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            try
            {
                var outcome = await ScreenBrightnessService.ReadAllScreensLiveOrCachedAsync(token);
                if (token.IsCancellationRequested || _isClosing) return;
                var liveStates = outcome.States;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_isClosing) return;
                    UpdateVisibleScreenContainers(liveStates);
                    int connectedCount = 0;
                    foreach (var state in liveStates)
                    {
                        if (state.IsConnected) connectedCount++;
                        if (state.SlotIndex < 0 || state.SlotIndex >= _containers.Length || _containers[state.SlotIndex] == null) continue;
                        var view = _containers[state.SlotIndex];
                        ScreenContainerBuilder.ApplyLiveStateToContainer(view, state, respectUserTouch: true);
                    }

                    _hardwareReadFailed = outcome.IsFallback;
                    UpdateHeaderStatus();
                    UpdateFocusModeChip();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (!ScreenBrightnessService.IsFatalException(ex))
            {
                Program.LogDebug($"[ScreenBrightnessOverlay] Live read failed: {ex.GetType().Name}: {ex.Message}");
                if (token.IsCancellationRequested || _isClosing) return;
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_isClosing) return;
                        var cached = ScreenBrightnessService.GetCachedStatesSafe();
                        UpdateVisibleScreenContainers(cached);
                        foreach (var st in cached)
                        {
                            if (st.SlotIndex < 0 || st.SlotIndex >= _containers.Length || _containers[st.SlotIndex] == null) continue;
                            ScreenContainerBuilder.ApplyLiveStateToContainer(_containers[st.SlotIndex], st, respectUserTouch: true);
                        }
                        _hardwareReadFailed = true;
                        UpdateHeaderStatus();
                    });
                }
                catch (Exception inner) when (!ScreenBrightnessService.IsFatalException(inner))
                {
                    Program.LogDebug($"[ScreenBrightnessOverlay] Cached fallback render failed: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        private TextBlock? _focusModeText;
        private StackPanel? _focusModeChip;

        private void UpdateFocusModeChip()
        {
            if (_focusModeChip == null || _focusModeText == null) return;
            var cfg = ScreenBrightnessService.LoadConfig();
            _focusModeChip.IsVisible = cfg.FocusModeActive;
            if (!cfg.FocusModeActive) return;
            var key = HotkeyManager.DescribeComboList(cfg.FocusModeHotkey);
            _focusModeText.Text = "🎯 " + ScreenBrightnessService.FocusModeDisplayName(cfg) + " on"
                + (string.IsNullOrEmpty(key) ? string.Empty : " - " + key + " brings the other screens back");
        }

        private void OnServiceStateChanged()
        {
            void Update()
            {
                if (_isClosing) return;
                if (!ScreenBrightnessService.IsFeatureActive)
                {
                    Close();
                    return;
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
                if (!_isUserDraggingMaster)
                {
                    UpdateMasterBaselineFromScreens();
                }
                UpdateHeaderStatus();
                UpdateFocusModeChip();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Update();
            }
            else
            {
                Dispatcher.UIThread.Post(Update);
            }
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
        public static ScreenContainerView CreateScreenContainer(ScreenLiveState initialState, Action? onNickChanged = null, Action<int, int>? onBrightnessChanged = null, bool showLockRatioToggle = true, Action<int>? onAssignKey = null)
        {
            int slotIndex = initialState.SlotIndex;
            string slotLabel = initialState.DefaultSlotLabel;

            var card = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8),
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

            bool hasCustomNick = !string.IsNullOrWhiteSpace(initialState.Nickname) &&
                                 !string.Equals(initialState.Nickname, slotLabel, StringComparison.OrdinalIgnoreCase);

            var slotBadge = new Border
            {
                Background = initialState.IsConnected
                    ? ScreenBrightnessOverlayWindow.AppBrush("AppAccentBrush", "#0284C7")
                    : ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#475569"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = !hasCustomNick,
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

            string displayNick = hasCustomNick ? initialState.Nickname : slotLabel;
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
            ToolTip.SetTip(lockRatioCheck, "On: one slider moves Brightness and Contrast together. Off: shows a separate Contrast slider.");

            lockRow.Children.Add(lockRatioCheck);

            if (onAssignKey != null)
            {
                var assignKeyBtn = new Button
                {
                    Content = "⌨ Key",
                    Classes = { "Secondary" },
                    Padding = new Thickness(8, 3),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(assignKeyBtn, "Give this screen its own shortcut: one key jumps only this screen to a brightness and contrast you choose.");
                assignKeyBtn.Click += (_, _) => onAssignKey(slotIndex);
                lockRow.Children.Add(assignKeyBtn);
            }

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
                SlidersGrid = slidersGrid,
                BrightnessLabelText = bLabel,
                BrightnessSlider = brightnessSlider,
                ContrastSlider = contrastSlider,
                SlotBadge = slotBadge,
                LockRatioCheckBox = lockRatioCheck,
                StatusBadgeText = statusText,
                SupportsContrast = !initialState.IsConnected || initialState.SupportsContrast,
                ShowLockRatioToggle = showLockRatioToggle,
                IsInternalLcd = initialState.IsInternalLcd,
                CommitPendingRename = CommitInlineRename
            };

            UpdateContrastInteractivity(view);

            brightnessSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => { view.UserTouched = true; view.IsDraggingBrightness = true; }, RoutingStrategies.Tunnel);
            brightnessSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { view.IsDraggingBrightness = false; ScreenBrightnessService.ReanchorAllScreens(); }, RoutingStrategies.Tunnel);
            brightnessSlider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => { view.IsDraggingBrightness = false; }, RoutingStrategies.Tunnel);

            contrastSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => { view.UserTouched = true; view.IsDraggingContrast = true; }, RoutingStrategies.Tunnel);
            contrastSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { view.IsDraggingContrast = false; ScreenBrightnessService.ReanchorAllScreens(); }, RoutingStrategies.Tunnel);
            contrastSlider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => { view.IsDraggingContrast = false; }, RoutingStrategies.Tunnel);

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
                ScreenBrightnessService.RequestIndividualOrLinkedChange(slotIndex, b, c, isBrightnessChanged: true, isContrastChanged: locked);
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
                ScreenBrightnessService.RequestIndividualOrLinkedChange(slotIndex, b, c, isBrightnessChanged: locked, isContrastChanged: true);
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

        /// <summary>
        /// One slider per screen unless the user tunes contrast separately: the contrast column is hidden (and its grid
        /// column collapsed) while Sync ratio is on or the display has no DDC/CI contrast. Unticking Sync ratio shows it.
        /// </summary>
        public static void UpdateContrastInteractivity(ScreenContainerView view)
        {
            bool locked = view.LockRatioCheckBox.IsChecked == true;
            bool supportsContrast = view.SupportsContrast && !view.IsInternalLcd;
            bool showContrast = supportsContrast && !locked;

            view.RightContrastColumn.IsVisible = showContrast;
            view.ContrastSlider.IsEnabled = showContrast;
            view.SlidersGrid.ColumnDefinitions[1].Width = showContrast ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            view.SlidersGrid.ColumnSpacing = showContrast ? 8 : 0;
            view.ContrastLabelText.Text = "Contrast";
            view.LockRatioCheckBox.IsEnabled = supportsContrast;
            view.LockRatioCheckBox.IsVisible = supportsContrast && view.ShowLockRatioToggle;

            if (!supportsContrast)
            {
                view.BrightnessLabelText.Text = "Brightness";
            }
            else if (locked)
            {
                view.BrightnessLabelText.Text = "Brightness + Contrast";
                ToolTip.SetTip(view.BrightnessSlider, "Contrast follows Brightness. Untick 'Sync ratio' to set Contrast separately.");
            }
            else
            {
                view.BrightnessLabelText.Text = "Brightness";
                ToolTip.SetTip(view.BrightnessSlider, null);
                ToolTip.SetTip(view.RightContrastColumn, "Adjust hardware DDC/CI contrast independently (0% to 100%).");
            }
        }

        public static void ApplyLiveStateToContainer(ScreenContainerView view, ScreenLiveState state, bool respectUserTouch)
        {
            view.ModelSubtitle.Text = FormatModelSubtitle(state);
            view.StatusBadgeText.Text = state.StatusMessage;
            view.SupportsContrast = !state.IsConnected || state.SupportsContrast;
            view.IsInternalLcd = state.IsInternalLcd;

            if (!view.InlineRenameBox.IsVisible)
            {
                bool hasCustom = !string.IsNullOrWhiteSpace(state.Nickname) &&
                                 !string.Equals(state.Nickname, state.DefaultSlotLabel, StringComparison.OrdinalIgnoreCase);
                view.SlotBadge.IsVisible = !hasCustom;
                view.TitleText.Text = hasCustom ? state.Nickname : state.DefaultSlotLabel;
            }

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
            view.SuppressSliderEvents = true;
            try
            {
                if (!view.InlineRenameBox.IsVisible)
                {
                    bool hasCustom = !string.IsNullOrWhiteSpace(state.Nickname) &&
                                     !string.Equals(state.Nickname, state.DefaultSlotLabel, StringComparison.OrdinalIgnoreCase);
                    view.SlotBadge.IsVisible = !hasCustom;
                    view.TitleText.Text = hasCustom ? state.Nickname : state.DefaultSlotLabel;
                }
                view.LockRatioCheckBox.IsChecked = state.LockRatio;
                if (!view.IsDraggingBrightness)
                {
                    view.BrightnessSlider.Value = state.Brightness;
                    view.BrightnessValueText.Text = state.Brightness.ToString();
                }
                if (!view.IsDraggingContrast)
                {
                    int c = state.LockRatio ? state.Brightness : state.Contrast;
                    view.ContrastSlider.Value = c;
                    view.ContrastValueText.Text = c.ToString();
                }
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
