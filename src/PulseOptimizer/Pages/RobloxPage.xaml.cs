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
        var other = RobloxBoost.ForeignLauncher();
        StatusText.Text = versions.Count == 0
            ? Pulse.Ui.Loc.T(RobloxBoost.WhyNotFound())
            : (S.LaunchThroughPulse ? "On" : "Off") + $" · Roblox {Path.GetFileName(versions[0]).Replace("version-", "")}"
              + (S.LastLaunch is DateTime t ? $" · last launched {Format.Ago(t)}" : "")
              + (!S.LaunchThroughPulse && other != null ? Pulse.Ui.Loc.F(" · Play currently opens {0}", other) : "");
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
        RobloxBoost.Save();
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
        if (on && !await Task.Run(() => RobloxBoost.Installed))
        {
            LaunchSwitch.IsChecked = false;
            await MainWindow.Instance.Alert("Pulse can't find Roblox", RobloxBoost.WhyNotFound());
            UpdateStatus();
            return;
        }
        var other = on ? RobloxBoost.ForeignLauncher() : null;
        if (other != null && !await MainWindow.Instance.Confirm(Pulse.Ui.Loc.F("{0} has Roblox's Play button", other),
                Pulse.Ui.Loc.F("{0} rewrites Roblox's settings every time it starts Roblox, so Pulse's settings can't work alongside it. Pulse can take over the Play button and start Roblox itself; turning this off later gives the Play button back to {0}.", other), "Use Pulse"))
        {
            LaunchSwitch.IsChecked = false;
            return;
        }
        try
        {
            await Task.Run(() => { if (on) { RobloxBoost.EnableLaunch(); RobloxBoost.ApplyAll(); } else RobloxBoost.DisableLaunch(); });
            RobloxBoost.Save();
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

    List<NvOption> _nv = new();
    bool _nvLoading;

    async Task ShowNvidia()
    {
        var (ok, hz) = await Task.Run(() =>
        {
            int h = 0;
            try { h = Displays.Enumerate().FirstOrDefault(m => m.IsPrimary)?.Current.Hz ?? 0; } catch { }
            return (NvidiaProfile.Available, h);
        });
        _hz = hz;
        // Always show the card, so people without an NVIDIA card see why it does nothing for them.
        NvBody.Visibility = NvPresets.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        NvNoCard.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (!ok) return;

        _nvLoading = true;
        _nv = NvidiaCatalog.CreateOptions(hz);
        if (S.NvidiaChoices.Count == 0 && S.NvidiaBackups.Count == 0 && S.NvidiaImport == null) S.NvidiaChoices = new(NvidiaCatalog.Presets["lowlatency"]);
        foreach (var o in _nv) o.Value = S.NvidiaChoices.GetValueOrDefault(o.Key, "");
        NvList.ItemsSource = _nv.Where(o => o.Main).ToList();
        NvMoreList.ItemsSource = _nv.Where(o => !o.Main).ToList();
        MarkNvPreset();
        _nvLoading = false;
        ShowNvCustom();
        ShowNvImport();

        try
        {
            var ids = _nv.Select(o => o.Id).ToList();
            var current = await Task.Run(() => NvidiaProfile.ReadCurrent(ids));
            NvidiaCatalog.ShowCurrent(_nv, current);
            NvReadError.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            NvReadError.Text = Pulse.Ui.Loc.T("Couldn't read Roblox's NVIDIA profile:") + " " + ex.Message;
            NvReadError.Visibility = Visibility.Visible;
        }
        bool applied = S.NvidiaBackups.Count > 0;
        NvOnPill.Visibility = NvUndoBtn.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        UpdateNvSummary();
    }

    List<NvSetting> NvPlan() => NvidiaCatalog.Build(_nv, S.NvidiaCustom, S.NvidiaImport);

    void MarkNvPreset()
    {
        var mine = _nv.Where(o => o.Value.Length > 0).ToDictionary(o => o.Key, o => o.Value);
        string match = S.NvidiaImport != null ? null
            : NvidiaCatalog.Presets.FirstOrDefault(p => p.Value.Count == mine.Count && p.Value.All(kv => mine.GetValueOrDefault(kv.Key) == kv.Value)).Key;
        NvPresetLow.IsChecked = match == "lowlatency";
        NvPresetNone.IsChecked = match == "none";
    }

    void UpdateNvSummary()
    {
        int n = NvPlan().Count;
        NvSummary.Text = n == 0 ? Pulse.Ui.Loc.T("Nothing will change in Roblox's NVIDIA profile.")
            : n == 1 ? Pulse.Ui.Loc.T("1 driver setting will be set for Roblox.")
            : Pulse.Ui.Loc.F("{0} driver settings will be set for Roblox.", n);
    }

    void SaveNvChoices()
    {
        S.NvidiaChoices = _nv.ToDictionary(o => o.Key, o => o.Value);
        RobloxBoost.Save();
        UpdateNvSummary();
    }

    void NvPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (_nvLoading || (sender as FrameworkElement)?.Tag is not string key) return;
        _nvLoading = true;
        foreach (var o in _nv) o.Value = NvidiaCatalog.Presets[key].GetValueOrDefault(o.Key, "");
        _nvLoading = false;
        S.NvidiaImport = null; // a preset replaces an imported file
        ShowNvImport();
        SaveNvChoices();
    }

    void NvChoice_Click(object sender, RoutedEventArgs e)
    {
        SaveNvChoices();
        _nvLoading = true;
        MarkNvPreset();
        _nvLoading = false;
    }

    void NvMoreToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = NvMoreToggle.IsChecked == true;
        NvMorePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NvMoreToggle.Content = Pulse.Ui.Loc.T(show ? "Fewer settings" : "More settings");
    }

    async void NvApply_Click(object sender, RoutedEventArgs e)
    {
        var list = NvPlan();
        if (list.Count == 0 && S.NvidiaBackups.Count == 0) { MainWindow.Instance.Toast("Pick at least one NVIDIA setting first."); return; }

        // Short list: the rows people recognise, then a count for the rest (an imported file can hold dozens).
        var shown = list.Where(x => !x.Name.StartsWith("Ultra Low") && !x.Name.StartsWith("Max pre-rendered")).ToList();
        var lines = list.Count == 0 ? Pulse.Ui.Loc.T("•  Nothing: Pulse's earlier changes are put back.")
            : string.Join("\n", shown.Take(6).Select(x => $"•  {Pulse.Ui.Loc.T(x.Name)}"))
              + (shown.Count > 6 ? "\n" + Pulse.Ui.Loc.F("•  and {0} more", shown.Count - 6) : "");
        if (!await MainWindow.Instance.Confirm("Change NVIDIA settings for Roblox?",
                lines + "\n\n" + Pulse.Ui.Loc.T("Only Roblox's profile changes. Undo puts the old values back. Takes effect the next time Roblox starts."), "Apply")) return;
        await ApplyNv(list);
    }

    async Task ApplyNv(List<NvSetting> list)
    {
        try
        {
            if (S.NvidiaBackups.Count > 0) { await Task.Run(() => NvidiaProfile.Undo(S.NvidiaBackups)); S.NvidiaBackups.Clear(); }
            if (list.Count > 0) S.NvidiaBackups = await Task.Run(() => NvidiaProfile.Apply(list));
            RobloxBoost.Save();
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
            RobloxBoost.Save();
            TweakEngine.AddLog("undo", "Restored Roblox's NVIDIA settings");
            MainWindow.Instance.Toast("Roblox's NVIDIA settings are back to how they were.");
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't restore", ex.Message); }
        await ShowNvidia();
    }

    // ───────── Import an NVIDIA Profile Inspector file ─────────

    async void NvImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Pulse.Ui.Loc.T("Import an NVIDIA Profile Inspector file"),
            Filter = Pulse.Ui.Loc.T("NVIDIA profile") + " (*.nip)|*.nip|" + Pulse.Ui.Loc.T("All files") + " (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        NipReadout nip;
        try { nip = await Task.Run(() => NipFile.Read(dlg.FileName)); }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't read that file", ex is FormatException ? ex.Message : Pulse.Ui.Loc.T("This isn't an NVIDIA Profile Inspector file (.nip).")); return; }
        if (nip.Use.Count == 0) { await MainWindow.Instance.Alert("Nothing to use in that file", "Every setting in it is hardware-specific or applies to the whole PC, so there's nothing to set for Roblox."); return; }

        var file = Path.GetFileName(dlg.FileName);
        var text = Pulse.Ui.Loc.F("Pulse will use {0} settings from {1}, for Roblox only.", nip.Use.Count, file);
        if (!nip.ForRoblox && nip.ProfileName.Length > 0)
            text += "\n\n" + Pulse.Ui.Loc.F("The file was made for NVIDIA's \"{0}\" profile. Pulse applies it to Roblox only, so your other games don't change.", nip.ProfileName);
        if (nip.Skipped > 0)
            text += "\n\n" + Pulse.Ui.Loc.F("Left out {0} that don't belong in one game's profile: SLI, Optimus and 3D Vision switches, settings for the whole PC, and unnamed IDs.", nip.Skipped);
        text += "\n\n" + Pulse.Ui.Loc.T("Undo puts Roblox's old values back.");
        if (!await MainWindow.Instance.Confirm(Pulse.Ui.Loc.F("Use {0}?", file), text, "Apply now")) return;

        S.NvidiaImport = new NvImport { FileName = file, Settings = nip.Use };
        // Rows the file covers step aside, so the file's values are the ones written.
        var ids = nip.Use.Select(x => x.Id).ToHashSet();
        _nvLoading = true;
        foreach (var o in _nv)
            if (o.Choices.Skip(1).Any(c => o.Writes(c.Value).Any(w => ids.Contains(w.Id)))) o.Value = "";
        _nvLoading = false;
        S.NvidiaChoices = _nv.ToDictionary(o => o.Key, o => o.Value);
        await ApplyNv(NvPlan());
    }

    void ShowNvImport()
    {
        var imp = S.NvidiaImport;
        NvImportBar.Visibility = imp == null ? Visibility.Collapsed : Visibility.Visible;
        if (imp != null) NvImportText.Text = Pulse.Ui.Loc.F("Using {0}: {1} settings from NVIDIA Profile Inspector.", imp.FileName, imp.Settings.Count);
    }

    void NvRemoveImport_Click(object sender, RoutedEventArgs e)
    {
        S.NvidiaImport = null;
        RobloxBoost.Save();
        ShowNvImport();
        MarkNvPreset();
        UpdateNvSummary();
        MainWindow.Instance.Toast("Removed. Press Apply to put Roblox's old NVIDIA values back.");
    }

    void ShowNvCustom() => NvCustomList.ItemsSource = S.NvidiaCustom.OrderBy(kv => kv.Key).ToList();

    void NvAddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (!NvidiaCatalog.TryParseCustom(NvCustomId.Text, NvCustomValue.Text, out var id, out var value, out var why))
        {
            NvCustomError.Text = Pulse.Ui.Loc.T(why);
            NvCustomError.Visibility = Visibility.Visible;
            return;
        }
        NvCustomError.Visibility = Visibility.Collapsed;
        S.NvidiaCustom[$"0x{id:X8}"] = $"0x{value:X8}";
        RobloxBoost.Save();
        NvCustomId.Text = ""; NvCustomValue.Text = "";
        ShowNvCustom();
        UpdateNvSummary();
    }

    void NvRemoveCustom_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        S.NvidiaCustom.Remove(key);
        RobloxBoost.Save();
        ShowNvCustom();
        UpdateNvSummary();
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
        RobloxBoost.Save();
        FlagName.Text = ""; FlagValue.Text = "";
        ShowCustom();
        UpdateStatus();
    }

    void RemoveFlag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        S.CustomFlags.Remove(key);
        RobloxBoost.Save();
        ShowCustom();
        UpdateStatus();
    }
}
