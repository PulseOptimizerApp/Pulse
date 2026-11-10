using Pulse.Core;
using Xunit;

namespace PulseOptimizer.Tests;

public class SetupTests
{
    [Fact]
    public void Settings_saved_before_setup_existed_have_no_setup_state()
    {
        // Existing users' state.json has no "Setup": it reads as null, so they aren't sent through the first-run setup.
        var s = System.Text.Json.JsonSerializer.Deserialize<AppState>("{\"Applied\":{}}");
        Assert.Null(s.Setup);
        var round = System.Text.Json.JsonSerializer.Deserialize<AppState>(System.Text.Json.JsonSerializer.Serialize(new AppState { Setup = new SetupState { Done = true, Games = { "valorant" } } }));
        Assert.True(round.Setup.Done);
        Assert.Equal("valorant", Assert.Single(round.Setup.Games));
    }
}

/// <summary>The first-run setup, every step, for a person to look at. Only with PULSE_SHOTS.</summary>
public class SetupShots
{
    [Fact]
    public void Screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Exception error = null;
        var th = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new System.Windows.Application();
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
                }
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var games = Pulse.Core.Games.LatencyPlan.FindGames();
                // A PC with a few games on it.
                foreach (var g in games.Where(g => g.Game.Id is "valorant" or "brawlhalla" or "cs2" or "fortnite" or "roblox" or "lol").ToList())
                    games[games.IndexOf(g)] = new Pulse.Core.Games.GameItem { Game = g.Game, Installed = true, Icon = g.Icon, CoverImage = Pulse.Core.Games.GameArt.Bundled(g.Game.Name) };
                var findings = new List<Finding>
                {
                    new() { Id = "a", Title = "Your CPU is being held back under load", Detail = "Under full load the CPU ran at 157% of its base speed, and was limited 100% of the time by heat, power or firmware limits.", Severity = Severity.Problem },
                    new() { Id = "b", Title = "Startup programs", Detail = "24 programs are set to launch when you sign in.", Severity = Severity.Check },
                    new() { Id = "c", Title = "Check that Resizable BAR is on", Detail = "Your graphics card supports Resizable BAR.", Severity = Severity.Check },
                    new() { Id = "d", Title = "DISPLAY1", Detail = "240 Hz", Severity = Severity.Ok },
                };
                int n = 0;
                foreach (var step in new[] { "welcome", "games", "check", "tweaks", "you", "done" })
                {
                    var w = new Pulse.Ui.SetupWizard();
                    var t = w.GetType();
                    t.GetField("_games", F)!.SetValue(w, games);
                    var picked = (HashSet<string>)t.GetField("_picked", F)!.GetValue(w);
                    foreach (var id in new[] { "valorant", "brawlhalla", "cs2", "fortnite" }) picked.Add(id);
                    if (step is "check" or "done") t.GetField("_findings", F)!.SetValue(w, findings);
                    t.GetMethod("Steps", F)!.Invoke(w, null);
                    var steps = (List<string>)t.GetField("_steps", F)!.GetValue(w);
                    t.GetMethod("Go", F)!.Invoke(w, new object[] { steps.IndexOf(step) });
                    var host = new System.Windows.Controls.Border { Width = 1200, Height = 740, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0C, 0x0F)), Child = w };
                    host.Measure(new System.Windows.Size(1200, 740)); host.Arrange(new System.Windows.Rect(0, 0, 1200, 740)); host.UpdateLayout();
                    // Let the step's fade-in finish.
                    w.FindName("x");
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 740, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    foreach (var e in new System.Windows.FrameworkElement[] { w }) e.Opacity = 1;
                    var body = (System.Windows.Controls.ContentControl)t.GetField("_body", F)!.GetValue(w);
                    body.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
                    body.Opacity = 1;
                    body.RenderTransform = null;
                    host.UpdateLayout();
                    rtb.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, $"setup-{++n}-{step}.png"));
                    enc.Save(fs);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }
}
