using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for the 2026-11-10 desync-reduction work: the sync digest and its tolerance
/// rules, the keyframe policy and its settings, big-event detection, the pause mirror, the
/// send thresholds and the divergence report. No game, no sockets.
/// </summary>
public static partial class Program
{
    private static void RunDesyncReductionTests()
    {
        DigestHashIgnoresOrderAndSeesMembership();
        DigestRoundTripsOnTheWire();
        DigestReaderRejectsHostileInput();

        ToleranceMatchesIdenticalDigests();
        ToleranceSkipsAKeyframeInTransit();
        ToleranceSkipsASupersededSummaryAndHeldDeltas();
        AKeyframeCanNeverStartExtendOrEndAMismatchRun();
        ToleranceAllowsAGapOfOneForOneSummaryOnly();
        ToleranceAllowsWhatTheHostHasNotSentYet();
        ToleranceCallsABigGapAMismatchAtOnce();
        ThreeMismatchesInARowAreSustainedAndAMatchBreaksTheRun();
        ATolerableSummaryNeitherCountsNorClears();
        TheOrbCountThatRaisedTheFalseDesyncIsNotCompared();
        ToleranceIgnoresACollectionOnlyOneSideKnows();

        KeyframeJoinIsNeverThrottled();
        KeyframeRequestInsideTheFloorIsIgnoredUnlessADesyncChoice();
        KeyframePeriodicCountsRunningTimeOnly();
        KeyframeEventWaitsForTheFloorAndIsNotLost();
        KeyframeNothingUnaskedBeforeAJoinerHasAState();
        KeyframeServingClearsEventsAndTheClock();
        KeyframePeriodicCanBeSwitchedOff();
        KeyframeSafetyIntervalStillServes();

        SettingsDefaultToThreeMinutes();
        SettingsReadTheFileAndTheOverride();
        SettingsClampAndSurviveGarbage();

        EventsDetectColoniesGainedLostAndTaken();
        EventsDetectWarPeaceAndFirstContactOncePerPair();
        EventsDetectAnEliminatedEmpire();
        EventsAreEmptyWhenNothingChanged();

        PauseMirrorFollowsTheHost();

        ThresholdsIgnoreNoiseAndSeeRealChange();

        DivergenceReportRanksDifferingFields();
        DivergenceReportToleratesRoundingAndCountsMembership();
    }

    // ------------------------------------------------------------- digest

    private static SyncDigest DigestOf(params (string Name, int Count, ulong Hash, int Unsent)[] entries)
    {
        var digest = new SyncDigest();
        foreach (var entry in entries)
        {
            digest.Add(entry.Name, entry.Count, entry.Hash, entry.Unsent);
        }

        return digest;
    }

    private static SyncCheckContext SameBaseline()
    {
        return new SyncCheckContext { SummaryBaseline = 3, AppliedBaseline = 3 };
    }

    private static void DigestHashIgnoresOrderAndSeesMembership()
    {
        ulong forwards = SyncDigest.EmptySetHash;
        forwards = SyncDigest.Fold(forwards, 1);
        forwards = SyncDigest.Fold(forwards, 2);
        forwards = SyncDigest.Fold(forwards, 3);

        ulong backwards = SyncDigest.EmptySetHash;
        backwards = SyncDigest.Fold(backwards, 3);
        backwards = SyncDigest.Fold(backwards, 1);
        backwards = SyncDigest.Fold(backwards, 2);

        ulong different = SyncDigest.EmptySetHash;
        different = SyncDigest.Fold(different, 1);
        different = SyncDigest.Fold(different, 2);
        different = SyncDigest.Fold(different, 4);

        ulong swapped = SyncDigest.EmptySetHash;
        swapped = SyncDigest.Fold(swapped, 0);
        swapped = SyncDigest.Fold(swapped, 6);

        Check("digest: the same ids in any order hash the same", forwards == backwards);
        Check("digest: a different member changes the hash", forwards != different);
        Check("digest: {0,6} does not collide with {1,2,3} (ids are mixed, not summed)", swapped != forwards);
        Check("digest: negative ids fold without throwing", SyncDigest.Fold(SyncDigest.EmptySetHash, -1) != SyncDigest.EmptySetHash);
    }

    private static void DigestRoundTripsOnTheWire()
    {
        SyncDigest sent = DigestOf(("ships", 282, 0xDEADBEEFCAFEF00DUL, 2), ("colonies", 12, 7UL, 0));

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            sent.Write(writer);
        }

        buffer.Position = 0;
        using var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8);
        SyncDigest received = SyncDigest.TryRead(reader);

        Check("digest: round trip keeps both entries", received is not null && received.Entries.Count == 2);
        Check("digest: round trip keeps count, hash and unsent",
            received.Find("ships").Count == 282 && received.Find("ships").SetHash == 0xDEADBEEFCAFEF00DUL && received.Find("ships").Unsent == 2);
        Check("digest: describe names every collection", sent.Describe() == "ships=282:CAFEF00D colonies=12:00000007", sent.Describe());
    }

    private static void DigestReaderRejectsHostileInput()
    {
        Check("digest: an empty stream is not a digest", ReadDigestBytes(Array.Empty<byte>()) is null);
        Check("digest: 200 entries are refused before any is read", ReadDigestBytes(new byte[] { 200 }) is null);
        Check("digest: a truncated entry is refused", ReadDigestBytes(new byte[] { 1, 5, (byte)'s', (byte)'h' }) is null);

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write("ships");
            writer.Write(-5);
            writer.Write(0UL);
            writer.Write(0);
        }

        Check("digest: a negative count is refused", ReadDigestBytes(buffer.ToArray()) is null);

        using var longName = new MemoryStream();
        using (var writer = new BinaryWriter(longName, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(new string('x', SyncDigest.MaxNameLength + 1));
            writer.Write(1);
            writer.Write(0UL);
            writer.Write(0);
        }

        Check("digest: an over-long name is refused", ReadDigestBytes(longName.ToArray()) is null);
    }

    private static SyncDigest ReadDigestBytes(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);
        return SyncDigest.TryRead(reader);
    }

    // ------------------------------------------------------------- tolerance

    private static void ToleranceMatchesIdenticalDigests()
    {
        var tolerance = new DesyncTolerance();
        SyncCheckResult result = tolerance.Evaluate(DigestOf(("ships", 10, 99UL, 0)), DigestOf(("ships", 10, 99UL, 0)), SameBaseline());

        Check("tolerance: identical digests match", result.Verdict == SyncVerdict.Match && result.ConsecutiveMismatches == 0);
    }

    private static void ToleranceSkipsAKeyframeInTransit()
    {
        var tolerance = new DesyncTolerance();
        var context = new SyncCheckContext { SummaryBaseline = 4, AppliedBaseline = 3 };
        SyncCheckResult result = tolerance.Evaluate(DigestOf(("ships", 50, 1UL, 0)), DigestOf(("ships", 10, 2UL, 0)), context);

        Check("tolerance: a summary for a keyframe still in transit is not compared", result.Verdict == SyncVerdict.NotComparable, result.Detail);
        Check("tolerance: and it is not a mismatch", result.ConsecutiveMismatches == 0 && !result.Sustained);
    }

    private static void ToleranceSkipsASupersededSummaryAndHeldDeltas()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 50, 1UL, 0));
        SyncDigest mine = DigestOf(("ships", 10, 2UL, 0));

        var superseded = new SyncCheckContext { SummaryBaseline = 2, AppliedBaseline = 3 };
        var held = new SyncCheckContext { SummaryBaseline = 3, AppliedBaseline = 3, DeltasHeld = 4 };
        var queued = new SyncCheckContext { SummaryBaseline = 3, AppliedBaseline = 3, FullStateQueued = true };

        Check("tolerance: a summary older than the adopted state is not compared", tolerance.Evaluate(host, mine, superseded).Verdict == SyncVerdict.NotComparable);
        Check("tolerance: held deltas mean not comparable", tolerance.Evaluate(host, mine, held).Verdict == SyncVerdict.NotComparable);
        Check("tolerance: a queued full state means not comparable", tolerance.Evaluate(host, mine, queued).Verdict == SyncVerdict.NotComparable);
        Check("tolerance: a missing digest means not comparable", tolerance.Evaluate(null, mine, SameBaseline()).Verdict == SyncVerdict.NotComparable);
    }

    private static void AKeyframeCanNeverStartExtendOrEndAMismatchRun()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 50, 1UL, 0));
        SyncDigest mine = DigestOf(("ships", 10, 2UL, 0));
        var inTransit = new SyncCheckContext { SummaryBaseline = 4, AppliedBaseline = 3 };

        tolerance.Evaluate(host, mine, SameBaseline());
        tolerance.Evaluate(host, mine, SameBaseline());
        SyncCheckResult duringKeyframe = tolerance.Evaluate(host, mine, inTransit);

        Check("tolerance: a keyframe in transit does not extend a run of two to three", duringKeyframe.ConsecutiveMismatches == 2 && !duringKeyframe.Sustained);

        for (int index = 0; index < 20; index++)
        {
            tolerance.Evaluate(host, mine, inTransit);
        }

        Check("tolerance: twenty summaries during a keyframe still raise nothing", tolerance.ConsecutiveMismatches == 2);

        var afterKeyframe = new SyncCheckContext { SummaryBaseline = 4, AppliedBaseline = 4 };
        SyncCheckResult stillWrong = tolerance.Evaluate(host, mine, afterKeyframe);
        Check("tolerance: a mismatch that survives the keyframe is the third and is sustained", stillWrong.BecameSustained && stillWrong.Sustained);
    }

    private static void ToleranceAllowsAGapOfOneForOneSummaryOnly()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 11, 1UL, 0));
        SyncDigest mine = DigestOf(("ships", 10, 2UL, 0));

        SyncCheckResult first = tolerance.Evaluate(host, mine, SameBaseline());
        SyncCheckResult second = tolerance.Evaluate(host, mine, SameBaseline());

        Check("tolerance: a ship-count gap of one is tolerated for one summary", first.Verdict == SyncVerdict.Tolerated && first.ConsecutiveMismatches == 0, first.Detail);
        Check("tolerance: the same gap on the next summary is a mismatch", second.Verdict == SyncVerdict.Mismatch && second.ConsecutiveMismatches == 1, second.Detail);

        tolerance.Evaluate(DigestOf(("ships", 11, 1UL, 0)), DigestOf(("ships", 11, 1UL, 0)), SameBaseline());
        SyncCheckResult afterAgreement = tolerance.Evaluate(host, mine, SameBaseline());
        Check("tolerance: after agreeing again, a new gap of one gets its one summary back", afterAgreement.Verdict == SyncVerdict.Tolerated);
    }

    private static void ToleranceAllowsWhatTheHostHasNotSentYet()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 13, 1UL, 3));
        SyncDigest mine = DigestOf(("ships", 10, 2UL, 0));

        SyncCheckResult first = tolerance.Evaluate(host, mine, SameBaseline());
        SyncCheckResult second = tolerance.Evaluate(host, mine, SameBaseline());

        Check("tolerance: three ships the host has not sent yet are latency", first.Verdict == SyncVerdict.Tolerated, first.Detail);
        Check("tolerance: and stay latency while the host still says so", second.Verdict == SyncVerdict.Tolerated);

        SyncCheckResult beyond = tolerance.Evaluate(DigestOf(("ships", 16, 1UL, 3)), mine, SameBaseline());
        Check("tolerance: a gap of six with three unsent is a mismatch", beyond.Verdict == SyncVerdict.Mismatch, beyond.Detail);
    }

    private static void ToleranceCallsABigGapAMismatchAtOnce()
    {
        var tolerance = new DesyncTolerance();
        SyncCheckResult result = tolerance.Evaluate(DigestOf(("ships", 20, 1UL, 0)), DigestOf(("ships", 10, 2UL, 0)), SameBaseline());

        Check("tolerance: ten missing ships are a mismatch on the first summary", result.Verdict == SyncVerdict.Mismatch && result.ConsecutiveMismatches == 1);
        Check("tolerance: one mismatch is not yet a desync", !result.Sustained && !result.BecameSustained);
        Check("tolerance: the detail names the collection and both counts", result.Detail.Contains("ships host 20 mine 10"), result.Detail);
    }

    private static void ThreeMismatchesInARowAreSustainedAndAMatchBreaksTheRun()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 20, 1UL, 0), ("colonies", 5, 5UL, 0));
        SyncDigest wrong = DigestOf(("ships", 10, 2UL, 0), ("colonies", 5, 5UL, 0));
        SyncDigest right = DigestOf(("ships", 20, 1UL, 0), ("colonies", 5, 5UL, 0));

        SyncCheckResult first = tolerance.Evaluate(host, wrong, SameBaseline());
        SyncCheckResult second = tolerance.Evaluate(host, wrong, SameBaseline());
        SyncCheckResult match = tolerance.Evaluate(host, right, SameBaseline());

        Check("tolerance: two mismatches are not sustained", !first.Sustained && !second.Sustained && second.ConsecutiveMismatches == 2);
        Check("tolerance: a match clears the run", match.Verdict == SyncVerdict.Match && match.ConsecutiveMismatches == 0);

        tolerance.Evaluate(host, wrong, SameBaseline());
        tolerance.Evaluate(host, wrong, SameBaseline());
        SyncCheckResult third = tolerance.Evaluate(host, wrong, SameBaseline());
        SyncCheckResult fourth = tolerance.Evaluate(host, wrong, SameBaseline());

        Check("tolerance: the third mismatch in a row becomes sustained", third.BecameSustained && third.Sustained && third.ConsecutiveMismatches == 3);
        Check("tolerance: the fourth is still sustained but does not raise the popup again", fourth.Sustained && !fourth.BecameSustained);

        tolerance.Reset();
        Check("tolerance: reset forgets the run", tolerance.ConsecutiveMismatches == 0);
    }

    private static void ATolerableSummaryNeitherCountsNorClears()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 20, 1UL, 0), ("colonies", 5, 5UL, 0));
        SyncDigest wrong = DigestOf(("ships", 10, 2UL, 0), ("colonies", 5, 5UL, 0));
        SyncDigest offByOneColony = DigestOf(("ships", 20, 1UL, 0), ("colonies", 4, 9UL, 0));

        tolerance.Evaluate(host, wrong, SameBaseline());
        tolerance.Evaluate(host, wrong, SameBaseline());
        SyncCheckResult tolerated = tolerance.Evaluate(host, offByOneColony, SameBaseline());

        Check("tolerance: a tolerated summary leaves a run of two at two", tolerated.Verdict == SyncVerdict.Tolerated && tolerated.ConsecutiveMismatches == 2);
    }

    private static void TheOrbCountThatRaisedTheFalseDesyncIsNotCompared()
    {
        // 2026-11-10: every carried collection identical, Orbs 1,401 on the host and 1,400 on
        // the joiner. The old fingerprint hashed the orb count in; the digest has no orb entry.
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 282, 11UL, 0), ("colonies", 12, 12UL, 0), ("designs", 669, 13UL, 0), ("characters", 12, 14UL, 0), ("fleets", 0, 0UL, 0));
        SyncDigest mine = DigestOf(("ships", 282, 11UL, 0), ("colonies", 12, 12UL, 0), ("designs", 669, 13UL, 0), ("characters", 12, 14UL, 0), ("fleets", 0, 0UL, 0));

        bool allMatched = true;
        for (int summary = 0; summary < 10; summary++)
        {
            SyncCheckResult result = tolerance.Evaluate(host, mine, SameBaseline());
            allMatched = allMatched && result.Verdict == SyncVerdict.Match;
        }

        Check("tolerance: the 2026-11-10 false desync (orbs 1401 vs 1400, all else equal) is ten matches", allMatched);
    }

    private static void ToleranceIgnoresACollectionOnlyOneSideKnows()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest host = DigestOf(("ships", 10, 1UL, 0), ("orbs", 1401, 77UL, 0));
        SyncDigest mine = DigestOf(("ships", 10, 1UL, 0));

        Check("tolerance: a collection this build does not carry is not compared", tolerance.Evaluate(host, mine, SameBaseline()).Verdict == SyncVerdict.Match);
    }

    // ------------------------------------------------------------- keyframes

    private const long FloorTicks = 600;
    private const long SafetyTicks = 36000;

    private static KeyframePolicy PolicyWith(double minutes, bool onEvents)
    {
        var settings = new SyncSettings { KeyframeMinutes = minutes, KeyframeOnEvents = onEvents };
        return new KeyframePolicy(settings, FloorTicks, SafetyTicks);
    }

    private static void KeyframeJoinIsNeverThrottled()
    {
        KeyframePolicy policy = PolicyWith(3, true);
        KeyframeDecision decision = policy.Decide(5, clientAsked: true, desyncExemption: false);

        Check("keyframe: the join bootstrap is served at once", decision.Serve && decision.Reason == KeyframeReason.ClientAsked && !decision.RequestIgnored);
    }

    private static void KeyframeRequestInsideTheFloorIsIgnoredUnlessADesyncChoice()
    {
        KeyframePolicy policy = PolicyWith(3, true);
        policy.NoteServed(100);

        KeyframeDecision inside = policy.Decide(400, clientAsked: true, desyncExemption: false);
        KeyframeDecision exempt = policy.Decide(400, clientAsked: true, desyncExemption: true);
        KeyframeDecision outside = policy.Decide(100 + FloorTicks, clientAsked: true, desyncExemption: false);

        Check("keyframe: a request inside the floor is ignored", !inside.Serve && inside.RequestIgnored);
        Check("keyframe: a desync choice is served inside the floor", exempt.Serve && exempt.Reason == KeyframeReason.DesyncChoice);
        Check("keyframe: a request at the floor is served", outside.Serve && outside.Reason == KeyframeReason.ClientAsked);
    }

    private static void KeyframePeriodicCountsRunningTimeOnly()
    {
        KeyframePolicy policy = PolicyWith(3, true);
        policy.NoteServed(0);

        policy.NoteTime(179, hostRunning: true);
        Check("keyframe: 179 s of running game is not yet three minutes", !policy.Decide(5000, false, false).Serve);

        policy.NoteTime(600, hostRunning: false);
        Check("keyframe: ten minutes PAUSED do not count", !policy.Decide(20000, false, false).Serve);

        policy.NoteTime(1, hostRunning: true);
        KeyframeDecision due = policy.Decide(20001, false, false);
        Check("keyframe: the 180th running second serves a periodic keyframe", due.Serve && due.Reason == KeyframeReason.Periodic);

        policy.NoteServed(20001);
        Check("keyframe: serving restarts the three minutes", !policy.Decide(20002 + FloorTicks, false, false).Serve && policy.RunningSecondsSinceServed == 0);
    }

    private static void KeyframeEventWaitsForTheFloorAndIsNotLost()
    {
        KeyframePolicy policy = PolicyWith(3, true);
        policy.NoteServed(1000);
        policy.NoteEvent("war declared between empires 1 and 4");

        KeyframeDecision inside = policy.Decide(1200, false, false);
        KeyframeDecision atFloor = policy.Decide(1000 + FloorTicks, false, false);

        Check("keyframe: an event inside the floor waits", !inside.Serve && policy.PendingEventCount == 1);
        Check("keyframe: and is served when the floor has passed", atFloor.Serve && atFloor.Reason == KeyframeReason.Event);
        Check("keyframe: the reason names the event", atFloor.Description == "war declared between empires 1 and 4", atFloor.Description);
    }

    private static void KeyframeNothingUnaskedBeforeAJoinerHasAState()
    {
        KeyframePolicy policy = PolicyWith(3, true);
        policy.NoteEvent("colony 3 gained by empire 1");
        policy.NoteTime(10000, hostRunning: true);

        Check("keyframe: events before the join are not queued", policy.PendingEventCount == 0);
        Check("keyframe: no periodic keyframe before a joiner has a state", !policy.Decide(5000, false, false).Serve);
    }

    private static void KeyframeServingClearsEventsAndTheClock()
    {
        KeyframePolicy policy = PolicyWith(3, true);
        policy.NoteServed(0);
        policy.NoteEvent("peace made between empires 1 and 4");
        policy.NoteEvent("first contact between empires 1 and 6");
        policy.NoteTime(500, hostRunning: true);

        KeyframeDecision both = policy.Decide(FloorTicks, false, false);
        Check("keyframe: two events share one keyframe", both.Reason == KeyframeReason.Event && both.Description.Contains("peace made") && both.Description.Contains("first contact"), both.Description);

        policy.NoteServed(FloorTicks);
        Check("keyframe: a served state carries every pending event", policy.PendingEventCount == 0 && !policy.Decide(FloorTicks * 2, false, false).Serve);

        for (int index = 0; index < 50; index++)
        {
            policy.NoteEvent("event " + index);
        }

        Check("keyframe: pending events are capped", policy.PendingEventCount == KeyframePolicy.MaxPendingEvents);

        KeyframePolicy eventsOff = PolicyWith(3, false);
        eventsOff.NoteServed(0);
        eventsOff.NoteEvent("war declared between empires 1 and 4");
        Check("keyframe: event keyframes can be switched off", eventsOff.PendingEventCount == 0);
    }

    private static void KeyframePeriodicCanBeSwitchedOff()
    {
        KeyframePolicy policy = PolicyWith(0, true);
        policy.NoteServed(0);
        policy.NoteTime(100000, hostRunning: true);

        Check("keyframe: an interval of 0 means no periodic keyframes", !policy.Decide(SafetyTicks - 1, false, false).Serve);
    }

    private static void KeyframeSafetyIntervalStillServes()
    {
        KeyframePolicy policy = PolicyWith(0, false);
        policy.NoteServed(0);

        KeyframeDecision decision = policy.Decide(SafetyTicks, false, false);
        Check("keyframe: the safety interval still serves", decision.Serve && decision.Reason == KeyframeReason.SafetyInterval);
    }

    // ------------------------------------------------------------- settings

    private static void SettingsDefaultToThreeMinutes()
    {
        SyncSettings settings = SyncSettings.Parse("", "", out string note);

        Check("settings: the default interval is 3 minutes", settings.KeyframeMinutes == 3 && settings.KeyframeSeconds == 180);
        Check("settings: event keyframes are on by default", settings.KeyframeOnEvents);
        Check("settings: the note says defaults", note == "defaults", note);
    }

    private static void SettingsReadTheFileAndTheOverride()
    {
        SyncSettings fromFile = SyncSettings.Parse("{ \"keyframeMinutes\": 5, \"keyframeOnEvents\": false }", "", out _);
        SyncSettings overridden = SyncSettings.Parse("{ \"keyframeMinutes\": 5 }", "2.5", out string note);

        Check("settings: the file sets the interval and the event switch", fromFile.KeyframeMinutes == 5 && !fromFile.KeyframeOnEvents);
        Check("settings: the environment overrides the file", overridden.KeyframeMinutes == 2.5, note);
    }

    private static void SettingsClampAndSurviveGarbage()
    {
        SyncSettings garbage = SyncSettings.Parse("{ not json", "", out string garbageNote);
        SyncSettings tooSmall = SyncSettings.Parse("", "0.1", out _);
        SyncSettings tooBig = SyncSettings.Parse("", "5000", out _);
        SyncSettings off = SyncSettings.Parse("", "0", out _);
        SyncSettings negative = SyncSettings.Parse("{ \"keyframeMinutes\": -4 }", "", out _);
        SyncSettings notNumber = SyncSettings.Parse("", "soon", out string notNumberNote);

        Check("settings: invalid JSON gives the defaults and says so", garbage.KeyframeMinutes == 3 && garbageNote.Contains("not valid JSON"), garbageNote);
        Check("settings: under a minute is raised to one", tooSmall.KeyframeMinutes == 1);
        Check("settings: over two hours is lowered to 120", tooBig.KeyframeMinutes == 120);
        Check("settings: zero switches periodic keyframes off", off.KeyframeMinutes == 0);
        Check("settings: a negative interval also means off", negative.KeyframeMinutes == 0);
        Check("settings: a non-numeric override is ignored", notNumber.KeyframeMinutes == 3 && notNumberNote.Contains("not a number"), notNumberNote);
    }

    // ------------------------------------------------------------- events

    private static void EventsDetectColoniesGainedLostAndTaken()
    {
        var before = new GalaxyEventCensus();
        before.ColonyOwners[1] = 2;
        before.ColonyOwners[5] = 2;
        before.ColonyOwners[7] = 2;

        var after = new GalaxyEventCensus();
        after.ColonyOwners[1] = 2;
        after.ColonyOwners[7] = 9;
        after.ColonyOwners[8] = 2;

        List<string> events = GalaxyEventDetector.Detect(before, after);

        Check("events: a new colony is gained", events.Contains("colony 8 gained by empire 2"), string.Join(" | ", events));
        Check("events: a vanished colony is lost", events.Contains("colony 5 lost by empire 2"));
        Check("events: a colony that changed hands is lost to the taker", events.Contains("colony 7 lost by empire 2 to empire 9"));
        Check("events: an unchanged colony is not an event", events.Count == 3);
    }

    private static void EventsDetectWarPeaceAndFirstContactOncePerPair()
    {
        var before = new GalaxyEventCensus();
        before.Relations[(1, 4)] = new RelationFacts(haveMet: true, atWar: false);
        before.Relations[(4, 1)] = new RelationFacts(haveMet: true, atWar: false);
        before.Relations[(1, 5)] = new RelationFacts(haveMet: true, atWar: true);
        before.Relations[(1, 6)] = new RelationFacts(haveMet: false, atWar: false);

        var after = new GalaxyEventCensus();
        after.Relations[(1, 4)] = new RelationFacts(haveMet: true, atWar: true);
        after.Relations[(4, 1)] = new RelationFacts(haveMet: true, atWar: true);
        after.Relations[(1, 5)] = new RelationFacts(haveMet: true, atWar: false);
        after.Relations[(1, 6)] = new RelationFacts(haveMet: true, atWar: false);
        after.Relations[(1, 7)] = new RelationFacts(haveMet: true, atWar: false);

        List<string> events = GalaxyEventDetector.Detect(before, after);

        Check("events: a war seen from both sides is one event", events.Count(text => text == "war declared between empires 1 and 4") == 1, string.Join(" | ", events));
        Check("events: peace is detected", events.Contains("peace made between empires 1 and 5"));
        Check("events: first contact is detected on an existing relation", events.Contains("first contact between empires 1 and 6"));
        Check("events: first contact is detected on a relation that just appeared", events.Contains("first contact between empires 1 and 7"));
        Check("events: nothing else", events.Count == 4);
    }

    private static void EventsDetectAnEliminatedEmpire()
    {
        var before = new GalaxyEventCensus();
        before.ActiveEmpires.Add(1);
        before.ActiveEmpires.Add(3);

        var after = new GalaxyEventCensus();
        after.ActiveEmpires.Add(1);

        List<string> events = GalaxyEventDetector.Detect(before, after);
        Check("events: an empire that stopped being active is eliminated", events.Count == 1 && events[0] == "empire 3 eliminated", string.Join(" | ", events));
    }

    private static void EventsAreEmptyWhenNothingChanged()
    {
        var census = new GalaxyEventCensus();
        census.ColonyOwners[1] = 2;
        census.ActiveEmpires.Add(2);
        census.Relations[(2, 3)] = new RelationFacts(haveMet: true, atWar: true);

        Check("events: the same census twice is no event", GalaxyEventDetector.Detect(census, census).Count == 0);
        Check("events: no previous census is no event", GalaxyEventDetector.Detect(null, census).Count == 0);
    }

    // ------------------------------------------------------------- pause

    private static void PauseMirrorFollowsTheHost()
    {
        Check("pause: host running, joiner auto-paused by DW2 -> resume", PauseMirror.Decide(1f, locallyPaused: true) == PauseMirrorAction.ResumeLocally);
        Check("pause: host running at 4x, joiner paused -> resume", PauseMirror.Decide(4f, locallyPaused: true) == PauseMirrorAction.ResumeLocally);
        Check("pause: host paused, joiner running -> pause", PauseMirror.Decide(0f, locallyPaused: false) == PauseMirrorAction.PauseLocally);
        Check("pause: both running -> nothing", PauseMirror.Decide(2f, locallyPaused: false) == PauseMirrorAction.None);
        Check("pause: both paused -> nothing", PauseMirror.Decide(0f, locallyPaused: true) == PauseMirrorAction.None);
    }

    // ------------------------------------------------------------- thresholds

    private static void ThresholdsIgnoreNoiseAndSeeRealChange()
    {
        Check("thresholds: a shield flicker of 0.1 is not sent", !SyncThresholds.VitalDiffers(0.3, 0.4));
        Check("thresholds: 1 % of a big shield is not sent", !SyncThresholds.VitalDiffers(1000, 1010));
        Check("thresholds: 5 % of a shield is sent", SyncThresholds.VitalDiffers(1000, 1050));
        Check("thresholds: fuel running out is sent", SyncThresholds.VitalDiffers(600, 0));
        Check("thresholds: a few thousand people on two billion are not sent", !SyncThresholds.PopulationDiffers(2000000000, 2000003000));
        Check("thresholds: half a percent of a population is sent", SyncThresholds.PopulationDiffers(2000000000, 2010000000));
        Check("thresholds: a tax rate moving a point is sent", SyncThresholds.ColonyRateDiffers(0.12, 0.13));
        Check("thresholds: a credit of cash on a million is not sent", !SyncThresholds.MoneyDiffers(1000000, 1000001.5));
        Check("thresholds: one percent of cash is sent", SyncThresholds.MoneyDiffers(1000000, 1010000));
        Check("thresholds: never-sent (NaN) always differs", SyncThresholds.Differs(double.NaN, 5, 1, 0.1));
    }

    // ------------------------------------------------------------- divergence report

    private const string HostDump =
        "# side=host hostTick=600\n" +
        "Ship\t1\tFuel._value=600\tName=\"A\"\n" +
        "Ship\t2\tFuel._value=300\tName=\"B\"\n" +
        "Ship\t3\tFuel._value=100\tName=\"C\"\n" +
        "Colony\t0\tpop.total=2014463920\tTaxRate=0.12\n";

    private const string ClientDump =
        "# side=client hostTick=600\n" +
        "Ship\t1\tFuel._value=600.0001\tName=\"A\"\n" +
        "Ship\t2\tFuel._value=250\tName=\"B\"\n" +
        "Ship\t4\tFuel._value=100\tName=\"D\"\n" +
        "Colony\t0\tpop.total=2006948958\tTaxRate=0.13\n";

    private static void DivergenceReportRanksDifferingFields()
    {
        List<DivergentKind> kinds = DivergenceReport.Compare(HostDump, ClientDump, DivergenceReport.DefaultRelativeTolerance);
        DivergentKind colony = kinds.First(kind => kind.Kind == "Colony");

        Check("report: both colony fields differ", colony.CommonObjectsDiffering == 1 && colony.Fields.Count == 2);
        Check("report: the example carries both values", colony.Fields.Any(field => field.Field == "TaxRate" && field.ExampleHost == "0.12" && field.ExampleClient == "0.13"));
        Check("report: the text names the kind and the field", DivergenceReport.Format(kinds, 5).Contains("pop.total"));
    }

    private static void DivergenceReportToleratesRoundingAndCountsMembership()
    {
        List<DivergentKind> kinds = DivergenceReport.Compare(HostDump, ClientDump, DivergenceReport.DefaultRelativeTolerance);
        DivergentKind ship = kinds.First(kind => kind.Kind == "Ship");

        Check("report: ship 3 is only on the host, ship 4 only on the client", ship.OnlyOnHost == 1 && ship.OnlyOnClient == 1 && ship.CommonObjects == 2);
        Check("report: a rounding error in fuel is not a difference", ship.CommonObjectsDiffering == 1 && ship.Fields.Count == 1 && ship.Fields[0].ExampleKey == "2");
        Check("report: text that differs is a difference", !DivergenceReport.ValuesAgree("\"A\"", "\"B\"", 0.001));
        Check("report: comment lines and empty input are ignored", DivergenceReport.Parse("# only a comment\n").Count == 0 && DivergenceReport.Parse("").Count == 0);
    }
}
