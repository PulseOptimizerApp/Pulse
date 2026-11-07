using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Pulse.Core.Devices;

/// <summary>A mouse (or other device) a driver recognised.</summary>
public sealed record DriverDevice(string Key, string Brand, string ProductName, int VendorId, int ProductId);

/// <summary>
/// Hosts OpenMouse's mouse-protocol drivers (AGPL-3.0-or-later, bundled in Assets/Drivers/mouse-drivers.js) in a
/// hidden WebView2 page, and answers their USB requests with <see cref="NativeHid"/>. The page is Pulse's own local
/// file: every web request is refused, so nothing goes online. Started when the Mouse page opens and stopped when
/// it's left, so it costs nothing the rest of the time.
/// </summary>
public static class MouseDrivers
{
    const string Host = "pulse-drivers.local";
    static CoreWebView2Controller _controller;
    static CoreWebView2 _web;
    static HwndSource _window;
    static Task _starting;
    static TaskCompletionSource<bool> _ready;
    static int _nextId;
    static readonly Dictionary<int, TaskCompletionSource<JsonNode>> Pending = new();
    static readonly Dictionary<string, NativeHid.Open> OpenDevices = new();
    static List<HidDeviceInfo> _hid = new();

    /// <summary>Why the drivers couldn't start (no WebView2 runtime, bundle missing…), or null.</summary>
    public static string Problem { get; private set; }

    /// <summary>What the driver engine is doing, for the page's progress line ("Starting the mouse drivers…").</summary>
    public static string Step { get; private set; } = "";
    public static event Action StepChanged;
    /// <summary>Every step and error since Pulse started, for "Copy details".</summary>
    public static readonly List<string> Log = new();

    static void Note(string step)
    {
        Step = step;
        lock (Log) { Log.Add($"{DateTime.Now:HH:mm:ss.fff} {step}"); if (Log.Count > 300) Log.RemoveRange(0, 100); }
        try { TweakEngine.AddLog("mouse", step); } catch { }
        StepChanged?.Invoke();
    }

    static async Task<T> Within<T>(Task<T> task, int ms, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new TimeoutException(what + " took too long.");
        return await task;
    }
    public static bool Running => _web != null;

    public static bool BundleIncluded => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("mouse-drivers.js");

    public static Task Start() => _starting ??= StartCore();

    static async Task StartCore()
    {
        try
        {
            if (!BundleIncluded) throw new InvalidOperationException("This build of Pulse doesn't include the mouse drivers.");
            Note("Starting the mouse drivers…");
            string version;
            try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { version = null; }
            if (string.IsNullOrEmpty(version))
                throw new InvalidOperationException("Mouse settings need Microsoft Edge WebView2, which is part of Windows 11. Install the WebView2 Runtime from Microsoft and reopen this page.");

            var folder = Path.Combine(TweakEngine.DataDir, "drivers");
            Directory.CreateDirectory(folder);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mouse-drivers.js")!)
            using (var f = File.Create(Path.Combine(folder, "mouse-drivers.js"))) await s.CopyToAsync(f);
            await File.WriteAllTextAsync(Path.Combine(folder, "host.html"),
                "<!doctype html><meta charset=utf-8><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'; script-src 'self'; connect-src 'none'; img-src 'none'\"><script src=\"mouse-drivers.js\"></script>");

            _window = new HwndSource(new HwndSourceParameters("Pulse mouse drivers") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP, never shown */ });
            Note("WebView2 " + version + ": creating the driver engine");
            // An elevated Pulse needs its own engine folder and no browser extras; each step has a time limit so a
            // stuck engine shows an error instead of loading forever.
            var options = new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --no-first-run");
            var env = await Within(CoreWebView2Environment.CreateAsync(null, Path.Combine(TweakEngine.DataDir, "webview2"), options), 20000, "Starting WebView2");
            Note("Creating the driver page");
            _controller = await Within(env.CreateCoreWebView2ControllerAsync(_window.Handle), 20000, "Creating the driver page");
            _controller.IsVisible = false;
            _web = _controller.CoreWebView2;
            var st = _web.Settings;
            st.AreDevToolsEnabled = false; st.AreDefaultContextMenusEnabled = false; st.AreDefaultScriptDialogsEnabled = false;
            st.IsStatusBarEnabled = false; st.IsZoomControlEnabled = false; st.AreBrowserAcceleratorKeysEnabled = false;
            _web.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Deny);
            // Offline by design: only Pulse's own local page loads; everything else is refused.
            _web.NavigationStarting += (_, e) => { if (!IsLocal(e.Uri)) e.Cancel = true; };
            _web.NewWindowRequested += (_, e) => e.Handled = true;
            _web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _web.WebResourceRequested += (_, e) =>
            {
                if (!IsLocal(e.Request.Uri)) e.Response = _web.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            _web.WebMessageReceived += OnMessage;
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _web.ProcessFailed += (_, e) => Note("Driver engine stopped: " + e.ProcessFailedKind);
            Note("Loading the drivers");
            _web.Navigate($"https://{Host}/host.html");
            if (await Task.WhenAny(_ready.Task, Task.Delay(15000)) != _ready.Task) throw new TimeoutException("The mouse drivers didn't load.");
            Note("Drivers ready");
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Note("Couldn't start: " + ex.GetType().Name + ": " + ex.Message);
            Stop();
            throw;
        }
    }

    internal static bool IsLocal(string uri) => uri != null && uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase);

    public static void Stop()
    {
        foreach (var d in OpenDevices.Values) try { d.Dispose(); } catch { }
        OpenDevices.Clear();
        foreach (var p in Pending.Values) p.TrySetException(new OperationCanceledException());
        Pending.Clear();
        try { _controller?.Close(); } catch { }
        try { _window?.Dispose(); } catch { }
        _controller = null; _web = null; _window = null; _starting = null;
    }

    // ───────── calls into the drivers ─────────

    static async Task<JsonNode> Api(JsonObject msg, int timeoutMs = 20000)
    {
        await Start();
        int id = ++_nextId;
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = tcs;
        msg["t"] = "api";
        msg["id"] = id;
        _web.PostWebMessageAsJson(msg.ToJsonString());
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task)
        {
            Pending.Remove(id);
            Note($"No answer to {(string)msg["cmd"]} {(string)msg["method"]} within {timeoutMs / 1000} s");
            throw new TimeoutException("The mouse didn't answer. Check that it's switched on and connected.");
        }
        try { return await tcs.Task; }
        catch (Exception ex) { Note($"{(string)msg["cmd"]} {(string)msg["method"]}: {ex.Message}"); throw; }
    }

    /// <summary>Every device a driver recognises (one per mouse).</summary>
    public static async Task<List<DriverDevice>> Scan()
    {
        Note("Looking for mice");
        var r = await Api(new JsonObject { ["cmd"] = "scan" }, 15000);
        return r?.AsArray().Select(n => new DriverDevice((string)n["key"], (string)n["brand"], (string)n["productName"] ?? "", (int)n["vendorId"], (int)n["productId"])).ToList() ?? new();
    }

    /// <summary>Everything the mouse reports (name, battery, DPI, polling rate, what it supports…).</summary>
    public static Task<JsonNode> Status(string key, int timeoutMs = 8000) => Api(new JsonObject { ["cmd"] = "status", ["key"] = key }, timeoutMs);

    /// <summary>Calls one of the driver's settings methods (setDpi, setPollingRate, setActiveDpiStage…).</summary>
    public static Task<JsonNode> Invoke(string key, string method, params object[] args)
    {
        var arr = new JsonArray();
        foreach (var a in args) arr.Add(JsonSerializer.SerializeToNode(a));
        return Api(new JsonObject { ["cmd"] = "invoke", ["key"] = key, ["method"] = method, ["args"] = arr });
    }

    /// <summary>The mouse's buttons, what each one does, and the actions it accepts.</summary>
    public static Task<JsonNode> Buttons(string key) => Api(new JsonObject { ["cmd"] = "buttons", ["key"] = key });

    public static Task<JsonNode> SetButton(string key, string button, string action) =>
        Api(new JsonObject { ["cmd"] = "set-button", ["key"] = key, ["id2"] = button, ["action"] = action });

    // ───────── messages from the drivers ─────────

    static async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode m;
        try { m = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        switch ((string)m?["t"])
        {
            case "ready":
                _ready?.TrySetResult(true);
                break;
            case "api-reply":
                if (Pending.Remove((int)m["id"], out var tcs))
                {
                    if ((bool)m["ok"]) tcs.TrySetResult(m["data"]?.DeepClone());
                    else tcs.TrySetException(new InvalidOperationException((string)m["error"] ?? "The mouse refused the change."));
                }
                break;
            case "hid":
                await HidRequest(m);
                break;
        }
    }

    /// <summary>A USB request from a driver: list devices, open one, send or read a report.</summary>
    static async Task HidRequest(JsonNode m)
    {
        int id = (int)m["id"];
        var reply = new JsonObject { ["t"] = "hid-reply", ["id"] = id, ["ok"] = true };
        try
        {
            string key = (string)m["key"];
            switch ((string)m["op"])
            {
                case "list":
                    _hid = await Task.Run(NativeHid.List);
                    Note($"{_hid.Count} USB input devices");
                    reply["data"] = new JsonArray(_hid.Select(Describe).ToArray());
                    break;
                case "open":
                    if (!OpenDevices.ContainsKey(key))
                    {
                        var info = _hid.FirstOrDefault(d => d.Key == key) ?? throw new InvalidOperationException("Device not found.");
                        Note($"Opening {info.ProductName} ({info.VendorId:X4}:{info.ProductId:X4})");
                        var open = await Task.Run(() => new NativeHid.Open(info));
                        open.Input += (reportId, data) => _window?.Dispatcher.BeginInvoke(() =>
                            _web?.PostWebMessageAsJson(new JsonObject { ["t"] = "input", ["key"] = key, ["reportId"] = reportId, ["data"] = Convert.ToBase64String(data) }.ToJsonString()));
                        OpenDevices[key] = open;
                    }
                    break;
                case "close":
                    if (OpenDevices.Remove(key, out var o)) o.Dispose();
                    break;
                case "send":
                    await Device(key).SendOutput((byte)(int)m["reportId"], Convert.FromBase64String((string)m["data"]));
                    break;
                case "feature-send":
                    await Device(key).SendFeature((byte)(int)m["reportId"], Convert.FromBase64String((string)m["data"]));
                    break;
                case "feature-get":
                    reply["data"] = Convert.ToBase64String(await Device(key).GetFeature((byte)(int)m["reportId"]));
                    break;
                default:
                    throw new InvalidOperationException("Unknown request.");
            }
        }
        catch (Exception ex)
        {
            reply["ok"] = false;
            reply["error"] = ex.Message;
        }
        _web?.PostWebMessageAsJson(reply.ToJsonString());
    }

    static NativeHid.Open Device(string key) =>
        OpenDevices.TryGetValue(key, out var d) ? d : throw new InvalidOperationException("The device isn't open.");

    /// <summary>A device in WebHID's shape (vendorId, productId, productName, collections with report ids).</summary>
    static JsonNode Describe(HidDeviceInfo d)
    {
        JsonArray Reports(IEnumerable<byte> ids, int length) => new(ids.Select(id => (JsonNode)new JsonObject
        {
            ["reportId"] = id,
            ["items"] = new JsonArray(new JsonObject { ["reportSize"] = 8, ["reportCount"] = Math.Max(0, length - 1) }),
        }).ToArray());
        return new JsonObject
        {
            ["key"] = d.Key, ["vendorId"] = d.VendorId, ["productId"] = d.ProductId, ["productName"] = d.ProductName,
            ["collections"] = new JsonArray(d.Collections.Select(c => (JsonNode)new JsonObject
            {
                ["usagePage"] = c.UsagePage, ["usage"] = c.Usage,
                ["inputReports"] = Reports(c.InputIds, c.InputLength),
                ["outputReports"] = Reports(c.OutputIds, c.OutputLength),
                ["featureReports"] = Reports(c.FeatureIds, c.FeatureLength),
                ["children"] = new JsonArray(),
            }).ToArray()),
        };
    }
}
