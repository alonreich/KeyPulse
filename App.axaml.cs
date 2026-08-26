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

            icons[0].ToolTipText = inactive > 0
                ? $"KeyPulse - {active} working, {inactive} NOT working (click to fix)"
                : $"KeyPulse - {active} shortcut{(active == 1 ? "" : "s")} working";
        }
        catch
        {
        }
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

                // ISSUE_12: the installer asks a running KeyPulse to shut down through this event.
                // A window close request is deliberately ignored by MainWindow (it hides instead),
                // so this is the one "please exit now" channel the app actually honours.
                Program.StartExitRequestListener(() =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Exit_Clicked(null, System.EventArgs.Empty));
                });

                Program.StartOpenWindowRequestListener(() =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        OpenKeybinds_Clicked(null, System.EventArgs.Empty);
                        // ISSUE_19: if Explorer's "Add to KeyPulse" staged a target for us, load it
                        // into the editor now that the window is coming forward.
                        mw.ConsumeStagedAdd();
                    });
                });

                // ISSUE_19: launched directly from the Explorer context menu - stage immediately.
                if (Program.PendingAddTarget != null)
                {
                    var staged = Program.PendingAddTarget;
                    Program.PendingAddTarget = null;
                    mw.StageExternalAdd(staged);
                }

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
                // ISSUE_32: BringToFront marks the user request BEFORE Show(), so the first
                // open after a --hidden boot can no longer be re-hidden by MainWindow_Opened
                // (the half-second flash-then-close). It also leaves a maximized window
                // maximized instead of forcing it back to Normal.
                mw.BringToFront();
            }
        }
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
        InputSimulator.CancelTyping();
        HotkeyManager.DisableCaptureHook();
        Program.StopOpenWindowRequestListener();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
