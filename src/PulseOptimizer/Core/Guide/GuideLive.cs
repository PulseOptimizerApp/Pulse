using System.Net.Http;

namespace Pulse.Core.Guide;

/// <summary>
/// Keeps the PC guide current: downloads guide.json from Pulse's own website (only a GET, nothing is sent) when the
/// guide is opened, at most every 15 minutes, and keeps the last good copy for offline use.
/// </summary>
public static class GuideLive
{
    /// <summary>The only address this file talks to.</summary>
    public const string Url = AppInfo.Website + "guide/guide.json";

    static string CacheFile => Path.Combine(TweakEngine.DataDir, "guide.json");
    static DateTime _lastTry;
    static bool _busy;

    /// <summary>The newest guide Pulse has: downloaded, saved from an earlier download, or the built-in copy.</summary>
    public static GuideContent Current { get; private set; } = Newest(Cached(), GuideData.BuiltIn);

    /// <summary>True when <see cref="Current"/> came from the website during this run of Pulse.</summary>
    public static bool FreshThisRun { get; private set; }

    /// <summary>True when the last download attempt failed (offline, or the site was down).</summary>
    public static bool LastTryFailed { get; private set; }

    /// <summary>Raised on the calling thread's continuation when a newer guide arrived.</summary>
    public static event Action Changed;

    static GuideContent Newest(GuideContent a, GuideContent b) => a == null ? b : b == null ? a : a.Updated >= b.Updated ? a : b;

    static GuideContent Cached()
    {
        try { return File.Exists(CacheFile) ? GuideContent.Parse(File.ReadAllText(CacheFile)) : null; } catch { return null; }
    }

    /// <summary>Downloads the latest guide if it's been a while. Returns true if the guide changed.</summary>
    public static async Task<bool> Refresh(bool force = false)
    {
        if (_busy || (!force && DateTime.UtcNow - _lastTry < TimeSpan.FromMinutes(15))) return false;
        _busy = true;
        _lastTry = DateTime.UtcNow;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 2_000_000 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pulse");
            var json = await http.GetStringAsync(Url + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute); // skip stale CDN copies
            var g = GuideContent.Parse(json);
            LastTryFailed = g == null;
            if (g == null) return false;
            FreshThisRun = true;
            if (g.Updated < Current.Updated) return false;
            bool changed = g.Updated > Current.Updated || json.Length != _lastLength;
            _lastLength = json.Length;
            Current = g;
            try { Directory.CreateDirectory(TweakEngine.DataDir); await File.WriteAllTextAsync(CacheFile, json); } catch { }
            if (changed) Changed?.Invoke();
            return changed;
        }
        catch { LastTryFailed = true; return false; }
        finally { _busy = false; }
    }

    static int _lastLength;
}
