using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KeyPulse;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public bool IsExiting { get; private set; }

    public static MainWindow? HiddenWindow;
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
                var mw = new MainWindow(); if (System.Linq.Enumerable.Contains(args, "--hidden")) { mw.WindowState = Avalonia.Controls.WindowState.Minimized; } desktop.MainWindow = mw;
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
                desktop.MainWindow.Show();
            desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
            desktop.MainWindow.Activate();
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}





