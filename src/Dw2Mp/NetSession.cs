using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Dw2MpShared;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// Host-authoritative state sync, speaking ONLY to the launcher over localhost.
///
/// PHASE 1 (the FAF model): the mod no longer owns any remote networking. It connects to
/// Dw2MpLobby on 127.0.0.1 and the launcher relays frames to the peer. This mirrors how
/// FAF runs Supreme Commander — the game is given `/gpgnet 127.0.0.1:port` and never
/// touches the internet itself; the client and its ICE adapter do.
///
/// Why it is worth the refactor:
///   * NAT traversal, Steam sockets, reconnection and relays can change with no game code
///     touched and no four-minute game reload to test.
///   * The mod's networking becomes one localhost connection that cannot fail for
///     interesting reasons.
///   * The launcher can show connection state and throughput, because every byte passes
///     through it.
///
/// The host/client ROLE still matters here — it decides whether this instance sends state
/// or applies it — but neither role opens a listening socket any more.
///
/// Three constraints from the earlier milestones shape the design, and none is optional:
///
///   * APPLY MUST RUN ON THE MAIN THREAD. StartGameExisting touches the content manager;
///     M3b died inside LoadImagesForFacilities when it was called from the simulation
///     thread. So the socket reader only enqueues, and DWGame.Update drains the queue.
///
///   * SERIALISING COSTS ~370 ms AND BLOCKS. The host does it on the simulation thread
///     every N ticks, so the host visibly hitches on each sync. That is honest for a
///     prototype and is the first thing M4b should fix (snapshot off-thread, or delta).
///
///   * THE SIMULATION ONLY ADVANCES IF GameServer.Now ADVANCES. The host runs DW2's own
///     clock (so its speed buttons work); the client does not simulate and its clock
///     FOLLOWS the host's, carried on every delta (FixedStep.FollowHost).
///
/// Framing is length-prefixed: [int32 payload length][byte type][payload]. Deliberately
/// dumb — a stream protocol that guesses at message boundaries is the classic way to
/// spend a day debugging something that is not the interesting problem.
/// </summary>
public static class NetSession
{
    internal enum Msg : byte
    {
        Hello = 1,
        FullState = 2,     // deflate-compressed galaxy bytes — the CORRECTION channel
        Command = 3,       // MessagePacket bytes — the STEADY-STATE channel
        StateSummary = 4,  // host -> client: tick + structural fingerprint. Tens of bytes.
        ResyncRequest = 5, // client -> host: "my structure disagrees, send me a full state"
        Delta = 6,         // host -> client: ship motion, applied IN PLACE. Hundreds of bytes.
        Content = 7,       // both ways: installed content packs, once DWGame.Initialize knows them
        DesyncReport = 8,  // client -> host: who diverged, at which host tick, both fingerprints (protocol 3)
        DesyncChoice = 9,  // either way: the popup button a player pressed (1 save and quit, 2 attempt resync)
        DesyncOutcome = 10 // client -> host: the resync it verified, succeeded or failed, with the failure count
    }

    public enum NetRole { Off, Host, Client }

    public static NetRole Role { get; private set; } = NetRole.Off;

    public static bool Active => Role != NetRole.Off;

    private static Action<string> _log;
    private static int _port;
    private static string _hostAddress;

    // --------------------------------------------------- outbound (host only)

    /// <summary>
    /// A ONE-SLOT outbox, newest wins. Full-state syncs supersede rather than accumulate:
    /// once a newer snapshot exists an older one has no value, so a queue would only add
    /// latency and memory to deliver worlds the client will immediately overwrite. This is
    /// the same reasoning the client already applies to its inbound backlog.
    /// </summary>
    private static byte[] _outbound;
    private static readonly AutoResetEvent _outboundReady = new(false);
    private static long _statesDropped;

    private static TcpListener _listener;
    private static TcpClient _peer;
    private static NetworkStream _stream;
    private static readonly object _sendGate = new();

    /// <summary>Received states waiting for the main thread. Only the newest matters.</summary>
    private static readonly ConcurrentQueue<byte[]> _inbound = new();

    private static long _lastSyncTick;
    private static int _syncsSent;
    private static int _syncsApplied;
    private static volatile bool _connected;

    // --- M4b command relay ---
    private static object _server;              // host: the live GameServer
    private static Type _messagePacketType;
    private static MethodInfo _packetWrite;
    private static MethodInfo _packetRead;
    private static FieldInfo _packetSerial;
    private static FieldInfo _packetTasks;
    private static FieldInfo _serverInputQueue;
    private static int _commandsSent;
    private static int _commandsInjected;

    private static long _sendCalls;
    private static long _sendCallsWithTasks;

    /// <summary>Relay an occasional empty packet so the path is testable without a player.</summary>
    private static bool RelayHeartbeat => Environment.GetEnvironmentVariable("DW2MP_RELAY_HEARTBEAT") == "1";

    private const long HeartbeatEvery = 1500;

    /// <summary>Host: remember the GameServer so relayed commands can be injected.</summary>
    public static void SetServer(object server) => _server ??= server;

    // --------------------------------------------------- compatibility: three axes
    //
    // Protocol version and game version travel in Hello; content packs travel in Content
    // once DWGame.Initialize knows them (see ContentPacks). Each side compares on receipt.
    // Any mismatch on any axis blocks state in BOTH directions: the client never asks for a
    // full state and the host never sends one, so the failure is a log line and two
    // single-player games rather than a corrupt galaxy or a renderer crash on the joiner
    // eighteen seconds after the join. The lobby refuses these same mismatches before the
    // games are even launched; this is the safety net for a session started some other way.

    private static string _peerContent;

    /// <summary>Why state must not cross the wire; null while the peers are compatible.</summary>
    private static volatile string _incompatibility;
    private static bool _incompatibilityLogged;

    /// <summary>The first reason wins, so the log names the root cause and not a consequence.</summary>
    private static void MarkIncompatible(string reason)
    {
        if (_incompatibility is null)
        {
            _incompatibility = reason;
        }
    }

    /// <summary>Called by ContentPacks once DWGame.Initialize has set the Installed flags.</summary>
    public static void OnContentKnown()
    {
        if (!Active)
        {
            return;
        }

        if (_connected)
        {
            Send(Msg.Content, System.Text.Encoding.UTF8.GetBytes(ContentPacks.Mine));
        }

        if (_peerContent is not null)
        {
            CompareContent();
        }
    }

    private static void ReadContent(byte[] payload)
    {
        _peerContent = System.Text.Encoding.UTF8.GetString(payload);

        var summary = _peerContent.Length == 0 ? "(none known)" : _peerContent.Replace(";", "  ");
        _log("# net: peer content " + summary);

        if (ContentPacks.Known)
        {
            CompareContent();
        }
    }

    private static void CompareContent()
    {
        var difference = ContentPacks.Compare(ContentPacks.Mine, _peerContent);
        if (difference.IsCompatible)
        {
            _log("# net: content OK — both games have the same content packs");
            return;
        }

        var sentence = difference.Describe("this game", "the other game");
        MarkIncompatible("content packs differ: " + sentence);
        _log("# net: *** CONTENT MISMATCH *** " + sentence +
             " — state will NOT be exchanged. Both players need the same DLC installed and enabled in Steam.");
    }

    /// <summary>True when state must not cross the wire. Logs the refusal once.</summary>
    private static bool IncompatibilityBlocksState(string what)
    {
        var reason = _incompatibility;
        if (reason is null)
        {
            return false;
        }

        if (!_incompatibilityLogged)
        {
            _incompatibilityLogged = true;
            _log($"# net[{Role}]: refusing to {what} — {reason}");
        }

        return true;
    }

    public static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var role = (Environment.GetEnvironmentVariable("DW2MP_ROLE") ?? "").Trim().ToLowerInvariant();
        Role = role switch { "host" => NetRole.Host, "client" => NetRole.Client, _ => NetRole.Off };
        if (Role == NetRole.Off) return;

        // The ONLY port the mod knows about is the launcher's local one. Remote address
        // and remote port are the launcher's business now.
        _port = int.TryParse(Environment.GetEnvironmentVariable("DW2MP_LOCAL_PORT"), out var p) ? p : 47810;
        _hostAddress = "127.0.0.1";
        // Full state is no longer sent on a timer at all -- see HostTick. It bootstraps a
        // join and repairs a divergence; commands carry everything in between. The old
        // DW2MP_SYNC_EVERY_TICKS knob is gone rather than left inert, because an env var
        // that silently does nothing is worse than one that does not exist.
        log($"# net: role={Role} launcher=127.0.0.1:{_port} — commands carry the steady state, " +
            $"full state on join/divergence (safety net every {MaxTicksBetweenFullStates} ticks)");

        // The client drains its queue on the main thread. The host does not need this
        // patch, but installing it in both roles keeps one code path.
        var gameType = AccessTools.TypeByName("DistantWorlds2.DWGame");
        var update = gameType is null ? null : AccessTools.Method(gameType, "Update");
        if (update is not null)
        {
            harmony.Patch(update, new HarmonyMethod(typeof(NetSession).GetMethod(
                nameof(OnMainThreadUpdate), BindingFlags.NonPublic | BindingFlags.Static)));
        }
        else log("# net: DWGame.Update not found; client cannot apply state");

        StateDelta.Log = log;
        DivergenceDump.Log = log;

        if (DivergenceDump.Enabled)
        {
            log("# net: *** DEV FLAG LIVE *** DW2MP_DIVERGENCE_DIR (with DW2MP_DEV=1): this game writes a census of its galaxy to disk at sampled summaries. Never set in a real session.");
        }
        GalaxyEventWatch.Log = log;

        if (Role == NetRole.Host)
        {
            _keyframes = new KeyframePolicy(LoadSyncSettings(log), MinSecondsBetweenServedStates, MaxTicksBetweenFullStates);
        }
        else
        {
            HostPause.Install(harmony, log);
        }
        InstallCommandRelay(harmony, log);

        var thread = new Thread(LauncherLoop)
        {
            IsBackground = true,   // must never keep the game alive on exit
            Name = "Dw2Mp.Net",
        };
        thread.Start();

        // The host's outbound sender lives on its own thread. See SenderLoop for why this
        // is a correctness requirement rather than a performance tweak.
        if (Role == NetRole.Host)
        {
            var sender = new Thread(SenderLoop)
            {
                IsBackground = true,
                Name = "Dw2Mp.Send",
            };
            sender.Start();
        }
    }

    // ------------------------------------------------------------ session

    /// <summary>
    /// Bumped whenever the wire format changes. Two peers with different values cannot
    /// talk, and finding that out in the handshake is far better than finding out via a
    /// corrupt galaxy halfway through a session.
    /// </summary>
    // 3 (2026-11-10): three desync messages added (DesyncReport, DesyncChoice, DesyncOutcome).
    // Hello itself is unchanged, so a protocol-2 peer still reads it and logs its own mismatch.
    // 4 (2026-11-10, desync reduction): StateSummary carries the full-state number it was built
    // against and a per-collection digest (SyncDigest) in place of one opaque fingerprint; a
    // Delta frame with an empty body is a clock-only sample; the delta body is version 9 with
    // five more sections. No new message type. Hello is still unchanged.
    private const int ProtocolVersion = 4;

    /// <summary>
    /// The Hello payload: protocol version, game version, and mode.
    ///
    /// Game version matters as much as protocol version. State transfer is DW2's own
    /// serialised galaxy, so two players on different game builds would exchange bytes
    /// that deserialise into nonsense — or throw somewhere deep and unhelpful. Checking
    /// it costs one string and turns an obscure crash into a clear message.
    /// </summary>
    private static byte[] BuildHello()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8);

        writer.Write(ProtocolVersion);
        writer.Write(GameVersion());
        writer.Write(Environment.GetEnvironmentVariable("DW2MP_MODE") ?? "coop-shared");
        writer.Flush();

        return buffer.ToArray();
    }

    private static void ReadHello(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            int protocol = reader.ReadInt32();
            string gameVersion = reader.ReadString();
            string mode = reader.ReadString();

            string mine = GameVersion();
            _log($"# net: peer protocol={protocol} game={gameVersion} mode={mode}");

            bool sameProtocol = protocol == ProtocolVersion;
            bool sameGameVersion = string.Equals(gameVersion, mine, StringComparison.Ordinal);

            // An older peer reads the same first three fields, so it logs its own
            // PROTOCOL MISMATCH line against OUR number. Fields are only ever appended to
            // Hello for that reason; a reordered Hello would leave the old side reading junk.
            if (!sameProtocol)
            {
                MarkIncompatible($"network protocol mismatch (peer {protocol}, ours {ProtocolVersion}); both players need the same mod build");
                _log($"# net: *** PROTOCOL MISMATCH *** peer={protocol} ours={ProtocolVersion} — " +
                     "state will NOT be exchanged. Both players need the same Dw2Mp build.");
            }

            if (!sameGameVersion)
            {
                MarkIncompatible($"game version mismatch (peer {gameVersion}, ours {mine}); both players need the same DW2 build");
                _log($"# net: *** GAME VERSION MISMATCH *** peer={gameVersion} ours={mine} — " +
                     "state will NOT be exchanged; it would not deserialise correctly.");
            }

            if (sameProtocol && sameGameVersion)
            {
                _log("# net: handshake OK — protocol and game version match");
            }
        }
        catch (Exception ex) { _log("# net: malformed Hello: " + ex.Message); }
    }

    /// <summary>DistantWorlds.Types' assembly version, e.g. 1.3.8.4; SnapshotHash pins its stream layout to it.</summary>
    internal static string GameVersion()
    {
        try
        {
            var asm = AccessTools.TypeByName("DistantWorlds.Types.Galaxy")?.Assembly;
            return asm?.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    // ------------------------------------------------------- command relay

    /// <summary>
    /// M4b. The client forwards its player commands to the host, which injects them into
    /// its own GameServer.InputQueue — the same queue the host's own client writes to in
    /// single-player. No new command format is invented: MessagePacket already has real
    /// ReadFromStream/WriteToStream (M0), so the game's own wire format is the protocol.
    ///
    /// Injection does NOT need the main thread. InputQueue is a
    /// ConcurrentDictionary&lt;int, MessagePacket&gt; and the server drains it, so a
    /// background thread may add to it safely — unlike state apply, which does.
    /// </summary>
    private static void InstallCommandRelay(Harmony harmony, Action<string> log)
    {
        _messagePacketType = AccessTools.TypeByName("DistantWorlds.Types.MessagePacket");
        var clientType = AccessTools.TypeByName("DistantWorlds.Types.GameClient");
        var serverType = AccessTools.TypeByName("DistantWorlds.Types.GameServer");

        if (_messagePacketType is null || clientType is null || serverType is null)
        {
            log("# net: command relay unavailable (types not found)");
            return;
        }

        _packetWrite = AccessTools.Method(_messagePacketType, "WriteToStream");
        _packetRead = AccessTools.Method(_messagePacketType, "ReadFromStream");
        _packetSerial = AccessTools.Field(_messagePacketType, "SerialNumber");
        _packetTasks = AccessTools.Field(_messagePacketType, "GameTasks");
        _serverInputQueue = AccessTools.Field(serverType, "InputQueue");

        if (_packetWrite is null || _packetRead is null || _serverInputQueue is null)
        {
            log("# net: command relay unavailable (MessagePacket/InputQueue members not found)");
            return;
        }

        if (Role != NetRole.Client) { log("# net: command relay ready (host side)"); return; }

        // Client only: mirror every outgoing packet to the host.
        var send = AccessTools.Method(clientType, "SendMessageToServer");
        if (send is null) { log("# net: GameClient.SendMessageToServer not found"); return; }

        harmony.Patch(send, postfix: new HarmonyMethod(typeof(NetSession).GetMethod(
            nameof(OnClientSendMessage), BindingFlags.NonPublic | BindingFlags.Static)));

        log("# net: command relay armed on GameClient.SendMessageToServer");
    }

    /// <summary>
    /// Postfix, not prefix: the local call still runs, so the client executes its own
    /// command immediately and the host's next sync corrects it. That is client-side
    /// prediction, and it is free here because the client's state is overwritten anyway.
    /// </summary>
    private static void OnClientSendMessage(object[] __args)
    {
        if (Role != NetRole.Client || !_connected) return;

        try
        {
            var packet = __args is { Length: > 0 } ? __args[0] : null;
            if (packet is null) return;

            int tasks = CountTasks(packet);
            long calls = Interlocked.Increment(ref _sendCalls);
            if (tasks > 0) Interlocked.Increment(ref _sendCallsWithTasks);

            // Heartbeat. "No relay activity" is otherwise ambiguous between the method
            // never being called and it being called with nothing to relay — and those
            // need completely different responses.
            if (calls == 1 || calls % 2000 == 0)
                _log($"# net[client]: SendMessageToServer calls={calls} withTasks={_sendCallsWithTasks}");

            // Normally only packets carrying player intent are worth relaying;
            // SendMessageToServer runs every cycle carrying timing data. But with nobody
            // playing the client, no task-bearing packet is ever produced, so an occasional
            // empty one is relayed to prove the path end to end.
            bool heartbeat = RelayHeartbeat && tasks == 0 && calls % HeartbeatEvery == 0;
            if (tasks == 0 && !heartbeat) return;

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                _packetWrite.Invoke(packet, new object[] { writer });
                writer.Flush();
            }

            Send(Msg.Command, buffer.ToArray());
            _commandsSent++;
            _log($"# net[client]: relayed packet #{_commandsSent} ({buffer.Length:N0}B, {tasks} task(s){(heartbeat ? ", heartbeat" : "")})");
        }
        catch (Exception ex) { _log("# net[client]: command relay failed " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static int CountTasks(object packet)
    {
        try
        {
            var tasks = _packetTasks?.GetValue(packet);
            if (tasks is null) return 0;
            return tasks.GetType().GetProperty("Count")?.GetValue(tasks) is int c ? c : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Client: send a synthetic command packet every N ticks.
    ///
    /// Piggybacking on GameClient.SendMessageToServer does not work as a test: it is
    /// command-driven, not per-tick — measured at ONE call in nine minutes with nobody at
    /// the keyboard. So organic traffic cannot exercise the relay in an automated run.
    ///
    /// This builds an empty MessagePacket and sends it on a tick interval, which
    /// exercises the entire path — construct, serialise, frame, wire, deserialise, inject
    /// into the host's InputQueue — independently of whether a human is issuing orders.
    /// A real player's commands then travel the same path with tasks attached.
    /// </summary>
    public static void ClientHeartbeatTick(long tick)
    {
        if (Role != NetRole.Client || !_connected || !RelayHeartbeat) return;
        if (_messagePacketType is null || _packetWrite is null) return;
        if (tick - _lastHeartbeatTick < HeartbeatEveryTicks) return;
        _lastHeartbeatTick = tick;

        try
        {
            var packet = Activator.CreateInstance(_messagePacketType);
            _packetSerial?.SetValue(packet, unchecked((int)tick));

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                _packetWrite.Invoke(packet, new object[] { writer });
                writer.Flush();
            }

            Send(Msg.Command, buffer.ToArray());
            _commandsSent++;
            _log($"# net[client]: sent synthetic command #{_commandsSent} tick={tick} ({buffer.Length:N0}B)");
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log($"# net[client]: synthetic command failed {cause.GetType().Name}: {cause.Message}");
        }
    }

    private static long _lastHeartbeatTick;
    private const long HeartbeatEveryTicks = 400;

    /// <summary>Host: turn received bytes back into a MessagePacket and queue it.</summary>
    private static void InjectCommand(byte[] payload)
    {
        if (_server is null) { _log("# net[host]: command arrived before the server was captured; dropped"); return; }

        try
        {
            var packet = Activator.CreateInstance(_messagePacketType);

            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);
            _packetRead.Invoke(packet, new object[] { reader });

            var queue = _serverInputQueue.GetValue(_server);
            if (queue is null) { _log("# net[host]: server InputQueue is null"); return; }

            int serial = _packetSerial?.GetValue(packet) is int s ? s : Environment.TickCount;

            var tryAdd = queue.GetType().GetMethod("TryAdd", new[] { typeof(int), _messagePacketType });
            if (tryAdd is null) { _log("# net[host]: InputQueue has no TryAdd(int, MessagePacket)"); return; }

            bool added = tryAdd.Invoke(queue, new[] { (object)serial, packet }) is true;
            _commandsInjected++;

            _log($"# net[host]: INJECTED command #{_commandsInjected} serial={serial} " +
                 $"tasks={CountTasks(packet)} added={added}");
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            _log("# net[host]: command inject failed " + cause.GetType().Name + ": " + cause.Message);
        }
    }

    // ------------------------------------------------------------- launcher

    /// <summary>
    /// One connection, to the launcher, on localhost. Both roles do exactly this — the
    /// difference between host and client is what they SEND, not how they connect.
    ///
    /// It retries, because the game takes minutes to load and the launcher may be
    /// restarted while it does. A single failed connect at startup used to mean no
    /// networking for the whole session with nothing in the log to explain it.
    /// </summary>
    private static void LauncherLoop()
    {
        for (int attempt = 1; attempt <= 120 && !_connected; attempt++)
        {
            try
            {
                _peer = new TcpClient();
                _peer.Connect(_hostAddress, _port);
                _peer.NoDelay = true;
                _stream = _peer.GetStream();
                _connected = true;
            }
            catch (SocketException)
            {
                if (attempt == 1 || attempt % 20 == 0)
                    _log($"# net: waiting for the launcher on 127.0.0.1:{_port} (attempt {attempt})");
                Thread.Sleep(1000);
            }
        }

        if (!_connected)
        {
            _log("# net: gave up waiting for the launcher — running single-player");
            return;
        }

        try
        {
            _log($"# net[{Role}]: connected to launcher on 127.0.0.1:{_port}");
            Send(Msg.Hello, BuildHello());
            if (ContentPacks.Known) Send(Msg.Content, System.Text.Encoding.UTF8.GetBytes(ContentPacks.Mine));

            while (_connected)
            {
                var (type, payload) = Receive();
                if (type is null) break;

                switch (type)
                {
                    // Only the client applies state, and only the host injects commands.
                    // Anything arriving for the wrong role means the peers disagree about
                    // who is hosting, which is worth saying out loud.
                    // The receive side honours the same block as the send side: a peer that
                    // failed the handshake must not get its state or commands applied here
                    // just because it sent them anyway.
                    case Msg.FullState when Role == NetRole.Client:
                    {
                        if (IncompatibilityBlocksState("accept a full state"))
                        {
                            break;
                        }

                        byte[] raw = Decompress(payload);

                        // One slot, newest wins: a full state replaces whatever is still waiting
                        // to be adopted, so a host cannot stack them up in memory.
                        int replaced = 0;
                        while (_inbound.TryDequeue(out _))
                        {
                            replaced++;
                        }

                        _inbound.Enqueue(raw);
                        _log($"# net[client]: state received packed={payload.Length:N0}B raw={raw.Length:N0}B (queued" +
                             (replaced > 0 ? $", replacing {replaced} not yet adopted)" : ")"));
                        break;
                    }

                    case Msg.Delta when Role == NetRole.Client:
                        if (IncompatibilityBlocksState("accept a delta"))
                        {
                            break;
                        }

                        _inboundDeltas.Enqueue(payload);
                        Interlocked.Increment(ref _deltasReceived);
                        break;

                    // Only queued here. The comparison runs on the main thread, after every
                    // delta that arrived before this summary has been applied.
                    case Msg.StateSummary when Role == NetRole.Client:
                        if (IncompatibilityBlocksState("check a state summary"))
                        {
                            break;
                        }

                        OnStateSummaryReceived(payload);
                        break;

                    // The host does not send a state here: HostTick owns serialisation,
                    // because it must run on the simulation thread to get a coherent
                    // snapshot. This only raises the flag it watches.
                    case Msg.ResyncRequest when Role == NetRole.Host:
                        _resyncRequested = true;
                        _resyncRequestsReceived++;
                        if (LogThrottle.ShouldLog(_resyncRequestsReceived, 5, 50))
                        {
                            _log($"# net[host]: client requested a resync (#{_resyncRequestsReceived})");
                        }

                        break;

                    case Msg.Command when Role == NetRole.Host:
                        if (IncompatibilityBlocksState("inject a command"))
                        {
                            break;
                        }

                        InjectCommand(payload);
                        break;

                    // The desync popup (mitch, in chat 2026-11-10). A report only makes sense
                    // at the host; a choice may come from either side; the outcome is the
                    // client's verification. Payload parsing lives in Desync and is defensive.
                    case Msg.DesyncReport when Role == NetRole.Host:
                        if (IncompatibilityBlocksState("handle a desync message"))
                        {
                            break;
                        }

                        Desync.OnReportReceived(payload);
                        break;

                    case Msg.DesyncChoice:
                        if (IncompatibilityBlocksState("handle a desync message"))
                        {
                            break;
                        }

                        Desync.OnChoiceReceived(payload);
                        break;

                    case Msg.DesyncOutcome when Role == NetRole.Host:
                        if (IncompatibilityBlocksState("handle a desync message"))
                        {
                            break;
                        }

                        Desync.OnOutcomeReceived(payload);
                        break;

                    case Msg.Hello:
                        ReadHello(payload);
                        break;

                    case Msg.Content:
                        ReadContent(payload);
                        break;

                    default:
                        _log($"# net[{Role}]: unexpected {type} ({payload.Length:N0}B) — role mismatch?");
                        break;
                }
            }
        }
        catch (Exception ex) { _log($"# net[{Role}]: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            _connected = false;
            _log($"# net[{Role}]: disconnected from launcher");

            // The tolerance state belongs to the main thread; ask it to forget, do not touch it here.
            _toleranceResetPending = true;

            // A desync popup must not outlive the link: a paused host with no peer would
            // otherwise stay paused for a resync that can never be verified.
            Desync.OnDisconnected();
        }
    }

    // --------------------------------------------- host: what goes on the wire, and why
    //
    // Commands are the steady state. Full galaxy state is a CORRECTION, sent when the
    // client asks for one and otherwise not at all.
    //
    // It used to be the other way round: a 4.4 MB full state every 1,200 ticks, carrying
    // the consequences of the client's own actions back to it. That cost ~25-36 MB of
    // unreleasable native memory per adoption (StartGameExisting acquires resources for the
    // incoming galaxy without releasing the outgoing one's), so the client grew until it
    // died. Lowering the interval only slowed it: any design that adopts on a timer leaks
    // by construction.
    //
    // What replaces it:
    //
    //   every SummaryEveryTicks   a StateSummary — tick, the full-state number it was built
    //                             against, and a digest of the collections deltas carry. About
    //                             a hundred bytes.
    //   client disagrees          a real, sustained mismatch: the client asks for a repair,
    //                             and after three in a row raises the desync popup.
    //   keyframes                 a full state every few minutes of running game and after a
    //                             big event (KeyframePolicy), so state that deltas do not carry is
    //                             never stale for long.
    //   MaxTicksBetweenFullStates the old safety net, still there.
    //
    // The summary is STRUCTURAL on purpose, and since 2026-11-10 it covers ONLY what the
    // sync carries (StateDelta.BuildDigest): the members of the ship, colony, design,
    // character and fleet collections. Not positions, which drift by design; and not the
    // Empires and Orbs counts the first fingerprint (ApplyState.StructuralFingerprint)
    // folded in, which deltas never carried and which raised a false desync on 2026-11-10.

    private static long _lastSummaryTick;
    private static long _summariesSent;
    private static volatile bool _resyncRequested;
    private static long _resyncsServed;

    private const long SummaryEveryTicks = 300;

    private static long MaxTicksBetweenFullStates =>
        long.TryParse(Environment.GetEnvironmentVariable("DW2MP_MAX_TICKS_BETWEEN_STATES"), out var v)
            ? v : 36000;

    /// <summary>
    /// Called from the simulation thread each tick.
    ///
    /// Serialising a full state still costs ~100 ms of simulation thread, so it happens only
    /// when a state is actually going to be sent. The SEND itself is handed to SenderLoop —
    /// writing to the socket from here once deadlocked the host for good.
    /// </summary>
    public static void HostTick(object galaxy, long tick)
    {
        if (Role != NetRole.Host || !_connected) return;
        if (IncompatibilityBlocksState("send state")) return;

        NoteKeyframeTime();

        // Deltas first, and often: this is the channel that makes the client's world move.
        // A few hundred bytes against megabytes, and applied in place so it costs no adoption.
        //
        // A summary is always preceded by a delta built on the same tick (2026-11-10). The
        // summary describes the galaxy as it is NOW; if the last delta were up to 29 ticks
        // old, every ship built in that gap would read as a mismatch the client could do
        // nothing about. Built on the same tick, the summary describes exactly what the
        // delta stream has carried.
        //
        // No delta before the first full state: the joiner has nothing but its throwaway
        // galaxy to apply it to. The 2026-11-10 run sent 364 KB of every design and ship
        // there, built in 439 ms on the simulation thread, for a galaxy about to be replaced.
        bool summaryDue = tick - _lastSummaryTick >= SummaryEveryTicks;
        bool deltaDue = tick - _lastDeltaTick >= DeltaEveryTicks;
        bool joinerHasState = _syncsSent > 0;

        if (deltaDue || summaryDue)
        {
            _lastDeltaTick = tick;

            bool deltaSent = false;
            if (joinerHasState)
            {
                deltaSent = SendDelta(galaxy, tick);
            }

            // A still or paused galaxy sends no delta, and the delta tag was the only thing
            // telling the joiner the host's time and speed. So when nothing changed, the tag
            // goes alone: the joiner's clock stops when the host pauses.
            if (!deltaSent)
            {
                SendClockSample();
            }
        }

        // Cheap heartbeat: it is the thing that makes rare full states safe.
        if (summaryDue)
        {
            _lastSummaryTick = tick;

            if (joinerHasState)
            {
                WatchForBigEvents(galaxy);
            }

            SendStateSummary(galaxy, tick);
        }

        // When to serve a full state is KeyframePolicy's decision (src/Shared, tested): the
        // joiner asked, a desync choice, a big event, the periodic keyframe, or the safety net.
        // The floor on SERVED states (security review 2026-11-10) lives there too: a peer
        // sending ResyncRequest every frame cannot make the host serialise megabytes a tick.
        bool asked = _resyncRequested;
        KeyframeDecision decision = _keyframes.Decide(tick, asked, _desyncServeExemption);

        if (decision.RequestIgnored)
        {
            _resyncRequested = false;
            _throttledServeRequests++;
            if (_throttledServeRequests == 1 || _throttledServeRequests % 50 == 0)
            {
                _log($"# net[host]: resync request ignored -- {_keyframes.WallSecondsSinceServed:N0} s since the last full state (floor {MinSecondsBetweenServedStates:N0} s); {_throttledServeRequests} ignored so far");
            }
        }

        if (!decision.Serve)
        {
            return;
        }

        bool servedOnRequest = decision.Reason == KeyframeReason.ClientAsked || decision.Reason == KeyframeReason.DesyncChoice;

        _resyncRequested = false;
        _desyncServeExemption = false;
        _lastSyncTick = tick;
        if (servedOnRequest)
        {
            _resyncsServed++;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            byte[] raw = ApplyState.SerialiseGalaxy(galaxy);
            double serialiseMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            byte[] packed = Compress(raw);
            double packMs = sw.Elapsed.TotalMilliseconds;

            var superseded = Interlocked.Exchange(ref _outbound, packed);
            if (superseded is not null) Interlocked.Increment(ref _statesDropped);
            _outboundReady.Set();

            // The full state IS the new baseline: the client is about to adopt exactly this
            // galaxy, so nothing in it has changed as far as that client is concerned, and
            // the next delta should carry only what happens after it. Priming rather than
            // clearing is what stops the first delta after every full state from re-sending
            // the entire galaxy the client just received.
            StateDelta.PrimeBaseline(galaxy);
            _keyframes.NoteServed(tick);
            GalaxyEventWatch.Rebase(galaxy);

            _syncsSent++;
            _log($"# net[host]: full state #{_syncsSent} tick={tick} " +
                 $"({DescribeKeyframe(decision)}) raw={raw.LongLength:N0}B " +
                 $"packed={packed.Length:N0}B serialise={serialiseMs:N0}ms pack={packMs:N0}ms queued" +
                 (superseded is null ? "" : $" (superseded 1, {Interlocked.Read(ref _statesDropped)} total)"));
        }
        catch (Exception ex)
        {
            _log("# net[host]: full state failed " + ex.GetType().Name + ": " + ex.Message);
            _connected = false;
        }
    }


    private static long _lastDeltaTick;
    private static long _deltasSent;
    private static long _deltaBytes;

    /// <summary>
    /// How often the client's world moves. 30 ticks is 3 s of simulated time at the
    /// fixed step, which is frequent enough to look continuous and rare enough that the
    /// per-ship change threshold still filters most ships out of most deltas.
    /// </summary>
    private const long DeltaEveryTicks = 30;

    /// <summary>Host: send what moved. Nothing moved means nothing is sent, and false comes back.</summary>
    private static bool SendDelta(object galaxy, long tick)
    {
        try
        {
            // Timed because the whole-object sections serialise every fleet and character
            // to detect change at all, on the simulation thread. That is the one part of
            // delta building whose cost scales with something other than what moved, so it
            // is the part worth watching rather than assuming.
            var sw = Stopwatch.StartNew();
            var payload = StateDelta.Build(galaxy, out int changed, out int total);
            var buildMs = sw.Elapsed.TotalMilliseconds;

            _deltaBuildMs += buildMs;
            if (buildMs > _worstDeltaBuildMs) { _worstDeltaBuildMs = buildMs; _worstDeltaAt = _deltasSent + 1; }

            // Every slow build is reported the moment it happens, with WHICH build it was,
            // what it spent per section, and whether a collection ran inside it. A running
            // average cannot explain an outlier, and a "worst" with no index cannot say
            // whether the outlier was startup or steady state.
            if (buildMs > SlowBuildMs)
                _log($"# net[host]: SLOW delta build #{_deltasSent + 1} {buildMs:N1}ms " +
                     $"[{StateDelta.LastBuildBreakdown()}] [{StateDelta.LastBuildCollections()}] [{StateDelta.LastWholeCounts()}]");

            if (payload is null) return false;

            Send(Msg.Delta, TagDelta(payload));
            _deltasSent++;
            _deltaBytes += payload.Length;

            if (_deltasSent == 1) _log("# net[host]: galaxy census — " + StateDelta.Census(galaxy));

            if (_deltasSent % 50 == 1)
                _log($"# net[host]: delta #{_deltasSent} tick={tick} {changed} record(s) changed " +
                     $"({total} ships total) ({payload.Length:N0}B; {_deltaBytes:N0}B total, " +
                     $"vs {_syncsSent} full state(s)) build={buildMs:N1}ms " +
                     $"avg={_deltaBuildMs / Math.Max(1, _deltasSent):N1}ms worst={_worstDeltaBuildMs:N1}ms@#{_worstDeltaAt} " +
                     $"[{StateDelta.SectionTimings()}] [{StateDelta.LastWholeCounts()}]");

            return true;
        }
        catch (Exception ex)
        {
            _log("# net[host]: delta failed " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static long _clockSamplesSent;

    /// <summary>
    /// Host: the delta tag with no delta behind it. Twenty bytes that carry the host's time
    /// and speed (0 when paused) on a tick where nothing in the galaxy changed.
    /// </summary>
    private static void SendClockSample()
    {
        try
        {
            Send(Msg.Delta, TagDelta(Array.Empty<byte>()));
            _clockSamplesSent++;
        }
        catch (Exception ex)
        {
            _log("# net[host]: clock sample failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ------------------------------------------------------------- host: keyframes

    private static KeyframePolicy _keyframes;
    private static long _lastKeyframeStamp;

    /// <summary>sync-settings.json beside the session files, with DW2MP_KEYFRAME_MINUTES over it.</summary>
    private static SyncSettings LoadSyncSettings(Action<string> log)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dw2Mp",
            "sync-settings.json");

        string json = "";
        try
        {
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
        }
        catch (Exception ex)
        {
            log("# net[host]: could not read " + path + ": " + ex.Message);
        }

        string environmentMinutes = Environment.GetEnvironmentVariable("DW2MP_KEYFRAME_MINUTES") ?? "";
        SyncSettings settings = SyncSettings.Parse(json, environmentMinutes, out string note);

        string interval = settings.KeyframeMinutes > 0
            ? $"every {settings.KeyframeMinutes:0.##} min of running game"
            : "periodic keyframes OFF";
        log($"# net[host]: keyframes {interval}, on big events = {settings.KeyframeOnEvents} ({note}; file {path})");

        return settings;
    }

    /// <summary>
    /// Feed the keyframe policy wall time, counted only while the host's game is running.
    /// Wall time and not ticks: the tick follows GameLogicCycleLengthInMilliseconds, measured at
    /// about 29 a second on 2026-11-10 where older comments here assume 10.
    /// </summary>
    private static void NoteKeyframeTime()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = _lastKeyframeStamp;
        _lastKeyframeStamp = now;

        if (previous == 0)
        {
            return;
        }

        double elapsedSeconds = (now - previous) / (double)Stopwatch.Frequency;
        var (_, hostSpeed) = HostClock();
        _keyframes.NoteTime(elapsedSeconds, hostSpeed > 0f);
    }

    /// <summary>
    /// Diff the galaxy's big facts against the last look (GalaxyEventWatch) and queue a
    /// keyframe for anything that happened. Runs at the summary cadence.
    /// </summary>
    private static void WatchForBigEvents(object galaxy)
    {
        try
        {
            List<string> events = GalaxyEventWatch.Detect(galaxy);
            foreach (string description in events)
            {
                _keyframes.NoteEvent(description);
                _log($"# net[host]: big event -- {description}; keyframe queued ({_keyframes.PendingEventCount} pending)");
            }
        }
        catch (Exception ex)
        {
            _log("# net[host]: event watch failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string DescribeKeyframe(KeyframeDecision decision)
    {
        return decision.Reason switch
        {
            KeyframeReason.ClientAsked => "client asked",
            KeyframeReason.DesyncChoice => "desync popup: attempt resync",
            KeyframeReason.Event => "keyframe after " + decision.Description,
            KeyframeReason.Periodic => "periodic keyframe",
            KeyframeReason.SafetyInterval => "safety interval",
            _ => "unknown reason",
        };
    }

    private static double _deltaBuildMs;
    private static double _worstDeltaBuildMs;
    private static long _worstDeltaAt;

    /// <summary>Anything above this is an outlier worth a line of its own, not a statistic.</summary>
    private const double SlowBuildMs = 5.0;
    /// <summary>
    /// Tick plus a structural fingerprint. Tens of bytes, computed by reading two fields
    /// per ship — against 24 MB of serialisation and ~100 ms for a full state.
    /// </summary>
    private static void SendStateSummary(object galaxy, long tick)
    {
        try
        {
            // What the sync carries, and only that: see SyncDigest for why the old fingerprint
            // (ship ids plus the Empires and Orbs counts) raised false desyncs.
            SyncDigest digest = StateDelta.BuildDigest(galaxy, isHost: true);
            string fingerprint = digest.Describe();

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(tick);
                writer.Write(CurrentBaseline);
                digest.Write(writer);
                writer.Flush();
            }

            Send(Msg.StateSummary, buffer.ToArray());
            _summariesSent++;

            // Dev measurement: this side's census for this tick, to diff against the client's.
            if (DivergenceDump.ShouldSample(tick))
            {
                DivergenceDump.Write(galaxy, "host", tick, $"baseline={_syncsSent} deltasSent={_deltasSent} fp={fingerprint}");
            }

            // One line every 20 summaries: often enough to see the channel is alive, rare
            // enough not to bury the full states, which are the interesting events now.
            if (_summariesSent % 20 == 1)
                _log($"# net[host]: summary #{_summariesSent} tick={tick} fp={fingerprint} " +
                     $"({buffer.Length}B) — full states so far: {_syncsSent} ({_resyncsServed} on request)");
        }
        catch (Exception ex)
        {
            _log("# net[host]: summary failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // -------------------------------------------------------------- client

    private static bool _loggedHold;

    /// <summary>
    /// Deltas waiting for the main thread. Unlike full states these are NOT superseded --
    /// each one carries only what changed since the last, so dropping one loses those ships'
    /// movement permanently until they next move. They are tiny, so draining all of them is
    /// cheap; the queue exists only to get off the network thread.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _inboundDeltas = new();

    private static long _deltasApplied;
    private static long _deltaShipsApplied;

    /// <summary>
    /// Main-thread pump. Applies at most one state per frame, and drops any backlog:
    /// with full-state syncs an older snapshot has no value once a newer one has arrived,
    /// and applying them in sequence would just stutter through stale worlds.
    /// </summary>
    private static void OnMainThreadUpdate()
    {
        // Both roles: a queued desync popup is shown here, on the only thread that may touch the UI.
        Desync.OnMainThreadUpdate();

        if (Role != NetRole.Client) return;

        DrainDeltas();
        HostPause.FollowHost();
        ProcessPendingSummaries();

        if (_inbound.IsEmpty) return;

        // HOLD, do not drop. The host starts syncing on its own schedule, and its first
        // frame routinely beats the client's game into existence. Dropping it left the
        // client running its own throwaway galaxy for the whole session -- two people in
        // two universes, which is the one outcome this design exists to prevent. Keeping
        // the frame queued costs one buffer and resolves itself within a few frames.
        if (!ApplyState.CanApply)
        {
            if (!_loggedHold)
            {
                _loggedHold = true;
                _log("# net[client]: state arrived before the game was ready — holding it");
            }
            return;
        }

        // A full state this side did not ask for waits until enough time has passed since
        // the last adoption (AdoptionGate): every adoption costs native memory that never
        // comes back, and the host decides when to send one. Asked-for states are never held.
        bool solicited = _syncsApplied == 0 || _resyncOutstanding || Desync.VerifyingResync;
        double secondsSinceLastAdoption = SecondsSince(_lastAdoptionStamp);
        if (!AdoptionGate.MayAdoptNow(solicited, secondsSinceLastAdoption))
        {
            if (!_loggedAdoptionHold)
            {
                _loggedAdoptionHold = true;
                _unsolicitedStatesHeld++;
                _log($"# net[client]: holding an unsolicited full state -- the last adoption was {secondsSinceLastAdoption:N0} s ago " +
                     $"(minimum {AdoptionGate.MinSecondsBetweenUnsolicited:N0} s); {_unsolicitedStatesHeld} held so far");
            }

            return;
        }

        byte[] newest = null;
        int dropped = -1;
        while (_inbound.TryDequeue(out var next))
        {
            newest = next;
            dropped++;
        }

        if (newest is null)
        {
            return;
        }

        if (dropped > 0)
        {
            _log($"# net[client]: dropped {dropped} stale state(s)");
        }

        _loggedAdoptionHold = false;
        _resyncOutstanding = false;
        _lastAdoptionStamp = Stopwatch.GetTimestamp();

        if (ApplyState.ApplyBytes(newest, out var info))
        {
            _syncsApplied++;
            _log($"# net[client]: APPLIED sync #{_syncsApplied}  {info}");
            ReleaseHeldDeltas();
        }
        else
        {
            _log("# net[client]: apply FAILED " + info);
        }
    }

    private static long _lastAdoptionStamp;
    private static volatile bool _resyncOutstanding;
    private static bool _loggedAdoptionHold;
    private static long _unsolicitedStatesHeld;

    /// <summary>Wall seconds since a Stopwatch timestamp; a very large number when there is none yet.</summary>
    private static double SecondsSince(long stamp)
    {
        if (stamp == 0)
        {
            return double.MaxValue;
        }

        return (Stopwatch.GetTimestamp() - stamp) / (double)Stopwatch.Frequency;
    }

    internal static bool Connected => _connected;

    // ------------------------------------------------------------- framing

    private static void Send(Msg type, byte[] payload)
    {
        lock (_sendGate)
        {
            var header = new byte[5];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), payload.Length);
            header[4] = (byte)type;

            _stream.Write(header, 0, header.Length);
            if (payload.Length > 0) _stream.Write(payload, 0, payload.Length);
            _stream.Flush();
        }
    }

    private static (Msg? Type, byte[] Payload) Receive()
    {
        var header = ReadExactly(5);
        if (header is null) return (null, null);

        int length = BitConverter.ToInt32(header, 0);
        var type = (Msg)header[4];

        // A corrupt length would otherwise allocate gigabytes before failing.
        if (length < 0 || length > 256 * 1024 * 1024)
        {
            _log($"# net: refusing implausible frame length {length}");
            return (null, null);
        }

        // Per-type cap for the desync messages: they carry a few primitives and short strings.
        // A bigger one is not ours; returning null ends the receive loop and the link.
        if (IsDesyncMessage(type) && length > MaxDesyncFrameBytes)
        {
            _log($"# net: {type} frame of {length:N0}B exceeds the {MaxDesyncFrameBytes}B cap; dropping the connection");
            return (null, null);
        }

        // A delta is kilobytes; the largest ever seen was 364 KB.
        if (type == Msg.Delta && length > WireLimits.MaxDeltaFrameBytes)
        {
            _log($"# net: Delta frame of {length:N0}B exceeds the {WireLimits.MaxDeltaFrameBytes:N0}B cap; dropping the connection");
            return (null, null);
        }

        // A summary is a tick, a baseline and at most sixteen short digest entries.
        if (type == Msg.StateSummary && length > MaxSummaryFrameBytes)
        {
            _log($"# net: StateSummary frame of {length:N0}B exceeds the {MaxSummaryFrameBytes}B cap; dropping the connection");
            return (null, null);
        }

        var payload = length == 0 ? Array.Empty<byte>() : ReadExactly(length);
        return payload is null ? (null, null) : (type, payload);
    }

    /// <summary>
    /// TCP is a stream: a single Read can return fewer bytes than asked for, and treating
    /// one Read as one message is the classic way to get intermittent corruption.
    /// </summary>
    private static byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        int offset = 0;

        while (offset < count)
        {
            int read;
            try { read = _stream.Read(buffer, offset, count - offset); }
            catch { return null; }

            if (read <= 0) return null;
            offset += read;
        }

        return buffer;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(48 * 1024 * 1024);

        // A bounded copy, not CopyTo: deflate expands a thousandfold, so a few megabytes on the
        // wire could otherwise become gigabytes here. Past the cap the exception ends the
        // receive loop, which drops the link.
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = deflate.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > WireLimits.MaxRawStateBytes)
            {
                throw new InvalidDataException($"full state expands past the {WireLimits.MaxRawStateBytes:N0}B cap");
            }

            output.Write(chunk, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Writes queued state to the socket, off the simulation thread.
    ///
    /// This exists for CORRECTNESS, not throughput. A socket write blocks when the far end
    /// stops reading, and a full state is ~4.4 MB — far more than any buffer will absorb.
    /// With the write inline on the simulation thread, a client that hangs, stalls or dies
    /// takes the HOST down with it: the host froze mid-sync on 2026-09-08 and never ticked
    /// again. A host must survive any client behaviour, including malicious.
    ///
    /// Blocking here is harmless: the sim keeps running, and each new sync simply replaces
    /// whatever this thread has not managed to send yet.
    /// </summary>
    private static void SenderLoop()
    {
        while (true)
        {
            try
            {
                // Timed wait, so a state queued during a lost connection is still picked
                // up once it returns rather than waiting for the next Set().
                _outboundReady.WaitOne(TimeSpan.FromMilliseconds(250));

                var payload = Interlocked.Exchange(ref _outbound, null);
                if (payload is null || !_connected) continue;

                var sw = Stopwatch.StartNew();
                Send(Msg.FullState, payload);
                var ms = sw.Elapsed.TotalMilliseconds;

                // Only worth a line when it actually blocked; a healthy send is ~2 ms and
                // logging every one would bury the interesting case.
                if (ms > 1000)
                    _log($"# net[host]: send took {ms:N0}ms — the client is not keeping up");
            }
            catch (Exception ex)
            {
                _log("# net[host]: send failed " + ex.GetType().Name + ": " + ex.Message);
                _connected = false;
            }
        }
    }

    // ------------------------------------------ client: deciding when a full state is due

    private static long _summariesSeen;
    private static long _resyncsRequested;
    private static DateTime _lastResyncRequest = DateTime.MinValue;

    /// <summary>
    /// The tolerance rules and the run-of-mismatches counter (src/Shared/SyncDigest.cs, tested).
    /// Three real mismatches in a row are a desync, as before; what changed on 2026-11-10 is
    /// what counts as one. A summary built against a different full state is not compared at
    /// all, and a gap the host says it has not sent yet is latency, not divergence.
    /// </summary>
    private static readonly DesyncTolerance _tolerance = new();

    /// <summary>
    /// Summaries waiting for the main thread, each with the number of deltas that had arrived
    /// before it (deltas and summaries travel in two queues, so "after DrainDeltas" alone is
    /// not "after every earlier delta"). SummaryGate (src/Shared, tested) hands the main
    /// thread at most one per call and at most one a second: the newest that is ready.
    /// </summary>
    private static readonly SummaryGate _summaryGate = new();

    /// <summary>Set by the network thread when the link drops; the main thread does the forgetting.</summary>
    private static volatile bool _toleranceResetPending;

    private static readonly Stopwatch _sessionClock = Stopwatch.StartNew();

    /// <summary>Deltas the network thread has queued, ever.</summary>
    private static long _deltasReceived;

    /// <summary>Deltas the main thread has taken off the queue (applied, held or dropped), each counted once.</summary>
    private static long _deltasTaken;

    private static long _gateDropsLogged;
    private static long _checksMatched;
    private static long _checksSkipped;
    private static long _checksTolerated;
    private static long _unreadableSummaries;
    private static int _mismatchRunLogged;

    /// <summary>
    /// A floor on how often a full state can be requested.
    ///
    /// 60 s because deltas do the job the full state used to do. The client's world MOVES
    /// continuously on a few hundred bytes; a full state is how it repairs a real mismatch, and
    /// adoption is the thing that costs ~30 MB of unreleasable native memory.
    /// </summary>
    private static readonly TimeSpan MinTimeBetweenResyncRequests = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Client, network thread: a summary arrived. Nothing is compared here.
    ///
    /// The comparison used to run on this thread the moment the packet landed, against a galaxy
    /// the main thread was still applying deltas to. So "the summary disagrees" could mean
    /// nothing more than "the delta sent just before it has not been applied yet". Now the
    /// summary waits its turn: the main thread applies every delta that arrived before it, then
    /// compares (ProcessPendingSummaries). The stream is ordered, so the comparison is too.
    /// </summary>
    private static void OnStateSummaryReceived(byte[] payload)
    {
        _summariesSeen++;

        // JOIN BOOTSTRAP. A client that has adopted nothing yet is not "in agreement", it is
        // empty: its galaxy is the throwaway it generated to have something for
        // StartGameExisting to replace. Ask immediately; there is nothing to compare against.
        if (_syncsApplied == 0)
        {
            string reason = ApplyState.CurrentGalaxy is null ? "no galaxy yet" : "joined, no state adopted yet";
            RequestResync(reason);
            return;
        }

        long deltasBefore = Interlocked.Read(ref _deltasReceived);
        _summaryGate.Offer(deltasBefore, payload);
    }

    /// <summary>Client, main thread, straight after DrainDeltas.</summary>
    private static void ProcessPendingSummaries()
    {
        if (_toleranceResetPending)
        {
            _toleranceResetPending = false;
            _tolerance.Reset();
            _summaryGate.Clear();
        }

        // At most ONE check per call, and SummaryGate allows at most one a second. A check
        // walks every carried collection on this thread; a flood of summaries used to mean a
        // flood of walks in a single frame (security review H1).
        long deltasTaken = Interlocked.Read(ref _deltasTaken);
        byte[] payload = _summaryGate.TakeNewestReady(deltasTaken, _sessionClock.Elapsed.TotalSeconds);

        long gateDrops = _summaryGate.Dropped + _summaryGate.Superseded;
        if (gateDrops >= _gateDropsLogged + 50)
        {
            _gateDropsLogged = gateDrops;
            _log($"# net[client]: {_summaryGate.Dropped} summary(ies) dropped from a full queue and {_summaryGate.Superseded} superseded by a newer one -- the host is sending them faster than one a second");
        }

        if (payload is null)
        {
            return;
        }

        try
        {
            CheckSummary(payload);
        }
        catch (Exception ex)
        {
            _checksFailed++;
            if (LogThrottle.ShouldLog(_checksFailed, 3, 50))
            {
                _log($"# net[client]: sync check failed {ex.GetType().Name} ({_checksFailed} so far)");
            }
        }
    }

    private static long _checksFailed;

    private static void CheckSummary(byte[] payload)
    {
        long hostTick;
        long summaryBaseline;
        SyncDigest hostDigest;

        using (var input = new MemoryStream(payload, writable: false))
        using (var reader = new BinaryReader(input, System.Text.Encoding.UTF8))
        {
            hostTick = reader.ReadInt64();
            summaryBaseline = reader.ReadInt64();
            hostDigest = SyncDigest.TryRead(reader);
        }

        if (hostDigest is null)
        {
            _unreadableSummaries++;
            if (_unreadableSummaries == 1 || _unreadableSummaries % 50 == 0)
            {
                _log($"# net[client]: summary at host tick {hostTick} has no readable digest ({_unreadableSummaries} so far); ignored");
            }

            return;
        }

        var galaxy = ApplyState.CurrentGalaxy;
        if (galaxy is null)
        {
            return;
        }

        SyncDigest mine = StateDelta.BuildDigest(galaxy, isHost: false);

        // The dev flag DW2MP_FORCE_DESYNC_AT_CYCLE replaces our digest until the popup has been
        // raised, so the whole path can be walked on one PC.
        if (Desync.ForcingDivergence())
        {
            mine = ForcedDigest(mine);
        }

        var context = new SyncCheckContext
        {
            SummaryBaseline = summaryBaseline,
            AppliedBaseline = _syncsApplied,
            DeltasHeld = _heldDeltas.Count,
            FullStateQueued = !_inbound.IsEmpty,
        };

        SyncCheckResult result = _tolerance.Evaluate(hostDigest, mine, context);

        // Dev measurement: this side's census for this host tick, written with every delta
        // before the summary already applied.
        if (DivergenceDump.ShouldSample(hostTick))
        {
            string dumpContext = $"baseline={_syncsApplied} summaryBaseline={summaryBaseline} verdict={result.Verdict} " +
                                 $"deltasApplied={_deltasApplied} deltasHeld={_heldDeltas.Count} [{StateDelta.MutationFailureSummary()}]";
            DivergenceDump.Write(galaxy, "client", hostTick, dumpContext);
        }

        if (result.Verdict == SyncVerdict.NotComparable)
        {
            _checksSkipped++;
            if (_checksSkipped == 1 || _checksSkipped % 20 == 0)
            {
                _log($"# net[client]: sync check skipped at host tick {hostTick} -- {result.Detail} ({_checksSkipped} skipped so far)");
            }

            return;
        }

        if (result.Verdict == SyncVerdict.Tolerated)
        {
            _checksTolerated++;
            if (LogThrottle.ShouldLog(_checksTolerated, 5, 20))
            {
                _log($"# net[client]: sync check tolerated at host tick {hostTick} -- {PeerText.Sanitise(result.Detail, MaxDetailLength)} ({_checksTolerated} tolerated so far)");
            }

            return;
        }

        bool matched = result.Verdict == SyncVerdict.Match;

        // A resync chosen on the popup is verified here: the first comparable summaries after
        // the new full state has landed decide whether it worked.
        if (Desync.VerifyingResync)
        {
            Desync.OnSummaryDuringResync(matched, _syncsApplied, hostTick);
            return;
        }

        if (matched)
        {
            _checksMatched++;

            if (_mismatchRunLogged > 0)
            {
                _log($"# net[client]: structure back in agreement after {_mismatchRunLogged} mismatch(es)");
                _mismatchRunLogged = 0;
            }

            if (_checksMatched % 20 == 1)
            {
                _log($"# net[client]: sync check OK #{_checksMatched} at host tick {hostTick} -- {mine.Describe()} " +
                     $"(skipped {_checksSkipped}, tolerated {_checksTolerated})");
            }

            return;
        }

        string detail = PeerText.Sanitise(result.Detail, MaxDetailLength);

        _mismatchRunLogged = result.ConsecutiveMismatches;
        if (LogThrottle.ShouldLog(result.ConsecutiveMismatches, DesyncTolerance.MismatchesBeforeDesync, 20))
        {
            _log($"# net[client]: sync check MISMATCH {result.ConsecutiveMismatches} of {DesyncTolerance.MismatchesBeforeDesync} at host tick {hostTick} -- {detail}");
        }

        // A desync popup is already open: the players are deciding, so nothing is asked for here.
        if (Desync.IncidentOpen)
        {
            return;
        }

        if (result.BecameSustained)
        {
            string hostText = PeerText.Sanitise(hostDigest.Describe(), MaxDetailLength);
            string mineText = mine.Describe();
            _log($"# net[client]: structure diverged — host {hostText} vs mine {mineText} at host tick {hostTick}");

            // mitch, in chat 2026-11-10: a detected desync raises the in-game popup (who
            // desynced; 1 save and quit, 2 attempt resync). The silent path below stays as
            // the fallback when the dialog API is not available.
            bool popupRaised = Desync.OnDivergenceConfirmed(hostTick, hostText, mineText);
            if (popupRaised)
            {
                return;
            }
        }

        // Before the popup: ask for a repair, quietly. A real mismatch is something only a
        // full state fixes, and if one fixes it the players never need to be asked. Both
        // floors still apply (60 s here, MinSecondsBetweenServedStates on the host).
        RequestResync($"sync check mismatched {result.ConsecutiveMismatches} time(s): {detail}");
    }

    /// <summary>Longest piece of digest text that goes into one log line.</summary>
    private const int MaxDetailLength = 300;

    /// <summary>A copy of our digest that cannot match: the ship entry is seven members out.</summary>
    private static SyncDigest ForcedDigest(SyncDigest mine)
    {
        var forced = new SyncDigest();

        foreach (SyncDigestEntry entry in mine.Entries)
        {
            if (entry.Name == "ships")
            {
                forced.Add(entry.Name, entry.Count + 7, entry.SetHash ^ 1UL, entry.Unsent);
                continue;
            }

            forced.Add(entry.Name, entry.Count, entry.SetHash, entry.Unsent);
        }

        return forced;
    }

    /// <summary>
    /// Ask the host for a full state, subject to the rate floor. One place, so the join
    /// bootstrap and the divergence path cannot drift apart on their limiting.
    /// </summary>
    private static void RequestResync(string reason, bool force = false)
    {
        if (IncompatibilityBlocksState("adopt the host's galaxy")) return;

        // A player who pressed "Attempt resync" must not be refused by the rate floor.
        if (!force && DateTime.UtcNow - _lastResyncRequest < MinTimeBetweenResyncRequests) return;

        _lastResyncRequest = DateTime.UtcNow;
        _resyncsRequested++;
        _resyncOutstanding = true;

        Send(Msg.ResyncRequest, Array.Empty<byte>());
        _log($"# net[client]: requested full state #{_resyncsRequested} — {reason}");
    }

    // ------------------------------------------------------------- desync popup glue

    /// <summary>Client: the popup's "Attempt resync", exempt from the rate floor.</summary>
    internal static void RequestResyncForDesync(string reason)
    {
        RequestResync(reason, force: true);
    }

    /// <summary>
    /// Host: serve a full state at the next HostTick, as if the client had asked. Called once
    /// per desync choice accepted on the popup, which is the one case allowed inside the
    /// served-state floor.
    /// </summary>
    internal static void MarkResyncRequestedForDesync()
    {
        _desyncServeExemption = true;
        _resyncRequested = true;
    }

    /// <summary>
    /// Wall seconds between served full states below which a plain ResyncRequest is ignored.
    /// It was 600 TICKS, documented as about 60 s; the tick turned out to be about 29 a second,
    /// so the floor was really 20 s. Seconds now, so it is what it says (2026-11-10).
    /// </summary>
    private const double MinSecondsBetweenServedStates = 60;

    private static long _resyncRequestsReceived;

    private static volatile bool _desyncServeExemption;

    private static long _throttledServeRequests;

    /// <summary>Desync frames are a few primitives and short strings; anything bigger is not ours and ends the link.</summary>
    private const int MaxDesyncFrameBytes = 1024;

    /// <summary>8 + 8 + 1 + 16 x (1 + 24 + 4 + 8 + 4) bytes is 673; anything past 2 KB is not a summary.</summary>
    private const int MaxSummaryFrameBytes = 2048;

    private static bool IsDesyncMessage(Msg type)
    {
        return type == Msg.DesyncReport || type == Msg.DesyncChoice || type == Msg.DesyncOutcome;
    }

    internal static int SyncsApplied => _syncsApplied;

    /// <summary>Send one of the desync messages, or nothing when the launcher link is down.</summary>
    internal static void SendDesync(Msg type, byte[] payload)
    {
        if (!_connected || _stream is null)
        {
            _log($"# net[{Role}]: {type} not sent -- not connected");
            return;
        }

        try
        {
            Send(type, payload);
        }
        catch (Exception ex)
        {
            _log($"# net[{Role}]: {type} send failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Apply queued deltas to the live galaxy, on the main thread, IN BASELINE ORDER.
    ///
    /// Main thread because the renderer reads these same ship positions every frame. The
    /// client's own simulation is stopped, so nothing else is writing them.
    ///
    /// Each delta carries the full-state number it was built against. A delta for the state
    /// the client has applied is applied; one for a state still in transit is HELD and
    /// re-queued when that state lands; one for a superseded state is dropped. Without this,
    /// a delta that overtook its full state on the wire was applied to the galaxy the state
    /// was about to replace, and everything it created vanished with that galaxy.
    /// </summary>
    private static void DrainDeltas()
    {
        if (_inboundDeltas.IsEmpty) return;

        var galaxy = ApplyState.CurrentGalaxy;
        if (galaxy is null) return;

        while (_inboundDeltas.TryDequeue(out var tagged))
        {
            Interlocked.Increment(ref _deltasTaken);

            var (baseline, payload) = UntagDelta(tagged);
            if (payload is null) continue;

            // The host serves one full state at a time, so a delta can be for the state after
            // ours and no further. Anything beyond that is not a delta this session produced.
            if (baseline > _syncsApplied + 1)
            {
                _deltasRefused++;
                if (LogThrottle.ShouldLog(_deltasRefused, 1, 50))
                {
                    _log($"# net[client]: refused a delta for full state #{baseline} (have #{_syncsApplied}); {_deltasRefused} refused so far");
                }

                continue;
            }

            if (baseline > _syncsApplied)
            {
                // Held deltas wait in memory for a state that may never come, so there is a
                // limit. Past it the delta is dropped; the state it belonged to, when it
                // lands, is followed by a mismatch and a repair instead of unbounded growth.
                bool heldQueueFull = _heldDeltas.Count >= MaxHeldDeltas || _heldDeltaBytes + tagged.Length > MaxHeldDeltaBytes;
                if (heldQueueFull)
                {
                    _deltasRefused++;
                    if (LogThrottle.ShouldLog(_deltasRefused, 1, 50))
                    {
                        _log($"# net[client]: held-delta limit reached ({_heldDeltas.Count} deltas, {_heldDeltaBytes:N0}B); delta dropped, {_deltasRefused} refused so far");
                    }

                    continue;
                }

                _heldDeltaBytes += tagged.Length;

                // Built against a full state we have not applied yet. Keep it, in order.
                _heldDeltas.Enqueue(tagged);
                if (++_deltasHeld == 1 || _deltasHeld % 50 == 0)
                    _log($"# net[client]: holding delta for full state #{baseline} (have #{_syncsApplied}); {_deltasHeld} held so far");
                continue;
            }

            if (baseline < _syncsApplied)
            {
                // Built against a galaxy we have already replaced. Nothing in it is true now.
                if (++_deltasDropped == 1 || _deltasDropped % 50 == 0)
                    _log($"# net[client]: dropped delta for superseded full state #{baseline} (have #{_syncsApplied}); {_deltasDropped} dropped so far");
                continue;
            }

            // The tag alone: nothing changed on the host this tick, but its clock (and its
            // pause) still has to reach us.
            if (payload.Length == 0)
            {
                FollowHostClock(tagged);
                _clockSamplesApplied++;
                continue;
            }

            if (StateDelta.Apply(galaxy, payload, out var info))
            {
                FollowHostClock(tagged);
                _deltasApplied++;
                if (_deltasApplied % 50 == 1)
                    _log($"# net[client]: delta #{_deltasApplied} applied — {info}  [totals: {StateDelta.AppliedTotals()}] [{StateDelta.MutationFailureSummary()}]");
                continue;
            }

            // A delta that cannot be READ is a protocol problem, not a state one.
            _log($"# net[client]: delta unreadable ({info}); requesting a full state");
            RequestResync("delta unreadable: " + info);
            return;
        }
    }

    /// <summary>
    /// After a full state lands, deltas held for it become applicable. Move them back to the
    /// front of the inbound queue in the order they arrived.
    /// </summary>
    private static void ReleaseHeldDeltas()
    {
        if (_heldDeltas.IsEmpty) return;

        var pending = new List<byte[]>();
        while (_heldDeltas.TryDequeue(out var d)) pending.Add(d);
        _heldDeltaBytes = 0;

        // These were counted when they were first taken and will be taken again.
        Interlocked.Add(ref _deltasTaken, -pending.Count);

        while (_inboundDeltas.TryDequeue(out var d)) pending.Add(d);
        foreach (var d in pending) _inboundDeltas.Enqueue(d);

        _log($"# net[client]: released {pending.Count} held delta(s) against full state #{_syncsApplied}");
    }
    // ------------------------------------------------ delta ordering against full states
    //
    // A delta is only meaningful against the full state it was built after. The host sends
    // full states through the sender thread's one-slot outbox and deltas inline from
    // HostTick, so a delta built AFTER snapshot T can reach the client BEFORE T does. Applied
    // then, it lands on the galaxy T is about to replace and is lost with it; every ship it
    // created is absent until the next full state, and every update naming one is skipped.
    // That was the residual 1-20 "absent" per delta that survived fixing creation itself.
    //
    // So every delta carries the number of the full state it is relative to, and the client
    //   applies it   if that number equals the full state it has applied,
    //   holds it     if it is for a full state still in transit,
    //   drops it     if it is for one already superseded.
    // Held deltas are re-queued in order once their full state lands.

    private static readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _heldDeltas = new();
    private static long _deltasHeld, _deltasDropped;
    private static long _deltasRefused;
    private static long _heldDeltaBytes;

    /// <summary>About five minutes of deltas at one a second; an honest host's full state lands in seconds.</summary>
    private const int MaxHeldDeltas = 512;

    private const long MaxHeldDeltaBytes = 16L * 1024 * 1024;
    private static long _clockSamplesApplied;

    /// <summary>Host: which full state the next deltas are relative to. Read on the sim thread.</summary>
    private static long CurrentBaseline => _syncsSent;

    // The tag also carries the host's CLOCK: GameServer.Now as DateTime ticks, and the
    // game speed (0 when paused). The client does not simulate, so its clock is whatever
    // it is told; see FixedStep.FollowHost. 20 bytes on a message of hundreds.
    private const int TagBytes = 8 + 8 + 4;

    private static MethodInfo _serverNow, _serverSpeed, _serverIsRunning;

    private static (long NowTicks, float Speed) HostClock()
    {
        try
        {
            if (_server is null) return (0, 0);
            var t = _server.GetType();
            _serverNow ??= AccessTools.PropertyGetter(t, "Now");
            _serverSpeed ??= AccessTools.Method(t, "GetGameSpeed");
            _serverIsRunning ??= AccessTools.PropertyGetter(t, "IsRunning");

            var now = (DateTime)(_serverNow?.Invoke(_server, null) ?? DateTime.MinValue);
            bool running = _serverIsRunning?.Invoke(_server, null) as bool? ?? true;
            float speed = running ? (_serverSpeed?.Invoke(_server, null) as float? ?? 1f) : 0f;
            return (now.Ticks, speed);
        }
        catch { return (0, 0); }
    }

    private static byte[] TagDelta(byte[] delta)
    {
        var (nowTicks, speed) = HostClock();
        var tagged = new byte[delta.Length + TagBytes];
        BitConverter.TryWriteBytes(tagged.AsSpan(0, 8), CurrentBaseline);
        BitConverter.TryWriteBytes(tagged.AsSpan(8, 8), nowTicks);
        BitConverter.TryWriteBytes(tagged.AsSpan(16, 4), speed);
        Buffer.BlockCopy(delta, 0, tagged, TagBytes, delta.Length);
        return tagged;
    }

    private static (long Baseline, byte[] Delta) UntagDelta(byte[] tagged)
    {
        if (tagged.Length < TagBytes) return (-1, null);
        var baseline = BitConverter.ToInt64(tagged, 0);
        var delta = new byte[tagged.Length - TagBytes];
        Buffer.BlockCopy(tagged, TagBytes, delta, 0, delta.Length);
        return (baseline, delta);
    }

    /// <summary>Client: hand the host's clock from a delta tag to the follow clock.</summary>
    private static void FollowHostClock(byte[] tagged)
    {
        if (tagged.Length < TagBytes) return;
        FixedStep.FollowHost(BitConverter.ToInt64(tagged, 8), BitConverter.ToSingle(tagged, 16));
    }
}
