using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Ui;

namespace Pulse.Pages;

public partial class DiagnosePage : UserControl, IPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly CpuMeter _cpu = new();
    bool _running, _specsLoaded;

    public DiagnosePage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateLive();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>The latest checkup, shared with the Home dashboard.</summary>
    public static List<Finding> Last { get; private set; }
    public static event Action Completed;

    public async void OnShown(object parameter)
    {
        if (parameter as string == "run") Run_Click(null, null);
        // A checkup that already ran (the first-run setup): show it instead of running it again.
        if (parameter is List<Finding> done) { RunBtnText.Text = "Run again"; ShowFindings(done); }
        if (_specsLoaded) return;
        _specsLoaded = true;
        _cpu.Sample();
        var specs = await Task.Run(() => new (string glyph, string label, string value)[]
        {
            ("", "Processor", $"{SystemInfo.Cpu} · {SystemInfo.Threads} threads"),
            ("", "Graphics", SystemInfo.GpuText),
            ("", "Memory", Format.Bytes(SystemInfo.TotalRam, 0)),
            ("", "Windows", SystemInfo.OsName),
            ("", "System drive", $"{Format.Bytes(SystemInfo.SystemDrive.AvailableFreeSpace)} free of {Format.Bytes(SystemInfo.SystemDrive.TotalSize, 0)} · {SystemInfo.SystemDriveMedia}"),
            ("", "Up time", FormatUptime(SystemInfo.Uptime)),
        });
        foreach (var (glyph, label, value) in specs) SpecGrid.Children.Add(Tile(glyph, label, value));
    }

    static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : $"{t.Hours} h {t.Minutes} min";

    static FrameworkElement Tile(string glyph, string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 16, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("IconText"), Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush"), Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("Label") });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(stack, 1);
        g.Children.Add(icon);
        g.Children.Add(stack);
        Anim.SetReveal(g, true);
        return g;
    }

    void UpdateLive()
    {
        if (MainWindow.Instance?.Quiet == true) return; // Pulse is behind a game or minimized
        var cpu = _cpu.Sample();
        var m = Native.Memory();
        double used = m.ullTotalPhys - m.ullAvailPhys;
        Anim.Bar(CpuBar, cpu);
        Anim.Bar(RamBar, used / m.ullTotalPhys);
        CpuText.Text = $"{cpu * 100:0}%";
        RamText.Text = $"{Format.Bytes(used)} / {Format.Bytes(m.ullTotalPhys, 0)}";
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        RunBtn.IsEnabled = false;
        SummarySpinner.Visibility = Visibility.Visible;
        SummaryRing.Visibility = Visibility.Hidden;
        SummaryBig.Text = "…";
        SummarySmall.Text = "checking";
        ProgressText.Visibility = Visibility.Visible;
        FindingsList.ItemsSource = null;

        var progress = new Progress<string>(s => ProgressText.Text = s + "…");
        List<Finding> findings;
        try { findings = await Task.Run(() => PcDiagnostics.RunAll(progress)); }
        finally
        {
            _running = false;
            RunBtn.IsEnabled = true;
            SummarySpinner.Visibility = Visibility.Collapsed;
            SummaryRing.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Collapsed;
            RunBtnText.Text = "Run again";
        }
        ShowFindings(findings);
    }

    void ShowFindings(List<Finding> findings)
    {
        int ok = findings.Count(f => f.Severity == Severity.Ok);
        int problems = findings.Count(f => f.Severity == Severity.Problem);
        int checks = findings.Count(f => f.Severity == Severity.Check);
        Last = findings;
        Completed?.Invoke();
        SummaryBig.Text = $"{ok}/{findings.Count}";
        SummarySmall.Text = "checks look good";
        SummaryRing.Value = 0;
        SummaryRing.AnimateTo(findings.Count == 0 ? 0 : 100.0 * ok / findings.Count);
        SummaryText.Text = problems == 0 && checks == 0
            ? "No problems found. Your PC is set up well, so tweaks will only make small differences from here."
            : $"{problems} problem{(problems == 1 ? "" : "s")} and {checks} thing{(checks == 1 ? "" : "s")} worth checking, listed below with the most impactful first.";
        FindingsHeader.Visibility = Visibility.Visible;
        FindingsList.ItemsSource = findings;
    }

    void FindingAction_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.RunAction((sender as FrameworkElement)?.Tag as string);

    async void Recommended_Click(object sender, RoutedEventArgs e) => await TweakRunner.ApplyRecommended();
    void Measure_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("measure");
    void Security_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("security");
}
