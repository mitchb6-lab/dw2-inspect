using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Makes each machine drive its OWN empire in a competitive session.
///
/// The problem. Host-authoritative means both players live in one galaxy, and that galaxy
/// is generated once, by the host, with every human slot's empire flagged IsPlayer. The
/// whole state then ships to the client wholesale. But DW2 assumes exactly one player
/// empire: EmpireList.GetPlayer() walks the list by index and returns the FIRST empire
/// with IsPlayer set. Both machines would therefore drive the host's empire.
///
/// Why not just fix the flags. EmpireList.SetPlayerEmpire(e) sets IsPlayer on one empire
/// and CLEARS it on every other, so using it would mean the two machines hold different
/// galaxy bytes — and in a host-authoritative design the client's state is supposed to be
/// a faithful copy of the host's. Divergent state is the one thing the sync protocol
/// cannot tolerate, and this would bake a divergence into every single sync.
///
/// So the flags stay identical on both sides and the PERSPECTIVE is local: a postfix on
/// GetPlayer returns the empire whose name matches this machine's slot. Nothing
/// serialised changes, so host and client keep byte-identical galaxies.
///
/// Name, not index. Generation interleaves our specified empires with auto-generated AI
/// empires, Independent and the pirates in an order we do not control — in the verified
/// run the two human empires landed at indices 2 and 3, with Independent at 0.
///
/// Inactive unless a competitive session names an empire, so every solo, co-op and
/// pre-lobby test path behaves exactly as before.
/// </summary>
public static class PlayerEmpire
{
    private static SessionConfig _session;
    private static Action<string> _log;
    private static string _wanted;

    private static FieldInfo _nameField;
    private static PropertyInfo _nameProperty;

    // Resolution is logged once per outcome, not once per call: GetPlayer is called from
    // rendering and UI paths many times a second.
    private static bool _loggedHit;
    private static bool _loggedMiss;

    public static bool Active => !string.IsNullOrWhiteSpace(_wanted);

    public static void Install(Harmony harmony, SessionConfig session, Action<string> log)
    {
        _session = session;
        _log = log;

        if (session is null || !session.IsCompetitive) return;

        _wanted = session.MyEmpireName;
        if (string.IsNullOrWhiteSpace(_wanted))
        {
            log("# player: competitive session names no empire for this slot; leaving GetPlayer alone");
            return;
        }

        var listType = AccessTools.TypeByName("DistantWorlds.Types.EmpireList");
        var getPlayer = listType is null ? null : AccessTools.Method(listType, "GetPlayer", Type.EmptyTypes);

        if (getPlayer is null) { log("# player: EmpireList.GetPlayer not found"); return; }

        harmony.Patch(getPlayer, postfix: new HarmonyMethod(
            typeof(PlayerEmpire).GetMethod(nameof(SelectOwnEmpire),
                BindingFlags.NonPublic | BindingFlags.Static)));

        log($"# player: slot {session.MySlot} drives '{_wanted}' (GetPlayer patched)");
    }

    /// <summary>
    /// Postfix, not prefix: the original still runs, so if our empire is missing for any
    /// reason the game gets whatever it would have got anyway rather than a null.
    /// </summary>
    private static void SelectOwnEmpire(object __instance, ref object __result)
    {
        if (_wanted is null || __instance is null) return;

        try
        {
            // Already ours. The common case after the first call, and cheap to detect.
            if (__result is not null && NameOf(__result) == _wanted) return;

            var count = __instance.GetType().GetProperty("Count")?.GetValue(__instance) is int c ? c : 0;
            var item = __instance.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (item is null) return;

            for (int i = 0; i < count; i++)
            {
                var empire = item.Invoke(__instance, new object[] { i });
                if (empire is null || NameOf(empire) != _wanted) continue;

                if (!_loggedHit)
                {
                    _loggedHit = true;
                    _log?.Invoke($"# player: GetPlayer -> '{_wanted}' at index {i} " +
                                 $"(was '{(__result is null ? "<null>" : NameOf(__result))}')");
                }

                __result = empire;
                return;
            }

            if (!_loggedMiss)
            {
                _loggedMiss = true;
                _log?.Invoke($"# player: '{_wanted}' is not in this galaxy's {count} empire(s); " +
                             "leaving the game's own choice in place");
            }
        }
        catch
        {
            // Never throw out of a hook the renderer calls every frame.
        }
    }

    /// <summary>
    /// Empire.Name is a PROPERTY, not a field. Reading it as a field returns null and
    /// prints as an empty string, which once produced a confident wrong conclusion about
    /// generated empire names — so both are tried and the miss is visible.
    /// </summary>
    private static string NameOf(object empire)
    {
        if (empire is null) return null;

        var type = empire.GetType();
        _nameProperty ??= type.GetProperty("Name");
        if (_nameProperty is not null) return _nameProperty.GetValue(empire) as string;

        _nameField ??= AccessTools.Field(type, "Name");
        return _nameField?.GetValue(empire) as string;
    }
}
