using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Pulse.Ui;

/// <summary>
/// Pulse's icon by the clock, shown only while Clips keeps running with the window closed. Click to open Pulse;
/// right-click for Save clip / Open Pulse / Quit. Built on Windows' own Shell_NotifyIcon (no extra libraries).
/// </summary>
public static class TrayIcon
{
    const int WM_APP_TRAY = 0x8000 + 77;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);

    static HwndSource _window;
    static IntPtr _icon;
    static bool _shown;

    public static event Action OpenRequested, QuitRequested;

    public static void Show()
    {
        if (_shown) return;
        _window ??= Create();
        if (_icon == IntPtr.Zero)
        {
            var small = new IntPtr[1];
            ExtractIconEx(Environment.ProcessPath!, 0, null, small, 1);
            _icon = small[0];
        }
        var d = Data();
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        _shown = Shell_NotifyIcon(NIM_ADD, ref d);
    }

    public static void Hide()
    {
        if (!_shown) return;
        var d = Data();
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _shown = false;
    }

    static NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = Loc.T("Pulse Optimizer: Clips is on"),
        szInfo = "", szInfoTitle = "",
    };

    static HwndSource Create()
    {
        var w = new HwndSource(new HwndSourceParameters("Pulse tray") { WindowStyle = 0, Width = 0, Height = 0 });
        w.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == WM_APP_TRAY)
            {
                int ev = lp.ToInt32() & 0xFFFF;
                if (ev == WM_LBUTTONUP) OpenRequested?.Invoke();
                else if (ev == WM_RBUTTONUP) Menu(hwnd);
                handled = true;
            }
            return IntPtr.Zero;
        });
        return w;
    }

    static void Menu(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd); // so the menu closes when you click elsewhere
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        MenuItem Item(string text, Action a)
        {
            var mi = new MenuItem { Header = Loc.T(text) };
            mi.Click += (_, _) => a();
            return mi;
        }
        menu.Items.Add(Item("Save clip", () => _ = ClipsService.SaveClip()));
        menu.Items.Add(Item("Open Pulse", () => OpenRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Pulse", () => QuitRequested?.Invoke()));
        menu.IsOpen = true;
    }
}
