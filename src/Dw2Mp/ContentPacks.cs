using System.Reflection;
using Dw2MpShared;
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
///
/// How the game decides (DWGame.Initialize, 1.3.8.4 on 2026-11-09): for each pack in DWGame.ContentPacks it
/// calls the store delegate DWGame.CheckContentPackInstalled(name) -- Steam, GOG or Matrix
/// assign it before Initialize -- and on true sets ContentPack.Installed, logs
/// "Content Pack installed: X", and for two packs flips a Galaxy flag
/// (SetShakturiInstalled, SetPirateExpansionInstalled) that gates their bundles. On false it
/// logs "Content Pack NOT installed: X". So Installed is the game's own word, not a guess.
/// </summary>
public static class ContentPacks
{
    /// <summary>"Name=1;Name=0;..." sorted by name (ContentPackWire); null until Initialize has run.</summary>
    public static string Mine { get; private set; }

    public static bool Known => Mine is not null;

    private static Action<string> _log;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var initialize = gameType is null ? null : AccessTools.Method(gameType, "Initialize");
        if (initialize is null)
        {
            log("# content: DWGame.Initialize not found; content packs unchecked");
            return;
        }

        var postfixMethod = typeof(ContentPacks).GetMethod(nameof(AfterInitialize), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(initialize, postfix: new HarmonyMethod(postfixMethod));
    }

    private static void AfterInitialize()
    {
        try
        {
            var entries = ReadFromGame();
            Mine = ContentPackWire.Format(entries);

            var summary = Mine.Length == 0 ? "(none known)" : Mine.Replace(";", "  ");
            _log("# content: packs " + summary);

            NetSession.OnContentKnown();
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log("# content: could not read content packs " + ex.GetType().Name + ": " + cause.Message);
        }
    }

    /// <summary>
    /// Reads DWGame.ContentPacks (a ContentPackList of DistantWorlds.Types.ContentPack).
    /// The pack's Name is a property, Installed and SteamAppId are fields -- the same pattern
    /// that made Empire.Name a trap, so each is looked up by kind rather than guessed.
    /// </summary>
    private static List<ContentPackEntry> ReadFromGame()
    {
        var entries = new List<ContentPackEntry>();

        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var packsField = AccessTools.Field(gameType, "ContentPacks");
        var packs = packsField?.GetValue(null) as System.Collections.IEnumerable;
        if (packs is null)
        {
            return entries;
        }

        foreach (var pack in packs)
        {
            if (pack is null)
            {
                continue;
            }

            var packType = pack.GetType();
            var name = AccessTools.Property(packType, "Name")?.GetValue(pack) as string ?? "?";
            var installed = AccessTools.Field(packType, "Installed")?.GetValue(pack) as bool? ?? false;
            var steamAppId = AccessTools.Field(packType, "SteamAppId")?.GetValue(pack) as uint? ?? 0;

            var entry = new ContentPackEntry
            {
                Name = name,
                Installed = installed,
                SteamAppId = steamAppId,
                Note = "DWGame.Initialize",
            };
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Packs the peer has that we lack, and vice versa, from two wire strings. Both
    /// directions matter: a galaxy generated WITH a pack cannot be drawn without it, and one
    /// generated without it may still confuse a game that has it (races and events the host
    /// never enabled). The comparison itself is ContentPackCompare, shared with the lobby.
    /// </summary>
    public static ContentPackDifference Compare(string mine, string theirs)
    {
        var ours = ContentPackWire.Parse(mine);
        var peers = ContentPackWire.Parse(theirs);
        return ContentPackCompare.Compare(ours, peers);
    }
}
