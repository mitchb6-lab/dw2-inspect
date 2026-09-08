using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M2b: give DW2 a fixed simulation timestep.
///
/// DW2 has no simulation tick. GameServer.Now decompiles to
///
///     _StopwatchStartTime + _Stopwatch.Elapsed
///
/// so simulated time advances with REAL time. Two runs therefore never execute the same
/// sequence of steps, which makes any state comparison between them meaningless — and
/// makes lockstep impossible, since lockstep needs step N on machine A to correspond to
/// step N on machine B.
///
/// This replaces that clock with
///
///     _StopwatchStartTime + (serverCycleCount * StepMilliseconds)
///
/// The server cycle becomes the tick. It is both the instrument M2b needs and the first
/// piece of the actual fix.
///
/// It also pins work partitioning. AdjustClientBlockSizes resizes the ship/location/
/// creature blocks from MEASURED milliseconds, so without pinning, two runs would divide
/// the same work differently and the comparison would be invalid again for a new reason.
/// One variable at a time is only meaningful if the others are actually held still.
///
/// What is deliberately NOT addressed here is thread completion order. If state still
/// diverges with an identical step sequence and identical partitioning, parallelism or
/// float arithmetic is the cause — and that is exactly the finding M2b exists to isolate.
/// </summary>
public static class FixedStep
{
    /// <summary>Simulated milliseconds per server cycle. 0 disables the fixed clock.</summary>
    public static double StepMilliseconds { get; private set; }

    public static bool PinBlockSizes { get; private set; }

    public static bool Enabled => StepMilliseconds > 0;

    private static FieldInfo _stopwatchStartTime;

    /// <summary>
    /// The tick counter. Owned by Determinism, which increments it once per
    /// UpdateGameAsServer — so Now is stable for the whole of a cycle, which matters
    /// because the game reads it many times within one.
    /// </summary>
    private static Func<long> _cycleCounter;

    public static void Install(Harmony harmony, double stepMs, bool pinBlocks, Func<long> cycleCounter, Action<string> log)
    {
        StepMilliseconds = stepMs;
        PinBlockSizes = pinBlocks;
        _cycleCounter = cycleCounter;

        var serverType = AccessTools.TypeByName("DistantWorlds.Types.GameServer");
        if (serverType is null) { log("# fixedstep: GameServer not found"); return; }

        _stopwatchStartTime = AccessTools.Field(serverType, "_StopwatchStartTime");
        if (_stopwatchStartTime is null) { log("# fixedstep: _StopwatchStartTime not found"); return; }

        if (Enabled)
        {
            // Both accessors, or anything reading NowPrecise keeps a wall-clock view and
            // the two disagree about what time it is.
            PatchClock(harmony, serverType, "get_Now", nameof(NowPrefix), log);
            PatchClock(harmony, serverType, "get_NowPrecise", nameof(NowPrefix), log);
            log($"# fixedstep: clock pinned to {stepMs} ms per server cycle");
        }

        if (pinBlocks)
        {
            var adjust = AccessTools.Method(serverType, "AdjustClientBlockSizes");
            if (adjust is null)
            {
                log("# fixedstep: AdjustClientBlockSizes not found; block sizes NOT pinned");
            }
            else
            {
                var skip = typeof(FixedStep).GetMethod(nameof(SkipPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(adjust, new HarmonyMethod(skip));
                log("# fixedstep: AdjustClientBlockSizes disabled (block sizes frozen at load values)");
            }
        }
    }

    private static void PatchClock(Harmony harmony, Type serverType, string getter, string prefixName, Action<string> log)
    {
        var target = AccessTools.Method(serverType, getter);
        if (target is null) { log($"# fixedstep: {getter} not found"); return; }

        var prefix = typeof(FixedStep).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(target, new HarmonyMethod(prefix));
    }

    /// <summary>Replaces the stopwatch clock. Returning false skips the original.</summary>
    private static bool NowPrefix(object __instance, ref DateTime __result)
    {
        try
        {
            var start = (DateTime)_stopwatchStartTime.GetValue(__instance);
            __result = start.AddMilliseconds(_cycleCounter() * StepMilliseconds);
            return false;
        }
        catch
        {
            // Fall through to the real implementation rather than handing the simulation
            // a garbage time.
            return true;
        }
    }

    private static bool SkipPrefix() => false;
}
