using Pulse.Core;
using Pulse.Core.Games.Live;

namespace Pulse.Ui;

/// <summary>
/// VALORANT store wishlist alerts: while the Riot Client is open, checks your store once after each daily refresh
/// (00:00 UTC) and tells you when a wishlist skin is in it, in your daily offers or the Night Market. Reads the store
/// exactly like the Store tab does; nothing is bought.
/// </summary>
public static class StoreWatch
{
    static System.Threading.Timer _timer;
    static int _busy;

    static Core.Clips.ClipSettings S => TweakEngine.State?.Clips;

    /// <summary>Wishlist skins found in the store, for the Store tab to show (raised on the UI thread).</summary>
    public static event Action<List<(WishSkin Skin, StoreItem Item, string Where)>> Found;

    public static void Init() => _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(20));

    public static void Shutdown() => _timer?.Dispose();

    /// <summary>The start of the store day a time falls in (the store refreshes at 00:00 UTC).</summary>
    public static DateTime StoreDay(DateTime utc) => utc.Date;

    static void Tick()
    {
        if (S is not { WishlistAlerts: true } s || s.Wishlist.Count == 0) return;
        // Once per store day: the store can't change before the next refresh.
        if (StoreDay(s.WishlistChecked) == StoreDay(DateTime.UtcNow)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Check(s); }
            catch { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    /// <summary>Reads the store and tells you about wishlist skins in it you haven't been told about today.</summary>
    public static async Task Check(Core.Clips.ClipSettings s)
    {
        var store = await ValorantStore.Load();
        if (store == null) return;
        s.WishlistChecked = DateTime.UtcNow;
        var found = ValorantStore.WishMatches(store, s.Wishlist.ToList());
        var day = StoreDay(DateTime.UtcNow).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var fresh = found.Where(f => !s.WishlistSeen.Contains(day + ":" + f.Item.Id)).ToList();
        foreach (var f in fresh) s.WishlistSeen.Add(day + ":" + f.Item.Id);
        // Keep a month of "already told you".
        s.WishlistSeen.RemoveAll(x => string.CompareOrdinal(x, StoreDay(DateTime.UtcNow.AddDays(-31)).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)) < 0);
        TweakEngine.Save();
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() => Found?.Invoke(found));
        if (fresh.Count == 0) return;
        string where(string w) => w == "night" ? Loc.T("in your Night Market") : Loc.T("in your store today");
        var title = fresh.Count == 1 ? Loc.F("{0} is in your store!", fresh[0].Skin.Name) : Loc.F("{0} wishlist skins are in your store!", fresh.Count);
        var text = string.Join("\n", fresh.Take(4).Select(f => $"{f.Skin.Name}: {f.Item.Price:N0} VP {where(f.Where)}" + (f.Item.DiscountPercent > 0 ? $" (−{f.Item.DiscountPercent}%)" : "")));
        TweakEngine.AddLog("valorant", "Wishlist: " + string.Join(", ", fresh.Select(f => f.Skin.Name)));
        app?.Dispatcher.BeginInvoke(() =>
        {
            TrayIcon.Notify(title, text);
            if (app.MainWindow is { IsVisible: true }) MainWindow.Instance?.Toast(title);
        });
    }
}
