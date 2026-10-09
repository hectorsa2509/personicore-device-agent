using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Personicore.DeviceAgent.Mac.Wizard;

namespace Personicore.DeviceAgent.Mac;

/// <summary>
/// Standalone application lifetime for exercising the HP-1988 Guided Setup Wizard on macOS.
/// </summary>
internal sealed class WizardApp : Application
{
    public override void Initialize()
    {
        Name = "Personicore Setup Wizard";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var wizard = new GuidedWizardWindow();
            desktop.MainWindow = wizard;
            wizard.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
