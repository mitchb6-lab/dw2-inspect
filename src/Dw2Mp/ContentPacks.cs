using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Which DLC ("content packs") this game has, so two players can be told they differ
/// BEFORE one of them adopts a galaxy their renderer cannot draw.
///
/// The first two-PC join crashed the joiner 18 seconds after adoption with
///
///     Missing texture: Ships/Planet Destroyers/FX/Shakturi/projectile
///
/// in ScaledRenderer.LoadBillboardTextures. That texture lives in PlanetDestroyers.bundle,
/// which belongs to the "Return of the Shakturi" content pack, and DWGame.CheckBundleAllowed
/// refuses to mount a pack's bundles unless ContentPack.Installed is true -- which
/// DWGame.Initialize sets from the store's ownership check. The host owned the pack and
/// its galaxy carried Planet Destroyer weapons; the joiner did not, so the first frame
/// that drew the host's galaxy asked for a texture that was never mounted.
///
/// The game version handshake could not see this: both were 1.3.6.3. Content is a second
/// axis of compatibility and gets its own check. Read AFTER DWGame.Initialize, because
/// Installed is false for everything until then.
/// </summary>
public static class ContentPacks
{
    /// <summary>"Name=1;Name=0;..." sorted by name; null until Initialize has run.</summary>
    public static string Mine { get; private set; }

    public static bool Known => Mine is not null;

    private static Action<string> _log;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var init = gameType is null ? null : AccessTools.Method(gameType, "Initialize");
        if (init is null) { log("# content: DWGame.Initialize not found; content packs unchecked"); return; }

        harmony.Patch(init, postfix: new HarmonyMethod(typeof(ContentPacks).GetMethod(
            nameof(AfterInitialize), BindingFlags.NonPublic | BindingFlags.Static)));
    }

    private static void AfterInitialize()
    {
        try
        {
            Mine = Describe();
            _log("# content: packs " + (Mine.Length == 0 ? "(none known)" : Mine.Replace(";", "  ")));
            NetSession.OnContentKnown();
        }
        catch (Exception ex)
        {
            _log("# content: could not read content packs " + ex.GetType().Name + ": " + (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>
    /// Reads DWGame.ContentPacks (a List of ContentPack) and renders each as Name=0|1.
    /// The pack's Name is a property, Installed is a field -- the same pattern that made
    /// Empire.Name a trap, so both are looked up by kind rather than guessed.
    /// </summary>
    private static string Describe()
    {
        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var packs = AccessTools.Field(gameType, "ContentPacks")?.GetValue(null) as System.Collections.IEnumerable;
        if (packs is null) return "";

        var parts = new List<string>();
        foreach (var pack in packs)
        {
            if (pack is null) continue;
            var t = pack.GetType();
            var name = AccessTools.Property(t, "Name")?.GetValue(pack) as string ?? "?";
            var installed = AccessTools.Field(t, "Installed")?.GetValue(pack) as bool? ?? false;
            parts.Add(name.Replace(";", ",").Replace("=", "-") + "=" + (installed ? "1" : "0"));
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    /// <summary>
    /// Packs the peer has that we lack, and vice versa. Both directions matter: a galaxy
    /// generated WITH a pack cannot be drawn without it, and one generated without it may
    /// still confuse a game that has it (races and events the host never enabled).
    /// </summary>
    public static (List<string> TheyHaveWeLack, List<string> WeHaveTheyLack) Compare(string mine, string theirs)
    {
        static Dictionary<string, bool> Parse(string s)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.LastIndexOf('=');
                if (eq <= 0) continue;
                map[part[..eq]] = part[(eq + 1)..] == "1";
            }
            return map;
        }

        var a = Parse(mine);
        var b = Parse(theirs);
        var theyHave = new List<string>();
        var weHave = new List<string>();

        foreach (var (name, installed) in b)
            if (installed && !(a.TryGetValue(name, out var ours) && ours)) theyHave.Add(name);
        foreach (var (name, installed) in a)
            if (installed && !(b.TryGetValue(name, out var theirsToo) && theirsToo)) weHave.Add(name);

        return (theyHave, weHave);
    }
}
