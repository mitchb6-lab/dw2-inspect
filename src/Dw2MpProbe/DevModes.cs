using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Dw2MpShared;

namespace Dw2MpProbe;

/// <summary>
/// A lobby-free relay for two game instances on one PC (mitch, in chat 2026-11-10: "measure
/// first"). The lobby is a WinForms window that nobody can drive unattended, and all it does
/// for the games after launch is pump frames between two sockets. This does the same pumping
/// from a console, so Scripts\net-twoinstance.ps1 can run a host and a joiner with no lobby.
///
///     dw2mpprobe relay 47811 47812
///
/// It listens on both loopback ports, takes one connection on each (the host's mod on the
/// first, the joiner's on the second) and forwards whole frames both ways. Like the lobby's
/// Relay it reads only the 5-byte header, [int32 length][byte type], and forwards the payload
/// untouched. It counts frames and bytes per message type per direction, which is the traffic
/// half of the measurement.
/// </summary>
public static class RelayMode
{
    private const int HeaderSize = 5;

    private const int MaxFrameBytes = 256 * 1024 * 1024;

    private sealed class TrafficCounter
    {
        public long Frames;
        public long Bytes;
    }

    private static readonly object _countersGate = new();

    private static readonly Dictionary<string, TrafficCounter> _counters = new(StringComparer.Ordinal);

    private static readonly DateTime _started = DateTime.UtcNow;

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: dw2mpprobe relay <hostLocalPort> <joinerLocalPort>");
            return 2;
        }

        bool hostPortParsed = int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hostPort);
        bool joinerPortParsed = int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int joinerPort);
        if (!hostPortParsed || !joinerPortParsed)
        {
            Console.WriteLine("relay: both ports must be numbers");
            return 2;
        }

        var hostListener = new TcpListener(IPAddress.Loopback, hostPort);
        var joinerListener = new TcpListener(IPAddress.Loopback, joinerPort);
        hostListener.Start();
        joinerListener.Start();
        Say($"listening on 127.0.0.1:{hostPort} (host game) and 127.0.0.1:{joinerPort} (joiner game)");

        Task<TcpClient> hostAccept = hostListener.AcceptTcpClientAsync();
        Task<TcpClient> joinerAccept = joinerListener.AcceptTcpClientAsync();
        Task.WaitAll(hostAccept, joinerAccept);

        TcpClient hostGame = hostAccept.Result;
        TcpClient joinerGame = joinerAccept.Result;
        hostGame.NoDelay = true;
        joinerGame.NoDelay = true;
        Say("both games connected; relaying");

        NetworkStream hostStream = hostGame.GetStream();
        NetworkStream joinerStream = joinerGame.GetStream();

        // Two independent pumps, as in the lobby: a 4 MB state going one way must never block
        // a small command coming back.
        Task hostToJoiner = Task.Run(() => Pump(hostStream, joinerStream, "host->joiner"));
        Task joinerToHost = Task.Run(() => Pump(joinerStream, hostStream, "joiner->host"));

        var bothPumps = new[] { hostToJoiner, joinerToHost };
        while (true)
        {
            int finished = Task.WaitAny(bothPumps, TimeSpan.FromSeconds(30));
            PrintTraffic();
            if (finished >= 0)
            {
                break;
            }
        }

        Say("a game disconnected; relay stopped");
        return 0;
    }

    private static void Pump(NetworkStream from, NetworkStream to, string direction)
    {
        var header = new byte[HeaderSize];

        while (true)
        {
            bool headerRead = ReadExactly(from, header, HeaderSize);
            if (!headerRead)
            {
                return;
            }

            int length = BitConverter.ToInt32(header, 0);
            byte type = header[4];

            if (length < 0 || length > MaxFrameBytes)
            {
                Say($"{direction}: implausible frame length {length}; stopping");
                return;
            }

            var payload = new byte[length];
            if (length > 0)
            {
                bool payloadRead = ReadExactly(from, payload, length);
                if (!payloadRead)
                {
                    return;
                }
            }

            try
            {
                to.Write(header, 0, HeaderSize);
                if (length > 0)
                {
                    to.Write(payload, 0, length);
                }

                to.Flush();
            }
            catch (Exception)
            {
                return;
            }

            Count(direction, type, HeaderSize + length);
        }
    }

    private static void Count(string direction, byte type, long bytes)
    {
        string key = direction + " " + MessageName(type);

        lock (_countersGate)
        {
            if (!_counters.TryGetValue(key, out TrafficCounter counter))
            {
                counter = new TrafficCounter();
                _counters[key] = counter;
            }

            counter.Frames++;
            counter.Bytes += bytes;
        }
    }

    /// <summary>NetSession.Msg, by number. A type this build does not know is still relayed and counted.</summary>
    private static string MessageName(byte type)
    {
        return type switch
        {
            1 => "Hello",
            2 => "FullState",
            3 => "Command",
            4 => "StateSummary",
            5 => "ResyncRequest",
            6 => "Delta",
            7 => "Content",
            8 => "DesyncReport",
            9 => "DesyncChoice",
            10 => "DesyncOutcome",
            _ => "Type" + type.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static void PrintTraffic()
    {
        lock (_countersGate)
        {
            foreach (var entry in _counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Say($"traffic {entry.Key}: {entry.Value.Frames:N0} frame(s), {entry.Value.Bytes:N0}B");
            }
        }
    }

    private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;

        while (offset < count)
        {
            int read;
            try
            {
                read = stream.Read(buffer, offset, count - offset);
            }
            catch (Exception)
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

    private static void Say(string line)
    {
        double elapsed = (DateTime.UtcNow - _started).TotalSeconds;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "relay: [{0,6:F1}s] {1}", elapsed, line));
        Console.Out.Flush();
    }
}

/// <summary>
/// Reads the census files the two games wrote (src\Dw2Mp\DivergenceDump.cs) and prints what
/// differed between the host and the joiner at each sampled host tick, then one ranking over
/// the whole run.
///
///     dw2mpprobe divergence C:\path\to\dumps [fieldsPerKind]
/// </summary>
public static class DivergenceMode
{
    private sealed class FieldTotal
    {
        public string Name = "";
        public long TotalObjects;
        public int MostInOneSample;
        public int SamplesSeen;
        public string Example = "";
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: dw2mpprobe divergence <dumpDirectory> [fieldsPerKind]");
            return 2;
        }

        string directory = args[1];
        int fieldsPerKind = 12;
        if (args.Length > 2)
        {
            bool parsed = int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested);
            if (parsed && requested > 0)
            {
                fieldsPerKind = requested;
            }
        }

        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"divergence: no directory {directory}");
            return 1;
        }

        string[] hostFiles = Directory.GetFiles(directory, "host-tick*.txt");
        Array.Sort(hostFiles, StringComparer.Ordinal);

        var totals = new Dictionary<string, FieldTotal>(StringComparer.Ordinal);
        var membership = new Dictionary<string, FieldTotal>(StringComparer.Ordinal);
        int pairs = 0;

        foreach (string hostFile in hostFiles)
        {
            string tickPart = Path.GetFileName(hostFile).Substring("host-".Length);
            string clientFile = Path.Combine(directory, "client-" + tickPart);
            if (!File.Exists(clientFile))
            {
                Console.WriteLine($"divergence: {Path.GetFileName(hostFile)} has no client file; skipped");
                continue;
            }

            string hostText = File.ReadAllText(hostFile);
            string clientText = File.ReadAllText(clientFile);
            List<DivergentKind> kinds = DivergenceReport.Compare(hostText, clientText, DivergenceReport.DefaultRelativeTolerance);
            pairs++;

            Console.WriteLine();
            Console.WriteLine($"=== {tickPart.Replace(".txt", "")}");
            Console.WriteLine("    " + FirstLine(hostText));
            Console.WriteLine("    " + FirstLine(clientText));
            Console.Write(DivergenceReport.Format(kinds, fieldsPerKind));

            foreach (DivergentKind kind in kinds)
            {
                AddTotal(membership, kind.Kind + " only on host", kind.OnlyOnHost, "");
                AddTotal(membership, kind.Kind + " only on client", kind.OnlyOnClient, "");

                foreach (DivergentField field in kind.Fields)
                {
                    string example = $"{field.ExampleKey}: host {field.ExampleHost} / client {field.ExampleClient}";
                    AddTotal(totals, kind.Kind + "." + field.Field, field.ObjectsDiffering, example);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== WHOLE RUN: {pairs} sampled tick(s)");
        Console.WriteLine("--- membership (objects one side has and the other does not)");
        PrintTotals(membership, 40);
        Console.WriteLine("--- fields, ranked by objects differing summed over all samples");
        PrintTotals(totals, 60);

        return pairs > 0 ? 0 : 1;
    }

    private static void AddTotal(Dictionary<string, FieldTotal> totals, string name, int objects, string example)
    {
        if (objects <= 0)
        {
            return;
        }

        if (!totals.TryGetValue(name, out FieldTotal total))
        {
            total = new FieldTotal { Name = name, Example = example };
            totals[name] = total;
        }

        total.TotalObjects += objects;
        total.SamplesSeen++;
        if (objects > total.MostInOneSample)
        {
            total.MostInOneSample = objects;
        }
    }

    private static void PrintTotals(Dictionary<string, FieldTotal> totals, int limit)
    {
        var ranked = totals.Values
            .OrderByDescending(total => total.TotalObjects)
            .ThenBy(total => total.Name, StringComparer.Ordinal)
            .Take(limit);

        foreach (FieldTotal total in ranked)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0,8}  max {1,5}  in {2,2} sample(s)  {3}",
                total.TotalObjects,
                total.MostInOneSample,
                total.SamplesSeen,
                total.Name);

            if (total.Example.Length > 0)
            {
                line += "   e.g. " + total.Example;
            }

            Console.WriteLine(line);
        }

        if (totals.Count == 0)
        {
            Console.WriteLine("       (none)");
        }
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOf('\n');
        if (end < 0)
        {
            return text.Trim();
        }

        return text.Substring(0, end).Trim();
    }
}
