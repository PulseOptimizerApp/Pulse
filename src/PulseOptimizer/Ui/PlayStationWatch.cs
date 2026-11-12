using Pulse.Core;
using Pulse.Core.Clips;

namespace Pulse.Ui;

/// <summary>
/// Notices a USB drive with PS5 (or PS4) captures being plugged in and brings the new clips into Pulse's Clips:
/// copy them to the USB on the console, plug it into the PC, done. Nothing on the USB is changed.
/// </summary>
public static class PlayStationWatch
{
    static System.Threading.Timer _timer;
    static HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    static int _busy;
    static bool _first = true;

    static ClipSettings S => TweakEngine.State?.Clips;

    public static void Init() => _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));

    public static void Shutdown() => _timer?.Dispose();

    static void Tick()
    {
        if (S is not { } s || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = ClipImport.PlayStationFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var plugged = now.Where(f => !_seen.Contains(f)).ToList();
            _seen = now;
            // A drive already in when Pulse started counts as plugged in too, but quietly checked once.
            if (_first) { _first = false; }
            foreach (var folder in plugged) Handle(s, folder);
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    static void Handle(ClipSettings s, string folder)
    {
        var clips = ClipImport.Scan(folder, "PlayStation", s);
        if (clips.Count == 0) return;
        bool webm = clips.Any(c => c.Path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase));
        var app = System.Windows.Application.Current;
        if (!s.AutoImportPlayStation)
        {
            app?.Dispatcher.BeginInvoke(() => TrayIcon.Notify(Loc.T("PS5 clips found"), Loc.F("{0} new clips on your USB drive. Open Pulse's Clips to bring them in.", clips.Count)));
            return;
        }
        if (webm && !Ffmpeg.Installed)
        {
            app?.Dispatcher.BeginInvoke(() => TrayIcon.Notify(Loc.T("PS5 clips found"), Loc.T("Open Pulse's Clips once so it can download its video tools, then plug the USB in again.")));
            return;
        }
        app?.Dispatcher.BeginInvoke(() => ClipToast.Show(Loc.T("Bringing in PS5 clips…"), Loc.F("{0} clips from your USB drive", clips.Count), ClipToast.Kind.Saving));
        var (added, _, _) = ClipImport.Import(clips, s);
        TweakEngine.Save();
        TweakEngine.AddLog("clips", $"Imported {added} PlayStation clip(s) from {folder}");
        app?.Dispatcher.BeginInvoke(() =>
        {
            ClipToast.Show(added > 0 ? Loc.F("{0} PS5 clips added", added) : Loc.T("Couldn't bring in the PS5 clips"),
                added > 0 ? Loc.T("They're in Pulse's Clips. You can unplug the USB.") : Loc.T("Try again from Clips › Import."), added > 0);
        });
    }
}
