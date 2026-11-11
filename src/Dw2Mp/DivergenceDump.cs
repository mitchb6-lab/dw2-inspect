using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Dev measurement (mitch, in chat 2026-11-10: "measure first"): write what this side's galaxy
/// holds at a host tick, so the host's file and the client's file for the SAME tick can be
/// diffed field by field. The diff is what ranks which state deltas should carry next.
///
/// Off unless DW2MP_DIVERGENCE_DIR is set. One text file per side per sampled summary:
///
///     host-tick00000300.txt      client-tick00000300.txt
///
/// One line per object, tab separated: Kind, Key, then name=value pairs. The walker is generic
/// on purpose. It records every primitive field, every one-level struct (positions, item ids)
/// and the Count of every collection field, so a divergence nobody thought to look for still
/// shows up. Naming fields by hand would only have measured what was already suspected.
///
/// The comparison itself is pure and lives in src\Shared\DivergenceReport.cs.
/// </summary>
public static class DivergenceDump
{
    private static readonly string DumpDirectory = Environment.GetEnvironmentVariable("DW2MP_DIVERGENCE_DIR") ?? "";

    /// <summary>Sample every Nth summary. A dump walks every ship, so it is not free.</summary>
    private static readonly int SampleEvery = ReadSampleEvery();

    private const int MaxStringLength = 60;

    private const int MaxStructFields = 6;

    public static bool Enabled => DumpDirectory.Length > 0;

    public static Action<string> Log { get; set; }

    private static int _dumpsWritten;

    private static readonly Dictionary<Type, FieldPlan[]> _plans = new();

    private enum FieldShape
    {
        Primitive,
        OneLevelStruct,
        Collection,
    }

    private sealed class FieldPlan
    {
        public FieldInfo Field;
        public FieldShape Shape;
        public FieldInfo[] InnerFields;
        public PropertyInfo CountProperty;
    }

    private static int ReadSampleEvery()
    {
        string text = Environment.GetEnvironmentVariable("DW2MP_DIVERGENCE_EVERY") ?? "";
        bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int every);
        if (!parsed || every < 1)
        {
            return 1;
        }

        return every;
    }

    /// <summary>True when the summary with this 1-based sequence number should be dumped.</summary>
    public static bool ShouldSample(long summaryNumber)
    {
        if (!Enabled)
        {
            return false;
        }

        return summaryNumber % SampleEvery == 0;
    }

    /// <summary>
    /// Write this side's census for one host tick. Never throws: a failed dump is a log line,
    /// not a broken session.
    /// </summary>
    public static void Write(object galaxy, string side, long hostTick, string context)
    {
        if (!Enabled || galaxy is null)
        {
            return;
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Directory.CreateDirectory(DumpDirectory);

            string fileName = string.Format(CultureInfo.InvariantCulture, "{0}-tick{1:D8}.txt", side, hostTick);
            string path = Path.Combine(DumpDirectory, fileName);

            int lineCount = 0;
            using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)))
            {
                writer.WriteLine($"# side={side} hostTick={hostTick} {context}");
                lineCount += WriteGalaxyLine(writer, galaxy);
                lineCount += WriteList(writer, galaxy, "Ships", "Ship", "ShipId", null);
                lineCount += WriteList(writer, galaxy, "Colonies", "Colony", "ColonyId", AppendColonyExtras);
                lineCount += WriteList(writer, galaxy, "Characters", "Character", "CharacterId", null);
                lineCount += WriteList(writer, galaxy, "Designs", "Design", "DesignId", null);
                lineCount += WriteEmpires(writer, galaxy);
            }

            _dumpsWritten++;
            Log?.Invoke($"# divergence: wrote {fileName} ({lineCount:N0} objects, {stopwatch.Elapsed.TotalMilliseconds:N0}ms, dump #{_dumpsWritten})");
        }
        catch (Exception exception)
        {
            var cause = exception.InnerException ?? exception;
            string[] stackLines = (cause.StackTrace ?? "").Split('\n');
            string where = stackLines.Length > 0 ? stackLines[0] : "";
            Log?.Invoke("# divergence: dump failed " + cause.GetType().Name + ": " + cause.Message + " " + where.Trim());
        }
    }

    // ------------------------------------------------------------------ kinds

    private static int WriteGalaxyLine(StreamWriter writer, object galaxy)
    {
        var line = new StringBuilder();
        line.Append("Galaxy\t0");
        AppendFields(line, galaxy);
        writer.WriteLine(line.ToString());
        return 1;
    }

    private static int WriteList(
        StreamWriter writer,
        object owner,
        string listFieldName,
        string kind,
        string idFieldName,
        Action<StringBuilder, object> appendExtras)
    {
        object list = ReadField(owner, listFieldName);
        int written = 0;

        foreach (object element in Elements(list))
        {
            object id = ReadField(element, idFieldName);
            if (id is null)
            {
                continue;
            }

            var line = new StringBuilder();
            line.Append(kind);
            line.Append('\t');
            line.Append(Convert.ToString(id, CultureInfo.InvariantCulture));
            AppendFields(line, element);
            appendExtras?.Invoke(line, element);
            writer.WriteLine(line.ToString());
            written++;
        }

        return written;
    }

    private static int WriteEmpires(StreamWriter writer, object galaxy)
    {
        object empires = ReadField(galaxy, "Empires");
        int written = 0;

        foreach (object empire in Elements(empires))
        {
            object empireId = ReadField(empire, "EmpireId");
            if (empireId is null)
            {
                continue;
            }

            string empireKey = Convert.ToString(empireId, CultureInfo.InvariantCulture);

            var line = new StringBuilder();
            line.Append("Empire\t");
            line.Append(empireKey);
            AppendFields(line, empire);
            AppendEconomy(line, empire);
            AppendResearch(line, empire);
            writer.WriteLine(line.ToString());
            written++;

            written += WriteRelations(writer, empire, empireKey);
            written += WriteFleets(writer, empire, empireKey);
        }

        return written;
    }

    private static int WriteRelations(StreamWriter writer, object empire, string empireKey)
    {
        object relations = ReadField(empire, "Relations");
        int written = 0;

        foreach (object relation in Elements(relations))
        {
            object otherEmpireId = ReadField(relation, "OtherEmpireId");
            object otherColonyOrbId = ReadField(relation, "OtherIndependentColonyOrbId");

            var line = new StringBuilder();
            line.Append("Relation\t");
            line.Append(empireKey);
            line.Append(':');
            line.Append(Convert.ToString(otherEmpireId, CultureInfo.InvariantCulture));
            line.Append(':');
            line.Append(Convert.ToString(otherColonyOrbId, CultureInfo.InvariantCulture));
            AppendFields(line, relation);
            writer.WriteLine(line.ToString());
            written++;
        }

        return written;
    }

    private static int WriteFleets(StreamWriter writer, object empire, string empireKey)
    {
        object fleets = ReadField(empire, "Fleets");
        int written = 0;

        foreach (object fleet in Elements(fleets))
        {
            object fleetId = ReadField(fleet, "FleetId");

            var line = new StringBuilder();
            line.Append("Fleet\t");
            line.Append(empireKey);
            line.Append(':');
            line.Append(Convert.ToString(fleetId, CultureInfo.InvariantCulture));
            AppendFields(line, fleet);
            writer.WriteLine(line.ToString());
            written++;
        }

        return written;
    }

    /// <summary>Per-race population: the nested collection deltas did not carry before 2026-11-10.</summary>
    private static void AppendColonyExtras(StringBuilder line, object colony)
    {
        object populations = ReadField(colony, "Population");
        long total = 0;

        foreach (object population in Elements(populations))
        {
            object raceId = ReadField(population, "_RaceId");
            object amount = ReadField(population, "Amount");
            if (raceId is null || amount is null)
            {
                continue;
            }

            long amountValue = Convert.ToInt64(amount, CultureInfo.InvariantCulture);
            total += amountValue;

            line.Append("\tpop.");
            line.Append(Convert.ToString(raceId, CultureInfo.InvariantCulture));
            line.Append('=');
            line.Append(amountValue.ToString(CultureInfo.InvariantCulture));
        }

        line.Append("\tpop.total=");
        line.Append(total.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendEconomy(StringBuilder line, object empire)
    {
        object economy = ReadField(empire, "Economy");
        if (economy is null)
        {
            return;
        }

        AppendProperty(line, economy, "Money", "econ.Money");
        AppendProperty(line, economy, "PrivateMoney", "econ.PrivateMoney");
    }

    /// <summary>~1,000 projects per empire is too many lines; three totals say whether research moved.</summary>
    private static void AppendResearch(StringBuilder line, object empire)
    {
        object research = ReadField(empire, "Research");
        object projects = ReadField(research, "Projects");
        if (projects is null)
        {
            return;
        }

        int projectCount = 0;
        int researchedCount = 0;
        double progressSum = 0;

        foreach (object project in Elements(projects))
        {
            projectCount++;

            if (ReadField(project, "Researched") is true)
            {
                researchedCount++;
            }

            if (ReadField(project, "Progress") is float progress)
            {
                progressSum += progress;
            }
        }

        line.Append("\tresearch.projects=");
        line.Append(projectCount.ToString(CultureInfo.InvariantCulture));
        line.Append("\tresearch.researched=");
        line.Append(researchedCount.ToString(CultureInfo.InvariantCulture));
        line.Append("\tresearch.progressSum=");
        line.Append(progressSum.ToString("R", CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ the generic walker

    private static void AppendFields(StringBuilder line, object item)
    {
        FieldPlan[] plans = PlanFor(item.GetType());

        foreach (FieldPlan plan in plans)
        {
            // One field that cannot be read must cost that field, not the whole dump: the
            // first run of 2026-11-10 lost every host-side file to a single
            // NotSupportedException and so measured nothing.
            try
            {
                AppendOneField(line, item, plan);
            }
            catch (Exception exception)
            {
                NoteFieldFailure(item.GetType(), plan.Field, exception);
            }
        }
    }

    private static void AppendOneField(StringBuilder line, object item, FieldPlan plan)
    {
        object value = plan.Field.GetValue(item);

        if (plan.Shape == FieldShape.Primitive)
        {
            AppendPair(line, plan.Field.Name, value);
            return;
        }

        if (value is null)
        {
            return;
        }

        if (plan.Shape == FieldShape.OneLevelStruct)
        {
            foreach (FieldInfo innerField in plan.InnerFields)
            {
                object innerValue = innerField.GetValue(value);
                AppendPair(line, plan.Field.Name + "." + innerField.Name, innerValue);
            }

            return;
        }

        object count = plan.CountProperty.GetValue(value);
        AppendPair(line, "#" + plan.Field.Name, count);
    }

    private static readonly HashSet<string> _fieldFailuresSeen = new();

    /// <summary>Say once which field could not be read, and why, then leave it out of every later dump line.</summary>
    private static void NoteFieldFailure(Type ownerType, FieldInfo field, Exception exception)
    {
        var cause = exception.InnerException ?? exception;
        string key = ownerType.Name + "." + field.Name;

        bool isNew;
        lock (_fieldFailuresSeen)
        {
            isNew = _fieldFailuresSeen.Add(key);
        }

        if (isNew)
        {
            Log?.Invoke($"# divergence: field {key} ({field.FieldType.Name}) left out -- {cause.GetType().Name}: {cause.Message}");
        }
    }

    private static void AppendProperty(StringBuilder line, object owner, string propertyName, string label)
    {
        try
        {
            PropertyInfo property = AccessTools.Property(owner.GetType(), propertyName);
            if (property is null)
            {
                return;
            }

            object value = property.GetValue(owner);
            AppendPair(line, label, value);
        }
        catch (Exception)
        {
            // A getter that throws leaves the field out; the report shows it as missing on one side.
        }
    }

    private static void AppendPair(StringBuilder line, string name, object value)
    {
        line.Append('\t');
        line.Append(name);
        line.Append('=');
        line.Append(Format(value));
    }

    private static string Format(object value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is float single)
        {
            return single.ToString("R", CultureInfo.InvariantCulture);
        }

        if (value is double doubleValue)
        {
            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        }

        if (value is DateTime dateTime)
        {
            return "t" + dateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        if (value is TimeSpan timeSpan)
        {
            return "t" + timeSpan.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        if (value is string text)
        {
            string cleaned = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            if (cleaned.Length > MaxStringLength)
            {
                cleaned = cleaned.Substring(0, MaxStringLength);
            }

            return "\"" + cleaned + "\"";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
    }

    private static FieldPlan[] PlanFor(Type type)
    {
        lock (_plans)
        {
            if (_plans.TryGetValue(type, out FieldPlan[] cached))
            {
                return cached;
            }

            var plans = new List<FieldPlan>();
            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (Type current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(Declared))
                {
                    FieldPlan plan = PlanField(field);
                    if (plan is not null)
                    {
                        plans.Add(plan);
                    }
                }
            }

            FieldPlan[] made = plans.OrderBy(plan => plan.Field.Name, StringComparer.Ordinal).ToArray();
            _plans[type] = made;
            return made;
        }
    }

    private static FieldPlan PlanField(FieldInfo field)
    {
        Type fieldType = field.FieldType;

        if (field.Name.Contains("LockObject", StringComparison.Ordinal))
        {
            return null;
        }

        if (IsPrimitiveLike(fieldType))
        {
            return new FieldPlan { Field = field, Shape = FieldShape.Primitive };
        }

        bool isItemWrapper = fieldType.Name.StartsWith("Item`", StringComparison.Ordinal);
        if (fieldType.IsValueType || isItemWrapper)
        {
            const BindingFlags Inner = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo[] innerFields = fieldType.GetFields(Inner)
                .Where(inner => IsPrimitiveLike(inner.FieldType))
                .OrderBy(inner => inner.Name, StringComparer.Ordinal)
                .ToArray();

            if (innerFields.Length == 0 || innerFields.Length > MaxStructFields)
            {
                return null;
            }

            return new FieldPlan { Field = field, Shape = FieldShape.OneLevelStruct, InnerFields = innerFields };
        }

        PropertyInfo countProperty = null;
        try
        {
            countProperty = fieldType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        }
        catch (AmbiguousMatchException)
        {
            countProperty = null;
        }

        if (countProperty is not null && countProperty.PropertyType == typeof(int) && countProperty.GetIndexParameters().Length == 0)
        {
            return new FieldPlan { Field = field, Shape = FieldShape.Collection, CountProperty = countProperty };
        }

        return null;
    }

    private static bool IsPrimitiveLike(Type type)
    {
        if (type.IsPrimitive || type.IsEnum)
        {
            return true;
        }

        return type == typeof(string) || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(decimal);
    }

    // ------------------------------------------------------------------ plumbing

    private static object ReadField(object owner, string name)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            FieldInfo field = AccessTools.Field(owner.GetType(), name);
            return field?.GetValue(owner);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The live members of one of DW2's lists, by Count and indexer. Not foreach: the host's
    /// simulation threads may touch a list while it is walked, and an indexer that returns null
    /// for a tombstone is survivable where a failed enumerator is not.
    /// </summary>
    private static IEnumerable<object> Elements(object list)
    {
        if (list is null)
        {
            yield break;
        }

        Type listType = list.GetType();
        PropertyInfo countProperty = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        MethodInfo indexer = listType.GetMethod("get_Item", new[] { typeof(int) });

        if (countProperty is null || indexer is null)
        {
            yield break;
        }

        int count;
        try
        {
            count = (int)countProperty.GetValue(list);
        }
        catch (Exception)
        {
            yield break;
        }

        var arguments = new object[1];
        for (int index = 0; index < count; index++)
        {
            object element;
            try
            {
                arguments[0] = index;
                element = indexer.Invoke(list, arguments);
            }
            catch (Exception)
            {
                continue;
            }

            if (element is not null)
            {
                yield return element;
            }
        }
    }
}
