using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Mac.Platform;

internal interface IChromeDnsPreferences
{
    bool IsForced { get; }
    string? ReadEffective();
    string? ReadLocal();
    void WriteLocal(string? value);
}

/// <summary>
/// Only creates an absent preference. The durable marker means undo can remove our
/// value without guessing what an administrator or the user configured beforehand.
/// </summary>
internal sealed class ChromeDnsPolicy(IChromeDnsPreferences preferences, string marker)
{
    private static readonly object Gate = new();
    private const string Receipt = "personicore-chrome-dns-off-v1";

    public string Apply()
    {
        lock (Gate)
        {
            if (preferences.IsForced) return AgentText.ChromeManaged;
            if (preferences.ReadEffective() is not null || preferences.ReadLocal() is not null)
                return AgentText.ChromeExisting;

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            // Write before the preference so a process crash never loses the undo path.
            using (var stream = new FileStream(marker + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(Receipt);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(marker + ".tmp", marker, true);
            preferences.WriteLocal("off");
            if (preferences.ReadLocal() != "off") throw new IOException("Chrome policy readback failed.");
            return AgentText.ChromeApplied;
        }
    }

    public string Undo()
    {
        lock (Gate)
        {
            if (!File.Exists(marker) || File.ReadAllText(marker) != Receipt) return AgentText.ChromeNotOwned;
            if (preferences.IsForced || preferences.ReadLocal() != "off")
                return AgentText.ChromeChangedElsewhere;
            preferences.WriteLocal(null);
            if (preferences.ReadLocal() is not null) throw new IOException("Chrome policy removal failed.");
            File.Delete(marker);
            return AgentText.ChromeUndone;
        }
    }
}
