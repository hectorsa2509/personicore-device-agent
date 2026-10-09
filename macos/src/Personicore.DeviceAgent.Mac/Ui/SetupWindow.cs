using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// The setup assistant: the three things a panelist does once, one at a time. It opens by
/// itself the first time the agent runs and stays available from the menu afterwards.
///
/// It owns no state and decides nothing. Every step's title, body, button and done-ness
/// comes from <see cref="SetupPlan"/>, and the actions are the same ones the menu calls —
/// the owner window, the certificate installer, the proxy switch — so there is no second
/// path into any of them to keep in step. The Windows window is this window, drawn with
/// WinForms.
/// </summary>
internal sealed class SetupWindow : Window
{
    private readonly Func<AgentSnapshot> _snapshot;
    private readonly Action _openOwners;
    private readonly Func<Task> _installCertificate;
    private readonly Action<bool> _setProxyWanted;
    private readonly Action _finish;

    private readonly TextBlock _progress;
    private readonly TextBlock _trail;
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly TextBlock _status;
    private readonly Button _action;
    private readonly Button _back;
    private readonly Button _next;

    private SetupStep _step;
    private bool _working;

    /// <summary>Which way the proxy step is about to move, fixed when the button is pressed.</summary>
    private bool _proxyTurningOn;

    /// <summary>
    /// Set by Finish, read by the controller when the window closes. Closing it is "later",
    /// and the assistant then opens again on the next start — the same contract the owner
    /// window already has.
    /// </summary>
    public bool Finished { get; private set; }

    public SetupWindow(
        Func<AgentSnapshot> snapshot,
        Action openOwners,
        Func<Task> installCertificate,
        Action<bool> setProxyWanted,
        Action finish)
    {
        _snapshot = snapshot;
        _openOwners = openOwners;
        _installCertificate = installCertificate;
        _setProxyWanted = setProxyWanted;
        _finish = finish;

        var current = snapshot();
        _step = SetupPlan.Opening(current);

        Title = AgentText.SetupWindowTitle;
        Icon = MarkIcons.ForWindow(current.Status);
        Width = 560;
        Height = 496;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.Paper);

        _progress = new TextBlock
        {
            Foreground = Palette.Brush(Palette.InkSoft),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
        };

        _trail = new TextBlock
        {
            Foreground = Palette.Brush(Palette.Ink),
            FontFamily = new FontFamily("Menlo"),
            FontSize = 12,
        };

        _title = new TextBlock
        {
            Foreground = Palette.Brush(Palette.Ink),
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };

        _body = new TextBlock
        {
            Foreground = Palette.Brush(Palette.InkSoft),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        _status = new TextBlock
        {
            Foreground = Palette.Brush(Palette.Ink),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        _action = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        _action.Click += async (_, _) => await RunStepAsync();

        _back = new Button { Content = AgentText.ButtonSetupBack };
        _back.Click += (_, _) => GoTo(-1);

        _next = new Button { IsDefault = true };
        _next.Click += (_, _) => OnNext();

        var root = new DockPanel();

        var header = Header();
        var footer = Footer();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);

        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(Body());

        Content = root;

        Render(current);
    }

    /// <summary>
    /// The controller pushes every snapshot in here, because all three steps can be
    /// completed from outside this window — the menu has the same items — and a step that
    /// went green elsewhere must not still be asking to be done.
    /// </summary>
    public void Apply(AgentSnapshot snapshot) => Render(snapshot);

    private Control Header() => new Border
    {
        Background = Palette.Brush(Palette.Ink),
        Padding = new Thickness(20, 14, 20, 16),
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = AgentText.SetupHeading,
                    Foreground = Palette.Brush(Palette.Paper),
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = AgentText.SetupSubtitle(Words.Mac),
                    Foreground = Palette.Brush(Palette.OnInk),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        },
    };

    private Control Body() => new StackPanel
    {
        Margin = new Thickness(20, 16, 20, 12),
        Spacing = 12,
        Children =
        {
            _progress,
            _trail,
            _title,
            _body,
            new Border
            {
                Background = Palette.Brush(Palette.PaperSunken),
                Padding = new Thickness(12, 10),
                MinHeight = 64,
                Child = _status,
            },
            _action,
        },
    };

    private Control Footer()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        Grid.SetColumn(_back, 0);
        Grid.SetColumn(_next, 2);
        grid.Children.Add(_back);
        grid.Children.Add(_next);

        return new Border
        {
            Background = Palette.Brush(Palette.PaperSunken),
            Padding = new Thickness(20, 12),
            Child = grid,
        };
    }

    private void Render(AgentSnapshot snapshot)
    {
        var view = SetupPlan.Describe(_step, snapshot, Words.Mac);
        var last = _step == SetupPlan.Steps[^1];

        _progress.Text = AgentText.SetupProgress(view.Number, SetupPlan.Count);
        _trail.Text = AgentText.SetupTrail(_step, snapshot);
        _title.Text = view.Title;
        _body.Text = view.Body;

        var working = _working ? WorkingText(_step) : string.Empty;

        _status.Text = working.Length > 0
            ? working
            : last && SetupPlan.AllDone(snapshot)
                ? AgentText.SetupAllDone
                : view.Status;

        _action.Content = view.ActionLabel;

        // The button follows the label, not the tick: a done step keeps it wherever pressing
        // it still does something — changing who uses the computer, turning the proxy back
        // off. Only the certificate goes quiet when done, and SetupPlan says why.
        var actionable = view.ActionLabel.Length > 0;
        _action.IsEnabled = actionable && view.Available && !_working;
        _action.IsVisible = actionable;

        _back.IsEnabled = view.Number > 1 && !_working;

        // "Skip for now" rather than "Next" while the step is outstanding: the button moves on
        // either way, and calling it Next would hide the fact that something was left undone.
        _next.Content = last
            ? AgentText.ButtonSetupFinish
            : view.Done ? AgentText.ButtonSetupNext : AgentText.ButtonSetupSkip;
        _next.IsEnabled = !_working;
    }

    /// <summary>
    /// What the status box says while a step is running. Empty for the panelists, because that
    /// step hands over to the owner window, which says everything itself.
    /// </summary>
    private string WorkingText(SetupStep step) => step switch
    {
        SetupStep.Certificate => AgentText.SetupCertificateWorking,
        SetupStep.Proxy => _proxyTurningOn ? AgentText.ProxyTurningOn : AgentText.ProxyTurningOff,
        _ => string.Empty,
    };

    private void GoTo(int delta)
    {
        var index = Array.IndexOf(SetupPlan.Steps, _step) + delta;
        if (index < 0 || index >= SetupPlan.Count) return;

        _step = SetupPlan.Steps[index];
        Render(_snapshot());
    }

    private void OnNext()
    {
        if (_step == SetupPlan.Steps[^1])
        {
            Finished = true;
            _finish();
            Close();
            return;
        }

        GoTo(1);
    }

    /// <summary>
    /// Runs the current step by calling exactly what the menu calls. Nothing is duplicated
    /// here: the owner window, the certificate installer and the proxy switch all keep their
    /// own guards, their own notifications and their own error handling.
    /// </summary>
    private async Task RunStepAsync()
    {
        if (_working) return;

        // Captured before anything moves: the status line has to say which way the proxy is
        // going, and the snapshot flips under it the moment the switch takes.
        _proxyTurningOn = !_snapshot().ProxyWanted;

        _working = true;
        Render(_snapshot());

        try
        {
            switch (_step)
            {
                case SetupStep.Owners:
                    // The owner window reports its own result, and registration is sent in the
                    // background: the step turns green on a later snapshot, not when it closes.
                    _openOwners();
                    break;

                case SetupStep.Certificate:
                    await _installCertificate();
                    break;

                case SetupStep.Proxy:
                    _setProxyWanted(_proxyTurningOn);
                    break;
            }
        }
        catch (Exception ex)
        {
            // This runs from an async void click handler: without catching here the exception
            // is lost and the window is left looking busy for ever.
            Config.Log.Error($"Setup step {_step} failed", ex);
        }
        finally
        {
            _working = false;
            Render(_snapshot());
        }
    }
}
