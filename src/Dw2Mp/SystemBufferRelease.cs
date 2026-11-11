using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Releases the vertex and index buffers of the render systems DW2 discards every time its
/// renderer is handed a new galaxy (2026-11-11), next to <see cref="OverlayRelease"/>, which
/// releases the two map overlay render targets. Same Harmony patch, same waiting list class,
/// same delay (ten seconds and sixty main-thread updates), same main-thread dispose, same
/// game-build gate and same off switch: this class is only called by OverlayRelease.
///
/// WHICH SYSTEMS (read in DW2's IL with a sweep of all 1,904 types of the four DistantWorlds
/// assemblies, game 1.3.8.4; the list and the reasons are data in RenderSystemCatalog):
///
///   RELEASED  GalaxyBackgroundSystem, TerritoryMapSystem, FlowSystem, TravelVectorSystem,
///             FleetOverlaySystem, AttackVectorSystem. For each: the DWRendererBase field is
///             the only field of that type an instance is ever stored in (newobj, then stfld,
///             in CheckInitializeGraphics); every reader loads the field again for each use
///             (the Update... methods, CustomOrbRenderer's two draw methods, the four property
///             getters, whose callers use the result at once or keep it in a local of a plain
///             method); no closure, event or static holds one; and every buffer in its binding
///             fields was created for that instance (Buffer.Vertex.New / Buffer.New in
///             EffectHelper.UpdateBuffers and GeometryPrimitiveHelper.CreateBuffersFromVertices,
///             neither of which keeps what it creates).
///
///   LEFT ALONE  NebulaCloudSystem: another live nebula system can hold and draw a discarded
///             one (SystemLevelNebulaCloudSystem). SymbolSystem: its background delegates
///             capture the instance. See RenderSystemCatalog for the full reasons.
///
/// WHAT IS RELEASED: vertex and index buffers only. NOT the effect instances (two of them are
/// statics shared by every instance: EffectHelper._GalaxyBackgroundEffect and _BasicColorEffect),
/// NOT the pipeline states (Stride's PipelineState.New hands out objects from a cache on the
/// graphics device, so two instances can hold the same one), NOT the textures (they belong to
/// ScaledRenderer and are handed to every new instance).
///
/// THE SECOND BARRIER, as in OverlayRelease: the discarded instance gets Enabled = false just
/// before its buffers are disposed. CustomOrbRenderer tests Enabled before every Draw of these
/// six systems. An Update that reached a discarded instance anyway would go through
/// EffectHelper.UpdateBuffers, which replaces a buffer it finds disposed.
///
/// The first exception anywhere is logged once and ends this release for the session, which
/// leaves DW2's own behaviour: the buffers stay allocated.
/// </summary>
public static class SystemBufferRelease
{
    /// <summary>One catalog entry resolved against the game that is running.</summary>
    private sealed class TrackedSlot
    {
        public RenderSystemSlot Slot;
        public FieldInfo RendererField;
        public Type SystemType;
        public FieldInfo EnabledField;
        public FieldInfo[] BufferFields;
    }

    /// <summary>A system that is in a renderer field right now.</summary>
    private sealed class LiveSystem
    {
        public object System;
        public TrackedSlot Tracked;
    }

    /// <summary>A field of some renderer type that holds one of the tracked system types.</summary>
    private sealed class HoldingField
    {
        public FieldInfo Field;
        public TrackedSlot Tracked;
    }

    private const BindingFlags DeclaredInstanceFields =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static readonly object _gate = new();
    private static Action<string> _log;
    private static bool _active;
    private static bool _failureLogged;

    private static TrackedSlot[] _trackedSlots;
    private static Type _vertexBindingType;
    private static Type _indexBindingType;
    private static PropertyInfo _vertexBindingBuffer;
    private static PropertyInfo _indexBindingBuffer;
    private static PropertyInfo _bufferSizeInBytes;
    private static PropertyInfo _bufferIsDisposed;

    private static DiscardedResourceQueue _queue;

    /// <summary>Renderers seen replacing a system. Weak: this list must not keep one alive.</summary>
    private static readonly List<WeakReference> _renderersSeen = new();

    /// <summary>Per renderer type, every field (its own and inherited) that holds a tracked system type.</summary>
    private static readonly Dictionary<Type, List<HoldingField>> _holdingFieldsByRendererType = new();

    private static long _buffersReleased;
    private static long _bytesReleased;

    /// <summary>
    /// Called by OverlayRelease.Install after its own gates (off switch, known game build) have
    /// passed. Resolves the catalog against the running game; a system whose fields are not what
    /// the sweep saw is left alone.
    /// </summary>
    public static void Prepare(Type rendererType, Action<string> log)
    {
        _log = log;

        _vertexBindingType = AccessTools.TypeByName("Stride.Graphics.VertexBufferBinding");
        _indexBindingType = AccessTools.TypeByName("Stride.Graphics.IndexBufferBinding");
        Type bufferType = AccessTools.TypeByName("Stride.Graphics.Buffer");
        if (_vertexBindingType is null || _indexBindingType is null || bufferType is null)
        {
            log("# overlay: render systems: Stride's VertexBufferBinding, IndexBufferBinding or Buffer not found; discarded render system buffers are NOT released");
            return;
        }

        _vertexBindingBuffer = AccessTools.Property(_vertexBindingType, "Buffer");
        _indexBindingBuffer = AccessTools.Property(_indexBindingType, "Buffer");
        _bufferSizeInBytes = AccessTools.Property(bufferType, "SizeInBytes");
        _bufferIsDisposed = AccessTools.Property(bufferType, "IsDisposed");

        bool membersFound = _vertexBindingBuffer is not null
            && _indexBindingBuffer is not null
            && _bufferSizeInBytes is not null
            && _bufferIsDisposed is not null
            && typeof(IDisposable).IsAssignableFrom(bufferType);
        if (!membersFound)
        {
            log("# overlay: render systems: the Buffer members this was written against were not all found; discarded render system buffers are NOT released");
            return;
        }

        Type[] bindingTypes = new[] { _vertexBindingType, _indexBindingType };
        List<TrackedSlot> tracked = new List<TrackedSlot>();
        List<string> releasedNames = new List<string>();
        List<string> leftAloneNames = new List<string>();

        foreach (RenderSystemSlot slot in RenderSystemCatalog.Slots)
        {
            TrackedSlot resolved = Resolve(rendererType, slot, bindingTypes);
            if (resolved is null)
            {
                leftAloneNames.Add(slot.SystemTypeName + " (its fields are not what was read in game 1.3.8.4)");
                continue;
            }

            tracked.Add(resolved);

            if (slot.ProvenUnreachable)
            {
                releasedNames.Add(slot.SystemTypeName);
            }
            else
            {
                leftAloneNames.Add(slot.SystemTypeName + " (not proven unreachable)");
            }
        }

        _trackedSlots = tracked.ToArray();

        _queue = new DiscardedResourceQueue(
            DiscardedResourceQueue.DefaultDelaySeconds,
            DiscardedResourceQueue.DefaultMinimumUpdates,
            DiscardedSystemBuffers.MaximumPendingSystems);

        _active = releasedNames.Count > 0;

        string releasedText = string.Join(", ", releasedNames);
        string leftAloneText = string.Join(", ", leftAloneNames);
        log($"# overlay: render systems: the vertex and index buffers of a replaced {releasedText} are released with the render targets; " +
            $"left alone: {leftAloneText}");
    }

    /// <summary>The catalog entry with its reflection handles, or null when the running game does not match it.</summary>
    private static TrackedSlot Resolve(Type rendererType, RenderSystemSlot slot, Type[] bindingTypes)
    {
        FieldInfo rendererField = AccessTools.Field(rendererType, slot.RendererFieldName);
        if (rendererField is null)
        {
            return null;
        }

        Type systemType = rendererField.FieldType;
        if (systemType.Name != slot.SystemTypeName)
        {
            return null;
        }

        FieldInfo enabledField = AccessTools.Field(systemType, "Enabled");
        if (enabledField is null || enabledField.FieldType != typeof(bool))
        {
            return null;
        }

        List<FieldInfo> bufferFields = new List<FieldInfo>();
        foreach (string bufferFieldName in slot.BufferFieldNames)
        {
            FieldInfo bufferField = AccessTools.Field(systemType, bufferFieldName);
            if (bufferField is null)
            {
                return null;
            }

            if (!DiscardedSystemBuffers.TypeMentions(bufferField.FieldType, bindingTypes))
            {
                return null;
            }

            bufferFields.Add(bufferField);
        }

        TrackedSlot tracked = new TrackedSlot();
        tracked.Slot = slot;
        tracked.RendererField = rendererField;
        tracked.SystemType = systemType;
        tracked.EnabledField = enabledField;
        tracked.BufferFields = bufferFields.ToArray();
        return tracked;
    }

    private static double NowSeconds()
    {
        return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    /// <summary>The first failure is logged and ends this release for the session; later ones are silent.</summary>
    private static void StopAfterFailure(string where, Exception exception)
    {
        _active = false;

        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        _log($"# overlay: *** FAILED (render system buffers) in {where}: {exception.GetType().Name}: {exception.Message} -- " +
             "no more discarded render system buffers are released this session (DW2's own behaviour: they stay allocated)");
    }

    // ------------------------------------------------------------------ noticing a replacement

    /// <summary>
    /// From the CheckInitializeGraphics prefix. Returns what each tracked field holds before DW2's
    /// own code runs, or null when nothing needs remembering: DW2 replaces a system that exists
    /// only on a forced reinitialisation, so an ordinary frame costs one comparison here.
    /// </summary>
    public static object[] CaptureBefore(object renderer, bool forceReinitialization)
    {
        if (!_active || !forceReinitialization)
        {
            return null;
        }

        try
        {
            object[] systemsBefore = new object[_trackedSlots.Length];
            for (int index = 0; index < _trackedSlots.Length; index++)
            {
                systemsBefore[index] = _trackedSlots[index].RendererField.GetValue(renderer);
            }

            return systemsBefore;
        }
        catch (Exception exception)
        {
            StopAfterFailure("the CheckInitializeGraphics prefix", exception);
            return null;
        }
    }

    /// <summary>From the CheckInitializeGraphics postfix: a tracked field that holds a different instance now has discarded the old one.</summary>
    public static void NoteAfter(object renderer, object[] systemsBefore)
    {
        if (!_active || systemsBefore is null)
        {
            return;
        }

        try
        {
            NoteReplacements(renderer, systemsBefore);
        }
        catch (Exception exception)
        {
            StopAfterFailure("the CheckInitializeGraphics postfix", exception);
        }
    }

    private static void NoteReplacements(object renderer, object[] systemsBefore)
    {
        RememberRenderer(renderer);

        double nowSeconds = NowSeconds();
        int queued = 0;
        StringBuilder queuedText = new StringBuilder();
        StringBuilder leftAloneText = new StringBuilder();

        for (int index = 0; index < _trackedSlots.Length; index++)
        {
            TrackedSlot tracked = _trackedSlots[index];
            object systemBefore = systemsBefore[index];
            object systemNow = tracked.RendererField.GetValue(renderer);

            if (systemBefore is null || ReferenceEquals(systemBefore, systemNow))
            {
                continue;
            }

            if (!tracked.Slot.ProvenUnreachable)
            {
                // Never queued. Its buffers are only measured, and only for the developer probe,
                // so that a run can say how much DW2 still keeps per adoption and in which system.
                if (AdoptionMemory.ProbeActive)
                {
                    DiscardedBufferSet measured = CollectBufferSet(tracked, systemBefore);
                    AppendSystemText(leftAloneText, measured);
                }

                continue;
            }

            // The buffers are read now, while this frame still holds the old instance: the
            // queue keeps the buffers and lets go of the instance (and of its galaxy).
            DiscardedBufferSet bufferSet = CollectBufferSet(tracked, systemBefore);
            if (bufferSet.Buffers.Length == 0)
            {
                continue;
            }

            bool wasQueued;
            lock (_gate)
            {
                wasQueued = _queue.NoteReplacement(tracked.Slot.RendererFieldName, systemBefore, systemNow, bufferSet, nowSeconds);
            }

            if (wasQueued)
            {
                queued++;
                AppendSystemText(queuedText, bufferSet);
            }
        }

        if (queued == 0 && leftAloneText.Length == 0)
        {
            return;
        }

        int waiting;
        long droppedQueueFull;
        lock (_gate)
        {
            waiting = _queue.PendingCount;
            droppedQueueFull = _queue.DroppedQueueFullCount;
        }

        string delayText = DiscardedResourceQueue.DefaultDelaySeconds.ToString("0", CultureInfo.InvariantCulture);
        string rendererName = renderer.GetType().Name;
        string message = $"# overlay: render systems: {rendererName} replaced {queued} system(s) with buffers [{queuedText}]; " +
            $"they are released in {delayText} s ({waiting} waiting, {droppedQueueFull} ever dropped because the queue was full)";
        if (leftAloneText.Length > 0)
        {
            message += $"; left alone, not proven unreachable [{leftAloneText}]";
        }

        _log(message);
    }

    private static void AppendSystemText(StringBuilder text, DiscardedBufferSet bufferSet)
    {
        if (text.Length > 0)
        {
            text.Append(", ");
        }

        double kilobytes = bufferSet.TotalBytes / 1024.0;
        string kilobytesText = kilobytes.ToString("N0", CultureInfo.InvariantCulture);
        text.Append(bufferSet.SystemTypeName);
        text.Append(' ');
        text.Append(bufferSet.Buffers.Length);
        text.Append(" buffer(s) ");
        text.Append(kilobytesText);
        text.Append(" KB");
    }

    /// <summary>Every buffer one system's binding fields name, each once, with their sizes added up. Reads only.</summary>
    private static DiscardedBufferSet CollectBufferSet(TrackedSlot tracked, object system)
    {
        List<object> bindings = new List<object>();
        foreach (FieldInfo bufferField in tracked.BufferFields)
        {
            object fieldValue = bufferField.GetValue(system);
            bool roomLeft = DiscardedSystemBuffers.CollectBindings(fieldValue, IsBinding, bindings);
            if (!roomLeft)
            {
                break;
            }
        }

        List<object> buffers = new List<object>();
        long totalBytes = 0;
        foreach (object binding in bindings)
        {
            object buffer = BufferOf(binding);
            if (buffer is null)
            {
                continue;
            }

            if (DiscardedResourceQueue.IsAmong(buffer, buffers))
            {
                continue;
            }

            buffers.Add(buffer);
            totalBytes += SizeInBytes(buffer);
        }

        DiscardedBufferSet bufferSet = new DiscardedBufferSet();
        bufferSet.SystemTypeName = tracked.Slot.SystemTypeName;
        bufferSet.Buffers = buffers.ToArray();
        bufferSet.TotalBytes = totalBytes;
        return bufferSet;
    }

    private static bool IsBinding(object value)
    {
        Type valueType = value.GetType();
        return valueType == _vertexBindingType || valueType == _indexBindingType;
    }

    private static object BufferOf(object binding)
    {
        if (binding.GetType() == _vertexBindingType)
        {
            return _vertexBindingBuffer.GetValue(binding);
        }

        return _indexBindingBuffer.GetValue(binding);
    }

    private static long SizeInBytes(object buffer)
    {
        object size = _bufferSizeInBytes.GetValue(buffer);
        return Convert.ToInt64(size, CultureInfo.InvariantCulture);
    }

    private static bool IsDisposed(object buffer)
    {
        object disposed = _bufferIsDisposed.GetValue(buffer);
        return disposed is true;
    }

    private static void RememberRenderer(object renderer)
    {
        lock (_gate)
        {
            for (int index = _renderersSeen.Count - 1; index >= 0; index--)
            {
                object seen = _renderersSeen[index].Target;
                if (seen is null)
                {
                    _renderersSeen.RemoveAt(index);
                    continue;
                }

                if (ReferenceEquals(seen, renderer))
                {
                    return;
                }
            }

            _renderersSeen.Add(new WeakReference(renderer));
        }
    }

    // ------------------------------------------------------------------ the release, main thread

    /// <summary>Main thread, every frame on the joiner, from OverlayRelease.OnMainThreadUpdate.</summary>
    public static void OnMainThreadUpdate()
    {
        if (!_active)
        {
            return;
        }

        try
        {
            ReleaseWhatIsDue();
        }
        catch (Exception exception)
        {
            StopAfterFailure("the release", exception);
        }
    }

    private static bool AlwaysInUse(object discardedSystem, object bufferSet)
    {
        return true;
    }

    private static void ReleaseWhatIsDue()
    {
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> due;
        List<LiveSystem> liveSystems;

        lock (_gate)
        {
            if (_queue.PendingCount == 0)
            {
                // Still counts the update: the queue's "sixty updates" is counted from here.
                _queue.TakeDue(NowSeconds(), AlwaysInUse);
                return;
            }

            liveSystems = LiveSystems();
            List<LiveSystem> liveSystemsNow = liveSystems;
            due = _queue.TakeDue(NowSeconds(), (discardedSystem, bufferSet) => IsLive(discardedSystem, liveSystemsNow));
        }

        if (due.Count == 0)
        {
            return;
        }

        List<object> buffersOfLiveSystems = BuffersOf(liveSystems);
        double buffersBefore = AdoptionMemory.ReadGraphicsCounterMegabytes("BuffersMemory");

        int systemsReleased = 0;
        int buffersDisposed = 0;
        long bytesDisposed = 0;
        int duplicates = 0;
        int neverCreated = 0;
        int alreadyDisposed = 0;
        int heldByLiveSystem = 0;

        foreach (DiscardedResourceQueue.PendingRelease pending in due)
        {
            DiscardedBufferSet bufferSet = (DiscardedBufferSet)pending.Resource;
            DiscardedSystemBuffers.ReleasePlan plan = DiscardedSystemBuffers.Plan(bufferSet.Buffers, buffersOfLiveSystems, SizeInBytes, IsDisposed);

            DisableDiscardedSystem(pending);

            foreach (object buffer in plan.ToDispose)
            {
                IDisposable disposable = (IDisposable)buffer;
                disposable.Dispose();
            }

            systemsReleased++;
            buffersDisposed += plan.ToDispose.Count;
            bytesDisposed += plan.BytesToDispose;
            duplicates += plan.Duplicates;
            neverCreated += plan.NeverCreated;
            alreadyDisposed += plan.AlreadyDisposed;
            heldByLiveSystem += plan.HeldByLiveSystem;
        }

        _buffersReleased += buffersDisposed;
        _bytesReleased += bytesDisposed;

        double buffersAfter = AdoptionMemory.ReadGraphicsCounterMegabytes("BuffersMemory");
        string buffersBeforeText = buffersBefore.ToString("N2", CultureInfo.InvariantCulture);
        string buffersAfterText = buffersAfter.ToString("N2", CultureInfo.InvariantCulture);
        string kilobytesText = (bytesDisposed / 1024.0).ToString("N0", CultureInfo.InvariantCulture);
        string sessionKilobytesText = (_bytesReleased / 1024.0).ToString("N0", CultureInfo.InvariantCulture);

        _log($"# overlay: render systems: released {buffersDisposed} buffer(s), {kilobytesText} KB, of {systemsReleased} discarded render system(s); " +
             $"not disposed: {duplicates} named twice, {neverCreated} never created, {alreadyDisposed} already disposed, {heldByLiveSystem} held by a live system; " +
             $"Stride buffers {buffersBeforeText} MB -> {buffersAfterText} MB; " +
             $"{_buffersReleased} buffer(s), {sessionKilobytesText} KB released this session");
    }

    /// <summary>
    /// The second barrier (the first is that nothing in the game can reach a discarded
    /// instance): CustomOrbRenderer tests Enabled before every Draw of these systems, so one
    /// that somebody did still hold would draw nothing. An instance that has already been
    /// collected needs no barrier.
    /// </summary>
    private static void DisableDiscardedSystem(DiscardedResourceQueue.PendingRelease pending)
    {
        object discardedSystem = pending.Owner;
        if (discardedSystem is null)
        {
            return;
        }

        foreach (TrackedSlot tracked in _trackedSlots)
        {
            if (tracked.SystemType == discardedSystem.GetType())
            {
                tracked.EnabledField.SetValue(discardedSystem, false);
                return;
            }
        }
    }

    /// <summary>True when the discarded system is back in a renderer field. Null once it has been collected, and a collected object is in no field.</summary>
    private static bool IsLive(object discardedSystem, List<LiveSystem> liveSystems)
    {
        if (discardedSystem is null)
        {
            return false;
        }

        foreach (LiveSystem live in liveSystems)
        {
            if (ReferenceEquals(live.System, discardedSystem))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every tracked system that is in a field of any renderer seen, right now. Caller holds the gate.</summary>
    private static List<LiveSystem> LiveSystems()
    {
        List<LiveSystem> liveSystems = new List<LiveSystem>();

        for (int index = _renderersSeen.Count - 1; index >= 0; index--)
        {
            object renderer = _renderersSeen[index].Target;
            if (renderer is null)
            {
                _renderersSeen.RemoveAt(index);
                continue;
            }

            List<HoldingField> holdingFields = HoldingFieldsOf(renderer.GetType());
            foreach (HoldingField holdingField in holdingFields)
            {
                object system = holdingField.Field.GetValue(renderer);
                if (system is null)
                {
                    continue;
                }

                LiveSystem live = new LiveSystem();
                live.System = system;
                live.Tracked = holdingField.Tracked;
                liveSystems.Add(live);
            }
        }

        return liveSystems;
    }

    /// <summary>
    /// Every instance field of a renderer type, inherited ones included, whose type is one of
    /// the released system types. That is the six DWRendererBase fields and, on MenuRenderer,
    /// its galaxy-preview background field. Caller holds the gate.
    /// </summary>
    private static List<HoldingField> HoldingFieldsOf(Type rendererType)
    {
        if (_holdingFieldsByRendererType.TryGetValue(rendererType, out List<HoldingField> known))
        {
            return known;
        }

        List<HoldingField> holdingFields = new List<HoldingField>();
        for (Type current = rendererType; current is not null; current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(DeclaredInstanceFields))
            {
                TrackedSlot tracked = TrackedSlotFor(field.FieldType);
                if (tracked is null)
                {
                    continue;
                }

                // A system that is not proven is not read here either: in a player's game
                // nothing of it is touched.
                if (!tracked.Slot.ProvenUnreachable)
                {
                    continue;
                }

                HoldingField holdingField = new HoldingField();
                holdingField.Field = field;
                holdingField.Tracked = tracked;
                holdingFields.Add(holdingField);
            }
        }

        _holdingFieldsByRendererType[rendererType] = holdingFields;
        return holdingFields;
    }

    private static TrackedSlot TrackedSlotFor(Type systemType)
    {
        foreach (TrackedSlot tracked in _trackedSlots)
        {
            if (tracked.SystemType == systemType)
            {
                return tracked;
            }
        }

        return null;
    }

    /// <summary>Every buffer the live systems name. A discarded system's buffer found here is never disposed.</summary>
    private static List<object> BuffersOf(List<LiveSystem> liveSystems)
    {
        List<object> buffers = new List<object>();
        foreach (LiveSystem live in liveSystems)
        {
            DiscardedBufferSet liveSet = CollectBufferSet(live.Tracked, live.System);
            buffers.AddRange(liveSet.Buffers);
        }

        return buffers;
    }
}
