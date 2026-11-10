using System.Globalization;
using System.Reflection;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// The in-game answer to a detected desync (mitch, in chat 2026-11-10).
///
/// Detection stays where it was: the client compares the host's structural fingerprint with
/// its own on every StateSummary, and three mismatches in a row are a divergence. What changes
/// is what happens next. Instead of a silent correction, the client reports WHO diverged to the
/// host (Msg.DesyncReport), the host pauses its simulation, and BOTH players get the game's own
/// two-button message dialog:
///
///     1. Save and quit    -- this process saves Desync-&lt;date&gt;.DWGame and exits cleanly
///     2. Attempt resync   -- the host sends a fresh full state, the client adopts it, and the
///                            next summary after it lands decides success or failure
///
/// Either player may press either button; the press is mirrored to the other side
/// (Msg.DesyncChoice) so both popups move together. After two failed resyncs only "Save and
/// quit" is offered. Everything is logged, and DW2MP_FORCE_DESYNC_AT_CYCLE=N makes a client
/// report a fake divergence once so the popup and the resync path can be exercised on one PC.
///
/// Threading: reports and outcomes arrive on the network thread; the dialog must be shown on
/// the main thread, so every popup is queued and NetSession.OnMainThreadUpdate drains it.
/// Button clicks arrive on the UI thread. All game calls go through reflection, like the rest
/// of this mod, so no new assembly reference is needed.
/// </summary>
public static class Desync
{
    private const int MaxNameLength = 64;

    private const int MaxFingerprintLength = 64;

    /// <summary>Summaries after the new full state that may still disagree before the attempt is called failed.</summary>
    private const int MismatchesBeforeResyncFails = 3;

    private static Action<string> _log = _ => { };

    private static readonly DesyncPolicy Policy = new();

    private static readonly object Gate = new();

    private static readonly Queue<PendingDialog> PendingDialogs = new();

    private static DesyncWho _who = new();

    private static long _hostTickAtDetection;

    private static string _gameTimeAtDetection = "";

    private static int _syncsAppliedWhenResyncAsked;

    private static int _mismatchesDuringResync;

    private static long _lastClientTick;

    private static bool _pauseHeld;

    private static bool _uiAvailable;

    private static readonly long ForceAtCycle = EnvLong("DW2MP_FORCE_DESYNC_AT_CYCLE", 0);

    private static bool _forceSpent;

    private static bool _forceLogged;

    private static Type _controllerType;
    private static MethodInfo _showDialog;
    private static FieldInfo _screenWidth;
    private static FieldInfo _screenHeight;
    private static ConstructorInfo _buttonConstructor;
    private static Type _eventHandlerType;
    private static Type _vector2Type;
    private static MethodInfo _scaleVector;

    /// <summary>True while a desync popup keeps the host paused; the watchdog must not resume the game then.</summary>
    public static bool HoldsPause => _pauseHeld;

    /// <summary>The client is waiting for the summary that decides a resync attempt.</summary>
    public static bool VerifyingResync => Policy.Phase == DesyncPhase.Resyncing && NetSession.Role == NetSession.NetRole.Client;

    public static void Install(Action<string> log)
    {
        _log = log;
        ResolveUi();

        if (ForceAtCycle > 0)
        {
            log($"# desync: DW2MP_FORCE_DESYNC_AT_CYCLE={ForceAtCycle} -- this client will report a fake divergence once it reaches that cycle");
        }
    }

    // ------------------------------------------------------------- detection

    /// <summary>Client, network thread. Called by NetSession when three summaries in a row disagreed. False means "not handled, correct silently as before".</summary>
    public static bool OnDivergenceConfirmed(long hostTick, string hostFingerprint, string myFingerprint)
    {
        if (!_uiAvailable)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Policy.Detect())
            {
                return true;
            }

            _forceSpent = true;
            _who = MyIdentity();
            _hostTickAtDetection = hostTick;
            _gameTimeAtDetection = GameTimeText();
        }

        _log($"# desync: DETECTED -- {_who.Describe()} diverged at host tick {hostTick}, game time {_gameTimeAtDetection}: host {hostFingerprint} vs mine {myFingerprint}");

        NetSession.SendDesync(NetSession.Msg.DesyncReport, BuildReport(hostTick, hostFingerprint, myFingerprint));
        QueueChoiceDialog("");
        return true;
    }

    /// <summary>Host, network thread.</summary>
    public static void OnReportReceived(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            var who = new DesyncWho
            {
                PlayerName = Cap(reader.ReadString(), MaxNameLength),
                Slot = reader.ReadInt32(),
                IsHost = reader.ReadBoolean(),
            };
            long hostTick = reader.ReadInt64();
            string hostFingerprint = Cap(reader.ReadString(), MaxFingerprintLength);
            string theirFingerprint = Cap(reader.ReadString(), MaxFingerprintLength);
            string gameTime = Cap(reader.ReadString(), 32);

            lock (Gate)
            {
                if (!Policy.Detect())
                {
                    _log($"# desync: report from {who.Describe()} ignored -- one is already being handled");
                    return;
                }

                _who = who;
                _hostTickAtDetection = hostTick;
                _gameTimeAtDetection = gameTime;
            }

            _log($"# desync: REPORTED by {who.Describe()} at host tick {hostTick}, game time {gameTime}: host {hostFingerprint} vs theirs {theirFingerprint}");
            PauseHost();
            QueueChoiceDialog("");
        }
        catch (Exception ex)
        {
            _log("# desync: malformed report " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ------------------------------------------------------------- choices

    /// <summary>UI thread: a button on this machine's popup.</summary>
    private static void ChooseLocally(DesyncChoice choice)
    {
        bool accepted;
        lock (Gate)
        {
            accepted = Policy.Choose(choice);
        }

        if (!accepted)
        {
            _log($"# desync: {choice} pressed but not valid in phase {Policy.Phase}; ignored");
            return;
        }

        string myName = MyIdentity().PlayerName;
        _log($"# desync: {myName} chose {choice} ({Policy.FailedResyncs} resync(s) failed so far)");
        NetSession.SendDesync(NetSession.Msg.DesyncChoice, BuildChoice(choice, myName));

        if (choice == DesyncChoice.SaveAndQuit)
        {
            SaveAndQuit();
            return;
        }

        StartResync(myName);
    }

    /// <summary>Either role, network thread: the peer pressed a button.</summary>
    public static void OnChoiceReceived(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            var choice = (DesyncChoice)reader.ReadByte();
            string chooser = Cap(reader.ReadString(), MaxNameLength);
            int failedOnPeer = reader.ReadInt32();

            bool accepted;
            lock (Gate)
            {
                Policy.Mirror(failedOnPeer);
                accepted = Policy.Choose(choice);
            }

            if (!accepted)
            {
                _log($"# desync: peer {chooser} chose {choice} but we are in phase {Policy.Phase}; ignored");
                return;
            }

            _log($"# desync: {chooser} chose {choice}");

            if (choice == DesyncChoice.SaveAndQuit)
            {
                QueueInfoDialog($"{chooser} chose Save and quit and has left the session.", "The game is yours to continue alone or quit. Nothing more will arrive from them.");
                ResumeHostIfHeld();
                lock (Gate)
                {
                    Policy.Reset();
                }

                return;
            }

            StartResync(chooser);
        }
        catch (Exception ex)
        {
            _log("# desync: malformed choice " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Both roles: the host serves a full state at its next tick; the client arms the verification.</summary>
    private static void StartResync(string chooser)
    {
        if (NetSession.Role == NetSession.NetRole.Host)
        {
            NetSession.MarkResyncRequested();
        }
        else
        {
            lock (Gate)
            {
                _syncsAppliedWhenResyncAsked = NetSession.SyncsApplied;
                _mismatchesDuringResync = 0;
            }

            NetSession.RequestResyncForDesync($"desync popup: {chooser} chose attempt resync");
        }

        _log($"# desync: resync attempt {Policy.FailedResyncs + 1} of {DesyncPolicy.MaxResyncAttempts} started by {chooser}");
        QueueInfoDialog("Resyncing...", $"{chooser} chose Attempt resync. The host is sending a fresh full state; the next check after it lands decides. Attempt {Policy.FailedResyncs + 1} of {DesyncPolicy.MaxResyncAttempts}.");
    }

    /// <summary>Client, network thread: a summary arrived while a resync is being verified.</summary>
    public static void OnSummaryDuringResync(bool matched, int syncsApplied, long hostTick)
    {
        bool landed = syncsApplied > _syncsAppliedWhenResyncAsked;
        if (!landed)
        {
            return;
        }

        if (matched)
        {
            FinishResync(true, hostTick);
            return;
        }

        int mismatches;
        lock (Gate)
        {
            _mismatchesDuringResync++;
            mismatches = _mismatchesDuringResync;
        }

        if (mismatches >= MismatchesBeforeResyncFails)
        {
            FinishResync(false, hostTick);
        }
    }

    private static void FinishResync(bool succeeded, long hostTick)
    {
        lock (Gate)
        {
            Policy.ResyncFinished(succeeded);
        }

        NetSession.SendDesync(NetSession.Msg.DesyncOutcome, BuildOutcome(succeeded));
        ShowOutcome(succeeded, hostTick);
    }

    /// <summary>Host, network thread: the client verified the resync.</summary>
    public static void OnOutcomeReceived(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            bool succeeded = reader.ReadBoolean();
            int failedOnPeer = reader.ReadInt32();

            lock (Gate)
            {
                Policy.ResyncFinished(succeeded);
                Policy.Mirror(failedOnPeer);
            }

            ShowOutcome(succeeded, _hostTickAtDetection);
        }
        catch (Exception ex)
        {
            _log("# desync: malformed outcome " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void ShowOutcome(bool succeeded, long hostTick)
    {
        if (succeeded)
        {
            _log($"# desync: RESYNC SUCCEEDED -- structure agrees again (host tick {hostTick})");
            ResumeHostIfHeld();
            QueueInfoDialog("Resync succeeded", $"{_who.Describe()} is back in sync with the host. The game continues.");
            return;
        }

        _log($"# desync: RESYNC FAILED -- attempt {Policy.FailedResyncs} of {DesyncPolicy.MaxResyncAttempts}; {(Policy.OffersResync ? "one more attempt is offered" : "only Save and quit is offered now")}");
        QueueChoiceDialog($"Resync attempt {Policy.FailedResyncs} failed: the structure still disagreed after the new full state. ");
    }

    // ------------------------------------------------------------- save and quit

    private static Timer _exitTimer;

    private static void SaveAndQuit()
    {
        var game = ApplyState.Game;
        string fileName = DesyncPolicy.SaveFileName(DateTime.Now);
        string virtualPath = "SavedGames/" + fileName;
        string expectedFile = Path.Combine(AppContext.BaseDirectory, "data", "SavedGames", fileName);

        try
        {
            if (game is null)
            {
                _log("# desync: save skipped -- no DWGame instance; quitting");
            }
            else
            {
                var createFolder = AccessTools.Method(game.GetType(), "CheckCreateSaveGameFolder");
                createFolder?.Invoke(game, null);

                var saveGame = AccessTools.Method(game.GetType(), "SaveGame", new[] { typeof(string) });
                saveGame?.Invoke(game, new object[] { virtualPath });

                // SaveGame swallows its own failure into a log line, so read the file back.
                if (File.Exists(expectedFile))
                {
                    _log($"# desync: SAVED {new FileInfo(expectedFile).Length:N0} bytes -> {expectedFile}");
                }
                else
                {
                    _log($"# desync: SAVE FAILED -- no file at {expectedFile}; read data\\Logs\\SessionLog.txt for 'Error while saving game'");
                }
            }
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log("# desync: save threw " + cause.GetType().Name + ": " + cause.Message);
        }

        _log("# desync: quitting in 3 s (the session lock is removed on the way out)");
        _exitTimer = new Timer(_ => Determinism.ExitProcess(0), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    // ------------------------------------------------------------- pause

    private static void PauseHost()
    {
        if (NetSession.Role != NetSession.NetRole.Host)
        {
            return;
        }

        try
        {
            var server = GameServer();
            if (server is null)
            {
                _log("# desync: cannot pause -- no GameServer");
                return;
            }

            AccessTools.Method(server.GetType(), "PauseGame")?.Invoke(server, null);
            _pauseHeld = true;
            _log("# desync: host simulation PAUSED while the popup is open");
        }
        catch (Exception ex)
        {
            _log("# desync: pause failed " + (ex.InnerException ?? ex).Message);
        }
    }

    private static void ResumeHostIfHeld()
    {
        if (!_pauseHeld)
        {
            return;
        }

        try
        {
            var server = GameServer();
            AccessTools.Method(server?.GetType(), "ResumeGame")?.Invoke(server, null);
            _log("# desync: host simulation RESUMED");
        }
        catch (Exception ex)
        {
            _log("# desync: resume failed " + (ex.InnerException ?? ex).Message);
        }

        _pauseHeld = false;
    }

    private static object GameServer()
    {
        var game = ApplyState.Game;
        if (game is null)
        {
            return null;
        }

        return AccessTools.Field(game.GetType(), "Server")?.GetValue(game);
    }

    // ------------------------------------------------------------- dev flag

    /// <summary>Called every client tick so the force flag knows where the game is.</summary>
    public static void NoteTick(long tick)
    {
        _lastClientTick = tick;
    }

    /// <summary>
    /// DW2MP_FORCE_DESYNC_AT_CYCLE: from that cycle until the divergence is confirmed, the
    /// client's own fingerprint is replaced so every summary mismatches. It stops the moment
    /// the popup is raised, so a following resync can genuinely succeed.
    /// </summary>
    public static string MaybeForceDivergence(string myFingerprint)
    {
        if (ForceAtCycle <= 0 || _forceSpent || _lastClientTick < ForceAtCycle)
        {
            return myFingerprint;
        }

        if (!_forceLogged)
        {
            _forceLogged = true;
            _log($"# desync: FORCING a fake divergence from cycle {_lastClientTick} (DW2MP_FORCE_DESYNC_AT_CYCLE)");
        }

        return "FORCED-DESYNC-" + myFingerprint;
    }

    // ------------------------------------------------------------- dialogs

    private sealed class PendingDialog
    {
        public string Title;
        public string Text;
        public DialogButton First;
        public DialogButton Second;
    }

    private sealed class DialogButton
    {
        public string Name;
        public string Label;
        public Action OnClick;
    }

    private static void QueueChoiceDialog(string prefix)
    {
        string text = prefix + DesyncPolicy.Describe(_who, _gameTimeAtDetection, _hostTickAtDetection) + "\n\n" + Policy.OptionsText();

        var saveAndQuit = new DialogButton
        {
            Name = "DesyncSaveAndQuit",
            Label = DesyncPolicy.SaveAndQuitLabel,
            OnClick = () => ChooseLocally(DesyncChoice.SaveAndQuit),
        };

        DialogButton attemptResync = null;
        if (Policy.OffersResync)
        {
            attemptResync = new DialogButton
            {
                Name = "DesyncAttemptResync",
                Label = DesyncPolicy.AttemptResyncLabel,
                OnClick = () => ChooseLocally(DesyncChoice.AttemptResync),
            };
        }

        QueueDialog(DesyncPolicy.Title, text, saveAndQuit, attemptResync);
    }

    private static void QueueInfoDialog(string title, string text)
    {
        var ok = new DialogButton
        {
            Name = "DesyncOk",
            Label = "OK",
            OnClick = () => { },
        };

        QueueDialog(title, text, ok, null);
    }

    private static void QueueDialog(string title, string text, DialogButton first, DialogButton second)
    {
        _log($"# desync: popup queued -- {title}: {text.Replace("\n", " | ")}");

        if (!_uiAvailable)
        {
            return;
        }

        lock (PendingDialogs)
        {
            PendingDialogs.Enqueue(new PendingDialog { Title = title, Text = text, First = first, Second = second });
        }
    }

    /// <summary>Main thread (DWGame.Update, via NetSession). Shows whatever the network thread queued.</summary>
    public static void OnMainThreadUpdate()
    {
        PendingDialog next = null;
        lock (PendingDialogs)
        {
            if (PendingDialogs.Count > 0)
            {
                next = PendingDialogs.Dequeue();
            }
        }

        if (next is null)
        {
            return;
        }

        try
        {
            ShowDialogNow(next);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log("# desync: popup failed " + cause.GetType().Name + ": " + cause.Message);
        }
    }

    private static void ResolveUi()
    {
        _controllerType = AccessTools.TypeByName("DistantWorlds.UI.UserInterfaceController");
        var buttonType = AccessTools.TypeByName("DistantWorlds.UI.DWButtonData");
        var eventArgsType = AccessTools.TypeByName("DistantWorlds.Types.DWEventArgs");
        _vector2Type = AccessTools.TypeByName("Stride.Core.Mathematics.Vector2");

        if (_controllerType is null || buttonType is null || eventArgsType is null || _vector2Type is null)
        {
            _log("# desync: dialog types not found (UserInterfaceController / DWButtonData / DWEventArgs / Vector2); a desync will be corrected silently and logged only");
            return;
        }

        _eventHandlerType = typeof(EventHandler<>).MakeGenericType(eventArgsType);
        _buttonConstructor = buttonType.GetConstructor(new[] { typeof(string), typeof(string), _eventHandlerType, typeof(object) });

        // The 9-parameter overload: (Texture, title, text, bool, button, button, screenWidth, screenHeight, Vector2 size).
        _showDialog = _controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "ShowMessageDialogCentered" && method.GetParameters().Length == 9);

        _screenWidth = AccessTools.Field(_controllerType, "ScreenWidth");
        _screenHeight = AccessTools.Field(_controllerType, "ScreenHeight");

        var helperType = AccessTools.TypeByName("DistantWorlds.UI.UserInterfaceHelper");
        _scaleVector = helperType?.GetMethod("CalculateScaledValue", new[] { _vector2Type });

        if (_buttonConstructor is null || _showDialog is null || _screenWidth is null || _screenHeight is null)
        {
            _log("# desync: dialog API shape not as expected (DWButtonData ctor / ShowMessageDialogCentered(9) / ScreenWidth); a desync will be corrected silently and logged only");
            return;
        }

        if (!_showDialog.IsStatic)
        {
            _log("# desync: ShowMessageDialogCentered is not static on this build; popup disabled");
            return;
        }

        _uiAvailable = true;
        _log("# desync: in-game popup ready (two-button message dialog)");
    }

    private static void ShowDialogNow(PendingDialog dialog)
    {
        object firstButton = MakeButton(dialog.First);
        object secondButton = dialog.Second is null ? null : MakeButton(dialog.Second);

        object size = Activator.CreateInstance(_vector2Type, 760f, 520f);
        if (_scaleVector is not null)
        {
            size = _scaleVector.Invoke(null, new[] { size });
        }

        int width = (int)_screenWidth.GetValue(null);
        int height = (int)_screenHeight.GetValue(null);

        _showDialog.Invoke(null, new object[] { null, dialog.Title, dialog.Text, true, firstButton, secondButton, width, height, size });
        _log($"# desync: popup SHOWN -- {dialog.Title}");
    }

    private sealed class ClickTarget
    {
        private readonly Action _action;

        public ClickTarget(Action action)
        {
            _action = action;
        }

        /// <summary>Bound to EventHandler&lt;DWEventArgs&gt; by contravariance: (object, object) accepts (object, DWEventArgs).</summary>
        public void Clicked(object sender, object eventArgs)
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                _log("# desync: button handler threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static object MakeButton(DialogButton button)
    {
        var target = new ClickTarget(button.OnClick);
        var clicked = typeof(ClickTarget).GetMethod(nameof(ClickTarget.Clicked));
        var handler = Delegate.CreateDelegate(_eventHandlerType, target, clicked);
        return _buttonConstructor.Invoke(new object[] { button.Name, button.Label, handler, null });
    }

    // ------------------------------------------------------------- payloads

    private static byte[] BuildReport(long hostTick, string hostFingerprint, string myFingerprint)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8);

        writer.Write(_who.PlayerName ?? "");
        writer.Write(_who.Slot);
        writer.Write(_who.IsHost);
        writer.Write(hostTick);
        writer.Write(hostFingerprint ?? "");
        writer.Write(myFingerprint ?? "");
        writer.Write(_gameTimeAtDetection ?? "");
        writer.Flush();
        return buffer.ToArray();
    }

    private static byte[] BuildChoice(DesyncChoice choice, string chooser)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8);

        writer.Write((byte)choice);
        writer.Write(chooser ?? "");
        writer.Write(Policy.FailedResyncs);
        writer.Flush();
        return buffer.ToArray();
    }

    private static byte[] BuildOutcome(bool succeeded)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8);

        writer.Write(succeeded);
        writer.Write(Policy.FailedResyncs);
        writer.Flush();
        return buffer.ToArray();
    }

    // ------------------------------------------------------------- helpers

    private static DesyncWho MyIdentity()
    {
        bool isHost = NetSession.Role == NetSession.NetRole.Host;
        var session = MakeSave.Session;

        var who = new DesyncWho
        {
            IsHost = isHost,
            Slot = session?.MySlot ?? -1,
            PlayerName = "",
        };

        var mySlot = session?.Players?.FirstOrDefault(player => player.Slot == session.MySlot);
        if (mySlot is not null)
        {
            who.PlayerName = Cap(mySlot.Name, MaxNameLength);
        }

        return who;
    }

    private static string GameTimeText()
    {
        try
        {
            var galaxy = ApplyState.CurrentGalaxy;
            var timeField = galaxy is null ? null : AccessTools.Field(galaxy.GetType(), "Time");
            if (timeField?.GetValue(galaxy) is DateTime gameTime)
            {
                return gameTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through: the popup still says who and at which tick.
        }

        return "unknown game time";
    }

    private static string Cap(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var cleaned = new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        return cleaned.Substring(0, maxLength);
    }

    private static long EnvLong(string name, long fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return fallback;
    }
}
