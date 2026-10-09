# macOS agent

Menu-bar app that registers this Mac as a personal device of the household. Same
behavior as the Windows agent: it finds the meter node, reads the household panelists
with the router's `jwt` + `graphql` (like `network.html`), asks who owns the Mac and saves
with `presenceCreateDevice`.

.NET 9 + [Avalonia](https://avaloniaui.net/) 12. The logic is **not** a copy: it is the
shared library in [`../core/`](../core/), the same code the Windows `.exe` runs.

> **Status: built, not yet run.** Registration (phase 1) and the proxy switch (phase 2)
> are both written. It compiles, the `.app` bundles pass `codesign --verify` and
> `plutil -lint`, and the helper's refusals were tested, but the app has **not been
> launched on any Mac yet**. The first run is the test plan below.

---

## What is done and what is not

| Piece | Status |
|---|---|
| Menu bar icon, colored by status, no Dock icon | Built |
| Node discovery, household read, owner dialog, save | Built — shared Core, same code as Windows; the household read was verified against production (read-only) |
| Owner dialog with the household panelist list; always-visible "Register computer" | Built |
| Details window, dry run (`PERSONICORE_DRY_RUN`), desktop outbox file | Built |
| Brand / model / type / OS (`Apple`, `MacBook Air (Mac15,12)`, `laptop`, `macOS Sequoia 15.7.4`) | Built — read with `sysctl`, `system_profiler`, `sw_vers` |
| Start at login (per-user LaunchAgent, no admin) | Built |
| Notifications | Built — native `UNUserNotificationCenter`, with permission request and test action |
| Local network permission text (`NSLocalNetworkUsageDescription`) | In `Info.plist` |
| Private Wi-Fi address warning with the exact setting path | Built |
| **Proxy switch** | Built — root helper installed on first use, see below |
| Developer ID signing + notarization | Wired into the build — see [Distribution](#distribution). Still **pending an Apple Developer account**: with none installed the build signs ad hoc and says so |
| Universal binary | Not planned: one `.app` per architecture, like one `.exe` per architecture on Windows |

---

## Build

From macOS (Python 3 with Pillow is needed for the icon):

```bash
./macos/build/make-app.sh
```

| Output | For |
|---|---|
| `macos/dist/arm64/Personicore Device Agent.app` · `PersonicoreDeviceAgent-macos-arm64.zip` | Apple silicon (M1 and later) |
| `macos/dist/x64/Personicore Device Agent.app` · `PersonicoreDeviceAgent-macos-x64.zip` | Intel Macs |

Each folder also gets `INSTALL.txt` (end-user steps) and `.env.example`. The bundles are
self-contained (~105 MB, ~43 MB zipped): no .NET needed on the panelist's Mac.

For development:

```bash
dotnet build macos/src/Personicore.DeviceAgent.Mac
```

---

## Distribution

The Mac App Store is not an option for this agent and never will be, which is worth
stating once so nobody spends a week on it. The store requires App Sandbox, and the agent
installs a root helper in `/Library/LaunchDaemons`, rewrites **system-wide** proxies with
`networksetup`, puts a CA in the System keychain and writes its own LaunchAgent — four
things the sandbox forbids and no entitlement grants. Even stripped of all of them, a
self-contained CoreCLR build needs `com.apple.security.cs.allow-unsigned-executable-memory`
for the JIT, which App Review does not give a sandboxed app.

**Developer ID + notarization is the channel.** `make-app.sh` does it when the machine is
set up for it, and reports on stderr which of the three outcomes happened:

| What the Mac has | What is produced | First launch for a panelist |
|---|---|---|
| nothing | ad hoc signature | refuses; runs only on the Mac that built it |
| a Developer ID Application certificate | hardened runtime + entitlements + timestamp | Privacy & Security → *Open Anyway* |
| that, and `PERSONICORE_NOTARY_PROFILE` | notarized and stapled | double click, no warning |

### One-time setup, per Mac that builds

1. **Apple Developer Program**, 99 USD/year. Generating a Developer ID certificate needs
   the **Account Holder** or **Admin** role in the team; a Developer cannot.
2. **The certificate.** Xcode → Settings → Accounts → Manage Certificates → `+` →
   **Developer ID Application**. Then `security find-identity -v -p codesigning` must list
   it. Two of them (an expired one and its replacement) is the common case, and the build
   refuses to guess: set `PERSONICORE_SIGN_IDENTITY` to the hash of the one to use.
3. **The notarization credential**, which is what turns *Open Anyway* into a double click:

   ```bash
   xcrun notarytool store-credentials personicore-notary \
       --apple-id "you@example.com" --team-id TEAMID1234
   ```

   The password it asks for is an **app-specific password** from appleid.apple.com, not
   the Apple ID's own. It is stored in the login keychain, so this is done once per Mac
   and never appears in the repository.
4. **Point the build at it** by adding to `packaging.env` in the repository root
   (gitignored, alongside the packaged credential):

   ```
   PERSONICORE_NOTARY_PROFILE=personicore-notary
   ```

Then `./build.sh mac` as always. The Developer ID signature asks Apple's timestamp
service once per binary and notarization waits for the service to answer, so a release
build takes minutes where a development build takes seconds. That is the whole difference
in the command.

### Publishing a release

1. **Aim the package.** `packaging.env` decides the endpoint and token baked into the
   bundle, and the build prints both on stderr. A package aimed at stage handed to a real
   panelist is the easy mistake here.
2. **Build**: `./build.sh mac`. Confirm the output says `signed Developer ID` and
   `notarized and stapled · Gatekeeper source="Notarized Developer ID"`.
3. **Check it on a Mac that did not build it** — a colleague's, or one with
   `spctl --assess` run after copying the zip over. A ticket that failed to staple only
   shows up as a Gatekeeper warning on someone else's machine.
4. **Hand over** `PersonicoreDeviceAgent-macos-arm64.zip` and `-x64.zip` with
   `INSTALL.txt`. Once notarized, the *Open Anyway* step in those instructions no longer
   applies and should come out.

Notarization still needs the account before it can be run once end to end; everything up
to `notarytool submit` is exercised, and the scripts refuse clearly rather than guessing
when a piece is missing.

### Signing by hand

Both halves are separate scripts, because a build made on one Mac may be signed or
notarized later on another:

```bash
macos/build/sign-app.sh "macos/dist/arm64/Personicore Device Agent.app"
macos/build/notarize-app.sh "macos/dist/arm64/Personicore Device Agent.app" \
                            macos/dist/PersonicoreDeviceAgent-macos-arm64.zip
```

`PERSONICORE_SIGN_ADHOC=true` forces the ad hoc signature even on a Mac that has a
certificate, for a build that is not leaving the machine.

**One trap, measured rather than read.** The bundle is signed with a single
`codesign --deep` pass, which is normally the wrong tool. It is the only one that works
here: the .NET apphost requires its assemblies, `deps.json` and `runtimeconfig.json` in
its own directory, so all 218 files sit in `Contents/MacOS`, and codesign treats every
one of them as nested code — the managed `.dll`s and the `.runtimeconfig.json` included.
Signing inner-to-outer fails with *"code object is not signed at all / In subcomponent:
…"*. Apple's warning about `--deep` is about nested **bundles** getting the outer
bundle's entitlements, and there are none here. Related: the main executable must never
be signed on its own first — codesign resolves that path to the bundle enclosing it and
signs the bundle instead. `sign-app.sh` verifies afterwards that all 19 Mach-O binaries
came out with the hardened runtime, because the notary service answers that question by
email twenty minutes later.

---

## Proxy switch

Menu → **Use Personicore proxy**. When on, macOS uses **the meter node's IP, port 3129**
as the web and secure web proxy on every network service, with local and private
addresses bypassed. It shows in System Settings → Network → *(Wi-Fi…)* → Details… →
**Proxies**, and Safari, Chrome, Edge and Firefox follow it.

Same rules as Windows: it follows the node's IP, pauses itself away from home so the Mac
keeps internet, restores the previous settings when switched off or on quit, and turns
itself off if the panelist changes the proxy in System Settings.

**Following the node.** The proxy points at an IP, and that IP comes from DHCP: reboot
the node, or let its lease renew, and the address the proxy uses is suddenly dead. The
whole cycle is hourly, so without help the panelist would spend up to an hour unable to
browse. While the agent runs it checks every **30 s** that the node still answers where
the proxy points (`proxyWatchIntervalSeconds`); when it does not, it resolves the node's
name and moves the proxy to the new address in seconds. If no node answers anywhere, the
panelist's own settings come back at once —browsing first— and the switch waits in
*paused*, doubling its wait up to five minutes so a Mac away from home is not probing
forever. The port is never checked: it is always 3129. What changes is the address.

**Watchdog.** If the agent is not running at all —quit, killed, crashed, never opened—
something still has to watch that proxy. A second LaunchDaemon runs the helper **every
minute** and does the same two things root can do on its own: follow the node to its new
address if it is still at home, and take the proxy out when no node answers at all, so a
Mac that left home is never left without internet. It is installed with the helper and
removed with it.

### Why it needs an administrator password once

On macOS proxies are **system-wide**, not per user, so changing them needs admin rights.
Prompting every time would be unworkable: the agent also changes the proxy on its own
when the Mac leaves or comes home. So the first time the switch is turned on, the agent
installs a small root helper — **one password prompt, asked by macOS itself** — and after
that there are no more prompts.

| Piece | Where |
|---|---|
| Helper script (root-owned, 40 lines of `sh`) | `/Library/Application Support/Personicore/proxy-helper.sh` |
| launchd daemon that exposes it on a local socket | `/Library/LaunchDaemons/com.personicore.personicore.proxyhelper.plist` |
| Socket the agent talks to | `/var/run/personicore/proxy.sock` |
| Previous proxy settings, to restore | `/Library/Application Support/Personicore/proxy-previous.txt` |
| What the helper set, per service and kind | `/Library/Application Support/Personicore/proxy-applied.txt` |
| Mark left when the watchdog paused the proxy | `/Library/Application Support/Personicore/proxy-paused.txt` |
| The node's name, so the watchdog can follow it | `/Library/Application Support/Personicore/router-host.txt` |
| Installed helper version (bumped → one prompt to upgrade) | `/Library/Application Support/Personicore/helper-version.txt` |
| Watchdog daemon (every minute) | `/Library/LaunchDaemons/com.personicore.personicore.proxyhelper.watchdog.plist` |
| Source of the script | [`helper/proxy-helper.sh`](helper/proxy-helper.sh) |

The helper accepts exactly five commands and **refuses** anything else:

| Command | What it does |
|---|---|
| `APPLY <ip> [host]` | The panelist turned the switch on: every network service, both kinds. |
| `REPOINT <ip> [host]` | The node moved and the agent is following it. Touches **only** what the helper still owns, so a proxy taken over in System Settings since is left alone. |
| `RESTORE` | Puts back what was saved — and answers `ERROR` if the OS refused, keeping the backup so the next attempt can still undo it. |
| `OWNED` | What root still owns, at which address, and whether the watchdog is the one that paused it. The agent asks before deciding that an unfamiliar proxy means the panelist changed it. The address is `mixed` when the owned settings do not agree — a move macOS accepted for one and refused for another — which the agent cannot see for itself, because it reads only the web proxy of each service. |
| `STATUS` | The proxy of each service. |

`APPLY` and `REPOINT` are refused unless the address is a private IPv4 **and**
a real Personicore node answers `router_info.sh` there; the port is fixed at 3129 in the
script and never comes from the request. That way the privileged side cannot be used to
point the Mac's traffic anywhere else. Every write is read back before the helper claims
it: a setting macOS refused is never recorded as ours, and one it refused to put back
keeps its claim and its backup. Before taking back a proxy it had released to the
panelist, the helper saves what they had set, so switching off afterwards returns their
setting and not ours. Refusals are logged to the system log
(`log show --predicate 'process == "logger"'`, tag `personicore-proxy-helper`).

**Known limitation:** the socket is reachable by any process of any local user, because
launchd cannot restrict it to one user. A local process could therefore turn the proxy on
or off — but only towards a real meter node. Tightening this (peer credential checks)
needs a compiled helper instead of a shell script; it is a deliberate trade-off for a
helper that can be read and audited in one page.

To remove the helper by hand:

```bash
sudo launchctl bootout system/com.personicore.personicore.proxyhelper
sudo rm -rf "/Library/Application Support/Personicore" /Library/LaunchDaemons/com.personicore.personicore.proxyhelper.plist /var/run/personicore
```

**HTTPS certificate.** Choose **Install HTTPS certificate…** in the agent menu while
connected to a meter node. The bundled public CA is installed with administrator
authorization: Windows uses Local Machine / Trusted Root Certification Authorities;
macOS uses the System keychain with native Always Trust authorization. No private key is shipped and TLS
validation remains enabled. See [`../docs/CERTIFICATES.md`](../docs/CERTIFICATES.md)
for the identity, limitations and removal instructions.

---

## Test plan for the first run

Run it on a Mac on the network of a Personicore router. Start in dry run so nothing is
written to production until the flow looks right.

1. **Dry run on.** Create `~/Library/Application Support/Personicore/DeviceAgent/.env`
   with `PERSONICORE_DRY_RUN=true`.
2. **Open the app.** Unzip, move to Applications, open. An ad hoc or merely signed build
   is not notarized, so on macOS 15: open once, then System Settings → Privacy &
   Security → *Open Anyway*. A notarized build opens with a double click.
3. **Local network prompt** → *Allow*. Check: the menu bar icon appears, no Dock icon.
4. **Owner dialog.** Within a few seconds the dialog opens by itself. Check:
   - the header shows the household ID;
   - the identity box shows this Mac's IP, MAC and machineId;
   - brand `Apple`, model like `MacBook Air (Mac15,12)`, OS `macOS Sequoia 15.x`;
   - the **household panelists are listed** as checkboxes;
   - "Register computer" is visible without scrolling.
5. **Register** with one panelist. Check: icon turns blue, and
   `~/Desktop/Personicore-outbox.txt` has the mutation with the real `routerId` and
   `panelistIds`.
6. **View details…** shows everything; *Copy* and *Open logs folder* work.
7. **Start at login** on → log out and in → the icon comes back.
8. **Real save.** Remove `PERSONICORE_DRY_RUN` (or set it to `false`), *Refresh now*.
   Check: icon turns green, and the Mac shows up in the router's `network.html` with the
   chosen name and panelist. The OS will not be saved until the backend adds `deviceOs`
   (see [`../docs/GRAPHQL-CONTRACT.md`](../docs/GRAPHQL-CONTRACT.md) §5.1).
9. **Away from home.** On another network the icon turns gray; back home it recovers.
10. **Proxy switch on.** Enter the administrator password once. Check: System Settings →
    Network → Wi-Fi → Details… → Proxies shows the node's IP and port 3129 for "Web
    proxy" and "Secure web proxy". (HTTPS sites will warn about certificates until the
    router CA is trusted — expected.)
11. **Proxy pause.** Move the Mac to another network: the proxy should come off by itself
    and browsing keep working. Back home it comes back.
12. **Proxy off**, then quit the agent: the previous proxy settings must be back both
    times.
13. **Watchdog.** With the proxy on, quit the agent and move the Mac to another network.
    Within a minute browsing must work again on its own (check the system log:
    `log show --last 10m --predicate 'eventMessage contains "personicore-proxy-helper"'`).
14. **The node changes address.** With the proxy on, reboot the meter node (or force a
    new lease). Within about half a minute the log must say *"The meter node moved from
    … to …; proxy re-pointed"* and browsing must never stop. Repeat with the agent
    closed: the same must happen from the watchdog, within a minute.

If something fails, the log is `~/Library/Logs/Personicore/agent.log`; set
`"logLevel": "debug"` in `~/Library/Application Support/Personicore/DeviceAgent/config.json`
for more detail.

**Most likely to need adjusting on the first run** (written without running it): the menu
bar icon size and tint, and window focus when the dialog opens by itself. Notifications
now come from Personicore itself through the native UserNotifications framework.

---

## Files on the Mac

```
~/Desktop/
└── Personicore-outbox.txt       only in dry run

~/Library/Application Support/Personicore/DeviceAgent/
├── state.json                   declared owner, household and last assignment
├── .env                         optional (dry run, endpoint/token override)
└── config.json                  optional

~/Library/Logs/Personicore/agent.log
~/Library/LaunchAgents/com.personicore.personicore.deviceagent.plist   when "Start at login" is on
```

Machine-wide `config.json` / `.env` (for IT): `/Library/Application Support/Personicore/DeviceAgent/`.

---

## Differences from Windows

| | Windows | macOS |
|---|---|---|
| UI | WinForms tray icon | Avalonia menu-bar icon (`LSUIElement`) |
| Start at login | user `Run` key | user LaunchAgent |
| Brand / model | BIOS registry / WMI | `Apple`; `system_profiler` + `sysctl hw.model` |
| Type | SMBIOS chassis | `MacBook*` → laptop, otherwise desktop |
| OS | `CurrentVersion` registry | `sw_vers` → `macOS Sequoia 15.7.4` |
| Notifications | tray balloon | Native UserNotifications |
| Proxy switch | WinINet per-user proxy, no admin | root helper (launchd + local socket), one password at install |
| First launch friction | SmartScreen (unsigned) | Gatekeeper until the build is notarized, then none + local network permission |

## Structure

```
macos/
├── build/make-app.sh                    builds the .app bundles and zips
├── build/sign-app.sh                    Developer ID or ad hoc; decides which
├── build/notarize-app.sh                submits to Apple and staples the ticket
├── build/agent.entitlements             hardened-runtime entitlements CoreCLR needs
├── helper/proxy-helper.sh               the only part that runs as root
├── INSTALL.txt                          end-user instructions
└── src/Personicore.DeviceAgent.Mac/
    ├── Program.cs, App.cs               startup, single instance, no Dock icon
    ├── Platform/                        device profile, LaunchAgent, notifications, shell
    └── Ui/                              menu bar controller, owner dialog, details, icons
../core/Personicore.DeviceAgent.Core/    shared with Windows: discovery, GraphQL, cycle, state
```

## Notification setup

1. Allow notifications when Personicore asks. In **System Settings → Notifications →
   Personicore Device Agent**, enable **Allow notifications** and select **Banners** or **Alerts**.
2. To receive them while mirroring or sharing the screen, enable **Allow notifications
   when mirroring or sharing the display** on the main **Notifications** page. This is
   global and may expose notifications from other apps while sharing; the agent does
   not change it automatically.
3. Check **Focus / Do Not Disturb** if a notice remains hidden. Click the clock to inspect
   Notification Center and use **Test proxy notification** in the agent menu to retry.

An accepted request does not guarantee a visible banner: macOS can silence it because
of the display state. The automatic reminder repeats every 30 minutes while the proxy
switch is off and the agent is running. The test sends the notice regardless of the
switch state without changing the proxy. See [INSTALL.txt](INSTALL.txt) for end-user steps.
