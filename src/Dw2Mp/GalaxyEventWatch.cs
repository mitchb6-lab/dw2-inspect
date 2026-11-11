using System.Globalization;
using System.Reflection;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Host: notices the big events a keyframe should follow (mitch, in chat 2026-11-10: "war
/// declared or peace made, first contact, colony founded/lost, an empire eliminated").
///
/// It reads a handful of facts from the live galaxy into a GalaxyEventCensus and lets the pure
/// GalaxyEventDetector (src\Shared\GalaxyEvents.cs, tested) compare it with the previous one.
/// Diffing rather than hooking: DW2 changes a relation through fourteen ChangeEmpireRelation
/// overloads and several treaty paths, and a hook per path would miss the one nobody found.
///
/// Only the PLAYERS' empires are watched for wars, peace, first contact and colonies. A galaxy
/// has a dozen AI empires and pirate factions meeting and fighting each other all the time; a
/// keyframe for each would cost the joiner ~30 MB of native memory and a hitch every few
/// minutes for events neither player was part of. An empire being eliminated counts whoever
/// it was.
/// </summary>
public static class GalaxyEventWatch
{
    private static GalaxyEventCensus _previous;

    private static HashSet<int> _watchedEmpireIds;

    public static Action<string> Log { get; set; }

    /// <summary>A full state was just serialised: it carries everything up to now, so start again from here.</summary>
    public static void Rebase(object galaxy)
    {
        try
        {
            _previous = TakeCensus(galaxy);
        }
        catch (Exception)
        {
            _previous = null;
        }
    }

    /// <summary>What happened since the last look. Empty the first time: there is nothing to compare with.</summary>
    public static List<string> Detect(object galaxy)
    {
        GalaxyEventCensus current = TakeCensus(galaxy);
        GalaxyEventCensus before = _previous;
        _previous = current;

        if (before is null)
        {
            return new List<string>();
        }

        return GalaxyEventDetector.Detect(before, current);
    }

    private static GalaxyEventCensus TakeCensus(object galaxy)
    {
        var census = new GalaxyEventCensus();
        object empires = ReadField(galaxy, "Empires");

        ResolveWatchedEmpires(empires);

        foreach (object empire in Elements(empires))
        {
            object empireIdValue = ReadField(empire, "EmpireId");
            if (empireIdValue is null)
            {
                continue;
            }

            int empireId = Convert.ToInt32(empireIdValue, CultureInfo.InvariantCulture);

            if (ReadField(empire, "Active") is true)
            {
                census.ActiveEmpires.Add(empireId);
            }

            if (!_watchedEmpireIds.Contains(empireId))
            {
                continue;
            }

            AddRelations(census, empire, empireId);
        }

        AddColonies(census, galaxy);
        return census;
    }

    private static void AddRelations(GalaxyEventCensus census, object empire, int empireId)
    {
        object relations = ReadField(empire, "Relations");

        foreach (object relation in Elements(relations))
        {
            object otherEmpireValue = ReadField(relation, "OtherEmpireId");
            object otherColonyValue = ReadField(relation, "OtherIndependentColonyOrbId");
            if (otherEmpireValue is null)
            {
                continue;
            }

            int otherEmpireId = Convert.ToInt32(otherEmpireValue, CultureInfo.InvariantCulture);
            int otherColonyOrbId = otherColonyValue is null ? -1 : Convert.ToInt32(otherColonyValue, CultureInfo.InvariantCulture);

            // A relation with an independent colony is not a relation with an empire.
            if (otherEmpireId < 0 || otherColonyOrbId >= 0)
            {
                continue;
            }

            bool haveMet = ReadField(relation, "HaveMet") is true;
            bool atWar = ReadField(relation, "AtWar") is true;
            census.Relations[(empireId, otherEmpireId)] = new RelationFacts(haveMet, atWar);
        }
    }

    private static void AddColonies(GalaxyEventCensus census, object galaxy)
    {
        object colonies = ReadField(galaxy, "Colonies");

        foreach (object colony in Elements(colonies))
        {
            object colonyIdValue = ReadField(colony, "ColonyId");
            object ownerItem = ReadField(colony, "Empire");
            object ownerIdValue = ReadField(ownerItem, "Id");
            if (colonyIdValue is null || ownerIdValue is null)
            {
                continue;
            }

            int ownerId = Convert.ToInt32(ownerIdValue, CultureInfo.InvariantCulture);
            if (!_watchedEmpireIds.Contains(ownerId))
            {
                continue;
            }

            int colonyId = Convert.ToInt32(colonyIdValue, CultureInfo.InvariantCulture);
            census.ColonyOwners[colonyId] = ownerId;
        }
    }

    /// <summary>
    /// The players' empires, by the names in the lobby's session. Resolved once, when at least
    /// one is found: names are what the session knows, and generation decides the ids.
    /// </summary>
    private static void ResolveWatchedEmpires(object empires)
    {
        if (_watchedEmpireIds is not null && _watchedEmpireIds.Count > 0)
        {
            return;
        }

        var watched = new HashSet<int>();
        var wantedNames = new HashSet<string>(StringComparer.Ordinal);

        SessionConfig session = MakeSave.Session;
        if (session is not null)
        {
            foreach (PlayerConfig player in session.Players)
            {
                string name = player?.Empire?.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    wantedNames.Add(name);
                }
            }
        }

        foreach (object empire in Elements(empires))
        {
            string name = ReadProperty(empire, "Name") as string;
            object empireIdValue = ReadField(empire, "EmpireId");
            if (name is null || empireIdValue is null || !wantedNames.Contains(name))
            {
                continue;
            }

            watched.Add(Convert.ToInt32(empireIdValue, CultureInfo.InvariantCulture));
        }

        bool firstResolution = _watchedEmpireIds is null;
        _watchedEmpireIds = watched;

        if (firstResolution || watched.Count > 0)
        {
            string ids = string.Join(", ", watched.OrderBy(id => id));
            Log?.Invoke($"# net[host]: event watch -- {watched.Count} player empire(s) watched (ids {ids}) of {wantedNames.Count} named in the session");
        }
    }

    private static object ReadField(object owner, string name)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            FieldInfo field = AccessTools.Field(owner.GetType(), name);
            return field?.GetValue(owner);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object ReadProperty(object owner, string name)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            PropertyInfo property = AccessTools.Property(owner.GetType(), name);
            return property?.GetValue(owner);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Live members by Count and indexer, as everywhere else in the mod: a tombstone is a null, not a failure.</summary>
    private static IEnumerable<object> Elements(object list)
    {
        if (list is null)
        {
            yield break;
        }

        Type listType = list.GetType();
        PropertyInfo countProperty = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        MethodInfo indexer = listType.GetMethod("get_Item", new[] { typeof(int) });
        if (countProperty is null || indexer is null)
        {
            yield break;
        }

        int count;
        try
        {
            count = (int)countProperty.GetValue(list);
        }
        catch (Exception)
        {
            yield break;
        }

        var arguments = new object[1];
        for (int index = 0; index < count; index++)
        {
            object element;
            try
            {
                arguments[0] = index;
                element = indexer.Invoke(list, arguments);
            }
            catch (Exception)
            {
                continue;
            }

            if (element is not null)
            {
                yield return element;
            }
        }
    }
}
