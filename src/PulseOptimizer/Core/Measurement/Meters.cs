using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;

namespace Pulse.Core.Measurement;

public record PollingResult(string Device, double RateHz, double JitterMs, int Samples, string Note);
public record CpuSample(double AveragePercent, List<(string Process, double Percent)> Top);

/// <summary>Background CPU: average total use over a quiet period, and which processes used it.</summary>
public static class BackgroundCpuMeter
{
    public static CpuSample Measure(TimeSpan duration, CancellationToken cancel = default)
    {
        var meter = new CpuMeter();
        meter.Sample();
        var start = Snapshot();
        var sw = Stopwatch.StartNew();
        var samples = new List<double>();
        while (sw.Elapsed < duration && !cancel.IsCancellationRequested)
        {
            Thread.Sleep(1000);
            samples.Add(meter.Sample() * 100);
        }
        var end = Snapshot();
        var wall = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
        var top = end
            .Where(e => start.ContainsKey(e.Key))
            .Select(e => (Name: e.Value.name, Ms: (e.Value.cpu - start[e.Key].cpu).TotalMilliseconds))
            .GroupBy(x => x.Name)
            .Select(g => (g.Key, g.Sum(x => x.Ms) / wall * 100))
            .OrderByDescending(x => x.Item2)
            .Take(8)
            .ToList();
        return new CpuSample(samples.Count > 0 ? samples.Average() : 0, top);
    }

    static Dictionary<int, (string name, TimeSpan cpu)> Snapshot()
    {
        var d = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            try { d[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch { /* protected process */ }
            finally { p.Dispose(); }
        }
        return d;
    }
}

/// <summary>
/// Controller polling as games see it through XInput: counts how often the controller's state
/// actually changes while the user moves the sticks. PlayStation pads only show up here when
/// Steam Input or DS4Windows translates them to XInput.
/// </summary>
public static class ControllerPollingMeter
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll")]
    static extern int XInputGetState(int index, out XINPUT_STATE state);

    const int ERROR_SUCCESS = 0;

    public static int? FirstConnected()
    {
        for (int i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == ERROR_SUCCESS) return i;
        return null;
    }

    /// <summary>Spin-polls for the given time. The user must keep moving a stick for an accurate rate.</summary>
    public static PollingResult Measure(int index, TimeSpan duration, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        uint last = 0;
        bool have = false, dropped = false;
        var stamps = new List<double>();
        while (sw.Elapsed < duration && !cancel.IsCancellationRequested)
        {
            var r = XInputGetState(index, out var s);
            if (r != ERROR_SUCCESS)
            {
                if (have) dropped = true; // was connected, now isn't
                Thread.Sleep(1);
                continue;
            }
            if (!have) { last = s.dwPacketNumber; have = true; }
            else if (s.dwPacketNumber != last)
            {
                last = s.dwPacketNumber;
                stamps.Add(sw.Elapsed.TotalMilliseconds);
            }
            Thread.SpinWait(50);
        }
        if (dropped) PcDiagnostics.RecordUsbDropout("Controller disconnected during the polling test");
        return Summarize("Controller (XInput)", stamps,
            dropped ? "The controller disconnected during the test." : "");
    }

    internal static PollingResult Summarize(string device, List<double> stamps, string note)
    {
        if (stamps.Count < 50)
            return new PollingResult(device, 0, 0, stamps.Count, "Not enough movement recorded — keep moving the stick or mouse during the test. " + note);
        var gaps = stamps.Zip(stamps.Skip(1), (a, b) => b - a)
                         .Where(g => g < 50) // ignore pauses in movement
                         .OrderBy(g => g).ToList();
        var median = Stats.Percentile(gaps, 0.5);
        var jitter = Stats.StdDev(gaps);
        return new PollingResult(device, median > 0 ? 1000.0 / median : 0, jitter, stamps.Count, note);
    }
}

/// <summary>Mouse report rate from raw input (WM_INPUT). Create and use on the UI thread.</summary>
public sealed class MousePollingMeter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    const int WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;

    readonly HwndSource _source;
    readonly Stopwatch _sw = new();
    readonly List<double> _stamps = new();
    bool _recording;

    public MousePollingMeter()
    {
        var p = new HwndSourceParameters("PulseMousePolling") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
        var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _source.Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Couldn't listen to raw mouse input (error " + Marshal.GetLastWin32Error() + ").");
        Pulse.Core.Input.PhysicalInput.OtherStarted();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT) Pulse.Core.Input.PhysicalInput.FromOtherWindow(lParam);
        if (msg == WM_INPUT && _recording) _stamps.Add(_sw.Elapsed.TotalMilliseconds);
        return IntPtr.Zero;
    }

    public void Start() { _stamps.Clear(); _sw.Restart(); _recording = true; }

    public PollingResult Stop()
    {
        _recording = false;
        return ControllerPollingMeter.Summarize("Mouse (raw input)", new List<double>(_stamps), "");
    }

    public void Dispose()
    {
        try
        {
            var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }
        Pulse.Core.Input.PhysicalInput.OtherStopped();
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}

/// <summary>Boot durations Windows itself records (Diagnostics-Performance event 100).</summary>
public static class BootTimeMeter
{
    public static List<(DateTime When, double Seconds)> LastBoots(int count = 5)
    {
        var script = $@"
$e = Get-WinEvent -FilterHashtable @{{LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=100}} -MaxEvents {count} -ErrorAction SilentlyContinue
$out = foreach ($x in $e) {{
  $xml = [xml]$x.ToXml()
  $ms = ($xml.Event.EventData.Data | Where-Object Name -eq 'BootTime').'#text'
  [pscustomobject]@{{ When = $x.TimeCreated.ToString('o'); Ms = [double]$ms }}
}}
ConvertTo-Json -InputObject @($out) -Compress";
        var r = Shell.PowerShell(script, 60_000);
        var list = new List<(DateTime, double)>();
        try
        {
            using var doc = JsonDocument.Parse(r.Output.Trim().Length > 0 ? r.Output : "[]");
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add((DateTime.Parse(e.GetProperty("When").GetString()), e.GetProperty("Ms").GetDouble() / 1000.0));
        }
        catch { }
        return list;
    }
}
