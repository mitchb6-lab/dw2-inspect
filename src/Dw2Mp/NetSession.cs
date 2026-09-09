using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
///   * THE SIMULATION ONLY ADVANCES IF GameServer.Now ADVANCES. Both ends need FixedStep
///     or the host will sit inert and sync the same state forever.
///
/// Framing is length-prefixed: [int32 payload length][byte type][payload]. Deliberately
/// dumb — a stream protocol that guesses at message boundaries is the classic way to
/// spend a day debugging something that is not the interesting problem.
/// </summary>
public static class NetSession
{
    private enum Msg : byte
    {
        Hello = 1,
        FullState = 2,     // deflate-compressed galaxy bytes — the CORRECTION channel
        Command = 3,       // MessagePacket bytes — the STEADY-STATE channel
        StateSummary = 4,  // host -> client: tick + structural fingerprint. Tens of bytes.
        ResyncRequest = 5, // client -> host: "my structure disagrees, send me a full state"
        Delta = 6,         // host -> client: ship motion, applied IN PLACE. Hundreds of bytes.
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
    private const int ProtocolVersion = 1;

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

            if (protocol != ProtocolVersion)
                _log($"# net: *** PROTOCOL MISMATCH *** peer={protocol} ours={ProtocolVersion} — expect failure");

            if (!string.Equals(gameVersion, mine, StringComparison.Ordinal))
                _log($"# net: *** GAME VERSION MISMATCH *** peer={gameVersion} ours={mine} — " +
                     "state transfer will not deserialise correctly");

            if (protocol == ProtocolVersion && gameVersion == mine)
                _log("# net: handshake OK — protocol and game version match");
        }
        catch (Exception ex) { _log("# net: malformed Hello: " + ex.Message); }
    }

    private static string GameVersion()
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

            while (_connected)
            {
                var (type, payload) = Receive();
                if (type is null) break;

                switch (type)
                {
                    // Only the client applies state, and only the host injects commands.
                    // Anything arriving for the wrong role means the peers disagree about
                    // who is hosting, which is worth saying out loud.
                    case Msg.FullState when Role == NetRole.Client:
                    {
                        byte[] raw = Decompress(payload);
                        _inbound.Enqueue(raw);
                        _log($"# net[client]: state received packed={payload.Length:N0}B raw={raw.Length:N0}B (queued)");
                        break;
                    }

                    case Msg.Delta when Role == NetRole.Client:
                        _inboundDeltas.Enqueue(payload);
                        break;

                    case Msg.StateSummary when Role == NetRole.Client:
                        OnStateSummary(payload);
                        break;

                    // The host does not send a state here: HostTick owns serialisation,
                    // because it must run on the simulation thread to get a coherent
                    // snapshot. This only raises the flag it watches.
                    case Msg.ResyncRequest when Role == NetRole.Host:
                        _resyncRequested = true;
                        _log("# net[host]: client requested a resync");
                        break;

                    case Msg.Command when Role == NetRole.Host:
                        InjectCommand(payload);
                        break;

                    case Msg.Hello:
                        ReadHello(payload);
                        break;

                    default:
                        _log($"# net[{Role}]: unexpected {type} ({payload.Length:N0}B) — role mismatch?");
                        break;
                }
            }
        }
        catch (Exception ex) { _log($"# net[{Role}]: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _connected = false; _log($"# net[{Role}]: disconnected from launcher"); }
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
    //   every SummaryEveryTicks   a StateSummary — tick plus a structural fingerprint, tens
    //                             of bytes, ~50,000x smaller than a full state.
    //   client disagrees          the client asks for a resync and the host sends one.
    //   MaxTicksBetweenFullStates a safety net, so a client whose fingerprint somehow keeps
    //                             matching while its world is wrong still gets corrected.
    //
    // The summary is STRUCTURAL on purpose — see ApplyState.StructuralFingerprint. It
    // notices ships appearing and disappearing, not the positional drift that DW2's
    // non-determinism guarantees between two independently simulating processes.

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

        // Deltas first, and often: this is now the channel that makes the client's world
        // move. A few hundred bytes against 4.37 MB, and applied in place so it costs no
        // adoption at all.
        if (tick - _lastDeltaTick >= DeltaEveryTicks)
        {
            _lastDeltaTick = tick;
            SendDelta(galaxy, tick);
        }

        // Cheap heartbeat first: it is the thing that makes rare full states safe.
        if (tick - _lastSummaryTick >= SummaryEveryTicks)
        {
            _lastSummaryTick = tick;
            SendStateSummary(galaxy, tick);
        }

        bool asked = _resyncRequested;
        bool overdue = tick - _lastSyncTick >= MaxTicksBetweenFullStates;

        if (!asked && !overdue) return;

        _resyncRequested = false;
        _lastSyncTick = tick;
        if (asked) _resyncsServed++;

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

            _syncsSent++;
            _log($"# net[host]: full state #{_syncsSent} tick={tick} " +
                 $"({(asked ? "client asked" : "safety interval")}) raw={raw.LongLength:N0}B " +
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

    /// <summary>Host: send what moved. Nothing moved means nothing is sent.</summary>
    private static void SendDelta(object galaxy, long tick)
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
            if (buildMs > _worstDeltaBuildMs) _worstDeltaBuildMs = buildMs;

            if (payload is null) return;

            Send(Msg.Delta, payload);
            _deltasSent++;
            _deltaBytes += payload.Length;

            if (_deltasSent == 1) _log("# net[host]: galaxy census — " + StateDelta.Census(galaxy));

            if (_deltasSent % 50 == 1)
                _log($"# net[host]: delta #{_deltasSent} tick={tick} {changed} record(s) changed " +
                     $"({total} ships total) ({payload.Length:N0}B; {_deltaBytes:N0}B total, " +
                     $"vs {_syncsSent} full state(s)) build={buildMs:N1}ms " +
                     $"avg={_deltaBuildMs / Math.Max(1, _deltasSent):N1}ms worst={_worstDeltaBuildMs:N1}ms");
        }
        catch (Exception ex)
        {
            _log("# net[host]: delta failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static double _deltaBuildMs;
    private static double _worstDeltaBuildMs;
    /// <summary>
    /// Tick plus a structural fingerprint. Tens of bytes, computed by reading two fields
    /// per ship — against 24 MB of serialisation and ~100 ms for a full state.
    /// </summary>
    private static void SendStateSummary(object galaxy, long tick)
    {
        try
        {
            var fingerprint = ApplyState.StructuralFingerprint(galaxy);

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(tick);
                writer.Write(fingerprint ?? "");
                writer.Flush();
            }

            Send(Msg.StateSummary, buffer.ToArray());
            _summariesSent++;

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
        if (Role != NetRole.Client) return;

        DrainDeltas();

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

        byte[] newest = null;
        int dropped = -1;
        while (_inbound.TryDequeue(out var next)) { newest = next; dropped++; }
        if (newest is null) return;

        if (dropped > 0) _log($"# net[client]: dropped {dropped} stale state(s)");

        if (ApplyState.ApplyBytes(newest, out var info))
        {
            _syncsApplied++;
            _log($"# net[client]: APPLIED sync #{_syncsApplied}  {info}");
        }
        else _log("# net[client]: apply FAILED " + info);
    }

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
        deflate.CopyTo(output);
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
    private static int _consecutiveMismatches;
    private static long _resyncsRequested;
    private static string _lastHostFingerprint = "";
    private static DateTime _lastResyncRequest = DateTime.MinValue;

    /// <summary>
    /// How many summaries in a row must disagree before asking for 4.4 MB.
    ///
    /// Not one. The two sides sample at different instants — the host fingerprints as it
    /// ticks, the client compares whenever the packet lands — so a ship built or destroyed
    /// on the host legitimately shows as a single mismatch that resolves itself. Requiring a
    /// run of them means we react to a genuine structural divergence and not to the seam
    /// between two clocks.
    /// </summary>
    private const int MismatchesBeforeResync = 3;

    /// <summary>
    /// A floor on how often a full state can be requested — and now, in practice, the
    /// structure-refresh rate.
    ///
    /// 60 s rather than 20 s because deltas took over the job the full state used to do.
    /// The client's world MOVES continuously on a few hundred bytes; a full state is now
    /// only how it learns about ships that came into existence since the last one. Being a
    /// minute behind on newly-built ships costs far less than a 4.4 MB adoption every 20 s,
    /// and adoption is the thing that costs ~30 MB of unreleasable native memory.
    /// </summary>
    private static readonly TimeSpan MinTimeBetweenResyncRequests = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Client: compare the host's structural fingerprint with our own and decide whether we
    /// need a correction.
    ///
    /// Runs on the network thread. It only READS galaxy structure, which is why it can:
    /// the alternative, marshalling to the main thread, would make the check as expensive
    /// as the thing it exists to avoid.
    /// </summary>
    private static void OnStateSummary(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(input, System.Text.Encoding.UTF8);

            long hostTick = reader.ReadInt64();
            string hostFingerprint = reader.ReadString();

            _summariesSeen++;
            _lastHostFingerprint = hostFingerprint;

            var galaxy = ApplyState.CurrentGalaxy;

            // JOIN BOOTSTRAP. A client that has adopted nothing yet is not "in agreement",
            // it is empty — its galaxy is the throwaway it generated to have something for
            // StartGameExisting to replace. Ask immediately rather than waiting for a
            // mismatch run, because there is nothing meaningful to compare against.
            if (_syncsApplied == 0)
            {
                RequestResync(galaxy is null ? "no galaxy yet" : "joined, no state adopted yet");
                return;
            }

            if (galaxy is null) return;

            var mine = ApplyState.StructuralFingerprint(galaxy);

            if (mine == hostFingerprint)
            {
                if (_consecutiveMismatches > 0)
                    _log($"# net[client]: structure back in agreement after {_consecutiveMismatches} mismatch(es)");
                _consecutiveMismatches = 0;
                return;
            }

            _consecutiveMismatches++;
            if (_consecutiveMismatches < MismatchesBeforeResync) return;

            // Report the divergence whether or not we act on it: the rate floor lives in
            // RequestResync, so a run of mismatches we decline to serve still shows up.
            if (_consecutiveMismatches == MismatchesBeforeResync)
                _log($"# net[client]: structure diverged — host {hostFingerprint} vs mine {mine} " +
                     $"at host tick {hostTick}");

            RequestResync($"structure diverged for {_consecutiveMismatches} summaries");
        }
        catch (Exception ex)
        {
            _log("# net[client]: summary read failed " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Ask the host for a full state, subject to the rate floor. One place, so the join
    /// bootstrap and the divergence path cannot drift apart on their limiting.
    /// </summary>
    private static void RequestResync(string reason)
    {
        if (DateTime.UtcNow - _lastResyncRequest < MinTimeBetweenResyncRequests) return;

        _lastResyncRequest = DateTime.UtcNow;
        _consecutiveMismatches = 0;
        _resyncsRequested++;

        Send(Msg.ResyncRequest, Array.Empty<byte>());
        _log($"# net[client]: requested full state #{_resyncsRequested} — {reason}");
    }

    /// <summary>
    /// Apply queued deltas to the live galaxy, on the main thread.
    ///
    /// Main thread because the renderer reads these same ship positions every frame, and a
    /// Vector3 written field-by-field from another thread can be read half-updated. The
    /// client's own simulation is stopped, so nothing else is writing them.
    ///
    /// A delta that does not fit -- unknown ship, disagreeing count -- is a structural
    /// divergence, not a bad packet. The rest of the queue is discarded (it is all relative
    /// to a galaxy we evidently do not have) and a full state is requested.
    /// </summary>
    private static void DrainDeltas()
    {
        if (_inboundDeltas.IsEmpty) return;

        var galaxy = ApplyState.CurrentGalaxy;
        if (galaxy is null) return;

        while (_inboundDeltas.TryDequeue(out var payload))
        {
            if (StateDelta.Apply(galaxy, payload, out var info))
            {
                _deltasApplied++;
                if (_deltasApplied % 50 == 1)
                    _log($"# net[client]: delta #{_deltasApplied} applied — {info}");
                continue;
            }

            while (_inboundDeltas.TryDequeue(out _)) { }
            RequestResync($"a delta did not fit: {info}");
            return;
        }
    }
}
