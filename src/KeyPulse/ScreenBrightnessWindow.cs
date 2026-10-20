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
        private CheckBox _lockDifferentialCheck = null!;
        private bool _suppressDifferentialCheckEvent;
        private Border? _masterBar;
        private Slider? _masterSlider;
        private Button? _masterMinusBtn;
        private Button? _masterPlusBtn;
        private TextBlock? _masterValueText;
        private int _lastMasterValue = 50;
        private bool _suppressMasterEvents;
        private bool _isUserDraggingMaster;
        private string _lastRenderedScreenConfig = string.Empty;
        private StackPanel? _directHotkeysStack;
        private CancellationTokenSource? _liveReadCts;

        public ScreenBrightnessWindow()
        {
            Title = "KeyPulse — Screen Brightness & Shortcut Cycling";
            MinWidth = 780;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = ScreenBrightnessOverlayWindow.AppBrush("AppSurfaceBrush", "#0B0F19");
            Focusable = true;

            ApplySavedWindowGeometry();
            BuildUi();

            PointerPressed += OnWindowPointerPressed;
            KeyDown += OnWindowKeyDown;
            Opened += OnWindowOpened;
            Closing += (_, _) =>
            {
                SaveWindowGeometry();
                ScreenBrightnessService.FlushConfig();
            };
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
                    ? appCfg.ScreenBrightnessWindowWidth : 920;
                Height = (!double.IsNaN(appCfg.ScreenBrightnessWindowHeight) && appCfg.ScreenBrightnessWindowHeight >= MinHeight)
                    ? appCfg.ScreenBrightnessWindowHeight : 750;

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
                Width = 920;
                Height = 750;
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
            ToolTip.SetTip(badge, tooltipText);
            Avalonia.Automation.AutomationProperties.SetName(badge, "Help: " + tooltipText);
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
                ColumnSpacing = 16,
                Margin = new Thickness(0, 0, 0, 14)
            };

            var titleStack = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Screen Brightness & Contrast Control",
                FontSize = 18.5,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Hardware DDC/CI & Laptop Backlight • Anti-Cheat Safe • 250ms Speed Limiter",
                FontSize = 11.5,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var topButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var identifyBtn = new Button
            {
                Content = "Identify Screens",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 6),
                FontSize = 12,
                TabIndex = 1
            };
            ToolTip.SetTip(identifyBtn, "Flash Screen A, Screen B, Screen C banners on each physical monitor so you know which card controls which display.");
            identifyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.ShowIdentifyBanners();
            };
            topButtons.Children.Add(identifyBtn);

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
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center
            };

            _globalLockCheck = new CheckBox
            {
                Content = "Sync Brightness & Contrast (1:1 ratio)",
                IsChecked = cfg.GlobalLockBrightnessContrast,
                FontWeight = FontWeight.SemiBold,
                FontSize = 12.5,
                TabIndex = 4
            };
            ToolTip.SetTip(_globalLockCheck, "When enabled, moving a Brightness slider automatically sets Contrast to the same percentage on all displays. Uncheck to adjust Contrast independently.");
            _globalLockCheck.IsCheckedChanged += OnGlobalLockChanged;
            globalLockRow.Children.Add(_globalLockCheck);

            _lockDifferentialCheck = new CheckBox
            {
                Content = "Lock relative brightness differential",
                IsChecked = cfg.LockMonitorDifferential,
                FontWeight = FontWeight.SemiBold,
                FontSize = 12.5,
                TabIndex = 5
            };
            ToolTip.SetTip(_lockDifferentialCheck, "When enabled, stepping or moving the All Displays slider adjusts all monitors together while preserving their relative difference (e.g. Screen A stays 15% brighter than Screen B).");
            _lockDifferentialCheck.IsCheckedChanged += (s, e) =>
            {
                if (_suppressDifferentialCheckEvent) return;
                var currentCfg = ScreenBrightnessService.LoadConfig();
                currentCfg.LockMonitorDifferential = _lockDifferentialCheck.IsChecked == true;
                ScreenBrightnessService.SaveConfig(currentCfg);
                SetStatus(currentCfg.LockMonitorDifferential
                    ? "Locked relative monitor brightness differential."
                    : "Unlocked relative monitor brightness differential (step hotkeys adjust mouse-focused display only).", true);
            };
            globalLockRow.Children.Add(_lockDifferentialCheck);
            globalLockRow.Children.Add(CreateHelpBadge("Lock relative differential: preserves the offset between monitors when stepping or moving all displays together (compressing naturally at 0% or 100%).\nSync Brightness & Contrast: keeps Contrast matched to Brightness at 1:1."));

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
                Content = "Open Brightness/Contrast Slider",
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
                Padding = new Thickness(14, 10),
                Margin = new Thickness(0, 0, 0, 2),
                IsVisible = false
            };

            var masterGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
                ColumnSpacing = 10
            };

            var masterBadge = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0284C7")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "SYNCED MASTER",
                    FontSize = 10,
                    FontWeight = FontWeight.ExtraBold,
                    Foreground = Brushes.White
                }
            };
            Grid.SetColumn(masterBadge, 0);
            masterGrid.Children.Add(masterBadge);

            var masterLabel = new TextBlock
            {
                Text = "⚡ ALL DISPLAYS (SYNCED):",
                FontSize = 12.5,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppAccentBrush", "#38BDF8"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(masterLabel, 1);
            masterGrid.Children.Add(masterLabel);

            var initialDelta = ScreenBrightnessService.LoadConfig().StepDelta;
            if (initialDelta <= 0) initialDelta = 5;

            _masterMinusBtn = new Button
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
            ToolTip.SetTip(_masterMinusBtn, $"Step all displays down by {initialDelta}% (maintains differential gap)");
            _masterMinusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                var curDelta = ScreenBrightnessService.LoadConfig().StepDelta;
                if (curDelta <= 0) curDelta = 5;
                ScreenBrightnessService.RequestMasterLevelChange(-curDelta);
            };
            Grid.SetColumn(_masterMinusBtn, 2);
            masterGrid.Children.Add(_masterMinusBtn);

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
            _masterSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => { _isUserDraggingMaster = true; }, RoutingStrategies.Tunnel);
            _masterSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { _isUserDraggingMaster = false; ScreenBrightnessService.ReanchorAllScreens(); UpdateMasterBaselineFromScreens(); }, RoutingStrategies.Tunnel);
            _masterSlider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => { _isUserDraggingMaster = false; UpdateMasterBaselineFromScreens(); }, RoutingStrategies.Tunnel);
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

            _masterPlusBtn = new Button
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
            ToolTip.SetTip(_masterPlusBtn, $"Step all displays up by {initialDelta}% (maintains differential gap)");
            _masterPlusBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                var curDelta = ScreenBrightnessService.LoadConfig().StepDelta;
                if (curDelta <= 0) curDelta = 5;
                ScreenBrightnessService.RequestMasterLevelChange(curDelta);
            };
            Grid.SetColumn(_masterPlusBtn, 4);
            masterGrid.Children.Add(_masterPlusBtn);

            _masterValueText = new TextBlock
            {
                Text = "50",
                FontSize = 12.5,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 28,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(_masterValueText, 5);
            masterGrid.Children.Add(_masterValueText);

            _masterBar.Child = masterGrid;
            contentStack.Children.Add(_masterBar);

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
                    },
                    onBrightnessChanged: (slot, val) => { ScreenBrightnessService.ReanchorAllScreens(); UpdateMasterBaselineFromScreens(); });
                _containers[i] = view;
            }

            UpdateVisibleScreenContainers(initialStates);
            contentStack.Children.Add(_containersGrid);

            var directHotkeysBorder = CreateDirectHotkeysPanel(cfg);
            contentStack.Children.Add(directHotkeysBorder);

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

            var addPresetBtn = new Button
            {
                Content = "+ Add New Preset",
                Classes = { "Primary" },
                Padding = new Thickness(12, 6),
                FontSize = 12,
                TabIndex = 7
            };
            ToolTip.SetTip(addPresetBtn, "Create a new brightness preset with custom display levels and optional shortcut key.");
            addPresetBtn.Click += OnAddNewPreset;
            addPresetButtons.Children.Add(addPresetBtn);

            Grid.SetColumn(addPresetButtons, 1);
            presetsHeader.Children.Add(addPresetButtons);
            presetsStack.Children.Add(presetsHeader);

            var cycleHotkeysBorder = CreateCycleHotkeysPanel(cfg);
            presetsStack.Children.Add(cycleHotkeysBorder);

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
            RenderDirectHotkeysList();
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
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppSurfaceBrush", "#101827")
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

            _suppressDifferentialCheckEvent = true;
            _lockDifferentialCheck.IsChecked = cfg.LockMonitorDifferential;
            _suppressDifferentialCheckEvent = false;

            RenderProfilesList();
            RenderDirectHotkeysList();
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

        private Border CreateCycleHotkeysPanel(ScreenBrightnessConfig cfg)
        {
            var border = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10),
                Margin = new Thickness(0, 4, 0, 8)
            };

            var root = new StackPanel { Spacing = 6 };

            var header = new TextBlock
            {
                Text = "Brightness Shortcut Keys",
                FontWeight = FontWeight.Bold,
                FontSize = 12.5,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            };
            root.Children.Add(header);

            var controlsRow = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var upStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 6)
            };
            upStack.Children.Add(new TextBlock
            {
                Text = "Cycle Forward:",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var upBox = new TextBox
            {
                Text = cfg.CycleUpHotkey,
                PlaceholderText = "e.g. Ctrl+Alt+Add",
                Width = 170,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var downBox = new TextBox
            {
                Text = cfg.CycleDownHotkey,
                PlaceholderText = "e.g. Ctrl+Alt+Subtract",
                Width = 170,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var stepUpBox = new TextBox
            {
                Text = cfg.StepUpHotkey,
                PlaceholderText = "e.g. Ctrl+Alt+Up",
                Width = 150,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var stepDownBox = new TextBox
            {
                Text = cfg.StepDownHotkey,
                PlaceholderText = "e.g. Ctrl+Alt+Down",
                Width = 150,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            void ClearConflicting(string hotkey, TextBox caller)
            {
                if (string.IsNullOrWhiteSpace(hotkey)) return;
                if (caller != upBox && string.Equals(cfg.CycleUpHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.CycleUpHotkey = string.Empty;
                    upBox.Text = string.Empty;
                }
                if (caller != downBox && string.Equals(cfg.CycleDownHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.CycleDownHotkey = string.Empty;
                    downBox.Text = string.Empty;
                }
                if (caller != stepUpBox && string.Equals(cfg.StepUpHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.StepUpHotkey = string.Empty;
                    stepUpBox.Text = string.Empty;
                }
                if (caller != stepDownBox && string.Equals(cfg.StepDownHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.StepDownHotkey = string.Empty;
                    stepDownBox.Text = string.Empty;
                }
            }

            AttachHotkeyRecorder(upBox, combo =>
            {
                ClearConflicting(combo, upBox);
                cfg.CycleUpHotkey = combo;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus(string.IsNullOrWhiteSpace(combo) ? "Cleared Cycle Forward hotkey." : $"Set Cycle Forward hotkey to {combo}.", true);
            });
            var upClearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(upClearBtn, "Clear Cycle Forward hotkey");
            upClearBtn.Click += (_, _) =>
            {
                upBox.Text = string.Empty;
                cfg.CycleUpHotkey = string.Empty;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus("Cleared Cycle Forward hotkey.", true);
            };
            upStack.Children.Add(upBox);
            upStack.Children.Add(upClearBtn);
            controlsRow.Children.Add(upStack);

            var downStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 6)
            };
            downStack.Children.Add(new TextBlock
            {
                Text = "Cycle Backward:",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            });
            AttachHotkeyRecorder(downBox, combo =>
            {
                ClearConflicting(combo, downBox);
                cfg.CycleDownHotkey = combo;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus(string.IsNullOrWhiteSpace(combo) ? "Cleared Cycle Backward hotkey." : $"Set Cycle Backward hotkey to {combo}.", true);
            });
            var downClearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(downClearBtn, "Clear Cycle Backward hotkey");
            downClearBtn.Click += (_, _) =>
            {
                downBox.Text = string.Empty;
                cfg.CycleDownHotkey = string.Empty;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus("Cleared Cycle Backward hotkey.", true);
            };
            downStack.Children.Add(downBox);
            downStack.Children.Add(downClearBtn);
            controlsRow.Children.Add(downStack);

            int currentDelta = cfg.StepDelta > 0 ? cfg.StepDelta : 5;

            var stepDeltaStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 6)
            };
            stepDeltaStack.Children.Add(new TextBlock
            {
                Text = "Step Delta:",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var stepDeltaCombo = new ComboBox
            {
                ItemsSource = new[] { "1%", "2%", "5% (Default)", "10%" },
                Width = 125,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            stepDeltaCombo.SelectedIndex = currentDelta switch
            {
                1 => 0,
                2 => 1,
                10 => 3,
                _ => 2
            };
            ToolTip.SetTip(stepDeltaCombo, "Amount to increment or decrement brightness with step hotkeys and synced master buttons.");
            stepDeltaStack.Children.Add(stepDeltaCombo);
            controlsRow.Children.Add(stepDeltaStack);

            var stepUpStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 6)
            };
            var stepUpLabel = new TextBlock
            {
                Text = $"Step Up (+{currentDelta}%):",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            };
            stepUpStack.Children.Add(stepUpLabel);
            AttachHotkeyRecorder(stepUpBox, combo =>
            {
                ClearConflicting(combo, stepUpBox);
                cfg.StepUpHotkey = combo;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus(string.IsNullOrWhiteSpace(combo) ? "Cleared Step Up hotkey." : $"Set Step Up hotkey to {combo}.", true);
            });
            var stepUpClearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(stepUpClearBtn, "Clear Step Up hotkey");
            stepUpClearBtn.Click += (_, _) =>
            {
                stepUpBox.Text = string.Empty;
                cfg.StepUpHotkey = string.Empty;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus("Cleared Step Up hotkey.", true);
            };
            stepUpStack.Children.Add(stepUpBox);
            stepUpStack.Children.Add(stepUpClearBtn);
            controlsRow.Children.Add(stepUpStack);

            var stepDownStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 6)
            };
            var stepDownLabel = new TextBlock
            {
                Text = $"Step Down (-{currentDelta}%):",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            };
            stepDownStack.Children.Add(stepDownLabel);
            AttachHotkeyRecorder(stepDownBox, combo =>
            {
                ClearConflicting(combo, stepDownBox);
                cfg.StepDownHotkey = combo;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus(string.IsNullOrWhiteSpace(combo) ? "Cleared Step Down hotkey." : $"Set Step Down hotkey to {combo}.", true);
            });
            var stepDownClearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(stepDownClearBtn, "Clear Step Down hotkey");
            stepDownClearBtn.Click += (_, _) =>
            {
                stepDownBox.Text = string.Empty;
                cfg.StepDownHotkey = string.Empty;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus("Cleared Step Down hotkey.", true);
            };
            stepDownStack.Children.Add(stepDownBox);
            stepDownStack.Children.Add(stepDownClearBtn);
            controlsRow.Children.Add(stepDownStack);

            root.Children.Add(controlsRow);

            var hint = new TextBlock
            {
                Text = $"Cycle hotkeys step through presets (up to 10 presets) in lowest-first order. Step hotkeys bump brightness by ±{currentDelta}%. Individual presets can also have their own dedicated shortcut below.",
                FontSize = 11,
                Classes = { "Muted" },
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(hint);

            stepDeltaCombo.SelectionChanged += (_, _) =>
            {
                int newDelta = stepDeltaCombo.SelectedIndex switch
                {
                    0 => 1,
                    1 => 2,
                    3 => 10,
                    _ => 5
                };
                cfg.StepDelta = newDelta;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                stepUpLabel.Text = $"Step Up (+{newDelta}%):";
                stepDownLabel.Text = $"Step Down (-{newDelta}%):";
                if (_masterMinusBtn != null) _masterMinusBtn.Content = $"−{newDelta}%";
                if (_masterPlusBtn != null) _masterPlusBtn.Content = $"+{newDelta}%";
                hint.Text = $"Cycle hotkeys step through presets (up to 10 presets) in lowest-first order. Step hotkeys bump brightness by ±{newDelta}%. Individual presets can also have their own dedicated shortcut below.";
                SetStatus($"Step delta set to ±{newDelta}%.", true);
            };

            border.Child = root;
            return border;
        }

        private Border CreateDirectHotkeysPanel(ScreenBrightnessConfig cfg)
        {
            var border = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelBrush", "#111827"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#1F2937"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14),
                Margin = new Thickness(0, 4, 0, 0)
            };

            var stack = new StackPanel { Spacing = 10 };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            var titleStack = new StackPanel { Spacing = 2 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Instant Screen Level Hotkeys",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Assign direct shortcuts to set a specific display (or all displays) directly to preset brightness & contrast values (e.g. Ctrl+Alt+1 -> Screen A to 70%).",
                FontSize = 11.5,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var addBtn = new Button
            {
                Content = "+ Add Instant Hotkey",
                Classes = { "Primary" },
                Padding = new Thickness(12, 6),
                FontSize = 12
            };
            ToolTip.SetTip(addBtn, "Add a new direct shortcut key for a specific display or all displays.");
            addBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                var currentCfg = ScreenBrightnessService.LoadConfig();
                currentCfg.DirectHotkeys.Add(new ScreenDirectHotkey
                {
                    Name = $"Instant {currentCfg.DirectHotkeys.Count + 1}",
                    TargetSlotIndex = -1,
                    Brightness = 65,
                    Contrast = 65,
                    LockRatio = true,
                    Hotkey = string.Empty
                });
                ScreenBrightnessService.SaveConfig(currentCfg);
                RenderDirectHotkeysList();
                SetStatus("Added new instant screen level hotkey. Press keys in the Shortcut box to record your combo.", true);
            };
            Grid.SetColumn(addBtn, 1);
            headerGrid.Children.Add(addBtn);
            stack.Children.Add(headerGrid);

            _directHotkeysStack = new StackPanel { Spacing = 8 };
            stack.Children.Add(_directHotkeysStack);

            border.Child = stack;
            return border;
        }

        private void RenderDirectHotkeysList()
        {
            if (_directHotkeysStack == null) return;
            _directHotkeysStack.Children.Clear();

            var cfg = ScreenBrightnessService.LoadConfig();
            if (cfg.DirectHotkeys == null || cfg.DirectHotkeys.Count == 0)
            {
                var emptyBlock = new TextBlock
                {
                    Text = "No instant hotkeys configured. Click '+ Add Instant Hotkey' to create dedicated shortcuts for individual displays.",
                    FontSize = 11.5,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#64748B"),
                    Margin = new Thickness(4, 4, 0, 4)
                };
                _directHotkeysStack.Children.Add(emptyBlock);
                return;
            }

            for (int idx = 0; idx < cfg.DirectHotkeys.Count; idx++)
            {
                var dh = cfg.DirectHotkeys[idx];
                _directHotkeysStack.Children.Add(CreateDirectHotkeyRow(dh, cfg, idx));
            }
        }

        private Border CreateDirectHotkeyRow(ScreenDirectHotkey dh, ScreenBrightnessConfig cfg, int index)
        {
            var card = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
                ColumnSpacing = 14,
                VerticalAlignment = VerticalAlignment.Center
            };

            var targetStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            targetStack.Children.Add(new TextBlock
            {
                Text = "Target Screen:",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
            });

            var screenNames = new List<string> { "All Displays" };
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                var sCfg = cfg.Screens.FirstOrDefault(s => s.SlotIndex == i);
                string slotTitle = ScreenBrightnessService.DefaultSlotName(i);
                if (sCfg != null && !string.IsNullOrWhiteSpace(sCfg.CustomNickname) &&
                    !string.Equals(sCfg.CustomNickname, slotTitle, StringComparison.OrdinalIgnoreCase))
                {
                    slotTitle = sCfg.CustomNickname;
                }
                screenNames.Add(slotTitle);
            }

            var targetCombo = new ComboBox
            {
                ItemsSource = screenNames.ToArray(),
                SelectedIndex = dh.TargetSlotIndex < 0 ? 0 : Math.Clamp(dh.TargetSlotIndex + 1, 0, screenNames.Count - 1),
                Width = 140,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(targetCombo, "Select which display this hotkey controls (or All Displays).");
            targetCombo.SelectionChanged += (_, _) =>
            {
                dh.TargetSlotIndex = targetCombo.SelectedIndex == 0 ? -1 : targetCombo.SelectedIndex - 1;
                ScreenBrightnessService.SaveConfig(cfg);
            };
            targetStack.Children.Add(targetCombo);
            Grid.SetColumn(targetStack, 0);
            grid.Children.Add(targetStack);

            var bStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            bStack.Children.Add(new TextBlock
            {
                Text = "Brightness:",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
            });

            var bRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            var bSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = dh.Brightness,
                Width = 100,
                VerticalAlignment = VerticalAlignment.Center
            };
            var bVal = new TextBlock
            {
                Text = $"{dh.Brightness}%",
                FontSize = 11.5,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                MinWidth = 36,
                VerticalAlignment = VerticalAlignment.Center
            };
            bRow.Children.Add(bSlider);
            bRow.Children.Add(bVal);
            bStack.Children.Add(bRow);
            Grid.SetColumn(bStack, 1);
            grid.Children.Add(bStack);

            var cStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            var lockChk = new CheckBox
            {
                Content = "Sync Contrast",
                IsChecked = dh.LockRatio,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var cRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            var cSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = dh.Contrast,
                Width = 80,
                IsEnabled = !dh.LockRatio,
                Opacity = dh.LockRatio ? 0.35 : 1.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            var cVal = new TextBlock
            {
                Text = $"{dh.Contrast}%",
                FontSize = 11.5,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                MinWidth = 36,
                Opacity = dh.LockRatio ? 0.35 : 1.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            bSlider.PropertyChanged += (_, pe) =>
            {
                if (pe.Property != RangeBase.ValueProperty) return;
                int val = (int)Math.Round(bSlider.Value);
                dh.Brightness = val;
                bVal.Text = $"{val}%";
                if (dh.LockRatio)
                {
                    dh.Contrast = val;
                    cSlider.Value = val;
                    cVal.Text = $"{val}%";
                }
                ScreenBrightnessService.SaveConfig(cfg);
            };
            cSlider.PropertyChanged += (_, pe) =>
            {
                if (pe.Property != RangeBase.ValueProperty || dh.LockRatio) return;
                int val = (int)Math.Round(cSlider.Value);
                dh.Contrast = val;
                cVal.Text = $"{val}%";
                ScreenBrightnessService.SaveConfig(cfg);
            };
            lockChk.IsCheckedChanged += (_, _) =>
            {
                dh.LockRatio = lockChk.IsChecked == true;
                cSlider.IsEnabled = !dh.LockRatio;
                cSlider.Opacity = dh.LockRatio ? 0.35 : 1.0;
                cVal.Opacity = dh.LockRatio ? 0.35 : 1.0;
                if (dh.LockRatio)
                {
                    dh.Contrast = dh.Brightness;
                    cSlider.Value = dh.Brightness;
                    cVal.Text = $"{dh.Brightness}%";
                }
                ScreenBrightnessService.SaveConfig(cfg);
            };
            cStack.Children.Add(lockChk);
            cRow.Children.Add(cSlider);
            cRow.Children.Add(cVal);
            cStack.Children.Add(cRow);
            Grid.SetColumn(cStack, 2);
            grid.Children.Add(cStack);

            var hkStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            hkStack.Children.Add(new TextBlock
            {
                Text = "Shortcut Key:",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
            });
            var hkRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            var hkBox = new TextBox
            {
                Text = dh.Hotkey,
                PlaceholderText = "e.g. Ctrl+Alt+1",
                Width = 140,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            AttachHotkeyRecorder(hkBox, combo =>
            {
                dh.Hotkey = combo;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus(string.IsNullOrWhiteSpace(combo)
                    ? "Cleared shortcut for instant preset."
                    : $"Assigned {combo} to instant preset.", true);
            });
            var hkClearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 4),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(hkClearBtn, "Clear shortcut key");
            hkClearBtn.Click += (_, _) =>
            {
                hkBox.Text = string.Empty;
                dh.Hotkey = string.Empty;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus("Cleared instant shortcut.", true);
            };
            hkRow.Children.Add(hkBox);
            hkRow.Children.Add(hkClearBtn);
            hkStack.Children.Add(hkRow);
            Grid.SetColumn(hkStack, 3);
            grid.Children.Add(hkStack);

            var applyBtn = new Button
            {
                Content = "Apply",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(applyBtn, "Apply these brightness & contrast levels right now.");
            applyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.TriggerDirectHotkey(dh);
                SetStatus($"Applied instant preset ({dh.Brightness}%).", true);
            };
            Grid.SetColumn(applyBtn, 4);
            grid.Children.Add(applyBtn);

            var delBtn = new Button
            {
                Content = "🗑",
                Classes = { "Danger" },
                Padding = new Thickness(8, 5),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(delBtn, "Delete this instant hotkey.");
            delBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                cfg.DirectHotkeys.Remove(dh);
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                RenderDirectHotkeysList();
                SetStatus("Deleted instant hotkey.", true);
            };
            Grid.SetColumn(delBtn, 5);
            grid.Children.Add(delBtn);

            card.Child = grid;
            return card;
        }

        public static void AttachHotkeyRecorder(TextBox box, Action<string> onUpdated)
        {
            Action<int, bool, bool, bool, bool> rawHandler = (vk, ctrl, alt, shift, win) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!box.IsFocused) return;
                    if (vk == 0x1B || vk == 0x08 || vk == 0x2E)
                    {
                        box.Text = string.Empty;
                        onUpdated(string.Empty);
                        return;
                    }
                    if (vk == 0x09) return;

                    var parts = new List<string>();
                    if (ctrl) parts.Add("Ctrl");
                    if (alt) parts.Add("Alt");
                    if (shift) parts.Add("Shift");
                    if (win) parts.Add("Win");

                    bool isModifierOnly = (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C ||
                                           vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5);

                    if (!isModifierOnly)
                    {
                        string keyName = HotkeyManager.VirtualKeyToName((uint)vk);
                        if (!string.IsNullOrWhiteSpace(keyName))
                        {
                            parts.Add(keyName);
                            string combo = string.Join("+", parts);
                            box.Text = combo;
                            onUpdated(combo);
                        }
                    }
                    else if (parts.Count > 0)
                    {
                        box.Text = string.Join("+", parts) + "+";
                    }
                });
            };

            box.GotFocus += (_, _) =>
            {
                HotkeyManager.OnRawKey += rawHandler;
                HotkeyManager.EnableCaptureHook();
            };

            box.LostFocus += (_, _) =>
            {
                HotkeyManager.OnRawKey -= rawHandler;
                HotkeyManager.DisableCaptureHook();
            };

            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Tab)
                {
                    e.Handled = false;
                    return;
                }

                if (e.Key == Key.Escape || e.Key == Key.Delete || e.Key == Key.Back)
                {
                    box.Text = string.Empty;
                    onUpdated(string.Empty);
                    e.Handled = true;
                    return;
                }

                e.Handled = true;
            };
        }

        private void OnAddNewPreset(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            var cfg = ScreenBrightnessService.LoadConfig();
            int pinnedCount = cfg.Profiles.Count(p => p.ShowInTrayQuickActions);

            var targets = new List<ScreenCycleEntry>();
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                targets.Add(new ScreenCycleEntry
                {
                    SlotIndex = i,
                    Brightness = 50,
                    Contrast = 50,
                    Enabled = true
                });
            }

            int count = cfg.Profiles.Count + 1;
            string baseName = $"Preset {count}";
            string candidate = baseName;
            int suffix = 2;
            while (cfg.Profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = $"Preset {count} ({suffix++})";
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
            ScreenBrightnessService.RegisterBrightnessHotkeys();
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
                Padding = new Thickness(14, 12)
            };

            var rowStack = new StackPanel { Spacing = 9 };

            var cardHeaderGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            var identityStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameBox = new TextBox
            {
                Text = profile.Name,
                MaxLength = 32,
                PlaceholderText = "Preset name (max 32 chars)",
                VerticalAlignment = VerticalAlignment.Center,
                Width = 180
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
            identityStack.Children.Add(nameBox);

            var badgeText = profile.IncludeInCycle
                ? (cycleStepIndex == 1 ? $"CYCLE STEP 1/{totalCycleSteps} (LOWEST • FIRST)" : $"CYCLE STEP {cycleStepIndex}/{totalCycleSteps}")
                : "NOT IN CYCLE";

            var stepBadge = new Border
            {
                Background = profile.IncludeInCycle
                    ? ScreenBrightnessOverlayWindow.AppBrush("AppAccentBrush", "#0369A1")
                    : ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#334155"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                }
            };
            identityStack.Children.Add(stepBadge);

            if (isActive)
            {
                var activeBadge = new Border
                {
                    Background = ScreenBrightnessOverlayWindow.AppBrush("AppSuccessBrush", "#10B981"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "ACTIVE",
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White
                    }
                };
                identityStack.Children.Add(activeBadge);
            }

            Grid.SetColumn(identityStack, 0);
            cardHeaderGrid.Children.Add(identityStack);

            var actionBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
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
                    var t = profile.Targets.FirstOrDefault(x => x.SlotIndex == i);
                    if (t == null)
                    {
                        t = new ScreenCycleEntry { SlotIndex = i, Enabled = true };
                        profile.Targets.Add(t);
                    }
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

            Grid.SetColumn(actionBtns, 1);
            cardHeaderGrid.Children.Add(actionBtns);
            rowStack.Children.Add(cardHeaderGrid);

            var togglesRow = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };

            var cycleGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
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
            togglesRow.Children.Add(cycleGroup);

            var trayGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
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
            togglesRow.Children.Add(trayGroup);

            var hotkeyGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
            hotkeyGroup.Children.Add(new TextBlock
            {
                Text = "Shortcut:",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var hotkeyBox = new TextBox
            {
                Text = profile.Hotkey,
                PlaceholderText = "e.g. Ctrl+Alt+Divide",
                Width = 150,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            AttachHotkeyRecorder(hotkeyBox, combo =>
            {
                profile.Hotkey = combo;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus(string.IsNullOrWhiteSpace(combo) ? $"Cleared shortcut for \"{profile.Name}\"." : $"Assigned {combo} to \"{profile.Name}\".", true);
            });
            var hotkeyClearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(5, 3),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(hotkeyClearBtn, "Clear shortcut for this preset");
            hotkeyClearBtn.Click += (_, _) =>
            {
                hotkeyBox.Text = string.Empty;
                profile.Hotkey = string.Empty;
                ScreenBrightnessService.SaveConfig(cfg);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                SetStatus($"Cleared shortcut for \"{profile.Name}\".", true);
            };
            hotkeyGroup.Children.Add(hotkeyBox);
            hotkeyGroup.Children.Add(hotkeyClearBtn);
            togglesRow.Children.Add(hotkeyGroup);

            rowStack.Children.Add(togglesRow);

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
                var target = profile.Targets.FirstOrDefault(t => t.SlotIndex == slotIdx);
                if (target == null)
                {
                    target = new ScreenCycleEntry { SlotIndex = slotIdx, Brightness = 50, Contrast = 50, Enabled = true };
                    profile.Targets.Add(target);
                }
                string slotTitle = ScreenBrightnessService.DefaultSlotName(slotIdx);
                if (!string.IsNullOrWhiteSpace(screenCfg.CustomNickname) &&
                    !string.Equals(screenCfg.CustomNickname, slotTitle, StringComparison.OrdinalIgnoreCase))
                {
                    slotTitle = screenCfg.CustomNickname;
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

                var enableChk = new CheckBox
                {
                    Content = screenCfg.LockRatio ? $"{slotTitle}:" : $"{slotTitle} (Bri):",
                    IsChecked = target.Enabled,
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(enableChk, $"Check to adjust {slotTitle} with this preset. Uncheck to leave {slotTitle} completely untouched when activating this preset.");

                var bSlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = target.Brightness,
                    Width = 105,
                    IsEnabled = target.Enabled,
                    Opacity = target.Enabled ? 1.0 : 0.35,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(bSlider, screenCfg.LockRatio
                    ? $"Target Brightness & Contrast for {slotTitle} when this preset is applied."
                    : $"Target Brightness for {slotTitle} when this preset is applied.");

                var bValText = new TextBlock
                {
                    Text = target.Enabled ? $"{target.Brightness}%" : "Untouched",
                    FontSize = 11.5,
                    FontWeight = FontWeight.Bold,
                    MinWidth = 36,
                    Opacity = target.Enabled ? 1.0 : 0.35,
                    Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                Slider? cSlider = null;
                TextBlock? cValText = null;

                enableChk.IsCheckedChanged += (_, _) =>
                {
                    target.Enabled = enableChk.IsChecked == true;
                    bSlider.IsEnabled = target.Enabled;
                    bSlider.Opacity = target.Enabled ? 1.0 : 0.35;
                    bValText.Opacity = target.Enabled ? 1.0 : 0.35;
                    bValText.Text = target.Enabled ? $"{target.Brightness}%" : "Untouched";
                    if (cSlider != null && cValText != null)
                    {
                        cSlider.IsEnabled = target.Enabled;
                        cSlider.Opacity = target.Enabled ? 1.0 : 0.35;
                        cValText.Opacity = target.Enabled ? 1.0 : 0.35;
                        cValText.Text = target.Enabled ? $"{target.Contrast}%" : "Untouched";
                    }
                    ScreenBrightnessService.SaveConfig(cfg);
                    ScreenBrightnessService.NotifyStateChanged();
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

                bRow.Children.Add(enableChk);
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

                    cSlider = new Slider
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = target.Contrast,
                        Width = 105,
                        IsEnabled = target.Enabled,
                        Opacity = target.Enabled ? 1.0 : 0.35,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ToolTip.SetTip(cSlider, $"Target Contrast for {slotTitle} when this preset is applied (independent because Sync ratio is off).");

                    cValText = new TextBlock
                    {
                        Text = target.Enabled ? $"{target.Contrast}%" : "Untouched",
                        FontSize = 11.5,
                        FontWeight = FontWeight.Bold,
                        MinWidth = 36,
                        Opacity = target.Enabled ? 1.0 : 0.35,
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
                    _masterSlider!.Value = avg;
                    _lastMasterValue = avg;
                    if (_masterValueText != null) _masterValueText.Text = avg.ToString();
                    _suppressMasterEvents = false;
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
                    : ScreenBrightnessOverlayWindow.AppBrush("AppPanelRaisedBrush", "#1E293B"),
                BorderBrush = isLinked
                    ? new SolidColorBrush(Color.Parse("#38BDF8"))
                    : ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#475569"),
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
                Foreground = isLinked ? Brushes.White : ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
            };

            var syncLabel = new TextBlock
            {
                Text = "SYNC",
                FontSize = 8.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isLinked ? Brushes.White : ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8")
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
            _masterSlider!.Value = avg;
            _lastMasterValue = avg;
            if (_masterValueText != null) _masterValueText.Text = avg.ToString();
            _suppressMasterEvents = false;
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
                    RenderDirectHotkeysList();
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

                if (_lockDifferentialCheck != null && _lockDifferentialCheck.IsChecked != cfg.LockMonitorDifferential)
                {
                    _suppressDifferentialCheckEvent = true;
                    _lockDifferentialCheck.IsChecked = cfg.LockMonitorDifferential;
                    _suppressDifferentialCheckEvent = false;
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
                RenderProfilesList();
                RenderDirectHotkeysList();
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
            ScreenBrightnessService.FlushConfig();
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
