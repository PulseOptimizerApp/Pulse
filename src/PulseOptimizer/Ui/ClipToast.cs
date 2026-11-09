using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Pulse.Ui;

/// <summary>
/// The clip popup: slides in from the screen edge the moment you clip ("Clipping…"), turns into "Clip saved" with
/// a picture of the clip, shows a draining line for how long it stays, then slides away. It never takes focus and
/// clicks pass through it, so the game keeps every keypress. (Games in exclusive fullscreen draw over it; the sound
/// still plays.)
/// </summary>
public static class ClipToast
{
    public enum Kind { Saving, Ok, Problem }

    static Window _w;
    static Grid _slot;           // holds the current card, cross-faded on updates
    static TranslateTransform _slide;
    static DispatcherTimer _hide;
    static string _corner = "TopRight";
    static string _clip;         // the clip this popup is about, so its picture can be added when ready
    static string _shownAt;      // the corner the popup on screen is in
    static bool _hiding;

    const double Width = 300, Edge = 20, Travel = 36;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    /// <summary>Where the popup shows: TopRight, TopLeft, BottomRight or BottomLeft.</summary>
    public static string Corner { get => _corner; set => _corner = value is "TopLeft" or "BottomRight" or "BottomLeft" ? value : "TopRight"; }

    static bool Left => _corner.EndsWith("Left");
    static bool Bottom => _corner.StartsWith("Bottom");

    public static void Show(string title, string detail, bool ok) => Show(title, detail, ok ? Kind.Ok : Kind.Problem);

    /// <summary>Shows the popup, or updates the one already on screen in place (no flicker between "Clipping…" and "Saved").</summary>
    public static void Show(string title, string detail, Kind kind, string clip = null)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Show(title, detail, kind, clip)); return; }

        _clip = kind == Kind.Ok ? clip : null;
        Clickable(_clip != null);
        var card = Card(app, title, detail, kind, clip);
        // "Clipping…" waits for the save (with a safety limit); results stay a moment so you can read them.
        var stay = kind == Kind.Saving ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(kind == Kind.Ok ? 3.2 : 4.5);
        Present(app, card, stay, kind);
    }

    /// <summary>The match recap card after a match (like Medal's): agent, result, rank, score, K/D/A, headshots, big plays and the best clip.</summary>
    public static void Recap(Core.Games.MatchRecord m, int clips, string bestClip)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Recap(m, clips, bestClip)); return; }
        _clip = bestClip;   // clicking the recap opens its best clip
        Clickable(_clip != null);
        Present(app, RecapCard.Build(app, m, clips, bestClip), TimeSpan.FromSeconds(10), Kind.Ok);
    }

    static void Present(Application app, FrameworkElement card, TimeSpan stay, Kind kind)
    {

        if (_w != null && _w.IsVisible && _slot != null && _shownAt == _corner)
        {
            // If it was sliding away, bring it back instead of letting it close.
            _hiding = false;
            _w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Swap(card);
            Restart(stay, kind);
            return;
        }

        _w?.Close();
        _shownAt = _corner;
        _slot = new Grid();
        _slot.Children.Add(card);
        _slide = new TranslateTransform(Left ? -Travel : Travel, 0);
        var root = new Grid { Margin = new Thickness(18), RenderTransform = _slide, Children = { _slot } };

        var area = SystemParameters.WorkArea;
        _w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, Content = root, Opacity = 0,
            FontFamily = (FontFamily)app.FindResource("UiFont"),
        };
        _w.SourceInitialized += (s, _) =>
        {
            _hwnd = new WindowInteropHelper((Window)s).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
            Clickable(_clip != null);
        };
        // A popup about a saved clip can be clicked to watch it; hovering keeps it up.
        root.MouseLeftButtonUp += (_, _) => { if (_clip != null) OpenClip(app, _clip); };
        root.MouseEnter += (_, _) => { if (_clip != null) _hide?.Stop(); };
        root.MouseLeave += (_, _) => { if (_clip != null && !_hiding) _hide?.Start(); };
        _w.Loaded += (s, _) =>
        {
            var w = (Window)s;
            w.Left = Left ? area.Left + Edge - 18 : area.Right - w.ActualWidth - Edge + 18;
            w.Top = Bottom ? area.Bottom - w.ActualHeight - Edge + 18 : area.Top + Edge - 18;
            // Stay pinned to the bottom edge if the card grows (a picture arrives).
            if (Bottom) w.SizeChanged += (_, e) => w.Top = area.Bottom - e.NewSize.Height - Edge + 18;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        };
        _w.Show();
        Restart(stay, kind);
    }

    static IntPtr _hwnd;

    /// <summary>
    /// The popup lets clicks through (so it never gets in the way, in a game or on the desktop) unless it's showing a
    /// saved clip: then it can be clicked to open it.
    /// </summary>
    static void Clickable(bool on)
    {
        if (_w == null || _hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT);
        if (_w.Content is FrameworkElement root) root.Cursor = on ? Cursors.Hand : null;
    }

    /// <summary>Brings Pulse up with the clip open in the player.</summary>
    static void OpenClip(Application app, string clip)
    {
        _hide?.Stop();
        Hide();
        if (app.MainWindow is not MainWindow mw) return;
        mw.ShowFromTray();
        mw.Navigate("clips", "open:" + clip);
    }

    /// <summary>The "Clipping…" popup, shown the moment the clip key is pressed.</summary>
    public static void Saving(int seconds) =>
        Show(Loc.T("Clipping…"), seconds > 0 ? Loc.F("Last {0} seconds", seconds) : "", Kind.Saving);

    /// <summary>Adds the clip's picture once it's made, if the popup for that clip is still up.</summary>
    public static void Picture(string clip)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Picture(clip)); return; }
        if (_w == null || !_w.IsVisible || _clip != clip || _slot?.Children.Count == 0) return;
        if (_slot.Children[^1] is FrameworkElement card && card.Tag is Action<string> addPicture) addPicture(clip);
    }

    static void Swap(FrameworkElement card)
    {
        var old = _slot.Children.OfType<FrameworkElement>().ToList();
        card.Opacity = 0;
        _slot.Children.Add(card);
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        foreach (var o in old)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => _slot.Children.Remove(o);
            o.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    static void Restart(TimeSpan stay, Kind kind)
    {
        _hide?.Stop();
        // The draining line under the card shows how long it stays (not while still saving).
        if (kind != Kind.Saving && _slot.Children[^1] is FrameworkElement card && card.FindName("Drain") is FrameworkElement drain
            && drain.RenderTransform is ScaleTransform st)
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, stay));

        _hide = new DispatcherTimer { Interval = stay };
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        _hide.Start();
    }

    static void Hide()
    {
        var w = _w;
        if (w == null) return;
        _hiding = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!_hiding && _w == w) return; // shown again while sliding away
            w.Close();
            if (_w == w) { _w = null; _slot = null; }
        };
        _slide?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Left ? -Travel : Travel, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        w.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    static FrameworkElement Card(Application app, string title, string detail, Kind kind, string clip)
    {
        var accent = (Brush)app.FindResource(kind switch { Kind.Ok => "GoodBrush", Kind.Problem => "WarnBrush", _ => "AccentBrush" });
        var accentColor = accent is SolidColorBrush sb ? sb.Color : Colors.White;

        var scope = new Grid();
        NameScope.SetNameScope(scope, new NameScope());

        // Picture of the clip (added when ready), across the top of the card.
        // Picture of the clip (added when ready), across the top, with a dark fade so the text under it reads well.
        var picture = new Border { CornerRadius = new CornerRadius(11, 11, 0, 0), ClipToBounds = true, Height = 0, Opacity = 0 };

        // The badge: a soft square with the icon; it pops in.
        var pop = new ScaleTransform(0.6, 0.6);
        FrameworkElement icon;
        if (kind == Kind.Saving)
        {
            var spin = new RotateTransform();
            icon = new Ellipse
            {
                Width = 16, Height = 16, StrokeThickness = 2.2, Stroke = accent, StrokeDashArray = new DoubleCollection { 7, 4 },
                StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = spin,
            };
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            icon = new TextBlock
            {
                Text = kind == Kind.Ok ? "" : "", FontFamily = (FontFamily)app.FindResource("IconFont"), FontSize = 16,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0x26, accentColor.R, accentColor.G, accentColor.B)),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pop, VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid { Children = { icon }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var back = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = back });
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = back });

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title.ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontWeight = FontWeights.Bold, FontSize = 14,
            FontFamily = (FontFamily)app.FindResource("DisplayFont"), Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrEmpty(detail))
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xA6, 0xA0)), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

        var row = new Grid { Margin = new Thickness(14, 12, 16, 13) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(badge);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var drain = new Rectangle
        {
            Height = 2, Fill = accent, Opacity = kind == Kind.Saving ? 0 : 0.9, VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        scope.RegisterName("Drain", drain);

        var body = new StackPanel { Children = { picture, row } };
        var card = new Border
        {
            Width = Width, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x12, 0x12, 0x16)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { body, drain } },
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        scope.Children.Add(card);

        // Called when the clip's picture is ready: grows the card smoothly to show it.
        scope.Tag = (Action<string>)(c =>
        {
            var thumb = Core.Clips.ClipLibrary.ThumbFor(c);
            if (!File.Exists(thumb) || picture.Child != null) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bmp.DecodePixelWidth = 640;
                bmp.UriSource = new Uri(thumb);
                bmp.EndInit();
                var fade = new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom, Height = 46,
                    Background = new LinearGradientBrush(Color.FromArgb(0, 0x12, 0x12, 0x16), Color.FromArgb(0xF2, 0x12, 0x12, 0x16), 90),
                };
                picture.Child = new Grid { Children = { new Image { Source = bmp, Stretch = Stretch.UniformToFill }, fade } };
                var h = Width * 9 / 16;
                picture.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                picture.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
                // Give people a moment to see the picture.
                if (_hide != null && _hide.IsEnabled) { _hide.Stop(); _hide.Interval = TimeSpan.FromSeconds(3); _hide.Start(); }
                if (drain.RenderTransform is ScaleTransform st) st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(3)));
            }
            catch { }
        });
        if (clip != null) Dispatcher.CurrentDispatcher.BeginInvoke(() => ((Action<string>)scope.Tag)(clip), DispatcherPriority.Background);
        return scope;
    }
}
