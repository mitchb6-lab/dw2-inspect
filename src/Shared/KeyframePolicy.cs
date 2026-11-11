using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Dw2MpShared;

/// <summary>Why the host is serving a full state now.</summary>
public enum KeyframeReason
{
    None,

    /// <summary>The joiner asked: the join bootstrap, or a real mismatch it wants repaired.</summary>
    ClientAsked,

    /// <summary>A player pressed "Attempt resync" on the desync popup. The one case allowed inside the floor.</summary>
    DesyncChoice,

    /// <summary>A big event (war, peace, first contact, colony founded or lost, empire eliminated).</summary>
    Event,

    /// <summary>The periodic keyframe: N minutes of running game since the last full state.</summary>
    Periodic,

    /// <summary>The old safety net: a very long time with no full state at all.</summary>
    SafetyInterval,
}

public sealed class KeyframeDecision
{
    public KeyframeReason Reason { get; set; } = KeyframeReason.None;

    /// <summary>For an event keyframe: what happened, for the log.</summary>
    public string Description { get; set; } = "";

    /// <summary>True when the joiner asked and the floor refused it. The request is dropped, not queued.</summary>
    public bool RequestIgnored { get; set; }

    public bool Serve => Reason != KeyframeReason.None;
}

/// <summary>
/// The host's sync settings, read from sync-settings.json beside the session files
/// (%LOCALAPPDATA%\Dw2Mp) with an environment override for the harness. Data, not a constant,
/// because the right keyframe interval is a trade between freshness and the ~30 MB of native
/// memory every adoption costs the joiner (see docs\multiplayer.md).
/// </summary>
public sealed class SyncSettings
{
    public const double DefaultKeyframeMinutes = 3;

    public const double MinimumKeyframeMinutes = 1;

    public const double MaximumKeyframeMinutes = 120;

    /// <summary>Minutes of RUNNING game between periodic keyframes. 0 switches them off.</summary>
    public double KeyframeMinutes { get; set; } = DefaultKeyframeMinutes;

    /// <summary>Serve a keyframe right after a big event.</summary>
    public bool KeyframeOnEvents { get; set; } = true;

    public double KeyframeSeconds => KeyframeMinutes * 60.0;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Settings from the file's text and the environment override. Never throws: unreadable
    /// input gives the defaults and a note saying so. A positive interval is clamped to
    /// [1, 120] minutes; zero or less means "no periodic keyframes".
    /// </summary>
    public static SyncSettings Parse(string json, string environmentMinutes, out string note)
    {
        var settings = new SyncSettings();
        var notes = new List<string>();

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                SyncSettings fromFile = JsonSerializer.Deserialize<SyncSettings>(json, Options);
                if (fromFile is not null)
                {
                    settings = fromFile;
                    notes.Add("read from sync-settings.json");
                }
            }
            catch (Exception exception)
            {
                notes.Add("sync-settings.json is not valid JSON (" + exception.GetType().Name + "); defaults used");
            }
        }

        if (!string.IsNullOrWhiteSpace(environmentMinutes))
        {
            bool parsed = double.TryParse(environmentMinutes, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes);
            if (parsed)
            {
                settings.KeyframeMinutes = minutes;
                notes.Add("DW2MP_KEYFRAME_MINUTES overrides the interval");
            }
            else
            {
                notes.Add("DW2MP_KEYFRAME_MINUTES is not a number; ignored");
            }
        }

        if (double.IsNaN(settings.KeyframeMinutes) || settings.KeyframeMinutes <= 0)
        {
            settings.KeyframeMinutes = 0;
        }
        else if (settings.KeyframeMinutes < MinimumKeyframeMinutes)
        {
            settings.KeyframeMinutes = MinimumKeyframeMinutes;
            notes.Add("interval raised to the 1 minute minimum");
        }
        else if (settings.KeyframeMinutes > MaximumKeyframeMinutes)
        {
            settings.KeyframeMinutes = MaximumKeyframeMinutes;
            notes.Add("interval lowered to the 120 minute maximum");
        }

        note = notes.Count == 0 ? "defaults" : string.Join("; ", notes);
        return settings;
    }
}

/// <summary>
/// When the host serves a full state (mitch, in chat 2026-11-10: "periodic keyframes ... every N
/// minutes and right after big events ... respect the existing host-side floor").
///
/// One instance on the host. It is told about every tick, every request and every event, and
/// answers one question: serve now, and why? The rules, in order:
///
///   1. Nothing has been served yet and the joiner asked: serve. The join is never throttled.
///   2. The joiner asked inside the floor: ignored, unless a desync choice exempted this one.
///   3. The joiner asked outside the floor: serve.
///   4. An event is pending and the floor has passed: serve. Inside the floor it WAITS, so an
///      event is delayed by the floor but never lost.
///   5. The periodic interval of RUNNING time has passed: serve. Paused time does not count: a
///      paused galaxy has not changed, and every adoption costs the joiner memory.
///   6. The safety interval has passed: serve.
///
/// Events and the periodic clock only exist once a joiner has a state (rule 1 has fired):
/// before that the first full state carries everything anyway. Time is given in seconds by the
/// caller, not counted in ticks: the tick rate follows the game's own cycle length, measured at
/// about 29 a second on 2026-11-10, not the 10 the older comments assume.
/// </summary>
public sealed class KeyframePolicy
{
    private readonly double _periodicSeconds;
    private readonly bool _serveOnEvents;
    private readonly long _floorTicks;
    private readonly long _safetyTicks;

    private bool _anyServed;
    private long _lastServedTick;
    private double _runningSecondsSinceServed;
    private readonly List<string> _pendingEvents = new();

    public const int MaxPendingEvents = 8;

    public KeyframePolicy(SyncSettings settings, long floorTicks, long safetyTicks)
    {
        _periodicSeconds = settings.KeyframeSeconds;
        _serveOnEvents = settings.KeyframeOnEvents;
        _floorTicks = floorTicks;
        _safetyTicks = safetyTicks;
    }

    public bool AnyServed => _anyServed;

    public int PendingEventCount => _pendingEvents.Count;

    public double RunningSecondsSinceServed => _runningSecondsSinceServed;

    /// <summary>Called once per host tick with the wall time since the previous one and whether the game was running.</summary>
    public void NoteTime(double elapsedSeconds, bool hostRunning)
    {
        if (!_anyServed || !hostRunning || elapsedSeconds <= 0)
        {
            return;
        }

        _runningSecondsSinceServed += elapsedSeconds;
    }

    /// <summary>A big event happened on the host. Remembered until a full state carries it.</summary>
    public void NoteEvent(string description)
    {
        if (!_serveOnEvents || !_anyServed)
        {
            return;
        }

        if (_pendingEvents.Count >= MaxPendingEvents)
        {
            return;
        }

        _pendingEvents.Add(description ?? "");
    }

    public KeyframeDecision Decide(long tick, bool clientAsked, bool desyncExemption)
    {
        var decision = new KeyframeDecision();
        bool withinFloor = _anyServed && tick - _lastServedTick < _floorTicks;

        if (clientAsked)
        {
            if (withinFloor && !desyncExemption)
            {
                decision.RequestIgnored = true;
            }
            else
            {
                decision.Reason = desyncExemption ? KeyframeReason.DesyncChoice : KeyframeReason.ClientAsked;
                return decision;
            }
        }

        if (!_anyServed)
        {
            // No joiner has a state yet. Only the safety interval can serve unasked.
            if (tick - _lastServedTick >= _safetyTicks)
            {
                decision.Reason = KeyframeReason.SafetyInterval;
            }

            return decision;
        }

        if (withinFloor)
        {
            return decision;
        }

        if (_pendingEvents.Count > 0)
        {
            decision.Reason = KeyframeReason.Event;
            decision.Description = string.Join(", ", _pendingEvents);
            return decision;
        }

        if (_periodicSeconds > 0 && _runningSecondsSinceServed >= _periodicSeconds)
        {
            decision.Reason = KeyframeReason.Periodic;
            return decision;
        }

        if (tick - _lastServedTick >= _safetyTicks)
        {
            decision.Reason = KeyframeReason.SafetyInterval;
        }

        return decision;
    }

    /// <summary>A full state was serialised at this tick, for whatever reason. It carries every pending event.</summary>
    public void NoteServed(long tick)
    {
        _anyServed = true;
        _lastServedTick = tick;
        _runningSecondsSinceServed = 0;
        _pendingEvents.Clear();
    }
}
