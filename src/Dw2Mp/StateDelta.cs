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
/// WHAT IT CARRIES, in three sections:
///
///   ships     position, hull damage, destroyed-ness      -- what moves on screen
///   colonies  corruption, approval, quality, max pop     -- what changes on the map
///   research  per-project progress and researched flag   -- what changes in the UI
///
/// Only records that actually changed are sent, each against a per-kind threshold, so a
/// quiet galaxy costs nothing.
///
/// WHAT IT DOES NOT CARRY. Anything structural -- objects appearing or disappearing.
/// Constructing a DW2 Ship, Colony or ResearchProject from the outside through reflection
/// would mean reproducing a constructor we cannot read, and getting it subtly wrong would
/// corrupt the client's galaxy silently.
///
/// So a delta updates records BOTH SIDES ALREADY HAVE, and silently skips ids the client
/// has not seen. Those arrive with the next full state. Treating an unknown id as a failure
/// was tried first and was a real bug: once the client stopped simulating it could no longer
/// create anything, so the moment the host built a ship the counts diverged permanently and
/// every delta was rejected -- deltas moved nothing for an entire run.
///
/// Deltas carry change; full states carry structure. Structure changes far less often, and
/// that gap is the whole saving.
/// </summary>
public static class StateDelta
{
    private const int Version = 3;

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
        Build(galaxy, out _, out _);
    }
    // ------------------------------------------------------------------ host

    /// <summary>
    /// Build a delta of everything that changed since the last one. Returns null when
    /// nothing did, so a still galaxy costs no traffic at all.
    /// </summary>
    public static byte[] Build(object galaxy, out int changed, out int total)
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
                changed += WriteShipSection(galaxy, writer, out total);
                changed += WriteColonySection(galaxy, writer);
                changed += WriteResearchSection(galaxy, writer);
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

    private static int WriteShipSection(object galaxy, BinaryWriter writer, out int total)
    {
        var ships = ListField(galaxy, "Ships");
        var item = Indexer(ships);
        total = CountOf(ships);

        // The host's total goes on the wire even when nothing moved: it is how the client
        // reports "I have 46 of your 57" without a separate message.
        writer.Write(total);

        var records = new List<(int Id, ShipState State)>();

        for (int i = 0; i < total && item is not null; i++)
        {
            var ship = At(item, ships, i);
            if (ship is null || !ResolveShip(ship)) continue;
            if (!TryReadShip(ship, out var id, out var now)) continue;

            if (_lastShips.TryGetValue(id, out var before) && !ShipDiffers(before, now)) continue;

            _lastShips[id] = now;
            records.Add((id, now));
        }

        writer.Write(records.Count);
        foreach (var (id, s) in records)
        {
            writer.Write(id);
            writer.Write(s.X); writer.Write(s.Y); writer.Write(s.Z);
            writer.Write(s.Damage);
            writer.Write(s.Destroyed);
        }

        return records.Count;
    }

    private static int WriteColonySection(object galaxy, BinaryWriter writer)
    {
        var colonies = ListField(galaxy, "Colonies");
        var item = Indexer(colonies);
        int count = CountOf(colonies);

        var records = new List<(short Id, ColonyState State)>();

        for (int i = 0; i < count && item is not null; i++)
        {
            var colony = At(item, colonies, i);
            if (colony is null || !ResolveColony(colony)) continue;
            if (!TryReadColony(colony, out var id, out var now)) continue;

            if (_lastColonies.TryGetValue(id, out var before) && !ColonyDiffers(before, now)) continue;

            _lastColonies[id] = now;
            records.Add((id, now));
        }

        writer.Write(records.Count);
        foreach (var (id, c) in records)
        {
            writer.Write(id);
            writer.Write(c.Corruption); writer.Write(c.Approval); writer.Write(c.Quality);
            writer.Write(c.MaxPopulation);
        }

        return records.Count;
    }

    private static int WriteResearchSection(object galaxy, BinaryWriter writer)
    {
        var empires = ListField(galaxy, "Empires");
        var empireItem = Indexer(empires);
        int empireCount = CountOf(empires);

        var records = new List<(short Empire, short Project, ResearchState State)>();

        for (int e = 0; e < empireCount && empireItem is not null; e++)
        {
            var empire = At(empireItem, empires, e);
            if (empire is null) continue;

            // Int16, not Int32. Reading it as int made the pattern fail for EVERY empire and
            // the research section came out empty in every delta, silently -- the same class
            // of mistake as reading Empire.Name as a field when it is a property.
            if (AccessTools.Field(empire.GetType(), "EmpireId")?.GetValue(empire) is not short empireId) continue;

            var research = AccessTools.Field(empire.GetType(), "Research")?.GetValue(empire);
            if (research is null) continue;

            var projects = AccessTools.Field(research.GetType(), "Projects")?.GetValue(research);
            var projectItem = Indexer(projects);
            int projectCount = CountOf(projects);

            for (int p = 0; p < projectCount && projectItem is not null; p++)
            {
                var project = At(projectItem, projects, p);
                if (project is null || !ResolveResearch(project)) continue;
                if (!TryReadResearch(project, out var projectId, out var now)) continue;

                var key = (empireId, projectId);
                if (_lastResearch.TryGetValue(key, out var before) && !ResearchDiffers(before, now)) continue;

                _lastResearch[key] = now;
                records.Add((empireId, projectId, now));
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

            int ships = ApplyShips(galaxy, reader, out int absentShips, out int hostShips, out int localShips);
            int colonies = ApplyColonies(galaxy, reader, out int absentColonies);
            int research = ApplyResearch(galaxy, reader, out int absentResearch);

            var parts = new List<string>();
            if (ships + absentShips > 0) parts.Add($"{ships} ship(s)" + (absentShips > 0 ? $" +{absentShips} absent" : ""));
            if (colonies + absentColonies > 0) parts.Add($"{colonies} colony" + (absentColonies > 0 ? $" +{absentColonies} absent" : ""));
            if (research + absentResearch > 0) parts.Add($"{research} project(s)" + (absentResearch > 0 ? $" +{absentResearch} absent" : ""));

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

    private static int ApplyShips(object galaxy, BinaryReader reader, out int absent, out int hostTotal, out int localTotal)
    {
        absent = 0;

        var ships = ListField(galaxy, "Ships");
        var getById = ById(ships, typeof(int));

        hostTotal = reader.ReadInt32();
        localTotal = CountOf(ships);
        int count = reader.ReadInt32();
        int applied = 0;

        for (int i = 0; i < count; i++)
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

    private static int ApplyColonies(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        var colonies = ListField(galaxy, "Colonies");
        var getById = ById(colonies, typeof(short)) ?? ById(colonies, typeof(int));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int i = 0; i < count; i++)
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
}
