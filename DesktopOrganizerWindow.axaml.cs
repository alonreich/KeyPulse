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
                ApplySavedWindowGeometry();

                _loadResult = DesktopOrganizerService.LoadConfigDetailed();
                _config = _loadResult.Config;
                SyncBehavioralControlsFromConfig();
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

            KeyDown += OnWindowKeyDown;
            DesktopOrganizerService.ProfilesChanged += OnExternalProfilesChanged;
            Closing += (s, e) => SaveWindowGeometry();
            Closed += (s, e) =>
            {
                DesktopOrganizerService.ProfilesChanged -= OnExternalProfilesChanged;
                _instance = null;
            };
        }

        private void OnWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        }

        private void OnExternalProfilesChanged()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _loadResult = DesktopOrganizerService.LoadConfigDetailed();
                _config = _loadResult.Config;
                SyncBehavioralControlsFromConfig();
                RefreshDisplayState();
                RenderProfiles();
            });
        }

        private void ApplySavedWindowGeometry()
        {
            try
            {
                var appCfg = ConfigStore.Load(out _, out _);
                var w = (!double.IsNaN(appCfg.DesktopOrganizerWindowWidth) && appCfg.DesktopOrganizerWindowWidth >= MinWidth)
                    ? appCfg.DesktopOrganizerWindowWidth : 820;
                var h = (!double.IsNaN(appCfg.DesktopOrganizerWindowHeight) && appCfg.DesktopOrganizerWindowHeight >= MinHeight)
                    ? appCfg.DesktopOrganizerWindowHeight : 640;
                Width = w;
                Height = h;

                if (!double.IsNaN(appCfg.DesktopOrganizerWindowX) && !double.IsNaN(appCfg.DesktopOrganizerWindowY))
                {
                    var x = (int)appCfg.DesktopOrganizerWindowX;
                    var y = (int)appCfg.DesktopOrganizerWindowY;
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
            catch { }
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
                    cfg.DesktopOrganizerWindowX = x;
                    cfg.DesktopOrganizerWindowY = y;
                    cfg.DesktopOrganizerWindowWidth = w;
                    cfg.DesktopOrganizerWindowHeight = h;
                }, out _);
            }
            catch { }
        }

        private void SyncBehavioralControlsFromConfig()
        {
            _suppressConfigEvents = true;
            try
            {
                var hasProfiles = _config.Profiles.Count > 0;
                if (this.FindControl<CheckBox>("AutoRestoreToggle") is CheckBox autoRestore)
                {
                    autoRestore.IsChecked = _config.AutoRestoreOnDisplayChange;
                    autoRestore.IsEnabled = !_loadResult.IsReadOnly && hasProfiles;
                }
                if (this.FindControl<TextBlock>("AutoRestoreHintText") is TextBlock autoHint)
                {
                    if (!hasProfiles)
                    {
                        autoHint.Text = "Save at least one desktop layout above to enable auto-restore on display changes.";
                        autoHint.Foreground = AppBrush("AppWarningBrush");
                    }
                    else
                    {
                        autoHint.Text = "Listens for monitor disconnects, reconnects, or sleep wake-ups and snaps icons back into place.";
                        autoHint.Foreground = AppBrush("AppTextMutedBrush");
                    }
                }
                if (this.FindControl<CheckBox>("AutoSnapshotToggle") is CheckBox autoSnap)
                {
                    autoSnap.IsChecked = _config.AutoPeriodicSnapshotEnabled;
                    autoSnap.IsEnabled = !_loadResult.IsReadOnly;
                }
                var intervalDays = Math.Clamp(_config.AutoSnapshotIntervalDays <= 0 ? 3 : _config.AutoSnapshotIntervalDays, 1, 30);
                _config.AutoSnapshotIntervalDays = intervalDays;
                if (this.FindControl<TextBox>("AutoSnapshotDaysBox") is TextBox daysBox)
                {
                    daysBox.Text = intervalDays.ToString();
                }
                UpdateIntervalStepperState();

                if (this.FindControl<ComboBox>("NewIconBehaviorCombo") is ComboBox behaviorCombo)
                {
                    behaviorCombo.SelectedIndex = Math.Clamp((int)_config.NewIconBehavior, 0, 3);
                    behaviorCombo.IsEnabled = !_loadResult.IsReadOnly;
                }
                if (this.FindControl<Button>("RestoreDefaultsBtn") is Button resetBtn)
                {
                    resetBtn.IsEnabled = !_loadResult.IsReadOnly && !_isBusy;
                }
            }
            finally
            {
                _suppressConfigEvents = false;
            }
        }

        private void UpdateIntervalStepperState()
        {
            bool enabled = !_loadResult.IsReadOnly && _config.AutoPeriodicSnapshotEnabled;
            int days = Math.Clamp(_config.AutoSnapshotIntervalDays <= 0 ? 3 : _config.AutoSnapshotIntervalDays, 1, 30);

            if (this.FindControl<StackPanel>("AutoSnapshotIntervalPanel") is StackPanel panel)
            {
                panel.Opacity = enabled ? 1.0 : 0.48;
            }
            if (this.FindControl<TextBox>("AutoSnapshotDaysBox") is TextBox box)
            {
                box.IsEnabled = enabled;
            }
            if (this.FindControl<Button>("AutoSnapshotMinusBtn") is Button minusBtn)
            {
                minusBtn.IsEnabled = enabled && days > 1;
            }
            if (this.FindControl<Button>("AutoSnapshotPlusBtn") is Button plusBtn)
            {
                plusBtn.IsEnabled = enabled && days < 30;
            }
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
            SyncBehavioralControlsFromConfig();

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

            var leftStack = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                ClipToBounds = true
            };

            var titleRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var titleTxt = new TextBlock
            {
                Text = profile.Name,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Foreground = AppBrush("AppTextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 4)
            };
            titleRow.Children.Add(titleTxt);

            var typeBadge = new Border
            {
                Background = AppBrush("AppPanelBrush"),
                BorderBrush = AppBrush(profile.IsAutoSnapshot ? "AppAccentBrush" : "AppSuccessBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 4),
                Child = new TextBlock
                {
                    Text = profile.IsAutoSnapshot
                        ? $"AUTO-SNAPSHOT ({Math.Max(1, autoIndex)}/{Math.Max(1, totalAutoCount)} • MAX {DesktopOrganizerService.MaxAutoSnapshots})"
                        : "MANUAL (Protected)",
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = AppBrush(profile.IsAutoSnapshot ? "AppAccentBrush" : "AppSuccessBrush")
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
                    Background = AppBrush("AppSuccessBrush"),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 4),
                    Child = new TextBlock
                    {
                        Text = "ACTIVE",
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White
                    }
                };
                titleRow.Children.Add(activeBadge);
            }

            var renameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            };

            var renameBox = new TextBox
            {
                Text = profile.Name,
                MaxLength = 32,
                Width = 200,
                MaxWidth = 240,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(6, 2),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var saveRenameBtn = new Button
            {
                Content = "Save",
                Classes = { "Primary" },
                Padding = new Thickness(8, 3),
                FontSize = 11.5,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var cancelRenameBtn = new Button
            {
                Content = "Cancel",
                Classes = { "Secondary" },
                Padding = new Thickness(8, 3),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };

            renameRow.Children.Add(renameBox);
            renameRow.Children.Add(saveRenameBtn);
            renameRow.Children.Add(cancelRenameBtn);

            void BeginRename()
            {
                renameBox.Text = profile.Name;
                titleRow.IsVisible = false;
                renameRow.IsVisible = true;
                renameBox.Focus();
                renameBox.SelectAll();
            }

            void CancelRename()
            {
                renameRow.IsVisible = false;
                titleRow.IsVisible = true;
            }

            void CommitRename()
            {
                if (!renameRow.IsVisible) return;
                var newName = (renameBox.Text ?? string.Empty).Trim();
                if (newName.Length > 32) newName = newName.Substring(0, 32).Trim();
                renameRow.IsVisible = false;
                titleRow.IsVisible = true;
                if (string.IsNullOrWhiteSpace(newName))
                {
                    SetStatus("⚠️ Layout name cannot be blank.", false);
                    return;
                }
                if (_config.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    SetStatus($"⚠️ Another layout is already named \"{newName}\".", false);
                    return;
                }
                if (newName != profile.Name && !_loadResult.IsReadOnly)
                {
                    profile.Name = newName;
                    titleTxt.Text = newName;
                    DesktopOrganizerService.SaveConfig(_config, out _);
                    SetStatus($"Renamed layout to \"{newName}\".", true);
                }
            }

            saveRenameBtn.Click += (s, e) =>
            {
                Program.PlaySound("click");
                CommitRename();
            };

            cancelRenameBtn.Click += (s, e) =>
            {
                Program.PlaySound("click");
                CancelRename();
            };

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
                    CancelRename();
                }
            };
            renameBox.LostFocus += (s, le) => CommitRename();

            leftStack.Children.Add(titleRow);
            leftStack.Children.Add(renameRow);

            var metaRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

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
            ToolTip.SetTip(renameBtn, "Rename this saved layout profile (max 32 characters).");
            renameBtn.Click += (s, e) =>
            {
                if (_loadResult.IsReadOnly || _isBusy) return;
                if (renameRow.IsVisible)
                {
                    CommitRename();
                }
                else
                {
                    BeginRename();
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
            deleteBtn.Click += async (s, e) =>
            {
                if (_loadResult.IsReadOnly || _isBusy) return;
                Program.PlaySound("click");
                bool confirmed = await ConfirmActionAsync(
                    "Delete Saved Layout",
                    $"Are you sure you want to delete the layout \"{profile.Name}\" ({profile.IconCount} icons)?\n\nThis cannot be undone.",
                    "Delete Layout");
                if (!confirmed) return;

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
                Icon = Icon
            };

            var root = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            root.Children.Add(new TextBlock
            {
                Text = title,
                Classes = { "SectionTitle" },
                FontSize = 16
            });
            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = AppBrush("AppTextPrimaryBrush")
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
                if (ke.Key == Avalonia.Input.Key.Escape)
                {
                    ke.Handled = true;
                    tcs.TrySetResult(false);
                    dlg.Close();
                }
                else if (ke.Key == Avalonia.Input.Key.Enter)
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
            if (name.Length > 32)
            {
                name = name.Substring(0, 32).Trim();
            }

            if (!string.IsNullOrWhiteSpace(name) &&
                _config.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Program.PlaySound("error");
                SetStatus($"⚠️ A saved layout named \"{name}\" already exists. Choose a unique name or click Update on that layout.", false);
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                var monitors = DesktopOrganizerService.GetMonitors();
                var count = monitors.Count;
                var baseName = count > 1
                    ? $"Dual-Screen ({DateTime.Now:MMM d, HH:mm})"
                    : $"Layout ({DateTime.Now:MMM d, HH:mm})";
                name = baseName.Length > 32 ? baseName.Substring(0, 32) : baseName;
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

        private async void RestoreDefaults_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy || _loadResult.IsReadOnly) return;
            Program.PlaySound("click");

            bool confirmed = await ConfirmActionAsync(
                "Restore Default Preferences",
                "Reset Desktop Organizer automation and icon placement settings back to their out-of-the-box defaults?\n\n• Auto-restore on display change: Off\n• Auto-backup icon layout: Off (Every 3 days)\n• New icon placement: Center of primary screen\n\nYour saved desktop layouts will NOT be deleted.",
                "Restore Defaults");
            if (!confirmed) return;

            if (DesktopOrganizerService.ResetBehavioralDefaults(out var err))
            {
                _loadResult = DesktopOrganizerService.LoadConfigDetailed();
                _config = _loadResult.Config;
                SyncBehavioralControlsFromConfig();
                Program.PlaySound("click");
                SetStatus("✓ Restored default Desktop Organizer preferences.", true);
            }
            else
            {
                Program.PlaySound("error");
                SetStatus("⚠️ " + err, false);
            }
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
                UpdateIntervalStepperState();
                DesktopOrganizerService.SaveConfig(_config, out _);
                if (_config.AutoPeriodicSnapshotEnabled)
                {
                    DesktopOrganizerService.EnsureAutoSnapshotScheduler();
                    await Task.Run(() => DesktopOrganizerService.TryTakePeriodicAutoSnapshot(forceImmediateIfNone: true, out _));
                    _loadResult = DesktopOrganizerService.LoadConfigDetailed();
                    _config = _loadResult.Config;
                    RenderProfiles();
                    SetStatus($"Auto-backup enabled (every {_config.AutoSnapshotIntervalDays} day{(_config.AutoSnapshotIntervalDays == 1 ? "" : "s")}, keeps latest 5).", true);
                }
                else
                {
                    SetStatus("Auto-backup disabled.", true);
                }
            }
        }

        private void ApplyAutoSnapshotDays(int newDays)
        {
            int clamped = Math.Clamp(newDays, 1, 30);
            _config.AutoSnapshotIntervalDays = clamped;
            if (this.FindControl<TextBox>("AutoSnapshotDaysBox") is TextBox box)
            {
                box.Text = clamped.ToString();
            }
            UpdateIntervalStepperState();
            if (!_suppressConfigEvents && !_loadResult.IsReadOnly)
            {
                DesktopOrganizerService.SaveConfig(_config, out _);
                SetStatus($"Auto-backup interval set to every {clamped} day{(clamped == 1 ? "" : "s")}.", true);
            }
        }

        private void AutoSnapshotMinus_Click(object? sender, RoutedEventArgs e)
        {
            if (_loadResult.IsReadOnly || !_config.AutoPeriodicSnapshotEnabled) return;
            Program.PlaySound("click");
            ApplyAutoSnapshotDays(_config.AutoSnapshotIntervalDays - 1);
        }

        private void AutoSnapshotPlus_Click(object? sender, RoutedEventArgs e)
        {
            if (_loadResult.IsReadOnly || !_config.AutoPeriodicSnapshotEnabled) return;
            Program.PlaySound("click");
            ApplyAutoSnapshotDays(_config.AutoSnapshotIntervalDays + 1);
        }

        private void AutoSnapshotDays_LostFocus(object? sender, RoutedEventArgs e)
        {
            CommitAutoSnapshotDaysBox();
        }

        private void AutoSnapshotDays_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                CommitAutoSnapshotDaysBox();
            }
        }

        private void CommitAutoSnapshotDaysBox()
        {
            if (_suppressConfigEvents || _loadResult.IsReadOnly) return;
            if (this.FindControl<TextBox>("AutoSnapshotDaysBox") is TextBox box)
            {
                if (int.TryParse(box.Text?.Trim(), out int parsed))
                {
                    ApplyAutoSnapshotDays(parsed);
                }
                else
                {
                    box.Text = Math.Clamp(_config.AutoSnapshotIntervalDays, 1, 30).ToString();
                    SetStatus("⚠️ Please enter a whole number between 1 and 30 days.", false);
                }
            }
        }

        private void NewIconBehaviorCombo_Changed(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressConfigEvents || _loadResult.IsReadOnly) return;
            if (sender is ComboBox cb && cb.SelectedIndex >= 0 && cb.SelectedIndex <= 3)
            {
                _config.NewIconBehavior = (UnsavedIconBehavior)cb.SelectedIndex;
                DesktopOrganizerService.SaveConfig(_config, out _);
                SetStatus("Updated placement preference for new icons.", true);
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
