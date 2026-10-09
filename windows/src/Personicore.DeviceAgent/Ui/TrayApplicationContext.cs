using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// The agent lives here: an icon in the notification area, no main window. Everything
/// visible (icon colour, tooltip, menu) is a projection of the last AgentSnapshot; the
/// real work happens in AgentWorker. The macOS menu bar is the same screen, drawn with
/// Avalonia instead of WinForms.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AgentWorker _worker;
    private readonly NotifyIcon _tray;
    private readonly Control _marshal;
    private readonly ProxyReminder _proxyReminder = new();
    private readonly System.Windows.Forms.Timer _proxyReminderTimer = new() { Interval = 1_000 };

    private void ShowProxyReminder() =>
        _tray.ShowBalloonTip(10_000, "Personicore", AgentText.ProxyDisabledReminder, ToolTipIcon.Warning);

    private void CheckProxyReminder()
    {
        if (_proxyReminder.ShouldNotify(_worker.ProxyAvailable, _worker.ProxyWanted)) ShowProxyReminder();
    }

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _setupItem;
    private readonly ToolStripMenuItem _registerItem;
    private readonly ToolStripMenuItem _detailsItem;
    private readonly ToolStripMenuItem _nodeItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _outboxItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _proxyItem;
    private readonly ToolStripMenuItem _proxyWindowItem;

    private bool _installingCertificate;
    private readonly ToolStripMenuItem _certificateItem;

    private OwnerForm? _openDialog;
    private ProxyForm? _openProxy;
    private SetupForm? _openSetup;

    /// <summary>
    /// The assistant opens itself once per run, not once per snapshot: without this the
    /// first cycle's three or four publishes would each try to open it.
    /// </summary>
    private bool _setupOffered;

    public TrayApplicationContext(AgentWorker worker)
    {
        _worker = worker;

        // An anchor on the UI thread: the worker's events arrive from the pool.
        // The handle is forced: without it, BeginInvoke from another thread throws.
        _marshal = new Control();
        _ = _marshal.Handle;

        StartupRegistration.RepairIfMoved();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _setupItem = new ToolStripMenuItem(AgentText.SetupItem, null, (_, _) => ShowSetup());
        _registerItem = new ToolStripMenuItem(AgentText.Register, null, (_, _) => ShowOwnerDialog());
        _detailsItem = new ToolStripMenuItem(AgentText.Details, null, (_, _) => ShowDetails());
        // Looks at the network again; it never speaks to the panel. Sending is the
        // owner dialog's job, where the panelist sees what is being sent.
        _nodeItem = new ToolStripMenuItem(AgentText.OpenNode, null, (_, _) => OpenNodePage());
        _refreshItem = new ToolStripMenuItem(AgentText.Refresh, null, (_, _) => _worker.WakeUp());
        _outboxItem = new ToolStripMenuItem(AgentText.Outbox, null, (_, _) => OpenOutbox());
        _startupItem = new ToolStripMenuItem(Words.Windows.StartupItem, null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
            Checked = StartupRegistration.IsEnabled(),
        };

        _proxyItem = new ToolStripMenuItem(AgentText.ProxyItem, null, (_, _) => ToggleProxy())
        {
            CheckOnClick = false,
            Visible = worker.ProxyAvailable,
        };

        _proxyWindowItem = new ToolStripMenuItem(AgentText.ProxySettingsItem, null, (_, _) => ShowProxyWindow())
        {
            Visible = worker.ProxyAvailable,
        };

        _certificateItem = new ToolStripMenuItem(AgentText.InstallCertificate, null,
            async (_, _) => await InstallCertificateAsync());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            // First of the actions, and first for a reason: it is the one item that explains
            // the other three to somebody who has just unzipped the agent.
            _setupItem,
            _registerItem,
            _outboxItem,
            _detailsItem,
            _nodeItem,
            _refreshItem,
            new ToolStripSeparator(),
            _proxyItem,
            _proxyWindowItem,
            new ToolStripMenuItem(AgentText.TestProxyNotification, null, (_, _) => ShowProxyReminder()),
            _certificateItem,
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(AgentText.Quit, null, (_, _) => Quit()),
        ]);

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.For(AgentStatus.Starting),
            Text = "Personicore · starting",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowDetails();
        _tray.BalloonTipClicked += (_, _) => ShowOwnerDialog();

        _worker.SnapshotChanged += OnSnapshotChanged;
        _worker.OwnerRequired += OnOwnerRequired;
        _worker.HouseholdNotAssigned += OnHouseholdNotAssigned;

        // A network change usually means a new IP, and the backend indexes the
        // assignment by IP: revalidate without waiting for the next poll.
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        Render(_worker.Snapshot);
        CheckProxyReminder();
        _proxyReminderTimer.Tick += (_, _) => CheckProxyReminder();
        _proxyReminderTimer.Start();
    }

    private async Task InstallCertificateAsync()
    {
        if (_installingCertificate) return;

        // Already trusted: say so and stop. Running the installer would raise a UAC prompt to
        // do nothing.
        if (_worker.Snapshot.CertificateTrusted)
        {
            _tray.ShowBalloonTip(8_000, "Personicore", AgentText.CertificateInstalled, ToolTipIcon.Info);
            return;
        }

        if (!_worker.Snapshot.ActionsUnlocked) return;
        _installingCertificate = true;
        Render(_worker.Snapshot);
        var message = AgentText.CertificateInstalled;
        try
        {
            await Net.CertificateInstaller.InstallAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Certificate installation failed", ex);
            message = AgentText.CertificateFailed;
        }
        finally
        {
            _installingCertificate = false;

            // Before the render, so the menu shows the new state rather than the state from
            // before the panelist installed it.
            _worker.RefreshCertificateTrust();
            Render(_worker.Snapshot);
        }
        _tray.ShowBalloonTip(8_000, "Personicore", message, ToolTipIcon.Info);
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Log.Debug("Network address change; running the cycle early.");
        _worker.WakeUp();
    }

    private void OnSnapshotChanged(AgentSnapshot snapshot) => OnUi(() => Render(snapshot));

    private void OnOwnerRequired(AgentSnapshot snapshot) => OnUi(() =>
    {
        // The assistant is already asking this, as its first step. Opening the owner dialog
        // on top of it would ask the same question twice, from two windows, one of which the
        // panelist did not open.
        if (_openSetup is { IsDisposed: false })
        {
            _openSetup.Activate();
            return;
        }

        _tray.ShowBalloonTip(
            10_000,
            "Personicore",
            AgentText.OwnerRequiredPromptClickable,
            ToolTipIcon.Info);

        ShowOwnerDialog();
    });

    /// <summary>
    /// The three first-run steps, one at a time. Opened from the menu, and by itself the
    /// first time the agent runs.
    /// </summary>
    /// <remarks>
    /// Not modal: its first step opens the owner dialog, which is, and a modal inside a
    /// modal would leave the panelist unable to reach the menu while they read it. The tray
    /// pushes snapshots into it so the steps stay current.
    /// </remarks>
    private void ShowSetup()
    {
        if (_openSetup is { IsDisposed: false })
        {
            _openSetup.Activate();
            return;
        }

        var setup = new SetupForm(
            () => _worker.Snapshot,
            ShowOwnerDialog,
            InstallCertificateAsync,
            wanted => SetProxy(wanted),
            _worker.MarkSetupDone);

        _openSetup = setup;

        setup.FormClosed += (_, _) =>
        {
            _openSetup = null;

            // Finish records itself. Closing with the X does not, except when there is
            // nothing left to walk through: re-offering three completed steps on every start
            // would be a nag, and the panelist has no way to make it stop.
            if (!setup.Finished && SetupPlan.AllDone(_worker.Snapshot)) _worker.MarkSetupDone();

            setup.Dispose();
        };

        setup.Show();
    }

    /// <summary>
    /// The panel has nobody to assign this computer to. Said once, because the agent
    /// retries by itself and there is nothing the panelist can do from here.
    /// </summary>
    private void OnHouseholdNotAssigned(AgentSnapshot snapshot) => OnUi(() =>
        _tray.ShowBalloonTip(10_000, "Personicore", snapshot.Detail, ToolTipIcon.Info));

    /// <summary>The proxy switch as a window, for when a checkmark in a menu is not obvious enough.</summary>
    private void ShowProxyWindow()
    {
        if (_openProxy is { IsDisposed: false })
        {
            _openProxy.Activate();
            return;
        }

        if (!_worker.Snapshot.ActionsUnlocked && !_worker.Snapshot.ProxyWanted)
        {
            _tray.ShowBalloonTip(6_000, "Personicore", AgentText.WaitingForNode, ToolTipIcon.Info);
            _worker.WakeUp();
            return;
        }

        using var window = new ProxyForm(() => _worker.Snapshot, wanted => _worker.SetProxyWanted(wanted));
        _openProxy = window;

        try
        {
            window.ShowDialog();
        }
        finally
        {
            _openProxy = null;
        }
    }

    private void Render(AgentSnapshot snapshot)
    {
        _tray.Icon = TrayIcons.For(snapshot.Status);

        _outboxItem.Visible = snapshot.Offline;

        var headline = AgentText.Headline(snapshot);

        _statusItem.Text = headline;

        // NotifyIcon truncates its tooltip at 63 characters; trimming here stops
        // Windows from cutting it in the middle of a word.
        var tooltip = $"Personicore · {headline}";
        _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..60] + "…";

        _registerItem.Text = snapshot.OwnerNames.Count > 0 ? AgentText.ChangeOwner : AgentText.Register;

        // Until a meter node answers on this network the menu offers only looking
        // (details and the log folder) and looking again: everything else needs the node.
        var unlocked = snapshot.ActionsUnlocked;

        _registerItem.Enabled = unlocked && snapshot.CanRegister;
        _registerItem.ToolTipText = unlocked ? string.Empty : AgentText.WaitingForNode;

        _proxyItem.Checked = snapshot.ProxyWanted;

        // Switching it off is always allowed: leaving a panelist with a switch they
        // cannot undo would be worse than the lock it enforces.
        _proxyItem.Enabled = unlocked || snapshot.ProxyWanted;
        _proxyItem.ToolTipText = unlocked ? AgentText.ProxyTooltip(snapshot) : AgentText.WaitingForNode;
        _proxyItem.Text = snapshot.ProxyWanted && snapshot.ProxyStatus.StartsWith("paused", StringComparison.Ordinal)
            ? AgentText.ProxyItemPaused
            : AgentText.ProxyItem;

        _proxyWindowItem.Enabled = unlocked || snapshot.ProxyWanted;
        _proxyWindowItem.ToolTipText = unlocked ? string.Empty : AgentText.WaitingForNode;

        _certificateItem.Text = AgentText.CertificateItem(snapshot);

        // Enabled while trusted too: the item is then a status line that reports itself when
        // clicked, and a greyed-out one reads as something broken.
        _certificateItem.Enabled = (unlocked || snapshot.CertificateTrusted) && !_installingCertificate;
        _startupItem.Enabled = unlocked;
        _startupItem.ToolTipText = unlocked ? string.Empty : AgentText.WaitingForNode;

        // Away from home the page is not there to open, and the name only resolves through
        // the node itself: an item that opens a browser on an error is worse than one that
        // is greyed out with a reason.
        _nodeItem.Enabled = unlocked && snapshot.NodeUrl.Length > 0;
        _nodeItem.ToolTipText = _nodeItem.Enabled ? snapshot.NodeUrl : AgentText.WaitingForNode;

        // Never greyed out: it is the one window that explains why everything else is, and
        // the steps inside it carry their own locks.
        _setupItem.Text = AgentText.SetupItem;

        // All three steps can also be done from the menu, so a window left open has to be
        // told: otherwise it goes on asking for a certificate installed behind its back.
        _openSetup?.Apply(snapshot);

        // First run: the agent has never been set up on this computer, so the assistant opens
        // itself rather than leaving a panelist to find three separate menu items.
        if (!snapshot.SetupCompleted && !_setupOffered)
        {
            _setupOffered = true;

            // Queued, not called: the first Render happens in the constructor, before
            // Application.Run, and a window shown from there has no message loop to be
            // activated by. The queued call runs as soon as the loop starts.
            if (!_marshal.IsDisposed)
            {
                _tray.ShowBalloonTip(8_000, AgentText.FirstRunWelcomeTitle, AgentText.FirstRunWelcomeBody(Words.Windows), ToolTipIcon.Info);
                _marshal.BeginInvoke(ShowSetup);
            }
        }
    }

    /// <summary>
    /// The switch. The result is shown in a notification because it is also visible in
    /// Windows: Settings → Network &amp; internet → Proxy → Manual proxy setup.
    /// </summary>
    private void ToggleProxy() => SetProxy(!_worker.Snapshot.ProxyWanted);

    /// <summary>
    /// The switch itself, so the menu line and the assistant's third step go through one
    /// path: the guard, the call and the notification are written once.
    /// </summary>
    private void SetProxy(bool wanted)
    {
        if (wanted == _worker.Snapshot.ProxyWanted) return;

        if (wanted && !_worker.Snapshot.ActionsUnlocked)
        {
            _tray.ShowBalloonTip(6_000, "Personicore", AgentText.WaitingForNode, ToolTipIcon.Info);
            _worker.WakeUp();
            return;
        }

        _worker.SetProxyWanted(wanted);

        var status = _worker.Snapshot.ProxyStatus;
        var message = status.StartsWith("error", StringComparison.Ordinal)
            ? AgentText.ProxyFailed(status[8..])
            : !wanted
                ? AgentText.ProxyOff
                : status.StartsWith("on", StringComparison.Ordinal)
                    ? AgentText.ProxyOn(status[5..], Words.Windows)
                    : AgentText.ProxyPending;

        _tray.ShowBalloonTip(6_000, "Personicore", message,
            status.StartsWith("error", StringComparison.Ordinal) ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void ShowOwnerDialog()
    {
        if (_openDialog is { IsDisposed: false })
        {
            _openDialog.Activate();
            return;
        }

        var snapshot = _worker.Snapshot;

        if (!snapshot.ActionsUnlocked || !snapshot.CanRegister)
        {
            MessageBox.Show(
                AgentText.CannotRegister(snapshot),
                "Personicore", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _worker.WakeUp();
            return;
        }

        var preselected = snapshot.OwnerIds;

        using var dialog = new OwnerForm(snapshot, preselected);
        _openDialog = dialog;

        try
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;

            RegisterAsync(dialog);
        }
        finally
        {
            _openDialog = null;
        }
    }

    /// <summary>
    /// Sends without blocking the UI and tells the panelist how it went: a silent
    /// failure here would leave a computer out of the panel without anyone noticing.
    /// </summary>
    private async void RegisterAsync(OwnerForm dialog)
    {
        var owners = dialog.SelectedOwners;
        var name = dialog.FriendlyName;
        var profile = dialog.EditedProfile;

        _registerItem.Enabled = false;

        try
        {
            await _worker.RegisterAsync(owners, name, profile, dialog.PrincipalOwnerId, CancellationToken.None);

            var people = string.Join(", ", owners.Select(o => o.DisplayName));

            _tray.ShowBalloonTip(
                6_000,
                "Personicore",
                _worker.Snapshot.Offline
                    ? AgentText.Simulated(name, owners.Select(o => o.DisplayName), Path.GetFileName(Outbox.FilePath))
                    : AgentText.Registered(name, owners.Select(o => o.DisplayName)),
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Could not register the computer", ex);
            MessageBox.Show(
                AgentText.RegisterFailed(ex.Message),
                "Personicore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _registerItem.Enabled = _worker.Snapshot.ActionsUnlocked && _worker.Snapshot.CanRegister;
        }
    }

    /// <summary>Opens the dump with whatever application handles .txt.</summary>
    private static void OpenOutbox()
    {
        try
        {
            if (!File.Exists(Outbox.FilePath))
            {
                MessageBox.Show(
                    AgentText.OutboxEmpty + Environment.NewLine + Environment.NewLine + Outbox.FilePath,
                    "Personicore", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Outbox.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the outbox file", ex);
        }
    }

    /// <summary>
    /// Opens the meter node's page in the panelist's usual browser. The address is the
    /// agent's own — the one it reached the node at, or the configured name — never one
    /// read from anything the node answered.
    /// </summary>
    private void OpenNodePage()
    {
        var url = _worker.Snapshot.NodeUrl;

        if (!_worker.Snapshot.ActionsUnlocked || url.Length == 0)
        {
            _tray.ShowBalloonTip(6_000, "Personicore", AgentText.WaitingForNode, ToolTipIcon.Info);
            _worker.WakeUp();
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}", ex);
        }
    }

    private void ShowDetails()
    {
        using var details = new DetailsForm(_worker.Snapshot);
        details.ShowDialog();
    }

    private void ToggleStartup()
    {
        if (!_worker.Snapshot.ActionsUnlocked)
        {
            _tray.ShowBalloonTip(6_000, "Personicore", AgentText.WaitingForNode, ToolTipIcon.Info);
            _worker.WakeUp();
            return;
        }

        var enabled = !_startupItem.Checked;
        StartupRegistration.SetEnabled(enabled);
        _startupItem.Checked = StartupRegistration.IsEnabled();

        if (_startupItem.Checked != enabled)
        {
            MessageBox.Show(
                AgentText.StartupFailed(Words.Windows),
                "Personicore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnUi(Action action)
    {
        if (_marshal.IsDisposed) return;

        if (_marshal.InvokeRequired) _marshal.BeginInvoke(action);
        else action();
    }

    private void Quit()
    {
        Log.Info("User closed the agent from the tray.");
        _worker.PauseProxyForExit();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _proxyReminderTimer.Stop();
            _proxyReminderTimer.Dispose();
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _worker.SnapshotChanged -= OnSnapshotChanged;
            _worker.OwnerRequired -= OnOwnerRequired;
            _worker.HouseholdNotAssigned -= OnHouseholdNotAssigned;

            // Closed rather than left to the process ending: its FormClosed handler is what
            // records a finished setup.
            if (_openSetup is { IsDisposed: false }) _openSetup.Close();

            _tray.Visible = false;
            _tray.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
