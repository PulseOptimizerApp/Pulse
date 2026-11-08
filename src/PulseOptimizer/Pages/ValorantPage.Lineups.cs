using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Games;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>Your own lineup library: filter by map, agent, site, side and difficulty; steps, a picture, a video link, favorites and ratings.</summary>
public partial class ValorantPage
{
    static string _luMap = "", _luAgent = "", _luSite = "", _luSide = "", _luText = "";
    static int _luDiff;
    static bool _luFav;
    static Lineup _luEdit;

    void BuildLineups()
    {
        if (_luEdit != null) { Root.Children.Add(LineupEditor(_luEdit)); return; }
        var head = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Heading("Lineups"));
        var add = Btn("Add a lineup", true);
        add.Margin = new Thickness(0);
        add.Click += (_, _) => { _luEdit = new Lineup { Map = _luMap, Agent = _luAgent, Site = _luSite.Length > 0 ? _luSite : "A", Side = _luSide.Length > 0 ? _luSide : "Attack" }; Build(); };
        Grid.SetColumn(add, 1); top.Children.Add(add);
        head.Children.Add(top);
        head.Children.Add(Dim(Loc.T("Save the lineups you use: where to stand, where to aim, and a picture or video. They stay on this PC."), 12.5, 4));

        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        ComboBox Pick(string any, IEnumerable<string> items, string sel, Action<string> set, Func<string, string> label = null)
        {
            var b = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 8, 8) };
            b.Items.Add(new ComboBoxItem { Content = Loc.T(any), Tag = "" });
            foreach (var i in items) b.Items.Add(new ComboBoxItem { Content = label?.Invoke(i) ?? i, Tag = i });
            b.SelectedIndex = Math.Max(0, b.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => (string)x.Tag == sel));
            b.SelectionChanged += (_, _) => { if (b.SelectedItem is ComboBoxItem ci) { set((string)ci.Tag); Build(); } };
            return b;
        }
        bar.Children.Add(Pick("All maps", Lineups.Maps, _luMap, v => _luMap = v));
        bar.Children.Add(Pick("All agents", Lineups.Agents.OrderBy(a => a), _luAgent, v => _luAgent = v));
        bar.Children.Add(Pick("All sites", Lineups.Sites, _luSite, v => _luSite = v, s => s == "Mid" ? Loc.T("Mid") : Loc.F("{0} site", s)));
        bar.Children.Add(Pick("Both sides", new[] { "Attack", "Defense" }, _luSide, v => _luSide = v, Loc.T));
        bar.Children.Add(Pick("Any difficulty", new[] { "1", "2", "3" }, _luDiff > 0 ? _luDiff.ToString(CultureInfo.InvariantCulture) : "", v => _luDiff = v.Length > 0 ? int.Parse(v, CultureInfo.InvariantCulture) : 0, DiffName));
        var fav = new CheckBox { Content = Loc.T("Favorites only"), IsChecked = _luFav, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 8), Foreground = Res("TextBrush") };
        fav.Click += (_, _) => { _luFav = fav.IsChecked == true; Build(); };
        bar.Children.Add(fav);
        head.Children.Add(bar);
        var search = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Search titles, abilities and steps"), Text = _luText, Margin = new Thickness(0, 4, 0, 0) };
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _luText = search.Text; Build(); } };
        search.LostFocus += (_, _) => { if (search.Text != _luText) { _luText = search.Text; Build(); } };
        head.Children.Add(search);
        Root.Children.Add(Card(head));

        var list = Lineups.Find(_luMap, _luAgent, _luSite, _luSide, _luDiff, _luFav, _luText);
        if (Lineups.All.Count == 0) { Root.Children.Add(Card(Dim(Loc.T("No lineups yet. Add one with a title, where it's for and the steps; a screenshot of where to aim helps the most.")))); return; }
        if (list.Count == 0) { Root.Children.Add(Card(Dim(Loc.T("No lineups match these filters.")))); return; }
        var wrap = new WrapPanel();
        foreach (var l in list) wrap.Children.Add(LineupCard(l));
        Root.Children.Add(wrap);
    }

    static string DiffName(string d) => d switch { "1" => Loc.T("Easy"), "2" => Loc.T("Medium"), "3" => Loc.T("Hard"), _ => d };

    static ImageSource LineupImage(string file, int width)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var path = Path.Combine(Lineups.ImageDir, file);
        if (!File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
            return b;
        }
        catch { return null; }
    }

    FrameworkElement LineupCard(Lineup l)
    {
        var sp = new StackPanel { Width = 330 };
        var pic = LineupImage(l.Image, 660) ?? MatchArt.MapPicture(l.Map);
        var img = new Border { Height = 170, CornerRadius = new CornerRadius(8, 8, 0, 0), ClipToBounds = true, Background = pic != null ? new ImageBrush(pic) { Stretch = Stretch.UniformToFill } : Res("Surface2Brush") };
        if (pic != null && l.Image.Length > 0)
        {
            img.Cursor = Cursors.Hand;
            img.ToolTip = Loc.T("Open the picture");
            img.MouseLeftButtonUp += (_, _) => { try { Process.Start(new ProcessStartInfo(Path.Combine(Lineups.ImageDir, l.Image)) { UseShellExecute = true }); } catch { } };
        }
        sp.Children.Add(img);
        var body = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = l.Title.Length > 0 ? l.Title : Loc.T("Untitled lineup"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var heart = new TextBlock { Text = l.Favorite ? "♥" : "♡", FontSize = 16, Cursor = Cursors.Hand, Foreground = l.Favorite ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x6E)) : Res("TextDimBrush"), ToolTip = Loc.T(l.Favorite ? "Remove from favorites" : "Add to favorites") };
        heart.MouseLeftButtonUp += (_, _) => { l.Favorite = !l.Favorite; Lineups.Save(l); Build(); };
        Grid.SetColumn(heart, 1); title.Children.Add(heart);
        body.Children.Add(title);
        var tags = string.Join(" · ", new[] { l.Agent, l.Ability, l.Map, l.Site == "Mid" ? Loc.T("Mid") : Loc.F("{0} site", l.Site), Loc.T(l.Side), DiffName(l.Difficulty.ToString(CultureInfo.InvariantCulture)) }.Where(x => !string.IsNullOrEmpty(x)));
        body.Children.Add(new TextBlock { Text = tags, FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < l.Steps.Count; i++)
            body.Children.Add(new TextBlock { Text = $"{i + 1}. {l.Steps[i]}", FontSize = 12.5, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 1; i <= 5; i++)
        {
            int v = i;
            var s = new TextBlock { Text = v <= l.Rating ? "★" : "☆", FontSize = 14, Padding = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                Foreground = v <= l.Rating ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B)) : Res("TextFaintBrush"), ToolTip = Loc.F("Rate {0} of 5", v) };
            s.MouseLeftButtonUp += (_, _) => { l.Rating = v == l.Rating ? 0 : v; Lineups.Save(l); Build(); };
            stars.Children.Add(s);
        }
        actions.Children.Add(stars);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        if (Lineups.SafeLink(l.Video))
        {
            var vid = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Video"), Margin = new Thickness(0, 0, 12, 0), ToolTip = l.Video };
            vid.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(l.Video.Trim()) { UseShellExecute = true }); } catch { } };
            btns.Children.Add(vid);
        }
        var edit = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Edit"), Margin = new Thickness(0, 0, 12, 0) };
        edit.Click += (_, _) => { _luEdit = Clone(l); Build(); };
        btns.Children.Add(edit);
        var del = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Delete") };
        bool armed = false;
        del.Click += (_, _) =>
        {
            if (!armed) { armed = true; del.Content = Loc.T("Click again to delete"); return; }
            Lineups.Delete(l.Id); Build();
        };
        btns.Children.Add(del);
        Grid.SetColumn(btns, 1); actions.Children.Add(btns);
        body.Children.Add(actions);
        sp.Children.Add(body);
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Margin = new Thickness(0, 0, 14, 14), Child = sp, VerticalAlignment = VerticalAlignment.Top };
    }

    static Lineup Clone(Lineup l) => System.Text.Json.JsonSerializer.Deserialize<Lineup>(System.Text.Json.JsonSerializer.Serialize(l));

    FrameworkElement LineupEditor(Lineup l)
    {
        bool isNew = !Lineups.All.Any(x => x.Id == l.Id);
        var sp = new StackPanel();
        sp.Children.Add(Heading(isNew ? "Add a lineup" : "Edit lineup"));
        var form = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void Row(string label, UIElement el)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = Loc.T(label), FontSize = 12.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
            Grid.SetRow(t, row); form.Children.Add(t);
            if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(el, row); Grid.SetColumn(el, 1); form.Children.Add(el);
            row++;
        }
        TextBox Box(string value, string hint, bool multi = false) => new()
        {
            Style = (Style)FindResource("SearchBox"), Text = value, Tag = Loc.T(hint),
            AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multi ? 110 : 0, VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        ComboBox Combo(IEnumerable<string> items, string sel, Func<string, string> label = null)
        {
            var b = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var i in items) b.Items.Add(new ComboBoxItem { Content = label?.Invoke(i) ?? i, Tag = i });
            b.SelectedIndex = Math.Max(0, b.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => (string)x.Tag == sel));
            return b;
        }
        string Sel(ComboBox b) => (b.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var title = Box(l.Title, "e.g. Recon dart for A main"); Row("Title", title);
        var map = Combo(Lineups.Maps, l.Map.Length > 0 ? l.Map : Lineups.Maps[0]); Row("Map", map);
        var agent = Combo(Lineups.Agents.OrderBy(a => a), l.Agent.Length > 0 ? l.Agent : "Sova"); Row("Agent", agent);
        var ability = Box(l.Ability, "e.g. Recon Bolt, Molly, Smoke"); Row("Ability", ability);
        var site = Combo(Lineups.Sites, l.Site, s => s == "Mid" ? Loc.T("Mid") : Loc.F("{0} site", s)); Row("Site", site);
        var side = Combo(new[] { "Attack", "Defense" }, l.Side, Loc.T); Row("Side", side);
        var diff = Combo(new[] { "1", "2", "3" }, l.Difficulty.ToString(CultureInfo.InvariantCulture), DiffName); Row("Difficulty", diff);
        var steps = Box(string.Join(Environment.NewLine, l.Steps), "One step per line: where to stand, where to aim, how to throw", true); Row("Steps", steps);
        var video = Box(l.Video, "https://… (optional)"); Row("Video link", video);
        var picRow = new StackPanel { Orientation = Orientation.Horizontal };
        var picName = new TextBlock { Text = l.Image.Length > 0 ? Loc.T("Picture added") : Loc.T("No picture"), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var choose = Btn("Choose a picture");
        choose.Margin = new Thickness(0);
        choose.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
            if (dlg.ShowDialog() != true) return;
            var name = Lineups.AddImage(dlg.FileName);
            if (name.Length == 0) { MainWindow.Instance.Toast(Loc.T("That picture couldn't be added (PNG, JPG, WebP or BMP under 15 MB)."), ToastKind.Info); return; }
            l.Image = name; picName.Text = Loc.T("Picture added");
        };
        picRow.Children.Add(choose);
        picRow.Children.Add(picName);
        Row("Picture", picRow);
        sp.Children.Add(form);

        var err = new TextBlock { FontSize = 12, Foreground = Res("WarnBrush"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(err);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var save = Btn("Save lineup", true);
        save.Click += (_, _) =>
        {
            var lines = steps.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (title.Text.Trim().Length == 0) { err.Text = Loc.T("Give it a title."); err.Visibility = Visibility.Visible; return; }
            if (video.Text.Trim().Length > 0 && !Lineups.SafeLink(video.Text)) { err.Text = Loc.T("The video link needs to start with https://"); err.Visibility = Visibility.Visible; return; }
            l.Title = title.Text.Trim(); l.Map = Sel(map); l.Agent = Sel(agent); l.Ability = ability.Text.Trim(); l.Site = Sel(site); l.Side = Sel(side);
            l.Difficulty = int.TryParse(Sel(diff), out var d) ? d : 1; l.Steps = lines; l.Video = video.Text.Trim();
            Lineups.Save(l);
            _luEdit = null; Build();
        };
        btns.Children.Add(save);
        var cancel = Btn("Cancel");
        cancel.Click += (_, _) => { _luEdit = null; Build(); };
        btns.Children.Add(cancel);
        sp.Children.Add(btns);
        return Card(sp);
    }
}
