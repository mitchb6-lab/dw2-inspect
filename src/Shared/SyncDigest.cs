using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dw2MpShared;

/// <summary>One collection the sync carries: how many members it has and a hash of which ones.</summary>
public sealed class SyncDigestEntry
{
    public string Name { get; set; } = "";

    public int Count { get; set; }

    /// <summary>Order-independent hash of the member ids. Equal sets give equal hashes on both machines.</summary>
    public ulong SetHash { get; set; }

    /// <summary>
    /// Host only: members that changed since the last delta was built, so the joiner cannot
    /// know about them yet. The joiner's own value is ignored.
    /// </summary>
    public int Unsent { get; set; }
}

/// <summary>
/// What a StateSummary says about the galaxy since protocol 4 (2026-11-10): one entry per
/// collection that deltas actually carry, and nothing else.
///
/// The fingerprint it replaces hashed the ship ids together with the Empires and Orbs counts.
/// Deltas never carried orbs, so the first orb the host gained was a permanent mismatch that no
/// full state could repair: the two-instance run of 2026-11-10 raised DESYNC DETECTED three
/// summaries after the join with every ship, colony, character and design identical on both
/// sides (Orbs 1,401 on the host, 1,400 on the joiner, before and after a fresh full state).
/// Comparing only what the sync carries removes that whole class of false alarm.
///
/// Pure .NET, no game types: compiled into the mod and into src\Dw2MpTests.
/// </summary>
public sealed class SyncDigest
{
    public const int MaxEntries = 16;

    public const int MaxNameLength = 24;

    public List<SyncDigestEntry> Entries { get; } = new();

    public void Add(string name, int count, ulong setHash, int unsent)
    {
        var entry = new SyncDigestEntry
        {
            Name = name,
            Count = count,
            SetHash = setHash,
            Unsent = unsent,
        };
        Entries.Add(entry);
    }

    public SyncDigestEntry Find(string name)
    {
        foreach (SyncDigestEntry entry in Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ set hash

    /// <summary>The hash of an empty set. Folding ids into it in any order gives the same result.</summary>
    public const ulong EmptySetHash = 0;

    /// <summary>
    /// Fold one member id into a set hash. Addition of a mixed id is commutative, so the two
    /// machines need not walk their lists in the same order, and nothing has to be sorted.
    /// </summary>
    public static ulong Fold(ulong setHash, long memberId)
    {
        unchecked
        {
            return setHash + Mix((ulong)memberId);
        }
    }

    /// <summary>SplitMix64's finaliser: every input bit affects every output bit.</summary>
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    // ------------------------------------------------------------------ wire

    public void Write(BinaryWriter writer)
    {
        int count = Math.Min(Entries.Count, MaxEntries);
        writer.Write((byte)count);

        for (int index = 0; index < count; index++)
        {
            SyncDigestEntry entry = Entries[index];
            string name = entry.Name ?? "";
            if (name.Length > MaxNameLength)
            {
                name = name.Substring(0, MaxNameLength);
            }

            writer.Write(name);
            writer.Write(entry.Count);
            writer.Write(entry.SetHash);
            writer.Write(entry.Unsent);
        }
    }

    /// <summary>
    /// Read a digest a peer sent. Returns null for anything that is not one: too many entries,
    /// an over-long or empty name, a negative count, a truncated stream. Never throws.
    /// </summary>
    public static SyncDigest TryRead(BinaryReader reader)
    {
        try
        {
            int count = reader.ReadByte();
            if (count > MaxEntries)
            {
                return null;
            }

            var digest = new SyncDigest();
            for (int index = 0; index < count; index++)
            {
                string name = reader.ReadString();
                int memberCount = reader.ReadInt32();
                ulong setHash = reader.ReadUInt64();
                int unsent = reader.ReadInt32();

                if (name.Length == 0 || name.Length > MaxNameLength)
                {
                    return null;
                }

                if (memberCount < 0 || unsent < 0)
                {
                    return null;
                }

                digest.Add(name, memberCount, setHash, unsent);
            }

            return digest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"ships=282:1A2B3C4D colonies=12:..." for logs and the desync report. Short on purpose.</summary>
    public string Describe()
    {
        var text = new StringBuilder();

        foreach (SyncDigestEntry entry in Entries)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(entry.Name);
            text.Append('=');
            text.Append(entry.Count.ToString(CultureInfo.InvariantCulture));
            text.Append(':');
            text.Append(((uint)(entry.SetHash & 0xFFFFFFFF)).ToString("X8", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}

public enum SyncVerdict
{
    /// <summary>Every carried collection agrees.</summary>
    Match,

    /// <summary>The two sides are not looking at the same baseline, so there is nothing to compare.</summary>
    NotComparable,

    /// <summary>A difference small and fresh enough to be latency. Neither a match nor a mismatch.</summary>
    Tolerated,

    /// <summary>A real difference in state the sync carries.</summary>
    Mismatch,
}

/// <summary>What the joiner knows about its own position in the stream when a summary is checked.</summary>
public sealed class SyncCheckContext
{
    /// <summary>The full-state number the host built this summary against.</summary>
    public long SummaryBaseline { get; set; }

    /// <summary>The full-state number the joiner has adopted.</summary>
    public long AppliedBaseline { get; set; }

    /// <summary>Deltas held back because their full state has not landed yet.</summary>
    public int DeltasHeld { get; set; }

    /// <summary>True when a full state has arrived and is waiting to be adopted.</summary>
    public bool FullStateQueued { get; set; }
}

public sealed class SyncCheckResult
{
    public SyncVerdict Verdict { get; set; }

    /// <summary>Why, in words a log reader can act on.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Real mismatches in a row, including this one.</summary>
    public int ConsecutiveMismatches { get; set; }

    /// <summary>True on the summary where the run of mismatches first reaches the desync threshold.</summary>
    public bool BecameSustained { get; set; }

    /// <summary>True while the run of mismatches is at or past the threshold.</summary>
    public bool Sustained { get; set; }
}

/// <summary>
/// The tolerance rules of the desync check (mitch, in chat 2026-11-10: "tolerates known latency
/// ... only a sustained real mismatch raises the popup").
///
///   NOT COMPARABLE  the summary was built against a different full state than the joiner has
///                   adopted (a keyframe is in transit, or the summary is older than the state
///                   just adopted), or deltas are being held, or a state is waiting to be
///                   adopted. Counters are left exactly as they were: a keyframe can never
///                   start, extend or end a run of mismatches.
///   TOLERATED       a collection differs, but by no more than the members the host says it has
///                   not sent yet (objects created since its last delta), or by one more than
///                   that for a single summary. The second summary in a row with the same
///                   collection still off is a mismatch.
///   MISMATCH        anything else. MismatchesBeforeDesync of them in a row, unbroken by a
///                   match, is a sustained desync.
///
/// One instance per joiner. Pure: no clocks, no game types.
/// </summary>
public sealed class DesyncTolerance
{
    /// <summary>A count gap this small, beyond what the host has not sent, is latency for one summary.</summary>
    public const int ExtraGapTolerated = 1;

    /// <summary>How many summaries in a row one collection may sit inside the extra gap.</summary>
    public const int SummariesExtraGapTolerated = 1;

    /// <summary>Real mismatches in a row before the popup. Unchanged from the fingerprint check it replaces.</summary>
    public const int MismatchesBeforeDesync = 3;

    private readonly Dictionary<string, int> _toleratedInARow = new(StringComparer.Ordinal);

    private int _consecutiveMismatches;

    public int ConsecutiveMismatches => _consecutiveMismatches;

    /// <summary>Forget everything: a desync incident was closed, or the link was lost.</summary>
    public void Reset()
    {
        _toleratedInARow.Clear();
        _consecutiveMismatches = 0;
    }

    public SyncCheckResult Evaluate(SyncDigest host, SyncDigest mine, SyncCheckContext context)
    {
        string notComparable = WhyNotComparable(host, mine, context);
        if (notComparable.Length > 0)
        {
            return Result(SyncVerdict.NotComparable, notComparable, false);
        }

        var mismatches = new List<string>();
        var tolerated = new List<string>();

        foreach (SyncDigestEntry hostEntry in host.Entries)
        {
            SyncDigestEntry myEntry = mine.Find(hostEntry.Name);
            if (myEntry is null)
            {
                // A collection this build does not know is not state this build's sync carries.
                continue;
            }

            bool sameMembers = hostEntry.Count == myEntry.Count && hostEntry.SetHash == myEntry.SetHash;
            if (sameMembers)
            {
                _toleratedInARow.Remove(hostEntry.Name);
                continue;
            }

            int gap = Math.Abs(hostEntry.Count - myEntry.Count);
            string description = $"{hostEntry.Name} host {hostEntry.Count} mine {myEntry.Count}";

            if (hostEntry.Unsent > 0 && gap <= hostEntry.Unsent)
            {
                tolerated.Add(description + $" ({hostEntry.Unsent} not sent yet)");
                continue;
            }

            if (gap <= hostEntry.Unsent + ExtraGapTolerated)
            {
                _toleratedInARow.TryGetValue(hostEntry.Name, out int runSoFar);
                if (runSoFar < SummariesExtraGapTolerated)
                {
                    _toleratedInARow[hostEntry.Name] = runSoFar + 1;
                    tolerated.Add(description + " (within one, first summary)");
                    continue;
                }

                mismatches.Add(description + " (still off by one on a second summary)");
                continue;
            }

            mismatches.Add(description);
        }

        if (mismatches.Count > 0)
        {
            return Result(SyncVerdict.Mismatch, string.Join("; ", mismatches), true);
        }

        if (tolerated.Count > 0)
        {
            return Result(SyncVerdict.Tolerated, string.Join("; ", tolerated), false);
        }

        _consecutiveMismatches = 0;
        return Result(SyncVerdict.Match, "", false);
    }

    private static string WhyNotComparable(SyncDigest host, SyncDigest mine, SyncCheckContext context)
    {
        if (host is null || mine is null)
        {
            return "no digest on one side";
        }

        if (context.SummaryBaseline > context.AppliedBaseline)
        {
            return $"summary is for full state #{context.SummaryBaseline}, still in transit (have #{context.AppliedBaseline})";
        }

        if (context.SummaryBaseline < context.AppliedBaseline)
        {
            return $"summary is for superseded full state #{context.SummaryBaseline} (have #{context.AppliedBaseline})";
        }

        if (context.FullStateQueued)
        {
            return "a full state is waiting to be adopted";
        }

        if (context.DeltasHeld > 0)
        {
            return $"{context.DeltasHeld} delta(s) held for a full state";
        }

        return "";
    }

    private SyncCheckResult Result(SyncVerdict verdict, string detail, bool isMismatch)
    {
        bool becameSustained = false;

        if (isMismatch)
        {
            _consecutiveMismatches++;
            becameSustained = _consecutiveMismatches == MismatchesBeforeDesync;
        }

        return new SyncCheckResult
        {
            Verdict = verdict,
            Detail = detail,
            ConsecutiveMismatches = _consecutiveMismatches,
            BecameSustained = becameSustained,
            Sustained = _consecutiveMismatches >= MismatchesBeforeDesync,
        };
    }
}
