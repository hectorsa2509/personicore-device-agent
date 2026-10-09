using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Personicore.DeviceAgent.Config;

/// <summary>
/// Agent configuration. Read from %PROGRAMDATA%\Personicore\DeviceAgent\config.json
/// and, if it exists, overridden by %LOCALAPPDATA%\Personicore\DeviceAgent\config.json
/// (handy for single-user testing without administrator rights).
/// Every value has a sane default: the agent starts with no file at all.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>The name the meter node resolves for itself through dnsmasq.</summary>
    public string RouterHost { get; set; } = "router.example.invalid";

    public int RouterPort { get; set; } = 80;

    /// <summary>Timeout per attempt against the node (discovery and CGI).</summary>
    public int RouterTimeoutSeconds { get; set; } = 4;

    /// <summary>
    /// How often household / IP / assignment are revalidated. One hour: the cadence asked for
    /// y encaja con el nodo, que publica su bloque `clients` cada 300 s — no hace falta
    /// re-assert more often than that. A change of network addressing brings the cycle
    /// forward without waiting for this interval to expire.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 3600;

/// <summary>
    /// Whether the agent is allowed to write to the backend at all.
    ///
    /// <b>On.</b> It was off for as long as there was no contract to send against; the
    /// device contract now exists (<c>presenceCreateDevice</c>), so the shipped package
    /// registers for real. Which backend it registers against is decided when the package
    /// is built — see <c>build/write-packaged-env.sh</c> — and the build prints the
    /// endpoint for exactly that reason.
    ///
    /// Setting it to false in config.json still works and is the thing to reach for before
    /// pointing a build at production: the agent does every step, works out precisely what
    /// it would send, and writes that to <see cref="Agent.Outbox"/> on the desktop instead
    /// of POSTing it. The tray reads "Simulated" and offers the file. The same builders
    /// produce the dump and the real request, so what the file shows is what would travel.
    /// </summary>
    public bool SendToBackend { get; set; } = true;

    /// <summary>
    /// How often the assignment is re-sent when nothing has changed. <b>0 disables it</b>,
    /// and 0 is the default: reporting is driven by change, not by a clock.
    ///
    /// It used to be twelve hours, which meant two identical reports a day for a computer
    /// that had held the same lease all week — the panel learned nothing from either, and a
    /// genuinely new address still waited for the next cycle to be noticed. The address is
    /// now watched directly (<see cref="IpWatchIntervalSeconds"/>), so the clock has
    /// nothing left to add.
    ///
    /// Set a number of seconds to bring the heartbeat back. The one reason to: it is the
    /// only signal that separates "this address is still current" from "this agent stopped
    /// running three weeks ago", and whether the backend needs that distinction is the
    /// backend's call (docs/GRAPHQL-CONTRACT.md §6.3).
    /// </summary>
    public int ReportIntervalSeconds { get; set; }

    /// <summary>
    /// How often the computer's own address is checked against the one last reported.
    ///
    /// Thirty seconds, and it is cheap enough to be: it resolves the route to the node with
    /// a connected UDP socket, which sends no packet — the stack just assigns a local IP —
    /// and compares it to a string. No request to the node, none to the backend.
    ///
    /// It exists because <c>NetworkChange.NetworkAddressChanged</c> is not enough on its
    /// own. It is what both agents already subscribe to, and on Windows it is reliable; on
    /// macOS a DHCP renewal that lands on a different address does not always surface as an
    /// interface event, and the IP would then sit wrong until the hourly cycle. Watching the
    /// address directly does not depend on the OS telling us.
    ///
    /// It only ever wakes the cycle; what to send is still the cycle's decision.
    /// </summary>
    public int IpWatchIntervalSeconds { get; set; } = 30;

    /// <summary>Wait after a failed cycle (node off, no network, backend down).</summary>
    public int RetryIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How often the node is checked at the address the proxy points to, while the proxy
    /// is on. It is the difference between the
    /// panelista note un corte o no: si el nodo se reinicia y el DHCP le da otra IP, el
    /// the proxy points at a dead address and there is no browsing until somebody
    /// notices. One HTTP request on the LAN every half minute is cheap; the full cycle
    /// (household, assignment, sweep) stays hourly.
    /// </summary>
    public int ProxyWatchIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// If the node does not answer a ping, sweep the local segment looking for who has
    /// the discovery port open. Turning it off leaves the agent depending solely on the
    /// name answering.
    /// </summary>
    public bool SubnetSweepEnabled { get; set; } = true;

    /// <summary>Concurrent TCP probes during the sweep.</summary>
    public int SubnetSweepConcurrency { get; set; } = 128;

    /// <summary>
    /// Ports that give a meter node away, cheapest signal first. A TCP connect is much
    /// cheaper than an HTTP request to every address on the network, so the sweep filters
    /// on these and only then asks router_info.sh.
    ///
    /// Three, not one, so that closing any single service does not make the node
    /// undiscoverable: 22 (SSH, open in the stock firmware), 3129 (the Squid explicit
    /// port) and 80 (the node's own web UI). A host only counts as a candidate, never as
    /// a node: router_info.sh is what decides.
    /// </summary>
    public int[] DiscoveryPorts { get; set; } = [22, 3129, 80];

    /// <summary>
    /// How long each probe waits. Deliberately short: on the LAN a live machine
    /// answers in milliseconds, and this value multiplies the sweep's total time.
    /// </summary>
    public int PortProbeTimeoutMs { get; set; } = 400;

    /// <summary>
    /// Which mutation records this computer.
    ///
    /// <list type="table">
    /// <item><term>device</term><description>
    ///   <c>presenceCreateDevice</c> alone. Creates the Device on the first write and
    ///   updates it afterwards, and is the only call that answers with the device's own id.
    ///   <b>It reports no address at all</b> — there is no <c>ip</c> field in its input — so
    ///   it covers half of what this agent exists for. Useful to isolate the device write
    ///   while testing; not what to ship.
    /// </description></item>
    /// <item><term>deviceAndIp</term><description>
    ///   <b>The default.</b> <c>presenceCreateDevice</c> for the computer, then
    ///   <c>presenceReportDeviceIp</c> for its address — the pair this contract intends. The
    ///   order is not optional: the IP report never creates a subject, so it has nothing to
    ///   attach an address to until the device exists.
    /// </description></item>
    /// <item><term>deviceAndRouterIps</term><description>
    ///   <c>presenceCreateDevice</c> plus the older <c>routerIpsAssigment</c>, which is
    ///   operators mapping addresses to panelists rather than device occupancy. It was the
    ///   default while <c>presenceReportDeviceIp</c> was undeployed; keep it only for a backend
    ///   that still lacks that mutation.
    /// </description></item>
    /// <item><term>routerIps</term><description>
    ///   <c>routerIpsAssigment</c> alone — what the router's own web page does. No device id.
    /// </description></item>
    /// <item><term>personalDevice</term><description>
    ///   <c>presenceCreatePersonalDevice</c>. Superseded by <c>device</c>; kept because it
    ///   is what some households were registered with.
    /// </description></item>
    /// <item><term>both</term><description><c>routerIps</c> + <c>personalDevice</c>.</description></item>
    /// </list>
    ///
    /// See docs/GRAPHQL-CONTRACT.md.
    /// </summary>
    public string RegistrationMode { get; set; } = "deviceAndIp";

    /// <summary>
    /// Send the operating system as a field of its own (deviceOs) in IPInput. If the
    /// backend does not accept it yet the agent notices, drops the flag in memory and
    /// retries without the field. See docs/GRAPHQL-CONTRACT.md.
    /// </summary>
    public bool SendDeviceOs { get; set; } = true;

    /// <summary>Open the owner dialog as soon as an unregistered household is found.</summary>
    public bool PromptOnFirstRun { get; set; } = true;

    /// <summary>
    /// Let the device write go out on a meter node other than the one that issued this
    /// computer's device id.
    /// </summary>
    /// <remarks>
    /// Off, because what the backend does with a <c>deviceId</c> sent under a different
    /// <c>routerId</c> is not verified, and two of the three possible answers lose data: it
    /// re-homes the row into the other household, or it answers <i>The device doesn't exist!</i>
    /// — which the agent is entitled to read as "support deleted this row", let the id go and
    /// create again, one orphaned row per return trip. Refusing to write is the only answer
    /// that cannot lose anything, and the panelist is told the computer is registered on
    /// another router.
    ///
    /// Turn it on for the one case where the write is genuinely wanted: support replaced the
    /// node record in the same household and the Device has to follow it. Even then the id is
    /// never let go of — see <c>DeviceIdStore.Forget</c>'s single trigger — so the worst a
    /// wrong guess can do is a refused mutation. See §7 of docs/GRAPHQL-CONTRACT.md.
    /// </remarks>
    public bool AllowNodeChange { get; set; }

    /// <summary>debug | info | warn | error</summary>
    public string LogLevel { get; set; } = "info";

    [JsonIgnore]
    public string RouterBaseUrl => $"http://{RouterHost}:{RouterPort}";

    public static string MachineConfigPath => Path.Combine(AgentPaths.MachineData, "config.json");

    public static string UserConfigPath => Path.Combine(AgentPaths.UserData, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // Support edits this file by hand in Notepad: a comment or a trailing comma
        // should not be enough to lose the whole configuration.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the configuration. An unreadable file does not stop the agent starting: the
    /// problem is recorded in <paramref name="problems"/> and the defaults are used.
    /// </summary>
    /// <remarks>
    /// The two files are merged property by property and deserialized once, rather than
    /// each being deserialized on its own with the last one winning. With the latter, a
    /// user file holding a single key —<c>{ "logLevel": "debug" }</c> is the one support
    /// asks a panelist for— put every other setting back to its default, <c>sendToBackend</c>
    /// among them: turning on diagnostics stopped the reporting it was meant to diagnose.
    /// </remarks>
    public static AgentConfig Load(out IReadOnlyList<string> problems)
    {
        var found = new List<string>();

        // Case-insensitive so that "logLevel" in one file and "LogLevel" in the other are
        // the same setting here, as they already are when deserializing.
        var merged = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });

        foreach (var path in new[] { MachineConfigPath, UserConfigPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is not JsonObject layer)
                {
                    found.Add($"{path} is not a JSON object; ignoring it.");
                    continue;
                }

                // Validate the complete candidate before committing a layer. A bad
                // user override must not discard valid machine-wide configuration.
                var candidate = (JsonObject)merged.DeepClone();
                foreach (var property in layer)
                    candidate[property.Key] = property.Value?.DeepClone();
                _ = candidate.Deserialize<AgentConfig>(JsonOptions);
                merged = candidate;
            }
            catch (Exception ex)
            {
                found.Add($"Could not read {path}: {ex.Message}");
            }
        }

        var config = new AgentConfig();

        try
        {
            config = merged.Deserialize<AgentConfig>(JsonOptions) ?? config;
        }
        catch (Exception ex)
        {
            // A wrong type in one key must not take the rest with it silently.
            found.Add($"Could not apply the configuration: {ex.Message}");
            config = new AgentConfig();
        }

        config.Sanitize(found);
        problems = found;
        return config;
    }

    /// <summary>
    /// A `null` or a zero in the JSON must not bring the agent down: a 0 s interval would
    /// leave it sweeping the network in a tight loop, and a null string would throw at
    /// start-up. It is corrected to the default and recorded in the log.
    /// </summary>
    private void Sanitize(List<string> problems)
    {
        var defaults = new AgentConfig();

        string Text(string? value, string fallback, string name)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            if (value is not null) problems.Add($"{name} is empty; using \"{fallback}\".");
            return fallback;
        }

        int AtLeast(int value, int minimum, int fallback, string name)
        {
            if (value >= minimum) return value;
            problems.Add($"{name}={value} is invalid (minimum {minimum}); using {fallback}.");
            return fallback;
        }

        string OneOf(string? value, string[] allowed, string fallback, string name)
        {
            var text = Text(value, fallback, name);
            if (allowed.Contains(text, StringComparer.OrdinalIgnoreCase)) return text;

            // A typo here used to be silent and expensive: an unknown mode sent nothing
            // at all while the agent went on reporting itself as registered.
            problems.Add($"{name}=\"{text}\" is not one of [{string.Join(", ", allowed)}]; using \"{fallback}\".");
            return fallback;
        }

        RouterHost = Text(RouterHost, defaults.RouterHost, "routerHost");
        RegistrationMode = OneOf(
            RegistrationMode,
            ["deviceAndIp", "device", "deviceAndRouterIps", "routerIps", "personalDevice", "both"],
            defaults.RegistrationMode, "registrationMode");
        LogLevel = Text(LogLevel, defaults.LogLevel, "logLevel");

        RouterPort = RouterPort is > 0 and <= 65535 ? RouterPort : AtLeast(-1, 1, defaults.RouterPort, "routerPort");
        RouterTimeoutSeconds = AtLeast(RouterTimeoutSeconds, 1, defaults.RouterTimeoutSeconds, "routerTimeoutSeconds");
        PollIntervalSeconds = AtLeast(PollIntervalSeconds, 60, defaults.PollIntervalSeconds, "pollIntervalSeconds");
        RetryIntervalSeconds = AtLeast(RetryIntervalSeconds, 10, defaults.RetryIntervalSeconds, "retryIntervalSeconds");

        // 0 is "no periodic report at all" and is the default. Anything between 1 and 59
        // is somebody expecting a heartbeat and getting one on every cycle instead, which
        // is what this setting exists to prevent, so it is corrected rather than honoured.
        if (ReportIntervalSeconds < 0 || (ReportIntervalSeconds is > 0 and < 60))
        {
            problems.Add($"reportIntervalSeconds={ReportIntervalSeconds} is invalid "
                         + "(0 to disable, otherwise at least 60); using 0.");
            ReportIntervalSeconds = 0;
        }

        // Five seconds is already more often than any DHCP lease changes; below that it is
        // a typo, not a decision.
        IpWatchIntervalSeconds = AtLeast(IpWatchIntervalSeconds, 5, defaults.IpWatchIntervalSeconds, "ipWatchIntervalSeconds");
        ProxyWatchIntervalSeconds = AtLeast(ProxyWatchIntervalSeconds, 10, defaults.ProxyWatchIntervalSeconds, "proxyWatchIntervalSeconds");
        SubnetSweepConcurrency = AtLeast(SubnetSweepConcurrency, 1, defaults.SubnetSweepConcurrency, "subnetSweepConcurrency");
        PortProbeTimeoutMs = AtLeast(PortProbeTimeoutMs, 50, defaults.PortProbeTimeoutMs, "portProbeTimeoutMs");

        var ports = (DiscoveryPorts ?? []).Where(p => p is > 0 and <= 65535).Distinct().ToArray();
        if (ports.Length == 0)
        {
            problems.Add("discoveryPorts empty or invalid; using [22].");
            ports = defaults.DiscoveryPorts;
        }
        DiscoveryPorts = ports;
    }
}
