using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M2b: the determinism experiment.
///
/// Lockstep multiplayer requires that two machines, given the same state and the same
/// commands, compute identical results.
///
/// The first version of this experiment sampled at equal GAME TIME and was invalid:
/// DW2's clock is a wall-clock stopwatch (GameServer.Now = _StopwatchStartTime +
/// _Stopwatch.Elapsed), so two runs reach the same game time having executed DIFFERENT
/// NUMBERS of update steps. A perfectly deterministic build would have failed that test.
///
/// M2b fixes the methodology by fixing the game. FixedStep pins the clock to a fixed
/// increment per server cycle and freezes work partitioning, so the server cycle becomes
/// a real simulation tick. State is then compared at equal TICK COUNTS, which is a valid
/// comparison: identical step sequences, so any divergence is genuinely the arithmetic.
///
/// Each snapshot is taken twice back to back. The simulation runs on background threads,
/// so a single hash could differ between runs from a torn read rather than a real
/// divergence; two agreeing hashes mean the instant is stable and a cross-run difference
/// is real.
/// </summary>
public static class Determinism
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dw2Mp",
        $"determinism-{Env("DW2MP_RUN_LABEL", "run")}.log");

    /// <summary>Server cycles between snapshots. The cycle IS the tick under FixedStep.</summary>
    private static readonly long SnapshotEveryCycles = EnvLong("DW2MP_SNAPSHOT_CYCLES", 2000);

    private static readonly int MaxSnapshots = EnvInt("DW2MP_MAX_SNAPSHOTS", 5);

    private static readonly bool ExitWhenDone = Env("DW2MP_EXIT_WHEN_DONE", "1") == "1";

    private static readonly double StepMilliseconds = EnvDouble("DW2MP_STEP_MS", 100);

    private static readonly bool PinBlocks = Env("DW2MP_PIN_BLOCKS", "1") == "1";

    private static readonly long CaptureAtCycles = EnvLong("DW2MP_CAPTURE_AT_CYCLES", 400);

    private static readonly long ApplyAfterCycles = EnvLong("DW2MP_APPLY_AFTER_CYCLES", 1600);

    /// <summary>When set, raw snapshot bytes are written here for byte-level diffing.</summary>
    private static readonly string DumpDir = Env("DW2MP_DUMP_DIR", "");

    private static readonly object Gate = new();

    private static Type _galaxyType;
    private static FieldInfo _timeField;
    private static FieldInfo _seedField;
    private static MethodInfo _writeToStream;
    private static Type _galaxyDataType;

    private static int _snapshotsTaken;
    private static long _nextSnapshotCycle;
    private static bool _finished;
    private static long _cycleCount;

    private const long HeartbeatEvery = 2000;

    private static Timer _watchdog;

    public static void Install(Harmony harmony)
    {
        var serverType = AccessTools.TypeByName("DistantWorlds.Types.GameServer");
        var target = serverType is null ? null : AccessTools.Method(serverType, "UpdateGameAsServer");
        if (target is null) { Log("SETUP FAILED: GameServer.UpdateGameAsServer not found"); return; }

        _galaxyType = AccessTools.TypeByName("DistantWorlds.Types.Galaxy");
        _galaxyDataType = AccessTools.TypeByName("DistantWorlds.Types.GameGalaxyData");
        _timeField = AccessTools.Field(_galaxyType, "Time");
        _seedField = AccessTools.Field(_galaxyType, "_RandomSeed");
        _writeToStream = AccessTools.Method(_galaxyType, "WriteToStream");

        if (_writeToStream is null || _timeField is null)
        {
            Log("SETUP FAILED: Galaxy.WriteToStream / Time not resolvable");
            return;
        }

        Log($"# every={SnapshotEveryCycles} cycles  maxSnapshots={MaxSnapshots}  exitWhenDone={ExitWhenDone}");

        // Install FixedStep FIRST: the clock must already be deterministic by the time
        // the first cycle runs, or snapshot 0 is taken under different rules to the rest.
        FixedStep.Install(harmony, StepMilliseconds, PinBlocks, () => Interlocked.Read(ref _cycleCount), Log);

        if (Env("DW2MP_SEQUENTIAL", "0") == "1")
            Sequential.Install(harmony, Log);

        // The lobby's agreed session, if the launcher supplied one. Loaded BEFORE MakeSave
        // installs, because it decides what galaxy gets generated and which empire this
        // machine plays.
        MakeSave.Session = SessionConfig.Load(Env("DW2MP_SESSION", ""), Log);

        // Perspective, not state: which of the galaxy's empires THIS machine drives. Must
        // come after the session is loaded and before anything renders.
        PlayerEmpire.Install(harmony, MakeSave.Session, Log);

        // NetSession reuses the apply path, so install it for either consumer.
        NetSession.Install(harmony, Log);

        // A session means "generate the agreed galaxy", for BOTH roles. The client's copy
        // is a throwaway that only exists to give StartGameExisting a game context to
        // adopt the host's state into — which is what removes the need for players to
        // share a save file at all.
        if (MakeSave.Active || MakeSave.Session is not null) MakeSave.Install(harmony, Log);

        if (Env("DW2MP_MEASURE_APPLY", "0") == "1" || NetSession.Active)
            ApplyState.Install(harmony, _writeToStream, _galaxyDataType, Log);

        var prefix = typeof(Determinism).GetMethod(nameof(OnServerCycle), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(target, new HarmonyMethod(prefix));
        Log($"# patched {serverType.Name}.{target.Name}");

        var started = DateTime.UtcNow;
        _watchdog = new Timer(_ =>
        {
            if (_finished) return;
            Log($"# watchdog {(int)(DateTime.UtcNow - started).TotalSeconds}s: {Interlocked.Read(ref _cycleCount)} cycle(s)");
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Log("# cycle  bytes  hashA  hashB  stable");
    }

    /// <summary>Harmony prefix. Must never throw: this runs inside the simulation loop.</summary>
    private static void OnServerCycle(object __instance, object[] __args)
    {
        try
        {
            var galaxy = __args is { Length: > 0 } ? __args[0] : null;
            if (galaxy is null || !_galaxyType.IsInstanceOfType(galaxy)) return;

            long n = Interlocked.Increment(ref _cycleCount);

            // A loaded save arrives PAUSED: the server cycles happily while Galaxy.Time
            // never moves, so nothing is ever simulated.
            if (n == 1) StartTheClock(__instance);

            // Hand the galaxy to the apply measurement once the game has settled. This
            // sits ABOVE the _finished check on purpose: the two measurements are
            // independent, and coupling them meant a completed snapshot run silently
            // cancelled the apply measurement before it ever armed.
            if (MakeSave.Active || MakeSave.Session is not null) MakeSave.OnServerCycle();

            // Host: ship a full state every N ticks. No-op in client or offline roles.
            // __instance is the GameServer; the host needs it to inject relayed commands
            // into InputQueue.
            NetSession.SetServer(__instance);
            NetSession.HostTick(galaxy, n);
            NetSession.ClientHeartbeatTick(n);

            if (ApplyState.Ready)
            {
                // Capture early, apply late, then watch. The gap between capture and
                // apply is what makes adoption falsifiable: the live clock must jump
                // BACKWARDS to the captured time, which nothing else would cause.
                if (n == CaptureAtCycles) ApplyState.Capture(galaxy);

                if (n == ApplyAfterCycles && ApplyState.HasCapture)
                {
                    Log($"# apply: arming at cycle {n}");
                    ApplyState.Arm(galaxy);
                }

                if (ApplyState.Applied) ApplyState.Observe(galaxy, n);
            }

            if (_finished) return;

            if (n == 1 || n % HeartbeatEvery == 0)
            {
                var t = (DateTime)_timeField.GetValue(galaxy);
                Log($"# cycle {n}: gameTime={t:yyyy-MM-dd HH:mm:ss.fff}");
            }

            if (n < _nextSnapshotCycle) return;

            lock (Gate)
            {
                if (_finished || n < _nextSnapshotCycle) return;
                _nextSnapshotCycle = n + SnapshotEveryCycles;
                Snapshot(galaxy, n);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR in prefix: " + ex.GetType().Name + ": " + ex.Message);
            _finished = true;
        }
    }

    /// <summary>
    /// Unpauses the simulation. GameServer.ResumeGame() is just _Stopwatch.Start();
    /// under FixedStep the stopwatch no longer drives anything, but the game still gates
    /// other work on being resumed, so it is still required.
    /// </summary>
    private static void StartTheClock(object server)
    {
        try
        {
            var type = server.GetType();
            var isRunning = AccessTools.PropertyGetter(type, "IsRunning")?.Invoke(server, null);
            Log($"# clock: IsRunning={isRunning} -> resuming (fixedStep={FixedStep.Enabled}, pinBlocks={FixedStep.PinBlockSizes})");

            AccessTools.Method(type, "ResumeGame")?.Invoke(server, null);

            float speed = (float)EnvDouble("DW2MP_GAME_SPEED", 4);
            AccessTools.Method(type, "ChangeGameSpeed")?.Invoke(server, new object[] { speed });
        }
        catch (Exception ex)
        {
            Log("# clock: resume failed " + ex.GetType().Name + ": " + (ex.InnerException ?? ex).Message);
        }
    }

    private static void Snapshot(object galaxy, long cycle)
    {
        if (_snapshotsTaken == 0)
            Log($"# galaxy seed = {_seedField?.GetValue(galaxy)}");

        if (Env("DW2MP_MEASURE_TRANSFER", "0") == "1")
            StateTransfer.Measure(galaxy, _writeToStream, _galaxyDataType, Log);

        var (hashA, bytes) = HashGalaxy(galaxy);
        var (hashB, _) = HashGalaxy(galaxy);

        bool stable = hashA == hashB && hashA is not null;

        Log(string.Format(CultureInfo.InvariantCulture,
            "{0,8}  {1,10}  {2}  {3}  {4}",
            cycle, bytes, hashA ?? "-", hashB ?? "-", stable ? "yes" : "NO"));

        if (++_snapshotsTaken < MaxSnapshots) return;

        _finished = true;
        Log("# run complete");

        if (!ExitWhenDone) return;

        Log("# exiting");
        Environment.Exit(0);
    }

    private static (string Hash, long Bytes) HashGalaxy(object galaxy)
    {
        try
        {
            // Fresh GameGalaxyData: it carries ViewPosition and stopwatch values which
            // vary every run and would manufacture a divergence out of nothing.
            var galaxyData = Activator.CreateInstance(_galaxyDataType);

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                _writeToStream.Invoke(galaxy, new[] { writer, galaxyData });
                writer.Flush();
            }

            byte[] raw = buffer.ToArray();

            // Optionally keep the bytes. A hash tells you THAT two runs differ; only the
            // bytes tell you WHERE, which is the difference between "a timestamp field
            // in the header" and "float drift scattered through every entity". Those
            // need completely different responses.
            if (DumpDir is { Length: > 0 })
            {
                try
                {
                    Directory.CreateDirectory(DumpDir);
                    var name = $"{Env("DW2MP_RUN_LABEL", "run")}-cycle{Interlocked.Read(ref _cycleCount)}.bin";
                    var path = Path.Combine(DumpDir, name);
                    if (!File.Exists(path)) File.WriteAllBytes(path, raw);
                }
                catch (Exception ex) { Log("# dump failed: " + ex.Message); }
            }

            return (Convert.ToHexString(SHA256.HashData(raw))[..16], raw.LongLength);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            Log($"# serialise failed: {cause.GetType().Name}: {cause.Message}");
            return (null, 0);
        }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }

        Console.WriteLine("[Dw2Mp/det] " + line);
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Env(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long EnvLong(string name, long fallback) =>
        long.TryParse(Env(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Env(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
