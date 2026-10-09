using System.Text.Json.Serialization;

namespace Personicore.DeviceAgent.Graph;

public sealed class Household
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("friendlyName")] public string? FriendlyName { get; set; }
}

public sealed class RouterNodeInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("macAddress")] public string MacAddress { get; set; } = string.Empty;
    [JsonPropertyName("household")] public Household? Household { get; set; }
}

public sealed class PanelistIp
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;
    [JsonPropertyName("friendlyName")] public string? FriendlyName { get; set; }
    [JsonPropertyName("machineId")] public string? MachineId { get; set; }
    [JsonPropertyName("deviceBrand")] public string? DeviceBrand { get; set; }
    [JsonPropertyName("deviceModel")] public string? DeviceModel { get; set; }
    [JsonPropertyName("deviceType")] public string? DeviceType { get; set; }
}

public sealed class Panelist
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("ips")] public List<PanelistIp>? Ips { get; set; }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var name = $"{FirstName} {LastName}".Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return string.IsNullOrWhiteSpace(Email) ? Id : Email!;
        }
    }
}

public sealed class RouterPanelist
{
    [JsonPropertyName("router")] public RouterNodeInfo? Router { get; set; }
    [JsonPropertyName("panelists")] public List<Panelist>? Panelists { get; set; }
}

public sealed class GetPanelistsRouterData
{
    [JsonPropertyName("getPanelistsRouter")] public RouterPanelist? GetPanelistsRouter { get; set; }
}

public sealed class RouterIpsAssigmentData
{
    [JsonPropertyName("routerIpsAssigment")] public RouterNodeInfo? RouterIpsAssigment { get; set; }
}

public sealed class PersonalDevice
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
}

public sealed class PresenceCreatePersonalDeviceData
{
    [JsonPropertyName("presenceCreatePersonalDevice")]
    public PersonalDevice? PresenceCreatePersonalDevice { get; set; }
}

/// <summary>
/// What <c>presenceCreateDevice</c> answers. <see cref="DeviceId"/> is the one field the
/// agent could not get from the old contract — <c>routerIpsAssigment</c> returned the
/// *router's* id — and it is what <see cref="Device.DeviceIdStore"/> keeps for good.
///
/// <see cref="RequestId"/> is the echo of the idempotency key. It is checked rather than
/// trusted: a payload echoing a different key is somebody else's write, and recording its
/// device id against this computer would tie us to another machine's row.
/// </summary>
public sealed class PresenceDevicePayload
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("routerId")] public string RouterId { get; set; } = string.Empty;
    [JsonPropertyName("householdId")] public string HouseholdId { get; set; } = string.Empty;
    [JsonPropertyName("acceptedAt")] public string? AcceptedAt { get; set; }
}

public sealed class PresenceCreateDeviceData
{
    [JsonPropertyName("presenceCreateDevice")] public PresenceDevicePayload? PresenceCreateDevice { get; set; }
}

/// <summary>
/// What <c>presenceReportDeviceIp</c> answers: the Local IP Period it opened, echoed back.
/// </summary>
/// <remarks>
/// <see cref="Ip"/> is the echo of the <i>accepted</i> address, which on a retry is the first
/// write's IP even when the retry body carries a different one. So it is worth comparing
/// against what was sent: a mismatch means this report was a retry of an older observation,
/// and the panel's idea of the address is not what this computer just saw.
/// </remarks>
public sealed class PresenceDeviceIpPayload
{
    [JsonPropertyName("reportId")] public string ReportId { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("routerId")] public string RouterId { get; set; } = string.Empty;
    [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;
    [JsonPropertyName("acceptedAt")] public string? AcceptedAt { get; set; }
}

public sealed class PresenceReportDeviceIpData
{
    [JsonPropertyName("presenceReportDeviceIp")] public PresenceDeviceIpPayload? PresenceReportDeviceIp { get; set; }
}

/// <summary>
/// What the household knows about itself after a successful query: the router's
/// identity, the household id (filled in automatically, never typed by the panelist)
/// and the list of people the computer can be attributed to.
/// </summary>
/// <param name="RouterId">The router's internal id in the backend.</param>
/// <param name="RouterMac">The node's registered MAC.</param>
/// <param name="HouseholdId">Household id; empty when the backend does not expose household.</param>
/// <param name="Panelists">Members of the household.</param>
public sealed record HouseholdContext(
    string RouterId,
    string RouterMac,
    string HouseholdId,
    IReadOnlyList<Panelist> Panelists);

/// <summary>The four fields asked for, plus what the backend needs for the key.</summary>
public sealed record DeviceRegistration(
    string Ip,
    string MachineId,
    string FriendlyName,
    string DeviceType,
    string DeviceBrand,
    string DeviceModel,
    string DeviceOs,
    IReadOnlyList<string> PanelistIds)
{
    /// <summary>
    /// How many people the panelist said use the computer. Kept apart from PanelistIds
    /// because offline the names are typed and there are no IDs: if `deviceShared` were
    /// derived from the IDs alone, a shared computer would be dumped as an individual one.
    /// </summary>
    public int OwnerCount { get; init; }

    /// <summary>
    /// Who the computer is mainly attributed to when several people share it. With one
    /// owner it is that owner and nobody is asked; with several the panelist picks, and
    /// the first one checked is the default.
    /// </summary>
    public string PrincipalPanelistId { get; init; } = string.Empty;

    public bool DeviceShared => Math.Max(OwnerCount, PanelistIds.Count) > 1;

    /// <summary>
    /// The other side of <see cref="DeviceShared"/>: a computer one person uses is
    /// personal, one that several people use is not. Derived rather than asked, because
    /// asking twice about the same fact is how the two answers end up contradicting
    /// each other.
    /// </summary>
    public bool IsPersonal => !DeviceShared;

    /// <summary>
    /// The owners as <c>presenceCreateDevice</c> counts them: real ids, deduped, in the
    /// order the panelist checked them.
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceShared"/> is deliberately not this. It takes the larger of the
    /// typed-name count and the id count, because a dry-run owner has a name and no id
    /// and a shared computer must not be dumped as a personal one. The new mutation is
    /// the opposite case: the server re-derives the flags from the ids it was given and
    /// rejects anything that disagrees, so three typed names and one id is not "shared" —
    /// it is one owner, and saying otherwise fails the whole write.
    /// </remarks>
    public IReadOnlyList<string> OwnerIds =>
        PanelistIds.Where(id => !string.IsNullOrWhiteSpace(id))
                   .Select(id => id.Trim())
                   .Distinct(StringComparer.Ordinal)
                   .ToList();

    /// <summary>Shared as the server derives it: more than one distinct owner id.</summary>
    public bool SharedByIds => OwnerIds.Count > 1;

    /// <summary>
    /// The principal owner, guaranteed to be one of <see cref="OwnerIds"/>. The panelist's
    /// choice when it is still in the list, otherwise the first owner — the field is
    /// <c>ID!</c> and the server rejects a principal who is not an owner, so an owner
    /// dropped between the dialog and the send must not take the registration with it.
    /// </summary>
    public string PrincipalOrFirst
    {
        get
        {
            var owners = OwnerIds;
            if (owners.Count == 0) return string.Empty;

            return owners.Contains(PrincipalPanelistId, StringComparer.Ordinal)
                ? PrincipalPanelistId
                : owners[0];
        }
    }
}
