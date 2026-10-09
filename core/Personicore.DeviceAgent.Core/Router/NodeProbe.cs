using System.Net.Http.Headers;

namespace Personicore.DeviceAgent.Router;

/// <summary>
/// "Is there a Personicore meter node at this address?" — one cheap HTTP call, used by
/// the proxy watchdog on both platforms: if the proxy we configured no longer answers,
/// the computer is away from home (or the node is off) and the proxy must come off, or
/// the panelist is left with no internet.
/// </summary>
public static class NodeProbe
{
    /// <summary>
    /// The first address of <paramref name="candidates"/> where a meter node answers, or
    /// null. Tried in order and not in parallel on purpose: the first candidate is almost
    /// always the right one, and a burst of requests to addresses that are not the node
    /// is exactly what a panelist's firewall notices.
    /// </summary>
    /// <param name="expectedMac">
    /// The household's own node, when it is known: a node answering with any other MAC does
    /// not count as reachable. Empty or null accepts whichever node answers, which is what a
    /// computer that has never been registered has to do.
    /// </param>
    public static async Task<string?> FirstReachableAsync(
        IEnumerable<string> candidates, TimeSpan timeout, string? expectedMac = null, CancellationToken ct = default)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !tried.Add(candidate.Trim())) continue;
            if (await IsMeterNodeAsync(candidate.Trim(), timeout, expectedMac, ct).ConfigureAwait(false))
                return candidate.Trim();
        }

        return null;
    }

    /// <summary>
    /// Is there a meter node at this address — and, when <paramref name="expectedMac"/> is
    /// given, is it <i>this household's</i>?
    /// </summary>
    /// <remarks>
    /// The MAC is checked here rather than by the callers because every caller is a proxy
    /// decision, and every meter node answers on the same name: without it, a computer carried
    /// into another panel household finds a node at the very same address and sends that home's
    /// proxy its traffic. "A node answers" and "our node answers" are not the same question.
    /// </remarks>
    public static async Task<bool> IsMeterNodeAsync(
        string ip, TimeSpan timeout, string? expectedMac = null, CancellationToken ct = default)
    {
        var mac = await IdentityMacAsync(ip, timeout, ct).ConfigureAwait(false);
        if (mac is null) return false;
        if (string.IsNullOrWhiteSpace(expectedMac)) return true;

        var wanted = Net.LocalNetwork.NormalizeMac(expectedMac);
        if (wanted.Length != 17) return true;

        return string.Equals(mac, wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The MAC the node at this address is registered under, or null when nothing that looks
    /// like a meter node answers there.
    /// </summary>
    public static async Task<string?> IdentityMacAsync(string ip, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;

        try
        {
            // Never through the system proxy: that is exactly what is being tested.
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var body = await http.GetStringAsync($"http://{ip}/cgi-bin/router_info.sh", ct).ConfigureAwait(false);
            var info = System.Text.Json.JsonSerializer.Deserialize<RouterInfo>(body.Trim().TrimStart('\ufeff'),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var mac = Net.LocalNetwork.NormalizeMac(info?.IdentityMac);
            return mac.Length == 17 ? mac : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
