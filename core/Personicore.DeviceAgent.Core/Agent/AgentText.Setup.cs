namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// Everything the setup assistant says. Three steps, in one place, so the WinForms window
/// and the Avalonia one cannot drift: they render <see cref="SetupPlan"/> and neither of
/// them owns a sentence.
/// </summary>
public static partial class AgentText
{
    // ── Menu and frame ──────────────────────────────────────────────────────

    public const string SetupItem = "Setup assistant…";
    public const string SetupWindowTitle = "Personicore · Setup";
    public const string SetupHeading = "Three steps and this computer is set up";
    public const string FirstRunWelcomeTitle = "Welcome to Personicore";
    public static string FirstRunWelcomeBody(PlatformWords words) =>
        $"Welcome to Personicore! The setup assistant will guide you through connecting {words.ThisComputer} to your household.";

    public static string SetupSubtitle(PlatformWords words) =>
        $"Who uses {words.ThisComputer}, the HTTPS certificate, and the measurement proxy. "
        + "You can stop after any step and come back from the Personicore menu.";

    public static string SetupProgress(int number, int total) => $"Step {number} of {total}";

    /// <summary>
    /// The three steps as one line, so the panelist can see where they are without the
    /// window having to draw a diagram. A done step is ticked, the current one is marked.
    /// </summary>
    public static string SetupTrail(SetupStep current, AgentSnapshot snapshot) =>
        string.Join("    ", SetupPlan.Steps.Select(step =>
        {
            var mark = SetupPlan.IsDone(step, snapshot) ? "✓" : step == current ? "▸" : "·";
            return $"{mark} {SetupPlan.Name(step)}";
        }));

    // ── Step 1 · the panelists ──────────────────────────────────────────────

    public const string SetupOwnersName = "Panelists";
    public const string SetupOwnersTitle = "Who uses this computer?";

    public static string SetupOwnersBody(PlatformWords words) =>
        $"Choose the household member {words.ThisComputer} belongs to. If several people use it, "
        + "check everyone: Personicore has to know whose activity it is recording before it "
        + "measures anything.";

    public const string SetupOwnersAction = "Choose panelists…";

    /// <summary>
    /// Once there are panelists the step is done, but it is not closed: who uses a computer
    /// changes. The same words as the menu item, because it opens the same dialog.
    /// </summary>
    public const string SetupOwnersActionDone = ChangeOwner;

    public static string SetupOwnersDone(IEnumerable<string> owners) =>
        "Done · " + string.Join(", ", owners);

    // ── Step 2 · the certificate ────────────────────────────────────────────

    public const string SetupCertificateName = "HTTPS certificate";
    public const string SetupCertificateTitle = "Trust Personicore's HTTPS certificate";

    public static string SetupCertificateBody(PlatformWords words) =>
        "The measurement proxy has to read secure pages, and it can only do that while "
        + $"{words.ThisComputer} trusts Personicore's certificate. {words.CertificateAdminNote}";

    public const string SetupCertificateDone =
        "Done · this computer trusts Personicore's certificate.";

    /// <summary>While the installer runs and the OS is asking for permission.</summary>
    public const string SetupCertificateWorking = "Installing the certificate…";

    // ── Step 3 · the proxy ──────────────────────────────────────────────────

    public const string SetupProxyName = "Measurement proxy";

    /// <summary>
    /// The heading follows the switch, like <see cref="CertificateItem"/> does: telling a
    /// panelist to turn on something that is already on, above a line reading "On", is how
    /// a screen stops being believed.
    /// </summary>
    public static string SetupProxyTitle(bool on) =>
        on ? "The Personicore proxy" : "Turn on the Personicore proxy";

    public static string SetupProxyBody(PlatformWords words) =>
        ProxySubtitle(words) + " " + words.ProxyAdminNote;

    public const string SetupProxyAction = "Turn on the proxy";

    /// <summary>
    /// Turning it off is offered here for the same reason the menu never greys it out: a
    /// panelist must never be left with a switch they cannot undo.
    /// </summary>
    public const string SetupProxyActionOff = "Turn off the proxy";

    // ── Closing ─────────────────────────────────────────────────────────────

    public const string SetupAllDone =
        "All three steps are done. Personicore is measuring this computer, and the menu shows "
        + "its status from now on.";

    /// <summary>
    /// Under a step the panelist chose not to do. It says where to find it again rather
    /// than nagging: skipping is a legitimate answer, forgetting where the switch was is not.
    /// </summary>
    public const string SetupSkippable =
        "You can leave this for later and come back from the Personicore menu.";

    public const string ButtonSetupBack = "Back";
    public const string ButtonSetupNext = "Next";
    public const string ButtonSetupSkip = "Skip for now";
    public const string ButtonSetupFinish = "Finish";
}
