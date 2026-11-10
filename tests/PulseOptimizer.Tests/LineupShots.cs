using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.Core;
using Xunit;
using Xunit.Abstractions;

namespace PulseOptimizer.Tests;

/// <summary>Pulse's pages at 2x, for checking the lineups page. Only with PULSE_LINEUP_SHOTS (needs a desktop and internet).</summary>
public class LineupShots
{
    readonly ITestOutputHelper _out;
    public LineupShots(ITestOutputHelper o) => _out = o;

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
        var dir = Environment.GetEnvironmentVariable("PULSE_LINEUP_SHOTS");
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

            var vp = typeof(Pulse.Pages.ValorantPage);
            void Set(string f, object v) => vp.GetField(f, F)!.SetValue(null, v);
            Shot("lineups-sova.png", () => { Set("_luAgent", "Sova"); Set("_luMap", "Ascent"); Set("_luSel", ""); return new Pulse.Pages.ValorantPage(); }, p => ((Pulse.IPage)p).OnShown("lineups"), 3000, 1000);
            Shot("lineups-viper.png", () => { Set("_luAgent", "Viper"); Set("_luMap", ""); Set("_luSel", ""); return new Pulse.Pages.ValorantPage(); }, p => ((Pulse.IPage)p).OnShown("lineups"), 3000, 1000);
            Shot("lineups-haven.png", () => { Set("_luAgent", "Sova"); Set("_luMap", "Haven"); Set("_luSel", ""); return new Pulse.Pages.ValorantPage(); }, p => ((Pulse.IPage)p).OnShown("lineups"), 3000, 1000);
            Shot("lineups-kj.png", () => { Set("_luAgent", "Killjoy"); Set("_luMap", ""); Set("_luSel", ""); return new Pulse.Pages.ValorantPage(); }, p => ((Pulse.IPage)p).OnShown("lineups"), 2500, 700);
            Shot("lineups-edit.png", () => { Set("_luEdit", new Pulse.Core.Games.Valorant.Lineup { Map = "Bind", Agent = "Sova", StandX = 0.3, StandY = 0.6, LandX = 0.5, LandY = 0.35 }); return new Pulse.Pages.ValorantPage(); }, p => ((Pulse.IPage)p).OnShown("lineups"), 2500, 1400);
            Set("_luEdit", null);
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        foreach (var l in log) _out.WriteLine(l);
        File.WriteAllLines(Path.Combine(dir, "log.txt"), log);
    }
}
