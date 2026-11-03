using Pulse.Core;

namespace Pulse.Pages;

/// <summary>
/// Applies a refresh-rate fix like Windows does: switch, ask "Keep these settings?",
/// and switch back automatically after 15 seconds if there's no answer (for example if the screen went black).
/// </summary>
public static class DisplayFixer
{
    public static async Task Apply(string key)
    {
        // key = device|width|height|hz
        var p = key.Split('|');
        if (p.Length != 4 || !int.TryParse(p[1], out var w) || !int.TryParse(p[2], out var h) || !int.TryParse(p[3], out var hz)) return;
        var device = p[0];
        var win = MainWindow.Instance;

        var (ok, msg, prev) = await Task.Run(() => Displays.SetMode(device, w, h, hz));
        if (!ok)
        {
            await win.Alert("Couldn't switch the refresh rate", msg + "\n\nYou can still change it in Windows Settings: the steps are under \"How to fix it\".");
            return;
        }

        var confirm = win.Confirm($"Keep {hz} Hz?", "", "Keep it", "Go back");
        for (int left = 15; left > 0 && !confirm.IsCompleted; left--)
        {
            win.SetDialogText($"If the screen looks wrong, wait and Pulse switches back automatically in {left} second{(left == 1 ? "" : "s")}.");
            await Task.WhenAny(confirm, Task.Delay(1000));
        }

        bool keep = confirm.IsCompleted && confirm.Result;
        if (!confirm.IsCompleted) win.HideOverlay();

        if (keep)
        {
            TweakEngine.AddLog("display", $"Switched {device.TrimStart('\\', '.')} to {hz} Hz");
            win.Toast(string.IsNullOrEmpty(msg) ? $"Now running at {hz} Hz." : msg);
        }
        else if (prev != null)
        {
            await Task.Run(() => Displays.SetMode(device, prev.Width, prev.Height, prev.Hz));
            win.Toast($"Switched back to {prev.Hz} Hz.", ToastKind.Info);
        }
    }
}
