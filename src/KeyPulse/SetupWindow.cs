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
        private bool _preserveAdminPrivilege;
        private readonly ObservableCollection<string> _logLines = new();

        private AppConfig _config = new AppConfig();
        private readonly string ConfigPath = ConfigStore.ConfigPath;

        private readonly bool _isUpdate;

        public SetupWindow(bool isUninstall, bool isUpdate = false)
        {
            _isUninstall = isUninstall;
            _isUpdate = isUpdate;
            _elevatedRetry = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--elevated-retry", StringComparison.OrdinalIgnoreCase));
            Title = isUninstall ? "KeyPulse Setup - Uninstalling" : (isUpdate ? "KeyPulse - Updating" : "KeyPulse Setup - Installing");
            Width = 550;
            Height = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            LoadConfig();

            _headerText = new TextBlock
            {
                Text = isUninstall ? "Processing Uninstallation..." : (isUpdate ? "Applying KeyPulse Update..." : "Processing Installation..."),
                Margin = new Thickness(15, 15, 15, 5),
                Classes = { "SectionTitle" }
            };

            _progressText = new TextBlock
            {
                Text = isUninstall ? "Preparing uninstall..." : (isUpdate ? "Preparing update..." : "Preparing install..."),
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

            try { if (Directory.Exists(legacyDir)) Directory.Delete(legacyDir, true); } catch { }
            try { if (Directory.Exists(fallbackDir)) Directory.Delete(fallbackDir, true); } catch { }

            if (!string.IsNullOrEmpty(self) && File.Exists(self))
            {
                try
                {
                    MoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Uninstall MoveFileEx cleanup failed: " + ex.Message);
                }
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

        /// <summary>ISSUE_5: offers to restart setup with Administrator rights.</summary>
        private async Task<bool> AskToRetryElevatedAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            Dispatcher.UIThread.Post(() =>
            {
                var previousContent = Content;
                var opName = _isUninstall ? "uninstaller" : (_isUpdate ? "updater" : "installer");

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
                    Text = $"KeyPulse is running with Administrator rights, so this {opName} cannot close it. Restart the {opName} with Administrator rights to finish, or close KeyPulse yourself from the icon next to the clock and run it again.",
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
        /// Starts this setup worker again with a UAC prompt. `--elevated-retry` guarantees the
        /// elevated copy never offers to elevate a second time, so this cannot loop.
        /// </summary>
        private static bool RelaunchSetupElevated(string arguments, out string error)
        {
            error = string.Empty;
            try
            {
                using var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = arguments,
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

        private static bool RelaunchUninstallerElevated(out string error) =>
            RelaunchSetupElevated("--uninstall --elevated-retry", out error);

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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

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

            var defaultDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            try { Directory.CreateDirectory(defaultDesktop); } catch { }
            probeSummary = string.Join("; ", probed) + "; Fallback: " + defaultDesktop;
            return defaultDesktop;
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


        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In] ref Guid riid,
            out IntPtr ppv);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        private static bool TryCreateShortcutViaComVTable(string shortcutPath, string targetPath, string workingDir, string description, string iconLocation)
        {
            IntPtr pShellLink = IntPtr.Zero;
            IntPtr pPersistFile = IntPtr.Zero;
            bool coInit = false;
            try
            {
                int hrInit = CoInitializeEx(IntPtr.Zero, 2 /* COINIT_APARTMENTTHREADED */);
                coInit = (hrInit == 0 || hrInit == 1);

                var clsidShellLink = new Guid("00021401-0000-0000-C000-000000000046");
                var iidShellLink = new Guid("000214F9-0000-0000-C000-000000000046");
                var iidPersistFile = new Guid("0000010b-0000-0000-C000-000000000046");

                int hr = CoCreateInstance(ref clsidShellLink, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iidShellLink, out pShellLink);
                if (hr != 0 || pShellLink == IntPtr.Zero) return false;

                unsafe
                {
                    void** vtableLink = *(void***)pShellLink;

                    var setPath = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtableLink[20];
                    fixed (char* pTarget = targetPath) { setPath(pShellLink, pTarget); }

                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        var setWorkingDir = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtableLink[9];
                        fixed (char* pDir = workingDir) { setWorkingDir(pShellLink, pDir); }
                    }

                    if (!string.IsNullOrEmpty(description))
                    {
                        var setDescription = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtableLink[7];
                        fixed (char* pDesc = description) { setDescription(pShellLink, pDesc); }
                    }

                    if (!string.IsNullOrEmpty(iconLocation))
                    {
                        var setIconLocation = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtableLink[17];
                        fixed (char* pIcon = iconLocation) { setIconLocation(pShellLink, pIcon, 0); }
                    }

                    var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtableLink[0];
                    hr = queryInterface(pShellLink, &iidPersistFile, &pPersistFile);
                    if (hr != 0 || pPersistFile == IntPtr.Zero) return false;

                    void** vtablePersist = *(void***)pPersistFile;

                    var save = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtablePersist[6];
                    fixed (char* pFile = shortcutPath) { hr = save(pPersistFile, pFile, 0 /* FALSE */); }

                    return hr == 0 && File.Exists(shortcutPath);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pPersistFile != IntPtr.Zero)
                {
                    unsafe
                    {
                        void** vtablePersist = *(void***)pPersistFile;
                        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtablePersist[2];
                        release(pPersistFile);
                    }
                }
                if (pShellLink != IntPtr.Zero)
                {
                    unsafe
                    {
                        void** vtableLink = *(void***)pShellLink;
                        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtableLink[2];
                        release(pShellLink);
                    }
                }
                if (coInit)
                {
                    try { CoUninitialize(); } catch { }
                }
            }
        }

        internal static bool CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description, string iconLocation, out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                var dir = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (TryCreateShortcutViaComVTable(shortcutPath, targetPath, workingDir, description, iconLocation))
                {
                    return true;
                }


                errorMessage = "Native Windows Shell COM shortcut creation failed.";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private async Task<List<string>> CreateShortcutsAsync(string startMenuShortcut, string? desktopShortcut, bool desktopShortcutRequested = true)
        {
            var warnings = new List<string>();
            Directory.CreateDirectory(Path.GetDirectoryName(startMenuShortcut)!);
            if (desktopShortcut != null) Directory.CreateDirectory(Path.GetDirectoryName(desktopShortcut)!);

            await Task.Run(() =>
            {
                if (!CreateShortcut(startMenuShortcut, Program.ExePath, Program.InstallDir, "KeyPulse - Global Hotkey & Shortcut Manager", Program.ExePath, out var errStart))
                {
                    warnings.Add($"Start Menu shortcut creation failed: {errStart ?? "Shortcut was not created."}");
                }

                if (desktopShortcut != null)
                {
                    if (!CreateShortcut(desktopShortcut, Program.ExePath, Program.InstallDir, "KeyPulse - Global Hotkey & Shortcut Manager", Program.ExePath, out var errDesk))
                    {
                        warnings.Add($"Desktop shortcut creation failed: {errDesk ?? "Shortcut was not created."}");
                    }
                }
                else if (desktopShortcutRequested)
                {
                    warnings.Add("Desktop shortcut skipped because no writable Desktop folder could be resolved.");
                }

                try
                {
                    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
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
            var layoutCount = 0;
            var brightnessCount = 0;
            try
            {
                shortcutCount = ConfigStore.Load(out _, out _).Hotkeys.Count;
            }
            catch { }

            try
            {
                var desktopConfig = DesktopOrganizerService.LoadConfig();
                layoutCount = desktopConfig?.Profiles?.Count ?? 0;
            }
            catch { }

            try
            {
                var sbConfig = ScreenBrightnessService.LoadConfig();
                brightnessCount = sbConfig?.Profiles?.Count ?? 0;
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

            var details = new List<string>();
            if (shortcutCount > 0) details.Add($"{shortcutCount} saved shortcut{(shortcutCount == 1 ? "" : "s")}");
            if (layoutCount > 0) details.Add($"{layoutCount} Desktop Organizer icon layout profile{(layoutCount == 1 ? "" : "s")}");
            if (brightnessCount > 0) details.Add($"{brightnessCount} Screen Brightness preset{(brightnessCount == 1 ? "" : "s")}");

            string deleteMsg = details.Count > 0
                ? $"This permanently deletes {string.Join(", ", details)}, every preference, and every rollback copy."
                : "This permanently deletes every saved preference, desktop layout profile, screen brightness preset, and rollback copy.";

            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = "Delete all of it?", Classes = { "SectionTitle" } });
            panel.Children.Add(new TextBlock
            {
                Text = deleteMsg,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Before deleting, KeyPulse writes dated safety copies of your shortcuts, desktop layout profiles, and screen brightness presets next to the KeyPulse folder, so this can be undone by hand if you change your mind.",
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
            w.KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape)
                {
                    e.Handled = true;
                    w.Close();
                }
            };

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
            var backupExe = Program.ExePath + ".bak";
            bool hadPreviousExe = File.Exists(Program.ExePath);

            try
            {
                SetProgress("Preparing installation...", 0, 6);
                Log("Cleaning up legacy temporary files...");
                try { DeleteMatchingFilesExceptCurrent("KeyPulse*.exe"); } catch { }
                try { DeleteMatchingFilesExceptCurrent("keypulse_*.txt"); } catch { }
                try { if (Directory.Exists(Program.LegacyNativeLibDir)) Directory.Delete(Program.LegacyNativeLibDir, true); } catch { }
                try { if (Directory.Exists(Program.LegacyLogDir)) Directory.Delete(Program.LegacyLogDir, true); } catch { }
                try
                {
                    var tempDir = Path.GetTempPath();
                    var myProc = Environment.ProcessPath ?? string.Empty;
                    foreach (var f in Directory.GetFiles(tempDir, "KeyPulse_Update_*"))
                    {
                        if (!string.Equals(f, myProc, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                    var updatesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.IsDevMode ? "KeyPulse_DEV" : "KeyPulse", "updates");
                    if (Directory.Exists(updatesDir))
                    {
                        foreach (var f in Directory.GetFiles(updatesDir, "KeyPulse_Update_*"))
                        {
                            if (!string.Equals(f, myProc, StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                    }
                } catch { }
                var installWarnings = new List<string>();

                bool wasRunningAsAdmin = false;
                if (Environment.GetCommandLineArgs().Contains("--was-elevated") || Program.IsElevated)
                {
                    wasRunningAsAdmin = true;
                }
                var currentInstances = FindInstalledInstances(out bool uninspectableFound);
                if (uninspectableFound)
                {
                    wasRunningAsAdmin = true;
                }
                else if (currentInstances.Count > 0)
                {
                    foreach (var p in currentInstances)
                    {
                        if (Program.IsProcessElevated(p))
                        {
                            wasRunningAsAdmin = true;
                            break;
                        }
                    }
                }
                else if (!wasRunningAsAdmin)
                {
                    wasRunningAsAdmin = Program.GetAdminTaskState() == Program.AdminTaskState.Enabled;
                }
                foreach (var p in currentInstances) p.Dispose();

                bool isUpgrade = File.Exists(Program.ExePath);
                if (isUpgrade)
                {
                    string installedVer = "unknown";
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(Program.ExePath);
                        if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                        {
                            installedVer = vi.FileVersion.EndsWith(".0", StringComparison.Ordinal) && vi.FileVersion.Count(c => c == '.') == 3
                                ? vi.FileVersion.Substring(0, vi.FileVersion.Length - 2)
                                : vi.FileVersion;
                        }
                    }
                    catch { }

                    var currentVer = Program.AppVersion;
                    var cmp = UpdateChecker.CompareVersions(currentVer, installedVer);
                    bool isDowngrade = cmp < 0 && installedVer != "unknown";

                    bool wipe = false;
                    if (!_isUpdate || isDowngrade)
                    {
                        SetProgress(isDowngrade ? "Warning: Downgrade detected..." : "Waiting for upgrade choice...", 0, 6);
                        var tcs = new TaskCompletionSource<bool>();
                        Dispatcher.UIThread.Post(() =>
                        {
                            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 14, MaxWidth = 490 };
                            panel.Children.Add(new TextBlock { Text = isDowngrade ? "Downgrade Warning Detected" : "Existing Installation Detected", Classes = { "SectionTitle" }, HorizontalAlignment = HorizontalAlignment.Center });

                            if (isDowngrade)
                            {
                                panel.Children.Add(new TextBlock
                                {
                                    Text = $"⚠️ You are attempting to install version v{currentVer}, but a newer version (v{installedVer}) is already installed on this PC.\nDowngrading can cause settings incompatibility, broken shortcuts, or data corruption.",
                                    Classes = { "ErrorText" },
                                    TextWrapping = TextWrapping.Wrap,
                                    TextAlignment = TextAlignment.Center
                                });
                            }
                            else
                            {
                                panel.Children.Add(new TextBlock { Text = $"Installed version: v{installedVer}  →  New version: v{currentVer}\nKeep your existing shortcuts and settings, or wipe them for a fresh install.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                            }

                            panel.Children.Add(new TextBlock { Text = "Wiping settings deletes saved shortcuts and app preferences.", Classes = { "ErrorText" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });

                            var btnKeep = new Button { Content = isDowngrade ? "Proceed with Downgrade (Keep Settings)" : "Keep Settings & Upgrade", Classes = { isDowngrade ? "Danger" : "Primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 0 };
                            var btnWipe = new Button { Content = isDowngrade ? "Wipe Settings & Install Older Version" : "Wipe Settings", Classes = { "Danger" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 1 };
                            var btnCancel = new Button { Content = "Cancel Installation (Recommended)", Classes = { "Primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TabIndex = 2, IsVisible = isDowngrade };

                            btnKeep.Click += (s, e) => { Content = _mainGrid; tcs.TrySetResult(false); };
                            btnCancel.Click += (s, e) =>
                            {
                                Log("Installation was cancelled by user due to downgrade warning.");
                                ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                            };
                            btnWipe.Click += async (s, e) =>
                            {
                                var confirmed = await ConfirmWipeAsync();
                                if (!confirmed)
                                {
                                    Log("Wipe was canceled. Nothing was deleted.");
                                    return;
                                }
                                Content = _mainGrid;
                                tcs.TrySetResult(true);
                            };

                            if (isDowngrade)
                            {
                                panel.Children.Add(btnCancel);
                            }
                            panel.Children.Add(btnKeep);
                            panel.Children.Add(btnWipe);

                            Content = new Border { Classes = { "Panel" }, Margin = new Thickness(24), Child = panel };
                            if (isDowngrade) btnCancel.Focus(); else btnKeep.Focus();
                        });

                        wipe = await tcs.Task;
                    }
                    else
                    {
                        Log("In-app update detected. Automatically keeping settings and shortcuts.");
                    }
                    if (wipe)
                    {
                        _preserveAdminPrivilege = false;
                        Log("User opted for a FRESH INSTALL. Terminating running instances before wiping...");

                        SetProgress("Terminating running instances...", 1, 6);
                        await CloseRunningInstancesAsync();
                        await Task.Delay(500);

                        var wipeSurvivors = FindInstalledInstances(out _);
                        foreach (var p in wipeSurvivors)
                        {
                            try { p.WaitForExit(3000); } catch { }
                            p.Dispose();
                        }

                        Log("Writing safety copies of configuration and desktop layouts...");
                        if (!ConfigStore.SaveAllWipeSafetyCopies(out var safetyCopies, out var copyError))
                        {
                            Log("  -> ERROR: Failed to create safety copies: " + copyError);
                            Log("  -> ABORTING wipe to protect existing user data.");
                            installWarnings.Add("Settings wipe was aborted because safety copies could not be created (" + copyError + "). Your existing configuration was preserved.");
                            wipe = false;
                            _config = ConfigStore.Load(out _, out _);
                        }
                        else
                        {
                            foreach (var sc in safetyCopies)
                            {
                                Log("  -> Safety copy kept as: " + sc);
                            }

                            try
                            {
                                var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
                                if (Directory.Exists(appDataDir))
                                {
                                    Directory.Delete(appDataDir, true);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("  -> Warning while clearing AppData directory: " + ex.Message);
                            }
                            _config = new AppConfig();
                            Log("  -> Wipe completed safely.");
                        }
                    }

                    if (!wipe)
                    {
                        _preserveAdminPrivilege = wasRunningAsAdmin;
                        Log("User opted to UPGRADE. Keeping existing settings.");
                        Log(wasRunningAsAdmin
                            ? "  -> Detected previous privilege state: Administrator (will preserve)."
                            : "  -> Detected previous privilege state: Standard User (will preserve).");
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
                            var args = _isUpdate ? "--update-worker --elevated-retry" : "--install-worker --elevated-retry";
                            if (RelaunchSetupElevated(args, out var relaunchError))
                            {
                                Log("  -> Restarting setup with Administrator rights...");
                                Dispatcher.UIThread.Post(() => ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty));
                                return;
                            }
                            Log("  -> Warning: Failed to restart with Administrator rights: " + relaunchError);
                        }
                    }

                    var stuck = $"{survivorCount} running KeyPulse process(es) could not be closed. Please close KeyPulse from the system tray before proceeding.";
                    installWarnings.Add(stuck);
                    Log("  -> Warning: " + stuck);
                }
                else
                {
                    Log("  -> Success.");
                }

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

                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] sourceHash, stagedHash;
                    using (var sf = File.OpenRead(Environment.ProcessPath!)) sourceHash = sha.ComputeHash(sf);
                    using (var tf = File.OpenRead(stagedExe)) stagedHash = sha.ComputeHash(tf);
                    if (!sourceHash.SequenceEqual(stagedHash))
                    {
                        try { File.Delete(stagedExe); } catch { }
                        throw new IOException("Cryptographic SHA-256 verification failed on the staged binary. Nothing was replaced.");
                    }
                }

                if (hadPreviousExe)
                {
                    try { File.Copy(Program.ExePath, backupExe, true); }
                    catch (Exception bEx) { Log("  -> Warning: could not create pre-install backup: " + bEx.Message); }

                    var oldExe = Program.ExePath + ".old";
                    try
                    {
                        if (File.Exists(oldExe)) File.Delete(oldExe);
                        File.Move(Program.ExePath, oldExe, true);
                        MoveFileEx(oldExe, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                    catch (Exception mEx)
                    {
                        Log("  -> Warning: could not rename current executable to .old: " + mEx.Message);
                    }
                }

                File.Move(stagedExe, Program.ExePath, true);
                Log("  -> Success.");

                if (isUpgrade && hadPreviousExe)
                {
                    try
                    {
                        File.WriteAllText(Program.CanaryFilePath, $"{Program.AppVersion}|{DateTime.UtcNow:O}|{backupExe}|0");
                    }
                    catch { }
                }

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
                bool shouldCreateDesktop = true;
                if ((_isUpdate || isUpgrade) && desktop != null)
                {
                    if (File.Exists(desktop))
                    {
                        Log("  -> Existing desktop shortcut detected; updating target path and properties.");
                    }
                    else
                    {
                        Log("  -> Upgrade/Update detected and desktop shortcut does not exist. Skipping creation to preserve clean desktop.");
                        shouldCreateDesktop = false;
                        desktop = null;
                    }
                }
                var shortcutWarnings = await CreateShortcutsAsync(startMenu, desktop, desktopShortcutRequested: shouldCreateDesktop);
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
                Log("Step 6/6: Updating startup and privilege settings...");
                try
                {
                    if (_preserveAdminPrivilege)
                    {
                        if (Program.CreateOrUpdateAdminTask(enable: true, out var taskError))
                        {
                            Log("  -> Preserved Administrator privileges (logon task configured with battery restrictions disabled).");
                            try
                            {
                                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                                runKey?.DeleteValue(Program.AppName, false);
                                var vbs = Path.Combine(Program.InstallDir, "boot.vbs");
                                if (File.Exists(vbs)) File.Delete(vbs);
                            }
                            catch { }
                        }
                        else
                        {
                            Log("  -> Warning: Could not configure Administrator task: " + taskError);
                            installWarnings.Add("Administrator logon task could not be configured: " + taskError);
                        }
                    }
                    else
                    {
                        if (Program.GetAdminTaskState() != Program.AdminTaskState.NotPresent)
                        {
                            Program.TryRemoveAdminTask(out _);
                            Log("  -> Preserved Standard User privileges (removed Administrator task).");
                        }

                        if (!isUpgrade || Program.IsStartupEnabled() || _config.LaunchOnBoot)
                        {
                            if (Program.TrySetRegistryStartup(true, out var startupError))
                            {
                                Log("  -> Start with Windows configured for Standard User.");
                            }
                            else
                            {
                                Log("  -> Warning: " + startupError);
                                installWarnings.Add("Start with Windows could not be applied: " + startupError);
                            }
                        }
                        else
                        {
                            Log("  -> Start with Windows is off. Skipping.");
                        }
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
                
                void LaunchAndExit()
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Program.ExePath,
                        Arguments = "",
                        UseShellExecute = true
                    };
                    if (_preserveAdminPrivilege)
                    {
                        psi.Verb = "runas";
                    }

                    try
                    {
                        using var launched = Process.Start(psi);
                    }
                    catch
                    {
                        if (_preserveAdminPrivilege)
                        {
                            psi.Verb = "";
                            try { using var fallback = Process.Start(psi); } catch { }
                        }
                    }
                    ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _headerText.Text = installWarnings.Count == 0 ? (_isUpdate ? "UPDATE SUCCESSFUL - RESTARTING..." : "INSTALLATION SUCCESSFUL") : "COMPLETED WITH WARNINGS";
                    _headerText.Foreground = installWarnings.Count == 0 ? AppBrush("AppSuccessSoftBrush") : AppBrush("AppWarningBrush");
                    _actionButton.Content = _isUpdate ? "Restarting KeyPulse..." : "Launch KeyPulse & Close";
                    _actionButton.IsVisible = true;
                    _actionButton.Focus();
                    _actionButton.Click += (s, ev) => LaunchAndExit();

                    if (_isUpdate && installWarnings.Count == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(800);
                            Dispatcher.UIThread.Post(LaunchAndExit);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                if (hadPreviousExe && File.Exists(backupExe))
                {
                    try
                    {
                        if (File.Exists(Program.ExePath))
                        {
                            var failedExe = Program.ExePath + ".failed-" + Guid.NewGuid().ToString("N")[..8];
                            try { File.Move(Program.ExePath, failedExe, true); } catch { }
                        }
                        File.Move(backupExe, Program.ExePath, true);
                        Log("  -> Restored previous working application executable from backup.");
                        if (File.Exists(Program.CanaryFilePath)) File.Delete(Program.CanaryFilePath);
                    }
                    catch (Exception restoreEx)
                    {
                        Log("  -> CRITICAL: Failed to restore previous application backup: " + restoreEx.Message);
                    }
                }

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
                try { using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); runKey?.DeleteValue(Program.AppName, false); } catch (Exception ex) { registrySucceeded = false; RecordFailure("Startup registry key", ex); }

                RemoveExplorerContextMenu();

                if (registrySucceeded) Log("  -> Success.");

                SetProgress("Step 3 of 4: Purging AppData configuration...", 3.5, 4);
                Log("Step 3.5/4: Purging AppData configuration...");
                try
                {
                    var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
                    if (Directory.Exists(appDataDir)) Directory.Delete(appDataDir, true);

                    if (Directory.Exists(Program.LegacyLogDir)) Directory.Delete(Program.LegacyLogDir, true);

                    var appDataParent = Directory.GetParent(appDataDir)?.FullName;
                    if (!string.IsNullOrEmpty(appDataParent))
                    {
                        foreach (var pattern in new[] { "KeyPulse.before-wipe-*.json", "KeyPulse.config.lkg.before-wipe-*.json", "KeyPulse.desktop_organizer.before-wipe-*.json", "desktop_organizer.before-wipe-*.json", "KeyPulse.screen_brightness.before-wipe-*.json" })
                        {
                            foreach (var leftover in Directory.GetFiles(appDataParent, pattern))
                            {
                                try { File.Delete(leftover); } catch { }
                            }
                        }
                    }

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
                        ((App)Application.Current!).Exit_Clicked(null, EventArgs.Empty);
                    };
                });
            }
        }
    }
}


