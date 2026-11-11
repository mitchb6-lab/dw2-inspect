using System;

namespace Dw2MpShared;

/// <summary>
/// When is a continuously-changing number worth sending again? The same question for a ship's
/// fuel, an empire's cash and a colony's population, so it has one answer in one place.
///
/// A value is "changed" when it has moved by more than an absolute floor AND by more than a
/// fraction of its own size. The floor stops tiny values from chattering (a shield at 0.3 that
/// flickers to 0.4); the fraction stops big ones from being sent every delta for a rounding
/// error (two billion people growing by a few thousand).
/// </summary>
public static class SyncThresholds
{
    public static bool Differs(double before, double now, double absoluteFloor, double relativeFraction)
    {
        if (double.IsNaN(before) || double.IsNaN(now))
        {
            // NaN is the "never sent" marker in a baseline: always send.
            return !(double.IsNaN(before) && double.IsNaN(now));
        }

        double difference = Math.Abs(before - now);
        if (difference <= absoluteFloor)
        {
            return false;
        }

        double scale = Math.Max(Math.Abs(before), Math.Abs(now));
        return difference > scale * relativeFraction;
    }

    /// <summary>A ship's shields, fuel, energy or construction level: half a point and 2 %.</summary>
    public static bool VitalDiffers(double before, double now)
    {
        return Differs(before, now, 0.5, 0.02);
    }

    /// <summary>An empire's cash: one credit and half a percent.</summary>
    public static bool MoneyDiffers(double before, double now)
    {
        return Differs(before, now, 1.0, 0.005);
    }

    /// <summary>One race's population on one colony: a thousand people and 0.2 %.</summary>
    public static bool PopulationDiffers(long before, long now)
    {
        return Differs(before, now, 1000.0, 0.002);
    }

    /// <summary>A colony rate or level (tax, development, migration, funding): 0.005 and 1 %.</summary>
    public static bool ColonyRateDiffers(double before, double now)
    {
        return Differs(before, now, 0.005, 0.01);
    }
}
