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

    /// <summary>True once the notice has been asked for; it is asked for exactly once a session.</summary>
    public bool NoticeGiven { get; private set; }

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
    /// Record the process's memory after an adoption. Returns true exactly once: the first time
    /// the growth over the baseline reaches the limit. A reading that is not positive (the
    /// process counter could not be read) is ignored.
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

        if (NoticeGiven)
        {
            return false;
        }

        if (GrowthBytes < _growthLimitBytes)
        {
            return false;
        }

        NoticeGiven = true;
        return true;
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
