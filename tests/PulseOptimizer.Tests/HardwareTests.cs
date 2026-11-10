using Pulse.Core.Guide;
using Xunit;

namespace PulseOptimizer.Tests;

public class HardwareTests
{
    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700KF", "Core i7-13700K")]
    [InlineData("Intel(R) Core(TM) i5-9400F CPU @ 2.90GHz", "Core i5-9400")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "Ryzen 7 5800X3D")]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "Ryzen 5 5600X")]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "Ryzen 7 7840HS")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265KF", "Core Ultra 7 265K")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", "Core i5-12400")]
    public void Windows_cpu_names_find_their_part(string windows, string expected) => Assert.Equal(expected, HardwareDb.MatchCpu(windows)?.Name);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", 12L << 30, "GeForce RTX 3060")]
    [InlineData("NVIDIA GeForce RTX 3060", 8L << 30, "GeForce RTX 3060 8GB")]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", 16L << 30, "GeForce RTX 4060 Ti 16GB")]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", 8L << 30, "GeForce RTX 4060 Ti")]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 0L, "GeForce RTX 4060 Laptop")]
    [InlineData("AMD Radeon RX 6700 XT", 0L, "Radeon RX 6700 XT")]
    [InlineData("AMD Radeon 780M Graphics", 0L, "Radeon 780M")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", 0L, "Arc B580")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", 0L, "GeForce GTX 1660 Super")]
    public void Windows_gpu_names_find_their_part(string windows, long vram, string expected) => Assert.Equal(expected, HardwareDb.MatchGpu(windows, vram)?.Name);

    [Fact]
    public void Generic_integrated_graphics_match_nothing() => Assert.Null(HardwareDb.MatchGpu("AMD Radeon(TM) Graphics"));

    static HwCpu Cpu(string n) => HardwareDb.Cpus.First(c => c.Name == n);
    static HwGpu Gpu(string n) => HardwareDb.Gpus.First(g => g.Name == n);
    static HwGame Game(string n) => HardwareDb.Games.First(g => g.Name == n);
    static readonly HwRam Ram = new(32, "DDR5", 6000, true);

    [Fact]
    public void The_reference_pc_lands_near_the_reference_numbers()
    {
        var e = HardwareDb.Estimate(Cpu("Ryzen 7 7800X3D"), Gpu("GeForce RTX 4070"), Ram, Game("Cyberpunk 2077"), 1440);
        Assert.InRange(e.Fps, 70, 82);
        Assert.Equal("gpu", e.Limit);
    }

    [Fact]
    public void Faster_parts_and_lower_resolutions_mean_more_frames()
    {
        var game = Game("Fortnite");
        var slow = HardwareDb.Estimate(Cpu("Ryzen 5 3600"), Gpu("GeForce GTX 1660 Super"), Ram, game, 1080).Fps;
        var fast = HardwareDb.Estimate(Cpu("Ryzen 7 9800X3D"), Gpu("GeForce RTX 5080"), Ram, game, 1080).Fps;
        Assert.True(fast > slow * 2);
        var at720 = HardwareDb.Estimate(Cpu("Ryzen 5 3600"), Gpu("GeForce GTX 1660 Super"), Ram, game, 720).Fps;
        var at1440 = HardwareDb.Estimate(Cpu("Ryzen 5 3600"), Gpu("GeForce GTX 1660 Super"), Ram, game, 1440).Fps;
        Assert.True(at720 > slow && slow > at1440);
    }

    [Fact]
    public void A_big_card_on_an_old_cpu_is_cpu_limited_and_caps_hold()
    {
        Assert.Equal("cpu", HardwareDb.Estimate(Cpu("Core i5-4460"), Gpu("GeForce RTX 4090"), Ram, Game("Counter-Strike 2"), 1080).Limit);
        var elden = HardwareDb.Estimate(Cpu("Ryzen 7 9800X3D"), Gpu("GeForce RTX 5090"), Ram, Game("Elden Ring"), 1080);
        Assert.Equal(60.0, elden.Fps);
        Assert.Equal("cap", elden.Limit);
    }

    [Fact]
    public void Short_video_memory_is_flagged_and_one_stick_costs_frames()
    {
        Assert.True(HardwareDb.Estimate(Cpu("Ryzen 5 5600"), Gpu("GeForce GTX 1650"), Ram, Game("Cyberpunk 2077"), 1440).VramShort);
        var two = HardwareDb.Estimate(Cpu("Ryzen 5 5600"), Gpu("GeForce RTX 4090"), new HwRam(16, "DDR4", 3200, true), Game("Valorant"), 1080).Fps;
        var one = HardwareDb.Estimate(Cpu("Ryzen 5 5600"), Gpu("GeForce RTX 4090"), new HwRam(16, "DDR4", 3200, false), Game("Valorant"), 1080).Fps;
        Assert.True(one < two * 0.9);
    }

    [Fact]
    public void Search_finds_parts_by_model_number()
    {
        Assert.IsType<HwCpu>(HardwareDb.Search("5600x").First());
        Assert.Equal("GeForce RTX 4060", ((HwGpu)HardwareDb.Search("rtx 4060").First(p => p is HwGpu)).Name);
        Assert.Contains(HardwareDb.Search("ddr5 6000"), p => p is HwRam);
    }

    [Fact]
    public void Every_part_has_a_sane_score() 
    {
        Assert.True(HardwareDb.Cpus.Count > 150 && HardwareDb.Gpus.Count > 130 && HardwareDb.Games.Count >= 12);
        Assert.All(HardwareDb.Cpus, c => Assert.InRange(c.Score, 15, 130));
        Assert.All(HardwareDb.Gpus, g => Assert.InRange(g.Score, 1, 250));
        Assert.Equal(HardwareDb.Cpus.Count, HardwareDb.Cpus.Select(c => c.Name).Distinct().Count());
        Assert.Equal(HardwareDb.Gpus.Count, HardwareDb.Gpus.Select(g => g.Name).Distinct().Count());
    }
}

/// <summary>The guide's Hardware tab, for a person to look at. Only with PULSE_SHOTS.</summary>
public class HardwareShots
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
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                void Shot(string file, Action<Type> set)
                {
                    var page = new Pulse.Pages.GuidePage();
                    var t = page.GetType();
                    t.GetField("_hwDetected", F)!.SetValue(null, true);
                    t.GetField("_ramAsked", F)!.SetValue(null, true);
                    t.GetField("_myCpu", F)!.SetValue(null, HardwareDb.MatchCpu("AMD Ryzen 5 5600X 6-Core Processor"));
                    t.GetField("_myGpu", F)!.SetValue(null, HardwareDb.MatchGpu("NVIDIA GeForce RTX 3060", 12L << 30));
                    t.GetField("_myRam", F)!.SetValue(null, new HwRam(16, "DDR4", 3200, true));
                    t.GetField("_bCpu", F)!.SetValue(null, t.GetField("_myCpu", F)!.GetValue(null));
                    t.GetField("_bGpu", F)!.SetValue(null, t.GetField("_myGpu", F)!.GetValue(null));
                    t.GetField("_bRam", F)!.SetValue(null, t.GetField("_myRam", F)!.GetValue(null));
                    set(t);
                    t.GetMethod("ShowTab", F)!.Invoke(page, new object[] { "hardware", false });
                    var host = new System.Windows.Controls.Border { Width = 1200, Height = 2600, Padding = new System.Windows.Thickness(28), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0C, 0x0F)), Child = page };
                    host.Measure(new System.Windows.Size(1200, 2600)); host.Arrange(new System.Windows.Rect(0, 0, 1200, 2600)); host.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 2600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }
                Shot("hardware-mine.png", t => { t.GetField("_hwPicked", F)!.SetValue(null, null); t.GetField("_hwQuery", F)!.SetValue(null, ""); });
                Shot("hardware-picked.png", t =>
                {
                    t.GetField("_hwQuery", F)!.SetValue(null, "rtx 50");
                    t.GetField("_hwPicked", F)!.SetValue(null, HardwareDb.Gpus.First(g => g.Name == "GeForce RTX 5070"));
                });
            }
            catch (Exception ex) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (error != null) throw new Exception("Screenshot failed", error);
    }
}
