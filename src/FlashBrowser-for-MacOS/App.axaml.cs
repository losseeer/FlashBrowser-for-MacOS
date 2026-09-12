using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FlashBrowserForMacOS;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            // --sol=<path>: open the .sol viewer alongside the browser (P1.4 dev/GUI runs).
            if (Program.LaunchSolPath is { } solPath)
            {
                new SolViewerWindow(solPath).Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
