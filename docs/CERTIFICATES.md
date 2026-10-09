# HTTPS certificate

Both agents embed `core/Personicore.DeviceAgent.Core/Certificates/mitmproxy-ca_computer.crt`.
It is the public certificate supplied for this deployment, not a generated replacement.

- Subject/issuer: `C=US, ST=Proxy, L=MITM, O=Squid, OU=SSL-Bump, CN=MITM Proxy CA`.
- Validity: May 13, 2025 through May 11, 2035 (UTC).
- SHA-256: `741FFDF0F0CBD5E75255B797F4208BCED3D1C27181DFC5FB2120F33AD231D94C`.

On the meter network, choose **Install HTTPS certificate…** in the agent menu and
complete the OS administrator authorization. The action does not enable the proxy.
Windows installs into Local Machine / Trusted Root Certification Authorities and skips
installation when the exact certificate is already present. macOS installs into the
System keychain and applies unrestricted **Always Trust** through Security.framework
in the interactive agent process; repeating the action repairs an imported-only
certificate. macOS presents its own authorization dialogs. Cancellation or failure is reported without claiming success.
The menu remains disabled while installation is running.

Both agents check current OS trust before enabling or automatically resuming the proxy.
Windows requires the exact CA in the machine root store and a successful system-chain
validation for server authentication. macOS evaluates the bundled CA against SSL trust
with `security verify-cert -p ssl -l -L`, without supplying a temporary root override.
An expired CA, explicit distrust, missing certificate or verification failure blocks
activation, clears the requested-on state and restores only proxy settings owned by the
agent. The status explains how to use **Install HTTPS certificate…** and retry. Installing
the certificate does not itself re-enable the proxy.

Trust is also rechecked during proxy monitoring. The Windows watchdog and macOS helper
check trust before following the node; the macOS helper checks system trust independently
when the app is closed. A user's macOS trust overrides are checked by the running agent.
The updated macOS helper (version 9) is required; an older helper must be upgraded with
administrator authorization before enabling the proxy. Canceling the upgrade leaves it
off. If restoring an existing proxy fails, the agent reports the failure rather than
claiming that browsing has been recovered.

The resource is checked for its pinned SHA-256 fingerprint, CA basic constraint and
validity dates before installation. Windows' elevated child only accepts the bundled
certificate, never a caller-supplied path. macOS checks the pinned fingerprint in the System keychain before import. If absent,
an elevated shell imports public DER bytes using add-certificates, without granting
trust. Temporary files are created by root and removed on exit. The GUI process then
creates the native certificate from embedded bytes and requests trust authorization.
TLS verification remains enabled. Applications with their own trust stores or
certificate pinning can require separate configuration or remain incompatible.

The deployed router must use this CA's corresponding private key to issue proxy
certificates. Matching the router has not been verified by adding this file. The
firmware key-sharing concern in [CROSS-PLATFORM-PLAN.md](CROSS-PLATFORM-PLAN.md) still
applies: possession of the CA private key allows impersonation to trusting computers.
Never include the private key in an agent package.

Trust persists when the agent exits or the proxy is disabled. To remove it, locate the
certificate by its fingerprint in `certlm.msc` on Windows (Trusted Root Certification
Authorities / Certificates), or in Keychain Access on macOS (System / Certificates),
and delete it with administrator authorization. Do not delete other certificates
that happen to share its display name.

To rotate this CA, replace the public resource and update `BundledCertificate.Sha256`
together, rebuild both agents, and separately plan removal of the old trust root.

Validation on real Windows and macOS must cover successful authorization, cancellation,
repeated installation and an HTTPS request through the actual router. Compilation and
resource checks alone do not verify those system behaviors.

## macOS import without trust (0.4.1 fix)

The prior elevated `osascript` installer could import the CA and then fail with
`SecTrustSettingsSetTrustSettings: The authorization was denied since no user
interaction was possible`. Version 0.4.1 attempted native import, but System keychain writes from the unprivileged
agent failed with OSStatus -61. Version 0.4.2 separates elevated import
(`security add-certificates`) from `SecTrustSettingsSetTrustSettings` in the GUI agent.
It skips import when the exact pinned certificate is already present. An existing certificate
is not treated as success: the trust operation still runs. NULL trust settings request
Apple's unrestricted Always Trust for this root, and success requires both an empty
admin trust-settings array and effective SSL validation for the signed-in user.
Cancellation or management restrictions still block the proxy.

Windows uses LocalMachine/Root (not Personal or Intermediate CA) and verifies native
chain trust both after elevation and in the original user's process. Version 0.4.1
also logs Windows chain rejection reasons and distinguishes import failure from an
imported certificate rejected by policy. It does not override explicit distrust.

Read-only native macOS diagnostic (does not install or trust anything):

```sh
dotnet run --project tools/chrome-setup-tests/ChromeSetupTests.csproj -- --certificate-readonly
```

## Verified on macOS, 2026-09-29

After the user completed the 0.4.2 authorization dialogs, the read-only diagnostic
confirmed all three results: the pinned certificate exists in System, admin Always
Trust is set, and effective SSL validation succeeds. Before installation all three
were false. Windows interactive installation remains unverified on a Windows host.
