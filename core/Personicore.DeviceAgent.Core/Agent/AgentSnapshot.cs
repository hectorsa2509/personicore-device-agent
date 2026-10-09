using Personicore.DeviceAgent.Device;
using Personicore.DeviceAgent.Graph;

namespace Personicore.DeviceAgent.Agent;

public enum AgentStatus
{
    /// <summary>Starting up, no complete cycle yet.</summary>
    Starting,

    /// <summary>Sin nodo meter alcanzable: fuera de casa, router apagado o sin ARP spoof.</summary>
    RouterNotFound,

    /// <summary>Node and household resolved, but nobody has said whose computer this is.</summary>
    OwnerRequired,

    /// <summary>
    /// The node answers and so does the backend, but the panel has nobody to assign this
    /// computer to yet: the router was never set up, or its household has no panelists.
    /// There is nothing the panelist can do from this computer; the agent keeps retrying.
    /// </summary>
    HouseholdNotAssigned,

    /// <summary>
    /// The household resolved and the panelists are there — the backend simply cannot take
    /// the registration: it refused the credential, or the operation is not in its schema
    /// yet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="HouseholdNotAssigned"/>, and that separation is the whole
    /// point. Both are "nothing to do from this computer", so reusing the other one looked
    /// harmless — but its headline is "Household not set up in the panel yet", and saying
    /// that while the household reads back perfectly sends whoever is debugging straight to
    /// the panel to look for a record that is already there.
    /// </remarks>
    BackendNotReady,

    /// <summary>The computer is registered and assigned in the household.</summary>
    Registered,

    /// <summary>Offline: the send was written to the desktop file, the API was never called.</summary>
    Simulated,

    /// <summary>The cycle failed: backend, credential or network. The detail is in the log.</summary>
    Error,
}

/// <summary>
/// An immutable photo of what the agent knows right now. The tray and the dialogs read
/// from here, never from the worker directly.
/// </summary>
public sealed record AgentSnapshot
{
    public AgentStatus Status { get; init; } = AgentStatus.Starting;
    public string Detail { get; init; } = "Starting…";

    public string RouterBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// The name the node is looked up by, from the configuration. Stamped on every
    /// snapshot so the menu can offer the node's page even before one has answered.
    /// </summary>
    public string RouterHost { get; init; } = string.Empty;
    public string RouterMac { get; init; } = string.Empty;
    public string RouterId { get; init; } = string.Empty;
    public string HouseholdId { get; init; } = string.Empty;

    public string LocalIp { get; init; } = string.Empty;
    public string Mac { get; init; } = string.Empty;
    public string MachineId { get; init; } = string.Empty;
    public bool MacIsRandomized { get; init; }
    public string InterfaceName { get; init; } = string.Empty;

    public DeviceProfile Profile { get; init; } = DeviceProfile.Unknown;
    public IReadOnlyList<Panelist> Panelists { get; init; } = [];
    public IReadOnlyList<string> OwnerIds { get; init; } = [];
    public IReadOnlyList<string> OwnerNames { get; init; } = [];
    public DateTimeOffset? LastRegisteredAt { get; init; }

    /// <summary>The owner the computer is mainly for; empty when there is only one.</summary>
    public string PrincipalOwnerName { get; init; } = string.Empty;

    /// <summary>
    /// The same person as <see cref="PrincipalOwnerName"/>, by id. The owner dialog opens
    /// its radio buttons from this: two people in a household can share a name, and a
    /// dialog reopened to correct the model used to hand the computer to whoever happened
    /// to be first in the list.
    /// </summary>
    public string PrincipalOwnerId { get; init; } = string.Empty;

    /// <summary>
    /// The identifier the backend gave this computer, or empty until it has given one.
    /// It is held by <see cref="Device.DeviceIdStore"/>, not by the state file, so that
    /// forgetting the owner or reinstalling does not create a second device in the panel.
    /// </summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// When the next report goes out if nothing changes before then. A changed IP does
    /// not wait for it.
    /// </summary>
    public DateTimeOffset? NextReportDue { get; init; }

    /// <summary>
    /// Whether this computer already trusts Personicore's HTTPS certificate.
    /// </summary>
    /// <remarks>
    /// Carried on the snapshot rather than asked for when a menu is drawn, because asking is
    /// not free: on macOS it spawns <c>/usr/bin/security verify-cert</c> and waits, and on
    /// Windows it builds an X509 chain. A menu that re-checked on every render would do that
    /// every time the panelist opened it.
    /// </remarks>
    public bool CertificateTrusted { get; init; }

    /// <summary>
    /// Whether the panelist has already been through the setup assistant. False on a
    /// freshly unzipped agent, which is what makes the assistant open itself the first time.
    /// </summary>
    /// <remarks>
    /// Says nothing about whether the three steps were actually done —
    /// <see cref="SetupPlan"/> reads that off this same snapshot. It only records that the
    /// panelist was walked through them and pressed Finish, so the window is not put in
    /// front of somebody who decided to skip a step.
    /// </remarks>
    public bool SetupCompleted { get; init; }

    /// <summary>The "Use Personicore proxy" switch and what is actually applied.</summary>
    public bool ProxyWanted { get; init; }
    public string ProxyStatus { get; init; } = "off";

    /// <summary>false when the backend rejected deviceOs and the OS never travelled.</summary>
    public bool DeviceOsAccepted { get; init; } = true;

    /// <summary>true when nothing is POSTed and the requests go to outbox.txt instead.</summary>
    public bool Offline { get; init; }

    /// <summary>
    /// PERSONICORE_DRY_RUN in the .env: a developer exercising the agent away from a
    /// household. It is <b>not</b> the same as <see cref="Offline"/>, which only says
    /// that nothing is being sent.
    ///
    /// The difference matters because dry run is the one mode allowed to carry on
    /// without a meter node — typing the owner by hand, inventing no household. On a
    /// panelist's computer that must never happen: no node means no household, and the
    /// agent has nothing to ask about.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>true while a meter node answered on this network during the last cycle.</summary>
    public bool NodeFound { get; init; }

    /// <summary>
    /// With no meter node the agent is a viewer: the only things worth offering are looking
    /// at what it knows (details and the log folder) and looking again. Registering, the
    /// proxy and the start-with-the-session item all depend on a node that is not there, so
    /// they are switched off instead of half-working.
    ///
    /// Dry run is never locked: there is no node to wait for, and the whole point of that
    /// mode is to exercise the agent away from a household.
    /// </summary>
    public bool ActionsUnlocked => NodeFound || DryRun;

    /// <summary>
    /// The meter node's own page, for <see cref="AgentText.OpenNode"/>.
    ///
    /// The address the agent actually reached the node at wins over the name: that is the
    /// one known to work, and it is what a node found by sweeping the segment answers on.
    /// The name is the fallback — it only resolves at home, through the node's own
    /// dnsmasq, which is exactly where this is offered. The default port is dropped
    /// because a browser does not need it and a panelist reading it over the phone does
    /// not need it either.
    /// </summary>
    public string NodeUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RouterBaseUrl))
                return string.IsNullOrWhiteSpace(RouterHost) ? string.Empty : $"http://{RouterHost}";

            var url = RouterBaseUrl.TrimEnd('/');
            return url.EndsWith(":80", StringComparison.Ordinal) ? url[..^3] : url;
        }
    }

    /// <summary>Whether the owner dialog can be opened at all.</summary>
    /// <remarks>
    /// It needs both a panelist to choose and a router id to key the assignment on;
    /// <see cref="AgentStatus.HouseholdNotAssigned"/> clears both, so it is false there
    /// too. Only in dry run is it enough to have identified the computer: there is no
    /// panelist list to ask for and no router id to wait for, and the owner is typed.
    /// </remarks>
    public bool CanRegister => Panelists.Count > 0
        ? !string.IsNullOrEmpty(RouterId)
        : DryRun && !string.IsNullOrEmpty(MachineId);
}
