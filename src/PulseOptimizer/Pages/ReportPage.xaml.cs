using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Reports;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The weekly gaming report: hours per game, average FPS and 1% lows against the week before, and whether a driver
/// or Windows update made a game run worse (or better). Plus the graphics driver check.
/// </summary>
public partial class ReportPage : UserControl, IPage
{
    static ReportState R => TweakEngine.State.Report ??= new ReportState();
    DateTime _week = PlayLog.WeekStart(DateTime.Now);
    static DriverCheck.Status _driver;
    static bool _checking;

    public ReportPage() => InitializeComponent();

    public void OnShown(object parameter)
    {
        if (parameter as string == "last") _week = PlayLog.WeekStart(DateTime.Now).AddDays(-7);
        Build();
        if (_driver == null && !_checking) _ = CheckDriver();
    }

    Brush Res(string k) => (Brush)FindResource(k);
    Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 14), Child = child };
    TextBlock Heading(string t) => new() { Text = Loc.T(t), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap };
    TextBlock Dim(string t, double size = 12.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, top, 0, 0), LineHeight = size * 1.45 };
    TextBlock Faint(string t, double size = 11.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, top, 0, 0) };
    Button Btn(string text, bool primary = false) => new() { Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"), Content = Loc.T(text), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };

    void Build()
    {
        Root.Children.Clear();
        Root.Children.Add(WeekCard());
        var changes = PlayLog.Changes(R.Sessions);
        if (changes.Count > 0) Root.Children.Add(ChangesCard(changes));
        Root.Children.Add(DriverCard());
        Root.Children.Add(SettingsCard());
    }

    // ───────── the week ─────────

    FrameworkElement WeekCard()
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        bool thisWeek = _week == PlayLog.WeekStart(DateTime.Now);
        title.Children.Add(Heading(thisWeek ? "This week" : _week == PlayLog.WeekStart(DateTime.Now).AddDays(-7) ? "Last week" : "Week"));
        title.Children.Add(Faint($"{_week:d MMM} – {_week.AddDays(6):d MMM}".Replace(".", ""), 12, 2));
        head.Children.Add(title);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var prev = Btn("‹ Earlier");
        prev.Click += (_, _) => { _week = _week.AddDays(-7); Build(); };
        var next = Btn("Later ›");
        next.IsEnabled = !thisWeek;
        next.Click += (_, _) => { _week = _week.AddDays(7); Build(); };
        nav.Children.Add(prev); nav.Children.Add(next);
        Grid.SetColumn(nav, 1);
        head.Children.Add(nav);
        sp.Children.Add(head);

        var games = PlayLog.Week(R.Sessions, _week);
        if (games.Count == 0)
        {
            sp.Children.Add(Dim(R.TrackPlay
                ? Loc.T("No games played this week yet. Pulse counts your play time (and FPS, if that's on) by itself while you play.")
                : Loc.T("Play time tracking is off. Turn it on below to get a weekly report."), 12.5, 12));
            return Card(sp);
        }
        double hours = games.Sum(g => g.Hours);
        var prevWeek = PlayLog.Week(R.Sessions, _week.AddDays(-7));
        double prevHours = prevWeek.Sum(g => g.Hours);
        var big = new WrapPanel { Margin = new Thickness(0, 14, 0, 4) };
        void Stat(string value, string label)
        {
            var s = new StackPanel { Margin = new Thickness(0, 0, 36, 0) };
            s.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
            s.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = Res("TextFaintBrush") });
            big.Children.Add(s);
        }
        Stat(Hours(hours), prevHours > 0 ? Loc.F("played ({0} the week before)", Hours(prevHours)) : Loc.T("played"));
        Stat(games.Sum(g => g.Sessions).ToString(CultureInfo.CurrentCulture), Loc.T("sessions"));
        Stat(games[0].Game, Loc.T("most played"));
        sp.Children.Add(big);

        var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        string[] heads = { "Game", "Played", "Average FPS", "1% low", "vs the week before" };
        double[] widths = { 0, 90, 110, 90, 190 };
        for (int i = 0; i < heads.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = Loc.T(heads[i]).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetColumn(t, i); g.Children.Add(t);
        }
        int row = 1;
        foreach (var x in games)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Cell(int col, string text, Brush b, bool bold = false)
            {
                var t = new TextBlock { Text = text, FontSize = 12.5, Foreground = b, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 5, 8, 5), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetRow(t, row); Grid.SetColumn(t, col); g.Children.Add(t);
            }
            Cell(0, x.Game, Res("TextBrush"), true);
            Cell(1, Hours(x.Hours), Res("TextBrush"));
            Cell(2, x.AvgFps > 0 ? x.AvgFps.ToString("0", CultureInfo.CurrentCulture) : "–", Res("TextBrush"));
            Cell(3, x.Low1 > 0 ? x.Low1.ToString("0", CultureInfo.CurrentCulture) : "–", Res("TextBrush"));
            string vs = "–";
            Brush vb = Res("TextFaintBrush");
            if (x.AvgFps > 0 && x.PrevAvgFps > 0)
            {
                double ch = (x.AvgFps - x.PrevAvgFps) / x.PrevAvgFps;
                vs = Math.Abs(ch) < 0.03 ? Loc.T("about the same") : Loc.F("{0}{1:0}% FPS", ch > 0 ? "+" : "−", Math.Abs(ch * 100));
                vb = Res(ch <= -0.08 ? "WarnBrush" : ch >= 0.08 ? "GoodBrush" : "TextDimBrush");
            }
            Cell(4, vs, vb);
            row++;
        }
        sp.Children.Add(g);
        if (games.All(x => x.AvgFps <= 0))
            sp.Children.Add(Faint(R.TrackFps ? Loc.T("FPS appears once you've played for a few minutes with FPS tracking on.") : Loc.T("FPS tracking is off, so only play time is shown."), 11.5, 8));
        sp.Children.Add(Faint(Loc.T("FPS changes with the map, the scene and your settings, so small differences between weeks are normal."), 11.5, 8));
        return Card(sp);
    }

    static string Hours(double h) => h >= 1 ? Loc.F("{0:0.#} h", h) : Loc.F("{0:0} min", h * 60);

    // ───────── after a driver or Windows update ─────────

    FrameworkElement ChangesCard(List<PlayLog.Change> changes)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("After your last update"));
        sp.Children.Add(Dim(Loc.T("For each game: the sessions right before your last driver or Windows update against the ones after it."), 12.5, 4));
        foreach (var c in changes.OrderBy(c => c.FpsChange))
        {
            var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(new TextBlock { Text = c.Game, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            row.Children.Add(Dim(Loc.F("{0}: {1} → {2}", Loc.T(c.What == "driver" ? "Driver" : "Windows"), c.From, c.To), 12, 2));
            var verdict = c.Worse ? Loc.F("Runs worse since then: average FPS {0:0} → {1:0}, 1% low {2:0} → {3:0}.", c.FpsBefore, c.FpsAfter, c.LowBefore, c.LowAfter)
                : c.Better ? Loc.F("Runs better since then: average FPS {0:0} → {1:0}, 1% low {2:0} → {3:0}.", c.FpsBefore, c.FpsAfter, c.LowBefore, c.LowAfter)
                : Loc.F("No real change: average FPS {0:0} → {1:0}, 1% low {2:0} → {3:0}.", c.FpsBefore, c.FpsAfter, c.LowBefore, c.LowAfter);
            row.Children.Add(new TextBlock { Text = verdict, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Res(c.Worse ? "WarnBrush" : c.Better ? "GoodBrush" : "TextDimBrush"), Margin = new Thickness(0, 4, 0, 0) });
            if (c.Worse)
                row.Children.Add(Dim(c.What == "driver"
                    ? Loc.T("If it keeps happening, a clean reinstall of the driver often fixes it (see the PC guide's clean driver guide), or go back to the previous driver until the next one is out.")
                    : Loc.T("Windows updates sometimes reset graphics settings: check Game Mode, Hardware-accelerated GPU scheduling and your power plan, or run Pulse's Diagnose."), 12, 6));
            if (c.SessionsAfter < 4) row.Children.Add(Faint(Loc.F("Based on {0} sessions since the update; a few more will make it surer.", c.SessionsAfter), 11.5, 4));
            sp.Children.Add(row);
        }
        if (changes.Any(c => c.Worse && c.What == "driver"))
        {
            var b = Btn("Clean driver install guide");
            b.Margin = new Thickness(0, 14, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Click += (_, _) => MainWindow.Instance.Navigate("guide", "clean-driver");
            sp.Children.Add(b);
        }
        return Card(sp);
    }

    // ───────── graphics driver ─────────

    async Task CheckDriver()
    {
        _checking = true;
        try { _driver = await DriverCheck.Check(); } catch { }
        _checking = false;
        TweakEngine.Save();
        if (IsVisible) Build();
    }

    FrameworkElement DriverCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Graphics driver"));
        var d = _driver;
        if (d == null)
        {
            sp.Children.Add(Dim(_checking ? Loc.T("Checking…") : Loc.T("Couldn't find a graphics card to check."), 12.5, 6));
            return Card(sp);
        }
        sp.Children.Add(Dim($"{d.Card} · {Loc.F("driver {0}", d.Installed)}" + (d.InstalledDate != default ? " · " + d.InstalledDate.ToString("d", CultureInfo.CurrentCulture) : ""), 12.5, 4));
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (d.Vendor == "NVIDIA" && d.Newest != null)
        {
            var msg = d.UpdateAvailable
                ? Loc.F("NVIDIA released driver {0} on {1}. You have {2}.", d.Newest.Version, d.Newest.Date.ToString("d", CultureInfo.CurrentCulture), d.Installed)
                : Loc.F("You have the newest Game Ready driver ({0}).", d.Installed);
            sp.Children.Add(new TextBlock { Text = msg, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res(d.UpdateAvailable ? "WarnBrush" : "GoodBrush"), Margin = new Thickness(0, 10, 0, 0) });
            if (d.UpdateAvailable)
                sp.Children.Add(Dim(Loc.T("New drivers often fix bugs and add support for new games. If your games run fine, there's no rush: some players wait a few days to see if others report problems."), 12, 6));
        }
        else if (d.Vendor == "NVIDIA")
            sp.Children.Add(Dim(DriverCheck.NvidiaCurrent(d.Card)
                ? Loc.T("Couldn't reach NVIDIA's driver lookup right now.")
                : Loc.T("Your card gets security updates now rather than new Game Ready drivers, so there's nothing newer to look for."), 12.5, 10));
        else
        {
            var age = d.AgeDays;
            sp.Children.Add(Dim(age > 90
                ? Loc.F("Your driver is {0} months old. {1} usually releases new ones every month or so; check {2} for a newer one.", age / 30, d.Vendor, d.Vendor == "AMD" ? "AMD Software" : "Intel's support page")
                : Loc.F("{0} doesn't offer a way to check for new drivers automatically. Yours is from {1}.", d.Vendor.Length > 0 ? d.Vendor : Loc.T("Your card's maker"), d.InstalledDate == default ? "?" : d.InstalledDate.ToString("d", CultureInfo.CurrentCulture)), 12.5, 10));
        }
        var get = Btn(d.Vendor.Length > 0 ? Loc.F("{0} drivers", d.Vendor) + "  ↗" : "Driver page  ↗", d.UpdateAvailable);
        get.Click += (_, _) => Open(DriverCheck.PageFor(d.Vendor));
        buttons.Children.Add(get);
        var guide = Btn("Clean install guide");
        guide.Click += (_, _) => MainWindow.Instance.Navigate("guide", "clean-driver");
        buttons.Children.Add(guide);
        var again = Btn(_checking ? "Checking…" : "Check again");
        again.IsEnabled = !_checking;
        again.Click += async (_, _) => { _driver = null; Build(); await CheckDriver(); };
        buttons.Children.Add(again);
        sp.Children.Add(buttons);
        return Card(sp);
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ───────── settings ─────────

    FrameworkElement SettingsCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("What Pulse keeps"));
        sp.Children.Add(Row("Count play time", "Which game you play and for how long. Kept on this PC only, 13 weeks back.", R.TrackPlay, v => { R.TrackPlay = v; GameSessionService.Refresh(); }));
        sp.Children.Add(Row("Count FPS while you play", "Uses the same frame counter as the overlay (PresentMon), quietly in the background while a game runs. Very light, but you can turn it off.", R.TrackFps, v => { R.TrackFps = v; GameSessionService.Refresh(); }));
        sp.Children.Add(Row("Tell me when a new driver is out", "Checks NVIDIA's driver lookup once a day and shows a note when there's a newer Game Ready driver.", R.DriverAlerts, v => R.DriverAlerts = v));
        var clear = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Clear play history"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!await MainWindow.Instance.Confirm(Loc.T("Clear play history?"), Loc.T("Removes every play session from the weekly report. This can't be undone."), Loc.T("Clear"), Loc.T("Cancel"))) return;
            R.Sessions.Clear();
            TweakEngine.Save();
            Build();
        };
        sp.Children.Add(clear);
        return Card(sp);
    }

    FrameworkElement Row(string label, string help, bool on, Action<bool> set)
    {
        var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        left.Children.Add(new TextBlock { Text = Loc.T(help), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 24, 0), LineHeight = 17 });
        g.Children.Add(left);
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Top };
        sw.Click += (_, _) => { set(sw.IsChecked == true); TweakEngine.Save(); };
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        return g;
    }
}
