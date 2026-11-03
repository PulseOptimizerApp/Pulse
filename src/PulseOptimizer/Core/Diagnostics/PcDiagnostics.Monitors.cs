namespace Pulse.Core;

/// <summary>Monitor checks: refresh rate, resolution, cable/port limits and adaptive sync.</summary>
public static partial class PcDiagnostics
{
    /// <summary>Monitors from the last checkup (used by the Games page for frame caps).</summary>
    public static List<MonitorInfo> LastMonitors { get; private set; } = new();

    static IEnumerable<Finding> Monitors()
    {
        var monitors = Displays.Enumerate();
        LastMonitors = monitors;
        var results = new List<Finding>();

        foreach (var m in monitors)
        {
            var id = m.DeviceName.TrimStart('\\', '.');
            var label = m.DisplayLabel;
            var conn = m.Connection.Length > 0 ? $" over {m.Connection}" : "";
            var cur = m.Current;
            var native = m.Native;
            int maxHere = m.MaxHzAtCurrent;
            int maxNative = m.MaxHzAt(native.Width, native.Height);
            int monitorMaxNative = m.MonitorMaxHzAt(native.Width, native.Height);
            bool problem = false;

            // 1. Below the best refresh rate Windows already offers at this resolution → one-click fix.
            if (maxHere > cur.Hz + 1)
            {
                problem = true;
                results.Add(new Finding
                {
                    Id = "mon-refresh:" + id, Rank = -2, Severity = Severity.Problem,
                    Title = $"{label} is running at {cur.Hz} Hz but can do {maxHere} Hz",
                    Detail = $"Set to {cur.Hz} Hz at {cur.Width}×{cur.Height}{conn}. Windows already supports {maxHere} Hz on this screen.",
                    Fix = $"Switch to {maxHere} Hz. Games and the desktop get {maxHere / (double)Math.Max(cur.Hz, 1):0.#}× more frames on screen, which is the biggest free smoothness upgrade there is.",
                    ActionKey = $"refresh:{m.DeviceName}|{cur.Width}|{cur.Height}|{maxHere}", ActionText = $"Switch to {maxHere} Hz",
                    Facts = { ["device"] = m.DeviceName, ["hz"] = maxHere.ToString(), ["cur"] = cur.Hz.ToString(), ["conn"] = m.Connection },
                });
            }

            // 2. The monitor reports a higher refresh than Windows offers → the cable, port or adapter is the limit.
            if (monitorMaxNative > Math.Max(maxNative, maxHere) + 1 && !m.IsBuiltIn)
            {
                problem = true;
                results.Add(new Finding
                {
                    Id = "mon-cable:" + id, Rank = -2, Severity = Severity.Problem,
                    Title = $"Your cable or port is holding {label} back",
                    Detail = $"The monitor says it supports {monitorMaxNative} Hz at {native.Width}×{native.Height}, but Windows only offers up to {maxNative} Hz{conn}.",
                    Fix = "This almost always means the cable, port or an adapter can't carry the full signal. Pulse can't fix a cable, but the steps below usually solve it in a few minutes.",
                    Facts = { ["conn"] = m.Connection, ["hz"] = monitorMaxNative.ToString(), ["have"] = maxNative.ToString() },
                });
            }

            // 3. Not at native resolution (blurry text and fewer pixels than the panel has).
            if ((cur.Width < native.Width || cur.Height < native.Height) && !m.IsBuiltIn)
            {
                results.Add(new Finding
                {
                    Id = "mon-res:" + id, Rank = 3, Severity = Severity.Check,
                    Title = $"{label} isn't at its native resolution",
                    Detail = $"Desktop is {cur.Width}×{cur.Height}; the screen's full resolution is {native.Width}×{native.Height}" + (maxNative > 0 ? $" ({maxNative} Hz)." : "."),
                    Fix = "Below native resolution, everything is stretched and looks soft. If you lowered it on purpose for a game, set it inside the game instead and keep the desktop at native.",
                    ActionKey = "settings:ms-settings:display", ActionText = "Open display settings",
                    Facts = { ["w"] = native.Width.ToString(), ["h"] = native.Height.ToString() },
                });
            }

            // 4. High-refresh screens: make sure adaptive sync is on (Windows can't read the driver toggle).
            if (Math.Max(maxHere, cur.Hz) >= 100)
            {
                results.Add(new Finding
                {
                    Id = "mon-vrr:" + id, Rank = 4, Severity = Severity.Check,
                    Title = $"Turn on G-SYNC or FreeSync for {label}",
                    Detail = "Adaptive sync makes the screen wait for each frame, so you get no tearing without the big delay of regular V-Sync. It's set in the graphics driver, which Windows doesn't report, so Pulse can't see whether it's already on.",
                    Fix = "Takes a minute to check. The steps below match your graphics card.",
                    Facts = { ["hz"] = Math.Max(maxHere, cur.Hz).ToString() },
                });
            }

            if (!problem)
            {
                results.Add(new Finding
                {
                    Id = "mon-ok:" + id, Rank = -2, Severity = Severity.Ok,
                    Title = $"{label}",
                    Detail = $"{cur.Width}×{cur.Height} at {cur.Hz} Hz{conn}, the most this screen offers." +
                             (m.IsBuiltIn ? "" : monitorMaxNative > 0 ? $" Monitor reports up to {monitorMaxNative} Hz." : ""),
                });
            }
        }

        if (monitors.Count == 0)
            results.Add(Unknown("monitors", "Monitors", "Windows didn't report any connected displays."));
        return results;
    }
}
