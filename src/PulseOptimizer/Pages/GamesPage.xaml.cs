using System.Windows;
using System.Windows.Controls;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Measurement;
using Pulse.Ui;

namespace Pulse.Pages;

public partial class GamesPage : UserControl, IPage
{
    List<GameItem> _games = new();
    List<PlanStep> _steps = new();
    GameItem _current;
    HardwareFacts _hw;
    MonitorInfo _monitor;
    bool _loaded, _checking;

    public GamesPage()
    {
        InitializeComponent();
        // Esc, Backspace or the mouse's back button leave a game.
        PreviewKeyDown += (_, e) =>
        {
            if (DetailView.Visibility == Visibility.Visible && Picker.Visibility != Visibility.Visible
                && (e.Key == System.Windows.Input.Key.Escape || (e.Key == System.Windows.Input.Key.Back && e.OriginalSource is not TextBox)))
            { Back_Click(null, null); e.Handled = true; }
            else if (Picker.Visibility == Visibility.Visible && e.Key == System.Windows.Input.Key.Escape)
            { Picker.Visibility = Visibility.Collapsed; e.Handled = true; }
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1 && DetailView.Visibility == Visibility.Visible)
            { Back_Click(null, null); e.Handled = true; }
        };
        Loaded += (_, _) => Focus();
        Focusable = true;
    }

    public async void OnShown(object parameter)
    {
        if (!_loaded)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            var gamesTask = LatencyPlan.GamesAsync();
            (_hw, _monitor) = await Task.Run(() =>
            {
                var hw = HardwareFacts.Get();
                var mons = PcDiagnostics.LastMonitors.Count > 0 ? PcDiagnostics.LastMonitors : Displays.Enumerate();
                return (hw, mons.FirstOrDefault(m => m.IsPrimary) ?? mons.FirstOrDefault());
            });
            _games = await gamesTask;
            foreach (var g in _games) UpdateProgress(g);
            ShowGrids();
            LoadingPanel.Visibility = Visibility.Collapsed;
            BuildChips();
            _loaded = true;
        }
        else RefreshRunning();

        // Opening Games from the sidebar (no game asked for) always shows the library, never a game left open.
        if (parameter == null && DetailView.Visibility == Visibility.Visible) Back_Click(null, null);

        if (parameter is string id && _games.FirstOrDefault(g => g.Game.Id == id) is GameItem pick) Open(pick);
    }

    void RefreshRunning()
    {
        var running = LatencyPlan.RunningExes();
        foreach (var g in _games) g.Running = g.Game.Exes.Any(running.Contains);
    }

    void UpdateProgress(GameItem g)
    {
        var steps = LatencyPlan.Build(g.Game, _hw, _monitor);
        g.Total = steps.Count;
        g.Done = steps.Count(s => s.Done);
    }

    // ───────── Library ─────────

    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameItem g) return;
        if (g.Game.IsAddTile) OpenPicker();
        else Open(g);
    }

    // ───────── Add a game ─────────

    public record Candidate(string Name, string Exe, string Where, System.Windows.Media.ImageSource Icon);
    List<Candidate> _candidates = new();

    static readonly System.Text.RegularExpressions.Regex NotTheGame = new(
        @"unins|setup|install|redist|vc_?redist|dxsetup|crash|report|helper|update|patch|prereq|easyanticheat|eac_|battleye|be_service|cef|webhelper|launcher|bootstrap|dotnet|directx|ue4prereq|uploader|benchmark|server",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The game's main .exe in its folder: the biggest one that isn't an installer, launcher or crash reporter.</summary>
    static string MainExe(string folder)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(folder, "*.exe", opts)
                .Where(f => !NotTheGame.IsMatch(Path.GetFileNameWithoutExtension(f)))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { return null; }
    }

    async void OpenPicker()
    {
        Picker.Visibility = Visibility.Visible;
        Anim.Play((FrameworkElement)Picker.Children[0]);
        PickerSearch.Text = "";
        PickerList.ItemsSource = null;
        PickerEmpty.Visibility = Visibility.Collapsed;
        PickerLoading.Visibility = Visibility.Visible;
        var have = _games.Where(g => g.Installed).Select(g => g.Game).ToList();
        _candidates = await Task.Run(() => PcDiagnostics.LauncherGames()
            .Where(lg => !have.Any(k => k.IsNamed(lg.Name) || k.Name.Equals(lg.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(lg => !lg.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) && !lg.Name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase))
            .Select(lg => (lg, exe: MainExe(lg.Path)))
            .Where(x => x.exe != null)
            .Select(x => new Candidate(x.lg.Name, x.exe, (x.lg.Path.Contains("steamapps", StringComparison.OrdinalIgnoreCase) ? "Steam · " : "Epic Games · ") + x.lg.Path, Native.LargeIcon(x.exe, 64)))
            .OrderBy(c => c.Name).ToList());
        PickerLoading.Visibility = Visibility.Collapsed;
        ShowCandidates();
    }

    void ShowCandidates()
    {
        var q = PickerSearch.Text.Trim();
        var list = q.Length == 0 ? _candidates : _candidates.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        PickerList.ItemsSource = list;
        PickerEmpty.Visibility = list.Count == 0 && PickerLoading.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    void PickerSearch_Changed(object sender, TextChangedEventArgs e) => ShowCandidates();
    void PickerClose_Click(object sender, RoutedEventArgs e) => Picker.Visibility = Visibility.Collapsed;

    void PickerRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Candidate c) AddGame(c.Name, c.Exe);
    }

    void PickerBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick the game's .exe", Filter = "Games (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(dlg.FileName);
        var name = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : Path.GetFileNameWithoutExtension(dlg.FileName);
        AddGame(name, dlg.FileName);
    }

    async void AddGame(string name, string exe)
    {
        var st = TweakEngine.State;
        if (st.CustomGames.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase)))
        {
            MainWindow.Instance.Toast($"{name} is already in your games.", ToastKind.Info);
            return;
        }
        st.CustomGames.Add(new CustomGame { Name = name, Exe = exe });
        TweakEngine.AddLog("games", $"Added {name} to your games");
        Picker.Visibility = Visibility.Collapsed;
        await ReloadGames();
        MainWindow.Instance.Toast($"Added {name}.");
        if (_games.FirstOrDefault(g => g.Game.IsCustom && g.Name == name) is GameItem added) Open(added);
    }

    async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var g = _current;
        if (g?.Custom == null) return;
        if (!await MainWindow.Instance.Confirm($"Remove {g.Name} from your games?", "Only Pulse's list changes. The game itself stays installed.", "Remove")) return;
        TweakEngine.State.CustomGames.RemoveAll(c => c.Name == g.Custom.Name && c.Exe == g.Custom.Exe);
        TweakEngine.State.GameChecklist.Remove(g.Game.Id);
        TweakEngine.Save();
        await ReloadGames();
        Back_Click(null, null);
    }

    async Task ReloadGames()
    {
        _games = await LatencyPlan.GamesAsync(refresh: true);
        foreach (var g in _games) UpdateProgress(g);
        ShowGrids();
    }

    /// <summary>Your games (installed, added, running) first; the rest of the supported games below, folded away.</summary>
    void ShowGrids()
    {
        GameGrid.ItemsSource = _games.Where(g => g.Installed || g.Running || g.Game.IsAddTile || g.Game.IsCustom).ToList();
        var rest = _games.Where(g => !(g.Installed || g.Running || g.Game.IsAddTile || g.Game.IsCustom)).OrderBy(g => g.Name).ToList();
        OtherGrid.ItemsSource = rest;
        OtherCount.Text = $"{rest.Count} supported games that aren't on this PC. Each has a checklist and the settings pros use.";
        OtherHeader.Visibility = rest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OtherToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = OtherToggle.IsChecked == true;
        OtherGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OtherToggle.Content = show ? "Hide" : "Show";
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        DetailView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        Anim.Play(LibraryView);
    }

    void BuildChips()
    {
        GenreChips.Children.Clear();
        var labels = new List<(string key, string text)> { ("foryou", "For you"), ("free", "Free to play") };
        labels.AddRange(Suggestions.Genres.Select(g => (g, char.ToUpperInvariant(g[0]) + g[1..])));
        foreach (var (key, text) in labels)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = text, Tag = key, GroupName = "Genre" };
            chip.Checked += (_, _) => ShowSuggestions(key);
            GenreChips.Children.Add(chip);
        }
        ((RadioButton)GenreChips.Children[0]).IsChecked = true;
    }

    List<string> InstalledNames()
    {
        var names = PcDiagnostics.LauncherGames().Select(g => g.Name).ToList();
        names.AddRange(_games.Where(g => g.Installed).Select(g => g.Name));
        return names;
    }

    async void ShowSuggestions(string key)
    {
        var games = _games;
        var (list, because) = await Task.Run(() =>
        {
            var names = InstalledNames();
            return key == "foryou"
                ? Suggestions.ForYou(games.Where(g => g.Installed && g.Game.Id != "other").Select(g => g.Game), names)
                : (Suggestions.ForGenre(key, names), "");
        });
        SuggestBecause.Text = key == "foryou"
            ? (because.Length > 0 ? because + ", you might like these." : "Install a game and Pulse will tailor this. Meanwhile, some great free ones:")
            : $"Well-reviewed {(key == "free" ? "free" : key)} games you don't have yet.";
        SuggestList.ItemsSource = list;
    }

    void Suggest_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url) MainWindow.Instance.RunAction("url:" + url);
    }

    // ───────── One game ─────────

    void Open(GameItem g)
    {
        _current = g;
        _steps = LatencyPlan.Build(g.Game, _hw, _monitor);
        StepList.ItemsSource = _steps;
        GameTitle.Text = g.Name;
        RemoveGameBtn.Visibility = g.Custom != null ? Visibility.Visible : Visibility.Collapsed;
        GameStatus.Text = g.Game.Id == "other" ? "General steps that work in most games" : g.Game.IsCustom ? "Added by you · general low-delay checklist" : g.StatusText + (g.HasReflex ? " · NVIDIA Reflex" : "") + (g.HasAntiLag2 ? " · AMD Anti-Lag 2" : "");
        Banner.Background = g.Tint;
        BannerIconBox.Background = g.Tint;
        var art = g.CoverImage ?? g.Icon;
        BannerBlur.Source = art;
        BannerIcon.Source = g.Icon ?? g.CoverImage;
        BannerLetters.Text = art == null ? g.Short : "";
        ReflexPill.Visibility = g.HasReflex ? Visibility.Visible : Visibility.Collapsed;
        AntiLagPill.Visibility = g.HasAntiLag2 ? Visibility.Visible : Visibility.Collapsed;

        var gpu = _hw.Gpu switch { GpuVendor.Nvidia => "NVIDIA graphics card", GpuVendor.Amd => "AMD graphics card", GpuVendor.Intel => "Intel graphics", _ => "graphics" };
        var mon = _monitor != null ? $" and {_monitor.Current.Hz} Hz monitor" : "";
        PlanIntro.Text = g.Game.IsGeneric
            ? $"General steps for {(g.Game.IsCustom ? g.Name : "any game")}, for your {gpu}{mon}. {g.Game.Note}"
            : $"For your {gpu}{mon}. Tick each step as you do it; Pulse remembers your progress.";
        CheckIntro.Text = g.Game.Id == "other"
            ? "Start the game, press Check delay, and play for 30 seconds. Pulse finds the running game itself."
            : $"Start {g.Name}, press Check delay, then play normally for 30 seconds. Do it before and after the checklist to see the difference.";

        ShowLastCheck();
        UpdateBar();
        ShowAuto();
        LibraryView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        DetailScroll.ScrollToTop();
        Anim.Play(PlanPanel);
    }

    void UpdateBar()
    {
        if (_current == null) return;
        _current.Done = _steps.Count(s => s.Done);
        _current.Total = _steps.Count;
        Anim.Bar(PlanBar, _steps.Count == 0 ? 0 : (double)_current.Done / _steps.Count);
    }

    void StepRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Clicks on the step's own link button don't toggle it.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button) return;
        if ((sender as FrameworkElement)?.DataContext is not PlanStep step || !step.CanTick) return;
        step.Done = !step.Done;
        Step_Click(sender, e);
    }

    void Step_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        LatencyPlan.SaveTicks(_current.Game.Id, _steps);
        UpdateBar();
        if (_steps.All(s => s.Done)) MainWindow.Instance.Toast($"Checklist done for {_current.Name}. Check the delay again to see the difference.");
    }

    void StepAction_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.RunAction((sender as FrameworkElement)?.Tag as string);

    // ───────── Apply the pro settings for me ─────────

    void ShowAuto()
    {
        if (_current?.Game.Id == "roblox")
        {
            // Roblox has its own launcher page (frame rate, uncapped FPS, Fast Flags, NVIDIA profile).
            AutoCard.Visibility = Visibility.Visible;
            AutoUndoBtn.Visibility = Visibility.Collapsed;
            AutoBtn.IsEnabled = true;
            AutoTitle.Text = "Uncap and boost Roblox";
            AutoText.Text = "The Roblox page sets an uncapped frame rate, the performance Fast Flags Roblox allows, and NVIDIA settings for Roblox only.";
            AutoBtnText.Text = "Open Roblox page";
            return;
        }
        var p = _current == null ? null : GameConfigs.For(_current.Game.Id);
        if (p == null) { AutoCard.Visibility = Visibility.Collapsed; return; }
        AutoCard.Visibility = Visibility.Visible;
        bool undo = GameConfigs.CanUndo(p.GameId);
        AutoUndoBtn.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        var path = p.FilePath;
        if (path == null)
        {
            AutoTitle.Text = "Apply the pro settings for me";
            AutoText.Text = $"Start {_current.Name} once and close it, so it creates its settings file. Then Pulse can set the pro video settings for you.";
            AutoBtn.IsEnabled = false;
            return;
        }
        AutoBtn.IsEnabled = true;
        AutoTitle.Text = undo ? "Pro settings applied" : "Apply the pro settings for me";
        AutoText.Text = undo
            ? $"Pulse changed {TweakEngine.State.GameConfigBackups[p.GameId].Count} video settings in {_current.Name}. Undo puts your old settings back exactly."
            : $"Pulse writes the video and performance settings from the step below straight into {_current.Name}. Your sensitivity, keybinds and crosshair are never touched.";
        AutoBtnText.Text = undo ? "Check again" : "Apply";
    }

    async void Auto_Click(object sender, RoutedEventArgs e)
    {
        var g = _current;
        if (g?.Game.Id == "roblox") { MainWindow.Instance.Navigate("roblox"); return; }
        var p = GameConfigs.For(g.Game.Id);
        var path = p?.FilePath;
        if (path == null) return;

        if (LatencyPlan.RunningExes().Overlaps(g.Game.Exes))
        {
            await MainWindow.Instance.Alert($"Close {g.Name} first", $"{g.Name} saves its settings when it closes, which would overwrite Pulse's changes. Close it completely, then press Apply again.");
            return;
        }

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't read the settings file", ex.Message); return; }
        var changes = p.Plan(text);
        if (changes.Count == 0)
        {
            await MainWindow.Instance.Alert("Already set", $"{g.Name}'s settings already match the pro settings Pulse can apply." + (p.Note.Length > 0 ? "\n\n" + p.Note : ""));
            return;
        }

        var lines = string.Join("\n", changes.Select(c => $"•  {c.Label}: {c.From} → {c.To}"));
        var ok = await MainWindow.Instance.Confirm($"Change {g.Name}'s video settings?",
            $"This changes {changes.Count} of {g.Name}'s video and performance settings:\n\n{lines}\n\n" +
            "Your sensitivity, keybinds, crosshair, audio and field of view are NOT changed. " +
            "Pulse saves a copy of your current settings first, and Undo puts them back exactly." +
            (p.Note.Length > 0 ? "\n\n" + p.Note : ""), "Change settings");
        if (!ok) return;

        try
        {
            await Task.Run(() => GameConfigs.Apply(p, path, text, changes));
            MainWindow.Instance.Toast($"Applied {changes.Count} pro settings to {g.Name}. Start the game to see them.");
        }
        catch (Exception ex)
        {
            await MainWindow.Instance.Alert("The settings weren't changed", ex.Message + "\n\nNothing was written.");
        }
        ShowAuto();
    }

    async void AutoUndo_Click(object sender, RoutedEventArgs e)
    {
        var g = _current;
        if (LatencyPlan.RunningExes().Overlaps(g.Game.Exes))
        {
            await MainWindow.Instance.Alert($"Close {g.Name} first", "Close the game completely, then press Undo again.");
            return;
        }
        if (!await MainWindow.Instance.Confirm($"Put {g.Name}'s settings back?", "Pulse restores the settings file exactly as it was before it applied the pro settings. Any video changes you made in the game since then are replaced too.", "Restore")) return;
        try
        {
            await Task.Run(() => GameConfigs.Undo(g.Game.Id));
            MainWindow.Instance.Toast($"{g.Name}'s original settings are back.");
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't restore", ex.Message); }
        ShowAuto();
    }

    // ───────── Delay check ─────────

    string FindRunningExe()
    {
        var running = LatencyPlan.RunningExes();
        var exe = _current.Game.Exes.FirstOrDefault(running.Contains);
        if (exe != null) return exe;
        if (_current.Game.Id == "other")
            return GameDetector.Find(PcDiagnostics.GameInstallFolders()).FirstOrDefault()?.Exe;
        return null;
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_checking || _current == null) return;
        if (!PresentMonCapture.Available)
        {
            await MainWindow.Instance.Alert("The delay recorder is missing", "PresentMon wasn't found next to Pulse. Download Pulse again from the official release so the tools folder is included.");
            return;
        }
        var exe = await Task.Run(() => FindRunningExe());
        if (exe == null)
        {
            await MainWindow.Instance.Alert($"Start {(_current.Game.Id == "other" ? "your game" : _current.Name)} first",
                "Pulse records the delay while the game is running. Start it, get into a match or the practice range, then press Check delay again.");
            return;
        }

        _checking = true;
        CheckBtn.IsEnabled = false;
        CheckRunning.Visibility = Visibility.Visible;
        try
        {
            for (int i = 10; i > 0; i--)
            {
                CheckCount.Text = i.ToString();
                CheckStatus.Text = "Switch to the game now. Recording starts in a moment…";
                CheckRing.AnimateTo((10 - i + 1) * 10, 900);
                await Task.Delay(1000);
            }
            CheckCount.Text = "●";
            CheckStatus.Text = "Recording 30 seconds… keep playing normally.";
            CheckRing.Value = 0;
            CheckRing.AnimateTo(100, 30_000);
            var cap = await Task.Run(() => PresentMonCapture.Capture(exe, 30));
            System.Media.SystemSounds.Asterisk.Play();
            if (!cap.Ok)
            {
                await MainWindow.Instance.Alert("The check didn't record", cap.Error);
                return;
            }
            var s = Stats.Summarize(cap.FrameTimesMs, cap.LatenciesMs);
            var check = new LatencyCheck
            {
                Date = DateTime.Now, AvgFps = s.AvgFps, Low1Fps = s.Low1Fps,
                LatencyMs = s.LatencyMedianMs, LatencyKind = cap.LatencyKind,
            };
            var id = _current.Game.Id;
            if (!TweakEngine.State.GameLatency.TryGetValue(id, out var list)) TweakEngine.State.GameLatency[id] = list = new List<LatencyCheck>();
            list.Insert(0, check);
            if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
            TweakEngine.AddLog("games", $"Checked delay in {_current.Name}" + (check.LatencyMs is double l ? $": {l:0.0} ms" : ""));
            ShowLastCheck();
        }
        finally
        {
            _checking = false;
            CheckBtn.IsEnabled = true;
            CheckRunning.Visibility = Visibility.Collapsed;
        }
    }

    void ShowLastCheck()
    {
        var list = TweakEngine.State.GameLatency.TryGetValue(_current.Game.Id, out var l) ? l : null;
        if (list == null || list.Count == 0)
        {
            ResultGrid.Visibility = Visibility.Collapsed;
            CompareText.Visibility = Visibility.Collapsed;
            CheckBtnText.Text = "Check delay";
            return;
        }
        var last = list[0];
        ResultGrid.Visibility = Visibility.Visible;
        CheckBtnText.Text = "Check again";
        DelayValue.Text = last.LatencyMs is double ms ? $"{ms:0.0} ms" : "—";
        DelayKind.Text = last.LatencyMs.HasValue ? KindText(last.LatencyKind) : "This game didn't report delay data; FPS is still useful.";
        FpsValue.Text = $"{last.AvgFps:0}";
        LowValue.Text = $"{last.Low1Fps:0}";

        if (list.Count > 1)
        {
            var first = list[^1];
            var parts = new List<string>();
            if (last.LatencyMs is double a && first.LatencyMs is double b && first.LatencyKind == last.LatencyKind)
                parts.Add(a < b ? $"Delay is {b - a:0.0} ms lower than your first check ({b:0.0} ms)" : $"Delay is {a - b:0.0} ms higher than your first check ({b:0.0} ms)");
            parts.Add($"1% low FPS {(last.Low1Fps >= first.Low1Fps ? "up" : "down")} from {first.Low1Fps:0} to {last.Low1Fps:0}");
            CompareText.Text = string.Join(". ", parts) + $". Single checks vary by a few percent, so look for clear differences.";
            CompareText.Visibility = Visibility.Visible;
        }
        else
        {
            CompareText.Text = $"Checked {Format.Ago(last.Date)}. Follow the checklist, then check again to compare.";
            CompareText.Visibility = Visibility.Visible;
        }
    }

    static string KindText(string kind) => kind switch
    {
        "Input to photon" => "From your input to the frame on screen",
        "Click to photon" => "From a mouse click to the frame on screen",
        "PC latency" => "PC latency, reported by the game (Reflex)",
        _ => "From the start of a frame to it reaching the screen",
    };
}
