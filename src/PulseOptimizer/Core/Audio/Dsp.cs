namespace Pulse.Core.Audio;

/// <summary>The 10 EQ bands Pulse uses.</summary>
public static class EqBands
{
    public static readonly string[] Labels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    public static readonly double[] Freqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
}

/// <summary>A second-order filter (RBJ audio EQ cookbook).</summary>
public sealed class Biquad
{
    double b0, b1, b2, a1, a2, z1, z2;

    public static Biquad HighPass(double fs, double f, double q = 0.707) => Make(fs, f, q, 0, "hp");
    public static Biquad LowPass(double fs, double f, double q = 0.707) => Make(fs, f, q, 0, "lp");
    public static Biquad Peak(double fs, double f, double q, double db) => Make(fs, f, q, db, "peak");
    public static Biquad LowShelf(double fs, double f, double db) => Make(fs, f, 0.707, db, "ls");
    public static Biquad HighShelf(double fs, double f, double db) => Make(fs, f, 0.707, db, "hs");

    static Biquad Make(double fs, double f, double q, double db, string type)
    {
        f = Math.Min(f, fs * 0.45);
        double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / fs, cs = Math.Cos(w), sn = Math.Sin(w), alpha = sn / (2 * q);
        double sq = 2 * Math.Sqrt(A) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case "hp": b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = (1 + cs) / 2; a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha; break;
            case "lp": b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = (1 - cs) / 2; a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha; break;
            case "peak": b0 = 1 + alpha * A; b1 = -2 * cs; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cs; a2 = 1 - alpha / A; break;
            case "ls":
                b0 = A * ((A + 1) - (A - 1) * cs + sq); b1 = 2 * A * ((A - 1) - (A + 1) * cs); b2 = A * ((A + 1) - (A - 1) * cs - sq);
                a0 = (A + 1) + (A - 1) * cs + sq; a1 = -2 * ((A - 1) + (A + 1) * cs); a2 = (A + 1) + (A - 1) * cs - sq; break;
            default:
                b0 = A * ((A + 1) + (A - 1) * cs + sq); b1 = -2 * A * ((A - 1) + (A + 1) * cs); b2 = A * ((A + 1) + (A - 1) * cs - sq);
                a0 = (A + 1) - (A - 1) * cs + sq; a1 = 2 * ((A - 1) - (A + 1) * cs); a2 = (A + 1) - (A - 1) * cs - sq; break;
        }
        return new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };
    }

    public float Process(float x)
    {
        double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return (float)y;
    }

    /// <summary>Gain (dB) of this filter at frequency f, for drawing the EQ curve.</summary>
    public double ResponseDb(double fs, double f)
    {
        double w = 2 * Math.PI * f / fs;
        var z1c = System.Numerics.Complex.FromPolarCoordinates(1, -w);
        var z2c = z1c * z1c;
        var h = (b0 + b1 * z1c + b2 * z2c) / (1 + a1 * z1c + a2 * z2c);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-9));
    }
}

/// <summary>Converts any rate to 48 kHz (or back) with 4-point Hermite interpolation, streaming.</summary>
public sealed class Resampler
{
    readonly double _step;
    double _pos;
    float _x0, _x1, _x2, _x3;
    public Resampler(int fromRate, int toRate) { _step = (double)fromRate / toRate; }

    /// <summary>Feeds input samples; output samples are appended to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        foreach (var s in input)
        {
            _x0 = _x1; _x1 = _x2; _x2 = _x3; _x3 = s;
            while (_pos < 1)
            {
                double t = _pos;
                double c0 = _x1, c1 = 0.5 * (_x2 - _x0), c2 = _x0 - 2.5 * _x1 + 2 * _x2 - 0.5 * _x3, c3 = 0.5 * (_x3 - _x0) + 1.5 * (_x1 - _x2);
                output.Add((float)(((c3 * t + c2) * t + c1) * t + c0));
                _pos += _step;
            }
            _pos -= 1;
        }
    }
}
