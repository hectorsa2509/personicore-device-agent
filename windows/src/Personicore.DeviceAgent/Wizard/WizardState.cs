using System;
using System.Text.RegularExpressions;

namespace Personicore.DeviceAgent.Wizard;

/// <summary>
/// State tracking for the 5-step Guided Setup Wizard (HP-1988).
/// Maintains onboarding progress, pairing token validation, hardware fingerprint,
/// and backend registration receipts.
/// </summary>
public sealed class WizardState
{
    public const int TotalSteps = 5;

    public int CurrentStep { get; set; } = 1;

    // Step 1: Consent
    public bool TermsAccepted { get; set; }

    // Step 2: Household Pairing Token
    private static readonly Regex TokenRegex = new(@"^[A-Za-z0-9\-]{6,12}$", RegexOptions.Compiled);
    public string PairingToken { get; set; } = string.Empty;

    public bool IsTokenValid => !string.IsNullOrWhiteSpace(PairingToken) &&
                                TokenRegex.IsMatch(PairingToken.Trim()) &&
                                PairingToken.Replace("-", "").Length >= 6;

    // Step 3: OS Permissions Status
    public bool IsAdmin { get; set; }
    public bool BackgroundServiceInstalled { get; set; }
    public bool FirewallRuleConfigured { get; set; }

    public bool ArePermissionsSatisfied => BackgroundServiceInstalled && FirewallRuleConfigured;

    // Step 4: Hardware Fingerprint & Registration Receipt
    public string DeviceUuid { get; set; } = Guid.NewGuid().ToString();
    public string Hostname { get; set; } = Environment.MachineName;
    public string OsName { get; set; } = "Windows";
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string OsArch { get; set; } = Environment.Is64BitOperatingSystem ? "x64" : "x86";
    public string AppVersion { get; set; } = "2.4.0";

    public bool HandshakeSuccessful { get; set; }
    public string RegisteredDeviceId { get; set; } = string.Empty;
    public string RegisteredHouseholdId { get; set; } = string.Empty;
    public string RegistrationAuthToken { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public string ActionableGuidance { get; set; } = string.Empty;

    // Navigation guards (RED-TEST-01)
    public bool CanAdvanceFromStep(int step) => step switch
    {
        1 => TermsAccepted,
        2 => IsTokenValid,
        3 => ArePermissionsSatisfied,
        4 => HandshakeSuccessful,
        5 => true,
        _ => false,
    };
}
