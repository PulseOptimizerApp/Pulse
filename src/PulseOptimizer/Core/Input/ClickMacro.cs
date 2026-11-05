using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Core.Input;

/// <summary>Everything the user picked on the Macros page. Saved in Pulse's state file.</summary>
public class MacroSettings
{
    /// <summary>"steady", "butterfly", "jitter" or "rhythm" (the user's own recorded clicking).</summary>
    public string Mode { get; set; } = "butterfly";
    /// <summary>Clicks per second asked for (the recorded rhythm keeps its own speed unless this is set to scale it).</summary>
    public double Cps { get; set; } = 16;
    /// <summary>How much each gap may vary, as a share (0 = metronome, 0.08 = natural, 0.15 = loose).</summary>
    public double Variation { get; set; } = 0.08;
    public string Button { get; set; } = "left";
    /// <summary>"hold" (clicks while the trigger is held) or "toggle" (press once to start, again to stop).</summary>
    public string Activation { get; set; } = "hold";
    /// <summary>Virtual-key code of the trigger (0x05 = mouse side button 4).</summary>
    public int TriggerKey { get; set; } = 0x05;
    /// <summary>Only click while the game Pulse detected is the window in front.</summary>
    public bool OnlyInGame { get; set; }
    /// <summary>The user's own clicking, recorded on the test pad: (gap before the click, how long it was held), in ms.</summary>
    public List<double[]> Rhythm { get; set; } = new();
    /// <summary>Play the recorded rhythm at <see cref="Cps"/> instead of its own speed.</summary>
    public bool ScaleRhythm { get; set; }
}

/// <summary>
/// The timing of each click (how long until it, and how long the button is held). Pure maths, so it can be tested:
/// the engine just waits and presses as told.
/// </summary>
public sealed class ClickPattern
{
    readonly MacroSettings _s;
    readonly Random _rng;
    int _index;
    bool _secondOfPair;

    public ClickPattern(MacroSettings s, int seed = 0) { _s = s; _rng = seed == 0 ? new Random() : new Random(seed); }

    double Normal(double sd) // Box–Muller
    {
        double u1 = 1 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sd;
    }

    double Vary(double ms, double share) => Math.Max(ms * (1 + Math.Clamp(Normal(share), -2.5 * share, 2.5 * share)), 1);

    /// <summary>The next click: wait <c>Gap</c> ms after the previous press started, then hold for <c>Hold</c> ms.</summary>
    public (double Gap, double Hold) Next()
    {
        double cps = Math.Clamp(_s.Cps, 1, 50);
        double v = Math.Clamp(_s.Variation, 0, 0.4);
        switch (_s.Mode)
        {
            case "butterfly":
            {
                // Two fingers on one button: a quick pair, then a longer gap while both fingers come back up.
                // The pair's spacing is about a third of the pair's period, like real butterfly clicking.
                double period = 2000 / cps;
                double inPair = period * (0.30 + _rng.NextDouble() * 0.10);
                double gap = _secondOfPair ? inPair : period - inPair;
                _secondOfPair = !_secondOfPair;
                double g = Vary(gap, v);
                return (g, Math.Min(Vary(14, 0.25), g * 0.6));
            }
            case "jitter":
            {
                // Tensed-arm jitter clicking: fast, short presses, noticeably uneven.
                double g = Vary(1000 / cps, Math.Max(v, 0.18));
                return (g, Math.Min(Vary(18, 0.3), g * 0.55));
            }
            case "rhythm" when _s.Rhythm.Count >= 4:
            {
                var r = _s.Rhythm[_index++ % _s.Rhythm.Count];
                double scale = _s.ScaleRhythm ? RhythmCps(_s.Rhythm) / cps : 1;
                double g = Math.Max(r[0] * scale, 1);
                return (g, Math.Clamp(r[1] * Math.Min(scale, 1), 1, g * 0.8));
            }
            default:
            {
                double g = Vary(1000 / cps, v);
                return (g, Math.Min(Vary(22, 0.25), g * 0.5));
            }
        }
    }

    /// <summary>The speed of a recorded rhythm, in clicks per second.</summary>
    public static double RhythmCps(IReadOnlyList<double[]> rhythm)
    {
        double total = rhythm.Sum(r => r[0]);
        return total > 0 ? rhythm.Count * 1000 / total : 0;
    }
}

/// <summary>
/// Presses the mouse button on the pattern's timing while the trigger is held (or toggled on). Runs on its own
/// high-priority thread and waits with Windows' high-resolution timer, so 20 CPS is really 20, evenly, instead of the
/// 15 ms steps ordinary timers give. Clicks are sent with SendInput, like any other input program: nothing touches
/// a game's memory, and Windows marks them as injected input.
///
/// Safeguards: Esc stops it at once; it never runs while a kernel anti-cheat is running (Vanguard, Easy Anti-Cheat,
/// BattlEye, FACEIT), because those can ban an account for injected clicks whatever they were for.
/// </summary>
public sealed class ClickMacro : IDisposable
{
    readonly Func<MacroSettings> _settings;
    readonly Func<bool> _allowedNow;
    Thread _thread;
    volatile bool _stop, _armed;
    IntPtr _timer;

    /// <summary>Clicking right now (armed and the trigger is held or toggled on).</summary>
    public bool Active { get; private set; }
    public bool Armed => _armed;
    /// <summary>Times of the latest presses (ms on <see cref="Clock"/>), for the live speed and the timeline.</summary>
    public readonly Queue<double> Recent = new();
    public readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>Raised (on the macro thread) when it starts or stops clicking, or is disarmed.</summary>
    public event Action StateChanged;

    /// <param name="allowedNow">Checked between bursts: false (anti-cheat running, wrong window) means don't click.</param>
    public ClickMacro(Func<MacroSettings> settings, Func<bool> allowedNow)
    {
        _settings = settings;
        _allowedNow = allowedNow;
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    }

    public void Arm()
    {
        if (_armed) return;
        _armed = true;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pulse click macro", Priority = ThreadPriority.Highest };
        _thread.Start();
        StateChanged?.Invoke();
    }

    public void Disarm()
    {
        if (!_armed) return;
        _armed = false;
        _stop = true;
        try { if (Thread.CurrentThread != _thread) _thread?.Join(500); } catch { }
        StateChanged?.Invoke();
    }

    /// <summary>Clicks per second actually sent over the last second.</summary>
    public double LiveCps
    {
        get
        {
            lock (Recent)
            {
                double now = Clock.Elapsed.TotalMilliseconds;
                return Recent.Count(t => now - t <= 1000);
            }
        }
    }

    void Loop()
    {
        bool toggled = false, wasDown = false;
        ClickPattern pattern = null;
        double next = 0;
        while (!_stop)
        {
            // Esc stops everything at once.
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { _armed = false; _stop = true; SetActive(false); StateChanged?.Invoke(); return; }
            var s = _settings();
            bool down = (GetAsyncKeyState(s.TriggerKey) & 0x8000) != 0;
            if (s.Activation == "toggle" && down && !wasDown) toggled = !toggled;
            wasDown = down;
            bool want = (s.Activation == "toggle" ? toggled : down) && _allowedNow();
            if (!want)
            {
                if (Active) SetActive(false);
                pattern = null;
                Wait(2);
                continue;
            }
            if (!Active) { SetActive(true); pattern = new ClickPattern(s); next = Clock.Elapsed.TotalMilliseconds; }
            var (gap, hold) = pattern.Next();
            // The first press comes right away; each later one at its gap after the previous press.
            double now = Clock.Elapsed.TotalMilliseconds;
            if (next > now) Wait(next - now);
            double pressedAt = Clock.Elapsed.TotalMilliseconds;
            Press(s.Button, true);
            lock (Recent) { Recent.Enqueue(pressedAt); while (Recent.Count > 200) Recent.Dequeue(); }
            Wait(hold);
            Press(s.Button, false);
            next = pressedAt + gap;
        }
        SetActive(false);
    }

    void SetActive(bool on)
    {
        if (Active == on) return;
        Active = on;
        StateChanged?.Invoke();
    }

    /// <summary>Waits about <paramref name="ms"/> milliseconds, accurately (high-resolution timer, then a short spin).</summary>
    void Wait(double ms)
    {
        if (ms <= 0) return;
        double end = Clock.Elapsed.TotalMilliseconds + ms;
        if (_timer != IntPtr.Zero && ms > 1.2)
        {
            long due = -(long)((ms - 0.6) * 10_000); // relative, in 100 ns units; wake a little early, then spin
            if (SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)) WaitForSingleObject(_timer, 1000);
        }
        else if (ms > 2) Thread.Sleep((int)(ms - 1.5));
        while (Clock.Elapsed.TotalMilliseconds < end) Thread.SpinWait(40);
    }

    static void Press(string button, bool down)
    {
        uint flag = button == "right" ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP) : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flag } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Kernel anti-cheats that can ban for injected clicks. The macro won't run while one is running.</summary>
    public static readonly string[] AntiCheatProcesses = { "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitservice", "faceit_ac" };

    /// <summary>The anti-cheat running now, or null.</summary>
    public static string AntiCheatRunning()
    {
        foreach (var name in AntiCheatProcesses)
        {
            var found = Process.GetProcessesByName(name);
            bool any = found.Length > 0;
            foreach (var p in found) p.Dispose();
            if (any) return name switch
            {
                "vgc" or "vgtray" => "Riot Vanguard",
                "BEService" => "BattlEye",
                "FACEIT" or "faceitservice" or "faceit_ac" => "FACEIT",
                _ => "Easy Anti-Cheat",
            };
        }
        return null;
    }

    public void Dispose()
    {
        Disarm();
        if (_timer != IntPtr.Zero) { CloseHandle(_timer); _timer = IntPtr.Zero; }
    }

    // ───────── Windows ─────────

    const int VK_ESCAPE = 0x1B;
    const int INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04, MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10;
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public int type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
