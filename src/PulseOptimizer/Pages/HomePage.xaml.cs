using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>The dashboard: PC health, live load, shortcuts and the game library.</summary>
public partial class HomePage : UserControl, IPage
{
    bool _loaded;

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LiveStats.Updated += UpdateLive;
            DiagnosePage.Completed += ShowHealth;
            LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            UpdateLive();
        };
        Unloaded += (_, _) =>
        {
            LiveStats.Updated -= UpdateLive;
            DiagnosePage.Completed -= ShowHealth;
        };
    }

    public async void OnShown(object parameter)
    {
        var hour = DateTime.Now.Hour;
        Greeting.Text = Loc.T(hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening");
        ShowHealth();
        UpdateStats();

        if (!_loaded)
        {
            _loaded = true;
            var (cpu, gpu, ram, os) = await Task.Run(() => (SystemInfo.Cpu, SystemInfo.GpuText, Format.Bytes(SystemInfo.TotalRam, 0), SystemInfo.OsName));
            PcLine.Text = $"{os} · {cpu}";
            CpuName.Text = cpu;
            GpuName.Text = gpu;
            RamName.Text = Loc.F("{0} installed", ram);
        }

        var games = await LatencyPlan.GamesAsync();
        GamesLoading.Visibility = Visibility.Collapsed;
        var real = games.Where(g => !g.Game.IsAddTile && g.Game.Id != "other").ToList();
        // Your own games first; fill up to 8 with supported ones so the strip is never empty.
        var mine = real.Where(g => g.Installed || g.Running).ToList();
        GameStrip.ItemsSource = mine.Concat(real.Except(mine)).Take(Math.Max(8, mine.Count)).ToList();
        int installed = real.Count(g => g.Installed);
        GameStat.Text = installed == 0 ? Loc.F("{0} games supported", real.Count) : Loc.F("{0} installed · {1} supported", installed, real.Count);
    }

    void UpdateStats()
    {
        var d = SystemInfo.SystemDrive;
        DeclutterStat.Text = Loc.F("{0} free on {1}", Format.Bytes(d.AvailableFreeSpace), d.Name.TrimEnd('\\'));
        int n = TweakEngine.State.Applied.Count;
        TweakStat.Text = n == 0 ? Loc.T("None applied yet") : Loc.F("{0} applied, all undoable", n);
        var c = TweakEngine.State.Clips;
        ClipStat.Text = c is { Enabled: true }
            ? Loc.F("{0} saves the last {1} s", ClipsService.HotkeyText(c.HotkeyModifiers, c.HotkeyKey), c.ReplaySeconds)
            : Loc.T("Save your best moments with a hotkey");
    }

    void UpdateLive()
    {
        ClipsUse.Text = ClipsService.State == ClipsState.Recording
            ? Loc.F("Clips is using {0}% CPU and {1}", ClipsService.CpuPercent.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), Format.Bytes(ClipsService.BufferBytes))
            : Loc.T(TweakEngine.State.Clips is { Enabled: true } ? "Clips is waiting for a game · it uses nothing until then" : "Clips is off · it uses nothing until you turn it on");
        if (LiveStats.Cpu.Count == 0) return;
        CpuText.Text = $"{LiveStats.CpuNow * 100:0}%";
        GpuText.Text = LiveStats.GpuNow is double g ? $"{g * 100:0}%" : "n/a";
        RamText.Text = $"{LiveStats.RamNow * 100:0}%";
        RamName.Text = Loc.F("{0} of {1} in use", Format.Bytes(LiveStats.RamUsedBytes), Format.Bytes(LiveStats.RamTotalBytes, 0));
        CpuSpark.SetValues(LiveStats.Cpu);
        GpuSpark.SetValues(LiveStats.Gpu);
        RamSpark.SetValues(LiveStats.Ram);
    }

    void ShowHealth()
    {
        var f = DiagnosePage.Last;
        if (f == null || f.Count == 0) return;
        int ok = f.Count(x => x.Severity == Severity.Ok);
        var issues = f.Where(x => x.Severity is Severity.Problem or Severity.Check).OrderBy(x => x.Severity == Severity.Problem ? 0 : 1).ThenBy(x => x.Rank).ToList();
        HealthBig.Text = $"{ok}/{f.Count}";
        HealthSmall.Text = Loc.T("checks pass");
        HealthRing.Value = 0;
        HealthRing.AnimateTo(100.0 * ok / f.Count);
        HealthTitle.Text = issues.Count == 0 ? Loc.T("Everything looks good") : issues.Count == 1 ? Loc.T("1 thing to fix") : Loc.F("{0} things to fix", issues.Count);
        HealthText.Text = Loc.T(issues.Count == 0 ? "No problems found in the last checkup." : "The biggest ones first:");
        IssueList.ItemsSource = issues.Take(3).ToList();
        HealthBtnText.Text = Loc.T(issues.Count == 0 ? "Run again" : "Fix these now");
    }

    void Health_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate("diagnose", DiagnosePage.Last == null ? "run" : null);

    void Tile_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate((sender as FrameworkElement)?.Tag as string);

    void Game_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameItem g) MainWindow.Instance.Navigate("games", g.Game.Id);
    }
}
