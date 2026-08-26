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
using System.Linq;
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
        private readonly bool _elevatedRetry;
        private readonly ObservableCollection<string> _logLines = new();

        private AppConfig _config = new AppConfig();
        private readonly string ConfigPath = ConfigStore.ConfigPath;

        public SetupWindow(bool isUninstall)
        {
            _isUninstall = isUninstall;
            _elevatedRetry = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--elevated-retry", StringComparison.OrdinalIgnoreCase));
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

        /// <summary>
        /// ISSUE_1 / ISSUE_2: saves ONLY this window's own geometry, read-modify-write, and never a
        /// whole settings snapshot.
        ///
        /// This method used to write `_config` - which is only ever loaded from disk inside the
        /// upgrade branch. Whenever config.json existed but KeyPulse.exe did not (the program folder
        /// deleted by hand, the exe quarantined by antivirus, or a roaming %APPDATA% arriving on a
        /// second PC where %LOCALAPPDATA% did not), setup started from a blank AppConfig and saved
        /// that blank over every shortcut the user had. On an ordinary upgrade it was subtler: the
        /// snapshot was read at the "Keep Settings" prompt, the running KeyPulse was then closed and
        /// saved its own newer state, and this overwrote it again on the way out.
        /// </summary>
        private void SaveSetupWindowGeometry()
        {
            try
            {
                var x = Position.X;
                var y = Position.Y;
                var width = Bounds.Width;
                var height = Bounds.Height;

                if (width <= 0 || height <= 0) return;

                ConfigStore.TryUpdate(c =>
                {
                    c.SetupWindowX = x;
                    c.SetupWindowY = y;
                    c.SetupWindowWidth = width;
                    c.SetupWindowHeight = height;
                }, out _);
            }
            catch { }
        }

        private void SetupWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isUninstall)
            {
                SaveSetupWindowGeometry();
                return;
            }

            var legacyDir = Program.LegacyNativeLibDir;
            var fallbackDir = Program.RuntimeFallbackDir;
            var self = Environment.ProcessPath;

            var selfDeleteScript =
                $"/c for /L %i in (1,1,30) do (ping 127.0.0.1 -n 2 > nul & rmdir /S /Q \"{legacyDir}\" & rmdir /S /Q \"{fallbackDir}\" & del /F /q \"{self}\" & if not exist \"{self}\" exit)";

            try
            {
                using var cleanup = Process.Start(new ProcessStartInfo("cmd.exe", selfDeleteScript)
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                Program.LogDebug("Uninstall self-delete could not be started: " + ex.Message);
            }
        }

        /// <summary>
        /// ISSUE_3 / ISSUE_12: the processes setup is allowed to close, and nothing else.
        ///
        /// The old loops closed EVERY process named "KeyPulse" - which a second copy of the
        /// installer also is, because it is the same binary under a different folder. Two installers
        /// launched together killed each other, and a kill landing during the binary copy left a
        /// truncated KeyPulse.exe behind. Only a process actually running the installed binary is a
        /// candidate; one we cannot inspect is assumed to be an elevated KeyPulse and is included,
        /// because that is exactly the case the uninstaller has to notice (ISSUE_5).
        /// The caller MUST dispose what it gets back.
        /// </summary>
        private static List<Process> FindInstalledInstances(out bool sawUninspectable)
        {
            var matches = new List<Process>();
            sawUninspectable = false;

            Process[] all;
            try { all = Process.GetProcessesByName(Program.AppName); }
            catch (Exception ex)
            {
                Program.LogDebug("Could not enumerate KeyPulse processes: " + ex.Message);
                return matches;
            }

            foreach (var process in all)
            {
                var keep = false;
                try
                {
                    if (process.Id != Environment.ProcessId)
                    {
                        string? path = null;
                        try { path = process.MainModule?.FileName; }
                        catch { sawUninspectable = true; keep = true; }

                        if (path != null && path.Equals(Program.ExePath, StringComparison.OrdinalIgnoreCase))
                        {
                            keep = true;
                        }
                    }
                }
                catch { }

                if (keep) matches.Add(process);
                else process.Dispose();
            }

            return matches;
        }

        /// <summary>ISSUE_5: offers to restart the uninstaller with Administrator rights.</summary>
        private async Task<bool> AskToRetryElevatedAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            Dispatcher.UIThread.Post(() =>
            {
                var previousContent = Content;

                var panel = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 14,
                    MaxWidth = 470
                };
                panel.Children.Add(new TextBlock { Text = "KeyPulse Is Running As Administrator", Classes = { "SectionTitle" }, HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new TextBlock
                {
                    Text = "KeyPulse is running with Administrator rights, so this uninstaller cannot close it. Restart the uninstaller with Administrator rights to finish, or close KeyPulse yourself from the icon next to the clock and run it again.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                });

                var retry = new Button { Content = "Restart As Administrator", Classes = { "Primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 0 };
                var skip = new Button { Content = "Continue Anyway", Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 1 };

                retry.Click += (s, e) => { Content = previousContent; tcs.TrySetResult(true); };
                skip.Click += (s, e) => { Content = previousContent; tcs.TrySetResult(false); };

                panel.Children.Add(retry);
                panel.Children.Add(skip);

                Content = new Border { Classes = { "Panel" }, Margin = new Thickness(24), Child = panel };
                retry.Focus();
            });

            return await tcs.Task;
        }

        /// <summary>
        /// Starts this same uninstaller again with a UAC prompt. `--elevated-retry` guarantees the
        /// elevated copy never offers to elevate a second time, so this cannot loop.
        /// </summary>
        private static bool RelaunchUninstallerElevated(out string error)
        {
            error = string.Empty;
            try
            {
                using var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = "--uninstall --elevated-retry",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
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

        /// <summary>
        /// ISSUE_7: the second, explicit confirmation before a wipe. It says how many shortcuts are
        /// about to be destroyed, and confirms that a dated safety copy will be written first.
        /// </summary>
        private async Task<bool> ConfirmWipeAsync()
        {
            var shortcutCount = 0;
            try
            {
                shortcutCount = ConfigStore.Load(out _, out _).Hotkeys.Count;
            }
            catch { }

            var w = new Window
            {
                Title = "Wipe every KeyPulse setting?",
                Width = 480,
                MinWidth = 420,
                MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                CanResize = false,
                Icon = this.Icon
            };

            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = "Delete all of it?", Classes = { "SectionTitle" } });
            panel.Children.Add(new TextBlock
            {
                Text = shortcutCount > 0
                    ? $"This permanently deletes ALL {shortcutCount} shortcut{(shortcutCount == 1 ? "" : "s")} you have saved, every preference, and every rollback copy."
                    : "This permanently deletes every saved preference and every rollback copy.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Before deleting, KeyPulse writes a dated safety copy of your settings next to the KeyPulse folder, so this can be undone by hand if you change your mind.",
                Classes = { "Muted" },
                TextWrapping = TextWrapping.Wrap
            });

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0)
            };
            var keep = new Button { Content = "Keep my settings", Classes = { "Primary" }, MinWidth = 140 };
            var wipe = new Button { Content = "Yes, wipe everything", Classes = { "Danger" }, MinWidth = 150 };
            buttons.Children.Add(keep);
            buttons.Children.Add(wipe);
            panel.Children.Add(buttons);

            var result = false;
            keep.Click += (s, e) => w.Close();
            wipe.Click += (s, e) => { result = true; w.Close(); };

            w.Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            w.Opened += (s, e) => keep.Focus();

            try
            {
                await w.ShowDialog(this);
            }
            catch (InvalidOperationException)
            {
                var closed = new TaskCompletionSource<object?>();
                w.Closed += (s, e) => closed.TrySetResult(null);
                w.Show();
                await closed.Task;
            }

            return result;
        }

        /// <summary>
        /// ISSUE_12: asks a running KeyPulse to exit through the named event the app actually
        /// honours (its window hides on WM_CLOSE by design, so CloseMainWindow could never work).
        /// Waits up to ~4 seconds for a clean exit, then force-kills any straggler.
        /// </summary>
        private async Task CloseRunningInstancesAsync()
        {
            Program.SignalInstancesToExit();

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var still = FindInstalledInstances(out _);
                var alive = still.Count;
                foreach (var p in still) p.Dispose();
                if (alive == 0)
                {
                    Log("  -> Closed cleanly on request.");
                    return;
                }

                await Task.Delay(500);
            }

            var stragglers = FindInstalledInstances(out _);
            foreach (var p in stragglers)
            {
                using (p)
                {
                    try
                    {
                        Log($"  -> Process {p.Id} did not exit on request; forcing.");
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        Log("  -> Failed to stop process " + p.Id + ": " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// ISSUE_19: per-user "Add to KeyPulse" entries for File Explorer's right-click menu:
        /// any file (programs, apps, shortcuts), any folder, and the background of an open folder.
        /// These are plain HKCU registrations: Windows 10 shows them directly in the menu, and
        /// Windows 11 shows them in the classic menu behind "Show more options" (Shift+right-click).
        /// A top-level entry in Windows 11's new-style menu would require a packaged COM
        /// IExplorerCommand server, which does not fit a single-file NativeAOT exe.
        /// </summary>
        private void RegisterExplorerContextMenu()
        {
            try
            {
                var command = $"\"{Program.ExePath}\" --add-target \"%1\"";
                var backgroundCommand = $"\"{Program.ExePath}\" --add-target \"%V\"";

                RegisterContextMenuShellKey(@"Software\Classes\*\shell\KeyPulse.Add", command);
                RegisterContextMenuShellKey(@"Software\Classes\Directory\shell\KeyPulse.Add", command);
                RegisterContextMenuShellKey(@"Software\Classes\Directory\Background\shell\KeyPulse.Add", backgroundCommand);
                Log("  -> \"Add to KeyPulse\" added to the right-click menu.");
            }
            catch (Exception ex)
            {
                Log("  -> Warning: could not register the right-click menu entry: " + ex.Message);
            }
        }

        private void RegisterContextMenuShellKey(string shellKeyPath, string command)
        {
            using var shellKey = Registry.CurrentUser.CreateSubKey(shellKeyPath, true);
            shellKey.SetValue(null, "Add to KeyPulse");
            shellKey.SetValue("Icon", Program.ExePath + ",0");
            using var commandKey = shellKey.CreateSubKey("command", true);
            commandKey.SetValue(null, command);
        }

        /// <summary>ISSUE_19: removes every per-user "Add to KeyPulse" menu entry.</summary>
        private void RemoveExplorerContextMenu()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\KeyPulse.Add", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\KeyPulse.Add", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\KeyPulse.Add", false);
                Log("  -> \"Add to KeyPulse\" removed from the right-click menu.");
            }
            catch (Exception ex)
            {
                Log("  -> Warning: could not remove the right-click menu entry: " + ex.Message);
            }
        }

        private async Task RunInstall()
        {
            try
            {
                SetProgress("Preparing installation...", 0, 6);
                Log("Cleaning up legacy temporary files...");
                try { DeleteMatchingFilesExceptCurrent("KeyPulse*.exe"); } catch { }
                try { DeleteMatchingFilesExceptCurrent("keypulse_*.txt"); } catch { }
                try { if (Directory.Exists(Program.LegacyNativeLibDir)) Directory.Delete(Program.LegacyNativeLibDir, true); } catch { }
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
                        btnWipe.Click += async (s, e) =>
                        {
                            // ISSUE_7: one misclick must not destroy everything. Wiping asks a
                            // second, explicit confirmation that says how many shortcuts are about
                            // to be destroyed - and a dated safety copy is written aside first, the
                            // same safety net a restore gets.
                            var confirmed = await ConfirmWipeAsync();
                            if (!confirmed)
                            {
                                Log("Wipe was canceled. Nothing was deleted.");
                                return;
                            }
                            Content = _mainGrid;
                            tcs.TrySetResult(true);
                        };

                        panel.Children.Add(btnKeep);
                        panel.Children.Add(btnWipe);

                        Content = new Border { Classes = { "Panel" }, Margin = new Thickness(24), Child = panel };
                        btnKeep.Focus();
                    });

                    bool wipe = await tcs.Task;
                    if (wipe)
                    {
                        Log("User opted for a FRESH INSTALL. Writing a safety copy, then wiping old settings and logs...");
                        var safetyCopy = ConfigStore.SaveWipeSafetyCopy();
                        if (safetyCopy != null)
                        {
                            Log("  -> Safety copy kept as: " + safetyCopy);
                        }
                        else
                        {
                            Log("  -> Warning: no safety copy could be written (there may be no existing settings).");
                        }
                        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse"), true); } catch { }
                        _config = new AppConfig();

                        Log("  -> Success.");
                    }
                    else
                    {
                        Log("User opted to UPGRADE. Keeping existing settings.");
                        Log("Validating existing configuration schema...");
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

                // ISSUE_12: a window close request can never work - the app hides to the tray by
                // design - so the old polite ask always failed, stalled a second and force-killed.
                // Ask through the named event the app honours, wait for a clean exit, and only kill
                // real stragglers.
                await CloseRunningInstancesAsync();
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

                var stagedExe = Program.ExePath + ".new";
                try { if (File.Exists(stagedExe)) File.Delete(stagedExe); } catch { }

                File.Copy(Environment.ProcessPath!, stagedExe, true);

                var stagedLength = new FileInfo(stagedExe).Length;
                var sourceLength = new FileInfo(Environment.ProcessPath!).Length;
                if (stagedLength != sourceLength)
                {
                    try { File.Delete(stagedExe); } catch { }
                    throw new IOException($"The copied program file is {stagedLength} bytes but should be {sourceLength}. Nothing was replaced.");
                }

                File.Move(stagedExe, Program.ExePath, true);
                Log("  -> Success.");

                SetProgress("Step 4 of 6: Registering with Windows...", 4, 6);
                Log("Step 4/6: Registering with Programs and Features (appwiz.cpl)...");
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

                // ISSUE_19: register the per-user "Add to KeyPulse" Explorer menu entries.
                RegisterExplorerContextMenu();

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
                        try { using var launched = Process.Start(new ProcessStartInfo { FileName = Program.ExePath, Arguments = "", UseShellExecute = true }); } catch { }
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

                // ISSUE_12: same as the upgrade path - ask through the exit event, then clean up.
                await CloseRunningInstancesAsync();
                await Task.Delay(500);

                var survivors = FindInstalledInstances(out _);
                var survivorCount = survivors.Count;
                foreach (var p in survivors) p.Dispose();

                if (survivorCount > 0)
                {
                    if (!Program.IsElevated && !_elevatedRetry)
                    {
                        Log($"  -> {survivorCount} KeyPulse process(es) could not be closed - they are running with Administrator rights.");
                        if (await AskToRetryElevatedAsync())
                        {
                            if (RelaunchUninstallerElevated(out var relaunchError))
                            {
                                Log("  -> Restarting the uninstaller with Administrator rights...");
                                Dispatcher.UIThread.Post(() => ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty));
                                return;
                            }

                            RecordFailure("Restarting with Administrator rights", new Exception(relaunchError));
                        }
                    }

                    var stuck = $"{survivorCount} running KeyPulse process(es) could not be closed. "
                              + "Right-click the KeyPulse icon next to the clock, choose \"Exit KeyPulse\", then run the uninstaller again.";
                    failures.Add(stuck);
                    Log("  -> Failed: " + stuck);
                }

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

                // ISSUE_19: take the "Add to KeyPulse" right-click entries back out.
                RemoveExplorerContextMenu();

                if (registrySucceeded) Log("  -> Success.");

                SetProgress("Step 3 of 4: Purging AppData configuration...", 3.5, 4);
                Log("Step 3.5/4: Purging AppData configuration...");
                try
                {
                    var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
                    if (Directory.Exists(appDataDir)) Directory.Delete(appDataDir, true);

                    if (Directory.Exists(Program.LegacyLogDir)) Directory.Delete(Program.LegacyLogDir, true);

                    // ISSUE_7: the dated pre-wipe safety copies live beside the folder so a wipe
                    // cannot destroy them; the uninstaller is the one that finally removes them.
                    var appDataParent = Directory.GetParent(appDataDir)?.FullName;
                    if (!string.IsNullOrEmpty(appDataParent))
                    {
                        foreach (var leftover in Directory.GetFiles(appDataParent, "KeyPulse.before-wipe-*.json"))
                        {
                            try { File.Delete(leftover); } catch { }
                        }
                    }

                    // ISSUE_19: any staged "Add to KeyPulse" hand-off file goes with the rest.
                    try { if (File.Exists(Program.StagedAddPath)) File.Delete(Program.StagedAddPath); } catch { }

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
                        using var cleanup = Process.Start(new ProcessStartInfo("cmd.exe", selfDeleteScript) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
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
                        using var cleanup = Process.Start(new ProcessStartInfo("cmd.exe", selfDeleteScript) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                        ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                    };
                });
            }
        }
    }
}


