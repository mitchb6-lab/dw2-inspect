using System;
using System.Collections.Generic;

namespace Dw2MpShared;

/// <summary>
/// The waiting list between "the game replaced this object" and "its graphics memory may be
/// released" (2026-11-11).
///
/// DW2's renderer gives every galaxy-bound render system a new instance for each galaxy it is
/// handed and disposes none of the old ones (docs/multiplayer.md "Adoption memory"). The mod
/// notices the replacement and releases the discarded instance's render target itself. This
/// class decides WHEN, and it is deliberately slow and stubborn about it:
///
///   - nothing is handed out before BOTH a wall-clock delay and a number of main-thread updates
///     have passed since the replacement, so a frame that was already being drawn is long over
///     and a process that was frozen for the whole delay still gets its frames first;
///   - at that moment the caller is asked whether the object or its resource is in use again.
///     One that is, is forgotten, never handed out: if the game discards it again it is noticed
///     again;
///   - a resource is queued once however often it is reported, and the queue has a ceiling. Past
///     the ceiling a replacement is counted and not queued: that is the game's leak coming back,
///     which is safe, where an unbounded list would not be.
///
/// WHAT IT HOLDS (security review of 2026-11-11): the RESOURCE strongly, because releasing it is
/// what the queue is for and a texture pins nothing else; the discarded OWNER only weakly. A
/// discarded MapOverlaySystem still points at the galaxy it was built for, about 26 MB, and a
/// host that sent states back to back could otherwise have parked one whole galaxy per queue
/// slot here for the length of the delay. The ceiling is 8 for the same reason.
///
/// Pure .NET: it knows nothing about Stride, Harmony or DW2. Tested.
/// </summary>
public sealed class DiscardedResourceQueue
{
    /// <summary>Seconds between a replacement and the release of what it discarded.</summary>
    public const double DefaultDelaySeconds = 10.0;

    /// <summary>Main-thread updates that must also have passed. At 30 frames a second this is two seconds.</summary>
    public const int DefaultMinimumUpdates = 60;

    /// <summary>
    /// Two overlays per renderer per adoption, and an unsolicited adoption at most every 30 s
    /// (AdoptionGate) against a 10 s delay: an honest session has two waiting, rarely four.
    /// </summary>
    public const int DefaultMaximumPending = 8;

    /// <summary>One discarded resource waiting for its release.</summary>
    public sealed class PendingRelease
    {
        /// <summary>The graphics resource to release. Held strongly.</summary>
        public object Resource;

        /// <summary>The object the game replaced, which owned the resource. Held weakly: see the class remarks.</summary>
        public WeakReference OwnerReference;

        /// <summary>Which field the owner was replaced in, for the log.</summary>
        public string SlotName;

        public double DueAtSeconds;

        public long DueAtUpdate;

        /// <summary>The owner if it is still alive, otherwise null. Nothing the game still uses can be null here.</summary>
        public object Owner
        {
            get
            {
                if (OwnerReference is null)
                {
                    return null;
                }

                return OwnerReference.Target;
            }
        }
    }

    private static readonly IReadOnlyList<PendingRelease> NothingDue = Array.Empty<PendingRelease>();

    private readonly double _delaySeconds;
    private readonly int _minimumUpdates;
    private readonly int _maximumPending;
    private readonly List<PendingRelease> _pending = new();
    private long _updates;

    public DiscardedResourceQueue(double delaySeconds, int minimumUpdates, int maximumPending)
    {
        _delaySeconds = delaySeconds;
        _minimumUpdates = minimumUpdates;
        _maximumPending = maximumPending;
    }

    /// <summary>Resources waiting now.</summary>
    public int PendingCount
    {
        get
        {
            return _pending.Count;
        }
    }

    /// <summary>Replacements that were queued.</summary>
    public long QueuedCount { get; private set; }

    /// <summary>Resources handed out for release.</summary>
    public long HandedOutCount { get; private set; }

    /// <summary>Resources that were in use again when their time came, and were forgotten instead.</summary>
    public long KeptInUseCount { get; private set; }

    /// <summary>Replacements not queued because the queue was full.</summary>
    public long DroppedQueueFullCount { get; private set; }

    /// <summary>
    /// The off switch. Only the text "0" switches the release off; unset, empty and anything
    /// else leave it on, so a typo cannot silently bring the leak back.
    /// </summary>
    public static bool EnabledFromSwitchText(string switchText)
    {
        if (switchText is null)
        {
            return true;
        }

        return switchText.Trim() != "0";
    }

    /// <summary>
    /// Report what one field held before and after the game's own code ran, and the resource the
    /// old object owned. Queues that resource when the object was really replaced. Returns true
    /// when it was queued.
    /// </summary>
    public bool NoteReplacement(string slotName, object before, object after, object resourceOfBefore, double nowSeconds)
    {
        if (before is null)
        {
            return false;
        }

        if (ReferenceEquals(before, after))
        {
            return false;
        }

        if (resourceOfBefore is null)
        {
            return false;
        }

        foreach (PendingRelease waiting in _pending)
        {
            if (ReferenceEquals(waiting.Resource, resourceOfBefore))
            {
                return false;
            }
        }

        if (_pending.Count >= _maximumPending)
        {
            DroppedQueueFullCount++;
            return false;
        }

        PendingRelease pending = new PendingRelease();
        pending.Resource = resourceOfBefore;
        pending.OwnerReference = new WeakReference(before);
        pending.SlotName = slotName;
        pending.DueAtSeconds = nowSeconds + _delaySeconds;
        pending.DueAtUpdate = _updates + _minimumUpdates;
        _pending.Add(pending);
        QueuedCount++;
        return true;
    }

    /// <summary>
    /// Call once per main-thread update. Returns the resources whose wait is over and that
    /// <paramref name="isInUseNow"/> says are not in use; the caller releases them. The predicate
    /// is given the owner (null once it has been collected) and the resource. The queue forgets
    /// everything it returns and everything found in use.
    /// </summary>
    public IReadOnlyList<PendingRelease> TakeDue(double nowSeconds, Func<object, object, bool> isInUseNow)
    {
        _updates++;

        if (_pending.Count == 0)
        {
            return NothingDue;
        }

        List<PendingRelease> due = null;

        for (int index = _pending.Count - 1; index >= 0; index--)
        {
            PendingRelease waiting = _pending[index];

            if (nowSeconds < waiting.DueAtSeconds)
            {
                continue;
            }

            if (_updates < waiting.DueAtUpdate)
            {
                continue;
            }

            _pending.RemoveAt(index);

            if (isInUseNow(waiting.Owner, waiting.Resource))
            {
                KeptInUseCount++;
                continue;
            }

            if (due is null)
            {
                due = new List<PendingRelease>();
            }

            due.Add(waiting);
            HandedOutCount++;
        }

        if (due is null)
        {
            return NothingDue;
        }

        return due;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the very object in one of
    /// <paramref name="currentValues"/> (the same reference, not an equal one).
    /// </summary>
    public static bool IsAmong(object candidate, IEnumerable<object> currentValues)
    {
        if (candidate is null)
        {
            return false;
        }

        foreach (object current in currentValues)
        {
            if (ReferenceEquals(current, candidate))
            {
                return true;
            }
        }

        return false;
    }
}
