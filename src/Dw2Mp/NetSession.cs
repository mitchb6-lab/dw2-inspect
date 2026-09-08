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

        var thread = new Thread(Role == NetRole.Host ? HostLoop : ClientLoop)
        {
            IsBackground = true,   // must never keep the game alive on exit
            Name = "Dw2Mp.Net",
        };
        thread.Start();
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
                _log($"# net[host]: received {type} ({payload.Length:N0}B)");
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
