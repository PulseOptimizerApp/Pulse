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
        if (!ClaimSingleInstance(e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase)))
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try { Ui.Loc.Init(Core.TweakEngine.State.Settings.Language); } catch { }
        var window = new Pulse.MainWindow();
        Ui.TrayIcon.OpenRequested += window.ShowFromTray;
        Ui.TrayIcon.QuitRequested += window.QuitApp;
        ListenForShow(window);
        SessionEnding += (_, _) => window.PrepareForExit();
        Ui.ClipsService.Init();

        // Started at sign-in for Clips: stay in the tray until the user opens Pulse.
        bool tray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) && Core.TweakEngine.State.Clips is { Enabled: true };
        if (tray) Ui.TrayIcon.Show();
        else window.Show();
    }

    const string MutexName = @"Local\PulseOptimizer.SingleInstance";
    const string ShowEventName = @"Local\PulseOptimizer.Show";
    static Mutex _instance;

    static bool ClaimSingleInstance(bool restarted)
    {
        _instance = new Mutex(false, MutexName);
        try { return _instance.WaitOne(restarted ? 10_000 : 0); } // after a language restart, wait for the old one to exit
        catch (AbandonedMutexException) { return true; }
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
