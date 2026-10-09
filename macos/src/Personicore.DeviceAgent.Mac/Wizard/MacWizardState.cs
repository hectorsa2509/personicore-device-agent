using System;
using System.Text.RegularExpressions;

namespace Personicore.DeviceAgent.Mac.Wizard;

/// <summary>
/// State tracking for the 5-step Guided Setup Wizard on macOS (HP-1988).
/// Maintains onboarding progress, pairing token validation, hardware fingerprint,
/// and backend registration receipts.
/// </summary>
public sealed class MacWizardState
{
    public const int TotalSteps = 5;

    public int CurrentStep { get; set; } = 1;

    // Step 1: Terms
    public bool TermsAccepted { get; set; }

    // Step 2: Household Pairing Token
    private static readonly Regex TokenRegex = new(@"^[A-Za-z0-9\-]{6,12}$", RegexOptions.Compiled);
    public string PairingToken { get; set; } = string.Empty;

    public bool IsTokenValid => !string.IsNullOrWhiteSpace(PairingToken) &&
                                TokenRegex.IsMatch(PairingToken.Trim()) &&
                                PairingToken.Replace("-", "").Length >= 6;

    // Step 3: macOS Permissions Status
    public bool AccessibilityGranted { get; set; }
    public bool ScreenCaptureGranted { get; set; }
    public bool LaunchDaemonConfigured { get; set; }

    public bool ArePermissionsSatisfied => AccessibilityGranted && LaunchDaemonConfigured;

    // Step 4: Hardware Fingerprint & Registration Receipt
    public string DeviceUuid { get; set; } = Guid.NewGuid().ToString();
    public string Hostname { get; set; } = Environment.MachineName;
    public string OsName { get; set; } = "macOS";
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string OsArch { get; set; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
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
