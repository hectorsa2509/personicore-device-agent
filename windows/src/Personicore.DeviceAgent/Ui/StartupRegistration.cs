using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// Start-up through the user's Run key, not a Windows service. A service runs in
/// session 0 and can neither draw a tray icon nor ask the panelist anything, which is
/// exactly what this agent has to do. See the README.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PersonicoreDeviceAgent";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the startup setting: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled) key.SetValue(ValueName, ExpectedCommand());
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            Log.Info($"Start with Windows {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the startup setting", ex);
        }
    }

    /// <summary>
    /// If start-up is enabled but points somewhere else (the panelist moved the .exe) it
    /// is rewritten: otherwise Windows would try to launch a file that is no longer there.
    /// </summary>
    public static void RepairIfMoved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string current) return;
            if (string.Equals(current, ExpectedCommand(), StringComparison.OrdinalIgnoreCase)) return;

            Log.Info($"Startup entry pointed to {current}; updating it.");
            SetEnabled(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check the startup setting: {ex.Message}");
        }
    }

    private static string ExpectedCommand() => $"\"{ExecutablePath()}\" {Program.AutostartArgument}";

    /// <summary>
    /// Path to the .exe. In a single-file publish Assembly.Location comes back empty, so
    /// the right source is the process module.
    /// </summary>
    public static string ExecutablePath() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath ?? string.Empty;
}
