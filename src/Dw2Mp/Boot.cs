using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M1: prove we are running inside the game process, and that Harmony works here.
///
/// Loaded with:
///     DistantWorlds2.exe --low-level-inject "&lt;path&gt;\Dw2Mp.dll"
///
/// With no "!entrypoint" suffix, ModHelpers.LowLevelInjectionManaged does
/// LoadFromAssemblyPath followed by RunModuleConstructor -- so [ModuleInitializer]
/// below is the hook, and nothing else is needed to get running.
///
/// Note the game swallows failures here: LowLevelInjection returns silently if the
/// path does not exist, and wraps the load in a catch. So the log file IS the test
/// result. No log means we never ran.
/// </summary>
public static class Boot
{
    /// <summary>
    /// LOCALAPPDATA, not the game directory: the install lives under Program Files
    /// and is not reliably writable without elevation.
    /// </summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dw2Mp", "boot.log");

    // CA2255 warns against [ModuleInitializer] in a library. That guidance assumes a
    // library referenced at compile time; here the module initializer IS the contract
    // with the game's injector, which calls RunModuleConstructor and nothing else.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Initialize()
    {
        // Never throw out of a module initializer. An exception here surfaces as a
        // TypeInitializationException from whatever unrelated code touches us next,
        // which is an unpleasant thing to debug inside someone else's process.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            Run();
        }
        catch (Exception ex)
        {
            TryLog("FATAL in module initializer: " + ex);
        }
    }

    private static void Run()
    {
        TryLog("=== Dw2Mp boot ===");
        TryLog($"process     : {Environment.ProcessPath}");
        TryLog($"pid         : {Environment.ProcessId}");
        TryLog($"runtime     : {Environment.Version} / {RuntimeInformation.FrameworkDescription}");
        TryLog($"we are      : {typeof(Boot).Assembly.Location}");

        ReportGameAssemblies();
        var harmony = SelfTestHarmony();

        // Opt-in: the determinism harness exits the process when its run completes, so
        // it must never arm itself during ordinary play.
        if (harmony is not null && Environment.GetEnvironmentVariable("DW2MP_DETERMINISM") == "1")
        {
            TryLog("determinism : arming M2 harness");
            Determinism.Install(harmony);
        }

        TryLog("=== boot complete ===");
    }

    /// <summary>
    /// Confirms we are inside the GAME, not merely in some .NET process. If
    /// DistantWorlds.Types is loaded and reports a version, we are positioned.
    /// </summary>
    private static void ReportGameAssemblies()
    {
        foreach (var name in new[] { "DistantWorlds.Types", "DistantWorlds.Core", "DistantWorlds2", "Stride.Engine" })
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);

            TryLog(asm is null
                ? $"assembly    : {name} NOT loaded (yet)"
                : $"assembly    : {name} {asm.GetName().Version}");
        }
    }

    /// <summary>
    /// Patches a method in THIS assembly and checks the patch took effect.
    ///
    /// Self-testing rather than patching a game method on purpose: it proves Harmony
    /// can emit and apply a patch in this process without needing a game to be
    /// running, and without leaving a patch on game code that M1 has no use for.
    /// </summary>
    private static Harmony SelfTestHarmony()
    {
        try
        {
            var harmony = new Harmony("dw2mp.m1.selftest");
            TryLog($"harmony     : {typeof(Harmony).Assembly.GetName().Version}");

            var target = typeof(Boot).GetMethod(nameof(PatchTarget), BindingFlags.NonPublic | BindingFlags.Static);
            var prefix = typeof(Boot).GetMethod(nameof(PatchPrefix), BindingFlags.NonPublic | BindingFlags.Static);

            string before = PatchTarget();
            harmony.Patch(target, new HarmonyMethod(prefix));
            string after = PatchTarget();

            TryLog(after == "patched"
                ? $"patching    : OK (\"{before}\" -> \"{after}\")"
                : $"patching    : FAILED (still \"{after}\")");

            return after == "patched" ? harmony : null;
        }
        catch (Exception ex)
        {
            TryLog("patching    : FAILED " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private static string PatchTarget() => "unpatched";

    private static bool PatchPrefix(ref string __result)
    {
        __result = "patched";
        return false;   // skip the original
    }

    private static void TryLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";

        try { File.AppendAllText(LogPath, stamped + Environment.NewLine); }
        catch { /* nothing useful to do; we may not even have a console */ }

        Console.WriteLine("[Dw2Mp] " + line);
    }
}
