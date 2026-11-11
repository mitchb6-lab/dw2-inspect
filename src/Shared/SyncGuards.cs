using System;
using System.Collections.Generic;
using System.IO;

namespace Dw2MpShared;

/// <summary>
/// Limits on what the joiner will read from the wire, added after the 2026-11-10 security
/// review of protocol 4. The host is the other player's PC, not a trusted server: every count
/// and every length in a frame is a number somebody else chose. Pure .NET, tested.
/// </summary>
public static class WireLimits
{
    /// <summary>One serialised ship, colony, design, character or fleet. Real ones are a few kilobytes.</summary>
    public const int MaxObjectBytes = 4 * 1024 * 1024;

    /// <summary>A Delta frame. The largest seen was 364 KB (every design and ship at once).</summary>
    public const int MaxDeltaFrameBytes = 16 * 1024 * 1024;

    /// <summary>A decompressed full state. Real ones are 13 to 42 MB; a deflate bomb is stopped here.</summary>
    public const long MaxRawStateBytes = 256L * 1024 * 1024;

    /// <summary>How far a record count may exceed the local collection it names: room for members created in the same delta.</summary>
    public const int CountMargin = 4096;

    public static long BytesLeft(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        return stream.Length - stream.Position;
    }

    /// <summary>
    /// Read a record count and refuse one that cannot be true: negative, more records than the
    /// bytes left in the frame could hold, or (when a local count is given) more than the local
    /// collection plus a margin. Pass -1 as the local count for a section that creates members.
    /// </summary>
    public static int ReadCount(BinaryReader reader, int minimumRecordBytes, int localCount, string what)
    {
        int count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException($"{what}: negative count {count}");
        }

        long bytesNeeded = (long)count * Math.Max(1, minimumRecordBytes);
        if (bytesNeeded > BytesLeft(reader))
        {
            throw new InvalidDataException($"{what}: {count} record(s) cannot fit in the {BytesLeft(reader)} byte(s) left");
        }

        if (localCount >= 0 && count > (long)localCount + CountMargin)
        {
            throw new InvalidDataException($"{what}: {count} record(s) for a collection of {localCount}");
        }

        return count;
    }

    /// <summary>
    /// Read a length-prefixed block. Null for a zero length (the writer's "nothing here"); an
    /// exception for a negative length, one over the cap, or one longer than the frame, so a
    /// hostile length never becomes an allocation.
    /// </summary>
    public static byte[] ReadCappedBytes(BinaryReader reader, int maxBytes, string what)
    {
        int length = reader.ReadInt32();
        if (length == 0)
        {
            return null;
        }

        if (length < 0 || length > maxBytes)
        {
            throw new InvalidDataException($"{what}: block of {length} byte(s) (cap {maxBytes})");
        }

        if (length > BytesLeft(reader))
        {
            throw new EndOfStreamException($"{what}: block of {length} byte(s) but only {BytesLeft(reader)} left");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException($"{what}: block truncated");
        }

        return bytes;
    }

    public static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

/// <summary>
/// The joiner's queue of summaries waiting to be checked, and the rule for taking one.
///
/// A check walks every ship, colony, design, character and fleet on the main thread. Before
/// this gate, every queued summary was checked in the same frame: a host that sent a thousand
/// summaries made the joiner build a thousand digests before it drew again (security review
/// H1, 2026-11-10). Now at most ONE summary is checked per call, never more often than once a
/// second, and it is the NEWEST one whose earlier deltas have all been taken; older ready ones
/// are discarded, because a newer summary says everything an older one could.
///
/// Thread-safe: the network thread offers, the main thread takes.
/// </summary>
public sealed class SummaryGate
{
    public const int MaxPending = 32;

    public const double MinSecondsBetweenChecks = 1.0;

    private readonly object _gate = new();
    private readonly Queue<(long DeltasBefore, byte[] Payload)> _pending = new();
    private double _lastCheckSeconds = double.NegativeInfinity;

    /// <summary>Summaries pushed out by newer ones while the queue was full.</summary>
    public long Dropped { get; private set; }

    /// <summary>Ready summaries discarded because a newer ready one was checked in their place.</summary>
    public long Superseded { get; private set; }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Queue a summary with the number of deltas that arrived before it. A full queue loses its OLDEST entry.</summary>
    public void Offer(long deltasBefore, byte[] payload)
    {
        lock (_gate)
        {
            if (_pending.Count >= MaxPending)
            {
                _pending.Dequeue();
                Dropped++;
            }

            _pending.Enqueue((deltasBefore, payload));
        }
    }

    /// <summary>
    /// The one summary to check now, or null. Null when the last check was under a second ago,
    /// or when no queued summary has had all its earlier deltas taken.
    /// </summary>
    public byte[] TakeNewestReady(long deltasTaken, double nowSeconds)
    {
        lock (_gate)
        {
            if (nowSeconds - _lastCheckSeconds < MinSecondsBetweenChecks)
            {
                return null;
            }

            byte[] newestReady = null;
            while (_pending.Count > 0 && _pending.Peek().DeltasBefore <= deltasTaken)
            {
                if (newestReady is not null)
                {
                    Superseded++;
                }

                newestReady = _pending.Dequeue().Payload;
            }

            if (newestReady is not null)
            {
                _lastCheckSeconds = nowSeconds;
            }

            return newestReady;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}

/// <summary>
/// Is a clock sample from the host believable? The joiner's whole clock is what the host says
/// it is, so a sample is checked before it is followed (security review M2): the time must be a
/// real DateTime, the speed a finite number in DW2's range, and the time must not have leapt
/// relative to the last sample by more than the wall time between them could explain.
/// A run of rejected samples is eventually believed, so a host whose clock really did move is
/// not ignored for ever.
/// </summary>
public sealed class ClockSampleCheck
{
    /// <summary>DW2's fastest speed is 4x; 8 leaves room without accepting nonsense.</summary>
    public const float MaxSpeed = 8f;

    public const double MaxBackwardSeconds = 10;

    public const double ForwardSlackSeconds = 60;

    public const int RejectionsBeforeBelieving = 30;

    private const long TicksPerSecond = 10_000_000;

    /// <summary>Leave a year of headroom so extrapolating from a sample cannot overflow DateTime.</summary>
    private static readonly long MaxTicks = DateTime.MaxValue.Ticks - (366L * 24 * 3600 * TicksPerSecond);

    private bool _hasPrevious;
    private long _previousTicks;
    private int _rejectedInARow;

    public long Rejected { get; private set; }

    public static bool InRange(long ticks, float speed)
    {
        if (ticks <= 0 || ticks > MaxTicks)
        {
            return false;
        }

        if (float.IsNaN(speed) || float.IsInfinity(speed))
        {
            return false;
        }

        return speed >= 0f && speed <= MaxSpeed;
    }

    public bool Accept(long ticks, float speed, double wallSecondsSincePrevious)
    {
        if (!InRange(ticks, speed))
        {
            Rejected++;
            return false;
        }

        if (_hasPrevious && _rejectedInARow < RejectionsBeforeBelieving)
        {
            double movedSeconds = (ticks - _previousTicks) / (double)TicksPerSecond;
            double wallSeconds = Math.Max(0, wallSecondsSincePrevious);
            double allowedForward = (wallSeconds + ForwardSlackSeconds) * MaxSpeed;

            if (movedSeconds < -MaxBackwardSeconds || movedSeconds > allowedForward)
            {
                Rejected++;
                _rejectedInARow++;
                return false;
            }
        }

        _hasPrevious = true;
        _previousTicks = ticks;
        _rejectedInARow = 0;
        return true;
    }
}

/// <summary>
/// The joiner's own limit on how often it adopts a full state it did not ask for (security
/// review M1). Every adoption costs it tens of megabytes of native memory that never come back
/// and a visible hitch, and the host decides when to send one. A state the joiner asked for
/// (the join, a repair, a resync chosen on the popup) is adopted at once; an unsolicited one
/// waits until this long has passed since the last adoption, with only the newest kept.
/// </summary>
public static class AdoptionGate
{
    /// <summary>Half the host's 60 s served-state floor, so an honest host's keyframes are never delayed.</summary>
    public const double MinSecondsBetweenUnsolicited = 30;

    public static bool MayAdoptNow(bool solicited, double secondsSinceLastAdoption)
    {
        if (solicited)
        {
            return true;
        }

        return secondsSinceLastAdoption >= MinSecondsBetweenUnsolicited;
    }
}

/// <summary>
/// Which failures have been logged already. Keyed on a short, fixed string (stage and exception
/// type), never on text that came off the wire or out of an exception message: the set this
/// replaces grew by one entry for every distinct message a peer could provoke (security review
/// M3). Capped; past the cap failures are still counted, just not remembered.
/// </summary>
public sealed class FailureLedger
{
    public const int MaxDistinct = 64;

    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private long _total;

    public long Total
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    public int DistinctCount
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count;
            }
        }
    }

    /// <summary>Count a failure. True when this key is new and should be logged once.</summary>
    public bool Note(string key)
    {
        lock (_gate)
        {
            _total++;

            if (_keys.Count >= MaxDistinct)
            {
                return false;
            }

            return _keys.Add(key ?? "");
        }
    }
}

/// <summary>Log the first few of something, then every Nth: a line a peer can cause must not be a line per packet.</summary>
public static class LogThrottle
{
    public static bool ShouldLog(long occurrence, int alwaysFirst, int thenEvery)
    {
        if (occurrence <= alwaysFirst)
        {
            return true;
        }

        return occurrence % thenEvery == 0;
    }
}

/// <summary>
/// The arguments of DW2's Population constructor, in DW2's order:
/// Population(Byte raceId, Int64 amount, Single reproductionRate, Single assimilation).
/// The first version of the colony-extras reader passed assimilation and reproduction rate the
/// other way round (found by the 2026-11-10 security review), so a race the joiner was told
/// about for the first time got the two swapped. One place, one order, one test.
/// </summary>
public static class PopulationArguments
{
    public static object[] ForConstructor(byte raceId, long amount, float assimilation, float reproductionRate)
    {
        return new object[] { raceId, amount, reproductionRate, assimilation };
    }
}

/// <summary>
/// The first eight bytes of a relation list as DW2's EmpireRelationList.WriteToStream writes
/// it: Int16 EmpireId, Int32 IndependentColonyOrbId, Int16 relation count. Checked before the
/// bytes are handed to DW2's own reader (security review): they must be for the list they are
/// about to replace, and the count must be one the bytes could hold.
/// </summary>
public static class RelationListHeader
{
    public const int HeaderBytes = 8;

    public const int MaxRelations = 4096;

    /// <summary>A relation is at least eight Int64 dates and four ids on the wire.</summary>
    public const int MinimumRelationBytes = 60;

    public static bool IsSane(byte[] bytes, short expectedListEmpireId)
    {
        if (bytes is null || bytes.Length < HeaderBytes)
        {
            return false;
        }

        short listEmpireId = BitConverter.ToInt16(bytes, 0);
        short relationCount = BitConverter.ToInt16(bytes, 6);

        if (listEmpireId != expectedListEmpireId)
        {
            return false;
        }

        if (relationCount < 0 || relationCount > MaxRelations)
        {
            return false;
        }

        long bytesNeeded = (long)relationCount * MinimumRelationBytes;
        return bytesNeeded <= bytes.Length - HeaderBytes;
    }
}

/// <summary>
/// Which full states the joiner has asked for and not yet been given (security review of
/// 2026-11-11, M1). A solicited state skips the AdoptionGate wait, so "solicited" must be
/// something a host cannot stretch: each reason here is good for ONE adoption attempt.
///
///   - the join: the first state this game adopts;
///   - a request this side sent (the join bootstrap, a quiet repair, "Attempt resync" on the
///     desync popup -- every one of them goes through NetSession.RequestResync).
///
/// Before this, the whole 60-second Resyncing phase of a desync incident counted as solicited,
/// so a host could send full states back to back for a minute and have each one adopted at
/// once, at about 45 MB of native memory apiece. Now the first state after the choice is
/// adopted at once and the next waits its 30 s like any other.
///
/// An attempt spends every reason at once, whether or not the state applied: a state that
/// failed to apply is asked for again, and asking is what arms the next exemption.
/// Thread-safe: requests are noted from the network thread, attempts happen on the main thread.
/// </summary>
public sealed class SolicitedStateExemptions
{
    private readonly object _gate = new();
    private bool _joinStateOwed = true;
    private bool _requestedStateOwed;

    /// <summary>True while a state this side is owed has not been attempted yet.</summary>
    public bool NextStateIsSolicited
    {
        get
        {
            lock (_gate)
            {
                return _joinStateOwed || _requestedStateOwed;
            }
        }
    }

    /// <summary>This side sent a request for a full state. Two requests before one state arrives are still one exemption.</summary>
    public void NoteStateRequested()
    {
        lock (_gate)
        {
            _requestedStateOwed = true;
        }
    }

    /// <summary>A full state is being adopted now. Whatever was owed has been delivered.</summary>
    public void SpendOnAdoptionAttempt()
    {
        lock (_gate)
        {
            _joinStateOwed = false;
            _requestedStateOwed = false;
        }
    }
}

/// <summary>
/// Is this the game build some code was read against? Code that depends on the exact shape of
/// DW2's IL (field layouts, who reads a render target) must switch itself off on any other
/// build rather than assume the shape survived a patch.
/// </summary>
public static class GameBuild
{
    public static bool IsKnown(string gameVersion, IReadOnlyList<string> knownPrefixes)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return false;
        }

        if (knownPrefixes is null)
        {
            return false;
        }

        foreach (string prefix in knownPrefixes)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                continue;
            }

            if (gameVersion.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
