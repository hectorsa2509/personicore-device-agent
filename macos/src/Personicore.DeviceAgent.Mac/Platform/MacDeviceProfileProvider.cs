using System.Text.Json;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Device;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>
/// Brand, model, type and OS on macOS. Brand is always Apple; the model identifier
/// (hw.model, e.g. Mac15,12) plus the marketing name from system_profiler
/// (e.g. MacBook Air) is what tells devices apart in the panel.
/// </summary>
public sealed class MacDeviceProfileProvider : IDeviceProfileProvider
{
    public DeviceProfile Read()
    {
        var identifier = Shell.Run("/usr/sbin/sysctl", "-n", "hw.model");
        var marketing = ReadMarketingName();
        var version = Shell.Run("/usr/bin/sw_vers", "-productVersion");
        var build = Shell.Run("/usr/bin/sw_vers", "-buildVersion");
        var computerName = Shell.Run("/usr/sbin/scutil", "--get", "ComputerName");

        var model = (marketing, identifier) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{marketing} ({identifier})",
            ({ Length: > 0 }, _) => marketing,
            (_, { Length: > 0 }) => identifier,
            _ => "Unknown",
        };

        // Portables are named MacBook*; everything else (iMac, Mac mini, Mac Studio,
        // Mac Pro) is a desktop.
        var type = (marketing + identifier).Contains("MacBook", StringComparison.OrdinalIgnoreCase)
            ? "laptop"
            : string.IsNullOrEmpty(marketing + identifier) ? "computer" : "desktop";

        var profile = new DeviceProfile(
            DeviceType: type,
            Brand: "Apple",
            Model: model,
            OperatingSystem: string.IsNullOrEmpty(version) ? "macOS" : $"macOS {ReleaseName(version)}{version}",
            OsVersion: build,
            SuggestedName: string.IsNullOrWhiteSpace(computerName) ? Environment.MachineName : computerName);

        Log.Debug($"Device profile: type={profile.DeviceType} brand={profile.Brand} " +
                  $"model={profile.Model} os={profile.OperatingSystem} ({profile.OsVersion})");

        return profile;
    }

    private static string ReadMarketingName()
    {
        var json = Shell.Run("/usr/sbin/system_profiler", "SPHardwareDataType", "-json");
        if (string.IsNullOrEmpty(json)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var hardware = document.RootElement.GetProperty("SPHardwareDataType")[0];
            return hardware.TryGetProperty("machine_name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            Log.Debug($"system_profiler output not understood: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>"Sequoia " for 15.x, so the panel reads like System Settings does.</summary>
    private static string ReleaseName(string version) =>
        (int.TryParse(version.Split('.')[0], out var major) ? major : 0) switch
        {
            12 => "Monterey ",
            13 => "Ventura ",
            14 => "Sonoma ",
            15 => "Sequoia ",
            26 => "Tahoe ",
            _ => string.Empty,
        };
}
