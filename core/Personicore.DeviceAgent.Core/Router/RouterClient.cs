using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Net;

namespace Personicore.DeviceAgent.Router;

/// <summary>
/// Client for the meter node's CGIs. Everything is plain HTTP inside the LAN:
/// el nodo es un puente transparente y no expone TLS.
/// </summary>
public sealed class RouterClient : IDisposable
{
    private readonly AgentConfig _config;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RouterClient(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient(new SocketsHttpHandler
        {
            // The node may be down; we do not want hung connections in the pool.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
            // Windows applies the system proxy (IE/WPAD settings) to private IPs too by
            // default: on a machine with a corporate proxy, requests to the node would end
            // up at the proxy and never reach the LAN.
            UseProxy = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(config.RouterTimeoutSeconds),
        };
    }

    /// <summary>
    /// Finds the meter node in three steps, cheapest to most expensive:
    ///
    ///   1. <b>ping</b> `router.example.invalid`. The node resolves that name for
    ///      itself through dnsmasq, so an answer means it is in front of us.
    ///   2. if it does not answer, <b>sweep the local segment for an open port 22</b>.
    ///      A TCP connect is far cheaper than an HTTP request, so filtering on SSH first
    ///      turns a sweep of hundreds of requests into a handful.
    ///   3. on those few candidates, go in over HTTP to `router_info.sh` for the router's
    ///      MAC, which is what identifies the household.
    ///
    /// Step 2 depends on the node having SSH listening — the factory firmware does. If a
    /// deployment ever closed it, `discoveryPorts` has to be adjusted.
    /// </summary>
    public async Task<RouterNode?> DiscoverAsync(CancellationToken ct)
    {
        if (await RespondsToPingAsync(_config.RouterHost, ct).ConfigureAwait(false))
        {
            var byName = await IdentifyAsync(_config.RouterBaseUrl, ct).ConfigureAwait(false);
            if (byName is not null)
            {
                Log.Info($"Meter node resolved by name: {_config.RouterHost} · mac={byName.Info.IdentityMac}");
                return byName with { ViaDns = true };
            }

            Log.Warn($"{_config.RouterHost} answers ping but does not serve router_info.sh; sweeping the network.");
        }

        if (!_config.SubnetSweepEnabled)
        {
            Log.Warn("The name does not respond and network sweep is disabled.");
            return null;
        }

        var candidates = await ScanForOpenPortAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            Log.Warn($"Sweep finished: no device with {DescribePorts()} open on the local network.");
            return null;
        }

        Log.Info($"Candidates with {DescribePorts()} open: {string.Join(", ", candidates)}");

        foreach (var address in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var node = await IdentifyAsync($"http://{address}:{_config.RouterPort}", ct).ConfigureAwait(false);
            if (node is null) continue;

            Log.Info($"Meter node found at {address} · mac={node.Info.IdentityMac}");
            return node;
        }

        Log.Warn("None of the candidates served router_info.sh: they are not meter nodes.");
        return null;
    }

    /// <summary>
    /// ICMP ping to the name. It also fails when the name does not resolve, which for
    /// our purposes is the same thing: the node is not in front of us. No privileges
    /// needed: the Ping class uses IcmpSendEcho2, not a raw socket.
    /// </summary>
    private async Task<bool> RespondsToPingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var timeout = Math.Max(500, _config.RouterTimeoutSeconds * 1000);
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(timeout), cancellationToken: ct)
                .ConfigureAwait(false);

            Log.Debug($"Ping to {host}: {reply.Status}" +
                      (reply.Status == IPStatus.Success ? $" from {reply.Address} in {reply.RoundtripTime} ms" : string.Empty));

            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or SocketException)
        {
            Log.Debug($"Ping to {host} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sweeps the local segment looking for whoever has the discovery port open. It
    /// probes with a bare TCP connect and a short timeout, in parallel: on a typical /24
    /// it finishes in under a second. It returns every hit, with the IPs the node's name
    /// resolves to first — even when the ping went unanswered that is still the most
    /// likely address, so it is validated before the rest.
    /// </summary>
    private async Task<IReadOnlyList<IPAddress>> ScanForOpenPortAsync(CancellationToken ct)
    {
        var targets = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        // 1. The addresses the name resolves to. Tried first even when the ping failed:
        //    it is still the most likely address, and the name is a public record that
        //    points at the node's private IP.
        var resolved = await ResolveHostAsync(_config.RouterHost, ct).ConfigureAwait(false);
        foreach (var address in resolved)
            if (seen.Add(address)) targets.Add(address);

        // 2. The default gateway: in deployments where the node is the gateway, this is
        //    a one-probe hit instead of a whole sweep.
        var gateway = LocalNetwork.DefaultGateway();
        if (gateway is not null && seen.Add(gateway)) targets.Add(gateway);

        // 3. Every local segment, with the adapter's real mask.
        foreach (var subnet in LocalNetwork.ActiveSubnets())
        {
            Log.Info($"Sweeping {subnet.Network}/{subnet.PrefixLength} " +
                     $"({subnet.HostCount} addresses) via {subnet.InterfaceName}.");

            foreach (var address in subnet.Hosts())
                if (seen.Add(address)) targets.Add(address);
        }

        // 4. The /24 around each resolved address. The node is not always on the
        //    panelist's own segment —it can sit behind the ISP router on another one,
        //    reachable by routing— and then nothing above would reach it.
        foreach (var address in resolved)
        {
            foreach (var neighbour in NeighboursOf(address))
                if (seen.Add(neighbour)) targets.Add(neighbour);
        }

        if (targets.Count == 0)
        {
            Log.Warn("No usable IPv4 segment to sweep.");
            return [];
        }

        var open = new List<IPAddress>();
        var gate = new object();
        using var slots = new SemaphoreSlim(Math.Max(1, _config.SubnetSweepConcurrency));

        await Task.WhenAll(targets.Select(async address =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!await IsPortOpenAsync(address, ct).ConfigureAwait(false)) return;

                lock (gate) open.Add(address);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);

        // Se devuelve en el orden de sondeo, no en el de llegada: primero el nombre, luego
        // the segment in order. A stable result keeps the logs comparable across cycles.
        return [.. targets.Where(open.Contains)];
    }

    /// <summary>
    /// Probes this address on every discovery port <b>at the same time</b>. In sequence,
    /// each extra port would multiply the time an absent host costs (one timeout each);
    /// in parallel a dead address still costs one timeout, so more ports make discovery
    /// more robust without making the sweep slower.
    /// </summary>
    private async Task<bool> IsPortOpenAsync(IPAddress address, CancellationToken ct)
    {
        var probes = _config.DiscoveryPorts.Select(port => ProbeAsync(address, port, ct)).ToArray();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return results.Any(open => open);
    }

    private async Task<bool> ProbeAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(_config.PortProbeTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await socket.ConnectAsync(address, port, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // No answer within the timeout: filtered port or no host there.
            return false;
        }
        catch (SocketException)
        {
            // Refused or unreachable: not the node either.
            return false;
        }
    }

    /// <summary>The 254 usable addresses of the /24 that contains <paramref name="address"/>.</summary>
    private static IEnumerable<IPAddress> NeighboursOf(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) yield break;

        var network = LocalSubnet.ToUInt32(address) & 0xFFFFFF00u;
        for (var host = 1u; host <= 254u; host++) yield return LocalSubnet.FromUInt32(network + host);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return [.. addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            Log.Debug($"{host} does not resolve: {ex.Message}");
            return [];
        }
    }

    private string DescribePorts() =>
        _config.DiscoveryPorts.Length == 1
            ? $"port {_config.DiscoveryPorts[0]}"
            : $"any of ports {string.Join('/', _config.DiscoveryPorts)}";

    /// <summary>
    /// Ask whatever is at this address who it is. Returns null for anything that is not
    /// a meter node, whatever the reason.
    /// </summary>
    public async Task<RouterNode?> IdentifyAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            var info = await GetJsonAsync<RouterInfo>($"{baseUrl}/cgi-bin/router_info.sh", ct)
                .ConfigureAwait(false);

            if (info is null) return null;
            if (LocalNetwork.NormalizeMac(info.IdentityMac).Length != 17) return null;

            return new RouterNode(baseUrl, info, ViaDns: false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything with port 22 open reaches this point: printers, NAS boxes, routers
            // with a web page of their own. Whatever they answer, it is not a node.
            Log.Debug($"{baseUrl} is not a meter node: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Task<RouterConfig?> GetConfigAsync(RouterNode node, CancellationToken ct) =>
        GetJsonAsync<RouterConfig>($"{node.BaseUrl}/cgi-bin/get_config.sh", ct);

    /// <summary>
    /// The inventory the node sees on the bridge. It is how this computer finds out
    /// which MAC the node sees it with: look for the entry whose IP matches the local IP
    /// of the route towards the node.
    /// </summary>
    public async Task<IReadOnlyList<RouterClientEntry>> ListClientsAsync(RouterNode node, CancellationToken ct)
    {
        try
        {
            var entries = await GetJsonAsync<List<RouterClientEntry>>(
                $"{node.BaseUrl}/cgi-bin/list_clients_json.sh", ct).ConfigureAwait(false);
            return entries ?? [];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Could not read the node client list: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// GET + JSON. The whole body is read inside the client's timeout (with
    /// ResponseHeadersRead the timeout would stop covering the body, and a hung CGI would
    /// block the cycle) and decoded by hand: the node's CGIs do not always send a
    /// Content-Type/charset that ReadFromJsonAsync accepts.
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK) return default;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes).Trim().TrimStart('\uFEFF');
        if (text.Length == 0) return default;

        return JsonSerializer.Deserialize<T>(text, JsonOptions);
    }

    public void Dispose() => _http.Dispose();
}
