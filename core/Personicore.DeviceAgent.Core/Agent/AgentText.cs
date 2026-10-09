using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// The few words that are genuinely different on each OS. Everything else a panelist
/// reads is identical on Windows and macOS, and lives in <see cref="AgentText"/>.
///
/// This type exists so that divergence has to be *declared*: a platform cannot quietly
/// reword a sentence, it can only fill in the blanks listed here.
/// </summary>
/// <param name="ThisComputer">"this PC" or "this Mac", for sentences that name it.</param>
/// <param name="StartupItem">The menu item that starts the agent with the session.</param>
/// <param name="ProxySettingsPath">Where the panelist can see the proxy in system settings.</param>
/// <param name="RandomizedMacNote">
/// What a randomized/private hardware address means here, and how to turn it off.
/// </param>
/// <param name="ProxyAdminNote">What turning the proxy on costs the panelist on this OS.</param>
/// <param name="RandomizedMacTag">Short marker next to the MAC in the diagnostics report.</param>
/// <param name="ProxyFirstRunNotice">
/// What the panelist is warned about before the proxy goes on for the very first time,
/// or empty where switching it on costs nothing. macOS installs a root helper and asks
/// for a password once; Windows only writes the current user's settings.
/// </param>
public sealed record PlatformWords(
    string ThisComputer,
    string StartupItem,
    string ProxySettingsPath,
    string RandomizedMacNote,
    string ProxyAdminNote,
    string RandomizedMacTag,
    string ProxyFirstRunNotice,
    string CertificateAdminNote);

/// <summary>
/// Everything the panelist reads, in one place and shared by both agents.
///
/// The Windows agent and the macOS agent are the same tool for the same person. When the
/// strings live in each UI they drift apart word by word, and support ends up looking
/// after two different products. Only what <see cref="PlatformWords"/> lists may differ.
/// </summary>
public static partial class AgentText
{
    public const string NotificationClose = "Close";
    public const string NotificationSettings = "Open notification settings";
    public const string NotificationTestBlocked = "Notification permission or banners are disabled. Open notification settings, select Personicore Device Agent and allow notifications with banners or alerts, then try again.";
    public const string NotificationTestFailed = "The notification could not be sent. See the agent log for details and try again using the latest packaged app.";
    public const string TestProxyNotification = "Test proxy notification";
    public const string ProxyDisabledReminder = "The Personicore proxy is not configured or is turned off. We recommend turning it on from the agent menu. If needed, complete the setup assistant first.";

    // ── Menu ────────────────────────────────────────────────────────────────
    public const string Register = "Register this computer…";
    public const string ChangeOwner = "Change owner…";
    public const string Outbox = "View what would be sent…";
    public const string Details = "View details…";

    /// <summary>
    /// Opens the meter node's own page in the browser. "Meter node" and not "router":
    /// the panelist's own router is a different box, and sending them to the wrong one is
    /// how a support call starts.
    /// </summary>
    public const string OpenNode = "Open the meter node…";
    public const string Refresh = "Refresh now";

    public const string ProxyItem = "Use Personicore proxy";
    public const string ProxyItemPaused = "Use Personicore proxy (paused)";
    public const string ProxySettingsItem = "Proxy settings…";
    public const string Quit = "Quit Personicore";

    // ── Status line ─────────────────────────────────────────────────────────

    /// <summary>The menu's status line, and the base of the icon tooltip.</summary>
    public static string Headline(AgentSnapshot snapshot) => snapshot.Status switch
    {
        AgentStatus.Simulated => snapshot.OwnerNames.Count > 0
            ? $"Simulated · {string.Join(", ", snapshot.OwnerNames)}"
            : "Simulated · written to the desktop",
        AgentStatus.Registered => snapshot.OwnerNames.Count > 0
            ? $"Registered · {string.Join(", ", snapshot.OwnerNames)}"
            : "Registered",
        AgentStatus.OwnerRequired => "Owner not set",
        AgentStatus.RouterNotFound => "No meter node on this network",
        AgentStatus.HouseholdNotAssigned => "Household not set up in the panel yet",
        AgentStatus.BackendNotReady => "Personicore is not ready for this computer yet",
        AgentStatus.Error => "Error: " + snapshot.Detail,
        _ => "Starting…",
    };

    // ── The lock: with no meter node, only look and look again ──────────────

    /// <summary>
    /// Why most of the menu is greyed out. It names the item that does work, so the
    /// panelist is given something to do instead of only a refusal.
    /// </summary>
    public const string WaitingForNode =
        "No Personicore meter node answers on this network yet. Check that you are connected to the "
        + "Personicore router network and use \"Refresh now\" to look again.";

    // ── The panel has nobody to assign this computer to ─────────────────────

    /// <summary>
    /// The meter node answers, but the panel does not know this router — or knows it
    /// without an id, which for an assignment amounts to the same thing.
    /// </summary>
    public const string RouterNotInPanel =
        "This home's router is not set up in Personicore yet, so this computer cannot be assigned to "
        + "anyone. Personicore support has to finish setting up the household — nothing needs to be "
        + "done on this computer. The agent keeps checking on its own.";

    /// <summary>The router is known, but its household has no panelists yet.</summary>
    public const string HouseholdHasNoPanelists =
        "This home has no people registered in Personicore yet, so there is nobody to assign this "
        + "computer to. Personicore support has to add them — nothing needs to be done on this "
        + "computer. The agent keeps checking on its own.";

    /// <summary>
    /// The backend answered and cannot take this registration: it refused the credential,
    /// or it does not have the operation in its schema yet.
    /// </summary>
    /// <remarks>
    /// Worded like the two above on purpose. From the panelist's chair all of these are the
    /// same event — the panel is not ready for this computer and nothing on the computer
    /// will change that — and a sentence about JWTs or schemas would only invite them to go
    /// looking. The detail support needs is in the log.
    /// </remarks>
    public const string BackendNotReady =
        "Personicore cannot register this computer yet. Personicore support has to finish it — "
        + "nothing needs to be done on this computer. The agent keeps checking on its own.";

    /// <summary>
    /// The panel has this computer on a different meter node from the one it is on now:
    /// almost always a move to another home, occasionally a node record recreated in the panel.
    /// </summary>
    /// <remarks>
    /// Said only on the backend's own refusal, never guessed at from a MAC that changed — the
    /// agent cannot tell a move from a replaced node, and a sentence about moving house told to
    /// somebody who has not moved is worse than the vaguer one. It names the move because that
    /// is what the panelist can recognise, and it asks nothing of them: moving a device between
    /// households is support's, and nothing on this computer will do it.
    /// </remarks>
    public const string DeviceOnAnotherNode =
        "This computer is registered on another Personicore router — the one in your previous home, "
        + "if you have moved. Personicore support has to move it to this home; nothing needs to be "
        + "done on this computer. Its address keeps being reported as soon as they do.";

    /// <summary>
    /// The owners and the address reached the panel; the device's own record did not.
    /// </summary>
    /// <remarks>
    /// It is a detail line under a <c>Registered</c> headline, not an error, and that is the
    /// point. The computer <i>is</i> assigned to its panelists and its address <i>is</i>
    /// current — saying "not ready" over that is false in the one line most people read, and
    /// it invites them to redo something that already worked. What is outstanding is a record
    /// support has to finish, so this says so plainly without asking them to act.
    /// </remarks>
    public static string DeviceRecordPending(IEnumerable<string> owners)
    {
        var people = string.Join(", ", owners);
        return (people.Length == 0 ? "Registered" : $"Registered · {people}")
               + " · Personicore has still to finish this computer's own record";
    }

    /// <summary>Why "Register this computer…" did not open the dialog.</summary>
    public static string CannotRegister(AgentSnapshot snapshot) => snapshot.Status switch
    {
        // The node and the backend are both fine; the panel is simply not ready.
        AgentStatus.HouseholdNotAssigned or AgentStatus.BackendNotReady => snapshot.Detail,

        _ when snapshot.Offline =>
            "This computer has not been identified on the network yet. Check that you are connected "
            + "to a network and try again.",

        _ => "No Personicore meter node answers on this network, so this computer cannot be "
             + "registered yet. Check that you are connected to the Personicore router network and "
             + "use \"Refresh now\" to look again.",
    };

    // ── Registration ────────────────────────────────────────────────────────
    public const string OwnerRequiredPrompt = "Tell us who this computer belongs to.";
    public const string OwnerRequiredPromptClickable = OwnerRequiredPrompt + " Click to register it.";

    public static string Registered(string friendlyName, IEnumerable<string> owners) =>
        $"Computer registered as \"{friendlyName}\" · {string.Join(", ", owners)}";

    public static string Simulated(string friendlyName, IEnumerable<string> owners, string fileName) =>
        $"\"{friendlyName}\" · {string.Join(", ", owners)}. Written to {fileName} (the API was not called).";

    public static string RegisterFailed(string reason) => $"Could not register the computer: {reason}";

    /// <summary>
    /// The dialog's answer no longer matches the household. It happens when the computer
    /// moves network while the dialog is open, which is exactly when getting it wrong
    /// would file the computer under the wrong home.
    /// </summary>
    public static string OwnersNotInHousehold(IEnumerable<string> names) =>
        $"{string.Join(", ", names)} are not in this household. The computer may have changed "
        + "network while the window was open — check the owners and try again.";

    public const string OutboxEmpty =
        "Nothing has been written yet. The file is created the first time the agent has something to send.";

    // ── Owner dialog ────────────────────────────────────────────────────────
    public const string OwnerWindowTitle = "Personicore · Register this computer";
    public const string OwnerHeading = "Who does this computer belong to?";
    public const string SectionDataToSend = "Data to be sent";
    public const string SectionOwner = "Owner";
    public const string FieldName = "Name";
    public const string FieldType = "Type";
    public const string FieldBrand = "Brand";
    public const string FieldModel = "Model";
    public const string FieldOs = "Operating system";
    public const string ButtonRegister = "Register computer";
    public const string ButtonNotNow = "Not now";
    public const string ButtonClose = "Close";
    public const string ButtonCopy = "Copy";
    public const string ButtonOpenLogs = "Open logs folder";

    public static readonly string[] DeviceTypes = ["laptop", "desktop", "tablet", "computer", "server"];

    /// <summary>
    /// The nearest value in <see cref="DeviceTypes"/>, or "computer".
    /// </summary>
    /// <remarks>
    /// <c>presenceCreateDevice</c> wants one of these exactly — "Laptop" is already a
    /// refusal — and three different things decide the value: what the OS reports, what the
    /// panelist picked, and whatever is in <c>state.json</c>, which support tells people to
    /// edit. Normalising at the one place the registration is built is cheaper than trusting
    /// all three, and the worst case is a computer filed as "computer", which is what the
    /// field means when nothing more specific is known.
    /// </remarks>
    public static string NormalizeDeviceType(string? value)
    {
        var text = value?.Trim() ?? string.Empty;

        return DeviceTypes.FirstOrDefault(type => type.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? "computer";
    }

    /// <summary>Where the household on the dialog's header came from.</summary>
    public static string OwnerSubtitle(AgentSnapshot snapshot) =>
        "Detected automatically from the household router · " + (string.IsNullOrWhiteSpace(snapshot.HouseholdId)
            ? $"router {snapshot.RouterMac}"
            : $"household {snapshot.HouseholdId}");

    public const string PickOwnersHint =
        "Check the household member it belongs to. If the computer is shared, check everyone who uses it.";

    public const string TypeOwnersHint =
        "Type who uses this computer. If it is shared, separate names with commas.\n"
        + "The household list could not be read: the API will assign the real IDs.";

    /// <summary>Only shown once more than one owner is checked; with one there is nothing to ask.</summary>
    public const string PrincipalLabel = "Mainly used by";

    public const string PrincipalHint =
        "Several people use this computer. Who is it mainly for?";

    public const string ErrorNoPrincipal = "Say who mainly uses the computer.";

    public const string ErrorNoTypedOwner = "Enter at least one name before registering the computer.";
    public const string ErrorNoSelectedOwner = "Select at least one person before registering the computer.";
    public const string ErrorNoName = "The computer needs a name.";

    /// <summary>
    /// The identity card: what the meter node sees of this computer, in a fixed-width
    /// block. Built here so the columns line up identically on both agents — they were
    /// one space apart when each UI wrote its own.
    /// </summary>
    public static string[] IdentityLines(AgentSnapshot snapshot) =>
    [
        $"IP on home network      {snapshot.LocalIp}   ·   {snapshot.InterfaceName}",
        $"Observed MAC            {snapshot.Mac}",
        $"machineId               {snapshot.MachineId}",
    ];

    // ── Proxy window ────────────────────────────────────────────────────────
    public const string ProxyWindowTitle = "Personicore · Proxy";
    public const string ProxyHeading = "Personicore proxy";
    public const string ProxySwitchLabel = "Use the Personicore proxy";

    public static string ProxySubtitle(PlatformWords words) =>
        $"Sends {words.ThisComputer}'s web traffic through the Personicore router, port 3129.";

    public const string ProxyCertificateWarning =
        "The proxy requires a trusted HTTPS certificate. Use ‘Install HTTPS certificate…’ "
        + "in the Personicore menu before turning it on.";

    /// <summary>One sentence for what the proxy is doing right now.</summary>
    public static string DescribeProxy(AgentSnapshot snapshot, PlatformWords words) => snapshot.ProxyStatus switch
    {
        var s when s.StartsWith("on", StringComparison.Ordinal) =>
            $"On. {Capitalize(words.ThisComputer)} is using {s[5..]} for web traffic.",
        var s when s.StartsWith(ProxyPausedOtherHousehold, StringComparison.Ordinal) =>
            "On, but paused: the Personicore router on this network belongs to another home, and "
            + "measuring through it would put this computer's activity in that home. It comes back "
            + $"once {words.ThisComputer} is registered here, or at home.",
        var s when s.StartsWith("paused", StringComparison.Ordinal) =>
            "On, but paused: there is no Personicore router on this network, so the proxy is off for "
            + "now and comes back at home.",
        var s when s.StartsWith("error", StringComparison.Ordinal) =>
            $"Could not be changed: {s[8..]}",
        var s when s.Contains("not installed", StringComparison.Ordinal) =>
            "Off. The helper that changes system proxies is not installed yet.",
        _ => $"Off. {Capitalize(words.ThisComputer)} connects directly, as usual.",
    };

    // ── The switch's own status line ────────────────────────────────────────
    // Short, and read back by DescribeProxy and the details window, so both halves live
    // here: a platform switch that spelled its own paused line would be the one sentence
    // the two agents could drift apart on.

    public const string ProxyPausedNoNode = "paused · no meter node on this network";

    /// <summary>
    /// A node answered and it is not this household's. Matched by
    /// <see cref="DescribeProxy"/>, so the two must stay the same string.
    /// </summary>
    public const string ProxyPausedOtherHousehold = "paused · the meter node here belongs to another household";

    public static string ProxyPausedStatus(ProxyPause pause) => pause switch
    {
        ProxyPause.OtherHousehold => ProxyPausedOtherHousehold,
        _ => ProxyPausedNoNode,
    };

    public const string ProxyTurningOn = "Turning the proxy on…";
    public const string ProxyTurningOff = "Turning the proxy off…";
    public const string ProxyOff = "Proxy off. Your previous proxy settings were restored.";

    public const string ProxyPending =
        "Proxy switched on. It will be applied as soon as the Personicore router is found.";

    public static string ProxyFailed(string reason) => $"The proxy could not be changed: {reason}";

    /// <summary>Hover text on the menu's proxy line, where the platform has hover text.</summary>
    public static string ProxyTooltip(AgentSnapshot snapshot) => $"Proxy: {snapshot.ProxyStatus}";

    public static string ProxyOn(string address, PlatformWords words) =>
        $"Proxy on: {address}. You can see it in {words.ProxySettingsPath}.";

    // ── Start with the session ──────────────────────────────────────────────
    public static string StartupFailed(PlatformWords words) =>
        $"Could not change \"{words.StartupItem}\". Check the agent log.";

    // ── Diagnostics report ──────────────────────────────────────────────────
    public const string DetailsWindowTitle = "Personicore · Agent status";

    /// <summary>
    /// Everything the agent knows, as copyable text. It is the first thing support asks
    /// a panelist for, so both agents produce byte-identical reports except for the two
    /// words that name the platform.
    /// </summary>
    public static string DetailsReport(AgentSnapshot s, PlatformWords words)
    {
        var owners = s.OwnerNames.Count == 0 ? "(not declared)" : string.Join(", ", s.OwnerNames);
        var registered = s.LastRegisteredAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(never)";
        var mode = !s.Offline
            ? "sending to the API"
            : s.Panelists.Count > 0
                ? "READ ONLY — household read from the API, sends written to the desktop file"
                : "READ ONLY — household not available, only writes the desktop file";

        string[] lines =
        [
            $"Mode                {mode}",
            $"Status              {s.Status}",
            $"Detail              {s.Detail}",
            string.Empty,
            "── HOUSEHOLD ────────────────────────────────",
            $"Meter node          {s.RouterBaseUrl}",
            $"Router MAC          {s.RouterMac}",
            $"Router ID           {s.RouterId}",
            $"Household ID        {(string.IsNullOrEmpty(s.HouseholdId) ? "(not exposed by the backend)" : s.HouseholdId)}",
            $"Panelists           {s.Panelists.Count}",
            string.Empty,
            "── THIS COMPUTER ────────────────────────────",
            $"IP                  {s.LocalIp}",
            $"Interface           {s.InterfaceName}",
            $"MAC                 {s.Mac}{(s.MacIsRandomized ? "   " + words.RandomizedMacTag : string.Empty)}",
            $"machineId           {s.MachineId}",
            $"Type                {s.Profile.DeviceType}",
            $"Brand               {s.Profile.Brand}",
            $"Model               {s.Profile.Model}",
            $"Operating system    {s.Profile.OperatingSystem}",
            $"Build               {s.Profile.OsVersion}",
            string.Empty,
            "── REGISTRATION ─────────────────────────────",
            $"Owner(s)            {owners}",
            $"Mainly used by      {(s.PrincipalOwnerName.Length == 0 ? "(the only owner)" : s.PrincipalOwnerName)}",
            $"Device ID           {(s.DeviceId.Length == 0 ? "(not issued by the panel yet)" : s.DeviceId)}",
            $"Last sent           {registered}",
            $"Next report         {s.NextReportDue?.ToString("yyyy-MM-dd HH:mm:ss") ?? "when the address or the device data changes"}",
            $"HTTPS certificate   {(s.CertificateTrusted ? "installed and trusted" : "not trusted on this computer")}",
            $"Proxy               {s.ProxyStatus}",
            $"OS accepted by API  {(s.DeviceOsAccepted ? "yes" : "no — the backend does not accept deviceOs yet")}",
            string.Empty,
            $"Log file            {Log.FilePath}",
            $"Outbox file         {Agent.Outbox.FilePath}",
        ];

        return string.Join(Environment.NewLine, lines);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
