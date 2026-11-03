using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Pulse.Core;

namespace Pulse.Pages;

public partial class StartupPage : UserControl, IPage
{
    List<StartupItem> _items = new();

    public StartupPage() => InitializeComponent();

    public async void OnShown(object parameter)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        _items = await Task.Run(() => StartupManager.Load());
        AppList.ItemsSource = _items;
        LoadingPanel.Visibility = Visibility.Collapsed;
        UpdateCount();
    }

    void UpdateCount()
    {
        int on = _items.Count(i => i.Enabled);
        int safe = _items.Count(i => i.Enabled && i.AdviceKind == "Safe");
        CountText.Text = $"{on} of {_items.Count} start with Windows · {safe} of those are safe to turn off";
    }

    async void Switch_Click(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        var item = (StartupItem)cb.DataContext;
        bool want = cb.IsChecked == true;
        item.IsBusy = true;
        try
        {
            await Task.Run(() => StartupManager.SetEnabled(item, want));
            MainWindow.Instance.Toast($"{item.Name} will {(want ? "" : "no longer ")}start with Windows.");
        }
        catch (Exception ex)
        {
            item.Raise(nameof(StartupItem.Enabled));
            await MainWindow.Instance.Alert("Couldn't change that", ex.Message);
        }
        finally
        {
            item.IsBusy = false;
            UpdateCount();
        }
    }

    void Locate_Click(object sender, RoutedEventArgs e)
    {
        var item = (StartupItem)((FrameworkElement)sender).DataContext;
        if (File.Exists(item.ExePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.ExePath}\"") { UseShellExecute = true });
        else
            MainWindow.Instance.Toast("That file no longer exists — this is a leftover entry you can turn off.", ToastKind.Info);
    }
}
