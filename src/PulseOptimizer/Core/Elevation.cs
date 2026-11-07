namespace Pulse.Core;

/// <summary>
/// Optional: open Pulse with admin rights without Windows' admin prompt every time. Pulse needs admin rights for
/// most of what it does (system tweaks, services, power plans, Roblox and NVIDIA settings), so Windows asks on
/// each launch. With this on, Pulse adds a Task Scheduler entry that starts it "with highest privileges"; a normal
/// launch starts that task instead of asking. Windows only allows creating it from an already-elevated Pulse, and
/// it only ever starts Pulse itself, with no arguments.
/// </summary>
public static class Elevation
{
    const string TaskName = "Pulse Optimizer (no admin prompt)";
    static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var r = Shell.Run(Schtasks, $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONCE /SD 01/01/2030 /ST 00:00 /RL HIGHEST", 30_000);
        return r.Code == 0;
    }

    public static void Disable() => Shell.Run(Schtasks, $"/Delete /F /TN \"{TaskName}\"", 30_000);

    /// <summary>Is the task there, pointing at this copy of Pulse?</summary>
    public static bool IsOn()
    {
        var r = Shell.Run(Schtasks, $"/Query /TN \"{TaskName}\" /XML", 15_000);
        return r.Code == 0 && Environment.ProcessPath is string exe && r.Output.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>From a normal (not elevated) launch: start Pulse through the task. False if it isn't set up.</summary>
    public static bool TryStartElevated()
    {
        try
        {
            if (!IsOn()) return false;
            return Shell.Run(Schtasks, $"/Run /TN \"{TaskName}\"", 15_000).Code == 0;
        }
        catch { return false; }
    }
}
