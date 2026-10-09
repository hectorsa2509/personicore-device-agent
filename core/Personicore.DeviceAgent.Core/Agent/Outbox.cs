using System.Text;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Graph;

namespace Personicore.DeviceAgent.Agent;

/// <summary>
/// Offline mode: instead of calling the backend, the agent writes here what it would
/// have sent. The dumped body is the same <see cref="GraphQlRequest"/> object the real
/// send would use, so it cannot drift from the code that sends it.
///
/// The file is appended to: every cycle leaves an entry, and seeing the same assignment
/// repeated with a different IP is exactly the proof that the agent re-asserts after a
/// cambio de lease.
/// </summary>
public static class Outbox
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>
    /// The dump goes to the desktop of the user running the agent, not to AppData: the
    /// point is that whoever installs the agent on any computer sees the file without
    /// being told where to look. If the desktop cannot be resolved (redirected profile,
    /// service account) it falls back to AppData, which always exists.
    /// </summary>
    public static string Directory { get; } = ResolveDirectory();

    public static string FilePath { get; } = Path.Combine(Directory, "Personicore-outbox.txt");

    private static string ResolveDirectory()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path)) return path;
        }

        return AgentPaths.UserData;
    }

    /// <summary>
    /// Records a simulated send.
    /// </summary>
    /// <param name="endpoint">Where the POST would go.</param>
    /// <param name="jwtSource">Where the credential would come from, without dumping the token.</param>
    /// <param name="snapshot">Contexto: hogar, equipo e identidad observada.</param>
    /// <param name="device">The device data exactly as it would be sent.</param>
    /// <param name="ownerNames">What the panelist answered.</param>
    /// <param name="requests">The requests that would be made, in order.</param>
    public static void Record(
        string endpoint,
        string jwtSource,
        AgentSnapshot snapshot,
        DeviceRegistration device,
        IReadOnlyList<string> ownerNames,
        IReadOnlyList<GraphQlRequest> requests)
    {
        var text = new StringBuilder();

        text.AppendLine(new string('═', 78));
        text.AppendLine($"  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}   SIMULATED SEND (no registration mutation was sent)");
        text.AppendLine(new string('═', 78));
        text.AppendLine();

        text.AppendLine("HOUSEHOLD");
        text.AppendLine($"  Meter node        {Show(snapshot.RouterBaseUrl)}");
        text.AppendLine($"  Router MAC        {Show(snapshot.RouterMac)}");
        text.AppendLine($"  Router ID         {Show(snapshot.RouterId, "(provided by the API)")}");
        text.AppendLine($"  Household ID      {Show(snapshot.HouseholdId, "(provided by the API)")}");
        text.AppendLine();

        text.AppendLine("THIS COMPUTER");
        text.AppendLine($"  IP                {Show(snapshot.LocalIp)}");
        text.AppendLine($"  Interface         {Show(snapshot.InterfaceName)}");
        text.AppendLine($"  MAC               {Show(snapshot.Mac)}{(snapshot.MacIsRandomized ? "   (randomized)" : string.Empty)}");
        text.AppendLine($"  machineId         {Show(snapshot.MachineId)}");
        text.AppendLine();

        text.AppendLine("DEVICE DATA");
        text.AppendLine($"  Name              {Show(device.FriendlyName)}");
        text.AppendLine($"  Type              {Show(device.DeviceType)}");
        text.AppendLine($"  Brand             {Show(device.DeviceBrand)}");
        text.AppendLine($"  Model             {Show(device.DeviceModel)}");
        text.AppendLine($"  Operating system  {Show(device.DeviceOs)}");
        text.AppendLine($"  Shared            {(device.DeviceShared ? "yes" : "no")}");
        text.AppendLine();

        text.AppendLine("OWNER AS DECLARED BY THE PANELIST");
        text.AppendLine($"  Names             {(ownerNames.Count == 0 ? "(not declared)" : string.Join(", ", ownerNames))}");

        var ids = device.PanelistIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        text.AppendLine(ids.Count > 0
            ? $"  panelistIds       {string.Join(", ", ids)}"
            : "  panelistIds       (empty: real IDs only come from getPanelistsRouter)");
        text.AppendLine();

        text.AppendLine("REQUESTS THAT WOULD BE SENT");
        text.AppendLine($"  POST              {endpoint}");
        text.AppendLine($"  Authorization     {jwtSource}");
        text.AppendLine("  Content-Type      application/json");
        text.AppendLine("  Body              {\"query\": <query>, \"variables\": <variables>}");
        text.AppendLine("  Note              empty fields (routerId, householdId, panelistIds)");
        text.AppendLine("                    are filled in by the getPanelistsRouter response");
        text.AppendLine();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];

            text.AppendLine($"  ── {i + 1}/{requests.Count} · {request.OperationName} " + new string('─', 46));
            text.AppendLine();
            text.AppendLine("  query:");
            text.AppendLine(Indent(request.Query, "    "));
            text.AppendLine();
            text.AppendLine("  variables:");
            text.AppendLine(Indent(request.VariablesJson(), "    "));
            text.AppendLine();
        }

        Append(text.ToString(), FilePath);
    }

    private static string Show(string value, string fallback = "(unknown)") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Indent(string text, string prefix) =>
        string.Join(Environment.NewLine,
            text.Split('\n').Select(line => prefix + line.TrimEnd('\r')));

    internal static void Append(string entry, string filePath)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                // The file is meant to be read by hand: past a certain size it stops
                // being useful. The previous batch is kept in case someone was reading it.
                var info = new FileInfo(filePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(filePath, filePath + ".1", overwrite: true);

                File.AppendAllText(filePath, entry, Encoding.UTF8);
                Log.Info($"Simulated send written to {filePath}");
            }
            catch (Exception ex)
            {
                Log.Error("Could not write the outbox file", ex);
                throw;
            }
        }
    }
}
