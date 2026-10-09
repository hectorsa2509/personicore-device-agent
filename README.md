# Personicore Device Agent

Resident agent that runs on the panelist's computer, discovers itself inside the home
network and **registers the computer as a personal device of the household**, asking
first who it belongs to.

It implements the *app self-report* lane of the
[device identification contract](../MANUAL-mac-identificador-unico.html): the only lane
that produces **authoritative** brand, model, type and operating system (`conf=high`)
instead of values inferred by fingerprinting.

```
personicore-device-agent/
├── core/        ← shared logic: discovery, GraphQL, cycle, state (all platforms)
├── windows/     ← Windows agent (system tray) — implemented
├── macos/       ← macOS agent (menu bar) — built and run on a real Mac
└── docs/        ← architecture, GraphQL contract and cross-platform plan
```

## What it does

| Ticket criterion | How it is met |
|---|---|
| Creates personal devices in the household | GraphQL mutation with the identity observed by the meter node |
| The household ID is filled in automatically from the router | `router_info.sh` → node MAC → `getPanelistsRouter` → `router.household.id` |
| The panelist is asked who the device belongs to | Dialog with the household's panelist list; shared computers supported |
| Sends type, brand, model and OS | Read from the computer itself (registry + WMI), editable except the OS |
| Proxy switch | Tray switch that points the system proxy at the meter node, port 3129 |
| The node's page is one click away | *Open the meter node…* opens it in the browser, at the address the agent reached it at |
| Nothing happens off the panel network | Without a meter node the menu only offers *View details…* (with the log folder) and *Refresh now* |

## No meter node, no actions

Until a meter node answers on the network, both agents stay in read-only mode: the status
line says *No meter node on this network* and the only live menu items are **Refresh now**
(sweep again) and **View details…** (which opens the log folder). Registering, the proxy
switch and the start-at-login item are greyed out, because all three depend on a node that
is not there and would only half-work.

Two deliberate exceptions: the proxy switch can always be turned **off** — leaving a
panelist with a switch they cannot undo would be worse than the lock it enforces — and in
dry-run mode (`PERSONICORE_DRY_RUN`) there is no node to wait for, so nothing is locked.

The rule lives in `AgentSnapshot.ActionsUnlocked` in the shared Core, and so does every
line of text the panelist reads (`AgentText`): the two agents cannot drift apart in
wording or in behavior.

## No assignment in the panel, no registration

The node can answer and the backend can be perfectly healthy while the panel still has
nobody to assign the computer to. Three shapes of the same dead end, all reported as
`HouseholdNotAssigned` — *Household not set up in the panel yet*:

| What came back | What the panelist is told |
|---|---|
| `getPanelistsRouter` knows no router for the node's MAC | the router is not set up in Personicore yet |
| The router has no id | same — for an assignment, an id-less router is an absent one |
| The household has no panelists | this home has no people registered yet |

In all three the agent says once that Personicore support has to finish the setup and
that nothing needs to be done on this computer, then keeps retrying quietly. It is said
**once per transition**, not once per retry: the cycle comes back every minute, and a
notification every minute about something the panelist cannot fix is harassment.

Registration is off while this lasts (the panelist list and router id are cleared, so
`CanRegister` is false), but the proxy, the details window and the logs stay available —
the node is there and measurement still works.

A reply that cannot be parsed at all is deliberately *not* reported this way: it is an
error, because it points at the wire (a captive portal, a proxy error page, a schema
change), not at the panel.

## Stack

One language, C# 13 on .NET 9, for everything. Only the drawing layer changes.

| Project | Target | UI framework |
|---|---|---|
| `core/Personicore.DeviceAgent.Core` | `net9.0` | none — discovery, GraphQL, cycle, state, all panelist-facing copy |
| `windows/src/Personicore.DeviceAgent` | `net9.0-windows` | WinForms (`NotifyIcon` tray + forms) |
| `macos/src/Personicore.DeviceAgent.Mac` | `net9.0` | Avalonia 12.1 (`TrayIcon` → `NSStatusItem`, `LSUIElement`) |

Both ship self-contained, so no .NET is installed on the panelist's machine. The Windows
project sets `EnableWindowsTargeting`, so all four packages build from a Mac — a Windows
machine is still needed to *run* them.

**Licensing:** Avalonia and its dependencies (Avalonia.Desktop, Themes.Fluent, SkiaSharp,
HarfBuzzSharp, MicroCom) are **MIT** — free for commercial, closed-source use. The paid
Avalonia products are *XPF* (WPF compatibility layer) and *Accelerate* (commercial dev
tooling); neither is used here.

## Right now it reads, and sends nothing

`sendToBackend` is **false** by default, because the contract for storing devices and
their IPs is being rewritten and there is nothing correct to send yet. The agent still
does everything else: it finds the meter node, reads the household and the panelists
from GraphQL, asks who the computer belongs to, and builds the exact requests it would
have POSTed — then writes them to **`Personicore-outbox.txt` on the desktop** instead of
sending. The menu shows the state as *Simulated* and offers **View what would be sent…**,
which opens that file.

The file is the proof: the same builders produce the dump and the real request, so
turning sending on later changes one word in `config.json` and nothing else.

```json
{ "sendToBackend": true }
```

## What is reported, and when

The assignment carries the computer's current IP, and it goes out when there is a reason:

| Reason in the log | When |
|---|---|
| `first report` | the first time after installing |
| `something changed` | a new IP — or a new owner, name or hardware profile |
| `the periodic report is due` | every 12 hours (`reportIntervalSeconds`) even if nothing changed |
| `the clock moved backwards` | the clock jumped back, which would otherwise park the next report |

*Refresh now* is not on that list: it looks at the network again and never reports on its
own. Sending on demand is the owner dialog's job, where the panelist sees what is sent.

The cycle itself still runs hourly — it is what discovers the node, notices a change of
owner and watches the proxy — but a cycle with nothing new to say stays quiet. A changed
IP never waits for the twelve hours: the OS wakes the agent on a network change, so it
usually goes out within seconds.

### The identifier the panel issues

When the backend hands out an identifier for this computer, it is stored **outside
`state.json`**, in several places at once, read-only and hidden, and healed from
whichever copy survives. Forgetting the owner, deleting the state file or reinstalling
must not create a second device in the panel for the same computer.

It is not undeletable, and cannot be: the agent runs as the panelist, without elevation,
so anything it can write that person can remove. Making it genuinely out of reach needs
an installer running as administrator, or widening the macOS root helper, which is
deliberately limited to APPLY/REPOINT/RESTORE/OWNED/STATUS.

Note that today no identifier arrives: `routerIpsAssigment` returns the **router's** id,
not the device's. The only per-device id in the current contract is `PersonalDevice.id`,
from `presenceCreatePersonalDevice`, which only runs when `registrationMode` includes it.
See §6 of [`docs/GRAPHQL-CONTRACT.md`](docs/GRAPHQL-CONTRACT.md) for what the next
contract has to provide.

## The cycle, in one line

> ping the meter node (or sweep the segment for port 22) → ask it for the credential →
> resolve household and panelists → find out which IP and MAC it sees us with → ask for
> the owner (first time only) → send, and re-assert the assignment on every IP change

The whole flow, step by step and as it runs once the mutations exist, is in
[`docs/FLOW.md`](docs/FLOW.md). Internals are in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); the exact queries in
[`docs/GRAPHQL-CONTRACT.md`](docs/GRAPHQL-CONTRACT.md).

## Current state: reads the household, holds the sends

Like the router's `network.html`, the agent takes the `jwt` and `graphql` endpoint from the
node's `get_config.sh` and **reads** household and panelists with `getPanelistsRouter`, so
the panelist picks the owner from the real household list. The **mutations are held**:
`sendToBackend` is false while the device/IP contract is rewritten, and the registration
goes to the desktop file instead. No configuration file is needed for any of this.

A `.env` file ([template](windows/.env.example)) is optional: it can override the endpoint
or token, replace the mutation without recompiling, or turn on a dry run
(`PERSONICORE_DRY_RUN=true`), which writes `Personicore-outbox.txt` to the desktop with the
exact body of the mutation instead of sending it. Example of the format in
[`windows/outbox.sample.txt`](windows/outbox.sample.txt).

The cycle repeats **every hour**, and runs early when the network addressing changes.

## Getting started

To produce the packages that are handed to panelists, in the root of the project:

```bash
./build.sh            # both platforms · 4 zips
./build.sh windows    # win-x64 + win-arm64
./build.sh mac        # macos-arm64 + macos-x64
```

The zips land next to this README, are checked with `unzip -t` before being listed, and
are gitignored — the originals stay in `windows/dist` and `macos/dist`. Windows builds
from any OS; the macOS bundle needs a Mac, because it uses `codesign` and `iconutil`.

The two scripts below are what `build.sh` calls, and can still be used on their own.

- **Windows** → [`windows/README.md`](windows/README.md). Builds and publishes from any
  OS, including macOS:

  ```bash
  ./windows/build/publish.sh
  ```

  It produces `windows/dist/win-x64/` and `windows/dist/win-arm64/`, one `.exe` each.

- **macOS** → [`macos/README.md`](macos/README.md). Builds from macOS:

  ```bash
  ./macos/build/make-app.sh
  ```

  It produces `macos/dist/arm64/` and `macos/dist/x64/`, one `.app` each. Built but not
  run yet: the README has the test plan for the first run.

- **Linux** → [`docs/CROSS-PLATFORM-PLAN.md`](docs/CROSS-PLATFORM-PLAN.md).

- **Android and iOS** → [`docs/MOBILE-PORT.md`](docs/MOBILE-PORT.md): node discovery,
  the HTTPS certificate and the proxy, written for the two mobile developers — **in
  Spanish**, which is the one document here that is. It says which parts port unchanged
  and which two cannot exist on a phone the way they do here.

## Status

| Piece | Status |
|---|---|
| Windows agent (tray, discovery, registration) | Implemented |
| Reads household and panelists with the router's jwt + endpoint | Implemented, verified against production |
| Saves with `routerIpsAssigment` using the router's endpoint | Implemented, **default mode** |
| Dry run to a desktop `.txt` (`PERSONICORE_DRY_RUN=true`) | Implemented |
| Hourly cycle | Implemented (`pollIntervalSeconds: 3600`) |
| Windows x64 and ARM64 (including Windows 10 on ARM) | Implemented — one `.exe` per architecture |
| User interface in English | Implemented |
| MSI packaging / code signing | Pending — unsigned, SmartScreen warns the first time it runs |
| Proxy switch (Windows) | Implemented — node IP:3129, visible in Settings › Network & internet › Proxy |
| Proxy follows the node when its IP changes | Implemented — checked every 30 s while the proxy is on, plus a watchdog every minute on both platforms |
| Shared Core library (Windows and macOS use the same logic) | Implemented |
| macOS agent, phase 1 (registration) | Implemented — run on a real Mac, registers and re-asserts |
| macOS proxy switch (root helper, one password at install) | Implemented — helper installed, switch exercised on a real Mac |
| macOS signing and notarization | Pending — needs an Apple Developer account |
| Linux agent | Pending — plan in [`docs/CROSS-PLATFORM-PLAN.md`](docs/CROSS-PLATFORM-PLAN.md) |
| `deviceOs` in `IPInput` (backend) | **Missing in production** (confirmed): the OS is dropped on send until it is added — see contract |
| `presenceCreatePersonalDevice` with the router JWT | Unverified — see contract |

## Configuring this public source distribution

Domain names under `example.invalid` are placeholders. Configure `routerHost` in
`config.json` and your own GraphQL and ingest endpoints before use. Service and
launchd identifiers use the `Personicore` / `com.personicore` names; existing
installations under other identifiers need a deliberate migration. No production
credentials, compiled packages, screenshots, or exported documents are included.
The bundled `.crt` contains only a public CA certificate, never a private key.
Do not distribute builds containing shared production tokens.
