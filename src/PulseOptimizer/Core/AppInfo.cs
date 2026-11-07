namespace Pulse.Core;

/// <summary>Where Pulse lives online. Updates and the Clips FFmpeg download come only from these GitHub releases.</summary>
public static class AppInfo
{
    /// <summary>Pulse's home: updates and downloads come from here first.</summary>
    public const string Repo = "PulseOptimizerApp/PulseOptimizer";

    /// <summary>Pulse's first home (on hold). Still checked second, so nothing breaks if it comes back.</summary>
    public const string OldRepo = "PulseOptimizer-app/PulseOptimizer";

    /// <summary>Every place Pulse may download its own releases from, newest home first.</summary>
    public static readonly string[] Repos = { Repo, OldRepo };

    public const string Website = "https://pulseoptimizerapp.github.io/";
}
