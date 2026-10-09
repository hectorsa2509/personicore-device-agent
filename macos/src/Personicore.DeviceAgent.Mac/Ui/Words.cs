using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// The only words this agent is allowed to say differently from the Windows one.
/// Everything else comes from <see cref="AgentText"/>, so the two cannot drift apart.
/// </summary>
internal static class Words
{
    public static readonly PlatformWords Mac = new(
        ThisComputer: "this Mac",
        StartupItem: "Start at login",
        ProxySettingsPath: "System Settings › Network › Details… › Proxies",
        RandomizedMacNote:
            "⚠  This network uses a private Wi-Fi address. Measurement still works, but continuity is "
            + "not guaranteed if macOS rotates it. To turn it off: System Settings → Wi-Fi → Details… "
            + "→ Private Wi-Fi address → Off.",
        ProxyAdminNote:
            "Turning it on asks for your administrator password once, to install the small helper that "
            + "changes system proxies. It shows in System Settings → Network → Details… → Proxies, it "
            + "pauses itself away from home, and turning it off puts your previous settings back.",
        RandomizedMacTag: "(private address)",
        ProxyFirstRunNotice:
            "Personicore needs your administrator password once to change the system proxy.",
        CertificateAdminNote:
            "macOS will ask for your administrator password once, to add the certificate to the "
            + "System keychain. After that there are no more prompts.");
}
