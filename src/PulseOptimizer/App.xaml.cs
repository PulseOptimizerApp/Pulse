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
        try { Ui.Loc.Init(Core.TweakEngine.State.Settings.Language); } catch { }
        new Pulse.MainWindow().Show();
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
