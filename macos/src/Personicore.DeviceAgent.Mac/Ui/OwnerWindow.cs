using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Device;
using Personicore.DeviceAgent.Graph;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// "Who does this computer belong to?" — same content and order as the Windows dialog.
/// The household ID is never asked: it comes from the router and is only shown as
/// confirmation. Brand, model and type are editable; the OS is read-only.
///
/// Header and button bar are docked and the middle scrolls, so "Register computer" is
/// always visible whatever the screen size (the Windows dialog once hid it).
/// </summary>
internal sealed class OwnerWindow : Window
{
    private readonly IReadOnlyList<Panelist> _panelists;
    private readonly TextBox _name = Field();
    private readonly ComboBox _type = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _brand = Field();
    private readonly TextBox _model = Field();
    private readonly TextBox _os = Field(readOnly: true);
    private readonly TextBox _typedOwners = Field();
    private readonly List<CheckBox> _ownerChecks = [];

    /// <summary>
    /// Who was already the principal owner when the window opened. Without it, reopening
    /// it to correct the model handed the Mac to whoever came first in the list: the radio
    /// buttons are rebuilt from scratch and had nothing to start from.
    /// </summary>
    private readonly string _initialPrincipalId;

    /// <summary>
    /// Who the computer is mainly for. Hidden until more than one owner is checked:
    /// asking a single-owner household to nominate a principal is a question with one
    /// answer. Same block, same wording and same default as the Windows dialog.
    /// </summary>
    private readonly StackPanel _principal = new() { Spacing = 2, IsVisible = false };

    private readonly TextBlock _principalHint = new()
    {
        Text = AgentText.PrincipalHint,
        Foreground = Palette.Brush(Palette.InkSoft),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 2),
        IsVisible = false,
    };
    private readonly TextBlock _error = new() { Foreground = Palette.Brush(Palette.Bad), FontWeight = FontWeight.SemiBold };

    private static readonly string[] Types = AgentText.DeviceTypes;

    private bool TypedOwnerMode => _panelists.Count == 0;

    /// <summary>true once the panelist clicked Register with valid data.</summary>
    public bool Confirmed { get; private set; }

    public IReadOnlyList<Panelist> SelectedOwners { get; private set; } = [];

    /// <summary>Which of them the computer is mainly for.</summary>
    public string PrincipalOwnerId { get; private set; } = string.Empty;

    public string FriendlyName => _name.Text?.Trim() ?? string.Empty;

    public DeviceProfile EditedProfile { get; private set; }

    public OwnerWindow(AgentSnapshot snapshot, IReadOnlyList<string> preselectedIds)
    {
        _panelists = snapshot.Panelists;
        _initialPrincipalId = snapshot.PrincipalOwnerId;
        EditedProfile = snapshot.Profile;

        Title = AgentText.OwnerWindowTitle;
        Icon = MarkIcons.ForWindow(snapshot.Status);
        Width = 580;
        Height = 700;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.Paper);

        _name.Text = string.IsNullOrWhiteSpace(snapshot.Profile.SuggestedName)
            ? Environment.MachineName
            : snapshot.Profile.SuggestedName;
        _type.ItemsSource = Types;
        _type.SelectedItem = AgentText.NormalizeDeviceType(snapshot.Profile.DeviceType);
        _brand.Text = snapshot.Profile.Brand;
        _model.Text = snapshot.Profile.Model;
        _os.Text = string.IsNullOrWhiteSpace(snapshot.Profile.OsVersion)
            ? snapshot.Profile.OperatingSystem
            : $"{snapshot.Profile.OperatingSystem}  (build {snapshot.Profile.OsVersion})";
        _typedOwners.Text = string.Join(", ", snapshot.OwnerNames);

        foreach (var panelist in _panelists)
        {
            var check = new CheckBox
            {
                Content = panelist.DisplayName,
                IsChecked = preselectedIds.Contains(panelist.Id),
                Foreground = Palette.Brush(Palette.Ink),
            };
            check.IsCheckedChanged += (_, _) => RefreshPrincipal();
            _ownerChecks.Add(check);
        }

        var root = new DockPanel();
        var header = BuildHeader(snapshot);
        var footer = BuildFooter();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = BuildBody(snapshot) });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    /// <summary>
    /// The agent opens this on its own, so it is brought to the front: otherwise macOS
    /// leaves it behind the active app.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var available = screen.WorkingArea.Height / screen.Scaling;
            if (Height > available) Height = available;
        }

        Topmost = true;
        Activate();
        Topmost = false;
    }

    private static Control BuildHeader(AgentSnapshot snapshot)
    {
        return new Border
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
                        Text = AgentText.OwnerHeading,
                        Foreground = Palette.Brush(Palette.Paper),
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                    },
                    new TextBlock
                    {
                        Text = AgentText.OwnerSubtitle(snapshot),
                        Foreground = Palette.Brush(Palette.OnInk),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
    }

    private Control BuildBody(AgentSnapshot snapshot)
    {
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 12), Spacing = 6 };

        var identity = new StackPanel { Spacing = 2 };
        foreach (var line in AgentText.IdentityLines(snapshot)) identity.Children.Add(Mono(line));
        if (snapshot.MacIsRandomized)
        {
            identity.Children.Add(new TextBlock
            {
                Text = Words.Mac.RandomizedMacNote,
                Foreground = Palette.Brush(Palette.Signal),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        body.Children.Add(new Border
        {
            Background = Palette.Brush(Palette.PaperSunken),
            Padding = new Thickness(12, 10),
            Child = identity,
        });

        body.Children.Add(SectionLabel(AgentText.SectionDataToSend));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 6 };
        AddRow(grid, 0, AgentText.FieldName, _name);
        AddRow(grid, 1, AgentText.FieldType, _type);
        AddRow(grid, 2, AgentText.FieldBrand, _brand);
        AddRow(grid, 3, AgentText.FieldModel, _model);
        AddRow(grid, 4, AgentText.FieldOs, _os);
        body.Children.Add(grid);

        body.Children.Add(SectionLabel(AgentText.SectionOwner));
        body.Children.Add(new TextBlock
        {
            Text = TypedOwnerMode ? AgentText.TypeOwnersHint : AgentText.PickOwnersHint,
            Foreground = Palette.Brush(Palette.InkSoft),
            TextWrapping = TextWrapping.Wrap,
        });

        if (TypedOwnerMode)
        {
            body.Children.Add(_typedOwners);
        }
        else
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var check in _ownerChecks) list.Children.Add(check);

            body.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = Palette.Brush(Palette.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4),
                Child = list,
            });
        }

        if (!TypedOwnerMode)
        {
            body.Children.Add(_principalHint);
            body.Children.Add(_principal);
        }

        body.Children.Add(_error);
        RefreshPrincipal();
        return body;
    }

    private Control BuildFooter()
    {
        var cancel = new Button
        {
            Content = AgentText.ButtonNotNow,
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancel.Click += (_, _) => Close();

        var register = new Button
        {
            Content = AgentText.ButtonRegister,
            Width = 170,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Palette.Brush(Palette.Ink),
            Foreground = Palette.Brush(Palette.Paper),
            FontWeight = FontWeight.Bold,
        };
        register.Click += (_, _) => OnRegister();

        return new Border
        {
            Background = Palette.Brush(Palette.PaperSunken),
            Padding = new Thickness(20, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children = { cancel, register },
            },
        };
    }

    /// <summary>
    /// One radio per checked owner, rebuilt whenever the checks change. A choice already
    /// made survives as long as that person is still checked, so unchecking a third
    /// person does not quietly move the principal to somebody else.
    /// </summary>
    private void RefreshPrincipal()
    {
        if (TypedOwnerMode) return;

        var checkedOwners = CheckedOwners();

        // The choice already on screen wins; the first build has none, and then it is the
        // one the panelist made last time. An id, never a name: households have two
        // people called the same thing often enough.
        var previous = PrincipalId is { Length: > 0 } chosen ? chosen : _initialPrincipalId;

        _principal.Children.Clear();

        var show = checkedOwners.Count > 1;
        _principal.IsVisible = show;
        _principalHint.IsVisible = show;
        if (!show) return;

        foreach (var panelist in checkedOwners)
        {
            _principal.Children.Add(new RadioButton
            {
                Content = panelist.DisplayName,
                Tag = panelist.Id,
                GroupName = "principal",
                IsChecked = panelist.Id == previous,
                Foreground = Palette.Brush(Palette.Ink),
            });
        }

        // Nobody carried over: the first one checked is the default, which is the answer
        // for the common case of a computer shared by a couple.
        if (_principal.Children.OfType<RadioButton>().All(radio => radio.IsChecked != true))
            _principal.Children.OfType<RadioButton>().First().IsChecked = true;
    }

    private List<Panelist> CheckedOwners() =>
        _ownerChecks.Select((check, i) => (check, i))
            .Where(x => x.check.IsChecked == true)
            .Select(x => _panelists[x.i])
            .ToList();

    /// <summary>The chosen principal, or the only owner when there is nothing to choose.</summary>
    private string PrincipalId
    {
        get
        {
            var chosen = _principal.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            if (chosen is not null) return (string)chosen.Tag!;

            var owners = TypedOwnerMode ? [] : CheckedOwners();
            return owners.Count == 1 ? owners[0].Id : string.Empty;
        }
    }

    private void OnRegister()
    {
        var selected = TypedOwnerMode
            ? (_typedOwners.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new Panelist { Id = string.Empty, FirstName = name })
                .ToList()
            : CheckedOwners();

        if (selected.Count == 0)
        {
            _error.Text = TypedOwnerMode ? AgentText.ErrorNoTypedOwner : AgentText.ErrorNoSelectedOwner;
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

        Confirmed = true;
        Close();
    }

    private static TextBox Field(bool readOnly = false) => new()
    {
        IsReadOnly = readOnly,
        Background = readOnly ? Palette.Brush(Palette.PaperSunken) : Brushes.White,
        Foreground = Palette.Brush(readOnly ? Palette.InkSoft : Palette.Ink),
    };

    private static TextBlock Mono(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Menlo, Monaco, monospace"),
        FontSize = 12,
        Foreground = Palette.Brush(Palette.Ink),
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.Bold,
        Foreground = Palette.Brush(Palette.InkSoft),
        Margin = new Thickness(0, 12, 0, 2),
    };

    private static void AddRow(Grid grid, int row, string caption, Control control)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var label = new TextBlock
        {
            Text = caption,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.Ink),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(label, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);

        grid.Children.Add(label);
        grid.Children.Add(control);
    }
}
