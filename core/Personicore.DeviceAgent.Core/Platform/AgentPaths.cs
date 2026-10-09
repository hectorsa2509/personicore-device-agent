using System.Runtime.InteropServices;

namespace Personicore.DeviceAgent.Config;

/// <summary>
/// Where the agent keeps its files on each OS. Windows keeps the exact paths of the
/// first releases, so existing state.json and logs are still found after an update.
/// </summary>
public static class AgentPaths
{
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>state.json, the per-user config.json and .env.</summary>
    public static string UserData { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Personicore", "DeviceAgent")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? Path.Combine(Home, "Library", "Application Support", "Personicore", "DeviceAgent")
                : Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), "personicore-device-agent");

    /// <summary>Machine-wide config.json and .env, written by an installer or IT.</summary>
    public static string MachineData { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Personicore", "DeviceAgent")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "/Library/Application Support/Personicore/DeviceAgent"
                : "/etc/personicore-device-agent";

    public static string Logs { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(UserData, "logs")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? Path.Combine(Home, "Library", "Logs", "Personicore")
                : Path.Combine(XdgDirectory("XDG_STATE_HOME", Path.Combine(".local", "state")), "personicore-device-agent", "logs");

    private static string XdgDirectory(string variable, string fallbackUnderHome)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? Path.Combine(Home, fallbackUnderHome) : value;
    }
}
