using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Device;
using Personicore.DeviceAgent.Graph;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// "Who does this computer belong to?" — same content and order as the macOS dialog.
/// The household id is never asked: it comes resolved from the router and is only shown
/// as confirmation. Brand, model and type arrive filled in from the computer itself and
/// stay editable because many OEM BIOSes lie; the operating system is read-only, because
/// there the system's own answer always wins.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OwnerForm : Form
{
    private readonly TextBox _name = Field();
    // DropDownList, not DropDown: the panelist picks, never types. presenceCreateDevice
    // accepts five exact values and "Laptop" is already a refusal, so an editable box was
    // a free-text field wired straight to a validated enum. macOS has always been a
    // picker here, and the two agents are the same product.
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = BodyFont };
    private readonly TextBox _brand = Field();
    private readonly TextBox _model = Field();
    private readonly TextBox _os = Field(readOnly: true);
    private readonly CheckedListBox _owners = new()
    {
        CheckOnClick = true,
        Font = BodyFont,
        BorderStyle = BorderStyle.FixedSingle,
        IntegralHeight = false,
    };
    private readonly Label _error = new()
    {
        AutoSize = false,
        ForeColor = Palette.Bad,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        Text = string.Empty,
    };

    private readonly TextBox _typedOwners = Field();
    private readonly IReadOnlyList<Panelist> _panelists;

    /// <summary>
    /// Who was already the principal owner when the dialog opened. Without it, reopening
    /// the dialog to correct the model handed the computer to whoever came first in the
    /// list: the radio buttons are rebuilt from scratch and had nothing to start from.
    /// </summary>
    private readonly string _initialPrincipalId;

    /// <summary>
    /// Who the computer is mainly for. Only meaningful once more than one owner is
    /// checked, so the whole block stays hidden until then: asking a single-owner
    /// household to nominate a principal is a question with one answer.
    /// </summary>
    /// <remarks>
    /// A flow panel and not a plain one with docked children: this is a stack that
    /// sometimes has to scroll, which is exactly what FlowLayoutPanel does and what
    /// Dock.Top inside an AutoScroll panel does badly.
    /// </remarks>
    private readonly FlowLayoutPanel _principal = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Dock = DockStyle.Fill,
        Visible = false,
    };

    private readonly Label _principalHint = new()
    {
        Text = AgentText.PrincipalHint,
        ForeColor = Palette.InkSoft,
        AutoSize = false,
        Dock = DockStyle.Fill,
        Visible = false,
    };

    /// <summary>The body, kept so the principal block can be given the height it needs.</summary>
    private TableLayoutPanel? _body;

    private int _principalRow = -1;
    private int _principalHintRow = -1;
    private int _ownersRow = -1;

    /// <summary>
    /// How tall one name is in the principal block, and how many are shown before it
    /// starts scrolling. Derived from the font because the form is scaled for the
    /// display: a pixel count fixed here at run time would stay at 100 % while
    /// everything around it grew.
    /// </summary>
    private int PrincipalRowHeight => Font.Height + 7;

    private const int PrincipalVisibleRows = 4;

    /// <summary>How many household members are shown before the list scrolls.</summary>
    private const int OwnersVisibleRows = 6;

    /// <summary>
    /// Offline there is no panelist list to offer — only the API has it — so the question
    /// is asked anyway, by hand. What matters is that it is asked, and it is in both modes.
    /// </summary>
    private bool TypedOwnerMode => _panelists.Count == 0;

    private static readonly Font BodyFont = new("Segoe UI", 9.75f);
    private static readonly Font LabelFont = new("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Font MonoFont = new("Consolas", 9f);

    /// <summary>Checked panelists. More than one ⇒ the backend marks it as shared.</summary>
    public IReadOnlyList<Panelist> SelectedOwners { get; private set; } = [];

    /// <summary>Which of them the computer is mainly for.</summary>
    public string PrincipalOwnerId { get; private set; } = string.Empty;

    public string FriendlyName => _name.Text.Trim();

    /// <summary>The final profile, with whatever the panelist corrected.</summary>
    public DeviceProfile EditedProfile { get; private set; }

    public OwnerForm(AgentSnapshot snapshot, IReadOnlyList<string> preselectedIds)
    {
        _panelists = snapshot.Panelists;
        _initialPrincipalId = snapshot.PrincipalOwnerId;
        EditedProfile = snapshot.Profile;

        // Every measurement in this form is in pixels at 96 DPI. Without scaling, at
        // 125 % or 150 % — the norm on laptops — the text grows (it is in points) but the
        // boxes do not, and the dialog comes out clipped and overlapping.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = AgentText.OwnerWindowTitle;
        // Positioned by hand in OnLoad, once the DPI scaling is known (see FitToScreen).
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = Palette.Paper;
        ForeColor = Palette.Ink;
        Font = BodyFont;
        ClientSize = new Size(580, 700);
        Icon = (Icon)TrayIcons.ForWindow(snapshot.Status).Clone();

        _name.Text = string.IsNullOrWhiteSpace(snapshot.Profile.SuggestedName)
            ? Environment.MachineName
            : snapshot.Profile.SuggestedName;

        _type.Items.AddRange(AgentText.DeviceTypes);

        // A DropDownList ignores Text; the selection is set by value, and the detected
        // type is normalised first so an unrecognised one lands on "computer" rather than
        // leaving the box blank.
        _type.SelectedItem = AgentText.NormalizeDeviceType(snapshot.Profile.DeviceType);
        _brand.Text = snapshot.Profile.Brand;
        _model.Text = snapshot.Profile.Model;
        _os.Text = string.IsNullOrWhiteSpace(snapshot.Profile.OsVersion)
            ? snapshot.Profile.OperatingSystem
            : $"{snapshot.Profile.OperatingSystem}  (build {snapshot.Profile.OsVersion})";

        foreach (var panelist in _panelists)
            _owners.Items.Add(panelist.DisplayName, preselectedIds.Contains(panelist.Id));

        // ItemCheck fires *before* the item's state flips, so the rebuild is queued to
        // run after it: reading the list inside the event would miss the click itself.
        _owners.ItemCheck += (_, _) => BeginInvoke(RefreshPrincipal);

        _typedOwners.Text = string.Join(", ", snapshot.OwnerNames);

        Controls.Add(BuildBody(snapshot));
        Controls.Add(BuildHeader(snapshot));
        Controls.Add(BuildFooter());

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>
    /// The agent opens this dialog on its own, not from a click, and Windows does not
    /// let a background process come to the front: without this it would appear behind
    /// the active window and only blink in the taskbar.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToScreen();
        FitOwnersList();
        RefreshPrincipal();
    }

    /// <summary>
    /// The household list, sized to whole names. At a fixed height the last one visible
    /// was sliced through the middle, which reads as a broken dialog rather than as a
    /// list with more in it. Run once the handle exists, because that is when the item
    /// height is the real one for this display.
    /// </summary>
    private void FitOwnersList()
    {
        if (TypedOwnerMode || _ownersRow < 0) return;

        var rows = Math.Clamp(_panelists.Count, 1, OwnersVisibleRows);
        SetRowHeight(_ownersRow, rows * _owners.ItemHeight + 6);
    }

    /// <summary>
    /// One radio per checked owner, rebuilt whenever the checks change. A choice already
    /// made is kept as long as that person is still checked, so unchecking a third
    /// person does not quietly move the principal to somebody else.
    /// </summary>
    private void RefreshPrincipal()
    {
        if (TypedOwnerMode || IsDisposed) return;

        var checkedOwners = SelectedFromList();

        // The choice already on screen wins; the first build has none, and then it is the
        // one the panelist made last time. An id, never a name: households have two
        // people called the same thing often enough.
        var previous = PrincipalId is { Length: > 0 } chosen ? chosen : _initialPrincipalId;

        _principal.Controls.Clear();

        var show = checkedOwners.Count > 1;
        _principal.Visible = show;
        _principalHint.Visible = show;

        if (!show)
        {
            // A hidden control still holds its row open, so a household where one person
            // was checked was left looking at a gap where the question would have been.
            SetRowHeight(_principalHintRow, 0);
            SetRowHeight(_principalRow, 0);
            return;
        }

        foreach (var panelist in checkedOwners)
        {
            _principal.Controls.Add(new RadioButton
            {
                Text = panelist.DisplayName,
                Tag = panelist.Id,
                Checked = panelist.Id == previous,
                AutoSize = true,
                MinimumSize = new Size(0, PrincipalRowHeight),
                Margin = Padding.Empty,
                ForeColor = Palette.Ink,
            });
        }

        // The block is given the height its names need, up to four of them; past that it
        // scrolls. It used to be a fixed 62 pixels whatever the household: with three
        // people one name showed, and the two below it could not be reached.
        SetRowHeight(_principalHintRow, PrincipalRowHeight);
        SetRowHeight(
            _principalRow,
            Math.Min(checkedOwners.Count, PrincipalVisibleRows) * PrincipalRowHeight + 8);

        // Nobody carried over: the first one checked is the default, which is the answer
        // for the common case of a computer shared by a couple.
        var radios = _principal.Controls.OfType<RadioButton>().ToList();
        if (radios.All(radio => !radio.Checked)) radios[0].Checked = true;
    }

    /// <summary>The chosen principal, or the only owner when there is nothing to choose.</summary>
    private string PrincipalId
    {
        get
        {
            var chosen = _principal.Controls.OfType<RadioButton>().FirstOrDefault(radio => radio.Checked);
            if (chosen is not null) return (string)chosen.Tag!;

            var owners = TypedOwnerMode ? new List<Panelist>() : SelectedFromList();
            return owners.Count == 1 ? owners[0].Id : string.Empty;
        }
    }

    /// <summary>
    /// At 125-150 % display scaling the dialog can be taller than the screen, which
    /// pushed the "Register computer" button out of view. The form is capped to the
    /// working area (taskbar excluded) and centered on it: the header and the button
    /// bar are docked, so they always stay visible and only the middle section scrolls.
    /// </summary>
    private void FitToScreen()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;

        if (Height > area.Height) Height = area.Height;
        if (Width > area.Width) Width = area.Width;

        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
    }

    private static Control BuildHeader(AgentSnapshot snapshot)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Palette.Ink };

        header.Controls.Add(new Label
        {
            Text = AgentText.OwnerHeading,
            ForeColor = Palette.Paper,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14),
        });

        header.Controls.Add(new Label
        {
            Text = AgentText.OwnerSubtitle(snapshot),
            ForeColor = Palette.OnInk,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Location = new Point(22, 46),
        });

        return header;
    }

    private Control BuildBody(AgentSnapshot snapshot)
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(20, 16, 20, 8),
            BackColor = Palette.Paper,
            AutoScroll = true,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _body = body;

        AddFullWidth(body, BuildIdentityCard(snapshot));
        AddFullWidth(body, SectionLabel(AgentText.SectionDataToSend));

        AddRow(body, AgentText.FieldName, _name);
        AddRow(body, AgentText.FieldType, _type);
        AddRow(body, AgentText.FieldBrand, _brand);
        AddRow(body, AgentText.FieldModel, _model);
        AddRow(body, AgentText.FieldOs, _os);

        AddFullWidth(body, SectionLabel(AgentText.SectionOwner));
        AddFullWidth(body, new Label
        {
            Text = TypedOwnerMode ? AgentText.TypeOwnersHint : AgentText.PickOwnersHint,
            ForeColor = Palette.InkSoft,
            AutoSize = false,
            Height = TypedOwnerMode ? 44 : 32,
            Dock = DockStyle.Fill,
        });

        if (TypedOwnerMode)
        {
            _typedOwners.Height = 26;
            AddFullWidth(body, _typedOwners);
        }
        else
        {
            _owners.Height = 120;
            _owners.Dock = DockStyle.Fill;
            _ownersRow = AddFullWidth(body, _owners);

            // Both start with no height at all: RefreshPrincipal gives them one the
            // moment a second owner is checked, and takes it away again when there is
            // only one.
            _principalHint.Height = 0;
            _principalHintRow = AddFullWidth(body, _principalHint);

            _principal.Height = 0;
            _principalRow = AddFullWidth(body, _principal);
        }

        _error.Height = 24;
        AddFullWidth(body, _error);

        return body;
    }

    /// <summary>
    /// Identity card: what the meter node sees of this computer. Deliberately not
    /// editable — the MAC is the key, and it is observed, not declared.
    ///
    /// The card is sized to its contents. It used to be a fixed 96px, which clipped the
    /// randomized-address note off the bottom whenever that note was the reason the
    /// panelist was reading the card.
    /// </summary>
    private static Control BuildIdentityCard(AgentSnapshot snapshot)
    {
        var card = new Panel
        {
            BackColor = Palette.PaperSunken,
            Padding = new Padding(12, 10, 12, 10),
            Height = snapshot.MacIsRandomized ? 134 : 78,
            Dock = DockStyle.Fill,
        };

        card.Controls.Add(new Label
        {
            Text = string.Join(Environment.NewLine, AgentText.IdentityLines(snapshot)),
            Font = MonoFont,
            ForeColor = Palette.Ink,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 54,
        });

        if (snapshot.MacIsRandomized)
        {
            card.Controls.Add(new Label
            {
                Text = Words.Windows.RandomizedMacNote,
                ForeColor = Palette.Signal,
                Font = new Font("Segoe UI", 8.5f),
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = 52,
            });
        }

        return card;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Palette.PaperSunken };

        var register = new Button
        {
            Text = AgentText.ButtonRegister,
            BackColor = Palette.Ink,
            ForeColor = Palette.Paper,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
            Size = new Size(160, 34),
            Location = new Point(400, 12),
        };
        register.FlatAppearance.BorderSize = 0;
        register.Click += OnRegister;

        var cancel = new Button
        {
            Text = AgentText.ButtonNotNow,
            BackColor = Palette.PaperSunken,
            ForeColor = Palette.Ink,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(110, 34),
            Location = new Point(280, 12),
            DialogResult = DialogResult.Cancel,
        };
        cancel.FlatAppearance.BorderColor = Palette.Line;

        footer.Controls.Add(register);
        footer.Controls.Add(cancel);

        AcceptButton = register;
        CancelButton = cancel;

        return footer;
    }

    private void OnRegister(object? sender, EventArgs e)
    {
        var selected = TypedOwnerMode ? TypedOwners() : SelectedFromList();

        if (selected.Count == 0)
        {
            _error.Text = TypedOwnerMode ? AgentText.ErrorNoTypedOwner : AgentText.ErrorNoSelectedOwner;
            (TypedOwnerMode ? (Control)_typedOwners : _owners).Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _error.Text = AgentText.ErrorNoName;
            _name.Focus();
            return;
        }

        var principal = selected.Count == 1 ? selected[0].Id : PrincipalId;

        if (selected.Count > 1 && principal.Length == 0)
        {
            _error.Text = AgentText.ErrorNoPrincipal;
            return;
        }

        SelectedOwners = selected;
        PrincipalOwnerId = principal;
        EditedProfile = EditedProfile with
        {
            DeviceType = AgentText.NormalizeDeviceType(_type.SelectedItem as string),
            Brand = string.IsNullOrWhiteSpace(_brand.Text) ? "Unknown" : _brand.Text.Trim(),
            Model = string.IsNullOrWhiteSpace(_model.Text) ? "Unknown" : _model.Text.Trim(),
            SuggestedName = _name.Text.Trim(),
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private List<Panelist> SelectedFromList() =>
        _owners.CheckedIndices.Cast<int>().Select(i => _panelists[i]).ToList();

    /// <summary>
    /// Hand-typed names. They come back as Panelist with an empty Id: the dump then says
    /// plainly that the real identifiers are still pending from the API, instead of
    /// inventing ones that would have to be reconciled later.
    /// </summary>
    private List<Panelist> TypedOwners() =>
        _typedOwners.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new Panelist { Id = string.Empty, FirstName = name })
            .ToList();

    private static TextBox Field(bool readOnly = false) => new()
    {
        Font = BodyFont,
        BorderStyle = BorderStyle.FixedSingle,
        ReadOnly = readOnly,
        BackColor = readOnly ? Palette.PaperSunken : Color.White,
        Dock = DockStyle.Fill,
    };

    private static Label SectionLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
        ForeColor = Palette.InkSoft,
        AutoSize = false,
        Height = 28,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.BottomLeft,
    };

    /// <summary>Caption on the left, field on the right.</summary>
    private static void AddRow(TableLayoutPanel host, string caption, Control control)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 3, 0, 3);

        var row = NewRow(host, 30);

        host.Controls.Add(new Label
        {
            Text = caption,
            Font = LabelFont,
            ForeColor = Palette.Ink,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);

        host.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// A row across both columns. The height is fixed to the control's: leaving it on
    /// AutoSize collapses any Dock.Fill child inside a TableLayoutPanel.
    /// </summary>
    /// <returns>The row, for the ones whose height changes while the dialog is open.</returns>
    private static int AddFullWidth(TableLayoutPanel host, Control control)
    {
        var row = NewRow(host, control.Height);

        control.Margin = new Padding(0, 4, 0, 4);
        control.Dock = DockStyle.Fill;

        host.Controls.Add(control, 0, row);
        host.SetColumnSpan(control, 2);
        return row;
    }

    /// <summary>
    /// One more row, and the cell to put a control in.
    /// </summary>
    /// <remarks>
    /// The cell is given explicitly because a TableLayoutPanel placing its children
    /// automatically skips the ones that are not visible, and the two controls of the
    /// principal block start hidden. When they appeared they took whatever cells were
    /// left over, with whatever heights those rows had been given: the question came out
    /// underneath its own answer, in a row too short to show a second name.
    /// </remarks>
    private static int NewRow(TableLayoutPanel host, int height)
    {
        var row = host.RowStyles.Count;
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        host.RowCount = row + 1;
        return row;
    }

    /// <summary>
    /// Re-sizes one row while the dialog is open. RowStyles are positional, so the index
    /// is the one <see cref="NewRow"/> handed out when the control was added.
    /// </summary>
    private void SetRowHeight(int row, int height)
    {
        if (_body is null || row < 0 || row >= _body.RowStyles.Count) return;
        _body.RowStyles[row] = new RowStyle(SizeType.Absolute, height);
    }
}
