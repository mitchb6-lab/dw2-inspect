using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// What a full-state adoption costs the joiner in memory, split so the cause can be named
/// instead of guessed at (2026-11-11).
///
/// One process number cannot tell these apart, so every reading carries all of them:
///
///   managed      the GC heap in use. Growing after a forced collect = objects are RETAINED.
///   gcCommitted  what the garbage collector has committed from Windows. DW2 runs SERVER GC
///                (DistantWorlds2.Windows.runtimeconfig.json), which keeps freed memory
///                committed for reuse, so this can sit far above "managed".
///   other        private bytes minus gcCommitted: native memory (Stride, the graphics
///                driver, the JIT). Growing here is not ours to collect.
///   private / workingSet   the process as Windows sees it.
///
/// Every adoption logs a cheap reading that forces nothing. The developer probe
/// (DW2MP_ADOPTION_MEMORY_PROBE with DW2MP_DEV=1) adds forced-collection readings before and
/// after, a count of replaced galaxies still alive, a census of collection sizes and a list of
/// the graphics resources created since the previous adoption.
///
/// WHAT IT FOUND (two runs, docs/multiplayer.md "Adoption memory"): no old galaxy is kept
/// alive; the managed heap is flat. Every adoption leaves 63 new graphics resources behind,
/// +32 MB of textures and +7 MB of buffers, because DWRendererBase.CheckInitializeGraphics
/// gives every galaxy-bound render system a new instance and disposes none of the old ones.
/// That is DW2's own leak, about 45 MB an adoption, and it is why the session memory budget
/// below exists: one notice to the player once the process has grown past the limit.
/// </summary>
public static class AdoptionMemory
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>How long after an adoption the "settled" reading is taken: the renderer rebuilds lazily.</summary>
    private const double SettleSeconds = 20.0;

    private static Action<string> _log;

    /// <summary>Adoption number from which the probe forces collections; 0 = probe off.</summary>
    private static int _probeFromAdoption;

    /// <summary>Adoption number at whose settled reading one aggressive collect is tried; 0 = never.</summary>
    private static int _aggressiveAtAdoption;

    private static int _adoptions;
    private static long _settleDueStamp;
    private static MemorySample _lastSettled;
    private static bool _haveLastSettled;
    private static MemorySample _firstSettled;
    private static bool _haveFirstSettled;
    private static int _firstSettledAdoption;

    private static SessionMemoryBudget _budget;

    /// <summary>Galaxies this game has replaced. Weak: this list must never be what keeps one alive.</summary>
    private static readonly List<WeakReference> _replacedGalaxies = new();

    private static Dictionary<string, int> _censusBeforeLast;
    private static Dictionary<string, int> _censusLast;

    private struct MemorySample
    {
        public bool Forced;
        public long ManagedBytes;
        public long GcCommittedBytes;
        public long GcHeapBytes;
        public long GcFragmentedBytes;
        public long LargeObjectBytes;
        public long PrivateBytes;
        public long WorkingSetBytes;
        public double TextureMegabytes;
        public double BufferMegabytes;
        public int Handles;
        public int Threads;

        public long OtherBytes
        {
            get
            {
                return PrivateBytes - GcCommittedBytes;
            }
        }
    }

    private static bool ProbeActive
    {
        get
        {
            return _probeFromAdoption > 0 && _adoptions >= _probeFromAdoption;
        }
    }

    public static void Install(Action<string> log)
    {
        _log = log;

        string limitText = Environment.GetEnvironmentVariable("DW2MP_MEMORY_BUDGET_MB");
        long limitBytes = SessionMemoryBudget.LimitFromMegabytesText(limitText);
        _budget = new SessionMemoryBudget(limitBytes);

        bool developerMode = Environment.GetEnvironmentVariable("DW2MP_DEV") == "1";
        if (developerMode)
        {
            _probeFromAdoption = ReadPositiveInt("DW2MP_ADOPTION_MEMORY_PROBE");
            _aggressiveAtAdoption = ReadPositiveInt("DW2MP_ADOPTION_MEMORY_AGGRESSIVE_AT");
        }

        log($"# memory: session budget {SessionMemoryBudget.FormatGigabytes(limitBytes)} of growth after the join, then one notice");

        if (_probeFromAdoption > 0)
        {
            log($"# memory: *** DEV FLAG LIVE *** DW2MP_ADOPTION_MEMORY_PROBE={_probeFromAdoption} (with DW2MP_DEV=1): " +
                "forced collections and a collection census around every adoption from that one on. Never set in a real session.");
            log("# memory: " + DescribeCollector());
        }
    }

    private static int ReadPositiveInt(string variableName)
    {
        string text = Environment.GetEnvironmentVariable(variableName);
        bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
        if (!parsed || value < 0)
        {
            return 0;
        }

        return value;
    }

    /// <summary>The collector settings DW2 runs under; they decide how freed memory is given back.</summary>
    private static string DescribeCollector()
    {
        var parts = new List<string>();
        parts.Add("serverGC=" + GCSettings.IsServerGC);
        parts.Add("latency=" + GCSettings.LatencyMode);

        try
        {
            IReadOnlyDictionary<string, object> configuration = GC.GetConfigurationVariables();

            foreach (KeyValuePair<string, object> setting in configuration)
            {
                parts.Add(setting.Key + "=" + Convert.ToString(setting.Value, CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            parts.Add("configuration unreadable (" + ex.GetType().Name + ")");
        }

        return "collector " + string.Join(" ", parts);
    }

    // ------------------------------------------------------------------ readings

    private static MemorySample TakeSample(bool forceCollection)
    {
        MemorySample sample = default;
        sample.Forced = forceCollection;
        sample.ManagedBytes = GC.GetTotalMemory(forceCollection);

        GCMemoryInfo collectorInfo = GC.GetGCMemoryInfo();
        sample.GcCommittedBytes = collectorInfo.TotalCommittedBytes;
        sample.GcHeapBytes = collectorInfo.HeapSizeBytes;
        sample.GcFragmentedBytes = collectorInfo.FragmentedBytes;

        ReadOnlySpan<GCGenerationInfo> generations = collectorInfo.GenerationInfo;
        const int LargeObjectHeapIndex = 3;
        if (generations.Length > LargeObjectHeapIndex)
        {
            sample.LargeObjectBytes = generations[LargeObjectHeapIndex].SizeAfterBytes;
        }

        using (Process process = Process.GetCurrentProcess())
        {
            sample.PrivateBytes = process.PrivateMemorySize64;
            sample.WorkingSetBytes = process.WorkingSet64;
            sample.Handles = process.HandleCount;
            sample.Threads = process.Threads.Count;
        }

        sample.TextureMegabytes = ReadGraphicsCounterMegabytes("TextureMemory");
        sample.BufferMegabytes = ReadGraphicsCounterMegabytes("BuffersMemory");
        return sample;
    }

    private static string Megabytes(long bytes)
    {
        double megabytes = bytes / BytesPerMegabyte;
        return megabytes.ToString("N0", CultureInfo.InvariantCulture) + "MB";
    }

    private static string SignedMegabytes(long bytes)
    {
        double megabytes = bytes / BytesPerMegabyte;
        string sign = megabytes >= 0 ? "+" : "";
        return sign + megabytes.ToString("N0", CultureInfo.InvariantCulture) + "MB";
    }

    private static string Describe(MemorySample sample)
    {
        string managedLabel = sample.Forced ? "managedAfterCollect" : "managed";

        string text = $"{managedLabel}={Megabytes(sample.ManagedBytes)} gcCommitted={Megabytes(sample.GcCommittedBytes)} " +
                      $"other={Megabytes(sample.OtherBytes)} private={Megabytes(sample.PrivateBytes)} " +
                      $"workingSet={Megabytes(sample.WorkingSetBytes)} " +
                      $"textures={sample.TextureMegabytes:N0}MB buffers={sample.BufferMegabytes:N0}MB";

        if (ProbeActive || sample.Forced)
        {
            text += $" gcHeap={Megabytes(sample.GcHeapBytes)} gcFragmented={Megabytes(sample.GcFragmentedBytes)} " +
                    $"largeObjects={Megabytes(sample.LargeObjectBytes)} handles={sample.Handles} threads={sample.Threads}";
        }

        return text;
    }

    private static string DescribeChange(MemorySample from, MemorySample to)
    {
        double textureChange = to.TextureMegabytes - from.TextureMegabytes;
        double bufferChange = to.BufferMegabytes - from.BufferMegabytes;

        return $"managed {SignedMegabytes(to.ManagedBytes - from.ManagedBytes)}, " +
               $"gcCommitted {SignedMegabytes(to.GcCommittedBytes - from.GcCommittedBytes)}, " +
               $"other {SignedMegabytes(to.OtherBytes - from.OtherBytes)}, " +
               $"private {SignedMegabytes(to.PrivateBytes - from.PrivateBytes)}, " +
               $"workingSet {SignedMegabytes(to.WorkingSetBytes - from.WorkingSetBytes)}, " +
               $"textures {textureChange:+#,0;-#,0;+0}MB, buffers {bufferChange:+#,0;-#,0;+0}MB";
    }

    // ------------------------------------------------------------------ adoption hooks

    /// <summary>
    /// Main thread, just before the galaxy is replaced. Remembers the outgoing galaxy weakly and,
    /// under the probe, logs a forced-collection reading.
    /// </summary>
    public static void BeforeAdoption(object outgoingGalaxy)
    {
        try
        {
            _adoptions++;

            if (outgoingGalaxy is not null && _probeFromAdoption > 0)
            {
                _replacedGalaxies.Add(new WeakReference(outgoingGalaxy));
            }

            if (ProbeActive)
            {
                MemorySample before = TakeSample(forceCollection: true);
                _log($"# memory: adoption #{_adoptions} BEFORE  {Describe(before)}");
            }
        }
        catch (Exception ex)
        {
            _log("# memory: reading before the adoption failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Main thread, just after the galaxy was replaced. Returns the short text for the APPLIED
    /// line and schedules the settled reading.
    /// </summary>
    public static string AfterAdoption()
    {
        try
        {
            if (ProbeActive)
            {
                MemorySample afterForced = TakeSample(forceCollection: true);
                _log($"# memory: adoption #{_adoptions} AFTER   {Describe(afterForced)}");
            }

            long settleTicks = (long)(SettleSeconds * Stopwatch.Frequency);
            _settleDueStamp = Stopwatch.GetTimestamp() + settleTicks;

            MemorySample after = TakeSample(forceCollection: false);
            return Describe(after);
        }
        catch (Exception ex)
        {
            return "memory unavailable (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>Main thread, every frame on the joiner. Takes the settled reading when it is due.</summary>
    public static void OnMainThreadUpdate()
    {
        if (_settleDueStamp == 0)
        {
            return;
        }

        if (Stopwatch.GetTimestamp() < _settleDueStamp)
        {
            return;
        }

        _settleDueStamp = 0;

        try
        {
            TakeSettledReading();
        }
        catch (Exception ex)
        {
            _log("# memory: settled reading failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void TakeSettledReading()
    {
        MemorySample settled = TakeSample(forceCollection: ProbeActive);
        string line = $"# memory: adoption #{_adoptions} SETTLED {Describe(settled)}";

        if (_haveLastSettled)
        {
            line += $"  [since the previous adoption: {DescribeChange(_lastSettled, settled)}]";
        }

        _log(line);

        if (!_haveFirstSettled)
        {
            _firstSettled = settled;
            _firstSettledAdoption = _adoptions;
            _haveFirstSettled = true;
        }
        else
        {
            int adoptionsSinceFirst = _adoptions - _firstSettledAdoption;
            _log($"# memory: since adoption #{_firstSettledAdoption} ({adoptionsSinceFirst} adoption(s) later): {DescribeChange(_firstSettled, settled)}");
        }

        _lastSettled = settled;
        _haveLastSettled = true;

        if (_probeFromAdoption > 0)
        {
            LogReplacedGalaxies(settled.Forced);
            LogCensusGrowth();
            LogNewGraphicsResources();
        }

        if (_aggressiveAtAdoption > 0 && _adoptions == _aggressiveAtAdoption)
        {
            TryAggressiveCollect(settled);
        }

        CheckBudget(settled);
    }

    /// <summary>
    /// DEV: one aggressive collect, which asks the collector to give back everything it can.
    /// The difference in private bytes is how much of the growth was the collector holding on.
    /// </summary>
    private static void TryAggressiveCollect(MemorySample before)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        double milliseconds = stopwatch.Elapsed.TotalMilliseconds;

        MemorySample after = TakeSample(forceCollection: false);
        _log($"# memory: DEV aggressive collect took {milliseconds:N0}ms  {Describe(after)}  [{DescribeChange(before, after)}]");

        // The next "since the previous adoption" line starts from the lowered floor.
        _lastSettled = after;
    }

    private static void CheckBudget(MemorySample settled)
    {
        bool noticeDue = _budget.Record(settled.PrivateBytes);
        if (!noticeDue)
        {
            return;
        }

        _log("# memory: *** SESSION MEMORY BUDGET *** " + _budget.LogText());
        Desync.QueueNotice(SessionMemoryBudget.NoticeTitle, _budget.NoticeText());
    }

    // ------------------------------------------------------------------ probe: replaced galaxies

    private static void LogReplacedGalaxies(bool collectedJustNow)
    {
        var aliveNumbers = new List<string>();
        for (int index = 0; index < _replacedGalaxies.Count; index++)
        {
            if (_replacedGalaxies[index].IsAlive)
            {
                // Number 1 is the galaxy the first adoption replaced: the joiner's own throwaway.
                aliveNumbers.Add("#" + (index + 1));
            }
        }

        string qualifier = collectedJustNow ? "after a forced collect" : "no collect forced, so garbage counts as alive";
        string which = aliveNumbers.Count == 0 ? "" : " -- replaced by adoption " + string.Join(", ", aliveNumbers);
        _log($"# memory: replaced galaxies still alive: {aliveNumbers.Count} of {_replacedGalaxies.Count} ({qualifier}){which}");
    }

    // ------------------------------------------------------------------ probe: graphics counters

    private static object ReadMember(object target, string memberName)
    {
        if (target is null)
        {
            return null;
        }

        Type type = target.GetType();

        PropertyInfo property = AccessTools.Property(type, memberName);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(target);
        }

        FieldInfo field = AccessTools.Field(type, memberName);
        if (field is not null)
        {
            return field.GetValue(target);
        }

        return null;
    }

    /// <summary>
    /// Stride's own count of graphics memory, in megabytes; -1 when it cannot be read. An integer
    /// counter is taken as bytes and a floating-point one as megabytes.
    /// </summary>
    private static double ReadGraphicsCounterMegabytes(string memberName)
    {
        try
        {
            object device = ReadMember(ApplyState.Game, "GraphicsDevice");
            object value = ReadMember(device, memberName);
            if (value is null)
            {
                return -1;
            }

            if (value is float || value is double)
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            return Convert.ToDouble(value, CultureInfo.InvariantCulture) / BytesPerMegabyte;
        }
        catch
        {
            return -1;
        }
    }

    // ------------------------------------------------------------------ probe: new graphics resources
    //
    // Stride's GraphicsDevice keeps every live texture and buffer in its Resources set, and a
    // graphics resource has no finalizer: one nobody disposes stays there for ever. Listing the
    // ones that appeared since the previous adoption, grouped by what they are, names the leak.

    private const int ResourceGroupReportLimit = 12;

    private static HashSet<object> _graphicsResourcesLast;

    private static void LogNewGraphicsResources()
    {
        object device = ReadMember(ApplyState.Game, "GraphicsDevice");
        object resourceSet = ReadMember(device, "Resources");
        if (resourceSet is not IEnumerable resources)
        {
            _log("# memory: GraphicsDevice.Resources not readable on this build");
            return;
        }

        var current = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Stride takes this same lock when a resource is created or destroyed on another thread.
        lock (resourceSet)
        {
            foreach (object resource in resources)
            {
                current.Add(resource);
            }
        }

        if (_graphicsResourcesLast is not null)
        {
            var groups = new Dictionary<string, int>();
            int created = 0;

            foreach (object resource in current)
            {
                if (_graphicsResourcesLast.Contains(resource))
                {
                    continue;
                }

                created++;
                string description = DescribeGraphicsResource(resource);
                groups.TryGetValue(description, out int countSoFar);
                groups[description] = countSoFar + 1;
            }

            int destroyed = 0;
            foreach (object resource in _graphicsResourcesLast)
            {
                if (!current.Contains(resource))
                {
                    destroyed++;
                }
            }

            _log($"# memory: graphics resources since the previous adoption: {created} new and still alive, {destroyed} gone, {current.Count:N0} in total");

            var ordered = new List<KeyValuePair<string, int>>(groups);
            ordered.Sort((left, right) => right.Value.CompareTo(left.Value));

            int reported = 0;
            foreach (KeyValuePair<string, int> group in ordered)
            {
                if (reported >= ResourceGroupReportLimit)
                {
                    break;
                }

                _log($"# memory:   x{group.Value}  {group.Key}");
                reported++;
            }
        }

        _graphicsResourcesLast = current;
    }

    private static string DescribeGraphicsResource(object resource)
    {
        try
        {
            string typeName = resource.GetType().Name;
            object name = ReadMember(resource, "Name");
            string nameText = name is null ? "" : " name=" + name;

            if (typeName == "Texture")
            {
                object width = ReadMember(resource, "Width");
                object height = ReadMember(resource, "Height");
                object format = ReadMember(resource, "Format");
                object mipLevels = ReadMember(resource, "MipLevels");
                object flags = ReadMember(resource, "Flags");
                return $"Texture {width}x{height} {format} mips={mipLevels} flags={flags}{nameText}";
            }

            if (typeName == "Buffer")
            {
                object sizeInBytes = ReadMember(resource, "SizeInBytes");
                object flags = ReadMember(resource, "Flags");
                return $"Buffer {sizeInBytes}B flags={flags}{nameText}";
            }

            return typeName + nameText;
        }
        catch (Exception ex)
        {
            return resource.GetType().Name + " (unreadable: " + ex.GetType().Name + ")";
        }
    }

    // ------------------------------------------------------------------ probe: collection census
    //
    // "Which list keeps getting longer?" answered by brute force: the size of every collection
    // reachable in a few steps from the game's static fields and from the DWGame, graphics
    // device, content manager and scene objects. A collection that is longer after every
    // adoption is where something is being kept.

    private const int CensusDepthFromStatics = 2;
    private const int CensusDepthFromRoots = 3;
    private const int CensusReportLimit = 25;

    private static readonly Dictionary<Type, PropertyInfo> _countProperties = new();
    private static readonly Dictionary<Type, FieldInfo[]> _instanceFields = new();

    private static void LogCensusGrowth()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Dictionary<string, int> census = TakeCensus();
        double milliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (_censusLast is not null)
        {
            var growing = new List<(string Path, int Now, int Growth, bool Twice)>();

            foreach (KeyValuePair<string, int> entry in census)
            {
                bool knownBefore = _censusLast.TryGetValue(entry.Key, out int countLast);
                if (!knownBefore || entry.Value <= countLast)
                {
                    continue;
                }

                bool grewTwice = false;
                if (_censusBeforeLast is not null && _censusBeforeLast.TryGetValue(entry.Key, out int countBeforeLast))
                {
                    grewTwice = countLast > countBeforeLast;
                }

                growing.Add((entry.Key, entry.Value, entry.Value - countLast, grewTwice));
            }

            growing.Sort((left, right) => right.Growth.CompareTo(left.Growth));

            _log($"# memory: census of {census.Count:N0} collection(s) in {milliseconds:N0}ms; {growing.Count} grew since the previous adoption");

            int reported = 0;
            foreach (var entry in growing)
            {
                if (reported >= CensusReportLimit)
                {
                    break;
                }

                string marker = entry.Twice ? " (also grew the time before)" : "";
                _log($"# memory:   +{entry.Growth:N0} -> {entry.Now:N0}  {entry.Path}{marker}");
                reported++;
            }
        }
        else
        {
            _log($"# memory: census of {census.Count:N0} collection(s) in {milliseconds:N0}ms (first one; nothing to compare with yet)");
        }

        _censusBeforeLast = _censusLast;
        _censusLast = census;
    }

    private static Dictionary<string, int> TakeCensus()
    {
        var census = new Dictionary<string, int>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        object game = ApplyState.Game;
        object device = ReadMember(game, "GraphicsDevice");
        object content = ReadMember(game, "Content");
        object sceneSystem = ReadMember(game, "SceneSystem");
        object sceneInstance = ReadMember(sceneSystem, "SceneInstance");
        object rootScene = ReadMember(sceneInstance, "RootScene");

        CensusObject("GraphicsDevice", device, CensusDepthFromRoots, census, visited);
        CensusObject("Content", content, CensusDepthFromRoots, census, visited);
        CensusObject("SceneInstance", sceneInstance, CensusDepthFromRoots, census, visited);
        CensusObject("RootScene", rootScene, CensusDepthFromRoots, census, visited);
        CensusObject("DWGame", game, CensusDepthFromRoots, census, visited);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string assemblyName = assembly.GetName().Name ?? "";
            if (!assemblyName.StartsWith("DistantWorlds", StringComparison.Ordinal))
            {
                continue;
            }

            CensusStatics(assembly, census, visited);
        }

        return census;
    }

    private static void CensusStatics(Assembly assembly, Dictionary<string, int> census, HashSet<object> visited)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (Type type in types)
        {
            if (type is null || type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (FieldInfo field in type.GetFields(StaticFields))
            {
                if (field.IsLiteral || field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string))
                {
                    continue;
                }

                object value;
                try
                {
                    value = field.GetValue(null);
                }
                catch
                {
                    continue;
                }

                CensusObject(type.Name + "." + field.Name, value, CensusDepthFromStatics, census, visited);
            }
        }
    }

    private static void CensusObject(string path, object value, int depthLeft, Dictionary<string, int> census, HashSet<object> visited)
    {
        if (value is null || value is string)
        {
            return;
        }

        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type.IsValueType || type.IsPointer)
        {
            return;
        }

        if (value is Array array)
        {
            census[path + "[]"] = array.Length;
            return;
        }

        int count = CollectionCount(value, type);
        if (count >= 0)
        {
            census[path] = count;
            return;
        }

        if (depthLeft <= 0 || !IsGameOrEngineType(type) || type.Name == "Galaxy")
        {
            return;
        }

        if (!visited.Add(value))
        {
            return;
        }

        foreach (FieldInfo field in InstanceFieldsOf(type))
        {
            object fieldValue;
            try
            {
                fieldValue = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            CensusObject(path + "." + field.Name, fieldValue, depthLeft - 1, census, visited);
        }
    }

    private static bool IsGameOrEngineType(Type type)
    {
        string assemblyName = type.Assembly.GetName().Name ?? "";
        return assemblyName.StartsWith("DistantWorlds", StringComparison.Ordinal) ||
               assemblyName.StartsWith("Stride", StringComparison.Ordinal);
    }

    /// <summary>The element count of a collection, or -1 when the object is not one.</summary>
    private static int CollectionCount(object value, Type type)
    {
        try
        {
            if (value is ICollection collection)
            {
                return collection.Count;
            }

            if (!_countProperties.TryGetValue(type, out PropertyInfo countProperty))
            {
                const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo candidate = null;
                try
                {
                    candidate = type.GetProperty("Count", InstanceMembers);
                }
                catch (AmbiguousMatchException)
                {
                    candidate = null;
                }

                bool usable = candidate is not null &&
                              candidate.PropertyType == typeof(int) &&
                              candidate.GetIndexParameters().Length == 0 &&
                              candidate.CanRead;

                countProperty = usable ? candidate : null;
                _countProperties[type] = countProperty;
            }

            if (countProperty is null)
            {
                return -1;
            }

            return (int)countProperty.GetValue(value);
        }
        catch
        {
            return -1;
        }
    }

    private static FieldInfo[] InstanceFieldsOf(Type type)
    {
        if (_instanceFields.TryGetValue(type, out FieldInfo[] cached))
        {
            return cached;
        }

        const BindingFlags DeclaredInstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var fields = new List<FieldInfo>();

        for (Type current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(DeclaredInstanceFields))
            {
                Type fieldType = field.FieldType;
                if (fieldType.IsPrimitive || fieldType.IsEnum || fieldType.IsPointer || fieldType == typeof(string))
                {
                    continue;
                }

                fields.Add(field);
            }
        }

        FieldInfo[] result = fields.ToArray();
        _instanceFields[type] = result;
        return result;
    }
}
