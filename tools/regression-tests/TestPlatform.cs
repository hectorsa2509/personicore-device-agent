namespace Personicore.DeviceAgent.Config;

public static class AgentPaths
{
    private static string Root => Environment.GetEnvironmentVariable("PERSONICORE_TEST_ROOT")
        ?? throw new InvalidOperationException("Test root must be set before accessing files.");
    public static string UserData => Path.Combine(Root, "user");
    public static string MachineData => Path.Combine(Root, "machine");
    public static string Logs => Path.Combine(Root, "logs");
}

public static class Log
{
    public static string FilePath => Path.Combine(AgentPaths.Logs, "agent.log");
    public static void Info(string message) { }
    public static void Debug(string message) { }
    public static void Warn(string message) { }
    public static void Error(string message, Exception? exception = null) { }
}
