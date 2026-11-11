using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dw2MpShared;

/// <summary>What one empire's relation with another says that matters for a keyframe.</summary>
public readonly struct RelationFacts
{
    public RelationFacts(bool haveMet, bool atWar)
    {
        HaveMet = haveMet;
        AtWar = atWar;
    }

    public bool HaveMet { get; }

    public bool AtWar { get; }
}

/// <summary>
/// The few facts about a galaxy whose change is a "big event" (mitch, in chat 2026-11-10: "war
/// declared or peace made, first contact, colony founded/lost, an empire eliminated"). The mod
/// fills one of these from the live galaxy every few seconds; GalaxyEventDetector compares two.
/// Plain data, no game types.
/// </summary>
public sealed class GalaxyEventCensus
{
    /// <summary>Colony id to owning empire id, for the colonies of watched empires.</summary>
    public Dictionary<int, int> ColonyOwners { get; } = new();

    /// <summary>Ids of empires that are still in the game.</summary>
    public HashSet<int> ActiveEmpires { get; } = new();

    /// <summary>(empire, other empire) to what that relation says, for watched empires' relations with other empires.</summary>
    public Dictionary<(int Empire, int Other), RelationFacts> Relations { get; } = new();
}

/// <summary>
/// Detects big events by diffing two censuses. Diffing, not hooking: DW2 changes a relation
/// through fourteen ChangeEmpireRelation overloads and several treaty paths, and a hook on each
/// would miss the one nobody found. Whatever path the game took, the facts changed.
///
/// A war or a first contact shows in both empires' relation lists, so pairs are reported once,
/// lower id first.
/// </summary>
public static class GalaxyEventDetector
{
    public static List<string> Detect(GalaxyEventCensus before, GalaxyEventCensus after)
    {
        var events = new List<string>();

        if (before is null || after is null)
        {
            return events;
        }

        DetectColonyEvents(before, after, events);
        DetectEliminations(before, after, events);
        DetectRelationEvents(before, after, events);

        return events;
    }

    private static void DetectColonyEvents(GalaxyEventCensus before, GalaxyEventCensus after, List<string> events)
    {
        foreach (var colony in after.ColonyOwners)
        {
            bool existed = before.ColonyOwners.TryGetValue(colony.Key, out int previousOwner);
            if (!existed)
            {
                events.Add($"colony {Text(colony.Key)} gained by empire {Text(colony.Value)}");
                continue;
            }

            if (previousOwner != colony.Value)
            {
                events.Add($"colony {Text(colony.Key)} lost by empire {Text(previousOwner)} to empire {Text(colony.Value)}");
            }
        }

        foreach (var colony in before.ColonyOwners)
        {
            if (!after.ColonyOwners.ContainsKey(colony.Key))
            {
                events.Add($"colony {Text(colony.Key)} lost by empire {Text(colony.Value)}");
            }
        }
    }

    private static void DetectEliminations(GalaxyEventCensus before, GalaxyEventCensus after, List<string> events)
    {
        foreach (int empireId in before.ActiveEmpires)
        {
            if (!after.ActiveEmpires.Contains(empireId))
            {
                events.Add($"empire {Text(empireId)} eliminated");
            }
        }
    }

    private static void DetectRelationEvents(GalaxyEventCensus before, GalaxyEventCensus after, List<string> events)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var relation in after.Relations)
        {
            before.Relations.TryGetValue(relation.Key, out RelationFacts previous);
            RelationFacts current = relation.Value;

            int lower = Math.Min(relation.Key.Empire, relation.Key.Other);
            int higher = Math.Max(relation.Key.Empire, relation.Key.Other);
            string pair = $"empires {Text(lower)} and {Text(higher)}";

            if (current.HaveMet && !previous.HaveMet)
            {
                AddOnce(events, reported, "first contact between " + pair);
            }

            if (current.AtWar && !previous.AtWar)
            {
                AddOnce(events, reported, "war declared between " + pair);
            }

            if (!current.AtWar && previous.AtWar)
            {
                AddOnce(events, reported, "peace made between " + pair);
            }
        }
    }

    private static void AddOnce(List<string> events, HashSet<string> reported, string description)
    {
        if (reported.Add(description))
        {
            events.Add(description);
        }
    }

    private static string Text(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>What the joiner should do to its own local pause state, given the host's.</summary>
public enum PauseMirrorAction
{
    None,
    PauseLocally,
    ResumeLocally,
}

/// <summary>
/// Host-owned pause, the pure half (mitch, in chat 2026-11-10: "only the host's pause and speed
/// count"). The joiner does not simulate, so its own pause state decides nothing about the
/// galaxy; but DW2 still shows it, and DW2 sets it by itself (StartGameExisting pauses on every
/// adoption, and an empire message auto-pauses). Left alone, the 2026-11-10 two-instance run
/// had the joiner sitting at DWGame.AutoPaused=True for the whole session while the host ran.
///
/// So the joiner's pause state simply follows the host's: every clock sample from the host
/// says "running at speed S" or "paused" (speed 0), and this says what to do about it.
/// </summary>
public static class PauseMirror
{
    public static PauseMirrorAction Decide(float hostSpeed, bool locallyPaused)
    {
        bool hostPaused = hostSpeed <= 0f;

        if (hostPaused && !locallyPaused)
        {
            return PauseMirrorAction.PauseLocally;
        }

        if (!hostPaused && locallyPaused)
        {
            return PauseMirrorAction.ResumeLocally;
        }

        return PauseMirrorAction.None;
    }
}
