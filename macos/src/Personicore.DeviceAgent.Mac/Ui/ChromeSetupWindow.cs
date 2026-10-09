using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Mac.Platform;

namespace Personicore.DeviceAgent.Mac.Ui;

internal sealed class ChromeSetupWindow : Window
{
    private readonly TextBlock _result = Text(string.Empty);
    private readonly StackPanel _buttons = new() { Spacing = 8 };
    private readonly CancellationTokenSource _closed = new();
    private readonly Func<AgentSnapshot> _snapshot;
    private readonly ChromeDnsPolicy _policy = new(new ChromeDnsPreferences(),
        Path.Combine(AgentPaths.UserData, "chrome-dns-policy.receipt"));

    public ChromeSetupWindow(Func<AgentSnapshot> snapshot)
    {
        _snapshot = snapshot;
        Title = AgentText.ChromeTitle;
        Width = 610;
        Height = 690;
        MinWidth = 500;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Brush(Palette.Paper);

        var body = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        body.Children.Add(Text(AgentText.ChromeTitle, 22));
        body.Children.Add(Text(AgentText.ChromeIntro));
        Add(AgentText.ChromeApply, async () =>
        {
            if (!_snapshot().ActionsUnlocked) return AgentText.ChromeNoProxy;
            if (!ChromeInstalled()) return AgentText.ChromeUnavailable;
            return await Task.Run(_policy.Apply);
        });
        Add(AgentText.ChromeUndo, () => Task.Run(_policy.Undo));
        Add(AgentText.ChromeOpenPolicy, () => OpenChrome("chrome://policy"));
        body.Children.Add(_buttons);
        body.Children.Add(Text(AgentText.ChromePermission));
        var permission = new Button { Content = AgentText.ChromeOpenPermission };
        permission.Click += (_, _) => Shell.Open("x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork");
        body.Children.Add(permission);
        body.Children.Add(Text(AgentText.ChromeTestNote));
        var check = new Button { Content = AgentText.ChromeTest };
        check.Click += async (_, _) => await Run(async () =>
        {
            var settings = await Task.Run(() => Shell.Run("/usr/sbin/scutil", "--proxy"));
            var proxy = ChromeConnectionCheck.ActiveProxy(settings);
            if (proxy is null) return AgentText.ChromeNoProxy;
            var result = await ChromeConnectionCheck.RunAsync(proxy, _closed.Token);
            return AgentText.ChromeAddress(_snapshot().LocalIp, proxy) + "\n\n" + result;
        }, check);
        body.Children.Add(check);
        var browser = new Button { Content = AgentText.ChromeOpenTest };
        browser.Click += async (_, _) => await Run(() => OpenChrome(ChromeConnectionCheck.TestUrl), browser);
        body.Children.Add(browser);
        body.Children.Add(_result);
        var close = new Button { Content = AgentText.ButtonClose, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        body.Children.Add(close);
        Content = new ScrollViewer { Content = body };
        Closed += (_, _) => _closed.Cancel();
    }

    private void Add(string label, Func<Task<string>> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await Run(action, button);
        _buttons.Children.Add(button);
    }

    private bool _busy;
    private async Task Run(Func<Task<string>> action, Button button)
    {
        if (_busy) return;
        _busy = true;
        button.IsEnabled = false;
        _result.Text = AgentText.ChromeBusy;
        try { _result.Text = await action(); }
        catch (Exception ex)
        {
            Log.Warn($"Chrome setup failed: {ex.GetType().Name}");
            _result.Text = AgentText.ChromeFailed;
        }
        finally { button.IsEnabled = true; _busy = false; }
    }

    private static bool ChromeInstalled() => Directory.Exists("/Applications/Google Chrome.app")
        || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Google Chrome.app"));

    private static Task<string> OpenChrome(string url)
    {
        if (!ChromeInstalled()) return Task.FromResult(AgentText.ChromeUnavailable);
        Shell.Run("/usr/bin/open", "-b", "com.google.Chrome", url);
        return Task.FromResult(AgentText.ChromeTestNote);
    }

    private static TextBlock Text(string value, int size = 13) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = Palette.Brush(Palette.Ink),
    };
}
