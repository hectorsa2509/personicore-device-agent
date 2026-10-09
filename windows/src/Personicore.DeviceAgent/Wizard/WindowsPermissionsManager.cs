using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Wizard;

/// <summary>
/// Manages Windows-specific system privilege verification, UAC elevation,
/// Windows Defender Firewall rules, and background service registration.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPermissionsManager
{
    public const string ServiceName = "PersonicoreAgentService";
    public const string FirewallRuleName = "Personicore Device Agent Ingest";

    public static bool CheckIsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to evaluate Windows administrator role: {ex.Message}");
            return false;
        }
    }

    public static bool CheckFirewallRule()
    {
        try
        {
            var start = new ProcessStartInfo("netsh.exe", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            process?.WaitForExit(5_000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not inspect Windows Defender Firewall rule: {ex.Message}");
            return false;
        }
    }

    public static bool CheckBackgroundServiceRunning()
    {
        try
        {
            var start = new ProcessStartInfo("sc.exe", $"query {ServiceName}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            var output = process?.StandardOutput.ReadToEnd() ?? string.Empty;
            process?.WaitForExit(5_000);
            return output.Contains("RUNNING") || process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not query Windows Service state: {ex.Message}");
            return false;
        }
    }

    public static bool RequestUacElevation(string arguments = "")
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath)) return false;

            var start = new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"UAC elevation was cancelled or failed: {ex.Message}");
            return false;
        }
    }

    public static bool ConfigureFirewallRule(string executablePath)
    {
        try
        {
            var args = $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=out action=allow program=\"{executablePath}\" enable=yes";
            var start = new ProcessStartInfo("netsh.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not add firewall rule: {ex.Message}");
            return false;
        }
    }

    public static bool RegisterScheduledWatchdogTask(string executablePath)
    {
        try
        {
            var start = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("/create");
            start.ArgumentList.Add("/f");
            start.ArgumentList.Add("/sc");
            start.ArgumentList.Add("onstart");
            start.ArgumentList.Add("/tn");
            start.ArgumentList.Add("Personicore\\DeviceAgent");
            start.ArgumentList.Add("/tr");
            start.ArgumentList.Add($"\"{executablePath}\" --background");
            start.ArgumentList.Add("/rl");
            start.ArgumentList.Add("HIGHEST");

            using var process = Process.Start(start);
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not register watchdog scheduled task: {ex.Message}");
            return false;
        }
    }
}
