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

            _logText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14
            };

            var scroll = new ScrollViewer { Content = _logText, Margin = new Thickness(10) };
            Content = scroll;

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
                Log("Starting installation sequence...");
                await Task.Delay(500);

                Log("Step 1/5: Terminating running instances...");
                foreach (var p in Process.GetProcessesByName("KeyPulse"))
                {
                    if (p.Id != Environment.ProcessId) { try { p.Kill(); } catch { } }
                }
                await Task.Delay(500);
                Log("  -> Success.");

                Log("Step 2/5: Creating installation directory...");
                Directory.CreateDirectory(Program.InstallDir);
                Log("  -> Success.");

                Log("Step 3/5: Copying application binaries...");
                File.Copy(Environment.ProcessPath!, Program.ExePath, true);
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

                Log("Step 5/5: Creating Start Menu shortcut...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\KeyPulse.lnk");
                var script = $"""
                    $WshShell = New-Object -ComObject WScript.Shell
                    $Shortcut = $WshShell.CreateShortcut('{startMenu}')
                    $Shortcut.TargetPath = '{Program.ExePath}'
                    $Shortcut.WorkingDirectory = '{Program.InstallDir}'
                    $Shortcut.Save()
                    """;
                var ps = new Process { StartInfo = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"") { CreateNoWindow = true, UseShellExecute = false } };
                ps.Start();
                await ps.WaitForExitAsync();
                Log("  -> Success.");

                Log("");
                Log("INSTALLATION COMPLETE. Launching KeyPulse in 2 seconds...");
                await Task.Delay(2000);
                Process.Start(new ProcessStartInfo { FileName = Program.ExePath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"\nERROR during installation: {ex.Message}");
                await Task.Delay(5000);
            }
            finally
            {
                Dispatcher.UIThread.Post(() => ((App)Application.Current!).Exit_Clicked(null, null));
            }
        }

        private async Task RunUninstall()
        {
            try
            {
                Log("Starting uninstallation sequence...");
                await Task.Delay(500);

                Log("Step 1/4: Terminating running instances...");
                foreach (var p in Process.GetProcessesByName("KeyPulse"))
                {
                    if (p.Id != Environment.ProcessId) { try { p.Kill(); } catch { } }
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

                Log("Step 4/4: Removing Start Menu shortcut...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\KeyPulse.lnk");
                if (File.Exists(startMenu)) File.Delete(startMenu);
                Log("  -> Success.");

                Log("");
                Log("UNINSTALLATION COMPLETE. Exiting in 3 seconds...");
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                Log($"\nERROR during uninstallation: {ex.Message}");
                await Task.Delay(5000);
            }
            finally
            {
                Dispatcher.UIThread.Post(() => ((App)Application.Current!).Exit_Clicked(null, null));
            }
        }
    }
}
