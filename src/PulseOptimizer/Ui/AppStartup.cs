using Pulse.Core;

namespace Pulse.Ui;

/// <summary>
/// One switch for "start Pulse with Windows", used by Home, the tray menu and settings. Pulse starts quietly in the
/// tray at sign-in (a scheduled task, so it can start with admin rights without a prompt). Clips and Macros used to
/// have their own switches; they all share this one entry.
/// </summary>
public static class AppStartup
{
    public static bool IsOn =>
        TweakEngine.State.Settings.StartWithWindows || TweakEngine.State.Clips?.StartWithWindows == true || TweakEngine.State.MacroOptions?.StartWithWindows == true;

    /// <summary>Turns it on or off. False when Windows didn't allow it.</summary>
    public static bool Set(bool on)
    {
        var st = TweakEngine.State;
        if (on)
        {
            st.Settings.StartWithWindows = true;
            st.Settings.StartInTray = true;
        }
        else
        {
            st.Settings.StartWithWindows = false;
            if (st.Clips != null) st.Clips.StartWithWindows = false;
            if (st.MacroOptions != null) st.MacroOptions.StartWithWindows = false;
        }
        bool ok = ClipsService.SetStartWithWindows(on);
        if (!ok && on) st.Settings.StartWithWindows = false;
        TweakEngine.AddLog("app", on && ok ? "Starts with Windows" : on ? "Windows didn't allow starting with Windows" : "Doesn't start with Windows");
        TweakEngine.Save();
        Changed?.Invoke();
        return ok;
    }

    public static event Action Changed;
}
