using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Pulse.Ui;

/// <summary>
/// The mouse's raw movement in counts, the way games read it (no Windows pointer speed or acceleration). Create on the
/// UI thread; <see cref="Moved"/> is raised there. Shares raw input with Pulse's macros the same way the mouse polling
/// check does, so macros keep working while it runs.
/// </summary>
public sealed class RawMouse : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, byte[] data, ref uint size, uint headerSize);

    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    readonly HwndSource _source;
    readonly byte[] _buf = new byte[128];
    bool _disposed;

    /// <summary>Raw counts moved (x right, y down).</summary>
    public event Action<int, int> Moved;

    public RawMouse()
    {
        _source = new HwndSource(new HwndSourceParameters("PulseRawMouse") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _source.Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            _source.Dispose();
            throw new InvalidOperationException("Couldn't read raw mouse input (error " + Marshal.GetLastWin32Error() + ").");
        }
        Pulse.Core.Input.PhysicalInput.OtherStarted();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        Pulse.Core.Input.PhysicalInput.FromOtherWindow(lParam);
        try
        {
            uint header = (uint)(8 + 2 * IntPtr.Size), size = (uint)_buf.Length;
            if (GetRawInputData(lParam, RID_INPUT, _buf, ref size, header) == uint.MaxValue) return IntPtr.Zero;
            if (BitConverter.ToUInt32(_buf, 0) != 0) return IntPtr.Zero;              // not a mouse
            int o = (int)header;
            ushort flags = BitConverter.ToUInt16(_buf, o);
            if ((flags & MOUSE_MOVE_ABSOLUTE) != 0) return IntPtr.Zero;               // tablets and remote desktop: not counts
            int dx = BitConverter.ToInt32(_buf, o + 12), dy = BitConverter.ToInt32(_buf, o + 16);
            if (dx != 0 || dy != 0) Moved?.Invoke(dx, dy);
        }
        catch { }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
