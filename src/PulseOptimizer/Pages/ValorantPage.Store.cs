using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
