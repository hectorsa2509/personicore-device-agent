using System.Runtime.Versioning;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Net;

namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// The "Use Personicore proxy" switch: points the Windows proxy at the meter node on port
/// 3129 while the panelist wants it, and puts things back when they don't.
///
/// Rules, applied on every cycle and on every click:
///   * wanted + node reachable → proxy = node IP:3129 (follows the node if its IP changes);
///   * wanted + no node        → paused: the panelist's previous settings are restored so
///                               the computer keeps internet away from home;
///   * wanted + somebody else's node → paused too, and said differently: measuring through
///                               another household's meter would file this computer's
///                               traffic under their home (the Core decides this, see
///                               ProxyPause);
///   * not wanted              → previous settings restored;
///   * if the panelist turns our proxy off in Windows Settings, that wins: the switch goes
///     off instead of fighting the setting on the next cycle.
///
/// Nothing is ever written over a proxy the agent did not set. Ownership is the address
/// in the state file: every restore checks it first, including the two the panelist asks
/// for by hand (switch off, quit), because a corporate proxy configured between two
/// cycles is on some networks the only way out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxySwitch(AgentState state) : IProxySwitch
{
    public const int Port = 3129;

    private readonly object _gate = new();

    public bool Wanted => state.ProxyWanted;

    /// <summary>One line for the tray and the details window.</summary>
    public string StatusText { get; private set; } = "off";

    public void SetWanted(bool wanted, string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        lock (_gate)
        {
            // The watchdog runs in its own process and may have restored or moved the
            // proxy since the last cycle; deciding from a stale copy is R4.
            state.ReloadProxy();
            state.ProxyWanted = wanted;
            state.SaveProxy();
            Log.Info($"Proxy switch turned {(wanted ? "on" : "off")} by the user.");
            SyncLocked(nodeIp, detectExternalChange: false, pause);
        }
    }

    /// <summary>Called on every cycle with the node's IP, or null when there is no node.</summary>
    public void Sync(string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        lock (_gate) SyncLocked(nodeIp, detectExternalChange: true, pause);
    }

    /// <summary>
    /// On Exit nothing is left watching the proxy: if the laptop then left home, a proxy
    /// pointing at the node would cut its internet. The choice is kept and re-applied on
    /// the next start.
    /// </summary>
    public void PauseForExit()
    {
        lock (_gate)
        {
            try
            {
                state.ReloadProxy();
                if (state.ProxyApplied) RestoreOrRelinquish(SystemProxy.Read());
            }
            catch (Exception ex)
            {
                Log.Error("Could not restore the proxy on exit", ex);
            }
        }
    }

    private void SyncLocked(string? nodeIp, bool detectExternalChange, ProxyPause pause)
    {
        try
        {
            // What the watchdog did while the agent was asleep is in the file, not in
            // memory: read it before judging a setting that is not what we remember
            // writing, or the safety net's own work looks like the panelist's.
            state.ReloadProxy();

            var current = SystemProxy.Read();

            if (detectExternalChange && state.ProxyApplied
                && (!current.ManualEnabled || !SameAddress(current.Server, state.ProxyAddress)))
            {
                Log.Info($"The proxy was changed in Windows Settings ({Describe(current)}); switch turned off.");
                state.ProxyWanted = false;
                state.ProxyApplied = false;
                state.ProxyPreviousFlags = null;
                state.ProxyPreviousServer = string.Empty;
                state.ProxyPreviousBypass = string.Empty;
                state.SaveProxy();
            }

            if (state.ProxyWanted && nodeIp is not null)
            {
                Enable(current, $"{nodeIp}:{Port}");
                StatusText = $"on · {state.ProxyAddress}";
            }
            else
            {
                if (state.ProxyApplied) RestoreOrRelinquish(current);
                StatusText = state.ProxyWanted ? AgentText.ProxyPausedStatus(pause) : "off";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the Windows proxy", ex);
            StatusText = $"error · {ex.Message}";
        }
    }

    private void Enable(ProxySettings current, string address)
    {
        if (state.ProxyApplied && current.ManualEnabled
            && SameAddress(current.Server, address) && SameAddress(state.ProxyAddress, address))
            return;

        if (!state.ProxyApplied)
        {
            state.ProxyPreviousFlags = current.Flags;
            state.ProxyPreviousServer = current.Server;
            state.ProxyPreviousBypass = current.Bypass;
        }

        // Automatic settings (auto-detect, setup script) are left as the panelist had them,
        // like turning on "Use a proxy server" in Settings does.
        SystemProxy.Write(new ProxySettings(
            current.Flags | SystemProxy.ProxyTypeDirect | SystemProxy.ProxyTypeProxy,
            address,
            SystemProxy.DefaultBypass));

        state.ProxyApplied = true;
        state.ProxyAddress = address;
        state.SaveProxy();

        // Safety net for when the agent is not running; see ProxyWatchdog.
        ProxyWatchdog.Install();

        Log.Info($"Windows proxy set to {address}.");
    }

    /// <summary>
    /// Puts the panelist's settings back — but only over the proxy the agent itself set.
    /// If Windows now holds somebody else's, restoring would write settings saved from
    /// before the agent touched anything over a configuration it never made: the switch
    /// simply stops claiming it. The watchdog has always made this check; switching off
    /// and quitting did not, and they are the two paths a panelist actually uses.
    /// </summary>
    private void RestoreOrRelinquish(ProxySettings current)
    {
        if (Owns(current))
        {
            RestoreNow(state, current);
            return;
        }

        Log.Info($"The proxy is now {Describe(current)}, which the agent did not set; "
                 + "leaving it alone and giving up ownership.");
        RelinquishNow(state);
    }

    /// <summary>
    /// Is what Windows holds right now the proxy this agent wrote? Not knowing what we
    /// wrote counts as not owning it.
    /// </summary>
    private bool Owns(ProxySettings current) =>
        current.ManualEnabled
        && !string.IsNullOrWhiteSpace(state.ProxyAddress)
        && SameAddress(current.Server, state.ProxyAddress);

    /// <summary>
    /// Moves the proxy to a new address without touching what was saved before the agent
    /// first changed anything: the node moved, the panelist's own settings did not.
    /// Static because the watchdog does the same thing without the rest of the agent.
    /// </summary>
    public static void RepointNow(AgentState state, ProxySettings current, string address)
    {
        SystemProxy.Write(new ProxySettings(
            current.Flags | SystemProxy.ProxyTypeDirect | SystemProxy.ProxyTypeProxy,
            address,
            SystemProxy.DefaultBypass));

        state.ProxyApplied = true;
        state.ProxyAddress = address;
        state.SaveProxy();

        Log.Info($"Windows proxy re-pointed to {address}.");
    }

    /// <summary>
    /// Puts back the settings saved before the agent touched them. Static because the
    /// watchdog process does the same thing without the rest of the agent.
    /// </summary>
    public static void RestoreNow(AgentState state, ProxySettings current)
    {
        var previous = state.ProxyPreviousFlags is { } flags
            ? new ProxySettings(flags, state.ProxyPreviousServer, state.ProxyPreviousBypass)
            : current with { Flags = (current.Flags & ~SystemProxy.ProxyTypeProxy) | SystemProxy.ProxyTypeDirect };

        SystemProxy.Write(previous);

        state.ProxyApplied = false;
        state.ProxyPreviousFlags = null;
        state.ProxyPreviousServer = string.Empty;
        state.ProxyPreviousBypass = string.Empty;
        state.SaveProxy();

        ProxyWatchdog.Remove();

        Log.Info($"Windows proxy restored to the previous settings ({Describe(previous)}).");

    }

    /// <summary>
    /// Stop claiming the system proxy, without touching it. Used when the settings no
    /// longer hold what the agent wrote: the panelist configured their own, and the
    /// saved "previous settings" describe a world that no longer exists — restoring them
    /// would overwrite a configuration the agent never made.
    /// </summary>
    public static void RelinquishNow(AgentState state)
    {
        state.ProxyWanted = false;
        state.ProxyApplied = false;
        state.ProxyPreviousFlags = null;
        state.ProxyPreviousServer = string.Empty;
        state.ProxyPreviousBypass = string.Empty;
        state.SaveProxy();

        ProxyWatchdog.Remove();
    }

    public static bool SameAddress(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Describe(ProxySettings s) =>
        s.ManualEnabled ? $"manual {s.Server}" : "no manual proxy";
}
