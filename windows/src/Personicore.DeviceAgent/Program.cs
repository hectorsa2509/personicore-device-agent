using System.Runtime.Versioning;
using System.Windows.Forms;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Ui;

namespace Personicore.DeviceAgent;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Local\ so the instance is unique per user session: two agents at once would fight
    /// over the same state file and send the same assignment twice.
    /// </summary>
    private const string InstanceMutexName = @"Local\Personicore.DeviceAgent.SingleInstance";

    /// <summary>
    /// Passed by the Run key and the scheduled task. It tells an automatic start from a
    /// panelist's double click: only the second is told that it is already running.
    /// Without this the hourly task would put up a dialog every hour.
    /// </summary>
    public const string AutostartArgument = "--autostart";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == Net.CertificateInstaller.Argument)
            return Net.CertificateInstaller.InstallElevated();

        // Run by the scheduled task every few minutes: one check, no UI, no mutex.
        if (args.Contains(Agent.ProxyWatchdog.Argument, StringComparer.OrdinalIgnoreCase))
            return Agent.ProxyWatchdog.Run();

        ApplicationConfiguration.Initialize();

        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            var automatic = args.Any(a => string.Equals(a, AutostartArgument, StringComparison.OrdinalIgnoreCase));
            if (!automatic)
            {
                MessageBox.Show(
                    "The Personicore agent is already running. Look for it in the notification area.",
                    "Personicore", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        var config = AgentConfig.Load(out var problems);
        Log.Configure(config.LogLevel);
        Log.Info($"— Personicore Device Agent starting · v{typeof(Program).Assembly.GetName().Version}");

        foreach (var problem in problems) Log.Warn(problem);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception", ex);
        };

        var env = EnvSettings.Load();
        var state = AgentState.Load();

        using var worker = new AgentWorker(
            config, env, state, new Device.WindowsDeviceProfileProvider(),
            new CertificateGuardProxy(new ProxySwitch(state), Net.CertificateInstaller.IsTrusted),
            Net.CertificateInstaller.IsTrusted);
        using var tray = new TrayApplicationContext(worker);

        worker.Start();
        Application.Run(tray);

        Log.Info("— Personicore Device Agent stopped");
        return 0;
    }
}
