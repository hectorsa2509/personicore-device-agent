using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Personicore.DeviceAgent.Net;

/// <summary>The local interface this computer reaches the meter node through.</summary>
/// <param name="Address">Local IP on the node's segment (the assignment key in the backend).</param>
/// <param name="MacAddress">That interface's MAC, lowercase and ':' separated.</param>
/// <param name="InterfaceName">The adapter's friendly name.</param>
/// <param name="IsWireless">true when the adapter is Wi-Fi.</param>
public sealed record LocalInterface(
    IPAddress Address,
    string MacAddress,
    string InterfaceName,
    bool IsWireless);

/// <summary>An IPv4 segment this computer is connected to.</summary>
/// <param name="Local">Our IP inside the segment.</param>
/// <param name="Network">Network address.</param>
/// <param name="PrefixLength">Mask bits.</param>
/// <param name="InterfaceName">The adapter it belongs to.</param>
public sealed record LocalSubnet(IPAddress Local, IPAddress Network, int PrefixLength, string InterfaceName)
{
    /// <summary>Usable addresses, excluding network, broadcast and our own.</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        var network = ToUInt32(Network);
        var total = 1u << (32 - PrefixLength);
        var self = ToUInt32(Local);

        for (var offset = 1u; offset < total - 1; offset++)
        {
            var address = network + offset;
            if (address == self) continue;
            yield return FromUInt32(address);
        }
    }

    public int HostCount => (1 << (32 - PrefixLength)) - 2;

    internal static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    internal static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}

public static class LocalNetwork
{
    /// <summary>
    /// IPv4 segments this computer is connected to, with the adapter's real mask — not
    /// an assumed /24. Segments larger than <paramref name="minimumPrefix"/> are dropped:
    /// sweeping a /16 would take minutes, and the meter node is always on the panelist's
    /// home network.
    /// </summary>
    public static IReadOnlyList<LocalSubnet> ActiveSubnets(int minimumPrefix = 22)
    {
        var subnets = new List<LocalSubnet>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;   // APIPA: no hay red real

                var prefix = PrefixLengthOf(unicast.IPv4Mask);
                if (prefix < minimumPrefix || prefix > 30) continue;

                var network = LocalSubnet.FromUInt32(
                    LocalSubnet.ToUInt32(unicast.Address) & LocalSubnet.ToUInt32(unicast.IPv4Mask));

                subnets.Add(new LocalSubnet(unicast.Address, network, prefix, nic.Name));
            }
        }

        return subnets;
    }

    private static int PrefixLengthOf(IPAddress? mask)
    {
        if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork) return 0;

        return System.Numerics.BitOperations.PopCount(LocalSubnet.ToUInt32(mask));
    }

    /// <summary>
    /// Discovers which interface the system would use to talk to <paramref name="target"/>.
    /// A "connected" UDP socket sends not a single packet: it only makes the stack
    /// resolve the route and assign the local IP. It is the cheap, reliable way to know
    /// which IP the node sees us with when there are several NICs (Wi-Fi + Ethernet + VPN).
    /// </summary>
    public static LocalInterface? ResolveRouteTo(IPAddress target)
    {
        IPAddress? local = null;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(target, 9);
            local = (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }

        if (local is null || IPAddress.Any.Equals(local)) return null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var match = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.Equals(local));
            if (match is null) continue;

            return new LocalInterface(
                local,
                FormatMac(nic.GetPhysicalAddress()),
                nic.Name,
                nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211);
        }

        // Ruta encontrada pero sin NIC asociable (VPN virtual, Hyper-V): sirve la IP,
        // and the MAC will come from the node's ARP table.
        return new LocalInterface(local, string.Empty, "unknown", false);
    }

    /// <summary>
    /// Is this a private IPv4 address, as <c>presenceReportDeviceIp</c> defines one?
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to the backend so a doomed report is never sent: the
    /// mutation rejects anything else with "The address is not a private IPv4", and that
    /// refusal would otherwise look like a fault instead of a computer that is not on a home
    /// network at the moment.
    ///
    /// The rules are the backend's, down to the strictness: four decimal octets, no leading
    /// zeros, no colon. <see cref="IPAddress.TryParse"/> alone is not enough — it accepts
    /// "1.2.3" and "192.168.01.1", which the resolver does not.
    /// </remarks>
    public static bool IsPrivateIpv4(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Contains(':')) return false;

        var parts = text.Split('.');
        if (parts.Length != 4) return false;

        var octets = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3) return false;
            if (part.Length > 1 && part[0] == '0') return false;          // no leading zeros
            if (!part.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(part, out octets[i]) || octets[i] > 255) return false;
        }

        // RFC1918 plus link-local, which is what the resolver accepts. Loopback, multicast,
        // 0.0.0.0 and anything public fall through to false.
        return octets switch
        {
            [10, _, _, _] => true,
            [172, >= 16 and <= 31, _, _] => true,
            [192, 168, _, _] => true,
            [169, 254, _, _] => true,
            _ => false,
        };
    }

    /// <summary>First default IPv4 gateway, the basis of the subnet sweep.</summary>
    public static IPAddress? DefaultGateway()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var gateway = nic.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a is not null && a.AddressFamily == AddressFamily.InterNetwork
                                     && !IPAddress.Any.Equals(a));
            if (gateway is not null) return gateway;
        }

        return null;
    }

    public static string FormatMac(PhysicalAddress? physical)
    {
        var bytes = physical?.GetAddressBytes();
        if (bytes is null || bytes.Length != 6) return string.Empty;
        return string.Join(':', bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Normaliza cualquier forma de MAC ("AA-BB-CC…", "AABBCC…", "aa:bb:cc…")
    /// to the node's format: lowercase, ':' separated. Empty string when it is not valid.
    /// </summary>
    public static string NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12) return string.Empty;

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>
    /// true when the "locally administered" bit is set, that is, a randomized MAC.
    /// Second hex character ∈ {2, 6, a, e}. See MANUAL-mac-identificador-unico.
    /// </summary>
    public static bool IsRandomizedMac(string mac)
    {
        var normalized = NormalizeMac(mac);
        return normalized.Length == 17 && normalized[1] is '2' or '6' or 'a' or 'e';
    }
}
