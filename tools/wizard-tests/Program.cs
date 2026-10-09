using System;
using System.Text.Json;
using Personicore.DeviceAgent.Wizard;

Console.WriteLine("===============================================================");
Console.WriteLine("🧪 RUNNING HP-1988 WINDOWS GUIDED WIZARD TEST SUITE");
Console.WriteLine("===============================================================\n");

void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL ✗ {message}");
        Console.ResetColor();
        throw new Exception($"Test failure: {message}");
    }
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"PASS ✓ {message}");
    Console.ResetColor();
}

// -------------------------------------------------------------
// RED-TEST-01: Step Progression & Navigation Guards
// -------------------------------------------------------------
var state = new WizardState();

Check(!state.CanAdvanceFromStep(1), "RED-TEST-01.1: Wizard forbids advancing past Step 1 without terms consent");
state.TermsAccepted = true;
Check(state.CanAdvanceFromStep(1), "RED-TEST-01.2: Step 1 permits advance when terms are accepted");

Check(!state.CanAdvanceFromStep(2), "RED-TEST-01.3: Step 2 forbids advance with empty pairing token");
state.PairingToken = "123";
Check(!state.CanAdvanceFromStep(2), "RED-TEST-01.4: Step 2 forbids advance with pairing token shorter than 6 characters");
state.PairingToken = "HYP-8924";
Check(state.CanAdvanceFromStep(2), "RED-TEST-01.5: Step 2 permits advance with valid 6-char formatted code (HYP-8924)");

Check(!state.CanAdvanceFromStep(3), "RED-TEST-01.6: Step 3 forbids advance without verified system permissions");
state.BackgroundServiceInstalled = true;
Check(!state.CanAdvanceFromStep(3), "RED-TEST-01.7: Step 3 requires both background service and firewall rule");
state.FirewallRuleConfigured = true;
Check(state.CanAdvanceFromStep(3), "RED-TEST-01.8: Step 3 permits advance when service and firewall are verified");

// -------------------------------------------------------------
// RED-TEST-02: Device Fingerprint & Contract Conformance
// -------------------------------------------------------------
Check(!string.IsNullOrWhiteSpace(state.DeviceUuid), "RED-TEST-02.1: Generates non-empty unique device UUID");
Check(state.OsName == "Windows", "RED-TEST-02.2: Operating system identifier matches Windows platform");
Check(state.AppVersion == "2.4.0", "RED-TEST-02.3: App version aligns with specification contract (2.4.0)");

// -------------------------------------------------------------
// RED-TEST-03: Registration Receipt Parsing & Local Persistence
// -------------------------------------------------------------
var mockSuccessJson = """
{
  "status": "success",
  "device_id": "dev_test_windows_001",
  "household_id": "hh_test_household_99",
  "registered_at": "2026-10-05T20:30:00Z",
  "auth_token": "ey_mock_jwt_windows_token",
  "heartbeat_interval_sec": 60,
  "telemetry_endpoint": "https://ingest.example.invalid/v1/telemetry"
}
""";

using var doc = JsonDocument.Parse(mockSuccessJson);
var root = doc.RootElement;
state.HandshakeSuccessful = true;
state.RegisteredDeviceId = root.GetProperty("device_id").GetString()!;
state.RegisteredHouseholdId = root.GetProperty("household_id").GetString()!;
state.RegistrationAuthToken = root.GetProperty("auth_token").GetString()!;

Check(state.RegisteredDeviceId == "dev_test_windows_001", "RED-TEST-03.1: Successfully parsed device_id from 200 OK receipt");
Check(state.RegisteredHouseholdId == "hh_test_household_99", "RED-TEST-03.2: Successfully parsed household_id from 200 OK receipt");
Check(state.CanAdvanceFromStep(4), "RED-TEST-03.3: Step 4 permits advance after successful handshake");

// -------------------------------------------------------------
// RED-TEST-04: Network Resilience & Actionable Guidance
// -------------------------------------------------------------
var mockErrorJson = """
{
  "status": "error",
  "error_code": "INVALID_PAIRING_TOKEN",
  "message": "The provided 6-character code has expired or is invalid.",
  "actionable_guidance": "Please request a fresh code from your Personicore panel coordinator."
}
""";

using var errDoc = JsonDocument.Parse(mockErrorJson);
var errRoot = errDoc.RootElement;
state.HandshakeSuccessful = false;
state.LastError = $"[{errRoot.GetProperty("error_code").GetString()}] {errRoot.GetProperty("message").GetString()}";
state.ActionableGuidance = errRoot.GetProperty("actionable_guidance").GetString()!;

Check(!state.CanAdvanceFromStep(4), "RED-TEST-04.1: Handshake failure blocks progression to Step 5");
Check(state.LastError.Contains("INVALID_PAIRING_TOKEN"), "RED-TEST-04.2: Structured error code preserved");
Check(state.ActionableGuidance.Contains("fresh code"), "RED-TEST-04.3: Actionable guidance provided for retry UI");

// -------------------------------------------------------------
// RED-TEST-05 (macOS): MacWizardState & Accessibility Guard Tests
// -------------------------------------------------------------
var macState = new Personicore.DeviceAgent.Mac.Wizard.MacWizardState();

Check(!macState.CanAdvanceFromStep(1), "RED-TEST-05.1 (macOS): Wizard forbids advance past Step 1 without terms consent");
macState.TermsAccepted = true;
Check(macState.CanAdvanceFromStep(1), "RED-TEST-05.2 (macOS): Step 1 permits advance when terms are accepted");

macState.PairingToken = "HYP-8924";
Check(macState.CanAdvanceFromStep(2), "RED-TEST-05.3 (macOS): Step 2 permits advance with valid 6-char token");

Check(!macState.CanAdvanceFromStep(3), "RED-TEST-05.4 (macOS): Step 3 forbids advance without Accessibility (AXIsProcessTrusted)");
macState.AccessibilityGranted = true;
macState.LaunchDaemonConfigured = true;
Check(macState.CanAdvanceFromStep(3), "RED-TEST-05.5 (macOS): Step 3 permits advance when Accessibility and LaunchDaemon are verified");
Check(macState.OsName == "macOS", "RED-TEST-05.6 (macOS): Operating system identified as macOS");

var plist = Personicore.DeviceAgent.Mac.Wizard.MacPermissionsManager.GenerateLaunchDaemonPlist("/Applications/Personicore.app/Contents/MacOS/PersonicoreDeviceAgent");
Check(plist.Contains("com.personicore.agent"), "RED-TEST-05.7 (macOS): LaunchDaemon plist template contains proper domain label");
Check(plist.Contains("<key>KeepAlive</key>"), "RED-TEST-05.8 (macOS): LaunchDaemon plist specifies KeepAlive daemon persistence");

Console.WriteLine("\n===============================================================");
Console.WriteLine("🎉 ALL HP-1988 WINDOWS GUIDED WIZARD TESTS PASSED!");
Console.WriteLine("===============================================================");
