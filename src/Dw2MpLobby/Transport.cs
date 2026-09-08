using System.Net;
using System.Net.Sockets;

namespace Dw2MpLobby;

/// <summary>
/// The remote link between two launchers.
///
/// An interface rather than a TcpClient everywhere, because the whole point of Phase 1 is
/// that the transport can be swapped without touching game code. Phase 2 adds a Steam
/// implementation behind this same seam.
/// </summary>
public interface IRemoteTransport : IDisposable
{
    string Describe { get; }

    bool Connected { get; }

    /// <summary>Host side: wait for the other player. Blocks until connected or cancelled.</summary>
    Task ListenAsync(CancellationToken ct);

    /// <summary>Client side: reach the host.</summary>
    Task ConnectAsync(string address, CancellationToken ct);

    Stream Stream { get; }
}

/// <summary>
/// Direct TCP: LAN, or internet via a virtual LAN (Tailscale/ZeroTier) or port forwarding.
/// This is what is proven working.
/// </summary>
public sealed class TcpTransport : IRemoteTransport
{
    private readonly int _port;
    private TcpListener _listener;
    private TcpClient _client;

    public TcpTransport(int port) => _port = port;

    public string Describe => $"Direct TCP on port {_port}";

    public bool Connected => _client?.Connected == true;

    public Stream Stream => _client?.GetStream();

    public async Task ListenAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        try { _client = await _listener.AcceptTcpClientAsync(ct); }
        finally { _listener.Stop(); }

        _client.NoDelay = true;
    }

    public async Task ConnectAsync(string address, CancellationToken ct)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(address, _port, ct);
        _client.NoDelay = true;
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
        _client?.Dispose();
    }
}

/// <summary>
/// Wraps a connection that is ALREADY established — the lobby's peer socket, handed to the
/// relay once the session starts.
///
/// Reconnecting after the lobby handshake would mean a second listen/dial cycle with a race
/// between the two launchers, for no benefit: the peers are connected and already agree on
/// the session.
/// </summary>
public sealed class ExistingStreamTransport : IRemoteTransport
{
    private readonly TcpClient _client;

    public ExistingStreamTransport(TcpClient client, Stream stream)
    {
        _client = client;
        Stream = stream;
    }

    public string Describe => "Lobby connection (already established)";

    public bool Connected => _client?.Connected == true;

    public Stream Stream { get; }

    // Already connected: both are no-ops rather than errors, so the relay needs no special
    // case for "this transport skips the connect phase".
    public Task ListenAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ConnectAsync(string address, CancellationToken ct) => Task.CompletedTask;

    public void Dispose() => _client?.Dispose();
}

/// <summary>
/// PHASE 2 — Steam networking. Deliberately NOT implemented, and the reason matters.
///
/// The design is sound and the pieces exist: both players own DW2 on Steam, so
/// SteamNetworkingSockets would give NAT traversal AND Valve-hosted relay fallback AND
/// peer discovery by Steam ID — replacing the single largest piece of infrastructure FAF
/// had to build and host themselves (their ICE adapter plus TURN servers).
///
/// It is not wired up because it cannot be verified here. It needs two Steam accounts on
/// two machines to test, and there is an unresolved question about which process should
/// own the Steam connection: DW2's own process already has Steam initialised with the
/// correct app id, whereas the launcher would have to initialise it separately.
///
/// Shipping networking code that has never carried a byte is how you get a feature that
/// looks finished and fails in someone else's hands. The seam is here; the implementation
/// waits for a second machine.
/// </summary>
public sealed class SteamTransport : IRemoteTransport
{
    public const string NotImplementedMessage =
        "Steam networking is not implemented yet.\r\n\r\n" +
        "It needs two Steam accounts on separate machines to verify, and a decision about " +
        "whether the launcher or the game process owns the Steam connection — DW2's process " +
        "already has Steam initialised with the right app id.\r\n\r\n" +
        "Use Direct TCP for now: LAN works as-is, and Tailscale or ZeroTier gives internet " +
        "play with no port forwarding.";

    public string Describe => "Steam networking (not implemented)";

    public bool Connected => false;

    public Stream Stream => null;

    public Task ListenAsync(CancellationToken ct) => throw new NotSupportedException(NotImplementedMessage);

    public Task ConnectAsync(string address, CancellationToken ct) => throw new NotSupportedException(NotImplementedMessage);

    public void Dispose() { }
}
