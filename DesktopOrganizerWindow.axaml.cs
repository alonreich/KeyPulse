using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace KeyPulse
{
    public partial class DesktopOrganizerWindow : Window
    {
        private static DesktopOrganizerWindow? _instance;
        private DesktopOrganizerConfig _config = new();
        private bool _suppressConfigEvents;

        public DesktopOrganizerWindow()
        {
            InitializeComponent();
            _config = DesktopOrganizerService.LoadConfig();

            _suppressConfigEvents = true;
            try
            {
                if (this.FindControl<CheckBox>("AutoRestoreToggle") is CheckBox autoRestore)
                {
                    autoRestore.IsChecked = _config.AutoRestoreOnDisplayChange;
                }
                if (this.FindControl<ComboBox>("NewIconBehaviorCombo") is ComboBox behaviorCombo)
                {
                    behaviorCombo.SelectedIndex = Math.Clamp((int)_config.NewIconBehavior, 0, 2);
                }
            }
            finally
            {
                _suppressConfigEvents = false;
            }

            RefreshDisplayState();
            RenderProfiles();

            Closed += (s, e) => _instance = null;
        }

        public static void OpenOrganizer(Window? owner = null)
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

            _instance = new DesktopOrganizerWindow();
            if (owner != null && owner.IsVisible)
            {
                _instance.Show(owner);
            }
            else
            {
                _instance.Show();
            }
        }

        private void RefreshDisplayState()
        {
            var monitors = DesktopOrganizerService.GetMonitors();
            var autoArrangeOn = DesktopOrganizerService.IsAutoArrangeActive(out var desktopVisible);

            if (this.FindControl<Border>("AutoArrangeWarning") is Border warningBanner)
            {
                warningBanner.IsVisible = autoArrangeOn;
            }

            if (this.FindControl<TextBlock>("MonitorStatusText") is TextBlock statusTxt)
            {
                string monitorSummary;
                if (monitors.Count == 1)
                {
                    monitorSummary = $"1 Monitor ({monitors[0].MonitorRect.Width}x{monitors[0].MonitorRect.Height})";
                }
                else
                {
                    var resList = string.Join(", ", monitors.Select(m => $"{m.MonitorRect.Width}x{m.MonitorRect.Height}"));
                    monitorSummary = $"{monitors.Count} Monitors Connected ({resList})";
                }

                int iconCount = 0;
                IntPtr hwnd = DesktopOrganizerService.GetDesktopListView();
                if (hwnd != IntPtr.Zero)
                {
                    if (SendMessageTimeout(hwnd, 0x1004 /* LVM_GETITEMCOUNT */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out IntPtr result) != IntPtr.Zero)
                    {
                        iconCount = (int)result;
                    }
                }

                statusTxt.Text = $"{monitorSummary} • {iconCount} Desktop Icon{(iconCount == 1 ? "" : "s")}";
            }

            if (this.FindControl<Button>("UndoBtn") is Button undoBtn)
            {
                undoBtn.IsEnabled = DesktopOrganizerService.HasUndo;
                if (DesktopOrganizerService.HasUndo)
                {
                    undoBtn.Content = "Undo Last Restore";
                }
            }
        }

        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private void RenderProfiles()
        {
            if (this.FindControl<StackPanel>("ProfilesStackPanel") is not StackPanel stack) return;
            if (this.FindControl<StackPanel>("EmptyStatePanel") is not StackPanel emptyPanel) return;

            stack.Children.Clear();

            if (_config.Profiles.Count == 0)
            {
                emptyPanel.IsVisible = true;
                return;
            }

            emptyPanel.IsVisible = false;

            foreach (var profile in _config.Profiles.OrderByDescending(p => p.Id == _config.ActiveProfileId))
            {
                var card = CreateProfileCard(profile);
                stack.Children.Add(card);
            }
        }

        private Border CreateProfileCard(DesktopLayoutProfile profile)
        {
            var isActive = profile.Id == _config.ActiveProfileId;

            var card = new Border
            {
                Classes = { "Panel" },
                Background = AppBrush(isActive ? "AppSelectedBrush" : "AppPanelRaisedBrush"),
                BorderBrush = AppBrush(isActive ? "AppAccentBrush" : "AppBorderBrush"),
                BorderThickness = new Thickness(isActive ? 1.5 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            // Left side details
            var leftStack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var titleTxt = new TextBlock
            {
                Text = profile.Name,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Foreground = AppBrush("AppTextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(titleTxt);

            if (isActive)
            {
                var activeBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#166534")),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "ACTIVE",
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#bbf7d0"))
                    }
                };
                titleRow.Children.Add(activeBadge);
            }

            leftStack.Children.Add(titleRow);

            var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            metaRow.Children.Add(CreateBadge($"{profile.DisplayCount} Screen{(profile.DisplayCount == 1 ? "" : "s")}"));
            metaRow.Children.Add(CreateBadge($"{profile.IconCount} Icons"));
            metaRow.Children.Add(new TextBlock
            {
                Text = $"Saved: {profile.CreatedAt}",
                Classes = { "Muted" },
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            leftStack.Children.Add(metaRow);

            Grid.SetColumn(leftStack, 0);
            grid.Children.Add(leftStack);

            // Right side buttons
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            var restoreBtn = new Button
            {
                Content = "Restore Layout",
                Classes = { "Primary" },
                MinWidth = 110,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            restoreBtn.Click += (s, e) =>
            {
                Program.PlaySound("click");
                var ok = DesktopOrganizerService.RestoreLayout(profile, _config.NewIconBehavior, out var report);
                _config.ActiveProfileId = profile.Id;
                DesktopOrganizerService.SaveConfig(_config);

                if (ok)
                {
                    Program.PlaySound("click");
                    SetStatus("✓ " + report, true);
                }
                else
                {
                    Program.PlaySound("error");
                    SetStatus("⚠️ " + report, false);
                }

                RefreshDisplayState();
                RenderProfiles();
            };
            buttons.Children.Add(restoreBtn);

            var updateBtn = new Button
            {
                Content = "Update",
                Classes = { "Secondary" },
                MinWidth = 75
            };
            ToolTip.SetTip(updateBtn, "Overwrite this profile with your current desktop icon positions.");
            updateBtn.Click += (s, e) =>
            {
                Program.PlaySound("click");
                var updated = DesktopOrganizerService.CaptureCurrentLayout(profile.Name, out var err);
                if (updated != null)
                {
                    profile.Icons = updated.Icons;
                    profile.Monitors = updated.Monitors;
                    profile.DisplayCount = updated.DisplayCount;
                    profile.IconSpacingX = updated.IconSpacingX;
                    profile.IconSpacingY = updated.IconSpacingY;
                    profile.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    _config.ActiveProfileId = profile.Id;
                    DesktopOrganizerService.SaveConfig(_config);

                    SetStatus($"Updated \"{profile.Name}\" with {profile.IconCount} current icons.", true);
                    RenderProfiles();
                }
                else
                {
                    SetStatus("Update failed: " + err, false);
                }
            };
            buttons.Children.Add(updateBtn);

            var deleteBtn = new Button
            {
                Content = "Delete",
                Classes = { "Danger" },
                MinWidth = 70
            };
            ToolTip.SetTip(deleteBtn, "Remove this saved layout profile.");
            deleteBtn.Click += (s, e) =>
            {
                Program.PlaySound("close");
                _config.Profiles.Remove(profile);
                if (_config.ActiveProfileId == profile.Id)
                {
                    _config.ActiveProfileId = _config.Profiles.FirstOrDefault()?.Id ?? string.Empty;
                }
                DesktopOrganizerService.SaveConfig(_config);
                SetStatus($"Deleted profile \"{profile.Name}\".", true);
                RenderProfiles();
            };
            buttons.Children.Add(deleteBtn);

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            card.Child = grid;
            return card;
        }

        private Border CreateBadge(string text)
        {
            return new Border
            {
                Background = AppBrush("AppBorderBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = FontWeight.Medium,
                    Foreground = AppBrush("AppTextPrimaryBrush")
                }
            };
        }

        private void SaveLayout_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            var nameBox = this.FindControl<TextBox>("NewProfileNameBox");
            var name = nameBox?.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                var monitors = DesktopOrganizerService.GetMonitors();
                var count = monitors.Count;
                name = count > 1
                    ? $"Dual-Screen Setup ({DateTime.Now:MMM d, HH:mm})"
                    : $"Desktop Layout ({DateTime.Now:MMM d, HH:mm})";
            }

            var profile = DesktopOrganizerService.CaptureCurrentLayout(name, out var error);
            if (profile == null)
            {
                Program.PlaySound("error");
                SetStatus("Save failed: " + error, false);
                return;
            }

            _config.Profiles.Add(profile);
            _config.ActiveProfileId = profile.Id;
            DesktopOrganizerService.SaveConfig(_config);

            if (nameBox != null) nameBox.Text = string.Empty;

            Program.PlaySound("click");
            SetStatus($"✓ Saved \"{profile.Name}\" with {profile.IconCount} icons across {profile.DisplayCount} display{(profile.DisplayCount == 1 ? "" : "s")}.", true);

            RefreshDisplayState();
            RenderProfiles();
        }

        private void Undo_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            if (DesktopOrganizerService.UndoLastRestore(out var report))
            {
                Program.PlaySound("click");
                SetStatus("✓ Reverted: " + report, true);
            }
            else
            {
                Program.PlaySound("error");
                SetStatus("⚠️ " + report, false);
            }

            RefreshDisplayState();
            RenderProfiles();
        }

        private void Refresh_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("click");
            RefreshDisplayState();
            RenderProfiles();
            SetStatus("Displays and icons refreshed.", true);
        }

        private void AutoRestoreToggle_Changed(object? sender, RoutedEventArgs e)
        {
            if (_suppressConfigEvents) return;
            if (sender is CheckBox chk)
            {
                _config.AutoRestoreOnDisplayChange = chk.IsChecked == true;
                DesktopOrganizerService.SaveConfig(_config);
                SetStatus(_config.AutoRestoreOnDisplayChange
                    ? "Auto-restore enabled. KeyPulse will automatically snap icons when displays change."
                    : "Auto-restore disabled.", true);
            }
        }

        private void NewIconBehaviorCombo_Changed(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressConfigEvents) return;
            if (sender is ComboBox cb && cb.SelectedIndex >= 0)
            {
                _config.NewIconBehavior = (UnsavedIconBehavior)cb.SelectedIndex;
                DesktopOrganizerService.SaveConfig(_config);
            }
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetStatus(string message, bool isSuccess)
        {
            if (this.FindControl<TextBlock>("StatusNotificationText") is TextBlock status)
            {
                status.Text = message;
                status.Foreground = AppBrush(isSuccess ? "AppSuccessSoftBrush" : "AppWarningBrush");
            }
        }

        private static IBrush AppBrush(string resourceKey)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var res) && res is IBrush b)
                {
                    return b;
                }
                if (app != null && app.TryGetResource("AppTextPrimaryBrush", app.ActualThemeVariant, out var fallback) && fallback is IBrush fb)
                {
                    return fb;
                }
            }
            catch { }
            return Brushes.Gray;
        }
    }
}
