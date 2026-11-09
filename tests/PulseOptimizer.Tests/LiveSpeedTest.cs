using Pulse.Core;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>
/// Runs the real speed test against Cloudflare. Only when PULSE_LIVE=1 (the "Live check" workflow), so normal builds
/// never depend on the network.
/// </summary>
public class LiveSpeedTest
{
    readonly ITestOutputHelper _out;
    public LiveSpeedTest(ITestOutputHelper o) => _out = o;

    static bool Live => Environment.GetEnvironmentVariable("PULSE_LIVE") == "1";

    [Fact]
    public async Task Speed_test_runs_end_to_end()
    {
        if (!Live) return;
        var log = new List<string>();
        var progress = new SyncProgress(p => log.Add($"{p.Phase} {p.Value:0.0} {p.Fraction:0.00}"));
        var r = await SpeedTest.Run(progress, CancellationToken.None);
        _out.WriteLine($"down {r.DownMbps:0.0} Mbps · up {r.UpMbps:0.0} Mbps · idle {r.IdleMs:0.0} ms · jitter {r.JitterMs:0.0} · loaded {r.LoadedDownMs:0}/{r.LoadedUpMs:0} ms · loss {r.LossPercent} · server {r.Server} · {r.BytesUsed / 1e6:0} MB");
        foreach (var l in log.Where((_, i) => i % 5 == 0)) _out.WriteLine(l);
        Assert.True(r.IdleMs > 0, "no ping");
        Assert.True(r.DownMbps > 1, "no download");
        Assert.True(r.UpMbps > 1, "no upload");
    }

    [Fact]
    public async Task Raw_requests_to_cloudflare()
    {
        if (!Live) return;
        using var h = new System.Net.Http.HttpClient();
        foreach (var url in new[] { "https://speed.cloudflare.com/__down?bytes=0", "https://speed.cloudflare.com/__down?bytes=1000", "https://speed.cloudflare.com/meta" })
        {
            try
            {
                using var res = await h.GetAsync(url);
                var body = await res.Content.ReadAsStringAsync();
                _out.WriteLine($"{url} → {(int)res.StatusCode} {body[..Math.Min(300, body.Length)]}");
            }
            catch (Exception ex) { _out.WriteLine($"{url} → {ex.GetType().Name}: {ex.Message}"); }
        }
        try
        {
            using var res = await h.PostAsync("https://speed.cloudflare.com/__up", new System.Net.Http.ByteArrayContent(new byte[100000]));
            _out.WriteLine($"__up → {(int)res.StatusCode}");
        }
        catch (Exception ex) { _out.WriteLine($"__up → {ex.GetType().Name}: {ex.Message}"); }
    }

    [Fact]
    public async Task Repeated_downloads_status_codes()
    {
        if (!Live) return;
        foreach (var browserLike in new[] { false, true })
        {
            using var h = new System.Net.Http.HttpClient();
            if (browserLike)
            {
                h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pulse/1.0");
                h.DefaultRequestHeaders.Referrer = new Uri("https://speed.cloudflare.com/");
                h.DefaultRequestHeaders.Add("Origin", "https://speed.cloudflare.com");
            }
            var codes = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var res = await h.GetAsync($"https://speed.cloudflare.com/__down?bytes={(i % 2 == 0 ? 25_000_000 : 10_000_000)}");
                    var bytes = (await res.Content.ReadAsByteArrayAsync()).Length;
                    codes.Add($"{(int)res.StatusCode}:{bytes / 1_000_000}MB/{sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex) { codes.Add(ex.GetType().Name); }
            }
            _out.WriteLine((browserLike ? "browser-like: " : "plain: ") + string.Join(" ", codes));
            try
            {
                using var meta = await h.GetAsync("https://speed.cloudflare.com/meta");
                _out.WriteLine("meta " + (int)meta.StatusCode + " " + (await meta.Content.ReadAsStringAsync()));
            }
            catch (Exception ex) { _out.WriteLine("meta " + ex.Message); }
        }
    }

    sealed class SyncProgress : IProgress<SpeedTest.Progress>
    {
        readonly Action<SpeedTest.Progress> _a;
        public SyncProgress(Action<SpeedTest.Progress> a) => _a = a;
        public void Report(SpeedTest.Progress p) { lock (this) _a(p); }
    }
}
