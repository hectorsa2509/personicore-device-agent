using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Personicore.DeviceAgent.Mac.Ui;

namespace Personicore.DeviceAgent.Mac;

/// <summary>Menu-bar-only app: no main window, it quits only from the menu.</summary>
internal sealed class App : Application
{
    private MenuBarController? _controller;

    public override void Initialize()
    {
        Name = "Personicore";
        Styles.Add(new FluentTheme());

        // The dialogs use the house palette on paper-colored backgrounds.
        RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _controller = new MenuBarController(desktop);
            desktop.Exit += (_, _) => _controller.Dispose();
            _controller.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
