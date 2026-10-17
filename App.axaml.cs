using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace KeyPulse;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public bool IsExiting { get; private set; }

    public static MainWindow? HiddenWindow;

    /// <summary>ISSUE_21: honour the user's Light / Dark / System choice.</summary>
    public static void ApplyTheme(string? theme)
    {
        var app = Current;
        if (app == null) return;

        app.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    /// <summary>
    /// ISSUE_7: the tray tooltip is the only surface a user sees when KeyPulse starts hidden, so it
    /// has to say when shortcuts are not working.
    /// </summary>
    public static void UpdateTrayStatus(int active, int inactive)
    {
        try
        {
            var current = Current;
            if (current == null) return;

            var icons = TrayIcon.GetIcons(current);
            if (icons == null || icons.Count == 0) return;

            if (!icons[0].IsVisible) icons[0].IsVisible = true;

            icons[0].ToolTipText = inactive > 0
                ? $"KeyPulse - {active} working, {inactive} NOT working (click to fix)"
                : $"KeyPulse - {active} shortcut{(active == 1 ? "" : "s")} working";
        }
        catch
        {
        }
    }

    private static List<HotkeyEntry>? _lastQuickActionHotkeys;
    private static bool _brightnessHookRegistered;

    public static void UpdateTrayQuickActions(IEnumerable<HotkeyEntry>? hotkeys)
    {
        try
        {
            var current = Current as App;
            if (current == null) return;

            if (!_brightnessHookRegistered)
            {
                _brightnessHookRegistered = true;
                ScreenBrightnessService.StateChanged += () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateTrayQuickActions(null));
                };
            }

            if (hotkeys != null)
            {
                _lastQuickActionHotkeys = new List<HotkeyEntry>(hotkeys);
            }

            var icons = TrayIcon.GetIcons(current);
            if (icons == null || icons.Count == 0) return;

            var trayIcon = icons[0];
            if (trayIcon.Menu == null)
            {
                trayIcon.Menu = new NativeMenu();
            }

            trayIcon.Menu.Items.Clear();

            var brightnessSubMenu = new NativeMenu();
            var sbConfig = ScreenBrightnessService.LoadConfig();
            var sbQuickProfiles = ScreenBrightnessService.GetTrayQuickActionProfiles(sbConfig);

            if (sbQuickProfiles.Count > 0)
            {
                brightnessSubMenu.Items.Add(new NativeMenuItem("QUICK ACTIONS") { IsEnabled = false });
                foreach (var profile in sbQuickProfiles)
                {
                    var capturedProfile = profile;
                    bool isCurrent = profile.Id == sbConfig.LastCycleProfileId;
                    string prefix = isCurrent ? "✓ " : "  ";
                    var pItem = new NativeMenuItem(prefix + ScreenBrightnessService.FormatProfileSummary(capturedProfile, sbConfig));
                    pItem.Click += (s, e) => ScreenBrightnessService.ApplyProfileAsync(capturedProfile);
                    brightnessSubMenu.Items.Add(pItem);
                }
                brightnessSubMenu.Items.Add(new NativeMenuItemSeparator());
            }

            var quickSlidersItem = new NativeMenuItem("Quick Sliders (Overlay)");
            quickSlidersItem.Click += current.ScreenBrightnessOverlay_Clicked;
            brightnessSubMenu.Items.Add(quickSlidersItem);

            var openBrightnessItem = new NativeMenuItem("Open Screen Brightness");
            openBrightnessItem.Click += current.ScreenBrightnessWindow_Clicked;
            brightnessSubMenu.Items.Add(openBrightnessItem);

            var brightnessRootItem = new NativeMenuItem("Screen Brightness")
            {
                Menu = brightnessSubMenu
            };
            trayIcon.Menu.Items.Add(brightnessRootItem);
            trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());

            var orgItem = new NativeMenuItem("Desktop Organizer");
            orgItem.Click += current.DesktopOrganizer_Clicked;
            trayIcon.Menu.Items.Add(orgItem);
            trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());

            var quickActions = (_lastQuickActionHotkeys ?? Enumerable.Empty<HotkeyEntry>())
                .Where(h => h.AddToQuickActionMenu && h.IsEnabled)
                .ToList();

            if (quickActions.Count > 0)
            {
                var headerItem = new NativeMenuItem("QUICK ACTIONS") { IsEnabled = false };
                trayIcon.Menu.Items.Add(headerItem);

                foreach (var entry in quickActions)
                {
                    var title = "  " + FormatQuickActionTitle(entry);
                    var item = new NativeMenuItem(title);
                    var captured = entry;
                    item.Click += (s, e) => ExecuteQuickAction(captured);
                    trayIcon.Menu.Items.Add(item);
                }

                trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());
            }

            var openItem = new NativeMenuItem("Open KeyPulse");
            openItem.Click += current.OpenKeybinds_Clicked;
            trayIcon.Menu.Items.Add(openItem);

            var settingsItem = new NativeMenuItem("Settings");
            settingsItem.Click += current.OpenSettings_Clicked;
            trayIcon.Menu.Items.Add(settingsItem);

            trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Exit KeyPulse");
            exitItem.Click += current.Exit_Clicked;
            trayIcon.Menu.Items.Add(exitItem);
        }
        catch (Exception ex)
        {
            KeyPulse.Program.LogCrash("UpdateTrayQuickActions failed: " + ex);
        }
    }

    public static void ExecuteQuickAction(HotkeyEntry entry)
    {
        try
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mw = desktop.MainWindow as MainWindow ?? HiddenWindow;
                if (mw != null)
                {
                    if (entry.Action == ActionType.TypeText || entry.Action == ActionType.InsertText)
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(180);
                            mw.ExecuteAction(entry);
                        });
                    }
                    else
                    {
                        Task.Run(() => mw.ExecuteAction(entry));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            KeyPulse.Program.LogCrash($"ExecuteQuickAction failed for {entry.ActionTitle}: {ex}");
        }
    }

    private static string FormatQuickActionTitle(HotkeyEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            var comboHint = entry.KeyCombinationsDisplay;
            return !string.IsNullOrWhiteSpace(comboHint) && comboHint != "No hotkey (Manual)"
                ? $"{entry.DisplayName} ({comboHint})"
                : entry.DisplayName;
        }

        if (entry.IsTargetObfuscated)
        {
            var comboHint = entry.KeyCombinationsDisplay;
            return string.IsNullOrWhiteSpace(comboHint) || comboHint == "No hotkey (Manual)"
                ? $"{entry.ActionDisplay} (Protected)"
                : $"{entry.ActionDisplay} ({comboHint})";
        }

        string actionDesc;
        switch (entry.Action)
        {
            case ActionType.OpenFolder:
            {
                var folder = (entry.Target ?? string.Empty).Trim().Trim('"').TrimEnd('\\', '/');
                var name = Path.GetFileName(folder);
                if (string.IsNullOrWhiteSpace(name)) name = folder;
                actionDesc = string.IsNullOrWhiteSpace(name) ? "Open Folder" : $"Open {name}";
                break;
            }
            case ActionType.LaunchProgram:
            {
                var raw = (entry.Target ?? string.Empty).Trim().Trim('"');
                var name = string.Empty;
                try
                {
                    if (MainWindow.TryResolveLaunchTarget(raw, out var resolvedFile, out _, out _))
                    {
                        name = Path.GetFileNameWithoutExtension(resolvedFile);
                    }
                    else
                    {
                        var firstToken = raw.Split(' ')[0].Trim('"');
                        name = Path.GetFileNameWithoutExtension(firstToken);
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(name)) name = "Program";
                actionDesc = $"Launch {name}";
                break;
            }
            case ActionType.BrowseChrome:
            {
                var url = (entry.Target ?? string.Empty).Trim();
                string displayUrl = url;
                try
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        displayUrl = uri.Host + (uri.PathAndQuery.Length > 1 && uri.PathAndQuery != "/" ? uri.PathAndQuery : "");
                    }
                }
                catch { }

                if (displayUrl.Length > 28) displayUrl = displayUrl.Substring(0, 25) + "...";
                actionDesc = string.IsNullOrWhiteSpace(displayUrl) ? "Open Web Link" : $"Open {displayUrl}";
                break;
            }
            case ActionType.TypeText:
            case ActionType.InsertText:
            {
                var snippet = (entry.Target ?? string.Empty).Trim().Replace("\r\n", " ").Replace("\n", " ");
                if (snippet.Length > 24) snippet = snippet.Substring(0, 21) + "...";
                var verb = entry.Action == ActionType.TypeText ? "Type" : "Paste";
                actionDesc = string.IsNullOrWhiteSpace(snippet) ? $"{verb} text" : $"{verb} \"{snippet}\"";
                break;
            }
            default:
                actionDesc = entry.ActionDisplay;
                break;
        }

        var combosDisplay = entry.KeyCombinationsDisplay;
        return string.IsNullOrWhiteSpace(combosDisplay) || combosDisplay == "No hotkey (Manual)"
            ? actionDesc
            : $"{actionDesc} ({combosDisplay})";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        KeyPulse.Program.LogDebug("OnFrameworkInitializationCompleted started");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = System.Environment.GetCommandLineArgs();
            KeyPulse.Program.LogDebug("Args: " + string.Join(", ", args));

            if (System.Linq.Enumerable.Contains(args, "--uninstall"))
            {
                desktop.MainWindow = new SetupWindow(true);
                KeyPulse.Program.LogDebug("Set SetupWindow(uninstall)");
            }
            else if (System.Linq.Enumerable.Contains(args, "--install-worker"))
            {
                desktop.MainWindow = new SetupWindow(false);
                KeyPulse.Program.LogDebug("Set SetupWindow(install)");
            }
            else
            {
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

                var startHidden = System.Linq.Enumerable.Contains(args, "--hidden");
                var mw = new MainWindow(startHidden);
                if (startHidden) HiddenWindow = mw;
                desktop.MainWindow = mw;

                Program.StartExitRequestListener(() =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Exit_Clicked(null, System.EventArgs.Empty));
                });

                Program.StartOpenWindowRequestListener(() =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        OpenKeybinds_Clicked(null, System.EventArgs.Empty);
                        mw.ConsumeStagedAdd();
                    });
                });

                IpcService.StartServer(target =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        OpenKeybinds_Clicked(null, System.EventArgs.Empty);
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            mw.StageExternalAdd(target);
                        }
                    });
                });

                if (Program.PendingAddTarget != null)
                {
                    var staged = Program.PendingAddTarget;
                    Program.PendingAddTarget = null;
                    mw.StageExternalAdd(staged);
                }

                DesktopOrganizerService.EnsureAutoSnapshotScheduler();
                KeyPulse.Program.LogDebug("Set MainWindow");
            }
        }
        else
        {
            KeyPulse.Program.LogDebug("Not IClassicDesktopStyleApplicationLifetime");
        }

        base.OnFrameworkInitializationCompleted();
        KeyPulse.Program.LogDebug("OnFrameworkInitializationCompleted finished");
    }

    public void OpenKeybinds_Clicked(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mw = desktop.MainWindow as MainWindow ?? HiddenWindow;
            if (mw != null)
            {
                if (desktop.MainWindow == null) desktop.MainWindow = mw;
                mw.BringToFront();
            }
        }
    }

    public void ScreenBrightnessOverlay_Clicked(object? sender, System.EventArgs e)
    {
        ScreenBrightnessOverlayWindow.ShowOverlay();
    }

    public void ScreenBrightnessWindow_Clicked(object? sender, System.EventArgs e)
    {
        ScreenBrightnessWindow.OpenWindow(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null);
    }

    public void DesktopOrganizer_Clicked(object? sender, System.EventArgs e)
    {
        DesktopOrganizerWindow.OpenOrganizer(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null);
    }

    public void OpenSettings_Clicked(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && (desktop.MainWindow as MainWindow ?? HiddenWindow) is MainWindow mw)
        {
            mw.Settings_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        }
    }

    public void Exit_Clicked(object? sender, System.EventArgs e)
    {
        IsExiting = true;
        IpcService.StopServer();
        InputSimulator.CancelTyping();
        HotkeyManager.DisableCaptureHook();
        Program.StopOpenWindowRequestListener();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
