using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Device;

/// <summary>
/// Reads brand, model, chassis type and operating system from the computer itself.
///
/// Resolution order: the registry first (cheap, no WMI, works from the user's session
/// without elevation), WMI only to fill in what the manufacturer left empty in the BIOS
/// key. In the identification contract this data is an `app self-report` → conf=high;
/// the key is still the MAC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceProfileProvider : IDeviceProfileProvider
{
    DeviceProfile IDeviceProfileProvider.Read() => DeviceProfileProvider.Read();
}

[SupportedOSPlatform("windows")]
public static class DeviceProfileProvider
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static DeviceProfile Read()
    {
        var brand = Clean(ReadLocalMachine(BiosKey, "SystemManufacturer"));
        var model = Clean(ReadLocalMachine(BiosKey, "SystemProductName"));
        var chassis = ParseInt(ReadLocalMachine(BiosKey, "EnclosureType"));

        if (IsPlaceholder(brand) || IsPlaceholder(model) || chassis is null)
        {
            var (wmiBrand, wmiModel, wmiChassis) = ReadFromWmi();
            if (IsPlaceholder(brand)) brand = Clean(wmiBrand);
            if (IsPlaceholder(model)) model = Clean(wmiModel);
            chassis ??= wmiChassis;
        }

        if (IsPlaceholder(brand)) brand = Clean(ReadLocalMachine(BiosKey, "BaseBoardManufacturer"));
        if (IsPlaceholder(model)) model = Clean(ReadLocalMachine(BiosKey, "BaseBoardProduct"));

        var deviceType = ChassisToDeviceType(chassis);

        // With no readable chassis (WMI down; on Windows 10 for ARM it may not be
        // available to an ARM64 process) a battery is the best hint left.
        if (chassis is null && HasBattery()) deviceType = "laptop";

        var profile = new DeviceProfile(
            DeviceType: deviceType,
            Brand: IsPlaceholder(brand) ? "Unknown" : brand,
            Model: IsPlaceholder(model) ? "Unknown" : model,
            OperatingSystem: ReadOperatingSystem(),
            OsVersion: ReadOsVersion(),
            SuggestedName: Environment.MachineName);

        Log.Debug($"Device profile: type={profile.DeviceType} brand={profile.Brand} " +
                  $"model={profile.Model} os={profile.OperatingSystem} ({profile.OsVersion})");

        return profile;
    }

    /// <summary>
    /// The OS's marketing name. In the registry, ProductName still says "Windows 10" on
    /// Windows 11: the only reliable signal is the build number (>= 22000 ⇒ Windows 11),
    /// so it is corrected here.
    /// </summary>
    private static string ReadOperatingSystem()
    {
        var productName = Clean(ReadLocalMachine(CurrentVersionKey, "ProductName"));
        var build = ParseInt(ReadLocalMachine(CurrentVersionKey, "CurrentBuild")) ?? 0;

        if (string.IsNullOrEmpty(productName))
            productName = RuntimeInformation.OSDescription;

        if (build >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);

        var display = Clean(ReadLocalMachine(CurrentVersionKey, "DisplayVersion"));
        if (string.IsNullOrEmpty(display))
            display = Clean(ReadLocalMachine(CurrentVersionKey, "ReleaseId"));

        return string.IsNullOrEmpty(display) ? productName : $"{productName} {display}";
    }

    private static string ReadOsVersion()
    {
        var build = Clean(ReadLocalMachine(CurrentVersionKey, "CurrentBuild"));
        var ubr = ParseInt(ReadLocalMachine(CurrentVersionKey, "UBR"));

        if (string.IsNullOrEmpty(build)) return Environment.OSVersion.Version.ToString();
        return ubr is null ? build : $"{build}.{ubr}";
    }

    /// <summary>
    /// SMBIOS chassis codes, mapped to the vocabulary device_fingerprint.sh already uses
    /// on the node: desktop / laptop / tablet / server / computer.
    /// </summary>
    private static string ChassisToDeviceType(int? chassis) => chassis switch
    {
        3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 24 or 34 or 35 or 36 => "desktop",
        8 or 9 or 10 or 11 or 14 => "laptop",
        30 or 31 or 32 => "tablet",
        17 or 23 or 25 or 28 => "server",
        _ => "computer",
    };

    private static bool HasBattery()
    {
        try
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus;
            return !status.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                   && !status.HasFlag(System.Windows.Forms.BatteryChargeStatus.Unknown);
        }
        catch (Exception ex)
        {
            Log.Debug($"Battery status unreadable: {ex.Message}");
            return false;
        }
    }

    private static (string Brand, string Model, int? Chassis) ReadFromWmi()
    {
        var brand = string.Empty;
        var model = string.Empty;
        int? chassis = null;

        try
        {
            using var system = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var row in system.Get().Cast<ManagementObject>())
            {
                brand = row["Manufacturer"]?.ToString() ?? string.Empty;
                model = row["Model"]?.ToString() ?? string.Empty;
                break;
            }

            using var enclosure = new ManagementObjectSearcher(
                "SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var row in enclosure.Get().Cast<ManagementObject>())
            {
                if (row["ChassisTypes"] is ushort[] { Length: > 0 } types) chassis = types[0];
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI unavailable for brand/model: {ex.Message}");
        }

        return (brand, model, chassis);
    }

    private static string ReadLocalMachine(string key, string value)
    {
        try
        {
            using var handle = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var subKey = handle.OpenSubKey(key);
            return subKey?.GetValue(value)?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug($"Registry {key}\\{value} unreadable: {ex.Message}");
            return string.Empty;
        }
    }

    private static string Clean(string? raw) => raw?.Trim() ?? string.Empty;

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw?.Trim(), out var parsed) ? parsed : null;

    /// <summary>
    /// Many OEMs leave a literal "To Be Filled By O.E.M." or "System Product Name" in
    /// the BIOS. Treating those as valid data pollutes the panel.
    /// </summary>
    private static bool IsPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var lowered = value.Trim().ToLowerInvariant();
        return lowered is "unknown" or "none" or "default string" or "not specified"
            || lowered.Contains("to be filled")
            || lowered.Contains("o.e.m.")
            || lowered.StartsWith("system product", StringComparison.Ordinal)
            || lowered.StartsWith("system manufacturer", StringComparison.Ordinal);
    }
}
