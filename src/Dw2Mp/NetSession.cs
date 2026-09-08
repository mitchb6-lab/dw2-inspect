using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;

namespace Dw2Mp;

/// <summary>
/// M4a: host-authoritative transport over TCP.
///
/// The host simulates and periodically ships a full compressed galaxy to the client; the
/// client adopts it through the M3-proven path. This is the first milestone where two
/// DW2 processes share a universe rather than merely being measured.
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
        FullState = 2,   // deflate-compressed galaxy bytes
        Command = 3,     // MessagePacket bytes (M4b)
    }

    public enum NetRole { Off, Host, Client }

    public static NetRole Role { get; private set; } = NetRole.Off;

    public static bool Active => Role != NetRole.Off;

    private static Action<string> _log;
    private static int _port;
    private static string _hostAddress;
    private static long _syncEveryTicks;

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

        _port = int.TryParse(Environment.GetEnvironmentVariable("DW2MP_PORT"), out var p) ? p : 47800;
        _hostAddress = Environment.GetEnvironmentVariable("DW2MP_HOST") is { Length: > 0 } h ? h : "127.0.0.1";
        _syncEveryTicks = long.TryParse(Environment.GetEnvironmentVariable("DW2MP_SYNC_EVERY_TICKS"), out var s) ? s : 600;

        log($"# net: role={Role} port={_port} host={_hostAddress} syncEvery={_syncEveryTicks} ticks");

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

        var thread = new Thread(Role == NetRole.Host ? HostLoop : ClientLoop)
        {
            IsBackground = true,   // must never keep the game alive on exit
            Name = "Dw2Mp.Net",
        };
        thread.Start();
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

    // ---------------------------------------------------------------- host

    private static void HostLoop()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _log($"# net[host]: listening on {_port}, waiting for a client");

            _peer = _listener.AcceptTcpClient();
            _peer.NoDelay = true;
            _stream = _peer.GetStream();
            _connected = true;

            _log($"# net[host]: client connected from {_peer.Client.RemoteEndPoint}");
            Send(Msg.Hello, Array.Empty<byte>());

            // Host reads too, so M4b's command relay has a channel already open.
            while (_connected)
            {
                var (type, payload) = Receive();
                if (type is null) break;

                if (type == Msg.Command) InjectCommand(payload);
                else _log($"# net[host]: received {type} ({payload.Length:N0}B)");
            }
        }
        catch (Exception ex) { _log("# net[host]: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _connected = false; _log("# net[host]: disconnected"); }
    }

    /// <summary>
    /// Called from the simulation thread each tick. Serialises and sends every N ticks.
    /// This is where the host hitches: ~370 ms of serialise plus ~340 ms of compression,
    /// inline. Deliberate for M4a — correctness first, then move it off-thread.
    /// </summary>
    public static void HostTick(object galaxy, long tick)
    {
        if (Role != NetRole.Host || !_connected) return;
        if (tick - _lastSyncTick < _syncEveryTicks) return;
        _lastSyncTick = tick;

        try
        {
            var sw = Stopwatch.StartNew();
            byte[] raw = ApplyState.SerialiseGalaxy(galaxy);
            double serialiseMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            byte[] packed = Compress(raw);
            double packMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            Send(Msg.FullState, packed);
            double sendMs = sw.Elapsed.TotalMilliseconds;

            _syncsSent++;
            _log($"# net[host]: sync #{_syncsSent} tick={tick} raw={raw.LongLength:N0}B " +
                 $"packed={packed.Length:N0}B serialise={serialiseMs:N0}ms pack={packMs:N0}ms send={sendMs:N0}ms");
        }
        catch (Exception ex)
        {
            _log("# net[host]: sync failed " + ex.GetType().Name + ": " + ex.Message);
            _connected = false;
        }
    }

    // -------------------------------------------------------------- client

    private static void ClientLoop()
    {
        try
        {
            _log($"# net[client]: connecting to {_hostAddress}:{_port}");

            _peer = new TcpClient();
            _peer.Connect(_hostAddress, _port);
            _peer.NoDelay = true;
            _stream = _peer.GetStream();
            _connected = true;

            _log("# net[client]: connected");

            while (_connected)
            {
                var (type, payload) = Receive();
                if (type is null) break;

                if (type == Msg.FullState)
                {
                    // Decompress here (background thread) so the main thread only pays
                    // for deserialise + apply. Queue and let Update drain it.
                    byte[] raw = Decompress(payload);
                    _inbound.Enqueue(raw);
                    _log($"# net[client]: state received packed={payload.Length:N0}B raw={raw.Length:N0}B (queued)");
                }
                else _log($"# net[client]: received {type} ({payload.Length:N0}B)");
            }
        }
        catch (Exception ex) { _log("# net[client]: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _connected = false; _log("# net[client]: disconnected"); }
    }

    /// <summary>
    /// Main-thread pump. Applies at most one state per frame, and drops any backlog:
    /// with full-state syncs an older snapshot has no value once a newer one has arrived,
    /// and applying them in sequence would just stutter through stale worlds.
    /// </summary>
    private static void OnMainThreadUpdate()
    {
        if (Role != NetRole.Client || _inbound.IsEmpty) return;

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
}
