using System.Text.RegularExpressions;
using Pulse.Core.Measurement;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>Pulse waits next to games all day, so the checks it repeats must stay cheap.</summary>
public class IdleCostTests
{
    [Fact]
    public void The_process_list_is_read_once_and_shared()
    {
        var a = GameDetector.Processes();
        var b = GameDetector.Processes();
        Assert.Same(a, b); // second call within 1.5 s reuses the first read
        Assert.Contains(a, p => p.Pid == Environment.ProcessId);
        Assert.True(GameDetector.IsRunning(System.Diagnostics.Process.GetCurrentProcess().ProcessName));
    }

    [Fact]
    public void Background_checks_use_the_shared_process_list()
    {
        // Services that run on a timer must not read the whole process list themselves.
        foreach (var file in new[] { "Core/Games/Live/LeagueLive.cs", "Core/Games/Live/ValorantMatches.cs", "Core/Input/Macros.cs",
                                     "Ui/MatchService.cs", "Ui/ClipsService.cs", "Ui/GameSessionService.cs", "Ui/MacroService.cs" })
        {
            var src = File.ReadAllText(Path.Combine(Repo.Root, "src", "PulseOptimizer", file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.False(Regex.IsMatch(src, @"Process\.GetProcesses(ByName)?\("), file + " reads the process list on its own; use GameDetector.Processes()");
        }
    }
}
