using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace Pulse.Core;

/// <summary>One finished speed test.</summary>
public sealed class SpeedResult
{
    public DateTime At { get; set; }
    public double DownMbps { get; set; }
    public double UpMbps { get; set; }
    /// <summary>Ping with nothing else going on (median of 20).</summary>
    public double IdleMs { get; set; }
    public double JitterMs { get; set; }
    /// <summary>Ping while the line is busy downloading / uploading: the lag spikes you feel in games.</summary>
    public double LoadedDownMs { get; set; }
    public double LoadedUpMs { get; set; }
    public double LossPercent { get; set; } = -1;
    public string Server { get; set; } = "";
    public bool Wifi { get; set; }
    public long BytesUsed { get; set; }

    /// <summary>How much ping rises when the connection is busy (the worse of download and upload).</summary>
    public double Bufferbloat => Math.Max(0, Math.Max(LoadedDownMs, LoadedUpMs) - IdleMs);

    /// <summary>A to F, the way bufferbloat tests grade it: how much extra lag a busy connection adds.</summary>
    public string BloatGrade => Bufferbloat switch { < 5 => "A+", < 30 => "A", < 60 => "B", < 200 => "C", < 400 => "D", _ => "F" };
}

public sealed class SpeedState
{
    public List<SpeedResult> History { get; set; } = new();
}

/// <summary>
/// The speed test, aimed at gaming: download and upload like other speed tests, and also what they leave out:
/// jitter, packet loss, and how much your ping rises while the connection is busy (bufferbloat), which is what
/// turns a fast connection into a laggy one. It runs against Cloudflare's public speed-test servers (the ones
/// speed.cloudflare.com uses), picking the nearest automatically. Nothing about this PC is sent; the upload is
/// random filler.
/// </summary>
public static class SpeedTest
{
    const string Base = "https://speed.cloudflare.com";
    static readonly HttpClient Http = new(new SocketsHttpHandler { MaxConnectionsPerServer = 16, AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    { Timeout = TimeSpan.FromSeconds(30) };

    public enum Phase { Ping, Download, Upload, Loss, Done }

    /// <summary>Progress while testing: the phase, the live number (Mbps or ms) and how far through it is (0–1).</summary>
    public readonly record struct Progress(Phase Phase, double Value, double Fraction);

    public static async Task<SpeedResult> Run(IProgress<Progress> progress, CancellationToken ct)
    {
        var r = new SpeedResult { At = DateTime.Now, Wifi = NetworkCheck.OnWifi() };
        long bytes = 0;

        // 1. Idle ping and jitter: 20 tiny requests (the time to the first byte back).
        var idle = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ms = await Rtt(ct);
            if (ms > 0) idle.Add(ms);
            progress?.Report(new Progress(Phase.Ping, idle.Count > 0 ? Median(idle) : 0, (i + 1) / 20.0));
        }
        if (idle.Count == 0) throw new HttpRequestException("The speed test server didn't answer.");
        r.IdleMs = Median(idle);
        r.JitterMs = Jitter(idle);
        r.Server = await ServerName(ct);

        // 2. Download: several streams at once for 10 seconds, ping measured alongside.
        (r.DownMbps, var dl, r.LoadedDownMs) = await Transfer(true, TimeSpan.FromSeconds(10), progress, ct);
        bytes += dl;

        // 3. Upload: the same, the other way.
        (r.UpMbps, var ul, r.LoadedUpMs) = await Transfer(false, TimeSpan.FromSeconds(8), progress, ct);
        bytes += ul;

        // 4. Packet loss: 60 pings to Cloudflare's DNS (1.1.1.1), the same small packets games care about.
        progress?.Report(new Progress(Phase.Loss, 0, 0));
        var loss = await NetworkCheck.PingMany(IPAddress.Parse("1.1.1.1"), 60, 40);
        if (loss.Sent > 0) r.LossPercent = loss.LossPercent;
        progress?.Report(new Progress(Phase.Done, 0, 1));
        r.BytesUsed = bytes;
        return r;
    }

    static async Task<double> Rtt(CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var res = await Http.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return res.IsSuccessStatusCode ? sw.Elapsed.TotalMilliseconds : -1;
        }
        catch (OperationCanceledException) { throw; }
        catch { return -1; }
    }

    /// <summary>The Cloudflare city answering (from its "cf-ray" header, like "…-IAD").</summary>
    static async Task<string> ServerName(CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.Headers.TryGetValues("cf-ray", out var v) && v.FirstOrDefault() is { } ray && ray.Contains('-'))
                return ray[(ray.LastIndexOf('-') + 1)..];
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Moves data for a while on 6 streams and measures the speed after a short warm-up (the first 2 seconds are left
    /// out, as connections speed up). Pings every 250 ms meanwhile, for the loaded latency.
    /// </summary>
    static async Task<(double Mbps, long Bytes, double LoadedMs)> Transfer(bool down, TimeSpan length, IProgress<Progress> progress, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        long total = 0, counted = 0;
        var sw = Stopwatch.StartNew();
        var warm = TimeSpan.FromSeconds(2);
        double warmAt = -1;
        var phase = down ? Phase.Download : Phase.Upload;
        var upload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(upload);

        void Count(int n)
        {
            Interlocked.Add(ref total, n);
            if (sw.Elapsed >= warm) Interlocked.Add(ref counted, n);
        }

        async Task Stream()
        {
            var buf = new byte[64 * 1024];
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (down)
                    {
                        using var res = await Http.GetAsync($"{Base}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
                        int n;
                        while ((n = await s.ReadAsync(buf, cts.Token)) > 0) Count(n);
                    }
                    else
                    {
                        using var content = new CountingContent(upload, Count);
                        using var res = await Http.PostAsync($"{Base}/__up", content, cts.Token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200); }
            }
        }

        var loaded = new List<double>();
        async Task Pinger()
        {
            await Task.Delay(1500);
            while (!cts.IsCancellationRequested)
            {
                try { var ms = await Rtt(cts.Token); if (ms > 0) lock (loaded) loaded.Add(ms); }
                catch (OperationCanceledException) { break; }
                await Task.Delay(250);
            }
        }

        var tasks = Enumerable.Range(0, 6).Select(_ => Stream()).Append(Pinger()).ToList();
        while (sw.Elapsed < length && !outer.IsCancellationRequested)
        {
            await Task.Delay(200);
            if (warmAt < 0 && sw.Elapsed >= warm) warmAt = sw.Elapsed.TotalSeconds;
            double secs = warmAt >= 0 ? sw.Elapsed.TotalSeconds - warmAt : sw.Elapsed.TotalSeconds;
            double live = warmAt >= 0 && secs > 0.2 ? Interlocked.Read(ref counted) * 8 / secs / 1e6 : Interlocked.Read(ref total) * 8 / Math.Max(0.2, sw.Elapsed.TotalSeconds) / 1e6;
            progress?.Report(new Progress(phase, live, sw.Elapsed / length));
        }
        double measured = sw.Elapsed.TotalSeconds - Math.Max(0, warmAt);
        cts.Cancel();
        try { await Task.WhenAll(tasks); } catch { }
        outer.ThrowIfCancellationRequested();
        double mbps = measured > 0 ? Interlocked.Read(ref counted) * 8 / measured / 1e6 : 0;
        double loadedMs;
        lock (loaded) loadedMs = loaded.Count > 0 ? Median(loaded) : 0;
        return (mbps, Interlocked.Read(ref total), loadedMs);
    }

    /// <summary>An upload body that counts bytes as they leave, so the live speed is real.</summary>
    sealed class CountingContent : HttpContent
    {
        readonly byte[] _data;
        readonly Action<int> _count;
        public CountingContent(byte[] data, Action<int> count) { _data = data; _count = count; Headers.ContentLength = data.Length; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            for (int i = 0; i < _data.Length; i += 64 * 1024)
            {
                int n = Math.Min(64 * 1024, _data.Length - i);
                await stream.WriteAsync(_data.AsMemory(i, n));
                _count(n);
            }
        }
        protected override bool TryComputeLength(out long length) { length = _data.Length; return true; }
    }

    internal static double Median(List<double> l)
    {
        if (l.Count == 0) return 0;
        var o = l.OrderBy(x => x).ToList();
        return o.Count % 2 == 1 ? o[o.Count / 2] : (o[o.Count / 2 - 1] + o[o.Count / 2]) / 2;
    }

    /// <summary>How much ping jumps from one measurement to the next (the average change).</summary>
    internal static double Jitter(List<double> l) => l.Count < 2 ? 0 : l.Zip(l.Skip(1), (a, b) => Math.Abs(b - a)).Average();

    // ───────── what it means for games, and how to fix it ─────────

    public enum Level { Great, Ok, Bad }

    public sealed record Finding(string Area, Level Level, string Summary, string Why, string[] Fixes);

    /// <summary>Each part of the result, rated for competitive games, with what to do when it isn't good.</summary>
    public static List<Finding> Diagnose(SpeedResult r)
    {
        var f = new List<Finding>();
        var wifiFix = r.Wifi ? new[] { "Plug your PC into the router with an Ethernet cable. It's the single biggest fix for ping, jitter and loss; Wi-Fi shares the air with every device and wall nearby." } : Array.Empty<string>();

        // Ping.
        f.Add(r.IdleMs switch
        {
            < 20 => new("Ping", Level.Great, "Very low ping to the nearest server.", "", Array.Empty<string>()),
            < 45 => new("Ping", Level.Ok, "Normal ping.", "", Array.Empty<string>()),
            _ => new("Ping", Level.Bad, "High ping even with nothing else running.",
                "Ping is the time for a packet to reach a server and come back. High idle ping usually comes from distance, Wi-Fi, or the route your provider takes.",
                wifiFix.Concat(new[]
                {
                    "Close downloads, cloud backups (OneDrive, Google Drive), game launchers updating and streaming on other devices, then test again.",
                    "Restart the router and modem: unplug them for 30 seconds. It clears a lot of slowdowns.",
                    "In your game, pick the closest server region. The ping here is to the nearest Cloudflare server; game servers can be further away.",
                    "If ping stays high on a cable too, contact your provider: ask them to check the line and the route to your area. Mention the time of day it's worst.",
                }).ToArray()),
        });

        // Jitter.
        f.Add(r.JitterMs switch
        {
            < 3 => new("Jitter", Level.Great, "Ping is steady.", "", Array.Empty<string>()),
            < 10 => new("Jitter", Level.Ok, "Ping moves around a little.", "", Array.Empty<string>()),
            _ => new("Jitter", Level.Bad, "Ping jumps around a lot.",
                "Jitter is how much your ping changes from moment to moment. In games it feels like rubber-banding and shots that don't register, even when the average ping looks fine.",
                wifiFix.Concat(new[]
                {
                    r.Wifi ? "If a cable isn't possible, use the 5 GHz Wi-Fi band and sit closer to the router, with no walls or microwave in between." : "Check the Ethernet cable: try another one, and another port on the router.",
                    "Stop other devices from streaming or downloading while you play, or set up QoS on your router (see bufferbloat below).",
                    "Update your network adapter driver from your motherboard or laptop maker's website.",
                }).ToArray()),
        });

        // Packet loss.
        if (r.LossPercent >= 0)
            f.Add(r.LossPercent switch
            {
                0 => new("Packet loss", Level.Great, "No packets lost.", "", Array.Empty<string>()),
                < 1.5 => new("Packet loss", Level.Ok, "A packet lost now and then.", "", Array.Empty<string>()),
                _ => new("Packet loss", Level.Bad, Pulse.Ui.Loc.F("{0:0.#}% of packets were lost.", r.LossPercent),
                    "Lost packets are game updates that never arrive: enemies teleport, shots don't count, and the game stutters. Any steady loss is worth fixing.",
                    wifiFix.Concat(new[]
                    {
                        "Restart the router and modem (unplug for 30 seconds).",
                        "Check every cable from the wall to the router: swap the Ethernet cable, make sure the coax or fiber cable is tight and not bent sharply.",
                        "Run the test again at a different time. Loss only in the evening usually means your provider's network is overloaded.",
                        "If it keeps happening, call your provider and say \"I have packet loss\": it's something they can measure and fix on the line.",
                    }).ToArray()),
            });

        // Bufferbloat.
        f.Add(r.BloatGrade switch
        {
            "A+" or "A" => new("Bufferbloat", Level.Great, Pulse.Ui.Loc.F("Grade {0}: ping stays low when the connection is busy.", r.BloatGrade), "", Array.Empty<string>()),
            "B" => new("Bufferbloat", Level.Ok, Pulse.Ui.Loc.F("Grade {0}: ping rises {1:0} ms when the connection is busy.", "B", r.Bufferbloat), "", Array.Empty<string>()),
            _ => new("Bufferbloat", Level.Bad, Pulse.Ui.Loc.F("Grade {0}: ping rises {1:0} ms when the connection is busy.", r.BloatGrade, r.Bufferbloat),
                "When someone downloads or uploads, your router fills its queue and every game packet waits in line behind the download. That's the lag spike you get when someone starts watching Netflix or a game updates.",
                new[]
                {
                    "Turn on SQM or Smart Queue in your router (often called QoS, \"Smart QoS\", \"Adaptive QoS\" or \"Game mode\"). Set it to about 90% of the download and upload speeds above. This fixes bufferbloat in most cases.",
                    "If your router has no such setting, many newer routers and mesh systems do (look for SQM, Cake or fq_codel in the specs). It's usually the router, not the provider.",
                    "Until then: pause big downloads and uploads (game updates, cloud backups, streaming in 4K) while you play.",
                    "Limit Steam, Battle.net and Epic download speeds in their settings so they never fill the line.",
                }),
        });

        // Speed.
        f.Add(r.DownMbps switch
        {
            >= 100 => new("Download", Level.Great, "Plenty of download speed for gaming, streaming and updates.", "", Array.Empty<string>()),
            >= 25 => new("Download", Level.Ok, "Enough for gaming; big game updates take a while.", "", Array.Empty<string>()),
            _ => new("Download", Level.Bad, "Slow download speed.",
                "Games themselves need little bandwidth (under 1 Mbps), but slow downloads mean long updates, and anything else downloading will cause lag.",
                wifiFix.Concat(new[]
                {
                    "Compare with the speed in your internet plan. Far below it on a cable means a problem with the line, the modem or the router.",
                    "Turn off any VPN and test again.",
                    "Restart the router and modem, and check nothing else is downloading.",
                    "If it stays well below your plan, contact your provider with these results.",
                }).ToArray()),
        });
        f.Add(r.UpMbps switch
        {
            >= 20 => new("Upload", Level.Great, "Plenty of upload for streaming and voice.", "", Array.Empty<string>()),
            >= 5 => new("Upload", Level.Ok, "Enough for games and voice chat; tight for streaming in high quality.", "", Array.Empty<string>()),
            _ => new("Upload", Level.Bad, "Slow upload speed.",
                "Upload carries your game input, voice chat and any stream. When it's full, everything you send waits.",
                new[]
                {
                    "Pause cloud backups and photo syncing while you play; they upload in the background.",
                    "Stream at a lower bitrate (6 Mbps or less on a slow upload).",
                    "If upload is far below your plan, contact your provider.",
                }),
        });
        return f;
    }

    /// <summary>One line on what the result means for competitive games.</summary>
    public static (Level Level, string Text) Verdict(SpeedResult r)
    {
        var d = Diagnose(r).Where(x => x.Area is "Ping" or "Jitter" or "Packet loss" or "Bufferbloat").ToList();
        if (d.All(x => x.Level == Level.Great)) return (Level.Great, "Excellent for competitive games");
        if (d.Any(x => x.Level == Level.Bad)) return (Level.Bad, "You'll likely feel lag in games: see what to fix below");
        return (Level.Ok, "Good for gaming");
    }

    // ───────── history ─────────

    static SpeedState S => TweakEngine.State.Speed ??= new SpeedState();

    public static void Save(SpeedResult r)
    {
        S.History.Insert(0, r);
        if (S.History.Count > 30) S.History.RemoveRange(30, S.History.Count - 30);
        TweakEngine.Save();
    }

    public static List<SpeedResult> History => S.History;
}
