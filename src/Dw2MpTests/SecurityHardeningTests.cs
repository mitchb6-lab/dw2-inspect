using Dw2MpLobby;
using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for the hardening that followed the 2026-11-10 security review of protocol 4:
/// the summary gate (H1), the wire limits (P1, M4), the clock sample check (M2), the failure
/// ledger (M3), the digest limits (M5), the adoption gate (M1), the relation list header, the
/// Population constructor order and the lobby's environment allow-list.
/// </summary>
public static partial class Program
{
    private static void RunSecurityHardeningTests()
    {
        AFloodOfSummariesIsOneCheckPerCall();
        TheGateWaitsForEarlierDeltasAndForASecond();

        CountsThatCannotBeTrueAreRefused();
        BlocksAreCappedAndCheckedAgainstTheFrame();
        NotANumberIsNotFinite();

        ClockSamplesMustBeRealAndInRange();
        AClockThatLeapsIsIgnoredUntilItInsists();

        TheFailureLedgerIsCappedAndCountsEverything();
        LogThrottleLogsTheFirstFewThenEveryNth();

        AnEmptyHostDigestIsNeverAMatch();
        NotSentYetIsCappedAndCannotBeClaimedForEver();
        DigestNamesArePlainOrRefused();

        UnsolicitedAdoptionsAreSpacedAndAskedForOnesAreNot();
        TheServedStateFloorIsWallClockSeconds();

        ARelationListMustBeForItsOwnEmpireAndFitItsBytes();
        PopulationConstructorArgumentsAreInDw2Order();
        TheLobbyStripsEveryDw2MpVariableItDoesNotSet();
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static BinaryReader ReaderOver(Action<BinaryWriter> write)
    {
        var buffer = new MemoryStream();
        var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true);
        write(writer);
        writer.Flush();
        buffer.Position = 0;
        return new BinaryReader(buffer, System.Text.Encoding.UTF8);
    }

    // ------------------------------------------------------------- H1: the summary gate

    private static void AFloodOfSummariesIsOneCheckPerCall()
    {
        var gate = new SummaryGate();
        for (int index = 0; index < 1000; index++)
        {
            gate.Offer(index, new[] { (byte)(index % 251) });
        }

        Check("gate: a flood of 1000 summaries keeps only the newest 32", gate.PendingCount == SummaryGate.MaxPending && gate.Dropped == 1000 - SummaryGate.MaxPending);

        int checksInOneCall = 0;
        byte[] first = gate.TakeNewestReady(deltasTaken: 1000, nowSeconds: 10.0);
        if (first is not null)
        {
            checksInOneCall++;
        }

        Check("gate: one call hands out exactly one summary to check", checksInOneCall == 1);
        Check("gate: and it is the newest one", first is not null && first[0] == (byte)(999 % 251));
        Check("gate: the older ready ones are discarded, not checked later", gate.PendingCount == 0 && gate.Superseded == SummaryGate.MaxPending - 1);

        int checksInTheNextHundredFrames = 0;
        for (int frame = 0; frame < 100; frame++)
        {
            gate.Offer(2000 + frame, new byte[] { 7 });
            byte[] taken = gate.TakeNewestReady(deltasTaken: 5000, nowSeconds: 10.0 + (frame * 0.009));
            if (taken is not null)
            {
                checksInTheNextHundredFrames++;
            }
        }

        Check("gate: a hundred frames inside the same second check nothing more", checksInTheNextHundredFrames == 0);
        Check("gate: after a second, one more", gate.TakeNewestReady(deltasTaken: 5000, nowSeconds: 11.0) is not null);
    }

    private static void TheGateWaitsForEarlierDeltasAndForASecond()
    {
        var gate = new SummaryGate();
        gate.Offer(deltasBefore: 5, new byte[] { 1 });
        gate.Offer(deltasBefore: 9, new byte[] { 2 });

        Check("gate: a summary whose earlier deltas are still queued is not taken", gate.TakeNewestReady(deltasTaken: 4, nowSeconds: 100) is null && gate.PendingCount == 2);

        byte[] ready = gate.TakeNewestReady(deltasTaken: 5, nowSeconds: 100);
        Check("gate: the ready one is taken and the later one stays", ready is not null && ready[0] == 1 && gate.PendingCount == 1);
        Check("gate: the later one is not taken within the same second", gate.TakeNewestReady(deltasTaken: 9, nowSeconds: 100.5) is null);

        byte[] later = gate.TakeNewestReady(deltasTaken: 9, nowSeconds: 101.0);
        Check("gate: and is taken a second later", later is not null && later[0] == 2);

        gate.Offer(1, new byte[] { 3 });
        gate.Clear();
        Check("gate: clear empties the queue", gate.PendingCount == 0);
    }

    // ------------------------------------------------------------- P1, M4: wire limits

    private static void CountsThatCannotBeTrueAreRefused()
    {
        Check("limits: a negative count is refused",
            Throws<InvalidDataException>(() => WireLimits.ReadCount(ReaderOver(writer => writer.Write(-1)), 4, -1, "test")));

        Check("limits: a count the remaining bytes cannot hold is refused",
            Throws<InvalidDataException>(() => WireLimits.ReadCount(ReaderOver(writer => { writer.Write(1000); writer.Write(new byte[40]); }), 4, -1, "test")));

        Check("limits: two billion records in a short frame are refused before any loop runs",
            Throws<InvalidDataException>(() => WireLimits.ReadCount(ReaderOver(writer => writer.Write(int.MaxValue)), 21, -1, "test")));

        Check("limits: a count far beyond the local collection is refused",
            Throws<InvalidDataException>(() => WireLimits.ReadCount(ReaderOver(writer => { writer.Write(6000); writer.Write(new byte[6000]); }), 1, 300, "test")));

        int accepted = WireLimits.ReadCount(ReaderOver(writer => { writer.Write(10); writer.Write(new byte[40]); }), 4, 12, "test");
        Check("limits: an honest count is read", accepted == 10);
    }

    private static void BlocksAreCappedAndCheckedAgainstTheFrame()
    {
        Check("limits: a zero-length block is 'nothing here'",
            WireLimits.ReadCappedBytes(ReaderOver(writer => writer.Write(0)), 100, "test") is null);

        Check("limits: a block over the cap is refused",
            Throws<InvalidDataException>(() => WireLimits.ReadCappedBytes(ReaderOver(writer => { writer.Write(101); writer.Write(new byte[101]); }), 100, "test")));

        Check("limits: a negative length is refused",
            Throws<InvalidDataException>(() => WireLimits.ReadCappedBytes(ReaderOver(writer => writer.Write(-7)), 100, "test")));

        Check("limits: a length longer than the frame is refused before allocating",
            Throws<EndOfStreamException>(() => WireLimits.ReadCappedBytes(ReaderOver(writer => { writer.Write(90); writer.Write(new byte[10]); }), 100, "test")));

        byte[] block = WireLimits.ReadCappedBytes(ReaderOver(writer => { writer.Write(3); writer.Write(new byte[] { 1, 2, 3 }); }), 100, "test");
        Check("limits: an honest block is read whole", block is not null && block.Length == 3 && block[2] == 3);
    }

    private static void NotANumberIsNotFinite()
    {
        Check("limits: NaN and both infinities are not finite",
            !WireLimits.IsFinite(double.NaN) && !WireLimits.IsFinite(float.PositiveInfinity) && !WireLimits.IsFinite(double.NegativeInfinity));
        Check("limits: float.MaxValue (the Independent empire's cash) is finite", WireLimits.IsFinite(float.MaxValue) && WireLimits.IsFinite(0));
    }

    // ------------------------------------------------------------- M2: clock samples

    private static readonly long SomeGameTime = new DateTime(2026, 11, 10).Ticks;

    private static void ClockSamplesMustBeRealAndInRange()
    {
        Check("clock: an ordinary sample is in range", ClockSampleCheck.InRange(SomeGameTime, 4f));
        Check("clock: paused (speed 0) is in range", ClockSampleCheck.InRange(SomeGameTime, 0f));
        Check("clock: zero or negative ticks are refused", !ClockSampleCheck.InRange(0, 1f) && !ClockSampleCheck.InRange(-5, 1f));
        Check("clock: ticks past DateTime's range are refused", !ClockSampleCheck.InRange(long.MaxValue, 1f) && !ClockSampleCheck.InRange(DateTime.MaxValue.Ticks, 1f));
        Check("clock: NaN, infinite and negative speeds are refused",
            !ClockSampleCheck.InRange(SomeGameTime, float.NaN) && !ClockSampleCheck.InRange(SomeGameTime, float.PositiveInfinity) && !ClockSampleCheck.InRange(SomeGameTime, -1f));
        Check("clock: a speed of a million is refused", !ClockSampleCheck.InRange(SomeGameTime, 1000000f));
    }

    private static void AClockThatLeapsIsIgnoredUntilItInsists()
    {
        var check = new ClockSampleCheck();
        long oneSecond = TimeSpan.TicksPerSecond;

        Check("clock: the first sample is believed", check.Accept(SomeGameTime, 4f, 0));
        Check("clock: four game seconds a second later is believed", check.Accept(SomeGameTime + (4 * oneSecond), 4f, 1.0));
        Check("clock: a year's leap is ignored", !check.Accept(SomeGameTime + (365L * 24 * 3600 * oneSecond), 4f, 1.0));
        Check("clock: an hour backwards is ignored", !check.Accept(SomeGameTime - (3600 * oneSecond), 4f, 1.0));
        Check("clock: an out-of-range sample is counted as ignored too", !check.Accept(SomeGameTime, float.NaN, 1.0) && check.Rejected == 3);
        Check("clock: the honest stream carries on", check.Accept(SomeGameTime + (8 * oneSecond), 4f, 1.0));

        long farAhead = SomeGameTime + (365L * 24 * 3600 * oneSecond);
        bool believedEarly = false;
        for (int sample = 0; sample < ClockSampleCheck.RejectionsBeforeBelieving; sample++)
        {
            believedEarly = believedEarly || check.Accept(farAhead + (sample * oneSecond), 1f, 1.0);
        }

        Check("clock: thirty samples that insist are still ignored one by one", !believedEarly);
        Check("clock: the thirty-first is believed, so a clock that really moved is followed", check.Accept(farAhead + (31 * oneSecond), 1f, 1.0));
    }

    // ------------------------------------------------------------- M3: the failure ledger

    private static void TheFailureLedgerIsCappedAndCountsEverything()
    {
        var ledger = new FailureLedger();
        bool firstIsNew = ledger.Note("Ship at read: EndOfStreamException");
        bool secondIsNew = ledger.Note("Ship at read: EndOfStreamException");

        Check("ledger: a key is new once", firstIsNew && !secondIsNew && ledger.DistinctCount == 1 && ledger.Total == 2);

        for (int index = 0; index < 1000; index++)
        {
            ledger.Note("peer-chosen text " + index);
        }

        Check("ledger: a thousand distinct keys leave at most 64 remembered", ledger.DistinctCount == FailureLedger.MaxDistinct);
        Check("ledger: every failure is still counted", ledger.Total == 1002);
        Check("ledger: past the cap nothing is 'new', so nothing more is logged", !ledger.Note("yet another"));
    }

    private static void LogThrottleLogsTheFirstFewThenEveryNth()
    {
        int logged = 0;
        for (long occurrence = 1; occurrence <= 1000; occurrence++)
        {
            if (LogThrottle.ShouldLog(occurrence, 5, 50))
            {
                logged++;
            }
        }

        Check("throttle: 1000 occurrences are 25 lines (5 first, then every 50th)", logged == 25, logged.ToString());
    }

    // ------------------------------------------------------------- M5: digest limits

    private static void AnEmptyHostDigestIsNeverAMatch()
    {
        var tolerance = new DesyncTolerance();
        SyncDigest mine = DigestOf(("ships", 10, 1UL, 0), ("colonies", 3, 2UL, 0));

        SyncCheckResult empty = tolerance.Evaluate(new SyncDigest(), mine, SameBaseline());
        Check("digest: an empty host digest is a mismatch, not a match", empty.Verdict == SyncVerdict.Mismatch, empty.Detail);
        Check("digest: the detail names what is missing", empty.Detail.Contains("ships is missing") && empty.Detail.Contains("colonies is missing"), empty.Detail);

        SyncCheckResult partial = tolerance.Evaluate(DigestOf(("ships", 10, 1UL, 0)), mine, SameBaseline());
        Check("digest: a host digest that leaves out one carried collection is a mismatch", partial.Verdict == SyncVerdict.Mismatch);

        tolerance.Evaluate(new SyncDigest(), mine, SameBaseline());
        Check("digest: three empty digests in a row are a sustained desync", tolerance.ConsecutiveMismatches == 3);
    }

    private static void NotSentYetIsCappedAndCannotBeClaimedForEver()
    {
        var capped = new DesyncTolerance();
        SyncDigest hostClaimingThousands = DigestOf(("ships", 510, 1UL, 100000));
        SyncDigest mine = DigestOf(("ships", 10, 2UL, 0));

        SyncCheckResult beyondCap = capped.Evaluate(hostClaimingThousands, mine, SameBaseline());
        Check("digest: 'not sent yet' excuses at most 32 members, whatever the host claims", beyondCap.Verdict == SyncVerdict.Mismatch, beyondCap.Detail);

        var limited = new DesyncTolerance();
        SyncDigest hostThreeUnsent = DigestOf(("ships", 13, 1UL, 3));
        int toleratedInARow = 0;
        SyncCheckResult last = null;
        for (int summary = 0; summary < DesyncTolerance.SummariesUnsentTolerated + 1; summary++)
        {
            last = limited.Evaluate(hostThreeUnsent, mine, SameBaseline());
            if (last.Verdict == SyncVerdict.Tolerated)
            {
                toleratedInARow++;
            }
        }

        Check("digest: 'not sent yet' is believed for three summaries in a row", toleratedInARow == DesyncTolerance.SummariesUnsentTolerated);
        Check("digest: on the fourth it is a mismatch", last.Verdict == SyncVerdict.Mismatch, last.Detail);

        limited.Evaluate(DigestOf(("ships", 10, 2UL, 0)), mine, SameBaseline());
        Check("digest: agreeing again gives the allowance back", limited.Evaluate(hostThreeUnsent, mine, SameBaseline()).Verdict == SyncVerdict.Tolerated);
    }

    private static void DigestNamesArePlainOrRefused()
    {
        Check("digest: lowercase letters and digits are a plain name", SyncDigest.IsPlainName("ships") && SyncDigest.IsPlainName("kind2"));
        Check("digest: a newline, a space, a brace or a capital is not", !SyncDigest.IsPlainName("ships\n# net: fake line") && !SyncDigest.IsPlainName("two words") && !SyncDigest.IsPlainName("{0}") && !SyncDigest.IsPlainName("Ships"));

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write("ships\n# desync: forged");
            writer.Write(1);
            writer.Write(0UL);
            writer.Write(0);
        }

        Check("digest: a digest whose name could forge a log line is refused on read", ReadDigestBytes(buffer.ToArray()) is null);
    }

    // ------------------------------------------------------------- M1: adoption and the floor

    private static void UnsolicitedAdoptionsAreSpacedAndAskedForOnesAreNot()
    {
        Check("adoption: a state the joiner asked for is adopted at once", AdoptionGate.MayAdoptNow(solicited: true, secondsSinceLastAdoption: 0.5));
        Check("adoption: an unsolicited state one second after the last waits", !AdoptionGate.MayAdoptNow(solicited: false, secondsSinceLastAdoption: 1));
        Check("adoption: and is adopted when the interval has passed", AdoptionGate.MayAdoptNow(solicited: false, secondsSinceLastAdoption: AdoptionGate.MinSecondsBetweenUnsolicited));
        Check("adoption: the first ever (no last adoption) is adopted", AdoptionGate.MayAdoptNow(solicited: false, secondsSinceLastAdoption: double.MaxValue));
        Check("adoption: the interval is inside the host's 60 s floor, so honest keyframes never wait", AdoptionGate.MinSecondsBetweenUnsolicited < FloorSeconds);
    }

    private static void TheServedStateFloorIsWallClockSeconds()
    {
        KeyframePolicy policy = PolicyWith(5, true);
        policy.NoteServed(100);

        // 59 seconds of wall time, during which the tick counter runs a long way: the floor
        // must not care how many ticks that was.
        policy.NoteTime(59, hostRunning: true);
        KeyframeDecision inside = policy.Decide(100 + 5000, clientAsked: true, desyncExemption: false);
        Check("floor: 5000 ticks but only 59 s after a full state, a request is still ignored", !inside.Serve && inside.RequestIgnored);

        policy.NoteTime(1, hostRunning: false);
        KeyframeDecision atFloor = policy.Decide(100 + 5001, clientAsked: true, desyncExemption: false);
        Check("floor: at 60 s it is served, paused or not", atFloor.Serve && atFloor.Reason == KeyframeReason.ClientAsked);
        Check("floor: the policy reports the wall time it measured", policy.WallSecondsSinceServed == 60);
    }

    // ------------------------------------------------------------- relations, population, lobby

    private static byte[] RelationListBytes(short listEmpireId, short relationCount, int bodyBytes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(listEmpireId);
            writer.Write(-1);
            writer.Write(relationCount);
            writer.Write(new byte[bodyBytes]);
        }

        return buffer.ToArray();
    }

    private static void ARelationListMustBeForItsOwnEmpireAndFitItsBytes()
    {
        Check("relations: a list for this empire whose count fits is sane", RelationListHeader.IsSane(RelationListBytes(3, 10, 10 * 200), expectedListEmpireId: 3));
        Check("relations: an empty list is sane", RelationListHeader.IsSane(RelationListBytes(3, 0, 0), expectedListEmpireId: 3));
        Check("relations: another empire's list is refused", !RelationListHeader.IsSane(RelationListBytes(4, 10, 10 * 200), expectedListEmpireId: 3));
        Check("relations: a negative count is refused", !RelationListHeader.IsSane(RelationListBytes(3, -1, 100), expectedListEmpireId: 3));
        Check("relations: a count the bytes cannot hold is refused", !RelationListHeader.IsSane(RelationListBytes(3, 4000, 500), expectedListEmpireId: 3));
        Check("relations: a count over the limit is refused", !RelationListHeader.IsSane(RelationListBytes(3, 5000, 5000 * 60), expectedListEmpireId: 3));
        Check("relations: fewer than eight bytes is refused", !RelationListHeader.IsSane(new byte[] { 3, 0, 0 }, expectedListEmpireId: 3) && !RelationListHeader.IsSane(null, 3));
    }

    private static void PopulationConstructorArgumentsAreInDw2Order()
    {
        // DW2: Population(Byte raceId, Int64 amount, Single reproductionRate, Single assimilation).
        object[] arguments = PopulationArguments.ForConstructor(raceId: 6, amount: 2000000000L, assimilation: 0.25f, reproductionRate: 1.5f);

        Check("population: four arguments, race then amount", arguments.Length == 4 && (byte)arguments[0] == 6 && (long)arguments[1] == 2000000000L);
        Check("population: reproduction rate is third", (float)arguments[2] == 1.5f);
        Check("population: assimilation is fourth", (float)arguments[3] == 0.25f);
    }

    private static void TheLobbyStripsEveryDw2MpVariableItDoesNotSet()
    {
        var inherited = new List<string>
        {
            "PATH",
            "DW2_PATH",
            "DW2MP_ROLE",
            "DW2MP_SESSION",
            "DW2MP_KEYFRAME_MINUTES",
            "DW2MP_FORCE_DESYNC_AT_CYCLE",
            "DW2MP_DESYNC_AUTO_CHOICE",
            "DW2MP_DIVERGENCE_DIR",
            "DW2MP_DIVERGENCE_EVERY",
            "DW2MP_CLIENT_SIMULATES",
            "DW2MP_MAX_TICKS_BETWEEN_STATES",
            "dw2mp_some_future_dev_flag",
        };

        List<string> stripped = LaunchEnvironment.NamesToStrip(inherited, developerMode: false);

        Check("lobby: every dev flag is stripped, including ones nobody listed",
            stripped.Contains("DW2MP_FORCE_DESYNC_AT_CYCLE") && stripped.Contains("DW2MP_DESYNC_AUTO_CHOICE") && stripped.Contains("DW2MP_DIVERGENCE_DIR")
            && stripped.Contains("DW2MP_DIVERGENCE_EVERY") && stripped.Contains("DW2MP_CLIENT_SIMULATES") && stripped.Contains("DW2MP_MAX_TICKS_BETWEEN_STATES")
            && stripped.Contains("dw2mp_some_future_dev_flag"), string.Join(",", stripped));
        Check("lobby: what the lobby sets, the keyframe interval and other programs' variables stay",
            !stripped.Contains("DW2MP_ROLE") && !stripped.Contains("DW2MP_SESSION") && !stripped.Contains("DW2MP_KEYFRAME_MINUTES") && !stripped.Contains("PATH") && !stripped.Contains("DW2_PATH"));
        Check("lobby: exactly seven are stripped", stripped.Count == 7, stripped.Count.ToString());
        Check("lobby: developer mode strips nothing", LaunchEnvironment.NamesToStrip(inherited, developerMode: true).Count == 0);
    }
}
