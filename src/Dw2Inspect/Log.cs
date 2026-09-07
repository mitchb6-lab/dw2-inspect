namespace Dw2Inspect;

/// <summary>
/// Warnings go to stderr so that piping stdout to a file keeps the dump clean.
/// </summary>
internal static class Log
{
    public static bool Quiet { get; set; }

    public static void Warn(string message)
    {
        if (Quiet) return;

        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Error.WriteLine("warning: " + message);
        }
        finally { Console.ForegroundColor = previous; }
    }
}
