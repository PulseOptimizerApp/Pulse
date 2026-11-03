using Pulse.Core.Declutter;
using Xunit;

namespace PulseOptimizer.Tests;

public class DeclutterTests
{
    // Packages Windows needs to work properly; no rule may ever match them.
    static readonly string[] Essential =
    {
        "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.WindowsCalculator",
        "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal", "Microsoft.SecHealthUI",
        "Microsoft.VCLibs.140.00", "Microsoft.UI.Xaml.2.8", "Microsoft.NET.Native.Runtime.2.2", "Microsoft.WindowsCamera",
        "Microsoft.ScreenSketch", "Microsoft.Paint", "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI",
        "Microsoft.HEIFImageExtension", "Microsoft.WebMediaExtensions", "Microsoft.MicrosoftEdge.Stable", "NVIDIACorp.NVIDIAControlPanel",
        "AdvancedMicroDevicesInc-2.AMDRadeonSoftware", "RealtekSemiconductorCorp.RealtekAudioControl",
    };

    [Fact]
    public void EssentialAppsAreNeverOfferedForRemoval()
    {
        foreach (var pkg in Essential)
            Assert.DoesNotContain(DeclutterEngine.StoreBloat, b => pkg.Contains(b.match, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XboxAppsNeededForGamesAreMarkedKeep()
    {
        foreach (var pkg in new[] { "Microsoft.GamingApp", "Microsoft.XboxGamingOverlay" })
        {
            var rule = DeclutterEngine.StoreBloat.First(b => pkg.Contains(b.match, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(AppAdvice.Keep, rule.advice);
        }
    }

    [Fact]
    public void EveryRuleExplainsItself()
    {
        Assert.All(DeclutterEngine.StoreBloat.Concat(DeclutterEngine.DesktopBloat), r => Assert.False(string.IsNullOrWhiteSpace(r.why)));
    }

    [Theory]
    [InlineData("Microsoft.BingNews", "Microsoft News")]
    [InlineData("SpotifyAB.SpotifyMusic", "Spotify Music")]
    [InlineData("king.com.CandyCrushSaga", "Candy Crush")]
    public void StoreNamesAreReadable(string package, string expected) =>
        Assert.Equal(expected, DeclutterEngine.FriendlyStoreName(package));

    [Fact]
    public void KeepAppsCannotBeRemoved()
    {
        var a = new AppEntry { Name = "Xbox", Advice = AppAdvice.Keep };
        Assert.False(a.CanRemove);
        var b = new AppEntry { Name = "Tips", Advice = AppAdvice.Safe, RemoveKey = "Microsoft.Getstarted_1.0" };
        Assert.True(b.CanRemove);
        b.Removed = true;
        Assert.False(b.CanRemove);
    }

    [Fact]
    public void AppsWithoutAnUninstallerCannotBeSelected()
    {
        var epic = new AppEntry { Name = "Fortnite", Kind = "Game" };
        epic.Selected = true;
        Assert.False(epic.Selected);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{12345678-ABCD-1234-ABCD-1234567890AB}", "MsiExec.exe /X{12345678-ABCD-1234-ABCD-1234567890AB} /passive /norestart")]
    [InlineData("MsiExec.exe /X{12345678-ABCD-1234-ABCD-1234567890AB}", "MsiExec.exe /X{12345678-ABCD-1234-ABCD-1234567890AB} /passive /norestart")]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\"", "\"C:\\Program Files\\App\\uninst.exe\"")]
    public void MsiUninstallsRunWithoutQuestions(string input, string expected) =>
        Assert.Equal(expected, DeclutterEngine.PassiveMsi(input));

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", true)]
    [InlineData("NVIDIA Graphics Driver 581.57", true)]
    [InlineData("Riot Vanguard", true)]
    [InlineData("Discord", false)]
    [InlineData("Counter-Strike 2", false)]
    public void SharedRuntimesAndDriversAreNeverListed(string name, bool hidden) =>
        Assert.Equal(hidden, DeclutterEngine.IsRuntime(name));

    [Fact]
    public void ReviewedFilesGoToTheRecycleBinNotPermanentDelete()
    {
        var src = File.ReadAllText(Path.Combine(Repo.Root, "src", "PulseOptimizer", "Core", "Declutter", "Declutter.cs"));
        Assert.Contains("FOF_ALLOWUNDO", src);
        Assert.DoesNotContain("/StartComponentCleanup /ResetBase", src);
    }
}
