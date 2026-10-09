using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// The setup assistant: the three things a panelist does once, one at a time. It opens by
/// itself the first time the agent runs and stays available from the menu afterwards.
///
/// It owns no state and decides nothing. Every step's title, body, button and done-ness
/// comes from <see cref="SetupPlan"/>, and the actions are the same ones the menu calls —
/// the owner dialog, the certificate installer, the proxy switch — so there is no second
/// path into any of them to keep in step. The macOS window is this window, drawn with
/// Avalonia.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SetupForm : Form
{
    private readonly Func<AgentSnapshot> _snapshot;
    private readonly Action _openOwners;
    private readonly Func<Task> _installCertificate;
    private readonly Action<bool> _setProxyWanted;
    private readonly Action _finish;

    private readonly Label _trail;
    private readonly Label _progress;
    private readonly Label _title;
    private readonly Label _body;
    private readonly Label _status;
    private readonly Button _action;
    private readonly Button _back;
    private readonly Button _next;

    private SetupStep _step;

    /// <summary>
    /// Set by Finish, read by the owner of the window when it closes. Closing with the X is
    /// "later", and the assistant then opens again on the next start — the same contract the
    /// owner dialog already has.
    /// </summary>
    public bool Finished { get; private set; }

    /// <summary>True while an action is running, so the button cannot be pressed twice.</summary>
    private bool _working;

    /// <summary>Which way the proxy step is about to move, fixed when the button is pressed.</summary>
    private bool _proxyTurningOn;

    public SetupForm(
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

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = AgentText.SetupWindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = Palette.Paper;
        ForeColor = Palette.Ink;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(560, 472);
        Icon = (Icon)TrayIcons.ForWindow(current.Status).Clone();

        _progress = new Label
        {
            Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
            ForeColor = Palette.InkSoft,
            AutoSize = false,
            Height = 20,
            Dock = DockStyle.Top,
        };

        _trail = new Label
        {
            Font = new Font("Consolas", 9.5f),
            ForeColor = Palette.Ink,
            AutoSize = false,
            Height = 28,
            Dock = DockStyle.Top,
        };

        _title = new Label
        {
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Palette.Ink,
            AutoSize = false,
            Height = 34,
            Dock = DockStyle.Top,
        };

        _body = new Label
        {
            ForeColor = Palette.InkSoft,
            AutoSize = false,
            Height = 76,
            Dock = DockStyle.Top,
        };

        _status = new Label
        {
            ForeColor = Palette.Ink,
            AutoSize = false,
            Dock = DockStyle.Fill,
        };

        // Sized to itself and left-aligned inside a docked strip, not docked itself: a
        // Dock.Top button stretches the full width, and the macOS one is a normal button.
        _action = new Button
        {
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
            Size = new Size(250, 34),
            Location = new Point(0, 8),
        };
        _action.FlatAppearance.BorderSize = 0;
        _action.Click += async (_, _) => await RunStepAsync();

        _back = new Button
        {
            Text = AgentText.ButtonSetupBack,
            BackColor = Palette.PaperSunken,
            ForeColor = Palette.Ink,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(100, 32),
            Location = new Point(20, 12),
        };
        _back.FlatAppearance.BorderColor = Palette.Line;
        _back.Click += (_, _) => GoTo(-1);

        _next = new Button
        {
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
            Size = new Size(140, 32),
            Location = new Point(404, 12),
        };
        _next.FlatAppearance.BorderSize = 0;
        _next.Click += (_, _) => OnNext();

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader(current));
        Controls.Add(BuildFooter());

        AcceptButton = _next;

        ResumeLayout(false);
        PerformLayout();

        Render(current);
    }

    /// <summary>
    /// The tray pushes every snapshot in here, because all three steps can be completed
    /// from outside this window — the menu has the same items — and a step that went green
    /// elsewhere must not still be asking to be done.
    /// </summary>
    public void Apply(AgentSnapshot snapshot)
    {
        if (IsDisposed) return;
        Render(snapshot);
    }

    /// <summary>
    /// Windows does not let a background process come to the front, and this window is
    /// opened by the agent rather than by a click: without this it would sit behind
    /// whatever the panelist is doing and only blink in the taskbar.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
    }

    private static Control BuildHeader(AgentSnapshot snapshot)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Palette.Ink };

        header.Controls.Add(new Label
        {
            Text = AgentText.SetupHeading,
            ForeColor = Palette.Paper,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14),
        });

        header.Controls.Add(new Label
        {
            Text = AgentText.SetupSubtitle(Words.Windows),
            ForeColor = Palette.OnInk,
            Font = new Font("Segoe UI", 9f),
            AutoSize = false,
            Size = new Size(510, 34),
            Location = new Point(22, 44),
        });

        return header;
    }

    /// <summary>
    /// Docked children are laid out last-added-first, so these are added bottom-up to read
    /// top-down: where we are, the three steps, this step's name, what it does, what it
    /// says now, and the button that does it.
    /// </summary>
    private Control BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 8) };

        var statusBox = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Palette.PaperSunken,
            Padding = new Padding(12, 10, 12, 10),
        };
        statusBox.Controls.Add(_status);

        var actionStrip = new Panel { Dock = DockStyle.Top, Height = 50 };
        actionStrip.Controls.Add(_action);

        body.Controls.Add(actionStrip);
        body.Controls.Add(statusBox);
        body.Controls.Add(_body);
        body.Controls.Add(_title);
        body.Controls.Add(_trail);
        body.Controls.Add(_progress);

        return body;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Palette.PaperSunken };
        footer.Controls.Add(_back);
        footer.Controls.Add(_next);
        return footer;
    }

    private void Render(AgentSnapshot snapshot)
    {
        var view = SetupPlan.Describe(_step, snapshot, Words.Windows);
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

        _action.Text = view.ActionLabel;

        // The button follows the label, not the tick: a done step keeps it wherever pressing
        // it still does something — changing who uses the computer, turning the proxy back
        // off. Only the certificate goes quiet when done, and SetupPlan says why. The strip
        // keeps its height, so hiding the button does not make the rest of the window jump. The strip keeps its height, so
        // hiding the button does not make the rest of the window jump.
        var actionable = view.ActionLabel.Length > 0;
        _action.Enabled = actionable && view.Available && !_working;
        _action.Visible = actionable;

        _back.Enabled = view.Number > 1 && !_working;

        // "Skip for now" rather than "Next" while the step is outstanding: the button moves
        // on either way, and calling it Next would hide the fact that something was left undone.
        _next.Text = last
            ? AgentText.ButtonSetupFinish
            : view.Done ? AgentText.ButtonSetupNext : AgentText.ButtonSetupSkip;
        _next.Enabled = !_working;
    }

    /// <summary>
    /// What the status box says while a step is running. Empty for the panelists, because
    /// that step hands over to a modal dialog that says everything itself — a "working…" line
    /// behind it would be describing a window the panelist cannot see past.
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
    /// here: the owner dialog, the certificate installer and the proxy switch all keep their
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
                    // Modal on top of this window, and it reports its own result. Registration
                    // is sent in the background, so the step turns green on a later snapshot
                    // rather than when the dialog closes.
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
            // reaches Application.ThreadException and the window is left looking busy for ever.
            Config.Log.Error($"Setup step {_step} failed", ex);
        }
        finally
        {
            _working = false;
            if (!IsDisposed) Render(_snapshot());
        }
    }
}
