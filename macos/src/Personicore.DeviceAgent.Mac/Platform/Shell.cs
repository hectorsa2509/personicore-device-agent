using System.Diagnostics;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>Runs the small macOS command-line tools the agent relies on.</summary>
internal static class Shell
{
    /// <summary>Standard output, trimmed; empty string if the tool fails or times out.</summary>
    public static string Run(string file, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return string.Empty;

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                Log.Warn($"{file} timed out.");
                return string.Empty;
            }

            return process.ExitCode == 0 ? output.Result.Trim() : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug($"{file} failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Opens a file or folder with its default app (Finder, TextEdit…).</summary>
    public static void Open(string path) => Run("/usr/bin/open", path);
}
