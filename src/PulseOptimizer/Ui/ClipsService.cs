using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Clips;
using Pulse.Core.Measurement;

namespace Pulse.Ui;

public enum ClipsState { Off, NeedsSetup, WaitingForGame, Recording, Problem }

/// <summary>
/// Runs Clips while Pulse is open (or in the tray): starts the instant replay when a game is playing and stops it when
/// the game closes, listens for the save hotkey, saves clips and keeps the folder under its size limit.
/// </summary>
public static class ClipsService
{
    static ClipSettings S => TweakEngine.State.Clips ??= new ClipSettings();

    static DispatcherTimer _timer;
    static ReplayRecorder _rec;
    static HwndSource _hotkeyWindow;
    static bool _hotkeyOk;
    static int _failures;
    static DateTime _retryAfter;
    static string _game;
    static bool _saving;
    static TimeSpan _lastCpu;
    static DateTime _lastCpuAt;

    public static ClipsState State { get; private set; } = ClipsState.Off;
    public static string Problem { get; private set; } = "";
    public static string Game => _game;
    public static Core.Clips.Encoder Encoder => _rec?.Encoder;
    public static bool HotkeyWorks => _hotkeyOk;
    public static bool QuickOk { get; private set; } = true;
    public static bool ShotOk { get; private set; } = true;
    public static long BufferBytes => _rec?.BufferBytes ?? 0;
    public static double BufferSeconds => _rec?.BufferSeconds ?? 0;
    /// <summary>FFmpeg's share of all CPU time since the last call (0..100).</summary>
    public static double CpuPercent
    {
        get
        {
            if (_rec == null) return 0;
            var cpu = _rec.CpuTime;
            var now = DateTime.UtcNow;
            double pct = _lastCpuAt == default ? 0 : (cpu - _lastCpu).TotalMilliseconds / Math.Max((now - _lastCpuAt).TotalMilliseconds, 1) / Environment.ProcessorCount * 100;
            _lastCpu = cpu; _lastCpuAt = now;
            return Math.Max(0, pct);
        }
    }

    /// <summary>Raised on the UI thread whenever the state changes.</summary>
    public static event Action Changed;
    /// <summary>Raised on the UI thread when a clip is saved (its path).</summary>
    public static event Action<string> Saved;

    /// <summary>Call once on the UI thread when Pulse starts.</summary>
    public static void Init()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        RegisterHotkey();
        Tick();
    }

    /// <summary>Settings changed: restart the recording with them.</summary>
    public static void Restart()
    {
        StopRecorder();
        _failures = 0;
        _stalls = 0;
        _screenOnly = IntPtr.Zero;
        _retryAfter = default;
        RegisterHotkey();
        Tick();
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        StopRecorder();
        if (_hotkeyWindow != null)
        {
            foreach (var id in new[] { HotkeyId, QuickId, ShotId, SessionId, MarkId }) UnregisterHotKey(_hotkeyWindow.Handle, id);
            _hotkeyWindow.Dispose();
            _hotkeyWindow = null;
        }
    }

    static void Set(ClipsState s, string problem = "")
    {
        if (State == s && Problem == problem) return;
        State = s;
        Problem = problem;
        Changed?.Invoke();
    }

    static bool _ticking;
    static bool _paused;
    /// <summary>Stops recording until set back to false (used by the cost test). Takes effect right away.</summary>
    public static bool Paused { get => _paused; set { _paused = value; Tick(); } }
    static int _stalls;
    static IntPtr _screenOnly; // a game window that couldn't be recorded directly

    /// <summary>Windows 11 can record a window without the yellow border Windows 10 forces, so window capture is 11+ only.</summary>
    static bool CanRecordWindow => Environment.OSVersion.Version.Build >= 22000;

    static IntPtr WantWindow(string game)
    {
        if (game == null || S.Capture != "game" || !CanRecordWindow) return IntPtr.Zero;
        var w = GameDetector.GameWindow;
        return w == _screenOnly ? IntPtr.Zero : w;
    }

    static async void Tick()
    {
        // The timer fires every 3 s; the first start can take longer (FFmpeg tests the encoders), so never overlap.
        if (_ticking) return;
        _ticking = true;
        try { await TickCore(); }
        finally { _ticking = false; }
    }

    static async Task TickCore()
    {
        if (!S.Enabled) { StopRecorder(); Set(ClipsState.Off); return; }
        if (!Ffmpeg.Installed) { StopRecorder(); Set(ClipsState.NeedsSetup); return; }
        if (Paused) { StopRecorder(); return; } // the "what Clips costs" test is measuring without it

        var game = await Task.Run(GameDetector.RunningGame);
        _game = game;
        bool want = !S.OnlyWhileGaming || game != null;

        if (!want) { StopRecorder(); Set(ClipsState.WaitingForGame); return; }
        // Record the game's own window when possible (see ClipSettings.Capture).
        IntPtr window = WantWindow(game);
        if (_rec != null && _rec.Window != IntPtr.Zero && window != _rec.Window && !_rec.Stalled && _rec.Running)
            StopRecorder(); // the game opened a new window (or a different game started): follow it

        if (_rec != null && (_rec.Stalled || !_rec.Running) && _rec.Window != IntPtr.Zero)
        {
            // Recording just the game's window didn't work here: use the whole screen for this game from now on.
            _screenOnly = _rec.Window;
            StopRecorder();
            window = IntPtr.Zero;
        }
        else if (_rec != null && _rec.Stalled)
        {
            // The picture stopped but FFmpeg is still running (usually a display mode change as a match loads, or a
            // switch to exclusive fullscreen). Restart right away so the replay follows the new mode.
            StopRecorder();
            _stalls++;
            if (_stalls >= 5)
            {
                Set(ClipsState.Problem, "Windows stopped giving Pulse the game's picture. Set the game to borderless or windowed fullscreen in its video settings, then turn Clips off and on.");
                _stalls = 0;
                _retryAfter = DateTime.Now.AddSeconds(30);
                return;
            }
        }
        else if (_rec != null && _rec.Running)
        {
            if (_rec.BufferSeconds > 30) _stalls = 0; // recording fine for a while: forget earlier stalls
            Set(ClipsState.Recording);
            return;
        }

        if (_rec != null && !_rec.Running)
        {
            // FFmpeg stopped on its own (display mode change, driver reset, device lost): try again a few times.
            var why = _rec.LastError;
            StopRecorder();
            _failures++;
            _retryAfter = DateTime.Now.AddSeconds(Math.Min(5 * _failures, 30));
            if (_failures >= 4) { Set(ClipsState.Problem, Explain(why)); return; }
        }
        if (DateTime.Now < _retryAfter) return;

        try
        {
            var encoders = await Encoders.Available();
            var enc = Encoders.Pick(encoders, S.Codec);
            if (enc == null) { Set(ClipsState.Problem, "FFmpeg didn't find a video encoder that works on this PC."); return; }
            _rec = new ReplayRecorder(S, enc, window, game != null ? GameDetector.GamePid : 0);
            _rec.Start();
            Set(ClipsState.Recording);
        }
        catch (Exception ex)
        {
            StopRecorder();
            _failures++;
            Set(ClipsState.Problem, ex.Message);
        }
    }

    static string Explain(string ffmpegError)
    {
        var e = ffmpegError ?? "";
        if (e.Contains("DuplicateOutput", StringComparison.OrdinalIgnoreCase) || e.Contains("ddagrab", StringComparison.OrdinalIgnoreCase))
            return "Windows wouldn't share the screen for recording. Another recorder may be using it, or the display picked in settings isn't connected.";
        if (e.Contains("nvenc", StringComparison.OrdinalIgnoreCase) || e.Contains("amf", StringComparison.OrdinalIgnoreCase) || e.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return "The graphics card's encoder refused to start. Updating the graphics driver usually fixes this; or try H.264 in the settings.";
        return e.Length > 0 ? "Recording stopped: " + e : "Recording stopped unexpectedly.";
    }

    static void StopRecorder()
    {
        var r = _rec;
        _rec = null;
        _lastCpuAt = default;
        if (r != null) Task.Run(r.Dispose);
    }

    /// <summary>Saves the instant replay as a clip. Safe to call from anywhere (hotkey, tray, button).</summary>
    public static Task<string> SaveClip() => SaveClip(S.ReplaySeconds);

    public static async Task<string> SaveClip(int seconds)
    {
        if (_saving) return null;
        if (_rec == null || !_rec.Running)
        {
            if (S.Enabled) ClipToast.Show(Loc.T("Nothing to save yet"), Loc.T(S.OnlyWhileGaming ? "Clips records while a game is running." : "The replay is starting."), false);
            return null;
        }
        _saving = true;
        try
        {
            var path = await _rec.Save(S.ClipsFolder, _game ?? "Clip", seconds);
            Feedback.Saved(S, seconds);
            _ = Task.Run(async () =>
            {
                try { await ClipLibrary.MakeThumbnail(path); } catch { }
                try { ClipLibrary.EnforceLimit(S); } catch { }
                Application.Current?.Dispatcher.Invoke(() => Saved?.Invoke(path));
            });
            TweakEngine.AddLog("clips", $"Saved a clip: {Path.GetFileName(path)}");
            return path;
        }
        catch (Exception ex)
        {
            ClipToast.Show(Loc.T("Couldn't save the clip"), Loc.T(ex.Message), false);
            return null;
        }
        finally { _saving = false; }
    }

    public static bool SessionOk { get; private set; } = true;
    public static bool MarkOk { get; private set; } = true;
    public static bool SessionActive => _rec?.SessionActive == true;
    public static double SessionSeconds => _rec?.SessionSeconds ?? 0;

    /// <summary>Starts or stops a long recording (hotkey, or the button on the Clips page).</summary>
    public static async Task<string> ToggleSession()
    {
        if (_rec == null || !_rec.Running)
        {
            if (S.Enabled) ClipToast.Show(Loc.T("Nothing to record yet"), Loc.T(S.OnlyWhileGaming ? "Clips records while a game is running." : "The replay is starting."), false);
            return null;
        }
        if (!_rec.SessionActive)
        {
            try
            {
                _rec.StartSession(S.ClipsFolder, _game ?? "Recording");
                ClipToast.Show(Loc.T("Recording started"), Loc.F("Press {0} again to stop.", HotkeyText(S.SessionModifiers, S.SessionKey)), true);
                TweakEngine.AddLog("clips", "Started a long recording");
                Changed?.Invoke();
            }
            catch (Exception ex) { ClipToast.Show(Loc.T("Couldn't start recording"), Loc.T(ex.Message), false); }
            return null;
        }
        ClipToast.Show(Loc.T("Finishing the recording…"), "", true);
        try
        {
            var path = await _rec.StopSession(S.ClipsFolder);
            Feedback.Saved(S, 0, Loc.T("Recording saved"));
            TweakEngine.AddLog("clips", $"Saved a recording: {Path.GetFileName(path)}");
            _ = Task.Run(async () =>
            {
                try { await ClipLibrary.MakeThumbnail(path); } catch { }
                Application.Current?.Dispatcher.Invoke(() => Saved?.Invoke(path));
            });
            Changed?.Invoke();
            return path;
        }
        catch (Exception ex)
        {
            ClipToast.Show(Loc.T("Couldn't save the recording"), Loc.T(ex.Message), false);
            return null;
        }
    }

    /// <summary>Marks this moment in the replay (and any long recording).</summary>
    public static void Bookmark()
    {
        if (_rec == null || !_rec.Running) return;
        _rec.AddBookmark();
        if (S.PlaySound) try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        if (S.ShowSavedPopup) ClipToast.Show(Loc.T("Bookmarked"), Loc.T("It shows as a marker when you open the clip."), true);
    }

    static bool _shooting;

    /// <summary>Saves the current game frame as a PNG.</summary>
    public static async Task<string> SaveScreenshot()
    {
        if (_shooting) return null;
        if (_rec == null || !_rec.Running)
        {
            if (S.Enabled) ClipToast.Show(Loc.T("Nothing to save yet"), Loc.T(S.OnlyWhileGaming ? "Clips records while a game is running." : "The replay is starting."), false);
            return null;
        }
        _shooting = true;
        try
        {
            var path = await _rec.SaveScreenshot(S.ScreenshotsFolder, _game ?? "Screenshot");
            Feedback.Screenshot(S);
            TweakEngine.AddLog("clips", $"Saved a screenshot: {Path.GetFileName(path)}");
            return path;
        }
        catch (Exception ex)
        {
            ClipToast.Show(Loc.T("Couldn't save the screenshot"), Loc.T(ex.Message), false);
            return null;
        }
        finally { _shooting = false; }
    }

    // ───────── Hotkey ─────────

    const int HotkeyId = 0x5011, QuickId = 0x5012, ShotId = 0x5013, SessionId = 0x5014, MarkId = 0x5015;
    const int WM_HOTKEY = 0x0312;
    const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    static void RegisterHotkey()
    {
        if (_hotkeyWindow == null)
        {
            // A message-only window: invisible, never activated, exists only to receive the hotkey.
            _hotkeyWindow = new HwndSource(new HwndSourceParameters("Pulse clips hotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            _hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_HOTKEY)
                {
                    int id = w.ToInt32();
                    if (id == HotkeyId) { handled = true; _ = SaveClip(); }
                    else if (id == QuickId) { handled = true; _ = SaveClip(S.QuickSeconds); }
                    else if (id == ShotId) { handled = true; _ = SaveScreenshot(); }
                    else if (id == SessionId) { handled = true; _ = ToggleSession(); }
                    else if (id == MarkId) { handled = true; Bookmark(); }
                }
                return IntPtr.Zero;
            });
        }
        foreach (var id in new[] { HotkeyId, QuickId, ShotId, SessionId, MarkId }) UnregisterHotKey(_hotkeyWindow.Handle, id);
        SessionOk = !(S.Enabled && S.SessionHotkey) || RegisterHotKey(_hotkeyWindow.Handle, SessionId, S.SessionModifiers | MOD_NOREPEAT, S.SessionKey);
        MarkOk = !(S.Enabled && S.BookmarkHotkey) || RegisterHotKey(_hotkeyWindow.Handle, MarkId, S.BookmarkModifiers | MOD_NOREPEAT, S.BookmarkKey);
        _hotkeyOk = S.Enabled && RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, S.HotkeyModifiers | MOD_NOREPEAT, S.HotkeyKey);
        QuickOk = !(S.Enabled && S.QuickClip) || RegisterHotKey(_hotkeyWindow.Handle, QuickId, S.QuickModifiers | MOD_NOREPEAT, S.QuickKey);
        ShotOk = !(S.Enabled && S.Screenshot) || RegisterHotKey(_hotkeyWindow.Handle, ShotId, S.ScreenshotModifiers | MOD_NOREPEAT, S.ScreenshotKey);
    }

    /// <summary>"Alt+F10" for the current hotkey.</summary>
    public static string HotkeyText(int mods, int key)
    {
        var parts = new List<string>();
        if ((mods & 2) != 0) parts.Add("Ctrl");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 4) != 0) parts.Add("Shift");
        parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey(key).ToString());
        return string.Join("+", parts);
    }

    // ───────── Start with Windows ─────────

    const string TaskName = "Pulse Optimizer clips";

    /// <summary>Starts Pulse in the tray at sign-in (with the admin rights it already has, so no prompt).</summary>
    public static bool SetStartWithWindows(bool on)
    {
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var exe = Environment.ProcessPath;
        var r = on
            ? Shell.Run(schtasks, $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /DELAY 0000:30")
            : Shell.Run(schtasks, $"/Delete /F /TN \"{TaskName}\"");
        return r.Code == 0 || !on;
    }
}

/// <summary>The short sound and the small popup that confirm a clip was saved, without taking focus from the game.</summary>
static class Feedback
{
    static byte[] _blip;

    public static void Saved(ClipSettings s, int seconds, string title = null)
    {
        if (s.PlaySound) Play();
        if (s.ShowSavedPopup) ClipToast.Show(title ?? Loc.T("Clip saved"), seconds > 0 ? Loc.F("Last {0} seconds", seconds) : "", true);
    }

    public static void Screenshot(ClipSettings s)
    {
        if (s.PlaySound) Play();
        if (s.ShowSavedPopup) ClipToast.Show(Loc.T("Screenshot saved"), Loc.T("In your clips folder, under Screenshots"), true);
    }

    static void Play()
    {
        try
        {
            _blip ??= Blip();
            var player = new System.Media.SoundPlayer(new MemoryStream(_blip));
            player.Play();
        }
        catch { }
    }

    /// <summary>A soft two-note chime, made in code so there's no sound file to ship.</summary>
    internal static byte[] Blip()
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (freq, ms) in new[] { (880.0, 70), (1320.0, 110) })
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Exp(-3.0 * i / n);
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / rate) * env * 9000));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var x in samples) w.Write(x);
        return ms2.ToArray();
    }
}
