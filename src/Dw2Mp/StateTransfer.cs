using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M3a: measure whether host-authoritative state transfer is viable.
///
/// Lockstep is closed (see docs/multiplayer.md M2b/M2c). The remaining architecture is
/// host-authoritative: one machine simulates, clients send GameTasks and receive galaxy
/// state. Whether that is playable rather than merely possible comes down to three
/// numbers, and this measures all three in-process:
///
///   serialise ms   — what the HOST pays each sync. Blocks the host's frame if done
///                    inline, so it bounds sync frequency.
///   payload bytes  — raw and compressed. Sets the bandwidth floor.
///   deserialise ms — what the CLIENT pays each sync. This is the one that decides it:
///                    a client that freezes for tens of seconds per sync is unplayable
///                    regardless of how good the rest is.
///
/// Deliberately measured in-process and separately from engine startup. Each run tonight
/// took minutes to reach a playable state, but almost all of that is scene and asset
/// setup that a already-running client would never repeat. Timing the whole launch would
/// have given a number that looks fatal and means nothing.
///
/// Galaxy.ReadFromStream returns a NEW Galaxy rather than mutating one, so deserialising
/// costs nothing but time — the live game is untouched.
/// </summary>
public static class StateTransfer
{
    private static MethodInfo _readFromStream;
    private static bool _resolved;

    public static void Measure(object galaxy, MethodInfo writeToStream, Type galaxyDataType, Action<string> log)
    {
        try
        {
            Resolve(galaxy.GetType(), log);

            // --- serialise (host cost) ---
            var galaxyData = Activator.CreateInstance(galaxyDataType);
            var buffer = new MemoryStream(48 * 1024 * 1024);

            var sw = Stopwatch.StartNew();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writeToStream.Invoke(galaxy, new[] { writer, galaxyData });
                writer.Flush();
            }
            double writeMs = sw.Elapsed.TotalMilliseconds;

            byte[] raw = buffer.ToArray();

            // --- compress (bandwidth floor) ---
            sw.Restart();
            byte[] packed = Deflate(raw);
            double packMs = sw.Elapsed.TotalMilliseconds;

            // --- deserialise (client cost) — the number that decides viability ---
            double readMs = -1;
            string readNote = "";
            if (_readFromStream is not null)
            {
                try
                {
                    sw.Restart();
                    using var input = new MemoryStream(raw, writable: false);
                    using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

                    // (BinaryReader, out GameGalaxyData, out List<...>)
                    var pars = _readFromStream.GetParameters();
                    var args = new object[pars.Length];
                    args[0] = reader;

                    // An instance overload must NOT be invoked on the live galaxy -- if it
                    // populates 'this' it would overwrite the running game. Deserialise
                    // into a blank object instead; we only want the timing.
                    object target = _readFromStream.IsStatic
                        ? null
                        : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(galaxy.GetType());

                    _readFromStream.Invoke(target, args);
                    readMs = sw.Elapsed.TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    var cause = ex.InnerException ?? ex;
                    readNote = $"read failed: {cause.GetType().Name}: {cause.Message}";
                }
            }
            else readNote = "ReadFromStream not resolved";

            log(string.Format(
                "# transfer: raw={0:N0}B  packed={1:N0}B ({2:P1})  write={3:N0}ms  pack={4:N0}ms  read={5}  {6}",
                raw.LongLength, packed.LongLength, (double)packed.LongLength / raw.LongLength,
                writeMs, packMs, readMs < 0 ? "n/a" : $"{readMs:N0}ms", readNote));
        }
        catch (Exception ex)
        {
            log("# transfer: measurement failed " + ex.GetType().Name + ": " + (ex.InnerException ?? ex).Message);
        }
    }

    private static void Resolve(Type galaxyType, Action<string> log)
    {
        if (_resolved) return;
        _resolved = true;

        // Deliberately permissive. The first attempt required static AND an exact Galaxy
        // return type and matched nothing, which cost a whole run to discover. Match on
        // the one thing that is certain -- the name and a BinaryReader first parameter --
        // then log what was actually found.
        foreach (var m in galaxyType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Static | BindingFlags.Instance |
                                                BindingFlags.DeclaredOnly))
        {
            if (m.Name != "ReadFromStream") continue;

            var p = m.GetParameters();
            log($"# transfer: candidate ReadFromStream(static={m.IsStatic}) -> {m.ReturnType.Name} " +
                $"({string.Join(", ", p.Select(x => x.ParameterType.Name))})");

            if (p.Length >= 1 && p[0].ParameterType == typeof(BinaryReader) && _readFromStream is null)
                _readFromStream = m;
        }

        if (_readFromStream is null)
            log("# transfer: no Galaxy.ReadFromStream(BinaryReader, ...) found");
    }

    /// <summary>
    /// Deflate at Fastest, not Optimal. A sync has to happen while someone is waiting,
    /// so the realistic figure is what a fast setting achieves, not the best possible
    /// ratio given unlimited CPU.
    /// </summary>
    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
