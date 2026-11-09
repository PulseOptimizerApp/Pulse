namespace Pulse.Core.Mouse;

/// <summary>A mouse setup: DPI and polling rate (0 = leave as it is).</summary>
public class MouseProfile
{
    /// <summary>The game, as Pulse names it ("VALORANT", "Counter-Strike 2"); null for "no game running".</summary>
    public string Game { get; set; }
    public int Dpi { get; set; }
    public int PollingHz { get; set; }
}

public class MouseSettings
{
    /// <summary>The mouse profiles apply to, as "VVVV:PPPP" (vendor:product). Null until one is picked on the Mouse page.</summary>
    public string Device { get; set; }
    /// <summary>Used when no game is running (and when a game without its own profile closes).</summary>
    public MouseProfile Desktop { get; set; }
    public List<MouseProfile> Games { get; set; } = new();
    /// <summary>A popup when a wireless mouse's battery drops below <see cref="BatteryAlertPercent"/>.</summary>
    public bool BatteryAlert { get; set; } = true;
    public int BatteryAlertPercent { get; set; } = 15;
    /// <summary>Last battery level seen (for the alert, which shows once per drop).</summary>
    public int LastBattery { get; set; } = -1;

    /// <summary>Something needs Pulse to watch for games: a game profile or a desktop profile.</summary>
    public static bool WantsGames(MouseSettings s) =>
        s?.Device != null && (s.Games.Count > 0 || s.Desktop is { } d && (d.Dpi > 0 || d.PollingHz > 0));

    /// <summary>The profile for a game (by name, ignoring case), the desktop one, or null.</summary>
    public MouseProfile For(string game) =>
        game == null ? Desktop : Games.FirstOrDefault(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase)) ?? Desktop;

    public static string DeviceId(int vendorId, int productId) => $"{vendorId:X4}:{productId:X4}";
}
