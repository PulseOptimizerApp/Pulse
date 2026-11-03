using System.Windows;
using System.Windows.Controls;
using Pulse.Core;
using Pulse.Core.Fresh;

namespace Pulse.Pages;

public partial class FreshPage : UserControl, IPage
{
    bool _busy;
    Snapshot _last;

    public FreshPage() => InitializeComponent();

    public void OnShown(object parameter)
    {
        WeeklySwitch.IsChecked = TweakEngine.State.WeeklyTuneUp;
        _last ??= TweakEngine.State.FreshHistory.FirstOrDefault();
        Render();
    }

    void Render()
    {
        var st = TweakEngine.State;
        var b = st.Baseline;
        if (b == null)
        {
            StatusTitle.Text = "No snapshot yet";
            CheckBtnText.Text = "Save snapshot";
            RebaseBtn.Visibility = Visibility.Collapsed;
            DriftHeader.Visibility = Visibility.Collapsed;
            DriftList.ItemsSource = null;
        }
        else
        {
            CheckBtnText.Text = "Check now";
            RebaseBtn.Visibility = _last != null && _last.Date > b.Date ? Visibility.Visible : Visibility.Collapsed;
            var drifts = _last != null && _last.Date > b.Date ? Freshness.Compare(b, _last) : new();
            int bad = drifts.Count(d => d.Kind != DriftKind.Good);
            StatusTitle.Text = _last == null || _last.Date <= b.Date ? "Baseline saved" : bad == 0 ? "As good as your baseline" : $"{bad} thing{(bad == 1 ? "" : "s")} drifted";
            StatusText.Text = $"Baseline from {b.Date:MMMM d, yyyy}: {b.StartupApps.Count} startup apps"
                + (b.BootSeconds is double s ? $", {s:0} s boot" : "") + $", {b.Processes} processes, {Format.Bytes(b.FreeBytes)} free."
                + (_last != null && _last.Date > b.Date ? $" Last checked {Format.Ago(_last.Date)}." : " Check again in a week or two to see what changed.");
            DriftHeader.Visibility = drifts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DriftList.ItemsSource = drifts;
        }

        var rows = st.FreshHistory.Take(8).Append(b).Where(x => x != null).Select(x => new
        {
            When = x == b ? $"{x.Date:MMM d} (baseline)" : x.Date.ToString("MMM d, HH:mm"),
            Startup = $"{x.StartupApps.Count} startup apps",
            Boot = x.BootSeconds is double s ? $"{s:0} s boot" : "boot n/a",
            Procs = $"{x.Processes} processes",
            Free = $"{Format.Bytes(x.FreeBytes)} free",
        }).ToList();
        HistoryList.ItemsSource = rows;
        HistoryHeader.Visibility = HistoryCard.Visibility = rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CheckBtn.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        try
        {
            var snap = await Task.Run(Freshness.Take);
            var st = TweakEngine.State;
            if (st.Baseline == null)
            {
                st.Baseline = snap;
                TweakEngine.AddLog("fresh", "Saved a baseline snapshot");
                MainWindow.Instance.Toast("Baseline saved. Pulse will compare against it from now on.");
            }
            else
            {
                st.FreshHistory.Insert(0, snap);
                if (st.FreshHistory.Count > 12) st.FreshHistory.RemoveRange(12, st.FreshHistory.Count - 12);
                _last = snap;
            }
            TweakEngine.Save();
            Render();
        }
        finally
        {
            _busy = false;
            CheckBtn.IsEnabled = true;
            Working.Visibility = Visibility.Collapsed;
        }
    }

    async void Rebase_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        if (!await MainWindow.Instance.Confirm("Make this the new baseline?", "Do this once you've dealt with what drifted and the PC feels fast again. Future checks compare against today.", "Save as baseline")) return;
        TweakEngine.State.Baseline = _last;
        TweakEngine.Save();
        Render();
    }

    async void Weekly_Click(object sender, RoutedEventArgs e)
    {
        bool on = WeeklySwitch.IsChecked == true;
        WeeklySwitch.IsEnabled = false;
        var (ok, msg) = await Task.Run(() => Freshness.SetWeekly(on));
        WeeklySwitch.IsEnabled = true;
        if (!ok)
        {
            WeeklySwitch.IsChecked = !on;
            await MainWindow.Instance.Alert("Couldn't change the weekly tune-up", msg);
            return;
        }
        MainWindow.Instance.Toast(on ? $"Weekly tune-up on: every {DateTime.Now.DayOfWeek} at noon." : "Weekly tune-up off. The scheduled task was removed.");
        if (on && TweakEngine.State.Baseline == null) Check_Click(null, null);
    }

    void Action_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.RunAction((sender as FrameworkElement)?.Tag as string);
}
