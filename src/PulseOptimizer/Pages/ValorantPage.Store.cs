using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core;
using Pulse.Core.Games.Live;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>Your VALORANT store without opening the game: today's skins, the Night Market, the bundle and your wallet.</summary>
public partial class ValorantPage
{
    static Storefront _store;
    static DateTime _storeAt;
    static bool _storeLoading;
    static string _storeError;

    void BuildStore()
    {
        var head = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Heading("Your store"));
        left.Children.Add(Dim(Loc.T("Today's skins, the Night Market when it's on, and the featured bundle, read from your own account through the Riot Client. Nothing is bought here."), 12.5, 4));
        top.Children.Add(left);
        var refresh = Btn(_storeLoading ? "Loading…" : _store == null ? "Show my store" : "Refresh", _store == null);
        refresh.IsEnabled = !_storeLoading;
        refresh.VerticalAlignment = VerticalAlignment.Top;
        refresh.Margin = new Thickness(16, 0, 0, 0);
        refresh.Click += async (_, _) => await LoadStore();
        Grid.SetColumn(refresh, 1);
        top.Children.Add(refresh);
        head.Children.Add(top);
        if (_store != null)
        {
            var wallet = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            void Coin(string label, int n)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 0) };
                sp.Children.Add(new TextBlock { Text = n.ToString("N0", CultureInfo.CurrentCulture), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
                sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11.5, Foreground = Res("TextFaintBrush") });
                wallet.Children.Add(sp);
            }
            Coin("VALORANT Points", _store.Vp);
            Coin("Radianite Points", _store.Radianite);
            Coin("Kingdom Credits", _store.KingdomCredits);
            head.Children.Add(wallet);
        }
        if (_storeError != null) head.Children.Add(Dim(_storeError, 12.5, 10).Also(t => t.Foreground = Res("WarnBrush")));
        var risk = new StackPanel();
        risk.Children.Add(Faint(Loc.T("Reads the same store data as the game's own store screen, using the Riot Client's login on this PC (like the match recaps). It isn't an official Riot app feature, so it carries the same small risk as other store-checker apps. Skin names and pictures come from valorant-api.com; only the skin's id is sent."), 11.5, 10));
        head.Children.Add(risk);
        Root.Children.Add(Card(head));

        Root.Children.Add(Card(WishlistPanel()));

        if (_store == null)
        {
            if (!_storeLoading && _storeError == null) _ = LoadStore();
            return;
        }

        var dailyLeft = _store.DailyLeft - (DateTime.UtcNow - _storeAt);
        Root.Children.Add(Section("Today's offers", dailyLeft, _store.Daily));
        if (_store.Night.Count > 0)
            Root.Children.Add(Section("Night Market", _store.NightLeft - (DateTime.UtcNow - _storeAt), _store.Night));
        if (_store.Bundle is { } b)
            Root.Children.Add(Section("Featured bundle", _store.BundleLeft - (DateTime.UtcNow - _storeAt), new List<StoreItem> { b }, wide: true));
    }

    async Task LoadStore()
    {
        if (_storeLoading) return;
        _storeLoading = true;
        _storeError = null;
        if (_tab == "store") Build();
        try
        {
            var s = await ValorantStore.Load();
            if (s == null) _storeError = Loc.T("Couldn't read your store. Open VALORANT or the Riot Client and try again.");
            else { _store = s; _storeAt = DateTime.UtcNow; }
        }
        catch { _storeError = Loc.T("Couldn't read your store. Open VALORANT or the Riot Client and try again."); }
        _storeLoading = false;
        if (_tab == "store") Build();
    }

    static string Left(TimeSpan t) => t <= TimeSpan.Zero ? Loc.T("refreshing now")
        : t.TotalDays >= 1 ? Loc.F("{0}d {1}h left", (int)t.TotalDays, t.Hours) : Loc.F("{0}h {1}m left", (int)t.TotalHours, t.Minutes);

    /// <summary>The colors VALORANT uses for each skin tier.</summary>
    static Color TierColor(string tier) => tier switch
    {
        "Select" => Color.FromRgb(0x5A, 0x9F, 0xE2),
        "Deluxe" => Color.FromRgb(0x00, 0x95, 0x87),
        "Premium" => Color.FromRgb(0xD1, 0x54, 0x8D),
        "Ultra" => Color.FromRgb(0xFA, 0xD6, 0x63),
        "Exclusive" => Color.FromRgb(0xF5, 0x95, 0x5B),
        _ => Color.FromRgb(0x8A, 0x8F, 0x98),
    };

    FrameworkElement Section(string title, TimeSpan left, List<StoreItem> items, bool wide = false)
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Heading(title));
        var t = new TextBlock { Text = Left(left), FontSize = 12, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 1);
        head.Children.Add(t);
        sp.Children.Add(head);
        var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var i in items) wrap.Children.Add(ItemCard(i, wide));
        sp.Children.Add(wrap);
        return Card(sp);
    }

    // ───────── wishlist ─────────

    static List<WishSkin> _allSkins;
    static bool _skinsLoading;
    static string _wishSearch = "";
    StackPanel _wishResults;

    bool Wished(StoreItem i) => S.Wishlist.Any(w => w.Ids.Contains(i.Id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Skins you want: Pulse checks your store after each refresh and tells you when one is in it.</summary>
    FrameworkElement WishlistPanel()
    {
        var sp = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Heading("Wishlist"));
        left.Children.Add(Dim(Loc.T("Add skins you want. After each store refresh, while the Riot Client is open, Pulse checks your store and Night Market and tells you when one is there."), 12.5, 4));
        top.Children.Add(left);
        var alerts = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = S.WishlistAlerts, Content = Loc.T("Notify me"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 2, 0, 0), Foreground = Res("TextBrush") };
        alerts.Click += (_, _) => { S.WishlistAlerts = alerts.IsChecked == true; TweakEngine.Save(); };
        Grid.SetColumn(alerts, 1); top.Children.Add(alerts);
        sp.Children.Add(top);

        if (S.Wishlist.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var w in S.Wishlist)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var pic = new Border { Width = 64, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
                row.Children.Add(pic);
                _ = SetPicture(pic, w);
                bool inStore = _store != null && ValorantStore.WishMatches(_store, new[] { w }).Count > 0;
                row.Children.Add(new TextBlock { Text = w.Name, FontSize = 12.5, FontWeight = inStore ? FontWeights.SemiBold : FontWeights.Normal, Foreground = inStore ? Res("GoodBrush") : Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
                if (inStore) row.Children.Add(new TextBlock { Text = "  " + Loc.T("in your store now"), FontSize = 11, Foreground = Res("GoodBrush"), VerticalAlignment = VerticalAlignment.Center });
                var x = new TextBlock { Text = "  ✕", FontSize = 12, Foreground = Res("TextDimBrush"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Remove from wishlist") };
                var me = w;
                x.MouseLeftButtonUp += (_, _) => { S.Wishlist.Remove(me); TweakEngine.Save(); Build(); };
                row.Children.Add(x);
                chips.Children.Add(new Border { Child = row, Padding = new Thickness(8, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), Background = Res("Surface2Brush"), BorderThickness = new Thickness(1),
                    BorderBrush = inStore ? Res("GoodBrush") : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) });
            }
            sp.Children.Add(chips);
        }

        var search = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Add a skin: search every skin (Reaver, Prime Vandal…)"), Text = _wishSearch, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        _wishResults = new StackPanel { Margin = new Thickness(0, 6, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        search.TextChanged += (_, _) => { _wishSearch = search.Text; FillWishResults(); };
        sp.Children.Add(search);
        sp.Children.Add(_wishResults);
        if (_allSkins == null && !_skinsLoading) _ = LoadAllSkins();
        FillWishResults();
        return sp;
    }

    async Task LoadAllSkins()
    {
        _skinsLoading = true;
        try { _allSkins = await ValorantStore.Skins(); } catch { _allSkins = new(); }
        _skinsLoading = false;
        FillWishResults();
    }

    void FillWishResults()
    {
        if (_wishResults == null) return;
        _wishResults.Children.Clear();
        var q = _wishSearch.Trim();
        if (q.Length < 2) return;
        if (_allSkins == null) { _wishResults.Children.Add(Dim(Loc.T("Loading every skin…"), 12, 4)); return; }
        if (_allSkins.Count == 0) { _wishResults.Children.Add(Dim(Loc.T("Couldn't load the skin list. Check your internet and try again."), 12, 4)); return; }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _allSkins.Where(s => words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(8).ToList();
        if (hits.Count == 0) { _wishResults.Children.Add(Dim(Loc.T("No skin by that name."), 12, 4)); return; }
        foreach (var h in hits)
        {
            bool have = S.Wishlist.Any(w => w.Id == h.Id);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pic = new Border { Width = 80, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
            _ = SetPicture(pic, h);
            row.Children.Add(pic);
            var name = new TextBlock { Text = h.Name, FontSize = 13, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var add = Btn(have ? "On your wishlist" : "Add", !have);
            add.IsEnabled = !have;
            add.Margin = new Thickness(0);
            var skin = h;
            add.Click += (_, _) =>
            {
                S.Wishlist.Insert(0, new WishSkin { Id = skin.Id, Name = skin.Name, Icon = skin.Icon, Ids = skin.Ids.ToList() });
                S.WishlistChecked = default; // check the store again with the new skin
                TweakEngine.Save();
                _wishSearch = "";
                Build();
                MainWindow.Instance.Toast(Loc.F("{0} is on your wishlist", skin.Name));
            };
            Grid.SetColumn(add, 2); row.Children.Add(add);
            _wishResults.Children.Add(row);
        }
    }

    async Task SetPicture(Border box, WishSkin w)
    {
        var file = await ValorantStore.SkinPicture(w);
        if (LoadPicture(file) is { } src) box.Child = new Image { Source = src, Stretch = Stretch.Uniform };
    }

    FrameworkElement ItemCard(StoreItem i, bool wide)
    {
        var c = TierColor(i.Tier);
        double w = wide ? 520 : 250, h = wide ? 220 : 120;
        var g = new Grid { Width = w };
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var pic = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0), ClipToBounds = true,
            Background = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x10, c.R, c.G, c.B), 90),
        };
        if (LoadPicture(i.Icon) is { } src)
            pic.Child = new Image { Source = src, Stretch = wide ? Stretch.UniformToFill : Stretch.Uniform, Margin = wide ? new Thickness(0) : new Thickness(18, 14, 18, 14) }.Also(im => RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality));
        g.Children.Add(pic);
        var info = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
        info.Children.Add(new TextBlock { Text = i.Name.Length > 0 ? i.Name : Loc.T("Unknown item"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var price = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        price.Children.Add(new TextBlock { Text = i.Price > 0 ? Loc.F("{0} VP", i.Price.ToString("N0", CultureInfo.CurrentCulture)) : "", FontSize = 12.5, Foreground = Res("TextBrush") });
        if (i.DiscountPercent > 0)
        {
            price.Children.Add(new TextBlock { Text = "  " + i.FullPrice.ToString("N0", CultureInfo.CurrentCulture), FontSize = 12, Foreground = Res("TextFaintBrush"), TextDecorations = TextDecorations.Strikethrough });
            price.Children.Add(new TextBlock { Text = $"  −{i.DiscountPercent}%", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("GoodBrush") });
        }
        if (i.Tier.Length > 0 && i.Tier != "Bundle")
            // VALORANT's own tier names, kept as the game shows them (the diamond also keeps them out of the translation table).
            price.Children.Add(new TextBlock { Text = "   ◆ " + i.Tier, FontSize = 11.5, Foreground = new SolidColorBrush(c) });
        info.Children.Add(price);
        if (Wished(i))
            info.Children.Add(new TextBlock { Text = "★ " + Loc.T("On your wishlist"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res("GoodBrush"), Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetRow(info, 1);
        g.Children.Add(info);
        return new Border
        {
            Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 12), Child = g,
        };
    }

    static ImageSource LoadPicture(string file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(file);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 520;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
