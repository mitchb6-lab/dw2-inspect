using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Dw2Inspect;

/// <summary>
/// Prints a method body as readable IL, resolving metadata tokens back to names.
/// </summary>
internal static class IlPrinter
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
                table[op.Value] = op;
        }

        return table;
    }

    public static void Print(MethodBase method, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("===== " + Describe(method));

        MethodBody body;
        try { body = method.GetMethodBody(); }
        catch (Exception ex) { output.WriteLine($"   <body unavailable: {ex.GetType().Name}>"); return; }

        if (body is null)
        {
            output.WriteLine("   <abstract or extern -- no body>");
            return;
        }

        byte[] il = body.GetILAsByteArray();

        if (IsStub(il))
        {
            output.WriteLine("   <stub: 00 00 00 2A -- body not restored; the declaring type static " +
                             "constructor did not run, or threw>");
            return;
        }

        PrintLocals(body, output);
        PrintExceptionClauses(body, output);
        PrintInstructions(method, il, output);
    }

    /// <summary>
    /// The signature of an unrestored body: nop, nop, nop, ret. Distinguishing this
    /// from real code is the whole reason the loader forces static constructors.
    /// </summary>
    private static bool IsStub(byte[] il) =>
        il.Length == 4 && il[0] == 0x00 && il[1] == 0x00 && il[2] == 0x00 && il[3] == 0x2A;

    private static void PrintLocals(MethodBody body, TextWriter output)
    {
        if (body.LocalVariables.Count == 0)
            return;

        var locals = body.LocalVariables
            .OrderBy(l => l.LocalIndex)
            .Select(l => $"{l.LocalIndex}:{Name(l.LocalType)}");

        output.WriteLine("   .locals   " + string.Join(", ", locals));
    }

    private static void PrintExceptionClauses(MethodBody body, TextWriter output)
    {
        IList<ExceptionHandlingClause> clauses;
        try { clauses = body.ExceptionHandlingClauses; }
        catch { return; }

        foreach (var c in clauses)
        {
            var kind = c.Flags switch
            {
                ExceptionHandlingClauseOptions.Clause => "catch " + Name(SafeCatchType(c)),
                ExceptionHandlingClauseOptions.Finally => "finally",
                ExceptionHandlingClauseOptions.Filter => "filter",
                ExceptionHandlingClauseOptions.Fault => "fault",
                _ => c.Flags.ToString(),
            };

            output.WriteLine($"   .try      IL_{c.TryOffset:X4}..IL_{c.TryOffset + c.TryLength:X4} " +
                             $"-> {kind} at IL_{c.HandlerOffset:X4}..IL_{c.HandlerOffset + c.HandlerLength:X4}");
        }
    }

    private static Type SafeCatchType(ExceptionHandlingClause c)
    {
        try { return c.CatchType; } catch { return null; }
    }

    private static void PrintInstructions(MethodBase method, byte[] il, TextWriter output)
    {
        var module = method.Module;

        // Token resolution inside a generic type or method needs that generic context.
        Type[] typeArgs = SafeGenericArgs(method.DeclaringType);
        Type[] methodArgs = method is MethodInfo mi && mi.IsGenericMethodDefinition
            ? mi.GetGenericArguments()
            : null;

        int i = 0;
        while (i < il.Length)
        {
            int offset = i;

            short code = il[i++];
            if (code == 0xFE && i < il.Length)
                code = (short)(0xFE00 | il[i++]);

            if (!OpCodesByValue.TryGetValue(code, out var op))
            {
                output.WriteLine($"   IL_{offset:X4}: <unknown opcode 0x{code:X}>");
                return;
            }

            string operand = ReadOperand(op, il, ref i, module, typeArgs, methodArgs);
            output.WriteLine($"   IL_{offset:X4}: {op.Name,-13} {operand}".TrimEnd());
        }
    }

    private static Type[] SafeGenericArgs(Type t)
    {
        try { return t is { IsGenericType: true } ? t.GetGenericArguments() : null; }
        catch { return null; }
    }

    private static string ReadOperand(OpCode op, byte[] il, ref int i, Module module, Type[] typeArgs, Type[] methodArgs)
    {
        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                return "";

            case OperandType.ShortInlineBrTarget:
            {
                sbyte delta = (sbyte)il[i];
                i += 1;
                return $"IL_{i + delta:X4}";
            }

            case OperandType.InlineBrTarget:
            {
                int delta = BitConverter.ToInt32(il, i);
                i += 4;
                return $"IL_{i + delta:X4}";
            }

            case OperandType.ShortInlineI:
            {
                // ldc.i4.s is signed; the other short-int forms are unsigned indices.
                string v = op == OpCodes.Ldc_I4_S ? ((sbyte)il[i]).ToString() : il[i].ToString();
                i += 1;
                return v;
            }

            case OperandType.InlineI:
            {
                int v = BitConverter.ToInt32(il, i);
                i += 4;
                return v.ToString();
            }

            case OperandType.InlineI8:
            {
                long v = BitConverter.ToInt64(il, i);
                i += 8;
                return v.ToString();
            }

            case OperandType.ShortInlineR:
            {
                float v = BitConverter.ToSingle(il, i);
                i += 4;
                return v.ToString("R");
            }

            case OperandType.InlineR:
            {
                double v = BitConverter.ToDouble(il, i);
                i += 8;
                return v.ToString("R");
            }

            case OperandType.ShortInlineVar:
            {
                byte v = il[i];
                i += 1;
                return v.ToString();
            }

            case OperandType.InlineVar:
            {
                ushort v = BitConverter.ToUInt16(il, i);
                i += 2;
                return v.ToString();
            }

            case OperandType.InlineString:
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                return Resolve(() => "\"" + Escape(module.ResolveString(token)) + "\"");
            }

            case OperandType.InlineField:
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                return Resolve(() =>
                {
                    var f = module.ResolveField(token, typeArgs, methodArgs);
                    return $"{Name(f.DeclaringType)}.{f.Name}";
                });
            }

            case OperandType.InlineMethod:
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                return Resolve(() =>
                {
                    var m = module.ResolveMethod(token, typeArgs, methodArgs);
                    var args = string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType)));
                    return $"{Name(m.DeclaringType)}::{m.Name}({args})";
                });
            }

            case OperandType.InlineType:
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                return Resolve(() => Name(module.ResolveType(token, typeArgs, methodArgs)));
            }

            case OperandType.InlineTok:
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                return Resolve(() =>
                {
                    var member = module.ResolveMember(token, typeArgs, methodArgs);
                    return member is Type t ? Name(t) : $"{Name(member.DeclaringType)}::{member.Name}";
                });
            }

            case OperandType.InlineSig:
            {
                i += 4;
                return "<calli signature>";
            }

            case OperandType.InlineSwitch:
            {
                int count = BitConverter.ToInt32(il, i);
                i += 4;

                var deltas = new int[count];
                for (int n = 0; n < count; n++)
                {
                    deltas[n] = BitConverter.ToInt32(il, i);
                    i += 4;
                }

                // Switch targets are relative to the end of the whole instruction.
                int afterInstruction = i;
                return "(" + string.Join(", ", deltas.Select(d => $"IL_{afterInstruction + d:X4}")) + ")";
            }

            default:
                i += 4;
                return "<operand>";
        }
    }

    private static string Resolve(Func<string> f)
    {
        try { return f(); }
        catch { return "<unresolved token>"; }
    }

    private static string Escape(string s)
    {
        if (s is null) return "";

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '"' => "\\\"",
                _ => ch.ToString(),
            });
        }

        return sb.ToString();
    }

    public static string Name(Type t) => t?.Name ?? "?";

    public static string Describe(MethodBase m)
    {
        var args = string.Join(", ", m.GetParameters().Select(p => $"{Name(p.ParameterType)} {p.Name}"));
        var owner = m.DeclaringType?.FullName ?? "?";
        return $"{owner}::{m.Name}({args})";
    }
}
