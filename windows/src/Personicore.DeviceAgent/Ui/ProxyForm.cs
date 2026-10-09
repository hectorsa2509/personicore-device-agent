using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// The proxy switch as an actual window, mirroring the macOS one section for section.
/// The tray menu has a checkable line too, but a checkmark in a menu is easy to miss:
/// this says in one place whether the proxy is on, where it points, and what it costs
/// (HTTPS warnings until the Personicore certificate is trusted).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxyForm : Form
{
    private readonly CheckBox _switch;
    private readonly Label _status;
    private readonly Func<AgentSnapshot> _snapshot;
    private readonly Action<bool> _setWanted;

    /// <summary>Set while the code updates the checkbox, so it does not re-enter the toggle.</summary>
    private bool _syncing;

    public ProxyForm(Func<AgentSnapshot> snapshot, Action<bool> setWanted)
    {
        _snapshot = snapshot;
        _setWanted = setWanted;

        var current = snapshot();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = AgentText.ProxyWindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 454);
        BackColor = Palette.Paper;
        ForeColor = Palette.Ink;
        Font = new Font("Segoe UI", 9.75f);
        Icon = (Icon)TrayIcons.ForWindow(current.Status).Clone();

        _switch = new CheckBox
        {
            Text = AgentText.ProxySwitchLabel,
            Checked = current.ProxyWanted,
            Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
            AutoSize = false,
            Height = 30,
            Dock = DockStyle.Top,
        };
        _switch.CheckedChanged += OnToggled;

        _status = new Label
        {
            Text = AgentText.DescribeProxy(current, Words.Windows),
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = Palette.InkSoft,
        };

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());
        Controls.Add(BuildFooter());

        ResumeLayout(false);
        PerformLayout();
    }

    private static Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Palette.Ink };

        header.Controls.Add(new Label
        {
            Text = AgentText.ProxyHeading,
            ForeColor = Palette.Paper,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14),
        });

        header.Controls.Add(new Label
        {
            Text = AgentText.ProxySubtitle(Words.Windows),
            ForeColor = Palette.OnInk,
            Font = new Font("Segoe UI", 9f),
            AutoSize = false,
            Size = new Size(470, 20),
            Location = new Point(22, 46),
        });

        return header;
    }

    /// <summary>
    /// Docked children are laid out last-added-first, so these are added bottom-up to
    /// read top-down: switch, what it is doing now, what it costs, the warning.
    /// </summary>
    private Control BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 8) };

        body.Controls.Add(new Label
        {
            Text = AgentText.ProxyCertificateWarning,
            ForeColor = Palette.Signal,
            Font = new Font("Segoe UI", 8.5f),
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 44,
        });

        body.Controls.Add(new Label
        {
            Text = Words.Windows.ProxyAdminNote,
            ForeColor = Palette.InkSoft,
            Font = new Font("Segoe UI", 8.5f),
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 62,
        });

        var statusBox = new Panel
        {
            Dock = DockStyle.Top,
            Height = 132,
            BackColor = Palette.PaperSunken,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 12),
        };
        statusBox.Controls.Add(_status);
        body.Controls.Add(statusBox);

        body.Controls.Add(_switch);

        return body;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Palette.PaperSunken };

        var close = new Button
        {
            Text = AgentText.ButtonClose,
            Size = new Size(100, 32),
            Location = new Point(404, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            DialogResult = DialogResult.OK,
        };
        close.FlatAppearance.BorderSize = 0;

        footer.Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        return footer;
    }

    private void OnToggled(object? sender, EventArgs e)
    {
        if (_syncing) return;

        var wanted = _switch.Checked;
        if (wanted == _snapshot().ProxyWanted) return;

        _status.Text = wanted ? AgentText.ProxyTurningOn : AgentText.ProxyTurningOff;
        _status.Refresh();

        _setWanted(wanted);

        var after = _snapshot();
        _status.Text = AgentText.DescribeProxy(after, Words.Windows);

        // The switch follows what actually happened, not what was asked: a change the
        // system refused must leave the box unticked.
        _syncing = true;
        _switch.Checked = after.ProxyWanted;
        _syncing = false;
    }
}
