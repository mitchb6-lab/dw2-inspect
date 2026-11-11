using System.Diagnostics;
using System.Reflection;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Host-owned pause and speed, on the joiner (mitch, in chat 2026-11-10: "verify that DW2's own
/// event auto-pause never pauses or desyncs the joiner, and that only the host's pause and
/// speed count. Fix if not").
///
/// VERIFIED NOT SO, 2026-11-10 two-instance run: the joiner sat at
/// "GameServer.IsRunning=False, DWGame.AutoPaused=True, GetGamePaused=True" from its first
/// adoption to the end of the session while the host ran. DWGame.StartGameExisting calls
/// PauseGame on every adoption and nothing on a joiner resumes it (the game's own load path
/// ends in AutoResumeGame; an adoption does not), and an empire message auto-pauses the same
/// way. The galaxy was never wrong: the joiner does not simulate, deltas kept applying and its
/// clock follows the host's. But its game SAID paused while the host's ran, a joiner had to
/// press play after every full state, and with periodic keyframes that would be every few
/// minutes.
///
/// What a pause in DW2 is: DWGame.PauseGame(bool) stops the local GameServer's stopwatch and
/// tells the local GameClients. It sends nothing. So a joiner's pause never reaches the host,
/// and the host's pause reaches the joiner only as speed 0 on the clock sample every delta
/// carries.
///
/// THE FIX is to make the joiner's local pause state and speed FOLLOW the host's, every frame,
/// on the main thread: host paused and joiner not, pause it; host running and joiner paused,
/// resume it, whoever paused it (DW2 itself, an adoption, or the joiner's own space bar). The
/// decision is PauseMirror.Decide in src\Shared, tested.
///
/// A save is left alone: DWGame pauses for its duration and resumes itself afterwards.
/// </summary>
public static class HostPause
{
    private static Action<string> _log;
    private static bool _installed;
    private static volatile bool _saving;

    private static MethodInfo _getGamePaused;
    private static MethodInfo _pauseGame;
    private static MethodInfo _resumeGame;
    private static MethodInfo _changeGameSpeed;
    private static MethodInfo _getGameSpeed;
    private static FieldInfo _allowResuming;

    private static long _lastCheckStamp;
    private static int _pausesMirrored;
    private static int _resumesMirrored;
    private static int _speedsMirrored;

    /// <summary>Look at most this often: a pause that takes a quarter of a second to follow is not worth a per-frame reflection call.</summary>
    private const double CheckEveryMilliseconds = 250;

    private const float SpeedTolerance = 0.01f;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        Type gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        if (gameType is null)
        {
            log("# pause: DWGame not found; the joiner's pause will NOT follow the host");
            return;
        }

        _getGamePaused = AccessTools.Method(gameType, "GetGamePaused", Type.EmptyTypes);
        _pauseGame = AccessTools.Method(gameType, "PauseGame", new[] { typeof(bool) });
        _resumeGame = AccessTools.Method(gameType, "ResumeGame", Type.EmptyTypes);
        _changeGameSpeed = AccessTools.Method(gameType, "ChangeGameSpeed", new[] { typeof(float) });
        _getGameSpeed = AccessTools.Method(gameType, "GetGameSpeed", Type.EmptyTypes);
        _allowResuming = AccessTools.Field(gameType, "AllowResuming");

        if (_getGamePaused is null || _pauseGame is null || _resumeGame is null)
        {
            log("# pause: DWGame.GetGamePaused / PauseGame(bool) / ResumeGame not found; the joiner's pause will NOT follow the host");
            return;
        }

        MethodInfo beginSave = AccessTools.Method(gameType, "BeginSaveGame");
        MethodInfo endSave = AccessTools.Method(gameType, "EndSaveGame");
        if (beginSave is not null && endSave is not null)
        {
            MethodInfo onBegin = typeof(HostPause).GetMethod(nameof(OnBeginSave), BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo onEnd = typeof(HostPause).GetMethod(nameof(OnEndSave), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(beginSave, prefix: new HarmonyMethod(onBegin));
            harmony.Patch(endSave, postfix: new HarmonyMethod(onEnd));
        }

        _installed = true;
        log("# pause: the joiner's pause and speed follow the host's (DW2's own auto-pause is undone while the host runs)");
    }

    private static void OnBeginSave()
    {
        _saving = true;
    }

    private static void OnEndSave()
    {
        _saving = false;
    }

    /// <summary>Client, main thread, every frame after the deltas (and their clock samples) are applied.</summary>
    public static void FollowHost()
    {
        if (!_installed || _saving || !FixedStep.Following)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsedMilliseconds = (now - _lastCheckStamp) * 1000.0 / Stopwatch.Frequency;
        if (_lastCheckStamp != 0 && elapsedMilliseconds < CheckEveryMilliseconds)
        {
            return;
        }

        _lastCheckStamp = now;

        object game = ApplyState.Game;
        if (game is null)
        {
            return;
        }

        try
        {
            float hostSpeed = FixedStep.HostSpeed;
            bool locallyPaused = _getGamePaused.Invoke(game, null) is true;
            PauseMirrorAction action = PauseMirror.Decide(hostSpeed, locallyPaused);

            if (action == PauseMirrorAction.PauseLocally)
            {
                _pauseGame.Invoke(game, new object[] { false });
                _pausesMirrored++;
                _log($"# pause: the host paused; joiner paused to match (#{_pausesMirrored})");
                return;
            }

            if (action == PauseMirrorAction.ResumeLocally)
            {
                // DWGame.ResumeGame does nothing unless AllowResuming is set; AutoResumeGame sets
                // it the same way before resuming.
                _allowResuming?.SetValue(game, true);
                _resumeGame.Invoke(game, null);
                _resumesMirrored++;
                _log($"# pause: the host is running at {hostSpeed:0.##}x; joiner resumed to match (#{_resumesMirrored})");
                return;
            }

            FollowSpeed(game, hostSpeed);
        }
        catch (Exception exception)
        {
            var cause = exception.InnerException ?? exception;
            _log("# pause: could not follow the host " + cause.GetType().Name + ": " + cause.Message);
            _installed = false;
        }
    }

    /// <summary>The speed the joiner's own UI shows. Nothing simulates at it; it only has to say what the host is doing.</summary>
    private static void FollowSpeed(object game, float hostSpeed)
    {
        if (hostSpeed <= 0f || _changeGameSpeed is null || _getGameSpeed is null)
        {
            return;
        }

        float localSpeed = _getGameSpeed.Invoke(game, null) is float speed ? speed : hostSpeed;
        if (Math.Abs(localSpeed - hostSpeed) <= SpeedTolerance)
        {
            return;
        }

        _changeGameSpeed.Invoke(game, new object[] { hostSpeed });
        _speedsMirrored++;
        _log($"# pause: joiner speed {localSpeed:0.##}x set to the host's {hostSpeed:0.##}x (#{_speedsMirrored})");

        // If the game did not take the value, asking again four times a second helps nobody.
        float speedAfter = _getGameSpeed.Invoke(game, null) is float after ? after : hostSpeed;
        if (Math.Abs(speedAfter - hostSpeed) > SpeedTolerance)
        {
            _log($"# pause: the joiner's game reports {speedAfter:0.##}x after being set to {hostSpeed:0.##}x; speed is no longer followed (pause still is)");
            _changeGameSpeed = null;
        }
    }
}
