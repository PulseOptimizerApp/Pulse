using System.Runtime.InteropServices;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Input;
using Pulse.Core.Measurement;

namespace Pulse.Ui;

/// <summary>
/// Runs the click macro while Pulse is open or in the tray. Off until the user arms it on the Macros page. Keeps the
/// safeguards cheap enough to check between every click: the anti-cheat check runs every 2 seconds and the
/// "only in my game" check 4 times a second, and the macro reads the cached answers.
/// </summary>
public static class MacroService
{
    public static MacroSettings S => TweakEngine.State.Macros ??= new MacroSettings();
    static ClickMacro _macro;
    static DispatcherTimer _timer;
    static volatile bool _allowed = true;
    static int _ticks;

    /// <summary>The anti-cheat that's stopping the macro right now, or null.</summary>
    public static string BlockedBy { get; private set; }
    public static bool Armed => _macro?.Armed == true;
    public static bool Active => _macro?.Active == true;
    public static ClickMacro Macro => _macro;
    /// <summary>Raised on the UI thread when the macro is armed, starts or stops clicking, or is blocked.</summary>
    public static event Action Changed;

    public static void Init()
    {
        _macro = new ClickMacro(() => S, () => _allowed);
        _macro.StateChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_macro.Armed && _wasArmed) ClipToast.Show(Loc.T("Macro off"), Loc.T("Turn it back on from the Macros page."), true);
            _wasArmed = _macro.Armed;
            Changed?.Invoke();
        });
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Check();
    }

    static bool _wasArmed;

    /// <summary>Arms the macro (the trigger then makes it click). Returns why it can't, or null.</summary>
    public static string Arm()
    {
        BlockedBy = ClickMacro.AntiCheatRunning();
        if (BlockedBy != null) return Loc.F("{0} is running, so the macro stays off: anti-cheats like it can ban accounts for macro clicks.", BlockedBy);
        _macro.Arm();
        _timer.Start();
        Check();
        ClipToast.Show(Loc.T("Macro on"), Loc.F("{0} {1} to click · Esc stops it", Loc.T(S.Activation == "toggle" ? "Press" : "Hold"), KeyName(S.TriggerKey)), true);
        TweakEngine.AddLog("macro", $"Click macro on: {S.Mode}, {S.Cps:0} CPS");
        return null;
    }

    public static void Disarm()
    {
        _macro?.Disarm();
        _timer?.Stop();
        Changed?.Invoke();
    }

    public static void Shutdown() { _timer?.Stop(); _macro?.Dispose(); }

    static void Check()
    {
        if (!_macro.Armed) { _timer.Stop(); return; }
        // Every 2 seconds: an anti-cheat that started after arming turns the macro off.
        if (_ticks++ % 8 == 0)
        {
            var ac = ClickMacro.AntiCheatRunning();
            if (ac != null)
            {
                BlockedBy = ac;
                _allowed = false;
                _macro.Disarm();
                ClipToast.Show(Loc.T("Macro off"), Loc.F("{0} started. Pulse turned the macro off to keep your account safe.", ac), false);
                Changed?.Invoke();
                return;
            }
            BlockedBy = null;
            if (S.OnlyInGame) GameDetector.RunningGame();
        }
        _allowed = !S.OnlyInGame || ForegroundIsGame();
    }

    static bool ForegroundIsGame()
    {
        var w = GetForegroundWindow();
        if (w == IntPtr.Zero) return false;
        GetWindowThreadProcessId(w, out var pid);
        return pid != 0 && pid == GameDetector.GamePid;
    }

    /// <summary>A key or mouse button as people call it.</summary>
    public static string KeyName(int vk) => vk switch
    {
        0x04 => Loc.T("Middle mouse"),
        0x05 => Loc.T("Mouse 4"),
        0x06 => Loc.T("Mouse 5"),
        _ => System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
