using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Mac.Platform;

if (args.Contains("--certificate-readonly"))
{
    using var ca = Personicore.DeviceAgent.Certificates.BundledCertificate.Load();
    var alwaysTrust = NativeCertificateTrust.HasAlwaysTrust(ca.RawData);
    var sslTrust = CertificateInstaller.IsTrusted();
    var imported = await CertificateInstaller.IsImportedAsync();
    Console.WriteLine($"Certificate trust (read-only): imported={imported}, admin Always Trust={alwaysTrust}, effective SSL trust={sslTrust}");
    if (args.Contains("--expect-untrusted") && (alwaysTrust || sslTrust))
        throw new Exception("Expected the imported-only certificate to remain untrusted.");
    return;
}

if (args.Contains("--native-readonly"))
{
    var native = new ChromeDnsPreferences();
    Console.WriteLine($"Native read completed: forced={native.IsForced}, local present={native.ReadLocal() is not null}, effective present={native.ReadEffective() is not null}");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "personicore-chrome-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception(label);
    Console.WriteLine("PASS " + label);
    passed++;
}
try
{
    var prefs = new Preferences();
    var marker = Path.Combine(root, "receipt");
    var policy = new ChromeDnsPolicy(prefs, marker);
    prefs.BeforeWrite = () => Check(File.Exists(marker), "recovery receipt exists before preference write");
    Check(policy.Apply() == AgentText.ChromeApplied && prefs.Local == "off", "absent policy is configured");
    prefs.BeforeWrite = null;
    Check(policy.Apply() == AgentText.ChromeExisting && prefs.Writes == 1, "repeated application is idempotent");
    policy = new ChromeDnsPolicy(prefs, marker);
    Check(policy.Undo() == AgentText.ChromeUndone && prefs.Local is null && !File.Exists(marker), "undo works after manager restart");
    Check(policy.Undo() == AgentText.ChromeNotOwned, "repeated undo is harmless");

    foreach (var value in new[] { "off", "automatic", "secure", "(non-string policy)" })
    {
        prefs.Local = value;
        int before = prefs.Writes;
        Check(policy.Apply() == AgentText.ChromeExisting && prefs.Writes == before && !File.Exists(marker), "existing policy preserved: " + value);
    }
    prefs.Local = null;
    prefs.Effective = "secure";
    Check(policy.Apply() == AgentText.ChromeExisting && prefs.Local is null, "inherited policy preserved");
    prefs.Effective = null;
    prefs.IsForced = true;
    Check(policy.Apply() == AgentText.ChromeManaged && !File.Exists(marker), "managed policy blocks application");
    prefs.IsForced = false;
    policy.Apply();
    prefs.IsForced = true;
    Check(policy.Undo() == AgentText.ChromeChangedElsewhere && prefs.Local == "off", "undo leaves forced policy untouched");
    prefs.IsForced = false;
    prefs.Local = "secure";
    Check(policy.Undo() == AgentText.ChromeChangedElsewhere && prefs.Local == "secure", "undo preserves externally changed value");

    prefs.Local = null;
    prefs.FailWrite = true;
    try { policy.Apply(); throw new Exception("Expected write failure"); }
    catch (IOException) { Check(File.Exists(marker) && prefs.Local is null, "write failure is surfaced with recovery receipt retained"); }
    prefs.FailWrite = false;
    policy.Apply();
    prefs.FailWrite = true;
    try { policy.Undo(); throw new Exception("Expected undo failure"); }
    catch (IOException) { Check(File.Exists(marker) && prefs.Local == "off", "failed undo retains recovery data"); }
    prefs.FailWrite = false;
    policy.Undo();

    const string active = "<dictionary> {\n HTTPSProxy : 192.168.0.143\n HTTPSPort : 3129\n HTTPSEnable : 1\n}";
    Check(ChromeConnectionCheck.ActiveProxy(active) == "192.168.0.143:3129", "active HTTPS proxy parsed");
    Check(ChromeConnectionCheck.ActiveProxy(active.Replace("HTTPSEnable : 1", "HTTPSEnable : 0")) is null, "disabled proxy not tested");
    Check(ChromeConnectionCheck.ActiveProxy(active.Replace("3129", "8080")) is null, "unrelated proxy port rejected");
    Check(ChromeConnectionCheck.ActiveProxy(active.Replace("192.168.0.143", "invalid")) is null, "invalid proxy host rejected");
    Check(ChromeConnectionCheck.ActiveProxy("") is null, "failed settings read is not a successful connection");
    Console.WriteLine($"{passed} checks passed; no Chrome preferences or network settings were modified.");
}
finally { Directory.Delete(root, true); }

internal sealed class Preferences : IChromeDnsPreferences
{
    public bool IsForced { get; set; }
    public string? Local { get; set; }
    public string? Effective { get; set; }
    public int Writes { get; private set; }
    public bool FailWrite { get; set; }
    public Action? BeforeWrite { get; set; }
    public string? ReadEffective() => Effective ?? Local;
    public string? ReadLocal() => Local;
    public void WriteLocal(string? value)
    {
        BeforeWrite?.Invoke();
        if (FailWrite) throw new IOException("Simulated preference failure.");
        Writes++;
        Local = value;
    }
}
