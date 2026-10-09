using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Mac.Platform;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// The proxy switch as an actual switch. The menu has a checkable line too, but a
/// checkmark in a menu is easy to miss: this window says in one place whether the proxy
/// is on, where it points, and what it costs (HTTPS warnings until the router CA is
/// trusted).
/// </summary>
internal sealed class ProxyWindow : Window
{
    private readonly ToggleSwitch _switch;
    private readonly TextBlock _status;
    private readonly Action<bool> _setWanted;
    private readonly Func<AgentSnapshot> _snapshot;
    private ChromeSetupWindow? _chromeSetup;

    public ProxyWindow(Func<AgentSnapshot> snapshot, Action<bool> setWanted)
    {
        _snapshot = snapshot;
        _setWanted = setWanted;

        var current = snapshot();

        Title = AgentText.ProxyWindowTitle;
        Icon = MarkIcons.ForWindow(current.Status);
        Width = 520;
        Height = 500;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.Paper);

        _switch = new ToggleSwitch
        {
            IsChecked = current.ProxyWanted,
            OnContent = "On",
            OffContent = "Off",
            Foreground = Palette.Brush(Palette.Ink),
        };
        _switch.IsCheckedChanged += OnToggled;

        _status = new TextBlock
        {
            Text = AgentText.DescribeProxy(current, Words.Mac),
            Foreground = Palette.Brush(Palette.InkSoft),
            TextWrapping = TextWrapping.Wrap,
        };

        var close = new Button { Content = AgentText.ButtonClose, IsDefault = true };
        close.Click += (_, _) => Close();

        Content = new DockPanel
        {
            Children =
            {
                Header(),
                Footer(close),
                Body(),
            },
        };

        DockPanel.SetDock((Control)((DockPanel)Content).Children[0], Dock.Top);
        DockPanel.SetDock((Control)((DockPanel)Content).Children[1], Dock.Bottom);
    }

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
                    Text = AgentText.ProxyHeading,
                    Foreground = Palette.Brush(Palette.Paper),
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                },
                new TextBlock
                {
                    Text = AgentText.ProxySubtitle(Words.Mac),
                    Foreground = Palette.Brush(Palette.OnInk),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        },
    };

    private Control Body() => new StackPanel
    {
        Margin = new Thickness(20, 18, 20, 12),
        Spacing = 14,
        Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Children =
                {
                    _switch,
                    new TextBlock
                    {
                        Text = AgentText.ProxySwitchLabel,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Palette.Brush(Palette.Ink),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            new Border
            {
                Background = Palette.Brush(Palette.PaperSunken),
                Padding = new Thickness(12, 10),
                Child = _status,
            },
            new TextBlock
            {
                Text = Words.Mac.ProxyAdminNote,
                Foreground = Palette.Brush(Palette.InkSoft),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
            new TextBlock
            {
                Text = AgentText.ProxyCertificateWarning,
                Foreground = Palette.Brush(Palette.Signal),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
            ChromeButton(),
        },
    };

    private Button ChromeButton()
    {
        var button = new Button { Content = AgentText.ChromeSetup };
        button.Click += (_, _) =>
        {
            if (_chromeSetup is not null) { _chromeSetup.Activate(); return; }
            _chromeSetup = new ChromeSetupWindow(_snapshot);
            _chromeSetup.Closed += (_, _) => _chromeSetup = null;
            _chromeSetup.Show(this);
        };
        Closed += (_, _) => _chromeSetup?.Close();
        return button;
    }

    private static Control Footer(Control close) => new Border
    {
        Background = Palette.Brush(Palette.PaperSunken),
        Padding = new Thickness(20, 12),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { close },
        },
    };

    private void OnToggled(object? sender, RoutedEventArgs e)
    {
        var wanted = _switch.IsChecked == true;
        if (wanted == _snapshot().ProxyWanted) return;

        _status.Text = wanted ? AgentText.ProxyTurningOn : AgentText.ProxyTurningOff;
        _setWanted(wanted);

        var after = _snapshot();
        _status.Text = AgentText.DescribeProxy(after, Words.Mac);

        // The switch follows what actually happened: an install that was cancelled, or a
        // helper that refused, leaves it off.
        _switch.IsChecked = after.ProxyWanted;
    }

}
