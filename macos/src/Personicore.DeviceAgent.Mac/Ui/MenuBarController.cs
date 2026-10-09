using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Mac.Platform;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// The agent lives here: an icon in the macOS menu bar, no Dock icon and no main window.
/// Everything visible is a projection of the last AgentSnapshot, exactly like the
/// Windows tray; the work happens in the shared AgentWorker.
/// </summary>
internal sealed class MenuBarController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly AgentWorker _worker;
    private readonly TrayIcon _tray;
    private readonly ProxyReminder _proxyReminder = new();
    private readonly DispatcherTimer _proxyReminderTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NativeMenuItem _testProxyNotification = new(AgentText.TestProxyNotification);

    private void CheckProxyReminder()
    {
        if (_proxyReminder.ShouldNotify(_worker.ProxyAvailable, _worker.ProxyWanted))
            Notifier.Show(AgentText.ProxyDisabledReminder);
    }

    private readonly NativeMenuItem _statusItem = new("Starting…") { IsEnabled = false };
    private readonly NativeMenuItem _setupItem = new(AgentText.SetupItem);
    private readonly NativeMenuItem _registerItem = new(AgentText.Register);
    private readonly NativeMenuItem _outboxItem = new(AgentText.Outbox);
    private readonly NativeMenuItem _detailsItem = new(AgentText.Details);
    private readonly NativeMenuItem _nodeItem = new(AgentText.OpenNode);
    private readonly NativeMenuItem _refreshItem = new(AgentText.Refresh);
    private readonly NativeMenuItem _proxyItem = new(AgentText.ProxyItem) { ToggleType = MenuItemToggleType.CheckBox };
    private readonly NativeMenuItem _proxyWindowItem = new(AgentText.ProxySettingsItem);
    private readonly NativeMenuItem _loginItem = new(Words.Mac.StartupItem) { ToggleType = MenuItemToggleType.CheckBox };
    private readonly NativeMenuItem _quitItem = new(AgentText.Quit);

    private bool _installingCertificate;
    private readonly NativeMenuItem _certificateItem = new(AgentText.InstallCertificate);

    private OwnerWindow? _openDialog;
    private DetailsWindow? _openDetails;
    private ProxyWindow? _openProxy;
    private SetupWindow? _openSetup;

    /// <summary>
    /// The assistant opens itself once per run, not once per snapshot: without this the
    /// first cycle’s three or four publishes would each try to open it.
    /// </summary>
    private bool _setupOffered;

    public MenuBarController(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        var config = AgentConfig.Load(out var problems);
        Log.Configure(config.LogLevel);
        Log.Info($"— Personicore Device Agent (macOS) starting · v{typeof(MenuBarController).Assembly.GetName().Version}");
        foreach (var problem in problems) Log.Warn(problem);

        var env = EnvSettings.Load();
        var state = AgentState.Load();

        _worker = new AgentWorker(
            config, env, state, new MacDeviceProfileProvider(),
            new CertificateGuardProxy(new MacProxySwitch(state, config.RouterHost), CertificateInstaller.IsTrusted),
            CertificateInstaller.IsTrusted);

        _setupItem.Click += (_, _) => ShowSetup();
        _registerItem.Click += (_, _) => ShowOwnerDialog();
        _outboxItem.Click += (_, _) => OpenOutbox();
        _detailsItem.Click += (_, _) => ShowDetails();
        _nodeItem.Click += (_, _) => OpenNodePage();
        _refreshItem.Click += (_, _) => _worker.WakeUp();
        _proxyItem.Click += (_, _) => ToggleProxy();
        _proxyWindowItem.Click += (_, _) => ShowProxyWindow();
        _testProxyNotification.Click += (_, _) => Notifier.Show(AgentText.ProxyDisabledReminder, test: true);
        _certificateItem.Click += async (_, _) => await InstallCertificateAsync();
        _loginItem.Click += (_, _) => ToggleLogin();
        _quitItem.Click += (_, _) => Quit();

        _loginItem.IsChecked = LoginItem.IsEnabled();
        LoginItem.RepairIfMoved();

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[]
                 {
                     _statusItem,
                     new NativeMenuItemSeparator(),
                     // First of the actions, and first for a reason: it is the one item that
                     // explains the other three to somebody who has just downloaded the agent.
                     _setupItem,
                     _registerItem,
                     _outboxItem,
                     _detailsItem,
                     _nodeItem,
                     _refreshItem,
                     new NativeMenuItemSeparator(),
                     _proxyItem,
                     _proxyWindowItem,
                     _testProxyNotification,
                     _certificateItem,
                     _loginItem,
                     new NativeMenuItemSeparator(),
                     _quitItem,
                 })
        {
            menu.Items.Add(item);
        }

        _tray = new TrayIcon
        {
            Icon = MarkIcons.ForMenuBar(AgentStatus.Starting),
            ToolTipText = "Personicore · starting",
            Menu = menu,
            IsVisible = true,
        };

        var icons = new TrayIcons { _tray };
        TrayIcon.SetIcons(Application.Current!, icons);

        _worker.SnapshotChanged += snapshot => OnUi(() => Render(snapshot));
        _worker.OwnerRequired += _ => OnUi(() =>
        {
            // The assistant is already asking this, as its first step. Opening the owner
            // window on top of it would ask the same question twice, from two windows, one
            // of which the panelist did not open.
            if (_openSetup is not null)
            {
                _openSetup.Activate();
                return;
            }

            Notifier.Show(AgentText.OwnerRequiredPrompt);
            ShowOwnerDialog();
        });

        // Nobody to assign this computer to yet. Said once, because the agent retries by
        // itself and there is nothing the panelist can do from here.
        _worker.HouseholdNotAssigned += snapshot => OnUi(() => Notifier.Show(snapshot.Detail));

        // A network change usually means a new IP, and the backend indexes the
        // assignment by IP: revalidate without waiting for the next poll.
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public void Start()
    {
        Render(_worker.Snapshot);
        CheckProxyReminder();
        _proxyReminderTimer.Tick += (_, _) => CheckProxyReminder();
        _proxyReminderTimer.Start();

        // Reading the hardware profile runs system_profiler (about a second): keep it off
        // the UI thread so the menu bar icon appears at once.
        _ = Task.Run(_worker.Start);
    }

    private async Task InstallCertificateAsync()
    {
        // Already trusted: say so and stop. Running the installer would ask for an
        // administrator password to do nothing.
        if (_worker.Snapshot.CertificateTrusted)
        {
            Notifier.Show(AgentText.CertificateInstalled);
            return;
        }

        if (_installingCertificate || !_worker.Snapshot.ActionsUnlocked) return;
        _installingCertificate = true;
        Render(_worker.Snapshot);
        var message = AgentText.CertificateInstalled;
        try
        {
            await CertificateInstaller.InstallAsync();
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
        Notifier.Show(message);
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Log.Debug("Network address change; running the cycle early.");
        _worker.WakeUp();
    }

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    private void Render(AgentSnapshot snapshot)
    {
        _tray.Icon = MarkIcons.ForMenuBar(snapshot.Status);
        _outboxItem.IsVisible = snapshot.Offline;

        var headline = AgentText.Headline(snapshot);

        _statusItem.Header = headline;
        _tray.ToolTipText = $"Personicore · {headline}";

        _registerItem.Header = snapshot.OwnerNames.Count > 0 ? AgentText.ChangeOwner : AgentText.Register;

        // Until a meter node answers on this network the menu offers only looking
        // (details and the log folder) and looking again: everything else needs the node.
        var unlocked = snapshot.ActionsUnlocked;

        _registerItem.IsEnabled = unlocked && snapshot.CanRegister;

        _proxyItem.IsChecked = snapshot.ProxyWanted;
        _proxyItem.Header = snapshot.ProxyWanted && snapshot.ProxyStatus.StartsWith("paused", StringComparison.Ordinal)
            ? AgentText.ProxyItemPaused
            : AgentText.ProxyItem;

        // Switching it off is always allowed: leaving a panelist with a switch they
        // cannot undo would be worse than the lock it enforces.
        _proxyItem.IsEnabled = unlocked || snapshot.ProxyWanted;
        _proxyWindowItem.IsEnabled = unlocked || snapshot.ProxyWanted;
        _certificateItem.Header = AgentText.CertificateItem(snapshot);

        // Enabled while trusted too: the item is then a status line that reports itself when
        // clicked, and a greyed-out one reads as something broken.
        _certificateItem.IsEnabled = (unlocked || snapshot.CertificateTrusted) && !_installingCertificate;
        _loginItem.IsEnabled = unlocked;

        // Away from home the page is not there to open, and the name only resolves through
        // the node itself: an item that opens a browser on an error is worse than one that
        // is greyed out with a reason.
        _nodeItem.IsEnabled = unlocked && snapshot.NodeUrl.Length > 0;

        // All three steps can also be done from the menu, so a window left open has to be
        // told: otherwise it goes on asking for a certificate installed behind its back.
        _openSetup?.Apply(snapshot);

        // First run: the agent has never been set up on this Mac, so the assistant opens
        // itself rather than leaving a panelist to find three separate menu items.
        if (!snapshot.SetupCompleted && !_setupOffered)
        {
            _setupOffered = true;
            Notifier.Show(AgentText.FirstRunWelcomeBody(Words.Mac), AgentText.FirstRunWelcomeTitle);
            ShowSetup();
        }
    }

    /// <summary>
    /// The three first-run steps, one at a time. Opened from the menu, and by itself the
    /// first time the agent runs.
    /// </summary>
    private void ShowSetup()
    {
        if (_openSetup is not null)
        {
            _openSetup.Activate();
            return;
        }

        var setup = new SetupWindow(
            () => _worker.Snapshot,
            ShowOwnerDialog,
            InstallCertificateAsync,
            SetProxy,
            _worker.MarkSetupDone);

        _openSetup = setup;

        setup.Closed += (_, _) =>
        {
            _openSetup = null;

            // Finish records itself. Closing the window does not, except when there is
            // nothing left to walk through: re-offering three completed steps on every start
            // would be a nag the panelist has no way to stop.
            if (!setup.Finished && SetupPlan.AllDone(_worker.Snapshot)) _worker.MarkSetupDone();
        };

        setup.Show();
        setup.Activate();
    }

    /// <summary>
    /// Opens the meter node's page in the panelist's usual browser. The address is the
    /// agent's own — the one it reached the node at, or the configured name — never one
    /// read from anything the node answered.
    /// </summary>
    private void OpenNodePage()
    {
        var snapshot = _worker.Snapshot;

        if (!snapshot.ActionsUnlocked || snapshot.NodeUrl.Length == 0)
        {
            Notifier.Show(AgentText.WaitingForNode);
            _worker.WakeUp();
            return;
        }

        Shell.Open(snapshot.NodeUrl);
    }

    private void ShowOwnerDialog()
    {
        if (_openDialog is not null)
        {
            _openDialog.Activate();
            return;
        }

        var snapshot = _worker.Snapshot;
        if (!snapshot.ActionsUnlocked || !snapshot.CanRegister)
        {
            Notifier.Show(AgentText.CannotRegister(snapshot));
            _worker.WakeUp();
            return;
        }

        var dialog = new OwnerWindow(snapshot, snapshot.OwnerIds);
        _openDialog = dialog;

        dialog.Closed += async (_, _) =>
        {
            _openDialog = null;
            if (!dialog.Confirmed) return;
            await RegisterAsync(dialog);
        };

        dialog.Show();
    }

    /// <summary>
    /// Sends without blocking the UI and tells the panelist how it went: a silent
    /// failure here would leave a computer out of the panel without anyone noticing.
    /// </summary>
    private async Task RegisterAsync(OwnerWindow dialog)
    {
        var owners = dialog.SelectedOwners;
        var name = dialog.FriendlyName;
        _registerItem.IsEnabled = false;

        try
        {
            await _worker.RegisterAsync(
                owners, name, dialog.EditedProfile, dialog.PrincipalOwnerId, CancellationToken.None);

            var people = owners.Select(o => o.DisplayName);
            Notifier.Show(_worker.Snapshot.Offline
                ? AgentText.Simulated(name, people, Path.GetFileName(Outbox.FilePath))
                : AgentText.Registered(name, people));
        }
        catch (Exception ex)
        {
            Log.Error("Could not register the computer", ex);
            Notifier.Show(AgentText.RegisterFailed(ex.Message));
        }
        finally
        {
            _registerItem.IsEnabled = _worker.Snapshot.ActionsUnlocked && _worker.Snapshot.CanRegister;
        }
    }

    private void ShowDetails()
    {
        if (_openDetails is not null)
        {
            _openDetails.Activate();
            return;
        }

        _openDetails = new DetailsWindow(_worker.Snapshot);
        _openDetails.Closed += (_, _) => _openDetails = null;
        _openDetails.Show();
        _openDetails.Activate();
    }

    private static void OpenOutbox()
    {
        if (!File.Exists(Outbox.FilePath))
        {
            Notifier.Show(AgentText.OutboxEmpty);
            return;
        }

        Shell.Open(Outbox.FilePath);
    }

    /// <summary>The switch as a window, for when a checkmark in a menu is not obvious enough.</summary>
    private void ShowProxyWindow()
    {
        if (!_worker.Snapshot.ActionsUnlocked && !_worker.Snapshot.ProxyWanted)
        {
            Notifier.Show(AgentText.WaitingForNode);
            _worker.WakeUp();
            return;
        }

        if (_openProxy is not null)
        {
            _openProxy.Activate();
            return;
        }

        _openProxy = new ProxyWindow(() => _worker.Snapshot, wanted => _worker.SetProxyWanted(wanted));
        _openProxy.Closed += (_, _) => _openProxy = null;
        _openProxy.Show();
        _openProxy.Activate();
    }

    /// <summary>
    /// The switch. Turning it on for the first time installs the root helper, which asks
    /// for an administrator password once; after that there are no more prompts.
    /// </summary>
    private void ToggleProxy() => SetProxy(!_worker.Snapshot.ProxyWanted);

    /// <summary>
    /// The switch itself, so the menu line and the assistant's third step go through one
    /// path: the guard, the helper notice, the call and the notification are written once.
    /// </summary>
    private void SetProxy(bool wanted)
    {
        if (wanted == _worker.Snapshot.ProxyWanted) return;

        if (wanted && !_worker.Snapshot.ActionsUnlocked)
        {
            Notifier.Show(AgentText.WaitingForNode);
            _worker.WakeUp();
            return;
        }

        if (wanted && (!ProxyHelper.IsInstalled || ProxyHelper.NeedsUpgrade))
        {
            Notifier.Show(Words.Mac.ProxyFirstRunNotice);
        }

        _worker.SetProxyWanted(wanted);

        var status = _worker.Snapshot.ProxyStatus;
        Notifier.Show(status switch
        {
            _ when status.StartsWith("error", StringComparison.Ordinal) => AgentText.ProxyFailed(status[8..]),
            _ when !wanted => AgentText.ProxyOff,
            _ when status.StartsWith("on", StringComparison.Ordinal) =>
                AgentText.ProxyOn(status[5..], Words.Mac),
            _ => AgentText.ProxyPending,
        });
    }

    private void ToggleLogin()
    {
        if (!_worker.Snapshot.ActionsUnlocked)
        {
            Notifier.Show(AgentText.WaitingForNode);
            _worker.WakeUp();
            return;
        }

        var enabled = !LoginItem.IsEnabled();
        LoginItem.SetEnabled(enabled);
        _loginItem.IsChecked = LoginItem.IsEnabled();

        if (_loginItem.IsChecked != enabled) Notifier.Show(AgentText.StartupFailed(Words.Mac));
    }

    private void Quit()
    {
        Log.Info("User quit the agent from the menu bar.");
        _worker.PauseProxyForExit();
        _openSetup?.Close();
        _desktop.Shutdown();
    }

    public void Dispose()
    {
        _proxyReminderTimer.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _tray.IsVisible = false;
        _tray.Dispose();
        _worker.Dispose();
        Log.Info("— Personicore Device Agent (macOS) stopped");
    }
}
