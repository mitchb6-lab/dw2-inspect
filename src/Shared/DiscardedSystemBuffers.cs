using System;
using System.Collections;
using System.Collections.Generic;

namespace Dw2MpShared;

/// <summary>
/// One galaxy-bound render system field of DW2's DWRendererBase, and what the IL sweep of
/// 2026-11-11 (game 1.3.8.4) concluded about the instance the renderer discards when it is
/// handed a new galaxy.
/// </summary>
public sealed class RenderSystemSlot
{
    /// <summary>The field of DWRendererBase that holds the system, for example "_FlowSystem".</summary>
    public string RendererFieldName;

    /// <summary>The system's type name without namespace, for example "FlowSystem".</summary>
    public string SystemTypeName;

    /// <summary>
    /// True when the sweep proved a replaced instance unreachable: only this renderer field
    /// holds it, every use loads the field again, nothing captures it, and its vertex and index
    /// buffers are its own. Only then may the mod dispose those buffers.
    /// </summary>
    public bool ProvenUnreachable;

    /// <summary>Why the buffers are left alone. Null when <see cref="ProvenUnreachable"/> is true.</summary>
    public string ReasonNotReleased;

    /// <summary>
    /// The system's fields that hold vertex and index buffer bindings: a binding, a dictionary of
    /// bindings, or a dictionary of lists of bindings.
    /// </summary>
    public string[] BufferFieldNames;
}

/// <summary>
/// The eight render systems DWRendererBase.CheckInitializeGraphics re-creates besides the two
/// map overlay systems (docs/multiplayer.md "Renderer release"). The list is data, so that the
/// decision "this one is released, this one is not, and why" is in one place and under test.
/// </summary>
public static class RenderSystemCatalog
{
    public const string NebulaReason =
        "a second holder exists: NebulaCloudSystem.SystemLevelNebulaCloudSystem. DWRendererBase.BindNebulaSystem hands one " +
        "renderer's nebula system to another renderer's, whose Update and DrawStormClouds then use it. A live system can " +
        "therefore still draw a discarded one";

    public const string SymbolReason =
        "closures capture the instance for background work: SymbolSystem.Update queues a thread-pool item and a Task.Run " +
        "whose delegates hold 'this'. As read they touch no buffer (ship visibility, CPU vertex lists, one effect parameter), " +
        "but the instance is not unreachable the moment the field is overwritten";

    private static readonly RenderSystemSlot[] AllSlots = Build();

    public static IReadOnlyList<RenderSystemSlot> Slots
    {
        get
        {
            return AllSlots;
        }
    }

    private static RenderSystemSlot Proven(string rendererFieldName, string systemTypeName, params string[] bufferFieldNames)
    {
        RenderSystemSlot slot = new RenderSystemSlot();
        slot.RendererFieldName = rendererFieldName;
        slot.SystemTypeName = systemTypeName;
        slot.ProvenUnreachable = true;
        slot.ReasonNotReleased = null;
        slot.BufferFieldNames = bufferFieldNames;
        return slot;
    }

    private static RenderSystemSlot NotProven(string rendererFieldName, string systemTypeName, string reason, params string[] bufferFieldNames)
    {
        RenderSystemSlot slot = new RenderSystemSlot();
        slot.RendererFieldName = rendererFieldName;
        slot.SystemTypeName = systemTypeName;
        slot.ProvenUnreachable = false;
        slot.ReasonNotReleased = reason;
        slot.BufferFieldNames = bufferFieldNames;
        return slot;
    }

    private static RenderSystemSlot[] Build()
    {
        List<RenderSystemSlot> slots = new List<RenderSystemSlot>();

        slots.Add(Proven("_GalaxyBackgroundSystem", "GalaxyBackgroundSystem",
            "_PlaneVertices",
            "_PlaneIndices"));

        slots.Add(Proven("_TerritoryMapSystem", "TerritoryMapSystem",
            "EmpireVertices",
            "EmpireIndices",
            "PirateEmpireVertices",
            "PirateEmpireIndices",
            "EmpireVerticesBorder",
            "EmpireIndicesBorder",
            "PirateEmpireVerticesBorder",
            "PirateEmpireIndicesBorder",
            "FakeVertices",
            "FakeIndices"));

        slots.Add(Proven("_FlowSystem", "FlowSystem",
            "_Vertices",
            "_Indices"));

        slots.Add(Proven("_TravelVectorSystem", "TravelVectorSystem",
            "_Vertices",
            "_Indices",
            "_VerticesSelected",
            "_IndicesSelected",
            "_VerticesEnemyFleets",
            "_IndicesEnemyFleets"));

        slots.Add(Proven("_FleetOverlaySystem", "FleetOverlaySystem",
            "_VerticesCircleFill",
            "_IndicesCircleFill",
            "_VerticesCirclePerimeter",
            "_VerticesLine",
            "_IndicesLine"));

        slots.Add(Proven("_AttackVectorSystem", "AttackVectorSystem",
            "_VerticesTarget",
            "_IndicesTarget",
            "_VerticesAttackers",
            "_IndicesAttackers"));

        slots.Add(NotProven("_NebulaCloudSystem", "NebulaCloudSystem", NebulaReason,
            "_NebulaCloudVertices",
            "_NebulaCloudIndices",
            "_EffectCloudVertices",
            "_EffectCloudIndices",
            "_StormCloudVertices",
            "_StormCloudIndices"));

        slots.Add(NotProven("_SymbolSystem", "SymbolSystem", SymbolReason,
            "_Vertices",
            "_Indices"));

        return slots.ToArray();
    }
}

/// <summary>
/// The vertex and index buffers one discarded render system owned, as the waiting list's
/// resource. It holds the buffers and the system's type name, never the system: the system
/// still points at the galaxy it was built for.
/// </summary>
public sealed class DiscardedBufferSet
{
    public string SystemTypeName;

    public object[] Buffers;

    /// <summary>The buffers' sizes added up when they were collected, for the log.</summary>
    public long TotalBytes;
}

/// <summary>
/// The pure half of releasing a discarded render system's buffers: finding the bindings inside
/// a field's value, and deciding which of the collected buffers may be disposed. Knows nothing
/// about Stride, Harmony or DW2. Tested.
/// </summary>
public static class DiscardedSystemBuffers
{
    /// <summary>Bindings read from one discarded system at most. More than this and the rest is left to DW2 (leaked, which is safe).</summary>
    public const int MaximumBindingsPerSystem = 4096;

    /// <summary>A dictionary of lists of bindings is two containers deep. Nothing in DW2 nests deeper.</summary>
    public const int MaximumContainerDepth = 3;

    /// <summary>
    /// Adds to <paramref name="bindings"/> every binding found in <paramref name="fieldValue"/>:
    /// the value itself, the values of a dictionary, the items of a list, to
    /// <see cref="MaximumContainerDepth"/> containers. Returns false when it stopped early
    /// because <see cref="MaximumBindingsPerSystem"/> was reached.
    /// </summary>
    public static bool CollectBindings(object fieldValue, Func<object, bool> isBinding, List<object> bindings)
    {
        return CollectBindings(fieldValue, isBinding, bindings, 0);
    }

    private static bool CollectBindings(object value, Func<object, bool> isBinding, List<object> bindings, int depth)
    {
        if (value is null)
        {
            return true;
        }

        if (isBinding(value))
        {
            if (bindings.Count >= MaximumBindingsPerSystem)
            {
                return false;
            }

            bindings.Add(value);
            return true;
        }

        if (value is string)
        {
            return true;
        }

        if (depth >= MaximumContainerDepth)
        {
            return true;
        }

        IEnumerable items = null;
        if (value is IDictionary dictionary)
        {
            items = dictionary.Values;
        }
        else if (value is IEnumerable enumerable)
        {
            items = enumerable;
        }

        if (items is null)
        {
            return true;
        }

        foreach (object item in items)
        {
            bool roomLeft = CollectBindings(item, isBinding, bindings, depth + 1);
            if (!roomLeft)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="fieldType"/> is one of <paramref name="bindingTypes"/> or a
    /// generic type built from one, however deep: Dictionary&lt;short, List&lt;Binding&gt;&gt;
    /// mentions Binding. Used to check, before anything is read, that a field named in the
    /// catalog still is what the sweep saw.
    /// </summary>
    public static bool TypeMentions(Type fieldType, IReadOnlyList<Type> bindingTypes)
    {
        if (fieldType is null)
        {
            return false;
        }

        foreach (Type bindingType in bindingTypes)
        {
            if (fieldType == bindingType)
            {
                return true;
            }
        }

        if (!fieldType.IsGenericType)
        {
            return false;
        }

        foreach (Type argument in fieldType.GetGenericArguments())
        {
            if (TypeMentions(argument, bindingTypes))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What <see cref="Plan"/> decided about one discarded system's buffers.</summary>
    public sealed class ReleasePlan
    {
        /// <summary>The buffers to dispose, each once.</summary>
        public readonly List<object> ToDispose = new List<object>();

        public long BytesToDispose;

        /// <summary>The same buffer named by two bindings: disposed once, counted here for the second.</summary>
        public int Duplicates;

        /// <summary>A buffer with no size was never created on the graphics device (DW2 uses "new Buffer()" as a placeholder).</summary>
        public int NeverCreated;

        public int AlreadyDisposed;

        /// <summary>A buffer a system that is in a renderer field also holds. Never disposed.</summary>
        public int HeldByLiveSystem;
    }

    /// <summary>
    /// Decides which of a discarded system's buffers may be disposed. Buffers are compared by
    /// reference, never by equality.
    /// </summary>
    public static ReleasePlan Plan(
        IEnumerable<object> buffers,
        IEnumerable<object> buffersOfLiveSystems,
        Func<object, long> sizeInBytes,
        Func<object, bool> isDisposed)
    {
        ReleasePlan plan = new ReleasePlan();

        foreach (object buffer in buffers)
        {
            if (buffer is null)
            {
                continue;
            }

            if (DiscardedResourceQueue.IsAmong(buffer, plan.ToDispose))
            {
                plan.Duplicates++;
                continue;
            }

            if (DiscardedResourceQueue.IsAmong(buffer, buffersOfLiveSystems))
            {
                plan.HeldByLiveSystem++;
                continue;
            }

            if (isDisposed(buffer))
            {
                plan.AlreadyDisposed++;
                continue;
            }

            long bytes = sizeInBytes(buffer);
            if (bytes <= 0)
            {
                plan.NeverCreated++;
                continue;
            }

            plan.ToDispose.Add(buffer);
            plan.BytesToDispose += bytes;
        }

        return plan;
    }

    /// <summary>
    /// How many discarded systems may wait at once. Six released systems for each of up to four
    /// renderers is one adoption; an unsolicited adoption comes at most every 30 s against a
    /// 10 s delay, so one adoption's worth is waiting, rarely two.
    /// </summary>
    public const int MaximumPendingSystems = 48;
}
