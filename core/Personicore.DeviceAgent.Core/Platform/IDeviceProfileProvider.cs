namespace Personicore.DeviceAgent.Device;

/// <summary>
/// Brand, model, type and OS of this computer. Each platform reads them its own way
/// (Windows: BIOS registry + WMI; macOS: sysctl + system_profiler).
/// </summary>
public interface IDeviceProfileProvider
{
    DeviceProfile Read();
}
