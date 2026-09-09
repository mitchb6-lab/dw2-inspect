using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Deltas: the continuously-changing parts of a galaxy, as a few hundred bytes instead of
/// 24 MB.
///
/// WHY THIS EXISTS. A full state costs ~25-36 MB of unreleasable native memory to adopt,
/// because StartGameExisting acquires resources for the incoming galaxy without releasing
/// the outgoing one's. That cost is per ADOPTION, not per byte -- so compressing the state,
/// or diffing it at the byte level and rebuilding it client-side, would save bandwidth and
/// change the memory problem not at all. The only way out is to stop adopting: update the
/// galaxy the client already has, IN PLACE.
///
/// WHAT IT CARRIES, in two shapes:
///
///   FIELD-LEVEL, thresholded -- for the collections with hundreds of members:
///     ships     position, hull damage, destroyed-ness    -- what moves on screen
///     colonies  corruption, approval, quality, max pop   -- what changes on the map
///     research  per-project progress and researched flag -- what changes in the UI
///
///   WHOLE-OBJECT, membership only -- for the collections with tens:
///     fleets, characters                                 -- created and removed
///
/// The split is by CARDINALITY, not taste. Field-level diffing needs someone to name the
/// fields that matter, which is impossible for objects whose state lives in nested
/// collections (which ships are in a fleet, which traits a character has). Whole-object
/// carries them by serialising the object, which costs a full serialise per object per
/// delta to detect change at all -- affordable for tens, wrong for hundreds.
///
/// STRUCTURE IS CARRIED, using DW2's OWN per-object serialisation. Ship, Colony, Fleet and
/// Character all expose WriteToStream(BinaryWriter) and ReadFromStream(Galaxy, BinaryReader),
/// and their list deserialisers do exactly
///     new T(galaxy); t.ReadFromStream(galaxy, reader); list.Add(t);
/// when loading a save -- except CharacterList, which uses `new Character()`. So no
/// constructor is reproduced and no field list is guessed.
///
/// This was once documented here as out of scope, on the grounds that building a Ship by
/// hand would be too risky. That was true and beside the point: the game already knows how,
/// and looking was worth more than accepting the limit. Before it, the client's ship count
/// drifted from the host's indefinitely -- 38 of 46, then 72 of 78 -- and every update for a
/// ship it had never heard of was skipped forever. Now the gap sits at 1, which is a ship
/// built between the host's snapshot and the client's apply.
///
/// WHY MEMBERSHIP ONLY for the whole-object kinds: see RefreshEvery. Refreshing their
/// CONTENTS was built, measured at 32.4 MB and then 5.4 MB of delta traffic against a
/// 523 KB baseline, and turned off on the numbers. Membership is the part deltas are needed
/// for -- an object the client does not have is one it can never be told about again.
///
/// STILL NOT CARRIED: research projects are created and removed with an empire rather than
/// during play, so they are update-only; and Colony.Population is a per-race collection,
/// which is the nested-collection problem one level down. Diplomacy is untouched.
///
/// Deltas carry change AND the structure they can safely reconstruct; full states carry
/// the rest, and remain the repair path when a delta does not fit.
/// </summary>
public static class StateDelta
{
    private const int Version = 8;

    /// <summary>
    /// Per-kind change thresholds. Without them every float that drifts by a rounding error
    /// counts as "changed" and the delta degenerates into a full dump of the collection.
    /// </summary>
    private const float MinMoveSquared = 0.01f;
    private const float MinDamageChange = 0.001f;
    private const float MinColonyChange = 0.01f;
    private const float MinResearchChange = 0.001f;

    private readonly record struct ShipState(float X, float Y, float Z, float Damage, bool Destroyed);
    private readonly record struct ColonyState(float Corruption, float Approval, float Quality, long MaxPopulation);
    private readonly record struct ResearchState(float Progress, bool Researched);

    // Host: what the client was last told, so we send only what changed since.
    private static readonly Dictionary<int, ShipState> _lastShips = new();
    private static readonly Dictionary<short, ColonyState> _lastColonies = new();
    private static readonly Dictionary<(short Empire, short Project), ResearchState> _lastResearch = new();

    private static readonly object _baseline = new();

    /// <summary>Forget what the client knows. Called after a full state, which resets the baseline.</summary>
    public static void ResetBaseline()
    {
        lock (_baseline)
        {
            _lastShips.Clear();
            _lastColonies.Clear();
            _lastResearch.Clear();
            _lastWhole.Clear();
        }
    }


    /// <summary>
    /// Set the baseline to the galaxy as it is right now, WITHOUT emitting anything.
    ///
    /// Use this after sending a full state rather than ResetBaseline. The client is about to
    /// adopt exactly this galaxy, so nothing in it has "changed" as far as that client is
    /// concerned, and the next delta should carry only what happens AFTER it.
    ///
    /// Clearing instead made the first delta after every full state a complete dump:
    /// 9,270 research projects and 103 KB, repeated for each of the run's five full states —
    /// roughly half of all delta traffic, all of it re-sending what the client had just
    /// received in the state itself.
    ///
    /// Priming at serialisation time, not adoption time, is correct: deltas then describe
    /// changes since the snapshot the client is adopting, which is exactly the baseline it
    /// will be starting from.
    /// </summary>
    public static void PrimeBaseline(object galaxy)
    {
        ResetBaseline();

        // Build populates the baseline dictionaries as a side effect. Discarding the payload
        // is the whole point: the client is receiving this content as a full state instead.
        Build(galaxy, out _, out _, primeOnly: true);
    }
    // ------------------------------------------------------------------ host

    /// <summary>
    /// Build a delta of everything that changed since the last one. Returns null when
    /// nothing did, so a still galaxy costs no traffic at all.
    /// </summary>
    // Per-section timing, so "the delta build costs 5 ms" can name WHICH section. The
    // previous note in the docs guessed research and said so; guessing is what this replaces.
    private static readonly Dictionary<string, double> _sectionMs = new();
    private static long _sectionSamples;

    // Per-BUILD section times, reset each build, alongside the running totals. The averages
    // alone cannot explain a spike: they say what a typical build costs, and a spike is by
    // definition not typical. A 24.8 ms worst against a 0.8 ms mean is either one build doing
    // far more work than the rest, or something outside this code stalling it -- and those
    // want different answers, so the instrumentation has to separate them.
    private static readonly Dictionary<string, double> _thisBuildMs = new();

    private static int Timed(string name, Func<int> section)
    {
        var sw = Stopwatch.StartNew();
        try { return section(); }
        finally
        {
            var ms = sw.Elapsed.TotalMilliseconds;
            _sectionMs[name] = _sectionMs.TryGetValue(name, out var v) ? v + ms : ms;
            _thisBuildMs[name] = ms;
        }
    }

    /// <summary>What the most recent build spent, section by section.</summary>
    public static string LastBuildBreakdown()
    {
        lock (_baseline)
        {
            if (_thisBuildMs.Count == 0) return "no sections";
            return string.Join(" ", _thisBuildMs
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value:N2}ms"));
        }
    }

    /// <summary>
    /// Garbage collections that happened during the most recent build.
    ///
    /// The discriminator for the spike. If a slow build coincides with a gen2 collection it
    /// is the GC stalling us, and no amount of making the sections faster will remove it --
    /// the answer would be allocating less. If it does not, the work really is ours.
    /// </summary>
    public static string LastBuildCollections() =>
        $"gc0={_gcDuringBuild[0]} gc1={_gcDuringBuild[1]} gc2={_gcDuringBuild[2]}";

    private static readonly int[] _gcDuringBuild = new int[3];

    /// <summary>Mean milliseconds per build, per section. For logging.</summary>
    public static string SectionTimings()
    {
        lock (_baseline)
        {
            if (_sectionSamples == 0) return "no samples";
            return string.Join(" ", _sectionMs
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value / _sectionSamples:N2}ms"));
        }
    }

    public static byte[] Build(object galaxy, out int changed, out int total, bool primeOnly = false)
    {
        changed = 0;
        total = 0;

        if (galaxy is null) return null;

        try
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true);

            writer.Write(Version);

            lock (_baseline)
            {
                if (!primeOnly) { _deltaSequence++; _sectionSamples++; }

                _thisBuildMs.Clear();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

                foreach (var kind in WholeObjectKinds)
                {
                    var k = kind;
                    changed += Timed(k.Collection.ToLowerInvariant(),
                                     () => WriteWholeObjectSection(galaxy, writer, k, primeOnly));
                }
                int shipTotal = 0;
                changed += Timed("ships", () => WriteShipSection(galaxy, writer, out shipTotal, primeOnly));
                total = shipTotal;

                changed += Timed("colonies", () => WriteColonySection(galaxy, writer, primeOnly));
                changed += Timed("research", () => WriteResearchSection(galaxy, writer));


                changed += Timed("fleets", () => WriteFleetSection(galaxy, writer, primeOnly));
                _gcDuringBuild[0] = GC.CollectionCount(0) - g0;
                _gcDuringBuild[1] = GC.CollectionCount(1) - g1;
                _gcDuringBuild[2] = GC.CollectionCount(2) - g2;
            }

            if (changed == 0) return null;

            writer.Flush();
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }
    /// <summary>
    /// Ships: removals, additions and updates.
    ///
    /// ADDITIONS carry the ship SERIALISED BY DW2 ITSELF -- Ship.WriteToStream on the host,
    /// `new Ship(galaxy)` then Ship.ReadFromStream on the client, which is exactly what
    /// ShipList.ReadFromStream does when loading a save. That is what makes creating objects
    /// client-side safe: no constructor is reproduced and no field list is guessed. The
    /// earlier refusal to handle creation assumed we would have to build a Ship by hand;
    /// the game already knows how, and it was worth looking before accepting the limit.
    ///
    /// A brand-new ship is an ADD, not an update: it is absent from the baseline, so sending
    /// it as an update would name an id the client does not have and be skipped forever.
    /// </summary>
    private static int WriteShipSection(object galaxy, BinaryWriter writer, out int total, bool primeOnly)
    {
        var ships = ListField(galaxy, "Ships");
        var item = Indexer(ships);
        total = CountOf(ships);

        // The host's total goes on the wire even when nothing moved: it is how the client
        // reports "I have 46 of your 57" without a separate message.
        writer.Write(total);

        var present = new HashSet<int>();
        var added = new List<object>();
        var updated = new List<(int Id, ShipState State)>();

        for (int i = 0; i < total && item is not null; i++)
        {
            var ship = At(item, ships, i);
            if (ship is null || !ResolveShip(ship)) continue;
            if (!TryReadShip(ship, out var id, out var now)) continue;

            present.Add(id);

            if (!_lastShips.TryGetValue(id, out var before))
            {
                _lastShips[id] = now;
                added.Add(ship);
                continue;
            }

            if (!ShipDiffers(before, now)) continue;

            _lastShips[id] = now;
            updated.Add((id, now));
        }

        var removed = _lastShips.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in removed) _lastShips.Remove(id);

        writer.Write(removed.Count);
        foreach (var id in removed) writer.Write(id);

        // Priming only needs the baseline populated; serialising every ship into a buffer
        // that is thrown away costs a WriteToStream per ship for nothing.
        writer.Write(primeOnly ? 0 : added.Count);
        if (!primeOnly)
        {
            foreach (var ship in added)
            {
                var bytes = SerialiseObject(ship, galaxy.GetType());
                writer.Write(bytes?.Length ?? 0);
                if (bytes is { Length: > 0 }) writer.Write(bytes);
            }
        }

        writer.Write(updated.Count);
        foreach (var (id, s) in updated)
        {
            writer.Write(id);
            writer.Write(s.X); writer.Write(s.Y); writer.Write(s.Z);
            writer.Write(s.Damage);
            writer.Write(s.Destroyed);
        }

        return removed.Count + (primeOnly ? 0 : added.Count) + updated.Count;
    }

    // --- creating and removing objects, using DW2's OWN per-object serialisation
    //
    // Ship and Colony both expose WriteToStream(BinaryWriter) and
    // ReadFromStream(Galaxy, BinaryReader), and their list deserialisers do exactly
    //     new T(galaxy); t.ReadFromStream(galaxy, reader); list.Add(t);
    // when loading a save. Using that is what makes creating objects client-side safe: no
    // constructor is reproduced and no field list is guessed.
    //
    // This was the stated reason creation was out of scope -- "reproducing a constructor we
    // cannot read". The game already knows how, and it was worth looking before accepting
    // the limitation.

    private readonly record struct Serialiser(ConstructorInfo Ctor, MethodInfo Read, MethodInfo Write, MethodInfo Regenerate);

    private static readonly Dictionary<Type, Serialiser> _serialisers = new();

    private static Serialiser SerialiserFor(Type type, Type galaxyType)
    {
        lock (_serialisers)
        {
            if (_serialisers.TryGetValue(type, out var cached)) return cached;

            // (Galaxy) first, then parameterless. DW2's list deserialisers are not uniform:
            // ShipList/ColonyList/FleetList do `new T(galaxy)`, but CharacterList does
            // `new Character()`. Looking only for the (Galaxy) form returned a null
            // constructor for Character and would have failed silently -- the object simply
            // never gets created and the section reports it absent forever.
            var ctor = type.GetConstructor(new[] { galaxyType })
                    ?? type.GetConstructor(Type.EmptyTypes);

            var made = new Serialiser(
                ctor,
                AccessTools.Method(type, "ReadFromStream", new[] { galaxyType, typeof(BinaryReader) }),
                AccessTools.Method(type, "WriteToStream", new[] { typeof(BinaryWriter) }),
                AccessTools.Method(type, "RegenerateSummary", Type.EmptyTypes));

            _serialisers[type] = made;
            return made;
        }
    }

    /// <summary>Construct through whichever form this type's list deserialiser uses.</summary>
    private static object Construct(Serialiser s, object galaxy) =>
        s.Ctor is null ? null
        : s.Ctor.GetParameters().Length == 1 ? s.Ctor.Invoke(new[] { galaxy })
        : s.Ctor.Invoke(null);

    private static byte[] SerialiseObject(object item, Type galaxyType)
    {
        try
        {
            var s = SerialiserFor(item.GetType(), galaxyType);
            if (s.Write is null) return null;

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                s.Write.Invoke(item, new object[] { writer });
                writer.Flush();
            }

            return buffer.ToArray();
        }
        catch { return null; }
    }


    /// <summary>
    /// Colonies: removals, additions and updates, exactly as for ships.
    ///
    /// Colonies are founded and lost during a game, so without creation the client's colony
    /// set would drift from the host's the same way its ship set did -- and colony updates
    /// for a colony it has never heard of are skipped forever.
    /// </summary>
    private static int WriteColonySection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        var colonies = ListField(galaxy, "Colonies");
        var item = Indexer(colonies);
        int count = CountOf(colonies);

        var present = new HashSet<short>();
        var added = new List<object>();
        var updated = new List<(short Id, ColonyState State)>();

        for (int i = 0; i < count && item is not null; i++)
        {
            var colony = At(item, colonies, i);
            if (colony is null || !ResolveColony(colony)) continue;
            if (!TryReadColony(colony, out var id, out var now)) continue;

            present.Add(id);

            if (!_lastColonies.TryGetValue(id, out var before))
            {
                _lastColonies[id] = now;
                added.Add(colony);
                continue;
            }

            if (!ColonyDiffers(before, now)) continue;

            _lastColonies[id] = now;
            updated.Add((id, now));
        }

        var removed = _lastColonies.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in removed) _lastColonies.Remove(id);

        writer.Write(removed.Count);
        foreach (var id in removed) writer.Write(id);

        writer.Write(primeOnly ? 0 : added.Count);
        if (!primeOnly)
        {
            foreach (var colony in added)
            {
                var bytes = SerialiseObject(colony, galaxy.GetType());
                writer.Write(bytes?.Length ?? 0);
                if (bytes is { Length: > 0 }) writer.Write(bytes);
            }
        }

        writer.Write(updated.Count);
        foreach (var (id, c) in updated)
        {
            writer.Write(id);
            writer.Write(c.Corruption); writer.Write(c.Approval); writer.Write(c.Quality);
            writer.Write(c.MaxPopulation);
        }

        return removed.Count + (primeOnly ? 0 : added.Count) + updated.Count;
    }

    // Compiled accessors for the research walk. See FastAccess: this loop touches ~10,300
    // projects on every delta and was 87% of the whole delta build under plain reflection.
    // Resolved lazily against the first real object rather than by type name, so a renamed
    // or restructured type degrades to "section reports nothing" exactly as before instead
    // of throwing.
    private static Func<object, short> _fastProjectId;
    private static Func<object, float> _fastProgress;
    private static Func<object, bool> _fastResearched;
    private static Func<object, short> _fastEmpireId;
    private static bool _fastResearchResolved;

    private static bool ResolveFastResearch(object empire, object project)
    {
        if (_fastResearchResolved) return _fastProjectId is not null;

        _fastEmpireId = FastAccess.Getter<short>(empire.GetType(), "EmpireId");
        _fastProjectId = FastAccess.Getter<short>(project.GetType(), "ResearchProjectId");
        _fastProgress = FastAccess.Getter<float>(project.GetType(), "Progress");
        _fastResearched = FastAccess.Getter<bool>(project.GetType(), "Researched");

        _fastResearchResolved = true;
        return _fastProjectId is not null;
    }

    /// <summary>
    /// How often the research section walks. Every 5th delta is ~15 s of simulated time.
    ///
    /// Safe here in a way it is NOT for fleet and character membership, and the difference
    /// is worth stating: research projects are created with their empire and already exist
    /// on the client, so this section is update-only. A delayed update is pure staleness --
    /// nothing becomes irrecoverable. A delayed CREATION would be, because every later
    /// update naming an object the client does not have is skipped as absent forever.
    ///
    /// The walk is what costs: ~10,300 projects per delta, measured at 3.97 ms of a 4.55 ms
    /// build under plain reflection and 1.96 ms of 2.3 ms with compiled accessors -- still
    /// ~85% of the whole build, for a handful of records. Compiling made each visit cheap;
    /// this makes most visits not happen.
    /// </summary>
    private const int ResearchEvery = 5;

    private static int WriteResearchSection(object galaxy, BinaryWriter writer)
    {
        // The count still goes on the wire on a skipped delta -- the reader consumes sections
        // positionally, so a section that writes nothing at all would desynchronise it.
        if (_deltaSequence % ResearchEvery != 0)
        {
            writer.Write(0);
            return 0;
        }

        var empires = ListField(galaxy, "Empires");
        var empireItem = Indexer(empires);
        int empireCount = CountOf(empires);

        var records = new List<(short Empire, short Project, ResearchState State)>();

        for (int e = 0; e < empireCount && empireItem is not null; e++)
        {
            var empire = At(empireItem, empires, e);
            if (empire is null) continue;

            var research = AccessTools.Field(empire.GetType(), "Research")?.GetValue(empire);
            if (research is null) continue;

            var projects = AccessTools.Field(research.GetType(), "Projects")?.GetValue(research);
            int projectCount = CountOf(projects);
            if (projects is null || projectCount == 0) continue;

            var projectItem = FastAccess.Indexer(projects.GetType());
            if (projectItem is null) continue;

            var first = projectItem(projects, 0);
            if (first is null || !ResolveFastResearch(empire, first)) continue;

            // Int16, not Int32. Reading it as int made the pattern fail for EVERY empire and
            // the research section came out empty in every delta, silently -- the same class
            // of mistake as reading Empire.Name as a field when it is a property.
            if (_fastEmpireId is null) continue;
            short empireId = _fastEmpireId(empire);

            for (int p = 0; p < projectCount; p++)
            {
                var project = projectItem(projects, p);
                if (project is null) continue;

                var now = new ResearchState(_fastProgress(project), _fastResearched(project));
                var key = (empireId, _fastProjectId(project));

                if (_lastResearch.TryGetValue(key, out var before) && !ResearchDiffers(before, now)) continue;

                _lastResearch[key] = now;
                records.Add((key.Item1, key.Item2, now));
            }
        }

        writer.Write(records.Count);
        foreach (var (empireId, projectId, r) in records)
        {
            writer.Write(empireId);
            writer.Write(projectId);
            writer.Write(r.Progress);
            writer.Write(r.Researched);
        }

        return records.Count;
    }


    // ------------------------------------------- whole-object sections (fleets, characters)
    //
    // Fleets and characters are carried WHOLE rather than field by field, and refreshed IN
    // PLACE rather than replaced.
    //
    // Why whole. Their meaningful state lives in nested collections and object references --
    // which ships are in a fleet, which traits and skills a character has, where it is
    // stationed. A scalar diff cannot express any of that, which is exactly why both were
    // excluded when deltas carried only scalars. Serialising the object hands the whole
    // problem to DW2's own writer.
    //
    // Why in place. Ship.ReadFromStream and Character.ReadFromStream both end `ldarg.0; ret`
    // -- they populate the instance they are CALLED ON and return it. So an object the client
    // already has can be refreshed by calling ReadFromStream on it with the host's bytes:
    // every field repopulated by DW2's reader, and the object identity preserved. Removing
    // and re-adding would have worked too, and would have broken every reference another
    // object holds to the old instance and reordered the list for no gain.
    //
    // Why not ships. This costs a full serialise per object per delta, on both sides, to
    // detect change at all. That is affordable for tens of fleets and characters and wrong
    // for hundreds of ships -- which is why ships keep field-level diffing with thresholds.
    // The two strategies are chosen by cardinality, not by taste.
    //
    // Change detection is a hash of the serialised bytes: exact, with no threshold to tune
    // and no judgement about which fields matter.

    private sealed record WholeObjectKind(string Collection, string TypeName, string IdField, bool IdIsShort);
    /// <summary>
    /// Galaxy-level whole-object collections.
    ///
    /// Fleets are NOT here, and the reason is worth recording. `Galaxy.Fleets` exists, is a
    /// FleetList, and is permanently EMPTY -- fleets live on `Empire.Fleets`, one list per
    /// empire. Pointed at the galaxy-level field, the section ran 330 times across a 480-second
    /// run in a 50-star galaxy with 19 empires and 576 ships and saw `n=0` every time.
    ///
    /// The census reported `Fleets=0` from the first run and that was read as "this galaxy has
    /// no fleets yet". It actually meant "this collection has no fleets, ever". A count of zero
    /// cannot tell an empty collection from the wrong one -- and a plausible field name is not
    /// evidence it is the one the game uses, which is the same trap as Empire.Name being a
    /// property and EmpireId being Int16.
    /// </summary>
    private static readonly WholeObjectKind[] WholeObjectKinds =
    {
        // Designs FIRST. A ship resolves its hull through Galaxy.Designs.GetById(DesignId) on
        // first use, and the AI designs a ship before it builds one -- so a created ship
        // routinely references a design the client has never seen. Without this, the new
        // ship arrives, its design does not, GetShipHull() is null, and RegenerateSummary
        // and later ShipSummary.DetermineShipSummary both dereference it: 100-173 silent
        // creation failures per run, then 506-626 crash dumps once the objects were kept.
        // The probe that named it: `GetShipHull=NULL` with every other input ok.
        new("Designs",    "DistantWorlds.Types.Design",    "DesignId",    IdIsShort: false),
        new("Characters", "DistantWorlds.Types.Character", "CharacterId", IdIsShort: false),
    };

    private static readonly Dictionary<(string Kind, long Id), ulong> _lastWhole = new();

    private static ulong HashBytes(byte[] bytes)
    {
        // FNV-1a. Only ever compared against itself, so the choice is about speed, not
        // cryptography -- and a collision costs one skipped update, not corruption.
        ulong h = 14695981039346656037UL;
        foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>
    /// How often a whole object is refreshed when only its CONTENTS changed.
    /// **0 means never** -- creations and removals only, which is where this landed.
    ///
    /// Measured over three 240-second runs, against a 523 KB baseline before fleets and
    /// characters existed:
    ///
    ///     refresh every delta   32.4 MB   -- more than the four full states it replaces
    ///     refresh every 10th     5.4 MB   -- 4 characters eating ~90% of the delta budget
    ///     membership only        (below)
    ///
    /// The detector is a byte hash, which is exact and therefore cannot tell "this
    /// character's allegiance flipped" from "a timestamp advanced". Character carries
    /// DateArrivedAtLocation, LastBattleDate, GhostCountdown and a growing GameEvents list,
    /// so every character is "changed" on every delta at ~3.5 KB each.
    ///
    /// At every-10th the refresh interval is 30 s against a full state every 60 s -- twice
    /// as fresh, for roughly ten times the traffic, to keep FOUR objects current. That is a
    /// bad exchange rate, and it is the measurement rather than the taste that decides it.
    ///
    /// What deltas are actually needed for here is SET MEMBERSHIP: an object the client does
    /// not have is one it can never be told about again, because every update naming it is
    /// skipped as absent. Contents are merely stale, and a full state fixes them. So
    /// membership is immediate and contents wait.
    ///
    /// Raise this above 0 if a client-side feature ever needs fresher fleet or character
    /// internals than the full-state interval; the machinery is intact and this is the only
    /// line to change.
    /// </summary>
    private const int RefreshEvery = 0;

    private static long _deltaSequence;

    private static int WriteWholeObjectSection(object galaxy, BinaryWriter writer, WholeObjectKind kind, bool primeOnly)
    {
        var list = ListField(galaxy, kind.Collection);
        var item = Indexer(list);
        int count = CountOf(list);

        bool refreshWindow = primeOnly || (RefreshEvery > 0 && _deltaSequence % RefreshEvery == 0);

        var present = new HashSet<long>();
        var upserts = new List<(long Id, byte[] Bytes)>();

        for (int i = 0; i < count && item is not null; i++)
        {
            var element = At(item, list, i);
            if (element is null) continue;

            var idField = AccessTools.Field(element.GetType(), kind.IdField);
            if (idField is null) break;

            long id;
            try { id = Convert.ToInt64(idField.GetValue(element)); }
            catch { continue; }

            present.Add(id);

            var key = (kind.Collection, id);
            bool isNew = !_lastWhole.ContainsKey(key);

            // Outside a refresh window an existing object is not even serialised. That is
            // most of the saving: serialising to compare is the expensive half.
            if (!isNew && !refreshWindow) continue;

            var bytes = SerialiseObject(element, galaxy.GetType());
            if (bytes is null) continue;

            var hash = HashBytes(bytes);
            if (!isNew && _lastWhole[key] == hash) continue;

            _lastWhole[key] = hash;
            if (!primeOnly) upserts.Add((id, bytes));
        }

        var removed = _lastWhole.Keys
            .Where(k => k.Kind == kind.Collection && !present.Contains(k.Id))
            .Select(k => k.Id)
            .ToList();

        foreach (var id in removed) _lastWhole.Remove((kind.Collection, id));

        writer.Write(removed.Count);
        foreach (var id in removed) WriteId(writer, id, kind.IdIsShort);

        writer.Write(upserts.Count);
        foreach (var (id, bytes) in upserts)
        {
            WriteId(writer, id, kind.IdIsShort);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        _lastWholeCounts[kind.Collection.ToLowerInvariant()] = (count, upserts.Count, removed.Count);

        return removed.Count + upserts.Count;
    }

    private static void WriteId(BinaryWriter writer, long id, bool asShort)
    {
        if (asShort) writer.Write((short)id); else writer.Write((int)id);
    }

    private static long ReadId(BinaryReader reader, bool asShort) =>
        asShort ? reader.ReadInt16() : reader.ReadInt32();

    private static int ApplyWholeObjectSection(object galaxy, BinaryReader reader, WholeObjectKind kind, out int absent)
    {
        absent = 0;

        var list = ListField(galaxy, kind.Collection);
        var getById = ById(list, kind.IdIsShort ? typeof(short) : typeof(int))
                   ?? ById(list, kind.IdIsShort ? typeof(int) : typeof(short));
        var type = AccessTools.TypeByName(kind.TypeName);
        int applied = 0;

        int removedCount = reader.ReadInt32();
        for (int i = 0; i < removedCount; i++)
        {
            long id = ReadId(reader, kind.IdIsShort);
            var element = Lookup(getById, list, id);
            if (element is null) { absent++; continue; }

            if (RemoveFromList(list, element)) applied++; else absent++;
        }

        int upsertCount = reader.ReadInt32();
        for (int i = 0; i < upsertCount; i++)
        {
            long id = ReadId(reader, kind.IdIsShort);
            int length = reader.ReadInt32();
            var bytes = length > 0 ? reader.ReadBytes(length) : null;
            if (bytes is null || bytes.Length != length) { absent++; continue; }

            var existing = Lookup(getById, list, id);

            if (existing is not null)
            {
                if (RefreshInPlace(galaxy, existing, bytes)) applied++; else absent++;
                continue;
            }

            if (AddFromBytes(galaxy, list, type, bytes)) applied++; else absent++;
        }

        return applied;
    }

    /// <summary>
    /// Repopulate an object the client already has, from the host's bytes, without replacing
    /// it. See the section comment above for why identity is worth preserving.
    /// </summary>
    private static bool RefreshInPlace(object galaxy, object existing, byte[] bytes)
    {
        try
        {
            var s = SerialiserFor(existing.GetType(), galaxy.GetType());
            if (s.Read is null) return false;

            using var input = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            var result = s.Read.Invoke(existing, new[] { galaxy, (object)reader });
            if (result is null) return false;      // the reader's own bad-id early-out

            s.Regenerate?.Invoke(existing, null);
            return true;
        }
        catch { return false; }
    }
    // ---------------------------------------------------------------- client

    /// <summary>
    /// Apply a delta to the galaxy in place.
    ///
    /// Records the client does not have are skipped and counted, not treated as an error --
    /// see the class comment. Returning false is reserved for a delta that cannot be read at
    /// all, which is a protocol problem rather than a state one.
    /// </summary>
    public static bool Apply(object galaxy, byte[] payload, out string info)
    {
        info = "";

        if (galaxy is null) { info = "no galaxy"; return false; }

        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            if (reader.ReadInt32() != Version) { info = "delta version mismatch"; return false; }

            var parts = new List<string>();

            // Order matters and is the wire format: the reader must consume these sections in
            // the same order the writer produced them, so both iterate WholeObjectKinds.
            foreach (var kind in WholeObjectKinds)
            {
                int n = ApplyWholeObjectSection(galaxy, reader, kind, out int missed);
                CountApplied(kind.Collection.ToLowerInvariant(), n);
                if (n + missed > 0)
                    parts.Add($"{n} {kind.Collection.ToLowerInvariant()}" + (missed > 0 ? $" +{missed} absent" : ""));
            }

            int ships = ApplyShips(galaxy, reader, out int absentShips, out int hostShips, out int localShips);
            int colonies = ApplyColonies(galaxy, reader, out int absentColonies);
            int research = ApplyResearch(galaxy, reader, out int absentResearch);

            CountApplied("ships", ships);
            CountApplied("colonies", colonies);
            CountApplied("research", research);

            if (ships + absentShips > 0) parts.Add($"{ships} ship(s)" + (absentShips > 0 ? $" +{absentShips} absent" : ""));
            if (colonies + absentColonies > 0) parts.Add($"{colonies} colony" + (absentColonies > 0 ? $" +{absentColonies} absent" : ""));
            if (research + absentResearch > 0) parts.Add($"{research} project(s)" + (absentResearch > 0 ? $" +{absentResearch} absent" : ""));


            // Fleets read LAST, matching the order Build writes them. The reader consumes
            // sections positionally, so this pairing IS the wire format: swap the two and
            // every field afterwards is read from the wrong offsets.
            int fleets = ApplyFleetSection(galaxy, reader, out int absentFleets);
            CountApplied("fleets", fleets);
            if (fleets + absentFleets > 0)
                parts.Add($"{fleets} fleet(s)" + (absentFleets > 0 ? $" +{absentFleets} absent" : ""));

            info = string.Join(", ", parts) +
                   (hostShips != localShips ? $" [have {localShips} of host's {hostShips} ships]" : "") +
                   $", {payload.Length:N0}B";
            return true;
        }
        catch (Exception ex)
        {
            info = (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    /// <summary>
    /// Ships: removals first, then additions, then updates.
    ///
    /// That order matters. A ship destroyed and its id reused would otherwise be added
    /// before the old one is gone; and an update for a ship arriving in the same delta must
    /// land after the add, not before it.
    /// </summary>
    private static int ApplyShips(object galaxy, BinaryReader reader, out int absent, out int hostTotal, out int localTotal)
    {
        absent = 0;

        var ships = ListField(galaxy, "Ships");
        var getById = ById(ships, typeof(int));

        hostTotal = reader.ReadInt32();
        localTotal = CountOf(ships);
        int applied = 0;

        // --- removals
        int removedCount = reader.ReadInt32();
        for (int i = 0; i < removedCount; i++)
        {
            int id = reader.ReadInt32();
            var ship = Lookup(getById, ships, id);
            if (ship is null) { absent++; continue; }

            if (RemoveFromList(ships, ship)) applied++;
            else absent++;
        }

        // --- additions, deserialised by DW2's own Ship.ReadFromStream
        int addedCount = reader.ReadInt32();
        for (int i = 0; i < addedCount; i++)
        {
            int length = reader.ReadInt32();
            var bytes = length > 0 ? reader.ReadBytes(length) : null;
            if (bytes is null || bytes.Length != length) { absent++; continue; }

            if (AddFromBytes(galaxy, ships, AccessTools.TypeByName("DistantWorlds.Types.Ship"), bytes)) applied++;
            else absent++;
        }

        // --- updates
        int updatedCount = reader.ReadInt32();
        for (int i = 0; i < updatedCount; i++)
        {
            int id = reader.ReadInt32();
            var state = new ShipState(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                                      reader.ReadSingle(), reader.ReadBoolean());

            var ship = Lookup(getById, ships, id);
            if (ship is null || !ResolveShip(ship)) { absent++; continue; }

            WriteShip(ship, state);
            applied++;
        }

        return applied;
    }

    /// <summary>Set by NetSession so mutation failures can be reported. Null-safe.</summary>
    public static Action<string> Log { get; set; }

    private static readonly HashSet<string> _mutationFailures = new();
    private static long _mutationFailureCount;

    /// <summary>
    /// Report a failed add or remove -- ONCE per distinct reason, with a running count.
    ///
    /// These used to be `catch { return false; }`. A creation that fails silently is not a
    /// dropped record, it is an object the client will never have: every later update naming
    /// it is skipped as absent, forever. In the 50-star run the client's ship count swung
    /// from 86 behind the host to 19 AHEAD of it, which a non-simulating client cannot do
    /// unless removals are not landing either. Both were failing and both were reporting
    /// success.
    /// </summary>
    private static void NoteMutationFailure(string reason)
    {
        Interlocked.Increment(ref _mutationFailureCount);
        bool isNew;
        lock (_mutationFailures) isNew = _mutationFailures.Add(reason);
        if (isNew)
            Log?.Invoke($"# delta: MUTATION FAILED -- {reason} (distinct #{_mutationFailures.Count}, " +
                        $"{Interlocked.Read(ref _mutationFailureCount)} total)");
    }

    public static string MutationFailureSummary() =>
        $"mutation failures={Interlocked.Read(ref _mutationFailureCount)} distinct={_mutationFailures.Count}";

    /// <summary>The id field DW2 uses for each type we create or remove.</summary>
    private static string IdFieldFor(Type type) => type.Name switch
    {
        "Ship"      => "ShipId",
        "Colony"    => "ColonyId",
        "Fleet"     => "FleetId",
        "Character" => "CharacterId",
        "Design"    => "DesignId",
        _           => null,
    };

    /// <summary>
    /// Is this exact element in the list, by id? The only trustworthy answer to "did that
    /// mutation happen". See AddFromBytes for why neither a method name nor its return is.
    /// </summary>
    private static bool ListContains(object list, object element, out string why)
    {
        why = "";
        var idField = IdFieldFor(element.GetType());
        var field = idField is null ? null : AccessTools.Field(element.GetType(), idField);
        if (field is null) { why = $"no id field for {element.GetType().Name}"; return false; }

        long id;
        try { id = Convert.ToInt64(field.GetValue(element)); }
        catch (Exception ex) { why = "id unreadable: " + ex.GetType().Name; return false; }

        var getById = ById(list, field.FieldType == typeof(short) ? typeof(short) : typeof(int))
                   ?? ById(list, field.FieldType == typeof(short) ? typeof(int) : typeof(short));
        if (getById is null) { why = $"{list.GetType().Name} has no GetById"; return false; }

        return ReferenceEquals(Lookup(getById, list, id), element);
    }

    /// <summary>
    /// Rebuild an object from the host's bytes and put it in the list.
    ///
    /// ORDER: read, ADD, verify, then regenerate. The first version regenerated before
    /// adding, and Ship.RegenerateSummary threw NullReferenceException on every ship that was
    /// not yet in the galaxy -- 173 times in one run, one distinct reason, and each one a
    /// ship the client then never had. The game's own order is the list deserialiser adding
    /// the object and summaries being rebuilt afterwards, on objects that already belong to a
    /// galaxy; this now matches it. Regeneration failing is logged and does NOT discard the
    /// object: a ship without a summary is a known, guarded hazard, a ship that does not
    /// exist is an absent-forever one.
    ///
    /// Uses `Add`, because that is what DW2's own list deserialisers call. The earlier
    /// preference for `CheckAdd` was based on reading its IL as a no-op; on a generic type
    /// that IL was an unrestored placeholder, and the staged failure log showed no Add ever
    /// failed. A method's name is not evidence, and neither was that reading.
    ///
    /// Then READS THE LIST BACK. A `true` return does not prove the mutation happened;
    /// GetById does.
    /// </summary>
    private static bool AddFromBytes(object galaxy, object list, Type elementType, byte[] bytes)
    {
        string stage = "start";
        string name = elementType?.Name ?? "?";

        try
        {
            if (list is null || elementType is null) { NoteMutationFailure($"{name}: no list or type"); return false; }

            var s = SerialiserFor(elementType, galaxy.GetType());
            if (s.Read is null) { NoteMutationFailure($"{name}: no ReadFromStream"); return false; }

            stage = "construct";
            var blank = Construct(s, galaxy);
            if (blank is null) { NoteMutationFailure($"{name}: no usable constructor"); return false; }

            stage = "read";
            object item;
            using (var input = new MemoryStream(bytes, writable: false))
            using (var reader = new BinaryReader(input, System.Text.Encoding.UTF8))
                item = s.Read.Invoke(blank, new[] { galaxy, (object)reader });

            if (item is null) { NoteMutationFailure($"{name}: ReadFromStream returned null"); return false; }

            stage = "add";
            var add = AccessTools.Method(list.GetType(), "Add", new[] { elementType })
                   ?? AccessTools.Method(list.GetType(), "Add");
            if (add is null) { NoteMutationFailure($"{name}: {list.GetType().Name} has no Add"); return false; }

            var result = add.Invoke(list, new[] { item });
            if (result is false) { NoteMutationFailure($"{name}: Add returned false"); return false; }

            stage = "verify";
            if (!ListContains(list, item, out var why))
            {
                NoteMutationFailure($"{name}: Add reported success but the list does not contain it" +
                                    (why.Length > 0 ? $" ({why})" : ""));
                return false;
            }

            // Only now, with the object in its galaxy. Failure here is reported, not fatal.
            stage = "regenerate";
            try { s.Regenerate?.Invoke(item, null); }
            catch (Exception ex)
            {
                var cause = ex.InnerException ?? ex;
                NoteMutationFailure($"{name}: regenerate after add: {cause.GetType().Name} (object kept)");
                ProbeSummaryInputs(galaxy, item);
            }

            return true;
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            NoteMutationFailure($"{name} at {stage}: {cause.GetType().Name}: {cause.Message}");
            return false;
        }
    }

    private static bool _loggedTombstone;

    /// <summary>
    /// Remove, then READ BACK by id -- and by COUNT.
    ///
    /// The count check exists because of a specific observation: with zero removal failures
    /// reported, the client's ship count still swung to 178 MORE than the host's. Read-back
    /// by GetById said every removed ship was gone; Count said otherwise. Both are true if
    /// removal clears the index and leaves a tombstone in the backing array that only the
    /// simulation's compaction reclaims -- and the client does not simulate. If that is the
    /// mechanism, Count is not a measure of membership on the client, and the host-vs-client
    /// gap in the log is an artefact. This logs it once so the question is answered by the
    /// next run rather than argued about.
    /// </summary>
    private static bool RemoveFromList(object list, object element)
    {
        string name = element?.GetType().Name ?? "?";

        try
        {
            var remove = AccessTools.Method(list.GetType(), "FastRemove", new[] { element.GetType() })
                      ?? AccessTools.Method(list.GetType(), "Remove", new[] { element.GetType() })
                      ?? AccessTools.Method(list.GetType(), "FastRemove");
            if (remove is null) { NoteMutationFailure($"{name}: {list.GetType().Name} has no FastRemove/Remove"); return false; }

            int before = CountOf(list);
            var result = remove.Invoke(list, new[] { element });
            if (result is false) { NoteMutationFailure($"{name}: remove returned false"); return false; }

            if (ListContains(list, element, out _))
            {
                NoteMutationFailure($"{name}: remove reported success but the list still contains it");
                return false;
            }

            int after = CountOf(list);
            if (after == before && !_loggedTombstone)
            {
                _loggedTombstone = true;
                Log?.Invoke($"# delta: NOTE — {name} removed by id but {list.GetType().Name}.Count did not drop " +
                            $"({before} -> {after}). Count includes tombstones; the host/client ship gap is " +
                            "an artefact of that, not of missing removals. Logged once.");
            }

            return true;
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            NoteMutationFailure($"{name} remove: {cause.GetType().Name}: {cause.Message}");
            return false;
        }
    }

    private static int ApplyColonies(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        var colonies = ListField(galaxy, "Colonies");
        var getById = ById(colonies, typeof(short)) ?? ById(colonies, typeof(int));
        var colonyType = AccessTools.TypeByName("DistantWorlds.Types.Colony");
        int applied = 0;

        int removedCount = reader.ReadInt32();
        for (int i = 0; i < removedCount; i++)
        {
            short id = reader.ReadInt16();
            var colony = Lookup(getById, colonies, id);
            if (colony is null) { absent++; continue; }

            if (RemoveFromList(colonies, colony)) applied++; else absent++;
        }

        int addedCount = reader.ReadInt32();
        for (int i = 0; i < addedCount; i++)
        {
            int length = reader.ReadInt32();
            var bytes = length > 0 ? reader.ReadBytes(length) : null;
            if (bytes is null || bytes.Length != length) { absent++; continue; }

            if (AddFromBytes(galaxy, colonies, colonyType, bytes)) applied++; else absent++;
        }

        int updatedCount = reader.ReadInt32();
        for (int i = 0; i < updatedCount; i++)
        {
            short id = reader.ReadInt16();
            var state = new ColonyState(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                                        reader.ReadInt64());

            var colony = Lookup(getById, colonies, id);
            if (colony is null || !ResolveColony(colony)) { absent++; continue; }

            WriteColony(colony, state);
            applied++;
        }

        return applied;
    }

    private static int ApplyResearch(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        var empires = ListField(galaxy, "Empires");
        var empireById = ById(empires, typeof(short)) ?? ById(empires, typeof(int));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int i = 0; i < count; i++)
        {
            short empireId = reader.ReadInt16();
            short projectId = reader.ReadInt16();
            var state = new ResearchState(reader.ReadSingle(), reader.ReadBoolean());

            var empire = Lookup(empireById, empires, empireId);
            var research = empire is null ? null : AccessTools.Field(empire.GetType(), "Research")?.GetValue(empire);
            var projects = research is null ? null : AccessTools.Field(research.GetType(), "Projects")?.GetValue(research);

            var projectById = ById(projects, typeof(short)) ?? ById(projects, typeof(int));
            var project = Lookup(projectById, projects, projectId);

            if (project is null || !ResolveResearch(project)) { absent++; continue; }

            WriteResearch(project, state);
            applied++;
        }

        return applied;
    }

    // ---------------------------------------------------------------- plumbing

    private static object ListField(object owner, string name)
    {
        try { return owner is null ? null : AccessTools.Field(owner.GetType(), name)?.GetValue(owner); }
        catch { return null; }
    }

    private static int CountOf(object list)
    {
        try { return list is null ? 0 : list.GetType().GetProperty("Count")?.GetValue(list) is int c ? c : 0; }
        catch { return 0; }
    }

    private static MethodInfo Indexer(object list)
    {
        try { return list?.GetType().GetMethod("get_Item", new[] { typeof(int) }); }
        catch { return null; }
    }

    private static MethodInfo ById(object list, Type keyType)
    {
        try { return list?.GetType().GetMethod("GetById", new[] { keyType }); }
        catch { return null; }
    }

    /// <summary>
    /// Look an id up through whichever GetById overload the list actually has. DW2 keys some
    /// collections by Int16 and others by Int32, so the argument is converted to the
    /// parameter type rather than assumed.
    /// </summary>
    private static object Lookup(MethodInfo getById, object list, long id)
    {
        if (getById is null || list is null) return null;

        try
        {
            var parameterType = getById.GetParameters()[0].ParameterType;
            object arg = parameterType == typeof(short) ? (short)id : (int)id;
            return getById.Invoke(list, new[] { arg });
        }
        catch { return null; }
    }

    private static object At(MethodInfo indexer, object list, int i)
    {
        try { return indexer.Invoke(list, new object[] { i }); }
        catch { return null; }
    }

    // --- ships

    private static FieldInfo _shipId, _shipPosition, _shipDamage, _shipDestroyed;
    private static FieldInfo _vx, _vy, _vz;
    private static Type _vectorType;

    private static bool ResolveShip(object ship)
    {
        if (_shipId is not null) return true;

        var type = ship.GetType();
        _shipId = AccessTools.Field(type, "ShipId");
        _shipPosition = AccessTools.Field(type, "_Position");
        _shipDamage = AccessTools.Field(type, "HullDamageLevel");
        _shipDestroyed = AccessTools.Field(type, "IsDestroyedCached");

        if (_shipId is null || _shipPosition is null) { _shipId = null; return false; }

        _vectorType = _shipPosition.FieldType;
        _vx = _vectorType.GetField("X");
        _vy = _vectorType.GetField("Y");
        _vz = _vectorType.GetField("Z");

        if (_vx is null || _vy is null || _vz is null) { _shipId = null; return false; }
        return true;
    }

    private static bool TryReadShip(object ship, out int id, out ShipState state)
    {
        id = 0;
        state = default;

        try
        {
            id = (int)_shipId.GetValue(ship);
            var position = _shipPosition.GetValue(ship);

            state = new ShipState(
                (float)_vx.GetValue(position),
                (float)_vy.GetValue(position),
                (float)_vz.GetValue(position),
                _shipDamage?.GetValue(ship) is float d ? d : 0f,
                _shipDestroyed?.GetValue(ship) is true);

            return true;
        }
        catch { return false; }
    }

    private static void WriteShip(object ship, ShipState state)
    {
        // Vector3 is a struct: build a box, set its fields, write the whole thing back.
        // Setting X on whatever GetValue returned would modify a copy and nothing else.
        var box = Activator.CreateInstance(_vectorType);
        _vx.SetValue(box, state.X);
        _vy.SetValue(box, state.Y);
        _vz.SetValue(box, state.Z);
        _shipPosition.SetValue(ship, box);

        _shipDamage?.SetValue(ship, state.Damage);
        _shipDestroyed?.SetValue(ship, state.Destroyed);
    }

    private static bool ShipDiffers(ShipState a, ShipState b)
    {
        if (a.Destroyed != b.Destroyed) return true;
        if (Math.Abs(a.Damage - b.Damage) > MinDamageChange) return true;

        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz > MinMoveSquared;
    }

    // --- colonies

    private static FieldInfo _colonyId, _corruption, _approval, _quality, _maxPopulation;
    private static bool _colonyResolved;

    /// <summary>
    /// Four scalars a player watches move. Population itself is deliberately absent:
    /// Colony.Population is a per-race PopulationList, not a number, so carrying it means
    /// carrying a collection whose members can appear and disappear -- which is structure,
    /// and structure is what full states are for.
    /// </summary>
    private static bool ResolveColony(object colony)
    {
        if (_colonyResolved) return _colonyId is not null;

        var type = colony.GetType();
        _colonyId = AccessTools.Field(type, "ColonyId");
        _corruption = AccessTools.Field(type, "Corruption");
        _approval = AccessTools.Field(type, "EmpireApprovalRating");
        _quality = AccessTools.Field(type, "QualityBoost");
        _maxPopulation = AccessTools.Field(type, "MaximumPopulation");

        _colonyResolved = true;
        return _colonyId is not null;
    }

    private static bool TryReadColony(object colony, out short id, out ColonyState state)
    {
        id = 0;
        state = default;

        try
        {
            id = (short)_colonyId.GetValue(colony);
            state = new ColonyState(
                _corruption?.GetValue(colony) is float c ? c : 0f,
                _approval?.GetValue(colony) is float a ? a : 0f,
                _quality?.GetValue(colony) is float q ? q : 0f,
                _maxPopulation?.GetValue(colony) is long m ? m : 0L);
            return true;
        }
        catch { return false; }
    }

    private static void WriteColony(object colony, ColonyState state)
    {
        _corruption?.SetValue(colony, state.Corruption);
        _approval?.SetValue(colony, state.Approval);
        _quality?.SetValue(colony, state.Quality);
        _maxPopulation?.SetValue(colony, state.MaxPopulation);
    }

    private static bool ColonyDiffers(ColonyState a, ColonyState b) =>
        a.MaxPopulation != b.MaxPopulation
        || Math.Abs(a.Corruption - b.Corruption) > MinColonyChange
        || Math.Abs(a.Approval - b.Approval) > MinColonyChange
        || Math.Abs(a.Quality - b.Quality) > MinColonyChange;

    // --- research

    private static FieldInfo _projectId, _progress, _researched;
    private static bool _researchResolved;

    private static bool ResolveResearch(object project)
    {
        if (_researchResolved) return _projectId is not null;

        var type = project.GetType();
        _projectId = AccessTools.Field(type, "ResearchProjectId");
        _progress = AccessTools.Field(type, "Progress");
        _researched = AccessTools.Field(type, "Researched");

        _researchResolved = true;
        return _projectId is not null;
    }

    private static bool TryReadResearch(object project, out short id, out ResearchState state)
    {
        id = 0;
        state = default;

        try
        {
            id = (short)_projectId.GetValue(project);
            state = new ResearchState(
                _progress?.GetValue(project) is float p ? p : 0f,
                _researched?.GetValue(project) is true);
            return true;
        }
        catch { return false; }
    }

    private static void WriteResearch(object project, ResearchState state)
    {
        _progress?.SetValue(project, state.Progress);
        _researched?.SetValue(project, state.Researched);
    }

    private static bool ResearchDiffers(ResearchState a, ResearchState b) =>
        a.Researched != b.Researched || Math.Abs(a.Progress - b.Progress) > MinResearchChange;

    /// <summary>
    /// Sizes of the collections deltas carry, for logging once at startup.
    ///
    /// Worth having because "0 fleets were sent" has two very different meanings -- the
    /// section is broken, or this galaxy has no fleets yet -- and the delta log alone cannot
    /// tell them apart.
    /// </summary>
    public static string Census(object galaxy)
    {
        if (galaxy is null) return "no galaxy";

        var parts = new List<string>();
        foreach (var name in new[] { "Ships", "Colonies", "Characters", "Empires" })
            parts.Add($"{name}={CountOf(ListField(galaxy, name))}");

        // Fleets counted where they actually live -- summed over Empire.Fleets, not read off
        // the permanently-empty Galaxy.Fleets that made this line report 0 for three runs.
        parts.Add($"Fleets={CountFleets(galaxy)}");

        return string.Join(" ", parts);
    }

    // Cumulative applied counts, per kind. Sampled logging cannot answer "has any research
    // ever arrived": the client logs every 50th delta and research runs every 5th, and
    // 50k+1 mod 5 is always 1 -- so every sampled delta is guaranteed to be a NON-research
    // one, and the section looked dead while working perfectly. Two cadences sharing a
    // factor is an easy way to build a blind spot into a sampler. Totals have no phase.
    private static readonly Dictionary<string, long> _appliedTotals = new();

    private static void CountApplied(string kind, int n)
    {
        if (n <= 0) return;
        lock (_appliedTotals)
            _appliedTotals[kind] = _appliedTotals.TryGetValue(kind, out var v) ? v + n : n;
    }

    /// <summary>Everything this client has applied, by kind, since it started.</summary>
    public static string AppliedTotals()
    {
        lock (_appliedTotals)
        {
            if (_appliedTotals.Count == 0) return "nothing applied yet";
            return string.Join(" ", _appliedTotals.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:N0}"));
        }
    }

    // What each whole-object section actually saw, last build. Added because a fleet-led
    // 21.4 ms spike appeared in a galaxy whose census reported Fleets=0 and whose client
    // applied no fleets at all -- three facts that cannot all be true of a section doing
    // nothing, and no amount of re-reading the timings distinguishes them.
    private static readonly Dictionary<string, (int Count, int Upserts, int Removed)> _lastWholeCounts = new();

    public static string LastWholeCounts()
    {
        lock (_baseline)
        {
            if (_lastWholeCounts.Count == 0) return "none";
            return string.Join(" ", _lastWholeCounts.Select(kv =>
                $"{kv.Key}[n={kv.Value.Count} +{kv.Value.Upserts} -{kv.Value.Removed}]"));
        }
    }

    // ------------------------------------------------- fleets (per empire, whole object)
    //
    // Fleets live on Empire.Fleets, one list per empire, so they need a two-level walk that
    // the galaxy-level whole-object path does not do. They are otherwise identical: carried
    // whole, membership only, created through DW2's own `new Fleet(galaxy)` +
    // Fleet.ReadFromStream.
    //
    // The KEY packs both ids. Fleet.FleetId is an Int16 scoped to its empire's list, so two
    // empires can each own a fleet #1; a baseline keyed on FleetId alone would treat one as a
    // change to the other and send neither correctly. Packing empire and fleet into one int
    // keeps a single wire shape and makes the collision impossible rather than unlikely.

    private static int PackFleetKey(short empireId, short fleetId) =>
        ((ushort)empireId << 16) | (ushort)fleetId;

    private static (short Empire, short Fleet) UnpackFleetKey(int key) =>
        ((short)(ushort)(key >> 16), (short)(ushort)(key & 0xFFFF));

    private static int _lastFleetCount;

    private static int WriteFleetSection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        var empires = ListField(galaxy, "Empires");
        var empireItem = Indexer(empires);
        int empireCount = CountOf(empires);

        var present = new HashSet<long>();
        var upserts = new List<(int Key, byte[] Bytes)>();
        int seen = 0;

        for (int e = 0; e < empireCount && empireItem is not null; e++)
        {
            var empire = At(empireItem, empires, e);
            if (empire is null) continue;

            var empireIdGetter = FastAccess.Getter<short>(empire.GetType(), "EmpireId");
            if (empireIdGetter is null) continue;
            short empireId = empireIdGetter(empire);

            var fleets = AccessTools.Field(empire.GetType(), "Fleets")?.GetValue(empire);
            int fleetCount = CountOf(fleets);
            if (fleets is null || fleetCount == 0) continue;

            var fleetItem = FastAccess.Indexer(fleets.GetType());
            if (fleetItem is null) continue;

            for (int f = 0; f < fleetCount; f++)
            {
                var fleet = fleetItem(fleets, f);
                if (fleet is null) continue;

                var idGetter = FastAccess.Getter<short>(fleet.GetType(), "FleetId");
                if (idGetter is null) break;

                seen++;
                int key = PackFleetKey(empireId, idGetter(fleet));
                present.Add(key);

                var mapKey = ("Fleets", (long)key);
                bool isNew = !_lastWhole.ContainsKey(mapKey);
                if (!isNew) continue;                       // membership only, as for characters

                var bytes = SerialiseObject(fleet, galaxy.GetType());
                if (bytes is null) continue;

                _lastWhole[mapKey] = HashBytes(bytes);
                if (!primeOnly) upserts.Add((key, bytes));
            }
        }

        var removed = _lastWhole.Keys
            .Where(k => k.Kind == "Fleets" && !present.Contains(k.Id))
            .Select(k => (int)k.Id)
            .ToList();

        foreach (var key in removed) _lastWhole.Remove(("Fleets", key));

        writer.Write(removed.Count);
        foreach (var key in removed) writer.Write(key);

        writer.Write(upserts.Count);
        foreach (var (key, bytes) in upserts)
        {
            writer.Write(key);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        _lastFleetCount = seen;
        _lastWholeCounts["fleets"] = (seen, upserts.Count, removed.Count);
        return removed.Count + upserts.Count;
    }

    private static int ApplyFleetSection(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        var empires = ListField(galaxy, "Empires");
        var empireById = ById(empires, typeof(short)) ?? ById(empires, typeof(int));
        var fleetType = AccessTools.TypeByName("DistantWorlds.Types.Fleet");
        int applied = 0;

        int removedCount = reader.ReadInt32();
        for (int i = 0; i < removedCount; i++)
        {
            var (empireId, fleetId) = UnpackFleetKey(reader.ReadInt32());

            var fleets = FleetsOf(empires, empireById, empireId);
            var fleet = fleets is null ? null : Lookup(ById(fleets, typeof(short)) ?? ById(fleets, typeof(int)), fleets, fleetId);
            if (fleet is null) { absent++; continue; }

            if (RemoveFromList(fleets, fleet)) applied++; else absent++;
        }

        int upsertCount = reader.ReadInt32();
        for (int i = 0; i < upsertCount; i++)
        {
            var (empireId, _) = UnpackFleetKey(reader.ReadInt32());
            int length = reader.ReadInt32();
            var bytes = length > 0 ? reader.ReadBytes(length) : null;
            if (bytes is null || bytes.Length != length) { absent++; continue; }

            var fleets = FleetsOf(empires, empireById, empireId);
            if (fleets is null) { absent++; continue; }

            if (AddFromBytes(galaxy, fleets, fleetType, bytes)) applied++; else absent++;
        }

        return applied;
    }

    private static object FleetsOf(object empires, MethodInfo empireById, short empireId)
    {
        var empire = Lookup(empireById, empires, empireId);
        return empire is null ? null : AccessTools.Field(empire.GetType(), "Fleets")?.GetValue(empire);
    }

    private static int CountFleets(object galaxy)
    {
        var empires = ListField(galaxy, "Empires");
        var item = Indexer(empires);
        int total = 0;

        for (int e = 0; e < CountOf(empires) && item is not null; e++)
        {
            var empire = At(item, empires, e);
            if (empire is null) continue;
            total += CountOf(AccessTools.Field(empire.GetType(), "Fleets")?.GetValue(empire));
        }

        return total;
    }

    // ------------------------------------------------------------ self-test

    /// <summary>
    /// Deterministic test of the fleet key, run at every launch and logged.
    ///
    /// WHY THIS IS THE COLLISION TEST. Fleet.FleetId is an Int16 scoped to its empire's own
    /// list, so two empires can each own a fleet #1. The client cannot collide on that: it
    /// looks a fleet up through FleetsOf(empireId) first, so same-numbered fleets land in
    /// different lists by construction. The ONLY place a collision can happen is the host's
    /// baseline, keyed by PackFleetKey -- and that is pure logic with no DW2 in it, so it is
    /// tested here rather than by hoping the AI builds two same-numbered fleets in one run.
    /// (It built one fleet in eight minutes across eighteen empires.)
    ///
    /// Each case names what it would have caught. A test that cannot say what it catches is
    /// decoration.
    /// </summary>
    public static string SelfTest()
    {
        var failures = new List<string>();

        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }

        // 1. Same fleet id under different empires must produce different keys. This is the
        //    collision itself: a key on FleetId alone would make these equal, and the host
        //    would treat empire 2's fleet #1 as an update to empire 1's -- sending neither
        //    correctly and, on removal, deleting the wrong one.
        Expect(PackFleetKey(1, 1) != PackFleetKey(2, 1), "empire 1 fleet 1 collides with empire 2 fleet 1");
        Expect(PackFleetKey(1, 1) != PackFleetKey(1, 2), "empire 1 fleet 1 collides with empire 1 fleet 2");

        // 2. Round trip across the whole Int16 range, including the sign bit. Packing uses
        //    (ushort) casts; unpacking must undo them. A wrong cast here would round-trip 0..32767
        //    perfectly and corrupt every id above that or below zero, which no small test sees.
        foreach (short e in new short[] { 0, 1, 2, 255, 256, short.MaxValue, -1, -2, short.MinValue })
        foreach (short f in new short[] { 0, 1, 2, 255, 256, short.MaxValue, -1, -2, short.MinValue })
        {
            var (e2, f2) = UnpackFleetKey(PackFleetKey(e, f));
            Expect(e2 == e && f2 == f, $"round trip ({e},{f}) -> ({e2},{f2})");
        }

        // 3. The baseline itself keeps both. This is the dictionary the host actually uses,
        //    exercised the way WriteFleetSection uses it, so a key type mismatch between the
        //    writer's `(long)key` and the remover's `(int)k.Id` would show up here too.
        lock (_baseline)
        {
            var saved = _lastWhole.Where(kv => kv.Key.Kind == "SelfTest").Select(kv => kv.Key).ToList();
            foreach (var k in saved) _lastWhole.Remove(k);

            _lastWhole[("SelfTest", (long)PackFleetKey(1, 1))] = 0xA;
            _lastWhole[("SelfTest", (long)PackFleetKey(2, 1))] = 0xB;
            Expect(_lastWhole.Count(kv => kv.Key.Kind == "SelfTest") == 2, "baseline merged two empires' fleet #1 into one entry");

            // Remove exactly one, the way the section does: by unpacked-then-repacked key.
            var (re, rf) = UnpackFleetKey(PackFleetKey(1, 1));
            _lastWhole.Remove(("SelfTest", (long)PackFleetKey(re, rf)));
            Expect(_lastWhole.ContainsKey(("SelfTest", (long)PackFleetKey(2, 1))), "removing empire 1's fleet #1 also removed empire 2's");
            Expect(!_lastWhole.ContainsKey(("SelfTest", (long)PackFleetKey(1, 1))), "removal of empire 1's fleet #1 did not take");

            _lastWhole.Remove(("SelfTest", (long)PackFleetKey(2, 1)));
        }

        return failures.Count == 0
            ? "fleet key self-test PASS (collision, sign-bit round trip, baseline isolation)"
            : "fleet key self-test FAIL: " + string.Join("; ", failures);
    }

    private static readonly HashSet<string> _nullMapsSeen = new();

    /// <summary>
    /// When RegenerateSummary fails on a freshly created ship, say WHICH of its inputs is
    /// null. Logged once per distinct pattern.
    ///
    /// Each input is read under its OWN try/catch and a failure becomes a value in the map.
    /// The first version read them in one block and one AmbiguousMatchException -- an
    /// overloaded member somewhere in the list -- aborted the whole probe, 100 times, and
    /// answered nothing. An instrument that can fail as a unit is one that can fail on the
    /// exact input it was built to see.
    ///
    /// Three hypotheses about this failure had already been wrong in a day (needs the list
    /// first; CheckAdd is a no-op; a resolve pass is skipped). ShipSummary.DetermineShipSummary
    /// takes galaxy, empire, hull, research, bonuses, artifacts, components and battle data;
    /// this reads each by the route RegenerateSummary does.
    /// </summary>
    private static void ProbeSummaryInputs(object galaxy, object ship)
    {
        var t = ship.GetType();
        var parts = new List<string>();

        string Try(string label, Func<string> read)
        {
            try { return label + "=" + read(); }
            catch (Exception ex) { return label + "=<" + (ex.InnerException ?? ex).GetType().Name + ">"; }
        }

        string NullOrOk(object o) => o is null ? "NULL" : "ok";

        // Fields via GetField with explicit flags and DeclaredOnly-first search, because
        // AccessTools.Field walks the hierarchy and a name declared on both Ship and its base
        // is exactly the kind of thing that turns into an ambiguity.
        FieldInfo FindField(string name)
        {
            for (var cur = t; cur is not null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f is not null) return f;
            }
            return null;
        }

        object shipGalaxy = null, empireItem = null, empire = null;

        parts.Add(Try("_Galaxy", () =>
        {
            var f = FindField("_Galaxy");
            if (f is null) return "<no field>";
            shipGalaxy = f.GetValue(ship);
            return shipGalaxy is null ? "NULL" : ReferenceEquals(shipGalaxy, galaxy) ? "ok" : "OTHER-GALAXY";
        }));

        parts.Add(Try("Empire(item)", () =>
        {
            var f = FindField("Empire");
            if (f is null) return "<no field>";
            empireItem = f.GetValue(ship);
            return NullOrOk(empireItem);
        }));

        parts.Add(Try("Empire.Resolve", () =>
        {
            if (empireItem is null) return "skip";
            var m = empireItem.GetType().GetMethod("Resolve", new[] { galaxy.GetType() });
            if (m is null) return "<no Resolve(Galaxy)>";
            empire = m.Invoke(empireItem, new[] { galaxy });
            var idF = empireItem.GetType().GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? empireItem.GetType().GetField("_Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return NullOrOk(empire) + "(id=" + (idF?.GetValue(empireItem) ?? "?") + ")";
        }));

        parts.Add(Try("Empire.Research", () =>
            empire is null ? "skip" : NullOrOk(empire.GetType().GetField("Research", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(empire))));

        parts.Add(Try("GetShipHull", () =>
        {
            var m = t.GetMethod("GetShipHull", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m is null) return "<no GetShipHull()>";
            var hullId = FindField("HullId")?.GetValue(ship);
            return NullOrOk(m.Invoke(ship, null)) + "(HullId=" + (hullId ?? "?") + ")";
        }));

        foreach (var name in new[] { "BattleData", "Components", "BonusValuesComplete", "_Design" })
            parts.Add(Try(name, () => { var f = FindField(name); return f is null ? "<no field>" : NullOrOk(f.GetValue(ship)); }));

        var map = string.Join(" ", parts);
        bool isNew;
        lock (_nullMapsSeen) isNew = _nullMapsSeen.Add(map);
        if (isNew) Log?.Invoke("# delta: SUMMARY INPUTS on a failing ship -> " + map);
    }
}
