namespace Dw2MpLobby;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // A fault on the UI thread -- a surprise from the other side's session, a WinForms
        // edge -- used to end the lobby with .NET's default dialog and no record of why. Log
        // it beside the session files and keep the window up where that is possible. Must be
        // set before the first Form exists.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => ReportUiThreadFault(eventArgs.Exception);

        // Locate the game before showing anything. A lobby full of empty dropdowns is a
        // worse experience than a clear message saying what is missing.
        var gamePath = GameData.FindGamePath(args.FirstOrDefault());

        if (gamePath is null)
        {
            MessageBox.Show(
                "Could not find Distant Worlds 2.\n\n" +
                "Checked the DW2_PATH environment variable and every Steam library on this machine.\n\n" +
                "Pass the install folder as the first argument, or set DW2_PATH.",
                "Distant Worlds 2 not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Application.Run(new LobbyForm(gamePath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "Lobby failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Where UI-thread faults are written: next to session-host.json and session-client.json.</summary>
    private static string ErrorLogPath()
    {
        var folder = Path.GetDirectoryName(SessionDescriptor.DefaultPath);
        return Path.Combine(folder, "lobby-errors.log");
    }

    private static void ReportUiThreadFault(Exception fault)
    {
        var logPath = ErrorLogPath();
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {fault.GetType().Name}: {fault.Message}{Environment.NewLine}{fault.StackTrace}{Environment.NewLine}";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            File.AppendAllText(logPath, line);
        }
        catch (Exception)
        {
            // Nowhere to write; the dialog below still says what happened.
        }

        MessageBox.Show(
            $"The lobby hit an error and kept running.\n\n{fault.GetType().Name}: {fault.Message}\n\n" +
            $"Details were written to {logPath}.",
            "Lobby error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
