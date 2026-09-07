using System.Collections.ObjectModel;
using System.Reflection;

namespace Dw2Inspect;

/// <summary>
/// Renders custom attributes.
///
/// Attribute arguments are METADATA, not IL string literals, so the 'strings'
/// command cannot see them. That distinction is not academic: DW2's whole
/// command-line surface is declared as [Option("ugc-publish", ...)] on properties
/// of DWCommandLineArgs, and is invisible to every other view this tool offers.
///
/// CustomAttributeData is used rather than GetCustomAttributes() deliberately --
/// it reads the metadata without constructing the attribute, so nothing in a
/// protected assembly gets executed just because we looked at it.
/// </summary>
internal static class AttributePrinter
{
    public static string Format(CustomAttributeData attribute)
    {
        string name = attribute.AttributeType.Name;

        // [ObsoleteAttribute] reads better as [Obsolete], matching source syntax.
        if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
            name = name[..^"Attribute".Length];

        var parts = new List<string>();

        foreach (var arg in attribute.ConstructorArguments)
            parts.Add(FormatValue(arg));

        foreach (var named in attribute.NamedArguments)
            parts.Add(named.MemberName + " = " + FormatValue(named.TypedValue));

        return parts.Count == 0 ? $"[{name}]" : $"[{name}({string.Join(", ", parts)})]";
    }

    private static string FormatValue(CustomAttributeTypedArgument arg)
    {
        return arg.Value switch
        {
            null => "null",
            ReadOnlyCollection<CustomAttributeTypedArgument> items =>
                "{ " + string.Join(", ", items.Select(FormatValue)) + " }",
            string s => "\"" + s.Replace("\"", "\\\"") + "\"",
            Type t => "typeof(" + t.Name + ")",
            bool b => b ? "true" : "false",
            _ => arg.Value.ToString(),
        };
    }

    /// <summary>
    /// Attribute lookup can throw when an attribute's own type fails to load, which
    /// is common in obfuscated assemblies. An empty list beats a crash.
    /// </summary>
    public static IList<CustomAttributeData> SafeGet(MemberInfo member)
    {
        try { return member.GetCustomAttributesData(); }
        catch (Exception ex)
        {
            Log.Warn($"could not read attributes on {member.Name}: {ex.GetType().Name}");
            return Array.Empty<CustomAttributeData>();
        }
    }
}
