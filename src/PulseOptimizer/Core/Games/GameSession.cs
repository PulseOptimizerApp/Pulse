using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Pulse.Core.Measurement;

namespace Pulse.Core.Games;

/// <summary>What Pulse does while any game is running (Games page switches).</summary>
public class GameSessionSettings
{
    /// <summary>High performance power plan while a game runs, put back when it closes.</summary>
    public bool GameMode { get; set; }
    /// <summary>Small FPS counter over the game (borderless and windowed games).</summary>
    public bool Overlay { get; set; }
    /// <summary>"tl", "tr", "bl", "br".</summary>
    public string OverlayCorner { get; set; } = "tl";
    /// <summary>What the overlay shows besides FPS.</summary>
    public bool OverlayGraph { get; set; } = true;
    public bool OverlayCpu { get; set; } = true;
    public bool OverlayGpu { get; set; } = true;
    public bool OverlayGpuTemp { get; set; } = true;
    public bool OverlayVram { get; set; }
    public bool OverlayRam { get; set; }
    public bool OverlayClock { get; set; }
    /// <summary>GPU core clock and power draw (NVIDIA cards, from the GPU tuning page's readings).</summary>
    public bool OverlayGpuClock { get; set; }
    public bool OverlayGpuPower { get; set; }
    /// <summary>"row" (one line across) or "stack" (one line per reading).</summary>
    public string OverlayLayout { get; set; } = "stack";
    /// <summary>0.8–1.6.</summary>
    public double OverlayScale { get; set; } = 1;
    /// <summary>Background opacity, 0–1.</summary>
    public double OverlayBackground { get; set; } = 0.7;
    /// <summary>Ctrl+Shift+O shows or hides it while you play.</summary>
    public bool OverlayHotkey { get; set; } = true;
}

/// <summary>Switches Windows to High performance while a game runs and back afterwards. Survives a Pulse restart.</summary>
public static class GamePowerPlan
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    static string FilePath => Path.Combine(TweakEngine.DataDir, "game-power.txt");
    static string Powercfg => Path.Combine(Environment.SystemDirectory, "powercfg.exe");

    public static bool Active => File.Exists(FilePath) && File.ReadAllText(FilePath).Trim().Length > 0;

    internal static string Guid(string text)
    {
        var m = Regex.Match(text ?? "", @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static bool Start()
    {
        try
        {
            if (Active) return true;
            var current = Guid(Shell.Run(Powercfg, "/getactivescheme", 10_000).Output);
            if (current == null || current == HighPerformance) return false;
            if (!Shell.Run(Powercfg, $"/setactive {HighPerformance}", 10_000).Ok) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, current);
            TweakEngine.AddLog("games", "Game mode: switched to the High performance power plan");
            return true;
        }
        catch { return false; }
    }

    public static void End()
    {
        try
        {
            if (!Active) return;
            var plan = Guid(File.ReadAllText(FilePath));
            if (plan != null) Shell.Run(Powercfg, $"/setactive {plan}", 10_000);
            File.WriteAllText(FilePath, "");
            TweakEngine.AddLog("games", "Game mode: power plan put back");
        }
        catch { }
    }
}

/// <summary>
/// Live frame times from PresentMon (shipped inside Pulse) for the FPS overlay. Reads its CSV output as it's written;
/// nothing touches the game itself.
/// </summary>
public sealed class LiveFrames : IDisposable
{
    readonly Process _p;
    readonly object _gate = new();
    readonly Queue<(double At, double Ms)> _frames = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    public string ProcessName { get; }

    LiveFrames(Process p, string processName) { _p = p; ProcessName = processName; }

    public static LiveFrames Start(string processName)
    {
        if (!PresentMonCapture.Available) return null;
        var exe = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
        var psi = new ProcessStartInfo(PresentMonCapture.ExePath,
            $"--process_name \"{exe}\" --output_stdout --no_console_stats --session_name PulseOverlay --stop_existing_session")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var p = Process.Start(psi);
        if (p == null) return null;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var lf = new LiveFrames(p, processName);
        _ = Task.Run(lf.Read);
        _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() != null) { } } catch { } });
        return lf;
    }

    async Task Read()
    {
        try
        {
            int col = -1;
            string line;
            while ((line = await _p.StandardOutput.ReadLineAsync()) != null)
            {
                var c = line.Split(',');
                if (col < 0)
                {
                    col = Array.FindIndex(c, h => h.Trim() is "MsBetweenPresents" or "msBetweenPresents" or "FrameTime");
                    continue;
                }
                if (col < c.Length && double.TryParse(c[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 1000)
                    Add(_clock.Elapsed.TotalSeconds, ms);
            }
        }
        catch { }
    }

    internal void Add(double at, double ms)
    {
        lock (_gate)
        {
            _frames.Enqueue((at, ms));
            while (_frames.Count > 0 && at - _frames.Peek().At > 10) _frames.Dequeue();
        }
    }

    /// <summary>Frame times (ms) of the last <paramref name="seconds"/> seconds, oldest first, for the frame time graph.</summary>
    public double[] Recent(double seconds)
    {
        lock (_gate)
        {
            double now = _clock.Elapsed.TotalSeconds;
            return _frames.Where(f => now - f.At <= seconds).Select(f => f.Ms).ToArray();
        }
    }

    /// <summary>FPS over the last second, 1% low over the last 10 seconds, and the last frame time.</summary>
    public (double Fps, double Low1, double FrameMs) Now()
    {
        lock (_gate) return Compute(_frames.ToList(), _clock.Elapsed.TotalSeconds);
    }

    internal static (double Fps, double Low1, double FrameMs) Compute(List<(double At, double Ms)> frames, double now)
    {
        if (frames.Count == 0) return (0, 0, 0);
        var lastSecond = frames.Where(f => now - f.At <= 1).Select(f => f.Ms).ToList();
        double fps = lastSecond.Count > 0 ? 1000 / lastSecond.Average() : 0;
        var sorted = frames.Select(f => f.Ms).OrderByDescending(x => x).ToList();
        int n = Math.Max(1, sorted.Count / 100);
        double low = 1000 / sorted.Take(n).Average();
        return (fps, low, frames[^1].Ms);
    }

    public bool Running => !_p.HasExited;

    public void Dispose()
    {
        try { if (!_p.HasExited) _p.Kill(true); } catch { }
        _p.Dispose();
    }
}
