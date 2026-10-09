using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// What the agent remembers between starts. Above all: who the panelist said this
/// computer belongs to, so the question is not asked again on every restart. It lives in
/// the user's profile because the answer is per session: two accounts on the same
/// computer are two different people in the household.
/// </summary>
public sealed class AgentState
{
    [JsonPropertyName("routerMac")] public string RouterMac { get; set; } = string.Empty;

    /// <summary>
    /// The identity MAC as the node itself reports it in <c>router_info.sh</c>.
    /// </summary>
    /// <remarks>
    /// Not the same source as <see cref="RouterMac"/>, which is what the <i>backend</i> echoes
    /// for the router record, and that is the whole reason this field exists. Everything that
    /// asks "is the node in front of me the one this computer is registered on?" — the proxy in
    /// the cycle, the proxy watchdog, the Windows scheduled task, the macOS root helper — gets
    /// its answer from <c>router_info.sh</c>, so the value it is compared against has to come
    /// from there too. The two agree in every case anyone has seen, because the household is
    /// looked up <i>by</i> this MAC; comparing across the two sources anyway would mean a
    /// backend that echoed the MAC in some way <c>NormalizeMac</c> cannot undo would turn the
    /// proxy off at home and refuse the device write there. That failure is silent, so it is
    /// worth not depending on an agreement nothing enforces.
    /// </remarks>
    [JsonPropertyName("nodeMac")] public string NodeMac { get; set; } = string.Empty;
    [JsonPropertyName("routerId")] public string RouterId { get; set; } = string.Empty;
    [JsonPropertyName("householdId")] public string HouseholdId { get; set; } = string.Empty;
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = string.Empty;
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;
    [JsonPropertyName("lastIp")] public string LastIp { get; set; } = string.Empty;
    [JsonPropertyName("friendlyName")] public string FriendlyName { get; set; } = string.Empty;
    [JsonPropertyName("panelistIds")] public List<string> PanelistIds { get; set; } = [];
    [JsonPropertyName("panelistNames")] public List<string> PanelistNames { get; set; } = [];

    /// <summary>
    /// Which of <see cref="PanelistIds"/> the computer is mainly for. Empty when only
    /// one person uses it, because then there is nothing to choose between.
    /// </summary>
    [JsonPropertyName("principalPanelistId")] public string PrincipalPanelistId { get; set; } = string.Empty;
    [JsonPropertyName("deviceType")] public string DeviceType { get; set; } = string.Empty;
    [JsonPropertyName("brand")] public string Brand { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("lastRegisteredAt")] public DateTimeOffset? LastRegisteredAt { get; set; }

    /// <summary>
    /// Fingerprint of the last assignment that actually reached the backend. The cycle
    /// compares it to decide whether there is anything new to say: without it the agent
    /// cannot tell "the IP is still 192.168.0.10" from "I have never sent 192.168.0.10".
    /// </summary>
    [JsonPropertyName("lastSentSignature")] public string LastSentSignature { get; set; } = string.Empty;

    // ── presenceCreateDevice idempotency ─────────────────────────────────────

    /// <summary>
    /// The idempotency key of the write currently intended, saved before it is sent.
    /// </summary>
    /// <remarks>
    /// <c>presenceCreateDevice</c> keys retries on <c>requestId</c> alone — not on
    /// <c>machineId</c> — so the key has to outlive the process. A create whose answer is
    /// lost on the wire (timeout, sleep, Wi-Fi gone) may already have been accepted; the
    /// only way to find out is to resend under the same key and let the server return the
    /// original payload. Generating a fresh key instead would insert a second Device for
    /// the same computer, which is exactly what the device id exists to prevent.
    /// </remarks>
    [JsonPropertyName("deviceRequestId")] public string DeviceRequestId { get; set; } = string.Empty;

    /// <summary>
    /// The fingerprint <see cref="DeviceRequestId"/> belongs to. A key is reused only for
    /// the write it was made for: reusing it for different data takes the server's retry
    /// path, which answers with the original payload and quietly discards the change.
    /// </summary>
    [JsonPropertyName("deviceRequestSignature")] public string DeviceRequestSignature { get; set; } = string.Empty;

    /// <summary>
    /// The id of the address observation currently intended, saved before it is sent.
    /// </summary>
    /// <remarks>
    /// The same contract as <see cref="DeviceRequestId"/>, for a different store:
    /// <c>presenceReportDeviceIp</c> keys receipts on <c>reportId</c> with a global unique
    /// index, so one id can only ever record one observation.
    ///
    /// This is what makes "send the first time and on every change" exact. A new address is a
    /// new observation and mints a new id, which closes the previous Local IP Period and opens
    /// one at the same instant. Resending the *same* observation — because the first attempt
    /// timed out — reuses the id, and the backend returns the original period instead of
    /// opening a second one for something that happened once.
    /// </remarks>
    [JsonPropertyName("ipReportId")] public string IpReportId { get; set; } = string.Empty;

    /// <summary>The observation <see cref="IpReportId"/> belongs to.</summary>
    [JsonPropertyName("ipReportSignature")] public string IpReportSignature { get; set; } = string.Empty;

    /// <summary>
    /// When the panelist finished the setup assistant, or null while they never have.
    /// </summary>
    /// <remarks>
    /// This is the whole "first run" test: with nothing here, the assistant opens itself on
    /// start-up. It is set by <c>Finish</c>, not by closing the window, so a panelist who
    /// shuts it to deal with it later is asked again next time — the same way the owner
    /// dialog already reopens while nobody has said whose computer this is.
    ///
    /// Not cleared by <see cref="Clear()"/>: "forget the owner" is a support instruction
    /// about one answer, and walking somebody through the certificate and the proxy again
    /// because of it would ask for an administrator password they already gave.
    /// </remarks>
    [JsonPropertyName("setupCompletedAt")] public DateTimeOffset? SetupCompletedAt { get; set; }

    // ── System proxy switch ──────────────────────────────────────────────────
    // Not touched by Clear(): forgetting the owner must not flip the panelist's proxy.

    /// <summary>What the panelist chose with the switch.</summary>
    [JsonPropertyName("proxyWanted")] public bool ProxyWanted { get; set; }

    /// <summary>true while the proxy set by the agent is active in Windows.</summary>
    [JsonPropertyName("proxyApplied")] public bool ProxyApplied { get; set; }

    /// <summary>"ip:port" the agent last wrote, to detect changes made in Windows Settings.</summary>
    [JsonPropertyName("proxyAddress")] public string ProxyAddress { get; set; } = string.Empty;

    /// <summary>The panelist's own proxy settings before the agent changed them, restored on switch-off.</summary>
    [JsonPropertyName("proxyPreviousFlags")] public int? ProxyPreviousFlags { get; set; }
    [JsonPropertyName("proxyPreviousServer")] public string ProxyPreviousServer { get; set; } = string.Empty;
    [JsonPropertyName("proxyPreviousBypass")] public string ProxyPreviousBypass { get; set; } = string.Empty;

    public static string Path => System.IO.Path.Combine(AgentPaths.UserData, "state.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// A declared owner is only valid for the household and the computer it was declared
    /// on. If the panelist moves router or changes the NIC, the question must be asked again.
    /// </summary>
    /// <remarks>
    /// Names without IDs count as an owner. They only happen in dry run, where there is
    /// no household list to pick from and the panelist types the names: the IDs are
    /// filtered out of the payload, so requiring them here meant a dry-run owner was
    /// declared, saved, and then forgotten on the very next cycle. Whether typed names
    /// are good enough for the situation is <c>HasUsableOwner</c>'s decision, and with a
    /// household list in hand it still says no.
    /// </remarks>
    /// <summary>
    /// What a probe should expect the node to answer with, falling back to the router record's
    /// MAC for a registration written before <see cref="NodeMac"/> was recorded.
    /// </summary>
    [JsonIgnore]
    public string ExpectedNodeMac => NodeMac.Length > 0 ? NodeMac : RouterMac;

    /// <summary>
    /// Whether a node answering with this MAC is the one this computer is registered on.
    /// </summary>
    /// <remarks>
    /// True while nothing has been registered: there is nothing yet to be foreign to, and a
    /// fresh install has to be able to use the node in front of it. It is the proxy's question
    /// — measuring through another household's meter files this computer's traffic under their
    /// home — and deliberately not the cycle's: reading that node's household is exactly how a
    /// panelist who has moved gets asked who this computer belongs to here.
    ///
    /// Also true when either MAC is not one, because "unreadable" is not "somebody else's": the
    /// node is only reached at all after <c>router_info.sh</c> returned a valid MAC, and failing
    /// closed on a parse would turn the proxy off at home.
    /// </remarks>
    public bool IsRegisteredOnNode(string nodeMac)
    {
        var ours = Personicore.DeviceAgent.Net.LocalNetwork.NormalizeMac(ExpectedNodeMac);
        var here = Personicore.DeviceAgent.Net.LocalNetwork.NormalizeMac(nodeMac);

        if (ours.Length != 17 || here.Length != 17) return true;

        return string.Equals(ours, here, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasOwnerFor(string routerMac, string machineId) =>
        (PanelistIds.Count > 0 || PanelistNames.Count > 0)
        && string.Equals(RouterMac, routerMac, StringComparison.OrdinalIgnoreCase)
        && string.Equals(MachineId, machineId, StringComparison.Ordinal);

    public static AgentState Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AgentState();
            return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(Path), JsonOptions) ?? new AgentState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Unreadable state, starting fresh: {ex.Message}");
            return new AgentState();
        }
    }

    // The lock covers read/merge/write across the resident agent and watchdog.
    // Hash the user-specific path so different accounts never share the same lock.
    private static readonly string MutexName =
        (OperatingSystem.IsWindows() ? @"Global\" : string.Empty)
        + "Personicore.State." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path)));

    private static void WithStateLock(Action action)
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Timed out waiting to save agent state.");
            action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Saves everything the agent owns, taking the proxy switch from the file rather than
    /// from memory.
    /// </summary>
    /// <remarks>
    /// The proxy watchdog is a separate process — a scheduled task on Windows, a
    /// LaunchDaemon on macOS — and it restores or re-points the proxy while the agent
    /// runs. Writing this object whole would put the agent's minutes-old idea of the
    /// switch back over the watchdog's work, and the agent would then read its own stale
    /// values as the panelist having changed the proxy by hand. Registration goes through
    /// here; the switch goes through <see cref="SaveProxy"/>.
    /// </remarks>
    public void Save()
    {
        WithStateLock(() =>
        {
            // Merge into a copy: saving registration must not overwrite proxy changes
            // another thread has made in memory but has not persisted yet.
            var merged = JsonSerializer.Deserialize<AgentState>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
            if (ReadFile() is { } disk) merged.CopyProxyFrom(disk);
            Write(merged);
        });
    }

    /// <summary>
    /// Saves only the proxy switch, keeping the registration the file already holds. The
    /// other half of <see cref="Save"/>: the watchdog process loaded its copy a minute
    /// ago and must not put back an owner the agent has changed since.
    /// </summary>
    public void SaveProxy()
    {
        WithStateLock(() =>
        {
            var merged = ReadFile() ?? this;
            if (!ReferenceEquals(merged, this)) merged.CopyProxyFrom(this);
            Write(merged);
        });
    }

    /// <summary>
    /// Re-reads the proxy switch from the file, discarding what is in memory. Called
    /// before deciding whether a setting the agent did not write means the panelist took
    /// the proxy over: while the agent slept, the watchdog may have restored or moved it.
    /// </summary>
    public void ReloadProxy()
    {
        WithStateLock(() =>
        {
            if (ReadFile() is { } disk) CopyProxyFrom(disk);
        });
    }

    /// <summary>The file as it is right now, or null when there is nothing readable.</summary>
    private static AgentState? ReadFile()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(Path), JsonOptions);
        }
        catch (Exception ex)
        {
            // Unreadable is not empty: the caller keeps what it has rather than treating
            // a damaged file as "the proxy was never applied".
            Log.Debug($"Could not re-read the state file: {ex.Message}");
            return null;
        }
    }

    private void CopyProxyFrom(AgentState other)
    {
        ProxyWanted = other.ProxyWanted;
        ProxyApplied = other.ProxyApplied;
        ProxyAddress = other.ProxyAddress;
        ProxyPreviousFlags = other.ProxyPreviousFlags;
        ProxyPreviousServer = other.ProxyPreviousServer;
        ProxyPreviousBypass = other.ProxyPreviousBypass;
    }

    private static void Write(AgentState state)
    {
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            System.IO.Directory.CreateDirectory(directory);

            // Atomic write: a power cut halfway must not leave a truncated JSON that
            // forces the panelist to declare the owner all over again.
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save agent state", ex);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Forgets the declared owner so the question is asked again.
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceRequestId"/> survives, like the proxy switch. It is the other half
    /// of the device id: if a create was accepted but its answer never arrived, that key is
    /// the only thing that can still claim the row the panel already holds. Clearing it
    /// would turn "ask who this computer belongs to again" into a second Device in the
    /// household, which is the one outcome the id machinery exists to prevent.
    ///
    /// <see cref="IpReportId"/> survives for the same reason: an observation the backend
    /// accepted but never acknowledged can only be reclaimed under its own id.
    /// </remarks>
    public void Clear()
    {
        RouterMac = NodeMac = RouterId = HouseholdId = MachineId = Mac = LastIp = FriendlyName = string.Empty;
        DeviceType = Brand = Model = Os = string.Empty;
        PanelistIds = [];
        PanelistNames = [];
        PrincipalPanelistId = string.Empty;
        LastRegisteredAt = null;
        Save();
    }

    /// <summary>
    /// The idempotency key for a write whose data fingerprints as <paramref name="signature"/>,
    /// minted and persisted the first time that fingerprint is seen and returned unchanged
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// Persisted here rather than by the caller after the fact, because the ordering is the
    /// whole point: the key has to be on disk before the request leaves, or a crash between
    /// the send and the save loses exactly the key the retry needed.
    /// </remarks>
    public string RequestIdFor(string signature)
    {
        if (DeviceRequestId.Length > 0
            && string.Equals(DeviceRequestSignature, signature, StringComparison.Ordinal))
        {
            return DeviceRequestId;
        }

        DeviceRequestId = Guid.NewGuid().ToString();
        DeviceRequestSignature = signature;
        Save();

        return DeviceRequestId;
    }

    /// <summary>
    /// The report id for an address observation, minted and persisted the first time that
    /// observation is seen and returned unchanged while it is still the current one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RequestIdFor"/> and not a shared helper on purpose: the two keys
    /// answer to different stores with different lifetimes, and a change of inventory must not
    /// mint a new address observation, nor a change of address a new device write.
    /// </remarks>
    public string IpReportIdFor(string signature)
    {
        if (IpReportId.Length > 0
            && string.Equals(IpReportSignature, signature, StringComparison.Ordinal))
        {
            return IpReportId;
        }

        IpReportId = Guid.NewGuid().ToString();
        IpReportSignature = signature;
        Save();

        return IpReportId;
    }
}
