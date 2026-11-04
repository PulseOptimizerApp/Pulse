using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Measurement;

namespace Pulse.Ui;

/// <summary>
/// Watches for a game (every few seconds) and, if the user turned them on, switches on game mode and shows the FPS
/// overlay while it runs. Runs in the tray too.
/// </summary>
public static class GameSessionService
{
    static GameSessionSettings S => TweakEngine.State.GameSession ??= new GameSessionSettings();
    static DispatcherTimer _timer, _overlayTimer;
    static int _pid;
    static LiveFrames _frames;
    static FpsOverlay _overlay;

    public static void Init()
    {
        if (_timer != null) return;
        GamePowerPlan.End(); // left over from a Pulse that was closed mid-game
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await Tick();
        _timer.Start();
        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _overlayTimer.Tick += (_, _) => UpdateOverlay();
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        StopOverlay();
        GamePowerPlan.End();
    }

    /// <summary>Settings changed on the Games page.</summary>
    public static void Refresh() { _pid = -1; _ = Tick(); }

    static async Task Tick()
    {
        bool want = S.GameMode || S.Overlay;
        string game = want ? await Task.Run(GameDetector.RunningGame) : null;
        int pid = game != null ? GameDetector.GamePid : 0;

        if (pid == 0)
        {
            if (_pid != 0) { StopOverlay(); GamePowerPlan.End(); }
            _pid = 0;
            return;
        }
        if (pid == _pid) return;
        _pid = pid;

        if (S.GameMode) GamePowerPlan.Start(); else GamePowerPlan.End();
        StopOverlay();
        if (S.Overlay)
        {
            string name = null;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
            if (name != null && (_frames = LiveFrames.Start(name)) != null)
            {
                _overlay = new FpsOverlay(S.OverlayCorner);
                _overlay.Show();
                _overlayTimer.Start();
            }
        }
    }

    static void UpdateOverlay()
    {
        if (_frames == null || _overlay == null) return;
        if (!_frames.Running) { StopOverlay(); _pid = -1; return; }
        var (fps, low, ms) = _frames.Now();
        _overlay.Set(fps, low, ms);
    }

    static void StopOverlay()
    {
        _overlayTimer?.Stop();
        _frames?.Dispose(); _frames = null;
        _overlay?.Close(); _overlay = null;
    }
}

/// <summary>A small always-on-top FPS box in a corner. Clicks pass through it to the game; it never takes focus.</summary>
sealed class FpsOverlay : Window
{
    readonly TextBlock _fps = new() { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
    readonly TextBlock _detail = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xC3, 0xC7, 0xD3)) };
    readonly string _corner;

    public FpsOverlay(string corner)
    {
        _corner = corner;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Focusable = false; IsHitTestVisible = false;
        var accent = new LinearGradientBrush(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x8B, 0x5C, 0xF6), 0);
        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_fps);
        stack.Children.Add(top);
        stack.Children.Add(_detail);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x0B, 0x0C, 0x10)), CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 14, 9), Child = stack,
        };
        _fps.Text = "– FPS";
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    void Place()
    {
        double m = 16;
        Left = _corner.EndsWith('r') ? SystemParameters.PrimaryScreenWidth - ActualWidth - m : m;
        Top = _corner.StartsWith('b') ? SystemParameters.PrimaryScreenHeight - ActualHeight - m : m;
    }

    public void Set(double fps, double low, double ms)
    {
        _fps.Text = fps > 0 ? Loc.F("{0} FPS", Math.Round(fps)) : "– FPS";
        _detail.Text = fps > 0 ? Loc.F("1% low {0} · {1} ms", Math.Round(low), ms.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)) : Loc.T("Waiting for frames…");
    }

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
