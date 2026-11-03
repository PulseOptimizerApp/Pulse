using Pulse.Core;

namespace Pulse.Pages;

/// <summary>Applies and undoes tweaks from the UI: restore point first, confirmation for risky ones, clear feedback.</summary>
public static class TweakRunner
{
    static bool _restorePointThisSession;
    static MainWindow W => MainWindow.Instance;

    public static async Task EnsureRestorePoint()
    {
        if (!TweakEngine.State.Settings.AutoRestorePoint || _restorePointThisSession) return;
        W.ShowBusy("Creating a safety restore point",
            "A Windows restore point lets you roll the whole system back if anything ever goes wrong. Pulse makes one before your first change each session. It can take up to a minute.",
            "Creating restore point…");
        var (ok, msg) = await Task.Run(() => TweakEngine.CreateRestorePoint());
        W.HideOverlay();
        _restorePointThisSession = true;
        if (!ok) W.Toast("Couldn't create a restore point (" + msg + "). Each tweak still has its own undo.", ToastKind.Warning);
    }

    /// <summary>Turns a tweak on or off. Returns true if it changed.</summary>
    public static async Task<bool> Set(Tweak t, bool on)
    {
        if (on && !t.GateStatus.Met)
        {
            if (!await W.Confirm("This PC may not benefit", t.GateStatus.Why + "\n\nApply it anyway? You can undo it any time.", "Apply anyway"))
            { t.Raise(nameof(Tweak.IsOn)); return false; }
        }
        else if (on && t.Risk == Risk.Advanced)
        {
            if (!await W.Confirm($"Apply “{t.Title}”?", t.Tradeoff + "\n\nYou can undo it any time from this page or Undo & restore.", "Apply"))
            { t.Raise(nameof(Tweak.IsOn)); return false; }
        }

        t.IsBusy = true;
        try
        {
            if (on) await EnsureRestorePoint();
            await Task.Run(() => { if (on) TweakEngine.Apply(t); else TweakEngine.Revert(t); });
            t.IsOn = on;
            t.Raise(nameof(Tweak.IsOn));
            var note = on && t.HasRestartNote ? $" {t.RestartNote} to finish." : "";
            W.Toast((on ? "Applied: " : "Undone: ") + t.Title + "." + note);
            return true;
        }
        catch (Exception ex)
        {
            t.Raise(nameof(Tweak.IsOn));
            await W.Alert("That change didn't go through", ex.Message + "\n\nNothing was left half-changed.");
            return false;
        }
        finally { t.IsBusy = false; }
    }

    /// <summary>Applies the recommended tweaks whose gate this PC meets.</summary>
    public static async Task<int> ApplyRecommended()
    {
        var todo = TweakCatalog.All.Where(t => t.Recommended && !t.IsOn && t.GateStatus.Met).ToList();
        if (todo.Count == 0)
        {
            W.Toast("All recommended tweaks for this PC are already on.", ToastKind.Info);
            return 0;
        }
        var list = string.Join("\n", todo.Select(t => "•  " + t.Title));
        if (!await W.Confirm($"Apply {todo.Count} recommended tweak{(todo.Count == 1 ? "" : "s")}?",
                list + "\n\nThese are the safe, documented ones that fit this PC. Each one is backed up and can be undone.", "Apply all"))
            return 0;

        await EnsureRestorePoint();
        W.ShowBusy("Applying tweaks", "Each change is backed up before it's made.");
        int ok = 0;
        var failed = new List<string>();
        foreach (var t in todo)
        {
            W.SetBusyStatus(t.Title + "…");
            t.IsBusy = true;
            try
            {
                await Task.Run(() => TweakEngine.Apply(t));
                t.IsOn = true;
                ok++;
            }
            catch (Exception ex) { failed.Add($"{t.Title}: {ex.Message}"); }
            finally { t.IsBusy = false; }
        }
        W.HideOverlay();
        if (failed.Count > 0)
            await W.Alert($"Applied {ok} of {todo.Count}", "These didn't go through and were left unchanged:\n\n" + string.Join("\n", failed));
        else
            W.Toast($"Applied {ok} tweaks. Some take full effect after you sign out or restart.");
        return ok;
    }
}
