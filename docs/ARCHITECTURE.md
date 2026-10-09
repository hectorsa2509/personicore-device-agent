# Agent architecture

Shared by Windows and macOS. Describes the cycle, the non-obvious decisions and why they
were made.

---

## 1. Where it fits

The meter node (TR3000 / AXT1800) is a **transparent bridge**: it sits between the
panelist's devices and the ISP router, without being the gateway or the DHCP server. It
observes from the same L2 and publishes what it sees.

```
[panelist's computer]  ← the agent runs here
        │  same L2
        ▼
[meter node]  br-lan · arpspoof · squid · dnsmasq
        │
        ▼
[ISP router] → internet → [GraphQL backend]
```

Of everything the node observes, **the client's MAC is the only unique identifier**.
Brand, model, type and OS are metadata hanging off that key. Today the node infers them
from hostname, DHCP option 60, User-Agent and OUI — with `conf` between `low` and
`medium`. The agent exists to replace that inference with data declared by the computer
itself.

---

## 2. The cycle

```
  ┌─ every pollIntervalSeconds (1 h by default) ──────────────────────┐
  │                                                                   │
  │  1. discover node     ping router.example.invalid          │
  │                       └ fallback: sweep the segment for port 22,  │
  │                         then validate with /cgi-bin/router_info.sh│
  │                                                                   │
  │  2. credential        GET /cgi-bin/get_config.sh → { jwt, graphql }│
  │                                                                   │
  │  3. node identity     GET /cgi-bin/router_info.sh → wan_mac       │
  │                                                                   │
  │  4. household         getPanelistsRouter(macAddress: wan_mac)     │
  │                       → routerId · householdId · panelists        │
  │                                                                   │
  │  5. own identity      local route to the node → local IP          │
  │                       GET /cgi-bin/list_clients_json.sh           │
  │                       → mac + machineId the node sees for that IP │
  │                                                                   │
  │  6. owner             is there a declared owner for (routerMac,   │
  │                       machineId)?  no → ask the panelist          │
  │                                                                   │
  │  7. send              presenceCreateDevice(input: {...})          │
  │                       (registrationMode; routerIpsAssigment for    │
  │                        the IP, which that input has no field for)  │
  └───────────────────────────────────────────────────────────────────┘
```

Step 7 sends for real by default, with the endpoint and jwt from step 2, like the
router's `network.html`. With `PERSONICORE_DRY_RUN=true` it writes the exact body it would
have sent to the desktop instead (§3.7).

Step 7 repeats on every cycle **on purpose**. The assignment in the backend is indexed by
IP, and the IP is handed out by the ISP router: a new lease invalidates it. That is why
this is a resident agent and not an installer that runs once. In addition, an addressing
change (`NetworkChange.NetworkAddressChanged`) runs the cycle early instead of waiting
for the next poll.

---

## 3. Decisions

### 3.1 It is a tray agent, not a Windows service

The ticket asks for a "Windows service". A Windows service runs in **session 0**, with no
desktop: it cannot draw an icon in the notification area or open a dialog, which is
exactly what the criterion *"the panelist is asked who the device belongs to"* requires.
The right way to cover both is a per-user-session agent, launched from the user's `Run`
key.

If continuity without a signed-in session were ever needed, the pattern is to split it
in two: a service that maintains the assignment and a tray agent that only asks the
question. It has not been done because it duplicates state and adds nothing today: with
no signed-in session the computer is not generating traffic to measure either.

### 3.2 Discovery: ping first, port 22 next

Three steps, cheapest first:

1. **ICMP ping to the name.** The node resolves `router.example.invalid` to itself
   via dnsmasq, and the public record points at its private address, so a reply means it
   is reachable. It is a single packet.
2. **If it does not reply, sweep for ports 22, 3129 or 80** — SSH, the Squid explicit
   port and the node's web UI, so that closing any one service does not make the node
   undiscoverable. All three are probed at the same time, so an absent address still
   costs one timeout. Addresses are tried in this order: what the name resolves to, the
   default gateway, every local segment, and the /24 around the resolved address —
   the node is not always on the panelist's own segment. With 128 parallel probes and a
   400 ms timeout, a /24 takes ~0.9 s and two ~1.7 s (measured).
3. **HTTP to `router_info.sh` on the candidates** to get the router MAC, which identifies
   the household. Any other device with those ports open is discarded here.

No login is ever made over SSH: port 22 is only a filter. All data comes from the node's
HTTP CGIs, which need no credentials, so no password is shipped inside the agent.

The segment is computed from the **adapter's real mask**, not assuming /24, and prefixes
shorter than /22 are skipped: sweeping a /16 would take minutes and the node is always on
the panelist's home network.

The IPs the name resolves to are probed **before** the rest of the segment, even if the
ping failed: they are still the most likely address, so they are validated first.

HTTP requests to the node bypass the Windows system proxy: otherwise, on a computer with
a corporate proxy, requests to private IPs would go to the proxy and never reach the LAN.

**Accepted risk**: discovery is not guaranteed. It fails when the node answers neither
the name nor any of the three ports from where the computer is: another segment that is
not routed, client isolation on a guest Wi-Fi, a segment larger than /22 (not swept), or
—on macOS— the local network permission denied. The ports and the host are configurable
(`discoveryPorts`, `routerHost`) so a deployment can point the agent straight at the
node.

### 3.3 The node says the MAC, not the computer

The agent *could* read its own adapter's MAC, but the one that matters is the one in the
node's ARP table: it is what indexes the traffic logs. So the identity is resolved like
this:

1. A "connected" UDP socket is opened towards the node. It sends no packet; it only makes
   the OS resolve the route and assign the local IP. It is the only reliable way to know
   which interface we leave through when Wi-Fi, Ethernet and VPN coexist.
2. The node's client list is requested and the entry with that IP is looked up.
3. The `machineId` the node reports is used.

The local adapter MAC is only a fallback, when the node has not seen the computer yet
(cold start, before the first ARP sweep). The `machineId` is then computed locally with
the **same algorithm** as the node:

```
base64(mac without ':')  →  strip '='  →  + first 2 hex digits of the MAC  →  + "PMPM"
```

### 3.4 Randomized MAC

The agent checks the locally administered bit (second hex digit ∈ `2 6 a e`) and warns
in the dialog. On Windows randomization is **off** by default, so in practice the real
MAC is seen; on macOS it depends on the per-network setting. Registration is not blocked:
as the contract says, turning off the private MAC is a request, not a requirement.

### 3.5 The household ID is never typed

The criterion requires it to be filled in automatically from the router. The chain is:

```
router_info.sh → wan_mac → getPanelistsRouter → router.household.id
```

The panelist only sees it as confirmation in the dialog header. If the backend deployment
does not expose `Router.household`, the agent detects it, logs it and carries on:
`routerIpsAssigment` is resolved by `routerId` and does not need the household id, and
`presenceCreateDevice` does not take one — it derives the household from the credential.

### 3.6 Degrading against an older backend

Two fields may not exist yet in the deployed schema: `Router.household` and `deviceOs`
in `IPInput`. This applies to `routerIpsAssigment` only: every field of
`PresenceCreateDeviceInput` is required, so on that path there is nothing to give up and
nothing is dropped. In both cases the agent retries without the field and records it. Telling
"the field does not exist" apart from "a real error" is done by looking for GraphQL
validation error markers (`Cannot query field`, `Unknown argument`, …), not just the field
name: otherwise a permissions failure would be mistaken for an outdated backend and the
agent would silently degrade.

### 3.7 Sends by default, like network.html

The agent uses the same credential and endpoint as the router's own web UI, served by
`get_config.sh`, and saves for real without any configuration file. There is a single
switch to stop it, and it lives outside the binary: `PERSONICORE_DRY_RUN=true` in the
`.env` keeps reading the household but writes the mutations to `Personicore-outbox.txt`
on the desktop instead of sending them.

The dump is not a description of what would be sent: it is produced by the same
`GraphQlRequest` the POST would use. That is why `PersonicoreApi` separates *building the
request* (static `Build*` methods) from *sending it*.

---

## 4. Persisted state

`%LOCALAPPDATA%\Personicore\DeviceAgent\state.json` — in the **user** profile, not the
machine's: two Windows accounts on the same PC are two different people in the household,
and each one answers the owner question separately.

The question is asked again when the `routerMac` changes (the panelist moved to another
node) or the `machineId` changes (different adapter). An IP change does **not** trigger
the question: only a silent resend.

The write is atomic (temporary file + `move`): a power cut halfway through must not force
the panelist to declare the owner again.

---

## 5. What Windows and macOS share

Identical on both platforms — it is the contract, not the implementation:

- node discovery (ping + port 22 sweep + HTTP validation)
- the node's three CGIs and their formats
- the GraphQL queries and mutations
- the `machineId` algorithm
- the shape of the persisted state
- the "ask again" rule

Only the platform layer changes:

| | Windows | macOS |
|---|---|---|
| Resident UI | `NotifyIcon` (notification area) | `NSStatusItem` (menu bar) |
| Startup | user `Run` key, or hourly scheduled task | `LaunchAgent` with `StartInterval 3600` |
| Output without endpoint | `%USERPROFILE%\Desktop` | `~/Desktop` |
| Brand / model | `HARDWARE\DESCRIPTION\System\BIOS` registry + WMI | `system_profiler SPHardwareDataType` / `sysctl hw.model` |
| Computer type | SMBIOS chassis code | model family (`MacBook*` → laptop) |
| State | `%LOCALAPPDATA%` | `~/Library/Application Support` |
