using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;

// A stub peer for the Dw2Mp transport.
//
// Two DW2 instances will not fit in memory on a 32 GB machine when the save is a
// late-game 40 MB galaxy (~15 GB working set each), so the two-process loopback test
// needs a small save. This lets the wire protocol be validated NOW with one real game
// instance: it connects as a client, reads frames, decompresses full states, and reports
// size and hash.
//
// It also isolates the transport from the apply path. M3 already proved a client can
// adopt a galaxy; what is unproven is framing, compression and the host's sync loop. A
// stub tests exactly that and nothing else, so a failure has one possible cause.

const int HeaderSize = 5;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 47800;
int expect = args.Length > 2 && int.TryParse(args[2], out var e) ? e : 2;

Console.WriteLine($"probe: connecting to {host}:{port}, expecting {expect} state(s)");

using var client = new TcpClient();

for (int attempt = 1; ; attempt++)
{
    try { client.Connect(host, port); break; }
    catch (SocketException) when (attempt < 120)
    {
        // The host only listens once the game has loaded, which takes minutes.
        if (attempt % 10 == 1) Console.WriteLine($"probe: waiting for host (attempt {attempt})...");
        Thread.Sleep(2000);
    }
}

client.NoDelay = true;
using var stream = client.GetStream();
Console.WriteLine("probe: CONNECTED");

int states = 0;
var started = DateTime.UtcNow;

while (states < expect)
{
    var header = ReadExactly(stream, HeaderSize);
    if (header is null) { Console.WriteLine("probe: peer closed"); break; }

    int length = BitConverter.ToInt32(header, 0);
    byte type = header[4];

    if (length < 0 || length > 256 * 1024 * 1024)
    {
        Console.WriteLine($"probe: implausible frame length {length} -- framing is broken");
        break;
    }

    var payload = length == 0 ? Array.Empty<byte>() : ReadExactly(stream, length);
    if (payload is null) { Console.WriteLine("probe: truncated frame"); break; }

    string typeName = type switch { 1 => "Hello", 2 => "FullState", 3 => "Command", _ => $"Unknown({type})" };
    var elapsed = (DateTime.UtcNow - started).TotalSeconds;

    if (type == 2)
    {
        byte[] raw;
        try { raw = Decompress(payload); }
        catch (Exception ex)
        {
            Console.WriteLine($"probe: [{elapsed,6:F1}s] FullState packed={payload.Length:N0}B -- DECOMPRESS FAILED: {ex.Message}");
            break;
        }

        states++;
        var hash = Convert.ToHexString(SHA256.HashData(raw))[..16];
        double ratio = (double)payload.Length / raw.Length;

        Console.WriteLine($"probe: [{elapsed,6:F1}s] FullState #{states}  packed={payload.Length:N0}B  " +
                          $"raw={raw.Length:N0}B  ratio={ratio:P1}  sha={hash}");
    }
    else Console.WriteLine($"probe: [{elapsed,6:F1}s] {typeName} ({payload.Length:N0}B)");
}

Console.WriteLine(states >= expect
    ? $"probe: OK -- received {states} full state(s); framing, compression and host sync all work"
    : $"probe: INCOMPLETE -- got {states} of {expect}");

return states >= expect ? 0 : 1;

// TCP is a stream: one Read may return fewer bytes than asked for.
static byte[] ReadExactly(NetworkStream s, int count)
{
    var buffer = new byte[count];
    int offset = 0;

    while (offset < count)
    {
        int read;
        try { read = s.Read(buffer, offset, count - offset); }
        catch { return null; }

        if (read <= 0) return null;
        offset += read;
    }

    return buffer;
}

static byte[] Decompress(byte[] data)
{
    using var input = new MemoryStream(data, writable: false);
    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream(48 * 1024 * 1024);
    deflate.CopyTo(output);
    return output.ToArray();
}
