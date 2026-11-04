using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;

namespace Pulse.Core.Clips;

/// <summary>
/// FFmpeg, the open-source video engine Clips records with. Pulse doesn't carry it: it's downloaded only when the user
/// turns Clips on, from this project's own GitHub release (the same release as the Pulse exe), and its SHA-256 is
/// checked against the value built into Pulse before it's used. That download is the only time Clips goes online.
/// </summary>
public static class Ffmpeg
{
    public const string Repo = "PulseOptimizer-app/PulseOptimizer";
    public const string AssetName = "ffmpeg-win64.zip";

    public static string Dir => Path.Combine(TweakEngine.DataDir, "ffmpeg");
    public static string Exe => Path.Combine(Dir, "ffmpeg.exe");
    public static bool Installed => File.Exists(Exe);

    /// <summary>Pulse's own version ("0.2.7"); "0.0.0" for test builds.</summary>
    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>The SHA-256 of the FFmpeg zip released with this build, embedded by the build. Empty in test builds.</summary>
    internal static string ExpectedSha256
    {
        get
        {
            try
            {
                using var s = typeof(Ffmpeg).Assembly.GetManifestResourceStream("ffmpeg.sha256");
                if (s == null) return "";
                using var r = new StreamReader(s);
                return r.ReadToEnd().Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            }
            catch { return ""; }
        }
    }

    static string ReleaseUrl(string file) => AppVersion == "0.0.0"
        ? $"https://github.com/{Repo}/releases/latest/download/{file}"
        : $"https://github.com/{Repo}/releases/download/v{AppVersion}/{file}";

    /// <summary>Downloads, checks and unpacks FFmpeg. Progress is 0..1.</summary>
    public static async Task Install(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var zip = Path.Combine(Dir, "download.zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseOptimizer");

        // The hash to trust: built into this exe, or for test builds the latest release's published SHA256SUMS.
        var expected = AppVersion == "0.0.0" ? "" : ExpectedSha256; // test builds download the latest release's FFmpeg
        if (expected.Length == 0)
        {
            var sums = await http.GetStringAsync(ReleaseUrl("SHA256SUMS.txt"), ct);
            expected = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1] == AssetName)?[0]?.ToLowerInvariant() ?? "";
            if (expected.Length == 0) throw new InvalidOperationException("The release doesn't list FFmpeg's checksum, so Pulse won't use it.");
        }

        using (var resp = await http.GetAsync(ReleaseUrl(AssetName), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Couldn't download FFmpeg (HTTP {(int)resp.StatusCode}). Check your internet connection and try again.");
            long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }

        string actual;
        await using (var f = File.OpenRead(zip)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(zip);
            throw new InvalidOperationException("The FFmpeg download didn't match its checksum, so Pulse deleted it. Try again later.");
        }

        using (var archive = ZipFile.OpenRead(zip))
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(Dir, entry.Name)); // flat: no folders from the archive
                if (!target.StartsWith(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase)) continue;
                entry.ExtractToFile(target, true);
            }
        File.Delete(zip);
        if (!Installed) throw new InvalidOperationException("The FFmpeg download didn't contain ffmpeg.exe.");
    }

    /// <summary>Removes the downloaded engine (when Clips is turned off and the user wants the space back).</summary>
    public static void Uninstall()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }

    public static ProcessStartInfo Start(string args, bool redirectOut = false) => new(Exe, args)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = redirectOut,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        WorkingDirectory = Dir,
    };

    /// <summary>Runs FFmpeg to completion. Returns (exit code, error text).</summary>
    public static async Task<(int Code, string Err)> Run(string args, CancellationToken ct = default, int timeoutMs = 600_000)
    {
        using var p = Process.Start(Start(args))!;
        var err = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await err);
    }
}
