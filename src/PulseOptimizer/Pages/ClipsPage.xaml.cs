using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Clips;
using Pulse.Core.Measurement;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>One card in the clip library.</summary>
public class ClipVm : Observable
{
    public ClipFile Clip { get; init; }
    public ImageSource Image { get; set; }
    public string Info { get; init; }

    bool _selecting, _selected;
    /// <summary>The library is in "select" mode (cards show a check circle).</summary>
    public bool Selecting { get => _selecting; set { if (Set(ref _selecting, value)) { Raise(nameof(SelectVisibility)); Raise(nameof(DeleteVisibility)); } } }
    public bool Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Raise(nameof(CheckVisibility)); Raise(nameof(CheckBrush)); } }
    }
    public Visibility SelectVisibility => _selecting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteVisibility => _selecting ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
    public Brush CheckBrush => _selected ? new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)) : new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
}

public partial class ClipsPage : UserControl, IPage, IReleasable
{
    static ClipSettings S => TweakEngine.State.Clips ??= new ClipSettings();

    readonly DispatcherTimer _usage = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    bool _loading, _capturingHotkey, _dragging, _playing;
    List<Core.Clips.Encoder> _encoders = new();
    ClipVm _open;

    public ClipsPage()
    {
        InitializeComponent();
        _usage.Tick += (_, _) => UpdateUsage();
        _playerTimer.Tick += (_, _) => PlayerTick();
        ClipsService.Changed += OnClipsChanged;
        ClipsService.Saved += OnClipSaved;
        // Leaving the page stops the player too (it used to keep ticking, and playing, in the background).
        Unloaded += (_, _) => { _usage.Stop(); if (PlayerLayer.Visibility == Visibility.Visible && _fs == null) ClosePlayer(); };
        _reload.Tick += (_, _) => { _reload.Stop(); LoadLibrary(); };
        _playerTimer.Interval = TimeSpan.FromMilliseconds(33);
        Timeline.Seek += Timeline_Seek;
        Timeline.SelectionChanged += UpdateTimes;
        Timeline.PreviewMouseLeftButtonUp += Timeline_MouseUp;
        PlayerLayer.PreviewKeyDown += Player_KeyDown;
    }

    void OnClipsChanged() => Dispatcher.InvokeAsync(UpdateStatus);
    void OnClipSaved(string _) => Dispatcher.InvokeAsync(LoadLibrary);

    /// <summary>Pulse dropped this screen (hidden in the tray): let go of everything that would keep it alive.</summary>
    public void Release()
    {
        ClipsService.Changed -= OnClipsChanged;
        ClipsService.Saved -= OnClipSaved;
        try { ClosePlayer(); } catch { }
        _usage.Stop(); _reload.Stop(); _fsIdle.Stop();
        _watcher?.Dispose(); _watcher = null;
        _allClips = new();
        ClipGrid.ItemsSource = null;
    }

    public async void OnShown(object parameter)
    {
        _usage.Start();
        if (parameter as string == "newest") { _settingsOpen = false; Scroll?.ScrollToTop(); }
        UpdateStatus();
        LoadLibrary(); // your clips first; the encoder check below can take a moment
        WatchFolder();
        BuildChips();
        await LoadEncoders();
        ShowSettings();
        UpdateStatus();
    }

    // Clips saved while the page is open, by a second copy, or moved into the folder by hand show up on their own.
    FileSystemWatcher _watcher;
    readonly System.Windows.Threading.DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(700) };

    void WatchFolder()
    {
        var folder = S.ClipsFolder;
        if (_watcher?.Path == folder) return;
        _watcher?.Dispose();
        _watcher = null;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            _watcher = new FileSystemWatcher(folder, "*.mp4") { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) => Dispatcher.InvokeAsync(() => { _reload.Stop(); _reload.Start(); });
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Renamed += (s, e) => h(s, e);
        }
        catch { _watcher = null; }
    }

    async Task LoadEncoders()
    {
        if (!Ffmpeg.Installed) return;
        try { _encoders = await Encoders.Available(); } catch { _encoders = new(); }
        BuildCodecChips();
    }

    // ───────── Turning Clips on and off ─────────

    async void Enable_Click(object sender, RoutedEventArgs e)
    {
        EnableBtn.IsEnabled = false;
        SetupPanel.Visibility = Visibility.Visible;
        try
        {
            if (!Ffmpeg.Installed)
            {
                SetupText.Text = Loc.T("Downloading FFmpeg from Pulse's GitHub release…");
                var progress = new Progress<double>(p =>
                {
                    SetupBar.Width = Math.Max(0, ((FrameworkElement)SetupBar.Parent).ActualWidth * p);
                    SetupText.Text = Loc.F("Downloading FFmpeg… {0}%", (int)(p * 100));
                });
                await Ffmpeg.Install(progress, CancellationToken.None);
            }
            SetupText.Text = Loc.T("Checking which video encoders your graphics card has…");
            Encoders.Forget();
            await LoadEncoders();
            if (_encoders.Count == 0) throw new InvalidOperationException(Loc.T("FFmpeg didn't find a video encoder that works on this PC."));
            // Pick the best format this card can do well: H.264 stays the default because it plays everywhere.
            if (!_encoders.Any(x => x.Codec == S.Codec)) S.Codec = "h264";
            S.Enabled = true;
            TweakEngine.Save();
            TweakEngine.AddLog("clips", "Turned on Clips");
            ClipsService.Restart();
            SetupPanel.Visibility = Visibility.Collapsed;
            ShowSettings();
            UpdateStatus();
            LoadLibrary();
            var gpu = Encoders.Pick(_encoders, S.Codec);
            MainWindow.Instance.Toast(gpu is { Hardware: true }
                ? Loc.F("Clips is on. Recording uses your {0} graphics card's encoder.", gpu.Vendor)
                : Loc.T("Clips is on. Your graphics card's encoder wasn't found, so recording uses the processor."));
        }
        catch (Exception ex)
        {
            SetupText.Text = ex.Message;
            await MainWindow.Instance.Alert("Clips couldn't be set up", ex.Message);
        }
        finally { EnableBtn.IsEnabled = true; }
    }

    async void Disable_Click(object sender, RoutedEventArgs e)
    {
        S.Enabled = false;
        TweakEngine.Save();
        ClipsService.Restart();
        if (S.StartWithWindows) { ClipsService.SetStartWithWindows(false); S.StartWithWindows = false; TweakEngine.Save(); }
        TweakEngine.AddLog("clips", "Turned off Clips");
        UpdateStatus();
        if (await MainWindow.Instance.Confirm("Remove the recording engine too?",
                "Clips is off. Your clips stay where they are. You can also delete the FFmpeg download (about 65 MB); Pulse downloads it again if you turn Clips back on.",
                "Remove it", "Keep it"))
        {
            Ffmpeg.Uninstall();
            Encoders.Forget();
            UpdateStatus();
        }
    }

    // ───────── Status ─────────

    void UpdateStatus()
    {
        bool on = S.Enabled && Ffmpeg.Installed;
        PitchCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // Your clips come first; settings open from the Settings button.
        SettingsCard.Visibility = on && _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = on && _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        if (!on) return;

        var hk = ClipsService.HotkeyText(S.HotkeyModifiers, S.HotkeyKey);
        HotkeyBig.Text = hk;
        HotkeyHint.Text = Loc.F("saves the last {0}", LengthText(S.ReplaySeconds));
        Brush dot;
        switch (ClipsService.State)
        {
            case ClipsState.Recording:
                dot = (Brush)FindResource("GoodBrush");
                StatusTitle.Text = ClipsService.Game != null ? Loc.F("Recording {0}", ClipsService.Game) : Loc.T("Recording");
                StatusText.Text = Loc.T("The replay is ready. Press the hotkey any time to save it.");
                break;
            case ClipsState.WaitingForGame:
                dot = (Brush)FindResource("InfoBrush");
                StatusTitle.Text = Loc.T("Ready: waiting for a game");
                StatusText.Text = Loc.T("Recording starts by itself when you play, and stops when the game closes. Nothing runs until then.");
                break;
            case ClipsState.Problem:
                dot = (Brush)FindResource("WarnBrush");
                StatusTitle.Text = Loc.T("Clips needs attention");
                StatusText.Text = Loc.T(ClipsService.Problem);
                break;
            default:
                dot = (Brush)FindResource("TextFaintBrush");
                StatusTitle.Text = Loc.T("Starting…");
                StatusText.Text = "";
                break;
        }
        StatusDot.Fill = dot;
        SaveNowBtn.IsEnabled = ClipsService.State == ClipsState.Recording;
        HotkeyWarn.Text = ClipsService.HotkeyWorks ? "" : Loc.T("Another app already uses this hotkey (often NVIDIA's overlay or Medal). Pick a different one.");
        if (ClipsService.VoiceError != null) VoiceText.Text = Loc.T(ClipsService.VoiceError);
        // Name the exact hotkey another app already has, so it's clear which one to change.
        var taken = new List<string>();
        if (!ClipsService.QuickOk) taken.Add(ClipsService.HotkeyText(S.QuickModifiers, S.QuickKey));
        if (!ClipsService.ShotOk) taken.Add(ClipsService.HotkeyText(S.ScreenshotModifiers, S.ScreenshotKey));
        if (!ClipsService.SessionOk) taken.Add(ClipsService.HotkeyText(S.SessionModifiers, S.SessionKey));
        if (!ClipsService.MarkOk) taken.Add(ClipsService.HotkeyText(S.BookmarkModifiers, S.BookmarkKey));
        ExtraWarn.Text = taken.Count == 0 ? "" : Loc.F("Another app already uses {0}. Click it and pick a different key.", string.Join(", ", taken));
        UpdateUsage();
    }

    void UpdateUsage()
    {
        if (StatusCard.Visibility != Visibility.Visible) return;
        SessionNowText.Text = ClipsService.SessionActive
            ? Loc.F("Stop recording ({0})", TimeSpan.FromSeconds(ClipsService.SessionSeconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture))
            : Loc.T("Start recording");
        SessionNowBtn.IsEnabled = ClipsService.State == ClipsState.Recording;
        CostBtn.IsEnabled = ClipsService.State == ClipsState.Recording && !_costRunning;
        if (ClipsService.State != ClipsState.Recording) { UsageText.Text = Loc.T("Using nothing right now."); return; }
        var enc = ClipsService.Encoder;
        string encText = enc == null ? "" : enc.Hardware ? Loc.F("{0} graphics card encoder ({1})", enc.Vendor, CodecName(enc.Codec)) : Loc.T("processor encoder (no graphics card encoder found)");
        // The whole cost, as Task Manager counts it (Pulse + its recorder), so it can be compared with other clippers.
        UsageText.Text = Loc.F(ClipsService.OnDisk
                ? "{0} s of replay on your drive · Pulse + recorder use {1} of memory · recorder CPU {2}% · {3}"
                : "{0} s of replay in memory · Pulse + recorder use {1} of memory · recorder CPU {2}% · {3}",
            (int)ClipsService.BufferSeconds, Format.Bytes(ClipsService.TotalMemory), ClipsService.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture), encText);
    }

    static string CodecName(string c) => c switch { "hevc" => "HEVC", "av1" => "AV1", _ => "H.264" };
    static string LengthText(int s) => s >= 60 ? Loc.F("{0} min", s / 60) : Loc.F("{0} s", s);

    async void SaveNow_Click(object sender, RoutedEventArgs e)
    {
        SaveNowBtn.IsEnabled = false;
        var path = await ClipsService.SaveClip();
        SaveNowBtn.IsEnabled = ClipsService.State == ClipsState.Recording;
        if (path != null) LoadLibrary();
    }

    // ───────── Settings ─────────

    static readonly (string Label, int Res, int Fps, int Mbps)[] Presets =
    {
        ("Light", 1080, 60, 20), ("Balanced", 0, 60, 40), ("High", 0, 60, 70),
    };

    void BuildChips()
    {
        Fill(LengthChips, "len", new[] { ("15 s", "15"), ("30 s", "30"), ("1 min", "60"), ("2 min", "120"), ("5 min", "300") });
        Fill(QualityChips, "q", Presets.Select(p => (p.Label, p.Label)).ToArray());
        Fill(GameVolChips, "gv", new[] { ("50%", "50"), ("75%", "75"), ("100%", "100"), ("150%", "150") });
        Fill(MicVolChips, "mv", new[] { ("50%", "50"), ("75%", "75"), ("100%", "100"), ("150%", "150") });
        Fill(ResChips, "res", new[] { ("Native", "native"), ("1440p", "1440"), ("1080p", "1080"), ("720p", "720") });
        Fill(FpsChips, "fps", new[] { ("30", "30"), ("60", "60"), ("120", "120"), ("144", "144") });
        Fill(BitrateChips, "br", new[] { ("15 Mbps", "15"), ("25 Mbps", "25"), ("40 Mbps", "40"), ("60 Mbps", "60"), ("80 Mbps", "80"), ("120 Mbps", "120") });
        Fill(QuickLenChips, "qlen", new[] { ("10 s", "10"), ("15 s", "15"), ("30 s", "30") });
        Fill(SyncChips, "sync", new[] { ("100 ms earlier", "-100"), ("50 ms earlier", "-50"), ("In sync", "0"), ("50 ms later", "50"), ("100 ms later", "100") });
        Fill(LimitChips, "lim", new[] { ("10 GB", "10"), ("30 GB", "30"), ("100 GB", "100"), ("No limit", "0") });
        Fill(CaptureChips, "cap", new[] { ("The game", "game"), ("Whole screen", "screen") });
        int monitors = Math.Max(1, MonitorCount());
        Fill(MonitorChips, "mon", Enumerable.Range(0, monitors).Select(i => (Loc.F("Display {0}", i + 1), i.ToString(CultureInfo.InvariantCulture))).ToArray());
        BuildCodecChips();
    }

    void BuildCodecChips()
    {
        var codecs = new List<(string, string)> { ("H.264", "h264") };
        if (_encoders.Any(e => e.Hardware && e.Codec == "hevc")) codecs.Add(("HEVC", "hevc"));
        if (_encoders.Any(e => e.Hardware && e.Codec == "av1")) codecs.Add(("AV1", "av1"));
        Fill(CodecChips, "codec", codecs.ToArray());
        Mark(CodecChips, S.Codec);
    }

    void Fill(WrapPanel panel, string group, (string Label, string Value)[] items)
    {
        panel.Children.Clear();
        foreach (var (label, value) in items)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "clips-" + group, Tag = value, Margin = new Thickness(0, 3, 6, 3) };
            rb.Checked += (s, _) => { if (!_loading) Chip(group, (string)((FrameworkElement)s).Tag); };
            panel.Children.Add(rb);
        }
    }

    static void Mark(WrapPanel panel, string value)
    {
        foreach (var rb in panel.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == value;
    }

    void ShowSettings()
    {
        _loading = true;
        Mark(LengthChips, S.ReplaySeconds.ToString(CultureInfo.InvariantCulture));
        Mark(GameVolChips, S.GameVolume.ToString(CultureInfo.InvariantCulture));
        Mark(MicVolChips, S.MicVolume.ToString(CultureInfo.InvariantCulture));
        Mark(ResChips, S.Resolution);
        Mark(FpsChips, S.Fps.ToString(CultureInfo.InvariantCulture));
        Mark(BitrateChips, S.BitrateMbps.ToString(CultureInfo.InvariantCulture));
        Mark(LimitChips, S.StorageLimitGb.ToString(CultureInfo.InvariantCulture));
        Mark(SyncChips, S.AudioOffsetMs.ToString(CultureInfo.InvariantCulture));
        Mark(MonitorChips, S.Monitor.ToString(CultureInfo.InvariantCulture));
        Mark(CaptureChips, S.Capture);
        CaptureHint.Text = Environment.OSVersion.Version.Build < 22000
            ? Loc.T("Recording just the game's window needs Windows 11. On Windows 10, Pulse records the whole screen.")
            : S.Capture == "game"
                ? Loc.T("Records only the game's window, on whichever screen it's on. Discord and other windows on top stay out of your clips. If a game can't be recorded this way, Pulse records the whole screen instead.")
                : Loc.T("Records everything on the display picked below, including windows on top of the game.");
        Mark(CodecChips, S.Codec);
        var preset = Presets.FirstOrDefault(p => (p.Res == 0 ? "native" : p.Res.ToString(CultureInfo.InvariantCulture)) == S.Resolution && p.Fps == S.Fps && p.Mbps == S.BitrateMbps);
        Mark(QualityChips, preset.Label ?? "");
        // Memory: the replay is kept in RAM, so say what the chosen length actually uses, and warn on small-memory PCs.
        double replayBytes = S.BitrateMbps * 1_000_000.0 / 8 * (S.ReplaySeconds + 2);
        double ramGb = SystemInfo.TotalRam / 1073741824.0;
        QualityText.Text = Loc.F("{0} · {1} FPS · {2} Mbps", S.Resolution == "native" ? Loc.T("Native resolution") : S.Resolution + "p", S.Fps, S.BitrateMbps) + "\n" +
            (S.ReplayOnDisk
                ? Loc.F("Your {1} replay takes about {0} on your drive, so Pulse uses almost no memory for it.", Format.Bytes(replayBytes), LengthText(S.ReplaySeconds))
                : Loc.F("Uses about {0} of memory for your {1} replay. It doesn't cost FPS unless your PC runs low on memory.", Format.Bytes(replayBytes), LengthText(S.ReplaySeconds)));
        bool heavy = !S.ReplayOnDisk && ramGb > 0 && replayBytes > ramGb * 1073741824.0 * (ramGb <= 8.5 ? 0.08 : 0.15);
        QualityText.Foreground = (Brush)FindResource(heavy ? "WarnBrush" : "TextDimBrush");
        if (heavy) QualityText.Text += "\n" + Loc.F("That's a lot for a PC with {0} GB. A shorter replay or Light quality keeps games from running short on memory.", Math.Round(ramGb));
        GameAudioSwitch.IsChecked = S.GameAudio;
        MicSwitch.IsChecked = S.Microphone;
        OnlyGamingSwitch.IsChecked = S.OnlyWhileGaming;
        DiskSwitch.IsChecked = S.ReplayOnDisk;
        TraySwitch.IsChecked = S.KeepRunningInTray;
        StartupSwitch.IsChecked = S.StartWithWindows;
        SeparateMicSwitch.IsChecked = S.SeparateMicTrack;
        AppTracksSwitch.IsChecked = S.AppTracks;
        InputSwitch.IsChecked = S.InputOverlay;
        StretchSwitch.IsChecked = S.StretchToScreen;
        VoiceSwitch.IsChecked = S.VoiceClip;
        VoicePhraseBox.Text = string.IsNullOrWhiteSpace(S.VoicePhrase) ? VoiceClip.DefaultPhrase : S.VoicePhrase;
        AppTracksRow.Visibility = AudioCapture.AppCaptureSupported ? Visibility.Visible : Visibility.Collapsed;
        CursorSwitch.IsChecked = S.RecordCursor;
        SoundSwitch.IsChecked = S.PlaySound;
        PopupSwitch.IsChecked = S.ShowSavedPopup;
        OpenAfterSwitch.IsChecked = S.OpenClipsAfterClip;
        FolderText.Text = S.ClipsFolder;
        HotkeyBtn.Content = ClipsService.HotkeyText(S.HotkeyModifiers, S.HotkeyKey);
        QuickBtn.Content = ClipsService.HotkeyText(S.QuickModifiers, S.QuickKey);
        ShotBtn.Content = ClipsService.HotkeyText(S.ScreenshotModifiers, S.ScreenshotKey);
        SessionBtn.Content = ClipsService.HotkeyText(S.SessionModifiers, S.SessionKey);
        MarkBtn.Content = ClipsService.HotkeyText(S.BookmarkModifiers, S.BookmarkKey);
        SessionSwitch.IsChecked = S.SessionHotkey;
        MarkSwitch.IsChecked = S.BookmarkHotkey;
        QuickSwitch.IsChecked = S.QuickClip;
        ShotSwitch.IsChecked = S.Screenshot;
        Mark(QuickLenChips, S.QuickSeconds.ToString(CultureInfo.InvariantCulture));
        _loading = false;
    }

    void Chip(string group, string v)
    {
        int n = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
        switch (group)
        {
            case "len": S.ReplaySeconds = n; break;
            case "q":
                var preset = Presets.First(x => x.Label == v);
                S.Resolution = preset.Res == 0 ? "native" : preset.Res.ToString(CultureInfo.InvariantCulture);
                S.Fps = preset.Fps; S.BitrateMbps = preset.Mbps;
                break;
            case "gv": S.GameVolume = n; break;
            case "mv": S.MicVolume = n; break;
            case "res": S.Resolution = v; break;
            case "fps": S.Fps = n; break;
            case "br": S.BitrateMbps = n; break;
            case "lim": S.StorageLimitGb = n; break;
            case "sync": S.AudioOffsetMs = n; break;
            case "qlen": S.QuickSeconds = n; break;
            case "mon": S.Monitor = n; break;
            case "cap": S.Capture = v; ShowSettings(); break;
            case "codec": S.Codec = v; break;
        }
        Changed(restart: group is not ("lim" or "qlen"));
    }

    void Setting_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.GameAudio = GameAudioSwitch.IsChecked == true;
        S.Microphone = MicSwitch.IsChecked == true;
        S.OnlyWhileGaming = OnlyGamingSwitch.IsChecked == true;
        S.ReplayOnDisk = DiskSwitch.IsChecked == true;
        S.KeepRunningInTray = TraySwitch.IsChecked == true;
        S.SeparateMicTrack = SeparateMicSwitch.IsChecked == true;
        S.AppTracks = AppTracksSwitch.IsChecked == true;
        S.InputOverlay = InputSwitch.IsChecked == true;
        S.StretchToScreen = StretchSwitch.IsChecked == true;
        S.VoiceClip = VoiceSwitch.IsChecked == true;
        S.RecordCursor = CursorSwitch.IsChecked == true;
        S.PlaySound = SoundSwitch.IsChecked == true;
        S.ShowSavedPopup = PopupSwitch.IsChecked == true;
        S.OpenClipsAfterClip = OpenAfterSwitch.IsChecked == true;
        S.QuickClip = QuickSwitch.IsChecked == true;
        S.Screenshot = ShotSwitch.IsChecked == true;
        S.SessionHotkey = SessionSwitch.IsChecked == true;
        S.BookmarkHotkey = MarkSwitch.IsChecked == true;
        Changed(restart: sender != TraySwitch && sender != SoundSwitch && sender != PopupSwitch);
    }

    void Changed(bool restart)
    {
        TweakEngine.Save();
        if (restart) ClipsService.Restart();
        ShowSettings();
        UpdateStatus();
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool on = StartupSwitch.IsChecked == true;
        if (!ClipsService.SetStartWithWindows(on))
        {
            StartupSwitch.IsChecked = !on;
            MainWindow.Instance.Toast("Windows didn't allow adding Pulse to startup.", ToastKind.Warning);
            return;
        }
        S.StartWithWindows = on;
        if (on) S.KeepRunningInTray = true;
        Changed(restart: false);
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        bool show = MoreToggle.IsChecked == true;
        MorePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MoreToggle.Content = Loc.T(show ? "Fewer settings" : "More settings");
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Pick where clips are saved"), InitialDirectory = S.ClipsFolder };
        if (dlg.ShowDialog() != true) return;
        S.Folder = dlg.FolderName;
        Changed(restart: false);
        LoadLibrary();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(S.ClipsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{S.ClipsFolder}\"") { UseShellExecute = true });
    }

    // Hotkeys: click a button, then press the keys.
    string _hotkeyTarget;

    Button HotkeyButton(string target) => target switch { "quick" => QuickBtn, "shot" => ShotBtn, "session" => SessionBtn, "mark" => MarkBtn, _ => HotkeyBtn };

    void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        _hotkeyTarget = (string)((FrameworkElement)sender).Tag;
        var b = HotkeyButton(_hotkeyTarget);
        b.Content = Loc.T("Press the keys…");
        b.Focus();
    }

    void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { _capturingHotkey = false; ShowSettings(); return; }
        int mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
        // Print Screen (alone or with Alt/Ctrl/Shift) belongs to Windows' Snipping Tool; taking it would break Win's screenshots.
        if (key == Key.PrintScreen) { HotkeyButton(_hotkeyTarget).Content = Loc.T("Print Screen is kept for Windows' Snipping Tool"); return; }
        bool fKey = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !fKey) { HotkeyButton(_hotkeyTarget).Content = Loc.T("Add Ctrl, Alt or Shift (or use an F key)"); return; }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        // One key combination does one thing.
        // One key combination does one thing.
        var others = new List<(string Target, int Mods, int Key, bool On)>
        {
            ("main", S.HotkeyModifiers, S.HotkeyKey, true), ("quick", S.QuickModifiers, S.QuickKey, S.QuickClip),
            ("shot", S.ScreenshotModifiers, S.ScreenshotKey, S.Screenshot), ("session", S.SessionModifiers, S.SessionKey, S.SessionHotkey),
            ("mark", S.BookmarkModifiers, S.BookmarkKey, S.BookmarkHotkey),
        };
        bool taken = others.Any(o => o.Target != _hotkeyTarget && o.On && o.Mods == mods && o.Key == vk);
        if (taken) { HotkeyButton(_hotkeyTarget).Content = Loc.T("Already used by another Pulse hotkey"); return; }
        _capturingHotkey = false;
        switch (_hotkeyTarget)
        {
            case "quick": S.QuickModifiers = mods; S.QuickKey = vk; S.QuickClip = true; break;
            case "shot": S.ScreenshotModifiers = mods; S.ScreenshotKey = vk; S.Screenshot = true; break;
            case "session": S.SessionModifiers = mods; S.SessionKey = vk; S.SessionHotkey = true; break;
            case "mark": S.BookmarkModifiers = mods; S.BookmarkKey = vk; S.BookmarkHotkey = true; break;
            default: S.HotkeyModifiers = mods; S.HotkeyKey = vk; break;
        }
        Changed(restart: true);
    }

    static int MonitorCount()
    {
        int n = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, IntPtr r, IntPtr d) => { n++; return true; }, IntPtr.Zero);
        return n;
    }
    delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);

    // ───────── Library ─────────

    int _libraryVersion;

    async void LoadLibrary()
    {
        bool on = S.Enabled && Ffmpeg.Installed;
        int version = ++_libraryVersion;
        List<ClipFile> clips;
        try { clips = await Task.Run(() => ClipLibrary.List(S)); }
        catch { return; }
        if (version != _libraryVersion) return; // a newer load started meanwhile (e.g. another clip was saved)
        LibraryHeader.Visibility = on || clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Visibility = on && clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipGrid.Visibility = clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibrarySummary.Text = clips.Count == 0 ? "" : Loc.F("{0} clips · {1}", clips.Count, Format.Bytes(clips.Sum(c => c.Size)));
        var vms = clips.Select(c => new ClipVm
        {
            Clip = c,
            Image = LoadImage(c.Thumbnail),
            Info = $"{c.Created.ToString("g", CultureInfo.CurrentCulture)} · {Format.Bytes(c.Size)}",
        }).ToList();
        bool selecting = SelectToggle.IsChecked == true;
        foreach (var v in vms) v.Selecting = selecting;
        _allClips = vms;
        LibraryTools.Visibility = clips.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BuildLibraryFilters();
        ApplyLibraryFilter();

        // Pictures for clips saved by another copy or before thumbnails existed.
        if (Ffmpeg.Installed && vms.Any(v => v.Image == null))
        {
            foreach (var v in vms.Where(v => v.Image == null).Take(30))
                try { await ClipLibrary.MakeThumbnail(v.Clip.Path); } catch { }
            if (vms.Any(v => v.Image == null && File.Exists(ClipLibrary.ThumbFor(v.Clip.Path)))) LoadLibrary();
        }
    }

    // ───────── Library filters ─────────

    List<ClipVm> _allClips = new();
    string _gameFilter = "", _sort = "new";
    bool _buildingFilters;

    void BuildLibraryFilters()
    {
        _buildingFilters = true;
        var games = _allClips.GroupBy(v => v.Clip.Game).OrderByDescending(g => g.Count()).Select(g => g.Key).Where(g => g.Length > 0).Take(8).ToList();
        if (_gameFilter.Length > 0 && _gameFilter != "\u2605" && !games.Contains(_gameFilter)) _gameFilter = "";
        var items = new List<(string, string)> { (Loc.T("All"), "") };
        if (_allClips.Any(v => v.Clip.Favorite)) items.Add((Loc.T("Favorites"), "\u2605"));
        items.AddRange(games.Select(g => (g, g)));
        GameFilterChips.Children.Clear();
        foreach (var (label, value) in items)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = label, GroupName = "clips-libgame", Tag = value, Margin = new Thickness(0, 3, 6, 3), IsChecked = value == _gameFilter };
            rb.Checked += (s, _) => { if (_buildingFilters) return; _gameFilter = (string)((FrameworkElement)s).Tag; ApplyLibraryFilter(); };
            GameFilterChips.Children.Add(rb);
        }
        SortChips.Children.Clear();
        foreach (var (label, value) in new[] { ("Newest", "new"), ("Oldest", "old"), ("Largest", "big") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "clips-libsort", Tag = value, Margin = new Thickness(6, 3, 0, 3), IsChecked = value == _sort };
            rb.Checked += (s, _) => { if (_buildingFilters) return; _sort = (string)((FrameworkElement)s).Tag; ApplyLibraryFilter(); };
            SortChips.Children.Add(rb);
        }
        _buildingFilters = false;
    }

    void ClipFilter_Changed(object sender, TextChangedEventArgs e) => ApplyLibraryFilter();

    void ApplyLibraryFilter()
    {
        var q = ClipSearch.Text?.Trim() ?? "";
        IEnumerable<ClipVm> list = _allClips;
        if (_gameFilter == "\u2605") list = list.Where(v => v.Clip.Favorite);
        else if (_gameFilter.Length > 0) list = list.Where(v => v.Clip.Game == _gameFilter);
        if (q.Length > 0) list = list.Where(v => v.Clip.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        list = _sort switch
        {
            "old" => list.OrderBy(v => v.Clip.Created),
            "big" => list.OrderByDescending(v => v.Clip.Size),
            _ => list.OrderByDescending(v => v.Clip.Created),
        };
        var shown = list.ToList();
        ClipGrid.ItemsSource = shown;
        ClipGrid.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryNoMatch.Visibility = _allClips.Count > 0 && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static ImageSource LoadImage(string path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.DecodePixelWidth = 500;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    // ───────── Player and trimmer ─────────

    List<string> _trackNames = new();
    double _pendingSeek = -1;
    bool _resumeAfterScrub;

    async void Clip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipVm vm) return;
        if (SelectToggle.IsChecked == true) { vm.Selected = !vm.Selected; UpdateSelection(); return; }
        _open = vm;
        Timeline.Markers = ClipLibrary.LoadMarks(vm.Clip.Path);
        LoadInputs(vm.Clip.Path);
        PlayerTitle.Text = vm.Clip.Name;
        ShowFavorite(vm.Clip.Favorite);
        Timeline.Strip = null;
        Timeline.Position = 0;
        PlayerLayer.Visibility = Visibility.Visible;
        PlayerLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        // A fresh clip starts with its own sound on (the last clip's muted parts don't carry over).
        _trackOn = new List<bool>();
        _lastPos = -1;
        Player.IsMuted = false;
        Player.Source = new Uri(vm.Clip.Path);
        Player.Play();
        _playing = true;
        PlayGlyph.Text = "";
        _playerTimer.Start();
        PlayerLayer.Focusable = true;
        Keyboard.Focus(PlayerLayer);

        // Which sounds can be chosen, and the filmstrip, load while the clip starts playing.
        _trackNames = await ClipLibrary.AudioTrackNames(vm.Clip.Path);
        BuildTrackToggles();
        _ = LoadParts(vm.Clip.Path);
    }

    async void Player_Opened(object sender, RoutedEventArgs e)
    {
        var d = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        Timeline.Duration = Math.Max(d, 0.1);
        Timeline.Start = 0;
        Timeline.End = Timeline.Duration;
        UpdateTimes();
        if (_open == null) return;
        var path = _open.Clip.Path;
        var strip = await ClipLibrary.Filmstrip(path, d);
        if (_open?.Clip.Path == path && strip != null) Timeline.Strip = LoadImage(strip);
    }

    void Player_Ended(object sender, RoutedEventArgs e)
    {
        // Loop the kept part, like a preview of the trimmed clip.
        Player.Position = TimeSpan.FromSeconds(Timeline.Start);
        if (_playing) Player.Play();
    }

    void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (_playing) { Player.Pause(); PlayGlyph.Text = ""; }
        else
        {
            var p = Player.Position.TotalSeconds;
            if (p < Timeline.Start || p >= Timeline.End - 0.05) Player.Position = TimeSpan.FromSeconds(Timeline.Start);
            Player.Play(); PlayGlyph.Text = "";
        }
        _playing = !_playing;
    }

    void PlayerTick()
    {
        if (PlayerLayer.Visibility != Visibility.Visible) return;
        if (_inputs != null && _inputMode != "off") InputView.Frame = _inputs.At(Timeline.Position);
        SyncParts();
        // Scrubbing: seek at most once per tick, to where the pointer is now, so the picture keeps up smoothly.
        if (_pendingSeek >= 0)
        {
            Player.Position = TimeSpan.FromSeconds(_pendingSeek);
            _pendingSeek = -1;
        }
        else if (!_scrubbing)
        {
            var p = Player.Position.TotalSeconds;
            if (_playing && p >= Timeline.End) { Player.Position = TimeSpan.FromSeconds(Timeline.Start); p = Timeline.Start; }
            Timeline.Position = Math.Min(p, Timeline.Duration);
        }
        UpdateTimes();
    }

    bool _scrubbing;

    void Timeline_Seek(double t)
    {
        if (!_scrubbing)
        {
            _scrubbing = true;
            _resumeAfterScrub = _playing;
            if (_playing) Player.Pause();
            Mouse.Capture(Timeline);
        }
        _pendingSeek = t;
    }

    void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        Player.Position = TimeSpan.FromSeconds(Timeline.Position);
        _pendingSeek = -1;
        if (_resumeAfterScrub) Player.Play();
    }

    void UpdateTimes()
    {
        TimeText.Text = $"{Clock(Timeline.Position)} / {Clock(Timeline.Duration)}";
        SelText.Text = Loc.F("Keeping {0} ({1} to {2})", Clock(Timeline.End - Timeline.Start), Clock(Timeline.Start), Clock(Timeline.End));
    }

    static string Clock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss\.f", CultureInfo.InvariantCulture);

    /// <summary>Keyboard: Space play/pause, arrows step one frame (Shift: one second), I and O set start and end, Delete deletes.</summary>
    void Player_KeyDown(object sender, KeyEventArgs e)
    {
        if (PlayerLayer.Visibility != Visibility.Visible) return;
        double frame = 1.0 / 60;
        void Step(double by)
        {
            if (_playing) TogglePlay();
            var t = Math.Clamp(Timeline.Position + by, 0, Timeline.Duration);
            Timeline.Position = t;
            Player.Position = TimeSpan.FromSeconds(t);
            UpdateTimes();
        }
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : -frame); break;
            case Key.Right: Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : frame); break;
            case Key.I: Timeline.Start = Math.Min(Timeline.Position, Timeline.End - Timeline.MinLength); UpdateTimes(); Timeline.InvalidateVisual(); break;
            case Key.O: Timeline.End = Math.Max(Timeline.Position, Timeline.Start + Timeline.MinLength); UpdateTimes(); Timeline.InvalidateVisual(); break;
            case Key.Delete: Delete_Click(sender, e); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.Escape: if (_fs != null) ExitFullscreen(); else ClosePlayer(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────── Audio mixer ─────────
    // Track 1 is everything mixed; the others are its parts (Game, Discord, Spotify, Microphone...). With every part
    // on you hear (and copies keep) the original mix; with some muted, the parts left on are played (and mixed) instead.

    List<bool> _trackOn = new();
    List<MediaPlayer> _parts = new();
    string _partsDir;
    bool _partsPlaying;
    int _partsReady;
    DateTime _partsSynced;
    double _lastPos = -1;

    void BuildTrackToggles()
    {
        var parts = _trackNames.Count >= 2 ? _trackNames.Skip(1).ToList() : _trackNames.Select(_ => "Sound").ToList();
        _trackOn = parts.Select(_ => true).ToList();
        AudioPanel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AudioRows.Children.Clear();
        for (int i = 0; i < parts.Count; i++)
        {
            int index = i;
            var icon = new TextBlock { Style = (Style)FindResource("IconText"), FontSize = 14, Width = 22 };
            var label = new TextBlock { Text = Loc.T(parts[i]), FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var row = new Button { Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(0) };
            row.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
            void Show()
            {
                icon.Text = _trackOn[index] ? "\uE767" : "\uE74F";
                icon.Foreground = _trackOn[index] ? (Brush)FindResource("TextBrush") : (Brush)FindResource("BadBrush");
                label.Foreground = _trackOn[index] ? (Brush)FindResource("TextBrush") : (Brush)FindResource("TextDimBrush");
                label.TextDecorations = _trackOn[index] ? null : TextDecorations.Strikethrough;
            }
            row.Click += (_, _) => { _trackOn[index] = !_trackOn[index]; Show(); ApplyAudio(); };
            Show();
            AudioRows.Children.Add(row);
        }
        ApplyAudio();
    }

    /// <summary>Which tracks the exported copy keeps: all of them (track 1, the full mix), some parts mixed, or none.</summary>
    /// <summary>Null (keep the original sound) until the clip's sound tracks are known, so a quick trim is never silent.</summary>
    List<int> ChosenTracks() => _trackOn.Count == 0 ? null : ClipLibrary.ChooseTracks(_trackNames.Count, _trackOn);

    void AudioBtn_Click(object sender, RoutedEventArgs e) => AudioPopup.IsOpen = !AudioPopup.IsOpen;

    /// <summary>Plays what the copy will keep: the original mix, the parts left on, or nothing.</summary>
    void ApplyAudio()
    {
        var chosen = ChosenTracks() ?? new List<int> { 0 };
        int muted = _trackOn.Count(x => !x);
        AudioBtnText.Text = muted == 0 ? Loc.T("Audio") : Loc.F("Audio ({0} muted)", muted);
        AudioGlyph.Text = chosen.Count == 0 ? "\uE74F" : "\uE767";
        // The separate sounds are only used once they're all ready; until then you hear the clip's own mix.
        bool useParts = chosen.Count > 0 && !(chosen.Count == 1 && chosen[0] == 0) && _parts.Count > 0 && _partsReady >= _parts.Count;
        Player.IsMuted = chosen.Count == 0 || useParts;
        for (int i = 0; i < _parts.Count; i++) _parts[i].Volume = useParts && i < _trackOn.Count && _trackOn[i] ? 1 : 0;
    }

    /// <summary>Each part as its own sound file, so it can be muted while you watch. Loads in the background.</summary>
    async Task LoadParts(string clip)
    {
        StopParts();
        if (_trackNames.Count < 2) return;
        var (dir, files) = await ClipLibrary.ExtractParts(clip, _trackNames.Count - 1);
        if (_open?.Clip.Path != clip || files.Count == 0) { ClipLibrary.CleanParts(dir); return; }
        _partsDir = dir;
        foreach (var f in files)
        {
            var mp = new MediaPlayer { Volume = 0 };
            mp.MediaOpened += (_, _) => { _partsReady++; ApplyAudio(); };
            mp.MediaEnded += (_, _) => _partsPlaying = false; // started again (from the right spot) on the next tick
            mp.Open(new Uri(f));
            _parts.Add(mp);
        }
        _partsPlaying = false;
        ApplyAudio();
    }

    void StopParts()
    {
        foreach (var p in _parts) { try { p.Stop(); p.Close(); } catch { } }
        _parts.Clear();
        _partsReady = 0;
        _partsPlaying = false;
        if (_partsDir != null) { var d = _partsDir; _partsDir = null; _ = Task.Delay(300).ContinueWith(_ => ClipLibrary.CleanParts(d)); }
    }

    /// <summary>Keeps the separate sounds in step with the video (called by the player's timer).</summary>
    void SyncParts()
    {
        if (_parts.Count == 0 || _partsReady < _parts.Count) return;
        var pos = Player.Position;
        // A jump (the trimmed part looping back, a seek): restart the sounds from the new spot.
        bool jumped = _lastPos >= 0 && Math.Abs(pos.TotalSeconds - _lastPos) > 0.5;
        _lastPos = pos.TotalSeconds;
        if (_playing && !_scrubbing)
        {
            if (!_partsPlaying || jumped)
            {
                foreach (var p in _parts) { p.Position = pos; p.Play(); }
                _partsPlaying = true;
                _partsSynced = DateTime.UtcNow;
                return;
            }
            // Small drift is fixed now and then, not every tick: seeking a sound over and over makes it go quiet.
            if (DateTime.UtcNow - _partsSynced < TimeSpan.FromMilliseconds(800)) return;
            foreach (var p in _parts)
                if (Math.Abs((p.Position - pos).TotalMilliseconds) > 150) { p.Position = pos; _partsSynced = DateTime.UtcNow; }
        }
        else if (_partsPlaying) { foreach (var p in _parts) p.Pause(); _partsPlaying = false; }
        else if (jumped) foreach (var p in _parts) p.Position = pos;
    }

    void ShowFavorite(bool on)
    {
        FavBtnGlyph.Text = on ? "\uE735" : "\uE734"; // filled or outlined star
        FavBtnGlyph.Foreground = on ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)) : (Brush)FindResource("TextBrush");
        FavBtn.ToolTip = Loc.T(on ? "Unfavorite" : "Favorite (kept when the storage limit is reached)");
    }

    void PlayerClose_Click(object sender, RoutedEventArgs e) => ClosePlayer();

    // ───────── Full screen ─────────
    // A borderless window on the same monitor borrows the video and the controls; the controls fade out while
    // the clip plays and come back when the mouse moves.

    Window _fs;
    Grid _cardGrid;
    Border _fsOverlay;
    readonly DispatcherTimer _fsIdle = new() { Interval = TimeSpan.FromSeconds(2.5) };

    void Full_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    void Video_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click: full screen. (Each click's release also toggles play, so two clicks leave it as it was.)
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    void ToggleFullscreen() { if (_fs == null) EnterFullscreen(); else ExitFullscreen(); }

    void EnterFullscreen()
    {
        if (_fs != null || PlayerLayer.Visibility != Visibility.Visible) return;
        var owner = Window.GetWindow(this);
        _cardGrid ??= (Grid)PlayerControls.Parent;

        Player.UnloadedBehavior = MediaState.Manual; // keep playing while it moves windows
        VideoBox.Child = null;
        _cardGrid.Children.Remove(Timeline);
        _cardGrid.Children.Remove(PlayerControls);

        var stack = new StackPanel { MaxWidth = 1400 };
        stack.Children.Add(Timeline);
        stack.Children.Add(PlayerControls);
        _fsOverlay = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(32, 60, 32, 24),
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xD9, 0x08, 0x0A, 0x0F), 90),
            Child = stack,
        };
        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(VideoGrid);
        root.Children.Add(_fsOverlay);

        _fs = new Window
        {
            Title = PlayerTitle.Text, Content = root, Background = Brushes.Black,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // Start inside the Pulse window so maximizing fills the monitor Pulse is on.
            Left = (owner?.Left ?? 0) + 40, Top = (owner?.Top ?? 0) + 40, Width = 640, Height = 360,
        };
        _fs.SourceInitialized += (_, _) => _fs.WindowState = WindowState.Maximized;
        _fs.PreviewKeyDown += (s, e) =>
        {
            if (e.Key is Key.Escape or Key.F) { ExitFullscreen(); e.Handled = true; return; }
            Player_KeyDown(s, e);
            ShowFsControls();
        };
        _fs.MouseMove += (_, _) => ShowFsControls();
        _fs.Closed += (_, _) => ExitFullscreen();
        _fsIdle.Tick -= FsIdle_Tick;
        _fsIdle.Tick += FsIdle_Tick;

        FullGlyph.Text = "\uE73F";
        FullBtn.ToolTip = Loc.T("Exit full screen (Esc)");
        _fs.Show();
        _fs.Activate();
        Keyboard.Focus(_fs);
        ShowFsControls();
    }

    void ShowFsControls()
    {
        if (_fs == null) return;
        _fs.Cursor = null;
        _fsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _fsIdle.Stop();
        _fsIdle.Start();
    }

    void FsIdle_Tick(object sender, EventArgs e)
    {
        _fsIdle.Stop();
        if (_fs == null || !_playing || _scrubbing || _fsOverlay.IsMouseOver) return;
        _fsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)));
        _fs.Cursor = Cursors.None;
    }

    void ExitFullscreen()
    {
        if (_fs == null) return;
        var fs = _fs;
        _fs = null;
        _fsIdle.Stop();
        if (fs.Content is Grid root) root.Children.Clear();
        if (_fsOverlay?.Child is StackPanel stack) stack.Children.Clear();
        VideoBox.Child = VideoGrid;
        _cardGrid.Children.Add(Timeline);
        _cardGrid.Children.Add(PlayerControls);
        Player.UnloadedBehavior = MediaState.Close;
        FullGlyph.Text = "\uE740";
        FullBtn.ToolTip = Loc.T("Full screen (F)");
        try { fs.Close(); } catch (InvalidOperationException) { /* already closing */ }
        Window.GetWindow(this)?.Activate();
        Keyboard.Focus(PlayerLayer);
    }

    void ClosePlayer()
    {
        ExitFullscreen();
        _playerTimer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
        StopParts();
        _inputs = null;
        InputBox.Visibility = Visibility.Collapsed;
        _playing = false;
        _scrubbing = false;
        PlayerLayer.Visibility = Visibility.Collapsed;
    }

    Core.Clips.Encoder BestEncoder() => Encoders.Pick(_encoders, S.Codec) ?? Encoders.Pick(_encoders, "h264");

    async void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        var enc = BestEncoder();
        if (enc == null) return;
        Player.Pause(); _playing = false; PlayGlyph.Text = "";
        var steps = new List<ProgressStep> { new("Cutting the clip") };
        MainWindow.Instance.ShowSteps("Saving the trimmed copy", "", steps, "");
        steps[0].Start();
        try
        {
            var burn = await PrepareInputBurn(_open.Clip.Path, Timeline.Start, Timeline.End);
            var path = await ClipLibrary.Trim(_open.Clip.Path, Timeline.Start, Timeline.End, enc, Math.Max(S.BitrateMbps, 20), ChosenTracks(), burn);
            await ClipLibrary.MakeThumbnail(path);
            steps[0].Finish(Format.Bytes(new FileInfo(path).Length));
            await MainWindow.Instance.FinishSteps("Trimmed copy saved", Loc.F("Saved as {0}. The original is untouched.", Path.GetFileName(path)));
            LoadLibrary();
        }
        catch (Exception ex)
        {
            steps[0].Skip(Loc.T("Didn't finish"));
            await MainWindow.Instance.FinishSteps("Trimming didn't work", ex.Message, ok: false);
        }
    }

    async void Discord_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        var enc = BestEncoder();
        if (enc == null) return;
        Player.Pause(); _playing = false; PlayGlyph.Text = "";
        var steps = new List<ProgressStep> { new("Shrinking to fit Discord") };
        MainWindow.Instance.ShowSteps("Making a Discord copy", "Long clips are made smaller so they stay under 10 MB.", steps, "");
        steps[0].Start();
        try
        {
            bool whole = Timeline.Start <= 0.01 && Timeline.End >= Timeline.Duration - 0.05;
            var burn = await PrepareInputBurn(_open.Clip.Path, whole ? 0 : Timeline.Start, whole ? Timeline.Duration : Timeline.End);
            var path = await ClipLibrary.ForDiscord(_open.Clip.Path, enc, whole ? 0 : Timeline.Start, whole ? 0 : Timeline.End, ChosenTracks(), burn);
            await ClipLibrary.MakeThumbnail(path);
            steps[0].Finish(Format.Bytes(new FileInfo(path).Length));
            // Put the file on the clipboard so it can be pasted straight into Discord.
            try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path }); } catch { }
            await MainWindow.Instance.FinishSteps("Ready for Discord",
                Loc.F("{0} is copied: paste it into a Discord chat with Ctrl+V.", Path.GetFileName(path)));
            LoadLibrary();
        }
        catch (Exception ex)
        {
            steps[0].Skip(Loc.T("Didn't finish"));
            await MainWindow.Instance.FinishSteps("The Discord copy didn't work", ex.Message, ok: false);
        }
    }

    // ───────── Input overlay ─────────

    InputTimeline _inputs;
    string _inputMode = "off"; // "off", "kbm", "xbox", "ps"

    void LoadInputs(string clip)
    {
        _inputs = InputTimeline.Load(clip);
        InputPanel.Visibility = _inputs?.HasInput == true ? Visibility.Visible : Visibility.Collapsed;
        InputChips.Children.Clear();
        if (_inputs?.HasInput != true) { _inputMode = "off"; InputBox.Visibility = Visibility.Collapsed; return; }
        _inputMode = _inputs.MostUsed();
        var pad = _inputs.PadKind == "ps" ? ("PlayStation", "ps") : ("Xbox", "xbox");
        foreach (var (label, value) in new[] { ("Off", "off"), ("Keyboard and mouse", "kbm"), pad })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "clips-input", Tag = value, Margin = new Thickness(0, 0, 6, 0), IsChecked = value == _inputMode };
            rb.Checked += (s, _) => { _inputMode = (string)((FrameworkElement)s).Tag; ShowInputMode(); };
            InputChips.Children.Add(rb);
        }
        ShowInputMode();
    }

    void ShowInputMode()
    {
        InputBox.Visibility = _inputMode == "off" ? Visibility.Collapsed : Visibility.Visible;
        if (_inputMode != "off") { InputView.Kind = _inputMode; InputView.Frame = _inputs?.At(Timeline.Position); }
        VideoGrid_SizeChanged(null, null);
    }

    void VideoGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // About a quarter of the picture's height, like the burned-in copy.
        InputBox.Height = Math.Max(60, VideoGrid.ActualHeight * 0.26);
    }

    /// <summary>
    /// Pictures of the overlay for each change between <paramref name="from"/> and <paramref name="to"/>, and an
    /// FFmpeg concat list that shows each for as long as it was held. Null when the overlay is off.
    /// </summary>
    async Task<InputBurn> PrepareInputBurn(string clip, double from, double to)
    {
        if (_inputs == null || _inputMode == "off") return null;
        var (_, height) = await ClipLibrary.VideoSize(clip);
        if (height <= 0) height = 1080;
        var part = _inputs.Slice(from, to);
        if (part.Frames.Count == 0) return null;
        // Changes closer than 1/30 s apart are merged (the picture can't show them anyway).
        var frames = new List<InputFrame>();
        foreach (var f in part.Frames)
        {
            if (frames.Count > 0 && f.T - frames[^1].T < 1 / 30.0) { var c = f.Copy(); c.T = frames[^1].T; frames[^1] = c; }
            else frames.Add(f);
        }
        double scale = height * 0.26 / InputOverlayView.BaseSize(_inputMode).Height;
        var dir = Path.Combine(Path.GetTempPath(), "pulse-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var list = new System.Text.StringBuilder("ffconcat version 1.0\n");
        double length = to - from;
        for (int i = 0; i < frames.Count; i++)
        {
            var png = Path.Combine(dir, $"{i:D5}.png");
            var bmp = InputOverlayView.Render(frames[i], _inputMode, scale);
            using (var fs = File.Create(png)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); enc.Save(fs); }
            double next = i + 1 < frames.Count ? frames[i + 1].T : length;
            list.Append($"file '{png.Replace("\\", "/")}'\nduration {Math.Max(0.001, next - frames[i].T).ToString("0.###", CultureInfo.InvariantCulture)}\n");
            if (i % 50 == 49) await Task.Yield(); // keep the window responsive
        }
        // The concat format needs the last picture listed once more.
        list.Append($"file '{Path.Combine(dir, $"{frames.Count - 1:D5}.png").Replace("\\", "/")}'\n");
        var listFile = Path.Combine(dir, "list.txt");
        File.WriteAllText(listFile, list.ToString());
        return new InputBurn(dir, listFile);
    }

    void VoicePhrase_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var p = VoicePhraseBox.Text.Trim();
        if (p == (string.IsNullOrWhiteSpace(S.VoicePhrase) ? VoiceClip.DefaultPhrase : S.VoicePhrase)) return;
        S.VoicePhrase = p == VoiceClip.DefaultPhrase ? "" : p;
        Changed(restart: true);
    }

    bool _settingsOpen;

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = !_settingsOpen;
        UpdateStatus();
        if (_settingsOpen) SettingsCard.BringIntoView();
    }

    // ───────── Long recording ─────────

    async void SessionNow_Click(object sender, RoutedEventArgs e)
    {
        SessionNowBtn.IsEnabled = false;
        await ClipsService.ToggleSession();
        UpdateUsage();
    }

    // ───────── What Clips costs ─────────

    bool _costRunning;

    async void Cost_Click(object sender, RoutedEventArgs e)
    {
        var pid = GameDetector.GamePid;
        string exe = null;
        try { if (pid > 0) using (var p = Process.GetProcessById(pid)) exe = p.ProcessName; } catch { }
        if (exe == null) { await MainWindow.Instance.Alert("Start a game first", "The test measures the game you're playing, with Clips off and on."); return; }
        if (!await MainWindow.Instance.Confirm("Test what Clips costs",
                Loc.F("Pulse measures {0} for about 2 minutes: three times with Clips off and three times with it on, taking turns. Keep playing something steady (the same map or a replay) so the runs are comparable.", ClipsService.Game ?? exe), "Start"))
            return;
        _costRunning = true;
        CostBtn.IsEnabled = false;
        CostBox.Visibility = Visibility.Visible;
        var off = new List<RunSummary>();
        var on = new List<RunSummary>();
        try
        {
            for (int i = 0; i < 6; i++)
            {
                bool clipsOn = i % 2 == 1;
                CostText.Text = Loc.F("Run {0} of 6: Clips {1}. Keep playing…", i + 1, Loc.T(clipsOn ? "on" : "off"));
                ClipsService.Paused = !clipsOn;
                await Task.Delay(clipsOn ? 6000 : 3000); // give recording time to start (or stop) before measuring
                var r = await Task.Run(() => PresentMonCapture.Capture(exe, 18));
                if (!r.Ok) throw new InvalidOperationException(r.Error);
                (clipsOn ? on : off).Add(Stats.Summarize(r.FrameTimesMs, r.LatenciesMs));
            }
            double offFps = Stats.Median(off.Select(x => x.AvgFps)), onFps = Stats.Median(on.Select(x => x.AvgFps));
            double offLow = Stats.Median(off.Select(x => x.Low1Fps)), onLow = Stats.Median(on.Select(x => x.Low1Fps));
            double cost = offFps > 0 ? (offFps - onFps) / offFps * 100 : 0;
            CostText.Text = Loc.F("Clips off: {0} FPS (1% low {1}) · Clips on: {2} FPS (1% low {3}).", Math.Round(offFps), Math.Round(offLow), Math.Round(onFps), Math.Round(onLow)) + " " +
                (Math.Abs(cost) < 1.5 ? Loc.T("No difference bigger than normal run-to-run noise.") : cost > 0 ? Loc.F("Clips costs about {0}% FPS here.", cost.ToString("0.#", CultureInfo.CurrentCulture)) : Loc.T("No measurable cost."));
            TweakEngine.AddLog("clips", $"Clips cost test: off {offFps:0} FPS, on {onFps:0} FPS");
        }
        catch (Exception ex) { CostText.Text = Loc.T("The test stopped: ") + Loc.T(ex.Message); }
        finally
        {
            ClipsService.Paused = false;
            _costRunning = false;
            UpdateUsage();
        }
    }

    // ───────── Selecting several clips ─────────

    void Select_Click(object sender, RoutedEventArgs e)
    {
        bool on = SelectToggle.IsChecked == true;
        SelectToggle.Content = Loc.T(on ? "Done" : "Select");
        foreach (var v in _allClips) { v.Selecting = on; if (!on) v.Selected = false; }
        UpdateSelection();
    }

    void UpdateSelection()
    {
        int n = _allClips.Count(v => v.Selected);
        DeleteSelectedBtn.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteSelectedBtn.Content = Loc.F("Delete {0}", n);
    }

    async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = _allClips.Where(v => v.Selected).ToList();
        if (picked.Count == 0) return;
        if (!await MainWindow.Instance.Confirm(Loc.F("Delete {0} clips?", picked.Count), Loc.F("{0} will be freed. Favorites are deleted too if selected.", Format.Bytes(picked.Sum(v => v.Clip.Size))), "Delete")) return;
        int failed = 0;
        foreach (var v in picked)
        {
            try
            {
                ClipLibrary.Delete(v.Clip.Path);
                S.Favorites.RemoveAll(f => f.Equals(Path.GetFileName(v.Clip.Path), StringComparison.OrdinalIgnoreCase));
            }
            catch { failed++; }
        }
        TweakEngine.Save();
        TweakEngine.AddLog("clips", $"Deleted {picked.Count - failed} clips");
        SelectToggle.IsChecked = false;
        Select_Click(SelectToggle, e);
        LoadLibrary();
        if (failed > 0) await MainWindow.Instance.Alert("Some clips weren't deleted", Loc.F("{0} are open in another app.", failed));
    }

    /// <summary>The trash button on a clip card: one confirm, then gone (no need to open the clip).</summary>
    async void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not ClipVm vm) return;
        if (!await MainWindow.Instance.Confirm("Delete this clip?", Loc.F("{0} ({1})", vm.Clip.Name, Format.Bytes(vm.Clip.Size)), "Delete")) return;
        try
        {
            ClipLibrary.Delete(vm.Clip.Path);
            S.Favorites.RemoveAll(f => f.Equals(Path.GetFileName(vm.Clip.Path), StringComparison.OrdinalIgnoreCase));
            TweakEngine.Save();
            TweakEngine.AddLog("clips", $"Deleted {Path.GetFileName(vm.Clip.Path)}");
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't delete the clip", ex.Message); }
        LoadLibrary();
    }

    // ───────── Rename ─────────

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        RenameBox.Text = _open.Clip.Name;
        RenameBox.Visibility = Visibility.Visible;
        PlayerTitle.Visibility = Visibility.Hidden;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = FinishRename(true); }
        else if (e.Key == Key.Escape) { e.Handled = true; _ = FinishRename(false); }
    }

    void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => _ = FinishRename(true);

    bool _renaming;

    async Task FinishRename(bool save)
    {
        if (_renaming || RenameBox.Visibility != Visibility.Visible) return;
        _renaming = true;
        RenameBox.Visibility = Visibility.Collapsed;
        PlayerTitle.Visibility = Visibility.Visible;
        try
        {
            if (!save || _open == null || RenameBox.Text.Trim() == _open.Clip.Name) return;
            var old = _open.Clip.Path;
            ClosePlayer(); // the player holds the file open
            await Task.Delay(150);
            var dest = ClipLibrary.Rename(old, RenameBox.Text);
            var oldName = Path.GetFileName(old);
            if (S.Favorites.RemoveAll(f => f.Equals(oldName, StringComparison.OrdinalIgnoreCase)) > 0) S.Favorites.Add(Path.GetFileName(dest));
            TweakEngine.Save();
            MainWindow.Instance.Toast(Loc.T("Renamed"));
            LoadLibrary();
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't rename the clip", ex.Message); }
        finally { _renaming = false; }
    }

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        var name = Path.GetFileName(_open.Clip.Path);
        if (_open.Clip.Favorite) S.Favorites.RemoveAll(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        else S.Favorites.Add(name);
        _open.Clip.Favorite = !_open.Clip.Favorite;
        ShowFavorite(_open.Clip.Favorite);
        TweakEngine.Save();
        LoadLibrary();
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_open.Clip.Path}\"") { UseShellExecute = true });
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        var clip = _open.Clip;
        if (!await MainWindow.Instance.Confirm("Delete this clip?", clip.Name, "Delete")) return;
        ClosePlayer();
        try
        {
            await Task.Delay(150); // let the player let go of the file
            ClipLibrary.Delete(clip.Path);
            S.Favorites.RemoveAll(f => f.Equals(Path.GetFileName(clip.Path), StringComparison.OrdinalIgnoreCase));
            TweakEngine.Save();
        }
        catch (Exception ex) { await MainWindow.Instance.Alert("Couldn't delete the clip", ex.Message); }
        LoadLibrary();
    }
}
