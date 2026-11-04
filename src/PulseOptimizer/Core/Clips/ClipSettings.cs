namespace Pulse.Core.Clips;

/// <summary>Everything the user picked on the Clips page. Saved in Pulse's state file.</summary>
public class ClipSettings
{
    /// <summary>Clips is opt-in: off until the user turns it on (that's also when FFmpeg is downloaded).</summary>
    public bool Enabled { get; set; }
    /// <summary>Seconds kept in the instant replay.</summary>
    public int ReplaySeconds { get; set; } = 60;
    /// <summary>"native", "1440", "1080", "720".</summary>
    public string Resolution { get; set; } = "native";
    public int Fps { get; set; } = 60;
    /// <summary>Video bitrate in megabits per second.</summary>
    public int BitrateMbps { get; set; } = 40;
    /// <summary>"h264", "hevc" or "av1" (only offered when the graphics card can encode it).</summary>
    public string Codec { get; set; } = "h264";
    /// <summary>Index of the display to record (0 = the graphics card's first output, usually the main screen).</summary>
    public int Monitor { get; set; }
    public bool RecordCursor { get; set; }

    public bool GameAudio { get; set; } = true;
    public int GameVolume { get; set; } = 100;
    public bool Microphone { get; set; }
    public int MicVolume { get; set; } = 100;
    /// <summary>Also put the microphone on its own second audio track (for editing). The first track is always the mix.</summary>
    public bool SeparateMicTrack { get; set; }

    /// <summary>Hotkey to save the replay: modifier flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4) and a virtual-key code.</summary>
    public int HotkeyModifiers { get; set; } = 1;   // Alt
    public int HotkeyKey { get; set; } = 0x77;      // F8 → Alt+F8 (Alt+F10 belongs to NVIDIA's instant replay, F8 alone to Medal)
    public bool HotkeyTakenWarned { get; set; }

    /// <summary>A second clip hotkey with its own length (for example a quick 15 s clip). Off until the user sets it.</summary>
    public bool QuickClip { get; set; }
    public int QuickModifiers { get; set; } = 1;   // Alt
    public int QuickKey { get; set; } = 0x75;      // F6 → Alt+F6
    public int QuickSeconds { get; set; } = 15;

    /// <summary>Screenshot hotkey: saves the current frame as a PNG in the clips folder's Screenshots folder.</summary>
    public bool Screenshot { get; set; } = true;
    public int ScreenshotModifiers { get; set; } = 1; // Alt
    public int ScreenshotKey { get; set; } = 0x76;    // F7 → Alt+F7

    /// <summary>Only keep a replay while a game is running (saves memory and GPU work the rest of the time).</summary>
    public bool OnlyWhileGaming { get; set; } = true;
    /// <summary>Keep clipping when the Pulse window is closed (Pulse stays in the tray).</summary>
    public bool KeepRunningInTray { get; set; } = true;
    /// <summary>Start Pulse in the tray when you sign in, so clips work without opening Pulse.</summary>
    public bool StartWithWindows { get; set; }
    public bool PlaySound { get; set; } = true;
    public bool ShowSavedPopup { get; set; } = true;

    /// <summary>"" = Videos\Pulse Clips.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Oldest clips (not favorites) are deleted when the folder grows past this. 0 = no limit.</summary>
    public int StorageLimitGb { get; set; } = 30;
    public List<string> Favorites { get; set; } = new();

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Pulse Clips");

    public string ClipsFolder => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder;
    public string ScreenshotsFolder => Path.Combine(ClipsFolder, "Screenshots");
}
