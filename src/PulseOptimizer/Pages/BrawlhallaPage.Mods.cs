using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;
using Pulse.Ui;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Pulse.Pages;

/// <summary>
/// Customize: your pictures on skin parts, your colors on a skin, recolored effects (dashes, dodges, gravity cancels,
/// attacks), map background themes, and smoother-play settings. Changes are collected here and written into the
/// game's files with one Apply (Brawlhalla closed); Restore originals puts every file back.
/// </summary>
public sealed partial class BrawlhallaPage
{
    string _mode = "stats", _modTab = "skins", _skinSearch = "", _bgTheme = "clean", _modStatus, _openColor;
    List<BhSkin> _skins;
    double _skinsProgress;
    bool _skinsLoading, _modBusy, _restoreArmed, _skinAdvanced;
    BhSkin _skin;
    List<SwfPart> _skinParts = new();
    List<(int Rgb, int Count)> _skinPalette = new();
    readonly HashSet<string> _openGroups = new();
    readonly Dictionary<string, BitmapSource> _bgThumbs = new();
    StackPanel _skinList;

    static BhMods Mods => S.Mods ??= new BhMods();

    static readonly (string Name, int Rgb)[] Presets =
    {
        ("Red", 0xFF3B3B), ("Orange", 0xFF8A1F), ("Gold", 0xFFC93C), ("Lime", 0x9BFF3B), ("Green", 0x22D36B), ("Cyan", 0x22E1FF),
        ("Blue", 0x3B7BFF), ("Purple", 0x9B5CFF), ("Pink", 0xFF5CC8), ("White", 0xF4F4F4), ("Black", 0x151515),
    };

    FrameworkElement ModeSwitch() => Segments(new[]
    {
        (Loc.T("Stats & tracker"), _mode == "stats", (Action)(() => { _mode = "stats"; Build(); })),
        (Loc.T("Customize the game"), _mode == "customize", (Action)(() => { _mode = "customize"; Build(); })),
    }).Also(s => s.Margin = new Thickness(0, 16, 0, 0));

    IEnumerable<FrameworkElement> ModsParts()
    {
        if (BrawlhallaMods.GameDir == null)
        {
            yield return Card(Section("Customize the game", Dim(Loc.T("Pulse couldn't find Brawlhalla on this PC. Customizing works with the Steam version; install it and open this page again."), 12.5, 0)));
            yield break;
        }
        if (!Mods.Acknowledged) { yield return Card(ModsNotice()); yield break; }
        yield return ApplyBar();
        yield return Segments(new[] { ("skins", "Skins"), ("colors", "Colors"), ("effects", "Effects"), ("maps", "Map themes"), ("smooth", "Smoother") }
            .Select(t => (Loc.T(t.Item2), _modTab == t.Item1, (Action)(() => { _modTab = t.Item1; _openColor = null; Build(); }))))
            .Also(s => s.Margin = new Thickness(0, 16, 0, 0));
        switch (_modTab)
        {
            case "skins":
                yield return SkinSteps();
                yield return ModInstaller();
                yield return SkinBrowser(SkinPictures);
                break;
            case "colors": yield return SkinBrowser(SkinColors); break;
            case "effects": yield return Card(EffectsPanel()); break;
            case "maps": foreach (var p in MapsPanel()) yield return p; break;
            default: yield return Card(SmoothPanel()); break;
        }
    }

    // ───────── notice ─────────

    FrameworkElement ModsNotice()
    {
        var sp = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(Heading("Before you customize"));
        sp.Children.Add(Dim(Loc.T("Pulse can put your own pictures on skins, change their colors, recolor effects and theme the maps. Please read this first:"), 13, 8));
        foreach (var (title, text, warn) in new[]
        {
            ("Only you see it", "Pulse changes Brawlhalla's files on this PC. Other players still see your normal skin, colors and maps.", false),
            ("It's against Brawlhalla's rules", "Blue Mammoth's terms don't allow changing the game's files. Mods like these are widespread and Pulse never touches the game while it runs, but your account is your own risk.", true),
            ("Recommended: only change skins you own", "That's the rule the modding community follows.", false),
            ("Everything can be undone", "Pulse keeps an untouched copy of each file before it changes it. Restore originals puts them all back, and so does Steam's Verify integrity of game files.", false),
            ("Updates undo it, Pulse redoes it", "When Brawlhalla updates, its files go back to normal. Pulse notices and applies your changes again.", false),
        })
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = warn ? Res("WarnBrush") : new SolidColorBrush(Gold), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 6, 0, 0) });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = warn ? Res("WarnBrush") : Res("TextBrush") });
            t.Children.Add(Dim(Loc.T(text), 12.5, 2));
            Grid.SetColumn(t, 1); row.Children.Add(t);
            sp.Children.Add(row);
        }
        var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("I understand, let me customize"), Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += (_, _) => { Mods.Acknowledged = true; TweakEngine.Save(); Build(); };
        sp.Children.Add(go);
        return sp;
    }

    // ───────── apply ─────────

    FrameworkElement ApplyBar()
    {
        bool running = BrawlhallaMods.GameRunning();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string state; Brush color = Res("TextBrush");
        if (_modBusy) state = Loc.T("Putting your changes into Brawlhalla…");
        else if (_modStatus != null) { state = _modStatus; }
        else if (BrawlhallaMods.NeedsReapply()) { state = Loc.T("A Brawlhalla update put the original files back. Apply to get your changes again."); color = Res("WarnBrush"); }
        else if (Mods.Pending) { state = Loc.T("You have changes that aren't in the game yet."); color = new SolidColorBrush(Gold); }
        else if (Mods.Any && Mods.Written.Count > 0) state = Loc.T("Your changes are in the game.");
        else state = Loc.T("Nothing changed yet. Pick something below, then Apply.");
        left.Children.Add(new TextBlock { Text = state, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = color, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(Dim(running ? Loc.T("Brawlhalla is open: close it to apply.") : Loc.T("Changes Brawlhalla's own files on this PC. Only you see them, at your own risk. Recommended: only change skins you own."), 12, 3)
            .Also(d => { if (running) d.Foreground = Res("WarnBrush"); }));
        grid.Children.Add(left);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var apply = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Apply to Brawlhalla"), Padding = new Thickness(18, 8, 18, 8), IsEnabled = !_modBusy && !running };
        apply.Click += async (_, _) => await RunMods(BrawlhallaMods.ApplyAll, Loc.T("Done. Open Brawlhalla to see your changes."));
        btns.Children.Add(apply);
        var restore = Ghost(_restoreArmed ? Loc.T("Click again to restore everything") : Loc.T("Restore originals"), async () =>
        {
            if (!_restoreArmed) { _restoreArmed = true; Build(); return; }
            _restoreArmed = false;
            await RunMods(BrawlhallaMods.RestoreAll, Loc.T("Brawlhalla's original files are back."));
        });
        restore.Margin = new Thickness(10, 0, 0, 0);
        restore.IsEnabled = !_modBusy && !running && (Mods.Any || Mods.Written.Count > 0);
        btns.Children.Add(restore);
        Grid.SetColumn(btns, 1); grid.Children.Add(btns);
        return Card(grid);
    }

    async Task RunMods(Func<string> work, string done)
    {
        _modBusy = true; _modStatus = null; Build();
        string error;
        try { error = await StaTask(work); }
        catch (Exception ex) { error = ex.Message; }
        _modBusy = false;
        _modStatus = error ?? done;
        MainWindow.Instance.Toast(error ?? done, error == null ? ToastKind.Success : ToastKind.Info);
        Build();
    }

    /// <summary>Runs work on its own thread with an STA apartment (WPF's picture code wants one), off the UI thread.</summary>
    static Task<string> StaTask(Func<string> work)
    {
        var tcs = new TaskCompletionSource<string>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    void Changed() { Mods.Pending = true; _modStatus = null; TweakEngine.Save(); }

    // ───────── skins: how it works, and installing downloaded mods ─────────

    FrameworkElement SkinSteps()
    {
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (n, title, text) in new[]
        {
            ("1", "Pick a skin", "Search on the left: a skin's name or its legend."),
            ("2", "Edit its PNGs", "Get the skin as PNGs, paint over them in any editor, then load your edits."),
            ("3", "Apply", "Close Brawlhalla, press Apply at the top, then play."),
        })
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Gold), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = n, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var t = new StackPanel { Margin = new Thickness(10, 0, 12, 0) };
            t.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            t.Children.Add(Dim(Loc.T(text), 12, 2));
            Grid.SetColumn(t, 1); row.Children.Add(t);
            grid.Children.Add(row);
        }
        return Card(grid);
    }

    /// <summary>Drop a downloaded mod (GameBanana and the like) and Pulse installs it, with the original kept.</summary>
    FrameworkElement ModInstaller()
    {
        var sp = new StackPanel();
        var drop = new Border { Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Inset), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        var dg = new Grid();
        dg.ColumnDefinitions.Add(new ColumnDefinition());
        dg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        dt.Children.Add(new TextBlock { Text = Loc.T("Got a mod from GameBanana? Drop it here"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        dt.Children.Add(Dim(Loc.T("The .zip you downloaded, or the .swf, .jpg or .png files inside it. Pulse finds where each file goes, keeps the original, and puts it in when you Apply."), 12, 2));
        dg.Children.Add(dt);
        var pick = Ghost(Loc.T("Choose mod file"), () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Brawlhalla mods") + "|*.zip;*.swf;*.jpg;*.jpeg;*.png;*.rar;*.7z", Multiselect = true };
            if (dlg.ShowDialog() == true) InstallMods(dlg.FileNames);
        });
        pick.Margin = new Thickness(16, 0, 0, 0);
        pick.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pick, 1); dg.Children.Add(pick);
        drop.Child = dg;
        drop.AllowDrop = true;
        drop.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        drop.DragEnter += (_, _) => drop.BorderBrush = new SolidColorBrush(Gold);
        drop.DragLeave += (_, _) => drop.BorderBrush = new SolidColorBrush(Line);
        drop.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) { e.Handled = true; InstallMods(f); } };
        sp.Children.Add(drop);

        foreach (var m in Mods.Installed.AsEnumerable().Reverse())
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = m.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var names = m.Files.Keys.Select(System.IO.Path.GetFileName).ToList();
            t.Children.Add(Dim((names.Count == 1 ? Loc.T("Replaces 1 file") : Loc.F("Replaces {0} files", names.Count)) + ": " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? "…" : ""), 11.5, 2));
            row.Children.Add(t);
            var id = m.Id;
            var rm = Ghost(Loc.T("Remove"), () => { BrawlhallaMods.RemoveMod(id); _modStatus = null; Build(); });
            Grid.SetColumn(rm, 1); row.Children.Add(rm);
            sp.Children.Add(row);
        }
        return Card(sp).Also(c => c.Margin = new Thickness(0, 14, 0, 0));
    }

    void InstallMods(IEnumerable<string> files)
    {
        var done = new List<string>();
        foreach (var f in files)
        {
            try
            {
                var (mod, skipped) = BrawlhallaMods.InstallMod(f);
                done.Add(mod.Name);
                if (skipped.Count > 0) MainWindow.Instance.Toast(Loc.F("{0}: {1} file(s) didn't match anything in Brawlhalla and were left out.", mod.Name, skipped.Count), ToastKind.Info);
            }
            catch (InvalidDataException ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Info); }
            catch (Exception ex) { MainWindow.Instance.Toast(Loc.F("Couldn't install {0}: {1}", System.IO.Path.GetFileName(f), ex.Message), ToastKind.Info); }
        }
        if (done.Count > 0)
        {
            _modStatus = null;
            MainWindow.Instance.Toast(Loc.F("Installed {0}. Press Apply to put it in the game.", string.Join(", ", done)), ToastKind.Success);
        }
        Build();
    }

    // ───────── skins: the browser both Skins and Colors use ─────────

    FrameworkElement SkinBrowser(Func<BhSkin, FrameworkElement> detail)
    {
        if (_skins == null)
        {
            if (!_skinsLoading) _ = LoadSkins();
            var sp = new StackPanel();
            sp.Children.Add(Heading("Reading Brawlhalla's skins…"));
            sp.Children.Add(Dim(Loc.T("The first time takes a few seconds. After that it's instant."), 12.5, 6));
            sp.Children.Add(new ProgressBar { Value = _skinsProgress * 100, Height = 4, Margin = new Thickness(0, 14, 0, 0), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left });
            return Card(sp);
        }
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel();
        var search = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Search skins or legends (Mikasa, Mordex…)"), Text = _skinSearch, FontSize = 13.5 };
        _skinList = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        search.TextChanged += (_, _) => { _skinSearch = search.Text; FillSkinList(); };
        left.Children.Add(search);
        left.Children.Add(new ScrollViewer { Content = _skinList, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        FillSkinList();
        var leftCard = Card(left);
        leftCard.Margin = new Thickness(0, 0, 14, 0);
        leftCard.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(leftCard);

        FrameworkElement right;
        if (_skin == null)
        {
            var sp = new StackPanel();
            sp.Children.Add(Heading(_modTab == "colors" ? "Pick a skin to recolor" : "Pick a skin"));
            sp.Children.Add(Dim(_modTab == "colors"
                ? Loc.T("Choose a skin on the left. You'll see every color it uses: click one to change it, or turn all of them at once.")
                : Loc.T("Choose a skin on the left, then a picture for any part: hair, eyes, torso, weapon. Pulse fits the picture into that part in every pose."), 12.5, 6));
            right = Card(sp);
        }
        else right = Card(detail(_skin));
        right.Margin = new Thickness(0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    async Task LoadSkins()
    {
        _skinsLoading = true;
        int shown = -1;
        var progress = new Progress<double>(p =>
        {
            _skinsProgress = p;
            // Redraw every 10%, not for every file.
            if ((int)(p * 10) != shown && _mode == "customize" && _skins == null) { shown = (int)(p * 10); Build(); }
        });
        try { _skins = await Task.Run(() => BrawlhallaMods.Skins(progress)); }
        catch { _skins = new(); }
        _skinsLoading = false;
        if (_mode == "customize") Build();
    }

    void FillSkinList()
    {
        if (_skinList == null || _skins == null) return;
        _skinList.Children.Clear();
        var q = _skinSearch.Trim();
        var list = (q.Length == 0 ? _skins : _skins.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Legend.Contains(q, StringComparison.OrdinalIgnoreCase) || s.File.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => IsChanged(s)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) { _skinList.Children.Add(Dim(Loc.T("No skin by that name. Skins use the game's own names: try part of it."), 12.5, 4)); return; }
        foreach (var s in list.Take(80))
        {
            bool on = _skin?.Key == s.Key;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = Pretty(s.Name), FontSize = 13, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            t.Children.Add(new TextBlock { Text = (s.Legend.Length > 0 ? s.Legend + " · " : "") + Loc.F("{0} parts", s.Parts.Count), FontSize = 11, Foreground = Res("TextDimBrush") });
            row.Children.Add(t);
            if (IsChanged(s)) row.Children.Add(new TextBlock { Text = Loc.T("Changed"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Gold), VerticalAlignment = VerticalAlignment.Center }.Also(x => Grid.SetColumn(x, 1)));
            var b = new Border { Child = row, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = on ? new SolidColorBrush(Inset) : Brushes.Transparent, BorderBrush = on ? new SolidColorBrush(Gold) : Brushes.Transparent, BorderThickness = new Thickness(1) };
            if (!on) { b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Inset); b.MouseLeave += (_, _) => b.Background = Brushes.Transparent; }
            var pick = s;
            b.MouseLeftButtonUp += async (_, _) => await OpenSkin(pick);
            _skinList.Children.Add(b);
        }
        if (list.Count > 80) _skinList.Children.Add(Dim(Loc.F("{0} more: search to narrow it down.", list.Count - 80), 11.5, 8));
    }

    static string Pretty(string name) => System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z0-9])", "$1 $2");

    bool IsChanged(BhSkin s) =>
        (Mods.Pictures.TryGetValue(s.File, out var p) && s.Parts.Any(p.ContainsKey)) || (Mods.Colors.TryGetValue(s.Key, out var c) && !c.IsOriginal);

    async Task OpenSkin(BhSkin s)
    {
        _skin = s; _openGroups.Clear(); _openColor = null;
        var parts = await Task.Run(() => BrawlhallaMods.PartsOf(s.File));
        var mine = s.Parts.ToHashSet();
        _skinParts = parts.Where(p => mine.Contains(p.Name)).ToList();
        _skinPalette = await Task.Run(() => BrawlhallaMods.PaletteOf(s.File, s.Parts));
        Build();
    }

    FrameworkElement SkinHeader(BhSkin s, string resetLabel, Action reset)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = Pretty(s.Name), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        t.Children.Add(Dim((s.Legend.Length > 0 ? s.Legend + " · " : "") + s.File, 12, 2));
        top.Children.Add(t);
        var r = Ghost(Loc.T(resetLabel), reset);
        r.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(r, 1); top.Children.Add(r);
        return top;
    }

    // ───────── skins: pictures ─────────

    FrameworkElement SkinPictures(BhSkin s)
    {
        var sp = new StackPanel();
        Mods.Pictures.TryGetValue(s.File, out var pics);
        pics ??= new();
        sp.Children.Add(SkinHeader(s, "Put this skin back", () =>
        {
            if (Mods.Pictures.TryGetValue(s.File, out var p)) foreach (var part in s.Parts) p.Remove(part);
            Changed(); Build();
        }));
        sp.Children.Add(PngSteps(s));
        sp.Children.Add(Switch("Or pick pictures part by part (advanced)", _skinAdvanced, v => { _skinAdvanced = v; Build(); }).Also(x => x.Margin = new Thickness(0, 18, 0, 4)));
        if (!_skinAdvanced) return sp;
        sp.Children.Add(BulkBox(s));
        sp.Children.Add(Dim(Loc.T("Or one at a time: click Choose picture next to a part, or drag a picture onto it. A PNG with a transparent background looks best."), 12.5, 14));
        if (_skinAdvanced)
        {
            sp.Children.Add(Dim(Loc.T("Make pictures about 4 times the size shown so they stay sharp. Pieces lets you give one piece (say, the back hair) its own picture."), 12, 4));
            sp.Children.Add(Switch("Stretch pictures to fill each part exactly", Mods.Stretch, v => { Mods.Stretch = v; Changed(); Build(); }).Also(x => x.Margin = new Thickness(0, 8, 0, 4)));
        }
        if (_skinParts.Count == 0) { sp.Children.Add(Dim(Loc.T("Reading this skin…"), 12.5, 12)); return sp; }

        foreach (var kind in new[] { "Head", "Body", "Weapons" })
        {
            var groups = _skinParts.GroupBy(p => BrawlhallaMods.PartGroup(p.Name)).Where(g => BrawlhallaMods.GroupKind(g.Key) == kind).OrderBy(g => g.Key).ToList();
            if (groups.Count == 0) continue;
            sp.Children.Add(Caps(kind, 11, Res("TextFaintBrush")).Also(c => c.Margin = new Thickness(0, 18, 0, 6)));
            foreach (var g in groups) sp.Children.Add(PartGroupRow(s, g.Key, g.ToList(), pics));
        }
        return sp;
    }

    /// <summary>The easy way: the skin's own parts as PNGs to paint over, then back in with one click.</summary>
    FrameworkElement PngSteps(BhSkin s)
    {
        var folder = BrawlhallaMods.SkinFolder(s);
        bool have = File.Exists(System.IO.Path.Combine(folder, "pulse-skin.json"));
        var box = new StackPanel();
        box.Children.Add(new TextBlock { Text = Loc.T("Edit this skin as PNGs"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        box.Children.Add(Dim(Loc.T("Pulse saves each part of the skin (hair, body, weapons) as a PNG from your own game files. Paint over them in any editor, save, and load them back. Parts you don't touch stay as they are."), 12.5, 4));

        FrameworkElement Step(string n, string text, Button b)
        {
            var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Gold), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = n, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var t = new TextBlock { Text = Loc.T(text), FontSize = 13, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0) };
            Grid.SetColumn(t, 1); g.Children.Add(t);
            if (b != null) { b.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(b, 2); g.Children.Add(b); }
            return g;
        }

        var get = new Button { Style = (Style)FindResource(have ? "GhostButton" : "PrimaryButton"), Content = have ? Loc.T("Open the PNGs") : Loc.T("Get the PNGs"), Padding = new Thickness(16, 7, 16, 7), IsEnabled = !_modBusy };
        get.Click += async (_, _) =>
        {
            if (!have)
            {
                _modBusy = true; Build();
                string err = null;
                try { await StaTask(() => { BrawlhallaMods.ExportSkin(s); return null; }); }
                catch (Exception ex) { err = ex is InvalidDataException ? ex.Message : Loc.F("Couldn't save the PNGs: {0}", ex.Message); }
                _modBusy = false; Build();
                if (err != null) { MainWindow.Instance.Toast(err, ToastKind.Info); return; }
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); } catch { }
        };
        box.Children.Add(Step("1", have ? "Your PNGs are in Documents › Pulse › Brawlhalla skins." : "Save this skin's parts as PNGs. Pulse opens the folder.", get));
        box.Children.Add(Step("2", "Paint over them in Paint, Photoshop, Krita or anything else. Keep the file names, and save as PNG.", null));
        var load = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Load my edits"), Padding = new Thickness(16, 7, 16, 7), IsEnabled = have && !_modBusy };
        load.Click += (_, _) =>
        {
            try
            {
                int n = BrawlhallaMods.ImportSkin(s);
                _modStatus = null;
                MainWindow.Instance.Toast(n == 0 ? Loc.T("No edited PNGs yet: save your changes in the editor, then try again.")
                    : Loc.F("Loaded {0} edited part(s). Press Apply to put them in the game.", n), n == 0 ? ToastKind.Info : ToastKind.Success);
            }
            catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Info); }
            Build();
        };
        box.Children.Add(Step("3", "Load your edits, then press Apply at the top.", load));
        if (have)
        {
            var redo = Ghost(Loc.T("Start over with fresh PNGs"), async () =>
            {
                _modBusy = true; Build();
                try { await StaTask(() => { BrawlhallaMods.ExportSkin(s); return null; }); }
                catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Info); }
                _modBusy = false; Build();
            });
            redo.HorizontalAlignment = HorizontalAlignment.Left;
            redo.Margin = new Thickness(0, 12, 0, 0);
            box.Children.Add(redo);
        }
        return new Border { Child = box, Padding = new Thickness(16), Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Inset), BorderBrush = new SolidColorBrush(Gold), BorderThickness = new Thickness(1) };
    }

    /// <summary>Many pictures at once, each named after the part it's for.</summary>
    FrameworkElement BulkBox(BhSkin s)
    {
        var box = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = Loc.T("Add many pictures at once"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        t.Children.Add(Dim(Loc.T("Name each picture after the part it's for, then pick them all (or drop them, or a folder, here). Each goes on every piece of that part."), 12, 2));
        top.Children.Add(t);
        var pick = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Add pictures"), Padding = new Thickness(16, 7, 16, 7), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif", Multiselect = true };
            if (dlg.ShowDialog() == true) Bulk(s, dlg.FileNames);
        };
        Grid.SetColumn(pick, 1); top.Children.Add(pick);
        box.Children.Add(top);

        // The names this skin understands, so nobody has to guess.
        var names = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        names.Children.Add(new TextBlock { Text = Loc.T("Names this skin uses:"), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        foreach (var g in _skinParts.Select(p => BrawlhallaMods.PartGroup(p.Name)).Distinct().OrderBy(g => Array.IndexOf(new[] { "Head", "Body", "Weapons" }, BrawlhallaMods.GroupKind(g))).ThenBy(g => g))
            names.Children.Add(new Border { Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(999), Background = new SolidColorBrush(PanelBg), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = g.Replace(" ", "") + ".png", FontSize = 11.5, Foreground = Res("TextBrush") } });
        box.Children.Add(names);

        var border = new Border { Child = box, Padding = new Thickness(14), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Inset), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1) };
        border.AllowDrop = true;
        border.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        border.DragEnter += (_, _) => border.BorderBrush = new SolidColorBrush(Gold);
        border.DragLeave += (_, _) => border.BorderBrush = new SolidColorBrush(Line);
        border.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) { e.Handled = true; Bulk(s, f); } };
        return border;
    }

    void Bulk(BhSkin s, string[] files)
    {
        try
        {
            var (pics, parts, noMatch, other) = BrawlhallaMods.BulkAdd(s, files);
            _modStatus = null;
            if (pics > 0) MainWindow.Instance.Toast(Loc.F("Put {0} picture(s) on {1} piece(s). Press Apply to put them in the game.", pics, parts), ToastKind.Success);
            if (noMatch.Count > 0) MainWindow.Instance.Toast(Loc.F("{0} didn't match a part of this skin. Rename them to one of the names shown.", string.Join(", ", noMatch.Take(4)) + (noMatch.Count > 4 ? "…" : "")), ToastKind.Info);
            if (other.Count > 0) MainWindow.Instance.Toast(Loc.T("Some pictures are another skin's own art, saved by Pulse, so they weren't used. Edit them into your own design first."), ToastKind.Info);
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Info); }
        Build();
    }

    FrameworkElement PartGroupRow(BhSkin s, string group, List<SwfPart> parts, Dictionary<string, string> pics)
    {
        var used = parts.Select(p => pics.GetValueOrDefault(p.Name)).ToList();
        var distinct = used.Where(u => u != null).Distinct().ToList();
        bool all = used.All(u => u != null);
        string shown = distinct.Count == 1 ? distinct[0] : null;

        void Set(IEnumerable<SwfPart> which, string pic)
        {
            if (!Mods.Pictures.TryGetValue(s.File, out var map)) Mods.Pictures[s.File] = map = new();
            foreach (var p in which) { if (pic == null) map.Remove(p.Name); else map[p.Name] = pic; }
            if (map.Count == 0) Mods.Pictures.Remove(s.File);
            Changed(); Build();
        }
        void Choose(IEnumerable<SwfPart> which)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif" };
            if (dlg.ShowDialog() != true) return;
            Use(which, dlg.FileName);
        }
        void Use(IEnumerable<SwfPart> which, string file)
        {
            try { Set(which, BrawlhallaMods.AddPicture(file)); }
            catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Info); }
        }

        var box = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Thumb(shown, distinct.Count > 1, 52));
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        t.Children.Add(new TextBlock { Text = group, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        double w = parts.Max(p => p.Width), h = parts.Max(p => p.Height);
        string state = distinct.Count == 0 ? Loc.T("Original") : all && distinct.Count == 1 ? Loc.T("Your picture") : Loc.T("Partly yours");
        t.Children.Add(Dim(_skinAdvanced ? (parts.Count == 1 ? Loc.T("1 piece") : Loc.F("{0} pieces", parts.Count)) + " · " + Loc.F("about {0} × {1}", (int)Math.Round(w), (int)Math.Round(h)) + " · " + state : state, 11.5, 2));
        Grid.SetColumn(t, 1); row.Children.Add(t);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(Ghost(distinct.Count > 0 ? Loc.T("Change picture") : Loc.T("Choose picture"), () => Choose(parts)));
        if (distinct.Count > 0) btns.Children.Add(Ghost(Loc.T("Original"), () => Set(parts, null)));
        if (parts.Count > 1 && _skinAdvanced)
        {
            bool open = _openGroups.Contains(s.Key + group);
            btns.Children.Add(Ghost(open ? Loc.T("Hide pieces") : Loc.T("Pieces"), () => { if (!_openGroups.Remove(s.Key + group)) _openGroups.Add(s.Key + group); Build(); }));
        }
        Grid.SetColumn(btns, 2); row.Children.Add(btns);
        box.Children.Add(row);

        if (parts.Count > 1 && _skinAdvanced && _openGroups.Contains(s.Key + group))
        {
            var list = new StackPanel { Margin = new Thickness(64, 8, 0, 0) };
            foreach (var p in parts.OrderBy(p => p.Name))
            {
                var pr = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                pr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pr.ColumnDefinitions.Add(new ColumnDefinition());
                pr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var mine = pics.GetValueOrDefault(p.Name);
                pr.Children.Add(Thumb(mine, false, 30));
                var label = new TextBlock { Text = BrawlhallaMods.PartLabel(p.Name) + "  ·  " + Loc.F("about {0} × {1}", (int)Math.Round(p.Width), (int)Math.Round(p.Height)), FontSize = 12, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
                Grid.SetColumn(label, 1); pr.Children.Add(label);
                var pb = new StackPanel { Orientation = Orientation.Horizontal };
                var one = new[] { p };
                pb.Children.Add(Ghost(Loc.T("Choose"), () => Choose(one)));
                if (mine != null) pb.Children.Add(Ghost(Loc.T("Original"), () => Set(one, null)));
                Grid.SetColumn(pb, 2); pr.Children.Add(pb);
                AllowPictureDrop(pr, f => Use(one, f));
                list.Children.Add(pr);
            }
            box.Children.Add(list);
        }

        var border = new Border { Child = box, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Inset), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1) };
        AllowPictureDrop(border, f => Use(parts, f));
        return border;
    }

    /// <summary>Drop a picture file from Explorer onto a part.</summary>
    void AllowPictureDrop(FrameworkElement el, Action<string> use)
    {
        el.AllowDrop = true;
        el.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        el.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) { e.Handled = true; use(files[0]); }
        };
    }

    FrameworkElement Thumb(string pic, bool mixed, double size)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(PanelBg), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1), ClipToBounds = true };
        if (pic != null)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = (int)(size * 2); img.UriSource = new Uri(System.IO.Path.Combine(BrawlhallaMods.PictureDir, pic)); img.EndInit(); img.Freeze();
                b.Child = new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(3) };
                return b;
            }
            catch { }
        }
        b.Child = new TextBlock { Text = mixed ? Loc.T("Mixed") : Loc.T("Original"), FontSize = size > 40 ? 10.5 : 8.5, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return b;
    }

    // ───────── skins: colors ─────────

    FrameworkElement SkinColors(BhSkin s)
    {
        if (!Mods.Colors.TryGetValue(s.Key, out var c)) c = new BhColors();
        void Save() { if (c.IsOriginal) Mods.Colors.Remove(s.Key); else Mods.Colors[s.Key] = c; Changed(); }

        var sp = new StackPanel();
        sp.Children.Add(SkinHeader(s, "Original colors", () => { Mods.Colors.Remove(s.Key); _openColor = null; Changed(); Build(); }));
        sp.Children.Add(Dim(Loc.T("Click a color to change it, or turn every color at once with the sliders. Black outlines stay black."), 12.5, 12));
        sp.Children.Add(Dim(Loc.T("Some skins use stand-in colors (bright green, peach) that the game swaps for the color you pick in Brawlhalla. Change one of those and that part keeps your color whatever color you pick in game."), 11.5, 6));
        if (_skinPalette.Count == 0) { sp.Children.Add(Dim(Loc.T("Reading this skin's colors…"), 12.5, 12)); return sp; }

        var map = BrawlhallaMods.ColorMap(c);
        var swatches = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var live = new List<(Border After, int Rgb)>();
        foreach (var (rgb, _) in _skinPalette.Where(x => x.Rgb != 0).Take(32))
        {
            int from = rgb;
            var after = new Border { Background = new SolidColorBrush(Rgb(map(rgb))) };
            live.Add((after, rgb));
            var two = new Grid { Width = 46, Height = 46 };
            two.RowDefinitions.Add(new RowDefinition());
            two.RowDefinitions.Add(new RowDefinition());
            two.Children.Add(new Border { Background = new SolidColorBrush(Rgb(rgb)) });
            Grid.SetRow(after, 1); two.Children.Add(after);
            bool open = _openColor == BrawlhallaMods.ToHex(rgb);
            var sw = new Border { Child = two, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, BorderThickness = new Thickness(2), BorderBrush = open ? new SolidColorBrush(Gold) : new SolidColorBrush(Line), ToolTip = Loc.F("#{0}: top is the original, bottom is yours", BrawlhallaMods.ToHex(rgb)) };
            sw.MouseLeftButtonUp += (_, _) => { _openColor = open ? null : BrawlhallaMods.ToHex(from); Build(); };
            swatches.Children.Add(sw);
        }
        sp.Children.Add(swatches);

        if (_openColor != null)
        {
            var editor = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var key = _openColor;
            editor.Children.Add(new TextBlock { Text = Loc.F("Change #{0} to:", key), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            editor.Children.Add(ColorChoices(c.Map.GetValueOrDefault(key), hex =>
            {
                if (hex == null) c.Map.Remove(key); else c.Map[key] = hex;
                Save(); Build();
            }));
            sp.Children.Add(new Border { Child = editor, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Inset), Margin = new Thickness(0, 4, 0, 0) });
        }

        void Refresh()
        {
            var m2 = BrawlhallaMods.ColorMap(c);
            foreach (var (after, rgb) in live) after.Background = new SolidColorBrush(Rgb(m2(rgb)));
        }
        FrameworkElement SliderRow(string label, int value, int min, int max, Action<int> set)
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12.5, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            var num = new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var sl = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            sl.ValueChanged += (_, e) => { set((int)e.NewValue); num.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture); Refresh(); };
            sl.PreviewMouseUp += (_, _) => Save();
            sl.LostKeyboardFocus += (_, _) => Save();
            Grid.SetColumn(sl, 1); g.Children.Add(sl);
            Grid.SetColumn(num, 2); g.Children.Add(num);
            return g;
        }
        sp.Children.Add(Caps("Every other color", 11, Res("TextFaintBrush")).Also(x => x.Margin = new Thickness(0, 16, 0, 0)));
        sp.Children.Add(SliderRow("Hue", c.Hue, -180, 180, v => c.Hue = v));
        sp.Children.Add(SliderRow("Saturation", c.Saturation, -100, 100, v => c.Saturation = v));
        sp.Children.Add(SliderRow("Brightness", c.Brightness, -100, 100, v => c.Brightness = v));
        return sp;
    }

    /// <summary>Preset colors, your own hex, and "original".</summary>
    FrameworkElement ColorChoices(string current, Action<string> pick)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var orig = Ghost(Loc.T("Original"), () => pick(null));
        orig.Margin = new Thickness(0, 0, 8, 8);
        wrap.Children.Add(orig);
        foreach (var (name, rgb) in Presets)
        {
            bool on = current != null && BrawlhallaMods.TryHex(current, out var cur) && cur == rgb;
            var sw = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Rgb(rgb)), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = Loc.T(name), BorderThickness = new Thickness(on ? 3 : 1), BorderBrush = on ? Brushes.White : new SolidColorBrush(Line) };
            int v = rgb;
            sw.MouseLeftButtonUp += (_, _) => pick(BrawlhallaMods.ToHex(v));
            wrap.Children.Add(sw);
        }
        var hex = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 120, Tag = "#RRGGBB", Text = current != null ? "#" + current : "", Margin = new Thickness(4, 0, 6, 8), VerticalContentAlignment = VerticalAlignment.Center };
        void Use() { if (BrawlhallaMods.TryHex(hex.Text, out var v)) pick(BrawlhallaMods.ToHex(v)); else MainWindow.Instance.Toast(Loc.T("Type a color like #FF8800."), ToastKind.Info); }
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) Use(); };
        wrap.Children.Add(hex);
        var use = Ghost(Loc.T("Use"), Use);
        use.Margin = new Thickness(0, 0, 8, 8);
        wrap.Children.Add(use);
        return wrap;
    }

    static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ───────── effects ─────────

    FrameworkElement EffectsPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Effect colors"));
        sp.Children.Add(Dim(Loc.T("Give your effects your own color. Pulse tints each one and keeps its bright highlights, so it still glows."), 12.5, 6));
        var effects = BrawlhallaMods.EffectList();
        var allKeys = effects.Select(e => e.Key).ToList();
        // One-click looks: one color for every effect.
        sp.Children.Add(new TextBlock { Text = Loc.T("One color for everything"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 16, 0, 0) });
        var packs = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var pack in BrawlhallaMods.Packs)
        {
            bool on = allKeys.All(k => Mods.Effects.GetValueOrDefault(k) == BrawlhallaMods.ToHex(pack.Rgb));
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Rgb(pack.Rgb)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(new TextBlock { Text = Loc.T(pack.Name), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            var b = new Border { Child = chip, Padding = new Thickness(12, 7, 14, 7), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(999), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Inset), BorderThickness = new Thickness(on ? 2 : 1), BorderBrush = on ? new SolidColorBrush(Gold) : new SolidColorBrush(Line) };
            var p = pack;
            b.MouseLeftButtonUp += (_, _) => { foreach (var k in allKeys) Mods.Effects[k] = BrawlhallaMods.ToHex(p.Rgb); Changed(); Build(); };
            packs.Children.Add(b);
        }
        var none = Ghost(Loc.T("All original"), () => { Mods.Effects.Clear(); Changed(); Build(); });
        none.Margin = new Thickness(0, 0, 8, 8);
        packs.Children.Add(none);
        sp.Children.Add(packs);

        // Where each effect is, so nothing has to be hunted for.
        var guide = new StackPanel();
        guide.Children.Add(new TextBlock { Text = Loc.T("Where's the effect I want?"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        foreach (var (what, where) in new[]
        {
            ("Dash, dodge, gravity cancel, jump", "Movement"),
            ("The flash when a hit lands, KO bursts, bombs", "Hits and KOs"),
            ("Slashes and sparks of a weapon's attacks and signatures", "Weapons: pick that weapon"),
            ("Effects of a legend or crossover skin (its own trails, signature effects)", "Legends and crossovers"),
            ("A skin's own colors (hair, outfit, weapon art)", "the Colors tab"),
            ("The plain white swing trail on light attacks", "can't change: the game draws it in code"),
        })
        {
            var r = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.Children.Add(new TextBlock { Text = Loc.T(what), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
            var wt = new TextBlock { Text = "→ " + Loc.T(where), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Gold), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(wt, 1); r.Children.Add(wt);
            guide.Children.Add(r);
        }
        sp.Children.Add(new Border { Child = guide, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Inset), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(1) });

        string lastGroup = null;
        foreach (var e in effects.OrderBy(e => Array.IndexOf(new[] { "Movement", "Hits and KOs", "Weapons", "Legends and crossovers" }, BrawlhallaMods.EffectGroup(e.Key))))
        {
            var g = BrawlhallaMods.EffectGroup(e.Key);
            if (g != lastGroup)
            {
                lastGroup = g;
                sp.Children.Add(Caps(g, 11, Res("TextFaintBrush")).Also(c => c.Margin = new Thickness(0, 24, 0, 0)));
            }
            var cur = Mods.Effects.GetValueOrDefault(e.Key);
            var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (BrawlhallaMods.TryHex(cur, out var rgb))
                head.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Rgb(rgb)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = Loc.T(e.Name), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            row.Children.Add(head);
            row.Children.Add(Dim(Loc.T(e.Detail), 12, 2));
            var key = e.Key;
            row.Children.Add(ColorChoices(string.IsNullOrEmpty(cur) ? null : cur, hex =>
            {
                if (hex == null) Mods.Effects.Remove(key); else Mods.Effects[key] = hex;
                Changed(); Build();
            }));
            sp.Children.Add(row);
        }
        return sp;
    }

    // ───────── map themes ─────────

    IEnumerable<FrameworkElement> MapsPanel()
    {
        var files = BrawlhallaMods.BackgroundFiles();
        var top = new StackPanel();
        top.Children.Add(Heading("Map themes"));
        top.Children.Add(Dim(Loc.T("Pick a theme, then click the maps to use it on, or use it on every map. Darker, calmer backgrounds make fighters easier to follow."), 12.5, 6));
        var sample = files.FirstOrDefault(f => f.Contains("Grove", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
        var themes = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        themes.Children.Add(ThemeCard("", Loc.T("Original"), Loc.T("The game's own art."), sample));
        foreach (var t in BrawlhallaMods.Themes) themes.Children.Add(ThemeCard(t.Key, Loc.T(t.Name), Loc.T(t.Detail), sample));
        themes.Children.Add(ThemeCard(_bgTheme.StartsWith("pic:", StringComparison.Ordinal) ? _bgTheme : "pick", Loc.T("Your picture"), Loc.T("Any picture you like."), sample));
        top.Children.Add(themes);
        var acts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        acts.Children.Add(Ghost(Loc.F("Use \"{0}\" on every map", ThemeName(_bgTheme)), () =>
        {
            foreach (var f in files) { if (_bgTheme.Length == 0) Mods.Backgrounds.Remove(f); else Mods.Backgrounds[f] = _bgTheme; }
            Changed(); Build();
        }));
        if (Mods.Backgrounds.Count > 0) acts.Children.Add(Ghost(Loc.T("Every map back to original"), () => { Mods.Backgrounds.Clear(); Changed(); Build(); }));
        top.Children.Add(acts);
        yield return Card(top);

        var grid = new WrapPanel();
        foreach (var f in files)
        {
            var file = f;
            var theme = Mods.Backgrounds.GetValueOrDefault(f, "");
            var tile = new StackPanel { Width = 196 };
            tile.Children.Add(new Border { Height = 110, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = BgBrush(f, theme) });
            tile.Children.Add(new TextBlock { Text = BrawlhallaMods.BackgroundLabel(f), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(2, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            tile.Children.Add(new TextBlock { Text = theme.Length == 0 ? Loc.T("Original") : ThemeName(theme), FontSize = 11, Foreground = theme.Length == 0 ? Res("TextDimBrush") : new SolidColorBrush(Gold), Margin = new Thickness(2, 1, 0, 0) });
            bool match = theme == _bgTheme;
            var b = new Border { Child = tile, Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = new SolidColorBrush(Inset), BorderThickness = new Thickness(1), BorderBrush = theme.Length > 0 ? new SolidColorBrush(Gold) : new SolidColorBrush(Line),
                ToolTip = match && theme.Length > 0 ? Loc.T("Click to put this map back") : Loc.F("Click to use \"{0}\" here", ThemeName(_bgTheme)) };
            b.MouseLeftButtonUp += (_, _) =>
            {
                if (match || _bgTheme.Length == 0) Mods.Backgrounds.Remove(file); else Mods.Backgrounds[file] = _bgTheme;
                Changed(); Build();
            };
            grid.Children.Add(b);
        }
        yield return Card(grid);
    }

    string ThemeName(string key) => key.Length == 0 ? Loc.T("Original") : key.StartsWith("pic:", StringComparison.Ordinal) ? Loc.T("Your picture") : Loc.T(BrawlhallaMods.Themes.FirstOrDefault(t => t.Key == key)?.Name ?? key);

    FrameworkElement ThemeCard(string key, string name, string detail, string sample)
    {
        bool on = key == _bgTheme || (key.StartsWith("pic:", StringComparison.Ordinal) && _bgTheme == key);
        var sp = new StackPanel { Width = 150 };
        sp.Children.Add(new Border { Height = 84, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = key == "pick" ? new SolidColorBrush(PanelBg) : sample == null ? new SolidColorBrush(PanelBg) : BgBrush(sample, key),
            Child = key == "pick" ? new TextBlock { Text = "+", FontSize = 26, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null });
        sp.Children.Add(new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(2, 7, 0, 0) });
        sp.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1, 0, 0) });
        var b = new Border { Child = sp, Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = new SolidColorBrush(Inset), BorderThickness = new Thickness(on ? 2 : 1), BorderBrush = on ? new SolidColorBrush(Gold) : new SolidColorBrush(Line) };
        b.MouseLeftButtonUp += (_, _) =>
        {
            if (key == "pick" || key.StartsWith("pic:", StringComparison.Ordinal))
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
                if (dlg.ShowDialog() != true) return;
                try { _bgTheme = "pic:" + BrawlhallaMods.AddPicture(dlg.FileName); }
                catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, ToastKind.Info); return; }
            }
            else _bgTheme = key;
            Build();
        };
        return b;
    }

    /// <summary>A background thumbnail with a theme on it (themes are cheap on a thumbnail).</summary>
    Brush BgBrush(string file, string theme)
    {
        try
        {
            if (!_bgThumbs.TryGetValue(file, out var thumb))
            {
                var img = new BitmapImage();
                img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = 300; img.UriSource = new Uri(BrawlhallaMods.OriginalBackground(file)); img.EndInit(); img.Freeze();
                _bgThumbs[file] = thumb = img;
            }
            var key = file + "|" + theme;
            if (theme.Length > 0 && !_bgThumbs.TryGetValue(key, out var themed)) _bgThumbs[key] = themed = BrawlhallaMods.ApplyTheme(thumb, theme);
            var src = theme.Length > 0 ? _bgThumbs[key] : thumb;
            return new ImageBrush(src) { Stretch = Stretch.UniformToFill };
        }
        catch { return new SolidColorBrush(PanelBg); }
    }

    // ───────── smoother ─────────

    FrameworkElement SmoothPanel()
    {
        var sp = new StackPanel { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(Heading("Smoother Brawlhalla"));
        sp.Children.Add(Dim(Loc.T("Brawlhalla is light, so stutter usually comes from Windows or settings, not raw power. These are the changes that actually help."), 12.5, 6));

        void Toggle(string title, string detail, bool on, Action<bool> set)
        {
            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
            cb.Click += (_, _) =>
            {
                try { set(cb.IsChecked == true); MainWindow.Instance.Toast(Loc.T(cb.IsChecked == true ? "On. It takes effect the next time Brawlhalla starts." : "Off.")); }
                catch (Exception) { cb.IsChecked = !cb.IsChecked; MainWindow.Instance.Toast(Loc.T("Windows didn't allow that. Run Pulse as administrator and try again."), ToastKind.Info); }
            };
            row.Children.Add(cb);
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            t.Children.Add(Dim(Loc.T(detail), 12, 2));
            Grid.SetColumn(t, 1); row.Children.Add(t);
            sp.Children.Add(row);
        }
        Toggle("Use the faster graphics card", "Windows' own per-game setting (Settings → System → Display → Graphics). On laptops and PCs with two graphics chips, it stops Brawlhalla landing on the slower one.", BrawlhallaMods.FastGpuOn, BrawlhallaMods.SetFastGpu);
        Toggle("High CPU priority", "Windows runs Brawlhalla ahead of background apps every time it starts, so a browser or Discord can't steal its turn mid-fight.", BrawlhallaMods.HighPriorityOn, BrawlhallaMods.SetHighPriority);

        sp.Children.Add(new TextBlock { Text = Loc.T("Clearer fights"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 22, 0, 0) });
        sp.Children.Add(Dim(Loc.T("Dimmed, softened backgrounds on every map, so fighters, weapons and projectiles stand out. Apply to put it in the game."), 12, 2));
        sp.Children.Add(Ghost(Loc.T("Use Clean & dark on every map"), () =>
        {
            foreach (var f in BrawlhallaMods.BackgroundFiles()) Mods.Backgrounds[f] = "clean";
            Changed(); _modTab = "maps"; _bgTheme = "clean"; Build();
        }).Also(b => b.Margin = new Thickness(0, 8, 0, 0)));

        sp.Children.Add(new TextBlock { Text = Loc.T("In Brawlhalla's settings"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 22, 0, 0) });
        foreach (var (s, why) in new[]
        {
            ("Display Mode: Fullscreen", "The least input delay, and Windows can't throttle the game."),
            ("V-Sync: Off", "V-Sync adds delay. Use the FPS limit instead."),
            ("FPS Limit: your monitor's refresh rate (144, 240…)", "Matching your monitor gives the steadiest frame times."),
            ("Overall Graphics Quality: Low", "Fewer effects in busy fights, so frame times stay even."),
            ("Play on a wired connection", "Brawlhalla's rollback netcode turns unstable ping into stutter, even at high FPS."),
        })
        {
            var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(new TextBlock { Text = Loc.T(s), FontSize = 13, Foreground = new SolidColorBrush(Gold), FontWeight = FontWeights.SemiBold });
            row.Children.Add(Dim(Loc.T(why), 12, 1));
            sp.Children.Add(row);
        }
        return sp;
    }
}
