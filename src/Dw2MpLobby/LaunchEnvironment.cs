namespace Dw2MpLobby;

/// <summary>
/// Which DW2MP_* environment variables a game launched by the lobby may inherit from the
/// shell the lobby was started in.
///
/// An ALLOW-list, since the 2026-11-10 security review: the lobby used to strip the dev flags
/// it knew about, so every new dev flag was live in real sessions until somebody remembered to
/// add it (DW2MP_CLIENT_SIMULATES, DW2MP_MAX_TICKS_BETWEEN_STATES and DW2MP_DIVERGENCE_EVERY
/// were never on the list). Now anything the lobby does not set itself is removed, unless the
/// developer switch DW2MP_DEV=1 is set. Pure, tested in src\Dw2MpTests.
/// </summary>
public static class LaunchEnvironment
{
    private const string Prefix = "DW2MP_";

    /// <summary>
    /// What the lobby sets itself in LobbyForm.StartGame, plus the one tuning knob that is clamped
    /// by the mod and the one off switch for a local-only feature.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DW2MP_DETERMINISM",
        "DW2MP_PIN_BLOCKS",
        "DW2MP_KEEP_RUNNING",
        "DW2MP_EXIT_WHEN_DONE",
        "DW2MP_SNAPSHOT_CYCLES",
        "DW2MP_MAX_SNAPSHOTS",
        "DW2MP_ROLE",
        "DW2MP_MODE",
        "DW2MP_RUN_LABEL",
        "DW2MP_SESSION",
        "DW2MP_LOCAL_PORT",
        "DW2MP_KEYFRAME_MINUTES",

        // An off switch, not a dev flag (2026-11-11): "0" stops the joiner's mod releasing the
        // render targets DW2 discards at each adoption. It changes nothing either player can
        // see in the simulation and nothing on the wire; without it a player whose graphics
        // driver disliked the release could only turn it off by enabling every dev flag.
        "DW2MP_OVERLAY_RELEASE",
    };

    /// <summary>The inherited DW2MP_* names to remove before launching. Empty in developer mode.</summary>
    public static List<string> NamesToStrip(IEnumerable<string> environmentNames, bool developerMode)
    {
        var namesToStrip = new List<string>();
        if (developerMode)
        {
            return namesToStrip;
        }

        foreach (string name in environmentNames)
        {
            if (name is null || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AllowedNames.Contains(name))
            {
                continue;
            }

            namesToStrip.Add(name);
        }

        return namesToStrip;
    }
}
