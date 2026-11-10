using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core.Games.Brawlhalla;

namespace Pulse.Ui;

public static partial class LobbyOverlay
{
    static readonly Brush BhGold = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42)));

    /// <summary>
    /// Brawlhalla: the match (or matches) that just showed up in your stats, in the corner: legend, result, mode,
    /// KOs, falls, damage, XP, rating change, and how today is going. Never takes focus from the game.
    /// </summary>
    public static void ShowBrawlhalla(List<BhMatch> fresh, Dictionary<int, BhLegend> legends, BhSession today)
    {
        var app = Application.Current;
        if (app == null || fresh is not { Count: > 0 }) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => ShowBrawlhalla(fresh, legends, today)); return; }
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        body.Children.Add(Brand(Loc.T("Brawlhalla")));
        foreach (var m in fresh.Take(3)) body.Children.Add(BhRow(m, legends));
        if (fresh.Count > 3) body.Children.Add(T(Loc.F("and {0} more", fresh.Count - 3), 11, Dim, bold: false).Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
        if (today is { Games: > 1 })
        {
            var line = Loc.F("This session: {0}W {1}L", today.Wins, today.Games - today.Wins);
            if (today.RatingChange != 0) line += " · " + Loc.F("{0} rating", (today.RatingChange > 0 ? "+" : "") + today.RatingChange);
            line += " · " + Loc.F("{0} KOs", today.Kos);
            body.Children.Add(new Border
            {
                Background = RowBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 12, 0, 0),
                Child = T(line, 11.5, White, bold: false),
            });
        }
        var buttons = new List<(string, bool, Action)>
        {
            (Loc.T("Open matches"), true, () =>
            {
                if (app.MainWindow is not MainWindow mw) return;
                mw.ShowFromTray();
                mw.Navigate("brawlhalla", "matches");
            }),
            (Loc.T("Close"), false, null),
        };
        Prompt(app, body, buttons, 30, 330);
    }

    static FrameworkElement BhRow(BhMatch m, Dictionary<int, BhLegend> legends)
    {
        string name = legends != null && legends.TryGetValue(m.LegendId, out var l) ? l.Name : Loc.F("Legend {0}", m.LegendId);
        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var art = BrawlhallaArt.Legend(name);
        g.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = RowBg,
            Child = art != null
                ? new Image { Source = art, Stretch = Stretch.UniformToFill }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality))
                : T(name[..1], 18, BhGold).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }),
        });

        var mid = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        string result = m.Games == 1 ? Loc.T(m.Wins == 1 ? "Victory" : "Defeat") : Loc.F("{0}W / {1}L", m.Wins, m.Losses);
        head.Children.Add(T(result, 15, m.Wins * 2 > m.Games ? Win : m.Wins * 2 < m.Games ? Loss : White));
        head.Children.Add(T("  " + name, 12.5, White, bold: false).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        mid.Children.Add(head);
        string mode = m.Mode switch
        {
            "1v1" => Loc.T("Ranked 1v1"),
            "2v2" => m.Partner.Length > 0 ? Loc.F("Ranked 2v2 with {0}", m.Partner) : Loc.T("Ranked 2v2"),
            "3v3" => Loc.T("Ranked 3v3"),
            _ => Loc.T("Casual or other"),
        };
        mid.Children.Add(T(mode + " · " + Loc.F("{0} KOs · {1} falls · {2} damage", m.Kos, m.Falls, m.Damage.ToString("N0", CultureInfo.CurrentCulture))
            + (m.Xp > 0 ? " · " + Loc.F("{0} XP", m.Xp.ToString("N0", CultureInfo.CurrentCulture)) : ""), 11, Dim, bold: false).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        if (m.RatingAfter > 0)
        {
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            right.Children.Add(T(m.RatingAfter.ToString("N0", CultureInfo.CurrentCulture), 16, White).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            int d = m.RatingChange;
            right.Children.Add(T(d == 0 ? Brawlhalla.TierOf(m.RatingAfter) : (d > 0 ? "+" : "") + d, 10.5, d > 0 ? Win : d < 0 ? Loss : Dim)
                .Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        return g;
    }
}
