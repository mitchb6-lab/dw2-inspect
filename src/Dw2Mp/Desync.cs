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
/// is what happens next. Instead of a silent correction, the client reports the divergence to
/// the host (Msg.DesyncReport), the host pauses its simulation, and BOTH players get the game's
/// own two-button message dialog:
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
/// Trust (security review, 2026-11-10): nothing a peer sends decides what this side offers.
/// Each side counts its own failed resyncs from its own transitions; WHO desynced is read from
/// this machine's session.json, never from the wire; reports are accepted at most once per 30 s;
/// a resync that is not verified within 60 s counts as failed; a lost link resumes the host and
/// resets the incident; and the dialog queue is capped. The wire carries only primitives and
/// two fingerprints plus a game-time string, all sanitised before they reach the log.
///
/// Threading: reports, choices and outcomes arrive on the network thread; the dialog must be
/// shown on the main thread, so every popup is queued and NetSession.OnMainThreadUpdate drains
/// it. Button clicks arrive on the UI thread. Shared state is read and written under Gate. All
/// game calls go through reflection, like the rest of this mod, so no new assembly reference is
/// needed.
/// </summary>
public static class Desync
{
    // 160 since protocol 4: the text is SyncDigest.Describe(), one "name=count:hash" per carried collection.
    private const int MaxFingerprintLength = 160;

    private const int MaxGameTimeLength = 32;

    /// <summary>Summaries after the new full state that may still disagree before the attempt is called failed.</summary>
    private const int MismatchesBeforeResyncFails = 3;

    /// <summary>A resync not verified by then has failed, whatever the peer says or does not say.</summary>
    private static readonly TimeSpan ResyncTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Reports accepted no more often than this; a flood raises one popup, not a hundred.</summary>
    private static readonly TimeSpan MinTimeBetweenReports = TimeSpan.FromSeconds(30);

    /// <summary>Dialogs waiting for the main thread; beyond this they are dropped.</summary>
    private const int MaxPendingDialogs = 2;

    private static Action<string> _log = _ => { };

    private static readonly DesyncPolicy Policy = new();

    /// <summary>Guards Policy and every field below it up to the dev-flag section.</summary>
    private static readonly object Gate = new();

    private static DesyncWho _who = new();

    private static long _hostTickAtDetection;

    private static string _gameTimeAtDetection = "";

    private static int _syncsAppliedWhenResyncAsked;

    private static int _mismatchesDuringResync;

    private static DateTime _resyncDeadlineUtc = DateTime.MaxValue;

    private static DateTime _lastReportAcceptedUtc = DateTime.MinValue;

    private static volatile bool _pauseHeld;

    private static readonly Queue<PendingDialog> PendingDialogs = new();

    private static bool _loggedDialogDrop;

    private static long _lastClientTick;

    private static bool _uiAvailable;

    // Needs the developer switch as well, like every dev flag since the 2026-11-10 security review:
    // the mod does not rely on the lobby having stripped it.
    private static readonly long ForceAtCycle =
        Environment.GetEnvironmentVariable("DW2MP_DEV") == "1" ? EnvLong("DW2MP_FORCE_DESYNC_AT_CYCLE", 0) : 0;

    private static volatile bool _forceSpent;

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
    public static bool VerifyingResync => CurrentPhase() == DesyncPhase.Resyncing && NetSession.Role == NetSession.NetRole.Client;

    public static void Install(Action<string> log)
    {
        if (AutoChoice != 0)
        {
            log($"# desync: *** DEV FLAG LIVE *** DW2MP_DESYNC_AUTO_CHOICE={AutoChoice} (with DW2MP_DEV=1): this game presses a desync popup button BY ITSELF five seconds after the choice appears. Never set in a real session.");
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DW2MP_DESYNC_AUTO_CHOICE")))
        {
            log("# desync: DW2MP_DESYNC_AUTO_CHOICE is set but ignored -- it needs DW2MP_DEV=1 and a value of 1 or 2");
        }

        _log = log;
        ResolveUi();

        if (ForceAtCycle > 0)
        {
            log($"# desync: DW2MP_FORCE_DESYNC_AT_CYCLE={ForceAtCycle} -- this client will report a fake divergence once it reaches that cycle");
        }
    }

    private static DesyncPhase CurrentPhase()
    {
        lock (Gate)
        {
            return Policy.Phase;
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

        string gameTime = GameTimeText();
        DesyncWho who;

        lock (Gate)
        {
            if (!Policy.Detect())
            {
                return true;
            }

            _forceSpent = true;
            _who = MyIdentity();
            _hostTickAtDetection = hostTick;
            _gameTimeAtDetection = gameTime;
            who = _who;
        }

        _log($"# desync: DETECTED -- {who.Describe()} diverged at host tick {hostTick}, game time {gameTime}: host {hostFingerprint} vs mine {myFingerprint}");

        NetSession.SendDesync(NetSession.Msg.DesyncReport, BuildReport(hostTick, hostFingerprint, myFingerprint, gameTime));
        QueueChoiceDialog("");
        return true;
    }

    /// <summary>Host, network thread. Who desynced comes from THIS machine's session, not the wire.</summary>
    public static void OnReportReceived(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            long hostTick = reader.ReadInt64();
            string hostFingerprint = PeerText.Sanitise(reader.ReadString(), MaxFingerprintLength);
            string theirFingerprint = PeerText.Sanitise(reader.ReadString(), MaxFingerprintLength);
            string gameTime = PeerText.Sanitise(reader.ReadString(), MaxGameTimeLength);

            var who = PeerIdentity();
            var now = DateTime.UtcNow;

            lock (Gate)
            {
                if (now - _lastReportAcceptedUtc < MinTimeBetweenReports)
                {
                    _log($"# desync: report from {who.Describe()} ignored -- one was accepted {(now - _lastReportAcceptedUtc).TotalSeconds:F0} s ago (floor {MinTimeBetweenReports.TotalSeconds:F0} s)");
                    return;
                }

                if (!Policy.Detect())
                {
                    _log($"# desync: report from {who.Describe()} ignored -- one is already being handled (phase {Policy.Phase})");
                    return;
                }

                _lastReportAcceptedUtc = now;
                _who = who;
                _hostTickAtDetection = hostTick;
                _gameTimeAtDetection = gameTime.Length > 0 ? gameTime : "unknown game time";
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
        DesyncPhase phaseAfter;
        lock (Gate)
        {
            accepted = Policy.Choose(choice);
            phaseAfter = Policy.Phase;
        }

        if (!accepted)
        {
            _log($"# desync: {choice} pressed but not valid in phase {phaseAfter}; ignored");

            // The player is still owed a choice: never leave a paused host with no dialog.
            if (phaseAfter == DesyncPhase.AwaitingChoice)
            {
                QueueChoiceDialog("That option is no longer available. ");
            }

            return;
        }

        string myName = MyIdentity().Describe();
        _log($"# desync: {myName} chose {choice}");
        NetSession.SendDesync(NetSession.Msg.DesyncChoice, BuildChoice(choice));

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
            string chooser = PeerIdentity().Describe();

            bool accepted;
            DesyncPhase phaseBefore;
            lock (Gate)
            {
                phaseBefore = Policy.Phase;
                accepted = Policy.Choose(choice);
            }

            if (!accepted)
            {
                _log($"# desync: peer {chooser} chose {choice} but we are in phase {phaseBefore}; ignored");
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

    /// <summary>Both roles, after an ACCEPTED resync choice: the host serves a full state at its next tick; the client arms the verification.</summary>
    private static void StartResync(string chooser)
    {
        int attempt;
        lock (Gate)
        {
            _resyncDeadlineUtc = DateTime.UtcNow + ResyncTimeout;
            _syncsAppliedWhenResyncAsked = NetSession.SyncsApplied;
            _mismatchesDuringResync = 0;
            attempt = Policy.NextAttemptNumber;
        }

        if (NetSession.Role == NetSession.NetRole.Host)
        {
            NetSession.MarkResyncRequestedForDesync();
        }
        else
        {
            NetSession.RequestResyncForDesync($"desync popup: {chooser} chose attempt resync");
        }

        _log($"# desync: resync attempt {attempt} of {DesyncPolicy.MaxResyncAttempts} started by {chooser}; deadline {ResyncTimeout.TotalSeconds:F0} s");
        QueueInfoDialog("Resyncing...", $"{chooser} chose Attempt resync. The host is sending a fresh full state; the next check after it lands decides. Attempt {attempt} of {DesyncPolicy.MaxResyncAttempts}.");
    }

    /// <summary>Client, network thread: a summary arrived while a resync is being verified.</summary>
    public static void OnSummaryDuringResync(bool matched, int syncsApplied, long hostTick)
    {
        bool landed;
        int mismatches = 0;

        lock (Gate)
        {
            landed = syncsApplied > _syncsAppliedWhenResyncAsked;
            if (landed && !matched)
            {
                _mismatchesDuringResync++;
                mismatches = _mismatchesDuringResync;
            }
        }

        if (!landed)
        {
            return;
        }

        if (matched)
        {
            ConcludeResync(true, $"structure agrees at host tick {hostTick}");
            return;
        }

        if (mismatches >= MismatchesBeforeResyncFails)
        {
            ConcludeResync(false, $"{mismatches} summaries still disagreed after the new full state (host tick {hostTick})");
        }
    }

    /// <summary>
    /// The one place a resync ends, on either role. Only a REAL transition (a resync was in
    /// flight) sends the outcome, resumes the host or shows a dialog.
    /// </summary>
    private static void ConcludeResync(bool succeeded, string why)
    {
        bool transitioned;
        int failed;
        bool offersResync;
        lock (Gate)
        {
            transitioned = Policy.ResyncFinished(succeeded);
            failed = Policy.FailedResyncs;
            offersResync = Policy.OffersResync;
            _resyncDeadlineUtc = DateTime.MaxValue;
        }

        if (!transitioned)
        {
            _log($"# desync: resync outcome ({(succeeded ? "success" : "failure")}, {why}) arrived with no resync in flight; ignored");
            return;
        }

        if (NetSession.Role == NetSession.NetRole.Client)
        {
            NetSession.SendDesync(NetSession.Msg.DesyncOutcome, BuildOutcome(succeeded));
        }

        if (succeeded)
        {
            _log($"# desync: RESYNC SUCCEEDED -- {why}");
            ResumeHostIfHeld();
            QueueInfoDialog("Resync succeeded", $"{WhoText()} is back in sync with the host. The game continues.");
            return;
        }

        _log($"# desync: RESYNC FAILED -- {why}; attempt {failed} of {DesyncPolicy.MaxResyncAttempts}; {(offersResync ? "one more attempt is offered" : "only Save and quit is offered now")}");
        QueueChoiceDialog($"Resync attempt {failed} failed: {why}. ");
    }

    /// <summary>Host, network thread: the client verified the resync. Counts only while one is in flight.</summary>
    public static void OnOutcomeReceived(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            bool succeeded = reader.ReadBoolean();
            ConcludeResync(succeeded, succeeded ? "the joiner verified the new full state" : "the joiner reports the structure still disagrees");
        }
        catch (Exception ex)
        {
            _log("# desync: malformed outcome " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Main thread: a resync nobody verified in time is a failed resync.</summary>
    private static void CheckResyncDeadline()
    {
        bool overdue;
        lock (Gate)
        {
            overdue = Policy.Phase == DesyncPhase.Resyncing && DateTime.UtcNow > _resyncDeadlineUtc;
        }

        if (overdue)
        {
            ConcludeResync(false, $"not verified within {ResyncTimeout.TotalSeconds:F0} s");
        }
    }

    /// <summary>NetSession's receive loop ended: resume if we hold the pause, forget the incident, drop queued popups.</summary>
    public static void OnDisconnected()
    {
        bool hadIncident;
        lock (Gate)
        {
            hadIncident = Policy.Phase != DesyncPhase.Idle;
            Policy.Reset();
            _resyncDeadlineUtc = DateTime.MaxValue;
        }

        lock (PendingDialogs)
        {
            PendingDialogs.Clear();
        }

        ResumeHostIfHeld();

        if (hadIncident)
        {
            _log("# desync: link to the launcher lost during a desync; incident reset, host resumed if it was paused");
        }
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
        if (NetSession.Role != NetSession.NetRole.Host || _pauseHeld)
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
        Interlocked.Exchange(ref _lastClientTick, tick);
    }

    /// <summary>
    /// DW2MP_FORCE_DESYNC_AT_CYCLE: from that cycle until the divergence is confirmed, the
    /// client's own fingerprint is replaced so every summary mismatches. It stops the moment
    /// the popup is raised, so a following resync can genuinely succeed.
    /// </summary>
    public static bool ForcingDivergence()
    {
        if (ForceAtCycle <= 0 || _forceSpent || Interlocked.Read(ref _lastClientTick) < ForceAtCycle)
        {
            return false;
        }

        if (!_forceLogged)
        {
            _forceLogged = true;
            _log($"# desync: FORCING a fake divergence from cycle {Interlocked.Read(ref _lastClientTick)} (DW2MP_FORCE_DESYNC_AT_CYCLE)");
        }

        return true;
    }

    /// <summary>True from the moment a desync is detected until the incident is closed.</summary>
    public static bool IncidentOpen => CurrentPhase() != DesyncPhase.Idle;

    // ------------------------------------------------------------- dev auto-choice
    //
    // DW2MP_DESYNC_AUTO_CHOICE=1 or 2: press that popup button by itself a few seconds after the
    // choice is offered. A harness run has nobody to click, and without this the first desync
    // of a run left the host paused behind a dialog for the rest of it (2026-11-10). Set it on
    // ONE side so the choice still has to cross the wire. The lobby strips it from a real
    // session unless DW2MP_DEV=1, exactly as it strips the force flag.

    private static readonly int AutoChoice = ReadAutoChoice();

    private static readonly TimeSpan AutoChoiceDelay = TimeSpan.FromSeconds(5);

    private static DateTime _autoChoiceDueUtc = DateTime.MaxValue;

    private static int ReadAutoChoice()
    {
        string text = Environment.GetEnvironmentVariable("DW2MP_DESYNC_AUTO_CHOICE") ?? "";

        // The mod checks the developer switch itself; it does not rely on the lobby having
        // stripped the flag (security review): a game started any other way inherits the shell.
        bool developerMode = Environment.GetEnvironmentVariable("DW2MP_DEV") == "1";
        if (!developerMode)
        {
            return 0;
        }

        if (text == "1")
        {
            return 1;
        }

        if (text == "2")
        {
            return 2;
        }

        return 0;
    }

    private static void ArmAutoChoice()
    {
        if (AutoChoice == 0)
        {
            return;
        }

        _autoChoiceDueUtc = DateTime.UtcNow + AutoChoiceDelay;
    }

    private static void CheckAutoChoice()
    {
        if (AutoChoice == 0 || DateTime.UtcNow < _autoChoiceDueUtc)
        {
            return;
        }

        _autoChoiceDueUtc = DateTime.MaxValue;

        bool awaitingChoice;
        bool offersResync;
        lock (Gate)
        {
            awaitingChoice = Policy.Phase == DesyncPhase.AwaitingChoice;
            offersResync = Policy.OffersResync;
        }

        if (!awaitingChoice)
        {
            return;
        }

        if (AutoChoice == 2 && !offersResync)
        {
            _log("# desync: DEV auto-choice 2 not pressed -- resync is no longer offered; the choice waits for a person");
            return;
        }

        DesyncChoice choice = AutoChoice == 1 ? DesyncChoice.SaveAndQuit : DesyncChoice.AttemptResync;
        _log($"# desync: DEV auto-choice pressing {choice} (DW2MP_DESYNC_AUTO_CHOICE={AutoChoice})");
        ChooseLocally(choice);
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

    /// <summary>The detection facts, copied under the gate, for dialog text.</summary>
    private static (DesyncWho Who, string GameTime, long HostTick, bool OffersResync, string Options) SnapshotForDialog()
    {
        lock (Gate)
        {
            return (_who, _gameTimeAtDetection, _hostTickAtDetection, Policy.OffersResync, Policy.OptionsText());
        }
    }

    private static string WhoText()
    {
        lock (Gate)
        {
            return _who.Describe();
        }
    }

    private static void QueueChoiceDialog(string prefix)
    {
        var facts = SnapshotForDialog();
        string text = prefix + DesyncPolicy.Describe(facts.Who, facts.GameTime, facts.HostTick) + "\n\n" + facts.Options;

        var saveAndQuit = new DialogButton
        {
            Name = "DesyncSaveAndQuit",
            Label = DesyncPolicy.SaveAndQuitLabel,
            OnClick = () => ChooseLocally(DesyncChoice.SaveAndQuit),
        };

        DialogButton attemptResync = null;
        if (facts.OffersResync)
        {
            attemptResync = new DialogButton
            {
                Name = "DesyncAttemptResync",
                Label = DesyncPolicy.AttemptResyncLabel,
                OnClick = () => ChooseLocally(DesyncChoice.AttemptResync),
            };
        }

        QueueDialog(DesyncPolicy.Title, text, saveAndQuit, attemptResync);
        ArmAutoChoice();
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

    /// <summary>
    /// A one-button notice from another part of the mod (the session memory budget). Shown on
    /// the main thread like every other popup here, and logged even when the UI is unavailable.
    /// </summary>
    internal static void QueueNotice(string title, string text)
    {
        QueueInfoDialog(title, text);
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
            if (PendingDialogs.Count >= MaxPendingDialogs)
            {
                if (!_loggedDialogDrop)
                {
                    _loggedDialogDrop = true;
                    _log($"# desync: more than {MaxPendingDialogs} popups waiting for the main thread; dropping the newest (logged once)");
                }

                return;
            }

            PendingDialogs.Enqueue(new PendingDialog { Title = title, Text = text, First = first, Second = second });
        }
    }

    /// <summary>Main thread (DWGame.Update, via NetSession). Shows whatever the network thread queued and enforces the resync deadline.</summary>
    public static void OnMainThreadUpdate()
    {
        CheckResyncDeadline();
        CheckAutoChoice();

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

    /// <summary>Protocol 3: host tick, both fingerprints, game time. Who desynced is NOT on the wire; each side reads its own session.</summary>
    private static byte[] BuildReport(long hostTick, string hostFingerprint, string myFingerprint, string gameTime)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8);

        writer.Write(hostTick);
        writer.Write(hostFingerprint ?? "");
        writer.Write(myFingerprint ?? "");
        writer.Write(gameTime ?? "");
        writer.Flush();
        return buffer.ToArray();
    }

    private static byte[] BuildChoice(DesyncChoice choice)
    {
        return new[] { (byte)choice };
    }

    private static byte[] BuildOutcome(bool succeeded)
    {
        return new[] { succeeded ? (byte)1 : (byte)0 };
    }

    // ------------------------------------------------------------- helpers

    /// <summary>This machine, from its own session.json.</summary>
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
            who.PlayerName = PeerText.Sanitise(mySlot.Name, 64);
        }

        return who;
    }

    /// <summary>
    /// The other machine, from THIS machine's session.json: the first player who is not us. The
    /// session is two players today; a peer cannot rename itself on the wire.
    /// </summary>
    private static DesyncWho PeerIdentity()
    {
        bool peerIsHost = NetSession.Role != NetSession.NetRole.Host;
        var session = MakeSave.Session;

        var who = new DesyncWho
        {
            IsHost = peerIsHost,
            Slot = -1,
            PlayerName = "",
        };

        var peerSlot = session?.Players?.FirstOrDefault(player => player.Slot != session.MySlot);
        if (peerSlot is not null)
        {
            who.Slot = peerSlot.Slot;
            who.PlayerName = PeerText.Sanitise(peerSlot.Name, 64);
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
