using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Stops DW2's autosave for the duration of a harness run. Opt-in: DW2MP_BLOCK_AUTOSAVE=1.
///
/// The determinism harness launches with --continue, and --continue is
/// MenuRenderer.ContinueGameClick: the newest .DWGame in data\SavedGames by write time. DW2
/// autosaves every GameSettings.AutoSaveMinutes of game time. On 2026-11-10 run 1 autosaved,
/// run 2 then loaded that autosave instead of the pinned save (its cycle-1 game time was run
/// 1's cycle-6,000 time, to the millisecond), and the harness reported "diverged at the first
/// snapshot" about two different games. The autosave also rotates through the player's own
/// Autosave1..N slots; an unattended run overwrote saves from September.
///
/// Two layers, because the first attempt at this froze the game:
///
/// 1. GameSettings.AutoSave is switched off IN MEMORY on the loaded DWGame.DWSettings, from a
///    prefix on DWGame.Update. The game then never schedules an autosave. The settings file
///    on disk is not touched, so the player's choice survives the run.
///
/// 2. A prefix on DWGame.BeginSaveGame(filePath) as a safety net. DWGame.Update is a state
///    machine on DWGame.Status: an autosave sets Status = InitiateSaving (3) and Update calls
///    BeginSaveGame every frame while it stays 3; BeginSaveGame is what moves it on. The first
///    version of this prefix only returned false, so Status stayed 3 and the simulation sat at
///    cycle 3,000 for thirteen minutes with IsRunning=True. Refusing an autosave therefore
///    also puts Status back to Running (6). Deliberate saves (MakeSave, a human) go through.
///
/// Not armed in a lobby session: there the host's autosave is the host's decision.
/// </summary>
public static class AutosaveBlock
{
    private const byte StatusRunning = 6;   // DistantWorlds.Types.GameStatus.Running

    private static Action<string> _log;
    private static int _blockedCount;
    private static bool _settingReported;

    private static FieldInfo _settingsField;
    private static FieldInfo _autoSaveField;
    private static FieldInfo _autoSaveMinutesField;
    private static FieldInfo _statusField;

    public static bool Enabled => Environment.GetEnvironmentVariable("DW2MP_BLOCK_AUTOSAVE") == "1";

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var settingsType = AccessTools.TypeByName("DistantWorlds.Types.GameSettings");
        if (gameType is null || settingsType is null)
        {
            log("# autosave: DWGame or GameSettings not found; autosaves NOT blocked -- the next --continue run may load one");
            return;
        }

        _settingsField = AccessTools.Field(gameType, "DWSettings");
        _autoSaveField = AccessTools.Field(settingsType, "AutoSave");
        _autoSaveMinutesField = AccessTools.Field(settingsType, "AutoSaveMinutes");
        _statusField = AccessTools.Field(gameType, "Status");

        var update = AccessTools.Method(gameType, "Update");
        var beginSaveGame = AccessTools.Method(gameType, "BeginSaveGame", new[] { typeof(string) });

        if (_settingsField is null || _autoSaveField is null || update is null || beginSaveGame is null || _statusField is null)
        {
            log("# autosave: DWGame.DWSettings / GameSettings.AutoSave / Update / BeginSaveGame / Status not all resolvable; autosaves NOT blocked");
            return;
        }

        var updatePrefix = typeof(AutosaveBlock).GetMethod(nameof(BeforeUpdate), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(update, new HarmonyMethod(updatePrefix));

        var savePrefix = typeof(AutosaveBlock).GetMethod(nameof(BeforeBeginSaveGame), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(beginSaveGame, new HarmonyMethod(savePrefix));

        log("# autosave: off for this run (DW2MP_BLOCK_AUTOSAVE=1): GameSettings.AutoSave cleared in memory, autosave calls refused; deliberate saves still go through");
    }

    /// <summary>
    /// Harmony prefix on DWGame.Update. Clears GameSettings.AutoSave on the live settings object
    /// whenever it is on -- once after the settings load, and again if the game reloads them.
    /// </summary>
    private static void BeforeUpdate(object __instance)
    {
        try
        {
            var settings = _settingsField.GetValue(__instance);
            if (settings is null)
            {
                return;
            }

            var autoSaveOn = _autoSaveField.GetValue(settings) is true;
            if (!autoSaveOn)
            {
                return;
            }

            _autoSaveField.SetValue(settings, false);

            if (!_settingReported)
            {
                _settingReported = true;
                var minutes = _autoSaveMinutesField?.GetValue(settings);
                _log($"# autosave: GameSettings.AutoSave was on (every {minutes ?? "?"} min); cleared in memory for this run. The settings file is not touched.");
            }
        }
        catch (Exception ex)
        {
            if (!_settingReported)
            {
                _settingReported = true;
                _log("# autosave: could not clear GameSettings.AutoSave " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Harmony prefix on DWGame.BeginSaveGame. Returning false skips the game's method; Status
    /// is put back to Running first, or Update would call this again every frame forever.
    /// </summary>
    private static bool BeforeBeginSaveGame(object __instance, string filePath)
    {
        var isAutosave = filePath is not null && filePath.Contains("Autosave", StringComparison.OrdinalIgnoreCase);
        if (!isAutosave)
        {
            return true;
        }

        try
        {
            var statusType = _statusField.FieldType;
            var running = Enum.ToObject(statusType, StatusRunning);
            _statusField.SetValue(__instance, running);
        }
        catch (Exception ex)
        {
            _log("# autosave: could not reset DWGame.Status " + ex.GetType().Name + "; letting the autosave through so the game does not freeze");
            return true;
        }

        _blockedCount++;
        if (_blockedCount <= 3)
        {
            _log($"# autosave: refused '{filePath}' (#{_blockedCount}); DWGame.Status reset to Running");
        }

        return false;
    }
}
