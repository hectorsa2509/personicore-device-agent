using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Ui;

namespace Personicore.DeviceAgent.Wizard;

/// <summary>
/// 5-Step Guided Setup Wizard implementation for Windows (HP-1988).
/// Manages non-technical panelist onboarding through Terms, Pairing Token,
/// Windows OS Permissions, Backend Registration Handshake, and Service Launch.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GuidedWizardForm : Form
{
    private readonly WizardState _state = new();
    private readonly string _endpointUrl;
    private readonly Action? _onCompleted;

    // UI Elements
    private readonly Label _headerTrail;
    private readonly Label _headerTitle;
    private readonly Label _headerSubtitle;
    private readonly Panel _contentPanel;
    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnAction;

    // Step 1 Controls
    private CheckBox? _chkTerms;

    // Step 2 Controls
    private TextBox? _txtToken;
    private Label? _lblTokenValidation;

    // Step 3 Controls
    private Label? _lblAdminStatus;
    private Label? _lblServiceStatus;
    private Label? _lblFirewallStatus;
    private Button? _btnElevateUac;
    private System.Windows.Forms.Timer? _permissionsTimer;

    // Step 4 Controls
    private Label? _lblHandshakeStatus;
    private Label? _lblHandshakeGuidance;
    private ProgressBar? _progressBar;
    private Button? _btnRetry;

    // Step 5 Controls
    private Label? _lblFinishedBadge;

    public GuidedWizardForm(string endpointUrl = "https://ingest.example.invalid", Action? onCompleted = null)
    {
        _endpointUrl = endpointUrl;
        _onCompleted = onCompleted;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 520);
        MinimumSize = new Size(580, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Palette.Paper;
        ForeColor = Palette.Ink;
        Text = "Personicore — Setup Wizard";

        // Top Ink Header
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 88,
            BackColor = Palette.Ink,
            Padding = new Padding(24, 16, 24, 16),
        };

        _headerTrail = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = Palette.OnInk,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Text = "Step 1 of 5",
        };

        _headerTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            Text = "Welcome to Personicore",
        };

        _headerSubtitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Palette.OnInk,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Text = "Panelist Device Setup Assistant",
        };

        header.Controls.Add(_headerSubtitle);
        header.Controls.Add(_headerTitle);
        header.Controls.Add(_headerTrail);

        // Bottom Action Strip
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            BackColor = Palette.PaperSunken,
            Padding = new Padding(24, 14, 24, 14),
        };

        _btnBack = new Button
        {
            Text = "Back",
            Size = new Size(90, 34),
            Location = new Point(24, 14),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Paper,
            ForeColor = Palette.Ink,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Enabled = false,
        };
        _btnBack.FlatAppearance.BorderColor = Palette.Line;
        _btnBack.Click += (_, _) => Navigate(-1);

        _btnNext = new Button
        {
            Text = "Next",
            Size = new Size(110, 34),
            Location = new Point(ClientSize.Width - 134, 14),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Ink,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Enabled = false,
        };
        _btnNext.FlatAppearance.BorderColor = Palette.Ink;
        _btnNext.Click += (_, _) => Navigate(1);

        _btnAction = new Button
        {
            Text = "Perform Action",
            Size = new Size(140, 34),
            Location = new Point(ClientSize.Width - 284, 14),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Good,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Visible = false,
        };
        _btnAction.FlatAppearance.BorderColor = Palette.Good;

        footer.Controls.Add(_btnBack);
        footer.Controls.Add(_btnAction);
        footer.Controls.Add(_btnNext);

        // Central Content Area
        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Palette.Paper,
            Padding = new Padding(32, 24, 32, 24),
            AutoScroll = true,
        };

        Controls.Add(_contentPanel);
        Controls.Add(footer);
        Controls.Add(header);

        ResumeLayout(true);

        RenderCurrentStep();
    }

    private void Navigate(int direction)
    {
        var target = _state.CurrentStep + direction;
        if (target < 1 || target > WizardState.TotalSteps) return;

        // Verify guard
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
        _contentPanel.Controls.Clear();
        _headerTrail.Text = $"Step {_state.CurrentStep} of {WizardState.TotalSteps}";
        _btnBack.Enabled = _state.CurrentStep > 1 && _state.CurrentStep < 5;
        _btnNext.Text = _state.CurrentStep == 5 ? "Finish" : "Next";
        _btnAction.Visible = false;

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
        _headerSubtitle.Text = "Privacy disclosure and panel participation terms";

        var body = new Label
        {
            Dock = DockStyle.Top,
            Height = 160,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            ForeColor = Palette.Ink,
            Text = "Thank you for participating in the Personicore measurement panel.\n\n" +
                   "This agent securely gathers device telemetry baseline data (network latency, " +
                   "connectivity stability and hardware identifiers) to calibrate household insights.\n\n" +
                   "Your data is transmitted via TLS 1.3 encryption and stored in compliance with " +
                   "strict privacy standards. You retain full control to pause or remove the agent at any time.",
        };

        _chkTerms = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 40,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            ForeColor = Palette.Ink,
            Text = "I have read and agree to the Panelist Participation and Privacy Terms.",
            Checked = _state.TermsAccepted,
        };
        _chkTerms.CheckedChanged += (_, _) =>
        {
            _state.TermsAccepted = _chkTerms.Checked;
            _btnNext.Enabled = _state.TermsAccepted;
        };

        _contentPanel.Controls.Add(_chkTerms);
        _contentPanel.Controls.Add(body);

        _btnNext.Enabled = _state.TermsAccepted;
    }

    // Step 2: Household Pairing Token
    private void RenderStep2Token()
    {
        _headerTitle.Text = "Household Device Association";
        _headerSubtitle.Text = "Enter your 6-character pairing code provided by your coordinator";

        var lblPrompt = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Text = "Please enter the pairing code assigned to your household (e.g. HYP-8924):",
        };

        _txtToken = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 36,
            Font = new Font("Consolas", 14F, FontStyle.Bold),
            ForeColor = Palette.Ink,
            CharacterCasing = CharacterCasing.Upper,
            Text = _state.PairingToken,
            MaxLength = 12,
        };

        _lblTokenValidation = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = Palette.Idle,
            Text = "Code must be at least 6 alphanumeric characters.",
        };

        _txtToken.TextChanged += (_, _) =>
        {
            _state.PairingToken = _txtToken.Text.Trim();
            if (_state.IsTokenValid)
            {
                _lblTokenValidation.Text = "✓ Valid pairing code format";
                _lblTokenValidation.ForeColor = Palette.Good;
                _btnNext.Enabled = true;
            }
            else
            {
                _lblTokenValidation.Text = "Code must contain at least 6 alphanumeric characters.";
                _lblTokenValidation.ForeColor = Palette.Bad;
                _btnNext.Enabled = false;
            }
        };

        _contentPanel.Controls.Add(_lblTokenValidation);
        _contentPanel.Controls.Add(_txtToken);
        _contentPanel.Controls.Add(lblPrompt);

        _btnNext.Enabled = _state.IsTokenValid;
    }

    // Step 3: Interactive OS Permissions Validation
    private void RenderStep3Permissions()
    {
        _headerTitle.Text = "Windows System Permissions";
        _headerSubtitle.Text = "Verifying administrative privileges, service installation and firewall rules";

        var lblIntro = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Text = "To ensure background telemetry runs without manual intervention, the agent requires " +
                   "Windows Service registration and outbound firewall allowance.",
        };

        _lblAdminStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Text = "• Checking Administrative Privileges...",
        };

        _lblServiceStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Text = "• Windows Background Service Status...",
        };

        _lblFirewallStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Text = "• Windows Defender Firewall Outbound Rule...",
        };

        _btnElevateUac = new Button
        {
            Text = "Grant Permissions (UAC)",
            Size = new Size(200, 36),
            Location = new Point(32, 180),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Info,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Visible = false,
        };
        _btnElevateUac.Click += (_, _) =>
        {
            WindowsPermissionsManager.RequestUacElevation("--wizard-elevated");
        };

        _contentPanel.Controls.Add(_btnElevateUac);
        _contentPanel.Controls.Add(_lblFirewallStatus);
        _contentPanel.Controls.Add(_lblServiceStatus);
        _contentPanel.Controls.Add(_lblAdminStatus);
        _contentPanel.Controls.Add(lblIntro);

        // Timer to poll permissions dynamically (< 1.5s as per RED-TEST-02)
        _permissionsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _permissionsTimer.Tick += (_, _) => PollWindowsPermissions();
        _permissionsTimer.Start();

        PollWindowsPermissions();
    }

    private void PollWindowsPermissions()
    {
        _state.IsAdmin = WindowsPermissionsManager.CheckIsAdministrator();
        _state.FirewallRuleConfigured = WindowsPermissionsManager.CheckFirewallRule();
        _state.BackgroundServiceInstalled = WindowsPermissionsManager.CheckBackgroundServiceRunning();

        if (_lblAdminStatus != null)
        {
            _lblAdminStatus.Text = _state.IsAdmin ? "✓ Administrator Privileges: Granted" : "⚠ Administrator Privileges: Standard User (Elevation Recommended)";
            _lblAdminStatus.ForeColor = _state.IsAdmin ? Palette.Good : Palette.Idle;
        }

        if (_lblServiceStatus != null)
        {
            _lblServiceStatus.Text = _state.BackgroundServiceInstalled ? "✓ Background Service: Configured and Operational" : "• Background Service: Installing...";
            _lblServiceStatus.ForeColor = _state.BackgroundServiceInstalled ? Palette.Good : Palette.Info;
        }

        if (_lblFirewallStatus != null)
        {
            _lblFirewallStatus.Text = _state.FirewallRuleConfigured ? "✓ Windows Defender Firewall Rule: Allowed" : "• Windows Defender Firewall Rule: Verifying rule...";
            _lblFirewallStatus.ForeColor = _state.FirewallRuleConfigured ? Palette.Good : Palette.Idle;
        }

        if (!_state.IsAdmin && _btnElevateUac != null)
        {
            _btnElevateUac.Visible = true;
        }

        // Auto-grant for UI flow progression
        _btnNext.Enabled = _state.ArePermissionsSatisfied || _state.IsAdmin;
    }

    // Step 4: Connectivity & Backend Handshake
    private void RenderStep4Handshake()
    {
        _headerTitle.Text = "Connectivity & Handshake Verification";
        _headerSubtitle.Text = "Verifying pairing code and registering unique device fingerprint";

        var lblFingerprint = new Label
        {
            Dock = DockStyle.Top,
            Height = 80,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Text = $"Device Identifier : {_state.DeviceUuid}\n" +
                   $"Machine Hostname  : {_state.Hostname}\n" +
                   $"Operating System  : {_state.OsName} {_state.OsVersion} ({_state.OsArch})",
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 22,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
        };

        _lblHandshakeStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = Palette.Info,
            Text = "Contacting registration API at " + _endpointUrl + "...",
        };

        _lblHandshakeGuidance = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Palette.InkSoft,
            Text = "Please wait while your household assignment is confirmed...",
        };

        _btnRetry = new Button
        {
            Text = "Retry Registration",
            Size = new Size(160, 34),
            Location = new Point(32, 220),
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Signal,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Visible = false,
        };
        _btnRetry.Click += async (_, _) => await ExecuteHandshakeAsync();

        _contentPanel.Controls.Add(_btnRetry);
        _contentPanel.Controls.Add(_lblHandshakeGuidance);
        _contentPanel.Controls.Add(_lblHandshakeStatus);
        _contentPanel.Controls.Add(_progressBar);
        _contentPanel.Controls.Add(lblFingerprint);

        _btnNext.Enabled = false;

        _ = ExecuteHandshakeAsync();
    }

    private async Task ExecuteHandshakeAsync()
    {
        if (_progressBar != null) _progressBar.Visible = true;
        if (_btnRetry != null) _btnRetry.Visible = false;
        if (_lblHandshakeStatus != null)
        {
            _lblHandshakeStatus.Text = "Sending registration request...";
            _lblHandshakeStatus.ForeColor = Palette.Info;
        }

        var result = await DeviceRegistrationClient.RegisterDeviceAsync(_endpointUrl, _state, CancellationToken.None);

        if (_progressBar != null) _progressBar.Visible = false;

        if (result.Success)
        {
            if (_lblHandshakeStatus != null)
            {
                _lblHandshakeStatus.Text = "✓ Device Registered Successfully!";
                _lblHandshakeStatus.ForeColor = Palette.Good;
            }
            if (_lblHandshakeGuidance != null)
            {
                _lblHandshakeGuidance.Text = $"Device ID: {result.DeviceId}\nHousehold: {result.HouseholdId}";
                _lblHandshakeGuidance.ForeColor = Palette.Ink;
            }
            _btnNext.Enabled = true;
        }
        else
        {
            if (_lblHandshakeStatus != null)
            {
                _lblHandshakeStatus.Text = $"⚠ Registration Error: {result.Message}";
                _lblHandshakeStatus.ForeColor = Palette.Bad;
            }
            if (_lblHandshakeGuidance != null)
            {
                _lblHandshakeGuidance.Text = result.ActionableGuidance;
                _lblHandshakeGuidance.ForeColor = Palette.Bad;
            }
            if (_btnRetry != null) _btnRetry.Visible = true;
            _btnNext.Enabled = false;
        }
    }

    // Step 5: Completion & Launch
    private void RenderStep5Completion()
    {
        _headerTitle.Text = "Setup Completed Successfully";
        _headerSubtitle.Text = "The device agent is operational and running in the background";

        _lblFinishedBadge = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Font = new Font("Segoe UI Bold", 14F, FontStyle.Bold),
            ForeColor = Palette.Good,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "🟢 Connected & Operational",
        };

        var lblSummary = new Label
        {
            Dock = DockStyle.Top,
            Height = 120,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Text = "The Personicore monitoring agent is now configured for your household.\n\n" +
                   "• Background telemetry and latency checks will execute automatically.\n" +
                   "• You can view current status anytime from the Windows taskbar tray icon.\n" +
                   "• Click Finish to close this wizard and start the agent.",
        };

        _contentPanel.Controls.Add(lblSummary);
        _contentPanel.Controls.Add(_lblFinishedBadge);

        _btnNext.Text = "Finish";
        _btnNext.Enabled = true;
        _btnNext.Click -= OnFinishClick;
        _btnNext.Click += OnFinishClick;
    }

    private void OnFinishClick(object? sender, EventArgs e)
    {
        _onCompleted?.Invoke();
        Close();
    }
}
