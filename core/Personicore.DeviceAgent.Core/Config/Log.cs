using System.Text;

namespace Personicore.DeviceAgent.Config;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// File log with size-based rotation. No dependencies: the agent lives on the
/// panelist's computer and support is done by reading this file.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeptFiles = 3;

    private static readonly object Gate = new();
    private static LogLevel _minLevel = LogLevel.Info;

    public static string Directory { get; } = AgentPaths.Logs;

    public static string FilePath { get; } = Path.Combine(Directory, "agent.log");

    public static void Configure(string level)
    {
        _minLevel = level.Trim().ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => LogLevel.Info,
        };
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        if (level < _minLevel) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToString().ToUpperInvariant(),-5}] {message}";

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                Rotate();
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Nunca tumbar el agente por no poder escribir el log.
            }
        }
    }

    private static void Rotate()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes) return;

        var oldest = $"{FilePath}.{KeptFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = KeptFiles - 1; i >= 1; i--)
        {
            var from = $"{FilePath}.{i}";
            if (File.Exists(from)) File.Move(from, $"{FilePath}.{i + 1}", overwrite: true);
        }

        File.Move(FilePath, $"{FilePath}.1", overwrite: true);
    }
}
