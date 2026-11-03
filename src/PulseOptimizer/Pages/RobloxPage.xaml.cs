using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Pulse.Core;
using Pulse.Core.Roblox;

namespace Pulse.Pages;

public partial class RobloxPage : UserControl, IPage
{
    readonly List<RobloxOption> _options = RobloxBoost.CreateOptions();
    bool _loading;

    public RobloxPage()
    {
        InitializeComponent();
        OptionList.ItemsSource = _options;
        FlagName.ItemsSource = RobloxBoost.Allowlist.OrderBy(f => f).ToList();
    }

    RobloxSettings S => RobloxBoost.Settings;

    int _hz;

    public async void OnShown(object parameter)
    {
        _ = ShowNvidia();
        _loading = true;
        if (S.Choices.Count == 0) S.Choices = new(RobloxBoost.Presets["balanced"]);
        foreach (var o in _options) o.Value = S.Choices.GetValueOrDefault(o.Key, "");
        MarkPreset();
        LaunchSwitch.IsChecked = S.LaunchThroughPulse && RobloxBoost.HandlerIsPulse();
        _loading = false;
        ShowCustom();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var versions = RobloxBoost.PlayerVersions();
        StatusText.Text = versions.Count == 0
            ? "Roblox isn't installed for this Windows account. Install it from roblox.com, then come back."
            : (S.LaunchThroughPulse ? "On" : "Off") + $" · Roblox {Path.GetFileName(versions[0]).Replace("version-", "")}"
              + (S.LastLaunch is DateTime t ? $" · last launched {Format.Ago(t)}" : "");
        var flags = RobloxBoost.BuildFlags(_options, S.CustomFlags);
        var fps = _options.First(o => o.Key == "fps").Value;
        var fpsText = fps == RobloxBoost.UncappedValue ? " and an uncapped frame rate (Roblox's settings file stays locked, so in-game settings changes won't save)"
                    : fps.Length > 0 ? $" and a {fps} FPS cap" : "";
        FlagSummary.Text = $"{flags.Count} Fast Flag{(flags.Count == 1 ? "" : "s")}{fpsText} will be applied.";
        ApplyBtn.IsEnabled = versions.Count > 0;
    }

    void MarkPreset()
    {
        string match = RobloxBoost.Presets.FirstOrDefault(p => p.Value.All(kv => S.Choices.GetValueOrDefault(kv.Key, "") == kv.Value)).Key;
        PresetMax.IsChecked = match == "maxfps";
        PresetBalanced.IsChecked = match == "balanced";
        PresetDefault.IsChecked = match == "default";
    }

    void SaveChoices()
    {
        S.Choices = _options.ToDictionary(o => o.Key, o => o.Value);
        TweakEngine.Save();
        UpdateStatus();
    }

    void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.Tag is not string key) return;
        _loading = true;
        foreach (var o in _options) o.Value = RobloxBoost.Presets[key].GetValueOrDefault(o.Key, "");
        _loading = false;
        S.Preset = key;
        SaveChoices();
    }

    void Choice_Click(object sender, RoutedEventArgs e)
    {
        SaveChoices();
        _loading = true;
        MarkPreset();
        _loading = false;
    }

    async void Launch_Click(object sender, RoutedEventArgs e)
    {
        bool on = LaunchSwitch.IsChecked == true;
        if (on && !RobloxBoost.Installed)
        {
            LaunchSwitch.IsChecked = false;
            await MainWindow.Instance.Alert("Roblox isn't installed", "Install Roblox from roblox.com and play once, then turn this on.");
            return;
        }
        try
        {
            await Task.Run(() => { if (on) { RobloxBoost.EnableLaunch(); RobloxBoost.ApplyAll(); } else RobloxBoost.DisableLaunch(); });
            TweakEngine.Save();
            MainWindow.Instance.Toast(on ? "Roblox now launches through Pulse. Press Play on roblox.com as usual." : "Roblox launches on its own again.");
        }
        catch (Exception ex)
        {
            LaunchSwitch.IsChecked = !on;
            await MainWindow.Instance.Alert("That didn't work", ex.Message);
        }
        UpdateStatus();
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        bool running = Process.GetProcessesByName("RobloxPlayerBeta").Length > 0;
        if (running && !await MainWindow.Instance.Confirm("Roblox is open",
                "Roblox reads these settings when it starts, so they apply from your next launch. Roblox may also overwrite the frame rate cap when it closes. Apply anyway?", "Apply"))
            return;
        int n = await Task.Run(RobloxBoost.ApplyAll);
        TweakEngine.AddLog("roblox", $"Applied Roblox settings to {n} version{(n == 1 ? "" : "s")}");
        MainWindow.Instance.Toast(S.LaunchThroughPulse
            ? "Applied. They'll stay applied after Roblox updates."
            : "Applied. Turn on Launch through Pulse so they survive Roblox updates.");
    }

    void Cache_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("cleanup");

    // ───────── NVIDIA profile (Roblox only) ─────────

    async Task ShowNvidia()
    {
        var (ok, hz) = await Task.Run(() =>
        {
            int h = 0;
            try { h = Displays.Enumerate().FirstOrDefault(m => m.IsPrimary)?.Current.Hz ?? 0; } catch { }
            return (NvidiaProfile.Available, h);
        });
        _hz = hz;
        NvCard.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        NvCapText.Text = hz >= 100
            ? $"Max Frame Rate: {Math.Min(hz - 3, 237)} FPS (just under your {hz} Hz, keeps G-SYNC/FreeSync in range; leave off if you don't use it)"
            : "Max Frame Rate (only useful with a 100 Hz+ monitor and G-SYNC)";
        NvCap.IsEnabled = hz >= 100;
        bool applied = S.NvidiaBackups.Count > 0;
        NvOnPill.Visibility = NvUndoBtn.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
    }

    async void NvApply_Click(object sender, RoutedEventArgs e)
    {
        var list = new List<NvSetting>();
        if (NvLowLatency.IsChecked == true) list.Add(new(NvidiaProfile.PRERENDERLIMIT_ID, "Low Latency Mode", 1, "On"));
        if (NvMaxPerf.IsChecked == true) list.Add(new(NvidiaProfile.PREFERRED_PSTATE_ID, "Power management mode", 1, "Prefer maximum performance"));
        if (NvVsyncOff.IsChecked == true) list.Add(new(NvidiaProfile.VSYNCMODE_ID, "Vertical sync", NvidiaProfile.VSYNCMODE_FORCEOFF, "Force off"));
        if (NvCap.IsChecked == true && _hz >= 100) list.Add(new(NvidiaProfile.FRL_FPS_ID, "Max Frame Rate", (uint)Math.Min(_hz - 3, 237), $"{Math.Min(_hz - 3, 237)} FPS"));
        if (list.Count == 0) return;

        var lines = string.Join("\n", list.Select(x => $"•  {x.Name}: {x.ValueText}"));
        if (!await MainWindow.Instance.Confirm("Change NVIDIA settings for Roblox?",
                lines + "\n\nOnly Roblox's profile changes; your other games and global settings stay as they are. Undo puts the old values back. Takes effect the next time Roblox starts.", "Apply")) return;
        try
        {
            if (S.NvidiaBackups.Count > 0) await Task.Run(() => NvidiaProfile.Undo(S.NvidiaBackups));
            S.NvidiaBackups = await Task.Run(() => NvidiaProfile.Apply(list));
            TweakEngine.AddLog("roblox", $"Applied {list.Count} NVIDIA settings to Roblox's profile");
            MainWindow.Instance.Toast("NVIDIA settings applied to Roblox. Restart Roblox if it's open.");
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("NVIDIA settings weren't changed", ex.Message); }
        await ShowNvidia();
    }

    async void NvUndo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => NvidiaProfile.Undo(S.NvidiaBackups));
            S.NvidiaBackups.Clear();
            TweakEngine.AddLog("undo", "Restored Roblox's NVIDIA settings");
            MainWindow.Instance.Toast("Roblox's NVIDIA settings are back to how they were.");
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't restore", ex.Message); }
        await ShowNvidia();
    }

    // ───────── Fast Flag editor ─────────

    void EditorToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = EditorToggle.IsChecked == true;
        EditorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EditorToggle.Content = show ? "Hide" : "Show";
    }

    void ShowCustom()
    {
        CustomList.ItemsSource = S.CustomFlags.OrderBy(kv => kv.Key).ToList();
    }

    void AddFlag_Click(object sender, RoutedEventArgs e)
    {
        var name = FlagName.Text?.Trim() ?? "";
        var value = FlagValue.Text?.Trim() ?? "";
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) value = "True";
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) value = "False";
        if (!RobloxBoost.IsValidCustom(name, value, out var why))
        {
            FlagError.Text = why;
            FlagError.Visibility = Visibility.Visible;
            return;
        }
        FlagError.Visibility = Visibility.Collapsed;
        S.CustomFlags[name] = value;
        TweakEngine.Save();
        FlagName.Text = ""; FlagValue.Text = "";
        ShowCustom();
        UpdateStatus();
    }

    void RemoveFlag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        S.CustomFlags.Remove(key);
        TweakEngine.Save();
        ShowCustom();
        UpdateStatus();
    }
}
