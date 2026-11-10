namespace Dw2MpShared;

/// <summary>
/// What the lobby needs out of Steam's appmanifest_&lt;appid&gt;.acf for the game: the build id,
/// which DLC the player has switched off (Steam > Properties > DLC), and the depot manifest id
/// of each installed DLC, which is the only per-pack version anything reports.
///
/// Read-only. The file is Steam's; this parses a copy of its text and never writes it. Steam
/// removes a DLC's depot when the player unticks it, so a pack can be "owned" and still not
/// installed -- which is exactly the state that made the 2026-11-08 save fail to load.
///
/// The format is Valve's KeyValues text: "key" "value" pairs and "key" { ... } blocks.
/// A small tokenizer and a recursive block parser cover all of it; a regex did not, because
/// depot blocks nest and the same key ("manifest") appears once per depot.
/// </summary>
public sealed class SteamAppManifest
{
    public string BuildId { get; private set; } = "";

    public HashSet<uint> DisabledDlcAppIds { get; } = new();

    public Dictionary<uint, string> DepotManifestByDlcAppId { get; } = new();

    /// <summary>
    /// &lt;library&gt;\steamapps\common\&lt;install dir&gt; becomes &lt;library&gt;\steamapps\appmanifest_&lt;appId&gt;.acf.
    /// Null when the game is not inside a Steam library (GOG, Matrix, a copied folder).
    /// Does not check that the file exists; the caller decides what a missing file means.
    /// </summary>
    public static string PathForGame(string gamePath, uint appId)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return null;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var installDir = Path.GetFullPath(gamePath).TrimEnd(separators);
        var commonDir = Path.GetDirectoryName(installDir);
        if (commonDir is null)
        {
            return null;
        }

        var steamAppsDir = Path.GetDirectoryName(commonDir);
        if (steamAppsDir is null)
        {
            return null;
        }

        bool underCommon = string.Equals(Path.GetFileName(commonDir), "common", StringComparison.OrdinalIgnoreCase);
        bool underSteamApps = string.Equals(Path.GetFileName(steamAppsDir), "steamapps", StringComparison.OrdinalIgnoreCase);
        if (!underCommon || !underSteamApps)
        {
            return null;
        }

        var fileName = $"appmanifest_{appId}.acf";
        return Path.Combine(steamAppsDir, fileName);
    }

    /// <summary>Parses the file's text. Throws FormatException if it nests deeper than KeyValuesText.MaxDepth.</summary>
    public static SteamAppManifest Parse(string text)
    {
        var manifest = new SteamAppManifest();
        var root = KeyValuesText.Parse(text);

        // Steam wraps everything in one "AppState" block; tolerate a file that does not.
        var appState = KeyValuesText.Child(root, "AppState");
        if (appState is null)
        {
            appState = root;
        }

        manifest.BuildId = KeyValuesText.Value(appState, "buildid") ?? "";

        var userConfig = KeyValuesText.Child(appState, "UserConfig");
        var disabledList = KeyValuesText.Value(userConfig, "DisabledDLC") ?? "";
        foreach (var idText in disabledList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            bool parsed = uint.TryParse(idText.Trim(), out var dlcAppId);
            if (parsed)
            {
                manifest.DisabledDlcAppIds.Add(dlcAppId);
            }
        }

        var installedDepots = KeyValuesText.Child(appState, "InstalledDepots");
        if (installedDepots is null)
        {
            return manifest;
        }

        foreach (var depotPair in installedDepots)
        {
            var depot = depotPair.Value as Dictionary<string, object>;
            if (depot is null)
            {
                continue;
            }

            var dlcAppIdText = KeyValuesText.Value(depot, "dlcappid");
            if (dlcAppIdText is null)
            {
                // The base game's own depots carry no dlcappid; they are not content packs.
                continue;
            }

            bool parsed = uint.TryParse(dlcAppIdText, out var dlcAppId);
            if (!parsed)
            {
                continue;
            }

            var depotManifest = KeyValuesText.Value(depot, "manifest") ?? "";
            manifest.DepotManifestByDlcAppId[dlcAppId] = depotManifest;
        }

        return manifest;
    }
}

/// <summary>
/// Valve KeyValues text, parsed into nested dictionaries: a value is either a string or
/// another Dictionary&lt;string, object&gt;. Keys compare case-insensitively because Steam's
/// own casing is inconsistent ("buildid", "DisabledDLC", "InstalledDepots").
/// </summary>
internal static class KeyValuesText
{
    private enum TokenKind
    {
        Text,
        Open,
        Close,
    }

    private sealed class Token
    {
        public TokenKind Kind;
        public string Text;
    }

    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Tokenise(text ?? "");
        int position = 0;
        return ParseBlock(tokens, ref position, 0);
    }

    /// <summary>
    /// Steam's files nest three levels deep. A file that nests past this is not one of
    /// Steam's, and recursing into it would be a stack overflow waiting for a long enough
    /// run of braces; Parse throws FormatException instead.
    /// </summary>
    public const int MaxDepth = 64;

    public static Dictionary<string, object> Child(Dictionary<string, object> block, string key)
    {
        if (block is null)
        {
            return null;
        }

        bool found = block.TryGetValue(key, out var value);
        if (!found)
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    public static string Value(Dictionary<string, object> block, string key)
    {
        if (block is null)
        {
            return null;
        }

        bool found = block.TryGetValue(key, out var value);
        if (!found)
        {
            return null;
        }

        return value as string;
    }

    private static Dictionary<string, object> ParseBlock(List<Token> tokens, ref int position, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"KeyValues text nests deeper than {MaxDepth} levels");
        }

        var block = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (position < tokens.Count)
        {
            var keyToken = tokens[position];
            if (keyToken.Kind == TokenKind.Close)
            {
                position++;
                return block;
            }

            if (keyToken.Kind == TokenKind.Open)
            {
                // A block with no key: malformed. Skip the brace and keep going.
                position++;
                continue;
            }

            position++;
            if (position >= tokens.Count)
            {
                break;
            }

            var valueToken = tokens[position];
            if (valueToken.Kind == TokenKind.Open)
            {
                position++;
                var child = ParseBlock(tokens, ref position, depth + 1);
                block[keyToken.Text] = child;
                continue;
            }

            if (valueToken.Kind == TokenKind.Close)
            {
                // A key with no value at the end of a block; record it empty and let the
                // caller see the Close on the next loop.
                block[keyToken.Text] = "";
                continue;
            }

            position++;
            block[keyToken.Text] = valueToken.Text;
        }

        return block;
    }

    private static List<Token> Tokenise(string text)
    {
        var tokens = new List<Token>();
        int index = 0;

        while (index < text.Length)
        {
            char current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '{')
            {
                tokens.Add(new Token { Kind = TokenKind.Open, Text = "{" });
                index++;
                continue;
            }

            if (current == '}')
            {
                tokens.Add(new Token { Kind = TokenKind.Close, Text = "}" });
                index++;
                continue;
            }

            bool isComment = current == '/' && index + 1 < text.Length && text[index + 1] == '/';
            if (isComment)
            {
                index = SkipToEndOfLine(text, index);
                continue;
            }

            if (current == '"')
            {
                index++;
                var quoted = ReadQuoted(text, ref index);
                tokens.Add(new Token { Kind = TokenKind.Text, Text = quoted });
                continue;
            }

            var bare = ReadBareWord(text, ref index);
            tokens.Add(new Token { Kind = TokenKind.Text, Text = bare });
        }

        return tokens;
    }

    private static int SkipToEndOfLine(string text, int index)
    {
        while (index < text.Length && text[index] != '\n')
        {
            index++;
        }

        return index;
    }

    /// <summary>Reads up to the closing quote, honouring \" and \\ the way Steam writes paths.</summary>
    private static string ReadQuoted(string text, ref int index)
    {
        var builder = new System.Text.StringBuilder();

        while (index < text.Length)
        {
            char current = text[index];

            if (current == '"')
            {
                index++;
                return builder.ToString();
            }

            bool isEscape = current == '\\' && index + 1 < text.Length;
            if (isEscape)
            {
                char escaped = text[index + 1];
                builder.Append(escaped);
                index += 2;
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    private static string ReadBareWord(string text, ref int index)
    {
        int start = index;

        while (index < text.Length)
        {
            char current = text[index];
            bool endsWord = char.IsWhiteSpace(current) || current == '{' || current == '}' || current == '"';
            if (endsWord)
            {
                break;
            }

            index++;
        }

        return text.Substring(start, index - start);
    }
}
