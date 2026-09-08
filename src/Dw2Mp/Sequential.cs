using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M2c: force DW2's simulation to run single-threaded.
///
/// M2b established that a fixed timestep is not enough — two runs from the same save
/// diverge across roughly a third of the galaxy within 1000 ticks, with the serialised
/// sizes drifting apart. The speed and totality of that points at thread completion
/// order rather than float rounding: float drift starts small and amplifies, whereas
/// reordering which entities are processed first changes outcomes immediately, and
/// changes who draws which random number.
///
/// This tests that hypothesis directly. DW2 parallelises through TaskHelper, which hands
/// out ParallelOptions (BackgroundTasks, ForegroundTasks, PriorityLocations,
/// ExclusiveLoading) and integer degrees (Background/Foreground/ImmediateParallelism).
/// Pinning every one of them to 1 makes Parallel.For and ParallelWhile run their bodies
/// sequentially, in index order, without altering a single loop body.
///
/// The getters are postfixed rather than the backing fields being set once, because a
/// ParallelOptions may be constructed per call or reset by the game; patching the
/// accessor covers both.
///
/// Expected cost: substantially slower simulation. That is the point of the experiment,
/// not a defect — if this reproduces, determinism is available but only by giving up the
/// game's parallelism, which is a design decision rather than a patch.
/// </summary>
public static class Sequential
{
    private static readonly string[] OptionProperties =
    {
        "BackgroundTasks",
        "ExclusiveLoading",
    };

    private static readonly string[] OptionFields =
    {
        "ForegroundTasks",
        "PriorityLocations",
    };

    private static readonly string[] DegreeProperties =
    {
        "BackgroundParallelism",
        "ForegroundParallelism",
        "ImmediateParallelism",
        "ExclusiveLoadingMaxDegreeOfParallelism",
    };

    public static void Install(Harmony harmony, Action<string> log)
    {
        var helper = AccessTools.TypeByName("DistantWorlds.Types.TaskHelper");
        if (helper is null) { log("# sequential: TaskHelper not found"); return; }

        var optionsPostfix = new HarmonyMethod(Method(nameof(ForceSingleOption)));
        var degreePostfix = new HarmonyMethod(Method(nameof(ForceSingleDegree)));

        foreach (var name in OptionProperties)
            Patch(harmony, AccessTools.PropertyGetter(helper, name), optionsPostfix, $"get_{name}", log);

        foreach (var name in DegreeProperties)
            Patch(harmony, AccessTools.PropertyGetter(helper, name), degreePostfix, $"get_{name}", log);

        // Public ParallelOptions fields have no accessor to patch, so pin the instances
        // directly. They are created once in the static constructor.
        foreach (var name in OptionFields)
        {
            var field = AccessTools.Field(helper, name);
            if (field?.GetValue(null) is ParallelOptions options)
            {
                options.MaxDegreeOfParallelism = 1;
                log($"# sequential: pinned field {name}");
            }
            else
            {
                log($"# sequential: field {name} not available");
            }
        }

        log("# sequential: simulation forced single-threaded");
    }

    private static void Patch(Harmony harmony, MethodInfo target, HarmonyMethod postfix, string label, Action<string> log)
    {
        if (target is null) { log($"# sequential: {label} not found"); return; }

        try { harmony.Patch(target, postfix: postfix); }
        catch (Exception ex) { log($"# sequential: {label} patch failed: {ex.Message}"); }
    }

    private static void ForceSingleOption(ParallelOptions __result)
    {
        if (__result is not null) __result.MaxDegreeOfParallelism = 1;
    }

    private static void ForceSingleDegree(ref int __result) => __result = 1;

    private static MethodInfo Method(string name) =>
        typeof(Sequential).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
