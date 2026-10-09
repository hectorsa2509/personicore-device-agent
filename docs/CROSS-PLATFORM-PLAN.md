# Cross-platform implementation plan

How to bring the agent —registration **and** the proxy switch— from Windows to macOS and
Linux, and what is and is not possible on mobile. The Windows agent is the reference
implementation; everything here keeps its contract (see [`ARCHITECTURE.md`](ARCHITECTURE.md)
and [`GRAPHQL-CONTRACT.md`](GRAPHQL-CONTRACT.md)).

---

## 1. What every platform must do

| Capability | Behavior (identical everywhere) |
|---|---|
| Find the meter node | ping `router.example.invalid` → fallback sweep for port 22 → validate with `/cgi-bin/router_info.sh` |
| Credential and endpoint | `jwt` and `graphql` from `/cgi-bin/get_config.sh`, like `network.html` |
| Household | `getPanelistsRouter(macAddress: wan_mac)` → routerId, householdId, panelists |
| Own identity | local route to the node → IP; `/cgi-bin/list_clients_json.sh` → MAC + machineId |
| Ask the owner | dialog with the household panelist list, first time only |
| Save | `routerIpsAssigment` with `updateIps`, re-asserted every hour and on IP change |
| Proxy switch | on/off by the panelist; proxy = **node IP : 3129**; visible in the OS settings |
| Dry run | `PERSONICORE_DRY_RUN=true` in `.env` → write the desktop file instead of sending |

### Proxy switch rules (all platforms)

1. **On** → set the OS proxy to `<node IP>:3129` for HTTP and HTTPS, with local/private
   addresses bypassed. Remember the user's previous proxy settings first.
   First verify OS trust in the bundled HTTPS CA. Missing or invalid trust blocks
   activation and clears the requested-on state; the menu explains installation and retry.
2. **Follow the node** → if the node's IP changes, update the proxy. Not at the pace of
   the hourly cycle: while the proxy is on, check every `proxyWatchIntervalSeconds`
   (30 s) that the node still answers where the proxy points, and when it does not,
   resolve the name again and move the proxy in seconds. The node rebooting or renewing
   its lease is the common case, and every second of it is a panelist who cannot browse.
   The port is never probed — it is always 3129; only the address changes.
3. **Pause away from home** → no node found ⇒ restore the previous settings so the
   computer keeps internet; re-apply when the node is back. The switch stays "on".
4. **Off** → restore exactly the previous settings.
5. **The OS settings win** → if the panelist turns the proxy off in the OS settings, the
   switch turns off instead of re-enabling it on the next cycle.
6. **Exit** → restore the previous settings (nothing is left watching); re-apply on the
   next start if the switch is on.
7. **The agent's own traffic never uses the proxy** (node CGIs and GraphQL go direct).
7b. **Watchdog** → a task that runs without the agent (Windows: user scheduled task every
   minute; macOS: LaunchDaemon every minute) does rule 2 and rule 3 on its own: follow
   the node to its new address if it is still there, and remove the proxy when nothing
   answers anywhere. It covers the agent being closed, killed or crashed — which would
   otherwise leave the computer browsing through an address that no longer exists.
8. Use the node's **IP**, never `router.example.invalid`: away from home that name
   can resolve to someone else's device. The name is only used to *find* a candidate
   address, and the address is accepted only after a meter node answers there — the root
   helper on macOS re-checks that itself before writing anything.

### The certificate caveat (all platforms)

Port 3129 is Squid's **explicit port with `ssl-bump`** (`squid.conf:156`): HTTPS is
decrypted with the router CA `mitmproxy-ca.pem`. The switch only sets the proxy. Both agents now offer **Install HTTPS certificate…**
for the bundled public CA, with administrator authorization; see [CERTIFICATES.md](CERTIFICATES.md).
Without the matching trusted CA, HTTPS sites show certificate errors while the proxy is on. Where each platform keeps trusted roots
is listed per platform below so that step can be planned.

> **Security note.** In the firmware repo, `files/etc/squid/cert/mitmproxy-ca.pem` holds
> both the CA certificate **and its private key**, and the same file is baked into every
> router. Any computer that trusts this CA can be intercepted by anyone who has the
> firmware image. Before distributing trust in this CA, generate a per-router CA on first
> boot and keep the key out of the repository.

---

## 2. Architecture: one core, three shells

```
Personicore.DeviceAgent.Core      (net9.0 class library, no UI, no OS APIs)
├── Router/     node discovery + CGI client          ← moved as-is from windows/
├── Graph/      GraphQL transport + operations       ← as-is
├── Agent/      cycle, state, outbox, proxy rules    ← as-is, minus WinForms events
├── Device/     MachineId                            ← as-is
└── Platform/   interfaces implemented per OS:
      IDeviceProfileProvider   brand, model, type, OS
      ISystemProxy             Read() / Write(ProxySettings)
      IAutostart               start at login on/off
      IPaths                   state, log, desktop folders

windows/   WinForms tray (today)          + WindowsDeviceProfile, WinInetProxy, RunKey
macos/     menu-bar app                   + MacDeviceProfile, MacProxy (helper), LaunchAgent
linux/     tray app                       + LinuxDeviceProfile, GnomeProxy/KdeProxy, XDG autostart
```

**Recommended UI stack for macOS and Linux: Avalonia** (cross-platform .NET UI with a
native tray/menu-bar icon). It lets both platforms reuse the Core unchanged and the same
dialog layout as Windows. Windows keeps WinForms for now; it can move to Avalonia later to
have a single UI.

Alternative for macOS: a native Swift/AppKit app that re-implements the Core. Better
system integration, but the logic is written twice. Choose it only if Avalonia's
notarization or menu-bar behavior turns out not to be good enough.

Step 1 of the plan is extracting the Core from the Windows project without behavior
changes (the Windows `.exe` must build and behave exactly as today).

---

## 3. macOS

### 3.1 Where the proxy lives and how the panelist sees it

System Settings → Network → *(Wi-Fi / Ethernet…)* → Details… → **Proxies** →
"Web proxy (HTTP)" and "Secure web proxy (HTTPS)" = `<node IP>` port `3129`, plus
"Bypass proxy settings for these hosts & domains". Safari, Chrome and Edge follow it;
Firefox does when set to "Use system proxy settings" (its default).

Proxies on macOS are set **per network service** and are **system-wide** (not per user).

### 3.2 How to set it

| Option | How | Privileges |
|---|---|---|
| `networksetup` CLI | `-setwebproxy "Wi-Fi" IP 3129`, `-setsecurewebproxy …`, `-setwebproxystate … on/off`, `-setproxybypassdomains …` | admin authorization |
| SystemConfiguration API | `SCPreferencesCreateWithAuthorization` + `SCNetworkProtocolSetConfiguration` on the `Proxies` protocol, then `SCPreferencesCommitChanges` / `ApplyChanges` | admin authorization |

Both need admin rights, and a panelist is often a standard user, so the agent uses a
**privileged helper**. **Built** — as a root-owned `sh` script exposed by launchd on a
local socket (`macos/helper/proxy-helper.sh`), rather than the `SMAppService` + XPC design
below: it is 40 auditable lines, needs no separate signed bundle, and asks for the
password once. The XPC design remains the option to move to when the app is notarized and
the socket needs to be restricted to one user. Sketch kept for that step:

1. The app bundle includes a small helper registered with **`SMAppService.daemon`**
   (macOS 13+). The panelist approves it **once** in System Settings → General → Login
   Items (admin password asked once, at install).
2. The menu-bar app asks the helper over **XPC** to apply or restore the proxy. The helper
   only accepts two commands —`apply(ip)` with a fixed port 3129, and `restore()`— and
   validates that `ip` is a private IPv4, so it cannot be abused to point the proxy
   anywhere else.
3. Which services: the one that routes to the node (primary service from
   `State:/Network/Global/IPv4` in `SCDynamicStore`), plus any other active Wi-Fi /
   Ethernet service, so switching from Ethernet to Wi-Fi keeps the proxy.
4. Previous settings are read per service before applying and restored per service.
5. Change detection: watch `Setup:/Network/Service/*/Proxies` with `SCDynamicStore`
   notifications to know at once if the panelist turned it off in System Settings.

### 3.3 The rest of the agent on macOS

| Piece | macOS implementation |
|---|---|
| Resident UI | menu-bar icon (`NSStatusItem`, `LSUIElement = true`); Avalonia `TrayIcon` maps to it |
| Start at login | `SMAppService.mainApp.register()` (macOS 13+) — visible in Login Items |
| Brand / model / type | `Apple`; `sysctl hw.model` (e.g. `Mac14,7`) → marketing name via `system_profiler SPHardwareDataType`; `MacBook*` → laptop, else desktop |
| OS | `ProcessInfo.operatingSystemVersion` → `macOS 15.4` |
| State / log | `~/Library/Application Support/Personicore/DeviceAgent`, `~/Library/Logs/Personicore` |
| Output file (dry run) | `~/Desktop/Personicore-outbox.txt` |
| Private Wi-Fi address | on by default per network: detect the locally administered bit and show the exact path to turn it off (Wi-Fi → Details… → Private Wi-Fi address → Off) |
| Local network permission | macOS 15+ asks before any LAN access; request it with a clear explanation, or the node is never found |
| Distribution | Developer ID signing + notarization (mandatory: Gatekeeper blocks unsigned apps); `.dmg` or `.pkg` (the `.pkg` can install the helper) |
| Architectures | universal binary (arm64 + x64) |
| Trusted roots (if the CA is ever distributed) | login keychain + `SecTrustSettingsSetTrustSettings` (asks the user) |

### 3.4 macOS milestones

1. Core extracted and shared (no macOS code yet).
2. Menu-bar app: discovery, household, owner dialog, save, dry run.
3. Privileged helper + proxy switch.
4. Signing, notarization, `.pkg` with the helper, universal binary.
5. Pilot on 3–5 panelist Macs (Intel and Apple silicon, macOS 13–15).

---

## 4. Linux (desktop)

There is no single system proxy on Linux: each desktop environment keeps its own, and
that is what its Settings page shows.

| Desktop | Where it shows | How to set it (per user, no root) |
|---|---|---|
| GNOME (Ubuntu, Fedora, Debian) | Settings → Network → Network Proxy | `gsettings set org.gnome.system.proxy mode 'manual'`; `org.gnome.system.proxy.http host/port`, `…https host/port`, `ignore-hosts` |
| KDE Plasma | System Settings → Network → Proxy | `~/.config/kioslaverc` `[Proxy Settings]` `ProxyType=1`, `httpProxy`, `httpsProxy`, `NoProxyFor`; then D-Bus `org.kde.KIO.Scheduler.reparseSlaveConfiguration` |
| Others (XFCE, …) | none standard | environment variables only (`http_proxy`, `https_proxy`, `no_proxy` in `~/.config/environment.d/`), effective from the next login |

Chrome/Chromium follow GNOME/KDE settings; Firefox follows them with "Use system proxy
settings". Change detection: `gsettings monitor` / `dconf watch` on GNOME,
file watch on `kioslaverc` on KDE.

| Piece | Linux implementation |
|---|---|
| Resident UI | tray via StatusNotifierItem/AppIndicator (Avalonia); GNOME needs the AppIndicator extension, which Ubuntu ships enabled |
| Start at login | `~/.config/autostart/personicore-device-agent.desktop` |
| Brand / model / type | `/sys/class/dmi/id/sys_vendor`, `product_name`, `chassis_type` (same SMBIOS codes as Windows) |
| OS | `/etc/os-release` `PRETTY_NAME` |
| State / log | `$XDG_CONFIG_HOME/personicore`, `$XDG_STATE_HOME/personicore` |
| Distribution | AppImage (works on any distro) and `.deb`; x64 and arm64 |
| Trusted roots (if ever needed) | system store needs root; Chrome/Firefox use `~/.pki/nssdb` / profile NSS DBs |

---

## 5. Mobile and other systems

| System | Registration agent | Proxy switch |
|---|---|---|
| Android | Possible as an app, but no background LAN agent is reliable (battery limits) | An app cannot change the Wi-Fi proxy; only the user (Wi-Fi → Proxy → Manual) or an MDM/device-owner policy. Plan: in-app instructions with the IP and port 3129 |
| iOS / iPadOS | Possible as an app for the owner question only | Only via a **configuration profile** (Wi-Fi payload with `ProxyType Manual`) installed by the user or MDM; the app can offer the profile for download |
| ChromeOS | Out of scope (no background agents) | Settings → Network → Proxy by the user, or admin policy |

The node already identifies these devices by fingerprint; the agent is for computers.

---

## 6. Order of work

| # | Deliverable | Depends on |
|---|---|---|
| 1 | Windows proxy switch (**done**) | — |
| 2 | Extract `Personicore.DeviceAgent.Core`, Windows unchanged (**done**) | 1 |
| 3 | macOS menu-bar app (registration) (**built, not run yet** — test plan in `macos/README.md`) | 2 |
| 4 | macOS privileged helper + proxy switch (**built, not run yet**) | 3 |
| 5 | macOS signing, notarization, `.pkg` | 4, Apple Developer ID |
| 6 | Linux tray app (GNOME + KDE proxy) | 2 |
| 7 | Decide how the router CA is trusted (or stop bumping on 3129) | security review |
| 8 | Mobile: proxy instructions / iOS profile | 7 |
