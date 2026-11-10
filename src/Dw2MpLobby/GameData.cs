using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dw2MpShared;

namespace Dw2MpLobby;

/// <summary>A content pack as DW2 defines it (1.3.8.4, 2026-11-09): name, Steam DLC app id, the bundle files that ship with it.</summary>
public sealed record KnownContentPack(string Name, uint SteamAppId, string[] Bundles);

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
    /// <summary>Distant Worlds 2 on Steam. Its appmanifest_1531540.acf lists the DLC depots.</summary>
    public const uint Dw2SteamAppId = 1531540;

    /// <summary>
    /// The content packs DW2 knows (1.3.8.4, 2026-11-09) -- name, Steam DLC app id and the
    /// bundle files that ship with each -- transcribed from DWGame's static constructor
    /// (dw2inspect il "DistantWorlds2.DWGame::.cctor"). Steam only downloads a DLC's depot
    /// when the account owns it and has it ticked, so a pack whose bundles are all on disk
    /// is, for the launcher's purposes, installed. The mod re-checks real ownership in-game.
    /// </summary>
    private static readonly KnownContentPack[] KnownContentPacks =
    {
        new("Factions - Ikkuro and Dhayut",     2140010, new[] { "Ikkuro.bundle", "Dhayut.bundle" }),
        new("Factions - Quameno and Gizureans", 2409480, new[] { "Quameno.bundle", "Gizurean.bundle" }),
        new("Return of the Shakturi",           2926560, new[] { "Shakturi.bundle", "Creatures_Shakturi.bundle", "PlanetDestroyers.bundle" }),
        new("Factions - Atuuk and Wekkarus",    3350550, new[] { "Atuuk.bundle", "Wekkarus.bundle" }),
        new("Shadows Rising",                   3989670, new[] { "ShadowsRising.bundle" }),
    };

    /// <summary>
    /// Every known content pack as this machine sees it. "Installed" means the pack's bundle
    /// files are all on disk AND Steam has not got it unticked (Properties > DLC). The version
    /// is the Steam depot manifest id when the game is in a Steam library; otherwise null.
    ///
    /// Read-only: the app manifest is Steam's file and is only ever read here.
    /// </summary>
    public static List<ContentPackEntry> ReadContentPacks(string gamePath)
    {
        var bundlesDir = Path.Combine(gamePath, "data", "db", "bundles");
        var steamManifest = ReadSteamManifest(gamePath);
        var entries = new List<ContentPackEntry>();

        foreach (var known in KnownContentPacks)
        {
            bool bundlesOnDisk = true;
            foreach (var bundle in known.Bundles)
            {
                var bundlePath = Path.Combine(bundlesDir, bundle);
                if (!File.Exists(bundlePath))
                {
                    bundlesOnDisk = false;
                }
            }

            bool disabledInSteam = steamManifest is not null && steamManifest.DisabledDlcAppIds.Contains(known.SteamAppId);

            string depotManifest = null;
            if (steamManifest is not null)
            {
                steamManifest.DepotManifestByDlcAppId.TryGetValue(known.SteamAppId, out depotManifest);
            }

            string note;
            if (disabledInSteam)
            {
                note = bundlesOnDisk ? "unticked in Steam, bundles still on disk" : "unticked in Steam";
            }
            else if (bundlesOnDisk)
            {
                note = "bundles on disk";
            }
            else
            {
                note = "bundles not on disk";
            }

            var entry = new ContentPackEntry
            {
                Name = known.Name,
                Installed = bundlesOnDisk && !disabledInSteam,
                SteamAppId = known.SteamAppId,
                Version = string.IsNullOrEmpty(depotManifest) ? null : depotManifest,
                Note = note,
            };
            entries.Add(entry);
        }

        return entries;
    }

    public static List<string> InstalledNames(IEnumerable<ContentPackEntry> entries)
    {
        var names = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Installed)
            {
                names.Add(entry.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Steam's app manifest for the game, or null when the game is not in a Steam library,
    /// the file is missing, or it cannot be read. Null means "no Steam view", not "no DLC";
    /// the bundle check still decides what is installed.
    /// </summary>
    public static SteamAppManifest ReadSteamManifest(string gamePath)
    {
        var manifestPath = SteamAppManifest.PathForGame(gamePath, Dw2SteamAppId);
        if (manifestPath is null)
        {
            return null;
        }

        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(manifestPath);
            return SteamAppManifest.Parse(text);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// DistantWorlds2.exe's product version, e.g. "1.3.6.3", or "unknown". The same string
    /// Scripts\package-joiner.ps1 writes into VERSION.txt, so the two can be compared by eye.
    /// </summary>
    public static string ReadGameVersion(string gamePath)
    {
        var exePath = Path.Combine(gamePath, "DistantWorlds2.exe");
        if (!File.Exists(exePath))
        {
            return "unknown";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var version = info.ProductVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = info.FileVersion;
            }

            return string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Names of the installed content packs: the protocol-1 field a protocol-1 host still reads.</summary>
    public static List<string> InstalledContentPacks(string gamePath)
    {
        var entries = ReadContentPacks(gamePath);
        return InstalledNames(entries);
    }

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
