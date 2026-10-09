using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;


namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// Diagnostics window: everything the agent knows, as copyable text. It is the first
/// thing a panelist is asked for when something does not add up in the panel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DetailsForm : Form
{
    public DetailsForm(AgentSnapshot snapshot)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = AgentText.DetailsWindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(620, 460);
        BackColor = Palette.Paper;
        Icon = (Icon)TrayIcons.ForWindow(snapshot.Status).Clone();

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.White,
            BorderStyle = BorderStyle.None,
            Text = AgentText.DetailsReport(snapshot, Words.Windows),
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Palette.PaperSunken };

        var openLog = new Button
        {
            Text = AgentText.ButtonOpenLogs,
            Size = new Size(200, 32),
            Location = new Point(16, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.PaperSunken,
        };
        openLog.FlatAppearance.BorderColor = Palette.Line;
        openLog.Click += (_, _) => OpenLogFolder();

        var copy = new Button
        {
            Text = AgentText.ButtonCopy,
            Size = new Size(100, 32),
            Location = new Point(226, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.PaperSunken,
        };
        copy.FlatAppearance.BorderColor = Palette.Line;
        copy.Click += (_, _) => Clipboard.SetText(text.Text);

        var close = new Button
        {
            Text = AgentText.ButtonClose,
            Size = new Size(100, 32),
            Location = new Point(496, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            DialogResult = DialogResult.OK,
        };
        close.FlatAppearance.BorderSize = 0;

        footer.Controls.AddRange([openLog, copy, close]);

        var padded = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 16, 8) };
        padded.Controls.Add(text);

        Controls.Add(padded);
        Controls.Add(footer);

        AcceptButton = close;
        CancelButton = close;

        ResumeLayout(false);
        PerformLayout();
    }


    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Config.Log.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Config.Log.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Config.Log.Error("Could not open the logs folder", ex);
        }
    }
}
