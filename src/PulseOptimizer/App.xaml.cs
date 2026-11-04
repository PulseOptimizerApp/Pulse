using System.Windows;
using System.Windows.Threading;

namespace Pulse;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiError;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        base.OnStartup(e);

        // The new version putting itself in place after an update (see Updater.FinishUpdate).
        int fu = Array.FindIndex(e.Args, a => a.Equals("--finish-update", StringComparison.OrdinalIgnoreCase));
        if (fu >= 0 && fu + 2 < e.Args.Length)
        {
            Core.Updater.FinishUpdate(e.Args[fu + 1], int.TryParse(e.Args[fu + 2], out var pid) ? pid : 0);
            Shutdown();
            return;
        }

        // Roblox's Play button (when Roblox launches through Pulse): no window, no admin rights needed.
        int r = Array.FindIndex(e.Args, a => a.Equals("--roblox", StringComparison.OrdinalIgnoreCase));
        if (r >= 0)
        {
            try { Core.Roblox.RobloxBoost.HandleLaunch(r + 1 < e.Args.Length ? e.Args[r + 1] : ""); } catch { }
            Shutdown();
            return;
        }

        // The app itself changes system settings, so it runs as administrator.
        if (!IsAdmin() && !e.Args.Contains("--tune-up", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", e.Args.Select(a => "\"" + a + "\"")))
                { UseShellExecute = true, Verb = "runas" });
            }
            catch { /* the user said no to the admin prompt */ }
            Shutdown();
            return;
        }

        // The weekly scheduled run: no window, just the safe clean-up and a drift check.
        if (e.Args.Contains("--tune-up", StringComparer.OrdinalIgnoreCase))
        {
            try { Core.Fresh.Freshness.RunScheduled(); } catch { }
            Shutdown();
            return;
        }
        // One Pulse at a time: a second launch just brings the first one forward (two recorders would fight).
        // A different copy (a new version downloaded somewhere else, or the same file replaced) takes over: the old
        // one, often hidden in the tray, closes so it no longer holds its file ("File In Use") or runs old code.
        bool restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
        if (!ClaimSingleInstance(restarted))
        {
            if (RunningIsOtherCopy())
            {
                try { EventWaitHandle.OpenExisting(QuitEventName).Set(); } catch { }
                if (!ClaimSingleInstance(true, 20_000)) { Shutdown(); return; }
            }
            else
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
        }
        // An update was downloaded and checked but the file swap didn't happen: finish it now (no second download).
        if (Core.Updater.ResumeSwap()) { Shutdown(); return; }
        Core.Updater.TidyAfterUpdate();
        WriteInstanceInfo();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try { Ui.Loc.Init(Core.TweakEngine.State.Settings.Language); } catch { }
        Core.TweakEngine.Migrate();
        var window = new Pulse.MainWindow();
        Ui.TrayIcon.OpenRequested += window.ShowFromTray;
        Ui.TrayIcon.QuitRequested += window.QuitApp;
        ListenForShow(window);
        ListenForQuit(window);
        SessionEnding += (_, _) => window.PrepareForExit();
        Ui.ClipsService.Init();
        Ui.GameSessionService.Init();

        // Started at sign-in for Clips: stay in the tray until the user opens Pulse.
        bool tray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) && Core.TweakEngine.State.Clips is { Enabled: true };
        if (tray) Ui.TrayIcon.Show();
        else window.Show();
    }

    const string MutexName = @"Local\PulseOptimizer.SingleInstance";
    const string ShowEventName = @"Local\PulseOptimizer.Show";
    static Mutex _instance;

    const string QuitEventName = @"Local\PulseOptimizer.Quit";
    static string InstanceFile => System.IO.Path.Combine(Core.TweakEngine.DataDir, "running.txt");

    static bool ClaimSingleInstance(bool restarted, int waitMs = 10_000)
    {
        _instance ??= new Mutex(false, MutexName);
        try { return _instance.WaitOne(restarted ? waitMs : 0); } // after a restart or a takeover, wait for the old one to exit
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Which file and version the running Pulse is, so a different copy can take over from it.</summary>
    static void WriteInstanceInfo()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Core.TweakEngine.DataDir);
            System.IO.File.WriteAllText(InstanceFile, Environment.ProcessPath + "\n" + Core.Updater.CurrentVersion + "\n" + System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).Ticks);
        }
        catch { }
    }

    static bool RunningIsOtherCopy()
    {
        try
        {
            var lines = System.IO.File.ReadAllLines(InstanceFile);
            if (lines.Length < 3) return false;
            return !string.Equals(lines[0], Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                || lines[1] != Core.Updater.CurrentVersion
                || lines[2] != System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).Ticks.ToString();
        }
        catch { return false; }
    }

    void ListenForQuit(Pulse.MainWindow window)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var t = new Thread(() =>
        {
            ev.WaitOne();
            Dispatcher.BeginInvoke(() => { window.PrepareForExit(); window.QuitApp(); Shutdown(); });
        }) { IsBackground = true, Name = "Pulse takeover listener" };
        t.Start();
    }

    void ListenForShow(Pulse.MainWindow window)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                Dispatcher.BeginInvoke(window.ShowFromTray);
            }
        }) { IsBackground = true, Name = "Pulse show listener" };
        t.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Ui.ClipsService.Shutdown(); } catch { }
        try { Ui.GameSessionService.Shutdown(); } catch { }
        try { Ui.TrayIcon.Hide(); } catch { }
        try { _instance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try { Core.TweakEngine.AddLog("error", e.Exception.Message); } catch { }
        if (MainWindow is Pulse.MainWindow w && w.IsLoaded)
            _ = w.Alert("Something went wrong", e.Exception.Message + "\n\nNothing was left half-changed: tweaks roll back automatically if they fail.");
        else
            MessageBox.Show(e.Exception.ToString(), "Pulse Optimizer");
    }
}
