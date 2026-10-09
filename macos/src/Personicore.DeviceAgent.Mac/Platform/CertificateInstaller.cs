using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Personicore.DeviceAgent.Certificates;

namespace Personicore.DeviceAgent.Mac.Platform;

internal static class CertificateInstaller
{
    public static bool IsTrusted()
    {
        string? path = null;
        try
        {
            using var certificate = BundledCertificate.Load();
            path = Path.GetTempFileName();
            File.WriteAllBytes(path, certificate.RawData);
            var start = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // -l permits a CA as the target. No -r: the bundled CA must not become
            // a temporary trust anchor for its own verification. SSL policy honors
            // Keychain trust settings, including user overrides and explicit distrust.
            foreach (var argument in new[] { "verify-cert", "-c", path, "-p", "ssl", "-l", "-L", "-q" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                process.Kill();
                process.WaitForExit();
                return false;
            }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Personicore.DeviceAgent.Config.Log.Error("Could not verify macOS HTTPS certificate trust", ex);
            return false;
        }
        finally
        {
            if (path is not null)
            {
                try { File.Delete(path); }
                catch (Exception ex) { Personicore.DeviceAgent.Config.Log.Warn($"Could not remove temporary CA: {ex.Message}"); }
            }
        }
    }

    public static async Task InstallAsync()
    {
        using var certificate = BundledCertificate.Load();
        // A previous attempt may have imported successfully but failed granting trust.
        if (!await IsImportedAsync()) await ImportIntoSystemKeychainAsync(certificate.RawData);
        if (!await IsImportedAsync())
            throw new InvalidOperationException("The certificate was not found in the System keychain after import.");
        // Keep authorization in this GUI process. Elevating an osascript shell prevents
        // Security.framework from showing the separate trust-consent dialog on macOS.
        await Task.Run(() =>
        {
            NativeCertificateTrust.Authorize(certificate.RawData);
            if (!NativeCertificateTrust.HasAlwaysTrust(certificate.RawData) || !IsTrusted())
                throw new InvalidOperationException("Certificate imported, but system trust could not be verified. Check user trust overrides or management policy.");
        });
    }

    internal static async Task<bool> IsImportedAsync()
    {
        var start = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "find-certificate", "-a", "-p", "/Library/Keychains/System.keychain" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not read the System keychain.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(); throw; }
        var pem = await output;
        var detail = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not read the System keychain: " + detail);
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
            return certificates.Any(c => c.GetCertHashString(HashAlgorithmName.SHA256) == BundledCertificate.Sha256);
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static async Task ImportIntoSystemKeychainAsync(byte[] der)
    {
        // Writing the System keychain needs root; granting trust needs the GUI session.
        // Only import runs here. Never call add-trusted-cert in the elevated shell.
        var command = "set -eu; umask 077; cert=$(/usr/bin/mktemp /private/tmp/personicore-ca.XXXXXXXX); "
            + "trap '/bin/rm -f \"$cert\"' EXIT; "
            + $"/usr/bin/printf '%s' '{Convert.ToBase64String(der)}' | /usr/bin/base64 -D > \"$cert\"; "
            + "/usr/bin/security add-certificates -k /Library/Keychains/System.keychain \"$cert\"";
        var script = "do shell script \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"")
            + "\" with administrator privileges";
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Certificate import did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        var detail = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Import into System keychain failed: " + detail);
    }
}
