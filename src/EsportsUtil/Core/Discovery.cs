using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace EsportsUtil.Core;

/// <summary>T35: LAN server autodetection. The agent first-run wizard fires a tiny UDP probe at
/// the fixed discovery port (255.255.255.255 + every NIC's directed broadcast); every esportsutil
/// SERVER answers with its reachable http URL + build. Pure pieces (parse, targets, advertise-URL)
/// live here so they are testable on Linux; the socket paths are thin wrappers.</summary>
public static class Discovery
{
    public const int DefaultPort = 4301;
    public static readonly byte[] Probe = "ESU-DISCOVER"u8.ToArray();
    public const string Service = "esportsutil";

    public sealed record Reply(string ServerUrl, string Version);

    public static string ReplyJson(string serverUrl, string version) =>
        JsonSerializer.Serialize(new { service = Service, server_url = serverUrl, version }, Wire.Options);

    public static Reply? ParseReply(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!d.RootElement.TryGetProperty("service", out var svc) || svc.GetString() != Service) return null;
            var url = d.RootElement.TryGetProperty("server_url", out var u) ? u.GetString() : null;
            var ver = d.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
            return string.IsNullOrWhiteSpace(url) ? null : new Reply(url.TrimEnd('/'), ver);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What a 0.0.0.0-bound server advertises back to one probe: the address of the very
    /// interface the probe arrived on, so a multi-NIC box answers each requester with a reachable
    /// URL. A server configured to an explicit host advertises exactly that instead.</summary>
    public static string ResolveAdvertiseUrl(string configuredUrl, IPAddress localIface, int fallbackPort)
    {
        var u = FirstRunSetup.NormalizeServerUrl(configuredUrl);
        if (u is not null)
        {
            var host = new Uri(u).Host;
            if (host is not ("0.0.0.0" or "::" or "*")) return u;
        }
        var port = u is not null ? new Uri(u).Port : fallbackPort;
        return $"http://{localIface}:{port}";
    }

    /// <summary>Broadcast destinations worth probing: the global broadcast plus each active
    /// UP IPv4 interface's directed broadcast (covers subnets that drop 255.255.255.255).</summary>
    public static List<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ip.IPv4Mask;
                    if (mask is null || IPAddress.IsLoopback(ip.Address)) continue;
                    var m = mask.GetAddressBytes(); var a = ip.Address.GetAddressBytes();
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++) bcast[i] = (byte)(a[i] | ~m[i]);
                    var b = new IPAddress(bcast);
                    if (!targets.Contains(b)) targets.Add(b);
                }
            }
        }
        catch (NetworkInformationException) { /* keep the global broadcast at least */ }
        return targets;
    }

    /// <summary>One probe → one reply from a specific endpoint (loopback-friendly: the test seam
    /// and the wizard's re-check both use this).</summary>
    public static async Task<Reply?> QueryAsync(IPEndPoint ep, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            await udp.SendAsync(Probe, Probe.Length, ep);
            var got = await udp.ReceiveAsync(ct);
            return ParseReply(System.Text.Encoding.UTF8.GetString(got.Buffer));
        }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Flood every broadcast target with the probe, collect unique servers for
    /// timeoutMs. Never throws: no servers found = empty list.</summary>
    public static async Task<List<Reply>> DetectAsync(int port, int timeoutMs, CancellationToken ct = default)
    {
        var found = new List<Reply>();
        var seen = new HashSet<string>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.ReceiveTimeout = timeoutMs;
            var deadline = Environment.TickCount64 + timeoutMs;
            foreach (var t in BroadcastTargets())
                await udp.SendAsync(Probe, Probe.Length, new IPEndPoint(t, port));
            while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
            {
                var left = (int)(deadline - Environment.TickCount64);
                if (left <= 0) break;
                udp.Client.ReceiveTimeout = left;
                UdpReceiveResult got;
                try { got = await udp.ReceiveAsync(ct); }
                catch (SocketException) { break; }   // receive window closed
                var r = ParseReply(System.Text.Encoding.UTF8.GetString(got.Buffer));
                if (r is null || !seen.Add(r.ServerUrl)) continue;
                found.Add(r);
            }
        }
        catch (SocketException) { /* broadcast unavailable on this host/OS */ }
        return found;
    }
}
