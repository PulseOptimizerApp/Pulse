using System.Diagnostics;

namespace Pulse.Core.Input;

/// <summary>One macro on the Macros page.</summary>
public class MacroDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "New macro";
    public bool Enabled { get; set; } = true;
    /// <summary>"clicker" (auto/butterfly clicking), "sequence" (keys, clicks, waits, movement) or "script" (a Logitech G Hub Lua script).</summary>
    public string Kind { get; set; } = "sequence";

    /// <summary>Virtual-key code of the key or mouse button that starts it (0 = none set yet).</summary>
    public int TriggerKey { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    /// <summary>"hold" (runs while held, repeating), "toggle" (press to start, press again to stop) or "once" (each press plays it).</summary>
    public string Mode { get; set; } = "once";
    /// <summary>For "once": how many times to play it per press.</summary>
    public int Repeat { get; set; } = 1;
    /// <summary>Pause between repeats, in ms.</summary>
    public double RepeatGap { get; set; }

    /// <summary>"anywhere", "game" (the game Pulse detected is in front) or "app" (<see cref="App"/> is in front).</summary>
    public string Where { get; set; } = "anywhere";
    /// <summary>Program name for Where = "app", like "RobloxPlayerBeta" (no .exe).</summary>
    public string App { get; set; } = "";

    /// <summary>Wait times vary by up to this share each way, so it doesn't look robotic (0 = exact).</summary>
    public double Humanize { get; set; }

    public MacroSettings Clicker { get; set; }
    public List<MacroStep> Steps { get; set; } = new();
    public string Script { get; set; }
    /// <summary>Script macros: the key or button the user presses for G1, G2… (index 0 = G1), as virtual-key codes.</summary>
    public List<int> GKeys { get; set; } = new();
}

/// <summary>One step of a sequence macro.</summary>
public class MacroStep
{
    /// <summary>"tap", "down", "up" (a key), "click", "press", "release" (a mouse button), "wait", "text", "move", "moveto", "scroll".</summary>
    public string Do { get; set; } = "tap";
    /// <summary>Virtual-key code for key steps.</summary>
    public int Key { get; set; }
    /// <summary>0 left, 1 right, 2 middle, 3 mouse 4, 4 mouse 5.</summary>
    public int Button { get; set; }
    /// <summary>Wait length, or how long a tap/click holds.</summary>
    public double Ms { get; set; }
    /// <summary>Random extra wait, 0 to this many ms.</summary>
    public double RandomMs { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Text { get; set; }

    public bool Moves => Do is "move" or "moveto";
}

/// <summary>Settings for all macros.</summary>
public class MacroOptions
{
    /// <summary>Stops every macro at once (virtual-key code). Esc by default.</summary>
    public int PanicKey { get; set; } = 0x1B;
    /// <summary>Show a small notice when macros turn on or off.</summary>
    public bool Notifications { get; set; } = true;
    /// <summary>Let macros run while a kernel anti-cheat is running. Off by default; the user takes the risk.</summary>
    public bool AllowWithAntiCheat { get; set; }
    /// <summary>
    /// Let mouse-moving macros run while a competitive shooter is open. Off by default and strongly advised against:
    /// there it's aim or recoil help, which is unfair to other players and gets accounts banned.
    /// </summary>
    public bool AllowMoveInShooters { get; set; }
    /// <summary>Recording: "recorded" (keep your timing), "fixed" (<see cref="FixedDelay"/> between every action) or "none".</summary>
    public string RecordDelays { get; set; } = "recorded";
    public double FixedDelay { get; set; } = 50;
    /// <summary>Recording also keeps mouse buttons and scrolling.</summary>
    public bool RecordMouse { get; set; } = true;
    /// <summary>Turn macros on when Pulse starts.</summary>
    public bool OnAtStart { get; set; }
}

/// <summary>What's allowed where. Pure functions so they can be tested.</summary>
public static class MacroPolicy
{
    /// <summary>Kernel anti-cheats. Some games running these are fine with macros (League, Fortnite), many aren't.</summary>
    public static readonly string[] AntiCheatProcesses = { "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitservice", "faceit_ac" };

    /// <summary>
    /// Competitive shooters, where moving the mouse with a macro is aim or recoil help: unfair, and they ban for it.
    /// Mouse movement is paused while one is running unless the user turns on AllowMoveInShooters (strongly advised against).
    /// </summary>
    public static readonly (string Process, string Game)[] Shooters =
    {
        ("VALORANT-Win64-Shipping", "VALORANT"), ("cs2", "Counter-Strike 2"), ("csgo", "Counter-Strike"),
        ("FortniteClient-Win64-Shipping", "Fortnite"), ("r5apex", "Apex Legends"), ("r5apex_dx12", "Apex Legends"),
        ("RainbowSix", "Rainbow Six Siege"), ("RainbowSix_BE", "Rainbow Six Siege"), ("RainbowSix_Vulkan", "Rainbow Six Siege"),
        ("RustClient", "Rust"), ("cod", "Call of Duty"), ("ModernWarfare", "Call of Duty"), ("BlackOps6", "Call of Duty"),
        ("Overwatch", "Overwatch 2"), ("TslGame", "PUBG"), ("EscapeFromTarkov", "Escape from Tarkov"),
        ("bf2042", "Battlefield 2042"), ("bf6", "Battlefield 6"), ("Marvel-Win64-Shipping", "Marvel Rivals"),
        ("deadlock", "Deadlock"), ("FragPunk", "FragPunk"), ("TheFinals", "The Finals"),
    };

    public static string AntiCheatName(string process) => process switch
    {
        "vgc" or "vgtray" => "Riot Vanguard",
        "BEService" => "BattlEye",
        "FACEIT" or "faceitservice" or "faceit_ac" => "FACEIT",
        _ => "Easy Anti-Cheat",
    };

    /// <summary>Given the running process names, the anti-cheat (display name) running, or null.</summary>
    public static string AntiCheatIn(ISet<string> running)
    {
        foreach (var p in AntiCheatProcesses) if (running.Contains(p)) return AntiCheatName(p);
        return null;
    }

    /// <summary>Given the running process names, the competitive shooter running, or null.</summary>
    public static string ShooterIn(ISet<string> running)
    {
        foreach (var (p, g) in Shooters) if (running.Contains(p)) return g;
        return null;
    }

    public static HashSet<string> RunningProcesses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName); } catch { }
            p.Dispose();
        }
        return set;
    }

    /// <summary>Does this macro move the mouse?</summary>
    public static bool MovesMouse(MacroDef m) =>
        m.Kind == "sequence" ? m.Steps.Any(s => s.Moves)
        : m.Kind == "script" && m.Script != null && (m.Script.Contains("MoveMouseRelative") || m.Script.Contains("MoveMouseTo"));

    /// <summary>Do the trigger's modifiers match what's held? (No modifier set = any.)</summary>
    public static bool ModifiersMatch(MacroDef m, Func<int, bool> isDown) =>
        (!m.Ctrl || isDown(0x11)) && (!m.Shift || isDown(0x10)) && (!m.Alt || isDown(0x12));

    /// <summary>The old single click macro, as the first macro in the list.</summary>
    public static MacroDef FromOldClicker(MacroSettings old) => new()
    {
        Name = "Click macro",
        Kind = "clicker",
        Clicker = old,
        TriggerKey = old.TriggerKey,
        Mode = old.Activation == "toggle" ? "toggle" : "hold",
        Where = old.OnlyInGame ? "game" : "anywhere",
    };

    /// <summary>A short description of what starts it: "Hold Mouse 4", "Press Ctrl + F".</summary>
    public static string TriggerText(MacroDef m, Func<string, string> t)
    {
        if (m.Kind == "script") return t("Runs a script");
        if (m.TriggerKey == 0) return t("No key set");
        var parts = new List<string>();
        if (m.Ctrl) parts.Add("Ctrl");
        if (m.Shift) parts.Add("Shift");
        if (m.Alt) parts.Add("Alt");
        parts.Add(t(Keys.Display(m.TriggerKey)));
        string verb = m.Mode switch { "hold" => t("Hold"), "toggle" => t("Toggle with"), _ => t("Press") };
        return verb + " " + string.Join(" + ", parts);
    }

    /// <summary>Sequence step timing: the wait, plus random extra, varied by the humanize share.</summary>
    public static double StepWait(double ms, double randomMs, double humanize, Random rng)
    {
        double w = ms + (randomMs > 0 ? rng.NextDouble() * randomMs : 0);
        if (humanize > 0) w *= 1 + (rng.NextDouble() * 2 - 1) * Math.Clamp(humanize, 0, 0.5);
        return Math.Max(w, 0);
    }
}
