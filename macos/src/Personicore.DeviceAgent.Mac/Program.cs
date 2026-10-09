using Avalonia;
using Avalonia.Controls;
using Personicore.DeviceAgent.Mac.Platform;

namespace Personicore.DeviceAgent.Mac;

internal static class Program
{
    /// <summary>
    /// One agent per user session: two would fight over state.json and send the same
    /// assignment twice.
    /// </summary>
    private const string InstanceMutexName = "Personicore.DeviceAgent.SingleInstance";

    /// <summary>Passed by the LaunchAgent: a second copy started at login exits silently.</summary>
    public const string AutostartArgument = "--autostart";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--wizard", StringComparer.OrdinalIgnoreCase))
        {
            return AppBuilder.Configure<WizardApp>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = true })
                .StartWithClassicDesktopLifetime(args);
        }

        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            if (!args.Contains(AutostartArgument, StringComparer.OrdinalIgnoreCase))
                Notifier.Show("The Personicore agent is already running. Look for it in the menu bar.");
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Menu bar agent: no Dock icon (the bundle's LSUIElement does the same).
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
