using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// The only words this agent is allowed to say differently from the macOS one.
/// Everything else comes from <see cref="AgentText"/>, so the two cannot drift apart.
/// </summary>
internal static class Words
{
    public static readonly PlatformWords Windows = new(
        ThisComputer: "this PC",
        StartupItem: "Start with Windows",
        ProxySettingsPath: "Settings › Network & internet › Proxy",
        RandomizedMacNote:
            "⚠  This adapter uses a randomized MAC address. Measurement still works, but continuity is "
            + "not guaranteed if Windows rotates it. To turn it off: Settings → Network & internet → "
            + "Wi-Fi → your network → Random hardware addresses → Off.",
        ProxyAdminNote:
            "Turning it on changes the proxy for your Windows user only, so no administrator password "
            + "is needed. It shows in Settings → Network & internet → Proxy, it pauses itself away "
            + "from home, and turning it off puts your previous settings back.",
        RandomizedMacTag: "(randomized)",
        // Nothing to warn about: the proxy is a per-user setting here.
        ProxyFirstRunNotice: "",
        CertificateAdminNote:
            "Windows will ask you to approve an administrator prompt once, to add the certificate "
            + "to the computer's trusted roots. After that there are no more prompts.");
}
