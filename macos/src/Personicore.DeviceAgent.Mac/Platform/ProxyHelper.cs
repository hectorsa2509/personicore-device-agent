using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>What the helper answered, already parsed.</summary>
/// <param name="Ok">false when the helper refused or could not be reached.</param>
/// <param name="Message">Detail for the log and the menu.</param>
public sealed record HelperResult(bool Ok, string Message)
{
    public static HelperResult NotInstalled { get; } = new(false, "the proxy helper is not installed");

    /// <summary>
    /// The answer to a re-point when the panelist has taken over every proxy this helper
    /// had set: there is nothing of ours left to move.
    /// </summary>
    public bool Nothing => Message.Equals("none", StringComparison.Ordinal);
}

/// <summary>
/// What root says it still owns. The agent cannot read the helper's files —they are
/// root's— and its own copy of the state goes stale the moment the watchdog acts, so this
/// is the authoritative answer to "is the proxy out there still ours?".
/// </summary>
/// <param name="Count">Proxy settings still owned, across services and kinds.</param>
/// <param name="Address">
/// The address they all point at, or empty when there are none — and also when they do
/// not agree (see <paramref name="Mixed"/>), because then there is no one address the
/// proxy is at.
/// </param>
/// <param name="Mixed">
/// true when the owned settings are not all at the same address, which is what a
/// re-point macOS accepted for one of them and refused for another leaves behind. The
/// agent cannot see it —it reads only the web proxy of each service— and would otherwise
/// report the proxy healthy while one setting pointed at an address the node has left.
/// </param>
/// <param name="Paused">
/// true when the watchdog is the one that took the proxy out, because no meter node
/// answered. It is what tells "we paused it" apart from "the panelist turned it off",
/// which are opposite answers: one comes back by itself at home, the other must not.
/// </param>
internal sealed record HelperOwnership(int Count, string Address, bool Paused, bool Mixed = false);

/// <summary>
/// Client for the root helper (macos/helper/proxy-helper.sh).
///
/// macOS proxies are system-wide and need admin rights, so the agent cannot set them by
/// itself. The helper is installed once —one administrator prompt— and from then on the
/// agent talks to it over a local socket, with no further prompts. That matters because
/// the agent also changes the proxy on its own: it pauses it when the Mac leaves home.
/// </summary>
internal static class ProxyHelper
{
    public const string Label = "com.personicore.personicore.proxyhelper";

    private const string SocketPath = "/var/run/personicore/proxy.sock";
    private const string ScriptPath = "/Library/Application Support/Personicore/proxy-helper.sh";
    private const string VersionPath = "/Library/Application Support/Personicore/helper-version.txt";
    private const string PlistPath = "/Library/LaunchDaemons/" + Label + ".plist";
    private const string WatchdogLabel = Label + ".watchdog";
    private const string WatchdogPlistPath = "/Library/LaunchDaemons/" + WatchdogLabel + ".plist";

    /// <summary>
    /// Bumped whenever the script or its LaunchDaemons change. A Mac that already has an
    /// older helper keeps working with it —it still applies and restores— and is offered
    /// the new one the next time the panelist turns the switch on, which is the only
    /// moment an administrator prompt is expected.
    /// </summary>
    private const int Version = 10;

    public static bool IsInstalled => File.Exists(PlistPath) && File.Exists(ScriptPath);

    /// <summary>true when an older helper is installed and could be replaced.</summary>
    public static bool NeedsUpgrade => IsInstalled && InstalledVersion < Version;

    private static int InstalledVersion
    {
        get
        {
            try
            {
                return File.Exists(VersionPath) && int.TryParse(File.ReadAllText(VersionPath).Trim(), out var v)
                    ? v
                    : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }
    }

    /// <param name="host">
    /// The name the node was found by. Root remembers it so the watchdog can follow the
    /// node to a new address on its own, with no agent running.
    /// </param>
    /// <param name="householdMac">
    /// The MAC the node is registered under, when this computer is registered: root keeps it
    /// and its watchdog then follows only that node. Empty before the first registration,
    /// where there is nothing yet to be foreign to.
    /// </param>
    public static HelperResult Apply(string nodeIp, string host, string householdMac = "") =>
        Send(Command("APPLY", nodeIp, host, householdMac));

    /// <summary>
    /// Follows the node to a new address, touching only what the helper still owns. The
    /// agent does this on its own, without anybody clicking anything, so it must not be
    /// APPLY: that one sets every service and both kinds, and would write over a proxy
    /// the panelist has taken over in System Settings since the switch went on.
    /// </summary>
    public static HelperResult Repoint(string nodeIp, string host, string householdMac = "") =>
        Send(Command("REPOINT", nodeIp, host, householdMac));

    /// <summary>
    /// "<c>VERB ip [host [mac]]</c>" — the helper reads them positionally, so the host is
    /// held by a dash when there is none and a MAC to pass.
    /// </summary>
    private static string Command(string verb, string nodeIp, string host, string householdMac)
    {
        var mac = householdMac.Trim();
        var name = host.Trim();

        if (mac.Length == 0) return name.Length == 0 ? $"{verb} {nodeIp}" : $"{verb} {nodeIp} {name}";

        return $"{verb} {nodeIp} {(name.Length == 0 ? "-" : name)} {mac}";
    }

    public static HelperResult Restore() => Send("RESTORE");

    /// <summary>
    /// What root still owns, or null when the helper did not answer — in which case the
    /// agent keeps what it had rather than guessing.
    /// </summary>
    public static HelperOwnership? Owned()
    {
        var result = Send("OWNED");
        if (!result.Ok) return null;

        // "owned <count> <address|none> <paused|active>"
        var parts = result.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 4
            || !parts[0].Equals("owned", StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var count))
        {
            Log.Warn($"The proxy helper answered OWNED with \"{result.Message}\", which this agent "
                     + "does not understand; leaving the proxy state alone.");
            return null;
        }

        var mixed = parts[2].Equals("mixed", StringComparison.Ordinal);

        return new HelperOwnership(
            count,
            mixed || parts[2].Equals("none", StringComparison.Ordinal) ? string.Empty : parts[2],
            parts[3].Equals("paused", StringComparison.Ordinal),
            mixed);
    }

    /// <summary>
    /// Installs the helper, asking for an administrator password once. Everything root
    /// runs is written here and owned by root afterwards, so the agent —which runs as the
    /// panelist— cannot change what the privileged side executes.
    /// </summary>
    public static HelperResult Install()
    {
        try
        {
            var staging = Path.Combine(Path.GetTempPath(), "personicore-proxy-helper");
            Directory.CreateDirectory(staging);

            var script = Path.Combine(staging, "proxy-helper.sh");
            var plist = Path.Combine(staging, Label + ".plist");
            var watchdog = Path.Combine(staging, WatchdogLabel + ".plist");
            using var ca = Personicore.DeviceAgent.Certificates.BundledCertificate.Load();
            File.WriteAllText(script, ReadEmbedded("proxy-helper.sh")
                .Replace("__PERSONICORE_CA_BASE64__", Convert.ToBase64String(ca.RawData), StringComparison.Ordinal));
            File.WriteAllText(plist, LaunchDaemonPlist());
            File.WriteAllText(watchdog, WatchdogPlist());

            var install = $"""
                set -e
                mkdir -p '/Library/Application Support/Personicore'
                cp '{script}' '{ScriptPath}'
                chown root:wheel '{ScriptPath}'
                chmod 755 '{ScriptPath}'
                cp '{plist}' '{PlistPath}'
                chown root:wheel '{PlistPath}'
                chmod 644 '{PlistPath}'
                cp '{watchdog}' '{WatchdogPlistPath}'
                chown root:wheel '{WatchdogPlistPath}'
                chmod 644 '{WatchdogPlistPath}'
                printf '{Version}' > '{VersionPath}'
                chown root:wheel '{VersionPath}'
                chmod 644 '{VersionPath}'
                mkdir -p /var/run/personicore
                chown root:staff /var/run/personicore
                chmod 750 /var/run/personicore
                launchctl bootout system/{Label} 2>/dev/null || true
                launchctl bootstrap system '{PlistPath}'
                launchctl bootout system/{WatchdogLabel} 2>/dev/null || true
                launchctl bootstrap system '{WatchdogPlistPath}'
                """;

            // One password prompt, from macOS itself.
            var result = Shell.Run("/usr/bin/osascript", "-e",
                $"do shell script {AppleScriptQuote(install)} with administrator privileges");

            Directory.Delete(staging, recursive: true);

            if (!IsInstalled)
            {
                Log.Warn($"The proxy helper was not installed (cancelled or failed): {result}");
                return new HelperResult(false, "the administrator prompt was cancelled or the install failed");
            }

            Log.Info("Proxy helper installed.");
            return new HelperResult(true, "installed");
        }
        catch (Exception ex)
        {
            Log.Error("Could not install the proxy helper", ex);
            return new HelperResult(false, ex.Message);
        }
    }

    /// <summary>Removes the helper. Asks for the administrator password once.</summary>
    public static HelperResult Uninstall()
    {
        var remove = $"""
            launchctl bootout system/{Label} 2>/dev/null || true
            launchctl bootout system/{WatchdogLabel} 2>/dev/null || true
            rm -f '{PlistPath}' '{WatchdogPlistPath}' '{ScriptPath}' '{VersionPath}'
            rm -rf /var/run/personicore
            """;

        Shell.Run("/usr/bin/osascript", "-e",
            $"do shell script {AppleScriptQuote(remove)} with administrator privileges");

        return IsInstalled
            ? new HelperResult(false, "the helper is still installed")
            : new HelperResult(true, "removed");
    }

    private static HelperResult Send(string command)
    {
        if (!IsInstalled) return HelperResult.NotInstalled;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.SendTimeout = 5_000;
            socket.ReceiveTimeout = 30_000;

            socket.Send(Encoding.UTF8.GetBytes(command + "\n"));
            socket.Shutdown(SocketShutdown.Send);

            var answer = new StringBuilder();
            var buffer = new byte[1024];
            int read;
            while ((read = socket.Receive(buffer)) > 0) answer.Append(Encoding.UTF8.GetString(buffer, 0, read));

            // The last line is OK/ERROR; STATUS prints one line per network service first.
            var last = answer.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim() ?? string.Empty;

            return last.StartsWith("OK", StringComparison.Ordinal)
                ? new HelperResult(true, last[2..].Trim())
                : new HelperResult(false, last.StartsWith("ERROR", StringComparison.Ordinal) ? last[5..].Trim() : "no answer from the helper");
        }
        catch (Exception ex)
        {
            Log.Error($"The proxy helper did not answer ({command})", ex);
            return new HelperResult(false, ex.Message);
        }
    }

    /// <summary>
    /// launchd runs the script in inetd mode: one process per connection, the socket as
    /// stdin/stdout. The socket lives in a directory only local users can enter.
    /// </summary>
    private static string LaunchDaemonPlist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Label}</string>
          <key>ProgramArguments</key>
          <array>
            <string>/bin/sh</string>
            <string>{ScriptPath}</string>
          </array>
          <key>inetdCompatibility</key>
          <dict>
            <key>Wait</key>
            <false/>
          </dict>
          <key>Sockets</key>
          <dict>
            <key>Listener</key>
            <dict>
              <key>SockFamily</key>
              <string>Unix</string>
              <key>SockPathName</key>
              <string>{SocketPath}</string>
              <key>SockPathMode</key>
              <integer>438</integer>
            </dict>
          </dict>
        </dict>
        </plist>

        """;

    /// <summary>
    /// The safety net, every minute: if the proxy the agent set no longer answers, root
    /// follows the node to its new address, and removes the proxy when there is no node
    /// at all. It runs even when the agent does not, so a Mac whose agent was killed is
    /// never left browsing through an address that is gone. One curl on the LAN a minute
    /// is cheap; a panelist who cannot browse is not.
    /// </summary>
    private static string WatchdogPlist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{WatchdogLabel}</string>
          <key>ProgramArguments</key>
          <array>
            <string>/bin/sh</string>
            <string>{ScriptPath}</string>
            <string>watchdog</string>
          </array>
          <key>StartInterval</key>
          <integer>60</integer>
          <key>RunAtLoad</key>
          <true/>
        </dict>
        </plist>

        """;

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>AppleScript string literal: only backslash and quote need escaping.</summary>
    private static string AppleScriptQuote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
