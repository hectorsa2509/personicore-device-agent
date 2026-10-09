# Working on this repository

Resident agent that registers a panelist's computer as a device of their Personicore
household, and keeps the panel's view of its IP current. One codebase, two agents:
a WinForms tray icon on Windows, an Avalonia menu-bar item on macOS.

Read [`docs/FLOW.md`](docs/FLOW.md) first — it is the end-to-end behaviour in one page.
[`docs/GRAPHQL-CONTRACT.md`](docs/GRAPHQL-CONTRACT.md) is what the backend does and does
not provide; its §6 is what the next contract must provide.

---

## Layout

| Path | What it is |
|---|---|
| `core/Personicore.DeviceAgent.Core` | `net9.0`. Discovery, GraphQL, the cycle, state, **all panelist-facing text**. No UI, no OS APIs. |
| `windows/src/Personicore.DeviceAgent` | `net9.0-windows`, WinForms. Tray icon and dialogs. |
| `macos/src/Personicore.DeviceAgent.Mac` | `net9.0`, Avalonia 12.1. Menu-bar item and windows. |
| `macos/helper/proxy-helper.sh` | The only part that runs as root. Deliberately limited to APPLY / REPOINT / RESTORE / OWNED / STATUS. |
| `tools/fake-graphql.py` | Stand-in backend for testing the states that depend on what the panel answers. |
| `tools/regression-tests`, `tools/chrome-setup-tests`, `tools/wizard-tests` | Console checks, no test framework. Each throws on its first failing assertion, so a failure is a non-zero exit and not a line to scroll past. |

C# 13 on .NET 9 throughout. Both agents ship self-contained: the panelist installs no
runtime. The Windows project sets `EnableWindowsTargeting`, so all four packages build
from a Mac — running them still needs the real OS.

**`macos/` is tracked, as of 2026-10-06.** It was held back while Windows went first; that
hold is over and `main` carries all three — Core, Windows and macOS. Only `macos/dist/` is
still ignored. Any older note calling macOS untracked is stale, not a rule.

---

## Commands

```bash
./build.sh              # 4 zips in the repo root, verified with unzip -t
./build.sh windows      # win-x64 + win-arm64
./build.sh mac          # macos-arm64 + macos-x64 (needs a Mac: codesign, iconutil)
```

The checks, which need neither a household nor a network:

```bash
dotnet run --project tools/regression-tests     # .env precedence, the assistant's steps, the outbox
dotnet run --project tools/chrome-setup-tests   # the Chrome DNS policy's undo, the proxy probe
dotnet run --project tools/wizard-tests         # the guided wizard's gates, both platforms
```

They print a `PASS` line each and throw on the first failure, so the exit code is the verdict.
`chrome-setup-tests` touches no real Chrome preference and no real network setting, and says so
on its last line; `regression-tests` redirects `AgentPaths` with `PERSONICORE_TEST_ROOT`.

Run and watch on macOS:

```bash
open "macos/dist/arm64/Personicore Device Agent.app"
tail -f ~/Library/Logs/Personicore/agent.log
pkill -f "Personicore Device Agent"
```

Where the agent keeps things:

| | macOS | Windows |
|---|---|---|
| State, per-user config, `.env` | `~/Library/Application Support/Personicore/DeviceAgent` | `%LOCALAPPDATA%\Personicore\DeviceAgent` |
| Log | `~/Library/Logs/Personicore/agent.log` | `…\DeviceAgent\logs` |
| What would be sent | `~/Desktop/Personicore-outbox.txt` | `…\Desktop\Personicore-outbox.txt` |
| Device id | `.personicore-device-id`, several slots (see below) | same |

---

## Rules that are easy to break

**All text the panelist reads lives in `AgentText` (Core).** Never put a user-facing
string in a UI file. What genuinely differs between platforms is declared in
`PlatformWords` — "this PC"/"this Mac", the settings path, the startup item's name. If a
sentence needs to differ and is not in `PlatformWords`, add a field there rather than
writing it twice; two copies of a sentence always end up as two different sentences.

**The two agents are the same product.** Same menu, same order, same wording, same
dialogs. A change to one is a change to both, and the shared parts belong in the Core.

**`Offline` and `DryRun` are not the same thing, and conflating them is a real bug that
has happened once.**

- `Offline` = *no mutation is sent* — the household query still runs over HTTP — and the
  requests that would have changed anything go to the desktop file. This is the
  shipped default (`sendToBackend: false`) while the device/IP contract is rewritten.
- `DryRun` = `PERSONICORE_DRY_RUN` in a `.env`: a developer exercising the agent **away
  from a household**. It is the only mode allowed to carry on with no meter node, no
  credential and a hand-typed owner.

Gating registration, the no-node lock or any "pretend there is a household" path on
`Offline` re-opens that bug: on a panelist's computer with no node, the agent invented a
local session and popped the owner dialog on a network that has no router. Use `DryRun`
for permissive paths and `Offline` only for "do not send".

**No meter node, no actions.** `AgentSnapshot.ActionsUnlocked` is the rule: until a node
answers, the menu offers only *View details…* (which opens the log folder) and *Refresh
now*. Registering, the proxy and the startup item are greyed out. Turning the proxy
**off** is always allowed — never leave a panelist with a switch they cannot undo.

**`state.json` has two writers, and each owns half of it.** The proxy watchdog is a
separate process — a scheduled task on Windows, a LaunchDaemon on macOS — and it changes
the switch while the agent runs. So `AgentState.Save()` writes everything *except* the
proxy fields, which it takes from the file, and `SaveProxy()` writes only those, onto
whatever the file holds. Anything touching `Proxy*` saves with `SaveProxy()` and reads
with `ReloadProxy()` before judging a setting it does not recognise; everything else uses
`Save()`. A plain `Save()` after changing the switch silently reverts it.

**The device id carries the node that issued it, and that is what makes `Forget()` safe.**
Each slot holds `<id> <issuer MAC>`; a slot with the id alone is one written before this
existed and reads as "issuer unknown". `DeviceIdRecord.BelongsToNode` answers the only
question asked of it, and it answers **yes whenever either side is unknown** — an id from an
earlier version is overwhelmingly a computer sitting at home where it was registered, and
failing closed there would stop every existing installation reporting. The first write the
backend accepts records the node (`RememberIssuer`), which is the whole of the migration: no
computer can know after the fact which household issued its id.

Two consequences in `SendDeviceAsync`, and neither is cosmetic:

- **Nothing is sent on a node that did not issue the id** (unless `allowNodeChange`). What the
  backend does with a `deviceId` under another `routerId` is unverified, and two of the three
  possible answers lose something.
- **`The device doesn't exist!` is authoritative only on the issuing node.** Asked anywhere
  else it is what a household-scoped lookup says about somebody else's id, and acting on it
  lets the id go and creates a row — so a computer moved between two metered homes grows one
  orphaned Device per return trip, which is exactly what this store exists to prevent. That
  path is only reachable with `allowNodeChange` on, which is why the switch cannot cost an
  identifier.

Both refusals go through `Refuse`, which honours `addressAlreadySent` exactly as `Write`
does: with the address and the owners already in the panel it is a `DeviceRecordPending`, not
a failed registration.

**The device id is written once and never replaced.** `DeviceIdStore` keeps it outside
`state.json`, in several slots, read-only and hidden, written to a temp file, flushed to
disk and renamed over the target so a power cut cannot leave half an identifier — which
would still look valid and be believed. A second, different id from the backend is
logged and ignored.

**Optional fields degrade, they do not fail.** The agent sends more about a computer than
the deployed `IPInput` accepts (`deviceOs`, `isPersonal`, `principalPanelistId`).
`PersonicoreApi` learns from the schema's own error message which field it does not know,
drops that one and retries. Add a new optional field to `OptionalFields`, never straight
into the entry, or one unknown field kills the whole registration.

**The proxy only ever goes through the node this computer is registered on.** Every meter
node resolves `router.example.invalid` for itself through its own dnsmasq, and
households use the same private ranges, so a laptop carried into another panel household
finds *a* node at the very same address. Proxying through it would file this computer's
traffic under that home. `AgentState.IsRegisteredOnNode` is the test, and what it compares
against is **`ExpectedNodeMac`, not `RouterMac`**: every probe asks `router_info.sh`, so the
remembered value has to come from there too (`state.nodeMac`, written with the registration).
`RouterMac` is the *backend's* echo of the router record and is only the fallback for a
registration written before `nodeMac` existed. The two agree in every case anyone has seen —
the household is looked up by that MAC — but comparing across the two sources means a backend
that echoed it in a way `NormalizeMac` cannot undo would turn the proxy off **at home** and
refuse the device write there, silently. Not worth depending on an agreement nothing enforces.
The check is applied at every level that can act without the others:

- the cycle (`IsForeignNode` → `Sync(null, ProxyPause.OtherHousehold)`),
- the agent's own proxy watchdog (the expected MAC goes to `NodeProbe`, so a foreign node is
  not "reachable" and is never followed),
- the Windows scheduled task (`state.RouterMac`),
- the macOS root helper, which is told the MAC with every `APPLY`/`REPOINT`, keeps it in
  `router-mac.txt` and holds its own watchdog to it. Written **before** the helper's own probe,
  so a request naming another MAC can only narrow what root will do.

Two things to leave alone. **An empty `RouterMac` accepts any node** — a computer that has
never been registered has nothing to be foreign to, and the proxy has to work during the first
run. And **an unreadable MAC counts as ours**: the node is only reached at all after
`router_info.sh` returned a valid one, and failing closed on a parse would turn the proxy off
at home. The switch stays *on* throughout; what changes is the pause reason, and
`ProxyPause.OtherHousehold` exists so the panelist is told why instead of reading "no meter
node on this network" on a network that plainly has one.

**The owner prompt is remembered per household *and* computer.** `_promptedFor` is
`PromptKey(routerMac, machineId)`. This computer's MAC does not change when it is carried to
another home, so a key made of the machineId alone said "already asked" in the one situation
where the question has to be asked again: `HasOwnerFor` correctly stopped believing the saved
owner, the cycle published `OwnerRequired`, and the dialog never opened — the panelist was
left looking at *Owner not set* with nothing to click. `RevalidateAsync` clearing it by hand
covered only the case where the new node answers at the old one's address, not the ordinary
one where the old node is gone and the new one is discovered.

**A device the panel holds against another router says so.** `presenceReportDeviceIp` cannot
move a Network Device between routers; it answers *The Network Device is not registered on
this router*, which is `DeviceOnAnotherNodeException` — a `BackendNotReadyException`, so the
loop's backoff is unchanged, with its own detail line (`AgentText.DeviceOnAnotherNode`). Raise
it **only** on that answer from the backend. The agent can see locally that the node is not
the one it registered on, and deliberately says nothing about moving house then: a node record
support recreated looks identical from here, and that is the "a headline must not describe a
cause it is guessing at" trap with a new face.

**The certificate item reports before it offers.** `AgentSnapshot.CertificateTrusted` says
whether the OS already trusts Personicore's CA, and `AgentText.CertificateItem` is the only
place that decides what the menu reads: *HTTPS certificate: installed* when it does,
*Install HTTPS certificate…* when it does not. Clicking it while trusted shows the status and
returns — running the installer would ask for an administrator password to do nothing.

Two things about it. **The check is not free** — on macOS it spawns `/usr/bin/security
verify-cert` and waits, on Windows it builds an X509 chain — so it is evaluated once per cycle
in the Core and carried on the snapshot, never asked for while a menu is drawn. And
`RefreshCertificateTrust()` is public because the UI installs it: without calling that right
after, the menu would go on offering the install until the next cycle, to someone who had just
done it. The trust check itself is per-platform and arrives as a `Func<bool>`.

**Reporting is driven by change, not by a clock, and there are three loops.** The cycle
(`pollIntervalSeconds`, hourly) revalidates everything; the proxy watchdog
(`proxyWatchIntervalSeconds`, 30 s) follows the node's address; and the address watcher
(`ipWatchIntervalSeconds`, 30 s) watches *this computer's* address. All three start and
stop together in `Start()` / `Dispose()`.

`reportIntervalSeconds` is **0, meaning no periodic report at all**. It used to be twelve
hours, which bought nothing — two identical reports a day from a computer that had held the
same lease all week — while a genuinely new address still waited up to an hour to be
noticed. Now the cycle speaks only when the fingerprint of what would be sent changed, and
the address is watched directly so that change is seen within seconds.

Three things about the watcher that are easy to undo by accident:

- **It compares against `_lastSeenIp`, not `_state.LastIp`.** `LastIp` is the last address
  *reported*; comparing against it would wake the cycle every 30 s for as long as reporting
  is failing, which is precisely the backoff `BackendNotReadyException` exists to give. It
  wakes once per change.
- **It does no I/O.** `ObserveLocalIp` resolves the route with a connected UDP socket, which
  sends no packet — the stack just assigns a local address. Measured at 3 ms, which is what
  makes a 30 s loop reasonable. Put a request to the node or the backend in there and that
  stops being true.
- **It never reports anything itself.** It calls `WakeUp()`, and the cycle decides. The
  address is part of the fingerprint, so a real change produces a report and anything else
  stays quiet.

It exists *as well as* `NetworkChange.NetworkAddressChanged`, which both agents already
subscribe to, because that event is reliable on Windows and not always on macOS: a DHCP
renewal onto a different address does not always surface as an interface event, and the
hourly cycle was then the only backstop. Watching the address directly does not depend on
the OS telling us.

**`presenceCreateDevice` has no `ip` field, so in `device` mode none of this reaches the
backend.** The address only travels via `routerIpsAssigment`, which means
`deviceAndRouterIps` (or `routerIps`). Making the watcher faster does not change that; it is
a contract question, not a cadence one — see §6.3 of the contract.

Do not add "force a report" back to menu items: sending on demand is the owner dialog's
job, where the panelist sees what is being sent.

**Where the credential comes from is a packaging decision, not a runtime one.** The
panelist never creates a `.env` and is never asked to sign in. Two ways in:

- **Baked in.** `build/write-packaged-env.sh` writes the credential while the package is
  built, taking the token from `PERSONICORE_PACKAGED_TOKEN` or from `packaging.env` in the
  repo root (gitignored, never committed). Where it puts it differs, and the difference is
  only how each platform hides a file from the panelist:
  - **Windows: compiled into the `.exe`.** `publish.sh` writes
    `core/…/Config/packaged.env` immediately before `dotnet publish`, the `.csproj` embeds
    it as the resource `personicore-packaged.env` when it exists, and a `trap` deletes it
    immediately after — including when the publish fails, or the next build would embed a
    token nobody meant to ship. The package is **one file plus INSTALL.txt**: there is no
    `.env` for the panelist to lose, to leave behind when they copy the `.exe` somewhere
    else, or to edit.
  - **macOS: inside the signed bundle** at `Contents/MacOS/.env`, and it **must be written
    before `codesign`** — the signature seals the bundle, so a file added afterwards
    invalidates it and the Mac kills the app.
- **From the node.** With nothing baked in, the agent uses the `jwt` and `graphql` that
  `get_config.sh` serves, like the router's own web page. The build says which of the two
  happened, on stderr, including the endpoint — shipping panelists a build aimed at stage
  is the easy mistake.

Four things about the baked-in token that are easy to get wrong:

- **It is not a secret.** It used to be `unzip -p <package> .env`; compiled in, `strings`
  on the extracted assembly still finds it. Hiding it from the panelist is a usability
  decision, not a security one. It has to be a token the backend can scope and revoke,
  never a person's.
- **It wins over the `.env` in the user's folder.** `EnvSettings.CandidatePaths()` is the
  order and the baked-in credential sits exactly where the packaged `.env` used to, right
  after the executable's own directory: a `.env` left in Application Support or
  `%LOCALAPPDATA%` for testing cannot point a panelist's build somewhere else. The two
  escape hatches are a real environment variable and a `.env` **next to the executable**,
  which is also what keeps the macOS bundle working unchanged. Two regression checks hold
  that order down.
- **Both build outputs are reused between builds** — the Windows publish directory and the
  Core source tree — so a build with no credential would otherwise ship the previous one's.
  The helper deletes a stale file in both places, and `publish.sh` also deletes any `.env`
  an older build left in `dist/win-*`, because that file would still beat the compiled-in
  one. macOS is safe only because `make-app.sh` does `rm -rf dist`.
- **`strings` on the `.exe` finds nothing**, and that is not a failed build:
  `EnableCompressionInSingleFile` compresses the bundled assemblies. Check
  `core/…/obj/Release/net9.0/Personicore.DeviceAgent.Core.dll` instead, which is the
  assembly the resource is compiled into.

**The macOS bundle is signed with one `--deep` pass, and that is not laziness.** The .NET
apphost requires its assemblies, `deps.json` and `runtimeconfig.json` in its own directory, so
all 218 files sit in `Contents/MacOS` and codesign treats every one as nested code — managed
`.dll`s and `runtimeconfig.json` included. Signing inner-to-outer, the normally-correct order,
fails with *"code object is not signed at all / In subcomponent: …"*. Apple's warning about
`--deep` concerns nested **bundles** inheriting the outer entitlements, and there are none here.
Two corollaries: the main executable must never be signed on its own first, because codesign
resolves that path to the enclosing bundle and signs *that* instead; and `sign-app.sh` checks
afterwards that all 19 Mach-O binaries carry the hardened runtime, because the notary service
answers that by email twenty minutes later. `macos/build/agent.entitlements` grants
`allow-jit`, `allow-unsigned-executable-memory` and `disable-library-validation` because
hardened-runtime CoreCLR does not start without them — which is also the reason the agent can
never be a Mac App Store app even stripped of the root helper: App Review does not grant
unsigned executable memory to a sandboxed app.

**Signing degrades the way the credential does.** No Developer ID certificate → ad hoc, runs on
the building Mac only. Certificate → hardened runtime. Certificate plus
`PERSONICORE_NOTARY_PROFILE` → notarized and stapled, which is the only build a panelist opens
without *Open Anyway*. The build says which on stderr, next to the endpoint it baked in. Two
certificates installed is a refusal, not a guess.

**The setup assistant is a projection, like every other screen.** `SetupPlan` (Core) owns the
three steps — panelists, HTTPS certificate, proxy — and both windows render it: `SetupForm`
(WinForms) and `SetupWindow` (Avalonia) hold no state and decide nothing. Four things about it
that are one edit away from breaking:

- **A step's done-ness is read off the snapshot, never remembered.** Owners from
  `OwnerNames`, the certificate from `CertificateTrusted`, the proxy from `ProxyWanted` — so a
  panelist who installed the certificate from the menu last month is not asked again, and one
  who turned the proxy off afterwards genuinely has not got a proxy on. Caching "step 2 done"
  anywhere re-introduces the administrator prompt that does nothing.
- **`Opening()` is not `FirstPending()`, and the difference is the whole first run.** Until
  `SetupCompleted` is recorded the assistant opens at step one even when step one is green:
  it is a walkthrough before it is anything else, and a panelist who registered from the owner
  dialog the agent opens by itself would otherwise land on "Trust Personicore's HTTPS
  certificate" never knowing three steps exist. This was a real report — *"se salta el paso 1"*
  — and it was the assistant working as written. After Finish it opens on what is outstanding,
  because then it is a repair and not a tour.
- **A done step is not a closed one.** The button follows `ActionLabel`, never the tick: step
  one becomes *Change owner…* because who uses a computer changes, and step three becomes
  *Turn off the proxy* because a switch must be undoable from the screen that threw it. The
  certificate is the single exception — re-installing a trusted CA spends an administrator
  password on nothing, which is the same reason `CertificateItem` reports instead of offering.
- **The steps call what the menu calls**, through `ShowOwnerDialog`, `InstallCertificateAsync`
  and `SetProxy`. There is deliberately no second path into any of them: the guards, the
  notifications and the error handling exist once. `ToggleProxy` is now `SetProxy(!wanted)` for
  that reason.
- **`SetupPlan` obeys `ActionsUnlocked`.** With no meter node all three steps are locked and the
  window says why. The certificate is locked too although installing it would work: it costs an
  administrator password, and spending one on a network where the setup cannot continue is a
  prompt for nothing. The one exception is the menu's own: **turning the proxy off is always
  available**, node or no node, because leaving a panelist with a switch they cannot undo is
  worse than the lock it enforces.
- **`SetupCompletedAt` is written by Finish, not by closing the window**, and it is saved with
  `Save()` — it is not a proxy field, so `SaveProxy()` would be the wrong half. Closing with the
  X leaves it unset and the assistant opens again next start, which is the contract the owner
  dialog already has. The one exception: closing when all three steps are done records it, or
  three green steps would be re-offered for ever. `Clear()` does not touch it.

While the assistant is open, `OwnerRequired` activates it instead of opening the owner dialog:
the first step is already asking that question, and two windows asking it is how a panelist ends
up answering twice.

**What the OS trusts, Safari inherits; Chrome needs two more things; Firefox is not handled.**
The CA lands in the macOS System keychain (`CertificateInstaller`) or in `LocalMachine\Root` on
Windows, and the proxy is written with `networksetup` or WinINet's per-connection options. Safari
and Edge read both and need nothing extra. Chrome reads both as well — including locally added
roots, despite its own root store — but on macOS two things sit outside them, which is the whole
reason `ChromeSetupWindow` exists:

- **Local Network permission is per application** (macOS 15+). Allowing Personicore does not
  allow Chrome, and without it Chrome cannot reach a proxy on a LAN address. The agent cannot
  grant it; it can only open Privacy & Security and say so (`AgentText.ChromePermission`).
- **Secure DNS**, turned off through Chrome's own preference domain — `DnsOverHttpsMode` in
  `com.google.Chrome` via CFPreferences, never by editing Chrome's live JSON, which leaves
  `cfprefsd` incoherent. `ChromeDnsPolicy` only ever **creates an absent** preference and writes
  a receipt before it does, so undo removes our value and never an administrator's; a forced or
  pre-existing policy is left alone and reported rather than replaced.

Two things follow. The window hangs off `ProxyWindow` and deliberately **not** off `SetupPlan`:
it is optional and Mac-only, so the three steps stay identical on both platforms, and what
Windows lacks here is a macOS restriction rather than a missing screen. And **Firefox is
deliberately not handled by the agent** — it keeps its own NSS trust store, so the installed CA
does not reach it unless `security.enterprise_roots.enabled` is on. It inherits the proxy and
nothing else.

**What the agent does not do, the panelist is told to do.** Since 2026-10-06 both quick guides
carry two closing pages, built by [`docs/manual/`](docs/manual/README.md). The first is the
setup assistant itself — `SetupPlan`'s three steps, quoted from `AgentText.Setup`, because the
window opens by itself and the guides only mentioned it inside a tip. (**Not** the five-step
wizard in `Wizard/`: that one only runs with `--wizard`.) The second is the two browsers the OS
settings do not cover:

- **Firefox**, on both platforms: tick *Allow Firefox to automatically trust third-party root
  certificates you install* (the Settings face of `security.enterprise_roots.enabled`) and set
  *DNS over HTTPS* to **Off** or **Default Protection**. On macOS, Step 3 now names Firefox too:
  Local Network permission is per application, so allowing Personicore does not allow Firefox.
- **Safari**, macOS only: it reads the OS certificate and proxy and needs nothing — unless the
  panelist has iCloud+. **Private Relay** tunnels Safari through Apple's relays, and the node's
  bypass list (firmware repo, not this one) lets `.icloud.com` past the proxy, so that traffic
  leaves spliced and nothing is measured. *Limit IP address tracking* on the Wi-Fi is the same
  mechanism in a second place.

Still check both Firefox settings against the version a panelist actually has: they were written
against the Settings UI, and they are the only part of the guides that no automated check covers.

**Comments and documentation are in English**, including in files that were Spanish
before. Match the surrounding density: explain *why*, not *what*.

---

## Traps

- **An id issued before 2026-10-06 has no issuer until the next accepted write.** Until then
  it behaves exactly as it always did, including the `Forget()` path. Nothing can be done
  about that: the computer has no record of which household issued it.
- **`AgentState` is read once at start-up.** Editing `state.json` while the agent runs
  changes nothing and will be overwritten. Restart after editing it.
- **`macos/build/make-app.sh` does `rm -rf dist`.** Do not run it while the app is
  running from `dist`, or the running process loses its bundle.
- **The machine-wide device-id slot is root-owned on macOS** and will never be writable
  by the agent. That is expected; the failure is cached per process so it is attempted
  once, not every cycle.
- **`pkill` skips the clean exit**, which is what restores the panelist's proxy. Quit from
  the menu when the proxy is on, or let the per-minute watchdog notice.
- **`open` does not start a second instance**; it fronts the running one.
- **The owner dialog opens itself** through the worker's `OwnerRequired` event. If it
  appears when it should not, the bug is in the cycle, not in the UI.
- **`Publish()` stamps `Offline`, `DryRun` and the proxy state** on every snapshot, so a
  publish cannot leave the menu reading a stale mode.

---

## Testing without production

**`sendToBackend` defaults to true now**, so an agent started with no `config.json` writes
for real to whatever endpoint it resolves — the baked-in `.env`, or the one the node
advertises, which in a real household is production. Put `{ "sendToBackend": false }` in a
`config.json` next to `state.json` before running it anywhere you have not checked, or
point it at the stub below.

The states that depend on what the panel answers are reachable with the stub:

```bash
SCENARIO=no-panelists python3 tools/fake-graphql.py
```

and a `.env` next to `state.json`:

```
PERSONICORE_GRAPHQL_ENDPOINT=http://127.0.0.1:8099/
PERSONICORE_GRAPHQL_TOKEN=test-token
```

That file only works against a build with **no** baked-in credential — a `dotnet run`, or a
package built with no `packaging.env`. A shipped package carries its credential inside the
executable and ignores it, which is the point. To redirect one of those, put the `.env`
next to the executable (inside `Contents/MacOS/` on macOS, which breaks the signature) or
set `PERSONICORE_GRAPHQL_ENDPOINT` in the environment.

Scenarios: `healthy`, `no-router`, `no-panelists`, `empty-router-id`, `router-token`,
`wrong-household`, `moved-household`. `router-token` and `wrong-household` refuse
`presenceCreateDevice` the way the real resolver will when handed the router's JWT;
`moved-household` lets the device write through and refuses the *address report* with
*The Network Device is not registered on this router*, which is the answer one computer gets
in the second metered home it is carried to (see §7 of the contract). The stub answers both
mutations for real, remembering request ids, so create / retry / update are all exercisable.

To reach *No meter node on this network* without changing networks, a `config.json` next
to `state.json`:

```json
{ "routerHost": "nonexistent-node.invalid", "subnetSweepEnabled": false, "logLevel": "debug" }
```

To see the cadence without waiting twelve hours, set `reportIntervalSeconds` low and
`pollIntervalSeconds` to 60 in the same file.

Clean up afterwards: remove `.env` and `config.json`, restore `state.json`, kill the stub.

---

## Where things stand

*2026-10-06. **`main` carries both platforms.** `feature/HP-1988-macos`,
`feature/HP-1988-windows` and `dev` were all the same commit — `09cdea2` — and `main` was its
ancestor, so bringing them together was a fast-forward of 11 commits: no merge commit, nothing to
resolve, and `git branch --no-merged main` now empty. All five projects build in Release from a
clean checkout of `main`, 0 warnings, and `regression-tests` and `chrome-setup-tests` pass there.
Two things are deliberately not done: **`origin/main` is still `b0e8a50`**, so the merge is local
only, and the **guided wizard is uncommitted** — `Wizard/` on both platforms, `WizardApp.cs` and
`tools/wizard-tests`, four modified files alongside. It compiles and its tests pass; it is simply
not in any branch yet.*

*2026-10-02. **Version `0.6.0`: the setup assistant.** The three first-run steps — panelists,
HTTPS certificate, proxy — are a window on both platforms, opened by itself on a fresh install
and from `Setup assistant…` in the menu afterwards. `SetupPlan` in the Core owns the steps and
both UIs render it; see the rule above. Compiles and packages on all four targets; the two
windows have not been looked at yet on either OS.*

*2026-10-01. **It works end to end against stage.** Commit `6547c6a` carries the first half of
this session; **17 files are still uncommitted** — the certificate status, all of
`presenceReportDeviceIp`, the update-echo fix and these notes. `macos/` stays untracked apart
from its README. Version `0.5.0` in both `.csproj`.*

### The one-line summary

HP-1844 deployed and the whole chain runs: household → `presenceCreateDevice` → device id
stored on disk → `presenceReportDeviceIp`. The packaged `routers` APP token is accepted,
because with `routerId` sent the resolver takes the household from the router and never asks
whether the token belongs to a panelist. Default mode is `deviceAndIp`.

```
Household read: routerId=6aad9929… panelists=2
Device created: deviceId=6abedf923121520012853c6b
Device id stored: 6abedf923121520012853c6b
Address reported: ip=192.168.0.116 reportId=38739bf8…
```

The id this Mac currently holds is `6abedf923121520012853c6b`.

### A deleted Device is recovered from, not sat on

`DeviceIdStore` never replaces a stored id — that is what stops a household growing two rows
for one computer. It has exactly one escape hatch, `Forget()`, and one trigger:
`The device doesn't exist!` from an update. That answer is authoritative, not transient — the
panel looked under that id in this household and found nothing — so keeping the id means
sending a dead reference for ever while every update fails. Re-creating cannot duplicate what
does not exist.

`SendDeviceAsync` recovers **in the same cycle**: logs it, clears every slot, and writes again
as a create. On the manual path the panelist is waiting, and failing there would have them click
Register to watch the same thing happen. Clearing the id also changes the write's fingerprint,
so `RequestIdFor` mints a fresh key by itself — the old one belongs to the deleted row, and
`Device.requestId` has a global unique index.

Verified end to end against stage after support deleted the row: dead id detected, 2 slots
cleared, new device created, new id stored, address reported, and the next cycle a clean update.
Do **not** widen that trigger. A refused credential, a missing mutation or a router mismatch are
all answers that change without the id changing.

**Why the row was deleted, which is worth not repeating:** testing left two Network Devices for
this Mac (a create sent by hand, then the agent minting its own key because its fingerprint had
moved), both were cleaned up in the panel, and the agent was then holding a dead id. **Do not
hand-send creates while the agent runs.** To check a schema use `tools/check-device-mutation.py`,
which only introspects.

### The two mutations, and the two ids that are not the same thing

| | `presenceCreateDevice` | `presenceReportDeviceIp` |
|---|---|---|
| Idempotency key | `requestId` | `reportId` |
| Minted per | intended write (inventory or owners changed) | **observation** — one per distinct address |
| Where it lives | `state.json` `deviceRequestId`, written before sending | `state.json` `ipReportId`, same |
| `deviceId` | omitted on create, sent on update | sent, or omitted to look up by `machineId` |

**`deviceId` is obtained once and sent for ever.** `DeviceIdStore` keeps it in three slots —
`AgentPaths.UserData`, `AgentPaths.MachineData` (root-owned on macOS, fails, expected) and
`$HOME` — hidden and read-only, write-once, any surviving copy healing the others. A second,
different id from the backend is logged and ignored.

**The report id must change when the address changes**, and this is the one that is easy to get
backwards. Reusing it makes the backend treat the call as a retry of the first observation and
return the original period: *the address silently stops updating*. Measured — sending
`10.0.0.5` under a used report id came back `192.168.0.200`. So: new address → new id → the
previous Local IP Period closes and a new one opens at the same instant (they abut); same
observation resent after a timeout → same id → the original period, no duplicate row.

`AgentState.IpReportIdFor` and `RequestIdFor` are deliberately two methods, not one helper: a
change of inventory must not mint a new address observation, nor a change of address a new
device write.

### Two things measured against stage that the documents do not say

**An update echoes the create's `requestId`, not the update's.** A fresh key sent with a
`deviceId` came back with the key from the create. So the echo is enforced **only on create**,
where nothing else ties the answer to the request; on update the guarantee is the `deviceId`,
checked separately. Comparing them on update failed every update — that was a real bug, found
by running it, not by reading.

**`routerIpsAssigment` refuses three fields the agent offers.** `IPInput` accepts eight:
`ip`, `friendlyName`, `machineId`, `deviceType`, `deviceBrand`, `deviceModel`, `deviceShared`,
`panelistIds`. `deviceOs`, `isPersonal` and `principalPanelistId` are refused one at a time,
learned, and dropped — `dropped=[…]` in the log is that machinery working. Only relevant in
`deviceAndRouterIps`; `presenceCreateDevice` accepts all three.

### Registration modes

| Value | Effect |
|---|---|
| `deviceAndIp` *(default)* | `presenceCreateDevice`, then `presenceReportDeviceIp`. The order is the contract's: the IP report never creates a subject. |
| `device` | the device alone, no address. For isolating the device write while testing. |
| `deviceAndRouterIps` | device plus the older `routerIpsAssigment`. Only for a backend without `presenceReportDeviceIp`. |
| `routerIps` | `routerIpsAssigment` alone — what the router's web page does. No device id. |
| `personalDevice`, `both` | superseded; kept because some households were registered with them. |

A `presenceCreateDevice` failure is **not** swallowed the way `both` swallows a
`personalDevice` one: there the assignment had already recorded the data, here the device write
*is* the record.

### Sending is on, and aimed wherever the package was built to aim

`sendToBackend` defaults to **true**. The target is fixed when the package is built, by the
credential baked in (see the credential rule above), and `./build.sh` prints that endpoint on
stderr.

**The packages in `windows/dist` and `macos/dist` now aim at PRODUCTION**
(`https://graphql.example.invalid/`), because that is what `packaging.env` holds — it said
stage until 2026-10-01. `./build.sh` prints the endpoint it baked in on stderr, and the agent logs it on every
start (`GraphQL endpoint: … · source=baked into the executable`): check one of the two before
handing a build to anybody, rather than trusting this paragraph. There is no longer a `.env`
in the Windows package to read it out of — `unzip -l` on one should show the `.exe` and
`INSTALL.txt`, nothing else. With no `packaging.env` at all the
agent falls back to the node's own endpoint and jwt, and note that the node's JWT
(`r0u73rshyph4`, `type: APP`) is a *production* token that stage rejects outright.

Before pointing a build at production, set `sendToBackend` to false in a `config.json` and read
`Personicore-outbox.txt`: the same builders produce the dump and the real request.

### Permanent constraints of the contract

- **`presenceCreateDevice` has no `ip` field** and is not getting one; the address belongs to
  `presenceReportDeviceIp`. Switching the default to `device` silently stops reporting it.
- **Idempotent on the client's key, never on `machineId`.** `Device.requestId` and
  `DeviceIpReportReceipt.reportId` both carry global unique indexes. Two creates under
  different keys make two rows and nothing on either side notices — as this session proved.
- **`deviceOs` is honoured on create and ignored on update.** A panelist who upgrades their OS
  keeps the version recorded at first registration, for ever. Do not "fix" it by deleting and
  recreating the device: that is a second row.
- **A Network Device cannot change router through an address report.** If support recreates the
  node record, the registration needs fixing, not a retry. Mapped to `BackendNotReadyException`.
- **Only private IPv4** reaches the address report. `LocalNetwork.IsPrivateIpv4` enforces the
  backend's own strictness — four decimal octets, no leading zeros, no colon, and only 10/8,
  172.16/12, 192.168/16, 169.254/16. `IPAddress.TryParse` is not enough: it accepts `1.2.3` and
  `192.168.01.1`, which the resolver does not. All thirteen cases from §6 are checked.

### Four traps this session created and then closed

Each is a one-line change away from coming back.

**A headline must not describe a cause it is guessing at.** `BackendNotReadyException` first
reused `HouseholdNotAssigned`, whose headline is "Household not set up in the panel yet" —
announced while the household read back perfectly. Then `BackendNotReady` was published for a
refused device write even when `routerIpsAssigment` had just succeeded. Hence
`DeviceRecordPendingException`, raised only when `addressAlreadySent`; both send paths catch it
ahead of the generic handler and publish `Registered` with `AgentText.DeviceRecordPending`.

**A failed device write must not discard the owner.** `RememberRegistration` is split:
`ApplyRegistration` saves who the computer belongs to *and its profile corrections*, and only
`RememberRegistration` advances `LastRegisteredAt` and `LastSentSignature`. The manual path used
to save nothing unless the whole send succeeded, so a refused device write threw away the
panelist's answer **and** their device type, brand, model and name — and the dialog reopened
asking again, for ever. `RememberOwner` keeps the choice and leaves the fingerprint alone.

**The error message must not name a credential it is only assuming.** It said "the agent sends
the router's JWT from get_config.sh", which stopped being true the moment a token was baked into
the package.

**The echo check must not fire on the update path** — see above.

### Reporting cadence and the address watcher

Three loops, started and stopped together in `Start()` / `Dispose()`: the cycle
(`pollIntervalSeconds`, hourly), the proxy watchdog (`proxyWatchIntervalSeconds`, 30 s) and the
address watcher (`ipWatchIntervalSeconds`, 30 s).

`reportIntervalSeconds` is **0 — no periodic report at all.** It was twelve hours, which bought
nothing: two identical reports a day from a computer that had held the same lease all week,
while a genuinely new address still waited up to an hour to be noticed.

Three things about the watcher that are easy to undo:

- **It compares against `_lastSeenIp`, not `_state.LastIp`.** `LastIp` is the last address
  *reported*; comparing against it would wake the cycle every 30 s for as long as reporting is
  failing — exactly what `BackendNotReadyException`'s backoff exists to prevent.
- **It does no I/O.** `ObserveLocalIp` resolves the route with a connected UDP socket, which
  sends no packet. Measured at 3 ms, which is what makes a 30 s loop reasonable.
- **It never reports anything itself.** It calls `WakeUp()`; the cycle decides.

It exists *as well as* `NetworkChange.NetworkAddressChanged`, which both agents subscribe to,
because that event is reliable on Windows and not always on macOS.

**Still untested: the firing path on a real lease change.** Forcing one needs `sudo` and would
disturb the machine's live network. Three lines are unexercised: observed-differs → `WakeUp()`.
The one-minute test: set `ipWatchIntervalSeconds` to 5, join a phone hotspot, come back, look
for `This computer's address changed: … → …. Reporting now.`

### The certificate item reports before it offers

`AgentSnapshot.CertificateTrusted` says whether the OS already trusts Personicore's CA, and
`AgentText.CertificateItem` is the only place that decides what the menu reads: *HTTPS
certificate: installed* or *Install HTTPS certificate…*. Clicking it while trusted shows the
status and returns — running the installer would ask for an administrator password to do
nothing. The check costs a process on macOS, so it is evaluated once per cycle in the Core and
carried on the snapshot, never asked for while a menu is drawn; `RefreshCertificateTrust()` is
public because the UI installs it and the menu must not go on offering what was just done.

### Classifying a backend answer

`PersonicoreApi.Describe` and `DescribeIpReport` map refusals no retry can fix onto
`BackendNotReadyException`, which the loop waits out on the poll interval and logs once.
Everything else stays an ordinary failure that retries in a minute.

The trap: `GraphQlClient` prefixes every message with the operation name, so matching an
operation name unquoted matched *every* error from that call, and `Cannot query field
"acceptedAt"` — real contract drift, worth seeing now — was filed as "not deployed yet". The
quoted-symbol check separates the two. Eight cases are checked; add to that set rather than
loosening the markers.

### What the panelist sees

| Situation | Status | Headline |
|---|---|---|
| no node answers | `RouterNotFound` | No meter node on this network |
| router or household not in the panel | `HouseholdNotAssigned` | Household not set up in the panel yet |
| refused **and nothing landed** | `BackendNotReady` | Personicore is not ready for this computer yet |
| address and owners landed, Device row did not | `Registered` | Registered · <owners> · Personicore has still to finish this computer's own record |
| sending held, body on the desktop | `Simulated` | Simulated · <owners> |
| registered | `Registered` | Registered · <owners> |

### Tools

```bash
python3 tools/check-device-mutation.py      # both mutations: deployed? does the shape still match?
```

No arguments: reads the `.env` the last build baked into `windows/dist/win-x64`, so it checks
the endpoint the packages will actually talk to. It reports the contract revision (`routerId:
ID` plus `presenceReportDeviceIp` present means HP-1844), then compares both inputs against what
the agent sends, **both ways**. Exit 0 both match, 3 not deployed, 4 deployed but different.

It shells out to `curl` because Python's own CA bundle rejects this endpoint's valid Google
Trust Services chain over a cross-signed root, and `curl` trusts the OS store as .NET does.
Every call carries a unique operation name and a `cb=` parameter so "you are reading a cached
schema" is answerable rather than arguable.

`tools/device-mutation-probe.graphql` is the same check as a document to paste into a GraphQL
client. `tools/fake-graphql.py` answers both mutations for real, remembering request ids and
report ids and keeping one open period per (router, subject), so create / retry / update and
first / no-op / abut / retry are all exercisable offline; scenarios `router-token` and
`wrong-household` reproduce the credential refusals.

### Older, still true

- **`ProxyForm` on Windows has never been rendered.** Written on a Mac, compiles, mirrors the
  macOS window section for section.
- **"Windows service" vs tray app** is an open product question: a service runs in session 0 and
  can neither draw a tray icon nor ask the panelist anything, which are two of this ticket's own
  criteria.

### Moving house, which is now three separate behaviours

2026-10-06. The four first-run / house-move journeys are written out in
[`docs/FLOW.md` §7](docs/FLOW.md), and three of them were gaps rather than designs:

- the owner dialog did not reopen after a move when the node was *discovered* rather than
  revalidated, which is the ordinary case;
- the refusal that a move produces read as "Personicore is not ready for this computer yet";
- the proxy followed whichever meter node answered, including another household's.

All three are closed, in the Core and therefore on both platforms, plus the two
out-of-process watchdogs and the root helper (helper `Version` 10 — a Mac with 9 installed
keeps working and is offered the new one the next time the switch is turned on). The two
journeys that were already right — first run with and without a node, and the proxy coming
off by itself away from home — are unchanged.

**One computer in two metered homes is still an open contract question, but it is no longer
destructive.** The device id now records the node that issued it, nothing is written on any
other node, and `The device doesn't exist!` is only acted on where the row was made — which
closes the path where each return trip grew an orphaned Device row. The second home re-asks
the owner, reports nothing, does not proxy, and keeps its hands off the first home's record.
`allowNodeChange` (off) is the escape hatch for a node support really did replace. The
analysis, what is verified and the two shapes the contract could take are in
[§7 of the contract](docs/GRAPHQL-CONTRACT.md); `SCENARIO=moved-household` reproduces it.

Not exercised on real hardware: the move itself needs two households. What is covered
offline is in `tools/regression-tests` (the prompt key, `IsRegisteredOnNode`, both pause
lines).

### Next

1. Commit the guided wizard and push `main`. Both are outstanding from 2026-10-06 and nothing
   blocks either: the wizard compiles on both platforms and `tools/wizard-tests` passes, and
   `main` is 11 commits ahead of `origin/main`.
2. Point `packaging.env` at the environment being shipped to and rebuild. The build prints the
   endpoint it baked in; `dist` currently aims at **production**.
3. Open `OwnerForm`, `ProxyForm` and `SetupForm` on a real Windows machine, and run one full
   cycle there. `OwnerForm`'s device-type control changed on 2026-10-01 (`DropDown` →
   `DropDownList`) and `SetupForm` is new; neither has been seen rendered. The assistant's own
   layout (the ink header, the three-step trail, the action strip) compiles and mirrors the
   Avalonia one section for section, which is not the same as having looked at it. Building the
   Windows target from a Mac proves the code is valid and says nothing about the layout.
4. Test the address watcher's firing path with a real lease change.
5. Decide whether Firefox is in scope. Today it inherits the proxy and not the CA; see the
   browser rule above.
