using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulse.Core;
using Pulse.Core.Input;
using Pulse.Core.Measurement;

namespace Pulse.Ui;

/// <summary>
/// Runs the user's macros while Pulse is open or in the tray. Off until the user turns macros on. Every 2 seconds it
/// checks what's running: a kernel anti-cheat turns macros off (unless the user allowed them there), and a
/// competitive shooter blocks mouse movement.
/// </summary>
public static class MacroService
{
    public static readonly object Gate = new();
    static MacroEngine _engine;
    static System.Threading.Timer _guard;

    public static MacroOptions Options => TweakEngine.State.MacroOptions ??= new MacroOptions();

    /// <summary>Every macro. Change it only while holding <see cref="Gate"/>, then call <see cref="Save"/>.</summary>
    public static List<MacroDef> List
    {
        get
        {
            var st = TweakEngine.State;
            if (st.MacroList == null)
            {
                // Carry the first version's single click macro over as the first macro.
                st.MacroList = new List<MacroDef> { MacroPolicy.FromOldClicker(st.Macros ?? new MacroSettings()) };
                if (st.Macros?.TriggerKey == 0) st.MacroList[0].TriggerKey = 0x05;
            }
            return st.MacroList;
        }
    }

    static MacroDef[] Snapshot() { lock (Gate) return List.ToArray(); }

    /// <summary>The anti-cheat that's keeping macros off, or null.</summary>
    public static string BlockedBy { get; private set; }
    /// <summary>The competitive shooter that's blocking mouse movement, or null.</summary>
    public static string Shooter => _engine?.MoveBlockedBy;
    public static bool Armed => _engine?.Armed == true;
    public static MacroEngine Engine => _engine;
    /// <summary>Raised on the UI thread when macros turn on or off, or one starts or stops.</summary>
    public static event Action Changed;

    public static void Save() { lock (Gate) TweakEngine.Save(); }

    public static void Init()
    {
        _engine = new MacroEngine(Snapshot, () => Options) { AllowedHere = AllowedHere };
        _engine.Changed += reason => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (reason == "panic" && Options.Notifications)
                ClipToast.Show(Loc.T("Macros stopped"), Loc.F("You pressed {0}. Turn them back on from the Macros page.", Loc.T(Keys.Display(Options.PanicKey))), true);
            if (!_engine.Armed) _guard?.Change(Timeout.Infinite, Timeout.Infinite);
            Changed?.Invoke();
        });
        _guard = new System.Threading.Timer(_ => Guard(), null, Timeout.Infinite, Timeout.Infinite);
        if (Options.OnAtStart) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Arm(quiet: true));
    }

    /// <summary>Turns macros on. Returns why it can't, or null.</summary>
    public static string Arm(bool quiet = false)
    {
        var running = MacroPolicy.RunningProcesses();
        var ac = MacroPolicy.AntiCheatIn(running);
        if (ac != null && !Options.AllowWithAntiCheat)
        {
            BlockedBy = ac;
            return Loc.F("{0} is running, so macros stay off. Some games with it allow macros (League of Legends, Fortnite) and some ban for them: if yours allows them, switch on \"Allow in games with anti-cheat\" below.", ac);
        }
        BlockedBy = null;
        _engine.MoveBlockedBy = MacroPolicy.ShooterIn(running);
        _engine.Arm();
        _guard.Change(2000, 2000);
        var list = Snapshot();
        int on = list.Count(m => m.Enabled);
        if (!quiet && Options.Notifications)
            ClipToast.Show(Loc.T("Macros on"), Loc.F("{0} macros ready · {1} stops them all", on, Loc.T(Keys.Display(Options.PanicKey))), true);
        TweakEngine.AddLog("macro", $"Macros on: {on} enabled" + (ac != null ? $" (allowed with {ac})" : ""));
        return null;
    }

    public static void Disarm()
    {
        _engine?.Disarm();
        _guard?.Change(Timeout.Infinite, Timeout.Infinite);
        Changed?.Invoke();
    }

    public static void Shutdown()
    {
        _guard?.Dispose();
        _engine?.Dispose();
    }

    /// <summary>Every 2 seconds while on (on a background thread): anti-cheats and shooters.</summary>
    static void Guard()
    {
        if (_engine == null || !_engine.Armed) return;
        HashSet<string> running;
        try { running = MacroPolicy.RunningProcesses(); } catch { return; }
        var ac = MacroPolicy.AntiCheatIn(running);
        if (ac != null && !Options.AllowWithAntiCheat)
        {
            _engine.Disarm("anticheat");
            BlockedBy = ac;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ClipToast.Show(Loc.T("Macros off"), Loc.F("{0} started. Pulse turned macros off to keep your account safe.", ac), false);
                Changed?.Invoke();
            });
            return;
        }
        string shooter = MacroPolicy.ShooterIn(running);
        if (shooter != _engine.MoveBlockedBy)
        {
            _engine.MoveBlockedBy = shooter;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
        if (Snapshot().Any(m => m.Enabled && m.Where == "game"))
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { try { GameDetector.RunningGame(); } catch { } });
    }

    // ───────── where a macro may act ─────────

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long _fgCheckedAt = -1000;
    static uint _fgPid;
    static string _fgName = "";
    static readonly Dictionary<uint, string> Names = new();

    static bool AllowedHere(MacroDef m)
    {
        if (m.Where == "anywhere") return true;
        RefreshForeground();
        if (m.Where == "game") return _fgPid != 0 && _fgPid == GameDetector.GamePid;
        if (m.Where == "app") return !string.IsNullOrWhiteSpace(m.App) && string.Equals(_fgName, CleanApp(m.App), StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public static string CleanApp(string app)
    {
        app = (app ?? "").Trim();
        return app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
    }

    static void RefreshForeground()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _fgCheckedAt) < 100) return;
        Interlocked.Exchange(ref _fgCheckedAt, now);
        var w = GetForegroundWindow();
        uint pid = 0;
        if (w != IntPtr.Zero) GetWindowThreadProcessId(w, out pid);
        if (pid == _fgPid) return;
        _fgPid = pid;
        lock (Names)
        {
            if (!Names.TryGetValue(pid, out var name))
            {
                try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { name = ""; }
                if (Names.Count > 200) Names.Clear();
                Names[pid] = name;
            }
            _fgName = name;
        }
    }

    /// <summary>The program in front right now, for "only in this program" (used by the page's Pick button).</summary>
    public static string ForegroundApp()
    {
        Interlocked.Exchange(ref _fgCheckedAt, -1000);
        _fgPid = 0;
        RefreshForeground();
        return _fgName;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
