using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Reaction;
using Xunit;

namespace PulseOptimizer.Tests;

public class PeekTrainerTests
{
    static PeekTrial Trial(bool fromLeft = true, bool enemy = true) =>
        new(1500, fromLeft, enemy, HeadY: 0.36, HeadSize: 40, Speed: 1, SwingPx: 120, ExposeMs: 700);

    [Fact]
    public void Figure_starts_hidden_swings_out_holds_and_hides_again()
    {
        var scene = new PeekScene(1000, 430);
        var t = Trial();
        var start = scene.At(t, 0);
        Assert.True(start.BodyLeft + start.BodyW <= scene.LeftEdge + 0.01, "fully behind the wall at the start");
        var held = scene.At(t, 400);
        Assert.Equal(scene.LeftEdge + 120, held.BodyLeft + held.BodyW, 3);
        Assert.False(held.Gone);
        Assert.True(scene.At(t, PeekScene.EndMs(t) + 1).Gone);
    }

    [Fact]
    public void Clicks_hit_the_head_the_body_or_nothing_and_never_through_the_wall()
    {
        var scene = new PeekScene(1000, 430);
        var t = Trial();
        var p = scene.At(t, 400);
        Assert.Equal(PeekResult.Head, scene.HitTest(t, 400, p.HeadX, p.HeadY));
        Assert.Equal(PeekResult.Body, scene.HitTest(t, 400, p.BodyLeft + p.BodyW - 5, p.BodyTop + 40));
        Assert.Null(scene.HitTest(t, 400, p.HeadX, p.HeadY - 60));
        // Early in the swing the head is still behind the wall: clicking where it is hits the wall.
        var early = scene.At(t, 10);
        Assert.Null(scene.HitTest(t, 10, early.HeadX, early.HeadY));
        // From the right wall it moves left.
        var r = Trial(fromLeft: false);
        var rp = scene.At(r, 400);
        Assert.True(rp.HeadX < scene.RightEdge);
        Assert.Equal(PeekResult.Head, scene.HitTest(r, 400, rp.HeadX, rp.HeadY));
    }

    [Fact]
    public void Staircase_settles_where_you_get_about_80_percent_right()
    {
        // A made-up player: certain to hit at level 1, getting worse as it rises; 80% happens at level 10.
        var rng = new Random(5);
        double P(double d) => d <= 10 ? 1 - 0.2 * (d - 1) / 9 : Math.Max(0, 0.8 - (d - 10) * 0.08);
        var levels = new List<double>();
        var successes = 0; int n = 0;
        double level = 1;
        for (int session = 0; session < 60; session++)
        {
            var t = new PeekTrainer(ReactionMode.Peek, level, seed: session + 1);
            while (!t.Finished)
            {
                var trial = t.Next();
                bool ok = rng.NextDouble() < P(t.Difficulty);
                if (session >= 20) { n++; if (ok) successes++; }
                t.Record(trial, ok ? PeekResult.Head : PeekResult.Missed, 400);
            }
            level = t.Difficulty;
            if (session >= 20) levels.Add(level);
        }
        Assert.InRange(levels.Average(), 8.5, 11.5);
        Assert.InRange((double)successes / n, 0.72, 0.88);
    }

    [Fact]
    public void Benchmark_never_changes_difficulty_and_has_teammates()
    {
        var t = new PeekTrainer(ReactionMode.Benchmark, startDifficulty: 15, seed: 3);
        Assert.Equal(PeekTrainer.BenchmarkDifficulty, t.Difficulty);
        int teammates = 0;
        while (!t.Finished)
        {
            var trial = t.Next();
            if (!trial.Enemy) teammates++;
            t.Record(trial, PeekResult.Missed);
        }
        Assert.Equal(PeekTrainer.BenchmarkDifficulty, t.Difficulty);
        Assert.Equal(PeekTrainer.BenchmarkTrials, t.Done);
        Assert.InRange(teammates, 1, 9);
    }

    [Fact]
    public void Guesses_and_teammate_shots_are_mistakes_and_score_counts_them()
    {
        var t = new PeekTrainer(ReactionMode.FriendOrFoe, seed: 2);
        var a = t.Next();
        Assert.Equal(PeekResult.TooEarly, t.Record(a, PeekResult.Head, 50));
        t.Record(t.Next(), PeekResult.ShotTeammate);
        t.Record(t.Next() with { Enemy = true }, PeekResult.Head, 300);
        t.Record(t.Next() with { Enemy = true }, PeekResult.Body, 400);
        var s = t.Summary();
        Assert.Equal(2, s.Mistakes);
        Assert.Equal(2, s.Trials);
        Assert.Equal(0.5, s.HeadshotShare);
        Assert.Equal(350 * (1 + 2.0 / 4), PeekTrainer.ScoreOf(350, 2, 4));
    }

    [Fact]
    public void Crosshair_drill_alternates_sides_in_pairs_and_wants_headshots()
    {
        var t = new PeekTrainer(ReactionMode.Crosshair, seed: 9);
        var sides = new List<bool>();
        for (int i = 0; i < 6; i++) { var tr = t.Next(); sides.Add(tr.FromLeft); t.Record(tr, PeekResult.Body, 300, 20); }
        Assert.Equal(new[] { true, true, false, false, true, true }, sides);
        Assert.Equal(PeekTrainer.MinDifficulty, t.Difficulty); // body shots don't count as success here
        Assert.Equal(20, t.Summary().OffsetPx);
    }

    [Fact]
    public void Difficulty_makes_peeks_shorter_heads_smaller_and_swings_faster()
    {
        Assert.True(PeekTrainer.ExposeAt(10) < PeekTrainer.ExposeAt(1));
        Assert.True(PeekTrainer.HeadAt(10) < PeekTrainer.HeadAt(1));
        Assert.True(PeekTrainer.SpeedAt(10) > PeekTrainer.SpeedAt(1));
        Assert.InRange(PeekTrainer.ExposeAt(20), 230, 260);
        Assert.Equal(16, PeekTrainer.HeadAt(40));
    }

    [Fact]
    public void Plan_streak_benchmark_due_and_progress()
    {
        var s = new ReactionSettings();
        var today = new DateTime(2026, 11, 9);
        Assert.Equal(4, TrainingPlan.Today(today).Length);
        Assert.Contains(ReactionMode.Peek, TrainingPlan.Today(today));
        Assert.Equal(0, TrainingPlan.Streak(s, today));
        s.PlanDays.AddRange(new[] { today.AddDays(-1), today.AddDays(-2), today.AddDays(-4) });
        Assert.Equal(2, TrainingPlan.Streak(s, today));            // yesterday's streak still counts until today ends
        s.PlanDays.Add(today);
        Assert.Equal(3, TrainingPlan.Streak(s, today));

        Assert.True(TrainingPlan.BenchmarkDue(s, today));
        s.History.Add(new ReactionSession { Mode = ReactionMode.Benchmark, When = today.AddDays(-20), Score = 600, MedianMs = 500, Accuracy = 0.7 });
        s.History.Add(new ReactionSession { Mode = ReactionMode.Benchmark, When = today.AddDays(-2), Score = 480, MedianMs = 430, Accuracy = 0.85 });
        Assert.False(TrainingPlan.BenchmarkDue(s, today));
        var (f, a) = TrainingPlan.Progress(s);
        Assert.Contains("better", f);
        Assert.Equal(20d, a[0]);
        Assert.Equal(70d, a[1]);

        TrainingPlan.SaveLevel(s, ReactionMode.Peek, 9);
        Assert.Equal(7.5, TrainingPlan.StartLevel(s, ReactionMode.Peek));
        Assert.Equal(1, TrainingPlan.StartLevel(s, ReactionMode.Crosshair));
    }

    /// <summary>Pictures of the reaction trainer and the logo's lightning for a person to look at. Only with PULSE_SHOTS.</summary>
    [Fact]
    public void Screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Exception error = null;
        var th = new Thread(() => { try { Shoot(dir); } catch (Exception ex) { error = ex; } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshots failed", error);
    }

    static void Shoot(string dir)
    {
        if (Application.Current == null)
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
        }
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var page = new Pulse.Pages.ReactionPage();
        var type = page.GetType();
        var host = new Border { Width = 1100, Height = 1500, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0F)), Child = page, Padding = new Thickness(28) };
        void Show(DependencyObject d)
        {
            if (d is FrameworkElement fe && Pulse.Ui.Anim.GetReveal(fe)) { fe.BeginAnimation(UIElement.OpacityProperty, null); fe.Opacity = 1; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Show(VisualTreeHelper.GetChild(d, i));
        }
        void Save(string name, FrameworkElement el, int w, int h)
        {
            el.Measure(new Size(w, h)); el.Arrange(new Rect(0, 0, w, h)); el.UpdateLayout();
            Show(el);
            el.UpdateLayout();
            var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name));
            enc.Save(fs);
        }
        Save("reaction-start.png", host, 1100, 1500);

        // Mid-peek: an enemy (then a teammate) swinging out, as it looks in the drill.
        object Get(string f) => type.GetField(f, F)!.GetValue(page);
        void Peek(string name, bool enemy)
        {
            type.GetMethod("Start", F)!.Invoke(page, null);
            var timer = (System.Windows.Threading.DispatcherTimer)Get("_timer");
            timer.Stop();
            var trial = (PeekTrial)Get("_pt");
            type.GetField("_pt", F)!.SetValue(page, trial with { Enemy = enemy });
            type.GetMethod("ShowPeek", F)!.Invoke(page, null);
            var scene = (PeekScene)Get("_scene");
            var pt = (PeekTrial)Get("_pt");
            type.GetMethod("Place", F)!.Invoke(page, new object[] { scene.At(pt, PeekScene.OutMs(pt) + 20) });
            Save(name, host, 1100, 1500);
            type.GetMethod("StopFrames", F)!.Invoke(page, null);
        }
        Peek("reaction-peek.png", true);
        Peek("reaction-teammate.png", false);

        // The lightning: a strip of frames.
        var strip = new WrapPanel { Width = 8 * 90, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0F)) };
        var sparksType = typeof(Pulse.Ui.Sparks);
        for (int i = 0; i < 16; i++)
        {
            var sp = new Pulse.Ui.Sparks { Width = 48, Height = 48, Inset = 7 };
            var g = new Grid { Width = 90, Height = 90 };
            g.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xEF, 0xE8)) });
            g.Children.Add(sp);
            strip.Children.Add(g);
            strip.Measure(new Size(720, 180)); strip.Arrange(new Rect(0, 0, 720, 180));
            if (i % 3 == 0) sparksType.GetMethod("Strike", F)!.Invoke(sp, new object[] { null });
            else sparksType.GetMethod("AddCrackle", F)!.Invoke(sp, new object[] { i / 16.0, 0.2, 6 });
            if (i % 5 == 4) { sparksType.GetField("_charge", F)!.SetValue(sp, 0.3); }
            sp.InvalidateVisual();
        }
        Save("sparks.png", strip, 720, 180);
    }
}
