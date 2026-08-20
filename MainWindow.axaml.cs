using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Win32;

namespace KeyPulse
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<HotkeyEntry> Hotkeys { get; } = new();
        private const string AppName = "KeyPulse";
        private readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "config.json");

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadConfig();
            HotkeyManager.Start();
            ApplyHotkeys();

            // Check startup - handled in settings now
        }

        private AppConfig _currentConfig = new AppConfig();

        private void LoadConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    if (loaded != null)
                    {
                        _currentConfig = loaded;
                        foreach (var item in loaded.Hotkeys) Hotkeys.Add(item);

                        if (!double.IsNaN(loaded.MainWindowX) && !double.IsNaN(loaded.MainWindowY))
                        {
                            Position = new Avalonia.PixelPoint((int)loaded.MainWindowX, (int)loaded.MainWindowY);
                        }
                        if (loaded.MainWindowWidth > 0 && loaded.MainWindowHeight > 0)
                        {
                            Width = loaded.MainWindowWidth;
                            Height = loaded.MainWindowHeight;
                        }
                        if (Enum.TryParse<Avalonia.Controls.WindowState>(loaded.MainWindowState, out var state))
                        {
                            WindowState = state;
                        }
                    }
                }
                catch { }
            }
        }

        private void SaveConfig()
        {
            try
            {
                _currentConfig.Hotkeys = new System.Collections.Generic.List<HotkeyEntry>(Hotkeys);
                if (WindowState != Avalonia.Controls.WindowState.Minimized)
                {
                    _currentConfig.MainWindowX = Position.X;
                    _currentConfig.MainWindowY = Position.Y;
                    _currentConfig.MainWindowWidth = Bounds.Width;
                    _currentConfig.MainWindowHeight = Bounds.Height;
                    _currentConfig.MainWindowState = WindowState.ToString();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonSerializer.Serialize(_currentConfig, AppConfigJsonContext.Default.AppConfig);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private void ApplyHotkeys()
        {
            HotkeyManager.Clear();
            foreach (var h in Hotkeys)
            {
                HotkeyManager.Register(h.KeyCombination, () => ExecuteAction(h));
            }
        }

        private void ExecuteAction(HotkeyEntry entry)
        {
            try
            {
                switch (entry.Action)
                {
                    case ActionType.OpenFolder:
                        Process.Start("explorer.exe", $"\"{entry.Target}\"");
                        break;
                    case ActionType.LaunchProgram:
                        Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
                        break;
                    case ActionType.BrowseChrome:
                        Process.Start("chrome.exe", $"\"{entry.Target}\"");
                        break;
                    case ActionType.TypeText:
                        InputSimulator.TypeText(entry.Target);
                        break;
                    case ActionType.InsertText:
                        InputSimulator.InsertText(entry.Target);
                        break;
                }
            }
            catch { }
        }

        public void KeyCombo_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var tb = sender as TextBox;
            var error = this.FindControl<TextBlock>("ErrorText");
            if (tb == null || error == null) return;

            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                error.IsVisible = false;
                tb.Foreground = Avalonia.Media.Brushes.White;
                return;
            }

            if (!HotkeyManager.Probe(tb.Text))
            {
                error.Text = "Shortcut already taken by OS or another app!";
                error.IsVisible = true;
                tb.Foreground = Avalonia.Media.Brushes.Red;
            }
            else
            {
                error.IsVisible = false;
                tb.Foreground = Avalonia.Media.Brushes.White;
            }
        }

        public void Add_Click(object? sender, RoutedEventArgs e)
        {
            var combo = this.FindControl<TextBox>("KeyCombo")?.Text;
            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            var error = this.FindControl<TextBlock>("ErrorText");

            if (!string.IsNullOrWhiteSpace(combo) && !string.IsNullOrWhiteSpace(target) && actionCombo != null)
            {
                if (!HotkeyManager.Probe(combo)) return;

                var actionType = (ActionType)actionCombo.SelectedIndex;
                var entry = new HotkeyEntry
                {
                    KeyCombination = combo,
                    Action = actionType,
                    Target = target
                };
                Hotkeys.Add(entry);
                SaveConfig();
                ApplyHotkeys();

                if (this.FindControl<TextBox>("KeyCombo") is TextBox t) t.Text = "";
                if (this.FindControl<TextBox>("TargetText") is TextBox tg) tg.Text = "";
            }
        }

        public void Remove_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is HotkeyEntry entry)
            {
                Hotkeys.Remove(entry);
                SaveConfig();
                ApplyHotkeys();
            }
        }

        public void KeyCombo_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            e.Handled = true;
            if (e.Key == Avalonia.Input.Key.LeftCtrl || e.Key == Avalonia.Input.Key.RightCtrl ||
                e.Key == Avalonia.Input.Key.LeftAlt || e.Key == Avalonia.Input.Key.RightAlt ||
                e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift ||
                e.Key == Avalonia.Input.Key.LWin || e.Key == Avalonia.Input.Key.RWin)
                return;

            var mods = e.KeyModifiers;
            var parts = new System.Collections.Generic.List<string>();
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Meta)) parts.Add("Win");
            
            parts.Add(e.Key.ToString());

            if (sender is TextBox tb)
            {
                tb.Text = string.Join("+", parts);
            }
        }

        public void Settings_Click(object? sender, RoutedEventArgs e)
        {
            var isStartup = false;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                isStartup = key?.GetValue(AppName) != null;
            }

            var w = new Window { Title = "Settings", Width = 300, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = this.Icon };
            var sp = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
            var chk = new CheckBox { Content = "Launch on Boot", IsChecked = isStartup };
            chk.IsCheckedChanged += (s, ev) =>
            {
                using var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (chk.IsChecked == true)
                    regKey?.SetValue(AppName, Process.GetCurrentProcess().MainModule?.FileName ?? "");
                else
                    regKey?.DeleteValue(AppName, false);
            };
            var closeBtn = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            closeBtn.Click += (s, ev) => w.Close();

            sp.Children.Add(chk);
            sp.Children.Add(closeBtn);
            w.Content = sp;
            w.ShowDialog(this);
        }

        protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
        {
            SaveConfig();
            if (((App)Application.Current!).IsExiting) return;
            e.Cancel = true;
            this.Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            HotkeyManager.Stop();
            base.OnClosed(e);
        }
    }
}