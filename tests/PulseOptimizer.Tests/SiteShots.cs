using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.Core;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>Pulse's pages at 2x, for the website. Only with PULSE_SITE_SHOTS (needs a desktop and internet).</summary>
public class SiteShots
{
    readonly ITestOutputHelper _out;
    public SiteShots(ITestOutputHelper o) => _out = o;

    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            var f = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => f.Continue = false));
            Dispatcher.PushFrame(f);
            Thread.Sleep(30);
        }
    }

    [Fact]
    public void Screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("PULSE_SITE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        var th = new Thread(() =>
        {
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
            }
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            const int W = 1180, H = 780;

            void Shot(string file, Func<FrameworkElement> make, Action<FrameworkElement> shown = null, int wait = 2500, int h = H)
            {
                try
                {
                    var page = make();
                    var host = new Border { Padding = new Thickness(36, 30, 36, 30), Background = (Brush)Application.Current.Resources["BgBrush"], Child = page, Width = W, Height = h };
                    var win = new Window
                    {
                        Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                        Left = -4000, Top = -4000, ShowActivated = false, Background = (Brush)Application.Current.Resources["BgBrush"],
                        FontFamily = (FontFamily)Application.Current.Resources["UiFont"], Foreground = (Brush)Application.Current.Resources["TextBrush"],
                    };
                    win.Show();
                    Pump(300);
                    shown?.Invoke(page);
                    Pump(wait);
                    host.UpdateLayout();
                    var rtb = new RenderTargetBitmap(W * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(dir, file))) enc.Save(fs);
                    win.Close();
                    log.Add("ok " + file);
                }
                catch (Exception ex) { log.Add($"FAIL {file}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
            }

            var findings = new List<Finding>
            {
                new() { Id = "a", Title = "Your memory is running at its slow default speed", Detail = "Your RAM is rated for 6000 MT/s but runs at 4800 MT/s because XMP/EXPO is off in the BIOS.", Fix = "Turn on XMP (EXPO on AMD) in the BIOS. It's one setting and usually worth 10–20% in CPU-heavy games.", Severity = Severity.Problem, Rank = 0 },
                new() { Id = "b", Title = "Your monitor is running below its refresh rate", Detail = "DISPLAY1 supports 240 Hz but Windows is sending 60 Hz.", Fix = "Settings → System → Display → Advanced display → Choose a refresh rate → 240 Hz.", Severity = Severity.Problem, Rank = 1 },
                new() { Id = "c", Title = "Startup programs", Detail = "24 programs are set to launch when you sign in.", Fix = "Each one uses memory and CPU in the background while you play. Review which ones you actually need.", Severity = Severity.Check, Rank = 2 },
                new() { Id = "d", Title = "Check that Resizable BAR is on", Detail = "Your NVIDIA GeForce RTX 4070 supports Resizable BAR.", Severity = Severity.Check, Rank = 3 },
                new() { Id = "e", Title = "Graphics driver", Detail = "NVIDIA 581.57, released 3 weeks ago.", Severity = Severity.Ok, Rank = 4 },
                new() { Id = "f", Title = "Memory channels", Detail = "2 modules installed across channels A, B.", Severity = Severity.Ok, Rank = 5 },
            };
            foreach (var f in findings) try { Pulse.Core.FixGuides.Attach(f, Pulse.Core.HardwareFacts.Get()); } catch { }

            Shot("diagnose.png", () => new Pulse.Pages.DiagnosePage(), p => ((Pulse.IPage)p).OnShown(findings), 3000);
            Shot("tweaks.png", () => new Pulse.Pages.TweaksPage(), p => ((Pulse.IPage)p).OnShown(null), 3000);
            Shot("home.png", () => new Pulse.Pages.HomePage(), p => ((Pulse.IPage)p).OnShown(null), 4000);
            Shot("clips.png", () => new Pulse.Pages.ClipsPage(), p => ((Pulse.IPage)p).OnShown(null), 3000);
            Shot("crosshair.png", () => new Pulse.Pages.CrosshairPage(), p => ((Pulse.IPage)p).OnShown(null), 2500);
            Shot("macros.png", () => new Pulse.Pages.MacrosPage(), p => ((Pulse.IPage)p).OnShown(null), 2000);
            Shot("reaction.png", () => new Pulse.Pages.ReactionPage(), p => ((Pulse.IPage)p).OnShown(null), 2000);
            Shot("debloat.png", () => new Pulse.Pages.DeclutterPage(), p => ((Pulse.IPage)p).OnShown(null), 8000);

            // The guide's Hardware tab, as a Ryzen 5 5600X + RTX 3060 PC.
            Shot("hardware.png", () =>
            {
                var g = new Pulse.Pages.GuidePage();
                var t = typeof(Pulse.Pages.GuidePage);
                t.GetField("_hwDetected", F)!.SetValue(null, true);
                t.GetField("_ramAsked", F)!.SetValue(null, true);
                var cpu = Pulse.Core.Guide.HardwareDb.MatchCpu("AMD Ryzen 5 5600X 6-Core Processor");
                var gpu = Pulse.Core.Guide.HardwareDb.MatchGpu("NVIDIA GeForce RTX 3060", 12L << 30);
                var ram = new Pulse.Core.Guide.HwRam(16, "DDR4", 3200, true);
                foreach (var (n, v) in new (string, object)[] { ("_myCpu", cpu), ("_bCpu", cpu), ("_myGpu", gpu), ("_bGpu", gpu), ("_myRam", ram), ("_bRam", ram), ("_hwPicked", null), ("_hwQuery", "") })
                    t.GetField(n, F)!.SetValue(null, v);
                return g;
            }, p => typeof(Pulse.Pages.GuidePage).GetMethod("ShowTab", F)!.Invoke(p, new object[] { "hardware", false }), 1500, 1500);

            // Brawlhalla: the #1 1v1 player.
            Shot("brawlhalla.png", () => new Pulse.Pages.BrawlhallaPage(), p =>
            {
                var board = Pulse.Core.Games.Brawlhalla.Brawlhalla.Rankings("1v1", "ALL", 1, null, 5).GetAwaiter().GetResult();
                var legends = Pulse.Core.Games.Brawlhalla.Brawlhalla.Legends().GetAwaiter().GetResult();
                Pulse.Core.Games.Brawlhalla.BhProfile prof = null;
                foreach (var r in board.Rows) { prof = Pulse.Core.Games.Brawlhalla.Brawlhalla.Profile(r.Id).GetAwaiter().GetResult(); if (prof != null) break; }
                var t = p.GetType();
                t.GetField("_legends", F)!.SetValue(p, legends);
                t.GetField("_profile", F)!.SetValue(p, prof);
                t.GetField("_tab", F)!.SetValue(p, "legends");
                t.GetMethod("Build", F)!.Invoke(p, null);
            }, 1500, 1300);

            // The first-run setup's games step.
            Shot("setup.png", () => new Pulse.Ui.SetupWizard(), null, 6000);
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        foreach (var l in log) _out.WriteLine(l);
        File.WriteAllLines(Path.Combine(dir, "log.txt"), log);
    }
}
