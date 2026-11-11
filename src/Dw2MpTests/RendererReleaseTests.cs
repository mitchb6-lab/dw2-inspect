using Dw2MpShared;

namespace Dw2MpTests;

/// <summary>
/// Pure tests for the release of the other render systems' buffers (2026-11-11): which systems
/// the catalog lets the mod release and which it does not, how bindings are found inside a
/// field's value, and which of a discarded system's buffers may be disposed. The disposing
/// itself needs the game and is verified by a two-instance run (docs/multiplayer.md "Renderer
/// release").
/// </summary>
public static partial class Program
{
    /// <summary>Stands in for Stride's VertexBufferBinding and IndexBufferBinding.</summary>
    private sealed class FakeBinding
    {
        public object Buffer;
    }

    /// <summary>Stands in for a Stride Buffer. Every two of them are "equal", to show that only the reference counts.</summary>
    private sealed class FakeBuffer
    {
        public long SizeInBytes;
        public bool Disposed;

        public override bool Equals(object other)
        {
            return other is FakeBuffer;
        }

        public override int GetHashCode()
        {
            return 1;
        }
    }

    private static void RunRendererReleaseTests()
    {
        TheCatalogReleasesSixSystemsAndLeavesTwoAlone();
        EveryCatalogEntryIsWellFormed();
        ABindingIsFoundOnItsOwn();
        BindingsAreFoundInADictionary();
        BindingsAreFoundInADictionaryOfLists();
        WhatIsNotABindingIsIgnored();
        CollectingStopsAtTheCeiling();
        CollectingDoesNotDescendForEver();
        AFieldTypeIsCheckedBeforeItIsRead();
        ThePlanDisposesEachBufferOnce();
        ThePlanNeverDisposesWhatALiveSystemHolds();
        ThePlanSkipsDisposedAndNeverCreatedBuffers();
        ThePlanComparesReferencesNotEquality();
        TheSystemQueueHasItsOwnCeiling();
        ABufferSetDoesNotNameItsSystem();
    }

    private static bool IsFakeBinding(object value)
    {
        return value is FakeBinding;
    }

    private static long FakeSize(object buffer)
    {
        FakeBuffer fake = (FakeBuffer)buffer;
        return fake.SizeInBytes;
    }

    private static bool FakeIsDisposed(object buffer)
    {
        FakeBuffer fake = (FakeBuffer)buffer;
        return fake.Disposed;
    }

    private static FakeBuffer NewFakeBuffer(long sizeInBytes)
    {
        FakeBuffer buffer = new FakeBuffer();
        buffer.SizeInBytes = sizeInBytes;
        return buffer;
    }

    private static FakeBinding NewFakeBinding()
    {
        FakeBinding binding = new FakeBinding();
        binding.Buffer = NewFakeBuffer(64);
        return binding;
    }

    private static RenderSystemSlot CatalogSlot(string systemTypeName)
    {
        foreach (RenderSystemSlot slot in RenderSystemCatalog.Slots)
        {
            if (slot.SystemTypeName == systemTypeName)
            {
                return slot;
            }
        }

        return null;
    }

    private static void TheCatalogReleasesSixSystemsAndLeavesTwoAlone()
    {
        int proven = 0;
        int notProven = 0;
        foreach (RenderSystemSlot slot in RenderSystemCatalog.Slots)
        {
            if (slot.ProvenUnreachable)
            {
                proven++;
            }
            else
            {
                notProven++;
            }
        }

        Check("renderer release: the catalog has the eight systems the renderer re-creates besides the overlays",
            RenderSystemCatalog.Slots.Count == 8, RenderSystemCatalog.Slots.Count.ToString());
        Check("renderer release: six of them are proven unreachable", proven == 6, proven.ToString());
        Check("renderer release: two are left alone", notProven == 2, notProven.ToString());

        RenderSystemSlot nebula = CatalogSlot("NebulaCloudSystem");
        RenderSystemSlot symbol = CatalogSlot("SymbolSystem");
        Check("renderer release: the nebula system is NOT released (another live nebula system can draw a discarded one)",
            nebula is not null && !nebula.ProvenUnreachable);
        Check("renderer release: the nebula entry says why", nebula is not null && nebula.ReasonNotReleased == RenderSystemCatalog.NebulaReason);
        Check("renderer release: the symbol system is NOT released (its background delegates capture the instance)",
            symbol is not null && !symbol.ProvenUnreachable);
        Check("renderer release: the symbol entry says why", symbol is not null && symbol.ReasonNotReleased == RenderSystemCatalog.SymbolReason);

        string[] releasedNames = new[]
        {
            "GalaxyBackgroundSystem",
            "TerritoryMapSystem",
            "FlowSystem",
            "TravelVectorSystem",
            "FleetOverlaySystem",
            "AttackVectorSystem",
        };
        foreach (string releasedName in releasedNames)
        {
            RenderSystemSlot slot = CatalogSlot(releasedName);
            Check("renderer release: " + releasedName + " is released", slot is not null && slot.ProvenUnreachable);
        }
    }

    private static void EveryCatalogEntryIsWellFormed()
    {
        HashSet<string> fieldNames = new HashSet<string>();
        bool namesUnique = true;
        bool fieldMatchesType = true;
        bool everyEntryHasBuffers = true;
        bool reasonsConsistent = true;
        bool noOverlayField = true;

        foreach (RenderSystemSlot slot in RenderSystemCatalog.Slots)
        {
            if (!fieldNames.Add(slot.RendererFieldName))
            {
                namesUnique = false;
            }

            if (slot.RendererFieldName != "_" + slot.SystemTypeName)
            {
                fieldMatchesType = false;
            }

            if (slot.BufferFieldNames is null || slot.BufferFieldNames.Length == 0)
            {
                everyEntryHasBuffers = false;
            }

            bool hasReason = !string.IsNullOrEmpty(slot.ReasonNotReleased);
            if (hasReason == slot.ProvenUnreachable)
            {
                reasonsConsistent = false;
            }

            if (slot.RendererFieldName == "_LongRangeScannerSystem" || slot.RendererFieldName == "_ExplorationDiplomacyOverlaySystem")
            {
                noOverlayField = false;
            }
        }

        Check("renderer release: no renderer field is listed twice", namesUnique);
        Check("renderer release: each renderer field is named after its system type", fieldMatchesType);
        Check("renderer release: every entry names its buffer fields", everyEntryHasBuffers);
        Check("renderer release: an entry has a reason exactly when it is not released", reasonsConsistent);
        Check("renderer release: the two overlay fields stay with the overlay release", noOverlayField);
    }

    private static void ABindingIsFoundOnItsOwn()
    {
        FakeBinding binding = NewFakeBinding();
        List<object> found = new List<object>();

        bool roomLeft = DiscardedSystemBuffers.CollectBindings(binding, IsFakeBinding, found);
        bool nullIsFine = DiscardedSystemBuffers.CollectBindings(null, IsFakeBinding, found);

        Check("renderer release: a binding field's value is collected", roomLeft && found.Count == 1 && ReferenceEquals(found[0], binding));
        Check("renderer release: a null field collects nothing", nullIsFine && found.Count == 1);
    }

    private static void BindingsAreFoundInADictionary()
    {
        Dictionary<int, FakeBinding> byRole = new Dictionary<int, FakeBinding>();
        byRole[1] = NewFakeBinding();
        byRole[2] = NewFakeBinding();
        List<object> found = new List<object>();

        DiscardedSystemBuffers.CollectBindings(byRole, IsFakeBinding, found);

        Check("renderer release: the values of a dictionary of bindings are collected", found.Count == 2, found.Count.ToString());
    }

    private static void BindingsAreFoundInADictionaryOfLists()
    {
        Dictionary<short, List<FakeBinding>> byEmpire = new Dictionary<short, List<FakeBinding>>();
        List<FakeBinding> first = new List<FakeBinding>();
        first.Add(NewFakeBinding());
        first.Add(NewFakeBinding());
        List<FakeBinding> second = new List<FakeBinding>();
        second.Add(NewFakeBinding());
        byEmpire[1] = first;
        byEmpire[2] = second;
        byEmpire[3] = null;
        List<object> found = new List<object>();

        DiscardedSystemBuffers.CollectBindings(byEmpire, IsFakeBinding, found);

        Check("renderer release: a dictionary of lists of bindings gives every binding", found.Count == 3, found.Count.ToString());
    }

    private static void WhatIsNotABindingIsIgnored()
    {
        List<object> mixed = new List<object>();
        mixed.Add("a string is not walked letter by letter");
        mixed.Add(42);
        mixed.Add(NewFakeBinding());
        mixed.Add(new object());
        List<object> found = new List<object>();

        bool roomLeft = DiscardedSystemBuffers.CollectBindings(mixed, IsFakeBinding, found);

        Check("renderer release: only bindings are collected from a mixed list", roomLeft && found.Count == 1, found.Count.ToString());
    }

    private static void CollectingStopsAtTheCeiling()
    {
        List<FakeBinding> tooMany = new List<FakeBinding>();
        for (int index = 0; index < DiscardedSystemBuffers.MaximumBindingsPerSystem + 5; index++)
        {
            tooMany.Add(NewFakeBinding());
        }

        List<object> found = new List<object>();
        bool roomLeft = DiscardedSystemBuffers.CollectBindings(tooMany, IsFakeBinding, found);

        Check("renderer release: collecting stops at the ceiling and says so",
            !roomLeft && found.Count == DiscardedSystemBuffers.MaximumBindingsPerSystem, found.Count.ToString());
    }

    private static void CollectingDoesNotDescendForEver()
    {
        // A list in a list in a list in a list: one container deeper than anything DW2 has.
        List<object> level4 = new List<object>();
        level4.Add(NewFakeBinding());
        List<object> level3 = new List<object>();
        level3.Add(level4);
        level3.Add(NewFakeBinding());
        List<object> level2 = new List<object>();
        level2.Add(level3);
        List<object> level1 = new List<object>();
        level1.Add(level2);
        List<object> found = new List<object>();

        DiscardedSystemBuffers.CollectBindings(level1, IsFakeBinding, found);

        Check("renderer release: bindings three containers deep are found, a fourth container is not opened", found.Count == 1, found.Count.ToString());
    }

    private static void AFieldTypeIsCheckedBeforeItIsRead()
    {
        Type[] bindingTypes = new[] { typeof(FakeBinding) };

        bool direct = DiscardedSystemBuffers.TypeMentions(typeof(FakeBinding), bindingTypes);
        bool inDictionary = DiscardedSystemBuffers.TypeMentions(typeof(Dictionary<int, FakeBinding>), bindingTypes);
        bool inDictionaryOfLists = DiscardedSystemBuffers.TypeMentions(typeof(Dictionary<short, List<FakeBinding>>), bindingTypes);
        bool unrelated = DiscardedSystemBuffers.TypeMentions(typeof(Dictionary<short, List<int>>), bindingTypes);
        bool plainObject = DiscardedSystemBuffers.TypeMentions(typeof(object), bindingTypes);
        bool nullType = DiscardedSystemBuffers.TypeMentions(null, bindingTypes);

        Check("renderer release: a binding field's type is recognised", direct);
        Check("renderer release: a dictionary of bindings is recognised", inDictionary);
        Check("renderer release: a dictionary of lists of bindings is recognised", inDictionaryOfLists);
        Check("renderer release: a field of something else is refused", !unrelated && !plainObject && !nullType);
    }

    private static void ThePlanDisposesEachBufferOnce()
    {
        FakeBuffer vertices = NewFakeBuffer(1000);
        FakeBuffer indices = NewFakeBuffer(24);
        List<object> buffers = new List<object>();
        buffers.Add(vertices);
        buffers.Add(indices);
        buffers.Add(vertices);
        buffers.Add(null);

        DiscardedSystemBuffers.ReleasePlan plan = DiscardedSystemBuffers.Plan(buffers, new List<object>(), FakeSize, FakeIsDisposed);

        Check("renderer release: each buffer is disposed once", plan.ToDispose.Count == 2, plan.ToDispose.Count.ToString());
        Check("renderer release: a buffer named twice is counted, not disposed twice", plan.Duplicates == 1, plan.Duplicates.ToString());
        Check("renderer release: the plan adds up the bytes", plan.BytesToDispose == 1024, plan.BytesToDispose.ToString());
    }

    private static void ThePlanNeverDisposesWhatALiveSystemHolds()
    {
        FakeBuffer shared = NewFakeBuffer(500);
        FakeBuffer own = NewFakeBuffer(500);
        List<object> buffers = new List<object>();
        buffers.Add(shared);
        buffers.Add(own);
        List<object> buffersOfLiveSystems = new List<object>();
        buffersOfLiveSystems.Add(shared);

        DiscardedSystemBuffers.ReleasePlan plan = DiscardedSystemBuffers.Plan(buffers, buffersOfLiveSystems, FakeSize, FakeIsDisposed);

        bool onlyOwn = plan.ToDispose.Count == 1 && ReferenceEquals(plan.ToDispose[0], own);
        Check("renderer release: a buffer a live system also holds is never disposed", onlyOwn && plan.HeldByLiveSystem == 1);
    }

    private static void ThePlanSkipsDisposedAndNeverCreatedBuffers()
    {
        FakeBuffer disposed = NewFakeBuffer(500);
        disposed.Disposed = true;
        FakeBuffer placeholder = NewFakeBuffer(0);
        FakeBuffer real = NewFakeBuffer(500);
        List<object> buffers = new List<object>();
        buffers.Add(disposed);
        buffers.Add(placeholder);
        buffers.Add(real);

        DiscardedSystemBuffers.ReleasePlan plan = DiscardedSystemBuffers.Plan(buffers, new List<object>(), FakeSize, FakeIsDisposed);

        bool onlyReal = plan.ToDispose.Count == 1 && ReferenceEquals(plan.ToDispose[0], real);
        Check("renderer release: a disposed buffer is not disposed again", onlyReal && plan.AlreadyDisposed == 1);
        Check("renderer release: a placeholder buffer with no size is left alone", onlyReal && plan.NeverCreated == 1);
    }

    private static void ThePlanComparesReferencesNotEquality()
    {
        // FakeBuffer.Equals says every two are equal. They are still two buffers.
        FakeBuffer first = NewFakeBuffer(10);
        FakeBuffer second = NewFakeBuffer(10);
        FakeBuffer liveLookalike = NewFakeBuffer(10);
        List<object> buffers = new List<object>();
        buffers.Add(first);
        buffers.Add(second);
        List<object> buffersOfLiveSystems = new List<object>();
        buffersOfLiveSystems.Add(liveLookalike);

        DiscardedSystemBuffers.ReleasePlan plan = DiscardedSystemBuffers.Plan(buffers, buffersOfLiveSystems, FakeSize, FakeIsDisposed);

        Check("renderer release: two equal-looking buffers are two buffers", plan.ToDispose.Count == 2 && plan.Duplicates == 0 && plan.HeldByLiveSystem == 0);
    }

    private static void TheSystemQueueHasItsOwnCeiling()
    {
        DiscardedResourceQueue queue = new DiscardedResourceQueue(
            DiscardedResourceQueue.DefaultDelaySeconds,
            DiscardedResourceQueue.DefaultMinimumUpdates,
            DiscardedSystemBuffers.MaximumPendingSystems);

        int queued = 0;
        for (int index = 0; index < DiscardedSystemBuffers.MaximumPendingSystems + 3; index++)
        {
            DiscardedBufferSet bufferSet = new DiscardedBufferSet();
            bufferSet.SystemTypeName = "FlowSystem";
            bufferSet.Buffers = new object[] { NewFakeBuffer(8) };
            if (queue.NoteReplacement("_FlowSystem", new object(), new object(), bufferSet, 0.0))
            {
                queued++;
            }
        }

        Check("renderer release: the system queue takes one adoption's systems with room to spare",
            DiscardedSystemBuffers.MaximumPendingSystems >= 24 && DiscardedSystemBuffers.MaximumPendingSystems <= 64,
            DiscardedSystemBuffers.MaximumPendingSystems.ToString());
        Check("renderer release: past its ceiling the system queue counts instead of growing",
            queued == DiscardedSystemBuffers.MaximumPendingSystems && queue.DroppedQueueFullCount == 3,
            queued + " queued, " + queue.DroppedQueueFullCount + " dropped");
        Check("renderer release: the overlay queue's ceiling is untouched", DiscardedResourceQueue.DefaultMaximumPending == 8);
    }

    private static void ABufferSetDoesNotNameItsSystem()
    {
        // The queue holds a buffer set strongly. If the set could hold the discarded system,
        // it would hold that system's galaxy for the length of the delay.
        bool onlyPlainFields = true;
        foreach (System.Reflection.FieldInfo field in typeof(DiscardedBufferSet).GetFields())
        {
            bool allowed = field.FieldType == typeof(string)
                || field.FieldType == typeof(object[])
                || field.FieldType == typeof(long);
            if (!allowed)
            {
                onlyPlainFields = false;
            }
        }

        Check("renderer release: a buffer set has a name, its buffers and a byte count, and no field for the system", onlyPlainFields);
    }
}
