using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pulse.Ui;

/// <summary>
/// A small "Clip saved" card in the top-right corner. It never takes focus and clicks pass through it, so the game
/// keeps every keypress. (Games in exclusive fullscreen draw over it; the sound still plays.)
/// </summary>
public static class ClipToast
{
    static Window _w;
    static DispatcherTimer _hide;

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    public static void Show(string title, string detail, bool ok)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Show(title, detail, ok)); return; }

        _w?.Close();
        var accent = (Brush)app.FindResource(ok ? "GoodBrush" : "WarnBrush");
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x16, 0x1D)),
            BorderBrush = (Brush)app.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 20, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = ok ? "" : "", FontFamily = (FontFamily)app.FindResource("IconFont"), FontSize = 18, Foreground = accent, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel
                    {
                        Margin = new Thickness(12, 0, 0, 0),
                        Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Brushes.White },
                            new TextBlock { Text = detail, FontSize = 12, Foreground = (Brush)app.FindResource("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) },
                        },
                    },
                },
            },
        };
        var area = SystemParameters.WorkArea;
        _w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            Content = card, Opacity = 0, FontFamily = (FontFamily)app.FindResource("UiFont"),
        };
        _w.SourceInitialized += (s, _) =>
        {
            var h = new WindowInteropHelper((Window)s).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
        };
        _w.Loaded += (s, _) =>
        {
            var w = (Window)s;
            w.Left = area.Right - w.ActualWidth - 24;
            w.Top = area.Top + 24;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        };
        _w.Show();

        _hide?.Stop();
        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var w = _w;
            if (w == null) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => { w.Close(); if (_w == w) _w = null; };
            w.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        _hide.Start();
    }
}
