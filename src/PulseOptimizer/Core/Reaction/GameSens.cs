namespace Pulse.Core.Reaction;

/// <summary>
/// Moves the drills' crosshair exactly like VALORANT does, so your hand learns the same distances.
///
/// VALORANT turns the camera 0.07° per mouse count at sensitivity 1 (so 0.07 × sens per count), straight from the
/// mouse's raw counts: Windows' pointer speed and acceleration don't apply. Its horizontal field of view is 103° across a
/// 16:9 picture. A target that's θ degrees off your crosshair shows up f·tan(θ) pixels away on screen, where
/// f = (width / 2) / tan(103° / 2). The drills place the crosshair the same way: they add up the raw counts as degrees
/// and draw it at f·tan(angle), so a flick that takes 3 cm of mousepad in game takes 3 cm here.
/// </summary>
public sealed record GameSens(double Sens, int Dpi)
{
    public const double YawPerCount = 0.07;
    public const double HorizontalFov = 103;

    public bool Valid => Sens > 0 && Sens <= 20 && Dpi >= 100 && Dpi <= 32000;

    /// <summary>Degrees the camera turns per mouse count.</summary>
    public double DegreesPerCount => YawPerCount * Sens;

    /// <summary>Centimetres of mouse movement for a full 360° turn.</summary>
    public double Cm360 => 360 / DegreesPerCount / Dpi * 2.54;

    /// <summary>
    /// The projection's focal length in the same units as the screen size (WPF units are fine as long as the screen
    /// size is in WPF units too). Wider than 16:9 is letterboxed the way VALORANT does; narrower uses the full width.
    /// </summary>
    public static double Focal(double screenW, double screenH)
    {
        double w = screenH > 0 ? Math.Min(screenW, screenH * 16 / 9) : screenW;
        return w / 2 / Math.Tan(HorizontalFov / 2 * Math.PI / 180);
    }

    /// <summary>Where on screen (relative to the centre) something at this angle appears.</summary>
    public static (double X, double Y) Project(double yawDeg, double pitchDeg, double focal) =>
        (focal * Math.Tan(yawDeg * Math.PI / 180), focal * Math.Tan(pitchDeg * Math.PI / 180));

    /// <summary>The angle for a point on screen (relative to the centre).</summary>
    public static (double Yaw, double Pitch) Unproject(double x, double y, double focal) =>
        (Math.Atan2(x, focal) * 180 / Math.PI, Math.Atan2(y, focal) * 180 / Math.PI);
}
