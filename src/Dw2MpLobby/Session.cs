using System.Text.Json;
using System.Text.Json.Serialization;
using Dw2MpShared;

namespace Dw2MpLobby;

/// <summary>
/// The lobby protocol number: the shape of session.json AND of the lobby's own messages
/// (Join, Session, Start, Refused), because the Join and Session payloads ARE this file's
/// types. One number, checked in both directions before a session can start.
///
/// History:
///   1  2026-09-07  the first lobby. Slots carried only names; no version check of any kind.
///   2  2026-11-09  slots carry lobbyProtocol, gameVersion and contentPackDetails (name,
///                  installed, Steam app id, depot manifest). The host refuses a join whose
///                  protocol, game build or content packs differ, with a Refused message
///                  naming the difference, and the joiner refuses a Session from an older host.
///
/// The mod reads the same number from session.json (SessionConfig.ExpectedProtocolVersion);
/// bump both together. This is separate from NetSession.ProtocolVersion, which is the mod's
/// in-game wire format (2 since 2026-09-10) and is unchanged by this.
/// </summary>
public static class LobbyProtocol
{
    public const int Version = 2;
}

/// <summary>
/// The contract between the lobby and the mod, written as session.json.
///
/// Deliberately plain data with no behaviour: the mod reads it with System.Text.Json
/// inside the game process, and the two sides must not need to share an assembly.
/// </summary>
public sealed class SessionDescriptor
{
    /// <summary>Bumped when the shape changes, so a stale file fails loudly. See LobbyProtocol.</summary>
    public int ProtocolVersion { get; set; } = LobbyProtocol.Version;

    /// <summary>"coop-shared" or "competitive".</summary>
    public string Mode { get; set; } = "coop-shared";

    public string HostAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 47800;

    public GalaxySettings Galaxy { get; set; } = new();

    public List<PlayerSlot> Players { get; set; } = new();

    /// <summary>
    /// Which slot THIS machine plays. The only field that differs between the two copies
    /// of the file, which is what lets both sides otherwise verify they agree.
    /// </summary>
    public int MySlot { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SessionDescriptor FromJson(string json) =>
        JsonSerializer.Deserialize<SessionDescriptor>(json, Options);

    /// <summary>
    /// Written next to the mod rather than into the game directory: the install is under
    /// Program Files and not reliably writable, which already bit us once with logging.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dw2Mp", "session.json");

    /// <summary>
    /// One file per role (session-host.json, session-client.json), so two lobbies on the same PC
    /// do not share a single session.json. With one shared file, the joiner's copy (slot 1) could
    /// overwrite the host's before the host's game read it, and the host would play the wrong empire.
    /// </summary>
    public static string PathForRole(string role)
    {
        var folder = Path.GetDirectoryName(DefaultPath)!;
        var fileName = $"session-{role}.json";
        return Path.Combine(folder, fileName);
    }

    public void Save(string path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }
}

public sealed class GalaxySettings
{
    public int Stars { get; set; } = 30;

    /// <summary>AI empires IN ADDITION to the human players.</summary>
    public int AiEmpires { get; set; } = 4;

    public int Seed { get; set; } = 12345;
}

public sealed class PlayerSlot
{
    public int Slot { get; set; }

    public string Name { get; set; } = "Player";

    public EmpireConfig Empire { get; set; } = new();

    /// <summary>
    /// The lobby protocol this player's launcher speaks (LobbyProtocol.Version). A slot from
    /// a protocol-1 launcher deserialises with 0 here, which is how the host tells "older
    /// launcher" apart from "same launcher, fewer packs" and refuses with the right message.
    /// </summary>
    public int LobbyProtocol { get; set; }

    /// <summary>
    /// DistantWorlds2.exe's product version on this player's machine ("1.3.6.3"), or
    /// "unknown". The mod checks the game assembly version again in-game; checking the exe
    /// here saves two four-minute loads when the builds differ.
    /// </summary>
    public string GameVersion { get; set; } = "unknown";

    /// <summary>
    /// DLC present in this player's game folder, by content-pack name. Compared by the host
    /// on join: a galaxy generated with a pack cannot be drawn by a game without it (the
    /// first two-PC join crashed on a Planet Destroyer texture from Return of the Shakturi).
    /// Presence on disk is the launcher's view; the mod checks ownership again in-game.
    ///
    /// Kept as plain names since protocol 1 so a protocol-1 host still reads this slot.
    /// The full picture is ContentPackDetails.
    /// </summary>
    public List<string> ContentPacks { get; set; } = new();

    /// <summary>
    /// Every content pack the game knows, as this machine sees it: installed or not, the
    /// Steam app id, the Steam depot manifest (the pack's version) and where the answer came
    /// from. Protocol 2. The host compares installed sets and versions and refuses the join
    /// when they differ. See Dw2MpShared.ContentPackEntry.
    /// </summary>
    public List<ContentPackEntry> ContentPackDetails { get; set; } = new();
}

/// <summary>Maps one-to-one onto DW2's GameStartSettingsEmpire.</summary>
public sealed class EmpireConfig
{
    public string Name { get; set; } = "New Empire";

    public int RaceId { get; set; }

    public int GovernmentId { get; set; }

    public int R { get; set; } = 60;
    public int G { get; set; } = 120;
    public int B { get; set; } = 220;
}
