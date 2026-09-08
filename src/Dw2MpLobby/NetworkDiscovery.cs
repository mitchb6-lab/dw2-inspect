using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dw2MpLobby;

public enum AddressKind
{
    Lan,
    Tailscale,
    Hamachi,
    ZeroTier,
    Radmin,
    OtherVirtual,
    FullTunnelVpn,
}

public sealed record LocalAddress(IPAddress Ip, AddressKind Kind, string Adapter)
{
    public bool ReachableOverInternet => Kind is not (AddressKind.Lan or AddressKind.FullTunnelVpn);

    public string Label => Kind switch
    {
        AddressKind.Lan => $"LAN — {Ip}  (same network only)",
        AddressKind.Tailscale => $"Tailscale — {Ip}  (works over the internet)",
        AddressKind.Hamachi => $"Hamachi — {Ip}  (works over the internet)",
        AddressKind.ZeroTier => $"ZeroTier — {Ip}  (works over the internet)",
        AddressKind.Radmin => $"Radmin VPN — {Ip}  (works over the internet)",
        AddressKind.OtherVirtual => $"Virtual adapter — {Ip}  ({Adapter})",
        AddressKind.FullTunnelVpn => $"VPN — {Ip}  ({Adapter}) — will NOT work, turn it off",
        _ => Ip.ToString(),
    };

    public override string ToString() => Label;
}

/// <summary>
/// Finds every address another player could connect to, and says which is which.
///
/// The host otherwise has to run <c>ipconfig</c> and guess which of five addresses to
/// send — and the wrong guess fails silently as a refused connection. Since the whole
/// point of the launcher is that a non-technical friend can use it, the host needs to be
/// told "send them this one".
///
/// Detection is by adapter description first (each tool names its own adapter) and IP
/// range second, because ranges alone are ambiguous: Tailscale uses 100.64/10 which is
/// also carrier-grade NAT, and ZeroTier's range is configurable.
/// </summary>
public static class NetworkDiscovery
{
    public static List<LocalAddress> FindLocalAddresses()
    {
        var found = new List<LocalAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var description = $"{nic.Name} {nic.Description}";

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = info.Address;
                if (IPAddress.IsLoopback(ip)) continue;

                var bytes = ip.GetAddressBytes();

                // 169.254.x.x means DHCP failed; the adapter is up but useless.
                if (bytes[0] == 169 && bytes[1] == 254) continue;

                found.Add(new LocalAddress(ip, Classify(description, bytes), nic.Name));
            }
        }

        // Best candidate first: something that works over the internet, then LAN, then
        // anything that will not work at all.
        return found
            .OrderBy(a => a.Kind switch
            {
                AddressKind.Tailscale => 0,
                AddressKind.ZeroTier => 1,
                AddressKind.Hamachi => 2,
                AddressKind.Radmin => 3,
                AddressKind.Lan => 4,
                AddressKind.OtherVirtual => 5,
                _ => 9,
            })
            .ToList();
    }

    private static AddressKind Classify(string description, byte[] bytes)
    {
        bool Named(string token) => description.Contains(token, StringComparison.OrdinalIgnoreCase);

        // Adapter name is the reliable signal — each tool brands its own.
        if (Named("Tailscale")) return AddressKind.Tailscale;
        if (Named("Hamachi")) return AddressKind.Hamachi;
        if (Named("ZeroTier")) return AddressKind.ZeroTier;
        if (Named("Radmin")) return AddressKind.Radmin;

        // Full-tunnel VPNs route everything and break peer connections. Checked AFTER the
        // virtual-LAN names above, because Tailscale is also WireGuard-based and would
        // otherwise be misclassified as a VPN to turn off.
        if (Named("NordLynx") || Named("NordVPN") || Named("ExpressVPN") ||
            Named("ProtonVPN") || Named("OpenVPN") || Named("Mullvad"))
            return AddressKind.FullTunnelVpn;

        // Ranges as a fallback. 25/8 is Hamachi's; 100.64/10 is Tailscale's (shared with
        // carrier-grade NAT, hence name-first).
        if (bytes[0] == 25) return AddressKind.Hamachi;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return AddressKind.Tailscale;

        bool privateRange =
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 10 ||
            bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31;

        return privateRange ? AddressKind.Lan : AddressKind.OtherVirtual;
    }

    /// <summary>
    /// Try to reach a host:port. Lets a joining player find out they typed the wrong
    /// address in two seconds, rather than after a four-minute game load.
    /// </summary>
    public static async Task<string> TestConnection(string address, int port, int timeoutMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Enter an address first.";

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(address, port);

            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
                return $"No answer from {address}:{port} within {timeoutMs / 1000}s.\r\n" +
                       "Is the host in-game yet? It does not listen until the galaxy has loaded.";

            await connect;   // surface the real exception if it failed
            return $"Connected to {address}:{port} — the host is listening.";
        }
        catch (SocketException ex)
        {
            return $"Could not connect to {address}:{port} — {ex.SocketErrorCode}.\r\n" +
                   "Check the host is in-game, the address is current, and any full-tunnel VPN is off.";
        }
        catch (Exception ex)
        {
            return $"Could not connect: {ex.Message}";
        }
    }
}
