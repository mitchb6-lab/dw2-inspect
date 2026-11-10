using System.IO;
using System.Text;

namespace Dw2MpShared;

/// <summary>
/// The BinaryWriter the determinism hash serialises into, and the one place that knows which
/// bytes of a ship hull are presentation rather than simulation.
///
/// Found 2026-11-10 by diffing the cycle-1 dumps of two runs that had loaded the same save:
/// 125 bytes differed, in five groups, each one ShipHull. In every group the hull's
/// _ExteriorComponentBaysLoadedFromModel flag was 0 in one run and 1 in the other, and its
/// BoundingBox was Stride's BoundingBox.Empty (Minimum = +MaxValue, Maximum = -MaxValue) in
/// the run with the flag down and a computed box in the other. Both are filled by the
/// RENDERER when it first loads a hull's 3D model (ShipHull.ExamineModelForExteriorBays), so
/// they record which ships the camera happened to look at in the first second. The
/// simulation never reads them and the game recomputes them on load.
///
/// The writer normalises both to their "not loaded" form while a hull is being written:
///   * the first bool a hull writes is that flag (ShipHull.WriteToStream writes 5 integers,
///     8 strings through helpers that write no bool, and 15 numbers before it), so it is
///     written as false;
///   * SerializationHelper.WriteBoundingBox is redirected to WriteEmptyBoundingBox by a
///     Harmony prefix in the mod, which only fires when the writer is this type.
///
/// Nothing is mutated on the galaxy; only the bytes that reach the hash change. Real saves
/// and the FullState sent to a joiner use a plain BinaryWriter and are untouched. The
/// counters exist so the harness can prove every hull was treated the same way.
/// </summary>
public sealed class SnapshotHashWriter : BinaryWriter
{
    /// <summary>Six floats: Minimum then Maximum, as SerializationHelper.WriteBoundingBox writes them.</summary>
    public const int BoundingBoxBytes = 24;

    public int HullsSeen { get; private set; }

    public int FlagsNormalised { get; private set; }

    public int BoundingBoxesNormalised { get; private set; }

    public bool InsideShipHull { get; private set; }

    private bool _flagWrittenForThisHull;

    public SnapshotHashWriter(Stream output)
        : base(output, Encoding.UTF8, leaveOpen: true)
    {
    }

    /// <summary>Called by the mod's prefix on ShipHull.WriteToStream.</summary>
    public void BeginShipHull()
    {
        InsideShipHull = true;
        _flagWrittenForThisHull = false;
        HullsSeen++;
    }

    /// <summary>Called by the mod's postfix on ShipHull.WriteToStream.</summary>
    public void EndShipHull()
    {
        InsideShipHull = false;
    }

    public override void Write(bool value)
    {
        bool isTheModelLoadedFlag = InsideShipHull && !_flagWrittenForThisHull;
        if (isTheModelLoadedFlag)
        {
            _flagWrittenForThisHull = true;
            FlagsNormalised++;
            base.Write(false);
            return;
        }

        base.Write(value);
    }

    /// <summary>
    /// Writes BoundingBox.Empty in place of a hull's computed box: Minimum = (MaxValue x3),
    /// Maximum = (MinValue x3), exactly the 24 bytes an unloaded hull produces. Returns false,
    /// writing nothing, when no hull is being written, so a bounding box anywhere else in the
    /// galaxy is left alone.
    /// </summary>
    public bool TryWriteEmptyBoundingBox()
    {
        if (!InsideShipHull)
        {
            return false;
        }

        BoundingBoxesNormalised++;

        for (int axis = 0; axis < 3; axis++)
        {
            base.Write(float.MaxValue);
        }

        for (int axis = 0; axis < 3; axis++)
        {
            base.Write(float.MinValue);
        }

        return true;
    }

    /// <summary>Every hull seen had exactly one flag and one box normalised.</summary>
    public bool Consistent => HullsSeen == FlagsNormalised && HullsSeen == BoundingBoxesNormalised;

    public string Summary => $"hulls={HullsSeen} flags={FlagsNormalised} boxes={BoundingBoxesNormalised}";
}
