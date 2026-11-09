using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Games.Valorant;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>
/// How fast the heavy pages build and draw, and whether opening them again and again grows memory. Only with
/// PULSE_PERF=1 (the "Performance check" workflow, run on every pull request), on Windows, with 40 made-up matches.
/// Writes a table to the job summary and fails when a page is slow enough to feel laggy or memory keeps growing.
/// </summary>
public class PerfTests
{
    readonly ITestOutputHelper _out;
    public PerfTests(ITestOutputHelper o) => _out = o;

    const double SlowMs = 400;          // a page that takes longer than this to appear feels laggy
    const double LeakMb = 40;           // managed memory may not keep growing by more than this over 15 rebuilds

    [Fact]
    public void Pages_are_quick_and_memory_stays_flat()
    {
        if (Environment.GetEnvironmentVariable("PULSE_PERF") != "1") return;
        string report = null;
        Exception error = null;
        var t = new Thread(() => { try { report = Run(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception("Performance check crashed", error);
        _out.WriteLine(report);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary)) File.AppendAllText(summary, report + "\n");
        Assert.DoesNotContain("SLOW", report);
        Assert.DoesNotContain("LEAK", report);
    }

    static string Run()
    {
        if (Application.Current == null)
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
        }
        foreach (var m in FakeMatches.Make(40)) VStore.Save(m);

        var page = new Pulse.Pages.ValorantPage();
        var type = page.GetType();
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        void Set(string field, object value)
        {
            var f = type.GetField(field, F)!;
            f.SetValue(f.IsStatic ? null : page, value);
        }
        object Call(string method, params object[] args) => type.GetMethod(method, F)!.Invoke(page, args);

        var host = new Border { Width = 1500, Height = 1000, Background = Brushes.Black, Child = page };
        double Draw()
        {
            var sw = Stopwatch.StartNew();
            host.Measure(new Size(1500, 1000));
            host.Arrange(new Rect(0, 0, 1500, 1000));
            host.UpdateLayout();
            var rtb = new RenderTargetBitmap(1500, 1000, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            return sw.Elapsed.TotalMilliseconds;
        }

        var rows = new List<(string Name, double First, double Median, int Elements)>();
        void Measure(string name, Action setup)
        {
            var times = new List<double>();
            for (int i = 0; i < 4; i++)
            {
                var sw = Stopwatch.StartNew();
                setup();
                Call("Build");
                double build = sw.Elapsed.TotalMilliseconds;
                times.Add(build + Draw());
            }
            int count = 0;
            void Walk(DependencyObject d) { count++; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i)); }
            Walk(host);
            var rest = times.Skip(1).OrderBy(x => x).ToList();
            rows.Add((name, times[0], rest[rest.Count / 2], count));
        }

        Draw();
        foreach (var tab in new[] { "coach", "intel", "lineups", "store", "overview" })
            Measure("Valorant: " + tab, () => Set("_tab", tab));
        foreach (var view in new[] { "overview", "matches", "performance", "agents", "maps", "weapons", "encounters", "insights" })
            Measure("Tracker: " + view, () => { Set("_tab", "tracker"); Set("_trView", view); Set("_trMatchId", null); });
        foreach (var mode in new[] { "heat", "rounds" })
            Measure("Heatmaps: " + mode, () => { Set("_tab", "maps"); Set("_mapMode", mode); });
        var match = VStore.All.First(m => VMatch.Counts(m.Queue));
        foreach (var mt in new[] { "scoreboard", "performance", "economy", "rounds", "duels" })
            Measure("Match: " + mt, () =>
            {
                Set("_tab", "tracker"); Set("_trView", "overview");
                Set("_trMatchId", match.Id);
                Set("_trMatchTab", mt);
            });

        // Memory: build the tracker and a match again and again; managed memory after a full collection must stay flat.
        Set("_trMatchId", null);
        long before = Collected();
        for (int i = 0; i < 15; i++)
        {
            Set("_tab", "tracker"); Set("_trView", i % 2 == 0 ? "overview" : "matches"); Call("Build"); Draw();
            Set("_tab", "maps"); Set("_mapMode", i % 2 == 0 ? "heat" : "rounds"); Call("Build"); Draw();
        }
        long after = Collected();
        double growth = (after - before) / 1e6;
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        var sb = new StringBuilder();
        sb.AppendLine("### Performance check");
        sb.AppendLine();
        sb.AppendLine("| Page | First open (ms) | After that (ms) | Elements | |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Name} | {r.First:0} | {r.Median:0} | {r.Elements:N0} | {(r.Median > SlowMs ? "SLOW" : "ok")} |");
        sb.AppendLine();
        sb.AppendLine($"Managed memory: {before / 1e6:0} MB before, {after / 1e6:0} MB after 30 rebuilds ({growth:+0.0;-0.0} MB) {(growth > LeakMb ? "LEAK" : "ok")}");
        sb.AppendLine($"Process: {proc.WorkingSet64 / 1e6:0} MB working set, {proc.PrivateMemorySize64 / 1e6:0} MB private");
        return sb.ToString();
    }

    static long Collected()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(true);
    }
}
