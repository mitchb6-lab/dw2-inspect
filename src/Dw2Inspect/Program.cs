using Dw2Inspect;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        dw2inspect -- read the code inside Distant Worlds 2

        USAGE
          dw2inspect <command> [argument] [options]

        COMMANDS
          types   [substring]        List type names, optionally filtered.
          find    <substring>        Search type AND method names.
          members <Type>             Methods and fields of a type.
          enum    <Type>             Enum members with their values.
          strings <Type>             Every string literal in a type's methods.
          il      <Type[::Method]>   Decompile method bodies to IL. Method may be *.
          refs    [substring]        Assembly references of each game assembly.

        OPTIONS
          --game <path>   Path to the Distant Worlds 2 install. Defaults to the
                          DW2_PATH environment variable, then a Steam library scan.
          --no-follow     For 'il': do not follow async/iterator methods into the
                          generated state machine that holds the real body.
          --quiet         Suppress warnings on stderr.

        EXAMPLES
          dw2inspect find Multiplayer
          dw2inspect enum PlayMode
          dw2inspect il GameClient::SendMessageToServer
          dw2inspect strings NetworkHelper
          dw2inspect il "DistantWorlds.Types.MessagePacket::*" > MessagePacket.il.txt

        NOTE
          The game ships with method bodies stripped -- on disk almost every method is
          the four bytes 00 00 00 2A. This tool loads the assemblies into its own
          process and runs each type's static constructor, which is what makes the
          protector restore the real IL. Nothing is written to the game directory.
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var options = Options.Parse(args);
            Log.Quiet = options.Quiet;

            var gameDir = GameLocator.Locate(options.GamePath);
            Log.Warn("using " + gameDir);

            var loader = new Loader(gameDir);
            var commands = new Commands(loader, Console.Out);

            switch (options.Command)
            {
                case "types":   commands.Types(options.Argument); break;
                case "find":    commands.Find(options.Argument); break;
                case "members": commands.MembersOf(options.Argument); break;
                case "enum":    commands.EnumOf(options.Argument); break;
                case "strings": commands.Strings(options.Argument); break;
                case "il":      commands.Il(options.Argument, options.FollowStateMachines); break;
                case "refs":    commands.Refs(options.Argument); break;

                default:
                    Console.Error.WriteLine($"unknown command '{options.Command}'.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(Usage);
                    return 2;
            }

            return 0;
        }
        catch (Dw2Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}

internal sealed class Options
{
    public string Command { get; private init; }

    public string Argument { get; private init; }

    public string GamePath { get; private init; }

    public bool FollowStateMachines { get; private init; } = true;

    public bool Quiet { get; private init; }

    public static Options Parse(string[] args)
    {
        string command = args[0];
        string argument = null;
        string gamePath = null;
        bool follow = true;
        bool quiet = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game":
                    if (i + 1 >= args.Length)
                        throw new Dw2Exception("--game needs a path.");
                    gamePath = args[++i];
                    break;

                case "--no-follow":
                    follow = false;
                    break;

                case "--quiet":
                    quiet = true;
                    break;

                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new Dw2Exception($"unknown option '{args[i]}'.");

                    if (argument is not null)
                        throw new Dw2Exception($"unexpected extra argument '{args[i]}'.");

                    argument = args[i];
                    break;
            }
        }

        return new Options
        {
            Command = command,
            Argument = argument,
            GamePath = gamePath,
            FollowStateMachines = follow,
            Quiet = quiet,
        };
    }
}
