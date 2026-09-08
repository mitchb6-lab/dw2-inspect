namespace Dw2MpLobby;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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
}
