using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Ship deltas: the continuously-changing part of a galaxy, as a few hundred bytes instead
/// of 24 MB.
///
/// WHY THIS EXISTS. A full state costs ~25-36 MB of unreleasable native memory to adopt,
/// because StartGameExisting acquires resources for the incoming galaxy without releasing
/// the outgoing one's. That cost is per ADOPTION, not per byte -- so compressing the state,
/// or diffing it at the byte level and rebuilding it client-side, would save bandwidth and
/// change the memory problem not at all. The only way out is to stop adopting: update the
/// galaxy the client already has, IN PLACE.
///
/// WHAT IT CARRIES. Position, hull damage and destroyed-ness, per ship, and only for ships
/// that actually changed. That is what moves on screen.
///
/// WHAT IT DOES NOT CARRY. Ships appearing or disappearing. Constructing a DW2 Ship from
/// the outside through reflection would mean reproducing a constructor we cannot read, and
/// getting it subtly wrong would corrupt the client's galaxy silently.
///
/// So a delta moves ships BOTH SIDES ALREADY KNOW ABOUT, and silently skips ids the client
/// has not seen. Those arrive with the next full state. Treating an unknown id as a failure
/// was tried first and was a real bug: once the client stopped simulating it could no longer
/// create ships at all, so the moment the host built one the counts diverged permanently and
/// every delta was rejected -- deltas moved nothing for an entire run.
///
/// Deltas carry motion; full states carry structure. Structure changes far less often than
/// position does, and that gap is the whole saving.
/// </summary>
public static class StateDelta
{
    private const int Version = 1;

    /// <summary>
    /// Squared distance a ship must move before it is worth sending. Positions are floats
    /// updated every tick, so with no threshold every ship is always "changed" and the delta
    /// degenerates into a full ship list.
    /// </summary>
    private const float MinMoveSquared = 0.01f;

    private const float MinDamageChange = 0.001f;

    private readonly record struct ShipState(float X, float Y, float Z, float Damage, bool Destroyed);

    // Host: what the client was last told, so we send only what changed since.
    private static readonly Dictionary<int, ShipState> _lastSent = new();

    private static FieldInfo _positionField, _damageField, _destroyedField, _idField;
    private static FieldInfo _vx, _vy, _vz;
    private static Type _vectorType;

    /// <summary>Forget what the client knows. Called after a full state, which resets the baseline.</summary>
    public static void ResetBaseline()
    {
        lock (_lastSent) _lastSent.Clear();
    }

    // ------------------------------------------------------------------ host

    /// <summary>
    /// Build a delta of everything that moved since the last one. Returns null when nothing
    /// changed, so a still galaxy costs no traffic at all.
    /// </summary>
    public static byte[] Build(object galaxy, out int changed, out int total)
    {
        changed = 0;
        total = 0;

        var ships = ShipsOf(galaxy);
        if (ships is null) return null;

        var item = ships.GetType().GetMethod("get_Item", new[] { typeof(int) });
        total = CountOf(ships);
        if (item is null || total <= 0) return null;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(Version);
        writer.Write(total);

        var countPosition = buffer.Position;
        writer.Write(0);              // placeholder, rewritten once the count is known

        lock (_lastSent)
        {
            for (int i = 0; i < total; i++)
            {
                object ship;
                try { ship = item.Invoke(ships, new object[] { i }); }
                catch { continue; }
                if (ship is null) continue;

                if (!Resolve(ship)) return null;
                if (!TryRead(ship, out var id, out var now)) continue;

                if (_lastSent.TryGetValue(id, out var before) && !Differs(before, now)) continue;

                _lastSent[id] = now;

                writer.Write(id);
                writer.Write(now.X);
                writer.Write(now.Y);
                writer.Write(now.Z);
                writer.Write(now.Damage);
                writer.Write(now.Destroyed);
                changed++;
            }
        }

        if (changed == 0) return null;

        writer.Flush();
        var bytes = buffer.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan((int)countPosition, 4), changed);
        return bytes;
    }

    // ---------------------------------------------------------------- client

    /// <summary>
    /// Apply a delta to the galaxy in place.
    ///
    /// Ships the client does not have are skipped and counted, not treated as an error --
    /// see the class comment. Returning false is reserved for a delta that cannot be read
    /// at all, which is a protocol problem rather than a state one.
    /// </summary>
    public static bool Apply(object galaxy, byte[] payload, out string info)
    {
        info = "";

        var ships = ShipsOf(galaxy);
        if (ships is null) { info = "no ship list"; return false; }

        var getById = ships.GetType().GetMethod("GetById", new[] { typeof(int) });
        if (getById is null) { info = "ShipList has no GetById"; return false; }

        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);
            if (reader.ReadInt32() != Version) { info = "delta version mismatch"; return false; }

            int hostTotal = reader.ReadInt32();
            int count = reader.ReadInt32();
            int localTotal = CountOf(ships);

            // A ship count that disagrees is NOT a failure, and treating it as one was a
            // real bug: once the client stopped simulating it could no longer create ships
            // at all, so the moment the host built one the counts diverged permanently and
            // every delta was rejected. Deltas moved nothing for a whole run.
            //
            // A delta moves ships both sides know about. Ships the client has never heard of
            // arrive with the next full state, and until then it is simply a little behind
            // on what exists -- while everything it CAN see keeps moving.
            int applied = 0, unknown = 0;

            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32();
                var state = new ShipState(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                                          reader.ReadSingle(), reader.ReadBoolean());

                var ship = getById.Invoke(ships, new object[] { id });
                if (ship is null) { unknown++; continue; }

                if (!Resolve(ship)) { info = "ship fields not resolvable"; return false; }

                Write(ship, state);
                applied++;
            }

            info = $"{applied} ship(s)" +
                   (unknown > 0 ? $", {unknown} not here yet" : "") +
                   (hostTotal != localTotal ? $" [have {localTotal} of host's {hostTotal}]" : "") +
                   $", {payload.Length:N0}B";
            return true;
        }
        catch (Exception ex)
        {
            info = (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static object ShipsOf(object galaxy)
    {
        try { return galaxy is null ? null : AccessTools.Field(galaxy.GetType(), "Ships")?.GetValue(galaxy); }
        catch { return null; }
    }

    private static int CountOf(object list)
    {
        try { return list.GetType().GetProperty("Count")?.GetValue(list) is int c ? c : 0; }
        catch { return 0; }
    }

    /// <summary>Resolve the field handles once. Ship layout does not change at runtime.</summary>
    private static bool Resolve(object ship)
    {
        if (_idField is not null) return true;

        var type = ship.GetType();
        _idField = AccessTools.Field(type, "ShipId");
        _positionField = AccessTools.Field(type, "_Position");
        _damageField = AccessTools.Field(type, "HullDamageLevel");
        _destroyedField = AccessTools.Field(type, "IsDestroyedCached");

        if (_idField is null || _positionField is null) { _idField = null; return false; }

        _vectorType = _positionField.FieldType;
        _vx = _vectorType.GetField("X");
        _vy = _vectorType.GetField("Y");
        _vz = _vectorType.GetField("Z");

        if (_vx is null || _vy is null || _vz is null) { _idField = null; return false; }
        return true;
    }

    private static bool TryRead(object ship, out int id, out ShipState state)
    {
        id = 0;
        state = default;

        try
        {
            id = (int)_idField.GetValue(ship);
            var position = _positionField.GetValue(ship);

            state = new ShipState(
                (float)_vx.GetValue(position),
                (float)_vy.GetValue(position),
                (float)_vz.GetValue(position),
                _damageField?.GetValue(ship) is float d ? d : 0f,
                _destroyedField?.GetValue(ship) is true);

            return true;
        }
        catch { return false; }
    }

    private static void Write(object ship, ShipState state)
    {
        // Vector3 is a struct: build a box, set its fields, write the whole thing back.
        // Setting X on whatever GetValue returned would modify a copy and nothing else.
        var box = Activator.CreateInstance(_vectorType);
        _vx.SetValue(box, state.X);
        _vy.SetValue(box, state.Y);
        _vz.SetValue(box, state.Z);
        _positionField.SetValue(ship, box);

        _damageField?.SetValue(ship, state.Damage);
        _destroyedField?.SetValue(ship, state.Destroyed);
    }

    private static bool Differs(ShipState a, ShipState b)
    {
        if (a.Destroyed != b.Destroyed) return true;
        if (Math.Abs(a.Damage - b.Damage) > MinDamageChange) return true;

        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz > MinMoveSquared;
    }
}
