using System.Text;

namespace Dw2MpShared;

/// <summary>
/// Text that arrived from the other machine, made safe to show and to log.
///
/// A player name, an empire name, a pack name or a refusal sentence is typed or built on the
/// other PC and then lands in this one's log pane, player list, a MessageBox or session.json.
/// Unchecked, a newline in a name forges a log line and an 8 MB name stalls the UI. So every
/// such string passes through here first: control characters go, the length is capped, and
/// the caller says how long is reasonable for that field (JoinPolicy holds the numbers).
///
/// Shared source: compiled into the lobby and the test runner (the mod has no peer strings
/// that reach a UI; its Content message is parsed by ContentPackWire).
/// </summary>
public static class PeerText
{
    /// <summary>Marks a capped string so a reader knows it was longer.</summary>
    public const string Ellipsis = "...";

    /// <summary>
    /// One line: every control character (below 0x20, and 0x7F) is removed, surrounding
    /// whitespace is trimmed, and the result is capped at maxLength. Null becomes "".
    /// </summary>
    public static string Sanitise(string text, int maxLength)
    {
        if (text is null)
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (IsControl(character))
            {
                continue;
            }

            builder.Append(character);
        }

        var cleaned = builder.ToString().Trim();
        return Cap(cleaned, maxLength);
    }

    /// <summary>
    /// Several lines, for a sentence shown in a dialog: '\n' survives, a run of newlines
    /// collapses to one, every other control character is removed (so '\r' goes and "\r\n"
    /// becomes "\n"), and the result is capped at maxLength. Null becomes "".
    /// </summary>
    public static string SanitiseMultiline(string text, int maxLength)
    {
        if (text is null)
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        bool lastWasNewline = false;

        foreach (var character in text)
        {
            if (character == '\n')
            {
                if (lastWasNewline)
                {
                    continue;
                }

                builder.Append('\n');
                lastWasNewline = true;
                continue;
            }

            if (IsControl(character))
            {
                continue;
            }

            builder.Append(character);
            lastWasNewline = false;
        }

        var cleaned = builder.ToString().Trim();
        return Cap(cleaned, maxLength);
    }

    private static bool IsControl(char character)
    {
        bool belowSpace = character < ' ';
        bool isDelete = character == '\x7F';
        return belowSpace || isDelete;
    }

    private static string Cap(string text, int maxLength)
    {
        if (maxLength <= 0)
        {
            return "";
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= Ellipsis.Length)
        {
            return text.Substring(0, maxLength);
        }

        var kept = text.Substring(0, maxLength - Ellipsis.Length);
        return kept + Ellipsis;
    }
}
