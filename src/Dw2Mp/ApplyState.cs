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

    /// <summary>
    /// Ready means the PATH is resolved. CanApply means there is actually a DWGame to
    /// adopt into — which is later, and is the distinction the client got wrong: the
    /// host's first sync arrived before the client had finished creating its throwaway
    /// game, the frame was dropped as unappliable, and the client then spent the whole
    /// run in its own divergent galaxy.
    /// </summary>
    public static bool CanApply => _startGameExisting is not null
                                && _readFromStream is not null
                                && _game is not null;

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

        GuardMessageDialog(harmony, log);
        GuardSimulationTasks(harmony, log);

        log("# apply: armed on DWGame.Update (main thread)");
    }

    /// <summary>The live DWGame, captured from Update. Null until the game has ticked once.</summary>
    public static object Game => _game;

    /// <summary>
    /// The galaxy the game is actually running, read live from DWGame rather than cached.
    ///
    /// Cached would be wrong: adoption replaces it, and a stale reference would have the
    /// client comparing the host's fingerprint against a galaxy it no longer plays.
    /// </summary>
    public static object CurrentGalaxy
    {
        get
        {
            try
            {
                return _game is null
                    ? null
                    : AccessTools.PropertyGetter(_game.GetType(), "Galaxy")?.Invoke(_game, null);
            }
            catch { return null; }
        }
    }

    /// <summary>Serialise a galaxy to bytes. Used by the host to produce a state sync.</summary>
    public static byte[] SerialiseGalaxy(object galaxy) => Serialise(galaxy).Bytes;

    /// <summary>
    /// Adopt a galaxy received from elsewhere. MUST be called on the main game thread —
    /// StartGameExisting touches the content manager, and M3b died inside
    /// LoadImagesForFacilities when this ran off-thread.
    ///
    /// This is the M3-proven sequence, extracted so the network layer reuses exactly the
    /// path that was verified rather than a second copy that quietly drifts from it.
    /// </summary>
    public static bool ApplyBytes(byte[] raw, out string info)
    {
        info = "";

        if (_game is null) { info = "no DWGame captured yet"; return false; }
        if (_readFromStream is null || _startGameExisting is null) { info = "apply path not resolved"; return false; }

        try
        {
            var sw = Stopwatch.StartNew();

            using var input = new MemoryStream(raw, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            var args = new object[_readFromStream.GetParameters().Length];
            args[0] = reader;

            // Constructor, not GetUninitializedObject: skipping it leaves the collections
            // it allocates null, which kills StartGameExisting inside asset loading.
            object target = _readFromStream.IsStatic ? null : NewGalaxy(_readFromStream.DeclaringType);

            var incoming = _readFromStream.Invoke(target, args);
            if (incoming is null) { info = "ReadFromStream returned null"; return false; }

            double readMs = sw.Elapsed.TotalMilliseconds;

            var incomingData = args.Length > 1 && args[1] is not null
                ? args[1]
                : Activator.CreateInstance(_galaxyDataType);

            // A deserialised galaxy carries per-game state but not the static definition
            // tables that asset loading walks.
            var copyStatics = AccessTools.Method(incoming.GetType(), "CopyStaticBaseDataToGalaxyInstance");
            copyStatics?.Invoke(copyStatics.IsStatic ? null : incoming, new[] { incoming });

            RebuildPathData(incoming);
            RegenerateShipSummaries(incoming);
            CheckEmpirePolicies(incoming);

            // Mirror the game's own load sequence. DW2 deserialises galaxies constantly --
            // every save load -- and it does three things around it that StartGameExisting
            // alone does not. Skipping them is why adoption produced hundreds of scattered
            // NullReferenceExceptions across research, refuelling, corruption and pirate
            // code. See AdoptionFixup for what each one is for.
            QuiesceBeforeSwap();

            sw.Restart();
            _startGameExisting.Invoke(_game, new[] { incoming, ReadStartTime(incoming), incomingData });
            ResetDerivedCaches(incoming);
            double applyMs = sw.Elapsed.TotalMilliseconds;

            _incoming = incoming;
            var fp = Fingerprint(incoming);
            _applyCount++;
            info = $"{raw.LongLength:N0}B  read={readMs:N0}ms  apply={applyMs:N0}ms  {fp.Summary}  {MemoryReport()}";
            return true;
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            info = $"{cause.GetType().Name}: {cause.Message}";
            return false;
        }
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
        ResetDerivedCaches(_incoming);
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

    /// <summary>
    /// Re-key the galaxy's derived path cache to the galaxy it now belongs to.
    ///
    /// SystemPathTimeSet._PathTimesPerSystem is a raw SortedList[] indexed directly by
    /// systemId, and GetPathTimesForSystem does `_PathTimesPerSystem[systemId]` with NO
    /// bounds check — it validates that systemId is non-negative and that the system
    /// exists in galaxy.Systems, then indexes an array that may be shorter than either.
    ///
    /// Adopting a galaxy is exactly the case that breaks it: the cache and the systems it
    /// is indexed by stop agreeing. This produced 518 IndexOutOfRangeExceptions in the
    /// 2026-09-08 two-instance run, all of them
    /// Colony.CalculateCorruption -> FindNearestCapitalByCorruptionReductionRatio ->
    /// GetPathTimesForSystem, and DW2 swallowed every one.
    ///
    /// Clear(Galaxy) is the game's own fix and does precisely the right thing: allocate
    /// a fresh array of galaxy.Systems.GetNextIdRaw() entries and Interlocked.Exchange it
    /// in. Losing the cached path times costs recomputation, which is the correct trade —
    /// they describe a galaxy that is no longer loaded.
    ///
    /// SystemPathTimeExpiry is reset too so the next lookup recomputes rather than
    /// trusting a starDate from the previous galaxy's timeline.
    /// </summary>
    private static void ResetDerivedCaches(object galaxy)
    {
        // Kill-switch so the fix can be A/B tested against itself. A run that produces no
        // crashes proves nothing unless the same run WITH the fix disabled produces them.
        if (Environment.GetEnvironmentVariable("DW2MP_RESET_PATH_CACHE") == "0")
        {
            _log("# apply: path cache reset DISABLED (DW2MP_RESET_PATH_CACHE=0)");
            return;
        }

        try
        {
            var type = galaxy.GetType();
            var cache = AccessTools.Field(type, "SystemPathTimesCached")?.GetValue(galaxy);

            if (cache is null) { _log("# apply: no SystemPathTimesCached to reset"); return; }

            var clear = AccessTools.Method(cache.GetType(), "Clear", new[] { type });
            if (clear is null) { _log("# apply: SystemPathTimeSet.Clear(Galaxy) not found"); return; }

            clear.Invoke(cache, new[] { galaxy });
            AccessTools.Field(type, "SystemPathTimeExpiry")?.SetValue(galaxy, 0L);

            _log("# apply: path cache re-keyed to the adopted galaxy");

            // The UI's message list survives the swap and then binds against objects from
            // the galaxy we just discarded. That is not cosmetic: EmpireMessageDialog.BindData
            // throws NullReferenceException out of MessageListView.Update, which is inside
            // DWGame.Update, so nothing catches it and the CLIENT PROCESS DIES. It killed
            // the client ~45s into every two-instance run on 2026-09-08, and looked like a
            // hang because a dead process stops writing watchdog lines.
            //
            // DWGame._ShouldRegenerateMessageLog is the game's own "this list is stale"
            // flag; setting it makes DW2 rebuild from the adopted galaxy at its own safe
            // point rather than us rebuilding UI state from under a frame in progress.
            var dwGameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
            if (dwGameType is not null && _game is not null)
            {
                var flag = AccessTools.Field(dwGameType, "_ShouldRegenerateMessageLog");
                if (flag is not null)
                {
                    flag.SetValue(_game, true);
                    _log("# apply: message log flagged for regeneration");
                }
                else _log("# apply: _ShouldRegenerateMessageLog not found");
            }
        }
        catch (Exception ex)
        {
            _log("# apply: path cache reset failed " + (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>
    /// Stops a message the UI cannot bind from killing the process.
    ///
    /// A galaxy swap leaves the message UI holding messages that refer to objects which no
    /// longer resolve. EmpireMessageDialog.BindData then throws NullReferenceException, and
    /// because the call chain is MessageListView.Update -> ... inside DWGame.Update, nothing
    /// catches it and the CLIENT PROCESS DIES. It killed the client ~45s into every
    /// two-instance run on 2026-09-08 and looked like a hang, because a dead process stops
    /// writing watchdog lines.
    ///
    /// Guarding only BindData was tried first and merely MOVED the crash one frame up into
    /// SetEmpireMessageDialogData, which dereferences the result. So the guard goes at the
    /// TOP of the subtree — MessageListView.Update — with the inner methods kept as belt
    /// and braces. Swallowing one frame of message-list UI is self-correcting; the next
    /// frame runs again.
    ///
    /// This is the seatbelt, not the fix. Flagging the message log for regeneration is the
    /// fix. But a mod that swaps state underneath a UI it does not fully understand should
    /// not be one null away from killing the game.
    /// </summary>
    private static void GuardMessageDialog(Harmony harmony, Action<string> log)
    {
        // Outermost first: whichever resolves, the subtree below it is covered.
        var targets = new (string Type, string Method)[]
        {
            ("DistantWorlds.UI.MessageListView",          "Update"),
            ("DistantWorlds.UI.MessageListView",          "ProcessInvestigationMessages"),
            ("DistantWorlds.UI.UserInterfaceController",  "SetEmpireMessageDialogData"),
            ("DistantWorlds.UI.EmpireMessageDialog",      "BindData"),
        };

        var finalizer = new HarmonyMethod(typeof(ApplyState).GetMethod(
            nameof(SwallowBindFailure), BindingFlags.NonPublic | BindingFlags.Static));

        var guarded = new List<string>();

        foreach (var (typeName, methodName) in targets)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type is null ? null : AccessTools.Method(type, methodName);
                if (method is null) continue;

                harmony.Patch(method, finalizer: finalizer);
                guarded.Add(methodName);
            }
            catch (Exception ex)
            {
                log($"# apply: could not guard {methodName} ({ex.GetType().Name})");
            }
        }

        log(guarded.Count == 0
            ? "# apply: NO message UI guard installed — a stale message can kill the client"
            : "# apply: guarded message UI (" + string.Join(", ", guarded) + ")");
    }

    private static bool _loggedBindFailure;

    /// <summary>
    /// Harmony finalizer. Returning null swallows the exception; the cost is a popup that
    /// does not appear, against a client that dies.
    /// </summary>
    private static Exception SwallowBindFailure(Exception __exception)
    {
        if (__exception is null) return null;

        if (!_loggedBindFailure)
        {
            _loggedBindFailure = true;
            _log?.Invoke("# apply: a message the UI could not bind was skipped (" +
                         __exception.GetType().Name + "); logged once, further ones are silent");
        }

        return null;
    }

    // ------------------------------------------------- adoption fixup
    //
    // AdoptionFixup, in one place because the reasoning is shared.
    //
    // StartGameExisting replaces the galaxy but does NOT invalidate state derived from it,
    // and it is not the whole of what DW2 does when it loads a galaxy. Comparing the game's
    // own path against ours:
    //
    //   DWGame.BeginLoadGame   -> Galaxy.WaitAllTasksCompleted()      we skipped
    //                          -> DWGame.ClearMessageQueues()          we skipped
    //   DWGame.LoadGame        -> PathFindingSystem.CalculateSystemDistances(galaxy)
    //                                                                  we skipped
    //                          -> CheckInitializeOrBindSinglePlayerGame
    //   DWGame.StartGameExisting -> Resource.ClearCachedValues()
    //                            -> CheckInitializeOrBindSinglePlayerGame
    //
    // So adoption was doing the bind and none of the preparation. Every one of these was
    // established by reading the game's own load sequence rather than guessing at field
    // names, which matters: the first attempt at this problem chased individual cache
    // fields, and there are at least eight on Empire alone (RefuellingPointsPerSystem,
    // RefuellingPointsPerSystemMilitary, RefuellingSystems, RefuellingSystemsMilitary,
    // RepairBasesPerSystem, RepairSystems, ConstructionBasesPerSystem,
    // _PathFindingDangerousSystems). Replicating the load path fixes the class.
    //
    // Set DW2MP_ADOPT_FIXUP=0 to disable, so the fix can be A/B tested against itself.

    private static bool FixupEnabled => Environment.GetEnvironmentVariable("DW2MP_ADOPT_FIXUP") != "0";

    /// <summary>
    /// Stop the world before swapping it.
    ///
    /// DW2 runs galaxy work across many threads. Replacing the galaxy while those tasks are
    /// in flight leaves worker threads walking half-old, half-new structures — which is
    /// exactly the shape of the failures adoption produced: scattered across unrelated
    /// subsystems, and transient rather than every call. Ship.Summary reading null inside
    /// IdentifyRefuellingPointBasesWeCanDockAt is one symptom of many.
    ///
    /// Message queues are cleared for the same reason the game clears them: they hold
    /// EmpireMessages referring to objects the swap is about to discard, and binding one
    /// afterwards throws out of DWGame.Update where nothing catches it.
    /// </summary>
    private static void QuiesceBeforeSwap()
    {
        if (!FixupEnabled) { _log("# apply: adoption fixup DISABLED (DW2MP_ADOPT_FIXUP=0)"); return; }

        try
        {
            var current = AccessTools.PropertyGetter(_game.GetType(), "Galaxy")?.Invoke(_game, null);

            if (current is not null)
            {
                AccessTools.Method(current.GetType(), "WaitAllTasksCompleted", Type.EmptyTypes)
                          ?.Invoke(current, null);
            }

            AccessTools.Method(_game.GetType(), "ClearMessageQueues", Type.EmptyTypes)
                      ?.Invoke(_game, null);

            if (!_loggedQuiesce)
            {
                _loggedQuiesce = true;
                _log("# apply: quiesced — galaxy tasks drained, message queues cleared");
            }
        }
        catch (Exception ex)
        {
            _log("# apply: quiesce failed " + (ex.InnerException ?? ex).Message);
        }
    }

    private static bool _loggedQuiesce;
    private static bool _loggedPathRebuild;

    /// <summary>
    /// Rebuild inter-system distances for the incoming galaxy.
    ///
    /// DWGame.LoadGame calls this before binding the game, and it is the step that makes
    /// pathfinding valid for the galaxy actually loaded. Without it the pathing tables
    /// describe whatever galaxy was here before.
    /// </summary>
    private static void RebuildPathData(object galaxy)
    {
        if (!FixupEnabled) return;

        try
        {
            var pathfinding = AccessTools.TypeByName("DistantWorlds.Types.PathFindingSystem");
            var calc = pathfinding is null
                ? null
                : AccessTools.Method(pathfinding, "CalculateSystemDistances", new[] { galaxy.GetType() });

            if (calc is null) { _log("# apply: CalculateSystemDistances not found"); return; }

            var sw = Stopwatch.StartNew();
            calc.Invoke(calc.IsStatic ? null : pathfinding, new[] { galaxy });

            if (!_loggedPathRebuild)
            {
                _loggedPathRebuild = true;
                _log($"# apply: system distances rebuilt for the adopted galaxy ({sw.Elapsed.TotalMilliseconds:N0}ms)");
            }
        }
        catch (Exception ex)
        {
            _log("# apply: system distance rebuild failed " + (ex.InnerException ?? ex).Message);
        }
    }

    private static bool _loggedSummaries;

    /// <summary>
    /// Rebuild Ship.Summary on the adopted galaxy.
    ///
    /// ShipSummary is DERIVED data and ReadFromStream does not restore it, so ships arrive
    /// with Summary null. IdentifyRefuellingPointBasesWeCanDockAt then does
    /// `ship.Summary.DockingBayCount` with no null check and throws — 68 of the 106 crashes
    /// remaining after the load-sequence fixup, and the single largest family.
    ///
    /// Ship.RegenerateSummary() is the game's own rebuild. Only null summaries are touched,
    /// which keeps the change minimal and makes the logged count evidence: if every ship
    /// needed one, the field genuinely is not serialised.
    ///
    /// Runs on the incoming galaxy BEFORE it goes live, so nothing is iterating it yet.
    /// </summary>
    private static void RegenerateShipSummaries(object galaxy)
    {
        if (!FixupEnabled) return;

        try
        {
            var ships = AccessTools.Field(galaxy.GetType(), "Ships")?.GetValue(galaxy);
            if (ships is null) { _log("# apply: no Ships collection to rebuild"); return; }

            var listType = ships.GetType();
            var count = AccessTools.PropertyGetter(listType, "Count")?.Invoke(ships, null) is int c ? c : 0;
            var item = AccessTools.Method(listType, "get_Item", new[] { typeof(int) });
            if (item is null) { _log("# apply: ShipList has no indexer"); return; }

            FieldInfo summaryField = null;
            MethodInfo regenerate = null;
            int rebuilt = 0;

            for (int i = 0; i < count; i++)
            {
                var ship = item.Invoke(ships, new object[] { i });
                if (ship is null) continue;

                summaryField ??= AccessTools.Field(ship.GetType(), "Summary");
                regenerate ??= AccessTools.Method(ship.GetType(), "RegenerateSummary", Type.EmptyTypes);
                if (summaryField is null || regenerate is null) break;

                if (summaryField.GetValue(ship) is not null) continue;

                regenerate.Invoke(ship, null);
                rebuilt++;
            }

            if (!_loggedSummaries)
            {
                _loggedSummaries = true;
                _log($"# apply: rebuilt {rebuilt} of {count} ship summaries (null after deserialisation)");
            }
        }
        catch (Exception ex)
        {
            _log("# apply: ship summary rebuild failed " + (ex.InnerException ?? ex).Message);
        }
    }

    private static int _applyCount;

    /// <summary>
    /// Memory after an adoption, so a leak can be characterised instead of guessed at.
    ///
    /// Three numbers, because they separate three different causes:
    ///   managed  — the GC heap. Growing here means objects are RETAINED (old galaxies).
    ///   private  — the whole process. Growing while managed is flat means native memory
    ///              (Stride/graphics) or Large Object Heap fragmentation, since a full
    ///              state is a 24 MB byte[] and every one of those goes straight to the LOH.
    ///   afterGC  — managed after a forced blocking collect. If this returns to baseline the
    ///              growth was uncollected garbage, not a leak.
    ///
    /// The forced collect runs occasionally rather than every time: it is expensive, and the
    /// point is to characterise the trend, not to paper over it by collecting.
    /// </summary>
    private static string MemoryReport()
    {
        try
        {
            var managed = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            var privateMb = Process.GetCurrentProcess().PrivateMemorySize64 / (1024.0 * 1024.0);

            var line = $"managed={managed:N0}MB private={privateMb:N0}MB";

            if (_applyCount % 5 == 0)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                var afterGc = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

                // Does a COMPACTING collect give memory back to the OS? Every sync is a
                // ~24 MB byte[], which goes straight to the Large Object Heap, and the LOH
                // is not compacted by default -- so committed segments grow while live
                // objects do not. That is exactly the shape observed: managed flat at
                // ~543 MB, private climbing 3.5 -> 8.1 GB. This distinguishes LOH
                // fragmentation from native (graphics) growth.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);

                var privateAfter = Process.GetCurrentProcess().PrivateMemorySize64 / (1024.0 * 1024.0);
                line += $" afterGC={afterGc:N0}MB afterLOHCompact={privateAfter:N0}MB";
            }

            return line;
        }
        catch { return "memory unavailable"; }
    }

    /// <summary>
    /// Keeps one unlucky object from killing the whole client.
    ///
    /// DW2 runs Empire/Ship/Orb/Colony tasks across worker threads, and it guards MOST of
    /// them itself — that is why hundreds of NullReferenceExceptions were survivable, each
    /// writing a dump and continuing. The paths it does NOT guard take the process with
    /// them: an unhandled exception on a .NET background thread terminates the process. One
    /// such escape, in Ship.DoTasks -> BasesCheckForRetrofitNotAtColony ->
    /// Design.CalculateAdvancedTechScore, killed the client at ~180s having applied 18
    /// syncs, with everything else healthy.
    ///
    /// Swallowing here is defensible SPECIFICALLY because of host-authoritative design: the
    /// host owns the truth and re-syncs the whole galaxy every 1,200 ticks, so a skipped
    /// tick for one ship on the client is corrected by the next sync. It would NOT be
    /// defensible on the host, and it would not be defensible in a peer-to-peer lockstep
    /// design, where a skipped tick is a divergence.
    ///
    /// This is not a licence to stop fixing roots. Every DISTINCT failure — method,
    /// exception type and throwing frame — is logged once, so new ones stay visible instead
    /// of being silently absorbed. The counts are reported so a rare escape is not confused
    /// with a storm.
    /// </summary>
    private static void GuardSimulationTasks(Harmony harmony, Action<string> log)
    {
        var targets = new (string Type, string Method)[]
        {
            ("DistantWorlds.Types.Empire",  "DoTasks"),
            ("DistantWorlds.Types.Empire",  "DoTasksHighPriority"),
            ("DistantWorlds.Types.Ship",    "DoTasks"),
            ("DistantWorlds.Types.Orb",     "DoTasks"),
            ("DistantWorlds.Types.Colony",  "DoTasks"),
            ("DistantWorlds.Types.Fleet",   "DoTasks"),
            // Added on evidence: the Design.CalculateAdvancedTechScore root reached the
            // client through ConstructionSystem, which the first guard list missed.
            ("DistantWorlds.Types.ConstructionSystem", "DoTasks"),
        };

        var finalizer = new HarmonyMethod(typeof(ApplyState).GetMethod(
            nameof(SurviveTaskFailure), BindingFlags.NonPublic | BindingFlags.Static));

        var guarded = new List<string>();

        foreach (var (typeName, methodName) in targets)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type is null ? null : AccessTools.Method(type, methodName);
                if (method is null) continue;

                harmony.Patch(method, finalizer: finalizer);
                guarded.Add($"{typeName.Split('.')[^1]}.{methodName}");
            }
            catch (Exception ex)
            {
                log($"# apply: could not guard {typeName}.{methodName} ({ex.GetType().Name})");
            }
        }

        log(guarded.Count == 0
            ? "# apply: NO simulation-task guard installed — one bad object can kill the client"
            : "# apply: guarded simulation tasks (" + string.Join(", ", guarded) + ")");
    }

    private static readonly HashSet<string> _seenTaskFailures = new();
    private static long _taskFailureCount;

    /// <summary>
    /// Harmony finalizer. Returns null to swallow. Logs each DISTINCT failure once, keyed
    /// by the guarded method, the exception type and the frame that actually threw — so a
    /// new root cause is visible the first time it happens rather than hidden in a count.
    /// </summary>
    private static Exception SurviveTaskFailure(Exception __exception, MethodBase __originalMethod)
    {
        if (__exception is null) return null;

        try
        {
            Interlocked.Increment(ref _taskFailureCount);

            var cause = __exception.InnerException ?? __exception;
            var frame = cause.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "<no stack>";
            var key = $"{__originalMethod?.Name}|{cause.GetType().Name}|{frame}";

            bool isNew;
            lock (_seenTaskFailures) isNew = _seenTaskFailures.Add(key);

            if (isNew)
                _log?.Invoke($"# apply: SURVIVED a task failure in {__originalMethod?.DeclaringType?.Name}." +
                             $"{__originalMethod?.Name} — {cause.GetType().Name} at {frame} " +
                             $"(distinct #{_seenTaskFailures.Count}, {Interlocked.Read(ref _taskFailureCount)} total)");
        }
        catch { /* a guard must never throw */ }

        return null;
    }

    /// <summary>Total task failures swallowed, for end-of-run reporting.</summary>
    public static long TaskFailures => Interlocked.Read(ref _taskFailureCount);

    /// <summary>
    /// A cheap STRUCTURAL summary of a galaxy: which ships exist, and how many of the big
    /// collections there are. Deliberately NOT positions, health or any continuous value.
    ///
    /// This is what decides when the client needs a full state, so what it does and does
    /// not notice IS the policy:
    ///
    ///   noticed     — a ship built, destroyed or removed; an empire eliminated. The client's
    ///                 view is then materially wrong and no amount of local simulation will
    ///                 repair it.
    ///   NOT noticed — every ship being a few metres from where the host has it. DW2 is not
    ///                 deterministic, so the client's own simulation diverges continuously
    ///                 from the host's. A fingerprint that included positions would mismatch
    ///                 within a tick or two and demand a resync every time, which is the
    ///                 fixed timer again wearing a smarter hat.
    ///
    /// Sorted by ShipId so the two sides compare the same set regardless of list order.
    /// Cost is one pass over the ship list reading two fields — a few thousand reflection
    /// calls, against 24 MB of serialisation for a full state.
    /// </summary>
    public static string StructuralFingerprint(object galaxy)
    {
        if (galaxy is null) return "";

        try
        {
            var sb = new System.Text.StringBuilder();
            var type = galaxy.GetType();

            sb.Append("e=").Append(ListCount(galaxy, "Empires"))
              .Append(";o=").Append(ListCount(galaxy, "Orbs"))
              .Append(";s=");

            var ships = AccessTools.Field(type, "Ships")?.GetValue(galaxy);
            var count = ListCount(galaxy, "Ships");
            sb.Append(count).Append(';');

            if (ships is not null && count > 0)
            {
                var item = ships.GetType().GetMethod("get_Item", new[] { typeof(int) });
                FieldInfo idField = null, destroyedField = null;
                var ids = new List<long>(count);

                for (int i = 0; i < count; i++)
                {
                    object ship;
                    try { ship = item?.Invoke(ships, new object[] { i }); }
                    catch { continue; }
                    if (ship is null) continue;

                    idField ??= AccessTools.Field(ship.GetType(), "ShipId");
                    destroyedField ??= AccessTools.Field(ship.GetType(), "IsDestroyedCached");
                    if (idField is null) break;

                    var id = Convert.ToInt64(idField.GetValue(ship));
                    var dead = destroyedField?.GetValue(ship) is true;

                    // Fold "destroyed" into the key so a ship dying is a structural change,
                    // not merely a field the summary happens not to read.
                    ids.Add(dead ? -id - 1 : id);
                }

                ids.Sort();
                foreach (var id in ids) sb.Append(id).Append(',');
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return Convert.ToHexString(SHA256.HashData(bytes))[..16];
        }
        catch
        {
            // An unreadable galaxy must not look like a MATCHING one, or divergence would
            // go unnoticed. Empty compares unequal to any real fingerprint.
            return "";
        }
    }

    private static bool _loggedPolicyCheck;

    /// <summary>
    /// Count empires arriving with a null Policy.
    ///
    /// Diagnostic, not a fix. Design.CalculateAdvancedTechScore reads
    /// Empire.Policy.ComponentSelectionFactors and is the one root cause still escaping into
    /// the task guards, and a null Policy would be the same shape of problem as Ship.Summary:
    /// derived state that ReadFromStream does not restore. If this reports zero, the null is
    /// somewhere else and that is worth knowing before anyone patches Policy.
    /// </summary>
    private static void CheckEmpirePolicies(object galaxy)
    {
        if (_loggedPolicyCheck) return;

        try
        {
            var empires = AccessTools.Field(galaxy.GetType(), "Empires")?.GetValue(galaxy);
            if (empires is null) return;

            var count = AccessTools.PropertyGetter(empires.GetType(), "Count")?.Invoke(empires, null) is int c ? c : 0;
            var item = AccessTools.Method(empires.GetType(), "get_Item", new[] { typeof(int) });
            if (item is null || count == 0) return;

            int nullPolicies = 0;

            for (int i = 0; i < count; i++)
            {
                var empire = item.Invoke(empires, new object[] { i });
                if (empire is null) continue;
                if (AccessTools.Field(empire.GetType(), "Policy")?.GetValue(empire) is null) nullPolicies++;
            }

            _loggedPolicyCheck = true;
            _log($"# apply: {nullPolicies} of {count} empires have a null Policy after adoption");
        }
        catch { /* diagnostic only */ }
    }
}
