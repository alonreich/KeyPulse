using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KeyPulse
{
    public class SetupWindow : Window
    {
        private ListBox _logList;
        private TextBlock _headerText;
        private TextBlock _progressText;
        private ProgressBar _progressBar;
        private Button _actionButton;
        private Grid _mainGrid;
        private bool _isUninstall;
        private readonly ObservableCollection<string> _logLines = new();

        private AppConfig _config = new AppConfig();
        private readonly string ConfigPath = ConfigStore.ConfigPath;

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
                Margin = new Thickness(15, 15, 15, 5),
                Classes = { "SectionTitle" }
            };

            _progressText = new TextBlock
            {
                Text = isUninstall ? "Preparing uninstall..." : "Preparing install...",
                Classes = { "Muted" },
                Margin = new Thickness(15, 0, 15, 4)
            };

            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = isUninstall ? 4 : 6,
                Value = 0,
                Margin = new Thickness(15, 0, 15, 10)
            };

            _logList = new ListBox
            {
                ItemsSource = _logLines,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
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
            _actionButton.Classes.Add("Primary");

            _mainGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
            
            Grid.SetRow(_headerText, 0);
            _mainGrid.Children.Add(_headerText);

            var progressPanel = new StackPanel { Spacing = 2 };
            progressPanel.Children.Add(_progressText);
            progressPanel.Children.Add(_progressBar);
            Grid.SetRow(progressPanel, 1);
            _mainGrid.Children.Add(progressPanel);

            var border = new Border { Classes = { "Panel" }, Padding = new Thickness(0), Margin = new Thickness(15, 0, 15, 0), Child = _logList };
            Grid.SetRow(border, 2);
            _mainGrid.Children.Add(border);

            Grid.SetRow(_actionButton, 3);
            _mainGrid.Children.Add(_actionButton);

            Content = _mainGrid;

            Loaded += SetupWindow_Loaded;
            Closing += SetupWindow_Closing;
        }

        private void LoadConfig()
        {
            try
            {
                // ISSUE_4: shared loader that quarantines a damaged file instead of silently
                // starting empty and then overwriting it.
                var loaded = ConfigStore.Load(out _, out _);
                _config = loaded;

                if (loaded.SetupWindowWidth > 0 && loaded.SetupWindowHeight > 0)
                {
                    Width = Math.Max(420, loaded.SetupWindowWidth);
                    Height = Math.Max(320, loaded.SetupWindowHeight);
                }

                if (!double.IsNaN(loaded.SetupWindowX) && !double.IsNaN(loaded.SetupWindowY))
                {
                    var pos = new Avalonia.PixelPoint((int)loaded.SetupWindowX, (int)loaded.SetupWindowY);
                    var isVisible = false;
                    foreach (var scr in Screens.All)
                    {
                        if (scr.Bounds.Contains(pos)) { isVisible = true; break; }
                    }

                    if (isVisible)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Position = pos;
                    }
                    else
                    {
                        WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                _config.SetupWindowX = Position.X;
                _config.SetupWindowY = Position.Y;
                _config.SetupWindowWidth = Bounds.Width;
                _config.SetupWindowHeight = Bounds.Height;

                ConfigStore.Save(_config, out _);
            }
            catch { }
        }

        private void SetupWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isUninstall)
                SaveConfig();
            else
            {
                var outDir = Path.Combine(Path.GetTempPath(), Program.AppName + "_NativeLibs");
                var selfDeleteScript = $"/c for /L %i in (1,1,30) do (ping 127.0.0.1 -n 2 > nul & rmdir /S /Q \"{outDir}\" & del /F /q \"{Environment.ProcessPath}\" & if not exist \"{Environment.ProcessPath}\" exit)";
                Process.Start(new ProcessStartInfo("cmd.exe", selfDeleteScript) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
        }

        private void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Dispatcher.UIThread.Post(() =>
            {
                _logLines.Add(line);
                _logList.ScrollIntoView(line);
            });
        }

        private void SetProgress(string message, double value, double maximum)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _progressText.Text = message;
                _progressBar.Maximum = maximum;
                _progressBar.Value = Math.Max(0, Math.Min(value, maximum));
            });
        }

        private static readonly IBrush TransparentBrush = new SolidColorBrush(Color.FromUInt32(0));

        private static IBrush AppBrush(string resourceKey)
        {
            try
            {
                if (Application.Current?.FindResource(resourceKey) is IBrush brush) return brush;
                if (Application.Current?.FindResource("AppTextPrimaryBrush") is IBrush fallback) return fallback;
            }
            catch
            {
            }

            return TransparentBrush;
        }

        private static void DeleteMatchingFilesExceptCurrent(string pattern, Action<string, Exception>? onFailure = null)
        {
            var currentPath = Environment.ProcessPath;
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), pattern))
            {
                if (!string.IsNullOrWhiteSpace(currentPath) && Path.GetFullPath(file).Equals(Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    onFailure?.Invoke(file, ex);
                }
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        private static string? ResolveDesktopDirectory(out string probeSummary)
        {
            var candidates = new List<string>();
            AddKnownFolderDesktopCandidate(candidates);
            AddDesktopCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", candidates);
            AddDesktopCandidateFromRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", candidates);
            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            AddCandidate(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"));
            AddCandidate(candidates, Path.Combine(Environment.GetEnvironmentVariable("OneDrive") ?? string.Empty, "Desktop"));
            AddCandidate(candidates, Path.Combine(Environment.GetEnvironmentVariable("OneDriveCommercial") ?? string.Empty, "Desktop"));
            AddCandidate(candidates, Path.Combine(Environment.GetEnvironmentVariable("OneDriveConsumer") ?? string.Empty, "Desktop"));

            var probed = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim());
                if (string.IsNullOrWhiteSpace(expanded)) continue;

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(expanded.Trim('"'));
                }
                catch
                {
                    probed.Add(expanded + " [invalid]");
                    continue;
                }

                if (!seen.Add(fullPath)) continue;
                if (!Directory.Exists(fullPath))
                {
                    probed.Add(fullPath + " [missing]");
                    continue;
                }

                var root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrWhiteSpace(root) && !Directory.Exists(root))
                {
                    probed.Add(fullPath + " [root missing]");
                    continue;
                }

                if (!CanWriteToDirectory(fullPath))
                {
                    probed.Add(fullPath + " [not writable]");
                    continue;
                }

                probed.Add(fullPath + " [selected]");
                probeSummary = string.Join("; ", probed);
                return fullPath;
            }

            probeSummary = string.Join("; ", probed);
            return null;
        }

        private static void AddKnownFolderDesktopCandidate(List<string> candidates)
        {
            var desktopId = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
            IntPtr pathPtr = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(desktopId, 0, IntPtr.Zero, out pathPtr) == 0 && pathPtr != IntPtr.Zero)
                {
                    AddCandidate(candidates, Marshal.PtrToStringUni(pathPtr));
                }
            }
            catch { }
            finally
            {
                if (pathPtr != IntPtr.Zero) CoTaskMemFree(pathPtr);
            }
        }

        private static void AddDesktopCandidateFromRegistry(RegistryKey root, string subKey, List<string> candidates)
        {
            try
            {
                using var key = root.OpenSubKey(subKey, false);
                if (key?.GetValue("Desktop") is string path)
                {
                    AddCandidate(candidates, path);
                }
            }
            catch { }
        }

        private static void AddCandidate(List<string> candidates, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) candidates.Add(path);
        }

        private static bool CanWriteToDirectory(string directory)
        {
            try
            {
                var probePath = Path.Combine(directory, ".keypulse_desktop_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return value.Replace("'", "''");
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        internal interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        private async Task<List<string>> CreateShortcutsAsync(string startMenuShortcut, string? desktopShortcut)
        {
            var warnings = new List<string>();
            Directory.CreateDirectory(Path.GetDirectoryName(startMenuShortcut)!);
            if (desktopShortcut != null) Directory.CreateDirectory(Path.GetDirectoryName(desktopShortcut)!);

            await Task.Run(() =>
            {
                try
                {
                    IShellLink link = (IShellLink)new ShellLink();
                    link.SetPath(Program.ExePath);
                    link.SetWorkingDirectory(Program.InstallDir);
                    IPersistFile file = (IPersistFile)link;
                    file.Save(startMenuShortcut, false);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Start Menu shortcut creation failed: {ex.Message}");
                }

                if (desktopShortcut != null)
                {
                    try
                    {
                        IShellLink link2 = (IShellLink)new ShellLink();
                        link2.SetPath(Program.ExePath);
                        link2.SetWorkingDirectory(Program.InstallDir);
                        IPersistFile file2 = (IPersistFile)link2;
                        file2.Save(desktopShortcut, false);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Desktop shortcut creation failed: {ex.Message}");
                    }
                }
                else
                {
                    warnings.Add("Desktop shortcut skipped because no writable Desktop folder could be resolved.");
                }
            });

            if (!File.Exists(startMenuShortcut))
            {
                warnings.Add("Start Menu shortcut was not created.");
            }

            if (desktopShortcut != null && !File.Exists(desktopShortcut))
            {
                warnings.Add("Desktop shortcut was not created at " + desktopShortcut);
            }

            return warnings;
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
                SetProgress("Preparing installation...", 0, 6);
                Log("Cleaning up legacy temporary files...");
                try { DeleteMatchingFilesExceptCurrent("KeyPulse*.exe"); } catch { }
                try { DeleteMatchingFilesExceptCurrent("keypulse_*.txt"); } catch { }
                // ISSUE_21: the graphics libraries moved out of %TEMP% and into the install folder.
                try { if (Directory.Exists(Program.LegacyNativeLibDir)) Directory.Delete(Program.LegacyNativeLibDir, true); } catch { }
                // ISSUE_8: logs moved out of %LOCALAPPDATA%\KeyPulse, which nothing ever cleaned up.
                try { if (Directory.Exists(Program.LegacyLogDir)) Directory.Delete(Program.LegacyLogDir, true); } catch { }
                var installWarnings = new List<string>();

                bool isUpgrade = File.Exists(Program.ExePath);
                if (isUpgrade)
                {
                    SetProgress("Waiting for upgrade choice...", 0, 6);
                    var tcs = new TaskCompletionSource<bool>();
                    Dispatcher.UIThread.Post(() =>
                    {
                        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 14, MaxWidth = 470 };
                        panel.Children.Add(new TextBlock { Text = "Existing Installation Detected", Classes = { "SectionTitle" }, HorizontalAlignment = HorizontalAlignment.Center });
                        panel.Children.Add(new TextBlock { Text = "Keep your existing shortcuts and settings, or wipe them for a fresh install.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                        panel.Children.Add(new TextBlock { Text = "Wiping settings deletes saved shortcuts and app preferences.", Classes = { "ErrorText" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                        
                        var btnKeep = new Button { Content = "Keep Settings & Upgrade", Classes = { "Primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 0 };
                        var btnWipe = new Button { Content = "Wipe Settings", Classes = { "Danger" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 1 };
                        
                        btnKeep.Click += (s, e) => { Content = _mainGrid; tcs.TrySetResult(false); };
                        btnWipe.Click += (s, e) => { Content = _mainGrid; tcs.TrySetResult(true); };
                        
                        panel.Children.Add(btnKeep);
                        panel.Children.Add(btnWipe);
                        
                        Content = new Border { Classes = { "Panel" }, Margin = new Thickness(24), Child = panel };
                        btnKeep.Focus();
                    });

                    bool wipe = await tcs.Task;
                    if (wipe)
                    {
                        Log("User opted for a FRESH INSTALL. Wiping old settings and logs...");
                        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse"), true); } catch { }
                        _config = new AppConfig();
                        
                        Log("  -> Success.");
                    }
                    else
                    {
                        Log("User opted to UPGRADE. Keeping existing settings.");
                        Log("Validating existing configuration schema...");
                        // ISSUE_4: a damaged config is renamed aside, never deleted. The old code
                        // called File.Delete on it, destroying the user's shortcuts permanently.
                        var loaded = ConfigStore.Load(out var configError, out var quarantined);
                        if (string.IsNullOrEmpty(configError))
                        {
                            _config = loaded;
                            Log("  -> Schema valid (" + loaded.Hotkeys.Count + " shortcut(s)).");
                        }
                        else
                        {
                            Log("  -> " + configError);
                            if (!string.IsNullOrEmpty(quarantined))
                            {
                                Log("  -> The damaged file was kept as: " + quarantined);
                                installWarnings.Add("Your previous settings could not be read. The old file was kept as " + quarantined + ".");
                            }
                            else
                            {
                                installWarnings.Add("Your previous settings could not be read.");
                            }
                            _config = new AppConfig();
                        }
                    }
                }

                Log("Starting installation sequence...");
                await Task.Delay(500);

                SetProgress("Step 1 of 6: Terminating running instances...", 1, 6);
                Log("Step 1/6: Terminating running instances...");
                foreach (var p in Process.GetProcessesByName("KeyPulse"))
                {
                    if (p.Id != Environment.ProcessId) { try { p.CloseMainWindow(); if (!p.WaitForExit(1000)) p.Kill(); p.WaitForExit(3000); } catch { } }
                }
                await Task.Delay(500);
                Log("  -> Success.");

                SetProgress("Step 2 of 6: Creating installation directory...", 2, 6);
                Log("Step 2/6: Cleaning legacy directories and creating new installation path...");
                try
                {
                    var pf64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.AppName);
                    var pf32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Program.AppName);
                    if (Directory.Exists(pf64)) Directory.Delete(pf64, true);
                    if (Directory.Exists(pf32)) Directory.Delete(pf32, true);
                }
                catch (Exception ex)
                {
                    Log("  -> Warning: Failed to clean legacy Program Files directory: " + ex.Message);
                }
                Directory.CreateDirectory(Program.InstallDir);
                Log("  -> Success.");

                SetProgress("Step 3 of 6: Copying application binaries...", 3, 6);
                Log("Step 3/6: Copying application binaries...");
                File.Copy(Environment.ProcessPath!, Program.ExePath, true);
                Log("  -> Success.");

                SetProgress("Step 4 of 6: Registering with Windows...", 4, 6);
                Log("Step 4/6: Registering with Programs and Features (appwiz.cpl)...");
                // ISSUE_16: Windows "Installed apps" showed KeyPulse with a blank version, blank
                // size and no install date, because only four values were ever written. The row now
                // carries everything Windows knows how to display.
                var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Program.AppName}");
                key.SetValue("DisplayName", Program.AppName);
                key.SetValue("DisplayIcon", Program.ExePath + ",0");
                key.SetValue("DisplayVersion", Program.AppVersion);
                key.SetValue("UninstallString", $"\"{Program.ExePath}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{Program.ExePath}\" --uninstall");
                key.SetValue("InstallLocation", Program.InstallDir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("Publisher", "Alon");
                key.SetValue("URLInfoAbout", "https://github.com/" + Program.RepositorySlug);
                key.SetValue("HelpLink", "https://github.com/" + Program.RepositorySlug + "/issues");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

                try
                {
                    var sizeKb = (int)Math.Max(1, new FileInfo(Program.ExePath).Length / 1024);
                    key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
                }
                catch (Exception sizeEx)
                {
                    Log("  -> Warning: could not record the installed size: " + sizeEx.Message);
                }

                Log("  -> Success (version " + Program.AppVersion + ").");

                SetProgress("Step 5 of 6: Creating shortcuts...", 5, 6);
                Log("Step 5/6: Creating Start Menu and Desktop shortcuts...");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\KeyPulse.lnk");
                var desktopDir = ResolveDesktopDirectory(out var desktopProbeSummary);
                if (!string.IsNullOrWhiteSpace(desktopProbeSummary))
                {
                    Log("  -> Desktop probe: " + desktopProbeSummary);
                }
                var desktop = desktopDir == null ? null : Path.Combine(desktopDir, "KeyPulse.lnk");
                var shortcutWarnings = await CreateShortcutsAsync(startMenu, desktop);
                if (shortcutWarnings.Count == 0)
                {
                    Log("  -> Success.");
                }
                else
                {
                    foreach (var warning in shortcutWarnings)
                    {
                        installWarnings.Add(warning);
                        Log("  -> Warning: " + warning);
                    }
                }

                SetProgress("Step 6 of 6: Finalizing startup settings...", 6, 6);
                Log("Step 6/6: Updating Launch on Boot script if enabled...");
                try
                {
                    // ISSUE_6 / ISSUE_12: ask the single authority (Run key OR elevated logon task)
                    // and regenerate the launcher script without the old 60-second relaunch loop.
                    if (Program.IsStartupEnabled())
                    {
                        if (Program.SetStartup(true, out var startupError))
                            Log("  -> Start with Windows is on; launcher regenerated.");
                        else
                        {
                            Log("  -> Warning: " + startupError);
                            installWarnings.Add("Start with Windows could not be re-applied: " + startupError);
                        }
                    }
                    else
                    {
                        Log("  -> Start with Windows is off. Skipping.");
                    }
                }
                catch (Exception ex)
                {
                    Log("  -> Warning: Failed to check startup settings: " + ex.Message);
                }

                Log("");
                Log(installWarnings.Count == 0 ? "INSTALLATION COMPLETE." : "INSTALLATION COMPLETED WITH WARNINGS.");
                foreach (var warning in installWarnings)
                {
                    Log("  - " + warning);
                }
                
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = installWarnings.Count == 0 ? "INSTALLATION SUCCESSFUL" : "INSTALLATION COMPLETED WITH WARNINGS";
                    _headerText.Foreground = installWarnings.Count == 0 ? AppBrush("AppSuccessSoftBrush") : AppBrush("AppWarningBrush");
                    _actionButton.Content = "Launch KeyPulse & Close";
                    _actionButton.IsVisible = true;
                    _actionButton.Focus();
                    _actionButton.Click += (s, ev) =>
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = Program.ExePath, Arguments = "", UseShellExecute = true }); } catch { }
                        ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                    };
                });
            }
            catch (Exception ex)
            {
                SetProgress("Installation failed.", 0, 6);
                Log($"\nERROR during installation: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = "INSTALLATION FAILED";
                    _headerText.Foreground = AppBrush("AppDangerBrush");
                    _actionButton.Content = "Close Setup";
                    _actionButton.IsVisible = true;
                    _actionButton.Focus();
                    _actionButton.Click += (s, ev) =>
                    {
                        ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                    };
                });
            }
        }

        private async Task RunUninstall()
        {
            try
            {
                SetProgress("Preparing uninstall...", 0, 4);
                var failures = new List<string>();

                void RecordFailure(string operation, Exception ex)
                {
                    var message = $"{operation}: {ex.Message}";
                    failures.Add(message);
                    Log("  -> Failed: " + message);
                }

                Log("Cleaning up legacy temporary files...");
                try { DeleteMatchingFilesExceptCurrent("KeyPulse*.exe", (file, ex) => RecordFailure("Temporary executable cleanup " + file, ex)); } catch (Exception ex) { RecordFailure("Temporary executable cleanup", ex); }
                try { DeleteMatchingFilesExceptCurrent("keypulse_*.txt", (file, ex) => RecordFailure("Temporary log cleanup " + file, ex)); } catch (Exception ex) { RecordFailure("Temporary log cleanup", ex); }
                try { if (Directory.Exists(Program.LegacyNativeLibDir)) Directory.Delete(Program.LegacyNativeLibDir, true); } catch (Exception ex) { RecordFailure("Legacy native library folder", ex); }
                try { var fallbackRuntime = Path.Combine(Path.GetTempPath(), Program.AppName + "_Runtime"); if (Directory.Exists(fallbackRuntime)) Directory.Delete(fallbackRuntime, true); } catch { }

                Log("Starting uninstallation sequence...");
                await Task.Delay(500);

                SetProgress("Step 1 of 4: Terminating running instances...", 1, 4);
                Log("Step 1/4: Terminating running instances...");
                var processFailureCount = failures.Count;
                foreach (var p in Process.GetProcessesByName("KeyPulse"))
                {
                    if (p.Id != Environment.ProcessId)
                    {
                        try
                        {
                            p.CloseMainWindow();
                            if (!p.WaitForExit(1000)) p.Kill();
                            if (!p.WaitForExit(3000))
                            {
                                var message = $"Process {p.Id}: did not exit within 3 seconds";
                                failures.Add(message);
                                Log("  -> Failed: " + message);
                            }
                        }
                        catch (Exception ex)
                        {
                            RecordFailure($"Process {p.Id}", ex);
                        }
                    }
                }
                await Task.Delay(500);
                if (failures.Count == processFailureCount) Log("  -> Success.");

                SetProgress("Step 2 of 4: Removing application binaries...", 2, 4);
                Log("Step 2/4: Removing application binaries...");
                try
                {
                    if (Directory.Exists(Program.InstallDir)) Directory.Delete(Program.InstallDir, true);
                    
                    var pf64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.AppName);
                    var pf32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Program.AppName);
                    if (Directory.Exists(pf64)) Directory.Delete(pf64, true);
                    if (Directory.Exists(pf32)) Directory.Delete(pf32, true);
                    
                    Log("  -> Success.");
                }
                catch (Exception ex)
                {
                    RecordFailure("Application binaries", ex);
                }

                SetProgress("Step 3 of 4: Removing registry and startup entries...", 3, 4);
                Log("Step 3/4: Removing the Administrator logon task, Registry keys and startup entries...");

                // ISSUE_13: verify the scheduled task is really gone. The old code fired schtasks
                // from Program.cs, ignored the exit code, and reported success either way - leaving a
                // logon task that tried to start a deleted KeyPulse.exe at every sign-in.
                try
                {
                    if (Program.TryRemoveAdminTask(out var taskError))
                    {
                        Log("  -> Administrator logon task removed (or was not present).");
                    }
                    else
                    {
                        failures.Add(taskError);
                        Log("  -> Failed: " + taskError);
                    }
                }
                catch (Exception ex)
                {
                    RecordFailure("Administrator logon task", ex);
                }

                var registrySucceeded = true;
                try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Program.AppName}", false); } catch (Exception ex) { registrySucceeded = false; RecordFailure("Uninstall registry key", ex); }
                try { Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)?.DeleteValue(Program.AppName, false); } catch (Exception ex) { registrySucceeded = false; RecordFailure("Startup registry key", ex); }
                if (registrySucceeded) Log("  -> Success.");

                SetProgress("Step 3 of 4: Purging AppData configuration...", 3.5, 4);
                Log("Step 3.5/4: Purging AppData configuration...");
                try
                {
                    // %APPDATA%\KeyPulse now holds config.json AND the logs folder (ISSUE_8), so this
                    // single delete finally takes everything KeyPulse ever wrote about the user.
                    var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
                    if (Directory.Exists(appDataDir)) Directory.Delete(appDataDir, true);

                    // ISSUE_8: builds before this one wrote keypulse_debug.txt and keypulse_crash.txt -
                    // a record of every shortcut the user ever pressed - into %LOCALAPPDATA%\KeyPulse,
                    // and no uninstaller ever removed it. Take it now.
                    if (Directory.Exists(Program.LegacyLogDir)) Directory.Delete(Program.LegacyLogDir, true);

                    Log("  -> Success.");
                }
                catch (Exception ex)
                {
                    RecordFailure("AppData configuration", ex);
                }

                SetProgress("Step 4 of 4: Removing shortcuts...", 4, 4);
                Log("Step 4/4: Removing Start Menu and Desktop shortcuts...");
                var shortcutsSucceeded = true;
                try
                {
                    var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\KeyPulse.lnk");
                    if (File.Exists(startMenu)) File.Delete(startMenu);
                }
                catch (Exception ex)
                {
                    shortcutsSucceeded = false;
                    RecordFailure("Start Menu shortcut", ex);
                }

                try
                {
                    var desktopDir = ResolveDesktopDirectory(out var desktopProbeSummary);
                    if (!string.IsNullOrWhiteSpace(desktopProbeSummary))
                    {
                        Log("  -> Desktop probe: " + desktopProbeSummary);
                    }

                    if (desktopDir != null)
                    {
                        var desktop = Path.Combine(desktopDir, "KeyPulse.lnk");
                        if (File.Exists(desktop)) File.Delete(desktop);
                    }
                }
                catch (Exception ex)
                {
                    shortcutsSucceeded = false;
                    RecordFailure("Desktop shortcut", ex);
                }
                if (shortcutsSucceeded) Log("  -> Success.");

                Log("");
                if (failures.Count == 0)
                {
                    Log("UNINSTALLATION COMPLETE.");
                }
                else
                {
                    Log("UNINSTALLATION COMPLETED WITH WARNINGS.");
                    foreach (var failure in failures)
                    {
                        Log("  - " + failure);
                    }
                }
                
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = failures.Count == 0 ? "UNINSTALLATION SUCCESSFUL" : "UNINSTALLATION COMPLETED WITH WARNINGS";
                    _headerText.Foreground = failures.Count == 0 ? AppBrush("AppSuccessSoftBrush") : AppBrush("AppWarningBrush");
                    _actionButton.Content = "Close Setup";
                    _actionButton.IsVisible = true;
                    _actionButton.Focus();
                    _actionButton.Click += (s, ev) =>
                    {
                        var selfDeleteScript = $"/c ping 127.0.0.1 -n 3 > nul & del /F /q \"{Environment.ProcessPath}\"";
                        Process.Start(new ProcessStartInfo("cmd.exe", selfDeleteScript) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                        ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                    };
                });
            }
            catch (Exception ex)
            {
                SetProgress("Uninstall failed.", 0, 4);
                Log($"\nERROR during uninstallation: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = "UNINSTALLATION FAILED";
                    _headerText.Foreground = AppBrush("AppDangerBrush");
                    _actionButton.Content = "Close Setup";
                    _actionButton.IsVisible = true;
                    _actionButton.Focus();
                    _actionButton.Click += (s, ev) =>
                    {
                        var selfDeleteScript = $"/c ping 127.0.0.1 -n 3 > nul & del /F /q \"{Environment.ProcessPath}\"";
                        Process.Start(new ProcessStartInfo("cmd.exe", selfDeleteScript) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                        ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                    };
                });
            }
        }
    }
}




