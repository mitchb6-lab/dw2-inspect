using Dw2MpShared;

namespace Dw2MpLobby;

/// <summary>
/// What the host demands of a Join and what the joiner demands of a Session, as pure
/// functions over the session types, so the rules can be tested without a socket
/// (src\Dw2MpTests compiles this file in).
///
/// Two kinds of answer, kept apart on purpose:
///   * "not a player slot" (ValidateSlot / ValidateSession): the bytes did not describe a
///     player at all -- no empire, no name, absurd list sizes. Nothing to tell the sender;
///     the host drops the connection and keeps listening.
///   * "refused" (ReasonToRefuse): a real player whose launcher, game build or content packs
///     do not match. They get the sentence and can fix it and join again.
///
/// This check is COOPERATIVE. Each launcher reports its own packs, build and protocol and
/// the other side believes it. It saves two honest players an afternoon; it is not a defence
/// against a modified launcher that lies, and nothing here should be read as one.
///
/// Every string in a slot was typed or built on the other machine. SanitiseSlot caps it and
/// strips control characters before it can reach a log line, the player list or session.json.
/// </summary>
public static class JoinPolicy
{
    public const int MaxPlayerNameLength = 64;
    public const int MaxEmpireNameLength = 64;
    public const int MaxGameVersionLength = 64;
    public const int MaxModeLength = 32;
    public const int MaxHostAddressLength = 128;
    public const int MaxPackNameLength = 128;
    public const int MaxPackVersionLength = 32;
    public const int MaxPackNoteLength = 64;

    /// <summary>DW2 knows five packs; a slot claiming more than this is not describing DW2.</summary>
    public const int MaxContentPacks = 32;

    /// <summary>The lobby is two players today; eight leaves room without inviting a flood.</summary>
    public const int MaxPlayers = 8;

    /// <summary>
    /// Caps and strips every peer-supplied string in the slot, in place, and turns null lists
    /// into empty ones so later code can count them. Does not judge; ValidateSlot does.
    /// </summary>
    public static void SanitiseSlot(PlayerSlot slot)
    {
        if (slot is null)
        {
            return;
        }

        slot.Name = PeerText.Sanitise(slot.Name, MaxPlayerNameLength);
        slot.GameVersion = PeerText.Sanitise(slot.GameVersion, MaxGameVersionLength);

        if (slot.Empire is not null)
        {
            slot.Empire.Name = PeerText.Sanitise(slot.Empire.Name, MaxEmpireNameLength);
        }

        if (slot.ContentPacks is null)
        {
            slot.ContentPacks = new List<string>();
        }

        for (int i = 0; i < slot.ContentPacks.Count; i++)
        {
            slot.ContentPacks[i] = PeerText.Sanitise(slot.ContentPacks[i], MaxPackNameLength);
        }

        if (slot.ContentPackDetails is null)
        {
            slot.ContentPackDetails = new List<ContentPackEntry>();
        }

        foreach (var detail in slot.ContentPackDetails)
        {
            if (detail is null)
            {
                continue;
            }

            detail.Name = PeerText.Sanitise(detail.Name, MaxPackNameLength);
            detail.Note = PeerText.Sanitise(detail.Note, MaxPackNoteLength);

            // Null means "no version to compare" and must stay null; "" would mean the same
            // to the comparison, but keeping the distinction keeps the JSON honest.
            if (detail.Version is not null)
            {
                detail.Version = PeerText.Sanitise(detail.Version, MaxPackVersionLength);
            }
        }
    }

    /// <summary>
    /// Why the slot cannot be used at all, or null when it describes a player. Call after
    /// SanitiseSlot, which is what guarantees the lists are not null.
    /// </summary>
    public static string ValidateSlot(PlayerSlot slot)
    {
        if (slot is null)
        {
            return "no slot";
        }

        if (slot.Empire is null)
        {
            return "no empire";
        }

        if (string.IsNullOrWhiteSpace(slot.Name))
        {
            return "empty player name";
        }

        if (string.IsNullOrWhiteSpace(slot.Empire.Name))
        {
            return "empty empire name";
        }

        if (slot.ContentPacks is null || slot.ContentPackDetails is null)
        {
            return "slot was not sanitised";
        }

        if (slot.ContentPacks.Count > MaxContentPacks)
        {
            return $"too many content packs ({slot.ContentPacks.Count})";
        }

        if (slot.ContentPackDetails.Count > MaxContentPacks)
        {
            return $"too many content pack details ({slot.ContentPackDetails.Count})";
        }

        foreach (var detail in slot.ContentPackDetails)
        {
            if (detail is null)
            {
                return "a null content pack detail";
            }

            if (string.IsNullOrWhiteSpace(detail.Name))
            {
                return "a content pack detail with no name";
            }
        }

        return null;
    }

    /// <summary>
    /// The things that must agree before a session can start, checked in the order a player
    /// should fix them: launcher build, then whether the launcher said anything about its
    /// packs, then game build, then the packs themselves. Null means admit. Every sentence
    /// names both sides, because the player reading it is on one of them.
    /// </summary>
    public static string ReasonToRefuse(PlayerSlot host, PlayerSlot joiner)
    {
        var hostLabel = host is null ? "the host" : $"{host.Name} (host)";
        var joinerLabel = string.IsNullOrWhiteSpace(joiner.Name) ? "the joiner" : joiner.Name;

        if (joiner.LobbyProtocol != LobbyProtocol.Version)
        {
            var theirProtocol = joiner.LobbyProtocol == 0 ? "1 or older" : joiner.LobbyProtocol.ToString();
            return $"{joinerLabel}'s launcher speaks lobby protocol {theirProtocol}; {hostLabel} speaks {LobbyProtocol.Version}. " +
                   "Both players need the same Dw2Mp build: install the same package on both PCs.";
        }

        // A protocol-2 launcher always lists every pack it knows, installed or not. An empty
        // list is a launcher that did not do its job; comparing by names instead would
        // quietly skip the version check and call that "matching".
        bool detailsMissing = joiner.ContentPackDetails is null || joiner.ContentPackDetails.Count == 0;
        if (detailsMissing)
        {
            return $"{joinerLabel}'s launcher sent no content-pack details, so its DLC cannot be compared with {hostLabel}'s. " +
                   "Both players need the same Dw2Mp build: install the same package on both PCs.";
        }

        if (host is null)
        {
            return null;
        }

        var hostGame = host.GameVersion ?? "unknown";
        var joinerGame = joiner.GameVersion ?? "unknown";
        bool bothGameVersionsKnown = hostGame != "unknown" && joinerGame != "unknown";
        bool sameGameVersion = string.Equals(hostGame, joinerGame, StringComparison.Ordinal);
        if (bothGameVersionsKnown && !sameGameVersion)
        {
            return $"Game builds differ: {hostLabel} has Distant Worlds 2 {hostGame}, {joinerLabel} has {joinerGame}. " +
                   "Both players need the same game build.";
        }

        var difference = ContentDifference(host, joiner);
        var sentence = difference.Describe(hostLabel, joinerLabel);
        if (sentence is not null)
        {
            return "Content packs differ. " + sentence + " " +
                   "Both players need the same DLC installed and ticked in Steam (Distant Worlds 2 > Properties > DLC).";
        }

        return null;
    }

    /// <summary>
    /// Not a refusal, but worth a log line: a side whose game build could not be read, so
    /// the build check above was skipped and the in-game handshake is the only one left.
    /// </summary>
    public static string GameVersionWarning(PlayerSlot host, PlayerSlot joiner)
    {
        var unknownSides = new List<string>();

        if (host is not null && (host.GameVersion ?? "unknown") == "unknown")
        {
            unknownSides.Add($"{host.Name} (host)");
        }

        if ((joiner.GameVersion ?? "unknown") == "unknown")
        {
            unknownSides.Add(joiner.Name);
        }

        if (unknownSides.Count == 0)
        {
            return null;
        }

        return $"the game build on {string.Join(" and ", unknownSides)} could not be read, so builds were not compared here; " +
               "the in-game handshake still checks them.";
    }

    /// <summary>
    /// Compares two slots' content packs. Both protocol-2 slots carry full details (every
    /// known pack, installed or not, with its version); a protocol-1 slot carries only the
    /// names of installed packs, so the comparison falls back to names when either side has
    /// no details. Used by the host on join and by the form as a belt-and-braces check.
    /// </summary>
    public static ContentPackDifference ContentDifference(PlayerSlot ours, PlayerSlot theirs)
    {
        bool ourDetailsKnown = ours.ContentPackDetails is not null && ours.ContentPackDetails.Count > 0;
        bool theirDetailsKnown = theirs.ContentPackDetails is not null && theirs.ContentPackDetails.Count > 0;

        if (ourDetailsKnown && theirDetailsKnown)
        {
            return ContentPackCompare.Compare(ours.ContentPackDetails, theirs.ContentPackDetails);
        }

        return ContentPackCompare.CompareNames(ours.ContentPacks, theirs.ContentPacks);
    }

    /// <summary>
    /// Joiner: why the host's Session cannot be shown and written to session.json, or null
    /// when it can. Sanitises every slot in place first (same caps as a Join), so a session
    /// that passes is safe for the player list and the file.
    /// </summary>
    public static string ValidateSession(SessionDescriptor session)
    {
        if (session is null)
        {
            return "no session";
        }

        if (session.Players is null || session.Players.Count == 0)
        {
            return "no players";
        }

        if (session.Players.Count > MaxPlayers)
        {
            return $"too many players ({session.Players.Count})";
        }

        if (session.Galaxy is null)
        {
            return "no galaxy settings";
        }

        session.Mode = PeerText.Sanitise(session.Mode, MaxModeLength);
        session.HostAddress = PeerText.Sanitise(session.HostAddress, MaxHostAddressLength);

        for (int index = 0; index < session.Players.Count; index++)
        {
            var slot = session.Players[index];
            SanitiseSlot(slot);

            var problem = ValidateSlot(slot);
            if (problem is not null)
            {
                return $"player {index}: {problem}";
            }
        }

        bool mySlotNamesAPlayer = session.Players.Any(player => player.Slot == session.MySlot);
        if (!mySlotNamesAPlayer)
        {
            return $"mySlot {session.MySlot} names no player";
        }

        return null;
    }
}
