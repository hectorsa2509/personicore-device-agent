namespace Personicore.DeviceAgent.Agent;

public static partial class AgentText
{
    public const string CertificateRequired = "Proxy blocked: HTTPS certificate is not trusted. Choose ‘Install HTTPS certificate…’ in the Personicore menu, approve administrator access, then try again.";
    public const string ProxyHelperUpgradeRequired = "Proxy blocked: turn the proxy on again and approve the helper update. The update is required to check HTTPS certificate trust.";
    public const string CertificateRestoreFailed = "The previous proxy settings could not be restored. Turn off the Personicore proxy in system network settings to recover browsing.";
    public const string InstallCertificate = "Install HTTPS certificate…";

    /// <summary>
    /// The same menu item once the certificate is in place. No ellipsis and no verb: there is
    /// nothing to do, and an item that still reads "Install…" invites a panelist to type an
    /// administrator password for something already done.
    /// </summary>
    public const string CertificateAlreadyInstalled = "HTTPS certificate: installed";

    /// <summary>Which of the two the menu shows, and the only place that decision is made.</summary>
    public static string CertificateItem(AgentSnapshot snapshot) =>
        snapshot.CertificateTrusted ? CertificateAlreadyInstalled : InstallCertificate;
    public const string CertificateInstalled = "Personicore's HTTPS certificate is trusted on this computer. Restart your browser if needed.";
    public const string CertificateFailed = "The certificate could not be installed or authorization was canceled. See the agent log for details.";
}
