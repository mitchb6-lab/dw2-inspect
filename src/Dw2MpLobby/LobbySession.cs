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

        try { _peer = await _listener.AcceptTcpClientAsync(ct); }
        finally { _listener.Stop(); }

        _peer.NoDelay = true;
        _stream = _peer.GetStream();
        _log("A player connected. Waiting for their empire...");

        _ = Task.Run(() => ReadLoop(_cts.Token));
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
                    {
                        var slot = JsonSerializer.Deserialize<PlayerSlot>(payload);
                        if (slot is null) break;

                        // The host owns slot numbering. A client that asked for a slot
                        // already taken must not silently overwrite the host's own empire.
                        slot.Slot = Session.Players.Count;
                        Session.Players.Add(slot);

                        _log($"{slot.Name} joined as \"{slot.Empire.Name}\" (slot {slot.Slot}).");

                        // Echo the whole agreed session back, with THEIR slot marked, so
                        // both sides hold the same descriptor and differ only in mySlot.
                        var forThem = CloneWithMySlot(Session, slot.Slot);
                        await SendAsync(LobbyMsg.Session, JsonSerializer.SerializeToUtf8Bytes(forThem));

                        SessionChanged?.Invoke();
                        break;
                    }

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
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"Lobby connection lost: {ex.GetType().Name}: {ex.Message}"); }
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
