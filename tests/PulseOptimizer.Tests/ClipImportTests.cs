using Pulse.Core.Clips;
using Xunit;

namespace PulseOptimizer.Tests;

public class ClipImportTests
{
    [Fact]
    public void Game_comes_from_the_folder_or_the_file_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulse-import-test", "Clips");
        Directory.CreateDirectory(Path.Combine(root, "VALORANT"));
        var inGame = Path.Combine(root, "VALORANT", "MedalTVValorant20261104.mp4");
        var atRoot = Path.Combine(root, "Counter-Strike 2 2026-11-04 20-11-03.mp4");
        File.WriteAllBytes(inGame, new byte[10]); File.WriteAllBytes(atRoot, new byte[10]);
        Assert.Equal("VALORANT", ClipImport.GameFor(new FileInfo(inGame), root, "Medal"));
        Assert.Equal("Counter-Strike 2", ClipImport.GameFor(new FileInfo(atRoot), root, "Medal"));
    }

    [Fact]
    public void Imported_names_match_Pulses_own_and_group_by_game()
    {
        var c = new ImportCandidate(@"C:\Medal\Clips\VALORANT\a.mp4", "VALORANT", new DateTime(2026, 11, 4, 1, 30, 22), 1);
        var name = Path.GetFileNameWithoutExtension(ClipImport.TargetName(Path.GetTempPath(), c));
        Assert.Equal("VALORANT 2026-11-04 01-30-22", name);
        Assert.Equal("VALORANT", new ClipFile { Path = name + ".mp4" }.Game);
    }
}
