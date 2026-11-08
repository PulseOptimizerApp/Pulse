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

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool on = StartupSwitch.IsChecked == true;
        var st = TweakEngine.State.Settings;
        st.StartWithWindows = on;
        if (!Pulse.Ui.ClipsService.SetStartWithWindows(on))
        {
            st.StartWithWindows = false;
            StartupSwitch.IsChecked = false;
            MainWindow.Instance.Toast("Windows didn't allow it.", ToastKind.Warning);
        }
        else TweakEngine.AddLog("app", on ? "Starts with Windows" : "Doesn't start with Windows");
        TweakEngine.Save();
        TrayRow.Visibility = st.StartWithWindows ? Visibility.Visible : Visibility.Collapsed;
    }

    void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Pulse.Ui.Loc.T("Save settings to a file"), FileName = $"Pulse settings {DateTime.Now:yyyy-MM-dd}{SettingsBackup.Extension}",
            Filter = Pulse.Ui.Loc.T("Pulse settings") + $" (*{SettingsBackup.Extension})|*{SettingsBackup.Extension}", DefaultExt = SettingsBackup.Extension,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            SettingsBackup.Export(dlg.FileName);
            MainWindow.Instance.Toast(Pulse.Ui.Loc.F("Saved {0}. Open Pulse on the other PC and choose Restore from a file.", System.IO.Path.GetFileName(dlg.FileName)));
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Warning); }
    }

    async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Pulse.Ui.Loc.T("Restore from a file"), Filter = Pulse.Ui.Loc.T("Pulse settings") + $" (*{SettingsBackup.Extension})|*{SettingsBackup.Extension}", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        var info = SettingsBackup.Peek(dlg.FileName);
        if (info == null) { MainWindow.Instance.Toast("That file isn't a Pulse settings backup.", ToastKind.Warning); return; }
        var (version, created, crosshairs, macros) = info.Value;
        if (!await MainWindow.Instance.Confirm(Pulse.Ui.Loc.T("Restore these settings?"),
                Pulse.Ui.Loc.F("Saved {0} from Pulse {1}: {2} crosshairs, {3} macros. They replace the settings here; this PC's tweaks and undo history stay as they are. Pulse restarts to finish.",
                    created == DateTime.MinValue ? "?" : created.ToString("g"), version, crosshairs, macros),
                Pulse.Ui.Loc.T("Restore and restart"), Pulse.Ui.Loc.T("Cancel"))) return;
        var problem = SettingsBackup.Import(dlg.FileName);
        if (problem != null) { MainWindow.Instance.Toast(problem, ToastKind.Warning); return; }
        if (TweakEngine.State.Settings.StartWithWindows) Pulse.Ui.ClipsService.SetStartWithWindows(true);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
            MainWindow.Instance.PrepareForExit();
            MainWindow.Instance.QuitApp();
        }
        catch { MainWindow.Instance.Toast("Restored. Close and reopen Pulse to finish."); }
    }

    void Tray_Click(object sender, RoutedEventArgs e)
    {
        TweakEngine.State.Settings.StartInTray = TraySwitch.IsChecked == true;
        TweakEngine.Save();
    }

    void Render()
    {
        var st = TweakEngine.State;
        RestoreInfo.Text = st.LastRestorePoint is DateTime d
            ? $"Pulse last created one {Format.Ago(d)} ({d:MMM d, h:mm tt}). Open System Restore to roll back to it or any other point."
            : "Pulse hasn't created one yet. Windows may have its own restore points; open System Restore to see them.";
        AutoRp.IsChecked = st.Settings.AutoRestorePoint;
        NoPromptSwitch.IsChecked = Elevation.IsOn();
        StartupSwitch.IsChecked = st.Settings.StartWithWindows;
        TraySwitch.IsChecked = st.Settings.StartInTray;
        TrayRow.Visibility = st.Settings.StartWithWindows ? Visibility.Visible : Visibility.Collapsed;

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
