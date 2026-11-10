using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.Core;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>Pulse's pages at 2x, for the website. Only with PULSE_MOD_SHOTS (needs a desktop and internet).</summary>
public class ModShots
{
    readonly ITestOutputHelper _out;
    public ModShots(ITestOutputHelper o) => _out = o;

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
        var dir = Environment.GetEnvironmentVariable("PULSE_MOD_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        var th = new Thread(() =>
        {
            // Pages await work and come back to this thread: give it a dispatcher context like the app's UI thread.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { log.Add("UI exception: " + e.Exception.Message); e.Handled = true; };
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Pulse;component/Themes/Theme.xaml") });
            }
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            const int W = 1180, H = 780;

            void Shot(string file, Func<FrameworkElement> make, Action<FrameworkElement> shown = null, int wait = 2500, int h = H, Action<FrameworkElement> after = null)
            {
                try
                {
                    var page = make();
                    var host = new Border { Padding = new Thickness(36, 30, 36, 30), Background = (Brush)Application.Current.Resources["BgBrush"], Child = page, Width = W, Height = h };
                    var win = new Window
                    {
                        // In a canvas, so the page lays out at full size even on a CI desktop smaller than the page.
                        Content = new Canvas { Width = 10, Height = 10, Children = { host } }, Width = 40, Height = 40, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                        Left = -4000, Top = -4000, ShowActivated = false, Background = (Brush)Application.Current.Resources["BgBrush"],
                        FontFamily = (FontFamily)Application.Current.Resources["UiFont"], Foreground = (Brush)Application.Current.Resources["TextBrush"],
                    };
                    win.Show();
                    Pump(300);
                    shown?.Invoke(page);
                    Pump(wait);
                    if (after != null) { after(page); Pump(400); }
                    host.Measure(new Size(W, h)); host.Arrange(new Rect(0, 0, W, h));
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

            // A stand-in Brawlhalla folder: one test skin and two backgrounds.
            var game = Path.Combine(Path.GetTempPath(), "pulse-bh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(game, "mapArt", "Backgrounds"));
            File.WriteAllBytes(Path.Combine(game, "Brawlhalla.exe"), new byte[16]);
            File.WriteAllBytes(Path.Combine(game, "Gfx_Mikasa.swf"), BrawlhallaModTests.SkinFile());
            File.WriteAllBytes(Path.Combine(game, "Gfx_Viking.swf"), BrawlhallaModTests.SkinFile());
            foreach (var (name, a, b2) in new[] { ("BG_Grove.jpg", Colors.ForestGreen, Colors.Goldenrod), ("BG_Space.jpg", Colors.MidnightBlue, Colors.MediumPurple), ("BG_MiamiDome_Anim.jpg", Colors.OrangeRed, Colors.DeepSkyBlue) })
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new LinearGradientBrush(a, b2, 35), null, new Rect(0, 0, 640, 360)); dc.DrawEllipse(Brushes.White, null, new Point(420, 120), 50, 50); }
                var rt = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); rt.Render(dv);
                var je = new JpegBitmapEncoder(); je.Frames.Add(BitmapFrame.Create(rt));
                using var fs = File.Create(Path.Combine(game, "mapArt", "Backgrounds", name)); je.Save(fs);
            }
            typeof(Pulse.Core.Games.Brawlhalla.BrawlhallaMods).GetField("_gameDir", F)!.SetValue(null, game);
            var bp = typeof(Pulse.Pages.BrawlhallaPage);
            void Set(object page, string f, object v) => bp.GetField(f, F)!.SetValue(page, v);
            var mods = (TweakEngine.State.Brawlhalla ??= new()).Mods ??= new();
            FrameworkElement Page(string tab, bool ack)
            {
                mods.Acknowledged = ack;
                var p = new Pulse.Pages.BrawlhallaPage();
                Set(p, "_mode", "customize"); Set(p, "_modTab", tab);
                return p;
            }
            void Rebuild(FrameworkElement p) => bp.GetMethod("Build", F)!.Invoke(p, null);
            void OpenSkin(FrameworkElement p)
            {
                var skins = (System.Collections.IList)bp.GetField("_skins", F)!.GetValue(p);
                if (skins == null || skins.Count == 0) { log.Add("no skins"); return; }
                bp.GetMethod("OpenSkin", F)!.Invoke(p, new[] { skins[0] });
            }
            Shot("mods-notice.png", () => Page("skins", false), Rebuild, 1500);
            mods.Pictures["Gfx_Mikasa.swf"] = new() { ["a_Hair_Test"] = "missing.png" };
            mods.Effects["dash"] = "22E1FF";
            mods.Backgrounds["BG_Grove.jpg"] = "clean";
            mods.Backgrounds["BG_Space.jpg"] = "night";
            Shot("mods-skins.png", () => Page("skins", true), Rebuild, 4000, 900, p => { OpenSkin(p); Pump(1500); Rebuild(p); });
            Shot("mods-colors.png", () => Page("colors", true), Rebuild, 4000, 900, p => { OpenSkin(p); Pump(1500); bp.GetField("_openColor", F)!.SetValue(p, "FF0000"); Rebuild(p); });
            Shot("mods-effects.png", () => Page("effects", true), Rebuild, 1500, 1300);
            Shot("mods-maps.png", () => Page("maps", true), Rebuild, 2500, 900);
            Shot("mods-smooth.png", () => Page("smooth", true), Rebuild, 1500, 1000);
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        foreach (var l in log) _out.WriteLine(l);
        File.WriteAllLines(Path.Combine(dir, "log.txt"), log);
    }
}
