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
    /// <summary>The repo the newest version was found in (installs come from the same place).</summary>
    static string _foundIn = AppInfo.Repo;

    /// <summary>
    /// The version tag of the newest release ("0.3.1"), or null if it couldn't be checked. Looks at every home Pulse
    /// has had (<see cref="AppInfo.Repos"/>) and takes the newest, so a move never strands anyone.
    /// </summary>
    public static async Task<string> LatestVersion(CancellationToken ct = default)
    {
        string best = null;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseOptimizer");
        foreach (var repo in AppInfo.Repos)
        {
            try
            {
                // GitHub answers /releases/latest with a redirect to /releases/tag/vX.Y.Z.
                using var r = await http.GetAsync($"https://github.com/{repo}/releases/latest", ct);
                var v = TagFromUrl(r.Headers.Location?.ToString());
                // A new release takes a few minutes to build its files: only offer it once they're attached.
                if (v != null && !await Ready(http, repo, v, ct)) v = null;
                if (v != null && (best == null || Version.Parse(v) > Version.Parse(best)))
                {
                    best = v;
                    _foundIn = repo;
                }
            }
            catch { }
        }
        return best;
    }

    /// <summary>True once the release has its checksums (and so its exe) attached.</summary>
    static async Task<bool> Ready(HttpClient http, string repo, string version, CancellationToken ct)
    {
        try
        {
            using var r = await http.GetAsync($"https://github.com/{repo}/releases/download/v{version}/SHA256SUMS.txt", HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)r.StatusCode is >= 200 and < 400; // GitHub answers with a redirect to the file
        }
        catch { return false; }
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
    public static async Task<string> Install(string version, IProgress<(long Got, long Total)> progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return "Couldn't find Pulse's own file.";
        var dir = Path.GetDirectoryName(exe)!;
        var staged = Path.Combine(dir, "PulseOptimizer.update.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseOptimizer");
            var repo = _foundIn;
            string Url(string file) => $"https://github.com/{repo}/releases/download/v{version}/{file}";

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
                    progress?.Report((got, total));
                }
            }
            string actual;
            using (var f = File.OpenRead(staged)) actual = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            if (actual != expected) return "The download didn't match the release's checksum, so it wasn't installed.";

            // Swap after this process exits: keep trying to move the new file over the old one (Pulse can take a few
            // seconds to finish a recording and close), then start it. It used to try once after 2 seconds, and when
            // Pulse was still closing, the old version simply started again.
            // Remember the verified download: if the swap still doesn't happen (something held the file), the next start
            // finishes it instead of asking the user to download again.
            File.WriteAllText(MarkerFor(dir), $"{version}\n{expected}\n0");
            StartSwap(staged, exe);
            TweakEngine.AddLog("update", $"Updating Pulse to {version}");
            return null;
        }
        catch (OperationCanceledException) { return "Cancelled."; }
        catch (Exception ex) { return "The update couldn't be downloaded: " + ex.Message; }
    }

    public static string CurrentVersion => Ffmpeg.AppVersion;

    static string MarkerFor(string dir) => Path.Combine(dir, "PulseOptimizer.update.txt");

    /// <summary>Set at start-up when this run is the version an update just installed.</summary>
    public static string JustUpdatedTo { get; private set; }

    /// <summary>
    /// At start-up: if a verified update is waiting next to Pulse but the swap didn't happen, starts the swap again
    /// (at most twice) and returns true (Pulse should exit). If this run is the updated version, notes it for a
    /// "you're up to date" message. Returns false normally.
    /// </summary>
    public static bool ResumeSwap()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return false;
            var dir = Path.GetDirectoryName(exe)!;
            var marker = MarkerFor(dir);
            if (!File.Exists(marker)) return false;
            var parts = File.ReadAllText(marker).Split('\n', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || parts[0].Length == 0) return false;
            if (!IsNewer(parts[0], CurrentVersion))
            {
                if (parts[0] == CurrentVersion) JustUpdatedTo = parts[0];
                File.WriteAllText(marker, ""); // done
                return false;
            }
            var staged = Path.Combine(dir, "PulseOptimizer.update.exe");
            int tries = int.TryParse(parts[2], out var t) ? t : 0;
            if (tries >= 2 || !File.Exists(staged)) { File.WriteAllText(marker, ""); return false; }
            string actual;
            using (var f = File.OpenRead(staged)) actual = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            if (actual != parts[1]) { File.WriteAllText(marker, ""); return false; }
            File.WriteAllText(marker, $"{parts[0]}\n{parts[1]}\n{tries + 1}");
            StartSwap(staged, exe);
            TweakEngine.AddLog("update", $"Finishing the update to {parts[0]}");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts the downloaded new version and lets it put itself in place: it waits until this Pulse has really
    /// exited (its file is locked until then), copies itself over it, and starts it. No command script in between
    /// (the old way could fail without a word, and the old version simply opened again).
    /// </summary>
    static void StartSwap(string staged, string exe)
    {
        var psi = new ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("--finish-update");
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    static string SwapLog => Path.Combine(TweakEngine.DataDir, "update.log");

    static void Note(string text)
    {
        try { Directory.CreateDirectory(TweakEngine.DataDir); File.AppendAllText(SwapLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\r\n"); } catch { }
    }

    /// <summary>
    /// Runs in the new version (started with --finish-update "target" oldPid): waits for the old Pulse to exit,
    /// copies this file over it (retrying while anything still holds it), then starts it. Each step is written to
    /// update.log in Pulse's data folder.
    /// </summary>
    public static void FinishUpdate(string target, int oldPid)
    {
        var self = Environment.ProcessPath!;
        Note($"Finishing update {CurrentVersion}: {self} -> {target} (waiting for {oldPid})");
        try { using var old = Process.GetProcessById(oldPid); if (!old.WaitForExit(90_000)) Note("The old Pulse didn't exit within 90 s"); }
        catch { /* already gone */ }
        bool copied = false;
        Exception last = null;
        for (int i = 0; i < 120 && !copied; i++)
        {
            try { File.Copy(self, target, true); copied = true; }
            catch (Exception ex) { last = ex; Thread.Sleep(500); }
        }
        Note(copied ? "Copied" : $"Couldn't replace the file: {last?.Message}");
        try { Process.Start(new ProcessStartInfo(copied ? target : self, "--restarted") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! }); }
        catch (Exception ex) { Note("Couldn't start Pulse: " + ex.Message); }
    }

    /// <summary>The download left next to Pulse after an update is moved out of the way (to the temp folder).</summary>
    public static void TidyAfterUpdate()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var staged = Path.Combine(Path.GetDirectoryName(exe)!, "PulseOptimizer.update.exe");
            if (string.Equals(staged, exe, StringComparison.OrdinalIgnoreCase) || !File.Exists(staged)) return;
            var marker = MarkerFor(Path.GetDirectoryName(exe)!);
            if (File.Exists(marker) && File.ReadAllText(marker).Trim().Length > 0) return; // still waiting to be swapped in
            File.Move(staged, Path.Combine(Path.GetTempPath(), $"PulseOptimizer.update-{Guid.NewGuid():N}.exe"), true);
        }
        catch { }
    }
}
