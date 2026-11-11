using System.Windows.Interop;
using Pulse.Core;

namespace Pulse.Ui;

/// <summary>A key (Ctrl+Alt+F12 unless you pick another) swaps between PC and console from anywhere, even with the screen showing the console.</summary>
public static class ConsoleSwitchService
{
    static HwndSource _win;
    const int Id = 0x5C01, SoundId = 0x5C02;
    const uint MOD_NOREPEAT = 0x4000;
    static (int, int, bool, int, int) _keys;

    /// <summary>False when another app already owns the sound key.</summary>
    public static bool SoundKeyWorks { get; private set; } = true;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    static ConsoleSwitchSettings S => TweakEngine.State.ConsoleSwitch ??= new ConsoleSwitchSettings();

    /// <summary>False when another app already owns the key.</summary>
    public static bool HotkeyWorks { get; private set; } = true;

    public static event Action Swapped;

    public static void Init() => Refresh();

    /// <summary>Registers or drops the hotkey to match the settings.</summary>
    public static void Refresh()
    {
        bool screen = S.Hotkey && S.ConsoleInput != 0 && S.PcInput != 0;
        bool sound = S.SoundKey && !string.IsNullOrEmpty(S.SoundA) && !string.IsNullOrEmpty(S.SoundB);
        bool want = screen || sound;
        var keys = (screen ? S.HotkeyMods : -1, screen ? S.HotkeyKey : -1, sound, S.SoundKeyMods, S.SoundKeyKey);
        if (want == (_win != null) && (!want || _keys == keys)) return;
        Shutdown();
        if (!want) return;
        _keys = keys;
        _win = new HwndSource(new HwndSourceParameters("Pulse console switch") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _win.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == Id) { _ = SwapAsync(!S.OnConsole); handled = true; }
            else if (msg == 0x0312 && w.ToInt32() == SoundId) { _ = FlipSoundAsync(); handled = true; }
            return IntPtr.Zero;
        });
        HotkeyWorks = !screen || RegisterHotKey(_win.Handle, Id, (uint)S.HotkeyMods | MOD_NOREPEAT, (uint)S.HotkeyKey);
        SoundKeyWorks = !sound || RegisterHotKey(_win.Handle, SoundId, (uint)S.SoundKeyMods | MOD_NOREPEAT, (uint)S.SoundKeyKey);
    }

    public static void Shutdown()
    {
        if (_win == null) return;
        UnregisterHotKey(_win.Handle, Id);
        UnregisterHotKey(_win.Handle, SoundId);
        _win.Dispose();
        _win = null;
    }

    /// <summary>Flips the PC's sound to the other device, with a small note in the corner (the tray balloon) saying where.</summary>
    public static async Task FlipSoundAsync()
    {
        var s = S;
        var name = await Task.Run(() => ConsoleSwitch.FlipSound(s));
        var msg = name == null ? Loc.T("Windows didn't take the sound change.") : Loc.F("Sound: {0}", name);
        if (MainWindow.Instance is { IsVisible: true, IsActive: true }) MainWindow.Instance.Toast(msg, name == null ? ToastKind.Warning : ToastKind.Success);
        else TrayIcon.Notify("Pulse", msg);
        Swapped?.Invoke();
    }

    /// <summary>Swaps off the UI thread (monitors can take a moment to answer), then tells you how it went.</summary>
    public static async Task<string> SwapAsync(bool toConsole)
    {
        var s = S;
        var msg = await Task.Run(() => ConsoleSwitch.Swap(s, toConsole));
        TweakEngine.AddLog("console", msg);
        Swapped?.Invoke();
        // On PC there's a screen to show it on; on console the switch itself is the answer.
        if (!toConsole || !s.OnConsole) MainWindow.Instance?.Toast(msg, s.OnConsole == toConsole ? ToastKind.Success : ToastKind.Info);
        return msg;
    }
}
