using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M2: the determinism experiment.
///
/// Lockstep multiplayer requires that two machines, given the same state and the same
/// commands, compute identical results. DW2 is hostile to that in three ways (floats
/// throughout, a multithreaded simulation, and work partitioning derived from measured
/// wall-clock time). Whether any of it actually changes OUTCOMES is an empirical
/// question, and this measures it.
///
/// Method: hook the server cycle, and at fixed GAME-time intervals serialise the whole
/// galaxy through the game's own Galaxy.WriteToStream and hash the bytes. Run the same
/// scenario twice and compare the hash sequences.
///
/// The snapshot is taken TWICE back to back each time. That is the control: the
/// simulation runs on background threads, so a naive single hash could differ between
/// runs because of a torn read rather than because the simulation diverged. If the two
/// back-to-back hashes disagree, the measurement is unstable at that instant and the
/// row must not be read as evidence about the simulation.
/// </summary>
public static class Determinism
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dw2Mp",
        $"determinism-{Env("DW2MP_RUN_LABEL", "run")}.log");

    /// <summary>Game-days between snapshots.</summary>
    private static readonly double IntervalDays = EnvDouble("DW2MP_SNAPSHOT_DAYS", 5);

    /// <summary>Stop after this many snapshots, so runs are bounded and comparable.</summary>
    private static readonly int MaxSnapshots = EnvInt("DW2MP_MAX_SNAPSHOTS", 6);

    /// <summary>Exit the process once the run is complete, so a script can drive it.</summary>
    private static readonly bool ExitWhenDone = Env("DW2MP_EXIT_WHEN_DONE", "1") == "1";

    private static readonly object Gate = new();

    private static Type _galaxyType;
    private static FieldInfo _timeField;
    private static FieldInfo _timeStartField;
    private static FieldInfo _seedField;
    private static MethodInfo _writeToStream;
    private static Type _galaxyDataType;

    private static int _snapshotsTaken;
    private static double _nextSnapshotDay;
    private static bool _finished;

    private static long _cycleCount;
    private const long HeartbeatEvery = 2000;

    /// <summary>
    /// Fires regardless of whether the game is running, so an empty log can be
    /// diagnosed. "0 cycles" means the patch never fires (wrong hook, or no game
    /// started); "N cycles, elapsedDays flat" means the game is paused.
    /// </summary>
    private static Timer _watchdog;

    public static void Install(Harmony harmony)
    {
        var serverType = AccessTools.TypeByName("DistantWorlds.Types.GameServer");
        if (serverType is null) { Log("SETUP FAILED: GameServer type not found"); return; }

        // UpdateGameAsServer(Galaxy, DateTime) is the server's per-cycle entry point and
        // is handed the Galaxy directly, which is exactly what we need and saves us
        // hunting for a global.
        var target = AccessTools.Method(serverType, "UpdateGameAsServer");
        if (target is null) { Log("SETUP FAILED: UpdateGameAsServer not found"); return; }

        _galaxyType = AccessTools.TypeByName("DistantWorlds.Types.Galaxy");
        _galaxyDataType = AccessTools.TypeByName("DistantWorlds.Types.GameGalaxyData");
        _timeField = AccessTools.Field(_galaxyType, "Time");
        _timeStartField = AccessTools.Field(_galaxyType, "TimeStart");
        _seedField = AccessTools.Field(_galaxyType, "_RandomSeed");
        _writeToStream = AccessTools.Method(_galaxyType, "WriteToStream");

        if (_galaxyType is null || _writeToStream is null || _timeField is null)
        {
            Log("SETUP FAILED: Galaxy.WriteToStream / Time not resolvable");
            return;
        }

        var prefix = typeof(Determinism).GetMethod(nameof(OnServerCycle), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(target, new HarmonyMethod(prefix));

        Log($"# interval={IntervalDays}d  maxSnapshots={MaxSnapshots}  exitWhenDone={ExitWhenDone}");
        Log($"# patched {serverType.Name}.{target.Name}");

        var started = DateTime.UtcNow;
        _watchdog = new Timer(_ =>
        {
            if (_finished) return;
            var seen = Interlocked.Read(ref _cycleCount);
            Log($"# watchdog {(int)(DateTime.UtcNow - started).TotalSeconds}s: {seen} server cycle(s) seen");
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

        Log("# day  bytes  hashA  hashB  stable");
    }

    /// <summary>
    /// Harmony prefix. Must never throw: an exception here propagates into the game's
    /// simulation loop.
    /// </summary>
    private static void OnServerCycle(object __instance, object[] __args)
    {
        try
        {
            if (_finished) return;

            var galaxy = __args is { Length: > 0 } ? __args[0] : null;
            if (galaxy is null || !_galaxyType.IsInstanceOfType(galaxy)) return;

            var now = (DateTime)_timeField.GetValue(galaxy);
            var start = (DateTime)_timeStartField.GetValue(galaxy);

            // Game time, never wall-clock. Wall-clock would sample at different points
            // in the simulation on a faster machine, which is the very thing under test.
            double elapsedDays = (now - start).TotalDays;

            // Heartbeat. Without this, "no snapshot rows" is ambiguous between three very
            // different failures: the patch never fired, it fired but game time is frozen
            // (the game starts paused), or time is advancing too slowly for the interval.
            long n = Interlocked.Increment(ref _cycleCount);
            if (n == 1 || n % HeartbeatEvery == 0)
                Log($"# cycle {n}: gameTime={now:yyyy-MM-dd HH:mm} elapsedDays={elapsedDays:F3}");

            if (elapsedDays < _nextSnapshotDay) return;

            lock (Gate)
            {
                if (_finished || elapsedDays < _nextSnapshotDay) return;
                _nextSnapshotDay = elapsedDays + IntervalDays;
                Snapshot(galaxy, elapsedDays);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR in prefix: " + ex.GetType().Name + ": " + ex.Message);
            _finished = true;   // stop rather than log the same failure every cycle
        }
    }

    private static void Snapshot(object galaxy, double elapsedDays)
    {
        if (_snapshotsTaken == 0)
        {
            var seed = _seedField?.GetValue(galaxy);
            Log($"# galaxy seed = {seed}");
        }

        var (hashA, bytes) = HashGalaxy(galaxy);
        var (hashB, _) = HashGalaxy(galaxy);

        bool stable = hashA == hashB && hashA is not null;

        Log(string.Format(CultureInfo.InvariantCulture,
            "{0,7:F1}  {1,10}  {2}  {3}  {4}",
            elapsedDays, bytes, hashA ?? "-", hashB ?? "-", stable ? "yes" : "NO"));

        if (++_snapshotsTaken < MaxSnapshots) return;

        _finished = true;
        Log("# run complete");

        if (!ExitWhenDone) return;

        // Exit hard. A clean shutdown would run save prompts and teardown we do not
        // want, and the process has already produced everything we need.
        Log("# exiting");
        Environment.Exit(0);
    }

    /// <summary>
    /// Serialises the galaxy through the game's own writer and hashes the bytes.
    /// Returns (null, 0) if serialisation throws, which is itself a finding worth
    /// seeing in the log rather than a reason to crash the game.
    /// </summary>
    private static (string Hash, long Bytes) HashGalaxy(object galaxy)
    {
        try
        {
            // A fresh GameGalaxyData: it carries view position and stopwatch values,
            // which vary run to run and would fake a divergence. Zeroed, it contributes
            // a constant.
            var galaxyData = Activator.CreateInstance(_galaxyDataType);

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                _writeToStream.Invoke(galaxy, new[] { writer, galaxyData });
                writer.Flush();
            }

            byte[] raw = buffer.ToArray();
            byte[] digest = SHA256.HashData(raw);

            return (Convert.ToHexString(digest)[..16], raw.LongLength);
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

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Env(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
