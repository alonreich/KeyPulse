using System;
using System.Linq;
using System.Threading.Tasks;
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
        private DesktopOrganizerLoadResult _loadResult = new();
        private bool _suppressConfigEvents = true;
        private bool _isBusy;

        public DesktopOrganizerWindow()
        {
            _suppressConfigEvents = true;
            try
            {
                InitializeComponent();
                _loadResult = DesktopOrganizerService.LoadConfigDetailed();
                _config = _loadResult.Config;

                if (this.FindControl<CheckBox>("AutoRestoreToggle") is CheckBox autoRestore)
                {
                    autoRestore.IsChecked = _config.AutoRestoreOnDisplayChange;
                    if (_loadResult.IsReadOnly) autoRestore.IsEnabled = false;
                }
                if (this.FindControl<CheckBox>("AutoSnapshotToggle") is CheckBox autoSnap)
                {
                    autoSnap.IsChecked = _config.AutoPeriodicSnapshotEnabled;
                    if (_loadResult.IsReadOnly) autoSnap.IsEnabled = false;
                }
                if (this.FindControl<ComboBox>("NewIconBehaviorCombo") is ComboBox behaviorCombo)
                {
                    behaviorCombo.SelectedIndex = Math.Clamp((int)_config.NewIconBehavior, 0, 2);
                    if (_loadResult.IsReadOnly) behaviorCombo.IsEnabled = false;
                }
            }
            finally
            {
                _suppressConfigEvents = false;
            }

            if (this.FindControl<Button>("SaveLayoutBtn") is Button saveBtn && _loadResult.IsReadOnly)
            {
                saveBtn.IsEnabled = false;
            }

            DesktopOrganizerService.EnsureAutoSnapshotScheduler();
            RefreshDisplayState();
            RenderProfiles();

            if (_loadResult.IsReadOnly)
            {
                SetStatus("Saved layouts could not be loaded. KeyPulse has protected the existing file from being overwritten.", false);
            }
            else if (_loadResult.Status == DesktopOrganizerLoadStatus.Recovered)
            {
                SetStatus("Saved layouts were recovered from a previous verified copy.", true);
            }

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
                undoBtn.IsEnabled = DesktopOrganizerService.HasUndo && !_loadResult.IsReadOnly && !_isBusy;
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
            var currentMonitors = DesktopOrganizerService.GetMonitors();
            var autoProfiles = _config.Profiles.Where(p => p.IsAutoSnapshot).ToList();

            foreach (var profile in _config.Profiles
                .OrderByDescending(p => p.Id == _config.ActiveProfileId)
                .ThenBy(p => p.IsAutoSnapshot)
                .ThenByDescending(p => p.CreatedAt))
            {
                int autoRank = profile.IsAutoSnapshot ? autoProfiles.IndexOf(profile) + 1 : 0;
                var card = CreateProfileCard(profile, currentMonitors.Count, autoRank, autoProfiles.Count);
                stack.Children.Add(card);
            }
        }

        private Border CreateProfileCard(DesktopLayoutProfile profile, int currentMonitorCount, int autoIndex, int totalAutoCount)
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
            var renameBox = new TextBox
            {
                Text = profile.Name,
                MinWidth = 220,
                FontSize = 13,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };

            void CommitRename()
            {
                if (!renameBox.IsVisible) return;
                var newName = (renameBox.Text ?? string.Empty).Trim();
                renameBox.IsVisible = false;
                titleTxt.IsVisible = true;
                if (!string.IsNullOrWhiteSpace(newName) && newName != profile.Name && !_loadResult.IsReadOnly)
                {
                    profile.Name = newName;
                    titleTxt.Text = newName;
                    DesktopOrganizerService.SaveConfig(_config, out _);
                    SetStatus($"Renamed layout to \"{newName}\".", true);
                }
            }

            renameBox.KeyDown += (s, ke) =>
            {
                if (ke.Key == Avalonia.Input.Key.Enter)
                {
                    ke.Handled = true;
                    CommitRename();
                }
                else if (ke.Key == Avalonia.Input.Key.Escape)
                {
                    ke.Handled = true;
                    renameBox.Text = profile.Name;
                    renameBox.IsVisible = false;
                    titleTxt.IsVisible = true;
                }
            };
            renameBox.LostFocus += (s, le) => CommitRename();

            titleRow.Children.Add(titleTxt);
            titleRow.Children.Add(renameBox);

            var typeBadge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(profile.IsAutoSnapshot ? "#1e3a8a" : "#065f46")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = profile.IsAutoSnapshot
                        ? $"AUTO-SNAPSHOT ({Math.Max(1, autoIndex)}/{Math.Max(1, totalAutoCount)} • MAX {DesktopOrganizerService.MaxAutoSnapshots})"
                        : "MANUAL (Protected)",
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(profile.IsAutoSnapshot ? "#bfdbfe" : "#a7f3d0"))
                }
            };
            ToolTip.SetTip(typeBadge, profile.IsAutoSnapshot
                ? "Automatically captured periodic snapshot. KeyPulse keeps the latest 5 auto-snapshots and removes the oldest automatically."
                : "Manually saved by you. Never auto-deleted unless you click Delete.");
            titleRow.Children.Add(typeBadge);

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

            if (profile.DisplayCount != currentMonitorCount)
            {
                metaRow.Children.Add(CreateBadge($"Saved for {profile.DisplayCount} Screen{(profile.DisplayCount == 1 ? "" : "s")} ({currentMonitorCount} connected)"));
            }
            else
            {
                metaRow.Children.Add(CreateBadge($"{profile.DisplayCount} Screen{(profile.DisplayCount == 1 ? "" : "s")}"));
            }

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
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                IsEnabled = !_isBusy
            };
            restoreBtn.Click += async (s, e) =>
            {
                if (_isBusy) return;
                _isBusy = true;
                restoreBtn.IsEnabled = false;
                Program.PlaySound("click");
                SetStatus("Restoring desktop layout...", true);

                bool ok = false;
                string report = string.Empty;

                await Task.Run(() =>
                {
                    ok = DesktopOrganizerService.RestoreLayout(profile, _config.NewIconBehavior, out report);
                    if (ok && !_loadResult.IsReadOnly)
                    {
                        _config.ActiveProfileId = profile.Id;
                        DesktopOrganizerService.SaveConfig(_config, out _);
                    }
                });

                _isBusy = false;
                restoreBtn.IsEnabled = true;

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

            var renameBtn = new Button
            {
                Content = "Rename",
                Classes = { "Secondary" },
                MinWidth = 72,
                IsEnabled = !_loadResult.IsReadOnly && !_isBusy
            };
            ToolTip.SetTip(renameBtn, "Rename this saved layout profile.");
            renameBtn.Click += (s, e) =>
            {
                if (_loadResult.IsReadOnly || _isBusy) return;
                if (renameBox.IsVisible)
                {
                    CommitRename();
                }
                else
                {
                    renameBox.Text = profile.Name;
                    titleTxt.IsVisible = false;
                    renameBox.IsVisible = true;
                    renameBox.Focus();
                    renameBox.SelectAll();
                }
            };
            buttons.Children.Add(renameBtn);

            var updateBtn = new Button
            {
                Content = "Update",
                Classes = { "Secondary" },
                MinWidth = 75,
                IsEnabled = !_loadResult.IsReadOnly && !_isBusy
            };
            ToolTip.SetTip(updateBtn, "Overwrite this profile with your current desktop icon positions.");
            updateBtn.Click += async (s, e) =>
            {
                if (_isBusy || _loadResult.IsReadOnly) return;
                _isBusy = true;
                updateBtn.IsEnabled = false;
                Program.PlaySound("click");
                SetStatus("Updating layout profile...", true);

                bool saved = false;
                string err = string.Empty;

                await Task.Run(() =>
                {
                    var updated = DesktopOrganizerService.CaptureCurrentLayout(profile.Name, out err);
                    if (updated != null)
                    {
                        profile.Icons = updated.Icons;
                        profile.Monitors = updated.Monitors;
                        profile.DisplayCount = updated.DisplayCount;
                        profile.IconSpacingX = updated.IconSpacingX;
                        profile.IconSpacingY = updated.IconSpacingY;
                        profile.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        _config.ActiveProfileId = profile.Id;
                        saved = DesktopOrganizerService.SaveConfig(_config, out err);
                    }
                });

                _isBusy = false;
                updateBtn.IsEnabled = true;

                if (saved)
                {
                    SetStatus($"Updated \"{profile.Name}\" with {profile.IconCount} current icons.", true);
                    RenderProfiles();
                }
                else
                {
                    SetStatus("Could not save this layout. Your previously saved layouts were not changed.", false);
                }
            };
            buttons.Children.Add(updateBtn);

            var deleteBtn = new Button
            {
                Content = "Delete",
                Classes = { "Danger" },
                MinWidth = 70,
                IsEnabled = !_loadResult.IsReadOnly && !_isBusy
            };
            ToolTip.SetTip(deleteBtn, "Remove this saved layout profile.");
            deleteBtn.Click += (s, e) =>
            {
                if (_loadResult.IsReadOnly) return;
                Program.PlaySound("close");
                _config.Profiles.Remove(profile);
                if (_config.ActiveProfileId == profile.Id)
                {
                    _config.ActiveProfileId = _config.Profiles.FirstOrDefault()?.Id ?? string.Empty;
                }
                DesktopOrganizerService.SaveConfig(_config, out _, allowEmptyProfiles: true);
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

        private async void SaveLayout_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy || _loadResult.IsReadOnly) return;

            var saveBtn = this.FindControl<Button>("SaveLayoutBtn");
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

            _isBusy = true;
            if (saveBtn != null)
            {
                saveBtn.IsEnabled = false;
                saveBtn.Content = "Saving...";
            }
            SetStatus("Saving desktop layout...", true);
            Program.PlaySound("click");

            DesktopLayoutProfile? profile = null;
            bool saved = false;
            string error = string.Empty;

            await Task.Run(() =>
            {
                profile = DesktopOrganizerService.CaptureCurrentLayout(name, out error);
                if (profile != null)
                {
                    _config.Profiles.Add(profile);
                    _config.ActiveProfileId = profile.Id;
                    saved = DesktopOrganizerService.SaveConfig(_config, out error);
                    if (!saved)
                    {
                        _config.Profiles.Remove(profile);
                    }
                }
            });

            _isBusy = false;
            if (saveBtn != null)
            {
                saveBtn.IsEnabled = !_loadResult.IsReadOnly;
                saveBtn.Content = "Save Current Layout";
            }

            if (saved && profile != null)
            {
                if (nameBox != null) nameBox.Text = string.Empty;
                Program.PlaySound("click");
                SetStatus("Desktop layout saved successfully.", true);
                RefreshDisplayState();
                RenderProfiles();
            }
            else
            {
                Program.PlaySound("error");
                SetStatus("Could not save this layout. Your previously saved layouts were not changed.", false);
            }
        }

        private async void Undo_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy || _loadResult.IsReadOnly) return;
            _isBusy = true;
            Program.PlaySound("click");
            SetStatus("Reverting to previous layout snapshot...", true);

            bool ok = false;
            string report = string.Empty;

            await Task.Run(() =>
            {
                ok = DesktopOrganizerService.UndoLastRestore(out report);
            });

            _isBusy = false;
            if (ok)
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
            if (_suppressConfigEvents || _loadResult.IsReadOnly) return;
            if (sender is CheckBox chk)
            {
                _config.AutoRestoreOnDisplayChange = chk.IsChecked == true;
                DesktopOrganizerService.SaveConfig(_config, out _);
                SetStatus(_config.AutoRestoreOnDisplayChange
                    ? "Auto-restore enabled. KeyPulse will automatically snap icons when displays change."
                    : "Auto-restore disabled.", true);
            }
        }

        private async void AutoSnapshotToggle_Changed(object? sender, RoutedEventArgs e)
        {
            if (_suppressConfigEvents || _loadResult.IsReadOnly) return;
            if (sender is CheckBox chk)
            {
                _config.AutoPeriodicSnapshotEnabled = chk.IsChecked == true;
                DesktopOrganizerService.SaveConfig(_config, out _);
                if (_config.AutoPeriodicSnapshotEnabled)
                {
                    DesktopOrganizerService.EnsureAutoSnapshotScheduler();
                    await Task.Run(() => DesktopOrganizerService.TryTakePeriodicAutoSnapshot(forceImmediateIfNone: true, out _));
                    _loadResult = DesktopOrganizerService.LoadConfigDetailed();
                    _config = _loadResult.Config;
                    RenderProfiles();
                    SetStatus("Periodic Auto-Snapshot enabled (keeps latest 5 auto-snapshots; manual profiles are never auto-deleted).", true);
                }
                else
                {
                    SetStatus("Periodic Auto-Snapshot disabled.", true);
                }
            }
        }

        private void NewIconBehaviorCombo_Changed(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressConfigEvents || _loadResult.IsReadOnly) return;
            if (sender is ComboBox cb && cb.SelectedIndex >= 0)
            {
                _config.NewIconBehavior = (UnsavedIconBehavior)cb.SelectedIndex;
                DesktopOrganizerService.SaveConfig(_config, out _);
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
