using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Net;
using Personicore.DeviceAgent.Router;

namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// Safety net for the proxy switch, for when the agent is not running — the panelist
/// closed it, it crashed, or it never started — and the computer leaves home. A proxy
/// pointing at a router that is not there would leave it with no internet.
///
/// It is a user scheduled task (no administrator rights) that runs this same .exe every
/// minute with <see cref="Argument"/>. Two things can happen to a proxy nobody is
/// watching, and they need opposite answers:
///
///   * the node is still at home but got a new address —it rebooted, or its lease was
///     renewed— → follow it, so the panelist never notices;
///   * there is no node of this household —the computer left home, the node is off, or the
///     node in front of it belongs to somebody else's household— → take the proxy out, or
///     the panelist is left with no internet and no idea why.
///
/// Registered when the switch goes on and removed when it goes off, so a computer that
/// never used the proxy gets no task.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProxyWatchdog
{
    public const string Argument = "--proxy-watchdog";

    private const string TaskName = "Personicore Proxy Watchdog";

    /// <summary>One check, then exit. This is the whole process when run by the task.</summary>
    public static int Run()
    {
        var state = AgentState.Load();
        if (!state.ProxyApplied) return 0;

        var current = SystemProxy.Read();
        if (!current.ManualEnabled) return 0;      // already off: nothing to undo

        // There is a manual proxy, but it may not be ours. The panelist can set their own
        // in Settings while the agent is not running — which is exactly when this task
        // runs — and then neither of the two things below would be right: re-pointing
        // writes our address over theirs, and restoring writes settings saved from before
        // the agent ever touched anything. Both destroy a configuration the agent did not
        // make, and on some networks that configuration is the only way out.
        //
        // Ownership is the address we wrote. Not knowing what we wrote counts as not
        // owning it: the interactive switch makes the same check before it acts.
        if (string.IsNullOrWhiteSpace(state.ProxyAddress)
            || !ProxySwitch.SameAddress(current.Server, state.ProxyAddress))
        {
            Log.Info($"Proxy watchdog: the proxy is now {Describe(current.Server)}, which the agent did "
                     + "not set; leaving it alone and giving up ownership.");
            ProxySwitch.RelinquishNow(state);
            return 0;
        }

        if (!CertificateInstaller.IsTrusted())
        {
            Log.Warn(AgentText.CertificateRequired);
            state.ProxyWanted = false;
            state.SaveProxy();
            ProxySwitch.RestoreNow(state, current);
            return 0;
        }

        var address = state.ProxyAddress;
        var ip = address.Split(':')[0];

        // The node this computer is registered on, and no other. The name below resolves
        // through the node's own dnsmasq and every meter node answers to it, so in another
        // panel household it resolves to that household's meter: following it would send
        // their meter this computer's traffic to log. Empty before the first registration,
        // where any meter node is accepted — there is nothing yet to be foreign to.
        var ours = state.ExpectedNodeMac;

        var timeout = TimeSpan.FromSeconds(4);
        if (NodeProbe.IsMeterNodeAsync(ip, timeout, ours).GetAwaiter().GetResult()) return 0;

        // The node may still be here with another address: follow it before giving up,
        // otherwise the computer browses unmeasured until the agent runs again.
        var host = AgentConfig.Load(out _).RouterHost;
        var moved = NodeProbe.FirstReachableAsync(Resolve(host), timeout, ours).GetAwaiter().GetResult();

        if (moved is not null && !string.Equals(moved, ip, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Proxy watchdog: the meter node moved from {ip} to {moved}; following it.");
            ProxySwitch.RepointNow(state, current, $"{moved}:{ProxySwitch.Port}");
            return 0;
        }

        Log.Warn($"Proxy watchdog: no meter node of this household answers at {address}; "
                 + "removing the proxy so the computer keeps internet.");
        ProxySwitch.RestoreNow(state, current);
        return 0;
    }

    private static string Describe(string server) =>
        string.IsNullOrWhiteSpace(server) ? "(empty)" : server;

    /// <summary>The node's name, resolved now. Empty when there is no network at all.</summary>
    private static IEnumerable<string> Resolve(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString());
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return [];
        }
    }

    public static void Install()
    {
        // /sc minute /mo 1: every minute, as the current user, no elevation. A minute is
        // the difference between a panelist noticing the cut and not noticing it.
        Schtasks("/create", "/f", "/sc", "minute", "/mo", "1", "/tn", TaskName,
            "/tr", $"\"{Ui.StartupRegistration.ExecutablePath()}\" {Argument}");
    }

    public static void Remove() => Schtasks("/delete", "/f", "/tn", TaskName);

    private static void Schtasks(params string[] arguments)
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
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            process?.WaitForExit(15_000);
        }
        catch (Exception ex)
        {
            // The switch still works without the task; only the safety net is missing.
            Log.Warn($"Could not update the proxy watchdog task: {ex.Message}");
        }
    }
}
