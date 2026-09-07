using System.Reflection;
using System.Reflection.Emit;

namespace Dw2Inspect;

/// <summary>
/// The verbs the CLI exposes. Each writes to stdout; diagnostics go to stderr.
/// </summary>
internal sealed class Commands
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private readonly Loader _loader;

    private readonly TextWriter _out;

    public Commands(Loader loader, TextWriter output)
    {
        _loader = loader;
        _out = output;
    }

    /// <summary>List type names, optionally filtered by substring.</summary>
    public void Types(string filter)
    {
        var matches = _loader.AllTypes()
            .Where(t => filter is null || (t.FullName ?? t.Name).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName ?? t.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal);

        int count = 0;
        foreach (var name in matches)
        {
            _out.WriteLine(name);
            count++;
        }

        Log.Warn($"{count} type(s).");
    }

    /// <summary>Search type names AND method names for a substring.</summary>
    public void Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new Dw2Exception("find needs something to search for.");

        foreach (var type in _loader.AllTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            bool typeMatches = (type.FullName ?? type.Name).Contains(query, StringComparison.OrdinalIgnoreCase);

            MethodBase[] members;
            try { members = Members(type).ToArray(); }
            catch { continue; }

            var methodMatches = members
                .Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => "m " + m.Name);

            // Fields matter as much as methods here: enum members are fields, so
            // 'find Multiplayer' has to reach PlayMode.Multiplayer to be useful.
            var fieldMatches = type.GetFields(AllDeclared)
                .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => "f " + f.Name);

            var hits = methodMatches.Concat(fieldMatches).ToArray();

            if (!typeMatches && hits.Length == 0)
                continue;

            _out.WriteLine(type.FullName);

            foreach (var hit in hits)
                _out.WriteLine("    " + hit);
        }
    }

    /// <summary>Methods and fields of a type. No static constructor needed -- this is metadata only.</summary>
    public void MembersOf(string typeName)
    {
        foreach (var type in Resolve(typeName))
        {
            _out.WriteLine();
            _out.WriteLine("===== " + type.FullName + "   [" + type.Assembly.GetName().Name + "]");

            foreach (var m in Members(type).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var args = string.Join(", ", m.GetParameters().Select(p => IlPrinter.Name(p.ParameterType)));
                var returns = m is MethodInfo mi ? IlPrinter.Name(mi.ReturnType) : "void";
                _out.WriteLine($"   m  {returns,-24} {m.Name}({args})");
            }

            foreach (var f in type.GetFields(AllDeclared).OrderBy(f => f.Name, StringComparer.Ordinal))
                _out.WriteLine($"   f  {IlPrinter.Name(f.FieldType),-24} {f.Name}");
        }
    }

    /// <summary>Enum members with their underlying values.</summary>
    public void EnumOf(string typeName)
    {
        foreach (var type in Resolve(typeName))
        {
            if (!type.IsEnum)
            {
                Log.Warn($"{type.FullName} is not an enum; try 'members'.");
                continue;
            }

            _out.WriteLine();
            _out.WriteLine("===== " + type.FullName + " : " + IlPrinter.Name(Enum.GetUnderlyingType(type)));

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object value;
                try { value = f.GetRawConstantValue(); }
                catch { value = "?"; }

                _out.WriteLine($"   {f.Name} = {value}");
            }
        }
    }

    /// <summary>
    /// Every string literal in a type, gathered from ldstr operands. This is the
    /// fastest way to work out what an unfamiliar type actually does -- it is how
    /// NetworkHelper was identified as an HTTP telemetry poster rather than netcode.
    /// </summary>
    public void Strings(string typeName)
    {
        foreach (var type in Resolve(typeName))
        {
            _out.WriteLine();
            _out.WriteLine("===== " + type.FullName);

            // Include nested types: async bodies, iterator bodies and lambdas all
            // compile into generated nested classes, and that is usually where the
            // interesting literals are. NetworkHelper itself holds no strings at all
            // -- its Slitherine URL lives in the generated <SendData>d__0.
            foreach (var owner in TypeAndNested(type))
            {
                _loader.Prepare(owner);

                foreach (var m in Members(owner))
                {
                    byte[] il;
                    try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                    catch { continue; }

                    if (il is null)
                        continue;

                    var label = owner == type ? m.Name : owner.Name + "::" + m.Name;

                    foreach (var literal in Literals(m, il))
                        _out.WriteLine($"   {label,-40} \"{literal}\"");
                }
            }
        }
    }

    /// <summary>A type followed by every nested type beneath it, depth first.</summary>
    private static IEnumerable<Type> TypeAndNested(Type type)
    {
        yield return type;

        Type[] nested;
        try { nested = type.GetNestedTypes(AllDeclared); }
        catch { yield break; }

        foreach (var n in nested)
            foreach (var descendant in TypeAndNested(n))
                yield return descendant;
    }

    private static IEnumerable<string> Literals(MethodBase m, byte[] il)
    {
        // Walking every opcode just to find ldstr would need the full length table;
        // scanning for the ldstr byte and validating the token is cheaper and, because
        // a bogus token simply fails to resolve, safe enough for an orientation tool.
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != (byte)OpCodes.Ldstr.Value)
                continue;

            int token = BitConverter.ToInt32(il, i + 1);

            string s = null;
            try { s = m.Module.ResolveString(token); }
            catch { }

            if (!string.IsNullOrEmpty(s))
                yield return s.Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    /// <summary>
    /// Decompile method bodies to IL. Accepts "Type", "Type::Method", or "Type::*".
    /// </summary>
    public void Il(string spec, bool followStateMachines)
    {
        var parts = spec.Split("::", 2);
        var typeName = parts[0];
        var methodName = parts.Length > 1 ? parts[1] : "*";

        foreach (var type in Resolve(typeName))
        {
            // THE LOAD-BEARING LINE: without this the protector has not restored the
            // bodies and every method reads as the four-byte stub.
            _loader.Prepare(type);

            var selected = Members(type)
                .Where(m => methodName == "*" || m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (selected.Length == 0)
                Log.Warn($"no method named '{methodName}' on {type.FullName}.");

            foreach (var m in selected)
            {
                IlPrinter.Print(m, _out);

                if (followStateMachines)
                    FollowStateMachine(m);
            }
        }
    }

    /// <summary>
    /// An async or iterator method compiles to a stub that just starts a generated
    /// state machine, so dumping it alone shows nothing useful. Follow through to the
    /// MoveNext that holds the real body.
    /// </summary>
    private void FollowStateMachine(MethodBase m)
    {
        var machine = StateMachineType(m);
        if (machine is null)
            return;

        _loader.Prepare(machine);

        var moveNext = machine.GetMethod("MoveNext", AllDeclared);
        if (moveNext is null)
            return;

        _out.WriteLine();
        _out.WriteLine($"   --- following state machine {machine.Name} ---");
        IlPrinter.Print(moveNext, _out);
    }

    private static Type StateMachineType(MethodBase m)
    {
        foreach (var attr in m.GetCustomAttributesData())
        {
            var name = attr.AttributeType.Name;
            if (name != "AsyncStateMachineAttribute" && name != "IteratorStateMachineAttribute")
                continue;

            if (attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is Type t)
                return t;
        }

        return null;
    }

    /// <summary>Assembly references, which is how the absence of a dependency gets proved.</summary>
    public void Refs(string filter)
    {
        foreach (var asm in _loader.Assemblies)
        {
            _out.WriteLine();
            _out.WriteLine("===== " + asm.GetName().Name);

            var names = asm.GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(n => filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var n in names)
                _out.WriteLine("   " + n);
        }
    }

    private static IEnumerable<MethodBase> Members(Type type) =>
        type.GetMethods(AllDeclared)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(AllDeclared));

    private IReadOnlyList<Type> Resolve(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new Dw2Exception("a type name is required.");

        var found = _loader.FindTypes(typeName);

        if (found.Count == 0)
            throw new Dw2Exception($"no type matching '{typeName}'. Try: dw2inspect types {typeName}");

        if (found.Count > 8)
            throw new Dw2Exception($"'{typeName}' matches {found.Count} types; be more specific, " +
                                   $"or list them with: dw2inspect types {typeName}");

        return found;
    }
}
