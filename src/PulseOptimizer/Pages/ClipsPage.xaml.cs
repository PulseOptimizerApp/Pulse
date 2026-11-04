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
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>One card in the clip library.</summary>
public class ClipVm
{
    public ClipFile Clip { get; init; }
    public ImageSource Image { get; set; }
    public string Info { get; init; }
}

public partial class ClipsPage : UserControl, IPage
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
        ClipsService.Changed += () => Dispatcher.InvokeAsync(UpdateStatus);
        ClipsService.Saved += _ => Dispatcher.InvokeAsync(LoadLibrary);
        Unloaded += (_, _) => _usage.Stop();
        _playerTimer.Interval = TimeSpan.FromMilliseconds(33);
        Timeline.Seek += Timeline_Seek;
        Timeline.SelectionChanged += UpdateTimes;
        Timeline.PreviewMouseLeftButtonUp += Timeline_MouseUp;
        PlayerLayer.PreviewKeyDown += Player_KeyDown;
    }

    public async void OnShown(object parameter)
    {
        _usage.Start();
        BuildChips();
        await LoadEncoders();
        ShowSettings();
        UpdateStatus();
        LoadLibrary();
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
        StatusCard.Visibility = SettingsCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
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
        ExtraWarn.Text = ClipsService.QuickOk && ClipsService.ShotOk ? "" : Loc.T("Another app already uses one of these hotkeys. Pick a different one.");
        UpdateUsage();
    }

    void UpdateUsage()
    {
        if (StatusCard.Visibility != Visibility.Visible) return;
        if (ClipsService.State != ClipsState.Recording) { UsageText.Text = Loc.T("Using nothing right now."); return; }
        var enc = ClipsService.Encoder;
        string encText = enc == null ? "" : enc.Hardware ? Loc.F("{0} graphics card encoder ({1})", enc.Vendor, CodecName(enc.Codec)) : Loc.T("processor encoder (no graphics card encoder found)");
        UsageText.Text = Loc.F("Using {0} of memory for {1} s of replay · FFmpeg CPU {2}% · {3}",
            Format.Bytes(ClipsService.BufferBytes), (int)ClipsService.BufferSeconds, ClipsService.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture), encText);
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
        Fill(LimitChips, "lim", new[] { ("10 GB", "10"), ("30 GB", "30"), ("100 GB", "100"), ("No limit", "0") });
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
        Mark(MonitorChips, S.Monitor.ToString(CultureInfo.InvariantCulture));
        Mark(CodecChips, S.Codec);
        var preset = Presets.FirstOrDefault(p => (p.Res == 0 ? "native" : p.Res.ToString(CultureInfo.InvariantCulture)) == S.Resolution && p.Fps == S.Fps && p.Mbps == S.BitrateMbps);
        Mark(QualityChips, preset.Label ?? "");
        QualityText.Text = Loc.F("{0} · {1} FPS · {2} Mbps · about {3} of memory per minute",
            S.Resolution == "native" ? Loc.T("Native resolution") : S.Resolution + "p", S.Fps, S.BitrateMbps, Format.Bytes(S.BitrateMbps * 1_000_000.0 / 8 * 60));
        GameAudioSwitch.IsChecked = S.GameAudio;
        MicSwitch.IsChecked = S.Microphone;
        OnlyGamingSwitch.IsChecked = S.OnlyWhileGaming;
        TraySwitch.IsChecked = S.KeepRunningInTray;
        StartupSwitch.IsChecked = S.StartWithWindows;
        SeparateMicSwitch.IsChecked = S.SeparateMicTrack;
        CursorSwitch.IsChecked = S.RecordCursor;
        SoundSwitch.IsChecked = S.PlaySound;
        PopupSwitch.IsChecked = S.ShowSavedPopup;
        FolderText.Text = S.ClipsFolder;
        HotkeyBtn.Content = ClipsService.HotkeyText(S.HotkeyModifiers, S.HotkeyKey);
        QuickBtn.Content = ClipsService.HotkeyText(S.QuickModifiers, S.QuickKey);
        ShotBtn.Content = ClipsService.HotkeyText(S.ScreenshotModifiers, S.ScreenshotKey);
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
            case "qlen": S.QuickSeconds = n; break;
            case "mon": S.Monitor = n; break;
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
        S.KeepRunningInTray = TraySwitch.IsChecked == true;
        S.SeparateMicTrack = SeparateMicSwitch.IsChecked == true;
        S.RecordCursor = CursorSwitch.IsChecked == true;
        S.PlaySound = SoundSwitch.IsChecked == true;
        S.ShowSavedPopup = PopupSwitch.IsChecked == true;
        S.QuickClip = QuickSwitch.IsChecked == true;
        S.Screenshot = ShotSwitch.IsChecked == true;
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

    Button HotkeyButton(string target) => target switch { "quick" => QuickBtn, "shot" => ShotBtn, _ => HotkeyBtn };

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
        bool fKey = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !fKey) { HotkeyButton(_hotkeyTarget).Content = Loc.T("Add Ctrl, Alt or Shift (or use an F key)"); return; }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        // One key combination does one thing.
        bool taken = _hotkeyTarget switch
        {
            "quick" => (mods == S.HotkeyModifiers && vk == S.HotkeyKey) || (mods == S.ScreenshotModifiers && vk == S.ScreenshotKey),
            "shot" => (mods == S.HotkeyModifiers && vk == S.HotkeyKey) || (mods == S.QuickModifiers && vk == S.QuickKey),
            _ => (mods == S.QuickModifiers && vk == S.QuickKey && S.QuickClip) || (mods == S.ScreenshotModifiers && vk == S.ScreenshotKey && S.Screenshot),
        };
        if (taken) { HotkeyButton(_hotkeyTarget).Content = Loc.T("Already used by another Pulse hotkey"); return; }
        _capturingHotkey = false;
        switch (_hotkeyTarget)
        {
            case "quick": S.QuickModifiers = mods; S.QuickKey = vk; S.QuickClip = true; break;
            case "shot": S.ScreenshotModifiers = mods; S.ScreenshotKey = vk; S.Screenshot = true; break;
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

    async void LoadLibrary()
    {
        bool on = S.Enabled && Ffmpeg.Installed;
        var clips = await Task.Run(() => ClipLibrary.List(S));
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
        ClipGrid.ItemsSource = vms;

        // Pictures for clips saved by another copy or before thumbnails existed.
        if (Ffmpeg.Installed && vms.Any(v => v.Image == null))
        {
            foreach (var v in vms.Where(v => v.Image == null).Take(30))
                try { await ClipLibrary.MakeThumbnail(v.Clip.Path); } catch { }
            if (vms.Any(v => v.Image == null && File.Exists(ClipLibrary.ThumbFor(v.Clip.Path)))) LoadLibrary();
        }
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

    int _tracks = 1;
    double _pendingSeek = -1;
    bool _resumeAfterScrub;

    async void Clip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipVm vm) return;
        _open = vm;
        PlayerTitle.Text = vm.Clip.Name;
        FavBtn.Content = Loc.T(vm.Clip.Favorite ? "Unfavorite" : "Favorite");
        Timeline.Strip = null;
        Timeline.Position = 0;
        PlayerLayer.Visibility = Visibility.Visible;
        PlayerLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        Player.Source = new Uri(vm.Clip.Path);
        Player.Play();
        _playing = true;
        PlayGlyph.Text = "";
        _playerTimer.Start();
        PlayerLayer.Focusable = true;
        Keyboard.Focus(PlayerLayer);

        // Which sounds can be chosen, and the filmstrip, load while the clip starts playing.
        _tracks = await ClipLibrary.AudioTracks(vm.Clip.Path);
        MicTrack.Visibility = _tracks >= 3 ? Visibility.Visible : Visibility.Collapsed;
        GameTrack.Content = Loc.T(_tracks >= 3 ? "Game" : "Sound");
        AudioPanel.Visibility = _tracks >= 1 ? Visibility.Visible : Visibility.Collapsed;
        GameTrack.IsChecked = MicTrack.IsChecked = true;
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
            case Key.Escape: ClosePlayer(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Which audio track the exported copy keeps: mix, game only, mic only or none.</summary>
    int ChosenTrack()
    {
        bool game = GameTrack.IsChecked == true, mic = MicTrack.IsChecked == true;
        if (_tracks >= 3) return game && mic ? 0 : game ? 1 : mic ? 2 : -1;
        return _tracks >= 1 && game ? 0 : -1;
    }

    void Track_Click(object sender, RoutedEventArgs e) { /* read when exporting */ }

    void PlayerClose_Click(object sender, RoutedEventArgs e) => ClosePlayer();

    void ClosePlayer()
    {
        _playerTimer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
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
            var path = await ClipLibrary.Trim(_open.Clip.Path, Timeline.Start, Timeline.End, enc, Math.Max(S.BitrateMbps, 20), ChosenTrack());
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
            var path = await ClipLibrary.ForDiscord(_open.Clip.Path, enc, whole ? 0 : Timeline.Start, whole ? 0 : Timeline.End, ChosenTrack());
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

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        var name = Path.GetFileName(_open.Clip.Path);
        if (_open.Clip.Favorite) S.Favorites.RemoveAll(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        else S.Favorites.Add(name);
        _open.Clip.Favorite = !_open.Clip.Favorite;
        FavBtn.Content = Loc.T(_open.Clip.Favorite ? "Unfavorite" : "Favorite");
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
