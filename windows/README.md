# Windows agent

Notification-area (system tray) application that registers this computer as a personal
device of the household. .NET 9 + WinForms, no installer yet: a single `.exe` per
architecture.

---

## Build

The project sets `EnableWindowsTargeting`, so it **builds from any OS** —including
macOS—, even though it only runs on Windows.

```bash
./build/publish.sh
```

| Mode | Command | Output |
|---|---|---|
| Self-contained *(default)* | `./build/publish.sh` | `dist/win-x64/` and `dist/win-arm64/`, a ~46 MB `.exe` in each; no .NET needed on the panelist's computer |
| Framework-dependent | `./build/publish.sh --framework` | small `.exe`, requires .NET Desktop Runtime 9 installed |

On Windows the equivalent is `build\publish.ps1` (with `-FrameworkDependent` for the
second mode). The icon is regenerated on every publish with `build/make-icon.py` if
Python + Pillow are available; otherwise the resources already in `Resources/` are used.

Each `dist/<arch>/` folder also gets `INSTALL.txt` (end-user instructions) and
`.env.example`.

For development:

```bash
dotnet build src/Personicore.DeviceAgent -c Release
```

For a real panel, use the self-contained mode: you cannot assume the panelist's computer
has the .NET runtime.

---

## Install on the panelist's computer

1. Pick the `.exe` for the computer's processor (Settings → System → About → *System
   type*):

   | System type | Executable |
   |---|---|
   | 64-bit operating system, **x64** processor (Intel/AMD, the vast majority) | `dist/win-x64/PersonicoreDeviceAgent.exe` |
   | **ARM**-based processor (Snapdragon, Surface Pro X…) — Windows 10 or 11 | `dist/win-arm64/PersonicoreDeviceAgent.exe` |

   On Windows 10 on ARM **only** the `win-arm64` build works: that OS emulates x86 but
   not x64. On Windows 11 ARM both work; the native one runs better.
2. Copy it where it will stay, for example
   `%LOCALAPPDATA%\Personicore\PersonicoreDeviceAgent.exe`. If it was shared as a `.zip`,
   extract it first: running it from inside the zip launches it from a temporary folder.
   The `.exe` is the whole agent — the endpoint and the credential are compiled into it,
   so there is nothing to keep beside it and nothing for the panelist to configure.
3. Run it. **The first time, SmartScreen shows a warning** —*"Windows protected your
   PC"*— because the `.exe` is not signed yet: click **More info → Run anyway**. It only
   happens once per computer. The icon appears in the notification area.
4. Icon menu → **Start with Windows**. It writes the user's `Run` key, without asking for
   administrator rights.

No elevation is needed at any point: everything the agent reads (`HKLM` registry for
BIOS and Windows version, read-only WMI) is accessible to a standard user.

### Why it is not a Windows service

A service runs in session 0 and cannot draw a tray icon or open a dialog, which is
exactly what the "ask the panelist who owns the computer" criterion requires. The
reasoning is in [`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) §3.1.

---

## Sending to the Graph

The panelist never creates a `.env`. Either the package carries a credential — compiled
into the `.exe` at build time by `build/write-packaged-env.sh`, never committed — or, with
none, the agent takes `jwt` and `graphql` from the node's `get_config.sh` like the router's
`network.html` does. Either way it reads the household with `getPanelistsRouter` and saves
the registration with `presenceCreateDevice`.

An optional `.env` file can change that. Template in [`.env.example`](.env.example) — it is
no longer shipped in the package, it lives here for developers. It is looked up in this
order:

1. the path in `PERSONICORE_ENV_FILE`, if set;
2. **next to the `.exe`** (`.env.txt` also works, which is what Notepad leaves when file
   extensions are hidden);
3. **the credential compiled into the executable**, if this build has one;
4. `%LOCALAPPDATA%\Personicore\DeviceAgent\.env`;
5. `%PROGRAMDATA%\Personicore\DeviceAgent\.env`.

Three and four are in that order on purpose: a `.env` left in `%LOCALAPPDATA%` after a test
must not be able to point a panelist's build at another backend, while a `.env` dropped next
to the `.exe` is a deliberate developer override and still wins.

| Key | Purpose |
|---|---|
| `PERSONICORE_DRY_RUN` | `true` ⇒ read the household but **send nothing**; write it to the desktop file |
| `PERSONICORE_GRAPHQL_ENDPOINT` | Where the POST goes. Empty ⇒ the router's endpoint |
| `PERSONICORE_GRAPHQL_TOKEN` | Fixed credential. Empty ⇒ the meter node's JWT is used |
| `PERSONICORE_GRAPHQL_OPERATION` | Operation name, for the log and the outbox file |
| `PERSONICORE_GRAPHQL_MUTATION` | Mutation document on one line (`\n` as line break) |
| `PERSONICORE_GRAPHQL_MUTATION_FILE` | Same thing, pointing to a `.graphql` — more convenient. Relative path ⇒ relative to the `.env` |

A real environment variable wins over the file, so you can test another endpoint from a
console without editing anything.

**The mutation can be changed without recompiling**, but it must accept the same
variables, because those are the ones the agent builds:

```graphql
$routerId: ID!
$addIps: [IPInput!]
$removeIps: [RemoveIPAssignmentInput!]
$updateIps: [IPInput!]
```

What is free is which mutation is called and which fields it returns.

## Dry run: a `.txt` on the desktop

With `PERSONICORE_DRY_RUN=true` in the `.env`, the agent **does not send anything to the
API**. It still **reads** the household, so the dialog shows the real household panelists
and the IDs are real. Then it writes what it would send to:

```
%USERPROFILE%\Desktop\Personicore-outbox.txt
```

On the desktop of whoever runs it, on any computer, without anyone having to explain
where to look. If the desktop cannot be resolved (redirected profile) it falls back to
`%LOCALAPPDATA%`. Icon menu → **View what would be sent…** opens it directly; there is an
example of the format in [`outbox.sample.txt`](outbox.sample.txt).

Each entry has the household context, the observed identity, the four ticket fields,
the panelist's answer and **the exact body** of every request: query and variables. It is
not a description of what would be sent — it is built by the same code that would do the
real send, so it cannot drift out of date.

The file is appended to: each cycle adds an entry. Seeing the same assignment repeated
with a different IP is the proof that the agent re-asserts after a lease change.

`routerId`, `householdId` and `panelistIds` come from that read, so they are the real
ones. Only if the household cannot be read (no node on the network, the node serves no
`jwt`, or the backend does not answer) does the dialog fall back to asking for the owner
**as free text**, and those three fields come out empty. When the list becomes available
later, the agent asks once more so the owner is picked with a real ID.

## Cadence: every hour

The agent revalidates household, IP and assignment **every hour**
(`pollIntervalSeconds: 3600`). It also runs the cycle early, without waiting for the hour,
when the network addressing changes — which is when the assignment really needs to be
re-asserted, because the backend indexes it by IP.

With **Start with Windows** enabled that is enough: the agent stays resident and keeps
the hourly cycle on its own. If you also want it to come back even when the panelist
closes it with *Exit*, or want an auditable history in Task Scheduler:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\install-scheduled-task.ps1 -ExePath "$env:LOCALAPPDATA\Personicore\PersonicoreDeviceAgent.exe"
```

`-ExecutionPolicy Bypass` is needed because Windows blocks downloaded `.ps1` files by
default; it only applies to that run.

It registers a task for the current user, without elevation, that starts at sign-in and
repeats every hour. It is an **alternative** to the `Run` key, not a complement: using
both does not break anything (the second instance exits on its own), but adds nothing.

Both the `Run` key and the task launch the `.exe` with `--autostart`: if the agent is
already running, the new instance exits **silently**. Without the argument —the panelist
double-clicking it— it shows that it is already in the notification area. If the `.exe`
is moved, the `Run` entry is corrected on the next start.

## Proxy switch

Icon menu → **Use Personicore proxy** turns the Windows proxy on or off. When on, it
points to **the meter node's IP, port 3129** —the IP comes from `router_info.sh`, so it
follows the node if its address changes— with local and private addresses bypassed.

It is the per-user proxy, set through WinINet (no administrator needed), so it shows in
**Settings → Network & internet → Proxy → Manual proxy setup** ("Use a proxy server: On",
address and port 3129) and Edge, Chrome and most apps use it. Firefox uses it when set to
"Use system proxy settings", its default.

| Situation | What the agent does |
|---|---|
| Switch on, node found | Proxy = node IP:3129. The panelist's previous proxy settings are saved first |
| Switch on, no node (away from home) | **Paused**: previous settings restored so internet keeps working; re-applied when the node is back |
| **The node changes address** (rebooted, new DHCP lease) | Checked every **30 s** while the proxy is on: the name is resolved again and the proxy moves to the new address in seconds. Browsing never stops |
| Switch off | Previous settings restored |
| Turned off in Windows Settings | The switch turns off too; the agent does not fight the setting |
| Exit | Previous settings restored; re-applied on the next start if the switch is on |
| Agent not running at all (closed, killed, crashed) | A **watchdog** scheduled task runs every minute: it follows the node to its new address if it is still here, and removes the proxy when no node answers at all |

The agent's own traffic (node CGIs and GraphQL) never goes through the proxy.

The watchdog is a **user** scheduled task ("Personicore Proxy Watchdog", no elevation)
registered when the switch goes on and deleted when it goes off. It runs the same `.exe`
with `--proxy-watchdog`: one check, no window, and it exits immediately when the agent
never applied a proxy. A computer that never turned the proxy on never gets the task.

Why a minute, and why it does not only remove: a dead proxy is a panelist who cannot
browse, and the most common reason for one is not a laptop leaving home — it is the node
rebooting and coming back on another address. Removing the proxy there would keep the
panelist browsing but stop measuring them, so the watchdog tries to follow the node
first and only removes the proxy when nothing answers anywhere.

**HTTPS certificate.** Choose **Install HTTPS certificate…** in the agent menu while
connected to a meter node. The bundled public CA is installed with administrator
authorization: Windows uses Local Machine / Trusted Root Certification Authorities;
macOS uses the System keychain with SSL trust. No private key is shipped and TLS
validation remains enabled. See [`../docs/CERTIFICATES.md`](../docs/CERTIFICATES.md)
for the identity, limitations and removal instructions.

## Permissions

Normal agent operation does not need elevation. Installing the HTTPS certificate is
an explicit administrator action:

| Action | Permission |
|---|---|
| Install bundled HTTPS CA in the machine trust store | Administrator (UAC) |
| Read brand/model/chassis (`HKLM\HARDWARE...\BIOS` registry, local WMI) | Standard user read |
| Read the Windows version (`HKLM\SOFTWARE\...\CurrentVersion`) | Standard user read |
| Start with Windows (`HKCU\...\Run`) | The user only |
| User scheduled task (`RunLevel Limited`) | No elevation |
| Proxy switch (WinINet per-user proxy) | The user only |
| Write state, log and the desktop `.txt` | The user's own folders |
| Outbound HTTP to the LAN and the API | Windows Firewall allows outbound by default |

It only connects **outbound**: it opens no listening port, so the firewall prompt that
appears when accepting inbound connections never shows up either. Requests to the node
bypass the system proxy, so a corporate proxy does not get in the way of the LAN.

Where administrator rights **would** be needed, and why it is not done: installing into
`Program Files`, registering a real Windows service, or creating a machine-level task
(`SYSTEM`).

The real friction is not administrator rights, it is **SmartScreen**: an unsigned
`.exe` shows *"Windows protected your PC"* the first time and you have to click *More
info → Run anyway*. Some corporate antivirus products also look closely at an unsigned
46 MB self-contained executable. The fix is signing with a code-signing certificate
(Authenticode), which is still pending.

## How it finds the router

1. **Ping** `router.example.invalid`. If it replies, it reads
   `/cgi-bin/router_info.sh` over HTTP and gets the router MAC. This is the fast path:
   under a second.
2. If it does not reply, it **sweeps for open ports 22, 3129 or 80**, in this order of
   addresses: the ones the name resolves to, the default gateway, every local segment,
   and the /24 around the resolved address (the node is not always on the panelist's own
   segment). The three ports are probed at the same time, so more ports do not make the
   sweep slower; with 128 parallel probes and a 400 ms timeout, a home /24 takes about
   0.9 s and two /24s about 1.7 s (measured).
3. On those few candidates, it requests `http://<ip>/cgi-bin/router_info.sh` to get
   **the router MAC**, which identifies the household. Any other device with those ports
   open is discarded there.

It never logs in over SSH: the ports are only a filter, and all data comes from the
node's HTTP CGIs (`router_info.sh`, `get_config.sh`), which need no credentials.

The segment is computed from the adapter's real mask (not assuming /24) and networks
larger than /22 are skipped. The IPs the name resolves to are tried before the rest, even
if the ping failed.

| `config.json` key | Default | Purpose |
|---|---|---|
| `discoveryPorts` | `[22, 3129, 80]` | Ports that give away a node: SSH, the Squid explicit port, the node's web UI |
| `portProbeTimeoutMs` | `400` | Wait per probe; multiplies the total sweep time |
| `subnetSweepConcurrency` | `128` | Simultaneous probes |
| `subnetSweepEnabled` | `true` | Setting `false` leaves the agent relying on the ping only |

**Where discovery can still fail**, and what to do:

| Situation | Result | Fix |
|---|---|---|
| The node is on another segment and does not answer the name | Not found | Add its segment or its address to `discoveryPorts`' network by setting `routerHost` to the IP |
| AP/client isolation on the Wi-Fi (guest network) | Not found | Put the computer on the main network |
| All three ports closed on the node | Not found | Add the right port to `discoveryPorts` |
| Segment larger than /22 (corporate) | Only the name is tried | Set `routerHost` to the node's IP |
| macOS: local network permission denied | Not found | System Settings → Privacy & Security → Local Network |

The ping needs no privileges: .NET's `Ping` class uses `IcmpSendEcho2`, not a raw socket.

## The tray icon

It is the **Personicore mark** —the cluster of dots in the logo— tinted with the status
color. The shape never changes, so it is recognizable among twenty tray icons; the color
does:

| Color | Status | What is happening |
|---|---|---|
| Blue | Starting · Simulated | First cycle, or send written to the desktop `.txt` |
| Gray | No meter node | No meter node on this network (away from home, router off) |
| Orange | Owner not set | Household resolved, nobody has said who owns the computer |
| Green | Registered | Computer assigned; re-asserted on every IP change |
| Red | Error | Details in **View details…** and in the log |

**The icon stays there permanently**: the agent has no main window, it lives in the
notification area and only goes away with *Exit*. With **Start with Windows** enabled it
comes back on every sign-in.

A Windows 10/11 detail that confuses panelists: new icons land **hidden in the overflow**
(the `^` arrow on the taskbar), not directly visible. To pin it:

- drag the icon from the overflow onto the taskbar, or
- Settings → Personalization → Taskbar → *Other system tray icons* → turn on
  **Personicore Device Agent**.

Include that step in the panelist's installation instructions: if the icon stays hidden,
nobody sees the orange "owner not set" warning.

### Where the icon comes from

`build/make-icon.py` generates both graphic resources and runs on every publish:

- `Resources/mark-NN.png` — white silhouettes of the mark, one per size, embedded in the
  `.exe`; the tray tints them at runtime.
- `Resources/app.ico` — executable icon: bone-paper plate with the mark in pine ink,
  readable in light and dark themes.

The mark is not copied as a bitmap: it is described as **geometry** (25 dots with their
center and radius, measured on `personicore/pas/public/images/logo.png`). That allows optically
simplifying it per size —at 16 px 9 dots are drawn, not 25— which is what keeps it
readable in the tray. If the logo changes, the dots are measured again and only the
generator's `DOTS` table changes.

---

## Usage

The first time it finds the household, it opens the **Who does this computer belong
to?** dialog with:

- the observed identity (IP, MAC, `machineId`) read-only — the MAC is the key and is not
  declared, it is observed;
- name, type, brand and model pre-filled from the computer and **editable**, because the
  BIOS of many OEMs ships placeholders like `To Be Filled By O.E.M.`;
- the operating system, read-only;
- the household's panelist list. Checking more than one sends it as a shared computer.

The dialog scales with the display DPI (125 %, 150 %…) and is brought to the front when
the agent opens it on its own.

Icon menu: register or change owner, view details, refresh now, use Personicore proxy,
start with Windows, exit.

Screenshots of the interface are in [`screenshots/`](screenshots/).

---

## Configuration

Optional — with no file at all, the defaults work. It is read from:

1. `%PROGRAMDATA%\Personicore\DeviceAgent\config.json` (whole machine)
2. `%LOCALAPPDATA%\Personicore\DeviceAgent\config.json` (this user only; wins)

Comments and trailing commas are accepted; invalid or empty values fall back to the
default and are reported in the log. Template in
[`config.sample.json`](config.sample.json). The most commonly changed keys:

| Key | Default | Purpose |
|---|---|---|
| `pollIntervalSeconds` | `3600` | How often household, IP and assignment are revalidated (minimum 60) |
| `ipWatchIntervalSeconds` | `30` | How often this computer's own address is checked; a change is reported within seconds (minimum 5) |
| `reportIntervalSeconds` | `0` | Re-send unchanged data on a timer. `0` = never: reporting is change-driven |
| `discoveryPorts` | `[22]` | Ports that give away a meter node in the sweep |
| `portProbeTimeoutMs` | `400` | Wait per TCP probe |
| `subnetSweepEnabled` | `true` | Look for the node by sweeping the segment if DNS fails |
| `registrationMode` | `deviceAndIp` | `deviceAndIp`, `deviceAndRouterIps`, `device`, `routerIps`, `personalDevice` or `both` — see the contract. `deviceAndIp` is where this is going, once `presenceReportDeviceIp` is deployed |
| `sendDeviceOs` | `true` | Send the OS as its own field |
| `logLevel` | `info` | `debug` to diagnose discovery |

---

## Files on the computer

```
%USERPROFILE%\Desktop\
└── Personicore-outbox.txt   what would be sent, when the .env has no endpoint

%LOCALAPPDATA%\Personicore\DeviceAgent\
├── state.json               declared owner, household and last assignment
├── .env                     optional; the .exe's own directory wins over this one
├── config.json              optional
└── logs\agent.log           rotated at 2 MB, 3 files
```

---

## Troubleshooting

**The icon stays gray.** No reachable meter node. With `logLevel: "debug"` the log shows
what the ping answered, which segment was swept and which candidates with port 22 open
showed up. Two common causes: the node does not answer ICMP *and* has SSH closed, or this
computer is on a different segment than the node.

**Permanently orange.** The household was resolved but nobody answered the dialog, or it
was cancelled. Menu → **Register this computer…**.

**Never green; always blue "Simulated".** The `.env` has `PERSONICORE_DRY_RUN=true`.
Check `Personicore-outbox.txt` on the desktop to see what it would have sent.

**Red, or the dialog asks to type the owner instead of showing a list.** The household
could not be read. **View details** / the log say why: no node, no `jwt` in
`get_config.sh`, or an error from the GraphQL endpoint. Typing the owner only happens in
dry-run mode; when saving for real the agent needs the list.

**Green but the panel does not show the OS.** Expected until the backend accepts
`deviceOs`; **View details** shows it as `OS accepted by API: no`. See
[`../docs/GRAPHQL-CONTRACT.md`](../docs/GRAPHQL-CONTRACT.md) §5.1.

**Randomized MAC warning in the dialog.** The adapter presents a locally administered
MAC. On Windows randomization is off by default; if it is on, turn it off in Settings →
Network & internet → Wi-Fi → *Random hardware addresses*. It is a request, not a
requirement: measurement keeps working.

**It does not start on a Windows on ARM computer.** It is the x64 build; use the one in
`win-arm64`.

---

## Structure

```
src/Personicore.DeviceAgent/          Windows only
├── Program.cs              startup, single instance
├── Agent/ProxySwitch.cs    proxy switch rules
├── Device/                 brand, model, OS, chassis (registry + WMI)
├── Net/SystemProxy.cs      WinINet per-user proxy
└── Ui/                     tray, owner dialog, details

../core/Personicore.DeviceAgent.Core/ shared with macOS
├── Agent/                  cycle, persisted state, snapshot, outbox
├── Router/                 node CGI client and discovery
├── Graph/                  GraphQL transport and operations
├── Device/                 DeviceProfile, machineId
├── Net/                    local route to the node, MAC, randomized MAC
├── Config/                 configuration and logging
└── Platform/               interfaces each OS implements, per-OS paths
```

## Pending

- MSI/MSIX packaging and code signing — today the bare `.exe` is distributed.
- Verify `presenceCreatePersonalDevice` with the router JWT.
- Agent telemetry of its own (today it only writes to its local log).
- Back off the subnet sweep on foreign networks (office, VPN): today it retries every
  60 s while no node is found.
