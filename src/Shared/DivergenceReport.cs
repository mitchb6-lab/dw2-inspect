using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Dw2MpShared;

/// <summary>One field that differs between the host's dump and the client's, across how many objects.</summary>
public sealed class DivergentField
{
    public string Kind { get; set; } = "";

    public string Field { get; set; } = "";

    /// <summary>Objects both sides have whose value for this field differs.</summary>
    public int ObjectsDiffering { get; set; }

    public string ExampleKey { get; set; } = "";

    public string ExampleHost { get; set; } = "";

    public string ExampleClient { get; set; } = "";
}

/// <summary>How one kind of object (Ship, Colony, Relation ...) compares between the two dumps.</summary>
public sealed class DivergentKind
{
    public string Kind { get; set; } = "";

    public int HostObjects { get; set; }

    public int ClientObjects { get; set; }

    public int OnlyOnHost { get; set; }

    public int OnlyOnClient { get; set; }

    public int CommonObjects { get; set; }

    /// <summary>Common objects with at least one differing field.</summary>
    public int CommonObjectsDiffering { get; set; }

    public List<DivergentField> Fields { get; } = new();
}

/// <summary>
/// The pure half of the 2026-11-10 "measure first" step: compare the host's census of a galaxy
/// with the client's census for the same host tick, and rank what differs.
///
/// A dump is text, one object per line, tab separated: Kind, Key, then name=value pairs (see
/// src\Dw2Mp\DivergenceDump.cs, which writes them inside the game). Lines starting with # are
/// comments. Numbers compare with a small relative tolerance, because a float that crossed the
/// wire as a thresholded delta is allowed to be a rounding error away; everything else compares
/// as text. No game types here: this file is compiled into the probe and into src\Dw2MpTests.
/// </summary>
public static class DivergenceReport
{
    /// <summary>Two numbers closer than this, relative to the larger, count as equal.</summary>
    public const double DefaultRelativeTolerance = 0.001;

    public static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Parse(string dumpText)
    {
        var kinds = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(dumpText))
        {
            return kinds;
        }

        string[] lines = dumpText.Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            string kind = parts[0];
            string key = parts[1];

            if (!kinds.TryGetValue(kind, out var objects))
            {
                objects = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                kinds[kind] = objects;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 2; index < parts.Length; index++)
            {
                string pair = parts[index];
                int separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string name = pair.Substring(0, separator);
                string value = pair.Substring(separator + 1);
                fields[name] = value;
            }

            objects[key] = fields;
        }

        return kinds;
    }

    public static List<DivergentKind> Compare(string hostDump, string clientDump, double relativeTolerance)
    {
        var host = Parse(hostDump);
        var client = Parse(clientDump);

        var kindNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in host.Keys)
        {
            kindNames.Add(name);
        }

        foreach (string name in client.Keys)
        {
            kindNames.Add(name);
        }

        var results = new List<DivergentKind>();

        foreach (string kindName in kindNames)
        {
            host.TryGetValue(kindName, out var hostObjects);
            client.TryGetValue(kindName, out var clientObjects);
            hostObjects ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            clientObjects ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            results.Add(CompareKind(kindName, hostObjects, clientObjects, relativeTolerance));
        }

        return results;
    }

    private static DivergentKind CompareKind(
        string kindName,
        Dictionary<string, Dictionary<string, string>> hostObjects,
        Dictionary<string, Dictionary<string, string>> clientObjects,
        double relativeTolerance)
    {
        var result = new DivergentKind
        {
            Kind = kindName,
            HostObjects = hostObjects.Count,
            ClientObjects = clientObjects.Count,
        };

        var fieldsByName = new Dictionary<string, DivergentField>(StringComparer.Ordinal);

        foreach (var hostEntry in hostObjects)
        {
            if (!clientObjects.TryGetValue(hostEntry.Key, out var clientFields))
            {
                result.OnlyOnHost++;
                continue;
            }

            result.CommonObjects++;
            bool objectDiffers = false;

            var fieldNames = new HashSet<string>(hostEntry.Value.Keys, StringComparer.Ordinal);
            foreach (string name in clientFields.Keys)
            {
                fieldNames.Add(name);
            }

            foreach (string fieldName in fieldNames)
            {
                hostEntry.Value.TryGetValue(fieldName, out string hostValue);
                clientFields.TryGetValue(fieldName, out string clientValue);
                hostValue ??= "(absent)";
                clientValue ??= "(absent)";

                if (ValuesAgree(hostValue, clientValue, relativeTolerance))
                {
                    continue;
                }

                objectDiffers = true;

                if (!fieldsByName.TryGetValue(fieldName, out DivergentField field))
                {
                    field = new DivergentField
                    {
                        Kind = kindName,
                        Field = fieldName,
                        ExampleKey = hostEntry.Key,
                        ExampleHost = hostValue,
                        ExampleClient = clientValue,
                    };
                    fieldsByName[fieldName] = field;
                }

                field.ObjectsDiffering++;
            }

            if (objectDiffers)
            {
                result.CommonObjectsDiffering++;
            }
        }

        foreach (string clientKey in clientObjects.Keys)
        {
            if (!hostObjects.ContainsKey(clientKey))
            {
                result.OnlyOnClient++;
            }
        }

        var ranked = fieldsByName.Values
            .OrderByDescending(field => field.ObjectsDiffering)
            .ThenBy(field => field.Field, StringComparer.Ordinal);
        result.Fields.AddRange(ranked);

        return result;
    }

    /// <summary>Equal as text, or both numbers within the relative tolerance of each other.</summary>
    public static bool ValuesAgree(string hostValue, string clientValue, double relativeTolerance)
    {
        if (string.Equals(hostValue, clientValue, StringComparison.Ordinal))
        {
            return true;
        }

        bool hostIsNumber = double.TryParse(hostValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double hostNumber);
        bool clientIsNumber = double.TryParse(clientValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double clientNumber);
        if (!hostIsNumber || !clientIsNumber)
        {
            return false;
        }

        if (double.IsNaN(hostNumber) || double.IsNaN(clientNumber))
        {
            return double.IsNaN(hostNumber) && double.IsNaN(clientNumber);
        }

        double difference = Math.Abs(hostNumber - clientNumber);
        double scale = Math.Max(Math.Abs(hostNumber), Math.Abs(clientNumber));
        if (scale < 1e-6)
        {
            return difference < 1e-6;
        }

        return difference / scale <= relativeTolerance;
    }

    /// <summary>The report a person reads: per kind, membership first, then the most-divergent fields.</summary>
    public static string Format(List<DivergentKind> kinds, int topFieldsPerKind)
    {
        var text = new StringBuilder();

        foreach (DivergentKind kind in kinds)
        {
            text.Append(kind.Kind);
            text.Append(": host ");
            text.Append(kind.HostObjects.ToString(CultureInfo.InvariantCulture));
            text.Append(", client ");
            text.Append(kind.ClientObjects.ToString(CultureInfo.InvariantCulture));
            text.Append(", only on host ");
            text.Append(kind.OnlyOnHost.ToString(CultureInfo.InvariantCulture));
            text.Append(", only on client ");
            text.Append(kind.OnlyOnClient.ToString(CultureInfo.InvariantCulture));
            text.Append(", common objects differing ");
            text.Append(kind.CommonObjectsDiffering.ToString(CultureInfo.InvariantCulture));
            text.Append(" of ");
            text.Append(kind.CommonObjects.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();

            int shown = 0;
            foreach (DivergentField field in kind.Fields)
            {
                if (shown >= topFieldsPerKind)
                {
                    int remaining = kind.Fields.Count - shown;
                    text.AppendLine($"    ... and {remaining} more field(s)");
                    break;
                }

                text.Append("    ");
                text.Append(field.ObjectsDiffering.ToString(CultureInfo.InvariantCulture).PadLeft(6));
                text.Append("  ");
                text.Append(field.Field);
                text.Append("   e.g. ");
                text.Append(field.ExampleKey);
                text.Append(": host ");
                text.Append(field.ExampleHost);
                text.Append(" / client ");
                text.Append(field.ExampleClient);
                text.AppendLine();
                shown++;
            }
        }

        return text.ToString();
    }
}
