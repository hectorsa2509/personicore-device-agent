using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;
using Personicore.DeviceAgent.Device;
using Personicore.DeviceAgent.Graph;

const int Writes = 1500;

// Neither agent's Words type is reachable from here, and what the assistant says must not
// depend on which one it is: these stand in for both.
var Words = new PlatformWords("this computer", "Start with the session", "system settings",
    "randomized", "no administrator password is needed", "(randomized)", "", "one prompt");

if (args.Length > 0)
{
    var state = AgentState.Load();
    var start = Path.Combine(AgentPaths.UserData, "start");
    while (!File.Exists(start)) await Task.Delay(1);
    for (var i = 0; i < Writes; i++)
    {
        if (args[0] == "owner") { state.FriendlyName = "owner-" + i; state.Save(); }
        else { state.ProxyWanted = true; state.ProxyAddress = "proxy-" + i; state.SaveProxy(); }
    }
    return;
}

var root = Path.Combine(Path.GetTempPath(), "personicore-regression-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("PERSONICORE_TEST_ROOT", root);
Directory.CreateDirectory(AgentPaths.UserData);
Directory.CreateDirectory(AgentPaths.MachineData);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
try
{
    var reminderClock = new ReminderClock();
    var reminder = new ProxyReminder(reminderClock);
    Check(!reminder.ShouldNotify(true, false), "reminder waits before first notification");
    reminderClock.Advance(TimeSpan.FromMinutes(29));
    Check(!reminder.ShouldNotify(true, false), "reminder does not fire before 30 minutes");
    reminderClock.Advance(TimeSpan.FromMinutes(1));
    Check(reminder.ShouldNotify(true, false), "disabled proxy notifies after 30 minutes");
    Check(!reminder.ShouldNotify(true, false), "reminder does not duplicate on repeated ticks");
    reminderClock.Advance(TimeSpan.FromMinutes(30));
    Check(reminder.ShouldNotify(true, false), "disabled proxy repeats every 30 minutes");
    Check(!reminder.ShouldNotify(true, true), "enabled or paused proxy cancels the reminder");
    reminderClock.Advance(TimeSpan.FromHours(2));
    Check(!reminder.ShouldNotify(true, false), "disabling again starts a fresh interval");
    reminderClock.Advance(TimeSpan.FromMinutes(30));
    Check(!reminder.ShouldNotify(false, false), "unsupported platforms never notify");
    Check(!reminder.ShouldNotify(true, false), "unavailable proxy resets reminder cadence");

    var trust = false;
    var innerProxy = new FakeProxy();
    var guardedProxy = new CertificateGuardProxy(innerProxy, () => trust);
    guardedProxy.SetWanted(true, "192.168.1.2");
    Check(!guardedProxy.Wanted && innerProxy.Activations == 0
        && guardedProxy.StatusText.Contains("Install HTTPS certificate"),
        "missing trust blocks activation and explains how to install the certificate");
    trust = true;
    guardedProxy.Sync("192.168.1.2");
    Check(!guardedProxy.Wanted && innerProxy.Activations == 0,
        "installing a certificate does not silently re-enable a blocked proxy");
    guardedProxy.SetWanted(true, "192.168.1.2");
    Check(guardedProxy.Wanted && innerProxy.Activations == 1, "trusted explicit retry activates proxy");
    trust = false;
    guardedProxy.Sync("192.168.1.3");
    Check(!guardedProxy.Wanted && !innerProxy.Applied && innerProxy.Activations == 1,
        "lost trust restores the proxy instead of re-pointing it");
    innerProxy = new FakeProxy();
    innerProxy.SetWanted(true, null);
    guardedProxy = new CertificateGuardProxy(innerProxy, () => false);
    guardedProxy.Sync("192.168.1.2");
    Check(!guardedProxy.Wanted && innerProxy.Activations == 0,
        "persisted wanted state cannot resume without trust after startup or network return");
    guardedProxy = new CertificateGuardProxy(innerProxy, () => throw new IOException("trust unavailable"));
    guardedProxy.SetWanted(true, "192.168.1.2");
    Check(!guardedProxy.Wanted && innerProxy.Activations == 0, "trust-check errors fail closed");
    guardedProxy.SetWanted(false, null);
    Check(!guardedProxy.Wanted && guardedProxy.StatusText == "off", "switching off needs no trust check");

    // Custom trust verifies the packaged CA without changing the host trust store.
    using (var ca = Personicore.DeviceAgent.Certificates.BundledCertificate.Load())
    using (var chain = new System.Security.Cryptography.X509Certificates.X509Chain())
    {
        Check(!ca.HasPrivateKey, "bundled CA contains no private key");
        chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
        Check(chain.Build(ca), "bundled CA builds a valid custom-trust chain");
    }

    // Explicit temporary slots ensure these tests never touch the real device ID.
    var idRoot = Path.Combine(root, "device-id-tests");
    Directory.CreateDirectory(idRoot);
    var blockedParent = Path.Combine(idRoot, "blocked");
    File.WriteAllText(blockedParent, "not a directory");
    string[] blockedSlots = [Path.Combine(blockedParent, "id")];
    var idFailed = false;
    try { DeviceIdStore.SaveIfAbsent("device-1", blockedSlots); }
    catch (IOException) { idFailed = true; }
    Check(idFailed, "all failed ID writes report failure instead of registration success");
    File.Delete(blockedParent);
    Check(DeviceIdStore.SaveIfAbsent("device-1", blockedSlots), "ID persistence retries after a transient failure");
    Check(DeviceIdStore.Read(blockedSlots) == "device-1", "stored ID is recovered from disk");
    Check(!DeviceIdStore.SaveIfAbsent("device-1", blockedSlots), "same ID remains stable on repeated saves");
    var conflict = false;
    try { DeviceIdStore.SaveIfAbsent("device-2", blockedSlots); }
    catch (InvalidOperationException) { conflict = true; }
    Check(conflict && DeviceIdStore.Read(blockedSlots) == "device-1", "different backend ID fails without replacing the original");
    var invalid = false;
    try { DeviceIdStore.SaveIfAbsent(" ", blockedSlots); }
    catch (ArgumentException) { invalid = true; }
    Check(invalid, "empty ID cannot be acknowledged as saved");
    var unavailable = Path.Combine(idRoot, "unavailable");
    File.WriteAllText(unavailable, "not a directory");
    string[] partialSlots = [Path.Combine(unavailable, "id"), Path.Combine(idRoot, "backup")];
    Check(DeviceIdStore.SaveIfAbsent("device-3", partialSlots), "one verified surviving copy is sufficient");
    File.Delete(unavailable);
    Check(DeviceIdStore.Read(partialSlots) == "device-3"
        && File.ReadAllText(partialSlots[0]) == "device-3", "surviving copy repairs a missing slot");

    // The node that issued the id travels with it. Without that, "the device doesn't exist!"
    // asked in a second metered home reads as "support deleted this row": the id is let go of
    // and a new Device created, one orphan per return trip. See §7 of the contract.
    var issuerRoot = Path.Combine(idRoot, "issuer");
    Directory.CreateDirectory(issuerRoot);
    string[] issued = [Path.Combine(issuerRoot, "a"), Path.Combine(issuerRoot, "b")];
    DeviceIdStore.SaveIfAbsent("device-4", "aa:bb:cc:dd:ee:01", issued);
    var withIssuer = DeviceIdStore.ReadRecord(issued);
    Check(withIssuer.DeviceId == "device-4" && withIssuer.IssuedBy == "aa:bb:cc:dd:ee:01",
        "the id records the node it was issued on");
    Check(DeviceIdStore.Read(issued) == "device-4", "the id itself reads back without the issuer");
    Check(withIssuer.BelongsToNode("AA-BB-CC-DD-EE-01") && !withIssuer.BelongsToNode("aa:bb:cc:dd:ee:02"),
        "an id belongs to the node that issued it and to no other");
    Check(new DeviceIdRecord("device-4", "").BelongsToNode("aa:bb:cc:dd:ee:02")
          && withIssuer.BelongsToNode(""),
        "an unknown issuer or an unknown node is never treated as somebody else's");

    // Installations registered before this existed carry no issuer. They have to keep working,
    // and the first write the backend accepts is the only evidence of where the row lives that
    // the computer can ever have.
    string[] legacy = [Path.Combine(issuerRoot, "legacy")];
    File.WriteAllText(legacy[0], "device-5");
    Check(DeviceIdStore.ReadRecord(legacy) is { DeviceId: "device-5", IssuedBy: "" },
        "a slot written before the issuer existed still reads as an id");
    DeviceIdStore.RememberIssuer("aa:bb:cc:dd:ee:03", legacy);
    Check(DeviceIdStore.ReadRecord(legacy).IssuedBy == "aa:bb:cc:dd:ee:03", "an absent issuer is recorded once");
    DeviceIdStore.RememberIssuer("aa:bb:cc:dd:ee:04", legacy);
    Check(DeviceIdStore.ReadRecord(legacy).IssuedBy == "aa:bb:cc:dd:ee:03",
        "a recorded issuer is never moved by a later write");

    // One stamped copy teaches the others, or losing it would lose the answer with it.
    string[] mixed = [Path.Combine(issuerRoot, "plain"), Path.Combine(issuerRoot, "stamped")];
    File.WriteAllText(mixed[0], "device-6");
    File.WriteAllText(mixed[1], "device-6 aa:bb:cc:dd:ee:05");
    Check(DeviceIdStore.ReadRecord(mixed).IssuedBy == "aa:bb:cc:dd:ee:05"
          && File.ReadAllText(mixed[0]) == "device-6 aa:bb:cc:dd:ee:05",
        "a stamped slot teaches the slots that hold the id alone");
    DeviceIdStore.Forget(mixed);
    Check(DeviceIdStore.ReadRecord(mixed) == DeviceIdRecord.None, "forgetting clears the issuer with the id");

    // Two actual processes exercise a shared read/merge/write transaction.
    new AgentState().Save();
    Process Spawn(string role)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(role);
        return Process.Start(info)!;
    }
    using (var owner = Spawn("owner"))
    using (var proxy = Spawn("proxy"))
    {
        File.WriteAllText(Path.Combine(AgentPaths.UserData, "start"), "go");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Task.WhenAll(owner.WaitForExitAsync(timeout.Token), proxy.WaitForExitAsync(timeout.Token));
        Check(owner.ExitCode == 0 && proxy.ExitCode == 0, "concurrent writers complete without write errors");
    }
    var saved = AgentState.Load();
    Check(saved.FriendlyName == "owner-" + (Writes - 1) && saved.ProxyAddress == "proxy-" + (Writes - 1),
        "both writers preserve their final updates");
    saved.ProxyAddress = "pending-change";
    saved.Save();
    Check(saved.ProxyAddress == "pending-change", "registration save does not erase pending in-memory proxy changes");
    File.Delete(AgentState.Path);
    Directory.CreateDirectory(AgentState.Path);
    var failed = false;
    try { saved.Save(); } catch (IOException) { failed = true; }
    Check(failed, "state persistence failures propagate");
    Directory.Delete(AgentState.Path);

    File.WriteAllText(AgentConfig.MachineConfigPath, """{"sendToBackend":true,"routerHost":"configured.local"}""");
    File.WriteAllText(AgentConfig.UserConfigPath, """{"pollIntervalSeconds":"oops"}""");
    var config = AgentConfig.Load(out var problems);
    Check(config.SendToBackend && config.RouterHost == "configured.local" && problems.Count > 0,
        "invalid user layer preserves valid machine config and reports the error");
    File.WriteAllText(AgentConfig.UserConfigPath, """{"LogLevel":"debug","sendToBackend":false}""");
    config = AgentConfig.Load(out problems);
    Check(!config.SendToBackend && config.LogLevel == "debug" && config.RouterHost == "configured.local",
        "valid case-insensitive overrides including explicit false still apply");

    // The credential compiled into the executable. The package the panelist runs is one
    // file, so a .env left in their own folder — by support, or by testing — must not be
    // able to point that build somewhere else; only the executable's own directory and a
    // real environment variable may.
    var leftover = Path.Combine(AgentPaths.UserData, ".env");
    File.WriteAllText(leftover, "PERSONICORE_GRAPHQL_ENDPOINT=https://leftover.example.invalid/\n");
    var packaged = EnvSettings.Load();
    Check(packaged.GraphQlEndpoint == "https://packaged.example.invalid/"
          && packaged.GraphQlToken == "Bearer packaged-token"
          && packaged.SourcePath == EnvSettings.PackagedSource,
        "the baked-in credential wins over a .env left in the user's folder");

    Environment.SetEnvironmentVariable("PERSONICORE_GRAPHQL_ENDPOINT", "https://override.example.invalid/");
    Check(EnvSettings.Load().GraphQlEndpoint == "https://override.example.invalid/",
        "a real environment variable still overrides the baked-in credential");
    Environment.SetEnvironmentVariable("PERSONICORE_GRAPHQL_ENDPOINT", null);
    File.Delete(leftover);

    // Where the setup assistant opens. It is a walkthrough before it is a repair tool: a
    // panelist whose owner was declared from the dialog the agent opens by itself has step
    // one done already, and starting on step two is how they never learn that three steps
    // exist. Only after it has been finished once does it open on what is outstanding.
    Check(SetupPlan.Opening(new AgentSnapshot()) == SetupStep.Owners,
        "a fresh install opens the assistant on step one");

    var declared = new AgentSnapshot { OwnerNames = ["Ana"] };
    Check(SetupPlan.Opening(declared) == SetupStep.Owners,
        "an owner declared before the assistant ran still opens it on step one");
    Check(SetupPlan.FirstPending(declared) == SetupStep.Certificate,
        "with step one ticked, the certificate is the one outstanding");
    Check(SetupPlan.Opening(declared with { SetupCompleted = true }) == SetupStep.Certificate,
        "re-opened after being finished once, it opens on what is outstanding");
    Check(SetupPlan.AllDone(declared with { CertificateTrusted = true, ProxyWanted = true }),
        "owner, trusted certificate and proxy switch are the three green steps");

    // A done step is not a closed one. Who uses a computer changes, and a proxy the panelist
    // turned on has to come back off from the same screen — the menu never greys that one
    // out either. The certificate is the one exception: re-installing a trusted CA spends an
    // administrator password on nothing.
    var athome = declared with
    {
        NodeFound = true, RouterId = "router", Panelists = [new Panelist { Id = "p1" }],
        CertificateTrusted = true, ProxyWanted = true,
    };
    var owners = SetupPlan.Describe(SetupStep.Owners, athome, Words);
    Check(owners.Done && owners.ActionLabel == AgentText.ChangeOwner && owners.Available,
        "panelists already declared can still be changed from step one");
    Check(SetupPlan.Describe(SetupStep.Certificate, athome, Words).ActionLabel.Length == 0,
        "a trusted certificate offers no button to install it again");

    var proxyOn = SetupPlan.Describe(SetupStep.Proxy, athome, Words);
    Check(proxyOn.ActionLabel == AgentText.SetupProxyActionOff && proxyOn.Available,
        "step three turns the proxy back off once it is on");
    Check(SetupPlan.Describe(SetupStep.Proxy, declared with { ProxyWanted = true }, Words).Available,
        "turning the proxy off stays available with no meter node");
    Check(!SetupPlan.Describe(SetupStep.Proxy, declared, Words).Available,
        "turning it on still waits for the node");
    Check(proxyOn.Title != SetupPlan.Describe(SetupStep.Proxy, declared, Words).Title,
        "the proxy heading follows the switch instead of asking to turn on what is on");

    var outbox = Path.Combine(root, "outbox.txt");
    Directory.CreateDirectory(outbox);
    failed = false;
    try { Outbox.Append("test", outbox); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
    Check(failed, "outbox write failures propagate to the reporting caller");
    Directory.Delete(outbox);
    Outbox.Append("first\n", outbox);
    Outbox.Append("second\n", outbox);
    Check(File.ReadAllText(outbox) == "first\nsecond\n", "outbox writes remain append-only");

    config = new AgentConfig { PromptOnFirstRun = false };
    using var worker = new AgentWorker(config, new EnvSettings(), new AgentState(), new Profiles(), null);
    var prompts = 0;
    worker.OwnerRequired += _ => prompts++;
    worker.PromptForOwner("machine", "aa:bb:cc:dd:ee:01");
    Check(prompts == 0, "disabled automatic owner prompt raises no UI event");
    config.PromptOnFirstRun = true;
    worker.PromptForOwner("machine", "aa:bb:cc:dd:ee:01");
    worker.PromptForOwner("machine", "aa:bb:cc:dd:ee:01");
    Check(prompts == 1, "enabled automatic owner prompt fires once");

    // Moving house. The MAC of the computer does not change when it is carried to another
    // home, so a prompt remembered by machineId alone said "already asked" in the one
    // situation where the question has to be asked again — and the dialog never opened.
    worker.PromptForOwner("machine", "aa:bb:cc:dd:ee:02");
    Check(prompts == 2, "a different meter node asks who this computer belongs to again");
    worker.PromptForOwner("machine", "AA-BB-CC-DD-EE-02");
    Check(prompts == 2, "the same node written another way is not a second household");

    // The proxy follows the household this computer is registered on, not whichever meter
    // node happens to be in front of it: another panel household's node is reachable on the
    // same name, and measuring through it files this computer's traffic under their home.
    var atHomeState = new AgentState { RouterMac = "aa:bb:cc:dd:ee:01" };
    Check(atHomeState.IsRegisteredOnNode("AA:BB:CC:DD:EE:01"), "this household's own node is not foreign");
    Check(!atHomeState.IsRegisteredOnNode("aa:bb:cc:dd:ee:02"), "another household's node is foreign");
    Check(atHomeState.IsRegisteredOnNode("nonsense"), "an unreadable MAC does not turn the proxy off at home");
    Check(new AgentState().IsRegisteredOnNode("aa:bb:cc:dd:ee:02"),
        "before the first registration there is nothing to be foreign to");

    // Every probe reads router_info.sh, so what it is compared against has to come from there
    // too. RouterMac is the backend's echo of the router record and is only the fallback for a
    // registration written before the node's own answer was kept — otherwise a backend that
    // echoed the MAC differently would turn the proxy off at home, silently.
    Check(new AgentState { RouterMac = "aa:bb:cc:dd:ee:01" }.ExpectedNodeMac == "aa:bb:cc:dd:ee:01",
        "an older registration falls back to the router record's MAC");
    var twoSources = new AgentState { RouterMac = "aa:bb:cc:dd:ee:01", NodeMac = "aa:bb:cc:dd:ee:09" };
    Check(twoSources.ExpectedNodeMac == "aa:bb:cc:dd:ee:09"
          && twoSources.IsRegisteredOnNode("aa:bb:cc:dd:ee:09")
          && !twoSources.IsRegisteredOnNode("aa:bb:cc:dd:ee:01"),
        "what the node itself reports is what the probes are held to");

    // Two pauses, two sentences. Only one of them ends by itself when the panelist gets home,
    // and the switch stays on in both: a paused proxy the panelist cannot explain is how
    // "the proxy is broken" gets reported.
    var pausedAway = new AgentSnapshot { ProxyWanted = true, ProxyStatus = AgentText.ProxyPausedNoNode };
    var pausedVisiting = new AgentSnapshot { ProxyWanted = true, ProxyStatus = AgentText.ProxyPausedOtherHousehold };
    Check(AgentText.ProxyPausedStatus(ProxyPause.NoNode) == AgentText.ProxyPausedNoNode
          && AgentText.ProxyPausedStatus(ProxyPause.OtherHousehold) == AgentText.ProxyPausedOtherHousehold,
        "each pause has its own status line");
    Check(AgentText.DescribeProxy(pausedAway, Words) != AgentText.DescribeProxy(pausedVisiting, Words)
          && AgentText.DescribeProxy(pausedVisiting, Words).Contains("another home"),
        "away from home and visiting another household do not read the same");
    Check(SetupPlan.Describe(SetupStep.Proxy, pausedVisiting, Words).Available
          && SetupPlan.Describe(SetupStep.Proxy, pausedVisiting, Words).Done,
        "a proxy paused in somebody else's household is still on, and still turnable off");

    var fake = new FakeProxy();
    fake.SetWanted(true, null, ProxyPause.OtherHousehold);
    Check(!fake.Applied && fake.Paused == AgentText.ProxyPausedOtherHousehold,
        "the reason reaches the switch's status line on the click, not a cycle later");
    Check(new AgentSnapshot { NodeFound = true, RouterId = "router", Panelists = [new Panelist { Id = "p1" }] }.CanRegister,
        "manual registration remains available");

    var device = new DeviceRegistration("192.168.1.25", MachineId.FromMac("aa:bb:cc:dd:ee:ff"),
        "Home laptop", "laptop", "Example", "Model", "macOS", ["p1", "p2"])
        { PrincipalPanelistId = "p2", OwnerCount = 2 };
    var request = PersonicoreApi.BuildAssignmentRequest("router", device, new HashSet<string>(), new EnvSettings());
    using var json = JsonDocument.Parse(request.VariablesJson());
    var entry = json.RootElement.GetProperty("updateIps")[0];
    Check(entry.GetProperty("ip").GetString() == device.Ip && entry.GetProperty("panelistIds").GetArrayLength() == 2
        && entry.GetProperty("principalPanelistId").GetString() == "p2" && entry.GetProperty("deviceShared").GetBoolean()
        && !entry.GetProperty("isPersonal").GetBoolean() && !entry.TryGetProperty("email", out _),
        "simulated assignment includes local IP and owner IDs, not panelist personal details");
}
finally { Directory.Delete(root, recursive: true); }

sealed class Profiles : IDeviceProfileProvider
{
    public DeviceProfile Read() => DeviceProfile.Unknown;
}

sealed class FakeProxy : IProxySwitch
{
    public bool Wanted { get; private set; }
    public bool Applied { get; private set; }
    public int Activations { get; private set; }
    public string StatusText => Applied ? "on · test" : "off";
    public string Paused { get; private set; } = string.Empty;
    public void SetWanted(bool wanted, string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        Wanted = wanted;
        Sync(nodeIp, pause);
    }
    public void Sync(string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        Applied = Wanted && nodeIp is not null;
        Paused = Wanted && !Applied ? AgentText.ProxyPausedStatus(pause) : string.Empty;
        if (Applied) Activations++;
    }
    public void PauseForExit() => Applied = false;
}

sealed class ReminderClock : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
}
