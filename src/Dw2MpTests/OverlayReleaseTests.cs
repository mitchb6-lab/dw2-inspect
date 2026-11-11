using System.Runtime.CompilerServices;
using Dw2MpLobby;
using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for the waiting list between "the renderer replaced this overlay system" and "its
/// render target may be released" (2026-11-11): what is queued, when it is handed out, that a
/// resource in use is never handed out, and that the queue does not keep the discarded owner
/// (and with it a whole galaxy) alive. The disposing itself needs the game and is verified by a
/// two-instance run (docs/multiplayer.md "Overlay release").
/// </summary>
public static partial class Program
{
    private const double TestDelaySeconds = 10.0;
    private const int TestMinimumUpdates = 3;
    private const int TestMaximumPending = 4;

    private static void RunOverlayReleaseTests()
    {
        OnlyARealReplacementIsQueued();
        AnOwnerWithoutAResourceIsNotQueued();
        AResourceReportedTwiceIsQueuedOnce();
        NothingIsHandedOutBeforeTheDelay();
        NothingIsHandedOutBeforeEnoughUpdates();
        AResourceIsHandedOutExactlyOnce();
        AResourceInUseIsForgottenNotHandedOut();
        AForgottenResourceCanBeQueuedAgain();
        EachResourceWaitsItsOwnDelay();
        AFullQueueCountsInsteadOfGrowing();
        TheQueueDoesNotKeepTheDiscardedOwnerAlive();
        TheDefaultCeilingIsSmall();
        InUseMeansTheSameObjectNotAnEqualOne();
        OnlyZeroSwitchesTheReleaseOff();
        TheLobbyLetsTheOffSwitchThrough();
    }

    private static DiscardedResourceQueue NewTestQueue()
    {
        return new DiscardedResourceQueue(TestDelaySeconds, TestMinimumUpdates, TestMaximumPending);
    }

    private static bool NothingInUse(object owner, object resource)
    {
        return false;
    }

    private static bool EverythingInUse(object owner, object resource)
    {
        return true;
    }

    /// <summary>Runs the given number of main-thread updates at one moment and returns what the last one handed out.</summary>
    private static IReadOnlyList<DiscardedResourceQueue.PendingRelease> RunUpdates(
        DiscardedResourceQueue queue, int updates, double nowSeconds, Func<object, object, bool> isInUseNow)
    {
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> lastResult = Array.Empty<DiscardedResourceQueue.PendingRelease>();
        for (int update = 0; update < updates; update++)
        {
            lastResult = queue.TakeDue(nowSeconds, isInUseNow);
        }

        return lastResult;
    }

    private static void OnlyARealReplacementIsQueued()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        object oldSystem = new object();
        object newSystem = new object();
        object renderTarget = new object();

        bool nothingBefore = queue.NoteReplacement("slot", null, newSystem, renderTarget, 0.0);
        bool unchanged = queue.NoteReplacement("slot", oldSystem, oldSystem, renderTarget, 0.0);
        bool replaced = queue.NoteReplacement("slot", oldSystem, newSystem, renderTarget, 0.0);

        Check("overlay queue: a field that was empty before discards nothing", !nothingBefore);
        Check("overlay queue: a field that still holds the same object discards nothing", !unchanged);
        Check("overlay queue: a field given a new object queues the old one's resource", replaced);
        Check("overlay queue: one waiting, one counted", queue.PendingCount == 1 && queue.QueuedCount == 1, queue.PendingCount + "/" + queue.QueuedCount);

        DiscardedResourceQueue secondQueue = NewTestQueue();
        bool clearedToNull = secondQueue.NoteReplacement("slot", oldSystem, null, renderTarget, 0.0);
        Check("overlay queue: a field cleared to null also discards the old object", clearedToNull);
    }

    private static void AnOwnerWithoutAResourceIsNotQueued()
    {
        DiscardedResourceQueue queue = NewTestQueue();

        bool queued = queue.NoteReplacement("slot", new object(), new object(), null, 0.0);

        Check("overlay queue: a replaced object that never had a render target leaves nothing to release", !queued && queue.PendingCount == 0);
    }

    private static void AResourceReportedTwiceIsQueuedOnce()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        object oldSystem = new object();
        object renderTarget = new object();

        bool first = queue.NoteReplacement("slot", oldSystem, new object(), renderTarget, 0.0);
        bool second = queue.NoteReplacement("otherSlot", oldSystem, new object(), renderTarget, 1.0);
        bool sameResourceOtherOwner = queue.NoteReplacement("slot", new object(), new object(), renderTarget, 2.0);

        Check("overlay queue: the same resource reported twice is queued once", first && !second && queue.PendingCount == 1, queue.PendingCount.ToString());
        Check("overlay queue: and once even when a different owner reports it", !sameResourceOtherOwner && queue.PendingCount == 1);
    }

    private static void NothingIsHandedOutBeforeTheDelay()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        queue.NoteReplacement("slot", new object(), new object(), new object(), 100.0);

        IReadOnlyList<DiscardedResourceQueue.PendingRelease> early = RunUpdates(queue, 500, 109.9, NothingInUse);

        Check("overlay queue: nothing is handed out before the delay, however many updates pass", early.Count == 0 && queue.PendingCount == 1, early.Count.ToString());
    }

    private static void NothingIsHandedOutBeforeEnoughUpdates()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        queue.NoteReplacement("slot", new object(), new object(), new object(), 100.0);

        // A process frozen for an hour: the clock has moved, the frames have not.
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> firstUpdate = queue.TakeDue(3700.0, NothingInUse);
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> secondUpdate = queue.TakeDue(3700.0, NothingInUse);
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> thirdUpdate = queue.TakeDue(3700.0, NothingInUse);

        Check("overlay queue: a long freeze does not skip the updates that must pass first", firstUpdate.Count == 0 && secondUpdate.Count == 0);
        Check("overlay queue: handed out on the update that completes the count", thirdUpdate.Count == 1, thirdUpdate.Count.ToString());
    }

    private static void AResourceIsHandedOutExactlyOnce()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        object oldSystem = new object();
        object renderTarget = new object();
        queue.NoteReplacement("_LongRangeScannerSystem", oldSystem, new object(), renderTarget, 100.0);

        IReadOnlyList<DiscardedResourceQueue.PendingRelease> due = RunUpdates(queue, TestMinimumUpdates, 110.0, NothingInUse);
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> afterwards = RunUpdates(queue, 10, 500.0, NothingInUse);

        Check("overlay queue: handed out once the delay and the updates have both passed", due.Count == 1, due.Count.ToString());
        Check("overlay queue: it is the discarded object's resource, with the field it came from",
            due.Count == 1 && ReferenceEquals(due[0].Resource, renderTarget) && due[0].SlotName == "_LongRangeScannerSystem");
        Check("overlay queue: an owner that is still alive comes with it", due.Count == 1 && ReferenceEquals(due[0].Owner, oldSystem));
        Check("overlay queue: never handed out a second time", afterwards.Count == 0 && queue.PendingCount == 0 && queue.HandedOutCount == 1,
            afterwards.Count + "/" + queue.PendingCount + "/" + queue.HandedOutCount);

        GC.KeepAlive(oldSystem);
    }

    private static void AResourceInUseIsForgottenNotHandedOut()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        queue.NoteReplacement("slot", new object(), new object(), new object(), 100.0);

        IReadOnlyList<DiscardedResourceQueue.PendingRelease> whileInUse = RunUpdates(queue, TestMinimumUpdates, 110.0, EverythingInUse);
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> later = RunUpdates(queue, 10, 500.0, NothingInUse);

        Check("overlay queue: a resource in use when its time comes is not handed out", whileInUse.Count == 0);
        Check("overlay queue: and not later either: it is forgotten", later.Count == 0 && queue.PendingCount == 0, later.Count + "/" + queue.PendingCount);
        Check("overlay queue: counted as kept, not as handed out", queue.KeptInUseCount == 1 && queue.HandedOutCount == 0, queue.KeptInUseCount + "/" + queue.HandedOutCount);
    }

    private static void AForgottenResourceCanBeQueuedAgain()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        object system = new object();
        object renderTarget = new object();
        queue.NoteReplacement("slot", system, new object(), renderTarget, 100.0);
        RunUpdates(queue, TestMinimumUpdates, 110.0, EverythingInUse);

        bool queuedAgain = queue.NoteReplacement("slot", system, new object(), renderTarget, 200.0);
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> due = RunUpdates(queue, TestMinimumUpdates, 210.0, NothingInUse);

        Check("overlay queue: a resource kept because it was in use is queued again when its owner is discarded again", queuedAgain);
        Check("overlay queue: and handed out that time", due.Count == 1 && ReferenceEquals(due[0].Resource, renderTarget));
    }

    private static void EachResourceWaitsItsOwnDelay()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        object firstRenderTarget = new object();
        object secondRenderTarget = new object();
        queue.NoteReplacement("slot", new object(), new object(), firstRenderTarget, 100.0);
        RunUpdates(queue, 5, 104.0, NothingInUse);
        queue.NoteReplacement("slot", new object(), new object(), secondRenderTarget, 105.0);

        IReadOnlyList<DiscardedResourceQueue.PendingRelease> atEleven = RunUpdates(queue, 5, 111.0, NothingInUse);
        bool onlyFirstAtEleven = queue.HandedOutCount == 1 && queue.PendingCount == 1;
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> atSixteen = queue.TakeDue(116.0, NothingInUse);

        Check("overlay queue: the older resource comes out first, the newer one keeps waiting", onlyFirstAtEleven && atEleven.Count == 0,
            queue.HandedOutCount + "/" + queue.PendingCount + "/" + atEleven.Count);
        Check("overlay queue: the newer one comes out after its own delay", atSixteen.Count == 1 && ReferenceEquals(atSixteen[0].Resource, secondRenderTarget));
    }

    private static void AFullQueueCountsInsteadOfGrowing()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        int queued = 0;
        for (int index = 0; index < TestMaximumPending + 3; index++)
        {
            if (queue.NoteReplacement("slot", new object(), new object(), new object(), 0.0))
            {
                queued++;
            }
        }

        Check("overlay queue: the queue stops at its ceiling", queued == TestMaximumPending && queue.PendingCount == TestMaximumPending, queued + "/" + queue.PendingCount);
        Check("overlay queue: what did not fit is counted", queue.DroppedQueueFullCount == 3, queue.DroppedQueueFullCount.ToString());

        IReadOnlyList<DiscardedResourceQueue.PendingRelease> due = RunUpdates(queue, TestMinimumUpdates, 10.0, NothingInUse);
        bool roomAgain = queue.NoteReplacement("slot", new object(), new object(), new object(), 10.0);
        Check("overlay queue: a full queue hands everything out and then has room again", due.Count == TestMaximumPending && roomAgain, due.Count.ToString());
    }

    /// <summary>Queues an owner that nothing else refers to once this method has returned.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueueAnOwnerNobodyElseHolds(DiscardedResourceQueue queue, object renderTarget)
    {
        object owner = new object();
        queue.NoteReplacement("slot", owner, new object(), renderTarget, 100.0);
        return new WeakReference(owner);
    }

    private static void TheQueueDoesNotKeepTheDiscardedOwnerAlive()
    {
        DiscardedResourceQueue queue = NewTestQueue();
        object renderTarget = new object();
        WeakReference ownerReference = QueueAnOwnerNobodyElseHolds(queue, renderTarget);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        bool ownerCollectedWhileWaiting = !ownerReference.IsAlive;

        object ownerSeenByTheCheck = new object();
        object resourceSeenByTheCheck = null;
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> due = RunUpdates(queue, TestMinimumUpdates, 110.0, (owner, resource) =>
        {
            ownerSeenByTheCheck = owner;
            resourceSeenByTheCheck = resource;
            return false;
        });

        Check("overlay queue: the discarded owner (and the galaxy it points at) is collected while its resource waits", ownerCollectedWhileWaiting);
        Check("overlay queue: the resource is still held and still handed out", due.Count == 1 && ReferenceEquals(due[0].Resource, renderTarget));
        Check("overlay queue: a collected owner is handed out as null", due.Count == 1 && due[0].Owner is null);
        Check("overlay queue: the in-use check is given null for a collected owner, and the resource",
            ownerSeenByTheCheck is null && ReferenceEquals(resourceSeenByTheCheck, renderTarget));
    }

    private static void TheDefaultCeilingIsSmall()
    {
        Check("overlay queue: the default ceiling is 8, not 64", DiscardedResourceQueue.DefaultMaximumPending == 8,
            DiscardedResourceQueue.DefaultMaximumPending.ToString());
    }

    private static void InUseMeansTheSameObjectNotAnEqualOne()
    {
        string candidate = new string('a', 3);
        string equalButDifferent = new string('a', 3);
        object unrelated = new object();

        List<object> holdingAnEqualOne = new List<object> { unrelated, equalButDifferent };
        List<object> holdingTheSameOne = new List<object> { unrelated, candidate };

        Check("overlay queue: an equal object is not the same object", !DiscardedResourceQueue.IsAmong(candidate, holdingAnEqualOne));
        Check("overlay queue: the same object is found", DiscardedResourceQueue.IsAmong(candidate, holdingTheSameOne));
        Check("overlay queue: null is never in use", !DiscardedResourceQueue.IsAmong(null, new List<object> { null }));
        Check("overlay queue: nothing is in use when no renderer holds anything", !DiscardedResourceQueue.IsAmong(candidate, new List<object>()));
    }

    private static void OnlyZeroSwitchesTheReleaseOff()
    {
        Check("overlay switch: unset is on", DiscardedResourceQueue.EnabledFromSwitchText(null));
        Check("overlay switch: empty is on", DiscardedResourceQueue.EnabledFromSwitchText(""));
        Check("overlay switch: 1 is on", DiscardedResourceQueue.EnabledFromSwitchText("1"));
        Check("overlay switch: a word is on, so a typo cannot bring the leak back", DiscardedResourceQueue.EnabledFromSwitchText("off"));
        Check("overlay switch: 0 is off", !DiscardedResourceQueue.EnabledFromSwitchText("0"));
        Check("overlay switch: 0 with spaces is off", !DiscardedResourceQueue.EnabledFromSwitchText(" 0 "));
    }

    private static void TheLobbyLetsTheOffSwitchThrough()
    {
        List<string> inherited = new List<string>
        {
            "DW2MP_OVERLAY_RELEASE",
            "DW2MP_ADOPTION_MEMORY_PROBE",
        };

        List<string> stripped = LaunchEnvironment.NamesToStrip(inherited, developerMode: false);

        Check("lobby: the overlay off switch reaches a real session", !stripped.Contains("DW2MP_OVERLAY_RELEASE"), string.Join(",", stripped));
        Check("lobby: the memory probe beside it is still stripped", stripped.Contains("DW2MP_ADOPTION_MEMORY_PROBE") && stripped.Count == 1, string.Join(",", stripped));
    }
}
