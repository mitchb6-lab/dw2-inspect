using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dw2MpShared;

namespace Dw2MpLobby;

public enum LobbyMsg : byte
{
    Join = 1,      // client -> host: my PlayerSlot
    Session = 2,   // host -> client: the agreed SessionDescriptor, with their slot set
    Start = 3,     // host -> client: launch now
    Refused = 4,   // host -> client: UTF-8 reason the Join was not admitted; the host then closes and keeps listening
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
///
/// Everything that arrives here was typed or built on the other machine. A frame is at most
/// MaxFrameLength; a slot is sanitised and validated (JoinPolicy) before any of its strings
/// reach a log, the player list or session.json; a connection that throws is dropped and the
/// host keeps listening. The check itself is cooperative -- see JoinPolicy.
/// </summary>
public sealed class LobbySession : IDisposable
{
    private readonly int _port;
    private readonly Action<string> _log;

    private TcpListener _listener;
    private TcpClient _peer;
    private NetworkStream _stream;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// The largest lobby frame accepted. Every lobby message is a small JSON document -- two
    /// slots with five packs each is under 4 KB -- so 64 KB leaves room and stops a stranger
    /// from making the host allocate 8 MB per connection. The relay reads the stream after
    /// hand-over with its own limits.
    /// </summary>
    private const int MaxFrameLength = 64 * 1024;

    /// <summary>The longest refusal sentence a joiner will show or log. Ours are about 300 characters.</summary>
    private const int MaxRefusalLength = 600;

    public bool IsHost { get; private set; }

    public bool Connected => _peer?.Connected == true;

    /// <summary>The agreed session. Host builds it; client receives it.</summary>
    public SessionDescriptor Session { get; private set; }

    public event Action SessionChanged;

    public event Action StartRequested;

    /// <summary>The peer went away before the session started. Never raised after hand-over.</summary>
    public event Action ConnectionLost;

    /// <summary>
    /// Joiner only: the host refused our Join, or sent a Session this launcher cannot accept.
    /// Carries the sentence to show the player, already safe to display. The connection is
    /// finished.
    /// </summary>
    public event Action<string> Refused;

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
                bool admitted = await HandleConnection(peer, ct);
                if (admitted)
                {
                    break;
                }
            }
        }
        finally
        {
            _listener.Stop();
        }

        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    /// <summary>
    /// One connection: wait for its Join, then admit it or drop it. True when admitted, in
    /// which case the connection is now the session's. Whatever the connection does -- a
    /// malformed slot, a reset mid-frame, anything that throws -- is logged and the host
    /// goes back to listening; before 2026-11-09 one bad Join ended the lobby.
    /// </summary>
    private async Task<bool> HandleConnection(TcpClient peer, CancellationToken ct)
    {
        bool admitted = false;

        try
        {
            peer.NoDelay = true;
            _peer = peer;
            _stream = peer.GetStream();
            _log($"A connection from {peer.Client.RemoteEndPoint}. Waiting for their empire...");

            var (type, payload) = await ReceiveWithin(JoinGracePeriod, ct);

            if (type == LobbyMsg.Join)
            {
                var admission = await TryAdmit(payload);
                admitted = admission == Admission.Admitted;

                if (admission == Admission.Refused)
                {
                    // TryAdmit has logged the reason and sent it to them. They can fix it
                    // and join again without the host touching anything.
                    _log("Still waiting for a player...");
                }
                else if (admission == Admission.NotAPlayerSlot)
                {
                    _log("It sent a Join that was not a player slot; ignored. Still waiting for a player...");
                }
            }
            else if (type is null)
            {
                _log("It hung up without joining -- a Test probe, most likely. Still waiting for a player...");
            }
            else
            {
                _log($"It sent {type} instead of joining; ignored. Still waiting for a player...");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host closed the lobby; that is not the connection's fault and must stop HostAsync.
            throw;
        }
        catch (Exception ex)
        {
            _log($"That connection failed: {ex.GetType().Name}: {ex.Message}. Still waiting for a player...");
        }
        finally
        {
            if (!admitted)
            {
                _peer = null;
                _stream = null;
                peer.Dispose();
            }
        }

        return admitted;
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

    private enum Admission
    {
        Admitted,
        Refused,
        NotAPlayerSlot,
    }

    /// <summary>
    /// Host: take a joiner's slot into the session and echo the agreed session back with
    /// THEIR slot marked, so both sides hold the same descriptor and differ only in mySlot.
    /// The echo is the acknowledgement a joiner waits for.
    ///
    /// Before any of that the slot is sanitised and validated (a slot that is not a player
    /// is dropped), then must pass JoinPolicy.ReasonToRefuse (a player who does not match
    /// gets the reason as a Refused message). The host goes back to listening either way.
    /// </summary>
    private async Task<Admission> TryAdmit(byte[] payload)
    {
        PlayerSlot slot;
        try
        {
            slot = JsonSerializer.Deserialize<PlayerSlot>(payload);
        }
        catch (JsonException ex)
        {
            _log($"Join was not a player slot: {ex.Message}");
            return Admission.NotAPlayerSlot;
        }

        // Everything in the slot was typed or built on the other machine. Cap and strip it
        // before it can reach a log line, the player list or session.json -- then make sure
        // it describes a player at all.
        JoinPolicy.SanitiseSlot(slot);
        var problem = JoinPolicy.ValidateSlot(slot);
        if (problem is not null)
        {
            _log($"Join was not a usable player slot: {problem}.");
            return Admission.NotAPlayerSlot;
        }

        var host = Session.Players.FirstOrDefault(player => player.Slot == 0);

        var reason = JoinPolicy.ReasonToRefuse(host, slot);
        if (reason is not null)
        {
            await RefuseAsync(slot, reason);
            return Admission.Refused;
        }

        var warning = JoinPolicy.GameVersionWarning(host, slot);
        if (warning is not null)
        {
            _log("Note: " + warning);
        }

        // The host owns slot numbering. A client that asked for a slot already taken must
        // not silently overwrite the host's own empire.
        slot.Slot = Session.Players.Count;

        // Echo first, add second: a send that fails leaves the host's session exactly as it
        // was, instead of holding a slot for a player who never got the acknowledgement.
        var agreed = CloneWithMySlot(Session, slot.Slot);
        agreed.Players.Add(slot);
        await SendAsync(LobbyMsg.Session, JsonSerializer.SerializeToUtf8Bytes(agreed));

        Session.Players.Add(slot);

        _log($"{slot.Name} joined as \"{slot.Empire.Name}\" (slot {slot.Slot}). " +
             $"Game {slot.GameVersion}, content packs: {DescribeInstalled(slot)}.");

        SessionChanged?.Invoke();
        return Admission.Admitted;
    }

    private static string DescribeInstalled(PlayerSlot slot)
    {
        var names = slot.ContentPacks ?? new List<string>();
        if (names.Count == 0)
        {
            return "none (base game only)";
        }

        return string.Join(", ", names);
    }

    /// <summary>
    /// How long the refusal is given to leave the socket before the caller closes it. A close
    /// with bytes still in flight can arrive at the other end as a reset rather than the reason.
    /// </summary>
    private static readonly TimeSpan RefusalLinger = TimeSpan.FromMilliseconds(250);

    private async Task RefuseAsync(PlayerSlot joiner, string reason)
    {
        _log($"REFUSED {joiner.Name}: {reason}");

        try
        {
            await SendAsync(LobbyMsg.Refused, Encoding.UTF8.GetBytes(reason));
            await Task.Delay(RefusalLinger);
        }
        catch (Exception ex)
        {
            _log($"Could not deliver the refusal: {ex.GetType().Name}: {ex.Message}");
        }
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
        if (!IsHost || Session is null)
        {
            return;
        }

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
                if (type is null)
                {
                    break;
                }

                switch (type)
                {
                    case LobbyMsg.Join when IsHost:
                        // A second Join on the admitted connection. The first, handled by
                        // HostAsync, is this player's slot; a repeat used to append a second
                        // slot for the same player (until 2026-11-09). It is ignored.
                        _log("The joined player sent another Join; ignored, their slot is already in the session.");
                        break;

                    case LobbyMsg.Session when !IsHost:
                    {
                        bool accepted = AcceptSession(payload);
                        if (!accepted)
                        {
                            return;
                        }

                        break;
                    }

                    case LobbyMsg.Refused when !IsHost:
                    {
                        // The host's sentence goes into a dialog, so it is capped and stripped
                        // of control characters first, and labelled as the host's words. The
                        // log pane gets a one-line version: a newline there forges a log line.
                        var rawReason = Encoding.UTF8.GetString(payload);
                        var oneLine = PeerText.Sanitise(rawReason, MaxRefusalLength);
                        var forDialog = PeerText.SanitiseMultiline(rawReason, MaxRefusalLength);

                        _log("The host refused the join: " + oneLine);
                        Refused?.Invoke("The host's launcher said:\n\n" + forDialog);
                        return;
                    }

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
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log($"Lobby connection lost: {ex.GetType().Name}: {ex.Message}");
        }

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

    /// <summary>
    /// Joiner: the host's Session, checked before it becomes ours. False means the session was
    /// refused (the player has been told why through Refused) and the read loop must stop.
    /// </summary>
    private bool AcceptSession(byte[] payload)
    {
        SessionDescriptor received;
        try
        {
            received = JsonSerializer.Deserialize<SessionDescriptor>(payload);
        }
        catch (JsonException ex)
        {
            RefuseHostSession($"The host sent a session this launcher cannot read (not valid JSON: {ex.Message}).");
            return false;
        }

        // A host that admitted us without checking our protocol is an older launcher, and
        // it echoes its own number here. Stop before it starts a game whose session file we
        // would read differently.
        int hostProtocol = received?.ProtocolVersion ?? 0;
        if (hostProtocol != LobbyProtocol.Version)
        {
            RefuseHostSession($"The host's launcher speaks lobby protocol {hostProtocol}; this one speaks {LobbyProtocol.Version}. " +
                              "Both players need the same Dw2Mp build: install the same package on both PCs.");
            return false;
        }

        // Same caps and checks the host applies to a Join, so what reaches the player list
        // and session.json is a session, not whatever a stranger put on the wire.
        var problem = JoinPolicy.ValidateSession(received);
        if (problem is not null)
        {
            RefuseHostSession($"The host sent a session this launcher cannot read ({problem}).");
            return false;
        }

        Session = received;
        _log($"Session received: {Session.Players.Count} player(s), mode {Session.Mode}, you are slot {Session.MySlot}.");
        SessionChanged?.Invoke();
        return true;
    }

    private void RefuseHostSession(string reason)
    {
        _log("REFUSING the host's session: " + reason);
        Session = null;
        Refused?.Invoke(reason);
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
        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload);
        }

        await _stream.FlushAsync();
    }

    private async Task<(LobbyMsg? Type, byte[] Payload)> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[5];
        bool gotHeader = await ReadExactly(header, 5, ct);
        if (!gotHeader)
        {
            return (null, null);
        }

        int length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > MaxFrameLength)
        {
            _log($"Dropped a lobby frame claiming {length:N0} bytes (limit {MaxFrameLength:N0}).");
            return (null, null);
        }

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0)
        {
            bool gotPayload = await ReadExactly(payload, length, ct);
            if (!gotPayload)
            {
                return (null, null);
            }
        }

        return ((LobbyMsg)header[4], payload);
    }

    private async Task<bool> ReadExactly(byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;

        while (offset < count)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            }
            catch
            {
                return false;
            }

            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
