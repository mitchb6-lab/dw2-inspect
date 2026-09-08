using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dw2Mp;

/// <summary>
/// The lobby's session.json, read inside the game process.
///
/// Deliberately a separate set of plain classes rather than a shared assembly with the
/// launcher: the mod is injected into DW2 and must carry no dependency the game does not
/// already have. JSON is the contract; these mirror it.
/// </summary>
public sealed class SessionConfig
{
    public int ProtocolVersion { get; set; }
    public string Mode { get; set; } = "coop-shared";
    public string HostAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public GalaxyConfig Galaxy { get; set; } = new();
    public List<PlayerConfig> Players { get; set; } = new();
    public int MySlot { get; set; }

    public bool IsCompetitive => string.Equals(Mode, "competitive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which generated empire this machine plays.
    ///
    /// In co-op both players drive the SAME empire, so every machine points at slot 0 —
    /// that is the whole difference between the two modes, and it is one line rather than
    /// two code paths.
    /// </summary>
    public int PlayableEmpireIndex => IsCompetitive ? MySlot : 0;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Loads the session, or returns null. Never throws: a missing or malformed file means
    /// "no session, behave as before", which keeps every single-player and test path that
    /// predates the lobby working untouched.
    /// </summary>
    public static SessionConfig Load(string path, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (!File.Exists(path))
        {
            log($"# session: no file at {path}");
            return null;
        }

        try
        {
            var config = JsonSerializer.Deserialize<SessionConfig>(File.ReadAllText(path), Options);
            if (config is null) { log("# session: file parsed to null"); return null; }

            log($"# session: mode={config.Mode} players={config.Players.Count} mySlot={config.MySlot} " +
                $"stars={config.Galaxy.Stars} ai={config.Galaxy.AiEmpires} seed={config.Galaxy.Seed}");

            foreach (var p in config.Players)
                log($"#   slot {p.Slot}: {p.Name} — {p.Empire.Name} (race {p.Empire.RaceId}, gov {p.Empire.GovernmentId})");

            return config;
        }
        catch (Exception ex)
        {
            log($"# session: could not read {path}: {ex.Message}");
            return null;
        }
    }
}

public sealed class GalaxyConfig
{
    public int Stars { get; set; } = 30;
    public int AiEmpires { get; set; } = 4;
    public int Seed { get; set; } = 12345;
}

public sealed class PlayerConfig
{
    public int Slot { get; set; }
    public string Name { get; set; } = "Player";
    public EmpireSpec Empire { get; set; } = new();
}

public sealed class EmpireSpec
{
    public string Name { get; set; } = "New Empire";
    public int RaceId { get; set; }
    public int GovernmentId { get; set; }
    public int R { get; set; } = 60;
    public int G { get; set; } = 120;
    public int B { get; set; } = 220;
}
