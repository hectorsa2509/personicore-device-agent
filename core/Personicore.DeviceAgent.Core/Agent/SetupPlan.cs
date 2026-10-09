namespace Personicore.DeviceAgent.Agent;

/// <summary>The three things a panelist has to do once, in the order they have to happen.</summary>
/// <remarks>
/// The order is not cosmetic. The owner comes first because nothing should be measured
/// before the panel knows whose activity it is. The certificate comes before the proxy
/// because a proxy turned on without trust is blocked by
/// <see cref="CertificateGuardProxy"/> and the panelist would be left reading an error
/// about something they were never offered.
/// </remarks>
public enum SetupStep
{
    Owners,
    Certificate,
    Proxy,
}

/// <summary>
/// One step as a window draws it. Built from a snapshot, so the assistant is a projection
/// of agent state like every other screen: it never keeps its own idea of what is done.
/// </summary>
/// <param name="Number">1-based, for "Step 2 of 3".</param>
/// <param name="ActionLabel">The primary button. Empty when the step has nothing to press.</param>
/// <param name="Done">Already satisfied — by this assistant, by the menu, or before today.</param>
/// <param name="Available">Whether the action can be taken right now.</param>
/// <param name="Status">One line under the body: what is done, or what is being waited on.</param>
public sealed record SetupStepView(
    SetupStep Step,
    int Number,
    string Name,
    string Title,
    string Body,
    string ActionLabel,
    bool Done,
    bool Available,
    string Status);

/// <summary>
/// What the setup assistant shows, decided once for both agents.
///
/// Whether a step is done is read off the snapshot and never remembered: a panelist who
/// installed the certificate from the menu last week must not be asked to install it
/// again, and one who turned the proxy off afterwards has genuinely not got a proxy on.
/// </summary>
public static class SetupPlan
{
    public static readonly SetupStep[] Steps = [SetupStep.Owners, SetupStep.Certificate, SetupStep.Proxy];

    public static int Count => Steps.Length;

    public static bool IsDone(SetupStep step, AgentSnapshot snapshot) => step switch
    {
        SetupStep.Owners => snapshot.OwnerNames.Count > 0,
        SetupStep.Certificate => snapshot.CertificateTrusted,

        // The switch, not the applied state: away from home a proxy that is on reads
        // "paused", and asking the panelist to turn on something already on would be a
        // step that can never be completed.
        SetupStep.Proxy => snapshot.ProxyWanted,
        _ => false,
    };

    public static bool AllDone(AgentSnapshot snapshot) => Steps.All(step => IsDone(step, snapshot));

    /// <summary>
    /// The first step still outstanding, or the last one when there is nothing left to do.
    /// </summary>
    public static SetupStep FirstPending(AgentSnapshot snapshot) =>
        Steps.FirstOrDefault(step => !IsDone(step, snapshot), Steps[^1]);

    /// <summary>
    /// Where the assistant opens.
    /// </summary>
    /// <remarks>
    /// Until it has been finished once it opens at step one, even when step one is already
    /// done. It is a walkthrough before it is anything else, and a panelist who registered
    /// from the menu — or from the owner dialog the agent opens by itself — would otherwise
    /// be dropped straight into "Trust Personicore's HTTPS certificate" with no idea that
    /// three steps exist or that the first one is behind them. The trail says ✓ above it,
    /// which is the whole point: they see it done rather than never seeing it.
    ///
    /// Once <see cref="AgentSnapshot.SetupCompleted"/> is recorded, re-opening it from the
    /// menu is not a walkthrough but a repair, and it opens at whatever is outstanding.
    /// </remarks>
    public static SetupStep Opening(AgentSnapshot snapshot) =>
        snapshot.SetupCompleted ? FirstPending(snapshot) : Steps[0];

    public static string Name(SetupStep step) => step switch
    {
        SetupStep.Owners => AgentText.SetupOwnersName,
        SetupStep.Certificate => AgentText.SetupCertificateName,
        SetupStep.Proxy => AgentText.SetupProxyName,
        _ => string.Empty,
    };

    public static SetupStepView Describe(SetupStep step, AgentSnapshot snapshot, PlatformWords words)
    {
        var done = IsDone(step, snapshot);
        var available = IsAvailable(step, snapshot);

        return new SetupStepView(
            Step: step,
            Number: Array.IndexOf(Steps, step) + 1,
            Name: Name(step),
            Title: step switch
            {
                SetupStep.Owners => AgentText.SetupOwnersTitle,
                SetupStep.Certificate => AgentText.SetupCertificateTitle,
                SetupStep.Proxy => AgentText.SetupProxyTitle(snapshot.ProxyWanted),
                _ => string.Empty,
            },
            Body: step switch
            {
                SetupStep.Owners => AgentText.SetupOwnersBody(words),
                SetupStep.Certificate => AgentText.SetupCertificateBody(words),
                SetupStep.Proxy => AgentText.SetupProxyBody(words),
                _ => string.Empty,
            },
            // A done step still has a button wherever pressing it does something. Who uses a
            // computer changes, and a proxy a panelist turned on has to be undoable from the
            // same screen that turned it on. The certificate is the exception and the reason
            // is the administrator prompt: installing an already-trusted CA spends a password
            // on nothing, which is also why the menu item reports instead of offering.
            ActionLabel: step switch
            {
                SetupStep.Owners => done ? AgentText.SetupOwnersActionDone : AgentText.SetupOwnersAction,
                SetupStep.Certificate => done ? string.Empty : AgentText.InstallCertificate,
                SetupStep.Proxy => done ? AgentText.SetupProxyActionOff : AgentText.SetupProxyAction,
                _ => string.Empty,
            },
            Done: done,
            Available: available,
            Status: Status(step, snapshot, words, done, available));
    }

    /// <summary>
    /// Whether the step's button does anything if pressed now.
    /// </summary>
    /// <remarks>
    /// All three wait on the meter node, and that is the same
    /// <see cref="AgentSnapshot.ActionsUnlocked"/> rule the menu obeys — the assistant is
    /// not a way around it. The certificate is included deliberately: installing it is
    /// local and would work, but it costs an administrator prompt, and asking for one on a
    /// network where the rest of the setup cannot continue is a password spent for nothing.
    /// </remarks>
    private static bool IsAvailable(SetupStep step, AgentSnapshot snapshot) => step switch
    {
        SetupStep.Owners => snapshot.ActionsUnlocked && snapshot.CanRegister,

        // Switching it off is allowed with no node, exactly as in the menu: the lock is
        // there to stop a panelist starting something that cannot work, never to trap them
        // in it. Away from home the proxy pauses itself, and this is how they turn it off.
        SetupStep.Proxy => snapshot.ActionsUnlocked || snapshot.ProxyWanted,
        _ => snapshot.ActionsUnlocked,
    };

    private static string Status(
        SetupStep step, AgentSnapshot snapshot, PlatformWords words, bool done, bool available)
    {
        // The proxy says what it is actually doing, which is more than "done": on, or on
        // but paused away from home, or refused by the system.
        if (step == SetupStep.Proxy && done) return AgentText.DescribeProxy(snapshot, words);

        if (done)
        {
            return step switch
            {
                SetupStep.Owners => AgentText.SetupOwnersDone(snapshot.OwnerNames),
                SetupStep.Certificate => AgentText.SetupCertificateDone,
                _ => string.Empty,
            };
        }

        // Not done and nothing to press: say what is being waited on rather than leaving a
        // disabled button with no explanation, which reads as a broken window.
        if (!available)
        {
            return snapshot.Status is AgentStatus.HouseholdNotAssigned or AgentStatus.BackendNotReady
                ? snapshot.Detail
                : AgentText.WaitingForNode;
        }

        return AgentText.SetupSkippable;
    }
}
