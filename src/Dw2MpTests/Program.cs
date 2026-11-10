using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for src\Shared: the content-pack comparison both the mod and the lobby use, the
/// mod's Content wire string, and the Steam app-manifest reader. No game, no sockets, no
/// framework. Each check prints one line; the process exits 1 if any failed.
/// </summary>
public static class Program
{
    private static int _passed;
    private static readonly List<string> _failures = new();

    public static int Main()
    {
        WireFormatSortsAndRoundTrips();
        WireFormatSanitisesNames();
        WireParseToleratesEmptyInput();
        WireParseReadsTheProtocol2StringExactly();

        IdenticalInventoriesAreCompatible();
        PacksTheyHaveAndWeLackAreNamedOnTheirSide();
        PacksWeHaveAndTheyLackAreNamedOnOurSide();
        DifferencesInBothDirectionsAreBothReported();
        PacksNotInstalledOnEitherSideDoNotCount();
        APackMissingFromTheirListCountsAsNotInstalled();
        DifferentVersionsOfTheSamePackRefuse();
        AnUnknownVersionOnOneSideOnlyWarns();
        NamesOnlyComparisonMatchesTheOldLobby();

        SteamManifestReadsBuildDisabledDlcAndDepots();
        SteamManifestWithNothingDisabledIsEmpty();
        SteamManifestPathIsBesideTheGameInsideALibrary();
        SteamManifestPathIsNullOutsideALibrary();
        SteamManifestTokeniserHandlesEscapedBackslashesAndComments();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failures.Count} failed");
        foreach (var failure in _failures)
        {
            Console.WriteLine("FAIL " + failure);
        }

        return _failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- wire string

    private static void WireFormatSortsAndRoundTrips()
    {
        var entries = new List<ContentPackEntry>
        {
            Pack("Return of the Shakturi", installed: true),
            Pack("Factions - Atuuk and Wekkarus", installed: false),
        };

        var text = ContentPackWire.Format(entries);
        Check("wire: sorted by name", text == "Factions - Atuuk and Wekkarus=0;Return of the Shakturi=1", text);

        var parsed = ContentPackWire.Parse(text);
        Check("wire: round trip count", parsed.Count == 2, parsed.Count.ToString());
        Check("wire: round trip flag off", parsed[0].Name == "Factions - Atuuk and Wekkarus" && !parsed[0].Installed);
        Check("wire: round trip flag on", parsed[1].Name == "Return of the Shakturi" && parsed[1].Installed);
    }

    private static void WireFormatSanitisesNames()
    {
        var sanitised = ContentPackWire.SanitiseName("A;B=C");
        Check("wire: separators removed from names", sanitised == "A,B-C", sanitised);

        var entries = new List<ContentPackEntry> { Pack("A;B=C", installed: true) };
        var parsed = ContentPackWire.Parse(ContentPackWire.Format(entries));
        Check("wire: a hostile name still parses as one entry", parsed.Count == 1 && parsed[0].Installed);
    }

    private static void WireParseToleratesEmptyInput()
    {
        Check("wire: null parses to empty", ContentPackWire.Parse(null).Count == 0);
        Check("wire: empty parses to empty", ContentPackWire.Parse("").Count == 0);
        Check("wire: junk without '=' is skipped", ContentPackWire.Parse("junk;;=1").Count == 0);
    }

    private static void WireParseReadsTheProtocol2StringExactly()
    {
        // The exact format the 2026-09-10 mod sends. It must keep parsing, or an older peer
        // and a newer one would disagree about content with no protocol bump to warn them.
        var parsed = ContentPackWire.Parse("Factions - Atuuk and Wekkarus=1;Return of the Shakturi=0");
        Check("wire: protocol-2 string parses", parsed.Count == 2);
        Check("wire: protocol-2 installed flag", parsed[0].Installed && !parsed[1].Installed);
    }

    // ------------------------------------------------------------- comparison

    private static void IdenticalInventoriesAreCompatible()
    {
        var mine = MitchWithFourPacks();
        var theirs = MitchWithFourPacks();

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: identical is compatible", difference.IsCompatible);
        Check("compare: identical describes as null", difference.Describe("Mitch (host)", "Taylor") is null);
    }

    private static void PacksTheyHaveAndWeLackAreNamedOnTheirSide()
    {
        var mine = BaseGameOnly();
        var theirs = new List<ContentPackEntry> { Pack("Return of the Shakturi", installed: true) };

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: they have, we lack", difference.TheyHaveWeLack.Count == 1 && difference.TheyHaveWeLack[0] == "Return of the Shakturi");
        Check("compare: nothing we have they lack", difference.WeHaveTheyLack.Count == 0);
        Check("compare: not compatible", !difference.IsCompatible);

        var sentence = difference.Describe("Taylor", "Mitch (host)");
        Check("compare: sentence names their side", sentence == "Mitch (host) has Return of the Shakturi; Taylor does not.", sentence);
    }

    private static void PacksWeHaveAndTheyLackAreNamedOnOurSide()
    {
        var mine = MitchWithFourPacks();
        var theirs = BaseGameOnly();

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: four packs we have they lack", difference.WeHaveTheyLack.Count == 4, difference.WeHaveTheyLack.Count.ToString());
        Check("compare: sorted by name",
            difference.WeHaveTheyLack[0] == "Factions - Atuuk and Wekkarus" &&
            difference.WeHaveTheyLack[1] == "Factions - Ikkuro and Dhayut" &&
            difference.WeHaveTheyLack[2] == "Factions - Quameno and Gizureans" &&
            difference.WeHaveTheyLack[3] == "Return of the Shakturi");

        var sentence = difference.Describe("Mitch (host)", "Taylor");
        var expected = "Mitch (host) has Factions - Atuuk and Wekkarus, Factions - Ikkuro and Dhayut, " +
                       "Factions - Quameno and Gizureans, Return of the Shakturi; Taylor does not.";
        Check("compare: the sentence mitch will read", sentence == expected, sentence);
    }

    private static void DifferencesInBothDirectionsAreBothReported()
    {
        var mine = new List<ContentPackEntry> { Pack("Shadows Rising", installed: true) };
        var theirs = new List<ContentPackEntry> { Pack("Return of the Shakturi", installed: true) };

        var difference = ContentPackCompare.Compare(mine, theirs);
        var sentence = difference.Describe("Mitch (host)", "Taylor");
        var expected = "Mitch (host) has Shadows Rising; Taylor does not. Taylor has Return of the Shakturi; Mitch (host) does not.";
        Check("compare: both directions in one sentence", sentence == expected, sentence);
    }

    private static void PacksNotInstalledOnEitherSideDoNotCount()
    {
        var mine = new List<ContentPackEntry> { Pack("Shadows Rising", installed: false) };
        var theirs = new List<ContentPackEntry> { Pack("Shadows Rising", installed: false) };

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: a pack neither has is not a difference", difference.IsCompatible);
    }

    private static void APackMissingFromTheirListCountsAsNotInstalled()
    {
        var mine = new List<ContentPackEntry> { Pack("Shadows Rising", installed: true) };
        var theirs = new List<ContentPackEntry>();

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: absent from their list = they lack it", difference.WeHaveTheyLack.Count == 1 && !difference.IsCompatible);
    }

    private static void DifferentVersionsOfTheSamePackRefuse()
    {
        var mine = new List<ContentPackEntry> { Pack("Return of the Shakturi", installed: true, version: "1480154476699251668") };
        var theirs = new List<ContentPackEntry> { Pack("Return of the Shakturi", installed: true, version: "999") };

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: sets match", difference.SetsMatch);
        Check("compare: version differs is recorded", difference.VersionDiffers.Count == 1);
        Check("compare: version differs is not compatible", !difference.IsCompatible);

        var sentence = difference.Describe("Mitch (host)", "Taylor");
        var expected = "Return of the Shakturi is a different version (Mitch (host): 1480154476699251668, Taylor: 999).";
        Check("compare: version sentence names both versions", sentence == expected, sentence);
    }

    private static void AnUnknownVersionOnOneSideOnlyWarns()
    {
        var mine = new List<ContentPackEntry> { Pack("Return of the Shakturi", installed: true, version: "1480154476699251668") };
        var theirs = new List<ContentPackEntry> { Pack("Return of the Shakturi", installed: true, version: null) };

        var difference = ContentPackCompare.Compare(mine, theirs);
        Check("compare: unknown version is compatible", difference.IsCompatible);
        Check("compare: unknown version is listed", difference.VersionUnknown.Count == 1 && difference.VersionUnknown[0] == "Return of the Shakturi");
    }

    private static void NamesOnlyComparisonMatchesTheOldLobby()
    {
        var same = ContentPackCompare.CompareNames(
            new[] { "Return of the Shakturi", "Shadows Rising" },
            new[] { "Shadows Rising", "Return of the Shakturi" });
        Check("names: same set in a different order is compatible", same.IsCompatible);

        var different = ContentPackCompare.CompareNames(new[] { "Return of the Shakturi" }, new string[0]);
        Check("names: a missing name is a difference", different.WeHaveTheyLack.Count == 1 && !different.IsCompatible);

        var nulls = ContentPackCompare.CompareNames(null, null);
        Check("names: two nulls are compatible", nulls.IsCompatible);
    }

    // ------------------------------------------------------------- Steam manifest

    /// <summary>The shape of mitch's appmanifest_1531540.acf on 2026-11-09, with two DLC unticked for the test.</summary>
    private const string SampleManifest = """
        "AppState"
        {
        	"appid"		"1531540"
        	"name"		"Distant Worlds 2"
        	"LauncherPath"		"C:\\Program Files (x86)\\Steam\\steam.exe"
        	"buildid"		"26240608"
        	"InstalledDepots"
        	{
        		"1531541"
        		{
        			"manifest"		"7281601034614674715"
        			"size"		"24487269751"
        		}
        		"2140010"
        		{
        			"manifest"		"2085203991787705401"
        			"size"		"1519148165"
        			"dlcappid"		"2140010"
        		}
        		"2926560"
        		{
        			"manifest"		"1480154476699251668"
        			"size"		"5077128369"
        			"dlcappid"		"2926560"
        		}
        	}
        	"UserConfig"
        	{
        		"language"		"english"
        		"DisabledDLC"		"2140010,2926560"
        	}
        }
        """;

    private static void SteamManifestReadsBuildDisabledDlcAndDepots()
    {
        var manifest = SteamAppManifest.Parse(SampleManifest);

        Check("acf: buildid", manifest.BuildId == "26240608", manifest.BuildId);
        Check("acf: two disabled DLC", manifest.DisabledDlcAppIds.Count == 2);
        Check("acf: disabled ids", manifest.DisabledDlcAppIds.Contains(2140010) && manifest.DisabledDlcAppIds.Contains(2926560));
        Check("acf: two DLC depots (the base depot has no dlcappid)", manifest.DepotManifestByDlcAppId.Count == 2);

        manifest.DepotManifestByDlcAppId.TryGetValue(2926560, out var shakturiManifest);
        Check("acf: depot manifest by DLC app id", shakturiManifest == "1480154476699251668", shakturiManifest);
    }

    private static void SteamManifestWithNothingDisabledIsEmpty()
    {
        var text = SampleManifest.Replace("\"2140010,2926560\"", "\"\"");
        var manifest = SteamAppManifest.Parse(text);
        Check("acf: empty DisabledDLC", manifest.DisabledDlcAppIds.Count == 0);
    }

    private static void SteamManifestPathIsBesideTheGameInsideALibrary()
    {
        var path = SteamAppManifest.PathForGame(@"C:\Program Files (x86)\Steam\steamapps\common\Distant Worlds 2", 1531540);
        var expected = @"C:\Program Files (x86)\Steam\steamapps\appmanifest_1531540.acf";
        Check("acf: path beside the game", string.Equals(path, expected, StringComparison.OrdinalIgnoreCase), path);

        var withSlash = SteamAppManifest.PathForGame(@"F:\SteamLibrary\steamapps\common\Distant Worlds 2\", 1531540);
        Check("acf: trailing separator tolerated", string.Equals(withSlash, @"F:\SteamLibrary\steamapps\appmanifest_1531540.acf", StringComparison.OrdinalIgnoreCase), withSlash);
    }

    private static void SteamManifestPathIsNullOutsideALibrary()
    {
        Check("acf: GOG-style path gives null", SteamAppManifest.PathForGame(@"D:\Games\Distant Worlds 2", 1531540) is null);
        Check("acf: empty path gives null", SteamAppManifest.PathForGame("", 1531540) is null);
    }

    private static void SteamManifestTokeniserHandlesEscapedBackslashesAndComments()
    {
        var text = """
            // a comment line
            "AppState"
            {
            	"LauncherPath"		"C:\\Steam\\steam.exe"   // trailing comment
            	"buildid"		"1"
            	"UserConfig" { "DisabledDLC" "7" }
            }
            """;

        var manifest = SteamAppManifest.Parse(text);
        Check("acf: comments skipped, buildid read", manifest.BuildId == "1", manifest.BuildId);
        Check("acf: one-line block parsed", manifest.DisabledDlcAppIds.Contains(7));
    }

    // ------------------------------------------------------------- helpers

    private static ContentPackEntry Pack(string name, bool installed, string version = null)
    {
        var entry = new ContentPackEntry
        {
            Name = name,
            Installed = installed,
            Version = version,
        };
        return entry;
    }

    /// <summary>What mitch's PC reported on 2026-11-09: four packs on, Shadows Rising off.</summary>
    private static List<ContentPackEntry> MitchWithFourPacks()
    {
        var packs = new List<ContentPackEntry>
        {
            Pack("Factions - Ikkuro and Dhayut", installed: true, version: "2085203991787705401"),
            Pack("Factions - Quameno and Gizureans", installed: true, version: "4707671932583055779"),
            Pack("Return of the Shakturi", installed: true, version: "1480154476699251668"),
            Pack("Factions - Atuuk and Wekkarus", installed: true, version: "650156249882694280"),
            Pack("Shadows Rising", installed: false),
        };
        return packs;
    }

    private static List<ContentPackEntry> BaseGameOnly()
    {
        var packs = new List<ContentPackEntry>
        {
            Pack("Factions - Ikkuro and Dhayut", installed: false),
            Pack("Factions - Quameno and Gizureans", installed: false),
            Pack("Return of the Shakturi", installed: false),
            Pack("Factions - Atuuk and Wekkarus", installed: false),
            Pack("Shadows Rising", installed: false),
        };
        return packs;
    }

    private static void Check(string name, bool condition, string detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("ok   " + name);
            return;
        }

        var line = detail is null ? name : $"{name} -- got: {detail}";
        _failures.Add(line);
        Console.WriteLine("FAIL " + line);
    }
}
