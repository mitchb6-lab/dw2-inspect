using System.Net;
using System.Net.Sockets;

namespace Dw2MpLobby;

public sealed record RelayStats(bool GameConnected, bool PeerConnected, long ToPeer, long ToGame, int Frames);

/// <summary>
/// Sits between the local game and the remote peer, and pumps frames both ways.
///
/// This is Phase 1: the mod connects here on localhost and knows nothing else, exactly as
/// FAF gives Supreme Commander `/gpgnet 127.0.0.1:port` and lets the client own the
/// network. Everything that is hard about networking — NAT, transport choice, retries,
/// reconnection — becomes the launcher's problem, changeable without touching game code
/// or waiting four minutes for a game to reload.
///
/// The relay is deliberately DUMB about content. It reads the frame header only far
/// enough to know how many bytes follow, then forwards the frame untouched. It does not
/// need to understand a galaxy, and a protocol change in the mod needs no change here.
/// </summary>
public sealed class Relay : IDisposable
{
    private const int HeaderSize = 5;   // [int32 length][byte type]

    private readonly int _localPort;
    private readonly IRemoteTransport _transport;
    private readonly bool _isHost;
    private readonly string _remoteAddress;
    private readonly Action<string> _log;

    private readonly CancellationTokenSource _cts = new();

    private TcpListener _gameListener;
    private TcpClient _game;

    private long _toPeer, _toGame;
    private int _frames;

    public Relay(int localPort, IRemoteTransport transport, bool isHost, string remoteAddress, Action<string> log)
    {
        _localPort = localPort;
        _transport = transport;
        _isHost = isHost;
        _remoteAddress = remoteAddress;
        _log = log;
    }

    public RelayStats Stats => new(_game?.Connected == true, _transport.Connected, _toPeer, _toGame, _frames);

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            // Listen for the game FIRST. It takes minutes to load, and the peer connection
            // may complete long before or long after — neither ordering should matter.
            _gameListener = new TcpListener(IPAddress.Loopback, _localPort);
            _gameListener.Start();
            _log($"Waiting for Distant Worlds 2 on 127.0.0.1:{_localPort} ...");

            var gameTask = _gameListener.AcceptTcpClientAsync(_cts.Token).AsTask();

            var peerTask = _isHost
                ? _transport.ListenAsync(_cts.Token)
                : _transport.ConnectAsync(_remoteAddress, _cts.Token);

            if (_isHost) _log($"Waiting for the other player — {_transport.Describe}");
            else _log($"Connecting to {_remoteAddress} — {_transport.Describe}");

            _game = await gameTask;
            _game.NoDelay = true;
            _log("Distant Worlds 2 connected to the launcher.");

            await peerTask;
            _log("Peer connected. Relaying.");

            var gameStream = _game.GetStream();
            var peerStream = _transport.Stream;

            // Two independent pumps: a large state frame travelling one way must never
            // block a small command frame travelling the other.
            await Task.WhenAny(
                Pump(gameStream, peerStream, toPeer: true),
                Pump(peerStream, gameStream, toPeer: false));

            _log("Relay stopped.");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { _log($"Relay error: {ex.GetType().Name}: {ex.Message}"); }
        finally { try { _gameListener?.Stop(); } catch { } }
    }

    /// <summary>
    /// Forwards whole frames. Reading the 5-byte header tells us the payload length, so a
    /// frame is never split or merged — which a naive byte-for-byte copy would happily do
    /// and which would corrupt a galaxy in a way that is very hard to trace back here.
    /// </summary>
    private async Task Pump(Stream from, Stream to, bool toPeer)
    {
        var header = new byte[HeaderSize];

        while (!_cts.IsCancellationRequested)
        {
            if (!await ReadExactly(from, header, HeaderSize)) break;

            int length = BitConverter.ToInt32(header, 0);
            if (length < 0 || length > 256 * 1024 * 1024)
            {
                _log($"Refusing implausible frame length {length} — dropping the connection.");
                break;
            }

            var payload = new byte[length];
            if (length > 0 && !await ReadExactly(from, payload, length)) break;

            try
            {
                await to.WriteAsync(header, _cts.Token);
                if (length > 0) await to.WriteAsync(payload, _cts.Token);
                await to.FlushAsync(_cts.Token);
            }
            catch { break; }

            long total = HeaderSize + length;
            if (toPeer) _toPeer += total; else _toGame += total;
            _frames++;
        }
    }

    private async Task<bool> ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;

        while (offset < count)
        {
            int read;
            try { read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), _cts.Token); }
            catch { return false; }

            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _gameListener?.Stop(); } catch { }
        _game?.Dispose();
        _transport.Dispose();
        _cts.Dispose();
    }
}
