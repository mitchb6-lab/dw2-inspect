using System.Text.RegularExpressions;

namespace Dw2Inspect;

/// <summary>
/// Finds the Distant Worlds 2 install. Order: --game argument, DW2_PATH env var,
/// then every Steam library listed in libraryfolders.vdf, then the usual defaults.
/// </summary>
internal static class GameLocator
{
    /// <summary>The one file that must be present for a directory to be a DW2 install.</summary>
    private const string Sentinel = "DistantWorlds.Types.dll";

    private const string GameFolder = "Distant Worlds 2";

    private static readonly string[] SteamRoots =
    {
        @"C:\Program Files (x86)\Steam",
        @"C:\Program Files\Steam",
        @"D:\Steam",
    };

    public static string Locate(string explicitPath)
    {
        foreach (var candidate in Candidates(explicitPath))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && IsGameDir(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new Dw2Exception(
            "Could not find the Distant Worlds 2 install. Pass --game <path>, or set DW2_PATH. " +
            $"A valid path is the folder containing {Sentinel}.");
    }

    private static IEnumerable<string> Candidates(string explicitPath)
    {
        yield return explicitPath;
        yield return Environment.GetEnvironmentVariable("DW2_PATH");

        foreach (var library in SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", GameFolder);
    }

    /// <summary>
    /// Every Steam library root on the machine. Reads libraryfolders.vdf rather than
    /// guessing drive letters, because the game is commonly on a secondary library.
    /// </summary>
    private static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in SteamRoots)
        {
            if (seen.Add(root))
                yield return root;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            string text;
            try { text = File.ReadAllText(vdf); }
            catch (IOException) { continue; }

            // Lines look like:   "path"    "D:\SteamLibrary"
            foreach (Match m in Regex.Matches(text, @"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static bool IsGameDir(string dir)
    {
        try { return File.Exists(Path.Combine(dir, Sentinel)); }
        catch { return false; }
    }
}

internal sealed class Dw2Exception : Exception
{
    public Dw2Exception(string message) : base(message) { }
}
