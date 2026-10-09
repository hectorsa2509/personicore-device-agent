using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>Notifications belong to the agent's bundle, including permissions and foreground delivery.</summary>
internal static class Notifier
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationResult(int status, IntPtr detail, IntPtr context);

    // Keep the reverse P/Invoke delegate alive for all asynchronous native callbacks.
    private static readonly NotificationResult Result = OnResult;
    private static Window? _testResult;

    [DllImport("libPersonicoreNotifications.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_notify(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        NotificationResult callback, IntPtr context);

    public static void Show(string message, string title = "Personicore", bool test = false)
    {
        try
        {
            Log.Info(test ? "Testing native macOS notification." : "Requesting native macOS notification.");
            pc_notify(title, message, Result, test ? new IntPtr(1) : IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load native notifications", ex);
            if (test) Dispatcher.UIThread.Post(() => ShowTestResult(2));
        }
    }

    private static void OnResult(int status, IntPtr detail, IntPtr context)
    {
        // Exceptions must never cross the native callback boundary.
        try
        {
            var diagnostic = Marshal.PtrToStringUTF8(detail) ?? "No diagnostic";
            if (status == 0) Log.Info(diagnostic);
            else Log.Warn($"macOS notification ({status}): {diagnostic}");
            if (context != IntPtr.Zero && status != 0)
                Dispatcher.UIThread.Post(() => ShowTestResult(status));
        }
        catch (Exception ex) { Log.Error("Notification callback failed", ex); }
    }

    private static void ShowTestResult(int status)
    {
        _testResult?.Close();
        var settings = new Button { Content = AgentText.NotificationSettings };
        settings.Click += (_, _) => Shell.Open("x-apple.systempreferences:com.apple.Notifications-Settings.extension");
        var close = new Button { Content = AgentText.NotificationClose };
        var window = new Window
        {
            Title = AgentText.TestProxyNotification,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = status == 1 || status == 3 ? AgentText.NotificationTestBlocked
                            : AgentText.NotificationTestFailed,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    settings,
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (_testResult == window) _testResult = null; };
        _testResult = window;
        window.Show();
        window.Activate();
    }
}
