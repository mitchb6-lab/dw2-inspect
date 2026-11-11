using Dw2MpLobby;
using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for the fixes that followed the security review of the adoption-memory and
/// overlay-release work (2026-11-11): one-shot exemptions from the adoption wait, a settled
/// reading that cannot be pushed back for ever, a budget notice that is owed until it was really
/// shown, the known-build check, and the lobby's handling of the memory variables.
/// </summary>
public static partial class Program
{
    private static void RunMemoryHardeningTests()
    {
        TheJoinIsSolicitedOnce();
        ARequestIsGoodForOneAdoption();
        TwoRequestsBeforeOneStateAreOneExemption();
        AHostCannotStreamStatesDuringAResync();

        APendingSettledReadingIsNotPushedBack();
        AfterAReadingTheNewestAdoptionGetsItsOwn();
        AFloodOfAdoptionsStillGetsReadings();

        ANoticeThatCouldNotBeShownIsStillOwed();
        ANoticeIsNotOwedOnceTheGrowthIsGone();
        AnUnrelatedNoticeWaitsForADesyncIncidentToClose();

        TheBudgetOverrideNeedsDeveloperMode();
        OnlyTheBuildTheProofWasReadFromIsKnown();

        TheLobbyStripsTheMemoryDevFlags();
        TheLobbySaysWhenItPassesTheOffSwitchOn();
    }

    // ------------------------------------------------------------------ M1: one-shot exemptions

    private static void TheJoinIsSolicitedOnce()
    {
        SolicitedStateExemptions exemptions = new SolicitedStateExemptions();

        bool beforeAnyState = exemptions.NextStateIsSolicited;
        exemptions.SpendOnAdoptionAttempt();
        bool afterTheFirstState = exemptions.NextStateIsSolicited;

        Check("solicited: the first state after the join is solicited", beforeAnyState);
        Check("solicited: the second is not, although nothing was asked for in between", !afterTheFirstState);
    }

    private static void ARequestIsGoodForOneAdoption()
    {
        SolicitedStateExemptions exemptions = new SolicitedStateExemptions();
        exemptions.SpendOnAdoptionAttempt();

        exemptions.NoteStateRequested();
        bool afterTheRequest = exemptions.NextStateIsSolicited;
        exemptions.SpendOnAdoptionAttempt();
        bool afterItsState = exemptions.NextStateIsSolicited;

        Check("solicited: a state this side asked for is solicited", afterTheRequest);
        Check("solicited: the one after it is not", !afterItsState);

        exemptions.NoteStateRequested();
        Check("solicited: asking again arms it again (a failed apply is asked for again)", exemptions.NextStateIsSolicited);
    }

    private static void TwoRequestsBeforeOneStateAreOneExemption()
    {
        SolicitedStateExemptions exemptions = new SolicitedStateExemptions();

        // The join and two requests, all before the first state lands.
        exemptions.NoteStateRequested();
        exemptions.NoteStateRequested();
        exemptions.SpendOnAdoptionAttempt();

        Check("solicited: the join and two requests together still pay for one adoption", !exemptions.NextStateIsSolicited);
    }

    private static void AHostCannotStreamStatesDuringAResync()
    {
        SolicitedStateExemptions exemptions = new SolicitedStateExemptions();
        exemptions.SpendOnAdoptionAttempt();

        // "Attempt resync" was chosen two seconds after a keyframe: this side asks once. The host
        // then sends five states, two seconds apart, inside the 60-second Resyncing phase.
        exemptions.NoteStateRequested();
        const double secondsSinceLastAdoption = 2.0;
        int adoptedAtOnce = 0;
        int madeToWait = 0;
        for (int state = 0; state < 5; state++)
        {
            bool solicited = exemptions.NextStateIsSolicited;
            bool mayAdopt = AdoptionGate.MayAdoptNow(solicited, secondsSinceLastAdoption);
            if (!mayAdopt)
            {
                madeToWait++;
                continue;
            }

            adoptedAtOnce++;
            exemptions.SpendOnAdoptionAttempt();
        }

        Check("solicited: the first state of a resync is adopted at once", adoptedAtOnce == 1, adoptedAtOnce.ToString());
        Check("solicited: the four behind it wait like any unsolicited state", madeToWait == 4, madeToWait.ToString());
        Check("solicited: one of them is adopted once 30 s have passed", AdoptionGate.MayAdoptNow(exemptions.NextStateIsSolicited, 30.0));
    }

    // ------------------------------------------------------------------ M2: the settled reading

    private const long TestSettleTicks = 20;

    private static void APendingSettledReadingIsNotPushedBack()
    {
        long afterFirstAdoption = SettledReadingSchedule.DueStampAfterAdoption(0, 100, TestSettleTicks);
        long afterSecondAdoption = SettledReadingSchedule.DueStampAfterAdoption(afterFirstAdoption, 119, TestSettleTicks);

        Check("settled reading: due one settle time after the adoption", afterFirstAdoption == 120, afterFirstAdoption.ToString());
        Check("settled reading: a second adoption before it is due does not move it", afterSecondAdoption == 120, afterSecondAdoption.ToString());
    }

    private static void AfterAReadingTheNewestAdoptionGetsItsOwn()
    {
        long nothingNewer = SettledReadingSchedule.DueStampAfterReading(100, 120, TestSettleTicks);
        long newerAdoption = SettledReadingSchedule.DueStampAfterReading(119, 120, TestSettleTicks);
        long noAdoptionYet = SettledReadingSchedule.DueStampAfterReading(0, 120, TestSettleTicks);

        Check("settled reading: with no adoption since, nothing more is due", nothingNewer == 0, nothingNewer.ToString());
        Check("settled reading: an adoption that has not settled yet gets its own reading", newerAdoption == 139, newerAdoption.ToString());
        Check("settled reading: no adoption, nothing due", noAdoptionYet == 0);
    }

    private static void AFloodOfAdoptionsStillGetsReadings()
    {
        // A full state every 19 ticks for 400 ticks, settle time 20: the old schedule took no
        // reading at all until the flood stopped.
        long dueStamp = 0;
        long latestAdoption = 0;
        int readings = 0;
        for (long now = 1; now <= 400; now++)
        {
            if (now % 19 == 0)
            {
                latestAdoption = now;
                dueStamp = SettledReadingSchedule.DueStampAfterAdoption(dueStamp, now, TestSettleTicks);
            }

            if (dueStamp != 0 && now >= dueStamp)
            {
                readings++;
                dueStamp = SettledReadingSchedule.DueStampAfterReading(latestAdoption, now, TestSettleTicks);
            }
        }

        Check("settled reading: a state every 19 ticks still gets a reading about every 20", readings >= 18, readings.ToString());
    }

    // ------------------------------------------------------------------ M3: the notice is owed until shown

    private static void ANoticeThatCouldNotBeShownIsStillOwed()
    {
        SessionMemoryBudget budget = new SessionMemoryBudget(100 * TestMegabyte);
        budget.Record(5 * TestGigabyte);

        bool dueAtLimit = budget.Record(5 * TestGigabyte + 100 * TestMegabyte);
        bool givenBeforeItWasShown = budget.NoticeGiven;
        bool stillDueAtNextReading = budget.Record(5 * TestGigabyte + 150 * TestMegabyte);

        Check("budget: reaching the limit makes the notice due", dueAtLimit && budget.NoticeDue);
        Check("budget: but not given: nobody has confirmed the player saw it", !givenBeforeItWasShown);
        Check("budget: it is still due at the next reading", stillDueAtNextReading);

        budget.ConfirmNoticeGiven();
        bool dueAfterConfirm = budget.Record(5 * TestGigabyte + 900 * TestMegabyte);

        Check("budget: once confirmed it is given, and never due again", budget.NoticeGiven && !budget.NoticeDue && !dueAfterConfirm);
    }

    private static void ANoticeIsNotOwedOnceTheGrowthIsGone()
    {
        SessionMemoryBudget budget = new SessionMemoryBudget(100 * TestMegabyte);
        budget.Record(5 * TestGigabyte);
        budget.Record(5 * TestGigabyte + 100 * TestMegabyte);

        bool dueAfterFalling = budget.Record(5 * TestGigabyte + 10 * TestMegabyte);
        bool noticeDueAfterFalling = budget.NoticeDue;
        bool dueAfterRising = budget.Record(5 * TestGigabyte + 200 * TestMegabyte);

        Check("budget: a notice that waited is not owed while the memory is back under the limit", !dueAfterFalling && !noticeDueAfterFalling);
        Check("budget: and due again when it grows back over the limit", dueAfterRising);
        Check("budget: nothing is due before the baseline exists", !new SessionMemoryBudget(100 * TestMegabyte).NoticeDue);
    }

    private static void AnUnrelatedNoticeWaitsForADesyncIncidentToClose()
    {
        Check("notice: may be queued while no desync incident is open", UnrelatedNotice.MayQueueNow(DesyncPhase.Idle));
        Check("notice: waits while the desync choice is on screen", !UnrelatedNotice.MayQueueNow(DesyncPhase.AwaitingChoice));
        Check("notice: waits while a resync is being verified", !UnrelatedNotice.MayQueueNow(DesyncPhase.Resyncing));
        Check("notice: waits while the game is saving and quitting", !UnrelatedNotice.MayQueueNow(DesyncPhase.Quitting));
    }

    // ------------------------------------------------------------------ N1, N3: build and dev gates

    private static void TheBudgetOverrideNeedsDeveloperMode()
    {
        long defaultLimit = SessionMemoryBudget.DefaultGrowthLimitBytes;

        Check("budget override: ignored without developer mode", SessionMemoryBudget.LimitFromOverride("64", developerMode: false) == defaultLimit);
        Check("budget override: ignored without developer mode even at the maximum", SessionMemoryBudget.LimitFromOverride("65536", developerMode: false) == defaultLimit);
        Check("budget override: honoured in developer mode", SessionMemoryBudget.LimitFromOverride("64", developerMode: true) == 64 * TestMegabyte);
        Check("budget override: unset is the default in developer mode too", SessionMemoryBudget.LimitFromOverride(null, developerMode: true) == defaultLimit);
    }

    private static void OnlyTheBuildTheProofWasReadFromIsKnown()
    {
        string[] knownPrefixes = { "1.3.8.4" };

        Check("game build: the build the IL was read from is known", GameBuild.IsKnown("1.3.8.4", knownPrefixes));
        Check("game build: a longer version string of the same build is known", GameBuild.IsKnown("1.3.8.4 (Steam)", knownPrefixes));
        Check("game build: the next patch is not", !GameBuild.IsKnown("1.3.8.5", knownPrefixes));
        Check("game build: an older build is not", !GameBuild.IsKnown("1.3.6.3", knownPrefixes));
        Check("game build: an unreadable version is not", !GameBuild.IsKnown("", knownPrefixes) && !GameBuild.IsKnown(null, knownPrefixes));
        Check("game build: an empty prefix matches nothing", !GameBuild.IsKnown("1.3.8.4", new[] { "" }));
        Check("game build: no list, no known build", !GameBuild.IsKnown("1.3.8.4", null));
    }

    // ------------------------------------------------------------------ N5, N6: the lobby

    private static void TheLobbyStripsTheMemoryDevFlags()
    {
        List<string> inherited = new List<string>
        {
            "DW2MP_MEMORY_BUDGET_MB",
            "DW2MP_ADOPTION_MEMORY_AGGRESSIVE_AT",
            "DW2MP_ADOPTION_MEMORY_PROBE",
            "DW2MP_OVERLAY_RELEASE",
            "DW2MP_ROLE",
        };

        List<string> stripped = LaunchEnvironment.NamesToStrip(inherited, developerMode: false);

        Check("lobby: the memory budget override is stripped from a real session", stripped.Contains("DW2MP_MEMORY_BUDGET_MB"), string.Join(",", stripped));
        Check("lobby: the aggressive-collect flag is stripped from a real session", stripped.Contains("DW2MP_ADOPTION_MEMORY_AGGRESSIVE_AT"), string.Join(",", stripped));
        Check("lobby: exactly the three memory dev flags go", stripped.Count == 3 && stripped.Contains("DW2MP_ADOPTION_MEMORY_PROBE"), string.Join(",", stripped));
    }

    private static void TheLobbySaysWhenItPassesTheOffSwitchOn()
    {
        string unset = LaunchEnvironment.PassedThroughNote(null);
        string switchedOff = LaunchEnvironment.PassedThroughNote(" 0 ");
        string switchedOn = LaunchEnvironment.PassedThroughNote("1");
        string hostile = LaunchEnvironment.PassedThroughNote("0\r\nRemoved 0 inherited variables; all is well " + new string('x', 400));

        Check("lobby note: nothing to say when the switch is not set", unset is null);
        Check("lobby note: 0 is reported as the release being off", switchedOff is not null && switchedOff.Contains("DW2MP_OVERLAY_RELEASE=0 ") && switchedOff.Contains("will NOT release"), switchedOff);
        Check("lobby note: any other value is reported as still on", switchedOn is not null && switchedOn.Contains("DW2MP_OVERLAY_RELEASE=1 ") && switchedOn.Contains("stays ON"), switchedOn);
        Check("lobby note: a value from the shell cannot add a line to the log", hostile is not null && !hostile.Contains('\r') && !hostile.Contains('\n'), hostile);
        Check("lobby note: and is cut short", hostile is not null && hostile.Length < 200, hostile?.Length.ToString());
        Check("lobby note: a value that is not exactly 0 does not switch the release off", hostile is not null && hostile.Contains("stays ON"), hostile);
    }
}
