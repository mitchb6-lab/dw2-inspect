using System.Reflection;
using System.Security.Cryptography;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// The determinism hash: Galaxy.WriteToStream into a SnapshotHashWriter, SHA-256 of the bytes.
///
/// Why a special writer and two Harmony patches instead of plain BinaryWriter: the first
/// valid two-run comparison on 1.3.8.4 (2026-11-10, same save, same step sequence) differed
/// at cycle 1 with identical byte counts. The dumps showed 125 differing bytes in five
/// ShipHull records: the renderer had loaded five hull models in one run and none in the
/// other, filling _ExteriorComponentBaysLoadedFromModel and BoundingBox. That is what the
/// camera saw, not what the simulation did, and it made every cycle-1 verdict read
/// "diverged at the first snapshot" regardless of the simulation.
///
/// The patches below are no-ops for every other writer: a real save and the FullState a
/// joiner adopts still carry the hull caches exactly as the game wrote them. Nothing on the
/// galaxy is mutated to take the hash. See SnapshotHashWriter for the byte-level rule.
/// </summary>
public static class SnapshotHash
{
    private static bool _installed;

    /// <summary>True once both patches are in place. The harness reports when they are not.</summary>
    public static bool Installed => _installed;

    /// <summary>
    /// The game builds whose ShipHull.WriteToStream layout was read from the IL (the flag is the
    /// FIRST bool the hull writes; the box follows MaximumSize, DisplaySize, Bonuses and
    /// ResourcesRequired). The rule is positional, so on any other build the normalisation is
    /// skipped and the harness says so, rather than silently zeroing some other bool.
    /// </summary>
    private static readonly string[] KnownBuildPrefixes = { "1.3.8.4" };

    public static void Install(Harmony harmony, Action<string> log)
    {
        string gameVersion = NetSession.GameVersion();
        bool knownBuild = KnownBuildPrefixes.Any(prefix => gameVersion.StartsWith(prefix, StringComparison.Ordinal));
        if (!knownBuild)
        {
            log($"# hash: game build {gameVersion} is not one the hull layout was read from ({string.Join(", ", KnownBuildPrefixes)}); hull model caches are NOT normalised, so a cycle-1 divergence may be the renderer");
            return;
        }

        var hullType = AccessTools.TypeByName("DistantWorlds.Types.ShipHull");
        var helperType = AccessTools.TypeByName("DistantWorlds.Types.SerializationHelper");

        var hullWrite = hullType is null ? null : AccessTools.Method(hullType, "WriteToStream", new[] { typeof(BinaryWriter) });
        var boundingBoxWrite = helperType is null ? null : AccessTools.Method(helperType, "WriteBoundingBox");

        if (hullWrite is null || boundingBoxWrite is null)
        {
            log("# hash: ShipHull.WriteToStream or SerializationHelper.WriteBoundingBox not found; hull model caches are NOT normalised, so a cycle-1 divergence may be the renderer");
            return;
        }

        var beforeHull = new HarmonyMethod(typeof(SnapshotHash).GetMethod(nameof(BeforeHullWrite), BindingFlags.NonPublic | BindingFlags.Static));
        var afterHull = new HarmonyMethod(typeof(SnapshotHash).GetMethod(nameof(AfterHullWrite), BindingFlags.NonPublic | BindingFlags.Static));
        var beforeBoundingBox = new HarmonyMethod(typeof(SnapshotHash).GetMethod(nameof(BeforeBoundingBoxWrite), BindingFlags.NonPublic | BindingFlags.Static));

        harmony.Patch(hullWrite, prefix: beforeHull, postfix: afterHull);
        harmony.Patch(boundingBoxWrite, prefix: beforeBoundingBox);

        _installed = true;
        log("# hash: ship-hull model caches (model-loaded flag, bounding box) are normalised in the determinism hash; saves and full states are untouched");
    }

    /// <summary>Harmony prefix on ShipHull.WriteToStream(BinaryWriter). __0 is the writer.</summary>
    private static void BeforeHullWrite(BinaryWriter __0)
    {
        if (__0 is SnapshotHashWriter hashWriter)
        {
            hashWriter.BeginShipHull();
        }
    }

    /// <summary>Harmony postfix on ShipHull.WriteToStream(BinaryWriter).</summary>
    private static void AfterHullWrite(BinaryWriter __0)
    {
        if (__0 is SnapshotHashWriter hashWriter)
        {
            hashWriter.EndShipHull();
        }
    }

    /// <summary>
    /// Harmony prefix on SerializationHelper.WriteBoundingBox(BoundingBox, BinaryWriter). __1 is
    /// the writer. Returning false skips the game's write; the empty box has already been
    /// written in its place.
    /// </summary>
    private static bool BeforeBoundingBoxWrite(BinaryWriter __1)
    {
        if (__1 is SnapshotHashWriter hashWriter && hashWriter.TryWriteEmptyBoundingBox())
        {
            return false;
        }

        return true;
    }

    public sealed class Result
    {
        public string Hash { get; init; }

        public byte[] Raw { get; init; }

        /// <summary>hulls=N flags=N boxes=N, for the log.</summary>
        public string Normalisation { get; init; }

        /// <summary>Every hull had exactly one flag and one box normalised.</summary>
        public bool Consistent { get; init; }
    }

    /// <summary>
    /// Serialise the galaxy the way a save does, through the normalising writer, and hash it.
    /// A fresh GameGalaxyData is passed because the live one carries the view position and
    /// stopwatch values, which vary every run.
    /// </summary>
    public static Result Compute(object galaxy, MethodInfo writeToStream, Type galaxyDataType)
    {
        var galaxyData = Activator.CreateInstance(galaxyDataType);

        using var buffer = new MemoryStream();
        using var writer = new SnapshotHashWriter(buffer);

        writeToStream.Invoke(galaxy, new[] { writer, galaxyData });
        writer.Flush();

        byte[] raw = buffer.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(raw))[..16];

        var result = new Result
        {
            Hash = hash,
            Raw = raw,
            Normalisation = writer.Summary,
            Consistent = writer.Consistent,
        };
        return result;
    }
}
