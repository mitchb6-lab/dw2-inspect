using System.Text.Json;

namespace Dw2MpLobby;

public sealed record InstalledMod(string Id, string DisplayName, string Version, bool Enabled)
{
    public override string ToString() => $"{(Enabled ? "[on] " : "[off] ")}{DisplayName}  {Version}   ({Id})";
}

/// <summary>
/// Reads which DW2 mods are installed and which are enabled.
///
/// This is not cosmetic. State transfer ships DW2's own serialised galaxy, and mods change
/// the data the galaxy is built from — components, races, governments. Two players with
/// different mods enabled would exchange state that deserialises into something subtly or
/// catastrophically wrong, in exactly the way a version mismatch does.
///
/// So the enabled set belongs in the handshake alongside the game version. Vanilla-only is
/// the supported configuration for now; this exists so a mismatch is *detected* rather than
/// discovered as strange behaviour an hour into a session.
///
/// Layout comes from decompiling DwModSupport: mods live in &lt;game&gt;\mods\&lt;Name&gt;\mod.json,
/// Steam Workshop mods under steamapps\workshop\content\1531540, and mods\mods.json holds
/// an "order" array which IS the enabled set — EnableModInternal appends to it and
/// DisableModInternal splices out of it.
/// </summary>
public static class ModDetection
{
    public static List<InstalledMod> FindMods(string gamePath)
    {
        var enabled = ReadEnabledOrder(gamePath);
        var mods = new List<InstalledMod>();

        var modsRoot = Path.Combine(gamePath, "mods");
        if (!Directory.Exists(modsRoot)) return mods;

        foreach (var dir in Directory.EnumerateDirectories(modsRoot))
        {
            var manifest = Path.Combine(dir, "mod.json");
            if (!File.Exists(manifest)) continue;

            var id = "mods/" + Path.GetFileName(dir);
            var (name, version) = ReadManifest(manifest, Path.GetFileName(dir));

            mods.Add(new InstalledMod(id, name, version, enabled.Contains(id)));
        }

        // Enabled-first, so a mismatch is visible without scrolling.
        return mods.OrderByDescending(m => m.Enabled).ThenBy(m => m.DisplayName).ToList();
    }

    /// <summary>
    /// mods.json's "order" array is the ENABLED set, not merely a sort order — an empty
    /// one means every installed mod is off, which is easy to misread from the in-game UI.
    /// </summary>
    private static HashSet<string> ReadEnabledOrder(string gamePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(gamePath, "mods", "mods.json");

        if (!File.Exists(file)) return set;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in order.EnumerateArray())
                    if (entry.GetString() is { Length: > 0 } id) set.Add(id);
            }
        }
        catch { /* a malformed mods.json means "nothing enabled", not a crash */ }

        return set;
    }

    private static (string Name, string Version) ReadManifest(string path, string fallbackName)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string name = root.TryGetProperty("displayName", out var n) ? n.GetString() : null;
            string version = root.TryGetProperty("version", out var v) ? v.GetString() : null;

            return (string.IsNullOrWhiteSpace(name) ? fallbackName : name,
                    string.IsNullOrWhiteSpace(version) ? "?" : version);
        }
        catch { return (fallbackName, "?"); }
    }

    /// <summary>
    /// A short, order-independent fingerprint of the enabled set, for the handshake.
    /// Sorted so two players who enabled the same mods in a different order still match.
    /// </summary>
    public static string EnabledFingerprint(IEnumerable<InstalledMod> mods)
    {
        var on = mods.Where(m => m.Enabled).Select(m => $"{m.Id}@{m.Version}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        return on.Count == 0 ? "vanilla" : string.Join(";", on);
    }
}
