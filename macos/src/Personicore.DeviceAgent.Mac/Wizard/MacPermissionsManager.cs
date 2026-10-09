using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Wizard;

/// <summary>
/// Manages macOS-specific system permissions:
/// - Accessibility detection via AXIsProcessTrusted()
/// - Deep-linking to macOS System Settings Privacy pane
/// - LaunchDaemon installation and status verification in /Library/LaunchDaemons
/// </summary>
public static class MacPermissionsManager
{
    public const string LaunchDaemonPlistPath = "/Library/LaunchDaemons/com.personicore.agent.plist";
    public const string AccessibilitySettingsUri = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern bool AXIsProcessTrusted();

    public static bool CheckAccessibilityPermission()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return true;
            return AXIsProcessTrusted();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to evaluate AXIsProcessTrusted: {ex.Message}");
            return false;
        }
    }

    public static bool OpenAccessibilitySettings()
    {
        try
        {
            var start = new ProcessStartInfo("open", AccessibilitySettingsUri)
            {
                UseShellExecute = true,
            };
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed opening System Settings Accessibility pane: {ex.Message}");
            return false;
        }
    }

    public static bool CheckLaunchDaemonInstalled()
    {
        try
        {
            if (!File.Exists(LaunchDaemonPlistPath)) return false;

            var start = new ProcessStartInfo("launchctl", "list com.personicore.agent")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            process?.WaitForExit(3_000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed checking LaunchDaemon via launchctl: {ex.Message}");
            return false;
        }
    }

    public static string GenerateLaunchDaemonPlist(string binaryPath)
    {
        return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>com.personicore.agent</string>
            <key>ProgramArguments</key>
            <array>
                <string>{binaryPath}</string>
                <string>--daemon</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <true/>
            <key>StandardOutPath</key>
            <string>/var/log/personicore-agent.log</string>
            <key>StandardErrorPath</key>
            <string>/var/log/personicore-agent-err.log</string>
        </dict>
        </plist>
        """;
    }
}
