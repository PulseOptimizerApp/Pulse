using System.Collections.Concurrent;
using System.Diagnostics;
using Pulse.Core.Input.Lua;

namespace Pulse.Core.Input;

/// <summary>A macro's log (script prints and errors), newest last.</summary>
public sealed class MacroLog
{
    readonly List<string> _lines = new();
    public event Action Changed;

    public void Add(string line)
    {
        lock (_lines)
        {
            if (line == "\f") _lines.Clear(); // ClearLog()
            else
            {
                foreach (var l in line.Split('\n')) _lines.Add($"{DateTime.Now:HH:mm:ss}  {l}");
                if (_lines.Count > 300) _lines.RemoveRange(0, _lines.Count - 300);
            }
        }
        Changed?.Invoke();
    }

    public string Text { get { lock (_lines) return string.Join("\n", _lines); } }
    public void Clear() { lock (_lines) _lines.Clear(); Changed?.Invoke(); }
}

/// <summary>
/// Runs every enabled macro while macros are on. One supervisor thread watches the user's physical keys and buttons
/// (never input sent by programs) and starts and stops each macro on its own thread, so a long sequence never delays
/// another macro's trigger. Stopping always lets go of every key and button a macro was holding.
/// </summary>
public sealed class MacroEngine : IDisposable
{
    readonly Func<MacroDef[]> _macros;
    readonly Func<MacroOptions> _options;
    public readonly PhysicalInput Phys = new();
    /// <summary>Is this macro allowed to act right now (its "where" setting)? Called often: keep it cheap.</summary>
    public Func<MacroDef, bool> AllowedHere = _ => true;
    /// <summary>The competitive shooter running (mouse movement is blocked), or null.</summary>
    public volatile string MoveBlockedBy;
    /// <summary>
    /// Set while macros are paused (an anti-cheat the user hasn't allowed is running). Macros stay on and carry on
    /// by themselves once it closes.
    /// </summary>
    public volatile string PausedBy;
    bool _panicWasDown, _wasPaused;

    Thread _super;
    volatile bool _stop;
    readonly ConcurrentDictionary<string, Runner> _runs = new();
    readonly ConcurrentDictionary<string, ScriptRunner> _scripts = new();
    readonly Dictionary<string, (bool WasDown, bool Toggled)> _trig = new();
    public static readonly ConcurrentDictionary<string, MacroLog> Logs = new();

    readonly Queue<double> _clicks = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public bool Armed { get; private set; }
    /// <summary>Raised (on a background thread) when macros turn on or off or one starts or stops running. The reason is "panic" for the stop key.</summary>
    public event Action<string> Changed;

    public MacroEngine(Func<MacroDef[]> macros, Func<MacroOptions> options)
    {
        _macros = macros;
        _options = options;
        Phys.Changed += OnPhysical;
        Phys.Scrolled += n => { foreach (var s in _scripts.Values) s.OnScroll(n); };
    }

    public static MacroLog LogOf(string id) => Logs.GetOrAdd(id, _ => new MacroLog());

    public bool IsRunning(string id) => (_runs.TryGetValue(id, out var r) && !r.Done) || (_scripts.TryGetValue(id, out var s) && s.Alive);
    public int RunningCount => _runs.Values.Count(r => !r.Done);

    /// <summary>Clicks per second sent by all macros over the last second.</summary>
    public int LiveCps
    {
        get
        {
            lock (_clicks)
            {
                double now = Clock.Elapsed.TotalMilliseconds;
                while (_clicks.Count > 0 && now - _clicks.Peek() > 1000) _clicks.Dequeue();
                return _clicks.Count;
            }
        }
    }

    public void Arm()
    {
        if (Armed) return;
        Armed = true;
        _stop = false;
        Phys.Start();
        lock (_trig) _trig.Clear();
        _super = new Thread(Supervise) { IsBackground = true, Name = "Pulse macros", Priority = ThreadPriority.AboveNormal };
        _super.Start();
        Changed?.Invoke("on");
    }

    public void Disarm(string reason = "off")
    {
        if (!Armed) return;
        Armed = false;
        _stop = true;
        Phys.Signal.Set();
        if (Thread.CurrentThread != _super) _super?.Join(500);
        StopAll();
        Phys.Stop();
        Changed?.Invoke(reason);
    }

    void StopAll()
    {
        foreach (var r in _runs.Values) r.Stop();
        foreach (var s in _scripts.Values) s.Stop();
        _runs.Clear();
        _scripts.Clear();
    }

    public void Dispose() => Disarm();

    WindowsInput NewSink(MacroDef def)
    {
        var log = LogOf(def.Id);
        return new WindowsInput(Phys)
        {
            MoveBlockedBy = () => MoveBlockedBy,
            LogTo = log.Add,
            Pressed = _ => { lock (_clicks) { _clicks.Enqueue(Clock.Elapsed.TotalMilliseconds); if (_clicks.Count > 400) _clicks.Dequeue(); } },
        };
    }

    void OnPhysical(int vk, bool down)
    {
        foreach (var s in _scripts.Values) s.OnInput(vk, down);
    }

    void Supervise()
    {
        while (!_stop)
        {
            Phys.Signal.WaitOne(15);
            if (_stop) break;
            try { Tick(); }
            catch (Exception ex) { TweakEngine.AddLog("macro", "Macro error: " + ex.Message); }
        }
    }

    void Tick()
    {
        var opts = _options();
        // The stop key ends whatever is playing (and lets go of everything) but leaves macros on: Esc is pressed
        // all the time in games, and it shouldn't switch macros off each time.
        bool panic = opts.PanicKey != 0 && Phys.IsDown(opts.PanicKey);
        bool pressedPanic = panic && !_panicWasDown;
        _panicWasDown = panic;
        if (pressedPanic)
        {
            bool wasPlaying = _runs.Values.Any(r => !r.Done) || _scripts.Values.Any(s => s.Busy);
            StopAll();
            lock (_trig) _trig.Clear();
            if (wasPlaying) Changed?.Invoke("panic");
            return;
        }
        if (panic) return; // while it's held, nothing starts
        if (PausedBy != null)
        {
            if (!_wasPaused) { _wasPaused = true; StopAll(); lock (_trig) _trig.Clear(); Changed?.Invoke("paused"); }
            return;
        }
        if (_wasPaused) { _wasPaused = false; Changed?.Invoke("resumed"); }
        var list = _macros();
        var ids = new HashSet<string>();
        bool changed = false;
        foreach (var def in list)
        {
            ids.Add(def.Id);
            bool allowed = def.Enabled && AllowedHere(def);
            if (def.Kind == "script")
            {
                _scripts.TryGetValue(def.Id, out var sr);
                if (!def.Enabled || string.IsNullOrWhiteSpace(def.Script))
                {
                    if (sr != null) { sr.Stop(); _scripts.TryRemove(def.Id, out _); changed = true; }
                    continue;
                }
                // Started once per arming, and again whenever the script is edited. A script that stopped with an
                // error stays stopped until it's changed (so a broken script can't restart in a loop).
                if (sr == null || sr.Source != def.Script || !sr.GKeys.SequenceEqual(def.GKeys))
                {
                    sr?.Stop();
                    sr = new ScriptRunner(def, NewSink(def));
                    _scripts[def.Id] = sr;
                    sr.Start();
                    changed = true;
                }
                sr.Allowed = allowed;
                continue;
            }

            _runs.TryGetValue(def.Id, out var run);
            if (run != null && run.Done) { _runs.TryRemove(def.Id, out _); run = null; changed = true; }
            if (!def.Enabled || def.TriggerKey == 0)
            {
                if (run != null) { run.Stop(); _runs.TryRemove(def.Id, out _); changed = true; }
                continue;
            }
            bool down = Phys.IsDown(def.TriggerKey) && MacroPolicy.ModifiersMatch(def, Phys.IsDown);
            (bool WasDown, bool Toggled) st;
            lock (_trig) _trig.TryGetValue(def.Id, out st);
            bool rising = down && !st.WasDown;
            st.WasDown = down;
            string mode = def.Kind == "clicker" && def.Mode == "once" ? "hold" : def.Mode;
            switch (mode)
            {
                case "once":
                    if (rising)
                    {
                        bool quick = def.Speed is "fast" or "instant";
                        if (run != null && !quick) { run.Stop(); _runs.TryRemove(def.Id, out _); } // pressing again stops it
                        else
                        {
                            // Quick binds start again on every press, so spamming the key never gets swallowed.
                            if (run != null) { run.Stop(); _runs.TryRemove(def.Id, out _); }
                            if (allowed) { run = new Runner(def, NewSink(def), oneShot: true); _runs[def.Id] = run; run.Start(); }
                        }
                        changed = true;
                    }
                    break;
                case "toggle":
                case "hold":
                {
                    if (mode == "toggle" && rising) st.Toggled = !st.Toggled;
                    bool want = (mode == "toggle" ? st.Toggled : down) && allowed;
                    if (want && run == null) { run = new Runner(def, NewSink(def), oneShot: false); _runs[def.Id] = run; run.Start(); changed = true; }
                    else if (!want && run != null) { run.Stop(); _runs.TryRemove(def.Id, out _); changed = true; }
                    break;
                }
            }
            if (run != null) run.Sink.Allowed = allowed;
            lock (_trig) _trig[def.Id] = st;
        }
        // Macros that were deleted.
        foreach (var id in _runs.Keys.Where(k => !ids.Contains(k)).ToList()) { if (_runs.TryRemove(id, out var r)) r.Stop(); changed = true; }
        foreach (var id in _scripts.Keys.Where(k => !ids.Contains(k)).ToList()) { if (_scripts.TryRemove(id, out var s)) s.Stop(); changed = true; }
        if (changed) Changed?.Invoke("running");
    }

    /// <summary>Plays one clicker or sequence macro on its own thread.</summary>
    sealed class Runner
    {
        readonly MacroDef _def;
        public readonly WindowsInput Sink;
        readonly bool _oneShot;
        readonly Random _rng = new();
        public volatile bool Done;

        public Runner(MacroDef def, WindowsInput sink, bool oneShot) { _def = def; Sink = sink; _oneShot = oneShot; }

        public void Start() => new Thread(Body) { IsBackground = true, Name = "Pulse macro: " + _def.Name, Priority = ThreadPriority.Highest }.Start();

        public void Stop() => Sink.Stopped = true;

        void Body()
        {
            try
            {
                if (_def.Kind == "clicker") Click();
                else Sequence();
            }
            catch (Exception ex) { Sink.Log("Stopped: " + ex.Message); }
            finally
            {
                Sink.ReleaseAll();
                Sink.Dispose();
                Done = true;
            }
        }

        void Click()
        {
            var s = _def.Clicker ?? new MacroSettings();
            var pattern = new ClickPattern(s);
            int button = s.Button == "right" ? 1 : 0;
            var clock = Stopwatch.StartNew();
            double next = 0;
            while (!Sink.Stopped)
            {
                var (gap, hold) = pattern.Next();
                double now = clock.Elapsed.TotalMilliseconds;
                if (next > now) Sink.Sleep(next - now);
                if (Sink.Stopped) break;
                double pressedAt = clock.Elapsed.TotalMilliseconds;
                if (!Sink.Allowed) { Sink.Sleep(10); next = clock.Elapsed.TotalMilliseconds; continue; }
                Sink.Button(button, true);
                Sink.Wait(hold); // a click always finishes, even if stopped mid-press
                Sink.Button(button, false);
                next = pressedAt + gap;
            }
        }

        void Sequence()
        {
            var steps = _def.Steps.ToList();
            if (steps.Count == 0) return;
            int times = Math.Max(_def.Repeat, 1);
            bool hasWait = steps.Any(x => x.Do == "wait" || x.Do is "tap" or "click" or "text") || _def.RepeatGap > 0;
            for (int n = 0; !Sink.Stopped; n++)
            {
                foreach (var step in steps)
                {
                    if (Sink.Stopped) return;
                    Do(step);
                }
                if (_oneShot && n + 1 >= times) return;
                if (_def.RepeatGap > 0) Sink.Sleep(MacroPolicy.StepWait(_def.RepeatGap, 0, _def.Humanize, _rng));
                else if (!hasWait) Sink.Sleep(10); // never spin flat out
            }
        }

        void Do(MacroStep s)
        {
            double Hold(double dflt) => MacroPolicy.StepWait(s.Ms > 0 ? s.Ms : MacroPolicy.HoldFor(_def.Speed, dflt), s.Ms > 0 ? s.RandomMs : 0, _def.Humanize, _rng);
            switch (s.Do)
            {
                case "tap":
                {
                    int sc = Keys.ScanOfVk(s.Key);
                    Sink.Key(sc, true);
                    Sink.Wait(Hold(25));
                    Sink.Key(sc, false);
                    break;
                }
                case "down": Sink.Key(Keys.ScanOfVk(s.Key), true); break;
                case "up": Sink.Key(Keys.ScanOfVk(s.Key), false); break;
                case "click":
                    Sink.Button(s.Button, true);
                    Sink.Wait(Hold(20));
                    Sink.Button(s.Button, false);
                    break;
                case "press": Sink.Button(s.Button, true); break;
                case "release": Sink.Button(s.Button, false); break;
                case "wait": Sink.Sleep(MacroPolicy.StepWait(s.Ms, s.RandomMs, _def.Humanize, _rng)); break;
                case "text": Sink.Type(s.Text); break;
                case "move": Sink.Move(s.X, s.Y); break;
                case "moveto": Sink.MoveTo(s.X, s.Y); break;
                case "scroll": Sink.Wheel(s.Y); break;
            }
        }
    }

    /// <summary>Runs one Logitech-style script: loads it, sends PROFILE_ACTIVATED, then OnEvent for each button press.</summary>
    sealed class ScriptRunner
    {
        readonly MacroDef _def;
        readonly WindowsInput _sink;
        public readonly string Source;
        readonly BlockingCollection<(string Event, double Arg, string Family)> _queue = new(64);
        readonly int[] _gkeys;
        public IReadOnlyList<int> GKeys => _gkeys;
        volatile bool _stopped;
        LuaInterpreter _vm;
        GHubApi.Context _ctx;
        Thread _thread;
        public volatile bool Allowed = true;
        public volatile bool Alive;
        /// <summary>Running an event right now (not just waiting for buttons).</summary>
        public volatile bool Busy;

        public ScriptRunner(MacroDef def, WindowsInput sink)
        {
            _def = def;
            _sink = sink;
            Source = def.Script;
            _gkeys = def.GKeys.ToArray();
        }

        public void Start()
        {
            Alive = true;
            _thread = new Thread(Body, 16 * 1024 * 1024) { IsBackground = true, Name = "Pulse script: " + _def.Name, Priority = ThreadPriority.Highest }; // deep Lua recursion needs room
            _thread.Start();
        }

        public void Stop()
        {
            _stopped = true;
            _vm?.Stop();
            _sink.Stopped = true;
            try { _queue.CompleteAdding(); } catch { }
        }

        public void OnInput(int vk, bool down)
        {
            if (!Alive || _queue.IsAddingCompleted) return;
            if (down && !Allowed) return; // wrong window: presses don't reach the script, releases still do
            int g = Array.IndexOf(_gkeys, vk);
            if (g >= 0) { Post(down ? "G_PRESSED" : "G_RELEASED", g + 1, "kb"); return; }
            int n = GHubApi.EventNumberFromVk(vk);
            if (n == 0) return;
            if (n == 1 && _ctx?.PrimaryEvents != true) return; // like G Hub: left clicks only after EnablePrimaryMouseButtonEvents(true)
            Post(down ? "MOUSE_BUTTON_PRESSED" : "MOUSE_BUTTON_RELEASED", n, "mouse");
        }

        public void OnScroll(int clicks) { } // G Hub doesn't send scroll events either

        void Post(string ev, double arg, string family)
        {
            try { _queue.TryAdd((ev, arg, family)); } catch { }
        }

        void Body()
        {
            _sink.Log($"Script started ({_def.Name})");
            try
            {
                _vm = new LuaInterpreter();
                if (_stopped) return;
                _ctx = GHubApi.Install(_vm, _sink);
                _vm.Run(Source);
                if (_vm.Globals.Get("OnEvent") is not LuaFunction)
                    _sink.Log("This script has no OnEvent function, so buttons won't do anything. Logitech scripts start with: function OnEvent(event, arg, family)");
                _vm.CallGlobal("OnEvent", "PROFILE_ACTIVATED", 0.0, "");
                foreach (var (ev, arg, family) in _queue.GetConsumingEnumerable())
                {
                    if (_vm.Stopped) break;
                    Busy = true;
                    try { _vm.CallGlobal("OnEvent", ev, arg, family); }
                    finally { Busy = false; }
                }
            }
            catch (LuaStopped) { }
            catch (LuaException ex) { _sink.Log("Error: " + ex.Message); }
            catch (Exception ex) { _sink.Log("Error: " + ex.Message); }
            finally
            {
                _sink.ReleaseAll();
                Alive = false;
                if (_vm?.Stopped == true) _sink.Log("Script stopped");
                _sink.Dispose();
            }
        }
    }
}

/// <summary>Records the user's own keys and clicks (with their timing) into sequence steps.</summary>
public sealed class MacroRecorder : IDisposable
{
    readonly PhysicalInput _phys;
    readonly bool _ownsPhys;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<(double T, int Vk, bool Down, int Scroll)> _events = new();
    readonly int _ignore;
    public event Action<int> EventCount;

    /// <param name="ignoreVk">A key never recorded (the stop key).</param>
    public MacroRecorder(PhysicalInput phys, int ignoreVk)
    {
        _ignore = ignoreVk;
        _phys = phys;
        if (!_phys.Running) { _phys.Start(); _ownsPhys = true; }
        _phys.Changed += OnChanged;
        _phys.Scrolled += OnScrolled;
    }

    /// <summary>Raised (on the input thread) when the stop key is pressed: the page then finishes the recording.</summary>
    public event Action StopRequested;

    void OnChanged(int vk, bool down)
    {
        if (vk == _ignore) { if (down) StopRequested?.Invoke(); return; }
        // Clicks on Pulse's own window (the Stop button, anything else on the page) are never part of the macro.
        if (Keys.IsMouse(vk) && PointerOverPulse()) return;
        lock (_events) { _events.Add((_clock.Elapsed.TotalMilliseconds, vk, down, 0)); EventCount?.Invoke(_events.Count); }
    }

    void OnScrolled(int n)
    {
        if (PointerOverPulse()) return;
        lock (_events) { _events.Add((_clock.Elapsed.TotalMilliseconds, 0, false, n)); EventCount?.Invoke(_events.Count); }
    }

    static bool PointerOverPulse()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!GetCursorPos(out var p)) return false;
            var w = WindowFromPoint(p);
            if (w == IntPtr.Zero) return false;
            GetWindowThreadProcessId(w, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>Stops and returns the steps. Clicks on Pulse's own window (like the Stop button) were never recorded.</summary>
    public List<MacroStep> Finish(string delays = "recorded", double fixedMs = 50, bool mouse = true)
    {
        Dispose();
        List<(double, int, bool, int)> ev;
        lock (_events) ev = _events.ToList();
        return ToSteps(ev, dropLastLeftClick: false, delays, fixedMs, mouse);
    }

    public void Dispose()
    {
        _phys.Changed -= OnChanged;
        _phys.Scrolled -= OnScrolled;
        if (_ownsPhys) _phys.Stop();
    }

    /// <summary>
    /// Turns recorded presses into steps: quick press-and-release becomes a tap or click, gaps become waits.
    /// Delays like other macro recorders: "recorded" (as you pressed them), "fixed" (every gap is <paramref name="fixedMs"/>)
    /// or "none" (as fast as possible).
    /// </summary>
    public static List<MacroStep> ToSteps(List<(double T, int Vk, bool Down, int Scroll)> events, bool dropLastLeftClick = false,
        string delays = "recorded", double fixedMs = 50, bool includeMouse = true)
    {
        var ev = events.ToList();
        if (!includeMouse) { ev = ev.Where(e => e.Scroll == 0 && !Keys.IsMouse(e.Vk)).ToList(); dropLastLeftClick = false; }
        if (dropLastLeftClick)
        {
            int up = ev.FindLastIndex(e => e.Vk == Keys.VK_LBUTTON && !e.Down);
            int down = up > 0 ? ev.FindLastIndex(up - 1, e => e.Vk == Keys.VK_LBUTTON && e.Down) : -1;
            if (up >= 0 && up >= ev.Count - 2) { ev.RemoveAt(up); if (down >= 0) ev.RemoveAt(down); }
            else if (ev.Count > 0 && ev[^1].Vk == Keys.VK_LBUTTON && ev[^1].Down) ev.RemoveAt(ev.Count - 1);
        }
        // Keys still held at the start of recording show only a release: drop those.
        var held = new HashSet<int>();
        ev = ev.Where(e =>
        {
            if (e.Scroll != 0) return true;
            if (e.Down) { held.Add(e.Vk); return true; }
            return held.Remove(e.Vk);
        }).ToList();

        var steps = new List<MacroStep>();
        double last = ev.Count > 0 ? ev[0].T : 0;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            double gap = e.T - last;
            if (steps.Count > 0 && gap >= 1 && delays != "none")
                steps.Add(new MacroStep { Do = "wait", Ms = delays == "fixed" ? fixedMs : Math.Round(gap) });
            last = e.T;
            if (e.Scroll != 0) { steps.Add(new MacroStep { Do = "scroll", Y = e.Scroll }); continue; }
            bool mouse = Keys.IsMouse(e.Vk);
            // Pressed and let go with nothing in between: one tap/click holding for that long.
            if (e.Down && i + 1 < ev.Count && ev[i + 1].Vk == e.Vk && !ev[i + 1].Down && ev[i + 1].Scroll == 0)
            {
                double hold = delays switch { "fixed" => fixedMs, "none" => 0, _ => Math.Max(Math.Round(ev[i + 1].T - e.T), 1) };
                steps.Add(mouse ? new MacroStep { Do = "click", Button = Keys.ButtonOfVk(e.Vk), Ms = hold } : new MacroStep { Do = "tap", Key = e.Vk, Ms = hold });
                last = ev[i + 1].T;
                i++;
                continue;
            }
            steps.Add(mouse
                ? new MacroStep { Do = e.Down ? "press" : "release", Button = Keys.ButtonOfVk(e.Vk) }
                : new MacroStep { Do = e.Down ? "down" : "up", Key = e.Vk });
        }
        return steps;
    }
}
