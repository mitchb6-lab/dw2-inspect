using System.Reflection;
using System.Runtime.CompilerServices;

namespace Dw2Inspect;

/// <summary>
/// Loads the game's managed assemblies into this process so their method bodies
/// can be read.
///
/// WHY THIS EXISTS, AND WHY IT IS NOT A DECOMPILER IN THE NORMAL SENSE:
/// DistantWorlds.Types.dll ships with its method bodies stripped. On disk, ~22,500
/// of its ~26,000 methods are literally the four bytes 00 00 00 2A -- nop, nop, nop,
/// ret. A conventional decompiler pointed at the file sees nothing but stubs.
///
/// The real bodies are restored by an obfuscated dispatcher that each type's static
/// constructor invokes. So the trick is simply to LET THE TYPE INITIALISE: call
/// RuntimeHelpers.RunClassConstructor, after which MethodBase.GetMethodBody()
/// returns genuine IL through ordinary reflection. NetworkHelper.SendData goes from
/// 4 bytes to 43; GameClient.UpdateGameAsClient from 4 to 771.
///
/// Nothing is injected and nothing is written -- the game's DLLs are only ever
/// inputs to this process.
/// </summary>
internal sealed class Loader
{
    /// <summary>The assemblies that hold game code, in dependency-ish order.</summary>
    public static readonly string[] GameAssemblies =
    {
        "DistantWorlds.Types",
        "DistantWorlds.Core",
        "DistantWorlds.UI",
        "DistantWorlds2",
    };

    public string GameDir { get; }

    private readonly List<Assembly> _loaded = new();

    private readonly HashSet<Type> _initialised = new();

    public Loader(string gameDir)
    {
        GameDir = gameDir;

        // The game resolves plenty of its own dependencies (Stride, SharpDX, Steamworks)
        // by simple name, so redirect every probe into the install directory.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromGameDir;

        // Some loads look for native or side-by-side files relative to the working
        // directory rather than the assembly location.
        Directory.SetCurrentDirectory(gameDir);
    }

    public IReadOnlyList<Assembly> Assemblies
    {
        get
        {
            if (_loaded.Count == 0)
            {
                foreach (var name in GameAssemblies)
                {
                    var path = Path.Combine(GameDir, name + ".dll");
                    if (!File.Exists(path))
                        continue;

                    try { _loaded.Add(Assembly.LoadFrom(path)); }
                    catch (Exception ex) { Log.Warn($"could not load {name}: {ex.Message}"); }
                }

                if (_loaded.Count == 0)
                    throw new Dw2Exception($"No game assemblies loaded from {GameDir}.");
            }

            return _loaded;
        }
    }

    private Assembly ResolveFromGameDir(object sender, ResolveEventArgs args)
    {
        var simpleName = new AssemblyName(args.Name).Name;
        var path = Path.Combine(GameDir, simpleName + ".dll");

        if (!File.Exists(path))
            return null;

        try { return Assembly.LoadFrom(path); }
        catch { return null; }
    }

    /// <summary>
    /// Runs the type's static constructor so the protector restores its method bodies.
    /// Idempotent, and non-fatal: a handful of types (Galaxy, for one) throw during
    /// initialisation outside the real game host, and their bodies may stay stubbed.
    /// That is worth a warning, not a failure.
    /// </summary>
    public void Prepare(Type type)
    {
        if (type is null || !_initialised.Add(type))
            return;

        try
        {
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            Log.Warn($"static constructor for {type.Name} threw {cause.GetType().Name}; " +
                     "its method bodies may still read as stubs (00 00 00 2A).");
        }
    }

    /// <summary>
    /// Finds types by full name, by simple name, or by substring -- in that order of
    /// preference, so an exact name never gets buried under fuzzy matches. Nested
    /// types are included, which matters because async and iterator bodies live in
    /// compiler-generated nested state machines.
    /// </summary>
    public IReadOnlyList<Type> FindTypes(string query, bool substring = false)
    {
        var all = AllTypes().ToList();

        var exactFull = all.Where(t => string.Equals(t.FullName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactFull.Count > 0) return exactFull;

        var exactSimple = all.Where(t => string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactSimple.Count > 0 && !substring) return exactSimple;

        var loose = all.Where(t => (t.FullName ?? t.Name).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return loose.Count > 0 ? loose : exactSimple;
    }

    public IEnumerable<Type> AllTypes()
    {
        foreach (var asm in Assemblies)
        {
            Type[] types;

            // Obfuscated assemblies routinely fail to expose every type; take what loads.
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            foreach (var t in types)
                yield return t;
        }
    }
}
