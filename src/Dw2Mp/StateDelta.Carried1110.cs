using System.Collections;
using System.Globalization;
using System.Reflection;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// The state deltas started carrying on 2026-11-10, ranked by the two-instance measurement of
/// that day (Scripts\net-twoinstance.ps1 with DW2MP_DIVERGENCE_DIR; see docs\multiplayer.md).
/// Thirty seconds after a full state, with every ship, colony and design present on both sides:
///
///     colonies  12 of 12 differed in per-race population and development level, 6 in tax rate,
///               10 in migration factor, 3 in influence radius and growth funding
///     designs   542 of 669 differed in build cost and maintenance cost (derived on the host,
///               not restored by a full state)
///     ships     40 differed in shields, 15 in energy, 11 in fuel, 19 in construction level
///
/// plus the two mitch named that the first run could not measure (the host-side dump failed
/// before it reached empires): diplomatic relations and treaties, and empire cash.
///
/// Five sections, appended to the delta AFTER fleets, so the older sections' wire layout is
/// untouched:
///
///     ship vitals     shields, fuel, energy, hull construction level   field level, thresholded
///     colony extras   five rates and the per-race population list      field level, thresholded
///     design costs    build and maintenance cost                       field level, exact
///     empire money    state and private cash                           field level, thresholded
///     relations       an empire's whole relation list, in DW2's own bytes, when a treaty,
///                     war, peace or first contact changed it
///
/// Relations use DW2's own serialiser, as ships do: EmpireRelationList.WriteToStream on the
/// host; on the joiner a NEW list is read with EmpireRelationList.ReadFromStream and swapped in
/// for the old one, which is exactly what loading a save does. The trigger is a signature of the
/// relation's flags and small ids, not a hash of its bytes: a relation also holds incident
/// history and timestamps that move constantly, and a byte hash cannot tell a treaty from a
/// timestamp (that is what made refreshing characters cost 32 MB in September).
///
/// Each section walks only every Nth delta. All of them are update-only or replace-whole, so a
/// late update is staleness, never an object the joiner can no longer be told about.
/// </summary>
public static partial class StateDelta
{
    private const int VitalsEvery = 3;
    private const int ColonyExtrasEvery = 3;
    private const int DesignCostsEvery = 5;
    private const int MoneyEvery = 3;
    private const int RelationsEvery = 5;

    /// <summary>A relation list bigger than this is not sent; the next full state carries it.</summary>
    private const int MaxRelationListBytes = 4 * 1024 * 1024;

    private const int MaxRacesPerColony = 64;

    private readonly record struct ShipVitals(float Shields, float Fuel, float Energy, float HullConstruction);

    private readonly record struct PopulationRecord(byte RaceId, long Amount, float Assimilation, float ReproductionRate);

    private sealed class ColonyExtras
    {
        public double DevelopmentLevel;
        public double TaxRate;
        public double MigrationFactor;
        public double InfluenceRadius;
        public double GrowthFunding;
        public List<PopulationRecord> Populations = new();
    }

    private readonly record struct DesignCosts(double BuildCost, double MaintenanceCost);

    private readonly record struct EmpireMoney(float Money, float PrivateMoney);

    // Host: what the joiner was last told.
    private static readonly Dictionary<int, ShipVitals> _lastVitals = new();
    private static readonly Dictionary<short, ColonyExtras> _lastColonyExtras = new();
    private static readonly Dictionary<int, DesignCosts> _lastDesignCosts = new();
    private static readonly Dictionary<short, EmpireMoney> _lastMoney = new();
    private static readonly Dictionary<short, ulong> _lastRelationSignatures = new();

    private static void ResetCarriedBaselines()
    {
        _lastVitals.Clear();
        _lastColonyExtras.Clear();
        _lastDesignCosts.Clear();
        _lastMoney.Clear();
        _lastRelationSignatures.Clear();
    }

    /// <summary>
    /// A section walks on its own cadence, and always while priming: priming must record every
    /// baseline no matter where the delta counter happens to be.
    /// </summary>
    private static bool SectionDue(int every, bool primeOnly)
    {
        if (primeOnly)
        {
            return true;
        }

        return _deltaSequence % every == 0;
    }

    // ------------------------------------------------------------------ ship vitals

    private sealed class WrappedFloat
    {
        public FieldInfo Outer;
        public FieldInfo Inner;
    }

    private static WrappedFloat _shieldsField;
    private static WrappedFloat _fuelField;
    private static WrappedFloat _energyField;
    private static FieldInfo _hullConstructionField;
    private static bool _vitalsResolved;

    /// <summary>Ship.Shields, .Fuel and .Energy are AtomicFloat structs: the number is their _value field.</summary>
    private static WrappedFloat ResolveWrapped(Type ownerType, string fieldName)
    {
        FieldInfo outer = AccessTools.Field(ownerType, fieldName);
        if (outer is null)
        {
            return null;
        }

        FieldInfo inner = AccessTools.Field(outer.FieldType, "_value");
        if (inner is null)
        {
            return null;
        }

        return new WrappedFloat { Outer = outer, Inner = inner };
    }

    private static void ResolveVitals(object ship)
    {
        if (_vitalsResolved)
        {
            return;
        }

        Type shipType = ship.GetType();
        _shieldsField = ResolveWrapped(shipType, "Shields");
        _fuelField = ResolveWrapped(shipType, "Fuel");
        _energyField = ResolveWrapped(shipType, "Energy");
        _hullConstructionField = AccessTools.Field(shipType, "HullConstructionLevel");
        _vitalsResolved = true;
    }

    private static float ReadWrapped(WrappedFloat wrapped, object owner)
    {
        if (wrapped is null)
        {
            return 0f;
        }

        object box = wrapped.Outer.GetValue(owner);
        object inner = wrapped.Inner.GetValue(box);
        return Convert.ToSingle(inner, CultureInfo.InvariantCulture);
    }

    /// <summary>A struct: read the box, set the number inside it, write the whole box back.</summary>
    private static void WriteWrapped(WrappedFloat wrapped, object owner, float value)
    {
        if (wrapped is null)
        {
            return;
        }

        object box = wrapped.Outer.GetValue(owner);
        object converted = Convert.ChangeType(value, wrapped.Inner.FieldType, CultureInfo.InvariantCulture);
        wrapped.Inner.SetValue(box, converted);
        wrapped.Outer.SetValue(owner, box);
    }

    private static double ReadNumber(FieldInfo field, object owner)
    {
        if (field is null)
        {
            return 0;
        }

        object value = field.GetValue(owner);
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static void WriteNumber(FieldInfo field, object owner, double value)
    {
        if (field is null)
        {
            return;
        }

        object converted = Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture);
        field.SetValue(owner, converted);
    }

    private static bool VitalsDiffer(ShipVitals before, ShipVitals now)
    {
        if (SyncThresholds.VitalDiffers(before.Shields, now.Shields))
        {
            return true;
        }

        if (SyncThresholds.VitalDiffers(before.Fuel, now.Fuel))
        {
            return true;
        }

        if (SyncThresholds.VitalDiffers(before.Energy, now.Energy))
        {
            return true;
        }

        return SyncThresholds.VitalDiffers(before.HullConstruction, now.HullConstruction);
    }

    private static int WriteVitalsSection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        if (!SectionDue(VitalsEvery, primeOnly))
        {
            writer.Write(0);
            return 0;
        }

        object ships = ListField(galaxy, "Ships");
        MethodInfo item = Indexer(ships);
        int total = CountOf(ships);

        var present = new HashSet<int>();
        var updated = new List<(int Id, ShipVitals Vitals)>();

        for (int index = 0; index < total && item is not null; index++)
        {
            object ship = At(item, ships, index);
            if (ship is null || !ResolveShip(ship))
            {
                continue;
            }

            ResolveVitals(ship);

            int shipId;
            ShipVitals now;
            try
            {
                shipId = (int)_shipId.GetValue(ship);
                now = new ShipVitals(
                    ReadWrapped(_shieldsField, ship),
                    ReadWrapped(_fuelField, ship),
                    ReadWrapped(_energyField, ship),
                    (float)ReadNumber(_hullConstructionField, ship));
            }
            catch (Exception)
            {
                continue;
            }

            present.Add(shipId);

            bool known = _lastVitals.TryGetValue(shipId, out ShipVitals before);
            if (known && !VitalsDiffer(before, now))
            {
                continue;
            }

            _lastVitals[shipId] = now;

            // A ship the joiner is only now being told about arrives whole, vitals included.
            if (known)
            {
                updated.Add((shipId, now));
            }
        }

        List<int> gone = _lastVitals.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (int id in gone)
        {
            _lastVitals.Remove(id);
        }

        if (primeOnly)
        {
            writer.Write(0);
            return 0;
        }

        writer.Write(updated.Count);
        foreach (var (shipId, vitals) in updated)
        {
            writer.Write(shipId);
            writer.Write(vitals.Shields);
            writer.Write(vitals.Fuel);
            writer.Write(vitals.Energy);
            writer.Write(vitals.HullConstruction);
        }

        return updated.Count;
    }

    private static int ApplyVitalsSection(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        object ships = ListField(galaxy, "Ships");
        MethodInfo getById = ById(ships, typeof(int));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int index = 0; index < count; index++)
        {
            int shipId = reader.ReadInt32();
            var vitals = new ShipVitals(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            object ship = Lookup(getById, ships, shipId);
            if (ship is null)
            {
                absent++;
                continue;
            }

            try
            {
                ResolveVitals(ship);
                WriteWrapped(_shieldsField, ship, vitals.Shields);
                WriteWrapped(_fuelField, ship, vitals.Fuel);
                WriteWrapped(_energyField, ship, vitals.Energy);
                WriteNumber(_hullConstructionField, ship, vitals.HullConstruction);
                applied++;
            }
            catch (Exception exception)
            {
                NoteMutationFailure("Ship vitals: " + (exception.InnerException ?? exception).GetType().Name);
                absent++;
            }
        }

        return applied;
    }

    // ------------------------------------------------------------------ colony extras

    private static FieldInfo _developmentLevelField;
    private static FieldInfo _taxRateField;
    private static FieldInfo _migrationFactorField;
    private static FieldInfo _influenceRadiusField;
    private static FieldInfo _growthFundingField;
    private static FieldInfo _populationListField;
    private static FieldInfo _populationRaceField;
    private static FieldInfo _populationAmountField;
    private static FieldInfo _populationAssimilationField;
    private static FieldInfo _populationReproductionField;
    private static ConstructorInfo _populationConstructor;
    private static bool _colonyExtrasResolved;

    private static void ResolveColonyExtras(object colony)
    {
        if (_colonyExtrasResolved)
        {
            return;
        }

        Type colonyType = colony.GetType();
        _developmentLevelField = AccessTools.Field(colonyType, "_DevelopmentLevel");
        _taxRateField = AccessTools.Field(colonyType, "TaxRate");
        _migrationFactorField = AccessTools.Field(colonyType, "_MigrationFactor");
        _influenceRadiusField = AccessTools.Field(colonyType, "ColonyInfluenceRadius");
        _growthFundingField = AccessTools.Field(colonyType, "GrowthFundingRatio");
        _populationListField = AccessTools.Field(colonyType, "Population");

        Type populationType = AccessTools.TypeByName("DistantWorlds.Types.Population");
        if (populationType is not null)
        {
            _populationRaceField = AccessTools.Field(populationType, "_RaceId");
            _populationAmountField = AccessTools.Field(populationType, "Amount");
            _populationAssimilationField = AccessTools.Field(populationType, "Assimilation");
            _populationReproductionField = AccessTools.Field(populationType, "ReproductionRate");
            _populationConstructor = populationType.GetConstructor(new[] { typeof(byte), typeof(long), typeof(float), typeof(float) });
        }

        _colonyExtrasResolved = true;
    }

    private static bool PopulationReadable =>
        _populationListField is not null && _populationRaceField is not null && _populationAmountField is not null;

    private static ColonyExtras ReadColonyExtras(object colony)
    {
        var extras = new ColonyExtras
        {
            DevelopmentLevel = ReadNumber(_developmentLevelField, colony),
            TaxRate = ReadNumber(_taxRateField, colony),
            MigrationFactor = ReadNumber(_migrationFactorField, colony),
            InfluenceRadius = ReadNumber(_influenceRadiusField, colony),
            GrowthFunding = ReadNumber(_growthFundingField, colony),
        };

        if (!PopulationReadable)
        {
            return extras;
        }

        // PopulationList is a List<Population>. The simulation may add a race while this walks,
        // so it is indexed with a bounds check on every step, not enumerated.
        if (_populationListField.GetValue(colony) is not IList populations)
        {
            return extras;
        }

        for (int index = 0; index < populations.Count && extras.Populations.Count < MaxRacesPerColony; index++)
        {
            object population;
            try
            {
                population = populations[index];
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            if (population is null)
            {
                continue;
            }

            var record = new PopulationRecord(
                (byte)_populationRaceField.GetValue(population),
                (long)_populationAmountField.GetValue(population),
                (float)ReadNumber(_populationAssimilationField, population),
                (float)ReadNumber(_populationReproductionField, population));
            extras.Populations.Add(record);
        }

        return extras;
    }

    private static bool ColonyExtrasDiffer(ColonyExtras before, ColonyExtras now)
    {
        if (SyncThresholds.ColonyRateDiffers(before.DevelopmentLevel, now.DevelopmentLevel))
        {
            return true;
        }

        if (SyncThresholds.ColonyRateDiffers(before.TaxRate, now.TaxRate))
        {
            return true;
        }

        if (SyncThresholds.ColonyRateDiffers(before.MigrationFactor, now.MigrationFactor))
        {
            return true;
        }

        if (SyncThresholds.ColonyRateDiffers(before.InfluenceRadius, now.InfluenceRadius))
        {
            return true;
        }

        if (SyncThresholds.ColonyRateDiffers(before.GrowthFunding, now.GrowthFunding))
        {
            return true;
        }

        if (before.Populations.Count != now.Populations.Count)
        {
            return true;
        }

        for (int index = 0; index < now.Populations.Count; index++)
        {
            PopulationRecord earlier = before.Populations[index];
            PopulationRecord current = now.Populations[index];

            if (earlier.RaceId != current.RaceId)
            {
                return true;
            }

            if (SyncThresholds.PopulationDiffers(earlier.Amount, current.Amount))
            {
                return true;
            }

            if (SyncThresholds.ColonyRateDiffers(earlier.Assimilation, current.Assimilation))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// NOT primed after a full state, unlike the older sections. The development level is derived
    /// by the simulation and a full state does not restore it (the joiner read 0.78 where the
    /// host had 0.52), so the host cannot assume the joiner's copy equals its own. The baseline is
    /// cleared instead, and the first pass after every full state sends every colony once: a few
    /// kilobytes against megabytes.
    /// </summary>
    private static int WriteColonyExtrasSection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        if (primeOnly)
        {
            _lastColonyExtras.Clear();
            writer.Write(0);
            return 0;
        }

        if (!SectionDue(ColonyExtrasEvery, primeOnly))
        {
            writer.Write(0);
            return 0;
        }

        object colonies = ListField(galaxy, "Colonies");
        MethodInfo item = Indexer(colonies);
        int count = CountOf(colonies);

        var present = new HashSet<short>();
        var updated = new List<(short Id, ColonyExtras Extras)>();

        for (int index = 0; index < count && item is not null; index++)
        {
            object colony = At(item, colonies, index);
            if (colony is null || !ResolveColony(colony))
            {
                continue;
            }

            ResolveColonyExtras(colony);

            short colonyId;
            ColonyExtras now;
            try
            {
                colonyId = (short)_colonyId.GetValue(colony);
                now = ReadColonyExtras(colony);
            }
            catch (Exception)
            {
                continue;
            }

            present.Add(colonyId);

            bool known = _lastColonyExtras.TryGetValue(colonyId, out ColonyExtras before);
            if (known && !ColonyExtrasDiffer(before, now))
            {
                continue;
            }

            _lastColonyExtras[colonyId] = now;
            updated.Add((colonyId, now));
        }

        List<short> gone = _lastColonyExtras.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (short id in gone)
        {
            _lastColonyExtras.Remove(id);
        }

        writer.Write(updated.Count);
        foreach (var (colonyId, extras) in updated)
        {
            writer.Write(colonyId);
            writer.Write(extras.DevelopmentLevel);
            writer.Write(extras.TaxRate);
            writer.Write(extras.MigrationFactor);
            writer.Write(extras.InfluenceRadius);
            writer.Write(extras.GrowthFunding);

            writer.Write((byte)extras.Populations.Count);
            foreach (PopulationRecord population in extras.Populations)
            {
                writer.Write(population.RaceId);
                writer.Write(population.Amount);
                writer.Write(population.Assimilation);
                writer.Write(population.ReproductionRate);
            }
        }

        return updated.Count;
    }

    private static int ApplyColonyExtrasSection(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        object colonies = ListField(galaxy, "Colonies");
        MethodInfo getById = ById(colonies, typeof(short)) ?? ById(colonies, typeof(int));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int index = 0; index < count; index++)
        {
            short colonyId = reader.ReadInt16();
            var extras = new ColonyExtras
            {
                DevelopmentLevel = reader.ReadDouble(),
                TaxRate = reader.ReadDouble(),
                MigrationFactor = reader.ReadDouble(),
                InfluenceRadius = reader.ReadDouble(),
                GrowthFunding = reader.ReadDouble(),
            };

            int raceCount = reader.ReadByte();
            for (int race = 0; race < raceCount; race++)
            {
                var record = new PopulationRecord(reader.ReadByte(), reader.ReadInt64(), reader.ReadSingle(), reader.ReadSingle());
                extras.Populations.Add(record);
            }

            object colony = Lookup(getById, colonies, colonyId);
            if (colony is null)
            {
                absent++;
                continue;
            }

            try
            {
                ResolveColonyExtras(colony);
                WriteNumber(_developmentLevelField, colony, extras.DevelopmentLevel);
                WriteNumber(_taxRateField, colony, extras.TaxRate);
                WriteNumber(_migrationFactorField, colony, extras.MigrationFactor);
                WriteNumber(_influenceRadiusField, colony, extras.InfluenceRadius);
                WriteNumber(_growthFundingField, colony, extras.GrowthFunding);
                ApplyPopulations(colony, extras.Populations);
                applied++;
            }
            catch (Exception exception)
            {
                NoteMutationFailure("Colony extras: " + (exception.InnerException ?? exception).GetType().Name);
                absent++;
            }
        }

        return applied;
    }

    /// <summary>
    /// Make the colony's per-race list say what the host's says: update the races both have,
    /// add the ones only the host has, remove the ones only the joiner has. Existing Population
    /// objects are updated in place, so anything holding one keeps a live object.
    /// </summary>
    private static void ApplyPopulations(object colony, List<PopulationRecord> hostPopulations)
    {
        if (!PopulationReadable)
        {
            return;
        }

        if (_populationListField.GetValue(colony) is not IList populations)
        {
            return;
        }

        var hostRaces = new HashSet<byte>();
        foreach (PopulationRecord record in hostPopulations)
        {
            hostRaces.Add(record.RaceId);
        }

        for (int index = populations.Count - 1; index >= 0; index--)
        {
            object existing = populations[index];
            if (existing is null)
            {
                continue;
            }

            byte raceId = (byte)_populationRaceField.GetValue(existing);
            if (!hostRaces.Contains(raceId))
            {
                populations.RemoveAt(index);
            }
        }

        foreach (PopulationRecord record in hostPopulations)
        {
            object target = null;
            foreach (object existing in populations)
            {
                if (existing is not null && (byte)_populationRaceField.GetValue(existing) == record.RaceId)
                {
                    target = existing;
                    break;
                }
            }

            if (target is null)
            {
                if (_populationConstructor is null)
                {
                    NoteMutationFailure("Population: no (Byte, Int64, Single, Single) constructor");
                    continue;
                }

                object created = _populationConstructor.Invoke(new object[] { record.RaceId, record.Amount, record.Assimilation, record.ReproductionRate });
                populations.Add(created);
                continue;
            }

            _populationAmountField.SetValue(target, record.Amount);
            WriteNumber(_populationAssimilationField, target, record.Assimilation);
            WriteNumber(_populationReproductionField, target, record.ReproductionRate);
        }
    }

    // ------------------------------------------------------------------ design costs

    private static FieldInfo _designIdField;
    private static FieldInfo _buildCostField;
    private static FieldInfo _maintenanceCostField;
    private static bool _designCostsResolved;

    private static void ResolveDesignCosts(object design)
    {
        if (_designCostsResolved)
        {
            return;
        }

        Type designType = design.GetType();
        _designIdField = AccessTools.Field(designType, "DesignId");
        _buildCostField = AccessTools.Field(designType, "BuildCost");
        _maintenanceCostField = AccessTools.Field(designType, "MaintenanceCost");
        _designCostsResolved = true;
    }

    /// <summary>
    /// Not primed after a full state, for the same reason as colony extras: the host recomputes
    /// these and a full state does not carry the result (542 of 669 designs read 1,500 on the
    /// joiner against 3,228 on the host straight after adoption).
    /// </summary>
    private static int WriteDesignCostsSection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        if (primeOnly)
        {
            _lastDesignCosts.Clear();
            writer.Write(0);
            return 0;
        }

        if (!SectionDue(DesignCostsEvery, primeOnly))
        {
            writer.Write(0);
            return 0;
        }

        object designs = ListField(galaxy, "Designs");
        MethodInfo item = Indexer(designs);
        int count = CountOf(designs);

        var updated = new List<(int Id, DesignCosts Costs)>();

        for (int index = 0; index < count && item is not null; index++)
        {
            object design = At(item, designs, index);
            if (design is null)
            {
                continue;
            }

            ResolveDesignCosts(design);
            if (_designIdField is null || _buildCostField is null)
            {
                break;
            }

            int designId;
            DesignCosts now;
            try
            {
                designId = Convert.ToInt32(_designIdField.GetValue(design), CultureInfo.InvariantCulture);
                now = new DesignCosts(ReadNumber(_buildCostField, design), ReadNumber(_maintenanceCostField, design));
            }
            catch (Exception)
            {
                continue;
            }

            bool known = _lastDesignCosts.TryGetValue(designId, out DesignCosts before);
            if (known && before.BuildCost == now.BuildCost && before.MaintenanceCost == now.MaintenanceCost)
            {
                continue;
            }

            _lastDesignCosts[designId] = now;
            updated.Add((designId, now));
        }

        writer.Write(updated.Count);
        foreach (var (designId, costs) in updated)
        {
            writer.Write(designId);
            writer.Write(costs.BuildCost);
            writer.Write(costs.MaintenanceCost);
        }

        return updated.Count;
    }

    private static int ApplyDesignCostsSection(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        object designs = ListField(galaxy, "Designs");
        MethodInfo getById = ById(designs, typeof(int)) ?? ById(designs, typeof(short));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int index = 0; index < count; index++)
        {
            int designId = reader.ReadInt32();
            double buildCost = reader.ReadDouble();
            double maintenanceCost = reader.ReadDouble();

            object design = Lookup(getById, designs, designId);
            if (design is null)
            {
                absent++;
                continue;
            }

            try
            {
                ResolveDesignCosts(design);
                WriteNumber(_buildCostField, design, buildCost);
                WriteNumber(_maintenanceCostField, design, maintenanceCost);
                applied++;
            }
            catch (Exception exception)
            {
                NoteMutationFailure("Design costs: " + (exception.InnerException ?? exception).GetType().Name);
                absent++;
            }
        }

        return applied;
    }

    // ------------------------------------------------------------------ empire money

    private static FieldInfo _empireIdField;
    private static FieldInfo _economyField;
    private static PropertyInfo _moneyProperty;
    private static PropertyInfo _privateMoneyProperty;
    private static PropertyInfo _editMoneyProperty;
    private static PropertyInfo _editPrivateMoneyProperty;
    private static FieldInfo _relationsField;
    private static bool _empireResolved;

    private static void ResolveEmpire(object empire)
    {
        if (_empireResolved)
        {
            return;
        }

        Type empireType = empire.GetType();
        _empireIdField = AccessTools.Field(empireType, "EmpireId");
        _economyField = AccessTools.Field(empireType, "Economy");
        _relationsField = AccessTools.Field(empireType, "Relations");

        if (_economyField is not null)
        {
            Type economyType = _economyField.FieldType;
            _moneyProperty = AccessTools.Property(economyType, "Money");
            _privateMoneyProperty = AccessTools.Property(economyType, "PrivateMoney");
            _editMoneyProperty = AccessTools.Property(economyType, "EditMoney");
            _editPrivateMoneyProperty = AccessTools.Property(economyType, "EditPrivateMoney");
        }

        _empireResolved = true;
    }

    private static int WriteMoneySection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        if (!SectionDue(MoneyEvery, primeOnly))
        {
            writer.Write(0);
            return 0;
        }

        object empires = ListField(galaxy, "Empires");
        MethodInfo item = Indexer(empires);
        int count = CountOf(empires);

        var updated = new List<(short Id, EmpireMoney Money)>();

        for (int index = 0; index < count && item is not null; index++)
        {
            object empire = At(item, empires, index);
            if (empire is null)
            {
                continue;
            }

            ResolveEmpire(empire);
            if (_empireIdField is null || _economyField is null || _moneyProperty is null)
            {
                break;
            }

            short empireId;
            EmpireMoney now;
            try
            {
                empireId = (short)_empireIdField.GetValue(empire);
                object economy = _economyField.GetValue(empire);
                if (economy is null)
                {
                    continue;
                }

                float money = Convert.ToSingle(_moneyProperty.GetValue(economy), CultureInfo.InvariantCulture);
                float privateMoney = _privateMoneyProperty is null
                    ? 0f
                    : Convert.ToSingle(_privateMoneyProperty.GetValue(economy), CultureInfo.InvariantCulture);
                now = new EmpireMoney(money, privateMoney);
            }
            catch (Exception)
            {
                continue;
            }

            bool known = _lastMoney.TryGetValue(empireId, out EmpireMoney before);
            bool moneyMoved = !known || SyncThresholds.MoneyDiffers(before.Money, now.Money);
            bool privateMoved = !known || SyncThresholds.MoneyDiffers(before.PrivateMoney, now.PrivateMoney);
            if (!moneyMoved && !privateMoved)
            {
                continue;
            }

            _lastMoney[empireId] = now;
            if (known)
            {
                updated.Add((empireId, now));
            }
        }

        if (primeOnly)
        {
            writer.Write(0);
            return 0;
        }

        writer.Write(updated.Count);
        foreach (var (empireId, money) in updated)
        {
            writer.Write(empireId);
            writer.Write(money.Money);
            writer.Write(money.PrivateMoney);
        }

        return updated.Count;
    }

    private static int ApplyMoneySection(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        object empires = ListField(galaxy, "Empires");
        MethodInfo empireById = ById(empires, typeof(short)) ?? ById(empires, typeof(int));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int index = 0; index < count; index++)
        {
            short empireId = reader.ReadInt16();
            float money = reader.ReadSingle();
            float privateMoney = reader.ReadSingle();

            object empire = Lookup(empireById, empires, empireId);
            if (empire is null)
            {
                absent++;
                continue;
            }

            try
            {
                ResolveEmpire(empire);
                object economy = _economyField?.GetValue(empire);
                if (economy is null || _editMoneyProperty is null)
                {
                    absent++;
                    continue;
                }

                // Economy.EditMoney's setter is `_Money.Value = value` and nothing else.
                _editMoneyProperty.SetValue(economy, money);
                _editPrivateMoneyProperty?.SetValue(economy, privateMoney);
                applied++;
            }
            catch (Exception exception)
            {
                NoteMutationFailure("Empire money: " + (exception.InnerException ?? exception).GetType().Name);
                absent++;
            }
        }

        return applied;
    }

    // ------------------------------------------------------------------ relations

    private static FieldInfo[] _relationSignatureFields;

    /// <summary>
    /// The fields whose change is a diplomatic EVENT: every bool, byte and short on a relation.
    /// That is HaveMet, AtWar, Subjugated, Locked, the thirteen Treaty flags, TreatyTradeLevel
    /// and the small ids. Deliberately not the Int64 dates, the float incident levels or the
    /// strategy enum, which move without anything a player would call a change.
    /// </summary>
    private static FieldInfo[] RelationSignatureFields(Type relationType)
    {
        if (_relationSignatureFields is not null)
        {
            return _relationSignatureFields;
        }

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _relationSignatureFields = relationType.GetFields(Instance)
            .Where(field => field.FieldType == typeof(bool) || field.FieldType == typeof(byte) || field.FieldType == typeof(short))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
        return _relationSignatureFields;
    }

    private static ulong RelationListSignature(object relations)
    {
        ulong signature = SyncDigest.EmptySetHash;
        MethodInfo item = Indexer(relations);
        int count = CountOf(relations);

        for (int index = 0; index < count && item is not null; index++)
        {
            object relation = At(item, relations, index);
            if (relation is null)
            {
                continue;
            }

            // One number per relation, then folded order-independently: the list's order is
            // not part of what a relation says.
            long relationHash = 17;
            foreach (FieldInfo field in RelationSignatureFields(relation.GetType()))
            {
                long value = Convert.ToInt64(field.GetValue(relation), CultureInfo.InvariantCulture);
                relationHash = unchecked(relationHash * 31 + value);
            }

            signature = SyncDigest.Fold(signature, relationHash);
        }

        return signature;
    }

    private static byte[] SerialiseRelationList(object relations)
    {
        MethodInfo write = AccessTools.Method(relations.GetType(), "WriteToStream", new[] { typeof(BinaryWriter) });
        if (write is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var listWriter = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            write.Invoke(relations, new object[] { listWriter });
            listWriter.Flush();
        }

        return buffer.ToArray();
    }

    private static int WriteRelationsSection(object galaxy, BinaryWriter writer, bool primeOnly)
    {
        if (!SectionDue(RelationsEvery, primeOnly))
        {
            writer.Write(0);
            return 0;
        }

        object empires = ListField(galaxy, "Empires");
        MethodInfo item = Indexer(empires);
        int count = CountOf(empires);

        var changed = new List<(short EmpireId, byte[] Bytes)>();

        for (int index = 0; index < count && item is not null; index++)
        {
            object empire = At(item, empires, index);
            if (empire is null)
            {
                continue;
            }

            ResolveEmpire(empire);
            if (_empireIdField is null || _relationsField is null)
            {
                break;
            }

            try
            {
                short empireId = (short)_empireIdField.GetValue(empire);
                object relations = _relationsField.GetValue(empire);
                if (relations is null)
                {
                    continue;
                }

                ulong signature = RelationListSignature(relations);
                bool known = _lastRelationSignatures.TryGetValue(empireId, out ulong before);
                if (known && before == signature)
                {
                    continue;
                }

                _lastRelationSignatures[empireId] = signature;

                // Unknown means "first sight after a full state", which carried the list itself.
                if (!known || primeOnly)
                {
                    continue;
                }

                byte[] bytes = SerialiseRelationList(relations);
                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxRelationListBytes)
                {
                    continue;
                }

                changed.Add((empireId, bytes));
            }
            catch (Exception)
            {
                continue;
            }
        }

        if (primeOnly)
        {
            writer.Write(0);
            return 0;
        }

        writer.Write(changed.Count);
        foreach (var (empireId, bytes) in changed)
        {
            writer.Write(empireId);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        return changed.Count;
    }

    private static int ApplyRelationsSection(object galaxy, BinaryReader reader, out int absent)
    {
        absent = 0;

        object empires = ListField(galaxy, "Empires");
        MethodInfo empireById = ById(empires, typeof(short)) ?? ById(empires, typeof(int));

        int count = reader.ReadInt32();
        int applied = 0;

        for (int index = 0; index < count; index++)
        {
            short empireId = reader.ReadInt16();
            int length = reader.ReadInt32();
            if (length <= 0 || length > MaxRelationListBytes)
            {
                throw new InvalidDataException($"relation list of {length} bytes");
            }

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("relation list truncated");
            }

            object empire = Lookup(empireById, empires, empireId);
            if (empire is null)
            {
                absent++;
                continue;
            }

            if (ReplaceRelationList(galaxy, empire, bytes))
            {
                applied++;
            }
            else
            {
                absent++;
            }
        }

        return applied;
    }

    /// <summary>
    /// What loading a save does: a NEW EmpireRelationList, filled by DW2's own ReadFromStream,
    /// then put in the empire's Relations field. The old list is never mutated, so anything
    /// reading it at that moment sees a consistent list. The swap only happens when the reader
    /// consumed exactly the bytes the host wrote; anything else means the two sides disagree
    /// about the layout, and a half-read list must not replace a good one.
    /// </summary>
    private static bool ReplaceRelationList(object galaxy, object empire, byte[] bytes)
    {
        string stage = "start";

        try
        {
            ResolveEmpire(empire);
            object current = _relationsField?.GetValue(empire);
            if (current is null)
            {
                NoteMutationFailure("Relations: empire has no relation list");
                return false;
            }

            Type listType = current.GetType();
            MethodInfo read = AccessTools.Method(listType, "ReadFromStream", new[] { galaxy.GetType(), typeof(BinaryReader) });
            if (read is null)
            {
                NoteMutationFailure("Relations: no ReadFromStream(Galaxy, BinaryReader)");
                return false;
            }

            stage = "construct";
            object fresh = Activator.CreateInstance(listType);

            stage = "read";
            long consumed;
            using (var input = new MemoryStream(bytes, writable: false))
            using (var listReader = new BinaryReader(input, System.Text.Encoding.UTF8))
            {
                read.Invoke(fresh, new[] { galaxy, (object)listReader });
                consumed = input.Position;
            }

            if (consumed != bytes.Length)
            {
                NoteMutationFailure($"Relations: reader consumed {consumed} of {bytes.Length} bytes; list not replaced");
                return false;
            }

            stage = "swap";
            _relationsField.SetValue(empire, fresh);
            return true;
        }
        catch (Exception exception)
        {
            var cause = exception.InnerException ?? exception;
            NoteMutationFailure($"Relations at {stage}: {cause.GetType().Name}: {cause.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ the sync digest

    /// <summary>
    /// What a StateSummary compares since protocol 4: the membership of every collection deltas
    /// create and remove members in, and nothing else. Built from the same lists and the same
    /// id fields the delta sections walk, so "the check compares only state the sync carries"
    /// is true by construction and stays true when a section is added.
    ///
    /// Ids come from the live objects, never from Count: DW2's lists keep a tombstone for a
    /// removed member until the simulation compacts them, and the joiner does not simulate.
    ///
    /// On the host, Unsent says how many members differ from the delta baseline, i.e. were
    /// created or removed since the last delta was built and so cannot be on the joiner yet.
    /// </summary>
    public static SyncDigest BuildDigest(object galaxy, bool isHost)
    {
        var digest = new SyncDigest();
        if (galaxy is null)
        {
            return digest;
        }

        lock (_baseline)
        {
            AddDigestEntry(digest, "ships", galaxy, "Ships", "ShipId", isHost, id => _lastShips.ContainsKey((int)id), _lastShips.Count);
            AddDigestEntry(digest, "colonies", galaxy, "Colonies", "ColonyId", isHost, id => _lastColonies.ContainsKey((short)id), _lastColonies.Count);

            foreach (WholeObjectKind kind in WholeObjectKinds)
            {
                string collection = kind.Collection;
                int baselineCount = _lastWhole.Keys.Count(key => key.Kind == collection);
                AddDigestEntry(
                    digest,
                    collection.ToLowerInvariant(),
                    galaxy,
                    collection,
                    kind.IdField,
                    isHost,
                    id => _lastWhole.ContainsKey((collection, id)),
                    baselineCount);
            }

            AddFleetDigestEntry(digest, galaxy, isHost);
        }

        return digest;
    }

    private static void AddDigestEntry(
        SyncDigest digest,
        string name,
        object galaxy,
        string listFieldName,
        string idFieldName,
        bool isHost,
        Func<long, bool> inBaseline,
        int baselineCount)
    {
        object list = ListField(galaxy, listFieldName);
        MethodInfo item = Indexer(list);
        int total = CountOf(list);

        int members = 0;
        int knownToBaseline = 0;
        ulong setHash = SyncDigest.EmptySetHash;
        FieldInfo idField = null;

        for (int index = 0; index < total && item is not null; index++)
        {
            object element = At(item, list, index);
            if (element is null)
            {
                continue;
            }

            idField ??= AccessTools.Field(element.GetType(), idFieldName);
            if (idField is null)
            {
                break;
            }

            long id;
            try
            {
                id = Convert.ToInt64(idField.GetValue(element), CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                continue;
            }

            members++;
            setHash = SyncDigest.Fold(setHash, id);

            if (isHost && inBaseline(id))
            {
                knownToBaseline++;
            }
        }

        int unsent = 0;
        if (isHost)
        {
            int createdSinceDelta = members - knownToBaseline;
            int removedSinceDelta = baselineCount - knownToBaseline;
            unsent = Math.Max(0, createdSinceDelta) + Math.Max(0, removedSinceDelta);
        }

        digest.Add(name, members, setHash, unsent);
    }

    private static void AddFleetDigestEntry(SyncDigest digest, object galaxy, bool isHost)
    {
        object empires = ListField(galaxy, "Empires");
        MethodInfo empireItem = Indexer(empires);
        int empireCount = CountOf(empires);

        int members = 0;
        int knownToBaseline = 0;
        ulong setHash = SyncDigest.EmptySetHash;

        for (int empireIndex = 0; empireIndex < empireCount && empireItem is not null; empireIndex++)
        {
            object empire = At(empireItem, empires, empireIndex);
            if (empire is null)
            {
                continue;
            }

            object empireIdValue = AccessTools.Field(empire.GetType(), "EmpireId")?.GetValue(empire);
            object fleets = AccessTools.Field(empire.GetType(), "Fleets")?.GetValue(empire);
            MethodInfo fleetItem = Indexer(fleets);
            int fleetCount = CountOf(fleets);
            if (empireIdValue is null || fleetItem is null)
            {
                continue;
            }

            short empireId = Convert.ToInt16(empireIdValue, CultureInfo.InvariantCulture);

            for (int fleetIndex = 0; fleetIndex < fleetCount; fleetIndex++)
            {
                object fleet = At(fleetItem, fleets, fleetIndex);
                object fleetIdValue = fleet is null ? null : AccessTools.Field(fleet.GetType(), "FleetId")?.GetValue(fleet);
                if (fleetIdValue is null)
                {
                    continue;
                }

                short fleetId = Convert.ToInt16(fleetIdValue, CultureInfo.InvariantCulture);
                long key = PackFleetKey(empireId, fleetId);

                members++;
                setHash = SyncDigest.Fold(setHash, key);

                if (isHost && _lastWhole.ContainsKey(("Fleets", key)))
                {
                    knownToBaseline++;
                }
            }
        }

        int unsent = 0;
        if (isHost)
        {
            int baselineCount = _lastWhole.Keys.Count(key => key.Kind == "Fleets");
            int createdSinceDelta = members - knownToBaseline;
            int removedSinceDelta = baselineCount - knownToBaseline;
            unsent = Math.Max(0, createdSinceDelta) + Math.Max(0, removedSinceDelta);
        }

        digest.Add("fleets", members, setHash, unsent);
    }
}
