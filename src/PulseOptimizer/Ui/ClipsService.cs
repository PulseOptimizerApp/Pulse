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
    public static long MemoryBytes => _rec?.MemoryBytes ?? 0;
    /// <summary>Everything clipping costs in memory right now: Pulse itself plus its recorder, as Task Manager counts it.</summary>
    public static long TotalMemory
    {
        get
        {
            using var me = Process.GetCurrentProcess();
            return Native.TaskManagerMemory(me) + (_rec?.RecorderMemory ?? 0);
        }
    }
    public static bool OnDisk => _rec?.OnDisk == true;
    // How far Pulse stepped down the video path after one gave no picture (see ReplayRecorder.VideoLevel).
    static int _videoLevel;
    static int _giveUps;
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
    static double _screenAspect;

    public static void Init()
    {
        if (_timer != null) return;
        // The screen's real shape, for stretched resolutions (read once; reading monitors takes a moment).
        _ = Task.Run(() =>
        {
            try
            {
                var m = Displays.Enumerate().FirstOrDefault(x => x.IsPrimary);
                if (m?.Native is { Width: > 0, Height: > 0 } n) _screenAspect = n.Width / (double)n.Height;
            }
            catch { }
        });
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
        _voice?.Dispose(); _voice = null; VoiceError = null;
        _failures = 0;
        _stalls = 0;
        _screenOnly = IntPtr.Zero;
        _videoLevel = 0;
        _giveUps = 0;
        _weakTriedWindow = false;
        _retryAfter = default;
        RegisterHotkey();
        Tick();
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        StopRecorder();
        _input?.Dispose(); _input = null;
        _voice?.Dispose(); _voice = null;
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

    // Integrated graphics: record the screen (Desktop Duplication copies only 30 or 60 frames a second), not the game's
    // window (window capture can copy every frame the game draws, 350 a second in Roblox, which a small chip can't spare).
    // If the screen gives no picture for this game, window capture is tried after all.
    static bool _weakTriedWindow;

    static IntPtr WantWindow(string game)
    {
        if (game == null || S.Capture != "game" || !CanRecordWindow) return IntPtr.Zero;
        if (SystemInfo.SharedGraphicsOnly && !_weakTriedWindow) return IntPtr.Zero;
        var w = GameDetector.GameWindow;
        return w == _screenOnly ? IntPtr.Zero : w;
    }

    static async void Tick()
    {
        // The timer fires every 3 s; the first start can take longer (FFmpeg tests the encoders), so never overlap.
        if (_ticking) return;
        _ticking = true;
        try { await TickCore(); }
        finally
        {
            _ticking = false;
            try { SyncInput(); } catch { }
            try { SyncVoice(); } catch { }
        }
    }

    static async Task TickCore()
    {
        if (!S.Enabled) { StopRecorder(); Set(ClipsState.Off); return; }
        if (!Ffmpeg.Installed) { StopRecorder(); Set(ClipsState.NeedsSetup); return; }
        if (Paused) { StopRecorder(); return; } // the "what Clips costs" test is measuring without it

        ApplyWeakPcStart();
        ReplayRecorder.SharedGraphics = SystemInfo.SharedGraphicsOnly;
        var game = await Task.Run(GameDetector.RunningGame);
        _game = game;
        bool want = !S.OnlyWhileGaming || game != null;

        if (!want) { StopRecorder(); Set(ClipsState.WaitingForGame); return; }
        // Record the game's own window when possible (see ClipSettings.Capture).
        IntPtr window = WantWindow(game);
        if (_rec != null && _rec.Window != IntPtr.Zero && window != _rec.Window && !_rec.Stalled && _rec.Running)
            StopRecorder(); // the game opened a new window (or a different game started): follow it

        if (_rec != null && _rec.Stalled && _rec.Window == IntPtr.Zero && SystemInfo.SharedGraphicsOnly && !_weakTriedWindow && game != null && CanRecordWindow)
        {
            _weakTriedWindow = true; // the screen gave no picture (exclusive fullscreen): try the game's window
            StopRecorder();
            window = WantWindow(game);
        }
        else if (_rec != null && (_rec.Stalled || !_rec.Running) && !_rec.HadVideo && _videoLevel < 3)
        {
            // No picture at all at this quality (some drivers or window sizes don't take the fastest path, which
            // used to leave Balanced and High recording nothing): try the next, more compatible way.
            TweakEngine.AddLog("clips", $"No picture at video level {_videoLevel}: {_rec.LastError}");
            StopRecorder();
            _videoLevel++;
        }
        else if (_rec != null && (_rec.Stalled || !_rec.Running) && _rec.Window != IntPtr.Zero)
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
                // Each restart makes the game hitch for a moment, so give up for longer each time instead of every 30 s.
                _giveUps++;
                _retryAfter = DateTime.Now.AddSeconds(Math.Min(30 * (1 << Math.Min(_giveUps - 1, 5)), 600));
                return;
            }
        }
        else if (_rec != null && _rec.Running && _rec.FallingBehind && StepDown())
        {
            // This PC can't make every frame at these settings (the clip would stutter): record a little smaller,
            // then at 30 FPS, so clips stay smooth. Remembered for this PC; picking a quality again clears it.
            TweakEngine.AddLog("clips", $"Recording couldn't keep up ({_rec.Frames.Rate(_rec.Seconds, 10):0} of {_rec.TargetFps} FPS): now {(S.FitHeight > 0 ? S.FitHeight + "p" : "full size")}{(S.FitFps > 0 ? $" at {S.FitFps} FPS" : "")}");
            TweakEngine.Save();
            StopRecorder();
            Changed?.Invoke();
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
            _rec = new ReplayRecorder(S, enc, window, game != null ? GameDetector.GamePid : 0)
            {
                ScreenAspect = _screenAspect, VideoLevel = _videoLevel,
                TargetFps = Math.Clamp(S.FitFps > 0 ? Math.Min(S.Fps, S.FitFps) : S.Fps, 24, 240),
            };
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

    /// <summary>One step smaller: 1080p, then 720p, then 720p at 30 FPS. False when there's nothing left to lower.</summary>
    static bool StepDown()
    {
        int h = S.FitHeight > 0 ? S.FitHeight : S.Resolution switch { "1440" => 1440, "1080" => 1080, "720" => 720, _ => 1440 };
        if (h > 1080) { S.FitHeight = 1080; return true; }
        if (h > 720) { S.FitHeight = 720; return true; }
        if (S.FitFps == 0 && S.Fps > 30) { S.FitFps = 30; return true; }
        return false;
    }

    /// <summary>What Pulse lowered on this PC to keep clips smooth, for the Clips page (or null).</summary>
    public static string FitNote => S.FitHeight == 0 && S.FitFps == 0 ? null
        : Loc.F("Lowered to {0} so clips stay smooth on this PC. Pick a quality again to try higher.",
            (S.FitHeight > 0 ? S.FitHeight + "p" : Loc.T("full size")) + (S.FitFps > 0 ? $" · {S.FitFps} FPS" : ""))
          + (SystemInfo.SharedGraphicsOnly ? "\n" + Loc.T("Your PC has integrated graphics, so the game and recording share one chip. Capping the game's FPS to your screen's refresh rate (the Roblox page can do it for Roblox) leaves room for both: the game stays smooth and so do your clips.") : "");

    /// <summary>
    /// The first time Clips runs on a PC with integrated graphics, start light: 720p at 30 FPS, the most a shared chip
    /// records smoothly while a game uses it. Picking a quality on the Clips page tries higher, and Pulse steps back
    /// down by itself if that stutters.
    /// </summary>
    static void ApplyWeakPcStart()
    {
        if (S.WeakPcStartApplied || !SystemInfo.SharedGraphicsOnly) return;
        S.WeakPcStartApplied = true;
        if (S.FitHeight == 0 || S.FitHeight > 720) S.FitHeight = 720;
        if (S.FitFps == 0 || S.FitFps > 30) S.FitFps = 30;
        TweakEngine.AddLog("clips", "Integrated graphics: Clips starts at 720p, 30 FPS so games keep their FPS and clips stay smooth");
        TweakEngine.Save();
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

    static InputCapture _input;
    static VoiceClip _voice;
    /// <summary>Why voice clipping isn't listening, if it was turned on and couldn't start.</summary>
    public static string VoiceError { get; private set; }

    /// <summary>Voice clipping listens while the replay records (if turned on).</summary>
    static void SyncVoice()
    {
        bool want = S.VoiceClip && _rec != null && _rec.Running;
        if (want && _voice == null && VoiceError == null)
        {
            _voice = VoiceClip.Start(S.VoicePhrase, () => _ = SaveClip(), out var err);
            VoiceError = err;
        }
        else if (!want && _voice != null) { _voice.Dispose(); _voice = null; }
    }

    /// <summary>Input recording follows the replay: on while it records (if the overlay is turned on).</summary>
    static void SyncInput()
    {
        bool want = S.InputOverlay && _rec != null && _rec.Running;
        if (want && _input == null) { try { _input = new InputCapture(); _input.Start(); } catch { _input = null; } }
        else if (!want && _input != null) { _input.Dispose(); _input = null; }
        if (_input != null) _input.KeepSeconds = _rec?.SessionActive == true ? 6 * 3600 : S.ReplaySeconds + 60;
    }

    static void SaveInputs(string clip)
    {
        if (_input == null || _rec == null || clip == null) return;
        try { var (from, to) = _rec.LastRange; _input.Slice(from, to).Save(clip); } catch { }
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
        Feedback.Pressed(S); // the sound plays the moment the key is pressed
        try
        {
            var path = await _rec.Save(S.ClipsFolder, _game ?? "Clip", seconds);
            SaveInputs(path);
            Feedback.Saved(S, seconds);
            Saved?.Invoke(path); // show it in the library now; the picture follows
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
            Feedback.Failed(S);
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
                SyncInput();
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
            SaveInputs(path);
            SyncInput();
            Feedback.Saved(S, 0, Loc.T("Recording saved"), playSound: true);
            TweakEngine.AddLog("clips", $"Saved a recording: {Path.GetFileName(path)}");
            Saved?.Invoke(path);
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
        Feedback.ScreenshotPressed(S); // the shutter sound plays the moment the key is pressed
        try
        {
            var path = await _rec.SaveScreenshot(S.ScreenshotsFolder, _game ?? "Screenshot");
            Feedback.Screenshot(S);
            TweakEngine.AddLog("clips", $"Saved a screenshot: {Path.GetFileName(path)}");
            return path;
        }
        catch (Exception ex)
        {
            Feedback.Failed(S);
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
        if (S.ScreenshotKey == 0x2C) { S.ScreenshotKey = 0x76; S.ScreenshotModifiers = 1; } // never take Print Screen from the Snipping Tool (back to Alt+F7)
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
    static byte[] _blip, _shutter;

    /// <summary>Right when the hotkey is pressed: the sound plays at once, before saving finishes, so it feels instant.</summary>
    public static void Pressed(ClipSettings s)
    {
        if (s.PlaySound) Play();
    }

    /// <summary>When the clip is on disk: the popup (the sound already played on the key press).</summary>
    public static void Saved(ClipSettings s, int seconds, string title = null, bool playSound = false)
    {
        if (s.PlaySound && playSound) Play();
        if (s.ShowSavedPopup) ClipToast.Show(title ?? Loc.T("Clip saved"), seconds > 0 ? Loc.F("Last {0} seconds", seconds) : "", true);
    }

    /// <summary>Saving failed after the sound already said yes: a different, lower sound so it's clear.</summary>
    public static void Failed(ClipSettings s)
    {
        if (s.PlaySound) try { System.Media.SystemSounds.Hand.Play(); } catch { }
    }

    public static void Screenshot(ClipSettings s)
    {
        if (s.ShowSavedPopup) ClipToast.Show(Loc.T("Screenshot saved"), Loc.T("In your clips folder, under Screenshots"), true);
    }

    static void Play(bool screenshot = false)
    {
        try
        {
            var wav = screenshot ? (_shutter ??= Shutter()) : (_blip ??= Blip());
            new System.Media.SoundPlayer(new MemoryStream(wav)).Play();
        }
        catch { }
    }

    /// <summary>Right when the screenshot hotkey is pressed: the shutter sound plays at once.</summary>
    public static void ScreenshotPressed(ClipSettings s)
    {
        if (s.PlaySound) Play(screenshot: true);
    }

    const int SoundRate = 48000;

    static double[] Mallet(double f, double seconds, double decay, double bright)
    {
        int n = (int)(SoundRate * seconds);
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SoundRate;
            double attack = Math.Min(1, t / 0.0015);
            x[i] = attack * (Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t / decay)
                + bright * Math.Sin(2 * Math.PI * f * 3.98 * t) * Math.Exp(-t / (decay * 0.25))
                + 0.12 * Math.Sin(2 * Math.PI * f * 10.0 * t) * Math.Exp(-t / 0.006));
        }
        return x;
    }

    /// <summary>A soft click like a camera shutter blade: a very short burst of smoothed noise.</summary>
    static double[] Click(int seed)
    {
        var rng = new Random(seed);
        int n = (int)(SoundRate * 0.01);
        var raw = new double[n];
        for (int i = 0; i < n; i++)
        {
            double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()); // normal noise
            raw[i] = g * Math.Exp(-(i / (double)SoundRate) / 0.0015);
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) { double sum = 0; int c = 0; for (int j = i - 2; j <= i + 2; j++) if (j >= 0 && j < n) { sum += raw[j]; c++; } x[i] = sum / c * 0.55; }
        return x;
    }

    static void Add(double[] into, double[] what, int at, double gain = 1)
    {
        for (int i = 0; i < what.Length && at + i < into.Length; i++) into[at + i] += what[i] * gain;
    }

    /// <summary>A short room echo mixed in lightly, a 3 ms fade at the end, then a 48 kHz stereo 16-bit WAV.</summary>
    static byte[] Finish(double[] dry, double wetMix)
    {
        int len = dry.Length + (int)(SoundRate * 0.35);
        var wet = new double[len];
        foreach (var (d, g) in new[] { (0.0297, 0.72), (0.0371, 0.68), (0.0411, 0.64), (0.0437, 0.6) })
        {
            int delay = (int)(SoundRate * d);
            var y = new double[len];
            for (int i = delay; i < len; i++) y[i] = (i - delay < dry.Length ? dry[i - delay] : 0) + g * 0.7 * y[i - delay];
            for (int i = 0; i < len; i++) wet[i] += y[i] * 0.25;
        }
        var mix = new double[len];
        double peak = 1e-9;
        for (int i = 0; i < len; i++) { mix[i] = (i < dry.Length ? dry[i] : 0) * (1 - wetMix) + wet[i] * wetMix; peak = Math.Max(peak, Math.Abs(mix[i])); }
        int fade = (int)(SoundRate * 0.003);
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int bytes = len * 4;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(SoundRate); w.Write(SoundRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        for (int i = 0; i < len; i++)
        {
            double gain = 0.55 / peak * (i >= len - fade ? (len - i) / (double)fade : 1);
            double l = mix[i], r = i >= 24 ? mix[i - 24] : 0; // a hair of width: the right side is 0.5 ms later
            w.Write((short)(Math.Clamp(l * gain, -1, 1) * 32767));
            w.Write((short)(Math.Clamp(r * gain, -1, 1) * 32767));
        }
        return ms2.ToArray();
    }

    /// <summary>"Bloop" (clip saved): two quick rising mallet notes, C6 then G6 55 ms later, with a short room echo.</summary>
    internal static byte[] Blip()
    {
        var a = Mallet(1046.5, 0.18, 0.07, 0.25);
        var b = Mallet(1568.0, 0.30, 0.11, 0.25);
        int off = (int)(SoundRate * 0.055);
        var dry = new double[off + b.Length];
        Add(dry, a, 0);
        Add(dry, b, off);
        return Finish(dry, 0.16);
    }

    /// <summary>Screenshot: a soft two-part shutter click, then one bright note from the same family as Bloop (G6).</summary>
    internal static byte[] Shutter()
    {
        var dry = new double[(int)(SoundRate * 0.45)];
        Add(dry, Click(1), 0);
        Add(dry, Click(2), (int)(SoundRate * 0.045), 0.8);
        Add(dry, Mallet(1568.0, 0.3, 0.09, 0.22), (int)(SoundRate * 0.06), 0.9);
        return Finish(dry, 0.15);
    }
}
