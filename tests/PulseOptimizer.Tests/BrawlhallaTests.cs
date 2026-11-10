using Pulse.Core.Games.Brawlhalla;
using Xunit;

namespace PulseOptimizer.Tests;

public class BrawlhallaTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "Tin 0")]
    [InlineData(720, "Tin 1")]
    [InlineData(909, "Tin 5")]
    [InlineData(910, "Bronze 1")]
    [InlineData(1129, "Bronze 5")]
    [InlineData(1130, "Silver 1")]
    [InlineData(1390, "Gold 1")]
    [InlineData(1679, "Gold 5")]
    [InlineData(1680, "Platinum 1")]
    [InlineData(1999, "Platinum 5")]
    [InlineData(2000, "Diamond")]
    [InlineData(2800, "Diamond")]
    public void Ratings_map_to_the_games_tiers(int rating, string tier) => Assert.Equal(tier, Brawlhalla.TierOf(rating));
}

/// <summary>The Brawlhalla page with a real top player, for a person to look at. Only with PULSE_SHOTS (needs internet).</summary>
public class BrawlhallaPageShots
{
    [Fact]
    public void Screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var legends = Brawlhalla.Legends().GetAwaiter().GetResult();
        var board = Brawlhalla.Rankings("1v1", "ALL", 1, null, 10).GetAwaiter().GetResult();
        var profile = Brawlhalla.Profile(board.Rows[0].Id).GetAwaiter().GetResult();
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
                void Shot(string file, Action<Pulse.Pages.BrawlhallaPage> set)
                {
                    var page = new Pulse.Pages.BrawlhallaPage();
                    var t = page.GetType();
                    t.GetField("_legends", F)!.SetValue(page, legends);
                    set(page);
                    t.GetMethod("Build", F)!.Invoke(page, null);
                    var host = new System.Windows.Controls.Border { Width = 1200, Height = 2600, Padding = new System.Windows.Thickness(28), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0C, 0x0F)), Child = page };
                    host.Measure(new System.Windows.Size(1200, 2600)); host.Arrange(new System.Windows.Rect(0, 0, 1200, 2600)); host.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 2600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }
                void Tab(Pulse.Pages.BrawlhallaPage p, string tab)
                {
                    p.GetType().GetField("_profile", F)!.SetValue(p, profile);
                    p.GetType().GetField("_tab", F)!.SetValue(p, tab);
                }
                Shot("brawlhalla-profile.png", p => Tab(p, "overview"));
                Shot("brawlhalla-legends.png", p =>
                {
                    Tab(p, "legends");
                    var top = profile.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).First();
                    p.GetType().GetField("_openLegend", F)!.SetValue(p, top.LegendId);
                });
                Shot("brawlhalla-weapons.png", p => Tab(p, "weapons"));
                Shot("brawlhalla-ranked.png", p => Tab(p, "ranked"));
                Shot("brawlhalla-board.png", p => p.GetType().GetField("_board", F)!.SetValue(p, board));
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }
}
