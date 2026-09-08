using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dw2MpLobby;

public sealed record RaceOption(int Id, string Name, Color DefaultColor)
{
    public override string ToString() => Name;
}

public sealed record GovernmentOption(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reads the empire options straight out of the game's own data files.
///
/// This is why the lobby can be a standalone application: everything a player needs to
/// choose — 23 races with their default colours and flags, 11 governments — is plain XML
/// in DW2's data folder. Nothing here requires the game to be running, or a save to exist.
/// </summary>
public static class GameData
{
    public static string FindGamePath(string explicitPath = null)
    {
        foreach (var candidate in Candidates(explicitPath))
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                File.Exists(Path.Combine(candidate, "DistantWorlds2.exe")))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string explicitPath)
    {
        yield return explicitPath;
        yield return Environment.GetEnvironmentVariable("DW2_PATH");

        foreach (var steam in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            yield return Path.Combine(steam, "steamapps", "common", "Distant Worlds 2");

            // Secondary Steam libraries are listed in libraryfolders.vdf; guessing drive
            // letters instead would fail for anyone whose games are not on C:.
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            string text;
            try { text = File.ReadAllText(vdf); }
            catch (IOException) { continue; }

            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
            {
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                yield return Path.Combine(lib, "steamapps", "common", "Distant Worlds 2");
            }
        }
    }

    /// <summary>
    /// The 23 playable-ish races. Only TOP-LEVEL &lt;Race&gt; elements count — RaceId also
    /// appears inside per-race relation tables, so a naive scan finds 418 of them.
    /// </summary>
    public static List<RaceOption> LoadRaces(string gamePath)
    {
        var file = Path.Combine(gamePath, "data", "Races.xml");
        var races = new List<RaceOption>();

        foreach (var race in XDocument.Load(file).Root?.Elements("Race") ?? Enumerable.Empty<XElement>())
        {
            if (!int.TryParse(race.Element("RaceId")?.Value, out var id)) continue;

            var name = race.Element("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = race.Element("BundleName")?.Value ?? $"Race {id}";

            races.Add(new RaceOption(id, name.Trim(), ReadColor(race.Element("MainColor"))));
        }

        return races.OrderBy(r => r.Id).ToList();
    }

    public static List<GovernmentOption> LoadGovernments(string gamePath)
    {
        var file = Path.Combine(gamePath, "data", "Governments.xml");
        var governments = new List<GovernmentOption>();

        foreach (var gov in XDocument.Load(file).Root?.Elements("Government") ?? Enumerable.Empty<XElement>())
        {
            if (!int.TryParse(gov.Element("GovernmentId")?.Value, out var id)) continue;

            var name = gov.Element("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = $"Government {id}";

            governments.Add(new GovernmentOption(id, name.Trim()));
        }

        return governments.OrderBy(g => g.Id).ToList();
    }

    /// <summary>
    /// Race colours are 0-255 ints in the XML, but a couple of files use 0-1 floats.
    /// Handle both rather than silently producing black empires.
    /// </summary>
    private static Color ReadColor(XElement element)
    {
        if (element is null) return Color.SteelBlue;

        int Channel(string name)
        {
            var raw = element.Element(name)?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return 128;

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return 128;

            return (int)Math.Clamp(v <= 1.0 && v > 0 ? v * 255 : v, 0, 255);
        }

        return Color.FromArgb(Channel("R"), Channel("G"), Channel("B"));
    }
}
