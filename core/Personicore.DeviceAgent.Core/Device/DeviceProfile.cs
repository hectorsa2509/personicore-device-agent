namespace Personicore.DeviceAgent.Device;

/// <summary>
/// The four fields to send (type, brand, model, OS) plus the suggested name for the
/// computer. Everything the agent knows about itself.
/// </summary>
public sealed record DeviceProfile(
    string DeviceType,
    string Brand,
    string Model,
    string OperatingSystem,
    string OsVersion,
    string SuggestedName)
{
    public static DeviceProfile Unknown => new(
        "computer", "Unknown", "Unknown",
        System.Runtime.InteropServices.RuntimeInformation.OSDescription, string.Empty, Environment.MachineName);
}
