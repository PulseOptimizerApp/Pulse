using Pulse.Core;
using Xunit;

namespace PulseOptimizer.Tests;

public class SettingsBackupTests
{
    [Fact]
    public void A_backup_can_be_read_back_and_leaves_this_PCs_history_out()
    {
        var file = Path.Combine(Path.GetTempPath(), "pulse-test" + SettingsBackup.Extension);
        SettingsBackup.Export(file);
        Assert.NotNull(SettingsBackup.Peek(file));
        using var zip = System.IO.Compression.ZipFile.OpenRead(file);
        using var r = new StreamReader(zip.GetEntry("pulse-settings.json")!.Open());
        var json = r.ReadToEnd();
        Assert.DoesNotContain("\"Applied\"", json);
        Assert.DoesNotContain("\"GameConfigBackups\"", json);
        Assert.Contains("\"Crosshair\"", json);
        zip.Dispose();
        File.Move(file, file + ".done", true);
    }

    [Fact]
    public void Undo_data_never_travels() =>
        Assert.DoesNotContain(SettingsBackup.Portable, k => k is "Applied" or "AppliedAt" or "Devices" or "GameConfigBackups" or "Log" or "Migrations");
}
