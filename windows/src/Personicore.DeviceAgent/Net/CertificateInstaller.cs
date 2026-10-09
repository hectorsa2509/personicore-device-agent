using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Personicore.DeviceAgent.Certificates;

namespace Personicore.DeviceAgent.Net;

internal static class CertificateInstaller
{
    public const string Argument = "--install-certificate";

    public static bool IsTrusted()
    {
        try
        {
            if (!IsInstalled()) return false;
            using var certificate = BundledCertificate.Load();
            using var chain = new X509Chain();
            // Use the real OS roots and distrust policy, never CustomRootTrust.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            if (chain.Build(certificate)) return true;
            Config.Log.Warn("Windows rejected HTTPS CA trust: " + string.Join("; ",
                chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}")));
            return false;
        }
        catch (Exception ex)
        {
            Config.Log.Error("Could not verify Windows HTTPS certificate trust", ex);
            return false;
        }
    }

    public static bool IsInstalled()
    {
        using var certificate = BundledCertificate.Load();
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Contains(certificate);
    }

    // The elevated process accepts no certificate path: it can only install our resource.
    public static int InstallElevated()
    {
        try
        {
            using var certificate = BundledCertificate.Load();
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            return IsTrusted() ? 0 : 2;
        }
        catch { return 1; }
    }

    public static async Task InstallAsync()
    {
        if (IsTrusted()) return;
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Argument)
        {
            UseShellExecute = true,
            Verb = "runas",
        }) ?? throw new InvalidOperationException("Certificate installer did not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode == 2)
            throw new InvalidOperationException("Certificate imported into LocalMachine/Root, but Windows did not trust it. Check certificate policy and the agent log.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Certificate installation failed (exit {process.ExitCode}).");
        if (!IsTrusted())
            throw new InvalidOperationException("The certificate is installed, but is not trusted for the signed-in user. Check certificate policy and the agent log.");
    }
}
