using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Pulse.Core;

/// <summary>Ping results for one address: average, jitter (how much it jumps around) and loss.</summary>
public record PingStats(string Address, int Sent, int Received, double AvgMs, double JitterMs, double MaxMs)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
    public bool Answered => Received > 0;
}

/// <summary>
/// "Is it my PC, my internet or the game's server?" Pings the router (your own network, Wi-Fi included) and, when it's
/// known, the game server, then says in plain words where the problem is. Sends only small ping packets: nothing is
/// uploaded or downloaded.
/// </summary>
public static class NetworkCheck
{
    /// <summary>The router (default gateway) of the connection in use, or null.</summary>
    public static IPAddress Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch { return null; }
    }

    /// <summary>True when the connection in use is Wi-Fi.</summary>
    public static bool OnWifi()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0).ToList();
            return up.Count > 0 && up.All(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch { return false; }
    }

    public static async Task<PingStats> PingMany(IPAddress address, int count = 20, int gapMs = 150)
    {
        var times = new List<double>();
        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(address, 1000);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
            }
            catch { }
            await Task.Delay(gapMs);
        }
        return Summarize(address.ToString(), count, times);
    }

    internal static PingStats Summarize(string address, int sent, IReadOnlyList<double> times)
    {
        if (times.Count == 0) return new PingStats(address, sent, 0, 0, 0, 0);
        double jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        return new PingStats(address, sent, times.Count, times.Average(), jitter, times.Max());
    }

    /// <summary>
    /// The server of the Roblox game you're in (or were last in), from Roblox's own log: it notes the server address
    /// when it joins. Null if none is found.
    /// </summary>
    public static IPAddress RobloxServer(string logsFolder)
    {
        try
        {
            var newest = new DirectoryInfo(logsFolder).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null || DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromHours(6)) return null;
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return ServerFromLog(sr.ReadToEnd());
        }
        catch { return null; }
    }

    internal static IPAddress ServerFromLog(string log)
    {
        string last = null;
        foreach (Match m in Regex.Matches(log ?? "", @"(?:UDMUX Address = |Connection accepted from |Joining game '[^']*' place \d+ at |Connecting to )(\d{1,3}(?:\.\d{1,3}){3})"))
            last = m.Groups[1].Value;
        return last != null && IPAddress.TryParse(last, out var ip) ? ip : null;
    }

    /// <summary>Plain-words verdict from the router and server results.</summary>
    public static string Verdict(PingStats router, PingStats server, bool wifi)
    {
        if (router == null || !router.Answered)
            return "Pulse couldn't reach your router, so it can't test your network. If games disconnect, restart the router.";
        bool homeBad = router.LossPercent >= 5 || router.JitterMs >= 15 || router.AvgMs >= 20;
        if (homeBad)
            return wifi
                ? "The problem is between your PC and your router: Wi-Fi is dropping or delaying packets. A cable, or the 5 GHz network closer to the router, fixes this."
                : "The problem is between your PC and your router, even on a cable. Try another cable or port, and restart the router.";
        if (server == null)
            return "Your home network is fine. Join a game, then check again to test the game's server too.";
        if (!server.Answered)
            return "Your home network is fine. The game server doesn't answer pings (many don't), so check in-game ping for the rest.";
        if (server.LossPercent >= 3 || server.JitterMs >= 20)
            return "Your home network is fine, but the path to the game server is unstable (packet loss or spikes). That's your internet provider or the server, not your PC.";
        if (server.AvgMs >= 120)
            return "Your connection is stable, but the server is far away. Pick a closer region or server if the game lets you.";
        return "All good: your network and the path to the game server are stable.";
    }
}
