using System.Runtime.InteropServices;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Device;

/// <summary>
/// The identifier the backend hands out for this computer the first time it is
/// registered, kept apart from <c>state.json</c> and across several locations.
///
/// Why not just a field in the state file: the state file is the thing support tells a
/// panelist to delete when something is wrong ("forget the owner and start again"), and
/// reinstalling the agent is the other common reset. Losing the identifier that way
/// would create a second device in the panel for the same computer.
///
/// <para><b>What this does and does not give you.</b> The agent runs as the panelist,
/// without elevation, so anything it can write the same person can delete — there is no
/// such thing as an undeletable file at this privilege level. What these slots buy is
/// survival of the ordinary accidents: wiping the agent's folder, deleting state.json,
/// reinstalling. The copies are written read-only and hidden so a "cleanup" does not
/// take them by accident, and any surviving copy heals the others on the next start.
/// A file genuinely out of the panelist's reach would need an installer running as
/// administrator (or, on macOS, widening the root helper, which is deliberately limited
/// to APPLY/REPOINT/RESTORE/OWNED/STATUS).</para>
/// </summary>
/// <summary>
/// The identifier and the meter node it was issued on.
/// </summary>
/// <param name="IssuedBy">
/// The MAC of the node whose household holds the Device row this id points at, or empty when
/// it is not known — a slot written before this was recorded, which is every slot on a
/// computer registered by an earlier version.
/// </param>
public sealed record DeviceIdRecord(string DeviceId, string IssuedBy)
{
    public static DeviceIdRecord None { get; } = new(string.Empty, string.Empty);

    /// <summary>
    /// Whether this id is one to send to the node answering with <paramref name="routerMac"/>.
    /// </summary>
    /// <remarks>
    /// True whenever either side is unknown, and that direction is deliberate. An id with no
    /// recorded issuer is one from before this was written down, and the ordinary case for it is
    /// a computer sitting at home where it was registered: refusing to write there would stop
    /// every existing installation reporting. The first write that succeeds records the node, and
    /// from then on the question has a real answer.
    /// </remarks>
    public bool BelongsToNode(string routerMac)
    {
        var ours = Net.LocalNetwork.NormalizeMac(IssuedBy);
        var here = Net.LocalNetwork.NormalizeMac(routerMac);

        if (ours.Length != 17 || here.Length != 17) return true;

        return string.Equals(ours, here, StringComparison.OrdinalIgnoreCase);
    }
}

public static class DeviceIdStore
{
    /// <summary>Leading dot: hidden on macOS and Linux without an extra syscall.</summary>
    private const string FileName = ".personicore-device-id";

    /// <summary>Opaque to us: the backend owns its shape. Only the obvious junk is refused.</summary>
    private const int MaxLength = 200;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Where the identifier is kept, most durable first. Each one survives a different
    /// accident: the agent's own folder being wiped, the user profile being cleaned, a
    /// reinstall into another location. A slot that cannot be written (a machine-wide
    /// folder owned by root, for instance) is skipped without complaint.
    /// </summary>
    private static IEnumerable<string> Slots()
    {
        yield return Path.Combine(AgentPaths.UserData, FileName);
        yield return Path.Combine(AgentPaths.MachineData, FileName);
        yield return Path.Combine(Home, FileName);
    }

    /// <summary>
    /// The identifier, or empty if this computer has never been given one. Whichever
    /// copy answers first is written back to the slots that lost it.
    /// </summary>
    public static string Read() => ReadRecord().DeviceId;

    internal static string Read(IReadOnlyList<string> slots) => ReadRecord(slots).DeviceId;

    /// <summary>The identifier and the node it was issued on.</summary>
    public static DeviceIdRecord ReadRecord() => ReadRecord(Slots().ToArray());

    internal static DeviceIdRecord ReadRecord(IReadOnlyList<string> slots)
    {
        var found = DeviceIdRecord.None;
        var stale = new List<string>();

        foreach (var slot in slots)
        {
            var record = ReadSlot(slot);

            if (record.DeviceId.Length == 0)
            {
                stale.Add(slot);
            }
            else if (found.DeviceId.Length == 0)
            {
                found = record;
            }
            else if (!string.Equals(found.DeviceId, record.DeviceId, StringComparison.Ordinal))
            {
                // Two different ids means one copy is from an older registration. The
                // first slot wins by order, and the odd one out is logged rather than
                // silently overwritten: it is evidence worth having in a support case.
                Log.Warn($"Device id in {slot} does not match the one in use; it will be overwritten.");
                stale.Add(slot);
            }
            else if (found.IssuedBy.Length == 0 && record.IssuedBy.Length > 0)
            {
                // The same id, and this copy knows which node issued it while the first did
                // not. One stamped slot teaches the others, which is how an installation from
                // before the issuer existed finishes migrating without a special path.
                found = found with { IssuedBy = record.IssuedBy };
            }
            else if (!string.Equals(found.IssuedBy, record.IssuedBy, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Device id in {slot} names another node as its issuer "
                         + $"({record.IssuedBy} against {found.IssuedBy}); the first slot wins.");
                stale.Add(slot);
            }
        }

        if (found.DeviceId.Length == 0) return DeviceIdRecord.None;

        // Also the slots that hold the id without the issuer: once one copy knows, they all
        // should, or losing the stamped slot would lose the answer with it.
        stale.AddRange(slots.Where(slot => !stale.Contains(slot) && !string.Equals(
            ReadSlot(slot).IssuedBy, found.IssuedBy, StringComparison.OrdinalIgnoreCase)));

        if (stale.Count > 0) Heal(found, stale);
        return found;
    }

    /// <summary>
    /// Records the identifier the backend gave us. It is written once and never
    /// replaced: a second identifier for the same computer means the panel has two
    /// devices where there should be one, and the first one is the one already in use.
    /// </summary>
    /// <returns>true when newly stored; false when the same ID was already stored.</returns>
    /// <exception cref="IOException">No copy could be saved and read back.</exception>
    /// <exception cref="InvalidOperationException">A different ID is already stored.</exception>
    /// <param name="issuedByRouterMac">
    /// The node this id was issued on, which is what later tells a report meant for this
    /// household from one aimed at another. Empty is allowed and means "not known".
    /// </param>
    public static bool SaveIfAbsent(string? identifier, string issuedByRouterMac = "") =>
        SaveIfAbsent(identifier, issuedByRouterMac, Slots().ToArray());

    internal static bool SaveIfAbsent(string? identifier, IReadOnlyList<string> slots) =>
        SaveIfAbsent(identifier, string.Empty, slots);

    internal static bool SaveIfAbsent(string? identifier, string issuedByRouterMac, IReadOnlyList<string> slots)
    {
        var value = Normalize(identifier);
        if (value.Length == 0) throw new ArgumentException("Invalid device identifier.", nameof(identifier));

        var record = new DeviceIdRecord(value, Net.LocalNetwork.NormalizeMac(issuedByRouterMac));

        var existing = ReadRecord(slots);
        if (existing.DeviceId.Length > 0)
        {
            if (!string.Equals(existing.DeviceId, value, StringComparison.Ordinal))
                throw new InvalidOperationException("The backend returned a different device id; keeping the one already stored.");
            return false;
        }

        Heal(record, slots);
        if (!slots.Any(slot => string.Equals(ReadSlot(slot).DeviceId, value, StringComparison.Ordinal)))
            throw new IOException("The device identifier could not be saved. Registration is incomplete.");
        Log.Info(record.IssuedBy.Length > 0
            ? $"Device id stored: {value} (issued on node {record.IssuedBy})"
            : $"Device id stored: {value}");
        return true;
    }

    /// <summary>
    /// Writes down which node issued the stored id, when nothing is written down yet.
    /// </summary>
    /// <remarks>
    /// This is the migration, and it is the only way there is one: an id issued by an earlier
    /// version carries no node, and nothing on the computer can say after the fact which
    /// household it was registered in. So the first write the backend accepts is taken as the
    /// answer — at that moment the panel has just updated that row from this node, which is
    /// exactly what "issued here" means for every purpose the stamp is used for.
    ///
    /// It never replaces an issuer that is already recorded. A Device moving node is a decision
    /// for the contract (§7), not something to infer from one accepted write.
    /// </remarks>
    public static void RememberIssuer(string routerMac) => RememberIssuer(routerMac, Slots().ToArray());

    internal static void RememberIssuer(string routerMac, IReadOnlyList<string> slots)
    {
        var mac = Net.LocalNetwork.NormalizeMac(routerMac);
        if (mac.Length != 17) return;

        var existing = ReadRecord(slots);
        if (existing.DeviceId.Length == 0 || existing.IssuedBy.Length > 0) return;

        Log.Info($"Device id {existing.DeviceId} is recorded as issued on node {mac}.");
        Heal(existing with { IssuedBy = mac }, slots);
    }

    /// <summary>
    /// Lets go of the stored identifier, in every slot.
    /// </summary>
    /// <remarks>
    /// The deliberate exception to "written once and never replaced", and the only one. Call it
    /// in a single circumstance: the backend has said that no device exists under this id in
    /// this household. That is not a transient failure and not a guess — the panel looked and
    /// found nothing — so keeping the id means sending a dead reference for ever, and every
    /// update failing with it.
    ///
    /// Re-creating afterwards cannot produce the duplicate this store exists to prevent, for
    /// the same reason: there is nothing left to duplicate.
    ///
    /// Do <b>not</b> call it for a refused credential, a missing mutation, a router mismatch or
    /// anything else. Those are all answers that change without the id changing.
    /// </remarks>
    public static void Forget() => Forget(Slots().ToArray());

    internal static void Forget(IReadOnlyList<string> slots)
    {
        var cleared = 0;

        foreach (var slot in slots)
        {
            try
            {
                if (!File.Exists(slot)) continue;
                Unprotect(slot);
                File.Delete(slot);
                cleared++;
            }
            catch (Exception ex)
            {
                // A slot that cannot be cleared is a problem, not a detail: the next read would
                // heal the others from it and the dead id would come straight back.
                Log.Warn($"Could not clear the device id in {slot}: {ex.Message}");
            }
        }

        Log.Info($"Device id let go of ({cleared} slot(s) cleared); this computer registers again.");
    }

    private static void Heal(DeviceIdRecord record, IEnumerable<string> slots)
    {
        var value = record.IssuedBy.Length > 0 ? record.DeviceId + " " + record.IssuedBy : record.DeviceId;

        foreach (var slot in slots)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(slot)!);

                // The copies are read-only; that attribute has to come off before the
                // file can be replaced, and go back on afterwards.
                Unprotect(slot);
                WriteDurably(slot, value);
                Protect(slot);
            }
            catch (Exception ex)
            {
                // A slot that cannot be written is normal, not a failure: a machine-wide
                // folder owned by root is exactly the slot we would like to have and
                // cannot. As long as one slot works, the identifier survives.
                Log.Debug($"Device id slot {slot} is not writable: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Write the identifier so that losing power in the middle cannot corrupt it.
    ///
    /// A plain write truncates the file first and then fills it, which leaves a window
    /// where a power cut ends with nothing — or worse, with half an identifier, because
    /// half an identifier still looks like a valid one to <see cref="Normalize"/> and
    /// would be believed. So the value goes to a temporary file, is flushed all the way
    /// to the disk, and only then replaces the real one: a rename is atomic, so after a
    /// cut the slot holds either the whole old value or the whole new one.
    /// </summary>
    private static void WriteDurably(string path, string value)
    {
        var temporary = path + ".tmp";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(value);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// One slot, as "&lt;id&gt;" or "&lt;id&gt; &lt;issuer MAC&gt;".
    /// </summary>
    /// <remarks>
    /// The issuer is appended to the same file rather than kept in a second one, because it has
    /// to survive precisely what the id survives — a wiped profile, a deleted state file, a
    /// reinstall — and two files that can be lost separately would eventually disagree about
    /// which is right. Anything that is not "id space MAC" is read as an id on its own, which is
    /// every file written before this: the id is opaque to us, so only a trailing token that is
    /// unmistakably a MAC is taken as one.
    /// </remarks>
    private static DeviceIdRecord ReadSlot(string path)
    {
        try
        {
            if (!File.Exists(path)) return DeviceIdRecord.None;

            var raw = Normalize(File.ReadAllText(path));
            if (raw.Length == 0) return DeviceIdRecord.None;

            var cut = raw.LastIndexOf(' ');
            if (cut > 0)
            {
                var tail = Net.LocalNetwork.NormalizeMac(raw[(cut + 1)..]);
                if (tail.Length == 17) return new DeviceIdRecord(raw[..cut].Trim(), tail);
            }

            return new DeviceIdRecord(raw, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read the device id from {path}: {ex.Message}");
            return DeviceIdRecord.None;
        }
    }

    /// <summary>
    /// The shape is the backend's business, so this only refuses what could not be an
    /// identifier at all: nothing, something longer than a field, or something with
    /// line breaks and control characters in it.
    /// </summary>
    private static string Normalize(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;

        if (value.Length == 0 || value.Length > MaxLength) return string.Empty;
        return value.Any(char.IsControl) ? string.Empty : value;
    }

    private static void Protect(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);
            else
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not protect {path}: {ex.Message}");
        }
    }

    private static void Unprotect(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) File.SetAttributes(path, FileAttributes.Normal);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not clear the protection on {path}: {ex.Message}");
        }
    }
}
