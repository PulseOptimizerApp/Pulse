using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Pulse.Core;

namespace Pulse.Pages;

public partial class SafetyPage : UserControl, IPage
{
    public SafetyPage() => InitializeComponent();

    public void OnShown(object parameter) => Render();

    void NoPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (NoPromptSwitch.IsChecked == true)
        {
            if (!Elevation.Enable()) { NoPromptSwitch.IsChecked = false; MainWindow.Instance.Toast("Windows didn't allow it. Pulse will keep asking.", ToastKind.Warning); return; }
            TweakEngine.AddLog("app", "Opens without the admin prompt");
        }
        else { Elevation.Disable(); TweakEngine.AddLog("app", "Asks for admin rights again"); }
    }

    void Render()
    {
        var st = TweakEngine.State;
        RestoreInfo.Text = st.LastRestorePoint is DateTime d
            ? $"Pulse last created one {Format.Ago(d)} ({d:MMM d, h:mm tt}). Open System Restore to roll back to it or any other point."
            : "Pulse hasn't created one yet. Windows may have its own restore points; open System Restore to see them.";
        AutoRp.IsChecked = st.Settings.AutoRestorePoint;
        NoPromptSwitch.IsChecked = Elevation.IsOn();

        var applied = TweakCatalog.All.Where(t => st.Applied.ContainsKey(t.Id)).ToList();
        AppliedList.ItemsSource = applied;
        NoneApplied.Visibility = applied.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoAllBtn.IsEnabled = applied.Count > 0;
        LogList.ItemsSource = st.Log.Take(100).ToList();
    }

    async void CreateRp_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Instance.ShowBusy("Creating a restore point", "This can take up to a minute.");
        var (ok, msg) = await Task.Run(() => TweakEngine.CreateRestorePoint());
        MainWindow.Instance.HideOverlay();
        if (ok) MainWindow.Instance.Toast("Restore point created.");
        else await MainWindow.Instance.Alert("Couldn't create a restore point", msg + "\n\nSystem Protection may be turned off for your C: drive (System Properties → System Protection).");
        Render();
    }

    void OpenRestore_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rstrui.exe")) { UseShellExecute = true });

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(TweakEngine.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TweakEngine.DataDir}\"") { UseShellExecute = true });
    }

    void AutoRp_Click(object sender, RoutedEventArgs e)
    {
        TweakEngine.State.Settings.AutoRestorePoint = AutoRp.IsChecked == true;
        TweakEngine.Save();
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var t = (Tweak)((FrameworkElement)sender).DataContext;
        await TweakRunner.Set(t, false);
        Render();
    }

    async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        var applied = TweakCatalog.All.Where(t => TweakEngine.AppliedByPulse(t)).ToList();
        if (!await MainWindow.Instance.Confirm($"Undo all {applied.Count} tweaks?", "Every setting goes back to exactly what it was before Pulse changed it.", "Undo everything"))
            return;
        MainWindow.Instance.ShowBusy("Undoing tweaks", "Restoring each saved value.");
        foreach (var t in applied)
        {
            MainWindow.Instance.SetBusyStatus(t.Title + "…");
            try { await Task.Run(() => TweakEngine.Revert(t)); t.IsOn = false; } catch { }
        }
        MainWindow.Instance.HideOverlay();
        MainWindow.Instance.Toast("Everything is back to how it was. Some settings finish restoring after a restart.");
        Render();
    }
}
