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

    public override void OnFrameworkInitializationCompleted()
    {
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "OnFrameworkInitializationCompleted started\n");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = System.Environment.GetCommandLineArgs();
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "Args: " + string.Join(", ", args) + "\n");
            
            if (System.Linq.Enumerable.Contains(args, "--uninstall"))
            {
                desktop.MainWindow = new SetupWindow(true);
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "Set SetupWindow(uninstall)\n");
            }
            else if (System.Linq.Enumerable.Contains(args, "--install-worker"))
            {
                desktop.MainWindow = new SetupWindow(false);
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "Set SetupWindow(install)\n");
            }
            else
            {
                desktop.MainWindow = new MainWindow();
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "Set MainWindow\n");
            }
        }
        else
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "Not IClassicDesktopStyleApplicationLifetime\n");
        }

        base.OnFrameworkInitializationCompleted();
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keypulse_debug.txt"), "OnFrameworkInitializationCompleted finished\n");
    }

    public void Settings_Clicked(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Show();
            desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
            desktop.MainWindow.Activate();
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