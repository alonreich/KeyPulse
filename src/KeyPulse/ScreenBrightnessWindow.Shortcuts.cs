using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace KeyPulse
{
    /// <summary>
    /// U4: every brightness shortcut in ONE table (what it does / keys / clear). Keys used to be set in
    /// five places: step keys, preset cycle keys, a key per preset, "Instant Screen Level Hotkeys" and
    /// the per-screen ⌨ Key button. Preset rows and screen-shortcut rows now only show their key; it
    /// is edited here. Rarely changed options (step size, Focus Mode details, Identify screens) live
    /// in the "Advanced" section.
    /// </summary>
    public sealed partial class ScreenBrightnessWindow
    {
        private StackPanel? _shortcutsTable;
        private string? _shortcutsSignature;
        private bool _shortcutsRebuildPending;

        private Border CreateShortcutsPanel()
        {
            var border = new Border
            {
                Background = ScreenBrightnessOverlayWindow.AppBrush("AppPanelBrush", "#111827"),
                BorderBrush = ScreenBrightnessOverlayWindow.AppBrush("AppBorderBrush", "#1F2937"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14)
            };

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = "Shortcuts",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Click a box and press the keys. Esc, Delete or Backspace clears it. A key can only do one thing: giving it to another row takes it from this one.",
                FontSize = 11.5,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });

            _shortcutsTable = new StackPanel { Spacing = 4 };
            stack.Children.Add(_shortcutsTable);
            border.Child = stack;

            RenderShortcutsTable(force: true);
            return border;
        }

        private void RenderShortcutsTable(bool force = false)
        {
            if (_shortcutsTable == null) return;
            var cfg = ScreenBrightnessService.LoadConfig();
            var rows = BrightnessShortcuts.BuildRows(cfg);
            var signature = string.Join("\u001f", rows.Select(r => r.Id + "\u001e" + r.WhatItDoes + "\u001e" + r.Keys));

            if (!force)
            {
                if (signature == _shortcutsSignature) { _shortcutsRebuildPending = false; return; }
                if (IsUserInteractingWithin(_shortcutsTable)) { _shortcutsRebuildPending = true; return; }
            }

            _shortcutsSignature = signature;
            _shortcutsRebuildPending = false;
            _shortcutsTable.Children.Clear();
            _directHotkeyBoxes.Clear();

            foreach (var row in rows)
            {
                _shortcutsTable.Children.Add(CreateShortcutRowControl(row));
            }
        }

        private Control CreateShortcutRowControl(BrightnessShortcuts.Row row)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,190,Auto"),
                ColumnSpacing = 8,
                Margin = new Thickness(0, 1)
            };

            var what = new TextBlock
            {
                Text = row.WhatItDoes,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextPrimaryBrush", "#F8FAFC")
            };
            Grid.SetColumn(what, 0);
            grid.Children.Add(what);

            var box = new TextBox
            {
                Text = row.Keys,
                PlaceholderText = row.Placeholder,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(box, string.IsNullOrWhiteSpace(row.Keys) ? "No key yet." : HotkeyManager.DescribeComboList(row.Keys));
            AttachHotkeyRecorder(box, combo => AssignShortcut(row.Id, combo));
            if (row.Id.StartsWith(BrightnessShortcuts.ScreenPrefix, StringComparison.Ordinal))
            {
                _directHotkeyBoxes[row.Id.Substring(BrightnessShortcuts.ScreenPrefix.Length)] = box;
            }
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var clearBtn = new Button
            {
                Content = "✕",
                Classes = { "Secondary" },
                Padding = new Thickness(6, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(clearBtn, "Remove this key");
            clearBtn.Click += (_, _) =>
            {
                box.Text = string.Empty;
                AssignShortcut(row.Id, string.Empty);
            };
            Grid.SetColumn(clearBtn, 2);
            grid.Children.Add(clearBtn);

            return grid;
        }

        /// <summary>Sets one row's key, taking an identical key away from every other brightness shortcut.</summary>
        private void AssignShortcut(string id, string combo)
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            if (!string.IsNullOrWhiteSpace(combo)) BrightnessShortcuts.ClearDuplicate(cfg, combo, id);

            string label = BrightnessShortcuts.BuildRows(cfg).FirstOrDefault(r => r.Id == id)?.WhatItDoes ?? "shortcut";
            if (!BrightnessShortcuts.SetKeys(cfg, id, combo)) return;

            ScreenBrightnessService.SaveConfig(cfg);
            ScreenBrightnessService.RegisterBrightnessHotkeys();
            SetStatus(string.IsNullOrWhiteSpace(combo)
                ? $"Removed the key for {label}."
                : $"{HotkeyManager.DescribeComboList(combo)} now does: {label}.", true);

            RenderProfilesList();
            RenderDirectHotkeysList();
            RenderShortcutsTable();
        }

        /// <summary>Read-only key text for preset / screen-shortcut rows (the key is edited in Shortcuts).</summary>
        private static TextBox CreateKeyDisplayBox(string keys)
        {
            var box = new TextBox
            {
                Text = keys,
                PlaceholderText = "no key",
                IsReadOnly = true,
                Focusable = false,
                IsHitTestVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Width = 150,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(box, "Change this key in the Shortcuts list above.");
            return box;
        }

        // ---- Advanced section -------------------------------------------------------------------

        private Expander CreateAdvancedSection(ScreenBrightnessConfig cfg)
        {
            var content = new StackPanel { Spacing = 12, Margin = new Thickness(4, 6, 4, 4) };

            // Step size
            var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            stepRow.Children.Add(new TextBlock { Text = "Brighter / Darker step:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            int currentDelta = cfg.StepDelta > 0 ? cfg.StepDelta : 5;
            var stepCombo = new ComboBox
            {
                ItemsSource = new[] { "1%", "2%", "5% (default)", "10%" },
                Width = 130,
                FontSize = 11.5,
                SelectedIndex = currentDelta switch { 1 => 0, 2 => 1, 10 => 3, _ => 2 }
            };
            stepCombo.SelectionChanged += (_, _) =>
            {
                int newDelta = stepCombo.SelectedIndex switch { 0 => 1, 1 => 2, 3 => 10, _ => 5 };
                var c = ScreenBrightnessService.LoadConfig();
                c.StepDelta = newDelta;
                ScreenBrightnessService.SaveConfig(c);
                ScreenBrightnessService.RegisterBrightnessHotkeys();
                if (_masterMinusBtn != null) _masterMinusBtn.Content = $"−{newDelta}%";
                if (_masterPlusBtn != null) _masterPlusBtn.Content = $"+{newDelta}%";
                RenderShortcutsTable();
                SetStatus($"Brighter / Darker now move {newDelta}% per press.", true);
            };
            stepRow.Children.Add(stepCombo);
            content.Children.Add(stepRow);

            // Focus Mode details (F1 rename, F6 dim level + contrast)
            var focusTitle = new TextBlock { Text = "Focus Mode", FontSize = 12.5, FontWeight = FontWeight.SemiBold };
            content.Children.Add(focusTitle);

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nameRow.Children.Add(new TextBlock { Text = "Name:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var nameBox = new TextBox { Text = ScreenBrightnessService.FocusModeDisplayName(cfg), MaxLength = 32, Width = 200, FontSize = 11.5 };
            nameBox.LostFocus += (_, _) =>
            {
                var c = ScreenBrightnessService.LoadConfig();
                var newName = string.IsNullOrWhiteSpace(nameBox.Text) ? ScreenBrightnessService.DefaultFocusModeName : nameBox.Text.Trim();
                if (newName == c.FocusModeName) return;
                c.FocusModeName = newName;
                ScreenBrightnessService.SaveConfig(c);
                ScreenBrightnessService.NotifyStateChanged();
                RenderShortcutsTable();
                SetStatus($"Focus Mode is now called \"{newName}\".", true);
            };
            nameRow.Children.Add(nameBox);
            content.Children.Add(nameRow);

            var dimRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            dimRow.Children.Add(new TextBlock { Text = "Other screens go to:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var dimSlider = new Slider { Minimum = 0, Maximum = ScreenBrightnessService.MaxFocusModeDimLevel, Width = 160, Value = Math.Clamp(cfg.FocusModeDimLevel, 0, ScreenBrightnessService.MaxFocusModeDimLevel), IsSnapToTickEnabled = true, TickFrequency = 1 };
            var dimText = new TextBlock { Text = $"{(int)dimSlider.Value}%", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 40 };
            dimSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                dimText.Text = $"{(int)Math.Round(dimSlider.Value)}%";
            };
            dimSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => SaveDimLevel(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            dimSlider.LostFocus += (_, _) => SaveDimLevel();
            void SaveDimLevel()
            {
                var c = ScreenBrightnessService.LoadConfig();
                int v = (int)Math.Round(dimSlider.Value);
                if (c.FocusModeDimLevel == v) return;
                c.FocusModeDimLevel = v;
                ScreenBrightnessService.SaveConfig(c);
                SetStatus($"Focus Mode dims the other screens to {v}% (applies the next time you turn it on).", true);
            }
            dimRow.Children.Add(dimSlider);
            dimRow.Children.Add(dimText);
            content.Children.Add(dimRow);

            var contrastChk = new CheckBox
            {
                Content = "Also lower their contrast (never below the safe minimum)",
                IsChecked = cfg.FocusModeLowerContrast,
                FontSize = 12
            };
            contrastChk.IsCheckedChanged += (_, _) =>
            {
                var c = ScreenBrightnessService.LoadConfig();
                c.FocusModeLowerContrast = contrastChk.IsChecked == true;
                ScreenBrightnessService.SaveConfig(c);
            };
            content.Children.Add(contrastChk);

            content.Children.Add(new TextBlock
            {
                Text = "Linked screens (⇄) leave Focus Mode as soon as you change the main screen and follow it again. Screens that are not linked keep the gap until you press the key again.",
                FontSize = 11,
                Foreground = ScreenBrightnessOverlayWindow.AppBrush("AppTextMutedBrush", "#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });

            // Identify screens (moved out of the header)
            var identifyBtn = new Button
            {
                Content = "Identify screens",
                Classes = { "Secondary" },
                Padding = new Thickness(10, 5),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(identifyBtn, "Shows Screen A / B / C on each monitor so you know which card controls which display.");
            identifyBtn.Click += (_, _) =>
            {
                Program.PlaySound("click");
                ScreenBrightnessService.ShowIdentifyBanners();
            };
            content.Children.Add(identifyBtn);

            return new Expander
            {
                Header = "Advanced",
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = content
            };
        }
    }

    /// <summary>The rows of the Brightness settings "Shortcuts" table and how a key is stored for each.</summary>
    internal static class BrightnessShortcuts
    {
        internal sealed record Row(string Id, string WhatItDoes, string Keys, string Placeholder);

        internal const string IdStepUp = "step-up";
        internal const string IdStepDown = "step-down";
        internal const string IdCycleUp = "cycle-up";
        internal const string IdCycleDown = "cycle-down";
        internal const string IdFocus = "focus";
        internal const string PresetPrefix = "preset:";
        internal const string ScreenPrefix = "screen:";

        internal static List<Row> BuildRows(ScreenBrightnessConfig cfg)
        {
            int delta = cfg.StepDelta > 0 ? cfg.StepDelta : 5;
            var rows = new List<Row>
            {
                new(IdStepUp, $"Brighter (+{delta}%)", cfg.StepUpHotkey, "e.g. Ctrl+Alt+="),
                new(IdStepDown, $"Darker (-{delta}%)", cfg.StepDownHotkey, "e.g. Ctrl+Alt+-"),
                new(IdFocus, $"{ScreenBrightnessService.FocusModeDisplayName(cfg)}: dim every screen except the main one (press again to undo)", cfg.FocusModeHotkey, "e.g. Ctrl+Alt+Num *"),
                new(IdCycleUp, "Next preset", cfg.CycleUpHotkey, "e.g. Ctrl+Alt+Up"),
                new(IdCycleDown, "Previous preset", cfg.CycleDownHotkey, "e.g. Ctrl+Alt+Down"),
            };

            foreach (var p in cfg.Profiles)
            {
                rows.Add(new Row(PresetPrefix + p.Id, $"Preset \"{p.Name}\"", p.Hotkey, "e.g. Ctrl+Alt+1"));
            }

            foreach (var dh in cfg.DirectHotkeys ?? new List<ScreenDirectHotkey>())
            {
                var target = dh.TargetSlotIndex < 0
                    ? "all screens"
                    : ScreenBrightnessService.FormatSlotName(cfg, dh.TargetSlotIndex);
                var name = string.IsNullOrWhiteSpace(dh.Name) ? "Screen shortcut" : dh.Name.Trim();
                rows.Add(new Row(ScreenPrefix + dh.Id, $"{name}: {target} to {dh.Brightness}%", dh.Hotkey, "e.g. Ctrl+Alt+F1"));
            }
            return rows;
        }

        internal static void ClearDuplicate(ScreenBrightnessConfig cfg, string combo, string exceptId)
        {
            foreach (var row in BuildRows(cfg))
            {
                if (row.Id == exceptId) continue;
                if (string.Equals(row.Keys?.Trim(), combo.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    SetKeys(cfg, row.Id, string.Empty);
                }
            }
        }

        internal static bool SetKeys(ScreenBrightnessConfig cfg, string id, string combo)
        {
            switch (id)
            {
                case IdStepUp: cfg.StepUpHotkey = combo; return true;
                case IdStepDown: cfg.StepDownHotkey = combo; return true;
                case IdCycleUp: cfg.CycleUpHotkey = combo; return true;
                case IdCycleDown: cfg.CycleDownHotkey = combo; return true;
                case IdFocus: cfg.FocusModeHotkey = combo; return true;
            }

            if (id.StartsWith(PresetPrefix, StringComparison.Ordinal))
            {
                var profile = cfg.Profiles.FirstOrDefault(p => p.Id == id.Substring(PresetPrefix.Length));
                if (profile == null) return false;
                profile.Hotkey = combo;
                return true;
            }

            if (id.StartsWith(ScreenPrefix, StringComparison.Ordinal))
            {
                var dh = cfg.DirectHotkeys?.FirstOrDefault(d => d.Id == id.Substring(ScreenPrefix.Length));
                if (dh == null) return false;
                dh.Hotkey = combo;
                return true;
            }
            return false;
        }

    }
}
