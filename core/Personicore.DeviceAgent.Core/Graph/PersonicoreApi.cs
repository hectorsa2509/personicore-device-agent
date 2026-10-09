using System.Text.Json;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Graph;

/// <summary>
/// The three operations the agent needs, degrading automatically when the backend does
/// not expose a field yet. The exact shape of the queries is documented in
/// docs/GRAPHQL-CONTRACT.md; it came from what is already used by the router's web
/// page (network.html) and by update_ips_from_graphql.sh in the firmware.
///
/// Building a request and sending it are separate on purpose: the `Build*` methods are
/// static and offline mode uses them too, to dump into the desktop file the exact body
/// that would have been sent.
/// </summary>
/// <param name="learned">
/// Fields the deployed schema has already refused, carried across cycles by the worker.
/// </param>
public sealed class PersonicoreApi(
    GraphQlClient client, AgentConfig config, EnvSettings env, HashSet<string>? learned = null)
{
    private bool _householdSelectionSupported = true;

    /// <summary>
    /// Fields of IPInput the deployed schema does not know, learned from its own error
    /// messages and dropped from then on.
    ///
    /// The agent gathers more about a computer than the backend can currently store —
    /// the operating system today, and now whether it is personal and who the principal
    /// panelist is. Sending them and degrading on refusal means the agent is ready the
    /// day the backend grows the field, without a release, and a registration is never
    /// lost over a field that was only ever extra.
    /// </summary>
    /// <summary>
    /// Shared with whoever creates this client, because a new one is built on every
    /// cycle: keeping the set here alone meant re-learning the same refusals every hour,
    /// paying a failed mutation and a retry each time to be told what the last cycle
    /// already knew.
    /// </summary>
    private readonly HashSet<string> _unsupported = Initial(config, learned);

    private static HashSet<string> Initial(AgentConfig config, HashSet<string>? learned)
    {
        var set = learned ?? new HashSet<string>(StringComparer.Ordinal);
        if (!config.SendDeviceOs) set.Add(OptionalFields.DeviceOs);
        return set;
    }

    /// <summary>Extra fields, in the order they are given up when the schema refuses them.</summary>
    private static class OptionalFields
    {
        public const string DeviceOs = "deviceOs";
        public const string IsPersonal = "isPersonal";
        public const string PrincipalPanelistId = "principalPanelistId";

        public static readonly string[] All = [DeviceOs, IsPersonal, PrincipalPanelistId];
    }

    /// <summary>true when the backend accepted the operating system as a field of its own.</summary>
    public bool DeviceOsAccepted => !_unsupported.Contains(OptionalFields.DeviceOs);

    /// <summary>true when the backend returned the household id in the last query.</summary>
    public bool HouseholdAvailable => _householdSelectionSupported;

    // ── Documentos ───────────────────────────────────────────────────────────

    // Fragments are stored unindented and indented when composed with Indent(): the
    // volcado se lee a mano y una query descuadrada cuesta de revisar.
    private const string PanelistSelection = """
        panelists {
          id
          firstName
          lastName
          email
          ips {
            ip
            friendlyName
            machineId
            deviceBrand
            deviceModel
            deviceType
          }
        }
        """;

    private const string AssignmentMutation = """
        mutation routerIpsAssigment(
          $routerId: ID!
          $addIps: [IPInput!]
          $removeIps: [RemoveIPAssignmentInput!]
          $updateIps: [IPInput!]
        ) {
          routerIpsAssigment(
            routerId: $routerId
            addIps: $addIps
            removeIps: $removeIps
            updateIps: $updateIps
          ) {
            id
            macAddress
          }
        }
        """;

    private const string PersonalDeviceMutation = """
        mutation presenceCreatePersonalDevice(
          $name: String!
          $uuid: String
          $type: PersonalDeviceType!
          $householdId: ID
          $model: String
          $brand: String
        ) {
          presenceCreatePersonalDevice(
            name: $name
            uuid: $uuid
            type: $type
            householdId: $householdId
            model: $model
            brand: $brand
          ) {
            id
            name
            uuid
          }
        }
        """;

    /// <summary>
    /// The contract that replaces <c>routerIpsAssigment</c> for the device itself. One
    /// operation covers create and update: the server decides which by whether
    /// <c>deviceId</c> is in the input, and keys retries on <c>requestId</c>.
    /// </summary>
    private const string DeviceMutation = """
        mutation PresenceCreateDevice($input: PresenceCreateDeviceInput!) {
          presenceCreateDevice(input: $input) {
            requestId
            deviceId
            routerId
            householdId
            acceptedAt
          }
        }
        """;

    /// <summary>
    /// Records which Local IP this computer occupies on the node, and for how long.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <c>presenceCreateDevice</c>, which has no address field and
    /// is not going to grow one: the backend keeps occupancy as Local IP Periods of its own.
    /// It also has nothing to do with <c>routerIpsAssigment</c> — that is operators mapping
    /// addresses to panelists, and this reads and writes none of it.
    /// </remarks>
    private const string DeviceIpMutation = """
        mutation PresenceReportDeviceIp($input: PresenceReportDeviceIpInput!) {
          presenceReportDeviceIp(input: $input) {
            reportId
            deviceId
            routerId
            ip
            acceptedAt
          }
        }
        """;

    // ── Building requests ────────────────────────────────────────────────────

    public static GraphQlRequest BuildHouseholdRequest(string routerMac, bool includeHousehold)
    {
        var householdSelection = includeHousehold
            ? """
              household {
                id
                friendlyName
              }
              """
            : null;

        var query = $$"""
            query getPanelistsRouter($macAddress: String!) {
              getPanelistsRouter(macAddress: $macAddress) {
                router {
                  id
                  macAddress
            {{Indent(householdSelection, 6)}}
                }
            {{Indent(PanelistSelection, 4)}}
              }
            }
            """;

        return new GraphQlRequest("getPanelistsRouter", query, new { macAddress = routerMac });
    }

    /// <summary>
    /// The send mutation is configurable from the `.env`. A document of one's own has
    /// to accept the same variables — $routerId, $addIps, $removeIps, $updateIps —
    /// because those are what the agent assembles; what can change without touching code
    /// is which mutation is called and what it returns.
    /// </summary>
    /// <param name="skip">
    /// Optional fields the deployed schema has already refused. Everything else goes.
    /// </param>
    public static GraphQlRequest BuildAssignmentRequest(
        string routerId, DeviceRegistration device, IReadOnlySet<string> skip, EnvSettings env)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ip"] = device.Ip,
            ["friendlyName"] = device.FriendlyName,
            ["machineId"] = device.MachineId,
            ["deviceType"] = device.DeviceType,
            ["deviceBrand"] = device.DeviceBrand,
            ["deviceModel"] = device.DeviceModel,
            ["deviceShared"] = device.DeviceShared,
            ["panelistIds"] = device.PanelistIds,
        };

        if (!skip.Contains(OptionalFields.DeviceOs) && !string.IsNullOrWhiteSpace(device.DeviceOs))
            entry[OptionalFields.DeviceOs] = device.DeviceOs;

        if (!skip.Contains(OptionalFields.IsPersonal))
            entry[OptionalFields.IsPersonal] = device.IsPersonal;

        if (!skip.Contains(OptionalFields.PrincipalPanelistId)
            && !string.IsNullOrWhiteSpace(device.PrincipalPanelistId))
            entry[OptionalFields.PrincipalPanelistId] = device.PrincipalPanelistId;

        var variables = new
        {
            routerId,
            addIps = Array.Empty<object>(),
            removeIps = Array.Empty<object>(),
            updateIps = new[] { entry },
        };

        var document = string.IsNullOrWhiteSpace(env.MutationDocument)
            ? AssignmentMutation
            : env.MutationDocument;

        return new GraphQlRequest(env.OperationName, document, variables);
    }

    public static GraphQlRequest BuildPersonalDeviceRequest(string householdId, DeviceRegistration device)
    {
        var variables = new
        {
            name = device.FriendlyName,
            uuid = device.MachineId,
            // The PersonalDeviceType enum does not distinguish desktop computers: WIFI
            // is the value describing a device the node sees on the household network.
            type = "WIFI",
            householdId,
            model = device.DeviceModel,
            brand = device.DeviceBrand,
        };

        return new GraphQlRequest("presenceCreatePersonalDevice", PersonalDeviceMutation, variables);
    }

    /// <summary>
    /// Builds the <c>presenceCreateDevice</c> input for this computer.
    /// </summary>
    /// <param name="requestId">
    /// The idempotency key. One per *intended* write, reused across retries of that same
    /// write and never across two of them: the server keys retries on it and answers a
    /// reused key with the original payload, so a second write under the first key is
    /// silently dropped.
    /// </param>
    /// <param name="deviceId">
    /// Empty on the first write, the stored id afterwards. Its presence is what makes the
    /// server update instead of insert, so getting it wrong is how a household ends up
    /// with two rows for one computer.
    /// </param>
    /// <remarks>
    /// There is no <c>householdId</c> in the input and no <c>ip</c>: the first the server
    /// derives from the token, the second this contract has no field for.
    ///
    /// <c>deviceShared</c> and <c>isPersonal</c> come from the owner ids and not from
    /// <see cref="DeviceRegistration.DeviceShared"/>. The server re-derives both from the
    /// ids it receives and rejects a mismatch outright, so the flags have to be read off
    /// the same list that travels with them.
    /// </remarks>
    public static GraphQlRequest BuildDeviceRequest(
        string routerId, DeviceRegistration device, string requestId, string deviceId)
    {
        var owners = device.OwnerIds;

        var input = new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["routerId"] = routerId,
            ["machineId"] = device.MachineId,
            ["name"] = device.FriendlyName,
            ["deviceType"] = device.DeviceType,
            ["brand"] = device.DeviceBrand,
            ["model"] = device.DeviceModel,
            ["deviceOs"] = device.DeviceOs,
            ["panelistIds"] = owners,
            ["principalPanelistId"] = device.PrincipalOrFirst,
            ["deviceShared"] = device.SharedByIds,
            ["isPersonal"] = !device.SharedByIds,
        };

        // Omitted on create and on retry, where the server ignores it anyway. Sending an
        // empty string instead would be an id that matches no device.
        if (!string.IsNullOrWhiteSpace(deviceId)) input["deviceId"] = deviceId;

        return new GraphQlRequest("PresenceCreateDevice", DeviceMutation, new { input });
    }

    /// <summary>
    /// Builds the <c>presenceReportDeviceIp</c> input for one observation of this computer's
    /// address.
    /// </summary>
    /// <param name="reportId">
    /// One per <i>observation</i>, not per cycle. A new address is a new observation and needs
    /// a new id; resending the same observation after a transport failure must reuse it, or the
    /// backend opens a second period for something that happened once.
    /// </param>
    /// <param name="deviceId">
    /// The id from <c>presenceCreateDevice</c>. Omitted when there is none yet, which makes the
    /// resolver find the Network Device by <c>machineId</c> instead — the same lookup, one index
    /// away, and it means a lost device id does not stop the address being reported.
    /// </param>
    public static GraphQlRequest BuildDeviceIpRequest(
        string routerId, string deviceId, string machineId, string ip, string reportId)
    {
        var input = new Dictionary<string, object?>
        {
            ["reportId"] = reportId,
            ["routerId"] = routerId,
            ["machineId"] = machineId,
            ["ip"] = ip,
        };

        if (!string.IsNullOrWhiteSpace(deviceId)) input["deviceId"] = deviceId;

        return new GraphQlRequest("PresenceReportDeviceIp", DeviceIpMutation, new { input });
    }

    /// <summary>
    /// Reports this computer's current Local IP. Returns the period the backend opened, or the
    /// original one when this was a retry.
    /// </summary>
    /// <remarks>
    /// The subject has to exist first: this mutation never creates one. With no Network Device
    /// registered — which is where the agent sits whenever <c>presenceCreateDevice</c> is
    /// refused — it answers "The subject does not exist in this household!", and that is a
    /// sequencing fact rather than a fault, so it is reported as such.
    /// </remarks>
    public async Task<PresenceDeviceIpPayload> ReportDeviceIpAsync(
        string routerId, string deviceId, string machineId, string ip, string reportId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            throw new GraphQlException("presenceReportDeviceIp: there is no reportId; nothing was sent.");
        if (string.IsNullOrWhiteSpace(routerId))
            throw new GraphQlException("presenceReportDeviceIp: the household query returned no routerId; nothing was sent.");
        if (string.IsNullOrWhiteSpace(machineId))
            throw new GraphQlException("presenceReportDeviceIp: machineId is empty; nothing was sent.");

        if (!Net.LocalNetwork.IsPrivateIpv4(ip))
        {
            throw new GraphQlException(
                $"presenceReportDeviceIp: \"{ip}\" is not a private IPv4 address, which is all this "
                + "mutation accepts; nothing was sent.");
        }

        PresenceReportDeviceIpData data;
        try
        {
            data = await client.ExecuteAsync<PresenceReportDeviceIpData>(
                BuildDeviceIpRequest(routerId, deviceId, machineId, ip, reportId), ct).ConfigureAwait(false);
        }
        catch (GraphQlException ex) when (DescribeIpReport(ex) is { } explained)
        {
            throw explained;
        }

        var payload = data.PresenceReportDeviceIp;
        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
        {
            throw new GraphQlException(
                "presenceReportDeviceIp: the backend answered without a period; the address was not recorded.");
        }

        // The accepted address, which on a retry is the first write's. Saying so matters: the
        // panel then holds an address this computer no longer has, and the next observation —
        // under its own report id — is what corrects it.
        if (!string.Equals(payload.Ip, ip, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"presenceReportDeviceIp accepted {payload.Ip} for a report of {ip}: this report id had "
                     + "already been used, so the backend returned the original period.");
        }
        else
        {
            Log.Info($"Address reported: ip={ip} deviceId={payload.DeviceId} routerId={Show(payload.RouterId)} "
                     + $"reportId={reportId}");
        }

        return payload;
    }

    /// <summary>
    /// The refusals <c>presenceReportDeviceIp</c> raises that mean something other than "the
    /// payload is wrong". Returns null for anything else.
    /// </summary>
    private static GraphQlException? DescribeIpReport(GraphQlException ex)
    {
        var message = ex.Message;

        bool Says(string marker) => message.Contains(marker, StringComparison.OrdinalIgnoreCase);

        // The identity write has not landed yet. Expected, not broken: this mutation never
        // creates a subject, so until presenceCreateDevice succeeds there is nothing to attach
        // an address to. Classed with the other "wait for the backend" cases so it backs off
        // instead of retrying every minute.
        if (Says("The subject does not exist in this household!"))
        {
            return new BackendNotReadyException(
                "presenceReportDeviceIp has nothing to attach the address to: this computer is not "
                + "registered as a Network Device yet, and this mutation never creates one. "
                + $"Backend said: {message}");
        }

        if (Says("Access denied!") || Says("The panelist doesn't exist!")
                                  || Says("The panelist doesn't belong to a household!"))
        {
            return new PanelistCredentialException(
                "presenceReportDeviceIp fell onto the panelist path, which means routerId did not reach "
                + $"the resolver — with it sent the JWT is not consulted. Backend said: {message}");
        }

        if (Says("Router doesn't exist or is inactive!") || Says("The router is not assigned to a household!"))
        {
            return new HouseholdNotAssignedException(
                $"presenceReportDeviceIp refused the router. Backend said: {message}");
        }

        // The node was recreated in the panel, or this computer was registered against another
        // one. An address report cannot move a Network Device between routers, so this needs the
        // registration fixing, not a retry.
        if (Says("The Network Device is not registered on this router")
            || Says("Machine ID does not match registration"))
        {
            return new DeviceOnAnotherNodeException(
                "presenceReportDeviceIp refused the identity: the panel has this computer registered "
                + $"against another router or another machineId. Backend said: {message}");
        }

        if (MentionsSchemaGapFor(message, "presenceReportDeviceIp", "PresenceReportDeviceIpInput"))
        {
            return new SchemaNotReadyException(
                "presenceReportDeviceIp is not in the schema this endpoint serves yet; the agent is "
                + $"ahead of the deployment. Nothing was sent. Backend said: {message}");
        }

        return null;
    }

    /// <summary>
    /// What the mutation requires, checked here so the panelist's registration does not
    /// depend on a round trip to find out. Every one of these is a documented server-side
    /// refusal; the point of repeating them is that a local message names the field,
    /// whereas the wire gives back a sentence to grep for.
    /// </summary>
    /// <returns>The reason it cannot be sent, or null when it can.</returns>
    private static string? DeviceInputProblem(string routerId, DeviceRegistration device, string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return "there is no requestId to make the write idempotent";
        if (string.IsNullOrWhiteSpace(routerId)) return "the household query returned no routerId";
        if (string.IsNullOrWhiteSpace(device.MachineId)) return "machineId is empty";
        if (string.IsNullOrWhiteSpace(device.FriendlyName)) return "name is empty";
        if (string.IsNullOrWhiteSpace(device.DeviceBrand)) return "brand is empty";
        if (string.IsNullOrWhiteSpace(device.DeviceModel)) return "model is empty";
        if (string.IsNullOrWhiteSpace(device.DeviceOs)) return "deviceOs is empty";

        if (!Agent.AgentText.DeviceTypes.Contains(device.DeviceType, StringComparer.Ordinal))
        {
            return $"deviceType \"{device.DeviceType}\" is not one of "
                   + $"[{string.Join(", ", Agent.AgentText.DeviceTypes)}]";
        }

        // Without an owner id there is nobody to attribute the computer to. It happens
        // when the owner was typed rather than picked — dry run — and those names are
        // filtered out of the payload on purpose.
        return device.OwnerIds.Count == 0
            ? "there are no panelist ids: the owner was typed, not picked from the household"
            : null;
    }

    /// <summary>
    /// Indents a fragment to fit the document. An empty fragment leaves a blank line in
    /// the query — harmless to GraphQL — instead of a stray indent.
    /// </summary>
    private static string Indent(string? block, int spaces)
    {
        if (string.IsNullOrEmpty(block)) return string.Empty;

        var prefix = new string(' ', spaces);
        return string.Join(Environment.NewLine,
            block.Split('\n').Select(line => prefix + line.TrimEnd('\r')));
    }

    // ── Sending ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resuelve hogar y panelistas a partir de la MAC del nodo. Es el paso que
    /// is what means the panelist never has to type the household id: the router gives
    /// the MAC and the household comes from the backend.
    /// </summary>
    public async Task<HouseholdContext> GetHouseholdAsync(string routerMac, CancellationToken ct)
    {
        try
        {
            return await QueryHouseholdAsync(routerMac, _householdSelectionSupported, ct).ConfigureAwait(false);
        }
        catch (GraphQlException ex)
            when (_householdSelectionSupported && ex is not HouseholdNotAssignedException)
        {
            // network.html asks for router { id macAddress } only. Whatever the reason the
            // household selection was refused (schema or permissions), retry exactly like
            // it; if that fails too, the retry's error is the one reported.
            // A backend deployment without Router.household exposed. The rest of the
            // flow still holds: routerIpsAssigment resolves by routerId.
            Log.Warn("The backend does not expose Router.household; continuing without a household id. " + ex.Message);
            _householdSelectionSupported = false;
            return await QueryHouseholdAsync(routerMac, includeHousehold: false, ct).ConfigureAwait(false);
        }
    }

    private async Task<HouseholdContext> QueryHouseholdAsync(
        string routerMac, bool includeHousehold, CancellationToken ct)
    {
        var data = await client.ExecuteAsync<GetPanelistsRouterData>(
            BuildHouseholdRequest(routerMac, includeHousehold), ct).ConfigureAwait(false);

        // A missing top-level field is not "this household is not set up": it is a reply we
        // do not understand — a captive portal, a proxy error page, a schema change. Calling
        // that a household problem would send support to the panel instead of to the wire.
        var result = data.GetPanelistsRouter
                     ?? throw new GraphQlException(
                         $"getPanelistsRouter returned no data for MAC {routerMac}.");

        var router = result.Router
                     ?? throw new HouseholdNotAssignedException(
                         $"getPanelistsRouter: MAC {routerMac} has no associated router.");

        return new HouseholdContext(
            RouterId: router.Id,
            RouterMac: router.MacAddress,
            HouseholdId: router.Household?.Id ?? string.Empty,
            Panelists: result.Panelists ?? []);
    }

    /// <summary>
    /// Associates this computer (IP + machineId + metadata) with the chosen panelists.
    /// This is the mutation the router's own web page already uses, so it is the path
    /// verified against production.
    /// </summary>
    public async Task AssignAsync(string routerId, DeviceRegistration device, CancellationToken ct)
    {
        // One attempt, plus one per optional field the schema might refuse. Each refusal
        // names its field, so the loop cannot spin: every pass either succeeds, gives up
        // exactly one field, or rethrows a failure that has nothing to do with the schema.
        for (var attempt = 0; attempt <= OptionalFields.All.Length; attempt++)
        {
            try
            {
                await SendAssignmentAsync(routerId, device, ct).ConfigureAwait(false);
                return;
            }
            catch (GraphQlException ex)
            {
                var refused = OptionalFields.All.FirstOrDefault(
                    field => !_unsupported.Contains(field) && MentionsField(ex, field));

                if (refused is null) throw;

                Log.Warn($"IPInput does not accept {refused}; resending without it. {ex.Message}");
                _unsupported.Add(refused);
            }
        }

        throw new GraphQlException("routerIpsAssigment: the schema refused every optional field.");
    }

    /// <summary>
    /// Sends the assignment and checks that something came back.
    /// </summary>
    /// <remarks>
    /// A body with `data` and nothing in it is not an acknowledgement. HTTP 200 with
    /// {"data":{}} is what a gateway that swallowed the mutation answers, and taking it
    /// for a delivery advances the fingerprint: the assignment that never arrived is then
    /// never retried either, and the menu says Registered.
    ///
    /// The built-in document is checked for the router it asks for. A mutation document
    /// supplied in the .env answers with a shape this class cannot know, so it gets the
    /// weakest check that still means something — the operation came back with a field,
    /// and that field is not null.
    /// </remarks>
    private async Task SendAssignmentAsync(string routerId, DeviceRegistration device, CancellationToken ct)
    {
        var request = BuildAssignmentRequest(routerId, device, _unsupported, env);

        if (string.IsNullOrWhiteSpace(env.MutationDocument))
        {
            var data = await client.ExecuteAsync<RouterIpsAssigmentData>(request, ct).ConfigureAwait(false);

            if (data.RouterIpsAssigment is null)
            {
                throw new GraphQlException(
                    "routerIpsAssigment: the backend answered without the router; nothing was assigned.");
            }
        }
        else
        {
            var data = await client.ExecuteAsync<JsonElement?>(request, ct).ConfigureAwait(false);

            if (!Answered(data))
            {
                throw new GraphQlException(
                    $"{env.OperationName}: the backend answered with an empty result; nothing was assigned.");
            }
        }

        var dropped = _unsupported.Count == 0 ? string.Empty : $" dropped=[{string.Join(',', _unsupported)}]";

        Log.Info($"Assignment sent: ip={device.Ip} machineId={device.MachineId} " +
                 $"panelists=[{string.Join(',', device.PanelistIds)}] " +
                 $"principal={Show(device.PrincipalPanelistId)} personal={device.IsPersonal}{dropped}");
    }

    /// <summary>
    /// Did the operation come back with anything at all? All that can be asked of a
    /// response whose shape is configured rather than known: an object with at least one
    /// field, and not every field null.
    /// </summary>
    private static bool Answered(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } element
        && element.EnumerateObject().Any(field => field.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    /// <summary>
    /// Creates the PersonalDevice inside the household. It is the literal reading of
    /// ("the service creates personal devices in the household") y requiere que el
    /// the backend accepts the router's JWT on this mutation; see docs/GRAPHQL-CONTRACT.md.
    /// </summary>
    public async Task<PersonalDevice> CreatePersonalDeviceAsync(
        string householdId, DeviceRegistration device, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(householdId))
            throw new GraphQlException("presenceCreatePersonalDevice: household id is missing.");

        var data = await client.ExecuteAsync<PresenceCreatePersonalDeviceData>(
            BuildPersonalDeviceRequest(householdId, device), ct).ConfigureAwait(false);

        // The identifier is the whole point of this mutation: it is what the agent stores
        // so that the same computer is never created twice in the panel. Without it there
        // is nothing to keep, and an empty answer must not pass for a created device.
        var created = data.PresenceCreatePersonalDevice;
        if (created is null || string.IsNullOrWhiteSpace(created.Id))
        {
            throw new GraphQlException(
                "presenceCreatePersonalDevice: the backend returned no device identifier.");
        }

        Log.Info($"PersonalDevice created in household {householdId}: {created.Id}");
        return created;
    }

    /// <summary>
    /// Records this computer as a Device of the household, creating it the first time and
    /// updating it afterwards. The replacement for <see cref="AssignAsync"/> on everything
    /// except the IP, which this contract has no field for.
    /// </summary>
    /// <param name="requestId">
    /// The idempotency key for this write. The caller owns it and persists it *before*
    /// calling: a create whose answer is lost on the wire has to be retried under the same
    /// key, or the household grows a second row for the same computer. Nothing in this
    /// contract is keyed on <c>machineId</c>.
    /// </param>
    /// <param name="deviceId">Empty until the first create answers; the stored id after that.</param>
    /// <exception cref="PanelistCredentialException">
    /// The resolver wants a panelist's token and got the router's.
    /// </exception>
    public async Task<PresenceDevicePayload> CreateDeviceAsync(
        string routerId, DeviceRegistration device, string requestId, string deviceId, CancellationToken ct)
    {
        if (DeviceInputProblem(routerId, device, requestId) is { } problem)
            throw new GraphQlException($"presenceCreateDevice: {problem}; nothing was sent.");

        var request = BuildDeviceRequest(routerId, device, requestId, deviceId);

        PresenceCreateDeviceData data;
        try
        {
            data = await client.ExecuteAsync<PresenceCreateDeviceData>(request, ct).ConfigureAwait(false);
        }
        catch (GraphQlException ex) when (Describe(ex) is { } explained)
        {
            throw explained;
        }

        // An empty payload is not an acknowledgement. HTTP 200 with {"data":{}} is what a
        // gateway that swallowed the mutation answers, and believing it would advance the
        // fingerprint: the write that never landed is then never retried either.
        var payload = data.PresenceCreateDevice;
        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
            throw new GraphQlException("presenceCreateDevice: the backend returned no deviceId; nothing was recorded.");

        // The echo is only meaningful on a create, and only there is it enforced.
        //
        // On create there is nothing else tying the answer to this request, so a payload
        // answering a different key is another write's and storing its deviceId would bind this
        // computer to another machine's row. On update the server echoes the *Device's* stored
        // requestId — the one from its create — not the key just sent, so comparing them failed
        // every update. Measured against stage, not inferred: sending a fresh key with a
        // deviceId came back with the create's. The update's own guarantee is the deviceId,
        // which is checked above.
        var isCreate = string.IsNullOrWhiteSpace(deviceId);

        if (isCreate
            && !string.IsNullOrWhiteSpace(payload.RequestId)
            && !string.Equals(payload.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new GraphQlException(
                $"presenceCreateDevice: answered requestId {payload.RequestId} for a create sent as {requestId}; "
                + "the device id was not stored.");
        }

        var what = string.IsNullOrWhiteSpace(deviceId) ? "created" : "updated";
        Log.Info($"Device {what}: deviceId={payload.DeviceId} household={Show(payload.HouseholdId)} "
                 + $"machineId={device.MachineId} owners=[{string.Join(',', device.OwnerIds)}] "
                 + $"principal={Show(device.PrincipalOrFirst)} shared={device.SharedByIds} "
                 + $"requestId={requestId}");

        return payload;
    }

    /// <summary>
    /// Turns the refusals this mutation is documented to raise into something whose cause
    /// is readable. Returns null for anything else, which is then reported as it came.
    /// </summary>
    /// <remarks>
    /// The messages are matched as substrings rather than compared: a gateway prefixes the
    /// resolver's text, and the exception already carries every error joined with " | ".
    /// </remarks>
    private static GraphQlException? Describe(GraphQlException ex)
    {
        var message = ex.Message;

        bool Says(string marker) => message.Contains(marker, StringComparison.OrdinalIgnoreCase);

        // The resolver saying it wanted a person's token. Under HP-1844 these three can only
        // come from the routerId-omitted path, which this agent never takes — so with routerId
        // sent, any of them means the deployment predates that change.
        if (Says("Access denied!") || Says("The panelist doesn't exist!")
                                  || Says("The panelist doesn't belong to a household!"))
        {
            // Deliberately does not name which credential is in use. It used to say "the
            // router's JWT from get_config.sh", which was wrong the moment a token was
            // baked into the package — and it sent the first person to read it looking at
            // the node instead of at the token. What matters is the shape of the problem:
            // this resolver wants a token whose user is an active panelist, and neither
            // credential the agent can hold is one.
            return new PanelistCredentialException(
                "presenceCreateDevice rejected the credential, not the data: it resolves the household "
                + "from the token's panelist, and the token in use does not belong to one. Note that "
                + "routerIpsAssigment accepts the same token, because it resolves by routerId — so this "
                + "is about which user signed the request, not about reachability or permissions in "
                + $"general. Nothing the agent can change fixes it. Backend said: {message}");
        }

        // The token's household and the router's disagree. On a panelist's computer that
        // means the token is not this household's, which is a setup problem, not a bug here.
        if (Says("The JWT does not authorize this router!") || Says("The router is not assigned to a household!"))
        {
            return new HouseholdNotAssignedException(
                "presenceCreateDevice refused the router: the household behind the credential is not the "
                + $"household this router belongs to. Backend said: {message}");
        }

        if (Says("Router doesn't exist or is inactive!"))
        {
            return new HouseholdNotAssignedException(
                "presenceCreateDevice refused the router: the panel has no active record for it. "
                + $"Backend said: {message}");
        }

        // An update whose target is gone. The one refusal in this method that the agent can
        // act on, so it gets a class of its own rather than the generic failure.
        if (Says("The device doesn't exist!"))
        {
            return new DeviceGoneException(
                "presenceCreateDevice cannot find the device this computer's stored id points at: it has "
                + $"been deleted or deactivated in the panel. Backend said: {message}");
        }

        // The agent is ahead of the deployment. Expected while a contract is on its way out
        // to an environment, and the only sensible answer is to ask again much later: no
        // payload this agent can build exists in that schema yet.
        if (MentionsSchemaGap(message))
        {
            return new SchemaNotReadyException(
                "presenceCreateDevice is not in the schema this endpoint serves yet; the agent is "
                + $"ahead of the deployment. Nothing was sent. Backend said: {message}");
        }

        return null;
    }

    /// <summary>
    /// Is this the endpoint saying it has never heard of the operation, as opposed to
    /// refusing something inside it?
    /// </summary>
    /// <remarks>
    /// The name is matched <b>in quotes</b>, and that is the whole subtlety. Every
    /// exception from this call already carries the operation name, because
    /// <see cref="GraphQlClient"/> prefixes it — so an unquoted search matched any error at
    /// all, and `Cannot query field "acceptedAt"` (the backend renaming a field of the
    /// payload, which is real contract drift worth seeing now) was filed as "not deployed
    /// yet" and sat on for an hour. GraphQL validation quotes the symbol it could not
    /// resolve, so requiring the quotes is what separates "this operation does not exist"
    /// from "something inside it does not".
    /// </remarks>
    private static bool MentionsSchemaGap(string message) =>
        MentionsSchemaGapFor(message, "presenceCreateDevice", "PresenceCreateDeviceInput");

    /// <summary>
    /// <see cref="MentionsSchemaGap"/> for a named operation and its input type.
    /// </summary>
    private static bool MentionsSchemaGapFor(string message, params string[] symbols)
    {
        if (!symbols.Any(symbol => message.Contains($"\"{symbol}\"", StringComparison.OrdinalIgnoreCase)))
            return false;

        string[] markers =
        [
            "cannot query field", "unknown type", "is not defined", "unknown argument",
            "no such field", "not defined by type",
        ];

        return markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Distingue "este campo no existe en el esquema" de un error real de negocio.
    /// Without this, a permissions failure would be mistaken for an out-of-date backend
    /// and the agent would silently degrade.
    /// </summary>
    private static bool MentionsField(GraphQlException ex, string field)
    {
        var message = ex.Message;
        if (!message.Contains(field, StringComparison.OrdinalIgnoreCase)) return false;

        string[] schemaErrors =
        [
            "cannot query field", "unknown argument", "is not defined by type",
            "unknown field", "did you mean", "no such field", "unknown type",
        ];

        return schemaErrors.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
