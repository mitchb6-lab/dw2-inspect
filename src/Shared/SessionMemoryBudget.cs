using System;
using System.Globalization;

namespace Dw2MpShared;

/// <summary>
/// The joiner's memory budget for one session (2026-11-11).
///
/// Adopting a full state costs the joiner memory that does not all come back, so a long session
/// with periodic keyframes grows. This class decides ONE thing: the moment to tell the player,
/// once, that saving and restarting the game is now a good idea. It never blocks an adoption:
/// a refused keyframe would leave two players in two different galaxies, which is worse than a
/// large process.
///
/// The number it is fed is the process's memory after each adoption has settled. The first
/// reading is the baseline (the join itself), so the growth is measured from the moment this
/// player was in the shared galaxy, not from the title screen. Pure .NET, tested.
/// </summary>
public sealed class SessionMemoryBudget
{
    private const long BytesPerMegabyte = 1024L * 1024;

    /// <summary>Growth over the baseline that triggers the notice: 1.5 GB.</summary>
    public const long DefaultGrowthLimitBytes = 1536L * BytesPerMegabyte;

    /// <summary>The override (megabytes) is clamped to this range, so a typo cannot silence or spam the notice.</summary>
    public const long MinGrowthLimitMegabytes = 16;

    public const long MaxGrowthLimitMegabytes = 64L * 1024;

    private readonly long _growthLimitBytes;
    private long _baselineBytes = -1;
    private long _latestBytes;

    public SessionMemoryBudget(long growthLimitBytes)
    {
        _growthLimitBytes = growthLimitBytes;
    }

    /// <summary>Readings taken so far, the baseline included.</summary>
    public int Readings { get; private set; }

    /// <summary>
    /// True once the caller has confirmed (ConfirmNoticeGiven) that the notice was handed to the
    /// player. Reaching the limit does not set it: a notice that could not be shown yet, because a
    /// desync popup was open, is still owed (security review of 2026-11-11, M3).
    /// </summary>
    public bool NoticeGiven { get; private set; }

    /// <summary>True while the growth is at or over the limit and the notice has not been confirmed as given.</summary>
    public bool NoticeDue
    {
        get
        {
            if (NoticeGiven)
            {
                return false;
            }

            if (_baselineBytes < 0)
            {
                return false;
            }

            return GrowthBytes >= _growthLimitBytes;
        }
    }

    public long GrowthLimitBytes
    {
        get
        {
            return _growthLimitBytes;
        }
    }

    /// <summary>Bytes gained since the baseline; never negative, and zero before the baseline exists.</summary>
    public long GrowthBytes
    {
        get
        {
            if (_baselineBytes < 0)
            {
                return 0;
            }

            return Math.Max(0, _latestBytes - _baselineBytes);
        }
    }

    /// <summary>
    /// The limit from an override in megabytes (a settings value or an environment variable).
    /// Null, empty, unreadable or non-positive text gives the default; anything else is clamped.
    /// </summary>
    public static long LimitFromMegabytesText(string megabytesText)
    {
        if (string.IsNullOrWhiteSpace(megabytesText))
        {
            return DefaultGrowthLimitBytes;
        }

        bool parsed = long.TryParse(megabytesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long megabytes);
        if (!parsed || megabytes <= 0)
        {
            return DefaultGrowthLimitBytes;
        }

        long clamped = Math.Clamp(megabytes, MinGrowthLimitMegabytes, MaxGrowthLimitMegabytes);
        return clamped * BytesPerMegabyte;
    }

    /// <summary>
    /// The limit for this session. The override is a developer setting (security review of
    /// 2026-11-11, N3): it is honoured only in developer mode, so a variable left in a player's
    /// shell, or planted there, can neither silence the notice nor bring it forward.
    /// </summary>
    public static long LimitFromOverride(string megabytesText, bool developerMode)
    {
        if (!developerMode)
        {
            return DefaultGrowthLimitBytes;
        }

        return LimitFromMegabytesText(megabytesText);
    }

    /// <summary>
    /// Record the process's memory after an adoption. Returns true while the notice is due: the
    /// growth over the baseline has reached the limit and the caller has not yet confirmed that
    /// the player was told (ConfirmNoticeGiven). A reading that is not positive (the process
    /// counter could not be read) is ignored.
    /// </summary>
    public bool Record(long processBytes)
    {
        if (processBytes <= 0)
        {
            return false;
        }

        Readings++;
        _latestBytes = processBytes;

        if (_baselineBytes < 0)
        {
            _baselineBytes = processBytes;
            return false;
        }

        return NoticeDue;
    }

    /// <summary>The caller handed the notice to the player. It is never due again this session.</summary>
    public void ConfirmNoticeGiven()
    {
        NoticeGiven = true;
    }

    public const string NoticeTitle = "Multiplayer: memory use";

    /// <summary>What the player reads. Plain words, one recommendation, no numbers they cannot act on.</summary>
    public string NoticeText()
    {
        string grown = FormatGigabytes(GrowthBytes);

        return "This game has been using more and more memory since you joined: about " + grown + " more so far.\n\n" +
               "Each time the two games are brought back in step, Distant Worlds 2 keeps some memory it does not give back.\n\n" +
               "Nothing is wrong yet. When it suits you both, have the host save the game, then both of you restart " +
               "Distant Worlds 2 and continue from that save. This notice is shown once.";
    }

    /// <summary>One log line for the same moment.</summary>
    public string LogText()
    {
        return "memory grew " + FormatGigabytes(GrowthBytes) + " over " + Readings + " reading(s), limit " +
               FormatGigabytes(_growthLimitBytes) + " -- recommending save and restart (shown once)";
    }

    public static string FormatGigabytes(long bytes)
    {
        double gigabytes = bytes / (1024.0 * 1024.0 * 1024.0);
        return gigabytes.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }
}

/// <summary>
/// When the "settled" memory reading after an adoption is due (security review of 2026-11-11,
/// M2). The reading is what feeds the session memory budget, and it used to be scheduled as
/// "20 s after the LATEST adoption": a host sending a full state every 19 s pushed it back for
/// ever, so the process grew and the player was never told. Now a reading that is already due
/// keeps its time, and after it is taken the next one is set for the newest adoption that has
/// not settled yet. Under a flood there is a reading every settle interval.
///
/// Stamps are Stopwatch ticks; 0 means "nothing due" and "no adoption yet". Pure, tested.
/// </summary>
public static class SettledReadingSchedule
{
    /// <summary>The due stamp after an adoption at <paramref name="nowStamp"/>: a pending one is kept, never pushed back.</summary>
    public static long DueStampAfterAdoption(long pendingDueStamp, long nowStamp, long settleTicks)
    {
        if (pendingDueStamp != 0)
        {
            return pendingDueStamp;
        }

        return nowStamp + settleTicks;
    }

    /// <summary>The due stamp after a reading was taken at <paramref name="nowStamp"/>: the latest adoption's own settle time if that is still ahead, otherwise nothing.</summary>
    public static long DueStampAfterReading(long latestAdoptionStamp, long nowStamp, long settleTicks)
    {
        if (latestAdoptionStamp == 0)
        {
            return 0;
        }

        long latestSettlesAt = latestAdoptionStamp + settleTicks;
        if (latestSettlesAt <= nowStamp)
        {
            return 0;
        }

        return latestSettlesAt;
    }
}
