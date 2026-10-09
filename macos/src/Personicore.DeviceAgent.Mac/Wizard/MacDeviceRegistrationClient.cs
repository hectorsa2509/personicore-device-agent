using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Wizard;

public sealed record MacRegistrationResult(
    bool Success,
    string DeviceId,
    string HouseholdId,
    string AuthToken,
    string ErrorCode,
    string Message,
    string ActionableGuidance);

/// <summary>
/// Client implementing the HP-1988 device registration contract for macOS.
/// Handles payload formulation, hardware fingerprinting, retry resiliency,
/// and local storage in /Library/Application Support/Personicore/config.json.
/// </summary>
public static class MacDeviceRegistrationClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static async Task<MacRegistrationResult> RegisterDeviceAsync(
        string endpointUrl,
        MacWizardState state,
        CancellationToken cancellationToken = default)
    {
        var url = endpointUrl.TrimEnd('/') + "/api/v1/devices/register";

        var payload = new
        {
            pairing_token = state.PairingToken.Trim(),
            device_fingerprint = new
            {
                device_uuid = state.DeviceUuid,
                hostname = state.Hostname,
                os_name = state.OsName,
                os_version = state.OsVersion,
                os_arch = state.OsArch,
                app_version = state.AppVersion,
            },
            permissions_status = new
            {
                accessibility_granted = state.AccessibilityGranted,
                screen_capture_granted = state.ScreenCaptureGranted,
                background_service_installed = state.LaunchDaemonConfigured,
            },
        };

        var jsonBody = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Client-Version", state.AppVersion);
        request.Headers.Add("X-Client-Platform", $"darwin-{state.OsArch}");

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            if (response.IsSuccessStatusCode)
            {
                var deviceId = root.TryGetProperty("device_id", out var devProp) ? devProp.GetString() ?? "" : "";
                var householdId = root.TryGetProperty("household_id", out var hhProp) ? hhProp.GetString() ?? "" : "";
                var authToken = root.TryGetProperty("auth_token", out var tokProp) ? tokProp.GetString() ?? "" : "";

                state.HandshakeSuccessful = true;
                state.RegisteredDeviceId = deviceId;
                state.RegisteredHouseholdId = householdId;
                state.RegistrationAuthToken = authToken;
                state.LastError = string.Empty;
                state.ActionableGuidance = string.Empty;

                PersistConfigToDisk(state);

                return new MacRegistrationResult(true, deviceId, householdId, authToken, "", "Registration succeeded", "");
            }

            var errorCode = root.TryGetProperty("error_code", out var ecProp) ? ecProp.GetString() ?? "REGISTRATION_FAILED" : "HTTP_" + (int)response.StatusCode;
            var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Registration failed." : "Server responded with status " + response.StatusCode;
            var guidance = root.TryGetProperty("actionable_guidance", out var gProp) ? gProp.GetString() ?? "Please verify your pairing code and network connection." : "Please verify your code and retry.";

            state.HandshakeSuccessful = false;
            state.LastError = $"[{errorCode}] {message}";
            state.ActionableGuidance = guidance;

            return new MacRegistrationResult(false, "", "", "", errorCode, message, guidance);
        }
        catch (HttpRequestException ex)
        {
            var err = $"Connection error: {ex.Message}";
            state.HandshakeSuccessful = false;
            state.LastError = err;
            state.ActionableGuidance = "Ensure your internet connection is active and firewall allows outbound traffic.";
            return new MacRegistrationResult(false, "", "", "", "NETWORK_ERROR", err, state.ActionableGuidance);
        }
        catch (TaskCanceledException)
        {
            var err = "Registration request timed out.";
            state.HandshakeSuccessful = false;
            state.LastError = err;
            state.ActionableGuidance = "The server took too long to answer. Click Retry to re-send the request.";
            return new MacRegistrationResult(false, "", "", "", "TIMEOUT", err, state.ActionableGuidance);
        }
        catch (Exception ex)
        {
            var err = $"Unexpected error: {ex.Message}";
            state.HandshakeSuccessful = false;
            state.LastError = err;
            state.ActionableGuidance = "An unhandled error occurred during registration. Please contact support.";
            return new MacRegistrationResult(false, "", "", "", "INTERNAL_ERROR", err, state.ActionableGuidance);
        }
    }

    public static void PersistConfigToDisk(MacWizardState state)
    {
        try
        {
            var baseDir = "/Library/Application Support/Personicore";
            if (!Directory.Exists(baseDir))
            {
                // Fallback to user library if root is unavailable
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Personicore");
            }
            Directory.CreateDirectory(baseDir);

            var path = Path.Combine(baseDir, "config.json");
            var config = new
            {
                deviceId = state.RegisteredDeviceId,
                householdId = state.RegisteredHouseholdId,
                authToken = state.RegistrationAuthToken,
                registeredAt = DateTime.UtcNow.ToString("o"),
                appVersion = state.AppVersion,
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
            Log.Info($"Registered configuration written to {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed writing config to Library/Application Support: {ex.Message}");
        }
    }
}
