namespace Dw2MpShared;

/// <summary>
/// One DLC ("content pack") as one machine sees it.
///
/// This file is compiled into the mod, the lobby AND the test runner by a Compile Include in
/// each project. It is plain data and pure logic on purpose: the mod is injected into DW2 and
/// may carry no assembly the game does not already have, and the lobby and the mod must not
/// need to share one. Sharing the SOURCE gives one comparison, tested once, in three places.
///
/// Where the fields come from:
///   Name        the game's own name for the pack ("Return of the Shakturi"), from DWGame's
///               static constructor; also the key both sides compare on.
///   Installed   lobby: the pack's bundle files are on disk AND Steam has not disabled it;
///               mod: DWGame.Initialize asked the store and set ContentPack.Installed.
///   SteamAppId  the DLC's Steam app id (0 when unknown), so a log reader can match it to
///               Steam's own DLC list and to appmanifest_1531540.acf.
///   Version     the Steam depot manifest id of the installed pack, or null when there is no
///               version to report (non-Steam install, or the pack is not installed). The game
///               itself has no per-pack version; its assemblies carry one version for everything.
///   Note        where the answer came from, for a person reading the lobby's log.
/// </summary>
public sealed class ContentPackEntry
{
    public string Name { get; set; } = "";

    public bool Installed { get; set; }

    public uint SteamAppId { get; set; }

    public string Version { get; set; }

    public string Note { get; set; }
}

/// <summary>A pack both sides have installed, at two different versions.</summary>
public sealed class ContentPackVersionDifference
{
    public string Name { get; set; } = "";

    public string Ours { get; set; }

    public string Theirs { get; set; }
}

/// <summary>
/// The result of comparing two machines' content packs. Each list is sorted by name so the
/// same two machines always produce the same sentence.
/// </summary>
public sealed class ContentPackDifference
{
    /// <summary>Installed on their side, not installed (or unknown) on ours.</summary>
    public List<string> TheyHaveWeLack { get; } = new();

    /// <summary>Installed on our side, not installed (or unknown) on theirs.</summary>
    public List<string> WeHaveTheyLack { get; } = new();

    /// <summary>Installed on both sides, with a version on both sides, and the versions differ.</summary>
    public List<ContentPackVersionDifference> VersionDiffers { get; } = new();

    /// <summary>
    /// Installed on both sides, but at least one side has no version to compare. Not a
    /// refusal: a GOG or Matrix install has no Steam manifest, and the game version check in
    /// the mod's handshake still covers the code. Worth a log line, nothing more.
    /// </summary>
    public List<string> VersionUnknown { get; } = new();

    public bool SetsMatch => TheyHaveWeLack.Count == 0 && WeHaveTheyLack.Count == 0;

    /// <summary>True when a session may start: same installed set, and no known version differs.</summary>
    public bool IsCompatible => SetsMatch && VersionDiffers.Count == 0;

    /// <summary>
    /// The sentence a player reads. Names every pack that differs and which side has it, e.g.
    /// "Mitch (host) has Return of the Shakturi; Taylor does not." Null when compatible.
    /// </summary>
    public string Describe(string ourLabel, string theirLabel)
    {
        if (IsCompatible)
        {
            return null;
        }

        var sentences = new List<string>();

        if (WeHaveTheyLack.Count > 0)
        {
            var packs = string.Join(", ", WeHaveTheyLack);
            sentences.Add($"{ourLabel} has {packs}; {theirLabel} does not.");
        }

        if (TheyHaveWeLack.Count > 0)
        {
            var packs = string.Join(", ", TheyHaveWeLack);
            sentences.Add($"{theirLabel} has {packs}; {ourLabel} does not.");
        }

        foreach (var versionDifference in VersionDiffers)
        {
            sentences.Add(
                $"{versionDifference.Name} is a different version ({ourLabel}: {versionDifference.Ours}, " +
                $"{theirLabel}: {versionDifference.Theirs}).");
        }

        return string.Join(" ", sentences);
    }
}

public static class ContentPackCompare
{
    /// <summary>
    /// Compares two full inventories. A pack missing from one side's list counts as not
    /// installed there: an older build that never heard of a pack cannot have it mounted.
    /// </summary>
    public static ContentPackDifference Compare(IEnumerable<ContentPackEntry> ours, IEnumerable<ContentPackEntry> theirs)
    {
        var ourInstalled = InstalledByName(ours);
        var theirInstalled = InstalledByName(theirs);
        var difference = new ContentPackDifference();

        foreach (var name in SortedNames(theirInstalled.Keys))
        {
            bool weHaveIt = ourInstalled.ContainsKey(name);
            if (!weHaveIt)
            {
                difference.TheyHaveWeLack.Add(name);
            }
        }

        foreach (var name in SortedNames(ourInstalled.Keys))
        {
            bool theyHaveIt = theirInstalled.TryGetValue(name, out var theirEntry);
            if (!theyHaveIt)
            {
                difference.WeHaveTheyLack.Add(name);
                continue;
            }

            var ourEntry = ourInstalled[name];
            bool ourVersionKnown = !string.IsNullOrEmpty(ourEntry.Version);
            bool theirVersionKnown = !string.IsNullOrEmpty(theirEntry.Version);

            if (!ourVersionKnown || !theirVersionKnown)
            {
                difference.VersionUnknown.Add(name);
                continue;
            }

            bool sameVersion = string.Equals(ourEntry.Version, theirEntry.Version, StringComparison.Ordinal);
            if (!sameVersion)
            {
                var versionDifference = new ContentPackVersionDifference
                {
                    Name = name,
                    Ours = ourEntry.Version,
                    Theirs = theirEntry.Version,
                };
                difference.VersionDiffers.Add(versionDifference);
            }
        }

        return difference;
    }

    /// <summary>
    /// Compares two lists of installed pack NAMES, with no versions. This is what an older
    /// lobby sends (its slot carries only names), so a new lobby can still tell it apart
    /// from a lobby that lacks a pack.
    /// </summary>
    public static ContentPackDifference CompareNames(IEnumerable<string> oursInstalled, IEnumerable<string> theirsInstalled)
    {
        var ours = EntriesFromNames(oursInstalled);
        var theirs = EntriesFromNames(theirsInstalled);
        return Compare(ours, theirs);
    }

    private static List<ContentPackEntry> EntriesFromNames(IEnumerable<string> names)
    {
        var entries = new List<ContentPackEntry>();
        if (names is null)
        {
            return entries;
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var entry = new ContentPackEntry
            {
                Name = name,
                Installed = true,
            };
            entries.Add(entry);
        }

        return entries;
    }

    private static Dictionary<string, ContentPackEntry> InstalledByName(IEnumerable<ContentPackEntry> entries)
    {
        var byName = new Dictionary<string, ContentPackEntry>(StringComparer.Ordinal);
        if (entries is null)
        {
            return byName;
        }

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (!entry.Installed)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            byName[entry.Name] = entry;
        }

        return byName;
    }

    private static List<string> SortedNames(IEnumerable<string> names)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }
}

/// <summary>
/// The mod's Content message: "Name=1;Name=0;..." sorted by name, UTF-8 on the wire.
///
/// Unchanged since network protocol 2, and kept that way on purpose: an older mod parses
/// this exactly, so the in-game check keeps working across builds. Versions do not travel
/// here because the game reports none; the lobby carries them in its own JSON.
/// </summary>
public static class ContentPackWire
{
    public static string Format(IEnumerable<ContentPackEntry> entries)
    {
        var parts = new List<string>();
        if (entries is null)
        {
            return "";
        }

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var safeName = SanitiseName(entry.Name);
            var installedFlag = entry.Installed ? "1" : "0";
            parts.Add(safeName + "=" + installedFlag);
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    public static List<ContentPackEntry> Parse(string text)
    {
        var entries = new List<ContentPackEntry>();
        if (string.IsNullOrEmpty(text))
        {
            return entries;
        }

        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            int separator = part.LastIndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = part.Substring(0, separator);
            var flag = part.Substring(separator + 1);
            var entry = new ContentPackEntry
            {
                Name = name,
                Installed = flag == "1",
            };
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>The two characters the format uses must not appear inside a name.</summary>
    public static string SanitiseName(string name)
    {
        if (name is null)
        {
            return "?";
        }

        var withoutSeparators = name.Replace(";", ",").Replace("=", "-");
        return withoutSeparators;
    }
}
