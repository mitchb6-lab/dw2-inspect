using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M3b/M3c: measure what it costs a RUNNING client to adopt a received galaxy, and
/// prove it actually adopted it.
///
/// M3b established that DWGame.StartGameExisting returns in 8 ms. That proves the call
/// returned; it does not prove the client is now running the incoming state. A no-op
/// would also return in 8 ms, and building a network protocol on top of a silent no-op
/// would be an expensive mistake to discover later.
///
/// M3c makes adoption falsifiable:
///
///   1. CAPTURE an early galaxy — bytes, hash, and its Time.
///   2. Let the simulation run on for hundreds of ticks, so the live state genuinely
///      differs from the captured one.
///   3. APPLY the captured (older) state back.
///   4. OBSERVE the galaxy the server hands out afterwards. If its clock has gone
///      BACKWARDS to the captured time and its serialised hash matches the captured
///      hash, the client is provably running the received state.
///
/// Applying an older state is deliberate: time going backwards is unambiguous. Applying
/// a newer one could be confused with the simulation simply having advanced.
/// </summary>
public static class ApplyState
{
    private static object _game;
    private static object _galaxy;
    private static object _incoming;
    private static volatile bool _armed;
    private static volatile bool _applied;

    private static byte[] _capturedBytes;
    private static string _capturedHash;
    private static DateTime _capturedTime;

    private static MethodInfo _writeToStream;
    private static MethodInfo _readFromStream;
    private static MethodInfo _startGameExisting;
    private static Type _galaxyDataType;
    private static Action<string> _log;

    private static int _observations;

    public static bool Ready => _startGameExisting is not null;

    public static bool HasCapture => _capturedBytes is not null;

    public static bool Applied => _applied;

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

    /// <summary>Step 1: keep an early galaxy to send back later.</summary>
    public static void Capture(object galaxy)
    {
        try
        {
            var (bytes, hash) = Serialise(galaxy);
            _capturedBytes = bytes;
            _capturedHash = hash;
            _capturedTime = ReadTime(galaxy);

            _log($"# apply: CAPTURED {bytes.LongLength:N0}B  hash={hash}  time={_capturedTime:yyyy-MM-dd HH:mm:ss.fff}");
        }
        catch (Exception ex)
        {
            _log("# apply: capture failed " + (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>Step 3: request the apply. Runs on the next DWGame.Update (main thread).</summary>
    public static void Arm(object galaxy)
    {
        if (_applied || _capturedBytes is null) return;
        _galaxy = galaxy;
        _armed = true;
    }

    /// <summary>
    /// Step 4: watch the galaxy the server hands out after the apply. This is the
    /// verification — everything before it only shows that a method returned.
    /// </summary>
    public static void Observe(object galaxy, long cycle)
    {
        if (!_applied || _observations >= 3) return;

        try
        {
            _observations++;

            var now = ReadTime(galaxy);
            bool sameObject = ReferenceEquals(galaxy, _incoming);
            bool timeMatches = now == _capturedTime;

            string verdict;
            if (_observations == 1)
            {
                var (_, hash) = Serialise(galaxy);
                bool hashMatches = hash == _capturedHash;
                verdict = hashMatches ? "ADOPTED (hash matches capture)" : $"hash={hash} != captured {_capturedHash}";
                _log($"# verify[{cycle}]: liveTime={now:HH:mm:ss.fff} capturedTime={_capturedTime:HH:mm:ss.fff} " +
                     $"timeReverted={timeMatches} isIncomingObject={sameObject} -> {verdict}");
            }
            else
            {
                _log($"# verify[{cycle}]: liveTime={now:HH:mm:ss.fff} timeReverted={timeMatches} isIncomingObject={sameObject}");
            }
        }
        catch (Exception ex)
        {
            _log("# verify: failed " + (ex.InnerException ?? ex).Message);
            _observations = 99;
        }
    }

    private static void OnUpdate(object __instance)
    {
        _game = __instance;

        if (!_armed || _applied || _galaxy is null) return;
        _applied = true;

        try { RunApply(); }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log($"# apply: FAILED {cause.GetType().Name}: {cause.Message}");
            foreach (var line in (cause.StackTrace ?? "").Split('\n').Take(6))
                _log("#   " + line.TrimEnd());
        }
    }

    private static void RunApply()
    {
        var liveTimeBefore = ReadTime(_galaxy);
        _log($"# apply: live time before = {liveTimeBefore:yyyy-MM-dd HH:mm:ss.fff} " +
             $"(captured was {_capturedTime:HH:mm:ss.fff}, delta {(liveTimeBefore - _capturedTime).TotalSeconds:N1}s)");

        // Deserialise the CAPTURED bytes — a genuinely foreign, older galaxy.
        var sw = Stopwatch.StartNew();
        using var input = new MemoryStream(_capturedBytes, writable: false);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

        var args = new object[_readFromStream.GetParameters().Length];
        args[0] = reader;

        // Run the CONSTRUCTOR. GetUninitializedObject skips it, leaving collections the
        // constructor allocates null, which makes the read bail early and then kills
        // StartGameExisting inside LoadImagesForFacilities.
        object target = _readFromStream.IsStatic ? null : NewGalaxy(_galaxy.GetType());

        _incoming = _readFromStream.Invoke(target, args);
        double readMs = sw.Elapsed.TotalMilliseconds;

        if (_incoming is null) { _log("# apply: ReadFromStream returned null"); return; }

        var incomingData = args.Length > 1 ? args[1] : Activator.CreateInstance(_galaxyDataType);

        // A deserialised galaxy carries per-game state but not the static definition
        // tables (facilities, races, components), and asset loading walks those.
        var copyStatics = AccessTools.Method(_incoming.GetType(), "CopyStaticBaseDataToGalaxyInstance");
        copyStatics?.Invoke(copyStatics.IsStatic ? null : _incoming, new[] { _incoming });

        sw.Restart();
        _startGameExisting.Invoke(_game, new[] { _incoming, ReadStartTime(_incoming), incomingData });
        double applyMs = sw.Elapsed.TotalMilliseconds;

        _log($"# apply: raw={_capturedBytes.LongLength:N0}B  deserialise={readMs:N0}ms  " +
             $"StartGameExisting={applyMs:N0}ms  (total client cost {readMs + applyMs:N0}ms)");
        _log("# apply: applied; watching the next server cycles to verify adoption");
    }

    private static (byte[] Bytes, string Hash) Serialise(object galaxy)
    {
        var data = Activator.CreateInstance(_galaxyDataType);
        using var buffer = new MemoryStream(48 * 1024 * 1024);

        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            _writeToStream.Invoke(galaxy, new[] { writer, data });
            writer.Flush();
        }

        byte[] raw = buffer.ToArray();
        return (raw, Convert.ToHexString(SHA256.HashData(raw))[..16]);
    }

    private static DateTime ReadTime(object galaxy) =>
        (DateTime)(AccessTools.Field(galaxy.GetType(), "Time")?.GetValue(galaxy) ?? DateTime.MinValue);

    private static object ReadStartTime(object galaxy) =>
        AccessTools.Field(galaxy.GetType(), "TimeStart")?.GetValue(galaxy) ?? DateTime.Now;

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
}
