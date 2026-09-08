using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// A one-shot tool: generate a SMALL new game and save it.
///
/// Two DW2 instances will not fit in 32 GB when the save is a late-game 40 MB galaxy —
/// each process reached a ~15 GB working set. The two-process loopback test therefore
/// needs a small early-game save, and this makes one without anyone clicking through the
/// New Game screen.
///
/// It cannot use --new-game. That path reads the stub GameStartSettings the game writes
/// on first run (234 bytes, no empires) and dies in DWGame.InitializeSinglePlayerGame
/// with a NullReferenceException because playerEmpire is null. So the settings are built
/// here instead, with one explicit IsPlayer empire, and handed straight to StartGameNew.
///
/// Sequence, all on the main thread because generation and saving both touch content:
///   1. First DWGame.Update  -> build settings, StartGameNew
///   2. Once the simulation has ticked -> SaveGame
///   3. Exit
/// </summary>
public static class MakeSave
{
    private static Action<string> _log;
    private static object _game;

    private static MethodInfo _startGameNew;
    private static MethodInfo _saveGame;
    private static Type _settingsType;
    private static Type _empireType;

    private static bool _started;
    private static bool _saved;
    private static long _cyclesSeen;
    private static long _cyclesAtStart = -1;

    private static int Stars => EnvInt("DW2MP_SAVE_STARS", 15);
    private static int OtherEmpires => EnvInt("DW2MP_SAVE_EMPIRES", 2);
    private static string SaveName => Env("DW2MP_SAVE_NAME", "MPTest");

    /// <summary>Ticks to let the new game settle before saving.</summary>
    private static int SettleCycles => EnvInt("DW2MP_SAVE_SETTLE_CYCLES", 300);

    public static bool Active => Env("DW2MP_MAKE_SAVE", "0") == "1";

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        if (gameType is null) { log("# makesave: DWGame not found"); return; }

        _startGameNew = AccessTools.Method(gameType, "StartGameNew");
        _saveGame = AccessTools.Method(gameType, "SaveGame");
        _settingsType = AccessTools.TypeByName("DistantWorlds.Types.GameStartSettings");
        _empireType = AccessTools.TypeByName("DistantWorlds.Types.GameStartSettingsEmpire");

        if (_startGameNew is null || _saveGame is null || _settingsType is null || _empireType is null)
        {
            log("# makesave: required types/methods not resolvable");
            return;
        }

        var update = AccessTools.Method(gameType, "Update");
        harmony.Patch(update, new HarmonyMethod(typeof(MakeSave).GetMethod(
            nameof(OnUpdate), BindingFlags.NonPublic | BindingFlags.Static)));

        // Guarantee a player empire at the point of use.
        //
        // StartGameExisting resolves the player via Galaxy.Empires.GetPlayer(), which
        // returns the first Empire with IsPlayer set and null otherwise — and
        // InitializeSinglePlayerGame then dereferences that null. Setting IsPlayer on the
        // GameStartSettingsEmpire does NOT survive Galaxy.Generate, so fixing the input
        // does not work; fixing it here does, and it is a no-op whenever a player already
        // exists (which is the case for every save-loading path, including M3/M4's).
        var startExisting = AccessTools.Method(gameType, "StartGameExisting");
        if (startExisting is not null)
        {
            harmony.Patch(startExisting, new HarmonyMethod(typeof(MakeSave).GetMethod(
                nameof(EnsurePlayerEmpire), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        // Generation itself succeeds; EndGenerateGame then dies in
        // Empire.GenerateSituationDescription(GameStartSettingsEmpire) because the empire
        // we promoted has no corresponding settings entry, so empireStart is null.
        //
        // That method produces FLAVOUR TEXT — the "your empire begins..." blurb. Skipping
        // it when its input is null costs a cosmetic string and lets generation finish.
        // Guarding a real simulation function this way would be unacceptable; guarding a
        // description is not.
        var situation = AccessTools.Method(
            AccessTools.TypeByName("DistantWorlds.Types.Empire"), "GenerateSituationDescription");

        if (situation is not null)
        {
            harmony.Patch(situation, new HarmonyMethod(typeof(MakeSave).GetMethod(
                nameof(SkipSituationDescription), BindingFlags.NonPublic | BindingFlags.Static)));
            log("# makesave: guarded Empire.GenerateSituationDescription against a null settings entry");
        }

        log($"# makesave: armed — stars={Stars} otherEmpires={OtherEmpires} name={SaveName}");
    }

    public static void OnServerCycle()
    {
        _cyclesSeen++;
        if (_cyclesAtStart < 0 && _started) _cyclesAtStart = _cyclesSeen;
    }

    /// <summary>
    /// Prefix on StartGameExisting. If the galaxy has no player empire, promote the first
    /// one. Never throws: this runs in front of the game's own start path.
    /// </summary>
    private static void EnsurePlayerEmpire(object[] __args)
    {
        try
        {
            var galaxy = __args is { Length: > 0 } ? __args[0] : null;
            if (galaxy is null) return;

            var empires = AccessTools.Field(galaxy.GetType(), "Empires")?.GetValue(galaxy);
            if (empires is null) return;

            var getPlayer = empires.GetType().GetMethod("GetPlayer", Type.EmptyTypes);
            if (getPlayer?.Invoke(empires, null) is not null) return;   // already fine

            int count = empires.GetType().GetProperty("Count")?.GetValue(empires) is int c ? c : 0;
            if (count == 0) { _log("# makesave: galaxy has no empires at all"); return; }

            var item = empires.GetType().GetMethod("get_Item", new[] { typeof(int) });

            // WHICH empire this machine plays. In competitive each player drives their own,
            // so it is their lobby slot; in co-op both drive slot 0. This is the payoff of
            // slot exchange -- StartGameExisting binds the entire UI to whichever empire
            // carries IsPlayer, so setting it here decides what this player commands.
            int wanted = Math.Clamp(Session?.PlayableEmpireIndex ?? 0, 0, count - 1);

            for (int offset = 0; offset < count; offset++)
            {
                int i = (wanted + offset) % count;   // preferred first, then any valid one

                var empire = item?.Invoke(empires, new object[] { i });
                var isPlayer = empire is null ? null : AccessTools.Field(empire.GetType(), "IsPlayer");
                if (isPlayer is null) continue;

                isPlayer.SetValue(empire, true);
                var name = AccessTools.Field(empire.GetType(), "Name")?.GetValue(empire);

                _log(i == wanted
                    ? $"# makesave: playing empire[{i}] '{name}' (slot {Session?.MySlot ?? 0}, mode {Session?.Mode ?? "solo"})"
                    : $"# makesave: empire[{wanted}] unusable; fell back to empire[{i}] '{name}'");
                return;
            }
        }
        catch (Exception ex)
        {
            _log("# makesave: EnsurePlayerEmpire failed " + (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>
    /// Returns an empty description instead of throwing when the settings entry is null.
    /// Returning false skips the original.
    /// </summary>
    private static bool SkipSituationDescription(ref string __result)
    {
        // Unconditional. The first attempt only skipped when the settings entry was null,
        // and it still threw — so the null is a FIELD inside a non-null entry (our
        // hand-built empire leaves several strings null). Rather than guess which, skip
        // the whole thing: this is only ever reached because MakeSave is active, and the
        // output is a flavour blurb that a throwaway test save does not need.
        __result = "";
        return false;
    }

    private static void OnUpdate(object __instance)
    {
        _game = __instance;

        try
        {
            if (!_started) { StartSmallGame(); return; }

            // In a multiplayer session the galaxy is generated to be PLAYED, not saved.
            // Saving would also exit the process, which is fine for the save-making tool
            // and catastrophic for a live session.
            if (Session is not null) { _saved = true; return; }

            if (_saved || _cyclesAtStart < 0) return;
            if (_cyclesSeen - _cyclesAtStart < SettleCycles) return;

            SaveIt();
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log($"# makesave: FAILED {cause.GetType().Name}: {cause.Message}");
            foreach (var line in (cause.StackTrace ?? "").Split('\n').Take(5))
                _log("#   " + line.TrimEnd());
            _saved = true;   // do not spin on the same failure every frame
        }
    }

    /// <summary>The agreed session, when the launcher supplied one.</summary>
    public static SessionConfig Session { get; set; }

    private static void StartSmallGame()
    {
        _started = true;

        var settings = Activator.CreateInstance(_settingsType);

        int stars = Session?.Galaxy.Stars ?? Stars;
        int ai = Session?.Galaxy.AiEmpires ?? OtherEmpires;
        int seed = Session?.Galaxy.Seed ?? 12345;

        // Defaults for everything except size and the one field whose absence broke
        // --new-game. Overriding more than necessary is how a subtly invalid
        // configuration gets built.
        SetField(settings, "StarCount", stars);
        SetField(settings, "OtherEmpiresAutoGenerateAmount", ai);
        SetField(settings, "RandomSeed", seed);

        if (Session is { Players.Count: > 0 }) AddSessionEmpires(settings);
        else AddPlayerEmpire(settings);

        _log($"# makesave: generating — stars={stars}, {ai} AI empire(s), seed={seed}");
        _startGameNew.Invoke(_game, new[] { settings });
        _log("# makesave: StartGameNew returned; waiting for the simulation to tick");
    }

    /// <summary>
    /// One GameStartSettingsEmpire per human player, built from the lobby's agreed
    /// session — so the galaxy the host generates actually contains both players'
    /// chosen empires rather than only the host's.
    ///
    /// In co-op both players drive the same empire, so only slot 0 is added; adding two
    /// would create a second empire nobody is playing.
    /// </summary>
    private static void AddSessionEmpires(object settings)
    {
        var empiresField = AccessTools.Field(_settingsType, "Empires");
        var empires = empiresField?.GetValue(settings);

        if (empires is null) { _log("# makesave: Empires list is null; falling back"); AddPlayerEmpire(settings); return; }

        var add = empires.GetType().GetMethod("Add", new[] { _empireType })
               ?? empires.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);

        if (add is null) { _log("# makesave: no Add on the empire list; falling back"); AddPlayerEmpire(settings); return; }

        var wanted = Session.IsCompetitive
            ? Session.Players
            : Session.Players.Take(1).ToList();

        foreach (var player in wanted)
        {
            var empire = Activator.CreateInstance(_empireType);

            SetField(empire, "IsPlayer", true);
            SetField(empire, "Name", player.Empire.Name);
            SetField(empire, "RaceId", (short)player.Empire.RaceId);
            SetField(empire, "GovernmentId", (short)player.Empire.GovernmentId);
            SetField(empire, "AllowAnyGovernment", true);

            add.Invoke(empires, new[] { empire });
            _log($"# makesave: empire for slot {player.Slot} — {player.Empire.Name} " +
                 $"(race {player.Empire.RaceId}, gov {player.Empire.GovernmentId})");
        }

        _log(Session.IsCompetitive
            ? $"# makesave: competitive — {wanted.Count} human empire(s)"
            : "# makesave: co-op shared — one empire for both players");
    }

    /// <summary>
    /// The whole reason --new-game fails: the stub settings contain no empire flagged
    /// IsPlayer, so InitializeSinglePlayerGame dereferences null.
    /// </summary>
    private static void AddPlayerEmpire(object settings)
    {
        var empiresField = AccessTools.Field(_settingsType, "Empires");
        var empires = empiresField?.GetValue(settings);

        if (empires is null)
        {
            _log("# makesave: Empires list is null; cannot add a player empire");
            return;
        }

        var empire = Activator.CreateInstance(_empireType);
        SetField(empire, "IsPlayer", true);
        SetField(empire, "Name", "MP Test");
        SetField(empire, "RaceId", (short)0);
        SetField(empire, "GovernmentId", (short)0);
        SetField(empire, "AllowAnyGovernment", true);

        var add = empires.GetType().GetMethod("Add", new[] { _empireType })
               ?? empires.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);

        if (add is null) { _log("# makesave: no Add method on the empire list"); return; }

        add.Invoke(empires, new[] { empire });

        var count = empires.GetType().GetProperty("Count")?.GetValue(empires);
        _log($"# makesave: player empire added (list count now {count})");
    }

    private static void SaveIt()
    {
        _saved = true;

        // SaveGame takes a PATH relative to the working directory, not a save name.
        // Passing a bare name writes "data/<name>" with no extension, which the game's
        // own load screen will not see. Discovered by finding the file in the wrong place.
        var path = Path.Combine("data", "SavedGames", SaveName + ".DWGame");

        _log($"# makesave: saving to '{path}'");
        AccessTools.Method(_game.GetType(), "CheckCreateSaveGameFolder")?.Invoke(_game, null);
        _saveGame.Invoke(_game, new object[] { path });

        _log("# makesave: SaveGame returned; exiting shortly");

        // Give the writer a moment; SaveGame may complete asynchronously.
        new Timer(_ =>
        {
            _log("# makesave: done");
            Environment.Exit(0);
        }, null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
    }

    private static void SetField(object target, string name, object value)
    {
        var f = AccessTools.Field(target.GetType(), name);
        if (f is null) { _log($"# makesave: field {name} not found"); return; }

        try { f.SetValue(target, Convert.ChangeType(value, f.FieldType)); }
        catch { try { f.SetValue(target, value); } catch { _log($"# makesave: could not set {name}"); } }
    }

    private static string Env(string n, string d) =>
        Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : d;

    private static int EnvInt(string n, int d) =>
        int.TryParse(Env(n, ""), out var v) ? v : d;
}
