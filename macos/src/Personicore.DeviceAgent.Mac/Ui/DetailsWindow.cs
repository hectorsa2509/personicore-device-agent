using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Mac.Platform;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// Everything the agent knows, as copyable text — the first thing support asks for.
/// Same lines as the Windows details window.
/// </summary>
internal sealed class DetailsWindow : Window
{
    public DetailsWindow(AgentSnapshot snapshot)
    {
        Title = AgentText.DetailsWindowTitle;
        Icon = MarkIcons.ForWindow(snapshot.Status);
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.Paper);

        var text = AgentText.DetailsReport(snapshot, Words.Mac);

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Menlo, Monaco, monospace"),
            FontSize = 12,
            Background = Brushes.White,
            Foreground = Palette.Brush(Palette.Ink),
        };

        var openLogs = new Button { Content = AgentText.ButtonOpenLogs };
        openLogs.Click += (_, _) =>
        {
            Directory.CreateDirectory(Log.Directory);
            Shell.Open(Log.Directory);
        };

        var copy = new Button { Content = AgentText.ButtonCopy };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };

        var close = new Button
        {
            Content = AgentText.ButtonClose,
            IsDefault = true,
            Background = Palette.Brush(Palette.Ink),
            Foreground = Palette.Brush(Palette.Paper),
        };
        close.Click += (_, _) => Close();

        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { openLogs, copy },
        });
        buttons.Children.Add(close);

        var footer = new Border
        {
            Background = Palette.Brush(Palette.PaperSunken),
            Padding = new Thickness(16, 10),
            Child = buttons,
        };

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(16, 16, 16, 8), Child = box });
        Content = root;
    }
}
