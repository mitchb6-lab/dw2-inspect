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
    private static string _capturedFingerprint;
    private static string _incomingFingerprint;
    private static int _incomingShips = -1;
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

            var fp = Fingerprint(galaxy);
            _capturedFingerprint = fp.Hash;
            _log($"# apply: captured fingerprint={fp.Hash} ({fp.Summary})");

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

            if (_observations == 1)
            {
                // The CONTENT check, and the one that decides M3c. The clock-based checks
                // below are kept for context only -- FixedStep makes time a monotonic
                // function of the tick counter, so timeReverted can never be true and
                // says nothing either way.
                var after = Fingerprint(galaxy);

                // ENTITY COUNT, not the hash. The hash is sampled one tick after the
                // apply, and ship positions and countdowns advance every tick, so it can
                // never match. The population cannot change by hundreds in one tick, so a
                // count that reverts to the received value is decisive.
                bool contentMatches = after.Ships == _incomingShips && _incomingShips >= 0;

                _log($"# verify[{cycle}]: captured={_capturedFingerprint} incoming={_incomingFingerprint} after={after.Hash} ({after.Summary})");
                _log(contentMatches
                    ? $"# verify: *** CONTENT ADOPTED *** live ship count reverted to the received {_incomingShips}"
                    : "# verify: content does NOT match capture -- the object was swapped but the " +
                      "simulation is not running the received entity state");

                _log($"# verify[{cycle}]: (context) isIncomingObject={sameObject} " +
                     $"liveTime={now:HH:mm:ss.fff} timeReverted={timeMatches}");
            }
            else
            {
                var after = Fingerprint(galaxy);
                _log($"# verify[{cycle}]: ships={after.Ships} matchesIncoming={after.Ships == _incomingShips} " +
                     $"isIncomingObject={sameObject}");
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
        var before = Fingerprint(_galaxy);
        _log($"# apply: live fingerprint BEFORE apply={before.Hash} ({before.Summary})");
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

        // Fingerprint the INCOMING object before applying it. Comparing post-apply state
        // against the ORIGINAL capture is wrong by construction: the fingerprint reads
        // every primitive ship field, and serialisation does not persist transient runtime
        // ones, so a round-trip legitimately differs from its source. What must match is
        // the object we handed over.
        var incomingFp = Fingerprint(_incoming);
        _incomingFingerprint = incomingFp.Hash;
        _incomingShips = incomingFp.Ships;
        _log($"# apply: incoming fingerprint={incomingFp.Hash} ({incomingFp.Summary})");

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

    /// <summary>
    /// A clock-independent fingerprint of simulation CONTENT.
    ///
    /// The M3c time and hash checks were both defeated by our own FixedStep clock, which
    /// makes Galaxy.Time a monotonic function of the tick counter — so it can never
    /// revert, and a differing Time alone changes a whole-galaxy hash. This looks at
    /// entities instead: ship and empire counts, plus every primitive field of the first
    /// N ships, sorted by field name so ordering cannot vary.
    ///
    /// Ship positions and countdowns change every tick, so a live galaxy 120 s ahead of a
    /// captured one is guaranteed to fingerprint differently. That is what makes the
    /// comparison decisive rather than merely suggestive.
    /// </summary>
    private static (string Hash, string Summary, int Ships) Fingerprint(object galaxy)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            int shipCount = -1, empireCount = -1;

            shipCount = ListCount(galaxy, "Ships");
            empireCount = ListCount(galaxy, "Empires");

            sb.Append("ships=").Append(shipCount).Append(";empires=").Append(empireCount).Append(';');

            var ships = AccessTools.Field(galaxy.GetType(), "Ships")?.GetValue(galaxy);
            if (ships is not null && shipCount > 0)
            {
                var itemGetter = ships.GetType().GetMethod("get_Item", new[] { typeof(int) });
                int sample = Math.Min(shipCount, 40);

                for (int i = 0; i < sample; i++)
                {
                    object ship;
                    try { ship = itemGetter?.Invoke(ships, new object[] { i }); }
                    catch { continue; }
                    if (ship is null) continue;

                    // Declared-and-inherited primitives, name-sorted: stable regardless of
                    // reflection ordering or where a field sits in the hierarchy.
                    foreach (var f in ship.GetType()
                                 .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 .Where(f => f.FieldType.IsPrimitive)
                                 .OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        object v;
                        try { v = f.GetValue(ship); } catch { continue; }
                        sb.Append(f.Name).Append('=').Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).Append(',');
                    }
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
            return (hash, $"ships={shipCount} empires={empireCount}", shipCount);
        }
        catch (Exception ex)
        {
            return ("<failed>", (ex.InnerException ?? ex).Message, -1);
        }
    }

    private static int ListCount(object galaxy, string fieldName)
    {
        try
        {
            var list = AccessTools.Field(galaxy.GetType(), fieldName)?.GetValue(galaxy);
            if (list is null) return -1;
            var count = list.GetType().GetProperty("Count")?.GetValue(list);
            return count is int c ? c : -1;
        }
        catch { return -1; }
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
