using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core;
using Pulse.Core.Display;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>Pulse Color: looks made for VALORANT, previewed on your own screenshot.</summary>
public partial class ValorantPage
{
    static VibranceSettings ColorSet => TweakEngine.State.Vibrance ??= new VibranceSettings();
    static BitmapSource _colorShot;
    static string _colorShotPath;
    double _split = 0.5;

    void BuildVibrance()
    {
        var s = ColorSet;
        var sp = new StackPanel();
        sp.Children.Add(Heading("Pulse Color"));
        sp.Children.Add(Dim(Loc.T("Looks made for VALORANT, so enemies are easier to spot. Pulse puts your look on only while the game is in front, and puts your own colours back when you leave it."), 12.5, 4));

        var on = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.Enabled, Content = Loc.T("Use Pulse Color in VALORANT"), Margin = new Thickness(0, 16, 0, 0), Foreground = Res("TextBrush") };
        on.Checked += (_, _) => { s.Enabled = true; TweakEngine.Save(); VibranceService.Refresh(); };
        on.Unchecked += (_, _) => { s.Enabled = false; TweakEngine.Save(); VibranceService.Refresh(); };
        sp.Children.Add(on);

        // Looks.
        var looks = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var p in PulseColor.Presets)
        {
            bool sel = s.Preset == p.Key;
            var t = new StackPanel { Width = 200 };
            t.Children.Add(new TextBlock { Text = Loc.T(p.Name), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            t.Children.Add(Dim(Loc.T(p.Detail), 11.5, 3));
            var b = new Border { Child = t, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = Res("SurfaceHoverBrush"), BorderBrush = sel ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(sel ? 2 : 1) };
            var key = p.Key;
            b.MouseLeftButtonUp += (_, _) => { s.Preset = key; TweakEngine.Save(); VibranceService.Refresh(); Build(); };
            looks.Children.Add(b);
        }
        sp.Children.Add(looks);

        if (s.Preset == "pop")
        {
            sp.Children.Add(Label("Your enemy highlight colour in VALORANT").Also(l => l.Margin = new Thickness(0, 8, 0, 6)));
            var hl = new WrapPanel();
            foreach (var (key, name) in new[] { ("red", "Red"), ("yellow", "Yellow (deuteranopia)"), ("purple", "Purple (tritanopia)") })
            {
                var k = key;
                hl.Children.Add(Chip(Loc.T(name), "pc-hl", s.Highlight == key, () => { s.Highlight = k; TweakEngine.Save(); VibranceService.Refresh(); Build(); }));
            }
            sp.Children.Add(hl);
        }
        if (s.Preset == "custom")
        {
            FrameworkElement SliderRow(string label, double min, double max, double value, Func<double, string> show, Action<double> set)
            {
                var g = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                var t = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Text = Loc.T(label) + ": " + show(value) };
                g.Children.Add(t);
                var sl = new Slider { Minimum = min, Maximum = max, Value = value, Width = 380, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                sl.ValueChanged += (_, e) => { set(e.NewValue); t.Text = Loc.T(label) + ": " + show(e.NewValue); TweakEngine.Save(); VibranceService.Refresh(); RedrawPreview(); };
                g.Children.Add(sl);
                return g;
            }
            sp.Children.Add(SliderRow("Colour", 50, 100, s.Level, v => $"{(int)v}%", v => s.Level = (int)v));
            sp.Children.Add(SliderRow("Brighter shadows", 0, 0.4, s.Shadows, v => $"{(int)(v / 0.4 * 100)}%", v => s.Shadows = v));
            sp.Children.Add(SliderRow("Contrast", 0.9, 1.2, s.Contrast, v => $"{(int)Math.Round((v - 1) * 100):+0;-0;0}%", v => s.Contrast = v));
        }
        Root.Children.Add(Card(sp));

        // Preview on the player's own screenshot.
        var pv = new StackPanel();
        pv.Children.Add(Heading("Preview"));
        pv.Children.Add(Dim(_colorShot != null ? Loc.T("Your latest VALORANT screenshot: drag the line to compare. Left is how it looks now, right is with Pulse Color.")
            : Loc.T("Take a screenshot in VALORANT with Pulse's screenshot key (or add any picture) to see your look on it. For now, a test picture."), 12.5, 4));
        pv.Children.Add(PreviewBox());
        var pick = Btn("Use another picture");
        pick.HorizontalAlignment = HorizontalAlignment.Left;
        pick.Margin = new Thickness(0, 10, 0, 0);
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog() == true) { _colorShot = LoadShot(dlg.FileName); _colorShotPath = dlg.FileName; Build(); }
        };
        pv.Children.Add(pick);
        Root.Children.Add(Card(pv));

        var card = VibranceService.Card;
        Root.Children.Add(Faint(card != null
            ? Loc.F("Uses your {0} graphics card's vibrance and Windows' screen colour curves. Your own settings are kept and put back. Nothing in the game is changed.", card.Card)
            : Loc.T("Uses Windows' screen colour curves (Pulse didn't find a vibrance control on this graphics card, so colour boost is lighter). Nothing in the game is changed."), 11.5, 12));
    }

    Image _previewAfter;
    Border _previewClip;

    static BitmapSource LoadShot(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = 900; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
            return new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).Also(f => f.Freeze());
        }
        catch { return null; }
    }

    /// <summary>The newest VALORANT screenshot (or clip picture) Pulse saved.</summary>
    static string LatestShot()
    {
        try
        {
            var c = TweakEngine.State.Clips;
            var dirs = new[] { c.ScreenshotsFolder, Path.Combine(c.ClipsFolder, "VALORANT") };
            return dirs.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>A test picture when there's no screenshot: soft gradients, a dark corner, and red, yellow and purple marks.</summary>
    static BitmapSource TestPicture()
    {
        int w = 900, h = 420;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double fx = x / (double)w, fy = y / (double)h;
                double r = 150 + 50 * fx, g = 140 + 40 * (1 - fy), b = 120 + 70 * fy;
                if (fx < 0.3 && fy > 0.45) { r *= 0.18; g *= 0.18; b *= 0.2; }         // a dark doorway
                if (fy > 0.8) { r = 110; g = 95; b = 80; }                              // the floor
                px[i] = (byte)b; px[i + 1] = (byte)g; px[i + 2] = (byte)r; px[i + 3] = 255;
            }
        void Mark(int cx, int cy, byte r, byte g, byte b)
        {
            for (int y = cy - 40; y < cy + 40; y++)
                for (int x = cx - 14; x < cx + 14; x++)
                {
                    bool edge = Math.Abs(x - cx) > 10 || Math.Abs(y - cy) > 36;
                    int i = (y * w + x) * 4;
                    if (edge) { px[i] = b; px[i + 1] = g; px[i + 2] = r; }
                    else { px[i] = 40; px[i + 1] = 40; px[i + 2] = 46; }
                }
        }
        Mark(120, 300, 220, 60, 60);   // in the dark
        Mark(450, 200, 230, 220, 60);
        Mark(720, 230, 190, 70, 210);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    FrameworkElement PreviewBox()
    {
        if (_colorShot == null && _colorShotPath == null)
        {
            _colorShotPath = LatestShot() ?? "";
            if (_colorShotPath.Length > 0) _colorShot = LoadShot(_colorShotPath);
        }
        var src = _colorShot ?? TestPicture();
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.SizeWE, ClipToBounds = true };
        var before = new Image { Source = src, Stretch = Stretch.Uniform };
        _previewAfter = new Image { Source = Styled(src), Stretch = Stretch.Uniform };
        _previewClip = new Border { HorizontalAlignment = HorizontalAlignment.Right, ClipToBounds = true };
        var line = new Border { Width = 2, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(before);
        grid.Children.Add(_previewClip);
        grid.Children.Add(line);
        void Place()
        {
            double w = grid.ActualWidth;
            if (w <= 0) return;
            _previewClip.Width = w * (1 - _split);
            _previewAfter.Width = w;
            _previewAfter.HorizontalAlignment = HorizontalAlignment.Right;
            _previewClip.Clip = null;
            line.Margin = new Thickness(w * _split - 1, 0, 0, 0);
        }
        grid.SizeChanged += (_, _) => Place();
        void Drag(MouseEventArgs e) { if (grid.ActualWidth > 0) { _split = Math.Clamp(e.GetPosition(grid).X / grid.ActualWidth, 0.02, 0.98); Place(); } }
        grid.MouseLeftButtonDown += (_, e) => { grid.CaptureMouse(); Drag(e); };
        grid.MouseMove += (_, e) => { if (grid.IsMouseCaptured) Drag(e); };
        grid.MouseLeftButtonUp += (_, _) => grid.ReleaseMouseCapture();
        // Right-aligned picture inside a right-aligned clip keeps both halves lined up.
        _previewClip.Child = new Grid { Children = { _previewAfter }, HorizontalAlignment = HorizontalAlignment.Right };
        return grid;
    }

    BitmapSource Styled(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        PulseColor.Apply(px, ColorSet.Look);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    void RedrawPreview()
    {
        if (_previewAfter == null) return;
        _previewAfter.Source = Styled(_colorShot ?? TestPicture());
    }
}
