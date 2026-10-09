using System.Text.Json.Serialization;

namespace Personicore.DeviceAgent.Router;

/// <summary>Response from /cgi-bin/router_info.sh on the meter node.</summary>
public sealed class RouterInfo
{
    [JsonPropertyName("wan_mac")] public string WanMac { get; set; } = string.Empty;
    [JsonPropertyName("lan_mac")] public string LanMac { get; set; } = string.Empty;
    [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;

    /// <summary>
    /// The MAC the node is registered under in the backend: the household's key.
    /// wan_mac is the one that travels at the root of every log block.
    /// </summary>
    [JsonIgnore]
    public string IdentityMac => !string.IsNullOrWhiteSpace(WanMac) ? WanMac : LanMac;
}

/// <summary>Response from /cgi-bin/get_config.sh: the credential and endpoint the node itself uses.</summary>
public sealed class RouterConfig
{
    [JsonPropertyName("jwt")] public string Jwt { get; set; } = string.Empty;
    [JsonPropertyName("graphql")] public string GraphQl { get; set; } = string.Empty;
}

/// <summary>One entry from /cgi-bin/list_clients_json.sh: what the node sees on the bridge.</summary>
public sealed class RouterClientEntry
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("brand")] public string Brand { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("deviceType")] public string DeviceType { get; set; } = string.Empty;
}

/// <summary>Nodo meter localizado y respondiendo en la LAN.</summary>
/// <param name="BaseUrl">The http:// root it is talked to on.</param>
/// <param name="Info">The node's identity.</param>
/// <param name="ViaDns">true when resolved by name; false when found by sweeping the subnet.</param>
public sealed record RouterNode(string BaseUrl, RouterInfo Info, bool ViaDns);
