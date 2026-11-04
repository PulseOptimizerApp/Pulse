using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        GpuSwitch.IsChecked = S.HighPerformanceGpu;
        GameModeSwitch.IsChecked = S.GameMode;
        AutoCleanSwitch.IsChecked = S.AutoClean;
        ShowPlaces();
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

    async void Gpu_Click(object sender, RoutedEventArgs e)
    {
        bool on = GpuSwitch.IsChecked == true;
        S.HighPerformanceGpu = on;
        try
        {
            await Task.Run(() => RobloxBoost.ApplyGpuPreference(on, RobloxBoost.PlayerVersions()));
            RobloxBoost.Save();
            TweakEngine.AddLog("roblox", on ? "Set Roblox to use the high-performance graphics card" : "Removed Roblox's graphics card preference");
            MainWindow.Instance.Toast(on ? "Roblox will use the graphics card from its next launch." : "Windows picks Roblox's graphics chip again.");
        }
        catch (Exception ex)
        {
            S.HighPerformanceGpu = !on;
            GpuSwitch.IsChecked = !on;
            await MainWindow.Instance.Alert("That didn't work", ex.Message);
        }
    }

    void GameMode_Click(object sender, RoutedEventArgs e)
    {
        S.GameMode = GameModeSwitch.IsChecked == true;
        RobloxBoost.Save();
        if (S.GameMode && !S.LaunchThroughPulse) MainWindow.Instance.Toast("Turn on Launch through Pulse too, so game mode starts when you press Play.", ToastKind.Info);
    }

    void AutoClean_Click(object sender, RoutedEventArgs e)
    {
        S.AutoClean = AutoCleanSwitch.IsChecked == true;
        RobloxBoost.Save();
        if (S.AutoClean && !S.LaunchThroughPulse) MainWindow.Instance.Toast("Turn on Launch through Pulse too, so the cleanup runs before Roblox starts.", ToastKind.Info);
    }

    // ───────── Settings for one game ─────────

    static readonly (string Key, string Label)[] PlaceChoices = { ("", "Same as above"), ("maxfps", "Max FPS"), ("balanced", "Balanced"), ("default", "Roblox default") };

    void ShowPlaces()
    {
        var last = RobloxBoost.LastPlace;
        PlaceText.Text = last == null
            ? Pulse.Ui.Loc.T("Launch a game through Pulse once and it shows up here.")
            : Pulse.Ui.Loc.F("Last game you launched: place {0}", last);
        PlaceChips.Children.Clear();
        if (last != null)
        {
            var current = S.PlacePresets.GetValueOrDefault(last, "");
            foreach (var (key, label) in PlaceChoices)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Pulse.Ui.Loc.T(label), GroupName = "roblox-place", Tag = key, Margin = new Thickness(0, 3, 6, 3), IsChecked = key == current };
                rb.Checked += (s, _) => SetPlace(last, (string)((FrameworkElement)s).Tag);
                PlaceChips.Children.Add(rb);
            }
        }
        PlaceList.Items.Clear();
        foreach (var (place, preset) in S.PlacePresets.Where(p => p.Key != last))
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var remove = new Button { Style = (Style)FindResource("LinkButton"), Content = Pulse.Ui.Loc.T("Remove"), Tag = place };
            remove.Click += (s, _) => SetPlace((string)((FrameworkElement)s).Tag, "");
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = Pulse.Ui.Loc.F("Place {0}: {1}", place, Pulse.Ui.Loc.T(PlaceChoices.FirstOrDefault(c => c.Key == preset).Label ?? preset)),
                Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            });
            PlaceList.Items.Add(row);
        }
    }

    void SetPlace(string place, string preset)
    {
        if (string.IsNullOrEmpty(preset)) S.PlacePresets.Remove(place);
        else S.PlacePresets[place] = preset;
        RobloxBoost.Save();
        Dispatcher.InvokeAsync(ShowPlaces);
    }

    // ───────── Lag check ─────────

    async void Net_Click(object sender, RoutedEventArgs e)
    {
        NetBtn.IsEnabled = false;
        NetText.Text = Pulse.Ui.Loc.T("Checking for about 5 seconds…");
        NetDetail.Text = "";
        try
        {
            var gw = NetworkCheck.Gateway();
            var server = NetworkCheck.RobloxServer(Path.Combine(RobloxUser.LocalAppData, "Roblox", "logs"));
            var routerTask = gw != null ? NetworkCheck.PingMany(gw) : Task.FromResult<PingStats>(null);
            var serverTask = server != null ? NetworkCheck.PingMany(server) : Task.FromResult<PingStats>(null);
            var (router, srv) = (await routerTask, await serverTask);
            bool wifi = NetworkCheck.OnWifi();
            NetText.Text = Pulse.Ui.Loc.T(NetworkCheck.Verdict(router, srv, wifi));
            var parts = new List<string>();
            if (router?.Answered == true) parts.Add(Pulse.Ui.Loc.F("Router{0}: {1} ms, jumps {2} ms, {3}% lost", wifi ? " (Wi-Fi)" : "", Math.Round(router.AvgMs), Math.Round(router.JitterMs), Math.Round(router.LossPercent)));
            if (srv?.Answered == true) parts.Add(Pulse.Ui.Loc.F("Roblox server: {0} ms, jumps {1} ms, {2}% lost", Math.Round(srv.AvgMs), Math.Round(srv.JitterMs), Math.Round(srv.LossPercent)));
            NetDetail.Text = string.Join(" · ", parts);
        }
        catch (Exception ex) { NetText.Text = ex.Message; }
        finally { NetBtn.IsEnabled = true; }
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
        int shared = list.Count == 0 ? 0 : await Task.Run(NvidiaProfile.AppsInRobloxProfile);
        var where = shared > 1
            ? Pulse.Ui.Loc.F("Heads up: NVIDIA's Roblox profile also covers {0} other programs, so these settings apply to them too. Undo puts everything back.", shared - 1)
            : Pulse.Ui.Loc.T("Only Roblox's profile changes. Undo puts the old values back. Takes effect the next time Roblox starts.");
        if (!await MainWindow.Instance.Confirm("Change NVIDIA settings for Roblox?", lines + "\n\n" + where, "Apply")) return;
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
