using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Releases the two 2048x2048 render targets DW2 leaks every time its renderer is handed a new
/// galaxy (2026-11-11). On a joiner that is every full-state adoption: +32 MB of Stride texture
/// memory each, never freed, because a Stride graphics resource has no finalizer.
///
/// WHAT DW2 DOES (read in its IL with dw2inspect, game 1.3.8.4):
///
///   DWRendererBase.CheckInitializeGraphics(forceReinitialization) runs for every renderer on
///   every frame. When the flag is set (DWRendererBase.SetGalaxyAndPlayer sets it) it stores a
///   NEW MapOverlaySystem in _LongRangeScannerSystem and in _ExplorationDiplomacyOverlaySystem
///   and calls Initialize on each, which calls Texture.New2D for MapOverlaySystem.BackBuffer.
///   The instances it overwrites are not disposed.
///
/// WHY THE OLD RENDER TARGET IS SAFE TO RELEASE (a sweep of all 1,904 types and 26,657 methods
/// in the four DistantWorlds assemblies):
///
///   - Those two fields are the only fields of type MapOverlaySystem anywhere, and nothing
///     captures one in a closure. They are written only by CheckInitializeGraphics.
///   - They are read only by CheckInitializeGraphics, DWRendererBase.UpdateMapOverlaySystems
///     and CustomOrbRenderer.DrawCorePreSceneInternal / DrawCorePostSceneInternal, and every
///     one of those loads the field again for each use. Nobody keeps an instance in a local
///     across frames, so an overwritten instance is unreachable from the game's code.
///   - BackBuffer is written only by MapOverlaySystem.Initialize and read only by
///     MapOverlaySystem.UpdateOverlay and Draw, on its own instance.
///   - Draw hands BackBuffer to an effect's parameters (BasicTextureKeys.Image). Those two
///     effects are SHARED (EffectHelper._BasicTextureEffect and _BasicTextureEffectLRS are
///     statics; only MapOverlaySystem.Initialize obtains them), so after an adoption they can
///     still name the old texture. That stale entry is never drawn: the only code that applies
///     those effects is MapOverlaySystem.Draw, which first sets Image to its own BackBuffer.
///   - MapOverlaySystem.Draw itself skips a BackBuffer that is null or IsDisposed.
///
/// WHAT THIS CLASS DOES: a prefix and a postfix on CheckInitializeGraphics compare the two
/// fields before and after. A replaced instance waits in a DiscardedResourceQueue for ten
/// seconds and sixty main-thread updates; then, on the main thread, its BackBuffer is disposed.
///
/// WHAT IT NEVER DOES:
///   - dispose anything belonging to an instance that is in either field of any renderer seen,
///     or a texture that such an instance holds;
///   - dispose the discarded instance's effects (shared, see above), its SpriteBatch or its
///     plane buffers (small; not proven the same way -- see docs/multiplayer.md);
///   - keep going after a failure: the first exception is logged once and the release stops
///     for the rest of the session, which leaves DW2's own behaviour (the leak).
///
/// Joiner only: that is where the leak repeats and where it was verified. Off switch:
/// DW2MP_OVERLAY_RELEASE=0 (an off switch, not a dev flag: the lobby lets it through).
/// </summary>
public static class OverlayRelease
{
    private const string OffSwitchVariable = "DW2MP_OVERLAY_RELEASE";
    private const string LongRangeScannerFieldName = "_LongRangeScannerSystem";
    private const string ExplorationDiplomacyFieldName = "_ExplorationDiplomacyOverlaySystem";

    /// <summary>What the two overlay fields of one renderer held before DW2's own code ran.</summary>
    public struct OverlaySystemsBefore
    {
        public bool Captured;
        public object LongRangeScanner;
        public object ExplorationDiplomacy;
    }

    private static readonly object _gate = new();
    private static Action<string> _log;
    private static bool _active;
    private static bool _failureLogged;

    private static FieldInfo _longRangeScannerField;
    private static FieldInfo _explorationDiplomacyField;
    private static FieldInfo _backBufferField;
    private static FieldInfo _enabledField;

    private static DiscardedResourceQueue _queue;

    /// <summary>Renderers seen holding an overlay system. Weak: this list must not keep one alive.</summary>
    private static readonly List<WeakReference> _renderersSeen = new();

    private static long _renderTargetsReleased;

    /// <summary>
    /// DW2 ships its method bodies as the four bytes "nop nop nop ret" and restores the real
    /// ones from each type's static constructor. Harmony copies the body it finds, so patching
    /// CheckInitializeGraphics before DW2 has initialised DWRendererBase would replace the real
    /// method with an empty one. The patch therefore waits until the body is real.
    /// </summary>
    private const int StubBodyBytes = 4;

    /// <summary>Main-thread updates to wait for the real body before giving up: about two minutes of frames.</summary>
    private const int PatchAttemptLimit = 7200;

    private static Harmony _harmony;
    private static MethodInfo _checkInitializeGraphics;
    private static bool _patchPending;
    private static int _patchAttempts;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;
        _harmony = harmony;

        string switchText = Environment.GetEnvironmentVariable(OffSwitchVariable);
        bool enabled = DiscardedResourceQueue.EnabledFromSwitchText(switchText);
        if (!enabled)
        {
            log($"# overlay: OFF ({OffSwitchVariable}=0): DW2 keeps two 2048x2048 render targets per adoption, about 32 MB each time");
            return;
        }

        Type rendererType = AccessTools.TypeByName("DistantWorlds2.DWRendererBase");
        Type overlayType = AccessTools.TypeByName("DistantWorlds2.MapOverlaySystem");
        if (rendererType is null || overlayType is null)
        {
            log("# overlay: DWRendererBase or MapOverlaySystem not found; discarded render targets are NOT released");
            return;
        }

        _longRangeScannerField = AccessTools.Field(rendererType, LongRangeScannerFieldName);
        _explorationDiplomacyField = AccessTools.Field(rendererType, ExplorationDiplomacyFieldName);
        _backBufferField = AccessTools.Field(overlayType, "BackBuffer");
        _enabledField = AccessTools.Field(overlayType, "Enabled");
        _checkInitializeGraphics = AccessTools.Method(rendererType, "CheckInitializeGraphics", new[] { typeof(bool) });

        bool allFound = _longRangeScannerField is not null
            && _explorationDiplomacyField is not null
            && _backBufferField is not null
            && _enabledField is not null
            && _checkInitializeGraphics is not null;
        if (!allFound)
        {
            log("# overlay: the overlay fields, MapOverlaySystem.BackBuffer / Enabled or CheckInitializeGraphics(bool) not all found; discarded render targets are NOT released");
            return;
        }

        bool typesAsExpected = _longRangeScannerField.FieldType == overlayType
            && _explorationDiplomacyField.FieldType == overlayType
            && _enabledField.FieldType == typeof(bool)
            && typeof(IDisposable).IsAssignableFrom(_backBufferField.FieldType);
        if (!typesAsExpected)
        {
            log("# overlay: the overlay fields are not the types this was written against (game 1.3.8.4); discarded render targets are NOT released");
            return;
        }

        _queue = new DiscardedResourceQueue(
            DiscardedResourceQueue.DefaultDelaySeconds,
            DiscardedResourceQueue.DefaultMinimumUpdates,
            DiscardedResourceQueue.DefaultMaximumPending);

        _patchPending = true;
        TryPatchGuarded();
    }

    private static void TryPatchGuarded()
    {
        try
        {
            TryPatch();
        }
        catch (Exception exception)
        {
            _patchPending = false;
            StopAfterFailure("patching CheckInitializeGraphics", exception);
        }
    }

    private static int BodyBytes(MethodInfo method)
    {
        MethodBody body = method.GetMethodBody();
        if (body is null)
        {
            return 0;
        }

        byte[] instructions = body.GetILAsByteArray();
        if (instructions is null)
        {
            return 0;
        }

        return instructions.Length;
    }

    /// <summary>Patches CheckInitializeGraphics once DW2 has restored its body. Called at install and then once per main-thread update.</summary>
    private static void TryPatch()
    {
        int bodyBytes = BodyBytes(_checkInitializeGraphics);
        if (bodyBytes <= StubBodyBytes)
        {
            _patchAttempts++;

            if (_patchAttempts == 1)
            {
                _log("# overlay: waiting for DW2 to initialise DWRendererBase before patching CheckInitializeGraphics (its body is still the stub)");
            }

            if (_patchAttempts >= PatchAttemptLimit)
            {
                _patchPending = false;
                _log($"# overlay: CheckInitializeGraphics was still a stub after {PatchAttemptLimit} updates; discarded render targets are NOT released");
            }

            return;
        }

        _patchPending = false;

        MethodInfo prefix = typeof(OverlayRelease).GetMethod(nameof(BeforeCheckInitializeGraphics), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo postfix = typeof(OverlayRelease).GetMethod(nameof(AfterCheckInitializeGraphics), BindingFlags.NonPublic | BindingFlags.Static);
        _harmony.Patch(_checkInitializeGraphics, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));

        _active = true;
        string delayText = DiscardedResourceQueue.DefaultDelaySeconds.ToString("0", CultureInfo.InvariantCulture);
        _log($"# overlay: on -- a map overlay system the renderer replaces has its render target released {delayText} s later " +
             $"({OffSwitchVariable}=0 switches this off; CheckInitializeGraphics body {bodyBytes} bytes, patched after {_patchAttempts} wait(s))");
    }

    private static double NowSeconds()
    {
        return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    /// <summary>The first failure is logged and ends the release for this session; later ones are silent.</summary>
    private static void StopAfterFailure(string where, Exception exception)
    {
        _active = false;

        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        _log($"# overlay: *** FAILED in {where}: {exception.GetType().Name}: {exception.Message} -- " +
             "no more render targets are released this session (DW2's own behaviour: about 32 MB kept per adoption)");
    }

    // ------------------------------------------------------------------ the patch

    /// <summary>Harmony prefix on DWRendererBase.CheckInitializeGraphics(bool). Every frame, every renderer: keep it cheap.</summary>
    private static void BeforeCheckInitializeGraphics(object __instance, out OverlaySystemsBefore __state)
    {
        __state = default;

        if (!_active)
        {
            return;
        }

        try
        {
            __state.LongRangeScanner = _longRangeScannerField.GetValue(__instance);
            __state.ExplorationDiplomacy = _explorationDiplomacyField.GetValue(__instance);
            __state.Captured = true;
        }
        catch (Exception exception)
        {
            __state = default;
            StopAfterFailure("the CheckInitializeGraphics prefix", exception);
        }
    }

    /// <summary>Harmony postfix: an overlay field that holds a different instance now has discarded the old one.</summary>
    private static void AfterCheckInitializeGraphics(object __instance, OverlaySystemsBefore __state)
    {
        if (!_active || !__state.Captured)
        {
            return;
        }

        try
        {
            object longRangeScannerNow = _longRangeScannerField.GetValue(__instance);
            object explorationDiplomacyNow = _explorationDiplomacyField.GetValue(__instance);

            if (longRangeScannerNow is not null || explorationDiplomacyNow is not null)
            {
                RememberRenderer(__instance);
            }

            bool longRangeScannerReplaced = !ReferenceEquals(__state.LongRangeScanner, longRangeScannerNow);
            bool explorationDiplomacyReplaced = !ReferenceEquals(__state.ExplorationDiplomacy, explorationDiplomacyNow);
            if (!longRangeScannerReplaced && !explorationDiplomacyReplaced)
            {
                return;
            }

            double nowSeconds = NowSeconds();
            int queued = 0;
            int waiting;
            long droppedQueueFull;

            lock (_gate)
            {
                if (_queue.NoteReplacement(LongRangeScannerFieldName, __state.LongRangeScanner, longRangeScannerNow, nowSeconds))
                {
                    queued++;
                }

                if (_queue.NoteReplacement(ExplorationDiplomacyFieldName, __state.ExplorationDiplomacy, explorationDiplomacyNow, nowSeconds))
                {
                    queued++;
                }

                waiting = _queue.PendingCount;
                droppedQueueFull = _queue.DroppedQueueFullCount;
            }

            if (queued > 0)
            {
                _log($"# overlay: {__instance.GetType().Name} replaced {queued} map overlay system(s); " +
                     $"their render targets are released in {DiscardedResourceQueue.DefaultDelaySeconds.ToString("0", CultureInfo.InvariantCulture)} s " +
                     $"({waiting} waiting, {droppedQueueFull} ever dropped because the queue was full)");
            }
        }
        catch (Exception exception)
        {
            StopAfterFailure("the CheckInitializeGraphics postfix", exception);
        }
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

    /// <summary>Main thread, every frame on the joiner. Releases the render targets whose wait is over.</summary>
    public static void OnMainThreadUpdate()
    {
        if (_patchPending)
        {
            TryPatchGuarded();
        }

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

    private static void ReleaseWhatIsDue()
    {
        IReadOnlyList<DiscardedResourceQueue.PendingRelease> due;
        List<object> overlaySystemsInUse;

        lock (_gate)
        {
            if (_queue.PendingCount == 0)
            {
                // Still counts the update: the queue's "sixty updates" is counted from here.
                _queue.TakeDue(NowSeconds(), AlwaysInUse);
                return;
            }

            overlaySystemsInUse = OverlaySystemsInUse();
            due = _queue.TakeDue(NowSeconds(), candidate => DiscardedResourceQueue.IsAmong(candidate, overlaySystemsInUse));
        }

        if (due.Count == 0)
        {
            return;
        }

        double texturesBefore = AdoptionMemory.ReadGraphicsCounterMegabytes("TextureMemory");
        int released = 0;
        int skipped = 0;
        string sizeText = "";

        foreach (DiscardedResourceQueue.PendingRelease pending in due)
        {
            string releasedSize = ReleaseRenderTarget(pending.Discarded, overlaySystemsInUse);
            if (releasedSize is null)
            {
                skipped++;
                continue;
            }

            released++;
            sizeText = releasedSize;
        }

        _renderTargetsReleased += released;
        double texturesAfter = AdoptionMemory.ReadGraphicsCounterMegabytes("TextureMemory");

        string texturesBeforeText = texturesBefore.ToString("N0", CultureInfo.InvariantCulture);
        string texturesAfterText = texturesAfter.ToString("N0", CultureInfo.InvariantCulture);

        _log($"# overlay: released {released} discarded map overlay render target(s){sizeText}, " +
             $"{skipped} skipped (none, already disposed or shared with a live overlay); " +
             $"Stride textures {texturesBeforeText} MB -> {texturesAfterText} MB; " +
             $"{_renderTargetsReleased} released this session");
    }

    private static bool AlwaysInUse(object candidate)
    {
        return true;
    }

    /// <summary>Every overlay system that is in either field of any renderer seen, right now. Caller holds the gate.</summary>
    private static List<object> OverlaySystemsInUse()
    {
        List<object> inUse = new List<object>();

        for (int index = _renderersSeen.Count - 1; index >= 0; index--)
        {
            object renderer = _renderersSeen[index].Target;
            if (renderer is null)
            {
                _renderersSeen.RemoveAt(index);
                continue;
            }

            object longRangeScanner = _longRangeScannerField.GetValue(renderer);
            if (longRangeScanner is not null)
            {
                inUse.Add(longRangeScanner);
            }

            object explorationDiplomacy = _explorationDiplomacyField.GetValue(renderer);
            if (explorationDiplomacy is not null)
            {
                inUse.Add(explorationDiplomacy);
            }
        }

        return inUse;
    }

    /// <summary>
    /// Disposes one discarded overlay system's render target. Returns its size as text, or null
    /// when nothing was disposed (no render target, already disposed, or a live overlay holds
    /// the very same texture).
    /// </summary>
    private static string ReleaseRenderTarget(object discardedSystem, List<object> overlaySystemsInUse)
    {
        object renderTarget = _backBufferField.GetValue(discardedSystem);
        if (renderTarget is null)
        {
            return null;
        }

        foreach (object systemInUse in overlaySystemsInUse)
        {
            object renderTargetInUse = _backBufferField.GetValue(systemInUse);
            if (ReferenceEquals(renderTargetInUse, renderTarget))
            {
                return null;
            }
        }

        object isDisposed = AccessTools.Property(renderTarget.GetType(), "IsDisposed")?.GetValue(renderTarget);
        if (isDisposed is true)
        {
            return null;
        }

        if (renderTarget is not IDisposable disposable)
        {
            return null;
        }

        object width = AccessTools.Property(renderTarget.GetType(), "Width")?.GetValue(renderTarget);
        object height = AccessTools.Property(renderTarget.GetType(), "Height")?.GetValue(renderTarget);

        // A second barrier, should the reading of DW2's IL above ever be wrong: both callers of
        // MapOverlaySystem.Draw check Enabled first, so a discarded instance that somebody did
        // still hold would draw nothing instead of drawing into a disposed render target.
        _enabledField.SetValue(discardedSystem, false);

        disposable.Dispose();

        return $" ({width}x{height})";
    }
}
