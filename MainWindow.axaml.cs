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
            if (Environment.GetCommandLineArgs().Contains("--hidden")) { EventHandler? handler = null; handler = (s, e) => { this.Hide(); this.Opened -= handler; }; this.Opened += handler; }
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
                            var pos = new Avalonia.PixelPoint((int)loaded.MainWindowX, (int)loaded.MainWindowY);
                            bool isVisible = false;
                            foreach (var scr in Screens.All)
                            {
                                if (scr.Bounds.Contains(pos)) { isVisible = true; break; }
                            }
                            if (isVisible) Position = pos;
                            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
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

        public async void ActionCombo_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                if (sender is ComboBox cb && cb.SelectedIndex != -1)
                {
                    var actionType = (ActionType)cb.SelectedIndex;
                    var targetText = this.FindControl<TextBox>("TargetText");
                    if (targetText == null) return;

                    if (actionType == ActionType.OpenFolder)
                    {
                        var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select Folder", AllowMultiple = false };
                        var folders = await this.StorageProvider.OpenFolderPickerAsync(options);
                        if (folders != null && folders.Count > 0)
                        {
                            targetText.Text = folders[0].Path.LocalPath;
                        }
                    }
                    else if (actionType == ActionType.LaunchProgram)
                    {
                        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { 
                            Title = "Select Executable", 
                            AllowMultiple = false, 
                            FileTypeFilter = new[] { 
                                new Avalonia.Platform.Storage.FilePickerFileType("Executables") { Patterns = new[] { "*.exe", "*.bat", "*.cmd", "*.ps1", "*.vbs", "*.lnk" } }, 
                                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } } 
                            } 
                        };
                        var files = await this.StorageProvider.OpenFilePickerAsync(options);
                        if (files != null && files.Count > 0)
                        {
                            targetText.Text = "\"" + files[0].Path.LocalPath + "\"";
                        }
                    }
                }
            }
            catch { }
        }

        private void ExecuteAction(HotkeyEntry entry)
        {
            try
            {
                switch (entry.Action)
                {
                    case ActionType.OpenFolder:
                        Process.Start("explorer.exe", $"\"{entry.Target.Trim('"')}\"");
                        break;
                    case ActionType.LaunchProgram:
                        string fileName = entry.Target;
                        string arguments = "";
                        if (fileName.StartsWith("\"")) {
                            int end = fileName.IndexOf("\"", 1);
                            if (end > 0) {
                                arguments = fileName.Substring(end + 1).Trim();
                                fileName = fileName.Substring(1, end - 1);
                            }
                        } else {
                            if (!File.Exists(fileName)) {
                                int lastSpace = fileName.LastIndexOf(" ");
                                while (lastSpace > 0) {
                                    string testPath = fileName.Substring(0, lastSpace);
                                    if (File.Exists(testPath)) {
                                        arguments = fileName.Substring(lastSpace + 1).Trim();
                                        fileName = testPath;
                                        break;
                                    }
                                    lastSpace = fileName.LastIndexOf(" ", lastSpace - 1);
                                }
                                if (!File.Exists(fileName))
                                {
                                    int firstSpace = fileName.IndexOf(" ");
                                    if (firstSpace > 0)
                                    {
                                        arguments = fileName.Substring(firstSpace + 1).Trim();
                                        fileName = fileName.Substring(0, firstSpace);
                                    }
                                }
                            }
                        }
                        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
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

            if (tb.Text.EndsWith("+"))
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
            Program.PlaySound("click");
            var combo = this.FindControl<TextBox>("KeyCombo")?.Text;
            var actionCombo = this.FindControl<ComboBox>("ActionCombo");
            var target = this.FindControl<TextBox>("TargetText")?.Text;
            var error = this.FindControl<TextBlock>("ErrorText");

            if (!string.IsNullOrWhiteSpace(combo) && !string.IsNullOrWhiteSpace(target) && actionCombo != null)
            {
                if (!HotkeyManager.Probe(combo))
                {
                    if (error != null) { error.Text = "Cannot save: Shortcut taken or invalid!"; error.IsVisible = true; }
                    return;
                }

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
            Program.PlaySound("close");
            if (sender is Button btn && btn.DataContext is HotkeyEntry entry)
            {
                if (btn.Content?.ToString() == "Confirm?")
                {
                    Hotkeys.Remove(entry);
                    SaveConfig();
                    ApplyHotkeys();
                }
                else
                {
                    btn.Content = "Confirm?";
                    btn.Foreground = Avalonia.Media.Brushes.Red;
                    Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                    {
                        if (btn.Content?.ToString() == "Confirm?")
                        {
                            btn.Content = "Remove";
                            btn.Foreground = Avalonia.Media.Brushes.White; // Reset color, since we force Dark mode this is safe
                        }
                    }, TimeSpan.FromSeconds(3));
                }
            }
        }

        public void KeyCombo_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            e.Handled = true;

            var mods = e.KeyModifiers;
            var parts = new System.Collections.Generic.List<string>();
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(Avalonia.Input.KeyModifiers.Meta)) parts.Add("Win");
            
            bool isModifierOnly = (e.Key == Avalonia.Input.Key.LeftCtrl || e.Key == Avalonia.Input.Key.RightCtrl ||
                e.Key == Avalonia.Input.Key.LeftAlt || e.Key == Avalonia.Input.Key.RightAlt ||
                e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift ||
                e.Key == Avalonia.Input.Key.LWin || e.Key == Avalonia.Input.Key.RWin);

            if (!isModifierOnly)
            {
                parts.Add(e.Key.ToString());
            }

            if (sender is TextBox tb)
            {
                var txt = string.Join("+", parts);
                if (isModifierOnly && parts.Count > 0) txt += "+";
                tb.Text = txt;
            }
        }

        public void Settings_Click(object? sender, RoutedEventArgs e)
        {
            Program.PlaySound("open");
            var isStartup = false;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                isStartup = key?.GetValue("KeyPulse") != null;
            }

            var w = new Window { Title = "Settings", Width = 400, Height = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = this.Icon };
            var grid = new Grid { Margin = new Avalonia.Thickness(20), RowDefinitions = new Avalonia.Controls.RowDefinitions("Auto,Auto,Auto,*,Auto") };
            
            var chk = new CheckBox { Content = "Launch on Boot", IsChecked = isStartup, TabIndex = 0, IsTabStop = true };
            chk.IsCheckedChanged += (s, ev) =>
            {
                Program.SetStartup(chk.IsChecked == true);
            };
            Grid.SetRow(chk, 0);
            grid.Children.Add(chk);

            var backupBtn = new Button { Content = "Backup Configuration", Margin = new Avalonia.Thickness(0,10,0,0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Avalonia.Media.Brushes.DarkSlateBlue, TabIndex = 1, IsTabStop = true };
            Grid.SetRow(backupBtn, 1);
            grid.Children.Add(backupBtn);

            var restoreBtn = new Button { Content = "Restore Configuration", Margin = new Avalonia.Thickness(0,10,0,0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Avalonia.Media.Brushes.Transparent, BorderBrush = Avalonia.Media.Brushes.Gray, BorderThickness = new Avalonia.Thickness(1), TabIndex = 2, IsTabStop = true };
            Grid.SetRow(restoreBtn, 2);
            grid.Children.Add(restoreBtn);

            var statusTxt = new TextBlock { Margin = new Avalonia.Thickness(0,10,0,0), Foreground = Avalonia.Media.Brushes.Orange, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
            Grid.SetRow(statusTxt, 3);
            grid.Children.Add(statusTxt);

            var closeBtn = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 3, IsTabStop = true };
            closeBtn.Click += (s, ev) => w.Close();
            Grid.SetRow(closeBtn, 4);
            grid.Children.Add(closeBtn);

            backupBtn.Click += async (s, ev) =>
            {
                backupBtn.IsEnabled = false; restoreBtn.IsEnabled = false;
                var options = new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "Export Backup", DefaultExtension = "json", SuggestedFileName = "KeyPulse_Backup.json" };
                var file = await w.StorageProvider.SaveFilePickerAsync(options);
                if (file != null)
                {
                    try {
                        SaveConfig();
                        File.Copy(ConfigPath, file.Path.LocalPath, true);
                        statusTxt.Text = "Backup exported successfully.";
                        statusTxt.Foreground = Avalonia.Media.Brushes.LightGreen;
                    } catch (Exception ex) { statusTxt.Text = "Export failed: " + ex.Message; statusTxt.Foreground = Avalonia.Media.Brushes.Red; }
                }
                backupBtn.IsEnabled = true; restoreBtn.IsEnabled = true;
            };

            restoreBtn.Click += async (s, ev) =>
            {
                backupBtn.IsEnabled = false; restoreBtn.IsEnabled = false;
                var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Import Backup", AllowMultiple = false };
                var files = await w.StorageProvider.OpenFilePickerAsync(options);
                if (files != null && files.Count > 0)
                {
                    try {
                        var json = File.ReadAllText(files[0].Path.LocalPath);
                        var loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                        if (loaded != null)
                        {
                            _currentConfig = loaded;
                            Hotkeys.Clear();
                            var conflicts = 0;
                            foreach (var item in loaded.Hotkeys)
                            {
                                if (!HotkeyManager.Probe(item.KeyCombination)) conflicts++;
                                Hotkeys.Add(item);
                            }
                            SaveConfig();
                            ApplyHotkeys();
                            if (conflicts > 0) {
                                statusTxt.Text = $"⚠ RESTORE WARNING\n\n• {conflicts} shortcut(s) could not be registered.\n• These keys are reserved by Windows or another app.\n\nPlease assign new keys.";
                                statusTxt.Foreground = Avalonia.Media.Brushes.Orange;
                            } else {
                                statusTxt.Text = "✔ Restore completed successfully.\nAll shortcuts are active.";
                                statusTxt.Foreground = Avalonia.Media.Brushes.LightGreen;
                            }
                        }
                    } catch (Exception ex) { statusTxt.Text = "Import failed: " + ex.Message; statusTxt.Foreground = Avalonia.Media.Brushes.Red; }
                }
                backupBtn.IsEnabled = true; restoreBtn.IsEnabled = true;
            };

            w.Content = grid;
            if (this.IsVisible) { w.ShowDialog(this); } else { w.Show(); }
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




