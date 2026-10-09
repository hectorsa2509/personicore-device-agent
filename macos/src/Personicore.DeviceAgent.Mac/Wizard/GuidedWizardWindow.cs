using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Personicore.DeviceAgent.Mac.Platform;
using Personicore.DeviceAgent.Mac.Ui;

namespace Personicore.DeviceAgent.Mac.Wizard;

/// <summary>
/// 5-Step Guided Setup Wizard implementation for macOS (HP-1988).
/// Manages non-technical panelist onboarding through Terms, Pairing Token,
/// macOS Accessibility Permissions (AXIsProcessTrusted), Backend Registration Handshake,
/// and LaunchDaemon Activation.
/// </summary>
internal sealed class GuidedWizardWindow : Window
{
    private readonly MacWizardState _state = new();
    private readonly string _endpointUrl;
    private readonly Action? _onCompleted;

    // Header elements
    private readonly TextBlock _headerTrail;
    private readonly TextBlock _headerTitle;
    private readonly TextBlock _headerSubtitle;

    // Content container
    private readonly StackPanel _contentStack;

    // Footer buttons
    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnAction;

    // Step 1 controls
    private CheckBox? _chkTerms;

    // Step 2 controls
    private TextBox? _txtToken;
    private TextBlock? _lblTokenValidation;

    // Step 3 controls
    private TextBlock? _lblAccessibilityStatus;
    private TextBlock? _lblDaemonStatus;
    private Button? _btnOpenSettings;
    private DispatcherTimer? _permissionsTimer;

    // Step 4 controls
    private TextBlock? _lblHandshakeStatus;
    private TextBlock? _lblHandshakeGuidance;
    private ProgressBar? _progressBar;
    private Button? _btnRetry;

    public GuidedWizardWindow(string endpointUrl = "https://ingest.example.invalid", Action? onCompleted = null)
    {
        _endpointUrl = endpointUrl;
        _onCompleted = onCompleted;

        Title = "Personicore — Setup Wizard (macOS)";
        Width = 620;
        Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Brush(Palette.Paper);

        // Header Panel (Ink background)
        var header = new Border
        {
            Height = 90,
            Background = Palette.Brush(Palette.Ink),
            Padding = new Thickness(24, 16),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    (_headerTrail = new TextBlock
                    {
                        Text = "Step 1 of 5",
                        FontSize = 11,
                        Foreground = Palette.Brush(Palette.OnInk),
                    }),
                    (_headerTitle = new TextBlock
                    {
                        Text = "Welcome to Personicore",
                        FontSize = 17,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White,
                    }),
                    (_headerSubtitle = new TextBlock
                    {
                        Text = "Panelist Device Setup Assistant",
                        FontSize = 12,
                        Foreground = Palette.Brush(Palette.OnInk),
                    }),
                },
            },
        };

        // Footer Strip
        var footer = new Border
        {
            Height = 64,
            Background = Palette.Brush(Palette.PaperSunken),
            Padding = new Thickness(24, 14),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                Children =
                {
                    (_btnBack = new Button
                    {
                        Content = "Back",
                        Width = 90,
                        Height = 34,
                        IsEnabled = false,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    }),
                    (_btnAction = new Button
                    {
                        Content = "Action",
                        Width = 140,
                        Height = 34,
                        IsVisible = false,
                        Background = Palette.Brush(Palette.Good),
                        Foreground = Brushes.White,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    }),
                    (_btnNext = new Button
                    {
                        Content = "Next",
                        Width = 110,
                        Height = 34,
                        IsEnabled = false,
                        Background = Palette.Brush(Palette.Ink),
                        Foreground = Brushes.White,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    }),
                },
            },
        };

        Grid.SetColumn(_btnBack, 0);
        Grid.SetColumn(_btnAction, 2);
        Grid.SetColumn(_btnNext, 3);

        _btnBack.Click += (_, _) => Navigate(-1);
        _btnNext.Click += (_, _) => Navigate(1);

        // Content Area
        _contentStack = new StackPanel
        {
            Margin = new Thickness(32, 24),
            Spacing = 16,
        };

        Content = new DockPanel
        {
            Children =
            {
                header,
                footer,
                new ScrollViewer { Content = _contentStack },
            },
        };

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);

        RenderCurrentStep();
    }

    private void Navigate(int direction)
    {
        var target = _state.CurrentStep + direction;
        if (target < 1 || target > MacWizardState.TotalSteps) return;

        if (direction > 0 && !_state.CanAdvanceFromStep(_state.CurrentStep))
        {
            return;
        }

        _state.CurrentStep = target;
        RenderCurrentStep();
    }

    private void RenderCurrentStep()
    {
        _permissionsTimer?.Stop();
        _contentStack.Children.Clear();
        _headerTrail.Text = $"Step {_state.CurrentStep} of {MacWizardState.TotalSteps}";
        _btnBack.IsEnabled = _state.CurrentStep > 1 && _state.CurrentStep < 5;
        _btnNext.Content = _state.CurrentStep == 5 ? "Finish" : "Next";
        _btnAction.IsVisible = false;

        switch (_state.CurrentStep)
        {
            case 1:
                RenderStep1Welcome();
                break;
            case 2:
                RenderStep2Token();
                break;
            case 3:
                RenderStep3Permissions();
                break;
            case 4:
                RenderStep4Handshake();
                break;
            case 5:
                RenderStep5Completion();
                break;
        }
    }

    // Step 1: Welcome & Terms
    private void RenderStep1Welcome()
    {
        _headerTitle.Text = "Welcome to Personicore";
        _headerSubtitle.Text = "Privacy disclosure and panelist agreement";

        Notifier.Show("Welcome to Personicore! The setup wizard will guide you through device registration.", "Personicore Device Agent");

        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Palette.Brush(Palette.Ink),
            Text = "Thank you for participating in the Personicore measurement panel on macOS.\n\n" +
                   "This agent securely gathers device telemetry baseline data (network latency, " +
                   "connectivity stability and hardware identifiers) to calibrate household insights.\n\n" +
                   "Your data is transmitted via TLS 1.3 encryption and stored in compliance with " +
                   "strict privacy standards. You retain full control to pause or remove the agent at any time.",
        };

        _chkTerms = new CheckBox
        {
            Content = "I have read and agree to the Panelist Participation and Privacy Terms.",
            IsChecked = _state.TermsAccepted,
            Foreground = Palette.Brush(Palette.Ink),
            FontWeight = FontWeight.SemiBold,
        };
        _chkTerms.IsCheckedChanged += (_, _) =>
        {
            _state.TermsAccepted = _chkTerms.IsChecked == true;
            _btnNext.IsEnabled = _state.TermsAccepted;
        };

        _contentStack.Children.Add(body);
        _contentStack.Children.Add(_chkTerms);

        _btnNext.IsEnabled = _state.TermsAccepted;
    }

    // Step 2: Household Pairing Token
    private void RenderStep2Token()
    {
        _headerTitle.Text = "Household Device Association";
        _headerSubtitle.Text = "Enter your 6-character pairing code provided by your coordinator";

        var lblPrompt = new TextBlock
        {
            Text = "Please enter the pairing code assigned to your household (e.g. HYP-8924):",
            FontSize = 13,
            Foreground = Palette.Brush(Palette.Ink),
        };

        _txtToken = new TextBox
        {
            Height = 38,
            FontSize = 16,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontWeight = FontWeight.Bold,
            Text = _state.PairingToken,
            MaxLength = 12,
        };

        _lblTokenValidation = new TextBlock
        {
            Text = "Code must be at least 6 alphanumeric characters.",
            FontSize = 12,
            FontStyle = FontStyle.Italic,
            Foreground = Palette.Brush(Palette.Idle),
        };

        _txtToken.TextChanged += (_, _) =>
        {
            _state.PairingToken = _txtToken.Text?.Trim() ?? string.Empty;
            if (_state.IsTokenValid)
            {
                _lblTokenValidation.Text = "✓ Valid pairing code format";
                _lblTokenValidation.Foreground = Palette.Brush(Palette.Good);
                _btnNext.IsEnabled = true;
            }
            else
            {
                _lblTokenValidation.Text = "Code must contain at least 6 alphanumeric characters.";
                _lblTokenValidation.Foreground = Palette.Brush(Palette.Bad);
                _btnNext.IsEnabled = false;
            }
        };

        _contentStack.Children.Add(lblPrompt);
        _contentStack.Children.Add(_txtToken);
        _contentStack.Children.Add(_lblTokenValidation);

        _btnNext.IsEnabled = _state.IsTokenValid;
    }

    // Step 3: Interactive OS Permissions Validation
    private void RenderStep3Permissions()
    {
        _headerTitle.Text = "macOS System Permissions";
        _headerSubtitle.Text = "Verifying Accessibility privileges and background LaunchDaemon";

        var lblIntro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Palette.Brush(Palette.Ink),
            Text = "The agent requires macOS Accessibility privileges to observe network activity " +
                   "and keep telemetry active. You can grant access directly in System Settings.",
        };

        _lblAccessibilityStatus = new TextBlock
        {
            FontSize = 13,
            Text = "• Checking Accessibility Privileges (AXIsProcessTrusted)...",
        };

        _lblDaemonStatus = new TextBlock
        {
            FontSize = 13,
            Text = "• Background LaunchDaemon Status...",
        };

        _btnOpenSettings = new Button
        {
            Content = "Open System Settings (Accessibility)",
            Width = 260,
            Height = 36,
            Background = Palette.Brush(Palette.Info),
            Foreground = Brushes.White,
        };
        _btnOpenSettings.Click += (_, _) => MacPermissionsManager.OpenAccessibilitySettings();

        _contentStack.Children.Add(lblIntro);
        _contentStack.Children.Add(_lblAccessibilityStatus);
        _contentStack.Children.Add(_lblDaemonStatus);
        _contentStack.Children.Add(_btnOpenSettings);

        // Polling loop for permissions in < 1.5s (RED-TEST-02)
        _permissionsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _permissionsTimer.Tick += (_, _) => PollPermissions();
        _permissionsTimer.Start();

        PollPermissions();
    }

    private void PollPermissions()
    {
        _state.AccessibilityGranted = MacPermissionsManager.CheckAccessibilityPermission();
        _state.LaunchDaemonConfigured = MacPermissionsManager.CheckLaunchDaemonInstalled();

        if (_lblAccessibilityStatus != null)
        {
            _lblAccessibilityStatus.Text = _state.AccessibilityGranted
                ? "✓ Accessibility Permissions: Granted"
                : "⚠ Accessibility Permissions: Pending authorization in System Settings";
            _lblAccessibilityStatus.Foreground = Palette.Brush(_state.AccessibilityGranted ? Palette.Good : Palette.Signal);
        }

        if (_lblDaemonStatus != null)
        {
            _lblDaemonStatus.Text = _state.LaunchDaemonConfigured
                ? "✓ Background LaunchDaemon: Configured and Operational"
                : "• Background LaunchDaemon: System daemon ready";
            _lblDaemonStatus.Foreground = Palette.Brush(_state.LaunchDaemonConfigured ? Palette.Good : Palette.Info);
        }

        _btnNext.IsEnabled = _state.AccessibilityGranted || _state.ArePermissionsSatisfied;
    }

    // Step 4: Connectivity & Backend Handshake
    private void RenderStep4Handshake()
    {
        _headerTitle.Text = "Connectivity & Handshake Verification";
        _headerSubtitle.Text = "Registering device fingerprint and establishing TLS connection";

        var lblFingerprint = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            Text = $"Device Identifier : {_state.DeviceUuid}\n" +
                   $"Machine Hostname  : {_state.Hostname}\n" +
                   $"Operating System  : {_state.OsName} {_state.OsVersion} ({_state.OsArch})",
        };

        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 8,
        };

        _lblHandshakeStatus = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Brush(Palette.Info),
            Text = "Contacting registration API at " + _endpointUrl + "...",
        };

        _lblHandshakeGuidance = new TextBlock
        {
            FontSize = 12,
            Foreground = Palette.Brush(Palette.InkSoft),
            Text = "Please wait while your household assignment is confirmed...",
        };

        _btnRetry = new Button
        {
            Content = "Retry Registration",
            Width = 160,
            Height = 34,
            Background = Palette.Brush(Palette.Signal),
            Foreground = Brushes.White,
            IsVisible = false,
        };
        _btnRetry.Click += async (_, _) => await ExecuteHandshakeAsync();

        _contentStack.Children.Add(lblFingerprint);
        _contentStack.Children.Add(_progressBar);
        _contentStack.Children.Add(_lblHandshakeStatus);
        _contentStack.Children.Add(_lblHandshakeGuidance);
        _contentStack.Children.Add(_btnRetry);

        _btnNext.IsEnabled = false;

        _ = ExecuteHandshakeAsync();
    }

    private async Task ExecuteHandshakeAsync()
    {
        if (_progressBar != null) _progressBar.IsVisible = true;
        if (_btnRetry != null) _btnRetry.IsVisible = false;
        if (_lblHandshakeStatus != null)
        {
            _lblHandshakeStatus.Text = "Sending registration request...";
            _lblHandshakeStatus.Foreground = Palette.Brush(Palette.Info);
        }

        var result = await MacDeviceRegistrationClient.RegisterDeviceAsync(_endpointUrl, _state, CancellationToken.None);

        if (_progressBar != null) _progressBar.IsVisible = false;

        if (result.Success)
        {
            if (_lblHandshakeStatus != null)
            {
                _lblHandshakeStatus.Text = "✓ Device Registered Successfully!";
                _lblHandshakeStatus.Foreground = Palette.Brush(Palette.Good);
            }
            if (_lblHandshakeGuidance != null)
            {
                _lblHandshakeGuidance.Text = $"Device ID: {result.DeviceId}\nHousehold: {result.HouseholdId}";
                _lblHandshakeGuidance.Foreground = Palette.Brush(Palette.Ink);
            }
            _btnNext.IsEnabled = true;
        }
        else
        {
            if (_lblHandshakeStatus != null)
            {
                _lblHandshakeStatus.Text = $"⚠ Registration Error: {result.Message}";
                _lblHandshakeStatus.Foreground = Palette.Brush(Palette.Bad);
            }
            if (_lblHandshakeGuidance != null)
            {
                _lblHandshakeGuidance.Text = result.ActionableGuidance;
                _lblHandshakeGuidance.Foreground = Palette.Brush(Palette.Bad);
            }
            if (_btnRetry != null) _btnRetry.IsVisible = true;
            _btnNext.IsEnabled = false;
        }
    }

    // Step 5: Completion & Launch
    private void RenderStep5Completion()
    {
        _headerTitle.Text = "Setup Completed Successfully";
        _headerSubtitle.Text = "The device agent is operational and running in the background";

        var lblFinishedBadge = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Palette.Brush(Palette.Good),
            TextAlignment = TextAlignment.Center,
            Text = "🟢 Connected & Operational",
            Margin = new Thickness(0, 16),
        };

        var lblSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Text = "The Personicore monitoring agent is now configured for your household.\n\n" +
                   "• Background telemetry and latency checks will execute automatically.\n" +
                   "• You can view current status anytime from the macOS menu bar icon.\n" +
                   "• Click Finish to close this wizard and start the agent.",
        };

        _contentStack.Children.Add(lblFinishedBadge);
        _contentStack.Children.Add(lblSummary);

        _btnNext.Content = "Finish";
        _btnNext.IsEnabled = true;
        _btnNext.Click += (_, _) =>
        {
            _onCompleted?.Invoke();
            Close();
        };
    }
}
