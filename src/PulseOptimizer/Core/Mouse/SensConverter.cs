namespace Pulse.Core.Mouse;

/// <summary>
/// Converts mouse sensitivity between games so a flick of the same length on your mousepad turns you the same amount
/// (same cm per 360°). Each game turns the camera a fixed number of degrees per mouse count at sensitivity 1 (its "yaw");
/// matching cm/360 is: new sens = old sens × (old yaw ÷ new yaw) × (old DPI ÷ new DPI).
///
/// Only games whose yaw is well established are listed. Values: VALORANT 0.07; Source-engine games 0.022; Overwatch 2
/// and Call of Duty 0.0066; Marvel Rivals 0.0175 (VALORANT × 4); Fortnite 0.005555 per % of its slider; Rainbow Six
/// Siege 0.00572958 per point at the default multiplier (0.02); The Finals 0.001.
/// </summary>
public static class SensConverter
{
    public sealed record Game(string Id, string Name, double Yaw, string Note = null, int Decimals = 3);

    public static readonly IReadOnlyList<Game> Games = new[]
    {
        new Game("valorant", "VALORANT", 0.07),
        new Game("cs2", "Counter-Strike 2", 0.022, Decimals: 2),
        new Game("apex", "Apex Legends", 0.022, Decimals: 2),
        new Game("overwatch", "Overwatch 2", 0.0066, Decimals: 2),
        new Game("cod", "Call of Duty (Warzone, MW, BO)", 0.0066, "Uses the \"Legacy\" sensitivity setting.", 2),
        new Game("rivals", "Marvel Rivals", 0.0175, Decimals: 2),
        new Game("fortnite", "Fortnite", 0.005555, "The X/Y sensitivity slider, in % (e.g. 8.0).", 1),
        new Game("r6", "Rainbow Six Siege", 0.00572958, "Mouse sensitivity at the default multiplier (0.02).", 0),
        new Game("finals", "The Finals", 0.001, Decimals: 1),
        new Game("tf2", "Team Fortress 2", 0.022, Decimals: 2),
        new Game("quake", "Quake Champions", 0.022, Decimals: 2),
        new Game("kovaaks", "KovaaK's (Source scale)", 0.022, Decimals: 2),
    };

    public static Game Find(string id) => Games.FirstOrDefault(g => g.Id == id);

    /// <summary>Centimetres of mouse movement for a full 360° turn.</summary>
    public static double Cm360(Game g, double sens, double dpi) => sens > 0 && dpi > 0 ? 360 / (g.Yaw * sens * dpi) * 2.54 : double.NaN;

    /// <summary>The sensitivity in <paramref name="to"/> that turns exactly as far as <paramref name="sens"/> in <paramref name="from"/>.</summary>
    public static double Convert(Game from, double sens, double fromDpi, Game to, double toDpi) =>
        sens > 0 && fromDpi > 0 && toDpi > 0 ? sens * (from.Yaw / to.Yaw) * (fromDpi / toDpi) : double.NaN;

    /// <summary>Sensitivity × DPI, how players compare settings within one game.</summary>
    public static double Edpi(double sens, double dpi) => sens * dpi;

    /// <summary>Rounded the way the game's own setting is (games only take so many decimals).</summary>
    public static double Round(Game g, double sens) => Math.Round(sens, g.Decimals, MidpointRounding.AwayFromZero);
}
