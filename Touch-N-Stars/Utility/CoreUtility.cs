using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace TouchNStars.Utility;

public static class CoreUtility {
    public static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "Logs");
    public static readonly string AfPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "AutoFocus");
    public static readonly string Hips2FitsUrl = "http://alasky.cds.unistra.fr/hips-image-services/hips2fits";

    // Asks ninaAPI for its port on every call: the cached one is the default 1888 until something asked, and the API
    // can be restarted on another port
    public static async Task<string> GetApiUrl() {
        int port = await TouchNStars.Communicator.GetPort(true);

        if (port <= 0) {
            throw new AdvancedApiNotRunningException();
        }

        return $"http://localhost:{port}/v2/api";
    }

    public static double HmsToDegrees(string hms) {
        string[] hmsParts = hms.Split(':');
        if (hmsParts.Length == 3) {
            return (double.Parse(hmsParts[0]) * 15) + (double.Parse(hmsParts[1]) * (15.0 / 60)) + (double.Parse(hmsParts[2]) * (15.0 / 3600));
        } else {
            throw new ArgumentException("HMS string must be in the format HH:MM:SS, was: " + hms);
        }
    }

    public static double DmsToDegrees(string dms) {
        int sign = dms.StartsWith('-') ? -1 : 1;
        string stripped = dms.StartsWith('-') || dms.StartsWith('+') ? dms.Substring(1) : dms;

        string[] dmsParts = stripped.Split(':');

        if (dmsParts.Length == 3) {
            return (double.Parse(dmsParts[0]) + (double.Parse(dmsParts[1]) / 60) + (double.Parse(dmsParts[2]) / 3600)) * sign;
        } else {
            throw new ArgumentException("DMS string must be in the format DD:MM:SS.s, was: " + dms);
        }
    }

    public static string SafeRead(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string content = reader.ReadToEnd();
        return content;
    }

    public static Dictionary<string, string> GetLocalNames() => BuildLocalNames();

    public static bool IsPortAvailable(int port) {
        bool isPortAvailable = true;

        IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        IPEndPoint[] ipEndPoints = ipGlobalProperties.GetActiveTcpListeners();

        foreach (IPEndPoint endPoint in ipEndPoints) {
            if (endPoint.Port == port) {
                isPortAvailable = false;
                break;
            }
        }

        return isPortAvailable;
    }

    public static int GetNearestAvailablePort(int startPort) {
        using var watch = MyStopWatch.Measure();
        int port = startPort;
        while (!IsPortAvailable(port)) {
            port++;
        }
        return port;
    }

    public static string GetIPv4Address() {
        IPAddress preferred = GetPreferredIPv4Address();
        if (preferred != null) {
            return preferred.ToString();
        }

        return GetFallbackIpv4Address();
    }

    private static Dictionary<string, string> BuildLocalNames() {
        var names = new Dictionary<string, string> {
            { "LOCALHOST", "localhost" }
        };

        string hostName = Dns.GetHostName();
        if (!string.IsNullOrEmpty(hostName)) {
            names["HOSTNAME"] = hostName;
        }

        string ipv4 = GetIPv4Address();
        if (!string.IsNullOrEmpty(ipv4)) {
            names["IPADRESS"] = ipv4;
        }

        return names;
    }

    private static IPAddress GetPreferredIPv4Address() {
        try {
            var candidates = new List<(IPAddress Address, int Score, int Order)>();

            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces()) {
                if (networkInterface == null) {
                    continue;
                }

                if (networkInterface.OperationalStatus != OperationalStatus.Up) {
                    continue;
                }

                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses.Any(g => g?.Address != null && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses) {
                    if (unicast?.Address == null) {
                        continue;
                    }

                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) {
                        continue;
                    }

                    if (IPAddress.IsLoopback(unicast.Address)) {
                        continue;
                    }

                    if (IsAutomaticPrivateAddress(unicast.Address)) {
                        continue;
                    }

                    int score = CalculateAddressScore(unicast.Address, hasGateway, networkInterface.Speed);
                    int order = GetAddressTieBreaker(unicast.Address);
                    candidates.Add((unicast.Address, score, order));
                }
            }

            if (candidates.Count == 0) {
                return null;
            }

            var best = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Order)
                .First();

            return best.Address;
        } catch (Exception ex) {
            Logger.Warning($"Failed to resolve preferred IPv4 address: {ex.Message}");
            return null;
        }
    }

    private static int CalculateAddressScore(IPAddress address, bool hasGateway, long linkSpeed) {
        byte[] octets = address.GetAddressBytes();

        int score = octets[0] switch {
            192 when octets[1] == 168 => 80,
            10 => 70,
            172 when octets[1] is >= 16 and <= 31 => 60,
            _ => 40
        };

        if (hasGateway) {
            score += 10;
        }

        if (linkSpeed > 0) {
            if (linkSpeed >= 1_000_000_000) {
                score += 5;
            } else if (linkSpeed >= 100_000_000) {
                score += 3;
            } else {
                score += 1;
            }
        }

        return score;
    }

    private static int GetAddressTieBreaker(IPAddress address) {
        byte[] octets = address.GetAddressBytes();
        return (octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3];
    }

    private static bool IsAutomaticPrivateAddress(IPAddress address) {
        byte[] octets = address.GetAddressBytes();
        return octets[0] == 169 && octets[1] == 254;
    }

    private static string GetFallbackIpv4Address() {
        try {
            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint) {
                return endPoint.Address.ToString();
            }
        } catch (Exception ex) {
            Logger.Debug($"IPv4 fallback resolution failed: {ex.Message}");
        }

        return "127.0.0.1";
    }
}

internal sealed class AdvancedApiNotRunningException : InvalidOperationException {

    public AdvancedApiNotRunningException()
        : base("The Advanced API web server is not running; start it in the Advanced API plugin settings") {
    }
}
