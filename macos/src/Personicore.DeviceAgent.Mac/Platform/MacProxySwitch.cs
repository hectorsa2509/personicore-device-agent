using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>
/// The "Use Personicore proxy" switch on macOS. Same rules as on Windows
/// (docs/CROSS-PLATFORM-PLAN.md §1): follow the node's IP, pause away from home — and on a
/// network whose meter node is another household's — restore on switch-off and on quit, and
/// let the System Settings win if the panelist turns the proxy off there.
///
/// What differs is who writes the setting: on macOS it is system-wide and needs admin
/// rights, so the work is done by the root helper (<see cref="ProxyHelper"/>), installed
/// once. Reading the current state needs no privileges, so the agent does that itself.
/// </summary>
/// <param name="routerHost">
/// The name the node is looked up by. It travels with every APPLY so that root can
/// follow the node to a new address by itself when the agent is not running.
/// </param>
internal sealed class MacProxySwitch(AgentState state, string routerHost) : IProxySwitch
{
    public const int Port = 3129;

    private readonly object _gate = new();

    public bool Wanted => state.ProxyWanted;

    public string StatusText { get; private set; } = "off";

    /// <summary>
    /// Turning it on installs the helper if needed, which asks for an administrator
    /// password once. Called from the menu, so the prompt always follows a click.
    /// </summary>
    public void SetWanted(bool wanted, string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        lock (_gate)
        {
            if (wanted && !ProxyHelper.IsInstalled)
            {
                var install = ProxyHelper.Install();
                if (!install.Ok)
                {
                    StatusText = $"error · {install.Message}";
                    return;
                }
            }
            else if (wanted && ProxyHelper.NeedsUpgrade)
            {
                // Older helpers lack the independent watchdog certificate check.
                Log.Info("An older proxy helper is installed; replacing it.");
                if (!ProxyHelper.Install().Ok || ProxyHelper.NeedsUpgrade)
                {
                    SetWanted(false, nodeIp, pause);
                    StatusText = "error · " + AgentText.ProxyHelperUpgradeRequired;
                    return;
                }
            }

            state.ProxyWanted = wanted;
            state.SaveProxy();
            Log.Info($"Proxy switch turned {(wanted ? "on" : "off")} by the user.");

            SyncLocked(nodeIp, detectExternalChange: false, explicitEnable: true, pause);
        }
    }

    public void Sync(string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        lock (_gate) SyncLocked(nodeIp, detectExternalChange: true, explicitEnable: false, pause);
    }

    /// <summary>
    /// On quit nothing is left watching the proxy: a Mac that then left home would have
    /// no internet. The choice is kept and re-applied on the next start.
    /// </summary>
    public void PauseForExit()
    {
        lock (_gate)
        {
            Reconcile();
            if (!state.ProxyApplied) return;

            var result = ProxyHelper.Restore();
            if (!result.Ok)
            {
                // Still applied, and the helper still has the backup: saying otherwise
                // would leave the Mac behind a proxy the agent no longer believes in, and
                // nothing —not the next start, not the watchdog— would go back for it.
                Log.Warn($"Could not restore the proxy on quit: {result.Message}");
                return;
            }

            state.ProxyApplied = false;
            state.SaveProxy();
        }
    }

    private void SyncLocked(string? nodeIp, bool detectExternalChange, bool explicitEnable, ProxyPause pause)
    {
        if (state.ProxyWanted && ProxyHelper.NeedsUpgrade)
        {
            state.ProxyWanted = false;
            state.SaveProxy();
            SyncLocked(nodeIp, detectExternalChange, explicitEnable: false, pause);
            StatusText = "error · " + AgentText.ProxyHelperUpgradeRequired;
            return;
        }
        if (state.ProxyWanted && !ProxyHelper.IsInstalled)
        {
            StatusText = "off · the proxy helper is not installed";
            return;
        }

        // When root answered, it has already checked every service and kind it claims,
        // one by one, and released whatever the panelist took over. Reading the same
        // question off the OS afterwards can only contradict a better answer.
        var authoritative = Reconcile();

        var current = MacSystemProxy.Read();

        if (!authoritative && detectExternalChange && state.ProxyApplied
            && !current.Any(p => p.Enabled && SameAddress(p.Address, state.ProxyAddress)))
        {
            Log.Info("The proxy was changed in System Settings; switch turned off.");
            state.ProxyWanted = false;
            state.ProxyApplied = false;
            state.SaveProxy();
        }

        if (state.ProxyWanted && nodeIp is not null)
        {
            Enable($"{nodeIp}:{Port}", nodeIp, current, explicitEnable);
            return;
        }

        if (state.ProxyApplied)
        {
            var result = ProxyHelper.Restore();
            if (result.Ok)
            {
                state.ProxyApplied = false;
                state.SaveProxy();
                Log.Info("System proxy restored to the previous settings.");
            }
            else
            {
                // The helper kept both the claim and the backup, so this is worth
                // retrying: the state stays "applied" and the next cycle tries again.
                StatusText = $"error · {result.Message}";
                return;
            }
        }

        StatusText = state.ProxyWanted ? AgentText.ProxyPausedStatus(pause) : "off";
    }

    /// <summary>
    /// Brings the agent's idea of the proxy in line with root's, which is the only
    /// authoritative one.
    /// </summary>
    /// <remarks>
    /// The helper's watchdog runs as its own LaunchDaemon, every minute, precisely when
    /// the agent is not running — and it cannot write the agent's state file. So the
    /// agent would wake holding what was true an hour ago: a proxy the watchdog had
    /// restored looked like the panelist turning it off, which switched the agent off for
    /// good, and a proxy the watchdog had moved looked the same way. Ownership and the
    /// pause marker come from the helper; only if it does not answer does the agent fall
    /// back on what it remembers.
    /// </remarks>
    /// <returns>true when root answered, and its answer is now what the state holds.</returns>
    private bool Reconcile()
    {
        state.ReloadProxy();

        if (!state.ProxyWanted && !state.ProxyApplied) return false;
        if (!ProxyHelper.IsInstalled) return false;

        var owned = ProxyHelper.Owned();
        if (owned is null) return false;

        if (owned.Count > 0)
        {
            if (owned.Mixed)
            {
                // Some settings moved and some did not. There is no address to record,
                // and leaving the old one would make the next cycle decide there is
                // nothing to do: an empty address is what sends it back through the
                // re-point, which is what brings the stragglers forward.
                Log.Warn($"Root holds {owned.Count} proxy setting(s) at more than one address; "
                         + "they will be brought back together on this cycle.");
                state.ProxyApplied = true;
                state.ProxyAddress = string.Empty;
                state.SaveProxy();
                return true;
            }

            if (state.ProxyApplied && SameAddress(state.ProxyAddress, owned.Address)) return true;

            Log.Info($"Root still holds {owned.Count} proxy setting(s) at {owned.Address}; following it.");
            state.ProxyApplied = true;
            state.ProxyAddress = owned.Address;
            state.SaveProxy();
            return true;
        }

        if (!state.ProxyApplied) return true;

        state.ProxyApplied = false;

        if (owned.Paused)
        {
            // Our own safety net: the switch stays on and the proxy comes back by itself
            // the next time a meter node answers.
            Log.Info("The proxy was taken out by the watchdog because no meter node answered.");
        }
        else
        {
            Log.Info("The proxy this agent set is no longer there and the watchdog did not "
                     + "take it out; switch turned off.");
            state.ProxyWanted = false;
        }

        state.SaveProxy();
        return true;
    }

    /// <param name="explicitEnable">
    /// true when the panelist just turned the switch on. Then, and only then, the whole
    /// Mac is set: every service, both kinds. Any other pass through here is the agent
    /// following the node to a new address on its own, and root is asked to move only
    /// what it still owns — otherwise the re-point writes over the proxy the panelist set
    /// on another service in the meantime.
    /// </param>
    private void Enable(string address, string nodeIp, IReadOnlyList<ServiceProxy> current, bool explicitEnable)
    {
        // ProxyAddress is empty while root holds settings at more than one address, so
        // this is false exactly when something still has to be moved. The reading of the
        // OS is the weaker half of the test —it sees only the web proxy of each service—
        // and it is Reconcile that has already checked the rest through root.
        var alreadyThere = state.ProxyApplied
                           && !string.IsNullOrEmpty(state.ProxyAddress)
                           && SameAddress(state.ProxyAddress, address)
                           && current.Any(p => p.Enabled && SameAddress(p.Address, address));

        if (!alreadyThere)
        {
            // The household's MAC travels with it: root's own watchdog resolves the node's
            // name while the agent is not running, and every meter node answers on that name.
            // Without it the watchdog follows whichever node is in front of the Mac, which in
            // another panel household is that household's meter.
            var result = explicitEnable || !state.ProxyApplied
                ? ProxyHelper.Apply(nodeIp, routerHost, state.ExpectedNodeMac)
                : ProxyHelper.Repoint(nodeIp, routerHost, state.ExpectedNodeMac);

            if (!result.Ok)
            {
                StatusText = $"error · {result.Message}";
                return;
            }

            if (result.Nothing)
            {
                // Every proxy this helper had set has been taken over in System Settings.
                // There is nothing left to follow the node with, and claiming it again
                // would be the overwrite the re-point exists to avoid.
                Log.Info("The panelist has taken over every proxy the helper set; switch turned off.");
                state.ProxyWanted = false;
                state.ProxyApplied = false;
                state.SaveProxy();
                StatusText = "off";
                return;
            }

            state.ProxyApplied = true;
            state.ProxyAddress = address;
            state.SaveProxy();
            Log.Info($"System proxy set to {address}.");
        }

        StatusText = $"on · {address}";
    }

    private static bool SameAddress(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One network service's web proxy, as System Settings shows it.</summary>
internal sealed record ServiceProxy(string Service, bool Enabled, string Address);

/// <summary>
/// Reads the current proxies with networksetup. Reading needs no privileges — only
/// writing does — so the agent can tell on its own when the panelist changed them in
/// System Settings.
/// </summary>
internal static class MacSystemProxy
{
    public static IReadOnlyList<ServiceProxy> Read()
    {
        var proxies = new List<ServiceProxy>();

        var services = Shell.Run("/usr/sbin/networksetup", "-listallnetworkservices");
        foreach (var line in services.Split('\n').Skip(1))
        {
            var service = line.TrimStart('*').Trim();
            if (service.Length == 0) continue;

            var web = Shell.Run("/usr/sbin/networksetup", "-getwebproxy", service);
            if (web.Length == 0) continue;

            string Field(string name) => web.Split('\n')
                .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal))?[(name.Length + 1)..].Trim()
                ?? string.Empty;

            proxies.Add(new ServiceProxy(
                service,
                Field("Enabled").Equals("Yes", StringComparison.OrdinalIgnoreCase),
                $"{Field("Server")}:{Field("Port")}"));
        }

        return proxies;
    }
}
