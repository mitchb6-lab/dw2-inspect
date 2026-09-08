using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M3b: measure what it costs a RUNNING client to adopt a received galaxy.
///
/// M3a showed deserialising a 39 MB late-game galaxy costs 220 ms. That buys a Galaxy
/// object and nothing more. The number that actually decides host-authoritative is what
/// DWGame.StartGameExisting(Galaxy, DateTime, GameGalaxyData) costs — rebinding the
/// scene, recreating visual entities, rebuilding renderer caches. That is the hitch a
/// player would feel on every sync.
///
/// Two details make this measurable honestly:
///
/// The DWGame instance is captured by patching DWGame.Update rather than hunted for in a
/// static, because nothing exposes one — and it has the better property of running the
/// measurement ON THE MAIN GAME THREAD, which is where StartGameExisting expects to be.
/// Timing it from the server-cycle thread would measure a crash, or a lock, or both.
///
/// The galaxy is round-tripped through serialise/deserialise first, so the object handed
/// to StartGameExisting is a genuinely foreign one, exactly as a received state would be.
/// Passing the live galaxy back would measure a no-op.
/// </summary>
public static class ApplyState
{
    private static object _game;
    private static object _galaxy;
    private static volatile bool _armed;
    private static volatile bool _done;

    private static MethodInfo _writeToStream;
    private static MethodInfo _readFromStream;
    private static MethodInfo _startGameExisting;
    private static Type _galaxyDataType;
    private static Action<string> _log;

    public static bool Ready => _startGameExisting is not null;

    public static void Install(Harmony harmony, MethodInfo writeToStream, Type galaxyDataType, Action<string> log)
    {
        _writeToStream = writeToStream;
        _galaxyDataType = galaxyDataType;
        _log = log;

        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var galaxyType = AccessTools.TypeByName("DistantWorlds.Types.Galaxy");
        if (gameType is null || galaxyType is null) { log("# apply: DWGame or Galaxy not found"); return; }

        _startGameExisting = AccessTools.Method(gameType, "StartGameExisting");
        if (_startGameExisting is null) { log("# apply: StartGameExisting not found"); return; }

        _readFromStream = galaxyType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "ReadFromStream" &&
                                 m.GetParameters().FirstOrDefault()?.ParameterType == typeof(BinaryReader));

        if (_readFromStream is null) { log("# apply: Galaxy.ReadFromStream not found"); return; }

        var update = AccessTools.Method(gameType, "Update");
        if (update is null) { log("# apply: DWGame.Update not found"); return; }

        harmony.Patch(update, new HarmonyMethod(typeof(ApplyState).GetMethod(
            nameof(OnUpdate), BindingFlags.NonPublic | BindingFlags.Static)));

        log("# apply: armed on DWGame.Update (main thread)");
    }

    /// <summary>Called from the server cycle once enough ticks have run.</summary>
    public static void Arm(object galaxy)
    {
        if (_done) return;
        _galaxy = galaxy;
        _armed = true;
    }

    private static void OnUpdate(object __instance)
    {
        _game = __instance;

        if (!_armed || _done || _galaxy is null) return;
        _done = true;

        try { RunMeasurement(); }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log($"# apply: FAILED {cause.GetType().Name}: {cause.Message}");

            // Without the stack this says only "something was null", which is useless
            // when the measurement has four distinct stages that could each throw.
            foreach (var line in (cause.StackTrace ?? "").Split('\n').Take(8))
                _log("#   " + line.TrimEnd());
        }

        _log("# apply: complete");
    }

    private static void RunMeasurement()
    {
        // 1. Serialise the live galaxy — stands in for the host's outbound state.
        var outboundData = Activator.CreateInstance(_galaxyDataType);
        var buffer = new MemoryStream(48 * 1024 * 1024);

        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            _writeToStream.Invoke(_galaxy, new[] { writer, outboundData });
            writer.Flush();
        }

        byte[] raw = buffer.ToArray();

        _log($"# apply: serialised {raw.LongLength:N0}B");

        // 2. Deserialise into a foreign Galaxy — stands in for the client's inbound state.
        var sw = Stopwatch.StartNew();
        using var input = new MemoryStream(raw, writable: false);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

        var args = new object[_readFromStream.GetParameters().Length];
        args[0] = reader;

        // Run the CONSTRUCTOR. GetUninitializedObject skips it, which leaves every
        // collection the constructor allocates as null — and StartGameExisting walks
        // those while loading facility images, so it dies with a bare
        // NullReferenceException that looks like the game rejecting foreign state.
        object target = _readFromStream.IsStatic ? null : NewGalaxy(_galaxy.GetType());

        var incoming = _readFromStream.Invoke(target, args);
        double readMs = sw.Elapsed.TotalMilliseconds;

        _log($"# apply: deserialised in {readMs:N0}ms -> {incoming?.GetType().Name ?? "null"}");

        // ReadFromStream fills the GameGalaxyData out-param; use THAT one, not a blank,
        // or the applied state carries no view/timing context.
        var incomingData = args.Length > 1 ? args[1] : outboundData;

        if (incoming is null) { _log("# apply: ReadFromStream returned null; cannot apply"); return; }

        // A deserialised galaxy carries per-game state but not the static definition
        // tables (facilities, races, components). Galaxy exposes an explicit method for
        // exactly this, and the crash was inside facility image loading — i.e. reading
        // those tables.
        var copyStatics = AccessTools.Method(incoming.GetType(), "CopyStaticBaseDataToGalaxyInstance");
        if (copyStatics is not null)
        {
            try
            {
                copyStatics.Invoke(copyStatics.IsStatic ? null : incoming, new[] { incoming });
                _log("# apply: static base data copied into incoming galaxy");
            }
            catch (Exception ex)
            {
                _log("# apply: CopyStaticBaseDataToGalaxyInstance failed: " + (ex.InnerException ?? ex).Message);
            }
        }
        else _log("# apply: CopyStaticBaseDataToGalaxyInstance not found");

        // 3. THE MEASUREMENT: make the running client adopt it.
        var startTime = ReadStartTime(incoming);

        _log($"# apply: calling StartGameExisting(game={_game != null}, data={incomingData != null}, t={startTime})");

        sw.Restart();
        _startGameExisting.Invoke(_game, new[] { incoming, startTime, incomingData });
        double applyMs = sw.Elapsed.TotalMilliseconds;

        _log($"# apply: raw={raw.LongLength:N0}B  deserialise={readMs:N0}ms  " +
             $"StartGameExisting={applyMs:N0}ms  (total client cost {readMs + applyMs:N0}ms)");
    }

    /// <summary>
    /// A Galaxy with its constructor actually run, falling back to an uninitialised
    /// object only if there is no usable constructor.
    /// </summary>
    private static object NewGalaxy(Type galaxyType)
    {
        try { return Activator.CreateInstance(galaxyType, nonPublic: true); }
        catch (Exception ex)
        {
            _log("# apply: Galaxy ctor failed (" + (ex.InnerException ?? ex).GetType().Name +
                 "); falling back to uninitialised object");
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(galaxyType);
        }
    }

    private static object ReadStartTime(object galaxy)
    {
        var field = AccessTools.Field(galaxy.GetType(), "TimeStart");
        return field is not null ? field.GetValue(galaxy) : DateTime.Now;
    }
}
