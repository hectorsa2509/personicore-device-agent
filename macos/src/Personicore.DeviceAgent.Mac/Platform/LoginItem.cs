using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>
/// "Start at login" through a per-user LaunchAgent: no admin rights, works for a build
/// that is not notarized yet, and shows in System Settings → General → Login Items.
/// </summary>
internal static class LoginItem
{
    private const string Label = "com.personicore.personicore.deviceagent";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    public static bool IsEnabled() => File.Exists(PlistPath);

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(PlistPath);
                Log.Info("Start at login disabled.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, Plist(Environment.ProcessPath ?? string.Empty));
            Log.Info("Start at login enabled.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not change start at login", ex);
        }
    }

    /// <summary>If the app was moved, point the LaunchAgent at the new location.</summary>
    public static void RepairIfMoved()
    {
        try
        {
            if (!IsEnabled()) return;
            var expected = Plist(Environment.ProcessPath ?? string.Empty);
            if (File.ReadAllText(PlistPath) == expected) return;

            File.WriteAllText(PlistPath, expected);
            Log.Info("Start at login pointed to an old location; updated.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check start at login: {ex.Message}");
        }
    }

    private static string Plist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Label}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{System.Security.SecurityElement.Escape(executable)}</string>
            <string>{Program.AutostartArgument}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>LimitLoadToSessionType</key>
          <string>Aqua</string>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>

        """;
}
