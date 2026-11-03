using System.Diagnostics;
using Pulse.Core.Measurement;
using Pulse.Ui;

namespace Pulse.Core.Fresh;

/// <summary>What "your PC right now" looks like, so later checks can show what drifted.</summary>
public class Snapshot
{
    public DateTime Date { get; set; }
    public double? BootSeconds { get; set; }
    public List<string> StartupApps { get; set; } = new();
    public int Processes { get; set; }
    public double IdleRamGb { get; set; }
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
    public long ClutterBytes { get; set; }
}

public enum DriftKind { Good, Changed, Problem }

public class Drift : Observable
{
    public string Title { get; init; }
    public string Detail { get; init; }
    public DriftKind Kind { get; init; }
    public string ActionKey { get; init; } = "";
    public string ActionText { get; init; } = "";
    public bool HasAction => ActionKey.Length > 0;
    public System.Windows.Media.Brush KindBrush => Kind switch { DriftKind.Problem => Palette.Bad, DriftKind.Changed => Palette.Warn, _ => Palette.Good };
}

/// <summary>
/// Keep it fresh. PCs rarely slow down because of one thing; they drift: apps add themselves to startup,
/// background processes pile up, temp files and installers fill the drive. Pulse saves a snapshot when the PC is in
/// good shape, then compares against it, cleans the safe things on a schedule, and tells you what changed.
/// </summary>
public static class Freshness
{
    const string TaskName = "Pulse Optimizer weekly tune-up";

    /// <summary>Measures the PC now. Takes 10–40 seconds; call off the UI thread.</summary>
    public static Snapshot Take()
    {
        var snap = new Snapshot { Date = DateTime.Now };
        try { snap.BootSeconds = BootTimeMeter.LastBoots(3).Select(b => (double?)b.Seconds).FirstOrDefault(); } catch { }
        try { snap.StartupApps = StartupManager.Load().Where(i => i.Enabled).Select(i => i.Name).Distinct().OrderBy(n => n).ToList(); } catch { }
        try { snap.Processes = Process.GetProcesses().Length; } catch { }
        var m = Native.Memory();
        snap.IdleRamGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        var d = SystemInfo.SystemDrive;
        snap.FreeBytes = d.AvailableFreeSpace; snap.TotalBytes = d.TotalSize;
        foreach (var c in Cleaner.Categories.Where(c => c.Id is "user-temp" or "win-temp" or "wu" or "crash"))
        {
            try { Cleaner.Scan(c); snap.ClutterBytes += Math.Max(c.Size, 0); } catch { }
        }
        return snap;
    }

    /// <summary>Compares now against the baseline. Pure, so it's unit-tested.</summary>
    public static List<Drift> Compare(Snapshot baseline, Snapshot now)
    {
        var list = new List<Drift>();
        if (baseline == null || now == null) return list;

        var added = now.StartupApps.Except(baseline.StartupApps, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0)
            list.Add(new Drift
            {
                Kind = added.Count >= 3 ? DriftKind.Problem : DriftKind.Changed,
                Title = $"{added.Count} new app{(added.Count == 1 ? "" : "s")} start{(added.Count == 1 ? "s" : "")} with Windows",
                Detail = string.Join(", ", added.Take(6)) + (added.Count > 6 ? $" and {added.Count - 6} more" : "") + ". Each one slows startup and runs in the background.",
                ActionKey = "page:startup", ActionText = "Review startup apps",
            });
        else list.Add(new Drift { Kind = DriftKind.Good, Title = "No new startup apps", Detail = $"{now.StartupApps.Count} apps start with Windows, same as your baseline." });

        if (baseline.BootSeconds is double b0 && now.BootSeconds is double b1 && b0 > 0)
        {
            double pct = (b1 - b0) / b0 * 100;
            list.Add(pct >= 25
                ? new Drift { Kind = pct >= 60 ? DriftKind.Problem : DriftKind.Changed, Title = $"Boot time is {pct:0}% slower", Detail = $"{b1:0} s now, {b0:0} s at your baseline (as Windows itself measures it). New startup apps and drivers are the usual cause.", ActionKey = "page:startup", ActionText = "Review startup apps" }
                : new Drift { Kind = DriftKind.Good, Title = "Boot time is steady", Detail = $"{b1:0} s, against {b0:0} s at your baseline." });
        }

        if (baseline.Processes > 0)
        {
            int extra = now.Processes - baseline.Processes;
            if (extra >= Math.Max(25, baseline.Processes * 0.2))
                list.Add(new Drift { Kind = DriftKind.Changed, Title = $"{extra} more background processes", Detail = $"{now.Processes} running now against {baseline.Processes} at your baseline. Usually tray apps, updaters and launchers.", ActionKey = "page:startup", ActionText = "See what starts" });
        }

        double freePct = now.TotalBytes > 0 ? (double)now.FreeBytes / now.TotalBytes : 1;
        long lost = baseline.FreeBytes - now.FreeBytes;
        if (freePct < 0.12)
            list.Add(new Drift { Kind = DriftKind.Problem, Title = "Your drive is nearly full", Detail = $"{Format.Bytes(now.FreeBytes)} free. A nearly full drive slows Windows, updates and game patches.", ActionKey = "page:cleanup", ActionText = "Open Debloat" });
        else if (lost > 15L * 1024 * 1024 * 1024)
            list.Add(new Drift { Kind = DriftKind.Changed, Title = $"{Format.Bytes(lost)} less free space", Detail = $"{Format.Bytes(now.FreeBytes)} free now. Debloat shows where it went.", ActionKey = "page:cleanup", ActionText = "Open Debloat" });
        else list.Add(new Drift { Kind = DriftKind.Good, Title = "Plenty of free space", Detail = $"{Format.Bytes(now.FreeBytes)} free." });

        if (now.ClutterBytes > 1L * 1024 * 1024 * 1024)
            list.Add(new Drift { Kind = DriftKind.Changed, Title = $"{Format.Bytes(now.ClutterBytes)} of temporary files", Detail = "Leftovers from apps and Windows Update. The weekly tune-up clears these for you.", ActionKey = "page:cleanup", ActionText = "Clean now" });

        return list.OrderBy(d => d.Kind switch { DriftKind.Problem => 0, DriftKind.Changed => 1, _ => 2 }).ToList();
    }

    /// <summary>The safe part of a tune-up: temp files, Windows Update leftovers, crash dumps (all older than 2 hours where it matters).</summary>
    public static long CleanSafe()
    {
        long freed = 0;
        foreach (var c in Cleaner.Categories.Where(c => c.Id is "user-temp" or "win-temp" or "wu" or "crash"))
        {
            try { freed += Cleaner.Clean(c); } catch { }
        }
        return freed;
    }

    /// <summary>What the weekly scheduled run does: clean the safe things, check for drift, leave a short report.</summary>
    public static void RunScheduled()
    {
        var freed = CleanSafe();
        var now = Take();
        var st = TweakEngine.State;
        st.FreshHistory.Insert(0, now);
        if (st.FreshHistory.Count > 12) st.FreshHistory.RemoveRange(12, st.FreshHistory.Count - 12);
        var drifts = Compare(st.Baseline, now).Where(d => d.Kind != DriftKind.Good).ToList();
        st.PendingReport = $"Weekly tune-up: cleared {Format.Bytes(freed)} of temporary files."
            + (drifts.Count == 0 ? " Nothing else changed." : " " + string.Join(" ", drifts.Select(d => d.Title + ".")));
        TweakEngine.AddLog("fresh", st.PendingReport);
        TweakEngine.Save();
    }

    // ───────── Weekly task (Windows Task Scheduler) ─────────

    /// <summary>Creates or removes the weekly task. It runs Pulse itself with --tune-up, quietly, as you.</summary>
    public static (bool ok, string message) SetWeekly(bool on)
    {
        ShellResult r;
        if (on)
        {
            var exe = Environment.ProcessPath ?? "";
            var day = DateTime.Now.DayOfWeek.ToString()[..3].ToUpperInvariant();
            r = Shell.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tune-up\" /SC WEEKLY /D {day} /ST 12:00 /RL HIGHEST");
        }
        else
        {
            r = Shell.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /F /TN \"{TaskName}\"");
            if (!r.Ok && (r.Output + r.Error).Contains("cannot find", StringComparison.OrdinalIgnoreCase)) r = new ShellResult(0, "", "");
        }
        if (r.Ok)
        {
            TweakEngine.State.WeeklyTuneUp = on;
            TweakEngine.AddLog("fresh", on ? "Turned on the weekly tune-up" : "Turned off the weekly tune-up");
            TweakEngine.Save();
        }
        return (r.Ok, r.Ok ? "" : CmdAction.FirstLine(r.Error + "\n" + r.Output) ?? "Task Scheduler refused the change.");
    }

    /// <summary>Re-points the weekly task at this copy of Pulse if the exe was moved since it was set up.</summary>
    public static void RefreshTaskPath()
    {
        if (!TweakEngine.State.WeeklyTuneUp) return;
        var r = Shell.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Query /TN \"{TaskName}\" /V /FO LIST");
        var exe = Environment.ProcessPath ?? "";
        if (!r.Ok || !r.Output.Contains(exe, StringComparison.OrdinalIgnoreCase)) SetWeekly(true);
    }
}
