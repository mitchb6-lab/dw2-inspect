using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dw2MpLobby;

/// <summary>
/// The contract between the lobby and the mod, written as session.json.
///
/// Deliberately plain data with no behaviour: the mod reads it with System.Text.Json
/// inside the game process, and the two sides must not need to share an assembly.
/// </summary>
public sealed class SessionDescriptor
{
    /// <summary>Bumped when the shape changes, so a stale file fails loudly.</summary>
    public int ProtocolVersion { get; set; } = 1;

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
