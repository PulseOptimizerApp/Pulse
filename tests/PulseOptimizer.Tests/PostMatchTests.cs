using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

public class PostMatchTests
{
    /// <summary>13 rounds: Blue wins 0–5, Red (you) wins 6–12.</summary>
    static VMatch Game()
    {
        var m = new VMatch { Id = "pm", Me = "me", Queue = "competitive", Map = "Ascent", FirstAttack = "Red" };
        foreach (var (id, team, agent) in new[] { ("me", "Red", "Jett"), ("m2", "Red", "Sova"), ("m3", "Red", "Omen"), ("e1", "Blue", "Reyna"), ("e2", "Blue", "Sage"), ("e3", "Blue", "Killjoy") })
            m.Players.Add(new VPlayer { Id = id, Team = team, Agent = agent, Score = id == "me" ? 3500 : 2500, Rounds = 13 });
        for (int r = 0; r < 13; r++) m.Rounds.Add(new VRound { N = r, Winner = r < 6 ? "Blue" : "Red", Result = "Elimination" });
        return m;
    }

    static void Kill(VMatch m, int round, int ms, string killer, string victim) =>
        m.Kills.Add(new VKill { Round = round, GameMs = round * 100000 + ms, RoundMs = ms, Killer = killer, Victim = victim });

    [Fact]
    public void Dying_first_in_lost_rounds_is_the_main_point()
    {
        var m = Game();
        for (int r = 0; r < 4; r++) Kill(m, r, 10000, "e1", "me");
        Kill(m, 8, 10000, "e1", "me");             // a first death in a round you won doesn't count
        var p = PostMatch.MainPoint(m, "me");
        Assert.True(p.Problem);
        Assert.Contains("died first", p.Format);
        Assert.Equal(4, p.Args[0]);
        Assert.Equal(DeathTag.FirstDeath, p.Tag);
    }

    [Fact]
    public void A_good_game_names_what_went_well()
    {
        var m = Game();
        foreach (var (v, i) in new[] { "e1", "e2", "e3", "e1", "e2" }.Select((v, i) => (v, i))) Kill(m, 7, 5000 + i * 1000, "me", v);
        var p = PostMatch.MainPoint(m, "me");
        Assert.False(p.Problem);
        Assert.Contains("ace", p.Format);
    }

    [Fact]
    public void Summary_has_the_result_score_and_place()
    {
        var m = Game();
        var s = PostMatch.For(m);
        Assert.Equal(1, s.Result);
        Assert.Equal(7, s.Us);
        Assert.Equal(6, s.Them);
        Assert.Equal(1, s.Place);
        Assert.Equal("Jett", s.Agent);
        Assert.Null(PostMatch.For(m, "nobody"));
    }

    /// <summary>A picture of the popup for a person to look at. Only with PULSE_SHOTS.</summary>
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
                if (Application.Current == null)
                {
                    var app = new Application();
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
                }
                var m = Game();
                for (int r = 0; r < 4; r++) Kill(m, r, 10000, "e1", "me");
                var me = m.Players[0]; me.Kills = 21; me.Deaths = 14; me.Assists = 5;
                var t = typeof(Pulse.Ui.LobbyOverlay);
                const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
                var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
                t.GetMethod("SummaryBody", F)!.Invoke(null, new object[] { body, PostMatch.For(m) });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                foreach (var (text, primary) in new[] { ("Open match", true), ("Both teams", false), ("Close", false) })
                    row.Children.Add((Button)t.GetMethod("Btn", F)!.Invoke(null, new object[] { Application.Current, text, primary }));
                body.Children.Add(row);
                var card = (FrameworkElement)t.GetMethod("Card", F)!.Invoke(null, new object[] { body, 340.0 });
                var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x3C)), Child = card };
                host.Measure(new Size(400, 600)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
                int w = (int)host.ActualWidth, h = (int)host.ActualHeight;
                var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
                rtb.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, "post-match.png"));
                enc.Save(fs);
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }
}
