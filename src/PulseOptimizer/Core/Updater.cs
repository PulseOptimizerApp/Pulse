using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Pulse.Core.Clips;

namespace Pulse.Core;

/// <summary>
/// Checks whether a newer Pulse is out, and installs it when the user says so. Only ever talks to this project's own
/// GitHub releases; the new exe is checked against the release's published SHA-256 before it replaces this one.
/// Nothing is sent: it's a plain download of a public page and file.
/// </summary>
public static class Updater
{
    const string Repo = "PulseOptimizer-app/PulseOptimizer";
    const string Latest = "https://github.com/" + Repo + "/releases/latest";

    /// <summary>The version tag of the newest release ("0.3.1"), or null if it couldn't be checked.</summary>
    public static async Task<string> LatestVersion(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseOptimizer");
            // GitHub answers /releases/latest with a redirect to /releases/tag/vX.Y.Z.
            using var r = await http.GetAsync(Latest, ct);
            return TagFromUrl(r.Headers.Location?.ToString());
        }
        catch { return null; }
    }

    internal static string TagFromUrl(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url ?? "", @"/releases/tag/v?(\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/> (dev builds 0.0.0 never update).</summary>
    public static bool IsNewer(string latest, string current) =>
        current != "0.0.0" && Version.TryParse(latest, out var l) && Version.TryParse(current, out var c) && l > c;

    /// <summary>
    /// Downloads the new exe next to this one, checks its SHA-256 against the release's SHA256SUMS, then restarts into
    /// it. Returns an error message, or null if Pulse is about to restart.
    /// </summary>
    public static async Task<string> Install(string version, IProgress<double> progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return "Couldn't find Pulse's own file.";
        var dir = Path.GetDirectoryName(exe)!;
        var staged = Path.Combine(dir, "PulseOptimizer.update.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseOptimizer");
            string Url(string file) => $"https://github.com/{Repo}/releases/download/v{version}/{file}";

            var sums = await http.GetStringAsync(Url("SHA256SUMS.txt"), ct);
            var expected = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == "PulseOptimizer.exe")?[0]?.ToLowerInvariant();
            if (string.IsNullOrEmpty(expected)) return "The new version doesn't list its checksum yet. Try again in a few minutes.";

            using (var resp = await http.GetAsync(Url("PulseOptimizer.exe"), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0, got = 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(staged);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress?.Report(got / (double)total);
                }
            }
            string actual;
            using (var f = File.OpenRead(staged)) actual = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            if (actual != expected) return "The download didn't match the release's checksum, so it wasn't installed.";

            // Swap after this process exits: keep trying to move the new file over the old one (Pulse can take a few
            // seconds to finish a recording and close), then start it. It used to try once after 2 seconds, and when
            // Pulse was still closing, the old version simply started again.
            var script = Path.Combine(dir, "pulse-update.cmd");
            File.WriteAllText(script, UpdateScript(staged, exe));
            Process.Start(new ProcessStartInfo(Shell.CmdExe, $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir });
            TweakEngine.AddLog("update", $"Updating Pulse to {version}");
            return null;
        }
        catch (OperationCanceledException) { return "Cancelled."; }
        catch (Exception ex) { return "The update couldn't be downloaded: " + ex.Message; }
    }

    public static string CurrentVersion => Ffmpeg.AppVersion;

    /// <summary>The swap script: up to a minute of retries, then start whichever version is in place.</summary>
    internal static string UpdateScript(string staged, string exe) =>
        "@echo off\r\n" +
        "for /l %%i in (1,1,60) do (\r\n" +
        $"  move /y \"{staged}\" \"{exe}\" >nul 2>&1 && goto started\r\n" +
        "  ping 127.0.0.1 -n 2 >nul\r\n" +
        ")\r\n" +
        ":started\r\n" +
        $"start \"\" \"{exe}\" --restarted\r\n";
}
