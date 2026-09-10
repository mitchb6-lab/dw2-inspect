using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dw2MpLobby;

public enum LobbyMsg : byte
{
    Join = 1,      // client -> host: my PlayerSlot
    Session = 2,   // host -> client: the agreed SessionDescriptor, with their slot set
    Start = 3,     // host -> client: launch now
}

/// <summary>
/// The pre-game negotiation, before either game starts.
///
/// This is what makes a joining player's empire actually reach the host. Without it the
/// host generates a galaxy from its own configuration and the joiner's choices are
/// decoration — which is exactly what the earlier build did.
///
/// The connection established here is REUSED as the relay's transport once the session
/// starts. Reconnecting instead would mean a second listen/dial cycle with a race between
/// the two launchers, for no benefit: the peers are already connected and already agree.
///
/// Framing matches the game protocol — [int32 length][byte type][payload] — so there is one
/// framing convention in the codebase rather than two subtly different ones.
/// </summary>
public sealed class LobbySession : IDisposable
{
    private readonly int _port;
    private readonly Action<string> _log;

    private TcpListener _listener;
    private TcpClient _peer;
    private NetworkStream _stream;

    private readonly CancellationTokenSource _cts = new();

    public bool IsHost { get; private set; }

    public bool Connected => _peer?.Connected == true;

    /// <summary>The agreed session. Host builds it; client receives it.</summary>
    public SessionDescriptor Session { get; private set; }

    public event Action SessionChanged;

    public event Action StartRequested;

    /// <summary>The peer went away before the session started. Never raised after hand-over.</summary>
    public event Action ConnectionLost;

    public LobbySession(int port, Action<string> log)
    {
        _port = port;
        _log = log;
    }

    /// <summary>Host: wait for one joining player, then merge their slot into the session.</summary>
    public async Task HostAsync(SessionDescriptor initial, CancellationToken ct)
    {
        IsHost = true;
        Session = initial;

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _log($"Lobby open on port {_port}. Waiting for a player to join...");

        // Keep accepting until a connection actually JOINS. The first build accepted exactly
        // one connection and stopped listening -- and on the first two-PC attempt that one
        // connection was the joiner's own Test probe, which connects and hangs up without
        // sending anything. The host then sat on "waiting for their empire" over a socket
        // the probe had already closed, with nothing listening, and the real join found no
        // host. A probe, a port scanner or a browser must not be able to consume the lobby.
        try
        {
            while (true)
            {
                var peer = await _listener.AcceptTcpClientAsync(ct);
                peer.NoDelay = true;
                _peer = peer;
                _stream = peer.GetStream();
                _log($"A connection from {peer.Client.RemoteEndPoint}. Waiting for their empire...");

                var (type, payload) = await ReceiveWithin(JoinGracePeriod, ct);

                if (type == LobbyMsg.Join && await TryAdmit(payload))
                    break;

                _log(type is null
                    ? "It hung up without joining -- a Test probe, most likely. Still waiting for a player..."
                    : $"It sent {type} instead of joining; ignored. Still waiting for a player...");

                _peer = null;
                _stream = null;
                peer.Dispose();
            }
        }
        finally { _listener.Stop(); }

        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    /// <summary>
    /// How long a fresh connection has to send its Join before the host goes back to
    /// listening. Long enough for a slow machine to serialise a slot; short enough that a
    /// stray connection does not hold the lobby hostage.
    /// </summary>
    private static readonly TimeSpan JoinGracePeriod = TimeSpan.FromSeconds(15);

    private async Task<(LobbyMsg? Type, byte[] Payload)> ReceiveWithin(TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        return await ReceiveAsync(timeout.Token);
    }

    /// <summary>
    /// Host: take a joiner's slot into the session and echo the agreed session back with
    /// THEIR slot marked, so both sides hold the same descriptor and differ only in mySlot.
    /// The echo is the acknowledgement a joiner waits for.
    /// </summary>
    private async Task<bool> TryAdmit(byte[] payload)
    {
        PlayerSlot slot;
        try { slot = JsonSerializer.Deserialize<PlayerSlot>(payload); }
        catch (JsonException ex) { _log($"Join was not a player slot: {ex.Message}"); return false; }
        if (slot is null) return false;

        // The host owns slot numbering. A client that asked for a slot already taken must
        // not silently overwrite the host's own empire.
        slot.Slot = Session.Players.Count;
        Session.Players.Add(slot);

        _log($"{slot.Name} joined as \"{slot.Empire.Name}\" (slot {slot.Slot}).");

        var forThem = CloneWithMySlot(Session, slot.Slot);
        await SendAsync(LobbyMsg.Session, JsonSerializer.SerializeToUtf8Bytes(forThem));

        SessionChanged?.Invoke();
        return true;
    }

    /// <summary>Client: connect and offer our slot.</summary>
    public async Task JoinAsync(string address, PlayerSlot mine, CancellationToken ct)
    {
        IsHost = false;

        _peer = new TcpClient();
        await _peer.ConnectAsync(address, _port, ct);
        _peer.NoDelay = true;
        _stream = _peer.GetStream();

        _log($"Connected to {address}:{_port}. Sending your empire...");
        await SendAsync(LobbyMsg.Join, JsonSerializer.SerializeToUtf8Bytes(mine));

        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    /// <summary>Host only: tell the client to launch, then launch ourselves.</summary>
    public async Task StartAsync()
    {
        if (!IsHost || Session is null) return;

        await SendAsync(LobbyMsg.Start, Array.Empty<byte>());
        _log("Start sent to the other player.");
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, payload) = await ReceiveAsync(ct);
                if (type is null) break;

                switch (type)
                {
                    case LobbyMsg.Join when IsHost:
                        // A second Join on an admitted connection (a client re-sending). The
                        // first one, before this loop starts, is handled by HostAsync.
                        await TryAdmit(payload);
                        break;

                    case LobbyMsg.Session when !IsHost:
                        Session = JsonSerializer.Deserialize<SessionDescriptor>(payload);
                        _log($"Session received: {Session?.Players.Count ?? 0} player(s), mode {Session?.Mode}, " +
                             $"you are slot {Session?.MySlot}.");
                        SessionChanged?.Invoke();
                        break;

                    case LobbyMsg.Start when !IsHost:
                        _log("Host started the session.");
                        StartRequested?.Invoke();
                        break;

                    default:
                        _log($"Ignoring unexpected lobby message {type}.");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log($"Lobby connection lost: {ex.GetType().Name}: {ex.Message}"); }

        // Reached on a clean hang-up (ReceiveAsync returned null) or an error -- never on
        // cancellation, which is the hand-over to the relay and is not a loss. The form
        // uses this to give Open lobby back; before it, a dropped lobby left the button
        // greyed out and the only way on was to restart the launcher.
        if (!ct.IsCancellationRequested)
        {
            _log("The other player disconnected from the lobby.");
            ConnectionLost?.Invoke();
        }
    }

    private static SessionDescriptor CloneWithMySlot(SessionDescriptor source, int mySlot)
    {
        var copy = SessionDescriptor.FromJson(source.ToJson());
        copy.MySlot = mySlot;
        return copy;
    }

    /// <summary>
    /// Hands the live connection to the relay. The lobby stops reading at this point —
    /// two readers on one socket would each consume half the other's frames.
    /// </summary>
    public IRemoteTransport HandOverTransport()
    {
        _cts.Cancel();
        return new ExistingStreamTransport(_peer, _stream);
    }

    // ------------------------------------------------------------- framing

    private async Task SendAsync(LobbyMsg type, byte[] payload)
    {
        var header = new byte[5];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), payload.Length);
        header[4] = (byte)type;

        await _stream.WriteAsync(header);
        if (payload.Length > 0) await _stream.WriteAsync(payload);
        await _stream.FlushAsync();
    }

    private async Task<(LobbyMsg? Type, byte[] Payload)> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[5];
        if (!await ReadExactly(header, 5, ct)) return (null, null);

        int length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > 8 * 1024 * 1024) return (null, null);

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0 && !await ReadExactly(payload, length, ct)) return (null, null);

        return ((LobbyMsg)header[4], payload);
    }

    private async Task<bool> ReadExactly(byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;

        while (offset < count)
        {
            int read;
            try { read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct); }
            catch { return false; }

            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
