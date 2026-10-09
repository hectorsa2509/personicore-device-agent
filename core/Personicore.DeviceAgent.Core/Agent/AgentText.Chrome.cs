namespace Personicore.DeviceAgent.Agent;

public static partial class AgentText
{
    public const string ChromeSetup = "Set up Chrome…";
    public const string ChromeTitle = "Chrome and the Personicore proxy";
    public const string ChromeIntro = "Use this optional DNS compatibility setting only on a Mac you administer. "
        + "It turns off Chrome Secure DNS for this macOS user, across Chrome profiles. DNS queries may be unencrypted. "
        + "Chrome may show ‘Managed by your organization’. Existing DNS policies are left unchanged. "
        + "This setting remains until you undo it here; it does not change certificate validation.";
    public const string ChromeApply = "Turn off Secure DNS";
    public const string ChromeUndo = "Undo this app’s DNS setting";
    public const string ChromeApplied = "The local Chrome DNS policy was saved and read back. Open Chrome policies and reload them to verify it is effective; "
        + "an organization policy can take precedence. If needed, save your work, quit Chrome with ⌘Q and reopen it.";
    public const string ChromeExisting = "Chrome already has a DNS policy. It was left unchanged. Check Chrome policies for the effective setting.";
    public const string ChromeManaged = "This DNS setting is managed. Ask your administrator; this app will not override it.";
    public const string ChromeUndone = "This app’s local DNS setting was removed. Reload Chrome policies or restart Chrome when convenient.";
    public const string ChromeNotOwned = "There is no DNS change owned by this app to undo.";
    public const string ChromeChangedElsewhere = "The DNS setting has changed or is managed elsewhere. It was left unchanged.";
    public const string ChromeUnavailable = "Google Chrome was not found in Applications. Install Chrome before using this setup.";
    public const string ChromeBusy = "Working…";
    public const string ChromeFailed = "The operation could not be completed. No successful configuration or recovery is being claimed. Check the agent log.";
    public const string ChromePermission = "In System Settings → Privacy & Security → Local Network, enable Google Chrome. "
        + "macOS requires you to authorize it; allowing Personicore does not allow Chrome. "
        + "After a change, save your work and quit Chrome with ⌘Q, then reopen it.";
    public const string ChromeOpenPermission = "Open Privacy & Security";
    public const string ChromeOpenPolicy = "Open Chrome policies";
    public const string ChromeTest = "Check proxy connection";
    public const string ChromeOpenTest = "Test in Chrome";
    public const string ChromeTestNote = "The check contacts example.com through the active macOS HTTPS proxy. "
        + "An agent check does not prove Chrome has permission or trusts the certificate. Use Test in Chrome too; "
        + "if it fails, record the exact ERR_ code. These checks do not switch the proxy off.";
    public const string ChromeNoProxy = "No active HTTPS proxy on port 3129 was found. Turn on the Personicore proxy, then check again.";
    public const string ChromeConnected = "The agent reached the proxy and completed a verified HTTPS request. Now test in Chrome; its permissions and certificate checks are separate.";
    public const string ChromeTcpFailed = "The agent could not reach the proxy port. Check the network, proxy service and local-network permission.";
    public const string ChromeTlsFailed = "HTTPS certificate or TLS verification failed. Check the router certificate and trust chain. Certificate verification was not bypassed.";
    public const string ChromeTunnelFailed = "The proxy rejected or could not establish the HTTPS tunnel. Check the proxy’s access rules and logs.";
    public const string ChromeHttpFailed = "The HTTPS check did not complete successfully. Check the proxy logs and try the same page in Chrome.";
    public static string ChromeAddress(string ip, string proxy) => $"Last detected Mac IP: {ip}\nActive HTTPS proxy: {proxy}";
}
