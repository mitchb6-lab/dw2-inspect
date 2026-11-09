using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Writes the text of every message dialog DW2 shows into the mod's log.
///
/// Unattended runs cannot see the screen. On 2026-11-08 a run sat for ten minutes because
/// DWGame.LoadGame had stopped at a "problems loading ship hulls" dialog (the save needed
/// DLC that Steam had switched off), and nothing in our log said so.
/// </summary>
public static class DialogLog
{
    private const int MaxLineLength = 600;

    private static Action<string> _log;

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var controllerType = AccessTools.TypeByName("DistantWorlds.UI.UserInterfaceController");
        if (controllerType is null)
        {
            log("# dialog: UserInterfaceController not found; dialogs not logged");
            return;
        }

        var prefixMethod = typeof(DialogLog).GetMethod(nameof(BeforeShow), BindingFlags.NonPublic | BindingFlags.Static);
        var prefix = new HarmonyMethod(prefixMethod);
        var patchedCount = 0;

        foreach (var method in AccessTools.GetDeclaredMethods(controllerType))
        {
            var isDialog = method.Name == "ShowMessageDialog" || method.Name == "ShowMessageDialogCentered";
            if (!isDialog)
            {
                continue;
            }

            harmony.Patch(method, prefix);
            patchedCount++;
        }

        log($"# dialog: logging {patchedCount} message-dialog overload(s)");
    }

    private static void BeforeShow(object[] __args)
    {
        try
        {
            var texts = new List<string>();
            foreach (var argument in __args)
            {
                if (argument is string text && text.Length > 0)
                {
                    var oneLine = text.Replace("\n", " | ");
                    texts.Add(oneLine);
                }
            }

            var line = string.Join(" / ", texts);
            if (line.Length > MaxLineLength)
            {
                line = line.Substring(0, MaxLineLength) + " ...";
            }

            _log("# dialog: " + line);
        }
        catch (Exception ex)
        {
            _log("# dialog: could not read dialog text " + ex.GetType().Name);
        }
    }
}
