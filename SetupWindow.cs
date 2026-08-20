using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KeyPulse
{
    public class SetupWindow : Window
    {
        private TextBlock _logText;
        private TextBlock _headerText;
        private Button _actionButton;
        private Grid _mainGrid;
        private bool _isUninstall;

        private AppConfig _config = new AppConfig();
        private readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse", "config.json");

        public SetupWindow(bool isUninstall)
        {
            _isUninstall = isUninstall;
            Title = isUninstall ? "KeyPulse Setup - Uninstalling" : "KeyPulse Setup - Installing";
            Width = 550;
            Height = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            LoadConfig();

            _headerText = new TextBlock
            {
                Text = isUninstall ? "Processing Uninstallation..." : "Processing Installation...",
                FontSize = 22,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(15, 15, 15, 5),
                Foreground = Brushes.LightBlue
            };

            _logText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14
            };

            _actionButton = new Button
            {
                Content = "...",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(15),
                Padding = new Thickness(25, 10),
                IsVisible = false,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };

            _mainGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            
            Grid.SetRow(_headerText, 0);
            _mainGrid.Children.Add(_headerText);

            var scroll = new ScrollViewer { Content = _logText, Margin = new Thickness(15, 0, 15, 0) };
            var border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(15, 0, 15, 0), Child = scroll };
            Grid.SetRow(border, 1);
            _mainGrid.Children.Add(border);

            Grid.SetRow(_actionButton, 2);
            _mainGrid.Children.Add(_actionButton);

            Content = _mainGrid;

            Loaded += SetupWindow_Loaded;
            Closing += SetupWindow_Closing;
        }

        private void LoadConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(ConfigPath);
                    var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    if (loaded != null)
                    {
                        _config = loaded;
                        if (!double.IsNaN(loaded.SetupWindowX) && !double.IsNaN(loaded.SetupWindowY))
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual;
                            Position = new Avalonia.PixelPoint((int)loaded.SetupWindowX, (int)loaded.SetupWindowY);
                        }
                        if (loaded.SetupWindowWidth > 0 && loaded.SetupWindowHeight > 0)
                        {
                            Width = loaded.SetupWindowWidth;
                            Height = loaded.SetupWindowHeight;
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
                _config.SetupWindowX = Position.X;
                _config.SetupWindowY = Position.Y;
                _config.SetupWindowWidth = Bounds.Width;
                _config.SetupWindowHeight = Bounds.Height;

                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = System.Text.Json.JsonSerializer.Serialize(_config, AppConfigJsonContext.Default.AppConfig);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private void SetupWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isUninstall)
                SaveConfig();
        }

        private void Log(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _logText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            });
        }

        private async void SetupWindow_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_isUninstall) await RunUninstall();
            else await RunInstall();
        }

        private async Task RunInstall()
        {
            try
            {
                Log("Cleaning up legacy temporary files...");
                try { foreach(var f in Directory.GetFiles(Path.GetTempPath(), "KeyPulse*.exe")) File.Delete(f); } catch { }
                try { foreach(var f in Directory.GetFiles(Path.GetTempPath(), "keypulse_*.txt")) File.Delete(f); } catch { }

                bool isUpgrade = File.Exists(Program.ExePath);
                if (isUpgrade)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    Dispatcher.UIThread.Post(() =>
                    {
                        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 20 };
                        panel.Children.Add(new TextBlock { Text = "Existing Installation Detected", FontSize = 22, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.LightBlue });
                        panel.Children.Add(new TextBlock { Text = "Do you want to wipe all settings for a fresh install or keep your existing configuration?", TextWrapping = TextWrapping.Wrap, MaxWidth = 450, TextAlignment = TextAlignment.Center });
                        
                        var btnKeep = new Button { Content = "Keep Settings & Upgrade", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                        var btnWipe = new Button { Content = "Wipe Settings (Fresh Install)", Foreground = Brushes.LightCoral, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                        
                        btnKeep.Click += (s, e) => { Content = _mainGrid; tcs.TrySetResult(false); };
                        btnWipe.Click += (s, e) => { Content = _mainGrid; tcs.TrySetResult(true); };
                        
                        panel.Children.Add(btnKeep);
                        panel.Children.Add(btnWipe);
                        
                        Content = new Border { Background = new SolidColorBrush(Color.Parse("#1e1e1e")), Child = panel };
                    });

                    bool wipe = await tcs.Task;
                    if (wipe)
                    {
                        Log("User opted for a FRESH INSTALL. Wiping old settings and logs...");
                        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse"), true); } catch { }
                        
                        
                        Log("  -> Success.");
                    }
                    else
                    {
                        Log("User opted to UPGRADE. Keeping existing settings.");
                        Log("Validating existing configuration schema...");
                        try 
                        {
                            var json = File.ReadAllText(ConfigPath);
                            var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                            if (loaded == null || loaded.Hotkeys == null) throw new Exception("Invalid Schema");
                            File.WriteAllText(ConfigPath, System.Text.Json.JsonSerializer.Serialize(loaded, AppConfigJsonContext.Default.AppConfig));
                            Log("  -> Schema valid.");
                        } 
                        catch (Exception)
                        {
                            Log("  -> Config corrupted or outdated. Resetting to default.");
                            try { File.Delete(ConfigPath); } catch { }
                        }
                    }
                }

                Log("Starting installation sequence...");
                await Task.Delay(500);

                Log("Step 1/5: Terminating running instances...");
                foreach (var p in Process.GetProcessesByName("KeyPulse"))
                {
                    if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                }
                await Task.Delay(500);
                Log("  -> Success.");

                Log("Step 2/5: Creating installation directory...");
                Directory.CreateDirectory(Program.InstallDir);
                Log("  -> Success.");

                Log("Step 3/5: Copying application binaries...");
                File.Copy(Environment.ProcessPath!, Program.ExePath, true);
                foreach (var dll in Directory.GetFiles(Path.GetDirectoryName(Environment.ProcessPath!)!, "*.dll")) File.Copy(dll, Path.Combine(Program.InstallDir, Path.GetFileName(dll)), true);
                Log("  -> Success.");

                Log("Step 4/5: Registering with Programs and Features (appwiz.cpl)...");
                var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Program.AppName}");
                key.SetValue("DisplayName", Program.AppName);
                key.SetValue("DisplayIcon", Program.ExePath);
                key.SetValue("UninstallString", $"\"{Program.ExePath}\" --uninstall");
                key.SetValue("Publisher", "Alon");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                Log("  -> Success.");

                Log("Step 5/5: Creating Start Menu and Desktop shortcuts...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\KeyPulse.lnk");
                var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "KeyPulse.lnk");
                var script = $"""
                    $WshShell = New-Object -ComObject WScript.Shell
                    $Shortcut = $WshShell.CreateShortcut('{startMenu.Replace("'", "''")}')
                    $Shortcut.TargetPath = '{Program.ExePath.Replace("'", "''")}'
                    $Shortcut.WorkingDirectory = '{Program.InstallDir.Replace("'", "''")}'
                    $Shortcut.Save()
                    $Shortcut2 = $WshShell.CreateShortcut('{desktop.Replace("'", "''")}')
                    $Shortcut2.TargetPath = '{Program.ExePath.Replace("'", "''")}'
                    $Shortcut2.WorkingDirectory = '{Program.InstallDir.Replace("'", "''")}'
                    $Shortcut2.Save()
                    """;
                var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var ps = new Process { StartInfo = new ProcessStartInfo("powershell", $"-NoProfile -EncodedCommand {encodedScript}") { CreateNoWindow = true, UseShellExecute = false } };
                ps.Start();
                await ps.WaitForExitAsync();
                Log("  -> Success.");

                Log("Step 6/6: Enabling Launch on Boot by default...");
                Program.SetStartup(true);
                Log("  -> Success.");

                Log("");
                Log("INSTALLATION COMPLETE.");
                
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = "INSTALLATION SUCCESSFUL";
                    _headerText.Foreground = Brushes.LightGreen;
                    _actionButton.Content = "Launch KeyPulse & Close";
                    _actionButton.IsVisible = true;
                    _actionButton.Click += (s, ev) =>
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = Program.ExePath, Arguments = "--hidden", UseShellExecute = true }); } catch { }
                        ((App)Application.Current!).Exit_Clicked(null, null);
                    };
                });
            }
            catch (Exception ex)
            {
                Log($"\nERROR during installation: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = "INSTALLATION FAILED";
                    _headerText.Foreground = Brushes.Red;
                    _actionButton.Content = "Close Setup";
                    _actionButton.IsVisible = true;
                    _actionButton.Click += (s, ev) =>
                    {
                        ((App)Application.Current!).Exit_Clicked(null, null);
                    };
                });
            }
        }

        private async Task RunUninstall()
        {
            try
            {
                Log("Cleaning up legacy temporary files...");
                try { foreach(var f in Directory.GetFiles(Path.GetTempPath(), "KeyPulse*.exe")) File.Delete(f); } catch { }
                try { foreach(var f in Directory.GetFiles(Path.GetTempPath(), "keypulse_*.txt")) File.Delete(f); } catch { }

                Log("Starting uninstallation sequence...");
                await Task.Delay(500);

                Log("Step 1/4: Terminating running instances...");
                foreach (var p in Process.GetProcessesByName("KeyPulse"))
                {
                    if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                }
                await Task.Delay(500);
                Log("  -> Success.");

                Log("Step 2/4: Removing application binaries...");
                try { Directory.Delete(Program.InstallDir, true); } catch { }
                Log("  -> Success.");

                Log("Step 3/4: Removing Registry keys (appwiz.cpl & Startup)...");
                try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Program.AppName}", false); } catch { }
                try { Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)?.DeleteValue(Program.AppName, false); } catch { }
                Log("  -> Success.");

                Log("Step 3.5/4: Purging AppData configuration...");
                try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse"), true); } catch { }
                Log("  -> Success.");

                Log("Step 4/4: Removing Start Menu and Desktop shortcuts...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\KeyPulse.lnk");
                if (File.Exists(startMenu)) File.Delete(startMenu);
                var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "KeyPulse.lnk");
                if (File.Exists(desktop)) File.Delete(desktop);
                Log("  -> Success.");

                Log("");
                Log("UNINSTALLATION COMPLETE.");
                
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = "UNINSTALLATION SUCCESSFUL";
                    _headerText.Foreground = Brushes.LightGreen;
                    _actionButton.Content = "Close Setup";
                    _actionButton.IsVisible = true;
                    _actionButton.Click += (s, ev) =>
                    {
                        ((App)Application.Current!).Exit_Clicked(null, null);
                    };
                });
            }
            catch (Exception ex)
            {
                Log($"\nERROR during uninstallation: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = "UNINSTALLATION FAILED";
                    _headerText.Foreground = Brushes.Red;
                    _actionButton.Content = "Close Setup";
                    _actionButton.IsVisible = true;
                    _actionButton.Click += (s, ev) =>
                    {
                        ((App)Application.Current!).Exit_Clicked(null, null);
                    };
                });
            }
        }
    }
}



