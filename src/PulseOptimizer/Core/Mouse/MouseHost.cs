using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Jint;

namespace Pulse.Core.Mouse;

/// <summary>
/// Runs OpenMouse's mouse protocol library (AGPL-3.0, github.com/OpenMouse-Project/mouse-protocol) inside Pulse: the
/// bundled script (Assets/Mouse/openmouse.js, built from tools/mouse/bridge.ts) runs in a small script engine on its
/// own thread, and Pulse answers its WebHID calls with Windows' HID functions (<see cref="Hid"/>). Started only when the
/// Mouse page or a game profile needs it, so it costs nothing otherwise.
/// </summary>
public sealed class MouseHost : IDisposable
{
    static MouseHost _instance;
    static readonly object Gate = new();

    /// <summary>The engine, started on first use. Null when this build doesn't include the protocol library.</summary>
    public static MouseHost Instance
    {
        get { lock (Gate) return _instance ??= Script() is { } js ? new MouseHost(js) : null; }
    }

    static int _holds;

    /// <summary>How devices are listed and opened: Windows' HID normally; a simulated mouse in tests.</summary>
    public static Func<List<Hid.DeviceInfo>> ListDevices = Hid.List;
    public static Func<Hid.DeviceInfo, Hid.ILink> Connect = info => new Hid.Connection(info);

    // ───────── diagnostics: what was said to the device (the last 400 lines) ─────────

    static readonly LinkedList<string> Trace = new();
    static readonly DateTime TraceStart = DateTime.UtcNow;

    public static void Note(string line)
    {
        lock (Trace)
        {
            Trace.AddLast($"{(DateTime.UtcNow - TraceStart).TotalSeconds,8:0.000}  {line}");
            while (Trace.Count > 400) Trace.RemoveFirst();
        }
    }

    public static List<string> RecentLog() { lock (Trace) return Trace.ToList(); }
    public static void ClearLog() { lock (Trace) Trace.Clear(); }

    static string Short(string hex) => hex == null ? "" : hex.Length > 40 ? hex[..40] + "…" : hex;

    /// <summary>Keeps the engine running while something uses it (the Mouse page, a profile being applied).</summary>
    public static void Hold() { lock (Gate) _holds++; }

    /// <summary>Done with it: when nothing else holds it, the engine stops and its memory is freed.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (--_holds > 0) return;
            _holds = 0;
            _instance?.Dispose();
            _instance = null;
        }
    }

    /// <summary>The bundled protocol library, or null.</summary>
    public static string Script()
    {
        var asm = typeof(MouseHost).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("openmouse.js", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;
    readonly List<(DateTime Due, int Id)> _timers = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _calls = new();
    readonly Dictionary<string, Hid.DeviceInfo> _infos = new();
    readonly ConcurrentDictionary<string, Hid.ILink> _open = new();
    readonly Dictionary<string, int> _users = new();
    readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Engine _engine;
    int _seq;
    volatile bool _disposed;

    /// <summary>What the library logged (for diagnostics).</summary>
    public event Action<string> Log;

    MouseHost(string script)
    {
        _thread = new Thread(() => Loop(script)) { IsBackground = true, Name = "Pulse mouse" };
        _thread.Start();
    }

    /// <summary>Calls one of PulseMouse's functions (scan, open, status, call, close). Returns its JSON.</summary>
    public async Task<string> Run(string method, params object[] args)
    {
        await _ready.Task;
        int token = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[token] = tcs;
        Post(() => _engine.Invoke("__pulseRun", token, method, JsonSerializer.Serialize(args)));
        return await tcs.Task;
    }

    void Post(Action a) { if (!_disposed) _queue.Add(a); }

    void Loop(string script)
    {
        try
        {
            _engine = new Engine(o => o.LimitRecursion(512));
            Globals(_engine);
            _engine.Execute(script);
            _engine.Execute("""
                globalThis.__pulseRun = (token, method, argsJson) => {
                  Promise.resolve()
                    .then(() => PulseMouse[method](...JSON.parse(argsJson)))
                    .then(v => __pulseHost.done(token, true, String(v)), e => __pulseHost.done(token, false, String((e && e.message) || e)));
                };
                """);
            _ready.TrySetResult(true);
        }
        catch (Exception ex) { _ready.TrySetException(ex); return; }

        while (!_disposed)
        {
            int wait = 1000;
            lock (_timers) if (_timers.Count > 0) wait = (int)Math.Clamp((_timers.Min(t => t.Due) - DateTime.UtcNow).TotalMilliseconds, 0, 1000);
            try
            {
                if (_queue.TryTake(out var action, wait)) action();
                List<int> due;
                lock (_timers)
                {
                    var now = DateTime.UtcNow;
                    due = _timers.Where(t => t.Due <= now).OrderBy(t => t.Due).Select(t => t.Id).ToList();
                    _timers.RemoveAll(t => t.Due <= now);
                }
                foreach (var id in due) _engine.Invoke("__pulseFire", id);
                _engine.Advanced.ProcessTasks();
            }
            catch (Exception ex) { Note("engine error: " + ex.Message); Log?.Invoke(ex.Message); }
        }
    }

    // ───────── what the script can call ─────────

    void Globals(Engine e)
    {
        e.SetValue("__pulseHost", new HostApi(this));
        e.SetValue("__pulseHid", new HidApi(this));
    }

    /// <summary>Timers, logging and answers to Pulse's own calls.</summary>
    public sealed class HostApi
    {
        readonly MouseHost _h;
        internal HostApi(MouseHost h) => _h = h;
        public void timer(double id, double ms) { lock (_h._timers) _h._timers.Add((DateTime.UtcNow.AddMilliseconds(Math.Max(0, ms)), (int)id)); }
        public void log(string text) { Note("driver: " + text); _h.Log?.Invoke(text); }
        public void done(double token, bool ok, string value)
        {
            if (!_h._calls.TryRemove((int)token, out var tcs)) return;
            if (ok) tcs.TrySetResult(value); else tcs.TrySetException(new InvalidOperationException(value));
        }
    }

    /// <summary>The WebHID functions, answered from Windows' HID (each answer comes back through __pulseDone).</summary>
    public sealed class HidApi
    {
        readonly MouseHost _h;
        internal HidApi(MouseHost h) => _h = h;

        public string list()
        {
            var all = ListDevices();
            lock (_h._infos) { _h._infos.Clear(); foreach (var d in all) _h._infos[d.Key] = d; }
            return DevicesJson(all);
        }

        public void open(double token, string key) => _h.Answer(token, "open " + key, () =>
        {
            // The Mouse page and a game profile can use the same mouse at once: count the users of each connection.
            lock (_h._users) _h._users[key] = _h._users.GetValueOrDefault(key) + 1;
            if (_h._open.ContainsKey(key)) return Task.FromResult<string>(null);
            Hid.DeviceInfo info;
            lock (_h._infos) _h._infos.TryGetValue(key, out info);
            if (info == null) throw new IOException("That device isn't connected any more.");
            var c = Connect(info);
            int inputs = 0;
            c.Input += (id, data) =>
            {
                if (++inputs <= 40) Note($"in  report {id}: {Short(Convert.ToHexString(data).ToLowerInvariant())}");
                _h.Post(() => _h._engine.Invoke("__pulseInput", key, (int)id, Convert.ToHexString(data).ToLowerInvariant()));
            };
            Note($"opened {key} ({info.Collections.Count} collections: {string.Join(", ", info.Collections.Select(x => $"page {x.UsagePage:X4} in[{string.Join(",", x.InputIds)}] out[{string.Join(",", x.OutputIds)}] feat[{string.Join(",", x.FeatureIds)}]/{x.FeatureLength}"))})");
            _h._open[key] = c;
            return Task.FromResult<string>(null);
        });

        public void close(double token, string key) => _h.Answer(token, "close " + key, () =>
        {
            lock (_h._users)
            {
                int left = _h._users.GetValueOrDefault(key) - 1;
                if (left > 0) { _h._users[key] = left; return Task.FromResult<string>(null); }
                _h._users.Remove(key);
            }
            if (_h._open.TryRemove(key, out var c)) c.Dispose();
            return Task.FromResult<string>(null);
        });

        public void send(double token, string key, double id, string hex) =>
            _h.Answer(token, $"out report {id}: {Short(hex)}", async () => { await _h.Conn(key).SendReport((byte)id, Convert.FromHexString(hex)); return null; });

        public void setFeature(double token, string key, double id, string hex) =>
            _h.Answer(token, $"set feature {id}: {Short(hex)}", async () => { await _h.Conn(key).SendFeature((byte)id, Convert.FromHexString(hex)); return null; });

        public void getFeature(double token, string key, double id) =>
            _h.Answer(token, $"get feature {id}", async () => Convert.ToHexString(await _h.Conn(key).GetFeature((byte)id)).ToLowerInvariant());
    }

    Hid.ILink Conn(string key) =>
        _open.TryGetValue(key, out var c) ? c : throw new IOException("The device isn't open.");

    /// <summary>Runs <paramref name="work"/> off the script thread, then hands the result back to the script.</summary>
    void Answer(double token, Func<Task<string>> work) => Answer(token, null, work);

    /// <summary>Same, writing what was asked and what came back to the diagnostics log.</summary>
    void Answer(double token, string what, Func<Task<string>> work) => Task.Run(async () =>
    {
        try
        {
            var v = await work();
            if (what != null) Note(v == null ? what : $"{what} → {Short(v)}");
            Post(() => _engine.Invoke("__pulseDone", token, true, v));
        }
        catch (Exception ex)
        {
            Note($"{what ?? "call"} failed: {ex.Message}");
            Post(() => _engine.Invoke("__pulseDone", token, false, ex.Message));
        }
    });

    /// <summary>The devices as WebHID describes them (each report as 8-bit items, which is all drivers look at).</summary>
    public static string DevicesJson(IEnumerable<Hid.DeviceInfo> devices) => JsonSerializer.Serialize(devices.Select(d => new
    {
        key = d.Key, vendorId = d.VendorId, productId = d.ProductId, productName = d.ProductName,
        collections = d.Collections.Select(c => new
        {
            usagePage = c.UsagePage, usage = c.Usage,
            inputReports = Reports(c.InputIds, c.InputLength),
            outputReports = Reports(c.OutputIds, c.OutputLength),
            featureReports = Reports(c.FeatureIds, c.FeatureLength),
            children = Array.Empty<object>(),
        }),
    }));

    static object[] Reports(List<byte> ids, int length) =>
        ids.Select(id => (object)new { reportId = (int)id, items = new[] { new { reportSize = 8, reportCount = Math.Max(0, length - 1) } } }).ToArray();

    public void Dispose()
    {
        _disposed = true;
        foreach (var t in _calls.Values) t.TrySetException(new ObjectDisposedException("mouse engine"));
        _calls.Clear();
        foreach (var c in _open.Values) try { c.Dispose(); } catch { }
        _open.Clear();
        _queue.CompleteAdding();
    }
}
