namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// The "Use Personicore proxy" switch. The rules are the same on every OS (see
/// docs/CROSS-PLATFORM-PLAN.md §1); how the system proxy is written is per platform.
/// A platform without it passes null to <see cref="AgentWorker"/> and the menu hides it.
/// </summary>
public interface IProxySwitch
{
    bool Wanted { get; }

    /// <summary>One line for the menu and the details window.</summary>
    string StatusText { get; }

    void SetWanted(bool wanted, string? nodeIp, ProxyPause pause = ProxyPause.NoNode);

    /// <summary>Called on every cycle with the node's IP, or null when there is no node.</summary>
    /// <param name="pause">
    /// Why there is no address, when <paramref name="nodeIp"/> is null. It only decides a
    /// line of status text; the decision itself is the Core's.
    /// </param>
    void Sync(string? nodeIp, ProxyPause pause = ProxyPause.NoNode);

    void PauseForExit();
}

/// <summary>
/// Why the switch is on and yet nothing is applied. Both cases are the Core's judgement —
/// a platform switch is told which one it is, never asked — and each reads differently to
/// the panelist, because only one of them ends by itself when they get home.
/// </summary>
public enum ProxyPause
{
    /// <summary>No meter node answered: away from home, or the node is off.</summary>
    NoNode,

    /// <summary>
    /// A meter node answered and it is not the one this computer is registered on — the
    /// panelist is visiting another panel household, or has moved and not registered here
    /// yet. Measuring through somebody else's meter would file this computer's traffic
    /// under their home, so the proxy waits until this computer is registered on the node
    /// in front of it.
    /// </summary>
    OtherHousehold,
}
