using System.Text;
using Personicore.DeviceAgent.Net;

namespace Personicore.DeviceAgent.Device;

/// <summary>
/// The machineId is the MAC encoded exactly as the meter node emits it
/// (list_clients_json.sh → generate_machine_id):
///
///     base64(mac sin ':')  →  quitar '='  →  + primeros 2 hex de la MAC  →  + "PMPM"
///
/// It is reversible, and it is the key the backend joins the device to the router's
/// `clients` blocks with. The agent computes it only as a fallback: when the
/// node answers, the machineId it reports is used, because it sees the real MAC of the
/// socket, while we only see the adapter's.
/// </summary>
public static class MachineId
{
    public static string FromMac(string mac)
    {
        var normalized = LocalNetwork.NormalizeMac(mac);
        if (normalized.Length != 17) return string.Empty;

        var clean = normalized.Replace(":", string.Empty);
        var base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(clean)).TrimEnd('=');

        return base64 + clean[..2] + "PMPM";
    }

    /// <summary>Inverse of <see cref="FromMac"/>. Empty string when the id cannot be decoded.</summary>
    public static string ToMac(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId) || !machineId.EndsWith("PMPM", StringComparison.Ordinal))
            return string.Empty;

        var body = machineId[..^4];
        if (body.Length < 3) return string.Empty;

        var base64 = body[..^2];
        var padded = base64.PadRight((base64.Length + 3) / 4 * 4, '=');

        try
        {
            var clean = Encoding.ASCII.GetString(Convert.FromBase64String(padded));
            return LocalNetwork.NormalizeMac(clean);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
