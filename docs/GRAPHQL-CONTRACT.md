# Backend contract

Everything the agent requests and sends. The shapes come from code already running in
production: `iot-firmware-factory/*/files/www/network.html` (the router's own web UI) and
`opt/personicore/update_ips_from_graphql.sh` (the firmware).

- **Endpoint**: the one the node advertises in `get_config.sh` (in production
  `https://graphql.example.invalid/`), exactly like `network.html`.
  `PERSONICORE_GRAPHQL_ENDPOINT` in an optional `.env` overrides it; if they differ, a
  warning is logged and the `.env` wins.
- **Credential**: `PERSONICORE_GRAPHQL_TOKEN` if set; otherwise the JWT served by
  `get_config.sh`. It goes in the `Authorization` header **verbatim** — nothing is added
  and nothing is stripped, so a backend wanting `Bearer eyJ…` needs the word in the
  value. The router's own JWT is sent bare, like `update_ips_from_graphql.sh`.

  The agent never asks the panelist for a credential. Which of the two it uses is a
  packaging decision: `build/write-packaged-env.sh` bakes a `.env` next to the executable
  at build time, and with one baked in that token is used instead of the node's. See
  "Where the credential comes from" in `CLAUDE.md`.

> **Everything here is sent for real by default**, with the node's `jwt` and `graphql`
> from `get_config.sh`, exactly like `network.html`. With `PERSONICORE_DRY_RUN=true` in the
> `.env`, the query in §2 still runs but the mutations are written to
> `Personicore-outbox.txt` on the desktop instead of POSTed.
>
> The sending mutation (§3) can be replaced from the `.env` without recompiling, as long
> as the document accepts the same variables.

---

## 1. Node endpoints (plain HTTP on the LAN)

| Endpoint | Returns | Purpose |
|---|---|---|
| `GET /cgi-bin/router_info.sh` | `{ wan_mac, lan_mac, ip }` | Node identity; `wan_mac` is the household key |
| `GET /cgi-bin/get_config.sh` | `{ jwt, graphql }` | Credential and endpoint |
| `GET /cgi-bin/list_clients_json.sh` | `[{ ip, mac, machineId, … }]` | Which MAC the node sees us with |

`router_info.sh` is also the discriminator for the subnet sweep: a candidate only counts
as a node if it returns a valid MAC.

---

## 2. Query — resolve household and panelists

```graphql
query getPanelistsRouter($macAddress: String!) {
  getPanelistsRouter(macAddress: $macAddress) {
    router {
      id
      macAddress
      household { id friendlyName }   # ← can degrade, see §5
    }
    panelists {
      id
      firstName
      lastName
      email
      ips { ip friendlyName machineId deviceBrand deviceModel deviceType }
    }
  }
}
```

This provides the three things the agent cannot know by itself: `routerId`,
`householdId` (the "filled in automatically from the router" criterion) and the list of
people offered to the panelist.

---

## 3. Main mutation — assign the computer

This is the path **verified against production**: the same one the router's web UI uses.

```graphql
mutation routerIpsAssigment(
  $routerId: ID!
  $addIps: [IPInput!]
  $removeIps: [RemoveIPAssignmentInput!]
  $updateIps: [IPInput!]
) {
  routerIpsAssigment(routerId: $routerId, addIps: $addIps, removeIps: $removeIps, updateIps: $updateIps) {
    id
    macAddress
  }
}
```

`updateIps` carries one entry:

| Field | Source | Ticket criterion |
|---|---|---|
| `ip` | local route to the node | assignment key |
| `machineId` | node client list | identity key |
| `friendlyName` | computer name, editable | — |
| `deviceType` | SMBIOS chassis → `laptop` / `desktop` / `tablet` | **device type** |
| `deviceBrand` | BIOS registry / WMI | **brand** |
| `deviceModel` | BIOS registry / WMI | **model** |
| `deviceOs` | `CurrentVersion` registry | **OS** — see §5 |
| `deviceShared` | `true` if more than one panelist was checked | — |
| `panelistIds` | the panelist's answer | device ownership |

---

## 4. Alternative mutation — create the PersonalDevice

The ticket literally says *"the service creates personal devices in the household"*. The
mutation that does that is:

```graphql
mutation presenceCreatePersonalDevice(
  $name: String!, $uuid: String, $type: PersonalDeviceType!,
  $householdId: ID, $model: String, $brand: String
) {
  presenceCreatePersonalDevice(
    name: $name, uuid: $uuid, type: $type,
    householdId: $householdId, model: $model, brand: $brand
  ) { id name uuid }
}
```

It is sent with `type: WIFI` (the `PersonalDeviceType` enum is `BEACON | CELLPHONE | WIFI
| BLUETOOTH`: there is no value for a computer) and `uuid` = `machineId`.

**It is not verified**, which is why it is not the default mode. Two unknowns:

1. The resolver calls `validatePanelistByUserId(householdId, context)`, i.e. it derives
   the panelist **from the JWT user**. The JWT served by the node belongs to the router,
   not to a person: it must be confirmed that the backend accepts it here.
2. The mutation takes no `panelistId`, so on its own it does not cover the ownership
   criterion; `setPersonalDevicePanelist` would have to be chained.

---

## 4b. Current mutation — `presenceCreateDevice`

The contract that replaces §3 and §4 for the device itself. Written by the backend team
and wired into the agent; **not deployed to stage yet**, so what follows is verified
against `tools/fake-graphql.py` and not against a real endpoint.

```graphql
mutation PresenceCreateDevice($input: PresenceCreateDeviceInput!) {
  presenceCreateDevice(input: $input) {
    requestId
    deviceId
    routerId
    householdId
    acceptedAt
  }
}
```

One operation covers three paths, and the server picks by what is in the input:

| Path | `requestId` | `deviceId` | Server |
|---|---|---|---|
| Create | new | omitted | inserts, sets `deviceOs` and `acceptedAt` |
| Retry | the create's | omitted | returns the original payload, applies nothing |
| Update | new | the stored one | overwrites inventory and owners, ignores `deviceOs` |

`householdId` is not sent: the server derives it from the credential.

### What the agent sends

Every field is required. `machineId`, `name`, `deviceType`, `brand`, `model` and
`deviceOs` are what the agent already gathered; `routerId` comes from §2.

`panelistIds` is deduped and `deviceShared` / `isPersonal` are derived **from that list**,
not from `DeviceRegistration.DeviceShared`. The two differ on purpose: `DeviceShared`
takes the larger of the typed-name count and the id count, so a dry-run owner with a name
and no id still reads as shared. The server re-derives both flags from the ids it was
given and rejects any disagreement, so for this mutation the flags have to come off the
same list that travels with them. `principalPanelistId` falls back to the first owner when
the panelist's choice is no longer in the list — the field is `ID!` and a principal who is
not an owner fails the write.

The input is checked before it is sent (`PersonicoreApi.DeviceInputProblem`). Every check
mirrors a documented refusal; the point is that a local message names the field instead of
handing back a sentence to grep for.

### Idempotency, and where the key lives

Retries are keyed on `requestId` **alone** — nothing in this contract is keyed on
`machineId`. So the key has to outlive the process, and it is written to `state.json`
(`deviceRequestId`) *before* the request leaves. A create that the server accepted but
whose answer never came back can then be reclaimed on the next cycle by resending the same
key; mint a fresh one there and the household has two Devices for one computer.

The key is minted from a fingerprint of the request's own variables, not from
`AgentWorker.Signature`: that one includes the IP, and this mutation has no IP field, so
keying on it would spend a real update re-sending identical inventory on every lease
change. `AgentState.RequestIdFor` mints and persists in one step, and
`AgentState.Clear()` deliberately keeps the key — forgetting the owner must not grow a
second device in the panel.

### Refusals that are not about the data

`PersonicoreApi.Describe` maps three of them, because the fix is not in the payload:

| Backend says | Becomes | Why |
|---|---|---|
| `Access denied!`, `The panelist doesn't exist!`, `The panelist doesn't belong to a household!` | `PanelistCredentialException` | the resolver wants a panelist's JWT; the agent has the router's |
| `The JWT does not authorize this router!`, `The router is not assigned to a household!` | `HouseholdNotAssignedException` | the credential's household is not this router's |
| `Router doesn't exist or is inactive!` | `HouseholdNotAssignedException` | no active router record |

A `PanelistCredentialException` is the predicted production failure, not an edge case —
see §6.2. The loop treats it as such: it waits the full poll interval instead of the retry
interval, logs once on the transition, and shows the panelist `AgentText.CredentialNotAccepted`,
worded like the other "support has to finish the setup" states because from their chair it
is the same event.

### What this contract does not carry

**There is no `ip` field.** Half of what the agent exists for — keeping the panel's view
of the address current — has no home here. `deviceAndRouterIps` is the mode that does both,
by sending `routerIpsAssigment` for the address and `presenceCreateDevice` for the device.
In `device` mode the IP is not reported at all, and the twelve-hour heartbeat lands on the
server's retry path and applies nothing: with no IP in the body, an unchanged report has an
unchanged fingerprint and therefore the same key.

---

## 4c. Registration modes

Selected with `registrationMode` in the configuration:

| Value | Effect |
|---|---|
| `deviceAndRouterIps` *(default)* | `routerIpsAssigment` for the address, then `presenceCreateDevice`. The only way to report both, and `presenceCreateDevice` has no `ip` field, so this is the shipping mode. |
| `device` | `presenceCreateDevice` only. Yields the device id but reports no address at all. For isolating the device write while testing. |
| `routerIps` | `routerIpsAssigment` only — what the router's own web page does. No device id. |
| `personalDevice` | `presenceCreatePersonalDevice` only. Superseded by `device`. |
| `both` | `routerIps` + `personalDevice`; if the second fails, the first already saved the data |

A `presenceCreateDevice` failure is **not** swallowed the way `both` swallows a
`personalDevice` one. There, the assignment had already recorded the data and the personal
device was a bonus; here the device write *is* the record, and the only call that answers
with the device's own id.

---

## 5. What the backend is missing

### 5.1 `deviceOs` in `IPInput` — blocks a ticket criterion

The ticket requires sending the operating system. `IPInput` **has no field for it
today** —confirmed against production by introspection on 2026-09-19—: it has
`deviceType`, `deviceBrand` and `deviceModel`, but no `deviceOs`. Production answers
`Field "deviceOs" is not defined by type IPInput` with HTTP 400, which is what the agent
detects to resend without it. Note that every other `IPInput` field is required
(`String!` / `Boolean!` / `[ID!]!`) in production.

The agent sends it anyway. If the backend replies with a validation error mentioning
`deviceOs`, it lowers the flag in memory, resends without the field and logs it; the
**View details** menu shows `OS accepted by API: no`. In other words: the registration is
not lost, but **the OS does not arrive** until the field is added.

Request to the backend team:

```graphql
input IPInput {
  ip: String!
  friendlyName: String
  machineId: String
  deviceType: String
  deviceBrand: String
  deviceModel: String
  deviceOs: String     # ← add
  deviceShared: Boolean
  panelistIds: [ID!]
}
```

Stuffing it into `deviceModel` would be the cheap alternative, and it is exactly what has
not been done: it pollutes the model and makes the data unrecoverable downstream.

### 5.2 `Router.household` in the deployed schema

The schema in the `graphql` repo has it (`type Router { … household: Household }`), but
the deployment serving the routers is ahead on other fields (`machineId`,
`deviceBrand`… are not in the repo's `schema.graphql`). If `household` were not exposed,
the agent keeps working without a household id; only the `personalDevice` mode is lost.

### 5.3 A device type for computers

`PersonalDeviceType` does not distinguish a PC from a beacon. If the chosen path ends up
being `presenceCreatePersonalDevice`, the enum needs to grow (`COMPUTER`, or `DESKTOP` /
`LAPTOP`) so the panel does not have to infer it from the `model` text.

---

## 6. What the agent needs from the next contract

This was the checklist written before the contract existed. **It is history now, not a
list of asks**: §4b is what arrived, and the contract is not open to renegotiation. Three of
the requirements below went unmet, and each is a permanent constraint rather than a pending
request:

- the **credential** (6.2) — unknown until a real call; the agent has nothing else to offer
- the **idempotency key** (6.2) — worked around agent-side, see §4b
- the **IP** (6.3) — no field for it, which is why `deviceAndRouterIps` is the default mode

### 6.1 Resolve the household from the node's MAC — one query

| In | Out |
|---|---|
| the node's MAC (`wan_mac` from `router_info.sh`) | an id to key the assignment on, the household id, and the household's panelists (`id` + a display name) |

The agent knows none of these by itself and cannot ask the panelist for them. If any is
missing it stops with *Household not set up in the panel yet*, tells the panelist once
that support has to finish the setup, and keeps retrying. See §5 of the README.

### 6.2 Report the device and its IP — one mutation

Sent when anything changes (a new IP above all) and, unchanged, every 12 hours.

**In**, everything the agent already gathers today: a stable device key (`machineId`, the
node's encoding of the MAC), `ip`, `friendlyName`, `deviceType`, `deviceBrand`,
`deviceModel`, `deviceOs`, `deviceShared`, `panelistIds`, and the device id from a
previous call once one has been issued.

**Out**: **the device's own unique identifier**. `presenceCreateDevice` answers this —
`deviceId` in its payload — and it is the only call that does. `routerIpsAssigment` returns
`{ id, macAddress }` of the *router*, and `presenceCreatePersonalDevice` returns a
`PersonalDevice.id` from a mutation that was never the verified path.

`ip` is **not** accepted by `presenceCreateDevice`; see the end of §4b.

Two properties the mutation must have:

- **Idempotent on the stable key.** The same device is reported again on every IP change
  and twice a day forever. It has to update, never accumulate duplicates.
  → **Not met.** `presenceCreateDevice` is idempotent on a client-generated `requestId`,
  not on `machineId`. Two creates with different keys make two Devices for one computer.
  The agent carries the key across restarts to compensate (§4b), which works until both
  `state.json` and every device-id slot are lost — reinstall plus profile wipe — and then
  nothing on either side notices the duplicate.
- **Accept the node's JWT.** The agent has no credential of its own: it uses the `jwt`
  from `get_config.sh`, which belongs to the *router*, not to a person, and sends it in
  `Authorization` with no `Bearer` prefix. A resolver that derives the panelist from the
  JWT user (as `presenceCreatePersonalDevice` does, via `validatePanelistByUserId`) will
  reject it. This is the single most likely way a new contract fails against this agent.
  → **Not met.** `presenceCreateDevice` takes `context.userId` and derives `householdId`
  from that panelist. This is the predicted failure, arrived. It cannot be fixed from the
  panelist's computer: the agent has no panelist credential and asks for none.

### 6.3 Two decisions the contract has to make

1. **IP history or current IP?** The agent reports one entry per change plus a heartbeat
   every 12 hours. If rows are append-only that is at least two rows per device per day;
   if the row is updated in place, the heartbeat is what proves the address is still
   current. Either works for the agent — the backend has to say which it is, because it
   changes whether the heartbeat should exist at all.
2. **Who retires an old IP?** Today `routerIpsAssigment` takes `removeIps`. If the new
   contract infers it server-side from the new report, the agent sends less; if not, it
   needs to be told which call releases the previous address.

### 6.4 How much work the swap is

The mutation document and its operation name are already replaceable from the `.env`
without recompiling, **as long as the variables stay the same** (`$routerId`, `$addIps`,
`$removeIps`, `$updateIps`). A contract with different variables is a code change, and a
small one: request building lives in `PersonicoreApi.Build*`, which the offline dump
shares, so a new shape is written once and both the real send and the desktop dump follow.

---

## 7. One computer, two metered homes — open

A panelist with two homes, each with its own meter node, or a laptop carried between two
panel households. Nothing in the identity distinguishes the two: `machineId` is a pure
function of the MAC (`MachineId.FromMac`), so it is the same in both homes, and the device
id is one value per computer. **The only thing that says which home a report belongs to is
`routerId`, and the agent holds exactly one of those at a time** — `state.json` has a single
`routerMac` / `routerId` / `householdId`.

What is **verified in the code** today:

| | |
|---|---|
| the device id | one per computer, write-once, three slots, no household in the key. It survives deleting `state.json`, *Forget the owner* and a reinstall — by design, so one computer does not become two Devices |
| the owner | `AgentState.HasOwnerFor` keys on (router MAC, machineId), so arriving in the other home re-asks who this computer belongs to. The answer is single-valued: answering for home B overwrites home A, and going back asks again. **Every time, in both directions** |
| the device write | the stored `deviceId` plus the *current* `routerId` — an update of the same row with a different router |
| the address report | carries `routerId` per observation, and refuses with *The Network Device is not registered on this router* when that is not the router the Device is registered on. The agent maps it to `DeviceOnAnotherNodeException` (own sentence, long backoff) |
| the idempotency keys | both fingerprints include `routerId`, so the two homes never collide on a key |

What is **not verified**, and decides everything else: whether `presenceCreateDevice` with a
`deviceId` and a *different* `routerId` moves the row, refuses, or ignores `routerId`. With
the household derived from the router, a move that is accepted silently re-homes the Device —
including into another panel household. Introspection cannot answer this; the backend team
can, or a controlled test in stage against a throwaway `machineId`.

`SCENARIO=moved-household python3 tools/fake-graphql.py` reproduces the refusing half offline:
the household reads, the device write succeeds, the address report refuses the identity.

### The two shapes this could take

1. **A Device belongs to a household, and the router belongs to the observation.** The
   router is already on the IP report and on the Local IP Period, so the data is shaped for
   this; what would have to change is the identity check — validate that the Device is in the
   *household* the router belongs to, not that it is registered on that router. One Device
   then covers both homes of one household, and its periods say which node each was seen on.
2. **A Device belongs to a router.** Then two nodes are two Devices for one computer, and the
   agent is wrong today in a different way: the device id has to be kept **per router**, not
   per computer, or the second home keeps trying to update the first home's row for ever.

Either way the agent needs the same change — **the registration becomes per node instead of
per computer**: the device id keyed by router (or household) rather than a single value, and
the owners, name and profile corrections kept per router so that returning home does not
re-ask a question that was answered last month. The proxy rule is already per node
(`AgentState.IsRegisteredOnNode`), and with a per-node registration it starts working in both
homes instead of only the last one registered.

### What the agent does in the meantime (implemented)

Single-homed, and explicitly rather than by accident:

- **the device id records the node that issued it** — `<id> <MAC>` in each slot; a slot holding
  the id alone is from before this and means "unknown", which is treated as *ours* so that
  every existing installation keeps reporting. The first accepted write records the node;
  nothing on the computer can know it any later than that;
- **nothing is written on another node.** No create, no update, no address report. The panelist
  is told the computer is registered on another Personicore router and that support has to move
  it;
- **`The device doesn't exist!` is acted on only where the row was made.** This is the one that
  mattered: asked in the second home it is what a household-scoped lookup answers for somebody
  else's id, and the agent was entitled to read it as "support deleted this row", let the id go
  (`DeviceIdStore.Forget`) and create again — one orphaned Device in the home it came back to,
  per return trip, silently;
- **`allowNodeChange`** in `config.json`, off, lets the write go out anyway for the one case
  where it is wanted: support replaced the node record inside the same household. Even then the
  id is never let go of, so the worst a wrong guess costs is a refused mutation.

So nothing is corrupted, no row is duplicated, and the second home simply does not report —
which is the right way to be wrong while the question above is open.

## Proposed hourly proxy status report

See [the proxy status proposal](PROXY-STATUS-PROPOSAL.md) for
`presenceReportDeviceProxyStatus`: enabled vs. actually active, an hourly heartbeat,
idempotency and authorization rules. This is a proposed operation, not a deployed contract;
the agents do not call it yet.
