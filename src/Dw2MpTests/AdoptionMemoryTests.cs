using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for the joiner's session memory budget (2026-11-11): the first reading is the
/// baseline, the notice is asked for exactly once, and an override cannot silence or spam it.
/// </summary>
public static partial class Program
{
    private const long TestMegabyte = 1024L * 1024;
    private const long TestGigabyte = 1024L * TestMegabyte;

    private static void RunAdoptionMemoryTests()
    {
        TheFirstReadingIsTheBaselineAndNeverANotice();
        TheNoticeComesOnceWhenGrowthReachesTheLimit();
        MemoryThatFallsBackIsNotGrowth();
        AnUnreadableReadingIsIgnored();
        TheBudgetOverrideIsClampedAndDefaultsWhenUnreadable();
        TheNoticeSaysWhatToDoAndHowMuch();
    }

    private static void TheFirstReadingIsTheBaselineAndNeverANotice()
    {
        var budget = new SessionMemoryBudget(SessionMemoryBudget.DefaultGrowthLimitBytes);

        bool noticeOnFirst = budget.Record(50 * TestGigabyte);

        Check("budget: the first reading is never a notice, however large", !noticeOnFirst);
        Check("budget: the first reading is the baseline, so growth is zero", budget.GrowthBytes == 0, budget.GrowthBytes.ToString());
        Check("budget: one reading counted", budget.Readings == 1, budget.Readings.ToString());
        Check("budget: default limit is 1.5 GB", budget.GrowthLimitBytes == 1536 * TestMegabyte, budget.GrowthLimitBytes.ToString());
    }

    private static void TheNoticeComesOnceWhenGrowthReachesTheLimit()
    {
        var budget = new SessionMemoryBudget(SessionMemoryBudget.DefaultGrowthLimitBytes);
        long baseline = 5 * TestGigabyte;
        budget.Record(baseline);

        bool noticeBelow = budget.Record(baseline + 1535 * TestMegabyte);
        Check("budget: one megabyte under the limit is not a notice", !noticeBelow);
        Check("budget: no notice given yet", !budget.NoticeGiven);

        bool noticeAtLimit = budget.Record(baseline + 1536 * TestMegabyte);
        Check("budget: reaching the limit is the notice", noticeAtLimit);
        Check("budget: notice recorded as given", budget.NoticeGiven);

        bool noticeAgain = budget.Record(baseline + 3 * TestGigabyte);
        Check("budget: never a second notice in the same session", !noticeAgain);

        bool noticeAfterFalling = budget.Record(baseline);
        bool noticeAfterRisingAgain = budget.Record(baseline + 4 * TestGigabyte);
        Check("budget: falling back and rising again is still not a second notice", !noticeAfterFalling && !noticeAfterRisingAgain);
        Check("budget: every reading counted", budget.Readings == 6, budget.Readings.ToString());
    }

    private static void MemoryThatFallsBackIsNotGrowth()
    {
        var budget = new SessionMemoryBudget(100 * TestMegabyte);
        budget.Record(5 * TestGigabyte);

        bool noticeWhenSmaller = budget.Record(4 * TestGigabyte);

        Check("budget: a process smaller than its baseline has grown by zero, not a negative amount", budget.GrowthBytes == 0, budget.GrowthBytes.ToString());
        Check("budget: and that is not a notice", !noticeWhenSmaller);

        budget.Record(5 * TestGigabyte + 60 * TestMegabyte);
        Check("budget: growth is measured from the baseline, not from the lowest reading", budget.GrowthBytes == 60 * TestMegabyte, budget.GrowthBytes.ToString());
    }

    private static void AnUnreadableReadingIsIgnored()
    {
        var budget = new SessionMemoryBudget(100 * TestMegabyte);

        bool noticeOnZero = budget.Record(0);
        bool noticeOnNegative = budget.Record(-1);
        Check("budget: zero and negative readings are not notices", !noticeOnZero && !noticeOnNegative);
        Check("budget: and are not counted", budget.Readings == 0, budget.Readings.ToString());

        budget.Record(2 * TestGigabyte);
        budget.Record(0);
        Check("budget: an unreadable reading after the baseline leaves the growth alone", budget.GrowthBytes == 0, budget.GrowthBytes.ToString());

        bool notice = budget.Record(2 * TestGigabyte + 100 * TestMegabyte);
        Check("budget: the baseline was the first READABLE reading", notice);
    }

    private static void TheBudgetOverrideIsClampedAndDefaultsWhenUnreadable()
    {
        long defaultLimit = SessionMemoryBudget.DefaultGrowthLimitBytes;

        Check("budget override: null is the default", SessionMemoryBudget.LimitFromMegabytesText(null) == defaultLimit);
        Check("budget override: empty is the default", SessionMemoryBudget.LimitFromMegabytesText("  ") == defaultLimit);
        Check("budget override: words are the default", SessionMemoryBudget.LimitFromMegabytesText("lots") == defaultLimit);
        Check("budget override: zero is the default, not 'notify at once'", SessionMemoryBudget.LimitFromMegabytesText("0") == defaultLimit);
        Check("budget override: negative is the default", SessionMemoryBudget.LimitFromMegabytesText("-500") == defaultLimit);
        Check("budget override: a fraction is the default", SessionMemoryBudget.LimitFromMegabytesText("1.5") == defaultLimit);

        Check("budget override: 2048 is 2 GB", SessionMemoryBudget.LimitFromMegabytesText(" 2048 ") == 2 * TestGigabyte);
        Check("budget override: 1 is raised to the minimum", SessionMemoryBudget.LimitFromMegabytesText("1") == SessionMemoryBudget.MinGrowthLimitMegabytes * TestMegabyte);
        Check("budget override: a huge value is lowered to the maximum, so it cannot overflow",
              SessionMemoryBudget.LimitFromMegabytesText("9000000000000") == SessionMemoryBudget.MaxGrowthLimitMegabytes * TestMegabyte);
    }

    private static void TheNoticeSaysWhatToDoAndHowMuch()
    {
        var budget = new SessionMemoryBudget(SessionMemoryBudget.DefaultGrowthLimitBytes);
        budget.Record(5 * TestGigabyte);
        budget.Record(5 * TestGigabyte + 1741 * TestMegabyte);

        string notice = budget.NoticeText();
        string logLine = budget.LogText();

        Check("budget notice: names the growth in gigabytes", notice.Contains("about 1.7 GB more"), notice);
        Check("budget notice: recommends saving and restarting", notice.Contains("save the game") && notice.Contains("restart"), notice);
        Check("budget notice: says it is shown once", notice.Contains("shown once"), notice);
        Check("budget log: growth, readings and limit", logLine == "memory grew 1.7 GB over 2 reading(s), limit 1.5 GB -- recommending save and restart (shown once)", logLine);
        Check("budget: gigabytes use a point whatever the culture", SessionMemoryBudget.FormatGigabytes(1536 * TestMegabyte) == "1.5 GB");
    }
}
