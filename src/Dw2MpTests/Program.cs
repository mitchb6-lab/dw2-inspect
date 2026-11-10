using Dw2MpLobby;
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
        DeeplyNestedKeyValuesIsRejected();

        SanitiseStripsControlCharactersAndCaps();
        SanitiseMultilineKeepsSingleNewlines();

        ASlotWithoutAnEmpireIsNotAPlayerSlot();
        ASlotWithAnEmptyNameIsNotAPlayerSlot();
        ASlotWithTooManyPacksIsNotAPlayerSlot();
        AGoodSlotIsAPlayerSlot();
        SanitiseSlotCapsAndStripsEveryPeerString();

        MatchingSlotsAreAdmitted();
        AnOlderLauncherIsRefusedBeforeAnythingElse();
        AProtocol2SlotWithoutDetailsIsRefused();
        DifferentGameBuildsAreRefused();
        AnUnknownGameBuildIsAWarningNotARefusal();
        DifferentPacksAreRefusedNamingBothSides();

        ASessionWithANullEmpireIsRejected();
        ASessionWhoseMySlotNamesNoPlayerIsRejected();
        ASessionWithTooManyPlayersIsRejected();
        AGoodSessionIsAccepted();

        HashWriterLeavesBoolsOutsideAHullAlone();
        HashWriterWritesAHullsModelLoadedFlagAsFalse();
        HashWriterTreatsEachHullSeparately();
        HashWriterWritesStridesEmptyBoundingBox();
        HashWriterLeavesBoundingBoxesOutsideAHullAlone();
        ALoadedAndAnUnloadedHullHashTheSame();

        DesyncPopupNamesWhoDesynced();
        DesyncIsDetectedOnce();
        DesyncOffersBothButtonsThenOnlySaveAndQuit();
        DesyncSaveAndQuitIsAlwaysAllowed();
        DesyncSuccessClosesTheIncident();
        DesyncCounterCannotBeDrivenFromTheWire();
        DesyncTransitionsReportWhetherTheyHappened();
        DesyncSaveFileNameIsItsOwn();

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

    private static void DeeplyNestedKeyValuesIsRejected()
    {
        var shallow = NestedKeyValues(KeyValuesText.MaxDepth - 4);
        var shallowManifest = SteamAppManifest.Parse(shallow);
        Check("acf: nesting under the limit parses", shallowManifest is not null);

        var deep = NestedKeyValues(KeyValuesText.MaxDepth + 6);
        bool threw = false;
        try
        {
            SteamAppManifest.Parse(deep);
        }
        catch (FormatException)
        {
            threw = true;
        }

        Check("acf: nesting past the limit throws FormatException", threw);
    }

    private static string NestedKeyValues(int levels)
    {
        var builder = new System.Text.StringBuilder();
        for (int level = 0; level < levels; level++)
        {
            builder.Append("\"k\" { ");
        }

        builder.Append("\"leaf\" \"1\" ");
        for (int level = 0; level < levels; level++)
        {
            builder.Append("} ");
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------- peer text

    private static void SanitiseStripsControlCharactersAndCaps()
    {
        var cleaned = PeerText.Sanitise("Tay\nlor\u0001 \u007F", 64);
        Check("text: control characters removed", cleaned == "Taylor", cleaned);

        Check("text: null becomes empty", PeerText.Sanitise(null, 10) == "");
        Check("text: trimmed", PeerText.Sanitise("  padded  ", 64) == "padded");

        var capped = PeerText.Sanitise(new string('x', 100), 64);
        Check("text: capped to the limit", capped.Length == 64, capped.Length.ToString());
        Check("text: a capped string ends with the ellipsis", capped.EndsWith(PeerText.Ellipsis));

        var exact = PeerText.Sanitise(new string('x', 64), 64);
        Check("text: a string at the limit is untouched", exact.Length == 64 && !exact.EndsWith(PeerText.Ellipsis));
    }

    private static void SanitiseMultilineKeepsSingleNewlines()
    {
        var cleaned = PeerText.SanitiseMultiline("line one\r\n\r\n\r\nline two\u0007!", 600);
        Check("text: newline runs collapse, other controls go", cleaned == "line one\nline two!", cleaned.Replace("\n", "\\n"));

        var capped = PeerText.SanitiseMultiline(new string('y', 700), 600);
        Check("text: multiline capped", capped.Length == 600, capped.Length.ToString());
    }

    // ------------------------------------------------------------- slot validation

    private static void ASlotWithoutAnEmpireIsNotAPlayerSlot()
    {
        var slot = GoodSlot("Taylor");
        slot.Empire = null;

        JoinPolicy.SanitiseSlot(slot);
        var problem = JoinPolicy.ValidateSlot(slot);
        Check("slot: null empire is not a player slot", problem == "no empire", problem);
    }

    private static void ASlotWithAnEmptyNameIsNotAPlayerSlot()
    {
        var slot = GoodSlot("   ");
        JoinPolicy.SanitiseSlot(slot);
        var problem = JoinPolicy.ValidateSlot(slot);
        Check("slot: blank player name is not a player slot", problem == "empty player name", problem);

        var noEmpireName = GoodSlot("Taylor");
        noEmpireName.Empire.Name = "\u0001";
        JoinPolicy.SanitiseSlot(noEmpireName);
        var empireProblem = JoinPolicy.ValidateSlot(noEmpireName);
        Check("slot: empire name that sanitises to nothing is not a player slot", empireProblem == "empty empire name", empireProblem);
    }

    private static void ASlotWithTooManyPacksIsNotAPlayerSlot()
    {
        var slot = GoodSlot("Taylor");
        for (int i = 0; i <= JoinPolicy.MaxContentPacks; i++)
        {
            slot.ContentPacks.Add($"Pack {i}");
        }

        JoinPolicy.SanitiseSlot(slot);
        var problem = JoinPolicy.ValidateSlot(slot);
        Check("slot: more packs than the cap is not a player slot", problem is not null && problem.StartsWith("too many content packs"), problem);

        var manyDetails = GoodSlot("Taylor");
        for (int i = 0; i <= JoinPolicy.MaxContentPacks; i++)
        {
            manyDetails.ContentPackDetails.Add(Pack($"Pack {i}", installed: true));
        }

        JoinPolicy.SanitiseSlot(manyDetails);
        var detailsProblem = JoinPolicy.ValidateSlot(manyDetails);
        Check("slot: more details than the cap is not a player slot", detailsProblem is not null && detailsProblem.StartsWith("too many content pack details"), detailsProblem);

        var nullDetail = GoodSlot("Taylor");
        nullDetail.ContentPackDetails.Add(null);
        JoinPolicy.SanitiseSlot(nullDetail);
        Check("slot: a null detail is not a player slot", JoinPolicy.ValidateSlot(nullDetail) == "a null content pack detail");
    }

    private static void AGoodSlotIsAPlayerSlot()
    {
        var slot = GoodSlot("Taylor");
        JoinPolicy.SanitiseSlot(slot);
        Check("slot: a good slot passes", JoinPolicy.ValidateSlot(slot) is null);

        var unsanitised = new PlayerSlot { Name = "x", ContentPacks = null, ContentPackDetails = null };
        Check("slot: validation before sanitising is refused, not an exception", JoinPolicy.ValidateSlot(unsanitised) == "slot was not sanitised");
    }

    private static void SanitiseSlotCapsAndStripsEveryPeerString()
    {
        var slot = GoodSlot("Tay\nlor" + new string('!', 200));
        slot.Empire.Name = "Em\rpire" + new string('!', 200);
        slot.GameVersion = "1.3\n.8.4";
        slot.ContentPacks = null;
        slot.ContentPackDetails = new List<ContentPackEntry>
        {
            new ContentPackEntry { Name = "Pack\nOne" + new string('.', 300), Note = "no\nte", Version = null },
        };

        JoinPolicy.SanitiseSlot(slot);

        Check("slot: player name has no newline and is capped", !slot.Name.Contains('\n') && slot.Name.Length == JoinPolicy.MaxPlayerNameLength, slot.Name.Length.ToString());
        Check("slot: empire name has no control character and is capped", !slot.Empire.Name.Contains('\r') && slot.Empire.Name.Length == JoinPolicy.MaxEmpireNameLength);
        Check("slot: game version has no newline", slot.GameVersion == "1.3.8.4", slot.GameVersion);
        Check("slot: null pack list becomes empty", slot.ContentPacks is not null && slot.ContentPacks.Count == 0);
        Check("slot: detail name capped", slot.ContentPackDetails[0].Name.Length == JoinPolicy.MaxPackNameLength && !slot.ContentPackDetails[0].Name.Contains('\n'));
        Check("slot: detail note cleaned", slot.ContentPackDetails[0].Note == "note", slot.ContentPackDetails[0].Note);
        Check("slot: null version stays null", slot.ContentPackDetails[0].Version is null);
    }

    // ------------------------------------------------------------- refusal policy

    private static void MatchingSlotsAreAdmitted()
    {
        var host = GoodSlot("Mitch");
        var joiner = GoodSlot("Taylor");
        var reason = JoinPolicy.ReasonToRefuse(host, joiner);
        Check("policy: matching slots are admitted", reason is null, reason);
        Check("policy: matching slots raise no warning", JoinPolicy.GameVersionWarning(host, joiner) is null);
    }

    private static void AnOlderLauncherIsRefusedBeforeAnythingElse()
    {
        var host = GoodSlot("Mitch");
        var joiner = GoodSlot("Taylor");
        joiner.LobbyProtocol = 0;
        joiner.ContentPackDetails.Clear();
        joiner.GameVersion = "0.0.0.1";

        var reason = JoinPolicy.ReasonToRefuse(host, joiner);
        Check("policy: protocol-1 joiner refused first", reason is not null && reason.Contains("lobby protocol 1 or older"), reason);
        Check("policy: the sentence names both sides", reason is not null && reason.Contains("Taylor's launcher") && reason.Contains("Mitch (host)"));
    }

    private static void AProtocol2SlotWithoutDetailsIsRefused()
    {
        var host = GoodSlot("Mitch");
        var joiner = GoodSlot("Taylor");
        joiner.ContentPackDetails.Clear();

        var reason = JoinPolicy.ReasonToRefuse(host, joiner);
        Check("policy: protocol-2 slot with no details is refused", reason is not null && reason.Contains("sent no content-pack details"), reason);

        var nullDetails = GoodSlot("Taylor");
        nullDetails.ContentPackDetails = null;
        var nullReason = JoinPolicy.ReasonToRefuse(host, nullDetails);
        Check("policy: null details refused the same way", nullReason is not null && nullReason.Contains("sent no content-pack details"));
    }

    private static void DifferentGameBuildsAreRefused()
    {
        var host = GoodSlot("Mitch");
        var joiner = GoodSlot("Taylor");
        joiner.GameVersion = "1.3.6.3";

        var reason = JoinPolicy.ReasonToRefuse(host, joiner);
        Check("policy: different game builds refused", reason is not null && reason.StartsWith("Game builds differ"), reason);
        Check("policy: both builds named", reason is not null && reason.Contains("1.3.8.4") && reason.Contains("1.3.6.3"));
    }

    private static void AnUnknownGameBuildIsAWarningNotARefusal()
    {
        var host = GoodSlot("Mitch");
        var joiner = GoodSlot("Taylor");
        joiner.GameVersion = "unknown";

        var reason = JoinPolicy.ReasonToRefuse(host, joiner);
        Check("policy: unknown build is not refused", reason is null, reason);

        var warning = JoinPolicy.GameVersionWarning(host, joiner);
        Check("policy: unknown build is warned about, naming the side", warning is not null && warning.Contains("Taylor"), warning);
    }

    private static void DifferentPacksAreRefusedNamingBothSides()
    {
        var host = GoodSlot("Mitch");
        var joiner = GoodSlot("Taylor");
        joiner.ContentPackDetails = BaseGameOnly();
        joiner.ContentPacks = new List<string>();

        var reason = JoinPolicy.ReasonToRefuse(host, joiner);
        var expectedStart = "Content packs differ. Mitch (host) has Factions - Atuuk and Wekkarus, Factions - Ikkuro and Dhayut, " +
                            "Factions - Quameno and Gizureans, Return of the Shakturi; Taylor does not.";
        Check("policy: different packs refused naming both sides", reason is not null && reason.StartsWith(expectedStart), reason);
        Check("policy: the fix is in the sentence", reason is not null && reason.Contains("Properties > DLC"));
    }

    // ------------------------------------------------------------- session validation

    private static void ASessionWithANullEmpireIsRejected()
    {
        var session = GoodSession();
        session.Players[1].Empire = null;

        var problem = JoinPolicy.ValidateSession(session);
        Check("session: a null empire is rejected, naming the player", problem == "player 1: no empire", problem);
    }

    private static void ASessionWhoseMySlotNamesNoPlayerIsRejected()
    {
        var session = GoodSession();
        session.MySlot = 7;

        var problem = JoinPolicy.ValidateSession(session);
        Check("session: mySlot must name a player", problem == "mySlot 7 names no player", problem);
    }

    private static void ASessionWithTooManyPlayersIsRejected()
    {
        var session = GoodSession();
        for (int i = 0; i <= JoinPolicy.MaxPlayers; i++)
        {
            session.Players.Add(GoodSlot($"Extra {i}"));
        }

        var problem = JoinPolicy.ValidateSession(session);
        Check("session: too many players rejected", problem is not null && problem.StartsWith("too many players"), problem);

        Check("session: null session rejected", JoinPolicy.ValidateSession(null) == "no session");
        Check("session: no players rejected", JoinPolicy.ValidateSession(new SessionDescriptor()) == "no players");
    }

    private static void AGoodSessionIsAccepted()
    {
        var session = GoodSession();
        session.Players[1].Name = "Tay\nlor";

        var problem = JoinPolicy.ValidateSession(session);
        Check("session: a good session is accepted", problem is null, problem);
        Check("session: its slots were sanitised on the way", session.Players[1].Name == "Taylor", session.Players[1].Name);
    }

    // ------------------------------------------------------------- snapshot hash writer

    /// <summary>
    /// The 24 bytes an unloaded hull's BoundingBox serialises to, copied from run1-cycle1.bin
    /// at offset 0x17EC4F on 2026-11-10: Minimum = float.MaxValue x3, Maximum = float.MinValue x3.
    /// </summary>
    private static readonly byte[] EmptyBoundingBoxBytes =
    {
        0xFF, 0xFF, 0x7F, 0x7F, 0xFF, 0xFF, 0x7F, 0x7F, 0xFF, 0xFF, 0x7F, 0x7F,
        0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF,
    };

    private static void HashWriterLeavesBoolsOutsideAHullAlone()
    {
        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        writer.Write(true);
        writer.Write(false);
        writer.Flush();

        var bytes = buffer.ToArray();
        Check("hash: bools outside a hull are written as given", bytes.Length == 2 && bytes[0] == 1 && bytes[1] == 0);
        Check("hash: nothing normalised outside a hull", writer.FlagsNormalised == 0 && writer.HullsSeen == 0);
    }

    private static void HashWriterWritesAHullsModelLoadedFlagAsFalse()
    {
        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        writer.BeginShipHull();
        writer.Write(true);
        writer.Write(true);
        writer.EndShipHull();
        writer.Write(true);
        writer.Flush();

        var bytes = buffer.ToArray();
        Check("hash: the first bool a hull writes becomes false", bytes[0] == 0);
        Check("hash: later bools in the same hull are untouched", bytes[1] == 1);
        Check("hash: bools after the hull are untouched", bytes[2] == 1);
        Check("hash: one flag counted for one hull", writer.FlagsNormalised == 1 && writer.HullsSeen == 1, writer.Summary);
    }

    private static void HashWriterTreatsEachHullSeparately()
    {
        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        for (int hull = 0; hull < 3; hull++)
        {
            writer.BeginShipHull();
            writer.Write(true);
            writer.Write(true);
            writer.TryWriteEmptyBoundingBox();
            writer.EndShipHull();
        }

        writer.Flush();

        var bytes = buffer.ToArray();
        int bytesPerHull = 2 + SnapshotHashWriter.BoundingBoxBytes;
        Check("hash: three hulls, three flags, three boxes", writer.HullsSeen == 3 && writer.FlagsNormalised == 3 && writer.BoundingBoxesNormalised == 3, writer.Summary);
        Check("hash: consistent when every hull was treated alike", writer.Consistent);
        Check("hash: each hull's first bool is false and its second is kept", bytes[0] == 0 && bytes[1] == 1 && bytes[bytesPerHull] == 0 && bytes[bytesPerHull + 1] == 1);
    }

    private static void HashWriterWritesStridesEmptyBoundingBox()
    {
        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        writer.BeginShipHull();
        bool wrote = writer.TryWriteEmptyBoundingBox();
        writer.EndShipHull();
        writer.Flush();

        var bytes = buffer.ToArray();
        Check("hash: empty box written inside a hull", wrote);
        Check("hash: empty box is 24 bytes", bytes.Length == SnapshotHashWriter.BoundingBoxBytes, bytes.Length.ToString());
        Check("hash: empty box bytes match an unloaded hull's dump", bytes.SequenceEqual(EmptyBoundingBoxBytes), Convert.ToHexString(bytes));
    }

    private static void HashWriterLeavesBoundingBoxesOutsideAHullAlone()
    {
        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        bool wrote = writer.TryWriteEmptyBoundingBox();
        writer.Flush();

        Check("hash: no box written outside a hull", !wrote && buffer.Length == 0);
        Check("hash: nothing counted outside a hull", writer.BoundingBoxesNormalised == 0);
    }

    /// <summary>
    /// The property the fix exists for: a hull the renderer has loaded (flag up, computed box)
    /// and the same hull before loading (flag down, empty box) must produce identical bytes.
    /// </summary>
    private static void ALoadedAndAnUnloadedHullHashTheSame()
    {
        float[] computedBox = { -3.03f, -3.14f, -4.01f, 3.03f, 3.14f, 4.01f };
        float[] emptyBox = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };

        var loaded = WriteHullLikeTheGame(modelLoaded: true, box: computedBox);
        var unloaded = WriteHullLikeTheGame(modelLoaded: false, box: emptyBox);

        Check("hash: loaded and unloaded hull serialise identically", loaded.SequenceEqual(unloaded), $"{Convert.ToHexString(loaded)} vs {Convert.ToHexString(unloaded)}");
        Check("hash: the hull's other fields survive", loaded.Length == 2 + 1 + 4 + 1 + SnapshotHashWriter.BoundingBoxBytes && loaded[7] == 1, loaded.Length.ToString());
    }

    /// <summary>
    /// ShipHull.WriteToStream in miniature: id, the model-loaded flag, a size, a later bool (a
    /// bonus flag), then SerializationHelper.WriteBoundingBox, which the mod's prefix redirects
    /// to the writer's empty box when a hull is being written.
    /// </summary>
    private static byte[] WriteHullLikeTheGame(bool modelLoaded, float[] box)
    {
        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        writer.BeginShipHull();
        writer.Write((short)7);
        writer.Write(modelLoaded);
        writer.Write(1200);
        writer.Write(true);

        bool replaced = writer.TryWriteEmptyBoundingBox();
        if (!replaced)
        {
            foreach (var component in box)
            {
                writer.Write(component);
            }
        }

        writer.EndShipHull();
        writer.Flush();
        return buffer.ToArray();
    }

    // ------------------------------------------------------------- desync policy

    private static void DesyncPopupNamesWhoDesynced()
    {
        var joiner = new DesyncWho { PlayerName = "Taylor", Slot = 1, IsHost = false };
        var text = DesyncPolicy.Describe(joiner, "2026-11-10 06:47:03", 2000);
        Check("desync: text names the joiner, slot, time and tick",
            text == "Taylor (joiner, slot 1) is out of sync with the host's game at 2026-11-10 06:47:03 (host tick 2000).", text);

        var unnamedHost = new DesyncWho { PlayerName = "", Slot = -1, IsHost = true };
        Check("desync: an unnamed host is 'the host'", unnamedHost.Describe() == "the host (host)", unnamedHost.Describe());

        var policy = new DesyncPolicy();
        Check("desync: first options offer both buttons", policy.OptionsText().Contains("2. Attempt resync") && policy.OptionsText().Contains("attempt 1 of 2"), policy.OptionsText());
    }

    private static void DesyncIsDetectedOnce()
    {
        var policy = new DesyncPolicy();
        Check("desync: nothing to choose before detection", !policy.Choose(DesyncChoice.AttemptResync));
        Check("desync: first detection is accepted", policy.Detect() && policy.Phase == DesyncPhase.AwaitingChoice);
        Check("desync: a second report while handling is refused", !policy.Detect());
    }

    private static void DesyncOffersBothButtonsThenOnlySaveAndQuit()
    {
        var policy = new DesyncPolicy();
        policy.Detect();

        Check("desync: attempt 1 accepted", policy.Choose(DesyncChoice.AttemptResync) && policy.Phase == DesyncPhase.Resyncing);
        policy.ResyncFinished(succeeded: false);
        Check("desync: one failure, back to the choice, resync still offered", policy.Phase == DesyncPhase.AwaitingChoice && policy.FailedResyncs == 1 && policy.OffersResync);

        Check("desync: attempt 2 accepted", policy.Choose(DesyncChoice.AttemptResync));
        policy.ResyncFinished(succeeded: false);
        Check("desync: two failures, resync no longer offered", policy.FailedResyncs == 2 && !policy.OffersResync);
        Check("desync: options text says only save and quit", policy.OptionsText().Contains("only option left is 1. Save and quit"), policy.OptionsText());
        Check("desync: a third resync press is refused", !policy.Choose(DesyncChoice.AttemptResync) && policy.Phase == DesyncPhase.AwaitingChoice);
    }

    private static void DesyncSaveAndQuitIsAlwaysAllowed()
    {
        var policy = new DesyncPolicy();
        policy.Detect();
        policy.Choose(DesyncChoice.AttemptResync);
        policy.ResyncFinished(succeeded: false);
        policy.Choose(DesyncChoice.AttemptResync);
        policy.ResyncFinished(succeeded: false);

        Check("desync: save and quit accepted after two failures", policy.Choose(DesyncChoice.SaveAndQuit) && policy.Phase == DesyncPhase.Quitting);
        Check("desync: nothing else accepted while quitting", !policy.Choose(DesyncChoice.AttemptResync));
        Check("desync: 'None' is never a valid press", !new DesyncPolicy().Choose(DesyncChoice.None));
    }

    private static void DesyncSuccessClosesTheIncident()
    {
        var policy = new DesyncPolicy();
        policy.Detect();
        policy.Choose(DesyncChoice.AttemptResync);
        policy.ResyncFinished(succeeded: false);
        policy.Choose(DesyncChoice.AttemptResync);
        policy.ResyncFinished(succeeded: true);

        Check("desync: success returns to idle and clears the counter", policy.Phase == DesyncPhase.Idle && policy.FailedResyncs == 0);
        Check("desync: a later desync starts fresh", policy.Detect() && policy.OffersResync && policy.NextAttemptNumber == 1);

        var idle = new DesyncPolicy();
        idle.ResyncFinished(succeeded: false);
        Check("desync: an outcome with no resync in flight changes nothing", idle.Phase == DesyncPhase.Idle && idle.FailedResyncs == 0);
    }

    /// <summary>
    /// Security review 2026-11-10: a peer must not be able to take the resync button away. The
    /// only way the failure counter moves is a failed outcome while a resync is in flight, so a
    /// flood of outcomes in any other phase, or any number the peer claims, changes nothing.
    /// </summary>
    private static void DesyncCounterCannotBeDrivenFromTheWire()
    {
        var awaiting = new DesyncPolicy();
        awaiting.Detect();
        for (int hostile = 0; hostile < 1000; hostile++)
        {
            awaiting.ResyncFinished(succeeded: false);
        }

        Check("desync: 1000 failed outcomes while awaiting a choice leave the counter at 0", awaiting.FailedResyncs == 0 && awaiting.OffersResync);
        Check("desync: resync is still offered after the flood", awaiting.Choose(DesyncChoice.AttemptResync));

        var idle = new DesyncPolicy();
        for (int hostile = 0; hostile < 1000; hostile++)
        {
            idle.ResyncFinished(succeeded: false);
        }

        Check("desync: failed outcomes with no incident leave the policy idle", idle.Phase == DesyncPhase.Idle && idle.FailedResyncs == 0);

        var quitting = new DesyncPolicy();
        quitting.Detect();
        quitting.Choose(DesyncChoice.SaveAndQuit);
        quitting.ResyncFinished(succeeded: false);
        Check("desync: an outcome while quitting changes nothing", quitting.Phase == DesyncPhase.Quitting && quitting.FailedResyncs == 0);

        var inFlight = new DesyncPolicy();
        inFlight.Detect();
        inFlight.Choose(DesyncChoice.AttemptResync);
        inFlight.ResyncFinished(succeeded: false);
        inFlight.ResyncFinished(succeeded: false);
        inFlight.ResyncFinished(succeeded: false);
        Check("desync: repeated outcomes for ONE in-flight resync count once", inFlight.FailedResyncs == 1 && inFlight.OffersResync);
    }

    private static void DesyncTransitionsReportWhetherTheyHappened()
    {
        var policy = new DesyncPolicy();
        Check("desync: an outcome with no resync in flight reports no transition", !policy.ResyncFinished(succeeded: true));

        policy.Detect();
        policy.Choose(DesyncChoice.AttemptResync);
        Check("desync: a failed outcome for a live resync reports a transition", policy.ResyncFinished(succeeded: false) && policy.Phase == DesyncPhase.AwaitingChoice);
        Check("desync: a second outcome for the same resync reports none", !policy.ResyncFinished(succeeded: false));

        policy.Choose(DesyncChoice.AttemptResync);
        Check("desync: a successful outcome for a live resync reports a transition", policy.ResyncFinished(succeeded: true) && policy.Phase == DesyncPhase.Idle);
    }

    private static void DesyncSaveFileNameIsItsOwn()
    {
        var name = DesyncPolicy.SaveFileName(new DateTime(2026, 11, 10, 6, 47, 3));
        Check("desync: save name carries the date and time", name == "Desync-20261110-064703.DWGame", name);
        Check("desync: save name never looks like an autosave or the pinned save",
            !name.StartsWith("Autosave", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("MPBase", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------- helpers

    /// <summary>A protocol-2 slot as mitch's launcher would send it on 2026-11-09, under the given name.</summary>
    private static PlayerSlot GoodSlot(string name)
    {
        var details = MitchWithFourPacks();
        var slot = new PlayerSlot
        {
            Slot = 0,
            Name = name,
            LobbyProtocol = LobbyProtocol.Version,
            GameVersion = "1.3.8.4",
            Empire = new EmpireConfig { Name = "Terran Union" },
            ContentPacks = details.Where(pack => pack.Installed).Select(pack => pack.Name).ToList(),
            ContentPackDetails = details,
        };
        return slot;
    }

    private static SessionDescriptor GoodSession()
    {
        var host = GoodSlot("Mitch");
        host.Slot = 0;

        var joiner = GoodSlot("Taylor");
        joiner.Slot = 1;

        var session = new SessionDescriptor
        {
            MySlot = 1,
        };
        session.Players.Add(host);
        session.Players.Add(joiner);
        return session;
    }

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
