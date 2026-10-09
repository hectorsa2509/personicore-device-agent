using System.Net;
using System.Net.Sockets;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Device;
using Personicore.DeviceAgent.Graph;
using Personicore.DeviceAgent.Net;
using Personicore.DeviceAgent.Router;

namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// The agent's cycle, end to end:
///
///   1. find the meter node on the LAN
///   2. ask it for the credential and the GraphQL endpoint (get_config.sh)
///   3. resolve household and panelists from the node's MAC
///   4. find out which IP and which MAC the node sees us with
///   5. if the owner of this computer is already known → re-assert the assignment
///      if not                                        → ask the panelist for the owner
///
/// Step 5 repeats on every cycle on purpose: the assignment is indexed by IP in the
/// backend, and the ISP hands out the IP, so a new lease means it has to be sent again.
/// That is why this is a resident agent and not an installer that runs once.
/// </summary>
public sealed class AgentWorker : IDisposable
{
    private readonly AgentConfig _config;
    private readonly EnvSettings _env;
    private readonly AgentState _state;
    private readonly RouterClient _routerClient;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly IDeviceProfileProvider _profiles;
    private readonly IProxySwitch? _proxy;

    private DeviceProfile _profile = DeviceProfile.Unknown;
    private RouterNode? _lastNode;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _proxyLoop;
    private Task? _ipLoop;

    /// <summary>
    /// The address the watcher last saw. Not <c>_state.LastIp</c>, which is the last one
    /// *reported*: comparing against that would wake the cycle every half minute for as
    /// long as reporting is failing, which is exactly the backoff
    /// <see cref="BackendNotReadyException"/> exists to provide. This wakes once per change.
    /// </summary>
    private string _lastSeenIp = string.Empty;
    private int _proxyMissing;
    private TaskCompletionSource<bool>? _wake;
    private int _wakePending;

    /// <summary>
    /// The household and computer the owner dialog was last opened for, as
    /// "routerMac|machineId".
    /// </summary>
    /// <remarks>
    /// The router is half of the key and that is the whole of a house move. The machineId is
    /// this computer's MAC, which does not change when it is carried to another home, so a key
    /// made of it alone said "already asked" in the one situation where the question has to be
    /// asked again: a panelist who moves has a node whose MAC is unknown to their saved owner,
    /// the cycle publishes OwnerRequired, and the dialog never opened. Revalidating a node that
    /// turned out to be a different one cleared this by hand, which covered only the case where
    /// the new node answers at the old one's address — not the ordinary one, where the old node
    /// is simply gone and the new one is discovered.
    /// </remarks>
    private string _promptedFor = string.Empty;
    private bool _warnedNotAssigned;

    /// <summary>
    /// Whether a <see cref="BackendNotReadyException"/> has already been reported. Logged
    /// on the transition only, like <see cref="_warnedNotAssigned"/>, and cleared by the
    /// first cycle that gets through.
    /// </summary>
    private bool _warnedNotReady;

    /// <summary>The OS trust check, or null when the platform did not supply one.</summary>
    private readonly Func<bool>? _certificateTrusted;

    /// <summary>
    /// The node address the cycle last handed to the proxy, already checked for being
    /// reachable (see <see cref="ResolveNodeIpAsync"/>). The switch uses this and not the
    /// node's own answer: a node that reports its WAN address would otherwise be written
    /// into the system proxy the moment the panelist clicks, and the click is precisely
    /// the moment there is no time to go and probe it.
    /// </summary>
    private string? _proxyNodeIp;

    /// <summary>
    /// Said once per arrival on a network whose node is not this computer's, not once every
    /// cycle: it is the kind of line somebody reads when a panelist says the proxy will not
    /// come on, and a copy an hour buries it.
    /// </summary>
    private bool _warnedForeignNode;

    /// <summary>
    /// Whether the last cycle found a node that is not this computer's. Carried so that the
    /// switch's own status line says so the moment it is clicked, instead of reading
    /// "no meter node on this network" until the next cycle corrects it.
    /// </summary>
    private bool _foreignNodePause;

    /// <summary>
    /// IPInput fields each backend has refused, remembered for as long as the agent runs.
    ///
    /// A new API client is built every cycle, so without this every report would
    /// rediscover the same refusals at the cost of a failed mutation each. It is keyed by
    /// endpoint because a refusal is that backend's answer, not a fact about the world:
    /// the node can advertise a different endpoint mid-session, and one that supports the
    /// field would otherwise never be given the chance to say so.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _refusedFields = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> RefusedBy(string endpoint)
    {
        var key = endpoint.Trim().TrimEnd('/');

        if (!_refusedFields.TryGetValue(key, out var fields))
            _refusedFields[key] = fields = new HashSet<string>(StringComparer.Ordinal);

        return fields;
    }

    /// <param name="profiles">How this OS reads brand, model, type and OS.</param>
    /// <param name="proxy">The proxy switch, or null where the platform does not have it yet.</param>
    /// <param name="certificateTrusted">
    /// Asks the operating system whether Personicore's HTTPS certificate is trusted here. The
    /// check is per-platform and lives in each UI project, so it arrives as a function; the
    /// result is cached on the snapshot because the check costs a process on macOS.
    /// </param>
    public AgentWorker(
        AgentConfig config, EnvSettings env, AgentState state,
        IDeviceProfileProvider profiles, IProxySwitch? proxy,
        Func<bool>? certificateTrusted = null)
    {
        _config = config;
        _env = env;
        _state = state;
        _routerClient = new RouterClient(config);
        _profiles = profiles;
        _proxy = proxy;
        _certificateTrusted = certificateTrusted;
        Snapshot = new AgentSnapshot { OwnerIds = state.PanelistIds, OwnerNames = state.PanelistNames };
    }

    public AgentSnapshot Snapshot { get; private set; }

    /// <summary>
    /// Read everything, send nothing. Two independent reasons put the agent here, and
    /// either one is enough: `sendToBackend: false` in the configuration, which is the
    /// default while the device/IP contract is being rewritten, or PERSONICORE_DRY_RUN
    /// in the .env, which is the per-machine way to test without touching production.
    ///
    /// It does not mean "do less": the node is still found, the household and panelists
    /// are still read from GraphQL, the owner is still asked for and the requests are
    /// still built. Only the POST is replaced by a line in the desktop file.
    /// </summary>
    public bool Offline => _env.DryRun || !_config.SendToBackend;

    /// <summary>
    /// A developer exercising the agent away from a household (PERSONICORE_DRY_RUN).
    /// Only this mode is allowed to carry on without a meter node; not sending is a
    /// different decision, and on a panelist's computer no node still means no household.
    /// </summary>
    private bool DryRun => _env.DryRun;

    /// <summary>Raised on every state change; the tray redraws from this.</summary>
    public event Action<AgentSnapshot>? SnapshotChanged;

    /// <summary>Raised when the household is resolved but nobody has said whose computer this is.</summary>
    public event Action<AgentSnapshot>? OwnerRequired;

    /// <summary>
    /// Raised when the agent *enters* HouseholdNotAssigned, not on every cycle: the retry
    /// runs every minute, and telling a panelist every minute about something they cannot
    /// fix is harassment, not information. It re-arms once the panel has the assignment.
    /// </summary>
    public event Action<AgentSnapshot>? HouseholdNotAssigned;

    public void Start()
    {
        Log.Info(Offline
            ? $"Reading only, sending nothing ({(_env.DryRun ? "PERSONICORE_DRY_RUN" : "sendToBackend is off")}). "
              + $"What would be sent is written to {Outbox.FilePath}"
            : "Sending to the backend is enabled.");

        _profile = WithSavedCorrections(_profiles.Read());
        Publish(Snapshot with { Profile = _profile });

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _proxyLoop = Task.Run(() => ProxyWatchLoopAsync(_cts.Token));
        _ipLoop = Task.Run(() => IpWatchLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Brings the next cycle forward ("Refresh now", a network change). If a cycle is
    /// already running there is nothing to wake, so the request is recorded instead:
    /// otherwise the click would be lost without the panelist noticing.
    /// </summary>
    /// <remarks>
    /// Waking up is not reporting. A cycle brought forward still says nothing to the
    /// panel unless it has something new to say: reporting on demand belongs to the
    /// owner dialog, where the panelist sees what is being sent before it goes.
    /// </remarks>
    public void WakeUp()
    {
        // A network change or a "Refresh now" are exactly the signals that the node may
        // be back: the proxy watchdog's backoff drops to its minimum again.
        Interlocked.Exchange(ref _proxyMissing, 0);
        Interlocked.Exchange(ref _wakePending, 1);
        Interlocked.Exchange(ref _wake, null)?.TrySetResult(true);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The failure wait, unless the cycle earns the long one. A refusal that no
            // retry can fix earns it too — see below.
            var wait = TimeSpan.FromSeconds(_config.RetryIntervalSeconds);

            try
            {
                if (await RunCycleAsync(ct).ConfigureAwait(false))
                {
                    wait = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
                    _warnedNotReady = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (DeviceOnAnotherNodeException ex)
            {
                // The panel has this computer on another router: the panelist moved, or support
                // recreated the node record. The wait is the long one for the same reason as
                // below — no retry moves a device between routers — but the line the panelist
                // reads is not "Personicore is not ready for this computer", which would send
                // them, and support, looking at a deployment.
                wait = TimeSpan.FromSeconds(_config.PollIntervalSeconds);

                if (!_warnedNotReady)
                {
                    _warnedNotReady = true;
                    Log.Error("This computer is registered on another meter node", ex);
                }

                Publish(Snapshot with
                {
                    Status = AgentStatus.BackendNotReady,
                    Detail = AgentText.DeviceOnAnotherNode,
                });
            }
            catch (BackendNotReadyException ex)
            {
                // The backend answered, and the answer will not change in a minute: it
                // refused who signed the request, or it has never heard of the operation.
                // Either way this waits the full poll interval and says so once — on the
                // retry cadence it is a stack trace a minute, for days, burying everything
                // else in the log the one time somebody comes to read it.
                wait = TimeSpan.FromSeconds(_config.PollIntervalSeconds);

                if (!_warnedNotReady)
                {
                    _warnedNotReady = true;
                    Log.Error("The backend cannot accept this registration yet", ex);
                }

                Publish(Snapshot with
                {
                    Status = AgentStatus.BackendNotReady,
                    Detail = AgentText.BackendNotReady,
                });
            }
            catch (Exception ex)
            {
                Log.Error("Agent cycle failed", ex);
                Publish(Snapshot with { Status = AgentStatus.Error, Detail = ex.Message });
            }

            await SleepAsync(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Interruptible wait: WakeUp cuts it short without waiting for the next poll.</summary>
    private async Task SleepAsync(TimeSpan duration, CancellationToken ct)
    {
        var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _wake, wake);

        if (Interlocked.Exchange(ref _wakePending, 0) == 1)
        {
            Interlocked.Exchange(ref _wake, null);
            return;
        }

        using var timer = new CancellationTokenSource(duration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        using var _ = linked.Token.Register(() => wake.TrySetResult(false));

        await wake.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // WakeUp both records the request and wakes: if it is not consumed here the next
        // cycle would find it pending, and every click would run two cycles.
        Interlocked.Exchange(ref _wakePending, 0);
    }

    /// <summary>
    /// Re-asks the operating system whether the HTTPS certificate is trusted, and publishes
    /// the answer if it changed.
    /// </summary>
    /// <remarks>
    /// Public because installing it is the UI's job and the result is immediate: without this,
    /// the menu would go on offering "Install HTTPS certificate…" until the next cycle, after
    /// the panelist had just installed it.
    /// </remarks>
    public void RefreshCertificateTrust()
    {
        if (_certificateTrusted is null) return;

        bool trusted;
        try
        {
            trusted = _certificateTrusted();
        }
        catch (Exception ex)
        {
            // A check that cannot run is not a certificate that is untrusted, but it is the
            // safer thing to show: the menu keeps offering the install, which is harmless when
            // it is already there (both installers return early if trust is already in place).
            Log.Debug($"Could not check HTTPS certificate trust: {ex.Message}");
            trusted = false;
        }

        if (trusted == Snapshot.CertificateTrusted) return;

        // Only on a change, which is rare: it takes an administrator password to alter. Worth a
        // line because "is the certificate trusted here?" is a support question, and the answer
        // is otherwise only visible in a menu nobody can see over the phone.
        Log.Info(trusted
            ? "Personicore's HTTPS certificate is trusted on this computer."
            : "Personicore's HTTPS certificate is not trusted on this computer.");

        Publish(Snapshot with { CertificateTrusted = trusted });
    }

    /// <summary>
    /// Records that the panelist has been through the setup assistant, so it stops opening
    /// itself on start-up.
    /// </summary>
    /// <remarks>
    /// Called by the UI when Finish is pressed — not when the window is closed, and not
    /// when the three steps happen to be satisfied: this is the panelist saying they are
    /// done with the assistant, which is a different fact from the steps being complete.
    /// Saved with <see cref="AgentState.Save"/> and never with <c>SaveProxy</c>: it is not
    /// a proxy field, and the watchdog has no business with it.
    /// </remarks>
    public void MarkSetupDone()
    {
        if (_state.SetupCompletedAt is not null) return;

        _state.SetupCompletedAt = DateTimeOffset.Now;
        _state.Save();
        Log.Info("Setup assistant finished by the panelist.");

        // Republish so the menu and any open window stop treating this as a first run.
        Publish(Snapshot);
    }

    private async Task<bool> RunCycleAsync(CancellationToken ct)
    {
        await _cycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Once per cycle, off the UI thread. Hourly is often enough for something only a
            // person with an administrator password can change.
            RefreshCertificateTrust();

            using var session = await ConnectAsync(ct).ConfigureAwait(false);
            if (session is null) return false;

            var identity = await ResolveIdentityAsync(session.Node, ct).ConfigureAwait(false);
            if (identity is null)
            {
                Log.Warn("Could not identify this computer on the home network: neither the node's "
                         + "client list nor the local route gave an IP and MAC for it.");

                Publish(Snapshot with
                {
                    Status = AgentStatus.Error,
                    NodeFound = session.Node is not null,
                    Detail = "Could not identify this computer on the home network.",
                });
                return false;
            }

            var snapshot = Snapshot with
            {
                Offline = Offline,
                NodeFound = session.Node is not null,
                RouterBaseUrl = session.Node?.BaseUrl ?? string.Empty,
                RouterMac = session.Household.RouterMac,
                RouterId = session.Household.RouterId,
                HouseholdId = session.Household.HouseholdId,
                LocalIp = identity.Ip,
                Mac = identity.Mac,
                MachineId = identity.MachineId,
                MacIsRandomized = LocalNetwork.IsRandomizedMac(identity.Mac),
                InterfaceName = identity.InterfaceName,
                Profile = _profile,
                Panelists = session.Household.Panelists,
                DeviceOsAccepted = session.Api?.DeviceOsAccepted ?? _config.SendDeviceOs,
            };

            // With no panelists there is nobody to assign the computer to and the owner
            // dialog would open empty; with no router id there is nothing for
            // routerIpsAssigment to key the assignment on. Both are the same dead end as a
            // router that was never set up, and neither is fixable from this computer.
            // Gated on having actually reached the backend, not on whether we intend to
            // send: with no API there is no answer to believe, and the owner is typed by
            // hand instead. With an API, an empty household is an answer, and the panelist
            // gets told the same thing whether or not sending is switched on.
            if (session.Api is not null
                && (session.Household.Panelists.Count == 0
                    || string.IsNullOrWhiteSpace(session.Household.RouterId)))
            {
                // Which of the two is missing decides what the panelist is told: with people
                // but no router id, saying "no people registered" would send support looking
                // in the wrong place.
                PublishNotAssigned(snapshot, session.Household.Panelists.Count == 0
                    ? AgentText.HouseholdHasNoPanelists
                    : AgentText.RouterNotInPanel);
                return false;
            }

            _warnedNotAssigned = false;

            if (!HasUsableOwner(session.Household.RouterMac, identity.MachineId, session.Household.Panelists))
            {
                Publish(snapshot with
                {
                    Status = AgentStatus.OwnerRequired,
                    Detail = "Owner of this computer not set yet.",
                    OwnerIds = [],
                    OwnerNames = [],
                    PrincipalOwnerId = string.Empty,
                });

                PromptForOwner(identity.MachineId, session.Household.RouterMac);

                return true;
            }

            var registration = BuildRegistration(
                identity,
                _state.PanelistIds,
                _state.FriendlyName,
                _state.PrincipalPanelistId,
                _state.PanelistNames.Count);

            var registered = snapshot with
            {
                OwnerIds = _state.PanelistIds,
                OwnerNames = _state.PanelistNames,
                PrincipalOwnerName = PrincipalName(_state.PanelistIds, _state.PanelistNames),
                PrincipalOwnerId = _state.PrincipalPanelistId,
                DeviceId = DeviceIdStore.Read(),
            };

            var reason = ReportReason(session, registration, registered.DeviceId);

            if (reason is null)
            {
                // Nothing new to say. The cycle still ran — node, household and owner were
                // all revalidated — it simply had no reason to speak to the backend.
                Log.Debug($"Nothing changed since the last report (ip={identity.Ip}); " +
                          (NextReportDue() is { } due
                              ? $"next one due {due:HH:mm}."
                              : "the next one happens when something changes."));

                Publish(registered with
                {
                    Status = Offline ? AgentStatus.Simulated : AgentStatus.Registered,
                    Detail = Offline
                        ? $"Simulated as {registration.FriendlyName} · written to the desktop"
                        : $"Registered as {registration.FriendlyName}",
                    LastRegisteredAt = _state.LastRegisteredAt,
                    NextReportDue = NextReportDue(),
                });

                return true;
            }

            Log.Info($"Reporting to the panel ({reason}): ip={identity.Ip}");

            try
            {
                if (!await SendAsync(session, registered, registration, _state.PanelistNames, ct).ConfigureAwait(false))
                {
                    Publish(registered with
                    {
                        Status = AgentStatus.Error,
                        Detail = "Nothing was sent: check registrationMode and the log.",
                    });
                    return false;
                }
            }
            catch (DeviceRecordPendingException ex)
            {
                // The address and the owners went through; the device's own record did not.
                // Caught here rather than left to the loop, because the loop's handler
                // publishes "Personicore is not ready for this computer" — which, with this
                // computer assigned to its panelists and its address current, is false in the
                // one line the panelist reads.
                //
                // The fingerprint is not advanced, so the device write is still owed. The wait
                // is the long one all the same: the refusal will not have changed in a minute.
                Log.Warn(ex.Message);

                Publish(registered with
                {
                    Status = Offline ? AgentStatus.Simulated : AgentStatus.Registered,
                    Detail = AgentText.DeviceRecordPending(_state.PanelistNames),
                    LastRegisteredAt = _state.LastRegisteredAt,
                    NextReportDue = NextReportDue(),
                });

                return true;
            }

            RememberRegistration(session, identity, registration, _state.PanelistNames);

            Publish(registered with
            {
                DeviceOsAccepted = session.Api?.DeviceOsAccepted ?? _config.SendDeviceOs,
                Status = Offline ? AgentStatus.Simulated : AgentStatus.Registered,
                Detail = Offline
                    ? $"Simulated as {registration.FriendlyName} · written to the desktop"
                    : $"Registered as {registration.FriendlyName}",
                LastRegisteredAt = _state.LastRegisteredAt,
                DeviceId = DeviceIdStore.Read(),
                NextReportDue = NextReportDue(),
            });

            return true;
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    /// <summary>
    /// The node's own identity MAC for this session, which is what every probe compares
    /// against. Falls back to the router record's MAC when there is no node (dry run only).
    /// </summary>
    private static string NodeIdentity(Session session) =>
        session.Node?.Info.IdentityMac is { Length: > 0 } reported ? reported : session.Household.RouterMac;

    /// <summary>Which question has already been asked: this computer, in this household.</summary>
    private static string PromptKey(string routerMac, string machineId) =>
        LocalNetwork.NormalizeMac(routerMac) + "|" + machineId;

    internal void PromptForOwner(string machineId, string routerMac)
    {
        if (!_config.PromptOnFirstRun || _promptedFor == PromptKey(routerMac, machineId)) return;
        Log.Info($"Owner not set for this computer (machineId={machineId}, router={routerMac}); asking the panelist.");
        _promptedFor = PromptKey(routerMac, machineId);
        OwnerRequired?.Invoke(Snapshot);
    }

    /// <summary>
    /// Registers the computer with the owners picked in the dialog. It runs its own full
    /// cycle rather than depend on a cached credential: the JWT is served by the node and
    /// may have rotated since the last poll.
    /// </summary>
    /// <param name="principalPanelistId">
    /// Who the computer is mainly for, when several people share it. Ignored with a
    /// single owner, who is the principal by definition.
    /// </param>
    public async Task RegisterAsync(
        IReadOnlyList<Panelist> owners,
        string friendlyName,
        DeviceProfile profile,
        string principalPanelistId,
        CancellationToken ct)
    {
        if (owners.Count == 0)
            throw new InvalidOperationException("Select at least one panelist.");

        await _cycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var session = await ConnectAsync(ct).ConfigureAwait(false);
            if (session is null)
            {
                // ConnectAsync already published why. If the node answered and the panel is
                // simply not ready, saying "could not reach the meter node" would send the
                // panelist to look at a router that is working fine.
                throw new InvalidOperationException(Snapshot.Status == AgentStatus.HouseholdNotAssigned
                    ? Snapshot.Detail
                    : "Could not reach the household meter node.");
            }

            var identity = await ResolveIdentityAsync(session.Node, ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("Could not identify this computer on the network.");

            _profile = profile;

            // The dialog was filled from a household read before it opened, and it stays
            // open as long as the panelist needs. By now the computer may be on another
            // network entirely, or the household may have emptied, so the answer is
            // checked against the household this cycle just read — never the one that
            // populated the dialog.
            if (session.Api is not null)
            {
                // Same gate as the cycle: a real answer with nobody to assign to, or no
                // router id to key the assignment on, blocks the submission instead of
                // letting the dialog's stale owners through.
                if (session.Household.Panelists.Count == 0
                    || string.IsNullOrWhiteSpace(session.Household.RouterId))
                {
                    throw new InvalidOperationException(session.Household.Panelists.Count == 0
                        ? AgentText.HouseholdHasNoPanelists
                        : AgentText.RouterNotInPanel);
                }

                var members = session.Household.Panelists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                var strangers = owners.Where(o => !members.Contains(o.Id)).Select(o => o.DisplayName).ToList();

                if (strangers.Count > 0)
                    throw new InvalidOperationException(AgentText.OwnersNotInHousehold(strangers));

                // The principal has to be one of the owners that survived the check.
                if (!string.IsNullOrWhiteSpace(principalPanelistId)
                    && owners.All(o => o.Id != principalPanelistId))
                {
                    throw new InvalidOperationException(AgentText.OwnersNotInHousehold([principalPanelistId]));
                }
            }

            var ids = owners.Select(o => o.Id).ToList();
            var names = owners.Select(o => o.DisplayName).ToList();
            var registration = BuildRegistration(identity, ids, friendlyName, principalPanelistId, names.Count);

            var snapshot = Snapshot with
            {
                Offline = Offline,
                NodeFound = session.Node is not null,
                RouterBaseUrl = session.Node?.BaseUrl ?? string.Empty,
                RouterMac = session.Household.RouterMac,
                RouterId = session.Household.RouterId,
                HouseholdId = session.Household.HouseholdId,
                LocalIp = identity.Ip,
                Mac = identity.Mac,
                MachineId = identity.MachineId,
                MacIsRandomized = LocalNetwork.IsRandomizedMac(identity.Mac),
                InterfaceName = identity.InterfaceName,
                Profile = profile,
                Panelists = session.Household.Panelists,
                OwnerIds = ids,
                OwnerNames = names,
                PrincipalOwnerName = PrincipalName(ids, names),
                PrincipalOwnerId = registration.PrincipalPanelistId,
                DeviceOsAccepted = session.Api?.DeviceOsAccepted ?? _config.SendDeviceOs,
            };

            try
            {
                if (!await SendAsync(session, snapshot, registration, names, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Nothing was sent: check registrationMode and the log.");
            }
            catch (DeviceRecordPendingException ex)
            {
                // The owners and the address reached the panel; only the device's own record
                // did not. So the answer is kept — discarding it would reopen this dialog on
                // the next cycle, and the one after, asking the same question for as long as
                // the backend stays as it is, while changing nothing — and the fingerprint is
                // deliberately left alone, so the device write is still owed and still
                // retried.
                //
                // No exception out of here: for the panelist this succeeded. Their computer
                // is assigned to the people they picked and its address is current. The
                // outstanding record is support's, and raising an error over it would ask them
                // to redo the part that worked.
                Log.Warn(ex.Message);
                RememberOwner(session, identity, registration, names);
                _promptedFor = PromptKey(session.Household.RouterMac, identity.MachineId);

                Publish(snapshot with
                {
                    Status = Offline ? AgentStatus.Simulated : AgentStatus.Registered,
                    Detail = AgentText.DeviceRecordPending(names),
                    LastRegisteredAt = _state.LastRegisteredAt,
                });

                return;
            }
            catch (BackendNotReadyException ex)
            {
                // Nothing landed at all. The owner is still worth keeping for the same
                // reason, but this is not a registration and must not read as one.
                //
                // A computer the panel holds against another router says so instead: the
                // panelist has just picked the people in this household, and telling them
                // Personicore "is not ready" hides the one thing that would let them —or
                // support— recognise what happened.
                var detail = ex is DeviceOnAnotherNodeException
                    ? AgentText.DeviceOnAnotherNode
                    : AgentText.BackendNotReady;

                RememberOwner(session, identity, registration, names);
                _promptedFor = PromptKey(session.Household.RouterMac, identity.MachineId);

                Publish(snapshot with
                {
                    Status = AgentStatus.BackendNotReady,
                    Detail = detail,
                });

                throw new InvalidOperationException(detail);
            }

            RememberRegistration(session, identity, registration, names);

            _promptedFor = PromptKey(session.Household.RouterMac, identity.MachineId);

            Publish(snapshot with
            {
                DeviceOsAccepted = session.Api?.DeviceOsAccepted ?? _config.SendDeviceOs,
                Status = Offline ? AgentStatus.Simulated : AgentStatus.Registered,
                Detail = Offline
                    ? $"Simulated as {registration.FriendlyName} · written to the desktop"
                    : $"Registered as {registration.FriendlyName}",
                LastRegisteredAt = _state.LastRegisteredAt,
            });
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    /// <summary>
    /// Sends according to the configured mode. `personalDevice` needs a household id; if
    /// the backend does not expose one it is logged instead of failing the whole cycle,
    /// because the router assignment has already recorded the data.
    /// </summary>
    /// <returns>
    /// true when the registration actually went somewhere — to the backend, or to the
    /// desktop file when sending is held. False means nothing left this computer, and
    /// the caller must not record it as delivered: doing so publishes "Registered" over
    /// a registration that never happened, and then suppresses the retry because the
    /// fingerprint says it was already sent.
    /// </returns>
    private async Task<bool> SendAsync(
        Session session,
        AgentSnapshot snapshot,
        DeviceRegistration registration,
        IReadOnlyList<string> ownerNames,
        CancellationToken ct)
    {
        var mode = _config.RegistrationMode.Trim().ToLowerInvariant();

        // Household may have been read from the API, but sending needs the .env endpoint.
        if (Offline || session.Api is null)
        {
            RecordOffline(session, snapshot, registration, ownerNames, mode);
            return true;
        }

        var assigned = false;

        if (mode is "routerips" or "both" or "deviceandrouterips")
        {
            await session.Api.AssignAsync(session.Household.RouterId, registration, ct).ConfigureAwait(false);
            assigned = true;
        }

        if (mode is "device" or "deviceandip" or "deviceandrouterips")
        {
            // Deliberately not wrapped the way `both` wraps personalDevice. There, the
            // router assignment had already recorded the data and the personal device was
            // a bonus; here the device write *is* the record, and the only call that comes
            // back with the device's own id. Letting it fail quietly would publish
            // "Registered" over nothing and then sit on the retry for twelve hours.
            var deviceId = await SendDeviceAsync(session, registration, assigned, ct).ConfigureAwait(false);

            // After the identity write, never before: presenceReportDeviceIp attaches a Local IP
            // Period to an existing subject and creates nothing, so the order is the contract's,
            // not a preference.
            if (mode is "deviceandip")
                await ReportAddressAsync(session, registration, deviceId, ct).ConfigureAwait(false);

            return true;
        }

        if (mode is not ("personaldevice" or "both"))
        {
            // Sanitize rejects unknown modes, so this is only reachable if that check and
            // this one ever drift apart. Silence would be the expensive failure here.
            if (!assigned) Log.Error($"registrationMode \"{mode}\" sends nothing; the computer was not registered.");
            return assigned;
        }

        if (string.IsNullOrWhiteSpace(session.Household.HouseholdId))
        {
            var message = "registrationMode requests personalDevice but the backend returned no householdId";

            // In "both" the router assignment already carried the data, so this is a
            // note. On its own, nothing was sent and it must not look like success.
            if (assigned) Log.Warn(message + "; skipping the personal device.");
            else Log.Error(message + "; the computer was not registered.");

            return assigned;
        }

        try
        {
            // Throws when the backend answers without a device: nothing to record, and
            // nothing to call a registration.
            var created = await session.Api
                .CreatePersonalDeviceAsync(session.Household.HouseholdId, registration, ct)
                .ConfigureAwait(false);

            // The identifier the panel gives this computer the first time. Kept outside
            // state.json and never replaced once stored — see DeviceIdStore.
            DeviceIdStore.SaveIfAbsent(created.Id);
            return true;
        }
        catch (GraphQlException ex) when (mode == "both")
        {
            Log.Warn($"presenceCreatePersonalDevice failed; the router assignment was still saved: {ex.Message}");
            return assigned;
        }
    }

    /// <summary>
    /// Records this computer as a Device through <c>presenceCreateDevice</c>: a create the
    /// first time, an update once an id is stored.
    /// </summary>
    /// <remarks>
    /// The order here is the part that matters. The idempotency key is minted from the body
    /// that is about to go out and written to disk <i>before</i> the request leaves, because
    /// this contract keys retries on that key alone — nothing is keyed on
    /// <c>machineId</c>. A create that is accepted but whose answer never comes back (a
    /// timeout, a lid closing, Wi-Fi dropping) can then be reclaimed on the next cycle by
    /// resending the same key, and the server hands back the row it already made. Mint a
    /// fresh key there instead and the household has two Devices for one computer, which is
    /// the single failure the device id exists to prevent.
    /// </remarks>
    /// <param name="addressAlreadySent">
    /// Whether <c>routerIpsAssigment</c> has already succeeded this cycle. It decides how a
    /// refusal is *described*, not whether it is one: with the address and the owners
    /// already in the panel, "Personicore is not ready for this computer" is false where it
    /// matters most — in the one line the panelist reads.
    /// </param>
    /// <returns>The device's id, or empty when the backend has not issued one yet.</returns>
    private async Task<string> SendDeviceAsync(
        Session session, DeviceRegistration registration, bool addressAlreadySent, CancellationToken ct)
    {
        var stored = DeviceIdStore.ReadRecord();
        var here = NodeIdentity(session);

        // The id names a Device row in the household of the node that issued it, and this is
        // not that node. Nothing is sent: what the backend does with a deviceId under another
        // routerId is unverified, and two of the three possible answers lose something — the
        // row is re-homed into this household, or "the device doesn't exist!" comes back and
        // the branch below is entitled to let the id go and create again, which grows an
        // orphaned row in the household the computer returns to. Refusing is the only answer
        // that cannot lose anything, and the panelist is told what it is.
        if (stored.DeviceId.Length > 0 && !stored.BelongsToNode(here) && !_config.AllowNodeChange)
        {
            throw Refuse(
                $"This computer's device id was issued on node {stored.IssuedBy} and the node here is "
                + $"{here}; nothing was sent. Moving a Device between nodes is the panel's to do — see "
                + "§7 of docs/GRAPHQL-CONTRACT.md — and allowNodeChange is the switch that lets the "
                + "write go out anyway.");
        }

        // Which refusal this is depends on what already landed, exactly as in Write below: with
        // the address and the owners in the panel, the only thing outstanding is the Device's own
        // record, and the panelist must not be told their registration failed.
        BackendNotReadyException Refuse(string message) => addressAlreadySent
            ? new DeviceRecordPendingException(message)
            : new DeviceOnAnotherNodeException(message);

        var deviceId = stored.DeviceId;
        var requestId = _state.RequestIdFor(DeviceWriteFingerprint(session, registration, deviceId));

        PresenceDevicePayload payload;
        try
        {
            payload = await Write(deviceId, requestId).ConfigureAwait(false);
        }
        catch (DeviceGoneException ex) when (deviceId.Length > 0)
        {
            // Authoritative only where the row was made. Asked on another node, "it does not
            // exist" says nothing about the household that holds it — it is the answer a
            // household-scoped lookup gives for somebody else's id — and letting the id go on
            // it is how one computer becomes two rows per home. Only reachable with
            // allowNodeChange on, which is why that switch cannot cost an identifier.
            if (!stored.BelongsToNode(here))
            {
                throw Refuse(
                    $"Node {here} has no device under the id issued on {stored.IssuedBy}, which is what a "
                    + "household-scoped lookup answers for an id from another household. The id is kept. "
                    + $"Backend said: {ex.Message}");
            }

            // The panel has no device under the stored id, so the update had no target. Let the
            // id go and create instead — in this cycle, not the next: on the manual path the
            // panelist is waiting, and telling them it failed when the fix is one call away
            // would have them click Register again to watch the same thing happen.
            //
            // Clearing the id also changes the write's fingerprint, so RequestIdFor mints a
            // fresh key on its own: the old one belongs to the row that was deleted, and
            // Device.requestId carries a global unique index.
            Log.Warn(ex.Message);
            DeviceIdStore.Forget();

            deviceId = string.Empty;
            payload = await Write(deviceId, _state.RequestIdFor(
                DeviceWriteFingerprint(session, registration, deviceId))).ConfigureAwait(false);
        }

        async Task<PresenceDevicePayload> Write(string id, string key)
        {
            try
            {
                return await session.Api!
                    .CreateDeviceAsync(session.Household.RouterId, registration, key, id, ct)
                    .ConfigureAwait(false);
            }
            catch (BackendNotReadyException ex) when (addressAlreadySent)
            {
                throw new DeviceRecordPendingException(ex.Message);
            }
        }

        if (deviceId.Length == 0)
        {
            try
            {
                DeviceIdStore.SaveIfAbsent(payload.DeviceId, here);
            }
            catch (InvalidOperationException ex)
            {
                // A slot was filled between the read above and here. The stored id wins:
                // it is the one the panel already has rows against.
                Log.Warn($"Keeping the device id already stored; the backend answered with another one. {ex.Message}");
            }
        }
        else if (!string.Equals(deviceId, payload.DeviceId, StringComparison.Ordinal))
        {
            // Logged, never acted on: the agent asked to update a specific device and was
            // told about a different one. Replacing the stored id would move this computer
            // onto that row and orphan its own.
            Log.Warn($"presenceCreateDevice answered with deviceId {payload.DeviceId} for an update of "
                     + $"{deviceId}; the stored id is kept.");
        }

        // An id from before the issuer was written down: the backend has just accepted a write
        // for it from this node, which is the only evidence of where it lives that this computer
        // can ever have. Records it once, and never over an issuer already there.
        if (stored.IssuedBy.Length == 0) DeviceIdStore.RememberIssuer(here);

        // What is on disk, not what the answer said: if a slot refused the write, the report has
        // to use the id the next cycle will use too.
        return DeviceIdStore.Read();
    }

    /// <summary>
    /// Reports this computer's current address as one observation.
    /// </summary>
    /// <remarks>
    /// The report id is minted from the observation — address, router, device, machineId — and
    /// persisted before the request leaves. That is what makes the rule exact: a new address is
    /// a new observation and gets a new id, which closes the previous Local IP Period and opens
    /// one at the same instant; the same observation resent after a timeout reuses its id, and
    /// the backend hands back the period it already opened instead of opening a second.
    ///
    /// Nothing here depends on a timer. The cycle only arrives with something to say when the
    /// fingerprint changed, and the address watcher brings it forward within seconds of a lease
    /// change, so "the first time and on every change" is the whole of it.
    /// </remarks>
    private async Task ReportAddressAsync(
        Session session, DeviceRegistration registration, string deviceId, CancellationToken ct)
    {
        var observation = string.Join('|',
            Destination(session), session.Household.RouterId, deviceId,
            registration.MachineId, registration.Ip);

        var reportId = _state.IpReportIdFor(observation);

        await session.Api!.ReportDeviceIpAsync(
            session.Household.RouterId, deviceId, registration.MachineId, registration.Ip, reportId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fingerprint of a <c>presenceCreateDevice</c> write, used to decide whether the
    /// stored idempotency key still belongs to it.
    /// </summary>
    /// <remarks>
    /// It is the request's own variables, not <see cref="Signature"/>: that one includes the
    /// IP, and this mutation has no field for an IP. Keying on it would mint a new key on
    /// every lease change and spend a real update re-sending identical inventory. Built
    /// from the serialized body so that a field added to the input is covered without
    /// anyone remembering to add it here too.
    /// </remarks>
    private string DeviceWriteFingerprint(Session session, DeviceRegistration registration, string deviceId) =>
        Destination(session) + "|" + PersonicoreApi
            .BuildDeviceRequest(session.Household.RouterId, registration, string.Empty, deviceId)
            .VariablesJson();

    /// <summary>
    /// Builds the very requests that would be sent and writes them to the desktop file.
    /// It uses PersonicoreApi's builders rather than a copy: the dump and the real send
    /// share the code that assembles the body.
    /// </summary>
    private void RecordOffline(
        Session session,
        AgentSnapshot snapshot,
        DeviceRegistration registration,
        IReadOnlyList<string> ownerNames,
        string mode)
    {
        var requests = new List<GraphQlRequest>();

        // The household query is only listed when it could not actually be run: once it
        // was, routerId and householdId in the mutations below are the real ones.
        if (string.IsNullOrEmpty(session.Household.RouterId))
            requests.Add(PersonicoreApi.BuildHouseholdRequest(
                string.IsNullOrEmpty(session.Household.RouterMac) ? "(node MAC)" : session.Household.RouterMac,
                includeHousehold: true));

        if (mode is "routerips" or "both" or "deviceandrouterips")
            requests.Add(PersonicoreApi.BuildAssignmentRequest(
                session.Household.RouterId,
                registration,
                _config.SendDeviceOs ? new HashSet<string>() : ["deviceOs"],
                _env));

        if (mode is "device" or "deviceandrouterips")
        {
            // The key is minted and stored here too, not faked for the dump. The write has
            // not happened, so when sending is switched on the body is unchanged and the
            // same key is reused — the dump shows the request that will actually go out,
            // down to its idempotency key, which is the only thing this file is for.
            var deviceId = DeviceIdStore.Read();

            requests.Add(PersonicoreApi.BuildDeviceRequest(
                session.Household.RouterId,
                registration,
                _state.RequestIdFor(DeviceWriteFingerprint(session, registration, deviceId)),
                deviceId));
        }

        if (mode is "deviceandip")
        {
            var reportDeviceId = DeviceIdStore.Read();
            var observation = string.Join('|',
                Destination(session), session.Household.RouterId, reportDeviceId,
                registration.MachineId, registration.Ip);

            requests.Add(PersonicoreApi.BuildDeviceIpRequest(
                session.Household.RouterId, reportDeviceId, registration.MachineId, registration.Ip,
                _state.IpReportIdFor(observation)));
        }

        if (mode is "personaldevice" or "both")
            requests.Add(PersonicoreApi.BuildPersonalDeviceRequest(
                session.Household.HouseholdId, registration));

        // Naming the real reason matters: whoever reads this file is deciding whether the
        // agent is holding back on purpose or is misconfigured, and there are two
        // different switches that put it here.
        var held = _env.DryRun ? "PERSONICORE_DRY_RUN=true in .env" : "sendToBackend is off in config.json";

        Outbox.Record(
            endpoint: session.Client is null
                ? $"(not sending: {held})"
                : $"(not sending: {held}; household was read from {session.Client.Endpoint})",
            jwtSource: session.Node is null
                ? "(not obtained: the meter node did not respond)"
                : $"JWT served by {session.Node.BaseUrl}/cgi-bin/get_config.sh",
            snapshot: snapshot,
            device: registration,
            ownerNames: ownerNames,
            requests: requests);
    }

    /// <summary>
    /// An owner is usable when it was declared for this household and this computer and,
    /// online, also carries real IDs. An owner declared offline is stored with the typed
    /// name only: once the API is reachable the question has to be asked again so the
    /// assignment carries real panelistIds.
    /// </summary>
    private bool HasUsableOwner(string routerMac, string machineId, IReadOnlyList<Panelist> household)
    {
        if (!_state.HasOwnerFor(routerMac, machineId)) return false;

        // Without a household list there is nothing to check against: dry run keeps the
        // typed names it was given, and asking again would be asking the same question.
        if (household.Count == 0) return true;

        // With a list, typed names (no ID) are not enough: ask again so the owner is
        // picked from the list with a real panelist ID.
        var saved = _state.PanelistIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (saved.Count == 0) return false;

        // And the saved IDs have to still be in the household. People leave panels; a
        // saved owner who is gone would be re-sent on every report, either bouncing off
        // the backend or attributing the computer to somebody who is no longer there.
        var members = household.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var missing = saved.Where(id => !members.Contains(id)).ToList();

        if (missing.Count == 0) return true;

        Log.Info($"Saved owner(s) [{string.Join(',', missing)}] are no longer in this household; "
                 + "asking who this computer belongs to again.");
        return false;
    }

    /// <param name="ownerCount">
    /// How many people the panelist said use this computer, which is not the same as how
    /// many IDs we hold. Owners typed in dry run have no IDs at all: counting the IDs
    /// turned a shared computer into a personal one on the cycle after it was declared,
    /// and flipped <c>deviceShared</c> and <c>isPersonal</c> with it.
    /// </param>
    private DeviceRegistration BuildRegistration(
        DeviceIdentity identity,
        IReadOnlyList<string> panelistIds,
        string friendlyName,
        string principalPanelistId,
        int ownerCount) =>
        new(
            Ip: identity.Ip,
            MachineId: identity.MachineId,
            // Environment.MachineName last, and it is not belt-and-braces: the dialog's
            // own SuggestedName is whatever is in its name box, so a panelist who clears
            // that box leaves both of the first two empty — and `name` is String!, so the
            // registration would fail on a field they thought they were only blanking.
            FriendlyName: FirstNonEmpty(friendlyName, _profile.SuggestedName, Environment.MachineName),
            DeviceType: AgentText.NormalizeDeviceType(_profile.DeviceType),
            DeviceBrand: _profile.Brand,
            DeviceModel: _profile.Model,
            DeviceOs: _profile.OperatingSystem,
            // Names typed offline arrive with an empty Id; they must never travel to the
            // backend as a panelistId.
            PanelistIds: [.. panelistIds.Where(id => !string.IsNullOrWhiteSpace(id))])
        {
            OwnerCount = Math.Max(ownerCount, panelistIds.Count),

            // One owner needs no nomination: they are the principal by being the only
            // one. Anything else is what the panelist chose in the dialog.
            PrincipalPanelistId = panelistIds.Count == 1
                ? panelistIds[0]
                : principalPanelistId,
        };

    /// <summary>The first of these with something in it, trimmed.</summary>
    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;

    /// <summary>
    /// Saves who this computer belongs to and what it is, without claiming anything was
    /// delivered.
    /// </summary>
    /// <remarks>
    /// The half of <see cref="RememberRegistration"/> that is worth keeping when a send
    /// fails. It deliberately leaves <c>LastRegisteredAt</c> and <c>LastSentSignature</c>
    /// alone: the owner stops being asked for, and the report is still owed, which is
    /// exactly the state of affairs.
    /// </remarks>
    private void RememberOwner(
        Session session, DeviceIdentity identity, DeviceRegistration registration, IReadOnlyList<string> names)
    {
        ApplyRegistration(session, identity, registration, names);
        _state.Save();
    }

    private void RememberRegistration(
        Session session, DeviceIdentity identity, DeviceRegistration registration, IReadOnlyList<string> names)
    {
        ApplyRegistration(session, identity, registration, names);

        var previousTime = _state.LastRegisteredAt;
        var previousSignature = _state.LastSentSignature;
        _state.LastRegisteredAt = DateTimeOffset.Now;
        _state.LastSentSignature = Signature(session, registration, DeviceIdStore.Read());

        try { _state.Save(); }
        catch
        {
            // Failed persistence must not suppress the next delivery attempt.
            _state.LastRegisteredAt = previousTime;
            _state.LastSentSignature = previousSignature;
            throw;
        }
    }

    private void ApplyRegistration(
        Session session, DeviceIdentity identity, DeviceRegistration registration, IReadOnlyList<string> names)
    {
        _state.RouterMac = session.Household.RouterMac;
        _state.NodeMac = NodeIdentity(session);
        _state.RouterId = session.Household.RouterId;
        _state.HouseholdId = session.Household.HouseholdId;
        _state.MachineId = identity.MachineId;
        _state.Mac = identity.Mac;
        _state.LastIp = identity.Ip;
        _state.FriendlyName = registration.FriendlyName;
        _state.PanelistIds = [.. registration.PanelistIds];
        _state.PanelistNames = [.. names];
        _state.PrincipalPanelistId = registration.PrincipalPanelistId;
        _state.DeviceType = registration.DeviceType;
        _state.Brand = registration.DeviceBrand;
        _state.Model = registration.DeviceModel;
        _state.Os = registration.DeviceOs;
    }

    /// <summary>Forgets the declared owner so the question is asked again.</summary>
    public void ForgetOwner()
    {
        _state.Clear();
        _promptedFor = string.Empty;
        Publish(Snapshot with
        {
            Status = AgentStatus.OwnerRequired,
            Detail = "Owner cleared; it needs to be set again.",
            OwnerIds = [],
            OwnerNames = [],
            PrincipalOwnerName = string.Empty,
            PrincipalOwnerId = string.Empty,
            LastRegisteredAt = null,
        });
        WakeUp();
    }

    /// <summary>
    /// Finds the node and, like the router's own network.html, reads household and
    /// panelists with the jwt + endpoint served by get_config.sh. Reading is always
    /// attempted; *sending* is a separate decision (see <see cref="Offline"/>). The .env
    /// endpoint and token, when set, win over the router's.
    ///
    /// In dry-run mode (no .env endpoint) nothing here is fatal: without a node, or if the
    /// read fails, the cycle carries on with local identity and a typed owner, and the
    /// desktop file says so. When really sending, the same failures stop the cycle.
    /// </summary>
    private async Task<Session?> ConnectAsync(CancellationToken ct)
    {
        // Reusing the previous cycle's node avoids repeating the subnet sweep, which is
        // the only expensive part of the cycle. If it no longer answers, rediscover.
        var node = _lastNode is null ? null : await RevalidateAsync(_lastNode, ct).ConfigureAwait(false);
        node ??= await _routerClient.DiscoverAsync(ct).ConfigureAwait(false);
        _lastNode = node;

        // Every cycle: follow the node's IP, or pause the proxy when there is no node — or
        // when the node in front of us is not the one this computer is registered on.
        var foreign = IsForeignNode(node);
        _foreignNodePause = foreign;
        var proxyIp = foreign ? null : await ResolveNodeIpAsync(node, ct).ConfigureAwait(false);
        _proxyNodeIp = proxyIp;
        if (_proxy is not null)
            _proxy.Sync(proxyIp, foreign ? ProxyPause.OtherHousehold : ProxyPause.NoNode);

        if (node is null)
        {
            if (DryRun)
            {
                Log.Info("Dry-run mode: no meter node found; using local identity only.");
                return LocalOnlySession(null);
            }

            Publish(Snapshot with
            {
                Status = AgentStatus.RouterNotFound,
                NodeFound = false,
                Detail = "Meter node not found on this network.",
            });
            return null;
        }

        var routerConfig = await _routerClient.GetConfigAsync(node, ct).ConfigureAwait(false);

        var jwt = string.IsNullOrWhiteSpace(_env.GraphQlToken)
            ? routerConfig?.Jwt ?? string.Empty
            : _env.GraphQlToken;

        var endpoint = _env.HasEndpoint
            ? _env.GraphQlEndpoint
            : routerConfig?.GraphQl?.Trim() ?? string.Empty;

        if (_env.HasEndpoint
            && !string.IsNullOrWhiteSpace(routerConfig?.GraphQl)
            && !string.Equals(routerConfig!.GraphQl.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"The node advertises {routerConfig.GraphQl} but .env points to {endpoint}.");
        }

        if (string.IsNullOrWhiteSpace(jwt) || string.IsNullOrWhiteSpace(endpoint))
        {
            var missing = string.IsNullOrWhiteSpace(jwt)
                ? "No credential: neither PERSONICORE_GRAPHQL_TOKEN nor a jwt in get_config.sh."
                : "No GraphQL endpoint: neither PERSONICORE_GRAPHQL_ENDPOINT nor graphql in get_config.sh.";

            if (DryRun)
            {
                Log.Warn(missing + " The household list cannot be read; the owner will be typed.");
                return LocalOnlySession(node);
            }

            Publish(Snapshot with
            {
                Status = AgentStatus.Error, Detail = missing, NodeFound = true, RouterBaseUrl = node.BaseUrl,
            });
            return null;
        }

        var client = new GraphQlClient(endpoint, jwt, TimeSpan.FromSeconds(30));
        var api = new PersonicoreApi(client, _config, _env, RefusedBy(endpoint));

        try
        {
            var household = await api.GetHouseholdAsync(node.Info.IdentityMac, ct).ConfigureAwait(false);
            Log.Info($"Household read from {endpoint}: routerId={household.RouterId} " +
                     $"panelists={household.Panelists.Count}");
            return new Session(node, client, api, household);
        }
        catch (Exception ex)
            when (DryRun && ex is not OperationCanceledException and not HouseholdNotAssignedException)
        {
            client.Dispose();
            Log.Warn($"Could not read the household from {endpoint} ({ex.Message}); the owner will be typed.");
            return LocalOnlySession(node);
        }
        catch (HouseholdNotAssignedException ex)
        {
            // The node is there and the backend answered: this is not a fault of the
            // computer or the network, the router has simply not been set up in the panel.
            client.Dispose();
            Log.Debug(ex.Message);
            PublishNotAssigned(
                Snapshot with { NodeFound = true, RouterBaseUrl = node.BaseUrl, RouterMac = node.Info.IdentityMac },
                AgentText.RouterNotInPanel);
            return null;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Session with no API: only what the LAN and the computer itself know. routerId,
    /// householdId and the panelist list only come from the backend.
    /// </summary>
    private static Session LocalOnlySession(RouterNode? node) =>
        new(node, Client: null, Api: null, new HouseholdContext(
            RouterId: string.Empty,
            RouterMac: node?.Info.IdentityMac ?? string.Empty,
            HouseholdId: string.Empty,
            Panelists: []));

    /// <summary>
    /// Is the previous cycle's node still there? Any failure counts as "not any more",
    /// not as a cycle error: the right answer is to discover it again.
    /// </summary>
    private async Task<RouterNode?> RevalidateAsync(RouterNode node, CancellationToken ct)
    {
        try
        {
            // Ask who is there, not merely whether somebody answers. Every meter node
            // serves the same hostname, so a laptop carried to another panel household
            // reaches a different node at the very same address: reusing the cached node
            // would query the previous household and file this computer's IP under it.
            var seen = await _routerClient.IdentifyAsync(node.BaseUrl, ct).ConfigureAwait(false);
            if (seen is null) return null;

            if (!string.Equals(
                    LocalNetwork.NormalizeMac(seen.Info.IdentityMac),
                    LocalNetwork.NormalizeMac(node.Info.IdentityMac),
                    StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"A different meter node answers at {node.BaseUrl} "
                         + $"({node.Info.IdentityMac} → {seen.Info.IdentityMac}); this is another household.");

                // The owner was declared for the previous household: it has to be asked for
                // again. The prompt key carries the router, so nothing has to be cleared for
                // the dialog to open — this only keeps a node that comes back from being
                // treated as new.
                _promptedFor = string.Empty;
                return seen with { ViaDns = node.ViaDns };
            }

            return node;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"Node {node.BaseUrl} no longer responds ({ex.Message}); rediscovering.");
            return null;
        }
    }

    /// <summary>
    /// The detected profile with the panelist's corrections laid back over it.
    ///
    /// Type, brand and model are read from the machine and are often wrong — many OEMs
    /// leave placeholders in the BIOS — which is why the dialog lets them be edited. A
    /// correction was being saved and then thrown away on the next start, and the next
    /// report quietly put the wrong values back over it. The operating system is not
    /// restored: there the system's own answer always wins, and it changes on upgrades.
    /// </summary>
    private DeviceProfile WithSavedCorrections(DeviceProfile detected)
    {
        var corrected = detected with
        {
            DeviceType = string.IsNullOrWhiteSpace(_state.DeviceType) ? detected.DeviceType : _state.DeviceType,
            Brand = string.IsNullOrWhiteSpace(_state.Brand) ? detected.Brand : _state.Brand,
            Model = string.IsNullOrWhiteSpace(_state.Model) ? detected.Model : _state.Model,
            SuggestedName = string.IsNullOrWhiteSpace(_state.FriendlyName)
                ? detected.SuggestedName
                : _state.FriendlyName,
        };

        if (corrected != detected)
            Log.Debug($"Profile corrections restored: type={corrected.DeviceType} "
                      + $"brand={corrected.Brand} model={corrected.Model}");

        return corrected;
    }

    /// <summary>
    /// Which IP and which MAC the node sees us with. The MAC from the node's inventory
    /// wins over the local adapter's: the node reads it from its ARP table, and that is
    /// what ends up indexing the traffic logs.
    /// </summary>
    private async Task<DeviceIdentity?> ResolveIdentityAsync(RouterNode? node, CancellationToken ct)
    {
        // With no node (only possible offline) the route to the gateway is probed: it
        // yields the same interface and the same IP the node would see us with.
        if (node is null) return ResolveIdentityFromGateway();

        var host = new Uri(node.BaseUrl).Host;

        IPAddress? nodeAddress;
        if (!IPAddress.TryParse(host, out nodeAddress))
        {
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                nodeAddress = resolved.FirstOrDefault(
                    a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (SocketException ex)
            {
                Log.Warn($"Name {host} no longer resolves: {ex.Message}");
                return null;
            }
        }

        if (nodeAddress is null) return null;

        var local = LocalNetwork.ResolveRouteTo(nodeAddress);
        if (local is null) return null;

        var ip = local.Address.ToString();
        var clients = await _routerClient.ListClientsAsync(node, ct).ConfigureAwait(false);
        var seen = clients.FirstOrDefault(c => string.Equals(c.Ip, ip, StringComparison.Ordinal));

        var mac = LocalNetwork.NormalizeMac(seen?.Mac);
        if (mac.Length != 17) mac = LocalNetwork.NormalizeMac(local.MacAddress);

        var machineId = !string.IsNullOrWhiteSpace(seen?.MachineId)
            ? seen!.MachineId
            : MachineId.FromMac(mac);

        if (string.IsNullOrEmpty(machineId))
        {
            Log.Warn($"No usable MAC for IP {ip} (interface {local.InterfaceName}).");
            return null;
        }

        if (seen is null)
            Log.Debug($"The node does not list {ip} yet; using local MAC {mac}.");

        return new DeviceIdentity(ip, mac, machineId, local.InterfaceName);
    }

    private DeviceIdentity? ResolveIdentityFromGateway()
    {
        var gateway = LocalNetwork.DefaultGateway();
        if (gateway is null)
        {
            Log.Warn("No IPv4 gateway: cannot determine local identity.");
            return null;
        }

        var local = LocalNetwork.ResolveRouteTo(gateway);
        var mac = LocalNetwork.NormalizeMac(local?.MacAddress);

        if (local is null || mac.Length != 17)
        {
            Log.Warn("Could not read the MAC of the interface that reaches the gateway.");
            return null;
        }

        return new DeviceIdentity(
            local.Address.ToString(), mac, MachineId.FromMac(mac), local.InterfaceName);
    }

    /// <summary>
    /// true when a meter node answered and it is not the one this computer is registered on.
    /// </summary>
    /// <remarks>
    /// The test is the MAC saved with the registration, so this can only say yes once this
    /// computer has been registered somewhere: on a fresh install there is nothing to be
    /// foreign to, and the proxy works from the first cycle exactly as it always did. Nothing
    /// is said to the panelist about <i>why</i> this node is not theirs — a household they have
    /// moved away from and a node record support recreated look the same from here — only that
    /// the proxy is waiting.
    ///
    /// It is about the proxy and nothing else. The cycle carries on reading this node's
    /// household, which is what asks who this computer belongs to here and is how a panelist
    /// who has really moved gets registered again.
    /// </remarks>
    private bool IsForeignNode(RouterNode? node)
    {
        var foreign = node is not null && !_state.IsRegisteredOnNode(node.Info.IdentityMac);

        if (foreign && !_warnedForeignNode)
        {
            _warnedForeignNode = true;
            Log.Info($"The meter node here ({node!.Info.IdentityMac}) is not the one this computer is "
                     + $"registered on ({_state.ExpectedNodeMac}); the proxy stays off until it is "
                     + "registered on this one.");
        }
        else if (!foreign)
        {
            _warnedForeignNode = false;
        }

        return foreign;
    }

    /// <summary>Tray switch. Applied at once when the node is known, otherwise on the next cycle.</summary>
    public void SetProxyWanted(bool wanted)
    {
        if (_proxy is null) return;

        _proxy.SetWanted(wanted, _proxyNodeIp, _foreignNodePause ? ProxyPause.OtherHousehold : ProxyPause.NoNode);
        Publish(Snapshot);

        // Nothing verified to point at yet: the cycle will apply it as soon as it has one.
        if (wanted && _proxyNodeIp is null) WakeUp();
    }

    /// <summary>Called on Exit: see <see cref="IProxySwitch.PauseForExit"/>.</summary>
    public void PauseProxyForExit() => _proxy?.PauseForExit();

    /// <summary>false where the platform has no proxy switch yet: the menu hides it.</summary>
    public bool ProxyAvailable => _proxy is not null;

    public bool ProxyWanted => _proxy?.Wanted ?? false;

    /// <summary>
    /// The proxy watchdog. The system proxy points at an IP, and DHCP hands that IP out:
    /// if the meter node is restarted — or the ISP renews its lease — the proxy points at
    /// a dead address and the panelist cannot browse. The normal cycle is hourly, so
    /// without this the outage would last up to an hour.
    ///
    ///   * the node answers where the proxy points → nothing to do;
    ///   * it does not → the name is resolved and the node is followed to its new address;
    ///   * not there either → the panelist's own proxy is restored at once — browsing
    ///     comes first — and the switch stays paused, ready to come back by itself.
    ///
    /// The port is never checked: it is always 3129. What changes is the address.
    ///
    /// While no node shows up the wait doubles to five minutes: away from home this
    /// cannot keep probing every thirty seconds forever. It drops back to the minimum as
    /// soon as anything changes (WakeUp) or the node reappears.
    /// </summary>
    /// <summary>
    /// Watches this computer's own address and brings the cycle forward the moment it
    /// changes. The whole point of the agent is that the panel's idea of the address is
    /// current, and an hourly cycle is an hour of being wrong.
    /// </summary>
    /// <remarks>
    /// Deliberately a loop of its own rather than more work inside the cycle: the cycle
    /// talks to the node and the backend and takes seconds, and it must not run every half
    /// minute. This does no I/O — see <see cref="ObserveLocalIp"/> — so it can.
    ///
    /// It never reports anything itself. It calls <see cref="WakeUp"/>, and the cycle
    /// decides what, if anything, to send: the address is part of the fingerprint, so a real
    /// change produces a report and anything else stays quiet.
    /// </remarks>
    private async Task IpWatchLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.IpWatchIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var ip = ObserveLocalIp();

                // Nothing routable: asleep, between networks, VPN coming up. Not a change,
                // and treating it as one would wake the cycle on every suspend.
                if (ip.Length == 0) continue;

                if (_lastSeenIp.Length == 0)
                {
                    // First observation. The opening cycle already covers whatever the
                    // address is; this only establishes what to compare against. Logged so
                    // that "is the watcher running, and is it looking at the right address?"
                    // is answerable from the log instead of by inference.
                    _lastSeenIp = ip;
                    Log.Debug($"Watching this computer's address every {interval.TotalSeconds:0}s; "
                              + $"it is {ip} now.");
                    continue;
                }

                if (string.Equals(_lastSeenIp, ip, StringComparison.Ordinal)) continue;

                Log.Info($"This computer's address changed: {_lastSeenIp} → {ip}. Reporting now.");
                _lastSeenIp = ip;
                WakeUp();
            }
            catch (Exception ex)
            {
                // A watcher that dies is a watcher that stops watching, and nothing would
                // say so. It keeps its interval and tries again.
                Log.Debug($"Address check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The IP this computer would use to reach the node, or empty when there is no route.
    /// </summary>
    /// <remarks>
    /// The node's address when one is known, the default gateway otherwise — either is on
    /// the home segment, which is the one whose address the panel cares about. A connected
    /// UDP socket sends no packet; the stack only resolves the route and assigns the local
    /// address, so this costs a syscall and touches neither the node nor the network.
    /// </remarks>
    private string ObserveLocalIp()
    {
        var target = _proxyNodeIp is { Length: > 0 } nodeIp && IPAddress.TryParse(nodeIp, out var parsed)
            ? parsed
            : LocalNetwork.DefaultGateway();

        return target is null ? string.Empty : LocalNetwork.ResolveRouteTo(target)?.Address.ToString() ?? string.Empty;
    }

    private async Task ProxyWatchLoopAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_config.RouterTimeoutSeconds);

        while (!ct.IsCancellationRequested)
        {
            var steps = Math.Min(Volatile.Read(ref _proxyMissing), 4);
            var seconds = Math.Min(300, _config.ProxyWatchIntervalSeconds * (1 << steps));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await WatchProxyOnceAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug($"Proxy watch failed: {ex.Message}");
            }
        }
    }

    private async Task WatchProxyOnceAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_proxy is null || !_proxy.Wanted)
        {
            Interlocked.Exchange(ref _proxyMissing, 0);
            return;
        }

        var applied = _state.ProxyApplied;
        var target = applied ? AddressHost(_state.ProxyAddress) : null;

        // Our node, not any node. Every meter node answers on the same name and often at the
        // same address, so without this the loop below "finds the node" in another panel
        // household and quietly sends that home's meter this computer's traffic. Empty before
        // the first registration, which accepts whichever node answers — there is nothing yet
        // to be foreign to.
        var ours = _state.ExpectedNodeMac;

        if (target is not null && await NodeProbe.IsMeterNodeAsync(target, timeout, ours, ct).ConfigureAwait(false))
        {
            // Trust can be revoked while the node remains reachable.
            _proxy.Sync(target);
            Publish(Snapshot);
            Interlocked.Exchange(ref _proxyMissing, 0);
            return;
        }

        var found = await NodeProbe.FirstReachableAsync(
            await NodeCandidatesAsync(ct).ConfigureAwait(false), timeout, ours, ct).ConfigureAwait(false);

        if (found is not null)
        {
            Log.Info(target is null
                ? $"Meter node found at {found}; the proxy comes back on."
                : $"The meter node moved from {target} to {found}; proxy re-pointed without waiting for the next cycle.");

            // The address changed: the node is rediscovered and the assignment — indexed
            // by IP in the backend — is re-asserted now, without waiting for the poll.
            _lastNode = null;
            _proxyNodeIp = found;
            _foreignNodePause = false;
            _proxy.Sync(found);
            Publish(Snapshot with { NodeFound = true });
            WakeUp();
            return;
        }

        if (applied)
        {
            // "Not ours" and "not there" are one answer to this loop —the proxy comes off
            // either way, because browsing comes first— and two different lines to read
            // afterwards. The cycle, which has the node's identity in hand, is what decides
            // between them; this only reports what it is acting on.
            Log.Warn(_foreignNodePause
                ? $"The node at {target} is not the one this computer is registered on; the proxy is "
                  + "removed so nothing is measured through another household."
                : $"No meter node answers ({target} is gone); the proxy is removed so the computer keeps browsing.");

            _lastNode = null;
            _proxyNodeIp = null;
            _proxy.Sync(null, _foreignNodePause ? ProxyPause.OtherHousehold : ProxyPause.NoNode);
            Publish(Snapshot with { NodeFound = false });
            WakeUp();
        }

        Interlocked.Increment(ref _proxyMissing);
    }

    /// <summary>
    /// Where to look for the node when the proxy loses it, cheapest first. The subnet
    /// sweep is not in here: that is the cycle's job, at the cycle's own pace.
    /// </summary>
    private async Task<IReadOnlyList<string>> NodeCandidatesAsync(CancellationToken ct)
    {
        var candidates = new List<string>();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(_config.RouterHost, ct).ConfigureAwait(false);
            candidates.AddRange(addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()));
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            Log.Debug($"{_config.RouterHost} does not resolve: {ex.Message}");
        }

        if (_lastNode is { } node)
        {
            if (IPAddress.TryParse(node.Info.Ip, out _)) candidates.Add(node.Info.Ip);
            var host = new Uri(node.BaseUrl).Host;
            if (IPAddress.TryParse(host, out _)) candidates.Add(host);
        }

        return candidates;
    }

    /// <summary>"192.168.1.5:3129" → "192.168.1.5"; null when there is nothing to look at.</summary>
    private static string? AddressHost(string address)
    {
        var host = address.Split(':')[0].Trim();
        return host.Length == 0 ? null : host;
    }

    /// <summary>
    /// The address the proxy should point at, checked before it is used: the one the
    /// node reports is preferred, but only if it answers there. If it reported an address
    /// on another interface — its WAN, say — the proxy would point at an unreachable IP,
    /// which is exactly the outage this prevents.
    /// </summary>
    private async Task<string?> ResolveNodeIpAsync(RouterNode? node, CancellationToken ct)
    {
        var reported = NodeIp(node);
        if (node is null || reported is null) return reported;

        var reached = new Uri(node.BaseUrl).Host;
        if (string.Equals(reported, reached, StringComparison.OrdinalIgnoreCase)) return reported;

        var timeout = TimeSpan.FromSeconds(_config.RouterTimeoutSeconds);
        if (await NodeProbe.IsMeterNodeAsync(reported, timeout, _state.ExpectedNodeMac, ct).ConfigureAwait(false))
            return reported;

        if (IPAddress.TryParse(reached, out _))
        {
            Log.Warn($"The node reports {reported} but does not answer there; the proxy uses {reached}, where it does.");
            return reached;
        }

        return reported;
    }

    /// <summary>
    /// The node's LAN IP, which is what the proxy must point to. router_info.sh reports
    /// it; otherwise the address we reached it at. Never the DNS name: away from home
    /// router.example.invalid can resolve to someone else's device.
    /// </summary>
    private static string? NodeIp(RouterNode? node)
    {
        if (node is null) return null;

        if (IPAddress.TryParse(node.Info.Ip, out var reported) && reported.AddressFamily == AddressFamily.InterNetwork)
            return reported.ToString();

        var host = new Uri(node.BaseUrl).Host;
        if (IPAddress.TryParse(host, out var reached)) return reached.ToString();

        try
        {
            return Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why this cycle should report, or null when it should stay quiet.
    ///
    /// Two things make a report worth sending: something the panel does not know yet
    /// (a new IP above all, but also a new owner, name or hardware profile), or enough
    /// time having passed that the panel should hear the IP is still current. Everything
    /// else is a repeat, and the assignment is indexed by IP: re-sending the same one
    /// twenty-four times a day tells the backend nothing it did not already have.
    /// </summary>
    private string? ReportReason(Session session, DeviceRegistration registration, string deviceId)
    {
        var signature = Signature(session, registration, deviceId);

        if (!string.Equals(_state.LastSentSignature, signature, StringComparison.Ordinal))
            return _state.LastSentSignature.Length == 0 ? "first report" : "something changed";

        if (_state.LastRegisteredAt is not { } last) return "never sent";

        var elapsed = DateTimeOffset.Now - last;

        // A clock that jumped backwards (a correction, a timezone change, a VM resuming)
        // would otherwise park the next report until the clock caught up again.
        if (elapsed < TimeSpan.Zero) return "the clock moved backwards";

        // 0 disables it: reporting is change-driven, and the address is watched directly
        // rather than re-asserted on a timer.
        if (_config.ReportIntervalSeconds <= 0) return null;

        return elapsed >= TimeSpan.FromSeconds(_config.ReportIntervalSeconds)
            ? "the periodic report is due"
            : null;
    }

    /// <summary>
    /// The principal owner's name, for the diagnostics report. Ids mean nothing to the
    /// person reading it over the phone with a panelist.
    /// </summary>
    private string PrincipalName(IReadOnlyList<string> ids, IReadOnlyList<string> names)
    {
        var index = ids.ToList().IndexOf(_state.PrincipalPanelistId);
        return index >= 0 && index < names.Count ? names[index] : string.Empty;
    }

    /// <summary>
    /// When the next unchanged report is due, or null when there is no such thing because
    /// the periodic report is off — which is the default. The details window then says the
    /// next report happens when something changes, instead of naming a time that will come
    /// and go with nothing sent.
    /// </summary>
    private DateTimeOffset? NextReportDue() => _config.ReportIntervalSeconds <= 0
        ? null
        : (_state.LastRegisteredAt ?? DateTimeOffset.Now).AddSeconds(_config.ReportIntervalSeconds);

    /// <summary>
    /// Where the last report went, and how. A simulated write and a real send are not the
    /// same event, and neither are two different backends: the node advertises its own
    /// endpoint, so "the endpoint in use" has to be the resolved one, not a placeholder.
    /// The registration mode belongs here too — switching to personalDevice asks for an
    /// operation the panel has never received, however identical the device data is.
    /// </summary>
    private string Destination(Session? session) => Offline
        ? "outbox"
        : session?.Client?.Endpoint is { Length: > 0 } endpoint
            ? endpoint
            : _env.HasEndpoint ? _env.GraphQlEndpoint : "(unresolved)";

    /// <summary>
    /// Everything the backend is told, flattened into one line. Comparing this rather
    /// than the IP alone means a change of owner, name or hardware is never sat on for
    /// twelve hours just because the address happened to stay the same.
    /// </summary>
    /// <remarks>
    /// The router and household ids are part of it because they are mutation variables,
    /// not context: when support recreates the router record for the same MAC, everything
    /// the panelist can see is unchanged — same people, same computer, same address — and
    /// the assignment the new record needs would have waited for the twelve-hour timer.
    /// </remarks>
    private string Signature(Session? session, DeviceRegistration r, string deviceId) => string.Join('|',
        Destination(session),
        _config.RegistrationMode.Trim().ToLowerInvariant(),
        session?.Household.RouterId ?? string.Empty,
        session?.Household.HouseholdId ?? string.Empty,
        r.Ip, r.MachineId, r.FriendlyName, r.DeviceType, r.DeviceBrand, r.DeviceModel, r.DeviceOs,
        r.DeviceShared, r.IsPersonal, r.PrincipalPanelistId,
        string.Join(',', r.PanelistIds.OrderBy(id => id, StringComparer.Ordinal)), deviceId);

    /// <summary>
    /// The panel has nobody to assign this computer to yet. The state is published and the
    /// panelist told once; the cycle keeps retrying on its own.
    ///
    /// The panelist list and the router id are cleared on the way out. They may still hold
    /// a previous cycle's values, and leaving them would keep <see cref="AgentSnapshot.CanRegister"/>
    /// true: the menu would offer a registration that is now impossible, and the dialog
    /// would open with a household the backend no longer admits to knowing.
    /// </summary>
    private void PublishNotAssigned(AgentSnapshot snapshot, string detail)
    {
        Publish(snapshot with
        {
            Status = AgentStatus.HouseholdNotAssigned,
            Detail = detail,
            Panelists = [],
            RouterId = string.Empty,
        });

        if (_warnedNotAssigned) return;
        _warnedNotAssigned = true;

        // Logged on the transition, not on every retry: a line a minute, for days, would
        // bury the rest of the log — and this is the line support looks for.
        Log.Warn("Nothing to assign this computer to yet · " + detail);
        HouseholdNotAssigned?.Invoke(Snapshot);
    }

    private void Publish(AgentSnapshot snapshot)
    {
        snapshot = snapshot with
        {
            Offline = Offline,
            DryRun = DryRun,
            RouterHost = _config.RouterHost,
            ProxyWanted = _proxy?.Wanted ?? false,
            ProxyStatus = _proxy?.StatusText ?? "not available on this platform yet",
            SetupCompleted = _state.SetupCompletedAt is not null,
        };
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
            _proxyLoop?.Wait(TimeSpan.FromSeconds(3));
            _ipLoop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // The loop ended through cancellation; nothing to report on the way out.
        }

        _cts?.Dispose();
        _routerClient.Dispose();
        _cycleLock.Dispose();
    }

    private sealed record DeviceIdentity(string Ip, string Mac, string MachineId, string InterfaceName);

    /// <summary>
    /// A cycle with everything needed to send. Offline, `Node` may be null (the node did
    /// not answer) and `Api`/`Client` always are: that is precisely the signal that
    /// SendAsync uses to dump to the desktop file instead of calling the backend.
    /// </summary>
    private sealed record Session(
        RouterNode? Node,
        GraphQlClient? Client,
        PersonicoreApi? Api,
        HouseholdContext Household) : IDisposable
    {
        public void Dispose() => Client?.Dispose();
    }
}
