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
/// STRUCTURE, TOO -- ships and colonies are created and removed client-side, using DW2's
/// OWN per-object serialisation. Ship and Colony both expose WriteToStream(BinaryWriter)
/// and ReadFromStream(Galaxy, BinaryReader), and their list deserialisers do exactly
///     new T(galaxy); t.ReadFromStream(galaxy, reader); list.Add(t);
/// when loading a save. So no constructor is reproduced and no field list is guessed.
///
/// This was once documented here as out of scope, on the grounds that building a Ship by
/// hand would be too risky. That was true and beside the point: the game already knows how,
/// and looking was worth more than accepting the limit. Before it, the client's ship count
/// drifted from the host's indefinitely -- 38 of 46, then 72 of 78 -- and every update for a
/// ship it had never heard of was skipped forever. Now the gap sits at 1, which is a ship
/// built between the host's snapshot and the client's apply.
///
/// STILL NOT CARRIED: research projects are created and removed with an empire, not during
/// play, so they are update-only; and anything reached through a nested collection
/// (Colony.Population is per-race, fleets, characters, diplomacy) is left to full states.
///
/// Deltas carry change AND the structure they can safely reconstruct; full states carry
/// the rest, and remain the repair path when a delta does not fit.
/// </summary>
public static class StateDelta
{
    private const int Version = 5;

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
        Build(galaxy, out _, out _, primeOnly: true);
    }
    // ------------------------------------------------------------------ host

    /// <summary>
    /// Build a delta of everything that changed since the last one. Returns null when
    /// nothing did, so a still galaxy costs no traffic at all.
    /// </summary>
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
                changed += WriteShipSection(galaxy, writer, out total, primeOnly);
                changed += WriteColonySection(galaxy, writer, primeOnly);
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

            var made = new Serialiser(
                type.GetConstructor(new[] { galaxyType }),
                AccessTools.Method(type, "ReadFromStream", new[] { galaxyType, typeof(BinaryReader) }),
                AccessTools.Method(type, "WriteToStream", new[] { typeof(BinaryWriter) }),
                AccessTools.Method(type, "RegenerateSummary", Type.EmptyTypes));

            _serialisers[type] = made;
            return made;
        }
    }

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

    /// <summary>
    /// Rebuild an object from the host's bytes and put it in the list.
    ///
    /// RegenerateSummary afterwards where the type has one: ShipSummary is derived and NOT
    /// serialised, established when adopting a full state left 64 of 64 ships with a null
    /// Summary. A per-object deserialise is the same code path, so a newly created ship
    /// would arrive with the same hole and crash the first thing that read it.
    /// </summary>
    private static bool AddFromBytes(object galaxy, object list, Type elementType, byte[] bytes)
    {
        try
        {
            if (list is null || elementType is null) return false;

            var s = SerialiserFor(elementType, galaxy.GetType());
            if (s.Ctor is null || s.Read is null) return false;

            var blank = s.Ctor.Invoke(new[] { galaxy });

            using var input = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            var item = s.Read.Invoke(blank, new[] { galaxy, (object)reader });
            if (item is null) return false;

            s.Regenerate?.Invoke(item, null);

            var add = AccessTools.Method(list.GetType(), "CheckAdd")
                   ?? AccessTools.Method(list.GetType(), "Add");
            if (add is null) return false;

            add.Invoke(list, new[] { item });
            return true;
        }
        catch { return false; }
    }

    private static bool RemoveFromList(object list, object element)
    {
        try
        {
            var remove = AccessTools.Method(list.GetType(), "CheckRemove")
                      ?? AccessTools.Method(list.GetType(), "FastRemove", new[] { element.GetType() });
            if (remove is null) return false;

            remove.Invoke(list, new[] { element });
            return true;
        }
        catch { return false; }
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
}
