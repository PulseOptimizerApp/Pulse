using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pulse.Core;

namespace Pulse.Pages;

public partial class TweaksPage : UserControl, IPage
{
    ICollectionView _view;
    string _filter = "fit";
    bool _loaded;

    public TweaksPage()
    {
        InitializeComponent();
    }

    public async void OnShown(object parameter)
    {
        if (!_loaded)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            // Gates and status checks query the system (some run PowerShell), so do them off the UI thread.
            await Task.Run(() =>
            {
                foreach (var t in TweakCatalog.All)
                {
                    _ = t.GateStatus;
                    TweakEngine.Refresh(t);
                }
            });
            _view = CollectionViewSource.GetDefaultView(TweakCatalog.All);
            _view.Filter = Filter;
            // Recommended-for-this-PC first, then what's on, then the rest.
            if (_view is ListCollectionView lcv) lcv.CustomSort = Comparer<object>.Create((a, b) => Rank((Tweak)a).CompareTo(Rank((Tweak)b)));
            TweakList.ItemsSource = _view;
            LoadingPanel.Visibility = Visibility.Collapsed;
            _loaded = true;
            foreach (var t in TweakCatalog.All) t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Tweak.IsOn)) Dispatcher.InvokeAsync(UpdateCount); };
        }
        UpdateCount();
    }

    static int Rank(Tweak t) =>
        !t.GateStatus.Met ? 3 : t.Recommended && !t.IsOn ? 0 : t.Recommended ? 1 : 2;

    bool Filter(object o)
    {
        var t = (Tweak)o;
        var q = Search.Text.Trim();
        if (q.Length > 0 &&
            !(t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              t.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              t.Changes.Contains(q, StringComparison.OrdinalIgnoreCase)))
            return false;
        return _filter switch
        {
            "all" => true,
            "fit" => t.GateStatus.Met && (t.Recommended || t.Evidence == Evidence.Gated || t.IsOn),
            _ => t.Category.ToString() == _filter,
        };
    }

    void UpdateCount()
    {
        var all = TweakCatalog.All;
        int on = all.Count(t => t.IsOn);
        int rec = all.Count(t => t.Recommended && t.GateStatus.Met && !t.IsOn);
        int recTotal = all.Count(t => t.Recommended && t.GateStatus.Met);
        int recOn = recTotal - rec;
        CountText.Text = rec == 0
            ? $"All {recTotal} recommended tweaks for this PC are on · {on} on in total"
            : $"{recOn} of {recTotal} recommended tweaks on · {rec} to go";
        Pulse.Ui.Anim.Bar(RecBar, recTotal == 0 ? 1 : (double)recOn / recTotal);
        foreach (var t in all) t.RefreshStatus();
    }

    void Chip_Checked(object sender, RoutedEventArgs e)
    {
        _filter = (string)((FrameworkElement)sender).Tag;
        _view?.Refresh();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    async void Switch_Click(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        var t = (Tweak)cb.DataContext;
        bool want = cb.IsChecked == true;
        // The click set IsChecked directly, which would cut the switch off from the tweak's real state.
        // Re-attach it so the switch always shows what's actually applied (also after a cancel or a failure).
        cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(Tweak.IsOn)) { Mode = BindingMode.OneWay });
        if (t.IsBusy) return;
        await TweakRunner.Set(t, want);
        UpdateCount();
    }

    void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        if (((FrameworkElement)sender).FindName("DetailsToggle") is System.Windows.Controls.Primitives.ToggleButton tb) tb.IsChecked = tb.IsChecked != true;
    }

    void Measure_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate("measure", ((FrameworkElement)sender).DataContext as Tweak);

    void Source_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string url && url.StartsWith("https://"))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    async void ApplyRecommended_Click(object sender, RoutedEventArgs e)
    {
        await TweakRunner.ApplyRecommended();
        _view?.Refresh();
        UpdateCount();
    }
}
